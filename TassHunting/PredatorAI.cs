using System;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace TassHunting
{
    /// <summary>
    /// PREDATOR OVERHAUL (user design 2026-07-18): "build them more like real ai."
    ///
    ///  APEX (black/brown/polar bear - things bigger than you):
    ///   - Always charge, never run: fleeondamage chance forced to 0.
    ///   - See you from a decent distance: unprovoked seek 16 -> 30 blocks,
    ///     retaliation seek 30 -> 40, chase timer 60s -> 240s.
    ///   - No napping in sight: idle tasks (Sleep/Sit/Lie) break at 30 blocks
    ///     instead of 10/5. The raised seek range also outranks the sleep task's
    ///     cancel priority (1.6 > 1.35, decompile-verified), so a sleeping bear
    ///     wakes up and CHARGES the moment you enter its seek range.
    ///   Panda/sun bears untouched - they are not "bigger than you".
    ///
    ///  PACK HUNTERS (wolf, hyena):
    ///   - Solo: hit and run. After landing a bite on a player, the wolf
    ///     disengages via the flee task's own InstaFleeFrom (9s / ~15 blocks,
    ///     decompile-verified), then circles back for another pass.
    ///   - Pack (another alive adult of the same species within PackRadius):
    ///     swarm. fleeondamage is suppressed entirely and the aggro chase runs
    ///     240s at 25 blocks - they do not stop, only flee if solo.
    ///   - No napping mid-hunt: wolf sleep also blocked during fleeondamage and
    ///     breaks at 20 blocks (vanilla: naps 12 blocks from an archer, wakes
    ///     only inside 10 - the playtested "sleeps until you shoot it again").
    ///
    /// All numbers rewritten IN CODE at AssetsFinalize (tasks matched by their
    /// semantics - code + emotion gate + animation - never by array index, so a
    /// vanilla reorder can't silently break us). Config-gated.
    /// </summary>
    public static class PredatorAI
    {
        internal static ICoreAPI Api;

        public static void ApplyServer(ICoreAPI api)
        {
            Api = api;
            var cfg = HuntingModSystem.Cfg;
            if (!cfg.PredatorOverhaulEnabled) return;

            int apex = 0, pack = 0;
            foreach (var et in api.World.EntityTypes)
            {
                string path = et.Code?.Path;
                if (path == null || path.Contains("-baby")) continue;

                if (MatchesAny(path, cfg.ApexCodes))
                {
                    if (HardenApex(et, cfg)) apex++;
                }
                else if (MatchesAny(path, cfg.PackCodes))
                {
                    if (AdjustPackHunter(et, cfg)) pack++;
                }
            }
            api.Logger.Event("[TassHunting] PredatorAI: hardened {0} apex types, adjusted {1} pack-hunter types.", apex, pack);
        }

        private static bool MatchesAny(string path, string[] prefixes)
        {
            foreach (var p in prefixes)
                if (path == p || path.StartsWith(p + "-") || (p.EndsWith("-") && path.StartsWith(p))) return true;
            return false;
        }

        // ---- json helpers (byType values are already resolved per concrete type
        // by the asset loader, so these trees hold plain values) ----

        private static JObject FindServerBehavior(EntityProperties et, string code)
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

        private static string S(JToken t) => t?.ToString() ?? "";

        private static bool HardenApex(EntityProperties et, HuntingConfig cfg)
        {
            bool touched = false;

            // never flee
            var emo = FindServerBehavior(et, "emotionstates");
            if (emo?["states"] is JArray states)
            {
                foreach (var st in states)
                    if (S(st["code"]) == "fleeondamage") { st["chance"] = 0f; touched = true; }
            }

            var taskai = FindServerBehavior(et, "taskai");
            if (taskai?["aitasks"] is JArray tasks)
            {
                foreach (var jt in tasks)
                {
                    var task = jt as JObject;
                    if (task == null) continue;
                    string code = S(task["code"]);

                    if (code == "seekentity" && ContainsPlayer(task))
                    {
                        bool retaliation = S(task["whenInEmotionState"]).Contains("aggressiveondamage");
                        float range = retaliation ? cfg.ApexAggroSeekRange : cfg.ApexSeekRange;
                        task["seekingRange"] = Math.Max(F(task["seekingRange"], 0), range);
                        if (task["belowTempSeekingRange"] != null)
                            task["belowTempSeekingRange"] = Math.Max(F(task["belowTempSeekingRange"], 0), range + 4f);
                        task["maxFollowTime"] = cfg.ApexMaxFollowTimeSec;
                        touched = true;
                    }

                    // idle tasks that watch for players (Sleep 10, Sit/Lie 5):
                    // the bear notices you from real distance and gets up
                    if (code == "idle" && task["stopOnNearbyEntityCodes"] != null)
                    {
                        task["stopRange"] = Math.Max(F(task["stopRange"], 0), cfg.ApexIdleStopRange);
                        touched = true;
                    }
                }
            }
            return touched;
        }

        private static bool AdjustPackHunter(EntityProperties et, HuntingConfig cfg)
        {
            bool touched = false;
            var taskai = FindServerBehavior(et, "taskai");
            if (taskai?["aitasks"] is JArray tasks)
            {
                foreach (var jt in tasks)
                {
                    var task = jt as JObject;
                    if (task == null) continue;
                    string code = S(task["code"]);

                    // the post-damage chase: long and wide so packs stay locked on
                    // (solo relentlessness is capped by the hit-and-run withdrawal)
                    if (code == "seekentity" && ContainsPlayer(task)
                        && S(task["whenInEmotionState"]).Contains("aggressiveondamage"))
                    {
                        task["seekingRange"] = Math.Max(F(task["seekingRange"], 0), cfg.PackAggroSeekRange);
                        task["maxFollowTime"] = cfg.PackMaxFollowTimeSec;
                        touched = true;
                    }

                    // no bedding down mid-hunt in front of the hunter
                    if (code == "idle" && S(task["animation"]).Equals("Sleep", StringComparison.OrdinalIgnoreCase))
                    {
                        task["stopRange"] = Math.Max(F(task["stopRange"], 0), 20f);
                        string gate = S(task["whenNotInEmotionState"]);
                        if (!gate.Contains("fleeondamage"))
                            task["whenNotInEmotionState"] = gate.Length > 0 ? gate + "|fleeondamage" : "fleeondamage";
                        touched = true;
                    }
                }
            }
            return touched;
        }

        private static bool ContainsPlayer(JObject task)
        {
            if (task["entityCodes"] is JArray codes)
                foreach (var c in codes) if (S(c) == "player") return true;
            return false;
        }

        private static float F(JToken t, float def)
        {
            if (t == null) return def;
            try { return t.Value<float>(); } catch { return def; }
        }

        // ---- dynamic pack sense (server runtime) ----

        /// <summary>Counts alive same-species ADULTS (not self, not babies) within
        /// PackRadius. Species = code path up to the first dash, so any two wolves
        /// near each other hunt as a pack regardless of spawn herd.</summary>
        internal static int CountPackmates(Entity e)
        {
            string path = e.Code?.Path;
            if (path == null) return 0;
            int dash = path.IndexOf('-');
            string species = dash > 0 ? path.Substring(0, dash + 1) : path;
            float r = HuntingModSystem.Cfg.PackRadius;

            int count = 0;
            e.World.GetEntitiesAround(e.Pos.XYZ, r, r, other =>
            {
                if (other.EntityId != e.EntityId && other.Alive && other is EntityAgent
                    && other.Code?.Path != null
                    && other.Code.Path.StartsWith(species)
                    && !other.Code.Path.Contains("-baby")) count++;
                return false; // collect nothing, just count
            });
            return count;
        }

        internal static bool IsPackPredator(Entity e)
        {
            string path = e?.Code?.Path;
            return path != null && !path.Contains("-baby") && MatchesAny(path, HuntingModSystem.Cfg.PackCodes);
        }
    }

    /// <summary>
    /// PACKS DO NOT BREAK: fleeondamage never triggers while a packmate is near.
    /// Prefix on the 3-arg TryTriggerState (the 2-arg overload funnels into it,
    /// decompile-verified), so both damage-triggered and scripted triggers are
    /// caught. Solo animals keep vanilla flee rules - "only flee if solo".
    /// </summary>
    [HarmonyPatch(typeof(EntityBehaviorEmotionStates), "TryTriggerState",
        typeof(string), typeof(double), typeof(long))]
    public static class Patch_PackNeverBreaks
    {
        public static bool Prefix(EntityBehaviorEmotionStates __instance, string statecode, ref bool __result)
        {
            var cfg = HuntingModSystem.Cfg;
            if (!cfg.PredatorOverhaulEnabled || !cfg.PackSuppressFlee) return true;
            if (statecode != "fleeondamage") return true;
            var e = __instance.entity;
            if (e == null || e.World.Side != EnumAppSide.Server) return true;
            if (!PredatorAI.IsPackPredator(e)) return true;
            if (PredatorAI.CountPackmates(e) < 1) return true;

            __result = false;
            return false;
        }
    }

    /// <summary>
    /// SOLO HIT-AND-RUN: a lone wolf/hyena that lands a bite on a player
    /// disengages immediately (InstaFleeFrom: 9s / ~15 blocks, then it may come
    /// back for another pass). Damage-landed is detected by comparing the
    /// target's health across attackTarget - the method no-ops without direct
    /// contact, so a whiffed swing never triggers the withdrawal.
    /// </summary>
    [HarmonyPatch(typeof(AiTaskMeleeAttack), "attackTarget")]
    public static class Patch_SoloHitAndRun
    {
        public static void Prefix(AiTaskMeleeAttack __instance, out float __state)
        {
            __state = -1f;
            var target = Traverse.Create(__instance).Field("targetEntity").GetValue<Entity>();
            var hp = target?.GetBehavior<EntityBehaviorHealth>();
            if (target is EntityPlayer && hp != null) __state = hp.Health;
        }

        public static void Postfix(AiTaskMeleeAttack __instance, float __state)
        {
            var cfg = HuntingModSystem.Cfg;
            if (!cfg.PredatorOverhaulEnabled || !cfg.SoloHitAndRun || __state < 0f) return;

            var tv = Traverse.Create(__instance);
            var target = tv.Field("targetEntity").GetValue<Entity>();
            var self = tv.Field("entity").GetValue<EntityAgent>();
            if (self == null || !(target is EntityPlayer) || !target.Alive) return;
            if (self.World.Side != EnumAppSide.Server) return;
            if (!PredatorAI.IsPackPredator(self)) return;

            var hp = target.GetBehavior<EntityBehaviorHealth>();
            if (hp == null || hp.Health >= __state) return;   // swing missed

            if (PredatorAI.CountPackmates(self) >= 1) return; // pack presses the attack

            var tm = self.GetBehavior<EntityBehaviorTaskAI>()?.TaskManager;
            if (tm == null) return;
            foreach (var t in tm.AllTasks)
            {
                if (t is AiTaskFleeEntity flee) { flee.InstaFleeFrom(target); break; }
            }
        }
    }
}
