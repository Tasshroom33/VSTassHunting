using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace TassHunting
{
    /// <summary>
    /// Absorbed AccurateArchery (previously two unconditional asset patches from
    /// 0.0.5): runtime AssetsFinalize mutations so they are CONFIG-GATED.
    /// Decompile-verified read sites (1.22.3): bows read the RESOLVED
    /// Attributes["statModifier"]["rangedWeaponsAcc"] while aiming; arrows read
    /// Attributes["breakChanceOnImpact"] (code fallback 0.5; vanilla ships a
    /// per-material byType map) at impact. Applied on BOTH sides, exactly like
    /// the JSON patches were.
    ///
    /// 0.6.1: the 0.3.0 absorption had flattened the per-material break list
    /// into a blanket zero (user: "Ai botched the task"). Restored as the
    /// config map ArrowBreakChanceByMaterial (user tier curve, halving from
    /// reed 32% down to steel 0%). Unlisted materials - modded arrows - are
    /// NOT touched: they keep their own mod's values.
    /// </summary>
    internal static class ArcheryTweaks
    {
        public static void Apply(ICoreAPI api)
        {
            var cfg = HuntingModSystem.Cfg;
            bool arrowsOn = cfg.ArrowBreakTuningEnabled && cfg.ArrowBreakChanceByMaterial != null && cfg.ArrowBreakChanceByMaterial.Count > 0;
            if (!cfg.BowAccuracyEnabled && !arrowsOn) return;

            int bows = 0, arrows = 0, skipped = 0;
            foreach (var item in api.World.Items)
            {
                string path = item?.Code?.Path;
                if (path == null) continue;

                if (cfg.BowAccuracyEnabled && path.StartsWith("bow-"))
                {
                    var tok = (item.Attributes?.Token as JObject) ?? new JObject();
                    tok["statModifier"] = new JObject { ["rangedWeaponsAcc"] = 1.0 };
                    item.Attributes = new JsonObject(tok);
                    bows++;
                }
                else if (arrowsOn && path.StartsWith("arrow-"))
                {
                    string material = path.Substring("arrow-".Length);
                    if (!cfg.ArrowBreakChanceByMaterial.TryGetValue(material, out float chance))
                    { skipped++; continue; } // unlisted (modded) material: leave it alone
                    var tok = (item.Attributes?.Token as JObject) ?? new JObject();
                    tok["breakChanceOnImpact"] = GameMath.Clamp(chance, 0f, 1f);
                    item.Attributes = new JsonObject(tok);
                    arrows++;
                }
            }
            api.Logger.Event("[TassHunting] archery tweaks: {0} bows at accuracy 1.0, {1} arrow types break-tuned per config curve, {2} unlisted arrow types untouched.", bows, arrows, skipped);
        }
    }
}
