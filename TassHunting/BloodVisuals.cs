// BLOOD VISUALS 0.9.0 - CLIENT-ONLY (user directive 2026-07-21: "kill any
// sort of server sync/multiplayer anything, just do it client only until we
// get client going"). The 0.6-0.8 server ledger + per-player sync spine is
// PARKED at commit bd8bac6 for the future multiplayer pass.
//
// HOW IT WORKS NOW - zero custom networking, zero visual latency:
//   - HIT splatter keys off the engine's "onHurtCounter"/"onHurt" watched
//     attributes (the red-flash signal) - reliable for real attacks.
//   - DoT-TICK splatter keys off BleedSystem's own "thbleedtick" counter
//     (0.9.3): the engine onHurt bump is gated by a 500ms invuln window and
//     drops internal Injury ticks near a hit, so the DoT needs its own
//     unconditional synced signal (decompile-verified, see BleedSystem).
//   - BleedSystem (server) also publishes its stack count as "thbleed"; the
//     client lays the drip trail locally from that.
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
            public int ArrowCount;   // arrows stuck when deposited - drives blood SIZE (user table)
            public long BornMs, LifeMs;
            public float FallHeight;
            public byte Kind;
            public bool HasSeg;
            public double PrevX, PrevY, PrevZ;
            public bool GroundEmitted;   // ground decal particles are spawned ONCE, then live their full lifetime
            public int SpurtsLeft;
            public long NextSpurtMs;
        }

        /// <summary>Per-entity observation state (hurt counter, trail anchor,
        /// corpse bleed window).</summary>
        private class Track
        {
            public int LastHurtCounter;
            public int LastBleedTick;  // dedicated DoT splatter signal (0.9.3)
            public int LastStacks;
            public long NextWaterMs;
            public int LastSpotId = -1;
            public double LastX, LastY, LastZ;
            public bool DeathHandled;
            public long LastSeenMs;
        }

        private readonly Dictionary<int, Spot> spots = new Dictionary<int, Spot>();
        private readonly Dictionary<long, Track> tracks = new Dictionary<long, Track>();
        private readonly Dictionary<(int x, int y, int z), float> waterTiles = new Dictionary<(int, int, int), float>();
        private readonly Dictionary<(int x, int y, int z), float> waterDisplay = new Dictionary<(int, int, int), float>();
        private readonly Dictionary<(int x, int y, int z), float> waterEmittedAt = new Dictionary<(int, int, int), float>();
        private readonly BlockPos scratch = new BlockPos(0);
        private long lastWarnMs = -10000;

        private SimpleParticleProperties groundProps, burstProps, waterProps;
        private string appliedColorHex;
        private float appliedWaterOpacity = -1f;
        private int bloodFresh, bloodAged; // ground blood dries fresh -> aged over the particle's life
        // last corpse-pool emit, captured for the chat-facing /tassbloodc readout
        // (point-and-ask: after a kill, ask what the pool actually did)
        private int lastPoolCount = -1;
        private float lastPoolLife;
        private long lastPoolSpotLifeMs;
        private long lastPoolAtMs = -1;
        private bool poolSpawnProbe;   // .tassbloodpool sets this; logs the next pool's per-particle placement once

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            tickId = api.Event.RegisterGameTickListener(ClientTick, 150);
            waterTickId = api.Event.RegisterGameTickListener(WaterSimTick, 1000);

            api.ChatCommands.Create("tassbloodc")
                .WithDescription("TassHunting blood (client-local) status")
                .HandleWith(_ =>
                {
                    string pool = lastPoolAtMs < 0
                        ? "no corpse pool emitted yet this session"
                        : $"last corpse pool: {lastPoolCount} particles, particle life {lastPoolLife:0.0}s, spot-record life {lastPoolSpotLifeMs / 1000f:0.0}s, emitted {(capi.World.ElapsedMilliseconds - lastPoolAtMs) / 1000f:0.0}s ago";
                    return TextCommandResult.Success(
                        $"[tassblood client] {spots.Count} spots, {waterTiles.Count} water tiles, {tracks.Count} tracked entities. {pool}");
                });

            api.ChatCommands.Create("tassbloodcolor")
                .WithDescription("Dump the RAW blood color numbers at every age (verify red, catch pink)")
                .HandleWith(_ =>
                {
                    var cfg = HuntingModSystem.Cfg;
                    EnsureParticleProps(cfg);
                    // RAW TRUTH, chat-facing (no log diving): the exact R,G,B the
                    // particle is BORN with at each age step. If any row is not
                    // red (R should dominate G and B), the color path is wrong -
                    // this is the measured number, not a claim.
                    var sb = new System.Text.StringBuilder();
                    sb.Append("[tassblood color] fresh #").Append(cfg.BloodColorHex)
                      .Append(" -> aged #").Append(cfg.BloodColorAgedHex).Append("\n");
                    sb.Append("fresh raw R,G,B = ").Append(CR(bloodFresh)).Append(',').Append(CG(bloodFresh)).Append(',').Append(CB(bloodFresh)).Append("\n");
                    sb.Append("aged  raw R,G,B = ").Append(CR(bloodAged)).Append(',').Append(CG(bloodAged)).Append(',').Append(CB(bloodAged)).Append("\n");
                    sb.Append("AgeColor lerp (what each drop is BORN, by spot age):\n");
                    foreach (float t in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
                    {
                        int c = AgeColor(t);
                        int r = CR(c), g = CG(c), b = CB(c);
                        string verdict = (r > 3 * Math.Max(1, g) && r > 3 * Math.Max(1, b)) ? "RED" : "NOT-RED";
                        sb.Append("  age ").Append((int)(t * 100)).Append("% : R,G,B = ")
                          .Append(r).Append(',').Append(g).Append(',').Append(b)
                          .Append("  [").Append(verdict).Append("]\n");
                    }
                    sb.Append("no color evolve is applied (0.11.3+): each drop keeps this color for life.");
                    return TextCommandResult.Success(sb.ToString());
                });

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

            api.ChatCommands.Create("tassbloodpool")
                .WithDescription("Lay ONE clean corpse pool at your feet and log exactly what it spawns")
                .HandleWith(_ =>
                {
                    var plr = capi.World.Player?.Entity;
                    if (plr == null) return TextCommandResult.Success("[tassblood] no player.");
                    long now = capi.World.ElapsedMilliseconds;
                    // one big pool, nothing else - isolates the pool from spray/trail
                    int id = DepositLocal(plr.Pos.X, plr.Pos.Y + 0.1, plr.Pos.Z, KindPool, MaxIntensity, 1f, null, now, 0);
                    poolSpawnProbe = true; // next pool render logs per-particle placement
                    return TextCommandResult.Success(
                        $"[tassblood] laid ONE pool (spot {id}) at your feet. Watch it, then run .tassbloodc for its measured life. Per-particle placement logged to client-main.log.");
                });

            // Client commands use the DOT prefix (.tassbloodc), NOT slash - slash
            // is server-side. Print the exact invocations so the prefix is never
            // a guessing game (VS: client=dot, server=slash).
            api.Logger.Event("[TassHunting] blood visuals ready. Diagnostics (CLIENT commands, dot prefix): .tassbloodc (status + last pool), .tassbloodcolor (raw color at each age), .tassbloodtest (lay test blood).");
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
                    tracks[ent.EntityId] = tr = new Track
                    {
                        LastHurtCounter = hurtC,
                        LastStacks = stacks,
                        LastBleedTick = ent.WatchedAttributes.GetInt("thbleedtick", 0)
                    };
                    tr.LastSeenMs = now;
                    continue; // never replay old hurts on first sight
                }
                tr.LastSeenMs = now;

                // 1a. HURT BEAT (arrow / melee hits): the engine's onHurt bump,
                //     reliable for real attacks. Gated by the on-hit min damage.
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
                            ent.Alive ? 1 : 0, Math.Max(1, stacks));
                    }
                }

                // 1b. BLEED TICK BEAT (0.9.3): the DEDICATED ungated counter -
                //     the engine onHurt path swallows internal DoT ticks inside
                //     the 500ms invuln window, and a real bleed tick is tiny
                //     (~0.14 for a hare) so the on-hit min-damage gate drops it
                //     too. NO damage floor here: the server already decided a
                //     tick landed. This is the "DoT has no splatter" fix.
                int bleedTick = ent.WatchedAttributes.GetInt("thbleedtick", 0);
                if (bleedTick != tr.LastBleedTick)
                {
                    tr.LastBleedTick = bleedTick;
                    if (cfg.SpawnSplatterOnDamage && trailScale > 0f && ent.Alive)
                    {
                        float bdmg = ent.WatchedAttributes.GetFloat("thbleeddmg", 0f);
                        float inten = Math.Min(4f, 1.0f + bdmg * 0.3f) * trailScale;
                        DepositLocal(ent.Pos.X, WoundY(ent), ent.Pos.Z, KindHit, inten, 0.5f * trailScale, null, now, 1, Math.Max(1, stacks));
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
                        (0.8f + 0.5f * stacks) * trailScale * gait, 0.35f * stacks * trailScale * gait, tr, now, 0, stacks);
                }

                // 3. DEATH: a corpse ALWAYS leaves a pool (user 2026-07-22: "where
                //    is the corpse pile of blood? i see the trail but no large
                //    corpse pile"). Every kill pools - melee, clean shot, arrow
                //    that already fell out, bleed-out - not just kills with a
                //    stuck arrow at the instant of death (the old corpseStacks>0
                //    gate is why melee/fell-out kills left NOTHING). Stacks only
                //    make it BIGGER; there is a solid baseline regardless.
                if (!ent.Alive && !tr.DeathHandled && cfg.CorpseBloodEnabled)
                {
                    tr.DeathHandled = true;
                    // baseline 1 "stack-equivalent" for any corpse, + the real
                    // bleed stacks it had. So a clean kill still pools; a heavily
                    // bled animal pools bigger. The pool is created ONCE, big -
                    // "Pool size" controls how wide, "Pool stays" how long.
                    float corpseStacks = 1f + Math.Max(stacks, tr.LastStacks);
                    // BIG pool intensity - this is the "bled out here" pool,
                    // not a trail dot. Base 4 + generous per-stack, capped.
                    float poolInten = Math.Min(MaxIntensity, 4f + 1.5f * corpseStacks);
                    // DIED IN WATER -> water blood, not a ground pool (user
                    // 2026-07-22). DepositLocal already routes to the water-tile
                    // system when it resolves water under the deposit; a corpse
                    // bleeding out in water should be a STRONG murk cloud, so the
                    // water amount here is big (2 + per-stack, up to the tile cap 6)
                    // - not the thin 0.5 a live drip uses. Deposited at the WOUND
                    // height so a submerged/floating body's water column is found.
                    float corpseWater = Math.Min(6f, 2f + corpseStacks);
                    DepositLocal(ent.Pos.X, WoundY(ent), ent.Pos.Z, KindPool,
                        poolInten, corpseWater, null, now, 1);
                }
            }

            // prune stale tracks (entity unloaded / long idle)
            if (tracks.Count > 64)
            {
                List<long> gone = null;
                foreach (var kv in tracks)
                    if (now - kv.Value.LastSeenMs > 15000)
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

        private int DepositLocal(double x, double y, double z, byte kind, float intensity, float waterAmount, Track tr, long now, int spurts, int arrowCount = 0)
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
                ArrowCount = arrowCount,
                BornMs = now,
                LifeMs = Math.Max(1000, life),
                FallHeight = (float)GameMath.Clamp(y - surfY, 0.0, 8.0),
                Kind = kind,
                SpurtsLeft = spurts
            };
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

        /// <summary>Parse #RRGGBB to a particle color int. ToRgba(a,r,g,b) =
        /// (a<<24)|(r<<16)|(g<<8)|b IS the correct packing - DECOMPILE-VERIFIED:
        /// the renderer unpacks R=>>16, G=>>8, B=&0xFF (EntityParticleGrasshopper),
        /// and every vanilla colored particle (forge/fire/bees) uses ToRgba.
        /// ColorFromRgba SWAPS r/b (rendered blood BLUE, 0.10.4 mistake). The
        /// evolve deltas below operate on true R/G/B and match ToRgba directly.
        /// (2026-07-22: do NOT flip this again - it is verified, see memory.)</summary>
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

        // Channel accessors for an ARGB-packed particle color (red = >>16).
        private static int CR(int c) => (c >> 16) & 0xFF;
        private static int CG(int c) => (c >> 8) & 0xFF;
        private static int CB(int c) => c & 0xFF;

        private void EnsureParticleProps(HuntingConfig cfg)
        {
            float waterOpacity = GameMath.Clamp(cfg.WaterBloodMaxOpacity, 0.02f, 1f);
            if (groundProps != null && appliedColorHex == cfg.BloodColorHex + "|" + cfg.BloodColorAgedHex
                && appliedWaterOpacity == waterOpacity) return;
            appliedColorHex = cfg.BloodColorHex + "|" + cfg.BloodColorAgedHex;
            appliedWaterOpacity = waterOpacity;
            int blood = ParseBloodColor(cfg.BloodColorHex, ColorUtil.ToRgba(255, 116, 8, 12));
            bloodFresh = blood;
            bloodAged = ParseBloodColor(cfg.BloodColorAgedHex, ColorUtil.ToRgba(255, 58, 4, 6));

            // Ground decals are CUBES (field 2026-07-22: Quads billboard to
            // face the camera - blood does not do that, looked jarring; cubes
            // sit still and read far better than both Quads and the other
            // hunting mods). Cubes for everything, air and ground.
            groundProps = new SimpleParticleProperties(
                1, 1, blood, new Vec3d(), new Vec3d(), new Vec3f(), new Vec3f(),
                4.6f, 0f, 0.25f, 0.5f, EnumParticleModel.Cube);

            // splatter: real blood, so VINTAGE STORY's own gravity (GravityEffect
            // 1.0 = the engine's semi-realistic fall, ~9 m/s equivalent) rather
            // than a hand-picked rate. The arc reads because the launch SPEED
            // (Splatter spread) is set high, not because gravity is dialed weak.
            //
            // BLOOD DRIES DARK, THEN SHRINKS AWAY - it does NOT fade (2026-07-22,
            // PROVEN with engine source + arithmetic, after pink-at-end-of-life):
            //
            // THE PINK, ACTUALLY FIXED (2026-07-22): removing the opacity fade did
            // NOT fix it - aged blood was STILL pink. Because the pink was never
            // the fade; it was the per-channel COLOR EVOLVE. RedEvolve/GreenEvolve/
            // BlueEvolve on a ToRgba SimpleParticleProperties is a path vanilla
            // NEVER uses (vanilla animates color only via HsvaColor on Advanced
            // particles; fixed-Color particles only ever OpacityEvolve). Animating
            // the three byte channels independently drifts the hue - the drop goes
            // pink partway through its life. FIX: NO color evolve at all. Each drop
            // is BORN its final color and never changes hue. Aging is done by
            // picking the SPAWN color as a lerp fresh->aged based on how old the
            // SPOT is (older spot = darker drops), so a trail still visibly dries
            // over time - but no single particle ever shifts toward pink.
            // (Vanish is by SizeEvolve shrink, set per-drop; opacity never fades.)
            burstProps = new SimpleParticleProperties(
                4, 4, blood, new Vec3d(), new Vec3d(), new Vec3f(), new Vec3f(),
                2.5f, 1f, 0.12f, 0.28f, EnumParticleModel.Cube);
            burstProps.ShouldDieInLiquid = true;
            // NO color evolve, NO opacity fade. Splatter is born its color (set
            // per-emit) and vanishes by shrinking. Nothing shifts its hue.


            // WATER BLOOD = a RE-SKIN of vanilla FloatingSedimentParticles - the
            // MUDDY stir-up effect (user 2026-07-22: "100% copy the muddy emitter
            // and just change the color; it already has that watery expand from
            // small to big floaty feel"). All values copied from the engine's
            // FloatingSedimentParticles (API.Common), only the color is ours:
            //   Quad model, lives IN water (DieInLiquid=false) and dies at the
            //   surface/air (DieInAir=true), zero gravity, ~9s life, starts tiny
            //   (0.25) and EXPANDS via LINEAR SizeEvolve +3 (-> ~3.25 = the "small
            //   to big" bloom), QUADRATIC -50 opacity (holds then fades late, NOT
            //   the -255 that cratered), VertexFlags 512 (soft water compositing),
            //   and a tiny +-1/16 random drift = the floaty jitter.
            // Alpha is a DIRECT percentage of WaterBloodMaxOpacity.
            int waterAlpha = GameMath.Clamp((int)Math.Round(waterOpacity * 255f), 3, 255);
            int water = ColorUtil.ToRgba(waterAlpha, CR(blood), CG(blood), CB(blood));
            waterProps = new SimpleParticleProperties(
                1, 1, water, new Vec3d(), new Vec3d(),
                new Vec3f(-1f / 16f, -1f / 16f, -1f / 16f), new Vec3f(1f / 16f, 1f / 16f, 1f / 16f),
                9f,      // LifeLength (sediment: 9s)
                0f,      // GravityEffect (floats)
                0.25f, 0.25f, // MinSize/MaxSize (sediment starts at 0.25)
                EnumParticleModel.Quad);
            waterProps.ShouldDieInLiquid = false;  // lives IN the water
            waterProps.ShouldDieInAir = true;      // dies when it reaches the surface/air
            waterProps.VertexFlags = 512;          // soft water compositing (sediment uses 512)
            waterProps.SizeEvolve = new EvolvingNatFloat(EnumTransformFunction.LINEAR, 3f);    // EXPAND small->big (sediment)
            waterProps.OpacityEvolve = new EvolvingNatFloat(EnumTransformFunction.QUADRATIC, -50f); // hold then fade (sediment)
        }

        /// <summary>The pink-free VANISH: hold size, then shrink to nothing at
        /// the end. SizeEvolve is firstval + QUADRATIC term = startSize +
        /// sign(factor)*(factor*seq)^2. To land at exactly 0 at seq=1 the term
        /// must equal -startSize, so factor = -sqrt(startSize). The square keeps
        /// the size near-full early (0.75x at seq .5) and collapses it fast at the
        /// end (0.19x at .9, 0 at 1.0). Because opacity NEVER fades, the shrinking
        /// dot stays a solid dark red until it is gone - it can never wash to pink
        /// over a bright background the way an alpha fade does.</summary>
        /// <summary>Blood color at spawn, lerped fresh->aged by the SPOT's age
        /// fraction (0 = just deposited, 1 = old). This is how blood "dries" now -
        /// per-drop at birth, NOT by animating channels mid-life (which drifted to
        /// pink). A fresh spot's drops are bright red; an old spot's drops are
        /// born already dark. Alpha is forced to 255 (blood never fades).</summary>
        private int AgeColor(float ageFrac)
        {
            float t = GameMath.Clamp(ageFrac, 0f, 1f);
            int r = (int)Math.Round(GameMath.Lerp(CR(bloodFresh), CR(bloodAged), t));
            int g = (int)Math.Round(GameMath.Lerp(CG(bloodFresh), CG(bloodAged), t));
            int b = (int)Math.Round(GameMath.Lerp(CB(bloodFresh), CB(bloodAged), t));
            return ColorUtil.ToRgba(255, r, g, b);
        }

        private static EvolvingNatFloat ShrinkAway(float startSize)
        {
            float factor = -(float)Math.Sqrt(Math.Max(0.0001f, startSize));
            return new EvolvingNatFloat(EnumTransformFunction.QUADRATIC, factor);
        }

        /// <summary>Blood SIZE multiplier by number of stuck arrows (user table
        /// 2026-07-22): 1 arrow -> 0.75, 2 -> 1.0, 3+ -> 1.5 (capped). Applied to
        /// the config base size, so a lone-arrow wound is small and a 3-arrow
        /// wound is a big gush.</summary>
        private static float ArrowSizeMult(int arrows)
        {
            if (arrows <= 0) return 0.75f; // hit blood before any bleed stack
            if (arrows == 1) return 0.75f;
            if (arrows == 2) return 1.0f;
            return 1.5f;                    // 3+ capped
        }

        /// <summary>Lerp two ARGB ints channel-wise (blood dries fresh->aged).</summary>
        private static float Hash01(int seed)
        {
            unchecked
            {
                uint h = (uint)seed * 2654435761u;
                h ^= h >> 13; h *= 2246822519u; h ^= h >> 16;
                return (h & 0xFFFFFF) / (float)0x1000000;
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
                        // It is all BLOOD: splatter lives as long as a trail drop
                        // (user 2026-07-22) - it just LAUNCHES explosively, arcs,
                        // lands, and settles into the same long-lasting stain.
                        float splatLife = Math.Max(0.2f, Math.Min(sp.LifetimeMin, sp.LifetimeMax)
                            + Math.Abs(sp.LifetimeMax - sp.LifetimeMin) * Hash01(kv.Key * 331 + s.SpurtsLeft * 13));
                        burstProps.LifeLength = splatLife;
                        burstProps.MinVelocity.Set(-0.45f * spdMax, 0.8f * spdMin, -0.45f * spdMax);
                        burstProps.AddVelocity.Set(0.9f * spdMax, spdMax - 0.8f * spdMin, 0.9f * spdMax);
                        // Born ONE solid color (by the spot's age), NO evolve, NO
                        // fade - vanishes by shrinking. Fresh spurts are bright red,
                        // spurts off an older wound are darker; never shifts hue.
                        float splatAge = GameMath.Clamp((now - s.BornMs) / (float)Math.Max(1, s.LifeMs), 0f, 1f);
                        burstProps.Color = AgeColor(splatAge);
                        burstProps.SizeEvolve = ShrinkAway((burstProps.MinSize + burstProps.MaxSize) * 0.5f);
                        // WIDE spawn box: particles emerge at the body surface,
                        // not hidden inside the mesh (the invisible-shot bug)
                        burstProps.MinPos.Set(s.X - 0.35, s.Y + s.FallHeight - 0.05, s.Z - 0.35);
                        burstProps.AddPos.Set(0.7, 0.4, 0.7);
                        capi.World.SpawnParticles(burstProps);
                    }
                }

                // GROUND DECAL - spawned exactly ONCE per spot (0.9.12: was
                // re-emitted every ~4.6s, and the seams between disposable
                // particle batches read as blinking). Each drop is now a SINGLE
                // particle that lives the spot's full lifetime, sinking slowly
                // through the block and fading over its whole tail - born once,
                // dies once, no re-emit, no blink.
                if (!cfg.BloodTrails.Enabled || s.GroundEmitted) continue;
                s.GroundEmitted = true;

                var tr = cfg.BloodTrails;
                float intenFrac = GameMath.Clamp(s.Intensity / MaxIntensity, 0f, 1f);
                // SIZE by stuck-arrow count (user table): 1->0.75, 2->1.0, 3+->1.5
                float arrowSize = ArrowSizeMult(s.ArrowCount);
                int spotId = kv.Key;
                float tsMin = Math.Max(0.05f, Math.Min(tr.SizeMin, tr.SizeMax));
                float tsMax = Math.Max(tsMin, Math.Max(tr.SizeMin, tr.SizeMax));
                float tSprMin = Math.Max(0f, Math.Min(tr.SpreadMin, tr.SpreadMax));
                float tSprMax = Math.Max(tSprMin, Math.Max(tr.SpreadMin, tr.SpreadMax));
                int tQtyMin = Math.Max(1, Math.Min(tr.QtyMin, tr.QtyMax));
                int tQtyMax = Math.Max(tQtyMin, Math.Max(tr.QtyMin, tr.QtyMax));
                float tLifeMin = Math.Max(1f, Math.Min(tr.LifetimeMin, tr.LifetimeMax));
                float tLifeMax = Math.Max(tLifeMin, Math.Max(tr.LifetimeMin, tr.LifetimeMax));

                // One persistent decal particle per drop. Each drop is born ONE
                // solid color and NEVER changes hue (no color evolve = no pink -
                // the mid-life channel animation was the pink, not the fade).
                // "Aging" across the scene comes from the SPOT's age: a fresh spot
                // deposits bright-red drops, an old spot deposits dark drops - set
                // per-drop via AgeColor(spot age fraction) below. Blood keeps FULL
                // opacity its whole life and vanishes only by SHRINKING (SizeEvolve
                // per-drop). No OpacityEvolve, no Red/Green/BlueEvolve at all.
                float spotAge = GameMath.Clamp((now - s.BornMs) / (float)Math.Max(1, s.LifeMs), 0f, 1f);
                groundProps.Color = AgeColor(spotAge);
                groundProps.MinQuantity = 1;
                groundProps.AddQuantity = 0;
                groundProps.GravityEffect = 0f;
                groundProps.WithTerrainCollision = false;

                if (s.Kind == KindTrail && s.HasSeg)
                {
                    double sx = s.X - s.PrevX, sz = s.Z - s.PrevZ;
                    double segLen = Math.Sqrt(sx * sx + sz * sz);
                    float perBlock = tQtyMin + (tQtyMax - tQtyMin) * (0.5f * Hash01(spotId * 61) + 0.5f * intenFrac);
                    int drops = GameMath.Clamp((int)Math.Ceiling(segLen * perBlock), 1, 24);
                    for (int k = 0; k < drops; k++)
                    {
                        float t = (k + 0.25f + 0.5f * Hash01(spotId * 271 + k * 31)) / drops;
                        float jit = tSprMin + (tSprMax - tSprMin) * Hash01(spotId * 199 + k * 7);
                        double lx = s.PrevX + sx * t + (Hash01(spotId * 211 + k * 17) - 0.5) * 2.0 * jit;
                        double lz = s.PrevZ + sz * t + (Hash01(spotId * 223 + k * 19 + 5) - 0.5) * 2.0 * jit;
                        double ly = s.PrevY + (s.Y - s.PrevY) * t;
                        if (!ResolveGroundY(lx, ly + 1.0, lz, out double gy)) continue;
                        float psize = GameMath.Lerp(tsMin, tsMax, 0.5f * Hash01(spotId * 239 + k * 23 + 11) + 0.5f * intenFrac) * arrowSize;
                        float dropLife = tLifeMin + (tLifeMax - tLifeMin) * Hash01(spotId * 307 + k * 11);
                        groundProps.LifeLength = dropLife;
                        groundProps.MinSize = psize;
                        groundProps.MaxSize = psize;
                        // HOLD size, then SHRINK to nothing at the end (the
                        // pink-free vanish - opaque dark dot -> gone, no fade).
                        groundProps.SizeEvolve = ShrinkAway(psize);
                        // SINK 50% of its own height, spread over ITS lifetime
                        // (velocity = half-height / life -> total sink = half a
                        // cube, whatever the life; no static number)
                        groundProps.MinVelocity.Set(0f, -(0.5f * psize) / dropLife, 0f);
                        groundProps.AddVelocity.Set(0f, 0f, 0f);
                        groundProps.MinPos.Set(lx, gy + 0.02, lz);
                        groundProps.AddPos.Set(0, 0, 0);
                        capi.World.SpawnParticles(groundProps);
                    }
                }
                else
                {
                    bool isCorpsePool = s.Kind == KindPool;
                    if (isCorpsePool && !cfg.CorpseBloodEnabled) continue;

                    // CORPSE POOL (user 2026-07-22: "a large corpse pile of blood
                    // like youd see in real life"). A death pool is NOT a trail
                    // dot - it gets its own BIG, DENSE sizing so it reads as a
                    // real pool: many particles, big particles, filled disc.
                    // CorpseSpreadMult scales how wide it spreads. Non-corpse
                    // spots (the rare hit-mark cluster) keep the thin trail sizing.
                    int count;
                    float radius, poolSizeMin, poolSizeMax;
                    if (isCorpsePool)
                    {
                        float spread = Math.Max(1f, cfg.CorpseSpreadMult);
                        // DENSE fill: lots of particles so the disc is solid, not
                        // dotted. Scales with how much the animal bled (intensity).
                        count = GameMath.Clamp((int)Math.Round((28 + 34 * intenFrac) * spread), 24, 240);
                        // WIDE: base radius ~0.6 block, up with intensity, x spread.
                        radius = (0.55f + 0.5f * intenFrac) * spread;
                        // BIG particles (a pool is thick), independent of the thin
                        // trail size config. Bigger animals (more intensity) pool
                        // with bigger blobs.
                        poolSizeMin = 0.9f + 0.6f * intenFrac;
                        poolSizeMax = 1.5f + 1.2f * intenFrac;
                    }
                    else
                    {
                        count = GameMath.Clamp((int)Math.Round((double)tQtyMin + (tQtyMax - tQtyMin) * intenFrac), 1, 16);
                        radius = (tSprMin + (tSprMax - tSprMin) * 0.5f) + 0.05f * s.Intensity;
                        poolSizeMin = tsMin; poolSizeMax = tsMax;
                    }
                    for (int k = 0; k < count; k++)
                    {
                        float ang = Hash01(spotId * 97 + k * 13) * GameMath.TWOPI;
                        // bias toward the CENTER (sqrt would be uniform disc; a
                        // lower power packs more blood in the middle = a real pool
                        // that is thick at the body and thins at the edge).
                        float rad = radius * (float)Math.Pow(Hash01(spotId * 131 + k * 29 + 7), 0.75);
                        float variation = 0.85f + 0.3f * Hash01(spotId * 173 + k * 41 + 3);
                        float psize = GameMath.Lerp(poolSizeMin, poolSizeMax, Hash01(spotId * 401 + k * 7)) * variation
                            * (isCorpsePool ? 1f : arrowSize);
                        // A corpse pool has its OWN lifetime (it lingers far longer
                        // than a trail dot); +-20% ragged per particle so the pool
                        // dries from the edges in rather than blinking out at once.
                        float dropLife = isCorpsePool
                            ? Math.Max(1f, cfg.CorpsePoolLifetimeSeconds) * (0.8f + 0.4f * Hash01(spotId * 349 + k * 17))
                            : tLifeMin + (tLifeMax - tLifeMin) * Hash01(spotId * 349 + k * 17);
                        groundProps.LifeLength = dropLife;
                        groundProps.MinSize = psize;
                        groundProps.MaxSize = psize;
                        // HOLD size, then SHRINK to nothing at the end (pink-free).
                        groundProps.SizeEvolve = ShrinkAway(psize);
                        // A POOL STAYS PUT (user 2026-07-22: "make it last the
                        // configured time ABOVE ground"). The old downward sink
                        // (-0.5*psize/life) started at the surface and drove the
                        // pool UNDERGROUND within the first third of its life - it
                        // was alive but buried = the "sinks too fast". Pool: NO
                        // sink, sits flat on the ground its whole life. Trail
                        // drops keep the gentle soak-in sink (small, reads right).
                        float sinkY = isCorpsePool ? 0f : -(0.5f * psize) / dropLife;
                        groundProps.MinVelocity.Set(0f, sinkY, 0f);
                        groundProps.AddVelocity.Set(0f, 0f, 0f);
                        groundProps.MinPos.Set(s.X + Math.Sin(ang) * rad, s.Y + 0.02, s.Z + Math.Cos(ang) * rad);
                        groundProps.AddPos.Set(0, 0, 0);
                        capi.World.SpawnParticles(groundProps);
                    }
                    // PROBE (.tassbloodpool): compare the pool's spawn Y to the
                    // real block top under it. If spawn Y is BELOW the surface,
                    // particles render inside the ground = look swallowed (the
                    // "thins fast" suspect). Logged once per probe request.
                    if (isCorpsePool && poolSpawnProbe)
                    {
                        poolSpawnProbe = false;
                        bool resolved = ResolveGroundY(s.X, s.Y + 1.0, s.Z, out double trueTop);
                        capi.Logger.Notification(
                            "[TassHunting] POOL PROBE: spawned {0} particles at Y={1:0.00}; block-top under pool = {2} (Y={3:0.00}); spawn is {4} the surface. size {5:0.00}-{6:0.00}, radius {7:0.00}, life ~{8:0.0}s.",
                            count, s.Y + 0.02, resolved ? "found" : "NOT FOUND", trueTop,
                            !resolved ? "?" : (s.Y + 0.02 >= trueTop ? "ABOVE (good)" : "BELOW (buried!)"),
                            poolSizeMin, poolSizeMax, radius, Math.Max(1f, cfg.CorpsePoolLifetimeSeconds));
                    }
                    // DIAGNOSTIC (per standing rule: every stubborn-bug iteration
                    // ships visible instrumentation, preferably CHAT-FACING). Every
                    // corpse pool records its measured count + lifetime for the
                    // /tassbloodc readout - so "pool lasts 1-5s" is a number you
                    // point-and-ask for after a kill, not a guess or a log dive.
                    if (isCorpsePool)
                    {
                        lastPoolCount = count;
                        // record the ACTUAL per-particle LifeLength last set (not
                        // the config value) so /tassbloodc reports what really got
                        // handed to the engine - if these differ, that IS the bug.
                        lastPoolLife = groundProps.LifeLength;
                        lastPoolSpotLifeMs = s.LifeMs;
                        lastPoolAtMs = now;
                        if (cfg.BloodDiagnostics)
                            capi.Logger.Notification(
                                "[TassHunting] corpse POOL emit: {0} particles, radius {1:0.00}, size {2:0.00}-{3:0.00}, life {4:0.0}s, intensity {5:0.0}",
                                count, radius, poolSizeMin, poolSizeMax, lastPoolLife, s.Intensity);
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
                if (cull != null) foreach (var k in cull) { next.Remove(k); waterEmittedAt.Remove(k); }
                while (next.Count > WaterTileCap)
                {
                    (int, int, int) weakest = default; float weakestAmt = float.MaxValue;
                    foreach (var kv in next)
                        if (kv.Value < weakestAmt) { weakestAmt = kv.Value; weakest = kv.Key; }
                    next.Remove(weakest);
                    waterEmittedAt.Remove(weakest);
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
                // ONE-SHOT, like the vanilla sediment effect (user 2026-07-22:
                // "the murky water starts one shot then slowly fades" - I had a
                // 2.4s re-emit loop that vanilla does NOT have, so the bloom kept
                // restarting and you saw fresh red over and over). A tile blooms
                // ONCE, then again ONLY if its blood strength meaningfully RISES
                // (new blood arriving) - never on a timer, never while it just
                // sits/decays. waterEmittedAt stores the strength we last bloomed
                // at (survives the diffusion rebuild; culled keys pruned with it).
                waterEmittedAt.TryGetValue(kv.Key, out float emittedAt);
                if (kv.Value <= emittedAt + 0.5f) continue; // already bloomed at >= this strength
                waterEmittedAt[kv.Key] = kv.Value;

                // Amount only drives HOW MANY sediment-style puffs bloom this beat
                // (more blood = denser murk); the look (size 0.25 -> ~3.25 expand,
                // 9s life, float, soft) is the fixed sediment recipe set in
                // EnsureParticleProps - do NOT override size/evolve here.
                float amt = Math.Min(4f, kv.Value);
                float clotAmt = Math.Max(0f, cfg.WaterClotAmount);
                int clots = (int)((1f + amt) * clotAmt);
                if (clots < 1) continue;
                waterProps.MinQuantity = clots;
                waterProps.AddQuantity = 1;
                // spawn across the tile and through its lower/mid depth so the
                // murk fills the water volume (reads from the side, not just top)
                waterProps.MinPos.Set(kv.Key.x + 0.5 - 0.42, kv.Key.y + 0.25, kv.Key.z + 0.5 - 0.42);
                waterProps.AddPos.Set(0.84, 0.5, 0.84);
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
            spots.Clear(); tracks.Clear(); waterTiles.Clear(); waterDisplay.Clear(); waterEmittedAt.Clear();
            capi = null;
            tickId = waterTickId = 0;
            base.Dispose();
        }
    }
}
