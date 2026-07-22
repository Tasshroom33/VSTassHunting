// WOUNDED SLOWDOWN: a configurable health-tier table that slows ALL creature
// movement (chase, flee, wander), not just fleeing - a wounded wolf chasing
// you slows down too, and the amount is a full per-tier table rather than a
// single global scalar.
//
// Mechanism (decompile-verified 1.22.3): creatures do NOT read the
// "walkspeed" stat (that is player plumbing - wearables/GUI). Every AI task
// drives movement through PathTraverserBase.movingSpeed via NavigateTo /
// NavigateTo_Async / WalkTowards on WaypointsTraverser or
// StraightLineTraverser. One prefix on those entry points scales the speed
// argument by the wounded tier - chase, flee, wander, all of it.

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;          // PathTraverserBase, EntityAgent, EntityPlayer
using Vintagestory.Essentials;          // WaypointsTraverser, StraightLineTraverser
using Vintagestory.GameContent;         // EntityBehaviorHealth

namespace TassHunting
{
    public class WoundedSlowTier
    {
        public float HealthPctMax; // tier applies when hp% <= this
        public float SlowPct;      // speed reduction in percent
    }

    [HarmonyPatch]
    public static class Patch_WoundedSlowdown
    {
        private static readonly FieldInfo EntityField = AccessTools.Field(typeof(PathTraverserBase), "entity");

        // Count of speed methods actually matched, logged once after PatchAll so a
        // future VS rename that yields ZERO targets surfaces in the log instead of
        // silently disabling wounded-slowdown (diagnostics-every-iteration law).
        internal static int MatchedCount;

        private static IEnumerable<MethodBase> TargetMethods()
        {
            MatchedCount = 0;
            // NOT DeclaredOnly (compat sweep 2026-07-22): StraightLineTraverser
            // INHERITS its speed methods from PathTraverserBase, so DeclaredOnly
            // missed that traverser entirely. Match by POSITION not param NAME:
            // ps[1] is the float speed slot across every matched overload - the
            // name "movingSpeed" is metadata that a name-stripped build would drop.
            foreach (var t in new[] { typeof(WaypointsTraverser), typeof(StraightLineTraverser) })
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name != "NavigateTo" && m.Name != "NavigateTo_Async" && m.Name != "WalkTowards") continue;
                    var ps = m.GetParameters();
                    if (ps.Length >= 2 && ps[1].ParameterType == typeof(float))
                    {
                        MatchedCount++;
                        yield return m;
                    }
                }
        }

        [HarmonyPrefix]
        private static void Prefix(object __instance, ref float movingSpeed)
        {
            var cfg = HuntingModSystem.Cfg;
            if (cfg == null || !cfg.WoundedSlowdownEnabled) return;
            if (!(EntityField.GetValue(__instance) is EntityAgent ent) || ent is EntityPlayer) return;

            var health = ent.GetBehavior<EntityBehaviorHealth>();
            if (health == null || health.MaxHealth <= 0f) return;
            float hpPct = health.Health / health.MaxHealth * 100f;

            // First tier (lowest HealthPctMax) that covers the current hp% wins.
            float slow = 0f, best = float.MaxValue;
            var tiers = cfg.WoundedSlowTiers;
            if (tiers == null) return;
            foreach (var t in tiers)
                if (t != null && hpPct <= t.HealthPctMax && t.HealthPctMax < best)
                { best = t.HealthPctMax; slow = t.SlowPct; }

            if (slow > 0f) movingSpeed *= Math.Max(0f, 1f - slow / 100f);
        }
    }
}
