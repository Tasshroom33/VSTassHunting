using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.Client;

namespace TassHuntingCompatHarness
{
    /// <summary>
    /// CRAFT SPIKE CAPTURE, server half (TASSHUNTING_CRAFTSPIKE=1). v3: the server only skips
    /// the character screen and puts a hammer in hotbar slot 0; the CLIENT half now performs
    /// the actual clicks, because v1 (server-pushed grid writes) and v2 (server-pushed held-slot
    /// toggles) both came back silent - every inventory-sync path is measured cheap, so the cost
    /// the owner feels must live in the client-side click machinery itself.
    /// </summary>
    public class CraftSpikeServerDriver : ModSystem
    {
        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            if (Environment.GetEnvironmentVariable("TASSHUNTING_CRAFTSPIKE") != "1") return;
            api.Event.PlayerNowPlaying += plr =>
            {
                try { plr.Entity.WatchedAttributes.SetBool("createCharacter", true); } catch { }
                try
                {
                    var hotbar = plr.InventoryManager.GetOwnInventory("hotbar");
                    var hammer = api.World.GetItem(new AssetLocation("hammer-copper"));
                    if (hotbar != null && hammer != null && hotbar[0].Itemstack == null)
                    {
                        hotbar[0].Itemstack = new ItemStack(hammer);
                        hotbar[0].MarkDirty();
                        api.Logger.Notification("[craftspike] hammer granted to hotbar slot 0");
                    }
                }
                catch (Exception e) { api.Logger.Error("[craftspike] grant failed: {0}", e); }
            };
            api.Logger.Notification("[craftspike] server driver armed (v3, client-clicks).");
        }
    }

    /// <summary>
    /// Client half, v3: arms the engine slow-tick profiler, then replicates the engine's own
    /// GuiElementItemSlotGridBase.SlotClick idiom - mouse-slot ActivateSlot plus packet send -
    /// with the survival inventory dialog genuinely open. Every 2s one click: pick the hammer
    /// up off hotbar slot 0, drop it into crafting grid slot 0, pick it back up, put it back.
    /// That is a manual grid insert minus only the OS mouse event. Per-click timestamped log
    /// lines line up against any "A tick took" breakdowns the profiler prints.
    /// </summary>
    public class CraftSpikeClientHarnessSystem : ModSystem
    {
        private ICoreClientAPI _capi = null!;
        private int _clicks;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

        public override void StartClientSide(ICoreClientAPI api)
        {
            if (Environment.GetEnvironmentVariable("TASSHUNTING_CRAFTSPIKE") != "1") return;
            _capi = api;
            api.Event.LevelFinalize += () =>
            {
                try
                {
                    ScreenManager.FrameProfiler.PrintSlowTicks = true;
                    ScreenManager.FrameProfiler.Enabled = true;
                    ScreenManager.FrameProfiler.PrintSlowTicksThreshold = 40;
                    ScreenManager.FrameProfiler.Begin(null);
                    api.Logger.Notification("[craftspike] client profiler armed at 40ms threshold");
                }
                catch (Exception e) { api.Logger.Error("[craftspike] profiler arm failed: {0}", e); }
                api.Event.RegisterCallback(_ => Begin(), 8000);
            };
        }

        private void Begin()
        {
            try
            {
                // Snapshot: TryClose/TryOpen mutate the engine's dialog bookkeeping mid-walk.
                var guis = new System.Collections.Generic.List<Vintagestory.API.Client.GuiDialog>(_capi.Gui.LoadedGuis);
                foreach (var gui in guis)
                {
                    string n = gui.GetType().Name;
                    if (n == "GuiDialogCreateCharacter" && gui.IsOpened()) { gui.TryClose(); _capi.Logger.Notification("[craftspike] closed character dialog"); }
                }
                bool dialogOpened = false;
                foreach (var gui in guis)
                {
                    if (gui.GetType().Name == "GuiDialogInventory")
                    {
                        dialogOpened = gui.TryOpen();
                        _capi.Logger.Notification("[craftspike] inventory dialog TryOpen => {0}", dialogOpened);
                        break;
                    }
                }
                if (!dialogOpened)
                {
                    // Fallback: open the inventories by packet the way the dialog itself does.
                    foreach (string invName in new[] { "craftinggrid", "backpack" })
                    {
                        var inv = _capi.World.Player.InventoryManager.GetOwnInventory(invName);
                        var pkt = inv?.Open(_capi.World.Player);
                        if (pkt != null) _capi.Network.SendPacketClient(pkt);
                    }
                    _capi.Logger.Notification("[craftspike] opened inventories by packet (no dialog)");
                }
                _capi.Event.RegisterCallback(_ => Step(), 2000);
            }
            catch (Exception e) { _capi.Logger.Error("[craftspike] begin failed: {0}", e); }
        }

        private void Step()
        {
            try
            {
                var im = _capi.World.Player.InventoryManager;
                var hotbar = im.GetOwnInventory("hotbar");
                var grid = im.GetOwnInventory("craftinggrid");
                if (hotbar == null || grid == null) { _capi.Logger.Notification("[craftspike] missing inventories"); return; }

                int phase = _clicks % 4;
                var (inv, slotId, what) = phase switch
                {
                    0 => (hotbar, 0, "pickup from hotbar"),
                    1 => (grid, 0, "drop into grid"),
                    2 => (grid, 0, "pickup from grid"),
                    _ => (hotbar, 0, "drop back to hotbar"),
                };
                SpikeCounters.Reset();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Click(inv, slotId);
                sw.Stop();
                _clicks++;
                _capi.Logger.Notification("[craftspike] click {0} ({1}): activate={2:0.0}ms | {3}",
                    _clicks, what, sw.Elapsed.TotalMilliseconds, SpikeCounters.Dump());
                SpikeCounters.Reset();
                _capi.Event.RegisterCallback(_ => _capi.Logger.Notification("[craftspike] packet-half after click {0}: {1}",
                    _clicks, SpikeCounters.Dump()), 1500);
                if (_clicks < 48) _capi.Event.RegisterCallback(_ => Step(), 2000);
                else _capi.Logger.Notification("[craftspike] CRAFTSPIKE CLIENT DONE");
            }
            catch (Exception e) { _capi.Logger.Error("[craftspike] click failed: {0}", e); }
        }

        // The exact GuiElementItemSlotGridBase.SlotClick idiom for an unmodified left click.
        private void Click(IInventory inv, int slotId)
        {
            var mouseInv = _capi.World.Player.InventoryManager.GetOwnInventory("mouse");
            var op = new ItemStackMoveOperation(_capi.World, EnumMouseButton.Left, 0, EnumMergePriority.AutoMerge);
            op.ActingPlayer = _capi.World.Player;
            op.CurrentPriority = EnumMergePriority.DirectMerge;
            object pkt = inv.ActivateSlot(slotId, mouseInv[0], ref op);
            if (pkt is object[] arr) { foreach (var p in arr) _capi.Network.SendPacketClient(p); }
            else if (pkt != null) _capi.Network.SendPacketClient(pkt);
        }
    }
}
