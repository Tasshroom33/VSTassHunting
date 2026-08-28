using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Util;

namespace TassHunting
{
    /// <summary>
    /// FOOD CHAIN (owner order 2026-08-28: "make this a blood bath where everything fights
    /// everything, even your tamed creatures"). HuntAppend is a config map of wildcard HUNTER
    /// codes to extra PREY codes. At load, on every matched hunter type, the prey codes are
    /// appended to:
    ///  - every seekentity task with NO emotion gate (the proactive hunts - the anger-gated
    ///    retaliation lists stay exactly as their mod curated them), and
    ///  - every meleeattack task's whitelist (melee only ever swings at listed codes, so a
    ///    hunted target the melee cannot touch would be a chase with no bite).
    /// Codes already present are never duplicated. Targeting is by CODE, and tamed animals
    /// share their species' code, so "even your tamed creatures" comes free: a rex given
    /// tameddeer-* hunts your riding elk exactly like a wild one.
    /// </summary>
    public static class FoodChain
    {
        public static void Apply(ICoreAPI api)
        {
            var map = HuntingModSystem.Cfg?.HuntAppend;
            if (map == null || map.Count == 0) return;

            var matched = new Dictionary<string, int>();
            foreach (var key in map.Keys) matched[key] = 0;
            int types = 0, appended = 0;

            foreach (var et in api.World.EntityTypes)
            {
                string path = et?.Code?.Path;
                if (path == null || path.Contains("-baby")) continue;
                string full = et.Code.ToShortString();
                string[] prey = null; string hitKey = null;
                foreach (var kv in map)
                {
                    if (!string.IsNullOrEmpty(kv.Key)
                        && (WildcardUtil.Match(kv.Key, full) || WildcardUtil.Match(kv.Key, path)))
                    { prey = kv.Value; hitKey = kv.Key; break; }
                }
                if (prey == null || prey.Length == 0) { if (hitKey != null) matched[hitKey]++; continue; }

                var taskai = FindServerBehavior(et, "taskai");
                if (!(taskai?["aitasks"] is JArray tasks)) { matched[hitKey]++; continue; }

                // HUNGER GOVERNOR (0.14.26 field fix, "sniping everything"): a hunter's real
                // hunt task is the one gated whenNotInEmotionState "saturated" - eat, then
                // stop hunting. The first release appended prey to EVERY ungated seek, which
                // includes the packs' ungoverned player-seek, so the new menu rode a task
                // that never gets full. Now: if the type HAS saturation-gated hunts, prey
                // goes ONLY there; the ungated fallback exists for creatures with no hunger
                // model at all.
                bool hasGovernedHunt = false;
                foreach (var jt in tasks)
                {
                    var t0 = jt as JObject;
                    if (t0?["code"]?.ToString() != "seekentity") continue;
                    if (!string.IsNullOrEmpty(t0["whenInEmotionState"]?.ToString())) continue;
                    if ((t0["whenNotInEmotionState"]?.ToString() ?? "").Contains("saturated")) { hasGovernedHunt = true; break; }
                }

                bool touched = false;
                foreach (var jt in tasks)
                {
                    var task = jt as JObject;
                    if (task == null) continue;
                    string code = task["code"]?.ToString();
                    bool gated = !string.IsNullOrEmpty(task["whenInEmotionState"]?.ToString());
                    bool governed = (task["whenNotInEmotionState"]?.ToString() ?? "").Contains("saturated");
                    bool isHuntSeek = code == "seekentity" && !gated && (!hasGovernedHunt || governed);
                    bool isMelee = code == "meleeattack";
                    if (!isHuntSeek && !isMelee) continue;
                    if (!(task["entityCodes"] is JArray codes)) continue;

                    var have = new HashSet<string>();
                    foreach (var c in codes) if (c != null) have.Add(c.ToString());
                    foreach (var p in prey)
                    {
                        if (string.IsNullOrEmpty(p) || have.Contains(p)) continue;
                        codes.Add(p);
                        have.Add(p);
                        appended++;
                        touched = true;
                    }
                }
                matched[hitKey]++;
                if (touched) types++;
            }

            foreach (var kv in matched)
                if (kv.Value == 0)
                    api.Logger.Warning("[TassHunting] food chain: hunter pattern '{0}' matched no entity types.", kv.Key);
            if (types > 0)
                api.Logger.Event("[TassHunting] food chain: {0} hunter types widened, {1} prey entries appended across hunt and melee lists.",
                    types, appended);
        }

        private static JObject FindServerBehavior(Vintagestory.API.Common.Entities.EntityProperties et, string code)
        {
            var arr = et.Server?.BehaviorsAsJsonObj;
            if (arr == null) return null;
            foreach (var jo in arr)
            {
                var t = jo?.Token as JObject;
                if (t?["code"]?.ToString() == code) return t;
            }
            return null;
        }
    }
}
