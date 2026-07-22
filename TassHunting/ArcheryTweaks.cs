using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace TassHunting
{
    /// <summary>
    /// Per-material arrow break-chance tuning at AssetsFinalize (config-gated).
    /// Arrows read the resolved Attributes["breakChanceOnImpact"] (vanilla ships
    /// a per-material byType map, fallback 0.5) at impact; this writes the
    /// configured value per material on both sides. The tier curve halves from
    /// reed 32% down to steel 0%; unlisted materials (modded arrows) are left
    /// untouched. Bows are pure vanilla; the true-aim spawn correction lives in
    /// HuntingModSystem.
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
