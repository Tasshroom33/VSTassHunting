// Real-client test of the exit-mid-bleed field report (Sanches31 2026-08-18), driven by
// Run-RejoinClientTest.ps1. Same world, same player account, two joins:
//
//   TASSHUNTING_REJOINCLIENT=1  server wounds the joined player; the CLIENT asserts the
//       bleeding box is really on screen and photographs it, then logs PHASE1 COMPLETE.
//       The script kills the client mid-bleed - the field "exit world" - and the server
//       shuts down cleanly right after the disconnect.
//   TASSHUNTING_REJOINCLIENT=2  the same account rejoins the same world. The CLIENT
//       asserts the field symptom is NOT there (no phantom box, thbleed 0), then the
//       server wounds and dresses the player again to prove bleeding AND healing still
//       work live - the exact things the report says break.
//
// Results are PASS/FAIL lines plus "REJOINCLIENT ... COMPLETE total= pass= fail=" in the
// CLIENT log, with framebuffer PNGs for the eyes.

using System;
using System.IO;
using TassHunting;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace TassHuntingCompatHarness
{
    /// <summary>Server half: wound the joined player; in phase 1 shut down after they exit,
    /// in phase 2 wound again and then dress, so the client can watch the whole cycle.</summary>
    public class RejoinClientServerDriver : ModSystem
    {
        private ICoreServerAPI _sapi = null!;
        private string? _phase;
        private bool _fired;
        private Entity? _attacker;
        private long _keepAliveId;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            _phase = Environment.GetEnvironmentVariable("TASSHUNTING_REJOINCLIENT");
            if (_phase != "1" && _phase != "2") return;
            _sapi = api;
            api.Event.PlayerNowPlaying += OnPlaying;
            if (_phase == "1") api.Event.PlayerDisconnect += OnLeft;
            api.Logger.Notification("[rejoinclient] server driver armed, phase {0}.", _phase);
        }

        private void OnPlaying(IServerPlayer player)
        {
            if (_fired) return;
            _fired = true;
            var ent = player.Entity;
            // What did the join hand the player? In phase 2 this is the verdict line: a
            // build without the scrub logs thbleed>0 here.
            _sapi.Logger.Notification(
                "[rejoinclient] server: joined, thbleed={0} thbleedsecs={1} bleedByUid={2}",
                ent.WatchedAttributes.GetInt("thbleed", 0),
                ent.WatchedAttributes.GetInt("thbleedsecs", 0),
                ent.WatchedAttributes.HasAttribute("tasshunt:bleedByUid"));
            // Settle first: the client needs its hud built before the attributes mean
            // anything on that side. Phase 2 waits longer so the client can finish its
            // no-phantom checks on an untouched entity first.
            _sapi.Event.RegisterCallback(_ => Wound(player), _phase == "1" ? 6000 : 12000);
        }

        private void Wound(IServerPlayer player)
        {
            try
            {
                var ent = player?.Entity;
                if (ent == null) return;
                // Long wounds: phase 1's must outlive the exit, phase 2's the checks.
                HuntingModSystem.Cfg.BleedWoundSeconds = 600f;
                HuntingModSystem.Cfg.BleedAffectsPlayers = true;

                // A real cause entity, not a nicety: EntityPlayer.OnHurt NREs on a null one.
                _attacker = SpawnAttacker(ent);

                // Keep the player alive - a death clears wounds too and fakes the result.
                _keepAliveId = _sapi.Event.RegisterGameTickListener(_ =>
                {
                    var hb = ent.GetBehavior<EntityBehaviorHealth>();
                    if (hb != null && hb.Health < hb.MaxHealth) hb.Health = hb.MaxHealth;
                }, 500);

                if (_phase == "1")
                {
                    ent.ReceiveDamage(Bite(), 1.5f);
                    _sapi.Event.RegisterCallback(_ =>
                    {
                        ent.ReceiveDamage(Bite(), 1.5f);
                        _sapi.Logger.Notification("[rejoinclient] server: wounded, thbleed={0}",
                            ent.WatchedAttributes.GetInt("thbleed", 0));
                    }, 900);
                    // Belt: if the disconnect is never seen, shut down anyway - the save
                    // still carries the mid-bleed state, which is the scenario.
                    _sapi.Event.RegisterCallback(_ => ShutDownOnce("fallback"), 90000);
                }
                else
                {
                    // Phase 2, in the FIELD ORDER. The bandage goes on BEFORE any fresh
                    // wound: on a broken build a fresh wound would rebuild the ledger and
                    // let the dressing "work", hiding exactly the symptom the report
                    // describes (bandaging a phantom does nothing). Then a fresh wound and
                    // a second dressing prove the live cycle still works.
                    Dress(ent, "first (on whatever the rejoin shows)");
                    _sapi.Event.RegisterCallback(_ =>
                    {
                        ent.ReceiveDamage(Bite(), 1.5f);
                        _sapi.Event.RegisterCallback(_2 =>
                        {
                            ent.ReceiveDamage(Bite(), 1.5f);
                            _sapi.Logger.Notification("[rejoinclient] server: fresh wounds, thbleed={0}",
                                ent.WatchedAttributes.GetInt("thbleed", 0));
                        }, 900);
                    }, 10000);
                    _sapi.Event.RegisterCallback(_ => Dress(ent, "second (on the fresh wounds)"), 20000);
                }
            }
            catch (Exception e) { _sapi.Logger.Error("[rejoinclient] server driver failed: {0}", e); }
        }

        private void Dress(Entity ent, string which)
        {
            _sapi.Logger.Notification("[rejoinclient] server: alive={0} before {1} dressing", ent.Alive, which);
            ent.ReceiveDamage(new DamageSource
            {
                Source = EnumDamageSource.Internal,
                Type = EnumDamageType.Heal,
                Duration = TimeSpan.FromSeconds(10),
                TicksPerDuration = 10
            }, 4f);
            _sapi.Logger.Notification("[rejoinclient] server: {0} dressing applied, thbleed={1}",
                which, ent.WatchedAttributes.GetInt("thbleed", 0));
        }

        private void OnLeft(IServerPlayer player)
        {
            _sapi.Logger.Notification("[rejoinclient] server: player disconnected mid-bleed, shutting down.");
            try { if (_keepAliveId != 0) _sapi.Event.UnregisterGameTickListener(_keepAliveId); } catch { }
            _keepAliveId = 0;
            // Quick like the singleplayer exit: despawn-retire ticks between disconnect and
            // save are part of the real sequence either way.
            _sapi.Event.RegisterCallback(_ => ShutDownOnce("disconnect"), 1500);
        }

        private bool _shutDown;
        private void ShutDownOnce(string why)
        {
            if (_shutDown) return;
            _shutDown = true;
            _sapi.Logger.Notification("[rejoinclient] server: ShutDown ({0}).", why);
            _sapi.Server.ShutDown();
        }

        private DamageSource Bite() => new DamageSource
        {
            Source = EnumDamageSource.Entity,
            SourceEntity = _attacker,
            Type = EnumDamageType.PiercingAttack,
            DamageTier = 3
        };

        private Entity? SpawnAttacker(Entity near)
        {
            var type = System.Linq.Enumerable.FirstOrDefault(_sapi.World.EntityTypes,
                t => t?.Code?.Path != null && t.Code.Path.StartsWith("pig-") && t.Code.Path.Contains("-adult-"));
            if (type == null) return null;
            Entity e = _sapi.World.ClassRegistry.CreateEntity(type);
            e.Pos.SetPos(near.Pos.X + 3, near.Pos.Y, near.Pos.Z);
            e.ServerPos.SetFrom(e.Pos);
            _sapi.World.SpawnEntity(e);
            return e;
        }
    }

    /// <summary>Client half: assert what the player actually sees, and photograph it.</summary>
    public class RejoinClientHarnessSystem : ModSystem
    {
        private ICoreClientAPI _capi = null!;
        private string? _phase;
        private string _shotDir = "";
        private int _total, _passed;
        private int _stage;
        private long _tickId;
        private Grabber? _grabber;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

        public override void StartClientSide(ICoreClientAPI api)
        {
            _phase = Environment.GetEnvironmentVariable("TASSHUNTING_REJOINCLIENT");
            if (_phase != "1" && _phase != "2") return;
            _capi = api;
            _shotDir = Environment.GetEnvironmentVariable("TASSHUNTING_REJOINCLIENT_SHOTS") ?? "";
            _grabber = new Grabber(api, _shotDir, msg => api.Logger.Notification("[rejoinclient] {0}", msg));
            api.Event.RegisterRenderer(_grabber, EnumRenderStage.Done, "tasshuntingrejoinclientgrab");
            // permittedWhilePaused: a fresh join can sit paused on a dialog.
            _tickId = api.Event.RegisterGameTickListener(Tick, 500, 0);
            api.Logger.Notification("[rejoinclient] client armed, phase {0}, shots to '{1}'.", _phase, _shotDir);
        }

        private void Check(string name, bool ok)
        {
            _total++;
            if (ok) _passed++;
            _capi.Logger.Notification("[rejoinclient] {0} {1}", ok ? "PASS" : "FAIL", name);
        }

        private void Done(string label)
        {
            _capi.Logger.Notification("[rejoinclient] REJOINCLIENT {0} COMPLETE total={1} pass={2} fail={3}",
                label, _total, _passed, _total - _passed);
            try { if (_tickId != 0) _capi.Event.UnregisterGameTickListener(_tickId); } catch { }
            _tickId = 0;
        }

        private void Tick(float dt)
        {
            try
            {
                var ent = _capi.World?.Player?.Entity;
                if (ent == null || _stage != 0) return;
                CloseCharacterDialog();
                if (_phase == "1")
                {
                    // Wait to be wounded, then look at the box.
                    if (ent.WatchedAttributes.GetInt("thbleed", 0) <= 0) return;
                    _stage = 1;
                    _capi.Event.RegisterCallback(_ => Phase1WoundedChecks(ent), 800, true);
                }
                else
                {
                    // Phase 2: start the no-phantom watch once we are in the world.
                    _stage = 1;
                    _capi.Event.RegisterCallback(_ => Phase2RejoinChecks(ent), 5000, true);
                }
            }
            catch (Exception e)
            {
                _capi.Logger.Error("[rejoinclient] client tick failed: {0}", e);
                Check("no-exception", false);
                Done(_phase == "1" ? "PHASE1" : "PHASE2");
            }
        }

        // ---- Phase 1: the box is really on screen, then the script pulls the plug ----------

        private void Phase1WoundedChecks(Entity ent)
        {
            try
            {
                var hud = _capi.ModLoader.GetModSystem<BleedHudSystem>()?.Hud;
                Check("p1-hud-built", hud != null);
                Check("p1-box-open-while-bleeding", hud != null && hud.IsOpened());
                Check("p1-label-reads-bleeding", hud?.Label != null && hud.Label.StartsWith("Bleeding"));
                _capi.Logger.Notification("[rejoinclient] p1 label='{0}' thbleed={1} secs={2}",
                    hud?.Label, ent.WatchedAttributes.GetInt("thbleed", 0),
                    ent.WatchedAttributes.GetInt("thbleedsecs", 0));
                _grabber?.RequestShot("p1-bleeding-before-exit");
                // A beat for the screenshot to be taken on the render thread, then hand the
                // kill switch to the script.
                _capi.Event.RegisterCallback(_ => Done("PHASE1"), 1200, true);
            }
            catch (Exception e)
            {
                _capi.Logger.Error("[rejoinclient] phase1 checks failed: {0}", e);
                Check("p1-no-exception", false);
                Done("PHASE1");
            }
        }

        // ---- Phase 2: no phantom, then bleeding and healing still work live ----------------

        private void Phase2RejoinChecks(Entity ent)
        {
            try
            {
                var hud = _capi.ModLoader.GetModSystem<BleedHudSystem>()?.Hud;
                // THE FIELD SYMPTOM: on a broken build the join snapshot already carries
                // thbleed>0 and the box is open by now.
                Check("p2-no-phantom-bleed", ent.WatchedAttributes.GetInt("thbleed", 0) == 0);
                Check("p2-no-phantom-countdown", ent.WatchedAttributes.GetInt("thbleedsecs", 0) == 0);
                Check("p2-box-closed-after-rejoin", hud == null || !hud.IsOpened());
                _capi.Logger.Notification("[rejoinclient] p2 rejoin thbleed={0} secs={1} boxopen={2}",
                    ent.WatchedAttributes.GetInt("thbleed", 0),
                    ent.WatchedAttributes.GetInt("thbleedsecs", 0), hud != null && hud.IsOpened());
                _grabber?.RequestShot("p2-rejoined-clean");

                // The server dresses BEFORE any fresh wound (field order). ~10s from now is
                // after that dressing and before the fresh wounds: whatever the rejoin put
                // on screen must be gone. On a build with the bug this is the report's
                // "healing stops working", photographed.
                _capi.Event.RegisterCallback(_ =>
                {
                    var hud2 = _capi.ModLoader.GetModSystem<BleedHudSystem>()?.Hud;
                    Check("p2-bandage-clears-shown-state",
                        ent.WatchedAttributes.GetInt("thbleed", 0) == 0
                        && (hud2 == null || !hud2.IsOpened()));
                    _grabber?.RequestShot("p2-after-first-dressing");
                    WaitForFreshWound(ent, 0);
                }, 10000, true);
            }
            catch (Exception e)
            {
                _capi.Logger.Error("[rejoinclient] phase2 rejoin checks failed: {0}", e);
                Check("p2-no-exception", false);
                Done("PHASE2");
            }
        }

        private void WaitForFreshWound(Entity ent, int attempt)
        {
            if (ent.WatchedAttributes.GetInt("thbleed", 0) > 0 || attempt >= 40)
            {
                _capi.Event.RegisterCallback(_ =>
                {
                    var hud = _capi.ModLoader.GetModSystem<BleedHudSystem>()?.Hud;
                    Check("p2-fresh-wound-box-opens", hud != null && hud.IsOpened()
                        && ent.WatchedAttributes.GetInt("thbleed", 0) > 0);
                    _grabber?.RequestShot("p2-rewounded");
                    WaitForClear(ent, 0);
                }, 800, true);
                return;
            }
            _capi.Event.RegisterCallback(_ => WaitForFreshWound(ent, attempt + 1), 1000, true);
        }

        private void WaitForClear(Entity ent, int attempt)
        {
            if (ent.WatchedAttributes.GetInt("thbleed", 0) == 0 || attempt >= 40)
            {
                _capi.Event.RegisterCallback(_ =>
                {
                    var hud = _capi.ModLoader.GetModSystem<BleedHudSystem>()?.Hud;
                    Check("p2-player-survived", ent.Alive);
                    Check("p2-dressing-cleared-wounds", ent.WatchedAttributes.GetInt("thbleed", 0) == 0);
                    Check("p2-box-closed-after-dressing", hud == null || !hud.IsOpened());
                    _grabber?.RequestShot("p2-healed");
                    _capi.Event.RegisterCallback(_2 => Done("PHASE2"), 1000, true);
                }, 800, true);
                return;
            }
            _capi.Event.RegisterCallback(_ => WaitForClear(ent, attempt + 1), 1000, true);
        }

        /// <summary>First join lands on the character-creation dialog; close it by type name.</summary>
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

        /// <summary>Framebuffer PNGs, taken on the render thread at the Done stage.</summary>
        private class Grabber : IRenderer
        {
            private readonly ICoreClientAPI _capi;
            private readonly string _dir;
            private readonly Action<string> _log;
            private string? _pending;

            public Grabber(ICoreClientAPI capi, string dir, Action<string> log)
            {
                _capi = capi; _dir = dir; _log = log;
            }

            public double RenderOrder => 1.0;
            public int RenderRange => 0;

            public void RequestShot(string name) { _pending = name; }

            public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
            {
                string? name = _pending;
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
