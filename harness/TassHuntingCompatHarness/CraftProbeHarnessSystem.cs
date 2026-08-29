using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using TassHunting;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace TassHuntingCompatHarness
{
    /// <summary>
    /// CRAFT PROBE (TASSHUNTING_CRAFTPROBE=1): measures the exact hot loop behind the owner's
    /// crafting-grid lag spike (2026-08-28) - InventoryCraftingGrid.FindMatchingRecipe walks
    /// EVERY unique recipe-ingredient key per filled grid slot per grid change, calling
    /// SatisfiesAsIngredient on each. This reproduces that walk headlessly with a hammer stack
    /// (the owner's repro item) and a handful of controls, times it, and prints:
    ///  - total keys and total walk time per stack (the per-grid-change cost),
    ///  - the walk broken down by key domain (which MOD's keys cost what),
    ///  - the top individual slowest keys with their codes.
    /// Run the wrapper script twice - dino packs present vs absent - and the diff IS the
    /// mechanism, measured, no theories.
    /// </summary>
    public class CraftProbeHarnessSystem : ModSystem
    {
        private ICoreServerAPI _sapi = null!;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            if (Environment.GetEnvironmentVariable("TASSHUNTING_CRAFTPROBE") != "1") return;
            _sapi = api;
            api.Event.SaveGameLoaded += () => api.Event.RegisterCallback(_ => Run(), 3000);
            api.Logger.Notification("[craftprobe] armed.");
        }

        private ItemStack Stack(string code)
        {
            var item = _sapi.World.GetItem(new AssetLocation(code));
            if (item != null) return new ItemStack(item);
            var block = _sapi.World.GetBlock(new AssetLocation(code));
            return block != null ? new ItemStack(block) : null;
        }

        private void Run()
        {
            try
            {
                var byIngredient = _sapi.World.FastSearchRecipesByIngredient;
                _sapi.Logger.Notification("[craftprobe] unique ingredient keys: {0}", byIngredient.Count);

                foreach (string code in new[] { "hammer-copper", "ore-nativecopper-granite", "plank-oak", "stick" })
                {
                    var stack = Stack(code);
                    if (stack == null) { _sapi.Logger.Notification("[craftprobe] {0}: item not found, skipped", code); continue; }

                    // Warmup once (JIT), then measure the walk 50 times for a stable average -
                    // this is exactly the loop FindMatchingRecipe runs per filled slot.
                    Walk(byIngredient, stack, null, null);
                    var perDomain = new Dictionary<string, double>();
                    var perKey = new List<(double ms, string key)>();
                    var sw = Stopwatch.StartNew();
                    const int REPS = 50;
                    for (int r = 0; r < REPS; r++)
                    {
                        bool last = r == REPS - 1;
                        Walk(byIngredient, stack, last ? perDomain : null, last ? perKey : null);
                    }
                    sw.Stop();
                    double perCallMs = sw.Elapsed.TotalMilliseconds / REPS;
                    _sapi.Logger.Notification("[craftprobe] {0}: full walk = {1:0.00} ms per grid change per slot ({2} keys)",
                        code, perCallMs, byIngredient.Count);

                    foreach (var kv in perDomain.OrderByDescending(k => k.Value).Take(8))
                        _sapi.Logger.Notification("[craftprobe]    domain {0}: {1:0.000} ms", kv.Key, kv.Value);
                    foreach (var (ms, key) in perKey.OrderByDescending(p => p.ms).Take(8))
                        _sapi.Logger.Notification("[craftprobe]    slow key {0:0.000} ms: {1}", ms, key);
                }
                _sapi.Logger.Notification("[craftprobe] CRAFTPROBE COMPLETE");
            }
            catch (Exception e)
            {
                _sapi.Logger.Error("[craftprobe] EXCEPTION: {0}", e);
                _sapi.Logger.Notification("[craftprobe] CRAFTPROBE COMPLETE");
            }
        }

        private void Walk(
            System.Collections.Generic.OrderedDictionary<IRecipeIngredientBase, List<IRecipeBase>> byIngredient,
            ItemStack stack,
            Dictionary<string, double> perDomain, List<(double, string)> perKey)
        {
            var sw = new Stopwatch();
            foreach (var kv in byIngredient)
            {
                sw.Restart();
                kv.Key.SatisfiesAsIngredient(stack, false);
                sw.Stop();
                if (perDomain != null)
                {
                    string dom = "?";
                    if (kv.Key is FastSearchCraftingRecipeIngredient f && f.Code != null) dom = f.Code.Domain;
                    perDomain.TryGetValue(dom, out double cur);
                    perDomain[dom] = cur + sw.Elapsed.TotalMilliseconds;
                    perKey?.Add((sw.Elapsed.TotalMilliseconds,
                        (kv.Key as FastSearchCraftingRecipeIngredient)?.Code?.ToString() ?? kv.Key.GetType().Name));
                }
            }
        }
    }
}
