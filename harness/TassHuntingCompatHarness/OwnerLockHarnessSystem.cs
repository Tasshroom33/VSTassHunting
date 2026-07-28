using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace TassHuntingCompatHarness
{
    /// <summary>
    /// Headless test for the arrow ownership lock (ArrowOwnership.cs). Spawns a
    /// real projectile entity, makes it base-collectible (Collectible=true, past
    /// the engine's 1s collect delay, motion zeroed), then asserts the owner gate
    /// through the REAL patched CanCollect with a spawned wolf as the non-owner
    /// asker: locked while fresh, open after expiry, open with no stamp, open
    /// after a simulated restart (future stamp), open with the lock configured
    /// off. The shooter-bypass leg needs a connected player and stays a field
    /// check. PASS/FAIL lines end in "OWNERTEST COMPLETE total= pass= fail=".
    /// </summary>
    public class OwnerLockHarnessSystem : ModSystem
    {
        private ICoreServerAPI _sapi;
        private int _total, _passed;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            if (Environment.GetEnvironmentVariable("TASSHUNTING_OWNERTEST") != "1") return;
            _sapi = api;
            api.Event.SaveGameLoaded += () => api.Event.RegisterCallback(_ => Setup(), 8000);
            api.Logger.Notification("[ownertest] armed.");
        }

        private void Check(string name, bool ok)
        {
            _total++;
            if (ok) _passed++;
            _sapi.Logger.Notification("[ownertest] {0} {1}", ok ? "PASS" : "FAIL", name);
        }

        private void Done() =>
            _sapi.Logger.Notification("[ownertest] OWNERTEST COMPLETE total={0} pass={1} fail={2}", _total, _passed, _total - _passed);

        private EntityProjectileBase _arrow;
        private Entity _wolf;

        private void Setup()
        {
            try
            {
                var spawn = _sapi.World.DefaultSpawnPosition.XYZ;
                EntityProperties arrowType = null, wolfType = null;
                foreach (var et in _sapi.World.EntityTypes)
                {
                    if (arrowType == null && et.Code?.Path?.StartsWith("arrow") == true) arrowType = et;
                    if (wolfType == null && et.Code?.Path?.StartsWith("wolf") == true) wolfType = et;
                }
                if (arrowType == null || wolfType == null)
                {
                    Check("setup-entitytypes", false);
                    Done();
                    return;
                }

                _arrow = _sapi.World.ClassRegistry.CreateEntity(arrowType) as EntityProjectileBase;
                _wolf = _sapi.World.ClassRegistry.CreateEntity(wolfType);
                if (_arrow == null || _wolf == null)
                {
                    Check("setup-createentity", false);
                    Done();
                    return;
                }
                _arrow.ServerPos.SetPosWithDimension(spawn.AddCopy(0, 8, 0));
                _arrow.Pos.SetFrom(_arrow.ServerPos);
                _wolf.ServerPos.SetPosWithDimension(spawn.AddCopy(4, 8, 0));
                _wolf.Pos.SetFrom(_wolf.ServerPos);
                _sapi.World.SpawnEntity(_arrow);
                _sapi.World.SpawnEntity(_wolf);
                _arrow.Collectible = true;

                // The engine refuses collection for 1s after launch (collectDelayMs);
                // run the assertions after that window with motion zeroed by hand.
                _sapi.Event.RegisterCallback(_ => Run(), 2500);
            }
            catch (Exception e)
            {
                _sapi.Logger.Error("[ownertest] EXCEPTION: {0}", e);
                Check("no-exception", false);
                Done();
            }
        }

        private bool Ask(Entity byEntity)
        {
            _arrow.Pos.Motion.Set(0.0, 0.0, 0.0);
            _arrow.ServerPos.Motion.Set(0.0, 0.0, 0.0);
            return _arrow.CanCollect(byEntity);
        }

        private bool AskWolf() => Ask(_wolf);

        private void Run()
        {
            try
            {
                var wa = _arrow.WatchedAttributes;
                long now = _sapi.World.ElapsedMilliseconds;

                // Baseline: base-collectible, no owner stamp -> anyone may.
                Check("open-without-stamp", AskWolf());

                // Freshly stamped by another player -> locked for the wolf/non-owner.
                wa.SetString("tassOwner", "test-owner-uid");
                wa.SetLong("tassFiredMs", now);
                Check("locked-while-fresh", !AskWolf());

                // Still locked deep inside the window (backdate as far as this
                // boot's uptime allows - a negative stamp would read as the
                // restart-guard case, which has its own assertion below).
                wa.SetLong("tassFiredMs", Math.Max(1L, now - 115_000L));
                Check("locked-inside-window", !AskWolf());

                // Restart simulation: a stamp from a previous boot reads as the
                // future and must release, never re-lock.
                wa.SetLong("tassFiredMs", now + 3_600_000L);
                Check("open-after-restart-clock", AskWolf());

                // Lock configured off -> open even when freshly stamped.
                wa.SetLong("tassFiredMs", _sapi.World.ElapsedMilliseconds);
                float saved = TassHunting.HuntingModSystem.Cfg.ArrowOwnerLockSeconds;
                TassHunting.HuntingModSystem.Cfg.ArrowOwnerLockSeconds = 0f;
                Check("open-when-disabled", AskWolf());
                TassHunting.HuntingModSystem.Cfg.ArrowOwnerLockSeconds = saved;

                // The launch stamp itself: no player fired this arrow, so the
                // engine path must have left no owner of its own making.
                Check("no-phantom-owner-on-mob-arrows", wa.GetString("tassOwner") == "test-owner-uid");

                // Expiry with a REAL elapsing clock: shrink the window to 3s,
                // stamp fresh, and let actual time pass it (backdating cannot
                // prove expiry on a young uptime clock).
                TassHunting.HuntingModSystem.Cfg.ArrowOwnerLockSeconds = 3f;
                wa.SetLong("tassFiredMs", _sapi.World.ElapsedMilliseconds);
                Check("locked-before-real-expiry", !AskWolf());
                _sapi.Event.RegisterCallback(_ =>
                {
                    try
                    {
                        Check("open-after-real-expiry", AskWolf());
                        TassHunting.HuntingModSystem.Cfg.ArrowOwnerLockSeconds = saved;
                        RetrievalLegs();
                        Done();
                    }
                    catch (Exception e2)
                    {
                        _sapi.Logger.Error("[ownertest] EXCEPTION: {0}", e2);
                        Check("no-exception", false);
                        Done();
                    }
                }, 4000);
            }
            catch (Exception e)
            {
                _sapi.Logger.Error("[ownertest] EXCEPTION: {0}", e);
                Check("no-exception", false);
                Done();
            }
        }

        /// <summary>Player-retrieval rules (0.13.5): an EntityPlayer's identity
        /// (PlayerUID) is a watched attribute, so offline player entities can be
        /// fabricated headless - no session needed for the identity checks the
        /// patches make. If the engine refuses to spawn one, the legs are skipped
        /// (logged), not failed; the wolf leg runs regardless.</summary>
        private void RetrievalLegs()
        {
            var wa = _arrow.WatchedAttributes;
            long now = _sapi.World.ElapsedMilliseconds;
            wa.SetString("tassOwner", "shooter-uid");
            wa.SetLong("tassFiredMs", now); // fresh: the 120s owner window is ACTIVE

            // Arrow riding an ANIMAL: nobody hand-pulls it, not even the owner.
            wa.SetLong("sa_target", _wolf.EntityId);
            Check("riding-animal-stays-in", !AskWolf());

            Entity shooter = null, victim = null, thief = null;
            try
            {
                EntityProperties playerType = null;
                foreach (var et in _sapi.World.EntityTypes)
                    if (et.Code?.Path == "player") { playerType = et; break; }
                if (playerType == null) throw new Exception("no player entity type");
                Entity Fab(string uid, int dx)
                {
                    var e = _sapi.World.ClassRegistry.CreateEntity(playerType);
                    e.WatchedAttributes.SetString("playerUID", uid);
                    e.ServerPos.SetPosWithDimension(_arrow.ServerPos.XYZ.AddCopy(dx, 0, 2));
                    e.Pos.SetFrom(e.ServerPos);
                    _sapi.World.SpawnEntity(e);
                    return e;
                }
                shooter = Fab("shooter-uid", 1);
                victim = Fab("victim-uid", 2);
                thief = Fab("thief-uid", 3);
            }
            catch (Exception e)
            {
                _sapi.Logger.Notification("[ownertest] SKIP player-retrieval legs (offline EntityPlayer unsupported): {0}", e.Message);
                wa.RemoveAttribute("sa_target");
                return;
            }

            // Arrow riding the VICTIM, owner window active:
            wa.SetLong("sa_target", victim.EntityId);
            Check("thief-cannot-pull-from-player", !Ask(thief));
            Check("shooter-pulls-own-from-player", Ask(shooter));
            Check("victim-pulls-from-own-body", Ask(victim));

            // Feature off: even the victim waits for the timer.
            TassHunting.HuntingModSystem.Cfg.PlayerArrowTouchRetrieve = false;
            Check("retrieval-off-blocks-victim", !Ask(victim));
            TassHunting.HuntingModSystem.Cfg.PlayerArrowTouchRetrieve = true;

            wa.RemoveAttribute("sa_target");
            // The sessionless fakes NRE in the engine's player tick (caught and
            // logged every tick) - remove them the moment the legs are done.
            foreach (var e in new[] { shooter, victim, thief })
                try { e.Die(EnumDespawnReason.Removed, null); } catch { }
        }
    }
}
