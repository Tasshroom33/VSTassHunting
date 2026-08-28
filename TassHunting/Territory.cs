using System;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Util;

namespace TassHunting
{
    /// <summary>
    /// RETALIATION + TERRITORY (owner order 2026-08-28: "a triceratops holds ground around
    /// itself, only letting go if you truly are out of its range, and it starts a fight based
    /// on range/territory instead of allowing you to get next to it").
    ///
    /// Two config code lists, both wildcards, both empty by default:
    ///
    ///  RetaliationCodes - creatures that remember being hurt: their anger memory
    ///  (emotionstates aggressiveondamage duration), their anger-chase range and their chase
    ///  persistence (the aggressiveondamage-gated seekentity's seekingRange / maxFollowTime)
    ///  are raised to the config numbers. Values only ever go UP (Math.Max), like HardenApex.
    ///
    ///  TerritorialCodes - additionally start the fight themselves. Engine mechanism
    ///  (decompile-verified 1.22.5): EntityBehaviorEmotionStates ticks
    ///  TryTriggerState("aggressivearoundentities") every 0.33s and fires it when an entity in
    ///  the state's entityCodes list sits within its notifyRange (EXACT code equality, no
    ///  wildcards - "player" resolves to game:player and matches). The dino packs already gate
    ///  a player-targeting seekentity AND their melee on that state for parental defense, so
    ///  adding "player" to the state's own trigger list turns baby-defense into territory:
    ///  enter the radius, the state fires, the existing attack chain prosecutes it. While you
    ///  stay inside it re-triggers on expiry, so "letting go" only starts once you leave, and
    ///  then takes the full memory to cool down.
    ///
    /// Herd note: territory only functions while the world's creatureHostility setting is
    /// "aggressive" (the vanilla default) - the engine skips the aroundentities tick entirely
    /// on passive/off worlds, which is the correct order of authority.
    /// </summary>
    public static class Territory
    {
        public static void Apply(ICoreAPI api)
        {
            var cfg = HuntingModSystem.Cfg;
            if (cfg == null) return;
            string[] ret = cfg.RetaliationCodes ?? new string[0];
            string[] terr = cfg.TerritorialCodes ?? new string[0];
            if (ret.Length == 0 && terr.Length == 0) return;

            int retTypes = 0, terrTypes = 0, noChain = 0;
            foreach (var et in api.World.EntityTypes)
            {
                string path = et?.Code?.Path;
                if (path == null || path.Contains("-baby")) continue;
                string full = et.Code.ToShortString();
                bool isTerr = Matches(terr, full, path);
                bool isRet = isTerr || Matches(ret, full, path);
                if (!isRet) continue;

                var emo = FindServerBehavior(et, "emotionstates");
                var taskai = FindServerBehavior(et, "taskai");
                bool touched = false;

                if (emo?["states"] is JArray states)
                {
                    JObject around = null;
                    foreach (var st in states)
                    {
                        var s = st as JObject;
                        string code = s?["code"]?.ToString();
                        if (code == "aggressiveondamage")
                        {
                            s["duration"] = Math.Max(F(s["duration"], 0), cfg.RetaliationMemorySeconds);
                            touched = true;
                        }
                        if (code == "aggressivearoundentities" && around == null) around = s;
                    }
                    if (isTerr)
                    {
                        if (around == null)
                        {
                            // No parental-defense state to extend: give it a fresh one. Slot 2 /
                            // priority 5 mirror the packs' own entries; chance 1 = always guards.
                            around = new JObject
                            {
                                ["code"] = "aggressivearoundentities",
                                ["duration"] = cfg.RetaliationMemorySeconds,
                                ["chance"] = 1f,
                                ["slot"] = 2,
                                ["priority"] = 5f,
                                ["entityCodes"] = new JArray()
                            };
                            states.Add(around);
                        }
                        var codes = around["entityCodes"] as JArray ?? new JArray();
                        bool hasPlayer = false;
                        foreach (var c in codes) if (c?.ToString() == "player") hasPlayer = true;
                        if (!hasPlayer) codes.Add("player");
                        around["entityCodes"] = codes;
                        around["notifyRange"] = Math.Max(F(around["notifyRange"], 12f), cfg.TerritoryRadius);
                        around["duration"] = Math.Max(F(around["duration"], 0), cfg.RetaliationMemorySeconds);
                        touched = true;
                    }
                }

                bool hasChain = false;
                if (taskai?["aitasks"] is JArray tasks)
                {
                    foreach (var jt in tasks)
                    {
                        var task = jt as JObject;
                        if (task == null) continue;
                        string code = task["code"]?.ToString();
                        string gate = task["whenInEmotionState"]?.ToString() ?? "";
                        bool angerGate = gate.Contains("aggressiveondamage") || gate.Contains("aggressivearoundentities");
                        if (code == "seekentity" && angerGate && ContainsPlayer(task))
                        {
                            task["seekingRange"] = Math.Max(F(task["seekingRange"], 0), cfg.RetaliationSeekRange);
                            task["maxFollowTime"] = Math.Max(F(task["maxFollowTime"], 0), cfg.RetaliationMaxFollowTimeSec);
                            if (gate.Contains("aggressivearoundentities")) hasChain = true;
                            touched = true;
                        }
                        // A melee gated only on being-hurt would seek but never swing while
                        // territorial - widen its gate so the guard actually fights.
                        if (isTerr && code == "meleeattack"
                            && gate.Contains("aggressiveondamage") && !gate.Contains("aggressivearoundentities"))
                        {
                            task["whenInEmotionState"] = gate + "|aggressivearoundentities";
                            touched = true;
                        }
                    }
                }

                if (touched)
                {
                    if (isTerr) { terrTypes++; if (!hasChain) noChain++; }
                    else retTypes++;
                }
            }

            if (retTypes + terrTypes == 0)
            {
                api.Logger.Warning("[TassHunting] territory: no entity types matched the Retaliation/Territorial code lists - nothing was changed.");
                return;
            }
            api.Logger.Event("[TassHunting] territory: {0} territorial types (guard radius {1}, memory {2}s), {3} retaliation-only types (chase {4} blocks / {5}s).",
                terrTypes, cfg.TerritoryRadius, cfg.RetaliationMemorySeconds, retTypes, cfg.RetaliationSeekRange, cfg.RetaliationMaxFollowTimeSec);
            if (noChain > 0)
                api.Logger.Warning("[TassHunting] territory: {0} territorial types have no player-seek gated on aggressivearoundentities - they will only defend at melee reach.", noChain);
        }

        private static bool Matches(string[] pats, string full, string path)
        {
            if (pats == null) return false;
            foreach (var p in pats)
                if (!string.IsNullOrEmpty(p) && (WildcardUtil.Match(p, full) || WildcardUtil.Match(p, path))) return true;
            return false;
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

        private static bool ContainsPlayer(JObject task)
        {
            if (task["entityCodes"] is JArray codes)
                foreach (var c in codes) if (c?.ToString() == "player") return true;
            return false;
        }

        private static float F(JToken t, float def)
        {
            if (t == null) return def;
            try { return t.Value<float>(); } catch { return def; }
        }
    }
}
