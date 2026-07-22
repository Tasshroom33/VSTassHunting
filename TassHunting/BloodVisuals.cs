// BLOOD VISUALS 0.9.0 - CLIENT-ONLY (user directive 2026-07-21: "kill any
// sort of server sync/multiplayer anything, just do it client only until we
// get client going"). The 0.6-0.8 server ledger + per-player sync spine is
// PARKED at commit bd8bac6 for the future multiplayer pass.
//
// HOW IT WORKS NOW - zero custom networking, zero visual latency:
//   - The engine already syncs the hurt state every client uses for the red
//     flash ("onHurt" damage + "onHurtCounter" watched attributes). Splatter
//     keys off that, so spurts land on EXACTLY the same beat as the flash and
//     hurt sound - for the shot AND for every bleed DoT tick (the DoT goes
//     through ReceiveDamage, which bumps the same attributes).
//   - BleedSystem (server) publishes its stack count as the "thbleed" watched
//     attribute; the client lays the drip trail locally from that.
//   - Death pools, corpse bleed-out, water sediment: all detected and
//     simulated locally.
// Late joiners do NOT see old blood in this mode - that is the deliberate
// trade until the look is signed off, then the parked sync spine returns.
//
// The LOOK is driven entirely by the user-spec config (0.8.0): the
// BloodTrails / BloodSplatter standard blocks + Water Effect + Bleed Damage.
// Taste never lives in code; if a juice iteration needs a build, the config
// is wrong.

using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace TassHunting
{
    public class BloodVisuals : ModSystem
    {
        private const byte KindTrail = 0, KindHit = 1, KindPool = 2;
        private const float MaxIntensity = 8f;
        private const int CorpseTickMs = 1200;
        private const float WaterCull = 0.008f;
        private const int WaterTileCap = 1024;

        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

        private ICoreClientAPI capi;
        private long tickId, waterTickId;
        private long nextDripPassMs;
        private int nextSpotId = 1;

        /// <summary>A local blood decal anchor (trail drip, hit mark, pool).</summary>
        private class Spot
        {
            public int Id;
            public double X, Y, Z;
            public float Intensity;
            public long BornMs, LifeMs;
            public float FallHeight;
            public byte Kind;
            public bool HasSeg;
            public double PrevX, PrevY, PrevZ;
            public long NextEmitMs;
            public int SpurtsLeft;
            public long NextSpurtMs;
            public bool DropletsPending;
        }

        /// <summary>Per-entity observation state (hurt counter, trail anchor,
        /// corpse bleed window).</summary>
        private class Track
        {
            public int LastHurtCounter;
            public int LastStacks;
            public long NextDripMs;
            public long NextWaterMs;
            public int LastSpotId = -1;
            public double LastX, LastY, LastZ;
            public bool DeathHandled;
            public long CorpseUntilMs, NextCorpseMs;
            public float CorpseStacks;
            public long LastSeenMs;
        }

        private readonly Dictionary<int, Spot> spots = new Dictionary<int, Spot>();
        private readonly Dictionary<long, Track> tracks = new Dictionary<long, Track>();
        private readonly Dictionary<(int x, int y, int z), float> waterTiles = new Dictionary<(int, int, int), float>();
        private readonly Dictionary<(int x, int y, int z), float> waterDisplay = new Dictionary<(int, int, int), float>();
        private readonly Dictionary<(int x, int y, int z), long> waterNextEmit = new Dictionary<(int, int, int), long>();
        private readonly BlockPos scratch = new BlockPos(0);
        private long lastWarnMs = -10000;

        private SimpleParticleProperties groundProps, burstProps, waterProps, dropletProps;
        private string appliedColorHex;
        private float appliedWaterOpacity = -1f;
        private bool appliedSoftWater;
        private static readonly EvolvingNatFloat FadeOut = new EvolvingNatFloat(EnumTransformFunction.QUADRATIC, -255f);
        private static readonly EvolvingNatFloat NoFade = new EvolvingNatFloat(EnumTransformFunction.LINEAR, 0f);

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            tickId = api.Event.RegisterGameTickListener(ClientTick, 150);
            waterTickId = api.Event.RegisterGameTickListener(WaterSimTick, 1000);

            api.ChatCommands.Create("tassbloodc")
                .WithDescription("TassHunting blood (client-local) status")
                .HandleWith(_ => TextCommandResult.Success(
                    $"[tassblood client] {spots.Count} spots, {waterTiles.Count} water tiles, {tracks.Count} tracked entities."));

            api.ChatCommands.Create("tassbloodtest")
                .WithDescription("Lay test blood at your feet: pool + trail line (client-local)")
                .HandleWith(_ =>
                {
                    var plr = capi.World.Player?.Entity;
                    if (plr == null) return TextCommandResult.Success("[tassblood] no player.");
                    long now = capi.World.ElapsedMilliseconds;
                    double yaw = plr.Pos.Yaw;
                    double fx = Math.Sin(yaw), fz = Math.Cos(yaw);
                    DepositLocal(plr.Pos.X, plr.Pos.Y + 0.8, plr.Pos.Z, KindPool, 6f, 3f, null, now, 1);
                    var fake = new Track();
                    for (int i = 1; i <= 8; i++)
                        DepositLocal(plr.Pos.X + fx * i * 1.1, plr.Pos.Y + 0.8, plr.Pos.Z + fz * i * 1.1,
                            KindTrail, Math.Max(1f, 4f - i * 0.4f), 1.2f, fake, now, 0);
                    return TextCommandResult.Success($"[tassblood] test blood laid ({spots.Count} spots, {waterTiles.Count} water tiles).");
                });

            api.Logger.Event("[TassHunting] blood visuals 0.9.0: client-local mode (sync spine parked).");
        }

        private void Warn(string site, Exception ex)
        {
            long now = capi?.World?.ElapsedMilliseconds ?? 0;
            if (now - lastWarnMs < 10000) return;
            lastWarnMs = now;
            capi?.Logger?.Warning("[TassHunting] blood {0} failed: {1}", site, ex.Message);
        }

        // ================= observation: entities -> blood events =============

        private void ClientTick(float dt)
        {
            try
            {
                var cfg = HuntingModSystem.Cfg;
                if (cfg == null || !cfg.BloodVisualsEnabled) return;
                var plr = capi.World.Player?.Entity;
                if (plr == null) return;
                long now = capi.World.ElapsedMilliseconds;

                ObserveEntities(cfg, plr, now);
                RenderSpots(cfg, plr, now);
                RenderWater(cfg, plr, now);
            }
            catch (Exception ex) { Warn("client tick", ex); }
        }

        private void ObserveEntities(HuntingConfig cfg, Entity plr, long now)
        {
            float range = cfg.BloodRenderDistanceBlocks + 8f;
            float range2 = range * range;
            bool dripPass = now >= nextDripPassMs;
            if (dripPass) nextDripPassMs = now + Math.Max(150, (int)(cfg.BloodDepositIntervalSeconds * 1000f));
            float trailScale = Math.Max(0f, cfg.BloodTrailScale);

            foreach (var ent in capi.World.LoadedEntities.Values)
            {
                if (ent == null) continue;
                double dx = ent.Pos.X - plr.Pos.X, dz = ent.Pos.Z - plr.Pos.Z;
                if (dx * dx + dz * dz > range2) continue;

                int stacks = ent.WatchedAttributes.GetInt("thbleed", 0);
                int hurtC = ent.WatchedAttributes.GetInt("onHurtCounter", 0);
                bool tracked = tracks.TryGetValue(ent.EntityId, out var tr);
                if (!tracked)
                {
                    if (stacks <= 0 && !(hurtC != 0 && ent.Alive == false))
                    {
                        // start tracking anything that CAN become interesting:
                        // alive with a hurt counter (so we catch its next hit)
                        if (hurtC == 0 && stacks <= 0) continue;
                    }
                    tracks[ent.EntityId] = tr = new Track { LastHurtCounter = hurtC, LastStacks = stacks };
                    tr.LastSeenMs = now;
                    continue; // never replay old hurts on first sight
                }
                tr.LastSeenMs = now;

                // 1. HURT BEAT: same synced attribute that drives the red
                //    flash - the shot AND every bleed DoT tick land here.
                if (hurtC != tr.LastHurtCounter)
                {
                    tr.LastHurtCounter = hurtC;
                    float dmg = ent.WatchedAttributes.GetFloat("onHurt", 0f);
                    if (cfg.SpawnSplatterOnDamage && dmg >= cfg.BloodOnHitMinDamage && trailScale > 0f)
                    {
                        // ONE SHOT per damage event (0.9.1 field: the pulse
                        // train read as delay - pulse 1 hid inside the mob)
                        float inten = Math.Min(4f, 1.0f + dmg * 0.3f) * trailScale;
                        DepositLocal(ent.Pos.X, WoundY(ent), ent.Pos.Z, KindHit, inten, 0.5f * trailScale, null, now,
                            ent.Alive ? 1 : 0);
                    }
                }

                // 2. BLEED TRAIL from the synced stack count
                tr.LastStacks = stacks;
                if (dripPass && ent.Alive && stacks > 0 && trailScale > 0f)
                {
                    var m = ent.Pos.Motion;
                    double horiz = Math.Sqrt(m.X * m.X + m.Z * m.Z);
                    float gait = horiz >= 0.08 ? Math.Max(0.1f, cfg.RunningBloodMult) : (horiz < 0.015 ? 0.85f : 1f);
                    DepositLocal(ent.Pos.X, WoundY(ent), ent.Pos.Z, KindTrail,
                        (0.8f + 0.5f * stacks) * trailScale * gait, 0.35f * stacks * trailScale * gait, tr, now, 0);
                }

                // 3. DEATH: pool + corpse bleed-out window
                if (!ent.Alive && !tr.DeathHandled)
                {
                    tr.DeathHandled = true;
                    float corpseStacks = Math.Max(stacks, tr.LastStacks);
                    if (corpseStacks <= 0 && hurtC != 0) corpseStacks = 1.5f; // one-shot kill still bleeds
                    corpseStacks *= Math.Max(0f, cfg.CorpseBloodScale);
                    if (corpseStacks > 0f)
                    {
                        int poolId = DepositLocal(ent.Pos.X, ent.Pos.Y + 0.15, ent.Pos.Z, KindPool,
                            1.5f + 0.8f * corpseStacks, 0.5f * corpseStacks, null, now, 1);
                        tr.CorpseStacks = corpseStacks;
                        tr.CorpseUntilMs = now + (long)(cfg.CorpseBleedSeconds * 1000f);
                        tr.NextCorpseMs = now + CorpseTickMs;
                        tr.LastSpotId = poolId;
                        tr.LastX = ent.Pos.X; tr.LastY = ent.Pos.Y; tr.LastZ = ent.Pos.Z;
                    }
                }

                // 4. corpse keeps feeding its pool
                if (tr.DeathHandled && now < tr.CorpseUntilMs && now >= tr.NextCorpseMs)
                {
                    tr.NextCorpseMs = now + CorpseTickMs;
                    if (tr.LastSpotId >= 0 && spots.TryGetValue(tr.LastSpotId, out var pool))
                        pool.Intensity = Math.Min(MaxIntensity, pool.Intensity + 0.35f * tr.CorpseStacks);
                    else
                        tr.LastSpotId = DepositLocal(tr.LastX, tr.LastY + 0.15, tr.LastZ, KindPool,
                            0.9f * tr.CorpseStacks, 0.5f * tr.CorpseStacks, null, now, 0);
                }
            }

            // prune stale tracks (entity unloaded / long idle, corpse done)
            if (tracks.Count > 64)
            {
                List<long> gone = null;
                foreach (var kv in tracks)
                    if (now - kv.Value.LastSeenMs > 15000 && now > kv.Value.CorpseUntilMs)
                        (gone = gone ?? new List<long>()).Add(kv.Key);
                if (gone != null) foreach (long id in gone) tracks.Remove(id);
            }
        }

        // ================= deposits (local ledger) ===========================

        /// <summary>Blood originates at the WOUND (~60% up the collision box),
        /// so drips visibly fall and splats land beneath the body.</summary>
        private static double WoundY(Entity ent)
        {
            float bodyTop = ent.CollisionBox?.Y2 ?? 0.8f;
            return ent.Pos.Y + bodyTop * 0.6;
        }

        private int DepositLocal(double x, double y, double z, byte kind, float intensity, float waterAmount, Track tr, long now, int spurts)
        {
            var cfg = HuntingModSystem.Cfg;
            if (!ResolveSurface(x, y, z, out double surfY, out bool isWater, out var waterKey)) return -1;

            if (isWater)
            {
                if (!cfg.WaterBloodEnabled || waterAmount <= 0f) return -1;
                if (tr != null)
                {
                    if (now < tr.NextWaterMs) { tr.LastSpotId = -1; return -1; }
                    tr.NextWaterMs = now + 1000;
                }
                waterTiles.TryGetValue(waterKey, out float cur);
                waterTiles[waterKey] = Math.Min(6f, cur + waterAmount);
                if (tr != null) tr.LastSpotId = -1;
                return -1;
            }

            // trail spacing: near drips grow the previous spot into a pool
            if (tr != null && tr.LastSpotId >= 0 && spots.TryGetValue(tr.LastSpotId, out var last))
            {
                double dx = x - tr.LastX, dz = z - tr.LastZ;
                float minSp = Math.Max(0.2f, cfg.BloodSpotMinSpacingBlocks);
                if (dx * dx + dz * dz < minSp * minSp)
                {
                    last.Intensity = Math.Min(MaxIntensity, last.Intensity + 0.6f);
                    if (last.Kind == KindTrail) last.Kind = KindPool;
                    return last.Id;
                }
            }

            // rain shortens the life of NEWLY deposited blood (no retroactive wash)
            long life = (long)(cfg.BloodSpotLifetimeSeconds * 1000f);
            float rainSpeed = GameMath.Clamp(cfg.RainClearSpeed, 0f, 2f);
            if (rainSpeed > 0.01f)
            {
                var clim = capi.World.BlockAccessor.GetClimateAt(
                    scratch.Set((int)Math.Floor(x), (int)Math.Floor(surfY) + 1, (int)Math.Floor(z)),
                    EnumGetClimateMode.NowValues);
                if (clim != null && clim.Rainfall > 0.1f && clim.Temperature > 1f
                    && capi.World.BlockAccessor.GetRainMapHeightAt((int)Math.Floor(x), (int)Math.Floor(z)) <= surfY + 1.01)
                {
                    life = (long)(life / (1f + rainSpeed));
                }
            }

            var spot = new Spot
            {
                Id = nextSpotId++,
                X = x, Y = surfY, Z = z,
                Intensity = Math.Min(MaxIntensity, intensity),
                BornMs = now,
                LifeMs = Math.Max(1000, life),
                FallHeight = (float)GameMath.Clamp(y - surfY, 0.0, 8.0),
                Kind = kind,
                SpurtsLeft = spurts
            };
            if (spot.FallHeight > 0.35f && cfg.BloodSplatter.Enabled)
            {
                spot.DropletsPending = true;
                spot.NextEmitMs = now + Math.Min(1200, (int)(spot.FallHeight * 150f));
            }
            if (tr != null && tr.LastSpotId >= 0 && kind == KindTrail)
            {
                double sx = x - tr.LastX, sz = z - tr.LastZ;
                if (sx * sx + sz * sz < 36.0)
                {
                    spot.HasSeg = true;
                    spot.PrevX = tr.LastX; spot.PrevY = tr.LastY; spot.PrevZ = tr.LastZ;
                }
            }
            spots[spot.Id] = spot;
            if (tr != null) { tr.LastSpotId = spot.Id; tr.LastX = x; tr.LastY = surfY; tr.LastZ = z; }

            // local cap
            if (spots.Count > Math.Max(64, cfg.BloodMaxSpots))
            {
                int oldest = -1; long oldestBorn = long.MaxValue;
                foreach (var kv in spots)
                    if (kv.Value.BornMs < oldestBorn) { oldestBorn = kv.Value.BornMs; oldest = kv.Key; }
                if (oldest >= 0) spots.Remove(oldest);
            }
            return spot.Id;
        }

        // ================= surface resolution ================================

        private bool ResolveSurface(double x, double y, double z, out double surfY, out bool isWater, out (int, int, int) waterKey)
        {
            surfY = 0; isWater = false; waterKey = default;
            var ba = capi.World.BlockAccessor;
            int ix = (int)Math.Floor(x), iz = (int)Math.Floor(z);
            int startY = (int)Math.Floor(y + 0.01);
            for (int i = 0; i <= 8; i++)
            {
                int cy = startY - i;
                if (cy < 1) return false;
                var fluid = ba.GetBlock(scratch.Set(ix, cy, iz), BlockLayersAccess.Fluid);
                if (fluid != null && fluid.IsLiquid())
                {
                    int top = cy;
                    for (int up = 1; up <= 6; up++)
                    {
                        var above = ba.GetBlock(scratch.Set(ix, cy + up, iz), BlockLayersAccess.Fluid);
                        if (above == null || !above.IsLiquid()) break;
                        top = cy + up;
                    }
                    isWater = true;
                    waterKey = (ix, top, iz);
                    return true;
                }
                var solid = ba.GetBlock(scratch.Set(ix, cy, iz), BlockLayersAccess.Solid);
                if (solid == null || solid.Id == 0) continue;
                float h = TopHeightOf(solid);
                if (h <= 0f) continue;
                surfY = cy + h;
                return true;
            }
            return false;
        }

        /// <summary>Within-voxel top height, 0 = passthrough. Vanilla
        /// snowlayer-N and THW thw-freshsnowlayer-N are N * 0.125 (code-based:
        /// thin layers have no collision box); else collision boxes.</summary>
        internal static float TopHeightOf(Block block)
        {
            string path = block.Code?.Path;
            if (path != null)
            {
                int idx = path.IndexOf("snowlayer-", StringComparison.Ordinal);
                if (idx >= 0 && int.TryParse(path.Substring(idx + 10), out int layer))
                    return GameMath.Clamp(layer, 1, 8) * 0.125f;
            }
            var boxes = block.CollisionBoxes;
            if (boxes == null || boxes.Length == 0) return 0f;
            float max = 0f;
            for (int i = 0; i < boxes.Length; i++)
                if (boxes[i].Y2 > max) max = boxes[i].Y2;
            return max;
        }

        // ================= particle props ====================================

        private static int ParseBloodColor(string hex, int fallback)
        {
            try
            {
                if (string.IsNullOrEmpty(hex)) return fallback;
                string h = hex.TrimStart('#');
                if (h.Length != 6) return fallback;
                int r = Convert.ToInt32(h.Substring(0, 2), 16);
                int g = Convert.ToInt32(h.Substring(2, 2), 16);
                int b = Convert.ToInt32(h.Substring(4, 2), 16);
                return ColorUtil.ToRgba(255, r, g, b);
            }
            catch { return fallback; }
        }

        private void EnsureParticleProps(HuntingConfig cfg)
        {
            float waterOpacity = GameMath.Clamp(cfg.WaterBloodMaxOpacity, 0.05f, 1f);
            bool soft = cfg.SoftWaterParticles;
            if (groundProps != null && appliedColorHex == cfg.BloodColorHex
                && appliedWaterOpacity == waterOpacity && appliedSoftWater == soft) return;
            appliedColorHex = cfg.BloodColorHex;
            appliedWaterOpacity = waterOpacity;
            appliedSoftWater = soft;
            int blood = ParseBloodColor(cfg.BloodColorHex, ColorUtil.ToRgba(255, 116, 8, 12));

            groundProps = new SimpleParticleProperties(
                1, 1, blood, new Vec3d(), new Vec3d(), new Vec3f(), new Vec3f(),
                4.6f, 0f, 0.25f, 0.5f, EnumParticleModel.Cube);

            // splatter: lazy 0.45-weight ballistics so the arc hangs and reads.
            // NO OPACITY FADE (0.9.2 field: blood must POP visually until the
            // end of its lifecycle) - the particle stays fully saturated for
            // flight AND ground-sit, and exits via late-accelerating SHRINK
            // (quadratic size decay set per shot) = soak-in right at the end.
            burstProps = new SimpleParticleProperties(
                4, 4, blood, new Vec3d(), new Vec3d(), new Vec3f(), new Vec3f(),
                2.5f, 0.45f, 0.12f, 0.28f, EnumParticleModel.Cube);
            burstProps.ShouldDieInLiquid = true;
            burstProps.OpacityEvolve = new EvolvingNatFloat(EnumTransformFunction.LINEAR, 0f);

            dropletProps = new SimpleParticleProperties(
                2, 3, blood, new Vec3d(), new Vec3d(),
                new Vec3f(-0.2f, -0.15f, -0.2f), new Vec3f(0.2f, -0.02f, 0.2f),
                1.1f, 0.9f, 0.1f, 0.2f, EnumParticleModel.Cube);
            dropletProps.ShouldDieInLiquid = true;
            dropletProps.OpacityEvolve = new EvolvingNatFloat(EnumTransformFunction.QUADRATIC, -255f);

            int wr = (blood >> 16) & 0xFF, wg = (blood >> 8) & 0xFF, wb = blood & 0xFF;
            int water = ColorUtil.ToRgba(Math.Max(6, (int)(130 * waterOpacity)), wr, wg, wb);
            waterProps = new SimpleParticleProperties(
                1, 1, water, new Vec3d(), new Vec3d(),
                new Vec3f(-0.02f, -0.01f, -0.02f), new Vec3f(0.02f, 0.015f, 0.02f),
                3.5f, 0f, 0.7f, 1.3f,
                soft ? EnumParticleModel.Quad : EnumParticleModel.Cube);
            waterProps.OpacityEvolve = new EvolvingNatFloat(EnumTransformFunction.QUADRATIC, -255f);
        }

        private static float Hash01(int seed)
        {
            unchecked
            {
                uint h = (uint)seed * 2654435761u;
                h ^= h >> 13; h *= 2246822519u; h ^= h >> 16;
                return (h & 0xFFFFFF) / (float)0x1000000;
            }
        }

        private void ApplyDecalState(bool ending, long remainMs, float pLife)
        {
            groundProps.LifeLength = ending ? Math.Min(pLife, remainMs / 1000f + 0.4f) : pLife;
            if (ending)
            {
                groundProps.OpacityEvolve = FadeOut;
                groundProps.MinVelocity.Set(0f, -0.02f, 0f);
                groundProps.AddVelocity.Set(0f, 0.01f, 0f);
            }
            else
            {
                groundProps.OpacityEvolve = NoFade;
                groundProps.MinVelocity.Set(0f, 0f, 0f);
                groundProps.AddVelocity.Set(0f, 0f, 0f);
            }
        }

        // ================= rendering =========================================

        private void RenderSpots(HuntingConfig cfg, Entity plr, long now)
        {
            if (spots.Count == 0) return;
            EnsureParticleProps(cfg);
            double px = plr.Pos.X, pz = plr.Pos.Z;
            float maxDist2 = cfg.BloodRenderDistanceBlocks * cfg.BloodRenderDistanceBlocks;

            List<int> dead = null;
            int rendered = 0;
            foreach (var kv in spots)
            {
                var s = kv.Value;
                long remainMs = s.BornMs + s.LifeMs - now;
                if (remainMs <= 0) { (dead = dead ?? new List<int>()).Add(kv.Key); continue; }
                if (rendered >= cfg.BloodMaxRenderedSpots) continue;
                double ddx = s.X - px, ddz = s.Z - pz;
                if (ddx * ddx + ddz * ddz > maxDist2) continue;
                rendered++;

                // falling droplets: fire once, before the splat materializes
                if (s.DropletsPending)
                {
                    s.DropletsPending = false;
                    if (cfg.BloodSplatter.Enabled && dropletProps != null)
                    {
                        var spd = cfg.BloodSplatter;
                        dropletProps.MinQuantity = Math.Max(1, spd.QtyMin / 3) + (int)(s.Intensity / 2f);
                        dropletProps.AddQuantity = 2;
                        dropletProps.MinSize = Math.Max(0.02f, Math.Min(spd.SizeMin, spd.SizeMax) * 0.7f);
                        dropletProps.MaxSize = Math.Max(dropletProps.MinSize, Math.Max(spd.SizeMin, spd.SizeMax) * 0.7f);
                        dropletProps.LifeLength = GameMath.Clamp(s.FallHeight * 0.2f, 0.4f, 1.4f);
                        dropletProps.MinPos.Set(s.X - 0.12, s.Y + s.FallHeight - 0.1, s.Z - 0.12);
                        dropletProps.AddPos.Set(0.24, 0.15, 0.24);
                        capi.World.SpawnParticles(dropletProps);
                    }
                }

                // spurt pulses: everything from the BloodSplatter block
                if (s.SpurtsLeft > 0 && now >= s.NextSpurtMs)
                {
                    s.SpurtsLeft--;
                    s.NextSpurtMs = now + 170;
                    var sp = cfg.BloodSplatter;
                    if (sp.Enabled && sp.QtyMax > 0)
                    {
                        float spdMin = Math.Max(0.05f, Math.Min(sp.SpreadMin, sp.SpreadMax));
                        float spdMax = Math.Max(spdMin, Math.Max(sp.SpreadMin, sp.SpreadMax));
                        burstProps.MinQuantity = Math.Max(1f, sp.QtyMin + (sp.QtyMax - sp.QtyMin) * GameMath.Clamp(s.Intensity / 4f, 0f, 1f));
                        burstProps.AddQuantity = 2f;
                        burstProps.MinSize = Math.Max(0.02f, Math.Min(sp.SizeMin, sp.SizeMax));
                        burstProps.MaxSize = Math.Max(burstProps.MinSize, Math.Max(sp.SizeMin, sp.SizeMax));
                        burstProps.LifeLength = Math.Max(0.2f, Math.Min(sp.LifetimeMin, sp.LifetimeMax)
                            + Math.Abs(sp.LifetimeMax - sp.LifetimeMin) * Hash01(kv.Key * 331 + s.SpurtsLeft * 13));
                        burstProps.MinVelocity.Set(-0.45f * spdMax, 0.8f * spdMin, -0.45f * spdMax);
                        burstProps.AddVelocity.Set(0.9f * spdMax, spdMax - 0.8f * spdMin, 0.9f * spdMax);
                        // full-color the whole life; QUADRATIC shrink back-loads
                        // the disappearance to the lifecycle's end (soak-in)
                        burstProps.SizeEvolve = new EvolvingNatFloat(EnumTransformFunction.QUADRATIC,
                            -0.85f * (burstProps.MinSize + burstProps.MaxSize) * 0.5f);
                        // WIDE spawn box: particles emerge at the body surface,
                        // not hidden inside the mesh (the invisible-shot bug)
                        burstProps.MinPos.Set(s.X - 0.35, s.Y + s.FallHeight - 0.05, s.Z - 0.35);
                        burstProps.AddPos.Set(0.7, 0.4, 0.7);
                        capi.World.SpawnParticles(burstProps);
                    }
                }

                if (!cfg.BloodTrails.Enabled) continue;
                if (now < s.NextEmitMs) continue;
                s.NextEmitMs = now + (int)(GameMath.Clamp(cfg.BloodRefreshSeconds, 1f, 15f) * 1000f);

                var tr = cfg.BloodTrails;
                long spotAgeMs = now - s.BornMs;
                float intenFrac = GameMath.Clamp(s.Intensity / MaxIntensity, 0f, 1f);
                int spotId = kv.Key;
                float tsMin = Math.Max(0.05f, Math.Min(tr.SizeMin, tr.SizeMax));
                float tsMax = Math.Max(tsMin, Math.Max(tr.SizeMin, tr.SizeMax));
                float tLifeMin = Math.Max(5f, Math.Min(tr.LifetimeMin, tr.LifetimeMax));
                float tLifeMax = Math.Max(tLifeMin, Math.Max(tr.LifetimeMin, tr.LifetimeMax));
                float tSprMin = Math.Max(0f, Math.Min(tr.SpreadMin, tr.SpreadMax));
                float tSprMax = Math.Max(tSprMin, Math.Max(tr.SpreadMin, tr.SpreadMax));
                int tQtyMin = Math.Max(1, Math.Min(tr.QtyMin, tr.QtyMax));
                int tQtyMax = Math.Max(tQtyMin, Math.Max(tr.QtyMin, tr.QtyMax));
                groundProps.MinQuantity = 1;
                groundProps.AddQuantity = 0;
                float pLife = GameMath.Clamp(cfg.BloodRefreshSeconds, 1f, 15f) * 1.15f;

                if (s.Kind == KindTrail && s.HasSeg)
                {
                    double sx = s.X - s.PrevX, sz = s.Z - s.PrevZ;
                    double segLen = Math.Sqrt(sx * sx + sz * sz);
                    float perBlock = tQtyMin + (tQtyMax - tQtyMin) * (0.5f * Hash01(spotId * 61) + 0.5f * intenFrac);
                    int drops = GameMath.Clamp((int)Math.Ceiling(segLen * perBlock), 1, 24);
                    for (int k = 0; k < drops; k++)
                    {
                        float dropLifeSec = tLifeMin + (tLifeMax - tLifeMin) * Hash01(spotId * 307 + k * 11);
                        long dropLifeMs = Math.Min((long)(dropLifeSec * 1000f), s.LifeMs);
                        long dropRemain = dropLifeMs - spotAgeMs;
                        if (dropRemain <= 0) continue;
                        long dropFadeWin = Math.Max(2500, dropLifeMs / 8);
                        bool dEnd = dropRemain <= dropFadeWin;
                        float dFade = dEnd ? 0.55f + 0.45f * GameMath.Clamp(dropRemain / (float)dropFadeWin, 0f, 1f) : 1f;
                        ApplyDecalState(dEnd, dropRemain, pLife);

                        float t = (k + 0.25f + 0.5f * Hash01(spotId * 271 + k * 31)) / drops;
                        float jit = tSprMin + (tSprMax - tSprMin) * Hash01(spotId * 199 + k * 7);
                        double lx = s.PrevX + sx * t + (Hash01(spotId * 211 + k * 17) - 0.5) * 2.0 * jit;
                        double lz = s.PrevZ + sz * t + (Hash01(spotId * 223 + k * 19 + 5) - 0.5) * 2.0 * jit;
                        double ly = s.PrevY + (s.Y - s.PrevY) * t;
                        if (!ResolveGroundY(lx, ly + 1.0, lz, out double gy)) continue;
                        float psize = GameMath.Lerp(tsMin, tsMax, 0.5f * Hash01(spotId * 239 + k * 23 + 11) + 0.5f * intenFrac) * dFade;
                        groundProps.MinSize = psize;
                        groundProps.MaxSize = psize;
                        groundProps.MinPos.Set(lx, gy + 0.02, lz);
                        groundProps.AddPos.Set(0, 0, 0);
                        capi.World.SpawnParticles(groundProps);
                    }
                }
                else
                {
                    long poolFadeWin = Math.Clamp((long)(s.LifeMs * 0.15f), 3000L, 30000L);
                    bool ending = remainMs <= poolFadeWin;
                    float fade = ending ? 0.55f + 0.45f * GameMath.Clamp(remainMs / (float)poolFadeWin, 0f, 1f) : 1f;
                    ApplyDecalState(ending, remainMs, pLife);
                    int count = GameMath.Clamp((int)Math.Round((double)tQtyMin + (tQtyMax - tQtyMin) * intenFrac), 1, 16);
                    float radius = (tSprMin + (tSprMax - tSprMin) * 0.5f) + 0.05f * s.Intensity;
                    for (int k = 0; k < count; k++)
                    {
                        float ang = Hash01(spotId * 97 + k * 13) * GameMath.TWOPI;
                        float rad = radius * (float)Math.Sqrt(Hash01(spotId * 131 + k * 29 + 7));
                        float variation = 0.85f + 0.3f * Hash01(spotId * 173 + k * 41 + 3);
                        float psize = GameMath.Lerp(tsMin, tsMax, intenFrac) * variation * fade;
                        groundProps.MinSize = psize;
                        groundProps.MaxSize = psize;
                        groundProps.MinPos.Set(s.X + Math.Sin(ang) * rad, s.Y + 0.02, s.Z + Math.Cos(ang) * rad);
                        groundProps.AddPos.Set(0, 0, 0);
                        capi.World.SpawnParticles(groundProps);
                    }
                }
            }
            if (dead != null) foreach (int id in dead) spots.Remove(id);
        }

        private bool ResolveGroundY(double x, double yStart, double z, out double surfY)
        {
            surfY = 0;
            var ba = capi.World.BlockAccessor;
            int ix = (int)Math.Floor(x), iz = (int)Math.Floor(z);
            int startY = (int)Math.Floor(yStart);
            for (int i = 0; i <= 4; i++)
            {
                int cy = startY - i;
                if (cy < 1) return false;
                var fluid = ba.GetBlock(scratch.Set(ix, cy, iz), BlockLayersAccess.Fluid);
                if (fluid != null && fluid.IsLiquid()) return false;
                var solid = ba.GetBlock(scratch.Set(ix, cy, iz), BlockLayersAccess.Solid);
                if (solid == null || solid.Id == 0) continue;
                float h = TopHeightOf(solid);
                if (h <= 0f) continue;
                surfY = cy + h;
                return true;
            }
            return false;
        }

        // ================= water (local sim + render) ========================

        private static readonly (int dx, int dz)[] Cardinals = { (1, 0), (-1, 0), (0, 1), (0, -1) };

        private void WaterSimTick(float dt)
        {
            try
            {
                var cfg = HuntingModSystem.Cfg;
                if (cfg == null || !cfg.BloodVisualsEnabled || !cfg.WaterBloodEnabled) return;
                if (waterTiles.Count == 0) return;

                var ba = capi.World.BlockAccessor;
                float keep = 1f - GameMath.Clamp(cfg.WaterBloodDecayPerSecond, 0.01f, 0.9f);
                float leakFrac = GameMath.Clamp(cfg.WaterBloodSpreadPerSecond, 0f, (1f - keep) / 4f);
                var next = new Dictionary<(int, int, int), float>(waterTiles.Count * 2);
                foreach (var kv in waterTiles)
                {
                    var (x, y, z) = kv.Key;
                    float amt = kv.Value;
                    Add(next, kv.Key, amt * keep);
                    float leak = amt * leakFrac;
                    if (leak <= 0f) continue;
                    foreach (var (dx, dz) in Cardinals)
                    {
                        var nk = (x + dx, y, z + dz);
                        if (next.ContainsKey(nk) || waterTiles.ContainsKey(nk)) { Add(next, nk, leak); continue; }
                        var fluid = ba.GetBlock(scratch.Set(x + dx, y, z + dz), BlockLayersAccess.Fluid);
                        if (fluid != null && fluid.IsLiquid()) Add(next, nk, leak);
                    }
                }
                List<(int, int, int)> cull = null;
                foreach (var kv in next)
                    if (kv.Value < WaterCull) (cull = cull ?? new List<(int, int, int)>()).Add(kv.Key);
                if (cull != null) foreach (var k in cull) { next.Remove(k); waterNextEmit.Remove(k); }
                while (next.Count > WaterTileCap)
                {
                    (int, int, int) weakest = default; float weakestAmt = float.MaxValue;
                    foreach (var kv in next)
                        if (kv.Value < weakestAmt) { weakestAmt = kv.Value; weakest = kv.Key; }
                    next.Remove(weakest);
                    waterNextEmit.Remove(weakest);
                }
                waterTiles.Clear();
                foreach (var kv in next) waterTiles[kv.Key] = kv.Value;
            }
            catch (Exception ex) { Warn("water sim", ex); }
        }

        private static void Add(Dictionary<(int, int, int), float> d, (int, int, int) k, float v)
        {
            d.TryGetValue(k, out float cur);
            d[k] = cur + v;
        }

        private void RenderWater(HuntingConfig cfg, Entity plr, long now)
        {
            if (waterTiles.Count == 0 || !cfg.TintSurroundingWater || !cfg.WaterBloodEnabled) return;
            EnsureParticleProps(cfg);
            double px = plr.Pos.X, pz = plr.Pos.Z;
            float maxDist2 = cfg.BloodRenderDistanceBlocks * cfg.BloodRenderDistanceBlocks;

            foreach (var kv in waterTiles)
            {
                if (kv.Value < 0.05f) continue;
                double dx = kv.Key.x + 0.5 - px, dz = kv.Key.z + 0.5 - pz;
                if (dx * dx + dz * dz > maxDist2) continue;
                waterNextEmit.TryGetValue(kv.Key, out long nextEmit);
                if (now < nextEmit) continue;
                waterNextEmit[kv.Key] = now + 2400;

                float amt = Math.Min(4f, kv.Value);
                float clotAmt = Math.Max(0f, cfg.WaterClotAmount);
                int clots = (int)((1f + amt) * clotAmt);
                if (clots < 1) continue;
                float wMin = Math.Max(0.05f, cfg.WaterClotSizeMin);
                float wMax = Math.Max(wMin, cfg.WaterClotSizeMax);
                float startSize = wMin * 0.6f;
                float endSize = wMin + (wMax - wMin) * GameMath.Clamp(amt / 4f, 0.3f, 1f);
                waterProps.MinQuantity = clots;
                waterProps.AddQuantity = 1;
                waterProps.MinSize = startSize;
                waterProps.MaxSize = startSize * 1.3f;
                waterProps.SizeEvolve = new EvolvingNatFloat(EnumTransformFunction.LINEAR, endSize - startSize);
                waterProps.MinPos.Set(kv.Key.x + 0.5 - 0.42, kv.Key.y + 0.35, kv.Key.z + 0.5 - 0.42);
                waterProps.AddPos.Set(0.84, 0.45, 0.84);
                capi.World.SpawnParticles(waterProps);
            }
        }

        // ================= teardown ==========================================

        public override void Dispose()
        {
            try
            {
                if (capi != null)
                {
                    if (tickId != 0) capi.Event.UnregisterGameTickListener(tickId);
                    if (waterTickId != 0) capi.Event.UnregisterGameTickListener(waterTickId);
                }
            }
            catch { }
            spots.Clear(); tracks.Clear(); waterTiles.Clear(); waterDisplay.Clear(); waterNextEmit.Clear();
            capi = null;
            tickId = waterTickId = 0;
            base.Dispose();
        }
    }
}
