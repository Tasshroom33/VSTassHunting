using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace TassHunting
{
    /// <summary>
    /// Absorbed AccurateArchery (previously two unconditional asset patches from
    /// 0.0.5): now runtime AssetsFinalize mutations so they are CONFIG-GATED
    /// (naming/absorb standardization 2026-07-19). Decompile-verified read sites
    /// (1.22.3): bows read the RESOLVED Attributes["statModifier"]
    /// ["rangedWeaponsAcc"] while aiming; arrows read
    /// Attributes["breakChanceOnImpact"] (default 0.5) at impact. Applied on
    /// BOTH sides, exactly like the JSON patches were.
    /// </summary>
    internal static class ArcheryTweaks
    {
        public static void Apply(ICoreAPI api)
        {
            var cfg = HuntingModSystem.Cfg;
            if (!cfg.BowAccuracyEnabled && !cfg.UnbreakableArrowsEnabled) return;

            int bows = 0, arrows = 0;
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
                else if (cfg.UnbreakableArrowsEnabled && path.StartsWith("arrow-"))
                {
                    var tok = (item.Attributes?.Token as JObject) ?? new JObject();
                    tok["breakChanceOnImpact"] = 0.0;
                    item.Attributes = new JsonObject(tok);
                    arrows++;
                }
            }
            api.Logger.Event("[TassHunting] archery tweaks: {0} bows at accuracy 1.0, {1} arrow types unbreakable.", bows, arrows);
        }
    }
}
