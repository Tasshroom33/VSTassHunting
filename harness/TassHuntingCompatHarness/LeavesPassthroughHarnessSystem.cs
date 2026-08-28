using System;
using System.Collections.Generic;
using System.Linq;
using TassHunting;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace TassHuntingCompatHarness
{
    /// <summary>
    /// LEAVES PASSTHROUGH (TASSHUNTING_LEAVESPASS=off|on). Proves the collision switch in
    /// BOTH directions: the script boots the server twice, once with the switch off (branchy
    /// leaves must still be solid - the negative control that makes the on-run mean anything)
    /// and once on (they must all be open). The on-run additionally round-trips the switch
    /// live in the same world - restore, verify the full cube came back, reopen - because the
    /// restore path is what a config-sync packet exercises on clients and nothing else here
    /// would run it.
    ///
    /// Three levels, mirroring the stay-wild harness:
    ///  - the TYPE: Block.CollisionBoxes on every leavesbranchy* block, which is what Sync edits;
    ///  - a PLACED block: Block.GetCollisionBoxes(accessor, pos) on a real world position -
    ///    the call the physics and pathfinding consumers actually make;
    ///  - CONTROLS: normal leaves stay passthrough (vanilla baseline), logs and soil stay
    ///    solid (no collateral), and branchy keeps its SELECTION boxes (still choppable).
    /// </summary>
    public class LeavesPassthroughHarnessSystem : ModSystem
    {
        private ICoreServerAPI _sapi = null!;
        private int _total, _passed;
        private bool _expectOpen;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            string mode = Environment.GetEnvironmentVariable("TASSHUNTING_LEAVESPASS");
            if (mode != "off" && mode != "on") return;
            _expectOpen = mode == "on";
            _sapi = api;
            api.Event.SaveGameLoaded += () => api.Event.RegisterCallback(_ => Run(), 3000);
            api.Logger.Notification("[leavespass] armed, expecting branchy leaves {0}.",
                _expectOpen ? "OPEN" : "SOLID");
        }

        private void Check(string name, bool ok)
        {
            _total++;
            if (ok) _passed++;
            _sapi.Logger.Notification("[leavespass] {0} {1}", ok ? "PASS" : "FAIL", name);
        }

        private static bool HasBoxes(Cuboidf[] boxes) => boxes != null && boxes.Length > 0;

        private void Run()
        {
            try
            {
                var cfg = HuntingModSystem.Cfg;
                _sapi.Logger.Notification("[leavespass] config: enabled={0} codes=[{1}]",
                    cfg.LeavesPassthroughEnabled, string.Join(" ", cfg.LeavesPassthroughCodes ?? new string[0]));
                Check("config-matches-run-mode", cfg.LeavesPassthroughEnabled == _expectOpen);

                // ---- the blocks the config names ----
                var branchy = _sapi.World.Blocks
                    .Where(b => b?.Code?.Path != null && b.Id != 0
                             && b.Code.Domain == "game"
                             && b.Code.Path.StartsWith("leavesbranchy"))
                    .ToList();
                Check("branchy-blocks-loaded", branchy.Count > 0);
                if (branchy.Count == 0) { Done(); return; }
                _sapi.Logger.Notification("[leavespass] {0} leavesbranchy* blocks found", branchy.Count);

                // TYPE LEVEL: every branchy block's collision matches the run mode.
                CheckAllBranchy(branchy, _expectOpen,
                    _expectOpen ? "type-branchy-open-when-on" : "type-branchy-solid-when-off");

                // Selection boxes never change - the tree is still choppable either way.
                Check("branchy-still-selectable",
                    branchy.All(b => b.SelectionBoxes != null && b.SelectionBoxes.Length > 0));

                // PLACED: the consumer-level call, at a real position. Use a placed (non-decaying)
                // variant so a decay tick cannot race the test.
                var subject = branchy.FirstOrDefault(b => b.Code.Path.Contains("-placed"))
                           ?? branchy[0];
                var spawn = _sapi.World.DefaultSpawnPosition;
                var pos = new BlockPos((int)spawn.X + 5, (int)spawn.Y + 6, (int)spawn.Z + 5);
                var ba = _sapi.World.BlockAccessor;
                ba.SetBlock(subject.BlockId, pos);
                var placedBoxes = ba.GetBlock(pos).GetCollisionBoxes(ba, pos);
                _sapi.Logger.Notification("[leavespass] placed {0} at {1}: collision boxes = {2}",
                    subject.Code.Path, pos, placedBoxes == null ? 0 : placedBoxes.Length);
                Check(_expectOpen ? "placed-branchy-open-when-on" : "placed-branchy-collides-when-off",
                    HasBoxes(placedBoxes) != _expectOpen);
                ba.SetBlock(0, pos);

                // CONTROLS - no collateral, and the baseline the feature copies.
                var normalLeaf = _sapi.World.Blocks.FirstOrDefault(b => b?.Code?.Path != null
                    && b.Code.Domain == "game" && b.Code.Path.StartsWith("leaves-grown"));
                if (normalLeaf == null) _sapi.Logger.Notification("[leavespass] no leaves-grown block - baseline control skipped");
                else Check("control-normal-leaves-passthrough", !HasBoxes(normalLeaf.CollisionBoxes));

                var log = _sapi.World.Blocks.FirstOrDefault(b => b?.Code?.Path != null
                    && b.Code.Domain == "game" && b.Code.Path.StartsWith("log-grown") && HasBoxes(b.CollisionBoxes));
                Check("control-logs-still-solid", log != null);

                var soil = _sapi.World.Blocks.FirstOrDefault(b => b?.Code?.Path != null
                    && b.Code.Domain == "game" && b.Code.Path.StartsWith("soil-") && HasBoxes(b.CollisionBoxes));
                Check("control-soil-still-solid", soil != null);

                // LIVE ROUND TRIP (on-run only): the restore path is what a config-sync packet
                // runs on a client whose local file disagreed with the server. Flip the live
                // config off, Sync, expect the full cube back on every branchy block; flip on,
                // Sync, expect them open again.
                if (_expectOpen)
                {
                    cfg.LeavesPassthroughEnabled = false;
                    LeavesPassthrough.Sync(_sapi);
                    CheckAllBranchy(branchy, false, "live-restore-returns-solid");
                    Check("live-restore-full-cube",
                        branchy.All(b => HasBoxes(b.CollisionBoxes) && b.CollisionBoxes.Max(c => c.Y2) >= 0.99f));

                    cfg.LeavesPassthroughEnabled = true;
                    LeavesPassthrough.Sync(_sapi);
                    CheckAllBranchy(branchy, true, "live-reopen-after-restore");
                }

                Done();
            }
            catch (Exception e)
            {
                _sapi.Logger.Error("[leavespass] EXCEPTION: {0}", e);
                Done();
            }
        }

        private void CheckAllBranchy(List<Block> branchy, bool expectOpen, string name)
        {
            var offenders = branchy.Where(b => HasBoxes(b.CollisionBoxes) == expectOpen).ToList();
            if (offenders.Count > 0)
                _sapi.Logger.Notification("[leavespass] {0} offenders (e.g. {1})",
                    offenders.Count, string.Join(", ", offenders.Take(6).Select(b => b.Code.Path)));
            Check(name, offenders.Count == 0);
        }

        private void Done() =>
            _sapi.Logger.Notification("[leavespass] LEAVESPASS COMPLETE total={0} pass={1} fail={2}",
                _total, _passed, _total - _passed);
    }
}
