// Real-client repro of the earwiq 2026-08-10 report, driven by
// Run-HarvestSyncClientTest.ps1: the SERVER runs the reported config
// (HarvestTimeMult 0.00, HarvestAutoDrop false, EmptyCorpseAutoRemove true), the
// CLIENT profile is a fresh install with no config file at all - the friend. The
// server kills a pig as a player kill and marks it harvested (the knife hold's
// outcome); the client then right-clicks the corpse exactly the way the engine does
// (EntityBehaviorHarvestable.OnInteract) and the carcass loot window MUST open.
//
// On a build without the config sync the client's own default HarvestAutoDrop=true
// makes its window-suppression patch eat that right-click while the server (false)
// never spills the loot - the friend who cannot loot, on screen.
// Results in the CLIENT log: PASS/FAIL lines + "HARVSYNC COMPLETE total= pass= fail=".

using System;
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
    /// <summary>Server half: pig, player-kill, harvested - then the client looks.</summary>
    public class HarvestSyncServerDriver : ModSystem
    {
        private ICoreServerAPI _sapi = null!;
        private bool _fired;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            if (Environment.GetEnvironmentVariable("TASSHUNTING_HARVSYNC") != "1") return;
            _sapi = api;
            api.Event.PlayerNowPlaying += OnPlaying;
            api.Logger.Notification("[harvsync] server driver armed. cfg: mult={0} autodrop={1} emptyremove={2}",
                HuntingModSystem.Cfg.HarvestTimeMult, HuntingModSystem.Cfg.HarvestAutoDrop,
                HuntingModSystem.Cfg.EmptyCorpseAutoRemove);
        }

        private void OnPlaying(IServerPlayer player)
        {
            if (_fired) return;
            _fired = true;
            _sapi.Event.RegisterCallback(_ => Stage(player), 6000);
        }

        private void Stage(IServerPlayer player)
        {
            try
            {
                var pent = player?.Entity;
                if (pent == null) return;
                var type = _sapi.World.EntityTypes.FirstOrDefault(
                    t => t?.Code?.Path != null && t.Code.Path.StartsWith("pig-") && t.Code.Path.Contains("-adult-"));
                if (type == null) { _sapi.Logger.Error("[harvsync] no pig type"); return; }
                Entity pig = _sapi.World.ClassRegistry.CreateEntity(type);
                pig.ServerPos.SetPos(pent.Pos.X + 2, pent.Pos.Y + 1, pent.Pos.Z);
                pig.Pos.SetFrom(pig.ServerPos);
                _sapi.World.SpawnEntity(pig);

                _sapi.Event.RegisterCallback(_ =>
                {
                    // A PLAYER kill, so the empty-corpse pre-roll path runs exactly as in
                    // the report's world.
                    pig.ReceiveDamage(new DamageSource
                    {
                        Source = EnumDamageSource.Player,
                        SourceEntity = pent,
                        Type = EnumDamageType.SlashingAttack,
                        DamageTier = 5
                    }, 9999f);
                    _sapi.Event.RegisterCallback(_2 =>
                    {
                        var bh = pig.GetBehavior<EntityBehaviorHarvestable>();
                        if (bh == null) { _sapi.Logger.Error("[harvsync] pig has no harvestable behavior"); return; }
                        // The knife hold's outcome, server side (the hold itself is a
                        // client input this harness cannot press).
                        bh.SetHarvested(player);
                        _sapi.Logger.Notification("[harvsync] server: pig dead={0} harvested={1} lootslots={2}",
                            !pig.Alive, bh.IsHarvested, bh.Inventory?.Count ?? -1);
                    }, 1500);
                }, 1200);
            }
            catch (Exception e) { _sapi.Logger.Error("[harvsync] server driver failed: {0}", e); }
        }
    }

    /// <summary>Client half: synced values, then the right-click that must open the window.</summary>
    public class HarvestSyncClientHarnessSystem : ModSystem
    {
        private ICoreClientAPI _capi = null!;
        private string _shotDir = "";
        private int _total, _passed;
        private bool _started;
        private long _tickId;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

        public override void StartClientSide(ICoreClientAPI api)
        {
            if (Environment.GetEnvironmentVariable("TASSHUNTING_HARVSYNC") != "1") return;
            _capi = api;
            _shotDir = Environment.GetEnvironmentVariable("TASSHUNTING_HARVSYNC_SHOTS") ?? "";
            _tickId = api.Event.RegisterGameTickListener(Tick, 500, 0);
            api.Logger.Notification("[harvsync] client armed.");
        }

        private void Check(string name, bool ok)
        {
            _total++;
            if (ok) _passed++;
            _capi.Logger.Notification("[harvsync] {0} {1}", ok ? "PASS" : "FAIL", name);
        }

        private void Done()
        {
            _capi.Logger.Notification("[harvsync] HARVSYNC COMPLETE total={0} pass={1} fail={2}",
                _total, _passed, _total - _passed);
            try { if (_tickId != 0) _capi.Event.UnregisterGameTickListener(_tickId); } catch { }
            _tickId = 0;
        }

        private void Tick(float dt)
        {
            try
            {
                var ent = _capi.World?.Player?.Entity;
                if (ent == null || _started) return;
                CloseCharacterDialog();
                _started = true;
                // The server stages pig -> kill -> harvested over ~9s; look at 14s.
                _capi.Event.RegisterCallback(_ => Run(ent), 14000, true);
            }
            catch (Exception e)
            {
                _capi.Logger.Error("[harvsync] client tick failed: {0}", e);
                Check("no-exception", false);
                Done();
            }
        }

        private void Run(EntityPlayer playerEnt)
        {
            try
            {
                // THE SYNC: this fresh-install client must be playing by the server's
                // config, not its own defaults.
                Check("sync-autodrop-is-servers", HuntingModSystem.Cfg.HarvestAutoDrop == false);
                Check("sync-mult-zero-became-vanilla", Math.Abs(HuntingModSystem.Cfg.HarvestTimeMult - 1f) < 0.001f);
                _capi.Logger.Notification("[harvsync] client cfg: mult={0} autodrop={1}",
                    HuntingModSystem.Cfg.HarvestTimeMult, HuntingModSystem.Cfg.HarvestAutoDrop);

                var pig = _capi.World.GetEntitiesAround(playerEnt.Pos.XYZ, 12f, 12f,
                        e => e?.Code?.Path != null && e.Code.Path.StartsWith("pig-"))
                    .FirstOrDefault(e => !e.Alive);
                Check("corpse-found", pig != null);
                if (pig == null) { Done(); return; }
                var bh = pig.GetBehavior<EntityBehaviorHarvestable>();
                Check("corpse-harvested-synced", bh != null && bh.IsHarvested);
                if (bh == null) { Done(); return; }

                // THE RIGHT-CLICK, exactly as the engine delivers it. On the broken
                // build the client's own suppression patch eats this and no window
                // opens - the friend from the report.
                EnumHandling handled = EnumHandling.PassThrough;
                bh.OnInteract(playerEnt, playerEnt.RightHandItemSlot, Vec3d.Zero, EnumInteractMode.Interact, ref handled);

                _capi.Event.RegisterCallback(_ =>
                {
                    bool windowOpen = _capi.Gui?.OpenedGuis?.Any(
                        d => d != null && d.GetType().Name == "GuiDialogCreatureContents") == true;
                    Check("carcass-window-opens", windowOpen);
                    Shot("carcass-window");
                    _capi.Event.RegisterCallback(_2 => Done(), 1200, true);
                }, 800, true);
            }
            catch (Exception e)
            {
                _capi.Logger.Error("[harvsync] client checks failed: {0}", e);
                Check("client-no-exception", false);
                Done();
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

        /// <summary>One framebuffer PNG, taken on the next Done-stage frame.</summary>
        private void Shot(string name)
        {
            if (string.IsNullOrEmpty(_shotDir)) return;
            var grab = new ShotOnce(_capi, Path.Combine(_shotDir, name + ".png"),
                msg => _capi.Logger.Notification("[harvsync] {0}", msg));
            _capi.Event.RegisterRenderer(grab, EnumRenderStage.Done, "tasshuntingharvsyncshot" + name);
        }

        private class ShotOnce : IRenderer
        {
            private readonly ICoreClientAPI _capi;
            private readonly string _path;
            private readonly Action<string> _log;
            private bool _done;

            public ShotOnce(ICoreClientAPI capi, string path, Action<string> log)
            {
                _capi = capi; _path = path; _log = log;
            }

            public double RenderOrder => 1.0;
            public int RenderRange => 0;

            public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
            {
                if (_done) return;
                _done = true;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    using (var bmp = _capi.Render.GrabScreenshot(_capi.Render.FrameWidth, _capi.Render.FrameHeight, false, true))
                    {
                        bmp.Save(_path);
                    }
                    _log("screenshot saved: " + _path);
                }
                catch (Exception e) { _log("screenshot failed: " + e.Message); }
            }

            public void Dispose() { }
        }
    }
}
