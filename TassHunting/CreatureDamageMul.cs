using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Util;

namespace TassHunting
{
    /// <summary>
    /// CREATURE MELEE DAMAGE (owner order 2026-08-28, the dino damage-law audit): a config map
    /// of wildcard entity codes to multipliers on their melee bite. Built for law-breakers -
    /// the 177-species survey showed the dino roster follows bite = 0.46*hp^0.70 (R2 0.78)
    /// with two species biting at exactly double their own law - but the mechanism is a RULE:
    /// the species list lives in the config file, never in code, so the next overtuned modded
    /// creature is one config line.
    ///
    /// Applied at AssetsFinalize, server side (meleeattack is a server behavior and damage is
    /// server-authoritative). byType values are already resolved per concrete type by the
    /// asset loader at this point (same engine fact PredatorAI relies on), so each type's
    /// meleeattack tasks hold plain damage numbers - every one of them is scaled, since a
    /// species can carry several melee tasks. First matching map entry wins, like
    /// the other wildcard maps in this mod.
    /// </summary>
    public static class CreatureDamageMul
    {
        public static void Apply(ICoreAPI api)
        {
            var cfg = HuntingModSystem.Cfg;
            var map = cfg?.CreatureMeleeDamageMul;
            if (map == null || map.Count == 0) return;

            var matched = new Dictionary<string, int>();
            foreach (var key in map.Keys) matched[key] = 0;
            int types = 0, tasks = 0;

            foreach (var et in api.World.EntityTypes)
            {
                if (et?.Code == null) continue;
                string full = et.Code.ToShortString(), path = et.Code.Path;
                string hit = null;
                foreach (var kv in map)
                {
                    if (WildcardUtil.Match(kv.Key, full) || WildcardUtil.Match(kv.Key, path)) { hit = kv.Key; break; }
                }
                if (hit == null) continue;
                float mult = map[hit];
                if (mult < 0f || Math.Abs(mult - 1f) < 0.0001f) { matched[hit]++; continue; }

                var arr = et.Server?.BehaviorsAsJsonObj;
                if (arr == null) continue;
                bool touched = false;
                foreach (var jo in arr)
                {
                    if (!((jo?.Token as JObject)?["code"]?.ToString() == "taskai")) continue;
                    if (!((jo.Token as JObject)?["aitasks"] is JArray aitasks)) continue;
                    foreach (var jt in aitasks)
                    {
                        if (!(jt is JObject task) || task["code"]?.ToString() != "meleeattack") continue;
                        if (task["damage"] == null) continue;
                        float dmg;
                        try { dmg = task["damage"].Value<float>(); } catch { continue; }
                        task["damage"] = dmg * mult;
                        tasks++; touched = true;
                    }
                }
                matched[hit]++;
                if (touched) types++;
            }

            // DIAGNOSTICS LAW: a pattern that matched nothing (typo, or the creature mod was
            // removed) says so in the log instead of silently tuning nobody.
            foreach (var kv in matched)
                if (kv.Value == 0)
                    api.Logger.Warning("[TassHunting] creature damage: pattern '{0}' matched no entity types - nothing scaled by it.", kv.Key);
            if (types > 0)
                api.Logger.Event("[TassHunting] creature damage: {0} entity types rescaled ({1} melee tasks) from {2} config patterns.",
                    types, tasks, map.Count);
        }
    }
}
