// Real-client test for the bleeding box (TASSHUNTING_HUDTEST=1). Two halves that talk to each
// other only through the game itself, which is the point - nothing is faked:
//
//   SERVER (BleedHudServerDriver): waits for the test player to join, opens a wound on them with
//   a real sharp DamageSource, waits, then applies a real healing-item DamageSource (the shape a
//   finished bandage sends).
//
//   CLIENT (BleedHudHarnessSystem): watches its own player entity and asserts what the player
//   would see - the box open with the right label while wounded, the countdown ticking down,
//   and the box gone after the dressing. It also saves PNG grabs of
//   the actual framebuffer so the box can be looked at, not just asserted about.
//
// Results are PASS/FAIL lines plus "HUDTEST COMPLETE total= pass= fail=" in the CLIENT log.
//
// MODINFO WARNING: this harness had to become side Universal to carry a client half, and
// "requiredOnClient": false MUST stay in its modinfo.json. Without it the server demands the
// harness from every joining client, and the butcher-compat run - whose client profile carries
// only TassHunting and Butchering - is kicked for lacking mods before a single check runs.

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
    /// <summary>Server half: wound the joined player, then dress the wound.</summary>
    public class BleedHudServerDriver : ModSystem
    {
        private ICoreServerAPI _sapi = null!;
        private bool _fired;
        private Entity? _attacker;
        private long _keepAliveId;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            if (Environment.GetEnvironmentVariable("TASSHUNTING_HUDTEST") != "1") return;
            _sapi = api;
            api.Event.PlayerNowPlaying += OnPlaying;
            api.Logger.Notification("[hudtest] server driver armed.");
        }

        private void OnPlaying(IServerPlayer player)
        {
            if (_fired) return;
            _fired = true;
            // Settle first: the client needs its hud built and the entity tracked before the
            // watched attributes mean anything on that side.
            _sapi.Event.RegisterCallback(_ => Wound(player), 6000);
        }

        private void Wound(IServerPlayer player)
        {
            try
            {
                var ent = player?.Entity;
                if (ent == null) return;
                // Long wounds so the countdown is unmistakable in the label and the screenshot.
                HuntingModSystem.Cfg.BleedWoundSeconds = 120f;
                HuntingModSystem.Cfg.BleedAffectsPlayers = true;

                // A real cause entity is required, not a nicety: vanilla EntityPlayer.OnHurt
                // (EntityPlayer.cs:1290) calls GetCauseEntity().GetPrefixAndCreatureName for the
                // damage-log line whenever Source is Entity, and NREs on a null one.
                _attacker = SpawnAttacker(ent);
                _sapi.Logger.Notification("[hudtest] server: attacker spawned={0}", _attacker != null);

                // KEEP THE PLAYER ALIVE for the whole run. A dead player's wounds are retired by
                // the bleed system too, so a death would clear the box and make the dressing check
                // pass for the wrong reason - which is exactly what the first run did (the test
                // player died in a fall a second before the dressing was due, and three checks
                // "passed" without a bandage ever being applied).
                _keepAliveId = _sapi.Event.RegisterGameTickListener(_ =>
                {
                    var hb = ent.GetBehavior<EntityBehaviorHealth>();
                    if (hb != null && hb.Health < hb.MaxHealth) hb.Health = hb.MaxHealth;
                }, 500);

                ent.ReceiveDamage(Bite(), 1.5f);
                _sapi.Logger.Notification("[hudtest] server: wound opened, thbleed={0}",
                    ent.WatchedAttributes.GetInt("thbleed", 0));

                // Second wound past the invulnerability window, so the label shows a count.
                _sapi.Event.RegisterCallback(_ =>
                {
                    ent.ReceiveDamage(Bite(), 1.5f);
                    _sapi.Logger.Notification("[hudtest] server: second wound, thbleed={0}",
                        ent.WatchedAttributes.GetInt("thbleed", 0));
                }, 900);

                // Then the dressing, the exact DamageSource a finished poultice sends. 12s leaves
                // the client its ~6s of checks and screenshots with room to spare; the first run
                // dressed at 16s, which was after the client had already given up waiting.
                _sapi.Event.RegisterCallback(_ =>
                {
                    _sapi.Logger.Notification("[hudtest] server: alive={0} before dressing",
                        ent.Alive);
                    ent.ReceiveDamage(new DamageSource
                    {
                        Source = EnumDamageSource.Internal,
                        Type = EnumDamageType.Heal,
                        Duration = TimeSpan.FromSeconds(10),
                        TicksPerDuration = 10
                    }, 4f);
                    _sapi.Logger.Notification("[hudtest] server: dressing applied, thbleed={0}",
                        ent.WatchedAttributes.GetInt("thbleed", 0));
                    try { if (_keepAliveId != 0) _sapi.Event.UnregisterGameTickListener(_keepAliveId); } catch { }
                    _keepAliveId = 0;
                }, 12000);
            }
            catch (Exception e) { _sapi.Logger.Error("[hudtest] server driver failed: {0}", e); }
        }

        private DamageSource Bite() => new DamageSource
        {
            Source = EnumDamageSource.Entity,
            SourceEntity = _attacker,
            Type = EnumDamageType.PiercingAttack,
            DamageTier = 3
        };

        /// <summary>A real creature to blame the wound on, parked next to the player.</summary>
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

    /// <summary>Client half: assert what the player sees, and photograph it.</summary>
    public class BleedHudHarnessSystem : ModSystem
    {
        private ICoreClientAPI _capi = null!;
        private string _shotDir = "";
        private int _total, _passed;
        private int _phase;               // 0 waiting for wounds, 1 wounded checks done, 2 finished
        private long _tickId;
        private ScreenGrabber? _grabber;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

        public override void StartClientSide(ICoreClientAPI api)
        {
            if (Environment.GetEnvironmentVariable("TASSHUNTING_HUDTEST") != "1") return;
            _capi = api;
            _shotDir = Environment.GetEnvironmentVariable("TASSHUNTING_HUDTEST_SHOTS") ?? "";
            _grabber = new ScreenGrabber(api, _shotDir, msg => api.Logger.Notification("[hudtest] {0}", msg));
            api.Event.RegisterRenderer(_grabber, EnumRenderStage.Done, "tasshuntinghudtestgrab");
            // permittedWhilePaused: a fresh join can sit paused on a dialog, and a listener without
            // it never fires (learned the hard way in the factions ui test).
            _tickId = api.Event.RegisterGameTickListener(Tick, 500, 0);
            api.Logger.Notification("[hudtest] client armed, shots to '{0}'.", _shotDir);
        }

        private void Check(string name, bool ok)
        {
            _total++;
            if (ok) _passed++;
            _capi.Logger.Notification("[hudtest] {0} {1}", ok ? "PASS" : "FAIL", name);
        }

        private void Done()
        {
            _capi.Logger.Notification("[hudtest] HUDTEST COMPLETE total={0} pass={1} fail={2}",
                _total, _passed, _total - _passed);
            try { if (_tickId != 0) _capi.Event.UnregisterGameTickListener(_tickId); } catch { }
            _tickId = 0;
        }

        private void Tick(float dt)
        {
            try
            {
                var ent = _capi.World?.Player?.Entity;
                if (ent == null) return;
                CloseCharacterDialog();
                var hudSystem = _capi.ModLoader.GetModSystem<BleedHudSystem>();
                int wounds = ent.WatchedAttributes.GetInt("thbleed", 0);

                if (_phase == 0)
                {
                    if (wounds <= 0) return; // still waiting for the server to wound us
                    // Give the 200ms hud tick a beat to compose and open.
                    _phase = 1;
                    _capi.Event.RegisterCallback(_ => RunWoundedChecks(ent, hudSystem), 800, true);
                }
            }
            catch (Exception e)
            {
                _capi.Logger.Error("[hudtest] client tick failed: {0}", e);
                Check("no-exception", false);
                Done();
            }
        }

        /// <summary>
        /// A first join lands on the character-creation dialog, which covers the screen and pauses
        /// the client. Close it by type NAME so this needs no reference to the client assembly.
        /// </summary>
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
            catch { /* a closed dialog is a nicety, not a test result */ }
        }

        private void RunWoundedChecks(Entity ent, BleedHudSystem? hudSystem)
        {
            try
            {
                // The box survived composing. This is the check that caught the svg icon coming
                // back with null data: the compose threw, the client system dropped the hud, and
                // Hud went null - so a null here means the panel failed to build, not that it is
                // merely hidden.
                Check("hud-built-and-composed", hudSystem?.Hud != null);
                var hud = hudSystem?.Hud;
                if (hud == null) { Done(); return; }

                int wounds = ent.WatchedAttributes.GetInt("thbleed", 0);
                int secs = ent.WatchedAttributes.GetInt("thbleedsecs", 0);
                Check("countdown-published", secs > 0);
                Check("box-open-while-bleeding", hud.IsOpened());
                Check("label-reads-bleeding", hud.Label != null && hud.Label.StartsWith("Bleeding"));
                // The countdown switches unit as it shrinks (2.0m -> 45s), so accept any of them.
                Check("label-carries-countdown", hud.Label != null
                    && (hud.Label.EndsWith("s") || hud.Label.EndsWith("m") || hud.Label.EndsWith("h")));
                _capi.Logger.Notification("[hudtest] label='{0}' wounds={1} secs={2}", hud.Label, wounds, secs);

                CheckPosition(hud);

                _grabber?.RequestShot("box");

                // Second look once the countdown has visibly moved and a bleed tick has landed,
                // which is also what proves the label refreshes in place rather than sticking.
                _capi.Event.RegisterCallback(_ =>
                {
                    try
                    {
                        Check("countdown-ticks-down", ent.WatchedAttributes.GetInt("thbleedsecs", 0) < secs);
                        _grabber?.RequestShot("later");
                        // WAIT for the dressing rather than sleeping a guessed interval - an
                        // earlier run checked at 12s while the server dressed at 16s and reported
                        // three false failures.
                        WaitForClear(ent, hud, 0);
                    }
                    catch (Exception e) { _capi.Logger.Error("[hudtest] second look failed: {0}", e); Check("second-look-no-exception", false); Done(); }
                }, 4500, true);
            }
            catch (Exception e)
            {
                _capi.Logger.Error("[hudtest] wounded checks failed: {0}", e);
                Check("wounded-no-exception", false);
                Done();
            }
        }

        /// <summary>
        /// WHERE THE BOX ACTUALLY IS, measured off its composed bounds rather than trusted
        /// from the config (the render-vs-math rule: read the real rect). 0.14.15 drops the
        /// LeftMiddle anchor 50 unscaled px so the box stops sharing the XSkills effect
        /// frame's spot; the engine scales that by GUIScale, so the expected drop is
        /// 50 x GUIScale. The second check is the one that speaks to the field report: the
        /// box's TOP edge must now clear the screen's middle line entirely, which is where
        /// the XSkills frame centres itself - measured against the same parent bounds the
        /// layout used, so a windowed vs fullscreen client cannot skew it.
        /// </summary>
        private void CheckPosition(BleedHud hud)
        {
            try
            {
                var bounds = hud.SingleComposer?.Bounds;
                double parentH = bounds?.ParentBounds?.OuterHeight ?? 0;
                if (bounds == null || parentH <= 0)
                {
                    Check("box-position-measurable", false);
                    return;
                }
                double scale = Vintagestory.API.Config.RuntimeEnv.GUIScale;
                double screenMiddleY = parentH / 2.0;
                double boxTopY = bounds.absY;
                double boxCentreY = boxTopY + bounds.OuterHeight / 2.0;
                double drop = boxCentreY - screenMiddleY;
                double expected = 50.0 * scale;
                _capi.Logger.Notification(
                    "[hudtest] box rect: top={0:0.0} h={1:0.0} centre={2:0.0} | screen h={3:0.0} middle={4:0.0} | drop={5:0.0} expected={6:0.0} (guiscale {7:0.00})",
                    boxTopY, bounds.OuterHeight, boxCentreY, parentH, screenMiddleY, drop, expected, scale);
                Check("box-dropped-50px-scaled", Math.Abs(drop - expected) < 8.0);
                Check("box-clears-screen-middle", boxTopY > screenMiddleY);
            }
            catch (Exception e)
            {
                _capi.Logger.Error("[hudtest] position check failed: {0}", e);
                Check("box-position-no-exception", false);
            }
        }

        /// <summary>Poll until the server's dressing lands, then check, or give up and report it.</summary>
        private void WaitForClear(Entity ent, BleedHud hud, int attempt)
        {
            if (ent.WatchedAttributes.GetInt("thbleed", 0) == 0 || attempt >= 40)
            {
                // A beat for the 200ms hud tick to notice and close the box.
                _capi.Event.RegisterCallback(_ => RunClearedChecks(ent, hud), 600, true);
                return;
            }
            _capi.Event.RegisterCallback(_ => WaitForClear(ent, hud, attempt + 1), 1000, true);
        }

        private void RunClearedChecks(Entity ent, BleedHud hud)
        {
            try
            {
                _phase = 2;
                // Death clears wounds too, so "still alive" is what makes the next check mean
                // "the dressing did it" instead of "something killed us".
                Check("player-survived-the-test", ent.Alive);
                Check("dressing-cleared-wounds", ent.WatchedAttributes.GetInt("thbleed", 0) == 0);
                Check("box-closed-after-dressing", !hud.IsOpened());
                _grabber?.RequestShot("cleared");
                _capi.Event.RegisterCallback(_ => Done(), 800, true);
            }
            catch (Exception e)
            {
                _capi.Logger.Error("[hudtest] cleared checks failed: {0}", e);
                Check("cleared-no-exception", false);
                Done();
            }
        }

        /// <summary>
        /// Saves the real framebuffer to a PNG. GrabScreenshot only works on the render thread, so
        /// the test asks for a shot and this renderer takes it on the next frame - and at the Done
        /// stage, which is after the GUI has been drawn.
        /// </summary>
        private class ScreenGrabber : IRenderer
        {
            private readonly ICoreClientAPI _capi;
            private readonly string _dir;
            private readonly Action<string> _log;
            private string? _pending;

            public ScreenGrabber(ICoreClientAPI capi, string dir, Action<string> log)
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
