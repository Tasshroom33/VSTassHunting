// BLOOD VISUALS (0.6.0): TassHunting renders its OWN blood - BloodTrail is no
// longer part of the stack. Design goal from the user: a player who logs in
// AFTER the shot can still pick up and follow the trail. That forces the
// The-Hunter-style split of authority:
//
//   SERVER  owns a blood-spot LEDGER (trail drips, hit splashes, death pools)
//           and a WATER-TILE diffusion field (blood spreading in water).
//           Deposits come from BleedSystem's active stacks.
//   CLIENT  renders the mirrored ledger as long-lived stationary cube
//           particles re-emitted on a cycle (the decal-look trick: zero
//           velocity, zero gravity, resting exactly on the surface), and
//           water tiles as swim-on-liquid clumps. No blocks are ever written;
//           no shader needed.
//
// NETWORKING (user directive 2026-07-21, performance-first): no blanket
// broadcasts. Every send is PER-PLAYER and PROXIMITY-SCOPED - a per-player
// sent-ids set means a spot is delivered when (and only when) a player is
// within sync range of it: at deposit time if they are nearby, or later when
// they WALK into range, or at login (empty sent-set = everything near them).
// Nobody near any blood = zero packets. Water tiles re-send at 1 Hz only to
// players in range of them (the client stales out unrefreshed tiles).
//
// Surface precision (no-gaps law): deposit positions resolve the REAL top of
// the block under the entity - snowlayer-N = N * 0.125 (vanilla and THW place
// the same snowlayer codes), everything else from collision boxes; blocks with
// no collision (grass, flowers) are scanned through downward. Blood never
// hovers and never clips under snow.
//
// Rate budgets: deposit tick early-outs when nothing bleeds and nothing is
// ledgered; diffusion is 1 Hz over a capped (2048) tile set with at most 4
// fluid lookups per tile; ledger capped (default 4096, oldest pruned). Every
// tick body, packet handler and command is try/catch-wrapped with rate-limited
// warn logging - blood must never take down a server or client.

using System;
using System.Collections.Generic;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace TassHunting
{
    [ProtoContract]
    public class BloodSpotDto
    {
        [ProtoMember(1)] public int Id;
        [ProtoMember(2)] public double X;
        [ProtoMember(3)] public double Y;   // resolved surface render height
        [ProtoMember(4)] public double Z;
        [ProtoMember(5)] public float Intensity;
        [ProtoMember(6)] public int RemainMs; // client computes its own expiry
        [ProtoMember(7)] public byte Kind;    // 0 trail, 1 hit, 2 pool
        [ProtoMember(8)] public int AgeMs;    // fresh spots splash on arrival; old ones (login/walk-up) don't
        [ProtoMember(9)] public float FallHeight; // wound height above the surface: drives falling droplets
        [ProtoMember(10)] public bool HasSeg;     // trail spot continuing a path: client draws a drop LINE
        [ProtoMember(11)] public double PrevX;    // previous anchor of the segment
        [ProtoMember(12)] public double PrevY;
        [ProtoMember(13)] public double PrevZ;
    }

    [ProtoContract]
    public class BloodSpotsPacket
    {
        [ProtoMember(1)] public List<BloodSpotDto> Spots = new List<BloodSpotDto>();
    }

    [ProtoContract]
    public class WaterBloodTileDto
    {
        [ProtoMember(1)] public int X;
        [ProtoMember(2)] public int Y;
        [ProtoMember(3)] public int Z;
        [ProtoMember(4)] public float Amount;
    }

    [ProtoContract]
    public class WaterBloodPacket
    {
        [ProtoMember(1)] public List<WaterBloodTileDto> Tiles = new List<WaterBloodTileDto>();
    }

    /// <summary>Server-initiated removal (admin clear / ledger cap prune) -
    /// without this, clients render ghost blood until natural expiry.</summary>
    [ProtoContract]
    public class BloodClearPacket
    {
        [ProtoMember(1)] public List<int> Ids = new List<int>();
        [ProtoMember(2)] public bool All;
    }

    public class BloodVisuals : ModSystem
    {
        public const string ChannelName = "tasshuntingblood";
        private const byte KindTrail = 0, KindHit = 1, KindPool = 2;

        // Internal pacing constants (the LOOK constants that stayed config-free;
        // water decay/spread graduated to config dials in 0.6.3).
        private const float MaxIntensity = 8f;
        private const int CorpseTickMs = 1200;
        private const float WaterCull = 0.008f;
        private const int WaterTileCap = 2048;
        private const int WaterBroadcastCap = 512;
        private const int WaterStaleMs = 6500;       // client drops unrefreshed tiles
        private const int SendChunkSize = 400;
        private const float SyncMarginBlocks = 24f;  // sync range = render distance + this
        private const int WarnIntervalMs = 10000;    // rate-limited error logging

        // ---------------- server ----------------

        private class Spot
        {
            public int Id;
            public double X, Y, Z;
            public float Intensity;
            public long BornMs;
            public long LifeMs;      // per-spot: rain-deposited blood lives shorter
            public float FallHeight; // wound height above surface at deposit time
            public byte Kind;
            public bool HasSeg;      // trail continuation: client draws a drop line back to Prev
            public double PrevX, PrevY, PrevZ;
        }

        private class TrailState
        {
            public int LastSpotId = -1;
            public double LastX, LastY, LastZ; // LastY = previous SURFACE height (segment endpoint)
            public long NextWaterMs; // water inflow gate: 1/s per entity, not per deposit tick
        }

        private class CorpseBleed
        {
            public double X, Y, Z;
            public float Stacks;
            public long UntilMs;
            public long NextMs;
            public int PoolSpotId = -1; // the ONE pool this corpse feeds (grown, not re-stacked)
        }

        private static BloodVisuals instance;

        private ICoreServerAPI sapi;
        private IServerNetworkChannel serverChannel;
        private long depositTickId, waterTickId;

        private readonly Dictionary<int, Spot> spots = new Dictionary<int, Spot>();
        private readonly Dictionary<long, TrailState> trails = new Dictionary<long, TrailState>();
        private readonly List<CorpseBleed> corpses = new List<CorpseBleed>();
        private readonly Dictionary<(int x, int y, int z), float> waterTiles = new Dictionary<(int, int, int), float>();
        // per-player delivery ledger: which spot ids this player already has
        private readonly Dictionary<string, HashSet<int>> sentIds = new Dictionary<string, HashSet<int>>();
        private readonly HashSet<int> dirtySpots = new HashSet<int>(); // grown/updated: resend even if sent
        private int nextSpotId = 1;
        private long nextPassMs;
        private long packetsSent, spotsDeposited;
        private long lastWarnMs = -WarnIntervalMs;
        private readonly BlockPos scratch = new BlockPos(0); // dimension 0: blood is an overworld feature

        public override void Start(ICoreAPI api)
        {
            api.Network.RegisterChannel(ChannelName)
                .RegisterMessageType<BloodSpotsPacket>()
                .RegisterMessageType<WaterBloodPacket>()
                .RegisterMessageType<BloodClearPacket>();
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            instance = this;
            serverChannel = api.Network.GetChannel(ChannelName);

            var cfg = HuntingModSystem.Cfg;
            if (cfg != null && cfg.BloodVisualsEnabled)
            {
                // fixed fast clock; the tick paces itself from config so the
                // drip-rate dial applies LIVE (no world rejoin)
                depositTickId = api.Event.RegisterGameTickListener(DepositTick, 250);
                waterTickId = api.Event.RegisterGameTickListener(WaterTick, 1000);
                api.Event.OnEntityDeath += OnEntityDeath;
                api.Event.PlayerDisconnect += OnPlayerDisconnect;
            }

            if (api.ModLoader.IsModEnabled("bloodtrail"))
                api.Logger.Warning("[TassHunting] BloodTrail is installed - both mods render blood, expect double visuals. TassHunting 0.6.0 replaces it fully; consider removing BloodTrail.");

            RegisterServerCommands(api);
        }

        private void WarnRateLimited(string site, Exception ex)
        {
            long now = sapi?.World?.ElapsedMilliseconds ?? capi?.World?.ElapsedMilliseconds ?? 0;
            if (now - lastWarnMs < WarnIntervalMs) return;
            lastWarnMs = now;
            (sapi?.Logger ?? capi?.Logger)?.Warning("[TassHunting] blood visuals {0} failed: {1}", site, ex.Message);
        }

        /// <summary>Blood originates at the WOUND (~60% up the collision box),
        /// not at the feet - feet are ON the ground, so fall height was always
        /// ~0 and nothing ever visibly fell (playtest 2026-07-21: "why does the
        /// blood just start at the top of the block instead of falling from
        /// their center of mass").</summary>
        private static double WoundY(Entity ent)
        {
            float bodyTop = ent.CollisionBox?.Y2 ?? 0.8f;
            return ent.Pos.Y + bodyTop * 0.6;
        }

        /// <summary>Called from the health-damage postfix on EVERY hit -
        /// deliberately independent of the bleed DoT proc (chance roll, alive
        /// gate). Splashes contact blood for qualifying hits, and guarantees a
        /// death pool for one-shot kills where no bleed stack ever existed
        /// (field 2026-07-21: the chicken must bleed).</summary>
        public static void NotifyDamage(Entity victim, DamageSource src, float damage)
        {
            var self = instance;
            var cfg = HuntingModSystem.Cfg;
            if (self?.sapi == null || cfg == null || !cfg.BloodVisualsEnabled || !cfg.BloodOnHitEnabled) return;
            if (victim == null || src == null || victim.World?.Side != EnumAppSide.Server) return;
            if (damage < cfg.BloodOnHitMinDamage) return;
            if (src.Type != EnumDamageType.PiercingAttack && src.Type != EnumDamageType.SlashingAttack) return;
            if (cfg.BleedPlayerCausedOnly && !(src.GetCauseEntity() is EntityPlayer)) return;
            if (!cfg.BleedAffectsPlayers && victim is EntityPlayer) return;
            try
            {
                float trailScale = Math.Max(0f, cfg.BloodTrailScale);
                float inten = Math.Min(4f, 1.0f + damage * 0.3f) * trailScale;
                if (inten > 0f)
                    self.Deposit(victim.Pos.X, WoundY(victim), victim.Pos.Z, KindHit, inten, 0.5f * trailScale, null);
                if (!victim.Alive && BleedSystem.StacksOn(victim.EntityId) == 0)
                {
                    float stacksEquiv = GameMath.Clamp(1f + damage * 0.35f, 1f, 4f) * Math.Max(0f, cfg.CorpseBloodScale);
                    if (stacksEquiv > 0f)
                        self.RegisterCorpseBleed(victim.Pos.X, victim.Pos.Y, victim.Pos.Z, stacksEquiv);
                }
                self.PushSyncNow(); // spurt must land on the hit beat, not the next tick
            }
            catch (Exception ex) { self.WarnRateLimited("damage hook", ex); }
        }

        /// <summary>Blood spurt synced to the bleed DoT beat - the hurt sound
        /// and red flash come with visible blood (playtest 2026-07-21: the DoT
        /// damage type is Injury, which NotifyDamage's slash/pierce gate
        /// rightly ignores, so this needs its own entry point).</summary>
        public static void NotifyBleedTick(Entity victim, int stacks)
        {
            var self = instance;
            var cfg = HuntingModSystem.Cfg;
            if (self?.sapi == null || cfg == null || !cfg.BloodVisualsEnabled || !cfg.BloodOnHitEnabled) return;
            if (victim == null || !victim.Alive) return;
            try
            {
                float trailScale = Math.Max(0f, cfg.BloodTrailScale);
                float inten = Math.Min(4f, 1.0f + 0.5f * stacks) * trailScale;
                if (inten <= 0f) return;
                self.Deposit(victim.Pos.X, WoundY(victim), victim.Pos.Z, KindHit, inten, 0.35f * stacks * trailScale, null);
                self.PushSyncNow();
            }
            catch (Exception ex) { self.WarnRateLimited("bleed tick spurt", ex); }
        }

        /// <summary>Immediate proximity-scoped delivery of whatever just got
        /// deposited/dirtied - damage-beat spurts cannot wait for the next
        /// deposit pass.</summary>
        private void PushSyncNow()
        {
            var cfg = HuntingModSystem.Cfg;
            if (cfg == null) return;
            SyncPlayers(cfg, sapi.World.ElapsedMilliseconds);
        }

        /// <summary>Immediate death pool + a record that keeps feeding it for
        /// the corpse bleed-out window. Used by both the stacked-bleeder death
        /// path and the one-shot-kill path.</summary>
        private void RegisterCorpseBleed(double x, double y, double z, float stacks)
        {
            if (stacks <= 0f) return; // CorpseBloodScale 0 = feature off
            var cfg = HuntingModSystem.Cfg;
            long now = sapi.World.ElapsedMilliseconds;
            int poolId = Deposit(x, y, z, KindPool, 1.5f + 0.8f * stacks, 0.5f * stacks, null);
            corpses.Add(new CorpseBleed
            {
                X = x, Y = y, Z = z, Stacks = stacks,
                UntilMs = now + (long)(cfg.CorpseBleedSeconds * 1000f),
                NextMs = now + CorpseTickMs,
                PoolSpotId = poolId
            });
        }

        private void OnEntityDeath(Entity entity, DamageSource src)
        {
            try
            {
                var cfg = HuntingModSystem.Cfg;
                if (cfg == null || !cfg.BloodVisualsEnabled || entity == null) return;
                int stacks = BleedSystem.StacksOn(entity.EntityId);
                if (stacks <= 0) return; // one-shot kills are handled by NotifyDamage

                RegisterCorpseBleed(entity.Pos.X, entity.Pos.Y, entity.Pos.Z,
                    stacks * Math.Max(0f, cfg.CorpseBloodScale));
                trails.Remove(entity.EntityId);
            }
            catch (Exception ex) { WarnRateLimited("death hook", ex); }
        }

        private void OnPlayerDisconnect(IServerPlayer plr)
        {
            try { if (plr?.PlayerUID != null) sentIds.Remove(plr.PlayerUID); } catch { }
        }

        private void DepositTick(float dt)
        {
            try
            {
                var cfg = HuntingModSystem.Cfg;
                if (cfg == null || !cfg.BloodVisualsEnabled) return;

                // EARLY OUT: the overwhelmingly common server state is "nothing
                // bleeding, nothing ledgered" - this tick must cost near zero then.
                bool anyLedger = spots.Count > 0 || corpses.Count > 0;
                var bleeders = BleedSystem.SnapshotActive();
                if (!anyLedger && bleeders.Count == 0) return;

                long now = sapi.World.ElapsedMilliseconds;
                // self-paced from config (live dial; the listener runs at 250ms)
                if (now < nextPassMs) return;
                nextPassMs = now + Math.Max(250, (int)(cfg.BloodDepositIntervalSeconds * 1000f));

                // 1. live bleeders drip a trail
                HashSet<long> aliveBleeders = bleeders.Count > 0 ? new HashSet<long>() : null;
                float trailScale = Math.Max(0f, cfg.BloodTrailScale);
                float runMult = Math.Max(0.1f, cfg.RunningBloodMult);
                foreach (var (ent, stacks) in bleeders)
                {
                    if (ent == null || !ent.Alive) continue;
                    aliveBleeders?.Add(ent.EntityId);
                    if (trailScale <= 0f) continue; // trails dialed off
                    if (!trails.TryGetValue(ent.EntityId, out var ts))
                        trails[ent.EntityId] = ts = new TrailState();
                    // GAIT (BloodTrail thresholds on per-tick motion length):
                    // sprinting tears the wound open, standing seeps
                    var m = ent.Pos.Motion;
                    double horiz = Math.Sqrt(m.X * m.X + m.Z * m.Z);
                    float gait = horiz >= 0.08 ? runMult : (horiz < 0.015 ? 0.85f : 1f);
                    // water blood leads at the collision-box FRONT of a mover
                    double fwdX = 0, fwdZ = 0;
                    if (horiz > 0.01)
                    {
                        double half = 0.45;
                        var cb = ent.CollisionBox;
                        if (cb != null) half = Math.Max(cb.XSize, cb.ZSize) * 0.5 * 0.9;
                        fwdX = m.X / horiz * half;
                        fwdZ = m.Z / horiz * half;
                    }
                    Deposit(ent.Pos.X, WoundY(ent), ent.Pos.Z,
                        KindTrail, (0.8f + 0.5f * stacks) * trailScale * gait, 0.35f * stacks * trailScale * gait, ts, fwdX, fwdZ);
                }

                // trail state of entities that stopped bleeding is dropped
                if (trails.Count > 0)
                {
                    List<long> gone = null;
                    foreach (var id in trails.Keys)
                        if (aliveBleeders == null || !aliveBleeders.Contains(id))
                            (gone = gone ?? new List<long>()).Add(id);
                    if (gone != null) foreach (var id in gone) trails.Remove(id);
                }

                // 2. corpses bleed out: GROW their one pool (a corpse over water
                // has no pool spot - Deposit keeps routing into the water tiles)
                for (int i = corpses.Count - 1; i >= 0; i--)
                {
                    var c = corpses[i];
                    if (now >= c.UntilMs) { corpses.RemoveAt(i); continue; }
                    if (now < c.NextMs) continue;
                    c.NextMs = now + CorpseTickMs;
                    if (c.PoolSpotId >= 0 && spots.TryGetValue(c.PoolSpotId, out var pool))
                    {
                        pool.Intensity = Math.Min(MaxIntensity, pool.Intensity + 0.35f * c.Stacks);
                        dirtySpots.Add(pool.Id);
                    }
                    else c.PoolSpotId = Deposit(c.X, c.Y, c.Z, KindPool, 0.9f * c.Stacks, 0.5f * c.Stacks, null);
                }

                // 3. expiry + cap prune (server side; clients expire on their own clock)
                if (spots.Count > 0)
                {
                    List<int> dead = null;
                    foreach (var kv in spots)
                        if (now - kv.Value.BornMs > kv.Value.LifeMs)
                            (dead = dead ?? new List<int>()).Add(kv.Key);
                    if (dead != null) foreach (int id in dead) RemoveSpot(id);
                    // cap prune: oldest first, and TELL the clients that have
                    // them (expiry needs no packet - clients expire on RemainMs)
                    int cap = Math.Max(64, cfg.BloodMaxSpots);
                    if (spots.Count > cap)
                    {
                        var byAge = new List<Spot>(spots.Values);
                        byAge.Sort((a, b) => a.BornMs.CompareTo(b.BornMs));
                        int excess = spots.Count - cap;
                        var removedIds = new List<int>(excess);
                        for (int i = 0; i < excess; i++) removedIds.Add(byAge[i].Id);
                        foreach (var kv in sentIds)
                        {
                            List<int> mine = null;
                            foreach (int id in removedIds)
                                if (kv.Value.Contains(id)) (mine = mine ?? new List<int>()).Add(id);
                            if (mine != null && sapi.World.PlayerByUid(kv.Key) is IServerPlayer p
                                && p.ConnectionState == EnumClientState.Playing)
                            {
                                serverChannel.SendPacket(new BloodClearPacket { Ids = mine }, p);
                                packetsSent++;
                            }
                        }
                        foreach (int id in removedIds) RemoveSpot(id);
                    }
                }

                // 4. proximity-scoped delivery
                SyncPlayers(cfg, now);
            }
            catch (Exception ex) { WarnRateLimited("deposit tick", ex); }
        }

        private void RemoveSpot(int id)
        {
            spots.Remove(id);
            dirtySpots.Remove(id);
            foreach (var kv in sentIds) kv.Value.Remove(id);
        }

        /// <summary>Per-player, proximity-scoped spot delivery. A spot goes to a
        /// player when it is within sync range AND (they never got it, or it
        /// changed). Late joiners and players walking up to old blood are the
        /// same case: ids missing from their sent-set. No players near any
        /// blood = no packets at all.</summary>
        private void SyncPlayers(HuntingConfig cfg, long now)
        {
            if (spots.Count == 0) { dirtySpots.Clear(); return; }
            var players = sapi.World.AllOnlinePlayers;
            if (players == null || players.Length == 0) { dirtySpots.Clear(); return; }

            float syncRange = cfg.BloodRenderDistanceBlocks + SyncMarginBlocks;
            float syncRange2 = syncRange * syncRange;
            List<BloodSpotDto> batch = null;

            foreach (var iplr in players)
            {
                var plr = iplr as IServerPlayer;
                var pent = plr?.Entity;
                if (pent == null || plr.ConnectionState != EnumClientState.Playing) continue;
                if (!sentIds.TryGetValue(plr.PlayerUID, out var sent))
                    sentIds[plr.PlayerUID] = sent = new HashSet<int>();

                double px = pent.Pos.X, pz = pent.Pos.Z;
                batch?.Clear();
                foreach (var kv in spots)
                {
                    var s = kv.Value;
                    bool needs = dirtySpots.Contains(s.Id) || !sent.Contains(s.Id);
                    if (!needs) continue;
                    double dx = s.X - px, dz = s.Z - pz;
                    if (dx * dx + dz * dz > syncRange2) continue;
                    int remain = (int)Math.Max(0, s.LifeMs - (now - s.BornMs));
                    if (remain <= 0) continue;
                    (batch = batch ?? new List<BloodSpotDto>()).Add(new BloodSpotDto
                    { Id = s.Id, X = s.X, Y = s.Y, Z = s.Z, Intensity = s.Intensity, RemainMs = remain, Kind = s.Kind, AgeMs = (int)(now - s.BornMs), FallHeight = s.FallHeight, HasSeg = s.HasSeg, PrevX = s.PrevX, PrevY = s.PrevY, PrevZ = s.PrevZ });
                    sent.Add(s.Id);
                    if (batch.Count >= SendChunkSize)
                    {
                        serverChannel.SendPacket(new BloodSpotsPacket { Spots = new List<BloodSpotDto>(batch) }, plr);
                        packetsSent++;
                        batch.Clear();
                    }
                }
                if (batch != null && batch.Count > 0)
                {
                    serverChannel.SendPacket(new BloodSpotsPacket { Spots = new List<BloodSpotDto>(batch) }, plr);
                    packetsSent++;
                }
            }
            dirtySpots.Clear();
        }

        /// <summary>One blood event at world position (x,y,z). Resolves the real
        /// surface below; routes to a water tile or a ground spot. For trail
        /// deposits (ts != null) close-together drips GROW the previous spot
        /// into a pool instead of stacking new spots. fwdX/fwdZ lead the WATER
        /// deposit to the front of the moving animal's collision box (playtest:
        /// the bow wave, not a wake behind it). Returns the ground spot id this
        /// blood went into, or -1 (water, or no surface).</summary>
        private int Deposit(double x, double y, double z, byte kind, float intensity, float waterAmount, TrailState ts, double fwdX = 0, double fwdZ = 0)
        {
            var cfg = HuntingModSystem.Cfg;
            if (!ResolveSurface(x, y, z, out double surfY, out bool isWater, out var waterKey)) return -1;
            long now = sapi.World.ElapsedMilliseconds;

            if (isWater)
            {
                if (!cfg.WaterBloodEnabled || waterAmount <= 0f) return -1;
                if (ts != null)
                {
                    // trail drips hit the water at DEPOSIT-TICK rate (0.35s) -
                    // ungated that pumped ~3 units/s/stack and stains lasted
                    // forever (field 2026-07-21). One inflow per second.
                    if (now < ts.NextWaterMs) { ts.LastSpotId = -1; return -1; }
                    ts.NextWaterMs = now + 1000;
                }
                // lead the blood to the FRONT of the moving animal
                if (fwdX != 0 || fwdZ != 0)
                {
                    int ox = (int)Math.Floor(x + fwdX), oz = (int)Math.Floor(z + fwdZ);
                    if (ox != waterKey.Item1 || oz != waterKey.Item3)
                    {
                        var f = sapi.World.BlockAccessor.GetBlock(scratch.Set(ox, waterKey.Item2, oz), BlockLayersAccess.Fluid);
                        if (f != null && f.IsLiquid()) waterKey = (ox, waterKey.Item2, oz);
                    }
                }
                bool newTile = !waterTiles.ContainsKey(waterKey);
                waterTiles.TryGetValue(waterKey, out float cur);
                waterTiles[waterKey] = Math.Min(6f, cur + waterAmount);
                // first blood in a column: push NOW - a fast animal is in and
                // out of the stream before the 1 Hz water broadcast (playtest)
                if (newTile) SendWaterToNearbyPlayers(cfg);
                if (ts != null) { ts.LastSpotId = -1; } // trail broken by water
                return -1;
            }

            // trail spacing: near the last drip -> grow it (pooling), else new spot
            if (ts != null && ts.LastSpotId >= 0 && spots.TryGetValue(ts.LastSpotId, out var last))
            {
                double dx = x - ts.LastX, dz = z - ts.LastZ;
                float minSp = Math.Max(0.2f, cfg.BloodSpotMinSpacingBlocks);
                if (dx * dx + dz * dz < minSp * minSp)
                {
                    last.Intensity = Math.Min(MaxIntensity, last.Intensity + 0.6f);
                    if (last.Kind == KindTrail) last.Kind = KindPool;
                    dirtySpots.Add(last.Id);
                    return last.Id;
                }
            }

            // RAIN (BloodTrail rule: only NEWLY deposited blood gets the rain
            // lifetime, no retroactive washing): sky-exposed + raining + warm
            // enough that it is rain, not snow. Rainmap is approximation-grade
            // here (lifetime only, never block targeting).
            long life = (long)(cfg.BloodSpotLifetimeSeconds * 1000f);
            if (cfg.BloodRainEnabled)
            {
                var clim = sapi.World.BlockAccessor.GetClimateAt(
                    scratch.Set((int)Math.Floor(x), (int)Math.Floor(surfY) + 1, (int)Math.Floor(z)),
                    EnumGetClimateMode.NowValues);
                if (clim != null && clim.Rainfall > 0.1f && clim.Temperature > 1f
                    && sapi.World.BlockAccessor.GetRainMapHeightAt((int)Math.Floor(x), (int)Math.Floor(z)) <= surfY + 1.01)
                {
                    life = Math.Min(life, (long)(cfg.BloodRainLifetimeSeconds * 1000f));
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
                Kind = kind
            };
            // trail continuation: remember the previous anchor so the client
            // can lay a drop LINE along the path segment (playtest: a line of
            // blood, not periodic splats)
            if (ts != null && ts.LastSpotId >= 0 && kind == KindTrail)
            {
                double sx = x - ts.LastX, sz = z - ts.LastZ;
                double segLen2 = sx * sx + sz * sz;
                if (segLen2 < 36.0) // teleport/despawn guard: no 6+ block lines
                {
                    spot.HasSeg = true;
                    spot.PrevX = ts.LastX; spot.PrevY = ts.LastY; spot.PrevZ = ts.LastZ;
                }
            }
            spots[spot.Id] = spot;
            spotsDeposited++;
            if (ts != null) { ts.LastSpotId = spot.Id; ts.LastX = x; ts.LastY = surfY; ts.LastZ = z; }
            return spot.Id;
        }

        /// <summary>Walk down from the entity's feet (max 8 blocks) to the first
        /// liquid or collidable surface. Snowlayer heights come from the block
        /// CODE (N * 0.125) because thin layers have no collision box; blocks
        /// with no collision at all (grass, flowers) are scanned through.</summary>
        private bool ResolveSurface(double x, double y, double z, out double surfY, out bool isWater, out (int, int, int) waterKey)
        {
            surfY = 0; isWater = false; waterKey = default;
            var ba = sapi.World.BlockAccessor;
            int ix = (int)Math.Floor(x), iz = (int)Math.Floor(z);
            int startY = (int)Math.Floor(y + 0.01);

            for (int i = 0; i <= 8; i++)
            {
                int cy = startY - i;
                if (cy < 1) return false;

                var fluid = ba.GetBlock(scratch.Set(ix, cy, iz), BlockLayersAccess.Fluid);
                if (fluid != null && fluid.IsLiquid())
                {
                    // find the top of the liquid column (swimming entity is inside it)
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
                if (h <= 0f) continue; // no collision (grass etc.): keep scanning down
                surfY = cy + h;
                return true;
            }
            return false;
        }

        /// <summary>Within-voxel top height of a block, 0 = passthrough.</summary>
        internal static float TopHeightOf(Block block)
        {
            string path = block.Code?.Path;
            if (path != null)
            {
                // vanilla "snowlayer-N" OR THW "thw-freshsnowlayer-N" - both are
                // N * 0.125 (THW shapes are 2N-voxel; same matcher THW itself uses).
                // Thin layers have no collision box, hence code-based, not box-based.
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

        // ---------------- water diffusion ----------------

        private static readonly (int dx, int dz)[] Cardinals = { (1, 0), (-1, 0), (0, 1), (0, -1) };

        private void WaterTick(float dt)
        {
            try
            {
                var cfg = HuntingModSystem.Cfg;
                if (cfg == null || !cfg.BloodVisualsEnabled || !cfg.WaterBloodEnabled) return;
                if (waterTiles.Count == 0) return; // EARLY OUT: no blood in any water

                var ba = sapi.World.BlockAccessor;
                // config dials; spread clamped so keep + 4*leak <= 1 (mass can
                // never grow, whatever the user dials in)
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
                        // only spread into actual water (blood does not climb the shore)
                        if (next.ContainsKey(nk) || waterTiles.ContainsKey(nk))
                        { Add(next, nk, leak); continue; }
                        var fluid = ba.GetBlock(scratch.Set(x + dx, y, z + dz), BlockLayersAccess.Fluid);
                        if (fluid != null && fluid.IsLiquid()) Add(next, nk, leak);
                    }
                }

                List<(int, int, int)> cull = null;
                foreach (var kv in next)
                    if (kv.Value < WaterCull) (cull = cull ?? new List<(int, int, int)>()).Add(kv.Key);
                if (cull != null) foreach (var k in cull) next.Remove(k);

                while (next.Count > WaterTileCap)
                {
                    (int, int, int) weakest = default; float weakestAmt = float.MaxValue;
                    foreach (var kv in next)
                        if (kv.Value < weakestAmt) { weakestAmt = kv.Value; weakest = kv.Key; }
                    next.Remove(weakest);
                }

                waterTiles.Clear();
                foreach (var kv in next) waterTiles[kv.Key] = kv.Value;

                SendWaterToNearbyPlayers(cfg);
            }
            catch (Exception ex) { WarnRateLimited("water tick", ex); }
        }

        private static void Add(Dictionary<(int, int, int), float> d, (int, int, int) k, float v)
        {
            d.TryGetValue(k, out float cur);
            d[k] = cur + v;
        }

        /// <summary>Water tiles re-send at 1 Hz, but ONLY the tiles within sync
        /// range of each player, and only to players that have any. The client
        /// drops tiles not refreshed within 6.5s, so walking away self-cleans.</summary>
        private void SendWaterToNearbyPlayers(HuntingConfig cfg)
        {
            if (waterTiles.Count == 0) return;
            var players = sapi.World.AllOnlinePlayers;
            if (players == null || players.Length == 0) return;

            float syncRange = cfg.BloodRenderDistanceBlocks + SyncMarginBlocks;
            float syncRange2 = syncRange * syncRange;
            List<WaterBloodTileDto> batch = null;

            foreach (var iplr in players)
            {
                var plr = iplr as IServerPlayer;
                var pent = plr?.Entity;
                if (pent == null || plr.ConnectionState != EnumClientState.Playing) continue;
                double px = pent.Pos.X, pz = pent.Pos.Z;

                batch?.Clear();
                foreach (var kv in waterTiles)
                {
                    double dx = kv.Key.x + 0.5 - px, dz = kv.Key.z + 0.5 - pz;
                    if (dx * dx + dz * dz > syncRange2) continue;
                    (batch = batch ?? new List<WaterBloodTileDto>()).Add(new WaterBloodTileDto
                    { X = kv.Key.x, Y = kv.Key.y, Z = kv.Key.z, Amount = kv.Value });
                    if (batch.Count >= WaterBroadcastCap) break; // strongest-N not needed: range already bounds it
                }
                if (batch != null && batch.Count > 0)
                {
                    serverChannel.SendPacket(new WaterBloodPacket { Tiles = new List<WaterBloodTileDto>(batch) }, plr);
                    packetsSent++;
                }
            }
        }

        // ---------------- server commands (diagnostics law) ----------------

        private void RegisterServerCommands(ICoreServerAPI api)
        {
            var p = api.ChatCommands.Parsers;
            api.ChatCommands.Create("tassblood")
                .WithDescription("TassHunting blood ledger: status, test (fake trail+pool at your feet), clear")
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(p.OptionalWord("mode"))
                .HandleWith(args =>
                {
                    try
                    {
                        string mode = (args[0] as string) ?? "status";
                        switch (mode)
                        {
                            case "clear":
                                int n = spots.Count, w = waterTiles.Count;
                                foreach (var kv in sentIds) kv.Value.Clear();
                                spots.Clear(); waterTiles.Clear(); corpses.Clear(); trails.Clear(); dirtySpots.Clear();
                                serverChannel.BroadcastPacket(new BloodClearPacket { All = true });
                                packetsSent++;
                                return TextCommandResult.Success($"[tassblood] cleared {n} spots, {w} water tiles (clients wiped instantly).");
                            case "test":
                                var ent = args.Caller?.Entity;
                                if (ent == null) return TextCommandResult.Success("[tassblood] no caller entity.");
                                double ex = ent.Pos.X, ey = ent.Pos.Y, ez = ent.Pos.Z;
                                double yaw = ent.Pos.Yaw;
                                double fx = Math.Sin(yaw), fz = Math.Cos(yaw);
                                Deposit(ex, ey, ez, KindPool, 6f, 4f, null);
                                for (int i = 1; i <= 8; i++)
                                    Deposit(ex + fx * i * 1.1, ey, ez + fz * i * 1.1, KindTrail, Math.Max(1f, 4f - i * 0.4f), 1.5f, null);
                                return TextCommandResult.Success($"[tassblood] test blood laid: pool at your feet + 8-spot trail ahead (water goes to tiles). Ledger now {spots.Count} spots, {waterTiles.Count} water tiles.");
                            default:
                                return TextCommandResult.Success(
                                    $"[tassblood] spots {spots.Count}/{HuntingModSystem.Cfg.BloodMaxSpots}, water tiles {waterTiles.Count}/{WaterTileCap}, active bleed trails {trails.Count}, corpse bleeds {corpses.Count}, deposited {spotsDeposited} total, packets {packetsSent}.");
                        }
                    }
                    catch (Exception ex2)
                    {
                        return TextCommandResult.Success($"[tassblood] command failed: {ex2.Message}");
                    }
                });
        }

        // ---------------- client ----------------

        private class CSpot
        {
            public double X, Y, Z;
            public float Intensity;
            public long ExpireMs;
            public long TotalLifeMs;    // per-spot (rain blood is short-lived): drives the fade window
            public float FallHeight;    // wound height above the splat
            public byte Kind;
            public long NextEmitMs;
            public bool BurstDone;
            public bool DropletsPending; // fresh elevated spot: droplets fall before the splat lands
            public bool HasSeg;          // trail: draw a drop line back to Prev
            public double PrevX, PrevY, PrevZ;
        }

        private class CTile
        {
            public float Amount;
            public long LastSeenMs;
            public long NextEmitMs;
        }

        private ICoreClientAPI capi;
        private long clientTickId;
        private readonly Dictionary<int, CSpot> cspots = new Dictionary<int, CSpot>();
        private readonly Dictionary<(int x, int y, int z), CTile> ctiles = new Dictionary<(int, int, int), CTile>();
        private SimpleParticleProperties groundProps, burstProps, waterProps, dropletProps;
        private string appliedColorHex;   // rebuild props when the config color changes (live tuning)
        private float appliedWaterOpacity = -1f;
        private float appliedScatter = -1f;
        private const int WaterEmitPeriodMs = 2400; // sediment quads live ~3.5s: cycles overlap

        private const int EmitPeriodMs = 4000;      // particle life 4.6s: slight overlap

        /// <summary>#RRGGBB -> particle color int. ToRgba(a,r,g,b) packs ARGB =
        /// the BGRA byte order the particle system reads (API-doc-verified) -
        /// same call THW's proven white flurries use.</summary>
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

        /// <summary>(Re)build the particle prototypes for the current config
        /// color. Called at start and whenever BloodColorHex changes.</summary>
        private void EnsureParticleProps(HuntingConfig cfg)
        {
            float waterOpacity = GameMath.Clamp(cfg.WaterBloodMaxOpacity, 0.05f, 1f);
            float scatter = GameMath.Clamp(cfg.BloodScatter, 0f, 0.5f);
            if (groundProps != null && appliedColorHex == cfg.BloodColorHex
                && appliedWaterOpacity == waterOpacity && appliedScatter == scatter) return;
            appliedColorHex = cfg.BloodColorHex;
            appliedWaterOpacity = waterOpacity;
            appliedScatter = scatter;
            int blood = ParseBloodColor(cfg.BloodColorHex, ColorUtil.ToRgba(255, 116, 8, 12));
            float sc = 4f * scatter; // BT's BloodSpread scale: 0.05 default = gentle scatter

            // Decals (trail drops, splats, pools): STABLE at full opacity for
            // the spot's whole life; fade + sink are applied per-emit only in
            // the spot's final BloodFadeSeconds window (0.7.3 - fading every
            // particle cycle made all blood blink out in ~5s, field report).
            groundProps = new SimpleParticleProperties(
                1, 1, blood,
                new Vec3d(), new Vec3d(),
                new Vec3f(), new Vec3f(),
                4.6f, 0f, 0.25f, 0.5f,
                EnumParticleModel.Cube);

            // Hit spurts ARC: launch up-and-over from the wound and fall under
            // full gravity (playtest: everything was flat, no juice). Scatter
            // widens both the launch cone and the up-kick.
            burstProps = new SimpleParticleProperties(
                4, 4, blood,
                new Vec3d(), new Vec3d(),
                new Vec3f(-(0.3f + 3f * sc), 0.5f + 2f * sc, -(0.3f + 3f * sc)),
                new Vec3f(0.3f + 3f * sc, 1.5f + 2f * sc, 0.3f + 3f * sc),
                1.1f, 1.0f, 0.1f, 0.22f,
                EnumParticleModel.Cube);
            burstProps.ShouldDieInLiquid = true;
            burstProps.OpacityEvolve = new EvolvingNatFloat(EnumTransformFunction.QUADRATIC, -255f);

            // Falling droplets (the "ours felt wrong" fix): blood visibly
            // LEAVES the wound and falls; the ground splat lands with it.
            dropletProps = new SimpleParticleProperties(
                2, 3, blood,
                new Vec3d(), new Vec3d(),
                new Vec3f(-sc, -0.15f, -sc), new Vec3f(sc, -0.02f, sc),
                1.1f, 0.9f, 0.1f, 0.2f,
                EnumParticleModel.Cube);
            dropletProps.ShouldDieInLiquid = true;
            dropletProps.OpacityEvolve = new EvolvingNatFloat(EnumTransformFunction.QUADRATIC, -255f);

            // Blood in water = the VANILLA lakebed-sediment look, tinted red
            // (user 2026-07-21: like stepping on the bottom of a lake - soft
            // translucent quads hanging IN the water column, not a gradient
            // circle film and not floating cubes). Translucent quad billboards,
            // near-still drift, alpha from the water opacity dial, fading out.
            int wr = (blood >> 16) & 0xFF, wg = (blood >> 8) & 0xFF, wb = blood & 0xFF;
            int water = ColorUtil.ToRgba(Math.Max(6, (int)(130 * waterOpacity)), wr, wg, wb);
            waterProps = new SimpleParticleProperties(
                1, 1, water,
                new Vec3d(), new Vec3d(),
                new Vec3f(-0.02f, -0.01f, -0.02f), new Vec3f(0.02f, 0.015f, 0.02f),
                3.5f, 0f, 0.7f, 1.3f,
                EnumParticleModel.Quad);
            waterProps.OpacityEvolve = new EvolvingNatFloat(EnumTransformFunction.QUADRATIC, -255f);
        }

        private readonly BlockPos cscratch = new BlockPos(0);
        private static readonly EvolvingNatFloat FadeOut = new EvolvingNatFloat(EnumTransformFunction.QUADRATIC, -255f);
        private static readonly EvolvingNatFloat NoFade = new EvolvingNatFloat(EnumTransformFunction.LINEAR, 0f);

        /// <summary>Client-side surface height for trail-line drops: the line
        /// interpolates between anchors, so on slopes each drop re-resolves
        /// the real block top under it (no-floating law). Water = no drop
        /// (the water tiles own that segment).</summary>
        private bool ResolveGroundYClient(double x, double yStart, double z, out double surfY)
        {
            surfY = 0;
            var ba = capi.World.BlockAccessor;
            int ix = (int)Math.Floor(x), iz = (int)Math.Floor(z);
            int startY = (int)Math.Floor(yStart);
            for (int i = 0; i <= 4; i++)
            {
                int cy = startY - i;
                if (cy < 1) return false;
                var fluid = ba.GetBlock(cscratch.Set(ix, cy, iz), BlockLayersAccess.Fluid);
                if (fluid != null && fluid.IsLiquid()) return false;
                var solid = ba.GetBlock(cscratch.Set(ix, cy, iz), BlockLayersAccess.Solid);
                if (solid == null || solid.Id == 0) continue;
                float h = TopHeightOf(solid);
                if (h <= 0f) continue;
                surfY = cy + h;
                return true;
            }
            return false;
        }

        /// <summary>Deterministic 0..1 from a seed - splat layouts must be
        /// IDENTICAL across re-emits (a re-rolled layout reads as blood
        /// flickering/moving; user field report 2026-07-21).</summary>
        private static float Hash01(int seed)
        {
            unchecked
            {
                uint h = (uint)seed * 2654435761u;
                h ^= h >> 13; h *= 2246822519u; h ^= h >> 16;
                return (h & 0xFFFFFF) / (float)0x1000000;
            }
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            api.Network.GetChannel(ChannelName)
                .SetMessageHandler<BloodSpotsPacket>(OnSpotsPacket)
                .SetMessageHandler<WaterBloodPacket>(OnWaterPacket)
                .SetMessageHandler<BloodClearPacket>(OnClearPacket);

            // 150ms: damage-beat spurts must appear ON the beat (playtest:
            // offbeat blood feels bad); heavy early-outs keep idle cost near zero
            clientTickId = api.Event.RegisterGameTickListener(ClientRenderTick, 150);

            api.ChatCommands.Create("tassbloodc")
                .WithDescription("TassHunting blood client mirror status")
                .HandleWith(_ => TextCommandResult.Success(
                    $"[tassblood client] mirror: {cspots.Count} spots, {ctiles.Count} water tiles."));
        }

        private void OnSpotsPacket(BloodSpotsPacket packet)
        {
            try
            {
                if (packet?.Spots == null || capi == null) return;
                long now = capi.World.ElapsedMilliseconds;
                var cfg = HuntingModSystem.Cfg;
                foreach (var d in packet.Spots)
                {
                    if (!cspots.TryGetValue(d.Id, out var s))
                    {
                        cspots[d.Id] = s = new CSpot();
                        // splash/droplet juice only for blood that JUST
                        // happened - login/walk-up blood appears silently
                        bool fresh = d.AgeMs < 3000;
                        s.BurstDone = !fresh;
                        if (fresh && d.FallHeight > 0.35f && cfg != null && cfg.FallingDropletsEnabled)
                        {
                            // droplets fall first; the splat materializes when
                            // they land (BT: ~150ms per block of drop height)
                            s.DropletsPending = true;
                            s.NextEmitMs = now + Math.Min(1200, (int)(d.FallHeight * 150f));
                        }
                    }
                    s.X = d.X; s.Y = d.Y; s.Z = d.Z;
                    s.Intensity = d.Intensity;
                    s.Kind = d.Kind;
                    s.FallHeight = d.FallHeight;
                    s.HasSeg = d.HasSeg;
                    s.PrevX = d.PrevX; s.PrevY = d.PrevY; s.PrevZ = d.PrevZ;
                    s.ExpireMs = now + d.RemainMs;
                    s.TotalLifeMs = Math.Max(1000, d.RemainMs + d.AgeMs);
                }
            }
            catch (Exception ex) { WarnRateLimited("spots packet", ex); }
        }

        private void OnClearPacket(BloodClearPacket packet)
        {
            try
            {
                if (packet == null || capi == null) return;
                if (packet.All) { cspots.Clear(); ctiles.Clear(); return; }
                if (packet.Ids != null) foreach (int id in packet.Ids) cspots.Remove(id);
            }
            catch (Exception ex) { WarnRateLimited("clear packet", ex); }
        }

        private void OnWaterPacket(WaterBloodPacket packet)
        {
            try
            {
                if (packet?.Tiles == null || capi == null) return;
                long now = capi.World.ElapsedMilliseconds;
                foreach (var t in packet.Tiles)
                {
                    var key = (t.X, t.Y, t.Z);
                    if (!ctiles.TryGetValue(key, out var ct)) ctiles[key] = ct = new CTile();
                    ct.Amount = t.Amount;
                    ct.LastSeenMs = now;
                }
            }
            catch (Exception ex) { WarnRateLimited("water packet", ex); }
        }

        private void ClientRenderTick(float dt)
        {
            try
            {
                var cfg = HuntingModSystem.Cfg;
                if (cfg == null || !cfg.BloodVisualsEnabled) return;
                if (cspots.Count == 0 && ctiles.Count == 0) return; // EARLY OUT
                var plr = capi.World.Player?.Entity;
                if (plr == null) return;
                EnsureParticleProps(cfg);
                long now = capi.World.ElapsedMilliseconds;
                double px = plr.Pos.X, pz = plr.Pos.Z;
                float maxDist2 = cfg.BloodRenderDistanceBlocks * cfg.BloodRenderDistanceBlocks;

                // ground spots
                List<int> dead = null;
                int rendered = 0;
                foreach (var kv in cspots)
                {
                    var s = kv.Value;
                    if (now >= s.ExpireMs) { (dead = dead ?? new List<int>()).Add(kv.Key); continue; }
                    if (rendered >= cfg.BloodMaxRenderedSpots) continue;
                    double dx = s.X - px, dz = s.Z - pz;
                    if (dx * dx + dz * dz > maxDist2) continue;
                    rendered++;

                    // falling droplets fire IMMEDIATELY (the splat itself is
                    // delay-gated by NextEmitMs) - blood visibly LEAVES the
                    // wound, falls, and the splat lands with it
                    if (s.DropletsPending)
                    {
                        s.DropletsPending = false;
                        if (cfg.FallingDropletsEnabled && dropletProps != null)
                        {
                            dropletProps.MinQuantity = 2 + (int)(s.Intensity / 2f);
                            dropletProps.AddQuantity = 2;
                            dropletProps.LifeLength = GameMath.Clamp(s.FallHeight * 0.2f, 0.4f, 1.4f);
                            dropletProps.MinPos.Set(s.X - 0.12, s.Y + s.FallHeight - 0.1, s.Z - 0.12);
                            dropletProps.AddPos.Set(0.24, 0.15, 0.24);
                            capi.World.SpawnParticles(dropletProps);
                        }
                    }

                    if (now < s.NextEmitMs) continue;
                    int emitMs = (int)(GameMath.Clamp(cfg.BloodRefreshSeconds, 1f, 15f) * 1000f);
                    s.NextEmitMs = now + emitMs;

                    if (!s.BurstDone)
                    {
                        s.BurstDone = true;
                        if (s.Kind != KindTrail)
                        {
                            // splash at the WOUND height, not at the ground
                            burstProps.MinQuantity = 3 + s.Intensity;
                            burstProps.AddQuantity = 3;
                            burstProps.MinPos.Set(s.X - 0.1, s.Y + s.FallHeight + 0.2, s.Z - 0.1);
                            burstProps.AddPos.Set(0.2, 0.3, 0.2);
                            capi.World.SpawnParticles(burstProps);
                        }
                    }

                    // DETERMINISTIC splat layout: positions/sizes are hashed
                    // from the spot id, so every re-emit re-covers the exact
                    // same pattern. STABLE at full opacity until the spot's
                    // final BloodFadeSeconds window - only then do cohorts
                    // shrink, fade out and sink into the ground (soak-in end).
                    long remainMs = s.ExpireMs - now;
                    long fadeWindowMs = (long)(GameMath.Clamp(cfg.BloodFadeSeconds, 1f, 120f) * 1000f);
                    bool ending = remainMs <= fadeWindowMs;
                    float lifeFrac = ending ? GameMath.Clamp(remainMs / (float)fadeWindowMs, 0f, 1f) : 1f;
                    float fade = 0.55f + 0.45f * lifeFrac;
                    float sMin = Math.Max(0.05f, cfg.BloodParticleSizeMin);
                    float sMax = Math.Max(sMin, cfg.BloodParticleSizeMax);
                    float intenFrac = GameMath.Clamp(s.Intensity / MaxIntensity, 0f, 1f);
                    int spotId = kv.Key;
                    groundProps.MinQuantity = 1;
                    groundProps.AddQuantity = 0;
                    float pLife = GameMath.Clamp(cfg.BloodRefreshSeconds, 1f, 15f) * 1.15f;
                    // particles live a touch past the refresh so cycles overlap
                    // seamlessly; the final cohort dies WITH the spot
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

                    if (s.Kind == KindTrail && s.HasSeg)
                    {
                        // TRAIL = a dotted LINE of small drops along the path
                        // segment - ORGANIC, not metronome (playtest: rigid,
                        // flat, same size): irregular spacing, per-drop size
                        // variety, and heavier bleeding = bigger, denser drops
                        // (intensity was ignored here before 0.7.4).
                        double sx = s.X - s.PrevX, sz = s.Z - s.PrevZ;
                        double segLen = Math.Sqrt(sx * sx + sz * sz);
                        float segVar = 0.7f + 0.6f * Hash01(spotId * 61);
                        int drops = GameMath.Clamp((int)Math.Ceiling(segLen * Math.Max(0.5f, cfg.TrailDropsPerBlock) * segVar * (0.8f + 0.5f * intenFrac)), 1, 24);
                        float dropSizeBase = GameMath.Lerp(sMin, Math.Min(sMax, sMin * 2.2f), intenFrac);
                        for (int k = 0; k < drops; k++)
                        {
                            float t = (k + 0.25f + 0.5f * Hash01(spotId * 271 + k * 31)) / drops; // uneven spacing
                            double lx = s.PrevX + sx * t + (Hash01(spotId * 211 + k * 17) - 0.5) * 0.18;
                            double lz = s.PrevZ + sz * t + (Hash01(spotId * 223 + k * 19 + 5) - 0.5) * 0.18;
                            double ly = s.PrevY + (s.Y - s.PrevY) * t;
                            if (!ResolveGroundYClient(lx, ly + 1.0, lz, out double gy)) continue; // no floating on slopes
                            float psize = dropSizeBase * (0.6f + 0.8f * Hash01(spotId * 239 + k * 23 + 11)) * fade;
                            groundProps.MinSize = psize;
                            groundProps.MaxSize = psize;
                            groundProps.MinPos.Set(lx, gy + 0.02, lz);
                            groundProps.AddPos.Set(0, 0, 0);
                            capi.World.SpawnParticles(groundProps);
                        }
                    }
                    else
                    {
                        // POOLS and HIT MARKS keep the clustered splat look
                        float jitter = 0.05f + 0.055f * s.Intensity;
                        int pMin = Math.Max(1, cfg.BloodParticlesMin);
                        int pMax = Math.Max(pMin, cfg.BloodParticlesMax);
                        int count = GameMath.Clamp(pMin + (int)(s.Intensity / 2f), pMin, pMax);
                        for (int k = 0; k < count; k++)
                        {
                            float ang = Hash01(spotId * 97 + k * 13) * GameMath.TWOPI;
                            float rad = jitter * (float)Math.Sqrt(Hash01(spotId * 131 + k * 29 + 7));
                            float variation = 0.85f + 0.3f * Hash01(spotId * 173 + k * 41 + 3);
                            float psize = GameMath.Lerp(sMin, sMax, intenFrac) * variation * fade;
                            groundProps.MinSize = psize;
                            groundProps.MaxSize = psize;
                            groundProps.MinPos.Set(s.X + Math.Sin(ang) * rad, s.Y + 0.02, s.Z + Math.Cos(ang) * rad);
                            groundProps.AddPos.Set(0, 0, 0);
                            capi.World.SpawnParticles(groundProps);
                        }
                    }
                }
                if (dead != null) foreach (int id in dead) cspots.Remove(id);

                // water tiles: red sediment clouds, vanilla lakebed language
                List<(int, int, int)> staleTiles = null;
                foreach (var kv in ctiles)
                {
                    var t = kv.Value;
                    if (now - t.LastSeenMs > WaterStaleMs)
                    { (staleTiles = staleTiles ?? new List<(int, int, int)>()).Add(kv.Key); continue; }
                    double dx = kv.Key.x + 0.5 - px, dz = kv.Key.z + 0.5 - pz;
                    if (dx * dx + dz * dz > maxDist2) continue;
                    if (t.Amount < 0.05f || now < t.NextEmitMs) continue;
                    t.NextEmitMs = now + WaterEmitPeriodMs;

                    float amt = Math.Min(4f, t.Amount);
                    float clotAmt = Math.Max(0f, cfg.WaterClotAmount);
                    int clots = (int)((1f + amt) * clotAmt);
                    if (clots < 1) continue; // dialed off
                    float wMin = Math.Max(0.05f, cfg.WaterClotSizeMin);
                    float wMax = Math.Max(wMin, cfg.WaterClotSizeMax);
                    // clots start SMALL and grow as they fade (playtest: the
                    // sediment puff blooms outward, it does not spawn full-size)
                    float startSize = wMin * 0.6f;
                    float endSize = wMin + (wMax - wMin) * GameMath.Clamp(amt / 4f, 0.3f, 1f);
                    waterProps.MinQuantity = clots;
                    waterProps.AddQuantity = 1;
                    waterProps.MinSize = startSize;
                    waterProps.MaxSize = startSize * 1.3f;
                    waterProps.SizeEvolve = new EvolvingNatFloat(EnumTransformFunction.LINEAR, endSize - startSize);
                    // hang IN the water column below the surface, not on it
                    waterProps.MinPos.Set(kv.Key.x + 0.5 - 0.42, kv.Key.y + 0.35, kv.Key.z + 0.5 - 0.42);
                    waterProps.AddPos.Set(0.84, 0.45, 0.84);
                    capi.World.SpawnParticles(waterProps);
                }
                if (staleTiles != null) foreach (var k in staleTiles) ctiles.Remove(k);
            }
            catch (Exception ex) { WarnRateLimited("client render tick", ex); }
        }

        // ---------------- teardown ----------------

        public override void Dispose()
        {
            try
            {
                if (sapi != null)
                {
                    if (depositTickId != 0) sapi.Event.UnregisterGameTickListener(depositTickId);
                    if (waterTickId != 0) sapi.Event.UnregisterGameTickListener(waterTickId);
                    sapi.Event.OnEntityDeath -= OnEntityDeath;
                    sapi.Event.PlayerDisconnect -= OnPlayerDisconnect;
                }
                if (capi != null && clientTickId != 0) capi.Event.UnregisterGameTickListener(clientTickId);
            }
            catch { }
            if (instance == this) instance = null;
            spots.Clear(); trails.Clear(); corpses.Clear(); waterTiles.Clear(); sentIds.Clear(); dirtySpots.Clear();
            cspots.Clear(); ctiles.Clear();
            sapi = null; capi = null; serverChannel = null;
            depositTickId = waterTickId = clientTickId = 0;
            base.Dispose();
        }
    }
}
