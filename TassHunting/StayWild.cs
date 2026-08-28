using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Util;

namespace TassHunting
{
    /// <summary>
    /// STAY WILD (owner order 2026-08-28: "i want dinos to just be wild animals you fear").
    /// Takes the domestication behaviors off whatever entity types the config names, so those
    /// creatures can never be tamed, petted, roped, owned or ridden - including later, if a
    /// companion mod that would have granted it (Jaunt, PetAI) is installed afterwards. The
    /// creature list and the behavior list are BOTH config, so this is a rule, not a species
    /// list welded into the mod.
    ///
    /// WHY THE ENTRY IS REMOVED AND NOT FLAGGED "enabled": false (decompile-verified, 1.22.5):
    /// the EntitySidedProperties CONSTRUCTOR is the only reader of that flag - it filters the
    /// disabled entries out of BehaviorsAsJsonObj once, when the type is built. loadBehaviors,
    /// which runs for every spawned entity afterwards, never looks at it again. Setting the
    /// flag at AssetsFinalize would be a silent no-op; the entry has to leave the array.
    ///
    /// BOTH SIDES: behaviors are per-side lists and a client builds its own copy, which is what
    /// drives mount rendering and rider controls. So whichever side this runs on, both arrays
    /// that side holds are filtered. A client WITHOUT this setting keeps its own client-side
    /// entry - it can then ask to mount and the server, which has no such behavior, simply does
    /// not answer. Mismatched, never fatal (entity behaviors degrade; see the workspace rules).
    /// </summary>
    public static class StayWild
    {
        public static void Apply(ICoreAPI api)
        {
            var cfg = HuntingModSystem.Cfg;
            if (cfg == null || !cfg.StayWildEnabled) return;

            string[] codes = cfg.StayWildCodes;
            string[] behaviorCodes = cfg.StayWildBehaviors;
            if (codes == null || codes.Length == 0) return;
            if (behaviorCodes == null || behaviorCodes.Length == 0) return;

            var strip = new HashSet<string>(behaviorCodes, StringComparer.OrdinalIgnoreCase);
            int typesMatched = 0, typesChanged = 0, removed = 0;

            foreach (var et in api.World.EntityTypes)
            {
                if (et?.Code == null) continue;
                // Match on the full code and on the bare path, so both "tyrannosauridae:*"
                // and "tyrannosauridae-*" are valid ways to name a family in the config.
                if (!WildcardUtil.Match(codes, et.Code.ToShortString())
                    && !WildcardUtil.Match(codes, et.Code.Path)) continue;
                typesMatched++;

                int n = Strip(et.Client, strip) + Strip(et.Server, strip);
                if (n > 0) { typesChanged++; removed += n; }
            }

            // DIAGNOSTICS LAW: a config naming creatures that do not exist (typo, or a dino
            // pack uninstalled) must say so in the log rather than quietly doing nothing.
            if (typesMatched == 0)
            {
                api.Logger.Warning("[TassHunting] stay-wild: no entity types matched {0} - nothing was changed. Check StayWildCodes.",
                    string.Join(", ", codes));
                return;
            }
            api.Logger.Event("[TassHunting] stay-wild ({0}): {1} entity types matched, {2} behaviors removed from {3} of them - they cannot be tamed, roped or ridden.",
                api.Side, typesMatched, removed, typesChanged);
        }

        /// <summary>Rebuild this side's behavior array without the stripped codes.
        /// Returns how many entries were dropped.</summary>
        private static int Strip(EntitySidedProperties sided, HashSet<string> strip)
        {
            var arr = sided?.BehaviorsAsJsonObj;
            if (arr == null || arr.Length == 0) return 0;

            var kept = new List<JsonObject>(arr.Length);
            int dropped = 0;
            foreach (var jo in arr)
            {
                string code = jo?["code"]?.AsString();
                if (code != null && strip.Contains(code)) { dropped++; continue; }
                kept.Add(jo);
            }
            if (dropped > 0) sided.BehaviorsAsJsonObj = kept.ToArray();
            return dropped;
        }
    }
}
