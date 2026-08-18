using System;
using System.Linq;
using TassHunting;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace TassHuntingCompatHarness
{
    /// <summary>
    /// The exit-mid-bleed field sequence (report 2026-08-18): leave a world while bleeding,
    /// come back, and the bleed box never counts down, deals no damage, and no bandage can
    /// close it. The wound ledger is server-session memory, but its published mirror
    /// ("thbleed" and friends) rides the entity's WatchedAttributes into the save - and a
    /// rejoining player re-enters through SpawnEntity with a FRESH entity id
    /// (decompile-verified ServerMain.SpawnEntity_internal), so the ledger can never
    /// reattach and nothing ever zeroed the mirror.
    ///
    /// Two boots of the SAME world, driven by Run-RejoinTest.ps1:
    ///   TASSHUNTING_REJOINTEST=1  wound a pig through the real damage funnel, prove it is
    ///                             bleeding, then shut the server down cleanly - the exact
    ///                             thing "exit world" does mid-bleed.
    ///   TASSHUNTING_REJOINTEST=2  same world boots again. The reloaded pig must come back
    ///                             clean (no phantom bleed state), a dressing must clear a
    ///                             phantom even without the load-time scrub, the SpawnEntity
    ///                             path (how a rejoining PLAYER re-enters) must scrub a
    ///                             poisoned entity, and fresh wounds must still work.
    /// PASS/FAIL log lines ending in "REJOINTEST COMPLETE total= pass= fail=".
    /// </summary>
    public class RejoinHarnessSystem : ModSystem
    {
        private ICoreServerAPI _sapi = null!;
        private int _total, _passed;
        private string? _phase;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            _phase = Environment.GetEnvironmentVariable("TASSHUNTING_REJOINTEST");
            if (_phase != "1" && _phase != "2") return;
            _sapi = api;
            api.Event.SaveGameLoaded += () => api.Event.RegisterCallback(_ =>
            {
                if (_phase == "1") RunPhase1(); else RunPhase2();
            }, 8000);
            api.Logger.Notification("[rejointest] armed, phase {0}.", _phase);
        }

        private void Check(string name, bool ok)
        {
            _total++;
            if (ok) _passed++;
            _sapi.Logger.Notification("[rejointest] {0} {1}", ok ? "PASS" : "FAIL", name);
        }

        private void Done() =>
            _sapi.Logger.Notification("[rejointest] REJOINTEST COMPLETE total={0} pass={1} fail={2}", _total, _passed, _total - _passed);

        private void Crash(Exception e)
        {
            _sapi.Logger.Error("[rejointest] EXCEPTION: {0}", e);
            Check("no-exception", false);
            Done();
        }

        private Entity? SpawnPig()
        {
            var spawn = _sapi.World.DefaultSpawnPosition;
            var type = _sapi.World.EntityTypes.FirstOrDefault(
                t => t?.Code?.Path != null && t.Code.Path.StartsWith("pig-") && t.Code.Path.Contains("-adult-"));
            if (type == null) return null;
            Entity e = _sapi.World.ClassRegistry.CreateEntity(type);
            e.ServerPos.SetPos(spawn.X + 2, spawn.Y + 1, spawn.Z);
            e.Pos.SetFrom(e.ServerPos);
            _sapi.World.SpawnEntity(e);
            return e;
        }

        /// <summary>A sourceless piercing hit: no attacker entity needed, class multiplier 1.</summary>
        private static DamageSource Sharp() => new DamageSource
        {
            Source = EnumDamageSource.Entity,
            Type = EnumDamageType.PiercingAttack,
            DamageTier = 2
        };

        private static DamageSource Dressing() => new DamageSource
        {
            Source = EnumDamageSource.Internal,
            Type = EnumDamageType.Heal,
            Duration = TimeSpan.FromSeconds(10),
            TicksPerDuration = 10
        };

        // ---- Phase 1: bleed, then exit the world mid-bleed -----------------------------------

        private void RunPhase1()
        {
            try
            {
                // Wounds must outlive the shutdown/reboot gap on the wall clock.
                HuntingModSystem.Cfg.BleedWoundSeconds = 600f;

                var pig = SpawnPig();
                Check("phase1-spawned", pig != null);
                if (pig == null) { Done(); ShutDownSoon(); return; }

                _sapi.Event.RegisterCallback(_ =>
                {
                    try
                    {
                        pig.ReceiveDamage(Sharp(), 3.5f);
                        _sapi.Event.RegisterCallback(_2 =>
                        {
                            try
                            {
                                var hb = pig.GetBehavior<EntityBehaviorHealth>();
                                Check("phase1-wounded", pig.WatchedAttributes.GetInt("thbleed", 0) == 1);
                                // The health scar is phase 2's witness that the reloaded pig
                                // really is the one that carried a wound into the save.
                                Check("phase1-health-scar", hb != null && hb.Health <= hb.MaxHealth - 1.5f);
                                // Chunk entities keep their EntityId across save/load (only
                                // players are renumbered) - store it so phase 2 finds THIS
                                // pig, not some wild one that wandered in near spawn.
                                _sapi.WorldManager.SaveGame.StoreData("rejointest:pigid",
                                    BitConverter.GetBytes(pig.EntityId));
                                _sapi.Logger.Notification("[rejointest] PHASE1 COMPLETE total={0} pass={1} fail={2}",
                                    _total, _passed, _total - _passed);
                                // Clean shutdown WHILE BLEEDING - the singleplayer "exit world".
                                ShutDownSoon();
                            }
                            catch (Exception e) { Crash(e); }
                        }, 700);
                    }
                    catch (Exception e) { Crash(e); }
                }, 1500);
            }
            catch (Exception e) { Crash(e); }
        }

        private void ShutDownSoon() => _sapi.Event.RegisterCallback(_ => _sapi.Server.ShutDown(), 1000);

        // ---- Phase 2: the same world boots again ---------------------------------------------

        private void RunPhase2()
        {
            try
            {
                // Every loaded pig, for the log: id, bleed state, health. The first run of
                // this test matched "any pig" and caught a full-health WILD pig instead of
                // the phase 1 one - this sweep is what showed it.
                foreach (var e in _sapi.World.LoadedEntities.Values.Where(
                    e => e?.Code?.Path != null && e.Code.Path.StartsWith("pig-")))
                {
                    var ehb = e.GetBehavior<EntityBehaviorHealth>();
                    _sapi.Logger.Notification("[rejointest] loaded pig id={0} thbleed={1} health={2}/{3}",
                        e.EntityId, e.WatchedAttributes.GetInt("thbleed", 0),
                        ehb?.Health ?? -1f, ehb?.MaxHealth ?? -1f);
                }

                // The pig from phase 1, reloaded with its chunk, found by its STORED entity
                // id. FAIL LOUDLY if it is gone - never retarget.
                byte[]? idBytes = _sapi.WorldManager.SaveGame.GetData("rejointest:pigid");
                Check("rejoin-pigid-stored", idBytes != null && idBytes.Length == 8);
                Entity? pig = null;
                if (idBytes != null && idBytes.Length == 8)
                    _sapi.World.LoadedEntities.TryGetValue(BitConverter.ToInt64(idBytes, 0), out pig);
                Check("rejoin-pig-found", pig != null);
                if (pig == null) { Done(); return; }

                var hb = pig.GetBehavior<EntityBehaviorHealth>();
                Check("rejoin-health-scar-persisted", hb != null && hb.Health <= hb.MaxHealth - 1.5f);

                // THE FIELD BUG: the reloaded entity must not wear stale bleed state.
                Check("rejoin-stale-bleed-scrubbed",
                    pig.WatchedAttributes.GetInt("thbleed", 0) == 0
                    && !pig.WatchedAttributes.HasAttribute("thbleeddmg")
                    && !pig.WatchedAttributes.HasAttribute("thbleedtick"));
                Check("rejoin-ledger-empty", BleedSystem.StacksOn(pig.EntityId) == 0);

                RunPhantomDressing(pig);
            }
            catch (Exception e) { Crash(e); }
        }

        /// <summary>
        /// "Healing stops working": a save poisoned with phantom bleed state has no ledger
        /// entry, and the old ClearWounds only wiped the published state when the ledger had
        /// wounds - so the dressing visibly did nothing. Stage exactly that (stale attributes,
        /// empty ledger) and the dressing must wipe it anyway.
        /// </summary>
        private void RunPhantomDressing(Entity pig)
        {
            try
            {
                pig.WatchedAttributes.SetInt("thbleed", 3);
                pig.WatchedAttributes.SetString("tasshunt:bleedByUid", "stale-uid");
                pig.WatchedAttributes.SetString("tasshunt:bleedByName", "stale-name");
                pig.WatchedAttributes.SetLong("tasshunt:bleedByMs", 1L);
                pig.ReceiveDamage(Dressing(), 2f);
                Check("rejoin-dressing-clears-phantom",
                    pig.WatchedAttributes.GetInt("thbleed", 0) == 0
                    && !pig.WatchedAttributes.HasAttribute("tasshunt:bleedByUid"));

                RunSpawnPathScrub(pig);
            }
            catch (Exception e) { Crash(e); }
        }

        /// <summary>
        /// The PLAYER rejoin path, staged mechanically: a rejoining player's entity is
        /// deserialized with its saved attributes intact and re-enters the world through
        /// SpawnEntity (fresh entity id, OnEntitySpawn - not OnEntityLoaded). Poison an
        /// entity's attributes BEFORE SpawnEntity and the moment it enters the world it must
        /// come out clean.
        /// </summary>
        private void RunSpawnPathScrub(Entity firstPig)
        {
            try
            {
                var spawn = _sapi.World.DefaultSpawnPosition;
                var type = _sapi.World.EntityTypes.FirstOrDefault(
                    t => t?.Code?.Path != null && t.Code.Path.StartsWith("pig-") && t.Code.Path.Contains("-adult-"));
                Check("rejoin-spawnpath-type", type != null);
                if (type == null) { Done(); return; }

                Entity poisoned = _sapi.World.ClassRegistry.CreateEntity(type);
                poisoned.ServerPos.SetPos(spawn.X + 5, spawn.Y + 1, spawn.Z);
                poisoned.Pos.SetFrom(poisoned.ServerPos);
                poisoned.WatchedAttributes.SetInt("thbleed", 2);
                poisoned.WatchedAttributes.SetInt("thbleedsecs", 44);
                poisoned.WatchedAttributes.SetInt("thbleedtick", 7);
                poisoned.WatchedAttributes.SetFloat("thbleeddmg", 0.5f);
                poisoned.WatchedAttributes.SetString("tasshunt:bleedByUid", "stale-uid");
                _sapi.World.SpawnEntity(poisoned);

                _sapi.Event.RegisterCallback(_ =>
                {
                    try
                    {
                        Check("rejoin-spawnpath-scrubbed",
                            poisoned.WatchedAttributes.GetInt("thbleed", 0) == 0
                            && poisoned.WatchedAttributes.GetInt("thbleedsecs", 0) == 0
                            && !poisoned.WatchedAttributes.HasAttribute("thbleedtick")
                            && !poisoned.WatchedAttributes.HasAttribute("thbleeddmg")
                            && !poisoned.WatchedAttributes.HasAttribute("tasshunt:bleedByUid"));
                        RunFreshWound(firstPig);
                    }
                    catch (Exception e) { Crash(e); }
                }, 500);
            }
            catch (Exception e) { Crash(e); }
        }

        /// <summary>After all the scrubbing, this session's bleed must still work end to end:
        /// wound opens, a tick lands, a dressing closes it.</summary>
        private void RunFreshWound(Entity pig)
        {
            try
            {
                var hb = pig.GetBehavior<EntityBehaviorHealth>();
                if (hb != null) hb.Health = hb.MaxHealth;
                pig.ReceiveDamage(Sharp(), 2.5f);
                _sapi.Event.RegisterCallback(_ =>
                {
                    try
                    {
                        Check("rejoin-fresh-wound-opens", pig.WatchedAttributes.GetInt("thbleed", 0) == 1);
                        _sapi.Event.RegisterCallback(_2 =>
                        {
                            try
                            {
                                // Tick evidence via the dedicated counter - a health read here
                                // would be confounded by the phantom dressing's heal-over-time.
                                Check("rejoin-fresh-wound-ticks",
                                    pig.WatchedAttributes.GetFloat("thbleeddmg", 0f) > 0f
                                    && pig.WatchedAttributes.GetInt("thbleedtick", 0) >= 1);
                                pig.ReceiveDamage(Dressing(), 2f);
                                Check("rejoin-dressing-still-works", pig.WatchedAttributes.GetInt("thbleed", 0) == 0);
                                Done();
                            }
                            catch (Exception e) { Crash(e); }
                        }, 4000);
                    }
                    catch (Exception e) { Crash(e); }
                }, 700);
            }
            catch (Exception e) { Crash(e); }
        }
    }
}
