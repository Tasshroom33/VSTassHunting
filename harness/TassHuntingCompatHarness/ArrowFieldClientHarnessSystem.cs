// ARROW FIELD real-client test (field report 2026-08-30: copper arrows vs a
// triceratops, ~4 arrows missing from a tight pickup loop; no death, no relog).
// Driven by Run-ArrowFieldClientTest.ps1. The server-side conservation ledger
// (ArrowLedgerHarnessSystem, 3 runs / 177 arrows / 0 lost) cleared the headless
// half; THIS test covers the player layer that a headless server cannot: the
// 4-block pickup vacuum, vanilla walk-over collect, real inventory accounting,
// and what the client actually receives and renders.
//
//   TASSHUNTING_ARROWFIELD=1        arms both halves
//   TASSHUNTING_ARROWFIELD_SHOTS    client screenshot directory
//
// SERVER DRIVER: waits for the real player, pins the field config (bounce on,
// pig classified ARMOR = the trike math, StickSeconds 60, StickUntilDeath off),
// fires copper arrows WITH FiredBy = THE REAL PLAYER (real firedBy stamp, real
// tassOwner UID, real ownership window - the exact filters the vacuum and
// highlighter key on), then walks the player through his own field-loop: the
// player is teleported to each known arrow the way he walks to each highlight
// particle, and the vacuum + walk-over collect do the rest. Phases: bounce
// volley -> sweep -> 65s work-loose window -> sweep -> forced-break volley
// (heads) -> sweep -> kill -> reconcile. The reconciliation closes the loop the
// headless test could not: every PickedUp despawn must show up as REAL
// INVENTORY GAIN, every break as a head on the ground and then in the bag.
//
// CLIENT OBSERVER: photographs the field and counts the projectile entities the
// client actually has, on the server's phase marker (a synced watched attribute
// on the player). Rendered truth: if the server says 18 arrows are lying there,
// the client must be holding 18 entities too.
//
// Results: PASS/FAIL lines in BOTH logs; the runner waits on
// "ARROWFIELD SERVER COMPLETE" and prints both sides.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TassHunting;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace TassHuntingCompatHarness
{
    /// <summary>Server half: fire the field's arrows as the player, sweep the player
    /// through them, and reconcile ledger vs inventory.</summary>
    public class ArrowFieldServerDriver : ModSystem
    {
        private ICoreServerAPI _sapi = null!;
        private int _total, _passed;
        private bool _started;

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
        private readonly Dictionary<long, string> _headEnts = new Dictionary<long, string>(); // id -> state: "ground"/"pickedup"/other
        private int _headsSpawned;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            if (Environment.GetEnvironmentVariable("TASSHUNTING_ARROWFIELD") != "1") return;
            _sapi = api;
            api.Event.OnEntityDespawn += OnDespawn;
            api.Event.OnEntitySpawn += OnSpawn;
            api.Event.PlayerNowPlaying += OnPlaying;
            api.Logger.Notification("[arrowfield] server driver armed.");
        }

        private void Check(string name, bool ok, string detail = null)
        {
            _total++;
            if (ok) _passed++;
            _sapi.Logger.Notification("[arrowfield] {0} {1}{2}", ok ? "PASS" : "FAIL", name,
                detail == null ? "" : " (" + detail + ")");
        }

        private void Note(string fmt, params object[] args) =>
            _sapi.Logger.Notification("[arrowfield] " + fmt, args);

        private void Done() =>
            _sapi.Logger.Notification("[arrowfield] ARROWFIELD SERVER COMPLETE total={0} pass={1} fail={2}",
                _total, _passed, _total - _passed);

        // ---------------- ledger ears ----------------

        private void OnDespawn(Entity entity, EntityDespawnData data)
        {
            try
            {
                var reason = data?.Reason ?? EnumDespawnReason.Removed;
                if (_ledger.TryGetValue(entity.EntityId, out var rec))
                {
                    rec.Despawned = true;
                    rec.Reason = reason;
                    var p = entity.ServerPos ?? entity.Pos;
                    rec.Events.Add($"t+{_sapi.World.ElapsedMilliseconds - rec.FiredMs}ms DESPAWN reason={reason} at {p.X:0.0},{p.Y:0.0},{p.Z:0.0}");
                }
                else if (_headEnts.ContainsKey(entity.EntityId))
                {
                    _headEnts[entity.EntityId] = reason == EnumDespawnReason.PickedUp ? "pickedup" : reason.ToString();
                }
            }
            catch (Exception e) { _sapi.Logger.Error("[arrowfield] despawn hook: {0}", e); }
        }

        private void OnSpawn(Entity entity)
        {
            try
            {
                if (!(entity is EntityItem ei)) return;
                string code = ei.Itemstack?.Collectible?.Code?.Path;
                if (code == null || !code.StartsWith("arrowhead", StringComparison.OrdinalIgnoreCase)) return;
                _headsSpawned++;
                _headEnts[entity.EntityId] = "ground";
                var p = entity.ServerPos ?? entity.Pos;
                Note("head spawned: {0} at {1:0.0},{2:0.0},{3:0.0}", code, p.X, p.Y, p.Z);
            }
            catch (Exception e) { _sapi.Logger.Error("[arrowfield] spawn hook: {0}", e); }
        }

        // ---------------- the shot: ItemBow's recipe, fired AS the player ----------------

        private Item _arrowItem;
        private float _dropOnImpactChance;

        private void FireOverlap(Entity target, string volley, bool forceBreak, bool asPlayer)
        {
            var etype = _sapi.World.GetEntityType(new AssetLocation("game", "arrow-copper"));
            var proj = etype == null ? null : _sapi.World.ClassRegistry.CreateEntity(etype) as EntityProjectileBase;
            if (proj == null) return;
            // Run-3 experiment: volley V fires AS THE REAL PLAYER (real firedBy stamp),
            // volley N fires shooterless - the headless ledger engaged 15/15 shooterless,
            // the two client runs engaged 0/20 as the player. Same world, both variants:
            // whichever half engages pins the cause.
            proj.FiredBy = asPlayer ? _plr?.Entity : null;
            proj.Damage = 5f;
            proj.DamageTier = 0;
            proj.ProjectileStack = new ItemStack(_arrowItem, 1);
            proj.DropOnImpactChance = forceBreak ? 0f : _dropOnImpactChance;

            var tp = target.ServerPos;
            double aimY = tp.Y + (target.CollisionBox != null ? (target.CollisionBox.Y1 + target.CollisionBox.Y2) * 0.5 : 0.5);
            var ent = (Entity)proj;
            ent.ServerPos.SetPos(tp.X, aimY, tp.Z + 0.4);   // overlap spawn: cannot miss (proven in the ledger harness)
            ent.ServerPos.Motion.Set(0, 0, -1);              // full-draw speed into the body
            ent.Pos.SetFrom(ent.ServerPos);
            ent.World = _sapi.World;
            ((IProjectile)proj).PreInitialize();
            _sapi.World.SpawnPriorityEntity(ent);

            var rec = new ArrowRec { Id = ent.EntityId, Volley = volley, FiredMs = _sapi.World.ElapsedMilliseconds };
            rec.Events.Add($"t+0ms FIRED[{volley}]{(forceBreak ? " forceBreak" : "")}{(asPlayer ? " asPlayer" : " shooterless")} at pig {tp.X:0.0},{tp.Y:0.0},{tp.Z:0.0}");
            _ledger[ent.EntityId] = rec;
            foreach (int delay in new[] { 500, 1500, 3000 })
                _sapi.Event.RegisterCallback(_ => Probe(rec), delay);
        }

        private void Probe(ArrowRec r)
        {
            try
            {
                var e = _sapi.World.GetEntityById(r.Id);
                long ms = _sapi.World.ElapsedMilliseconds - r.FiredMs;
                if (e == null)
                {
                    r.Events.Add($"t+{ms}ms probe: entity gone (despawned={r.Despawned} reason={(r.Despawned ? r.Reason.ToString() : "-")})");
                    return;
                }
                var sp = e.ServerPos; var cp = e.Pos;
                var pb = e as EntityProjectileBase;
                r.Events.Add($"t+{ms}ms probe: ServerPos {sp.X:0.00},{sp.Y:0.00},{sp.Z:0.00} m({sp.Motion.X:0.00},{sp.Motion.Y:0.00},{sp.Motion.Z:0.00}) Pos {cp.X:0.00},{cp.Y:0.00},{cp.Z:0.00} stuck={e.WatchedAttributes.GetBool("stuck", false)} sa_target={e.WatchedAttributes.GetLong("sa_target", 0L)} entityHit={pb?.EntityHit}");
            }
            catch (Exception ex) { _sapi.Logger.Error("[arrowfield] probe: {0}", ex); }
        }

        private void DumpEvents(string volley, int n)
        {
            foreach (var r in _ledger.Values.Where(r => r.Volley == volley).OrderBy(r => r.FiredMs).Take(n))
                foreach (var ev in r.Events)
                    Note("  id={0} {1}", r.Id, ev);
        }

        private int EngagedIn(string volley) => _ledger.Values.Count(r => r.Volley == volley && IsEngaged(r));

        private bool IsEngaged(ArrowRec r)
        {
            if (r.Despawned && r.Reason == EnumDespawnReason.Death) return true;
            var e = _sapi.World.GetEntityById(r.Id);
            return e is EntityProjectileBase pb && pb.EntityHit;
        }

        // ---------------- inventory accounting ----------------

        private int CountInInventory(string codePath)
        {
            int n = 0;
            var invs = _plr?.InventoryManager?.Inventories;
            if (invs == null) return 0;
            foreach (var kv in invs)
            {
                string cls = kv.Value?.ClassName;
                if (cls != "hotbar" && cls != "backpack") continue;
                foreach (var slot in kv.Value)
                    if (slot?.Itemstack?.Collectible?.Code?.Path == codePath) n += slot.Itemstack.StackSize;
            }
            return n;
        }

        // ---------------- the run ----------------

        private IServerPlayer _plr;
        private Entity _pig;
        private long _tickId;
        private double _clock, _healClock, _dwell;
        private int _fired, _phase, _hops;
        private bool _healOn;
        private int _arrowsBase, _headsBase;

        private void SetPhaseAttr(string name)
        {
            try { _plr?.Entity?.WatchedAttributes?.SetString("tassaf_phase", name); } catch { }
        }

        private void OnPlaying(IServerPlayer player)
        {
            if (_started) return;
            _started = true;
            _plr = player;
            Note("player joined: {0} ({1})", player.PlayerName, player.PlayerUID);
            _sapi.Event.RegisterCallback(_ => Setup(), 8000); // let the client settle and close dialogs
        }

        private void Setup()
        {
            try
            {
                var cfg = HuntingModSystem.Cfg;
                // the field config, 2026-08-30
                cfg.StickyProjectilesEnabled = true;
                cfg.StickUntilDeath = false;
                cfg.StickSeconds = 60f;
                cfg.BounceEnabled = true;
                cfg.DropArrowheadOnBreak = true;
                cfg.BloodDiagnostics = true;
                cfg.ArmorCreatures = new[] { "pig-*" };
                cfg.ThickHideCreatures = new string[0];
                cfg.ProjectilePickupRadius = 4f;         // the vacuum under test
                cfg.PickupOnlyOwnProjectiles = true;     // the firedBy filter under test
                cfg.ArrowOwnerLockSeconds = 120f;        // the UID owner lock under test
                // Runs 1-3 solved: TrueAimPostfix (a real feature, working correctly)
                // repositions any player-fired projectile to the shooter's eye at
                // PreInitialize - so harness arrows spawned AT the pig teleported back
                // to the player and never engaged. Off for the harness: our spawn
                // placement IS the aim, and volley V can finally run the player-owned
                // impact paths.
                cfg.TrueAimSpawnEnabled = false;

                _arrowItem = _sapi.World.GetItem(new AssetLocation("game", "arrow-copper"));
                float breakChance = _arrowItem?.Attributes?["breakChanceOnImpact"].AsFloat(0.5f) ?? 0.5f;
                _dropOnImpactChance = 1f - breakChance;
                Check("arrow-item-exists", _arrowItem != null, $"breakChanceOnImpact={breakChance:0.000}");

                // RUN-1 LESSON (2026-08-30): with FiredBy = a real player, the engine's
                // CanDealDamage checks the SHOOTER'S attackcreatures privilege - without it
                // DealDamage AND DamageProjectile are silently skipped (no bounce, no stick,
                // no break; the arrow just falls). Run 1: 0 engagements, 0 diagnostics.
                // Grant it so the impact paths actually run for a real shooter.
                bool hadPriv = _plr.HasPrivilege(Privilege.attackcreatures);
                if (!hadPriv)
                {
                    try { _sapi.Permissions.GrantPrivilege(_plr.PlayerUID, Privilege.attackcreatures); } catch (Exception e) { Note("priv grant failed: {0}", e.Message); }
                }
                Note("attackcreatures privilege: before={0} after={1}", hadPriv, _plr.HasPrivilege(Privilege.attackcreatures));

                var pe = _plr.Entity;
                _arrowsBase = CountInInventory("arrow-copper");
                _headsBase = CountInInventory("arrowhead-copper");
                Note("inventory baseline: arrows={0} heads={1}, player at {2:0.0},{3:0.0},{4:0.0}",
                    _arrowsBase, _headsBase, pe.ServerPos.X, pe.ServerPos.Y, pe.ServerPos.Z);

                _pig = SpawnPigNear(pe);
                Check("pig-spawned", _pig != null);
                if (_pig == null || _arrowItem == null) { Done(); return; }

                _phase = 1; _clock = 0; _fired = 0; _healOn = true;
                _tickId = _sapi.Event.RegisterGameTickListener(Tick, 250);
            }
            catch (Exception e)
            {
                _sapi.Logger.Error("[arrowfield] EXCEPTION in Setup: {0}", e);
                Done();
            }
        }

        private void Tick(float dt)
        {
            try
            {
                _clock += dt;
                _healClock += dt;
                if (_healClock >= 0.5)
                {
                    _healClock = 0;
                    if (_healOn && _pig != null && _pig.Alive)
                    {
                        var hb = _pig.GetBehavior<EntityBehaviorHealth>();
                        if (hb != null) hb.Health = hb.MaxHealth;
                    }
                    // The PLAYER must survive the whole run: a mid-test death would change
                    // their entity id, and the firedBy filters under test key on that id -
                    // a death would fail the sweep for a reason that is not the bug.
                    var php = _plr?.Entity?.GetBehavior<EntityBehaviorHealth>();
                    if (php != null && php.Health < php.MaxHealth) php.Health = php.MaxHealth;
                }

                switch (_phase)
                {
                    case 1: // VOLLEY V: 20 arrows AS THE PLAYER vs armor
                        if (_fired < 20)
                        {
                            if (_clock >= _fired * 0.5) { if (_pig.Alive) FireOverlap(_pig, "V", false, true); _fired++; }
                        }
                        else if (_clock >= 20 * 0.5 + 6)
                        {
                            Snapshot("volley V (asPlayer) settles");
                            Check("volleyV-asplayer-engages", EngagedIn("V") >= 15, $"{EngagedIn("V")}/20 hit (EntityHit)");
                            DumpEvents("V", 2);
                            NextPhase(10);
                        }
                        break;

                    case 10: // VOLLEY N: 10 shooterless arrows - the controlled comparison
                        if (_fired < 10)
                        {
                            if (_clock >= _fired * 0.5) { if (_pig.Alive) FireOverlap(_pig, "N", false, false); _fired++; }
                        }
                        else if (_clock >= 10 * 0.5 + 6)
                        {
                            Snapshot("volley N (shooterless) settles");
                            Check("volleyN-shooterless-engages", EngagedIn("N") >= 7, $"{EngagedIn("N")}/10 hit (EntityHit)");
                            DumpEvents("N", 2);
                            SetPhaseAttr("volleyed");   // client: count + photograph the field
                            NextPhase(2);
                        }
                        break;

                    case 2: // SWEEP 1: walk the player to every known lying arrow (his highlight loop)
                        if (_clock >= 4 && Sweep()) // 4s lead so the client shot happens on the full field
                        {
                            Snapshot("after sweep 1");
                            int lying = CountLying(), riding = CountRiding(), collected = CountCollected();
                            Check("sweep1-vacuums-the-field", lying == 0, $"lying={lying} riding={riding} collected={collected}");
                            NextPhase(3);
                        }
                        break;

                    case 3: // WORK-LOOSE: StickSeconds=60 + margin; riders release wherever the pig wanders
                        if (_clock >= 65)
                        {
                            Snapshot("after 65s work-loose window");
                            Check("workloose-all-released", CountRiding() == 0, $"{CountRiding()} still riding");
                            NextPhase(4);
                        }
                        break;

                    case 4: // SWEEP 2: collect the released riders
                        if (Sweep())
                        {
                            Snapshot("after sweep 2");
                            Check("sweep2-field-empty", CountLying() == 0 && CountRiding() == 0,
                                $"lying={CountLying()} riding={CountRiding()}");
                            NextPhase(5);
                        }
                        break;

                    case 5: // FORCED BREAKS: 6 arrows at DropOnImpactChance=0 -> 6 heads on the ground.
                        // UNCLASSIFY the pig first (run-1 lesson, 2026-08-30): against armor the
                        // 90% bounce eats the break roll before it happens (0.9^6 = 53% chance of
                        // zero breaks - exactly what run 1 rolled), and the head-pickup path -
                        // the path the field report is about - goes untested.
                        // Shooterless like the headless round proved: the head path must run
                        // this time even if the asPlayer engagement question is still open.
                        if (_fired == 0) HuntingModSystem.Cfg.ArmorCreatures = new string[0];
                        if (_fired < 6)
                        {
                            if (_clock >= _fired * 0.5) { if (_pig.Alive) FireOverlap(_pig, "B", true, false); _fired++; }
                        }
                        else if (_clock >= 6 * 0.5 + 4)
                        {
                            int breaks = _ledger.Values.Count(r => r.Volley == "B" && r.Despawned && r.Reason == EnumDespawnReason.Death);
                            Check("forced-breaks-happen", breaks == 6, $"{breaks}/6 broke");
                            Check("every-break-dropped-a-head", _headsSpawned == breaks, $"breaks={breaks} headsSpawned={_headsSpawned}");
                            NextPhase(6);
                        }
                        break;

                    case 6: // SWEEP 3: collect the heads (the vacuum's arrowhead branch under test)
                        if (Sweep())
                        {
                            int groundHeads = _headEnts.Count(kv => kv.Value == "ground" && _sapi.World.GetEntityById(kv.Key) != null);
                            Check("sweep3-heads-collected", groundHeads == 0, $"{groundHeads} heads still on the ground");
                            NextPhase(7);
                        }
                        break;

                    case 7: // cleanup kill + final settle
                        if (_clock >= 1 && _pig != null && _pig.Alive) { _healOn = false; _pig.ReceiveDamage(Kill(), 9999f); }
                        if (_clock >= 5) { Sweep(); NextPhase(8); }
                        break;

                    case 8:
                        if (_clock >= 6)
                        {
                            _sapi.Event.UnregisterGameTickListener(_tickId);
                            SetPhaseAttr("final");      // client: final count + photograph
                            _sapi.Event.RegisterCallback(_ => { Reconcile(); Done(); }, 4000);
                            _phase = 99;
                        }
                        break;
                }
            }
            catch (Exception e)
            {
                _sapi.Logger.Error("[arrowfield] EXCEPTION in Tick: {0}", e);
                try { _sapi.Event.UnregisterGameTickListener(_tickId); } catch { }
                Reconcile();
                Done();
                _phase = 99;
            }
        }

        private void NextPhase(int p) { _phase = p; _clock = 0; _fired = 0; _hops = 0; _dwell = 0; }

        /// <summary>One sweep step per call: stand near the next known arrow or head for
        /// 1.4s and let the vacuum + walk-over collect work. Returns true when nothing
        /// is left to visit (or the hop cap is hit - a cap hit means something refuses
        /// to be collected, which the checks then surface).</summary>
        private bool Sweep()
        {
            _dwell -= 0.25;
            if (_dwell > 0) return false;
            if (_hops >= 60) return true;

            Entity next = null;
            foreach (var r in _ledger.Values)
            {
                var e = _sapi.World.GetEntityById(r.Id);
                if (e != null && e.Alive && e.WatchedAttributes.GetLong("sa_target", 0L) == 0L) { next = e; break; }
            }
            if (next == null)
                foreach (var kv in _headEnts)
                {
                    if (kv.Value != "ground") continue;
                    var e = _sapi.World.GetEntityById(kv.Key);
                    if (e != null && e.Alive) { next = e; break; }
                }
            if (next == null) return true;

            var p = next.ServerPos ?? next.Pos;
            // +0.3, not +1.0 (run-3 lesson): vanilla collect centers its 1.5-block reach
            // at the player's CHEST (~+0.85). A +1.0 teleport put that center ~1.85 above
            // the arrow - just past vanilla's vertical reach - so unowned arrows (which
            // the own-arrows vacuum rightly skips) were never collected. A walking player
            // has their feet at the arrow's level; +0.3 reproduces that.
            _plr.Entity.TeleportToDouble(p.X + 0.5, p.Y + 0.3, p.Z + 0.5);
            _hops++;
            _dwell = 1.4;
            return false;
        }

        private int CountLying() => _ledger.Values.Count(r =>
        {
            var e = _sapi.World.GetEntityById(r.Id);
            return e != null && e.Alive && e.WatchedAttributes.GetLong("sa_target", 0L) == 0L;
        });

        private int CountRiding() => _ledger.Values.Count(r =>
        {
            var e = _sapi.World.GetEntityById(r.Id);
            return e != null && e.Alive && e.WatchedAttributes.GetLong("sa_target", 0L) != 0L;
        });

        private int CountCollected() => _ledger.Values.Count(r => r.Despawned && r.Reason == EnumDespawnReason.PickedUp);

        private int CountEngaged() => _ledger.Values.Count(r =>
        {
            if (r.Despawned && r.Reason == EnumDespawnReason.Death) return true; // a break IS an engagement
            var e = _sapi.World.GetEntityById(r.Id);
            return e is EntityProjectileBase pb && pb.EntityHit;
        });

        private void Snapshot(string label)
        {
            Note("SNAPSHOT {0}: fired={1} engaged={2} collected={3} lying={4} riding={5} broke={6} headsSpawned={7} invArrows={8} invHeads={9} pig={10}",
                label, _ledger.Count, CountEngaged(), CountCollected(), CountLying(), CountRiding(),
                _ledger.Values.Count(r => r.Despawned && r.Reason == EnumDespawnReason.Death),
                _headsSpawned, CountInInventory("arrow-copper"), CountInInventory("arrowhead-copper"),
                _pig == null ? "?" : $"{_pig.ServerPos.X:0.0},{_pig.ServerPos.Y:0.0},{_pig.ServerPos.Z:0.0} alive={_pig.Alive}");
        }

        private void Reconcile()
        {
            int collected = CountCollected();
            int breaks = _ledger.Values.Count(r => r.Despawned && r.Reason == EnumDespawnReason.Death);
            int lying = CountLying(), riding = CountRiding();
            var bad = _ledger.Values.Where(r =>
            {
                var e = _sapi.World.GetEntityById(r.Id);
                if (e != null && e.Alive) return false;
                return !(r.Despawned && (r.Reason == EnumDespawnReason.PickedUp || r.Reason == EnumDespawnReason.Death));
            }).ToList();

            int invArrowGain = CountInInventory("arrow-copper") - _arrowsBase;
            int invHeadGain = CountInInventory("arrowhead-copper") - _headsBase;
            int headsPicked = _headEnts.Count(kv => kv.Value == "pickedup");

            // THE closing of the loop the headless test could not do: a PickedUp despawn
            // that never became inventory is the silent-delete class.
            Check("collected-equals-inventory-gain", collected == invArrowGain,
                $"PickedUp despawns={collected} inventory arrow gain={invArrowGain}");
            Check("heads-picked-equals-inventory-gain", headsPicked == invHeadGain,
                $"head pickups={headsPicked} inventory head gain={invHeadGain}");
            Check("no-arrow-unaccounted", bad.Count == 0, $"{bad.Count} of {_ledger.Count} lost");
            foreach (var r in bad)
            {
                Note("LOST ARROW id={0} volley={1}: {2}", r.Id, r.Volley,
                    r.Despawned ? $"despawned reason={r.Reason}" : "NO DESPAWN EVENT AT ALL");
                foreach (var ev in r.Events) Note("  id={0} {1}", r.Id, ev);
            }
            Check("field-fully-recovered", lying == 0 && riding == 0, $"lying={lying} riding={riding}");
            Note("FINAL: fired={0} -> collected={1} broke={2} (heads spawned={3} picked={4}) lying={5} riding={6} | inventory: arrows +{7}, heads +{8}",
                _ledger.Count, collected, breaks, _headsSpawned, headsPicked, lying, riding, invArrowGain, invHeadGain);
        }

        private DamageSource Kill() => new DamageSource
        {
            Source = EnumDamageSource.Entity,
            SourceEntity = _plr?.Entity,
            CauseEntity = _plr?.Entity,
            Type = EnumDamageType.PiercingAttack,
            DamageTier = 5,
            IgnoreInvFrames = true
        };

        private Entity SpawnPigNear(Entity player)
        {
            var type = _sapi.World.EntityTypes.FirstOrDefault(
                t => t?.Code?.Path != null && t.Code.Path.StartsWith("pig-") && t.Code.Path.Contains("-adult-"));
            if (type == null) return null;
            Entity e = _sapi.World.ClassRegistry.CreateEntity(type);
            var pp = player.ServerPos;
            e.ServerPos.SetPos(pp.X + 7, pp.Y + 0.5, pp.Z);
            e.Pos.SetFrom(e.ServerPos);
            _sapi.World.SpawnEntity(e);
            return e;
        }
    }

    /// <summary>Client half: rendered truth. Counts the projectile entities the client
    /// actually holds and photographs the field, on the server's synced phase marker.</summary>
    public class ArrowFieldClientObserver : ModSystem
    {
        private ICoreClientAPI _capi = null!;
        private string _shotDir = "";
        private int _total, _passed;
        private long _tickId;
        private string _lastPhase = "";
        private bool _done;
        private Shot _shot;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

        public override void StartClientSide(ICoreClientAPI api)
        {
            if (Environment.GetEnvironmentVariable("TASSHUNTING_ARROWFIELD") != "1") return;
            _capi = api;
            _shotDir = Environment.GetEnvironmentVariable("TASSHUNTING_ARROWFIELD_SHOTS") ?? "";
            _shot = new Shot(api, _shotDir, msg => api.Logger.Notification("[arrowfield] {0}", msg));
            api.Event.RegisterRenderer(_shot, EnumRenderStage.Done, "tasshuntingarrowfieldshot");
            _tickId = api.Event.RegisterGameTickListener(Tick, 500, 0);
            api.Logger.Notification("[arrowfield] client observer armed, shots to '{0}'.", _shotDir);
        }

        private void Check(string name, bool ok, string detail = null)
        {
            _total++;
            if (ok) _passed++;
            _capi.Logger.Notification("[arrowfield] {0} {1}{2}", ok ? "PASS" : "FAIL", name,
                detail == null ? "" : " (" + detail + ")");
        }

        private int CountArrows()
        {
            var ent = _capi.World?.Player?.Entity;
            if (ent == null) return -1;
            var got = _capi.World.GetEntitiesAround(ent.Pos.XYZ, 40f, 20f,
                e => e is EntityProjectileBase);
            return got?.Length ?? 0;
        }

        private void Tick(float dt)
        {
            try
            {
                if (_done) return;
                var ent = _capi.World?.Player?.Entity;
                if (ent == null) return;
                CloseCharacterDialog();

                string phase = ent.WatchedAttributes.GetString("tassaf_phase", "");
                if (phase == _lastPhase) return;
                _lastPhase = phase;

                if (phase == "volleyed")
                {
                    int n = CountArrows();
                    Check("client-sees-arrow-field", n >= 12, $"client holds {n} projectile entities");
                    _shot.Request("arrowfield-volleyed");
                }
                else if (phase == "final")
                {
                    int n = CountArrows();
                    Check("client-field-cleared", n <= 2, $"client holds {n} projectile entities");
                    _shot.Request("arrowfield-final");
                    _done = true;
                    _capi.Logger.Notification("[arrowfield] ARROWFIELD CLIENT COMPLETE total={0} pass={1} fail={2}",
                        _total, _passed, _total - _passed);
                    try { _capi.Event.UnregisterGameTickListener(_tickId); } catch { }
                }
            }
            catch (Exception e)
            {
                _capi.Logger.Error("[arrowfield] client tick failed: {0}", e);
            }
        }

        private void CloseCharacterDialog()
        {
            try
            {
                var open = _capi.Gui?.OpenedGuis;
                if (open == null) return;
                for (int i = open.Count - 1; i >= 0; i--)
                {
                    var dlg = open[i];
                    if (dlg != null && dlg.GetType().Name == "GuiDialogCreateCharacter") dlg.TryClose();
                }
            }
            catch { }
        }

        /// <summary>Framebuffer PNGs on the render thread (rejoin-test pattern).</summary>
        private class Shot : IRenderer
        {
            private readonly ICoreClientAPI _capi;
            private readonly string _dir;
            private readonly Action<string> _log;
            private string _pending;

            public Shot(ICoreClientAPI capi, string dir, Action<string> log) { _capi = capi; _dir = dir; _log = log; }
            public double RenderOrder => 1.0;
            public int RenderRange => 0;
            public void Request(string name) { _pending = name; }

            public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
            {
                string name = _pending;
                if (name == null || string.IsNullOrEmpty(_dir)) return;
                _pending = null;
                try
                {
                    Directory.CreateDirectory(_dir);
                    string path = Path.Combine(_dir, name + ".png");
                    using (var bmp = _capi.Render.GrabScreenshot(_capi.Render.FrameWidth, _capi.Render.FrameHeight, false, true))
                    {
                        bmp.Save(path);
                    }
                    _log("screenshot saved: " + path);
                }
                catch (Exception e) { _log("screenshot failed: " + e.Message); }
            }

            public void Dispose() { }
        }
    }
}
