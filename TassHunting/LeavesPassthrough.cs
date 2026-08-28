using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace TassHunting
{
    /// <summary>
    /// LEAVES PASSTHROUGH (owner order 2026-08-28). Big creatures get cheesed at trees:
    /// their collision boxes snag on the branchy leaf clumps around trunks, and a player can
    /// stand inside or on top of that canopy plinking arrows at something that cannot reach
    /// them. Vanilla ships four of its six leaf types with "collisionbox: null" already -
    /// only leavesbranchy and leavesbranchystatic are solid. This drops the named leaf
    /// blocks' collision to that same null, so every leaf block is walk-through: the
    /// pathfinder (which reads collision boxes live) stops treating canopy as walls, and a
    /// leaf clump is no longer a place a player can stand.
    ///
    /// ONLY CollisionBoxes is touched. Selection boxes (chopping), decay, tree-felling
    /// group codes, drops and render state all stay exactly as loaded, which keeps the
    /// mutated surface as small as it can be. A null collision-box array is a first-class
    /// block state the engine runs on vanilla's own normal leaves every frame - this is not
    /// the never-null-a-collection trap, which is about item/entity collections whose
    /// consumers assume non-null.
    ///
    /// BOTH SIDES: the server's copy drives creature physics and pathfinding (the point of
    /// the feature), the client's copy drives the player's own movement. Each side holds
    /// its own Block objects, so Sync runs per side at AssetsFinalize; on a remote client
    /// the config-sync packet re-runs it so the SERVER's setting rules even when the
    /// client's local file disagrees (Sync is idempotent and reversible either way).
    /// A client without the mod at all keeps solid branchy leaves locally - it can still
    /// stand on canopy while server-side creatures walk through it; mismatched, never
    /// fatal (no serialized types, no coded classes are involved).
    ///
    /// HARD GUARD (sound-arc lesson 2026-08-28: only touch what the list can defend):
    /// whatever the config wildcards catch, only blocks whose material is Leaves ever lose
    /// collision - a fat-fingered "*" can never open a hedge, wall or fence.
    /// </summary>
    public static class LeavesPassthrough
    {
        // Original collision boxes per side, so the switch works in BOTH directions at
        // runtime (config sync arriving after AssetsFinalize, panel toggles). Keyed on the
        // live Block objects; Reset() drops stale entries from a previous world session.
        private static readonly Dictionary<Block, Cuboidf[]> savedServer = new Dictionary<Block, Cuboidf[]>();
        private static readonly Dictionary<Block, Cuboidf[]> savedClient = new Dictionary<Block, Cuboidf[]>();

        private static Dictionary<Block, Cuboidf[]> Store(ICoreAPI api) =>
            api.Side == EnumAppSide.Server ? savedServer : savedClient;

        /// <summary>Forget the previous world's blocks. Call once per side at AssetsFinalize,
        /// before the first Sync of the session.</summary>
        public static void Reset(ICoreAPI api) => Store(api).Clear();

        /// <summary>Move this side's world to the configured state - opens collision when the
        /// switch is on, restores the saved boxes when it is off. Safe to call repeatedly.</summary>
        public static void Sync(ICoreAPI api)
        {
            var cfg = HuntingModSystem.Cfg;
            var store = Store(api);

            if (cfg == null || !cfg.LeavesPassthroughEnabled)
            {
                if (store.Count == 0) return;
                foreach (var kv in store) kv.Key.CollisionBoxes = kv.Value;
                api.Logger.Event("[TassHunting] leaves passthrough off ({0}): collision restored on {1} leaf blocks.",
                    api.Side, store.Count);
                store.Clear();
                return;
            }

            string[] codes = cfg.LeavesPassthroughCodes;
            if (codes == null || codes.Length == 0)
            {
                api.Logger.Warning("[TassHunting] leaves passthrough is on but LeavesPassthroughCodes is empty - nothing was changed.");
                return;
            }

            int matched = 0, opened = 0;
            var refusedNonLeaf = new List<string>();
            foreach (var b in api.World.Blocks)
            {
                if (b?.Code == null || b.Id == 0) continue;
                // Full code and bare path both match, so "game:leavesbranchy*" and
                // "leavesbranchy*" are equally valid ways to name a block (same rule
                // as stay-wild).
                if (!WildcardUtil.Match(codes, b.Code.ToShortString())
                    && !WildcardUtil.Match(codes, b.Code.Path)) continue;
                matched++;
                if (b.BlockMaterial != EnumBlockMaterial.Leaves)
                {
                    if (refusedNonLeaf.Count < 6) refusedNonLeaf.Add(b.Code.ToShortString());
                    continue;
                }
                var boxes = b.CollisionBoxes;
                if (boxes == null || boxes.Length == 0) continue; // already passthrough
                if (!store.ContainsKey(b)) store[b] = boxes;
                b.CollisionBoxes = null; // vanilla's own passthrough-leaves shape
                opened++;
            }

            // DIAGNOSTICS LAW: a list that names nothing must say so, not quietly do nothing.
            if (matched == 0)
            {
                api.Logger.Warning("[TassHunting] leaves passthrough: no blocks matched {0} - nothing was changed. Check LeavesPassthroughCodes.",
                    string.Join(", ", codes));
                return;
            }
            api.Logger.Event("[TassHunting] leaves passthrough on ({0}): {1} blocks matched, {2} newly opened, {3} held open total.",
                api.Side, matched, opened, store.Count);
            if (refusedNonLeaf.Count > 0)
                api.Logger.Event("[TassHunting] leaves passthrough: refused {0} matched non-leaf blocks (e.g. {1}) - only leaf material ever opens.",
                    refusedNonLeaf.Count, string.Join(", ", refusedNonLeaf));

            // VISIBLE TRUTH: leaf blocks from other mods that are still solid are exactly
            // what the owner would widen the list with - surface them instead of leaving
            // them to be rediscovered as "my dino snagged on a modded tree".
            int outsideSolid = 0; string example = null;
            foreach (var b in api.World.Blocks)
            {
                if (b?.Code == null || b.Id == 0 || b.BlockMaterial != EnumBlockMaterial.Leaves) continue;
                var boxes = b.CollisionBoxes;
                if (boxes == null || boxes.Length == 0) continue;
                outsideSolid++;
                if (example == null) example = b.Code.ToShortString();
            }
            if (outsideSolid > 0)
                api.Logger.Event("[TassHunting] leaves passthrough: {0} leaf blocks outside the list still have collision (e.g. {1}) - add them to LeavesPassthroughCodes if they should open too.",
                    outsideSolid, example);
        }
    }
}
