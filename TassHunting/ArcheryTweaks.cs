using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace TassHunting
{
    /// <summary>
    /// Arrow tuning at AssetsFinalize (absorbed from AccurateArchery 0.0.5,
    /// config-gated code since 0.3.0). Decompile-verified read site (1.22.3):
    /// arrows read the RESOLVED Attributes["breakChanceOnImpact"] (code
    /// fallback 0.5; vanilla ships a per-material byType map) at impact.
    /// Applied on BOTH sides, exactly like the JSON patches were.
    ///
    /// 0.6.1: the 0.3.0 absorption had flattened the per-material break list
    /// into a blanket zero (user: "Ai botched the task"). Restored as the
    /// config map ArrowBreakChanceByMaterial (user tier curve, halving from
    /// reed 32% down to steel 0%). Unlisted materials - modded arrows - are
    /// NOT touched: they keep their own mod's values.
    ///
    /// 0.6.2: bow accuracy modification REMOVED entirely (user 2026-07-21) -
    /// bows fall back to vanilla's rangedWeaponsAcc progression (crude -0.05,
    /// simple 0, long +0.2, recurve +0.3). True-aim spawn correction (the
    /// other AccurateArchery half) lives on in HuntingModSystem and is kept.
    /// </summary>
    internal static class ArcheryTweaks
    {
        public static void Apply(ICoreAPI api)
        {
            var cfg = HuntingModSystem.Cfg;
            if (!cfg.ArrowBreakTuningEnabled || cfg.ArrowBreakChanceByMaterial == null || cfg.ArrowBreakChanceByMaterial.Count == 0) return;

            int arrows = 0, skipped = 0;
            foreach (var item in api.World.Items)
            {
                string path = item?.Code?.Path;
                if (path == null || !path.StartsWith("arrow-")) continue;

                string material = path.Substring("arrow-".Length);
                if (!cfg.ArrowBreakChanceByMaterial.TryGetValue(material, out float chance))
                { skipped++; continue; } // unlisted (modded) material: leave it alone
                var tok = (item.Attributes?.Token as JObject) ?? new JObject();
                tok["breakChanceOnImpact"] = GameMath.Clamp(chance, 0f, 1f);
                item.Attributes = new JsonObject(tok);
                arrows++;
            }
            api.Logger.Event("[TassHunting] archery tweaks: {0} arrow types break-tuned per config curve, {1} unlisted arrow types untouched (bows vanilla).", arrows, skipped);
        }
    }
}
