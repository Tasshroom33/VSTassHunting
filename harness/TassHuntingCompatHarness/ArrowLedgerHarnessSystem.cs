using System;
using System.Collections.Generic;
using System.Linq;
using TassHunting;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace TassHuntingCompatHarness
{
    /// <summary>
    /// ARROW LEDGER (TASSHUNTING_ARROWLEDGER=1): conservation-of-arrows, end to end,
    /// built for the 2026-08-30 field report ("shot a stack of copper arrows at a
    /// triceratops, walked the line both ways with pickup highlight on, four arrows
    /// gone"). The claim under test is NOT any one mechanism - it is the accounting
    /// identity itself: every projectile fired must end in exactly one observable
    /// terminal state. Riding, lying recoverable, broke-with-head, or picked up.
    /// Anything else - despawned early, or gone with no despawn event at all - is
    /// the eater, and this harness makes it name itself.
    ///
    /// Round 3 (rounds 1-2 registered ZERO entity hits: 7-block and even 2.5-block
    /// shots at a wandering pig all missed, so the entity-hit paths never ran):
    ///  - every arrow carries FLIGHT PROBES - its position/motion/stuck state is
    ///    snapshotted at +0.5s/+1.5s/+3s and printed for the volley's first three,
    ///    so a systematic no-fly or tunnel-through names itself;
    ///  - volley B spawns arrows OVERLAPPING the pig's collision box with motion
    ///    into it - the swept-box entity check must fire on the first tick, so
    ///    engagement is guaranteed if entity-hit detection works at all;
    ///  - the reconciliation reports engagement PER VOLLEY and dumps the resting
    ///    position of every surviving arrow.
    ///
    /// Arrows are built exactly the way ItemBow builds them (decompile 1.22.5:
    /// Damage=bow+arrow, DropOnImpactChance=1-breakChanceOnImpact, PreInitialize,
    /// SpawnPriorityEntity; DamageStackOnImpact is NOT set for arrows). FiredBy
    /// stays null: a headless server has no player entity, and null rolls the
    /// bounce exactly like a player shot (the animal-fight bypass only triggers on
    /// a LIVING NON-PLAYER cause). Config pinned to the field values 2026-08-30:
    /// StickUntilDeath false, StickSeconds 60, bounce on, pig classified ARMOR
    /// (copper vs armor = 90% bounce - the triceratops scenario).
    /// </summary>
    public class ArrowLedgerHarnessSystem : ModSystem
    {
        private ICoreServerAPI _sapi = null!;
        private int _total, _passed;

        private class ArrowRec
        {
            public long Id;
            public string Volley = "";
            public long FiredMs;
            public bool Despawned;
            public EnumDespawnReason Reason;
            public List<string> Events = new List<string>();
        }

        private readonly Dictionary<long, ArrowRec> _ledger = new Dictionary<long, ArrowRec>();
        private readonly List<string> _headSpawns = new List<string>();
        private readonly List<string> _strayItemSpawns = new List<string>();
        private int _headsSpawned;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            if (Environment.GetEnvironmentVariable("TASSHUNTING_ARROWLEDGER") != "1") return;
            _sapi = api;
            api.Event.OnEntityDespawn += OnDespawn;
            api.Event.OnEntitySpawn += OnSpawn;
            api.Event.SaveGameLoaded += () => api.Event.RegisterCallback(_ => Begin(), 3000);
            api.Logger.Notification("[arrowledger] armed.");
        }

        private void Check(string name, bool ok, string detail = null)
        {
            _total++;
            if (ok) _passed++;
            _sapi.Logger.Notification("[arrowledger] {0} {1}{2}", ok ? "PASS" : "FAIL", name,
                detail == null ? "" : " (" + detail + ")");
        }

        private void Note(string fmt, params object[] args) =>
            _sapi.Logger.Notification("[arrowledger] " + fmt, args);

        private void Done() =>
            _sapi.Logger.Notification("[arrowledger] ARROWLEDGER COMPLETE total={0} pass={1} fail={2}",
                _total, _passed, _total - _passed);

        // ---------------- event hooks: the ledger's ears ----------------

        private void OnDespawn(Entity entity, EntityDespawnData data)
        {
            try
            {
                if (_ledger.TryGetValue(entity.EntityId, out var rec))
                {
                    rec.Despawned = true;
                    rec.Reason = data?.Reason ?? EnumDespawnReason.Removed;
                    var p = entity.ServerPos ?? entity.Pos;
                    rec.Events.Add($"t+{Ms(rec)}ms DESPAWN reason={rec.Reason} at {p.X:0.0},{p.Y:0.0},{p.Z:0.0} stuck={entity.WatchedAttributes.GetBool("stuck", false)} sa_target={entity.WatchedAttributes.GetLong("sa_target", 0L)}");
                }
            }
            catch (Exception e) { _sapi.Logger.Error("[arrowledger] despawn hook: {0}", e); }
        }

        private void OnSpawn(Entity entity)
        {
            try
            {
                if (!(entity is EntityItem ei)) return;
                string code = ei.Itemstack?.Collectible?.Code?.Path;
                if (code == null) return;
                var p = entity.ServerPos ?? entity.Pos;
                if (code.StartsWith("arrowhead", StringComparison.OrdinalIgnoreCase))
                {
                    _headsSpawned++;
                    _headSpawns.Add($"{code} at {p.X:0.0},{p.Y:0.0},{p.Z:0.0}");
                }
                else if (code.Contains("arrow"))
                {
                    _strayItemSpawns.Add($"{code} at {p.X:0.0},{p.Y:0.0},{p.Z:0.0}");
                }
            }
            catch (Exception e) { _sapi.Logger.Error("[arrowledger] spawn hook: {0}", e); }
        }

        private long Ms(ArrowRec r) => _sapi.World.ElapsedMilliseconds - r.FiredMs;

        // ---------------- the shot ----------------

        private Item _arrowItem;
        private float _dropOnImpactChance;

        private EntityProjectileBase BuildArrow()
        {
            var etype = _sapi.World.GetEntityType(new AssetLocation("game", "arrow-copper"));
            if (etype == null) return null;
            var proj = _sapi.World.ClassRegistry.CreateEntity(etype) as EntityProjectileBase;
            if (proj == null) return null;
            proj.FiredBy = null;
            proj.Damage = 5f;
            proj.DamageTier = 0;
            proj.ProjectileStack = new ItemStack(_arrowItem, 1);
            proj.DropOnImpactChance = _dropOnImpactChance;
            return proj;
        }

        private ArrowRec Launch(EntityProjectileBase proj, Vec3d from, Vec3d dir, string volley)
        {
            var ent = (Entity)proj;
            ent.ServerPos.SetPos(from.X, from.Y, from.Z);
            ent.ServerPos.Motion.Set(dir.X, dir.Y, dir.Z); // bowDrawingStrength 1.0: unit vector, vanilla full draw
            ent.Pos.SetFrom(ent.ServerPos);
            ent.World = _sapi.World;
            ((IProjectile)proj).PreInitialize();
            _sapi.World.SpawnPriorityEntity(ent);

            var rec = new ArrowRec { Id = ent.EntityId, Volley = volley, FiredMs = _sapi.World.ElapsedMilliseconds };
            rec.Events.Add($"t+0ms FIRED[{volley}] from {from.X:0.00},{from.Y:0.00},{from.Z:0.00} motion {dir.X:0.00},{dir.Y:0.00},{dir.Z:0.00}");
            _ledger[ent.EntityId] = rec;
            foreach (int delay in new[] { 500, 1500, 3000 })
                _sapi.Event.RegisterCallback(_ => Probe(rec), delay);
            return rec;
        }

        private void Probe(ArrowRec r)
        {
            try
            {
                var e = _sapi.World.GetEntityById(r.Id);
                if (e == null)
                {
                    r.Events.Add($"t+{Ms(r)}ms probe: entity not in world (despawned={r.Despawned} reason={(r.Despawned ? r.Reason.ToString() : "-")})");
                    return;
                }
                var p = e.ServerPos ?? e.Pos;
                var pb = e as EntityProjectileBase;
                r.Events.Add($"t+{Ms(r)}ms probe: pos {p.X:0.00},{p.Y:0.00},{p.Z:0.00} motion {p.Motion.X:0.000},{p.Motion.Y:0.000},{p.Motion.Z:0.000} stuck={e.WatchedAttributes.GetBool("stuck", false)} sa_target={e.WatchedAttributes.GetLong("sa_target", 0L)} entityHit={pb?.EntityHit}");
            }
            catch (Exception ex) { _sapi.Logger.Error("[arrowledger] probe: {0}", ex); }
        }

        /// <summary>A real shot: 2.5 blocks out from wherever the animal is right now,
        /// full-draw motion at its box center.</summary>
        private ArrowRec FireFlight(Entity target, string volley)
        {
            var proj = BuildArrow();
            if (proj == null) return null;
            var tp = target.ServerPos;
            double aimY = tp.Y + BoxCenterY(target);
            var from = new Vec3d(tp.X, aimY + 0.6, tp.Z + 2.5);
            var dir = new Vec3d(tp.X - from.X, aimY - from.Y, tp.Z - from.Z).Normalize();
            return Launch(proj, from, dir, volley);
        }

        /// <summary>The unmissable shot: spawned overlapping the animal's collision box,
        /// motion pointed into its center - the swept entity check must see it on the
        /// very first tick if entity-hit detection works at all.</summary>
        private ArrowRec FireOverlap(Entity target, string volley)
        {
            var proj = BuildArrow();
            if (proj == null) return null;
            var tp = target.ServerPos;
            double aimY = tp.Y + BoxCenterY(target);
            var from = new Vec3d(tp.X, aimY, tp.Z + 0.4);
            var dir = new Vec3d(0, 0, -1);
            return Launch(proj, from, dir, volley);
        }

        private static double BoxCenterY(Entity t) =>
            t.CollisionBox != null ? (t.CollisionBox.Y1 + t.CollisionBox.Y2) * 0.5 : 0.5;

        // ---------------- the run: a state machine on a 250ms tick ----------------

        private Entity _pig;
        private long _tickId;
        private double _clock;
        private int _fired;
        private int _phase;
        private bool _healOn;
        private double _healClock;

        private void Begin()
        {
            try
            {
                var cfg = HuntingModSystem.Cfg;
                cfg.StickyProjectilesEnabled = true;
                cfg.StickUntilDeath = false;
                cfg.StickSeconds = 60f;
                cfg.BounceEnabled = true;
                cfg.DropArrowheadOnBreak = true;
                cfg.BloodDiagnostics = true;
                cfg.ArmorCreatures = new[] { "pig-*" };   // the pig plays the triceratops
                cfg.ThickHideCreatures = new string[0];

                _arrowItem = _sapi.World.GetItem(new AssetLocation("game", "arrow-copper"));
                Check("arrow-item-exists", _arrowItem != null);
                float breakChance = _arrowItem?.Attributes?["breakChanceOnImpact"].AsFloat(0.5f) ?? 0.5f;
                _dropOnImpactChance = 1f - breakChance;
                Check("copper-break-tuned", Math.Abs(breakChance - 0.04f) < 0.001f, $"breakChanceOnImpact={breakChance:0.000}");

                _pig = SpawnPig();
                Check("pig-spawned", _pig != null);
                if (_pig == null || _arrowItem == null) { Done(); return; }

                Note("run starts: pig at {0:0.0},{1:0.0},{2:0.0} box={3}", _pig.ServerPos.X, _pig.ServerPos.Y, _pig.ServerPos.Z,
                    _pig.CollisionBox == null ? "null" : $"{_pig.CollisionBox.XSize:0.00}x{_pig.CollisionBox.YSize:0.00}x{_pig.CollisionBox.ZSize:0.00}");

                _phase = 1; _clock = 0; _fired = 0; _healOn = true;
                _tickId = _sapi.Event.RegisterGameTickListener(Tick, 250);
            }
            catch (Exception e)
            {
                _sapi.Logger.Error("[arrowledger] EXCEPTION in Begin: {0}", e);
                Done();
            }
        }

        private void Tick(float dt)
        {
            try
            {
                _clock += dt;
                if (_healOn && _pig != null && _pig.Alive)
                {
                    _healClock += dt;
                    if (_healClock >= 0.5)
                    {
                        _healClock = 0;
                        var hb = _pig.GetBehavior<EntityBehaviorHealth>();
                        if (hb != null) hb.Health = hb.MaxHealth;
                    }
                }

                switch (_phase)
                {
                    case 1: // VOLLEY A: 25 real flight shots at ~1/s
                        if (_fired < 25)
                        {
                            if (_clock >= _fired * 0.9)
                            {
                                if (_pig.Alive) FireFlight(_pig, "A");
                                _fired++;
                            }
                        }
                        else if (_clock >= 25 * 0.9 + 8)
                        {
                            Snapshot("volley A (flight) settles");
                            DumpEvents("A", 3);
                            NextPhase(2);
                        }
                        break;

                    case 2: // VOLLEY B: 15 overlap shots - engagement is guaranteed or the pipeline is broken
                        if (_fired < 15)
                        {
                            if (_clock >= _fired * 0.5)
                            {
                                if (_pig.Alive) FireOverlap(_pig, "B");
                                _fired++;
                            }
                        }
                        else if (_clock >= 15 * 0.5 + 6)
                        {
                            Snapshot("volley B (overlap) settles");
                            DumpEvents("B", 3);
                            int engagedB = EngagedIn("B");
                            Check("overlap-volley-engages", engagedB >= 13, $"{engagedB}/15 hit the pig");
                            NextPhase(3);
                        }
                        break;

                    case 3: // the work-loose window: StickSeconds=60 + margin
                        if (_clock >= 75)
                        {
                            Snapshot("after 75s work-loose window");
                            int riding = _ledger.Values.Count(r => Riding(r));
                            Check("workloose-all-released", riding == 0, $"{riding} still riding after StickSeconds+15");
                            NextPhase(4);
                        }
                        break;

                    case 4: // kill for the death-release path (any arrows stuck within the last 60s)
                        if (_clock >= 1)
                        {
                            _healOn = false;
                            if (_pig != null && _pig.Alive) { _pig.ReceiveDamage(Sharp(), 9999f); Note("pig killed for the death-release path"); }
                            NextPhase(5);
                        }
                        break;

                    case 5: // settle, then control pig (unclassified: stick/break only)
                        if (_clock >= 8)
                        {
                            Snapshot("after kill-release settles");
                            HuntingModSystem.Cfg.ArmorCreatures = new string[0];
                            _pig = SpawnPig();
                            _healOn = true;
                            if (_pig == null) { Check("control-pig-spawned", false); NextPhase(7); }
                            else NextPhase(6);
                        }
                        break;

                    case 6: // VOLLEY C: 12 overlap shots, no bounce - sticks and breaks, then kill
                        if (_fired < 12)
                        {
                            if (_clock >= _fired * 0.5)
                            {
                                if (_pig.Alive) FireOverlap(_pig, "C");
                                _fired++;
                            }
                        }
                        else if (_clock >= 12 * 0.5 + 5)
                        {
                            Snapshot("volley C (control, unclassified) settles");
                            DumpEvents("C", 3);
                            _healOn = false;
                            if (_pig.Alive) _pig.ReceiveDamage(Sharp(), 9999f);
                            NextPhase(7);
                        }
                        break;

                    case 7: // final settle then reconcile
                        if (_clock >= 10)
                        {
                            _sapi.Event.UnregisterGameTickListener(_tickId);
                            Reconcile();
                            Done();
                            _phase = 99;
                        }
                        break;
                }
            }
            catch (Exception e)
            {
                _sapi.Logger.Error("[arrowledger] EXCEPTION in Tick: {0}", e);
                try { _sapi.Event.UnregisterGameTickListener(_tickId); } catch { }
                Reconcile();
                Done();
                _phase = 99;
            }
        }

        private void NextPhase(int p) { _phase = p; _clock = 0; _fired = 0; }

        private bool Riding(ArrowRec r)
        {
            var e = _sapi.World.GetEntityById(r.Id);
            return e != null && e.Alive && e.WatchedAttributes.GetLong("sa_target", 0L) != 0L;
        }

        private bool Engaged(ArrowRec r)
        {
            if (r.Despawned && r.Reason == EnumDespawnReason.Death) return true; // a break IS an engagement
            var e = _sapi.World.GetEntityById(r.Id);
            return e is EntityProjectileBase pb && pb.EntityHit;
        }

        private int EngagedIn(string volley) => _ledger.Values.Count(r => r.Volley == volley && Engaged(r));

        private void DumpEvents(string volley, int n)
        {
            foreach (var r in _ledger.Values.Where(r => r.Volley == volley).OrderBy(r => r.FiredMs).Take(n))
                foreach (var ev in r.Events)
                    Note("  id={0} {1}", r.Id, ev);
        }

        private void Snapshot(string label)
        {
            int riding = 0, lying = 0, deaths = 0, other = 0, gone = 0, engaged = 0;
            foreach (var r in _ledger.Values)
            {
                if (Engaged(r)) engaged++;
                var e = _sapi.World.GetEntityById(r.Id);
                if (e != null && e.Alive)
                {
                    if (e.WatchedAttributes.GetLong("sa_target", 0L) != 0L) riding++;
                    else lying++;
                }
                else if (r.Despawned)
                {
                    if (r.Reason == EnumDespawnReason.Death) deaths++; else other++;
                }
                else gone++;
            }
            var pp = _pig?.ServerPos;
            Note("SNAPSHOT {0}: fired={1} engaged={2} riding={3} lying={4} broke(Death)={5} otherDespawn={6} GONE-NO-EVENT={7} heads={8} pig={9}",
                label, _ledger.Count, engaged, riding, lying, deaths, other, gone, _headsSpawned,
                pp == null ? "?" : $"{pp.X:0.0},{pp.Y:0.0},{pp.Z:0.0} alive={_pig.Alive}");
        }

        private void Reconcile()
        {
            int lying = 0, riding = 0, breaks = 0;
            var bad = new List<ArrowRec>();
            var lyingLines = new List<string>();
            foreach (var r in _ledger.Values.OrderBy(r => r.FiredMs))
            {
                var e = _sapi.World.GetEntityById(r.Id);
                if (e != null && e.Alive)
                {
                    var p = e.ServerPos ?? e.Pos;
                    bool ride = e.WatchedAttributes.GetLong("sa_target", 0L) != 0L;
                    if (ride) riding++; else lying++;
                    lyingLines.Add($"id={r.Id}[{r.Volley}] {(ride ? "RIDING" : "LYING")} hit={(e as EntityProjectileBase)?.EntityHit} at {p.X:0.0},{p.Y:0.0},{p.Z:0.0}");
                    continue;
                }
                if (r.Despawned && r.Reason == EnumDespawnReason.Death) { breaks++; continue; }
                bad.Add(r);
            }

            foreach (string v in new[] { "A", "B", "C" })
            {
                int total = _ledger.Values.Count(r => r.Volley == v);
                if (total > 0) Note("volley {0}: {1} fired, {2} engaged the animal", v, total, EngagedIn(v));
            }

            Check("no-arrow-unaccounted", bad.Count == 0, $"{bad.Count} of {_ledger.Count} lost");
            foreach (var r in bad)
            {
                Note("LOST ARROW id={0} volley={1}: {2}",
                    r.Id, r.Volley,
                    r.Despawned ? $"despawned reason={r.Reason}" : "NO DESPAWN EVENT AT ALL");
                foreach (var ev in r.Events) Note("  id={0} {1}", r.Id, ev);
            }

            Check("every-break-dropped-a-head", _headsSpawned == breaks, $"breaks={breaks} heads={_headsSpawned}");
            Check("no-arrow-still-riding-at-end", riding == 0, $"{riding} riding");
            Check("no-stray-arrow-item-entities", _strayItemSpawns.Count == 0,
                _strayItemSpawns.Count == 0 ? null : string.Join("; ", _strayItemSpawns.Take(5)));
            Note("FINAL LEDGER: fired={0} lying={1} riding={2} broke+head={3} heads={4} lost={5}",
                _ledger.Count, lying, riding, breaks, _headsSpawned, bad.Count);
            foreach (var l in lyingLines) Note("  {0}", l);
            foreach (var h in _headSpawns) Note("head: {0}", h);
        }

        private static DamageSource Sharp() => new DamageSource
        {
            Source = EnumDamageSource.Unknown,
            Type = EnumDamageType.PiercingAttack,
            DamageTier = 0,
            IgnoreInvFrames = true
        };

        private Entity SpawnPig()
        {
            var type = _sapi.World.EntityTypes.FirstOrDefault(
                t => t?.Code?.Path != null && t.Code.Path.StartsWith("pig-") && t.Code.Path.Contains("-adult-"));
            if (type == null) return null;
            var spawn = _sapi.World.DefaultSpawnPosition;
            Entity e = _sapi.World.ClassRegistry.CreateEntity(type);
            e.ServerPos.SetPos(spawn.X + 3, spawn.Y + 1, spawn.Z + 3);
            e.Pos.SetFrom(e.ServerPos);
            _sapi.World.SpawnEntity(e);
            return e;
        }
    }
}
