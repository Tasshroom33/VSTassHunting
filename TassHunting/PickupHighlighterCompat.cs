using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common.Entities;

namespace TassHunting
{
    /// <summary>
    /// Compat shim for tacf's Item Pickup Highlighter (user request 2026-07-18):
    /// its projectile filter highlights EVERY projectile-class entity â€” including
    /// enemy-thrown ones. The engine already syncs the shooter (the projectile's
    /// "firedBy" watched attribute carries the shooter's entity id, decompile-
    /// verified in EntityProjectileBase.ToBytes), so this postfixes the mod's
    /// compiler-generated filter lambda: projectiles not fired by YOU stop
    /// highlighting. Reflection-only â€” no reference to their assembly, no effect
    /// when the mod is absent, and if a future version renames things we just log
    /// and do nothing. Toggle: HighlightOnlyOwnProjectiles in TasshroomHunting.json.
    /// </summary>
    public static class PickupHighlighterCompat
    {
        private static ICoreClientAPI _capi;

        public static void TryPatch(ICoreClientAPI capi, Harmony harmony)
        {
            _capi = capi;
            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "ItemPickupHighlighter");
                var sysType = asm?.GetType("ItemPickupHighlighter.ItemPickupHighlighterModSystem");
                if (sysType == null) { capi.Logger.Warning("[TassHunting] pickup highlighter type not found; own-projectile filter inactive."); return; }

                // The filter is a static lambda inside HighlightNearbyItems, compiled
                // into a nested "<>c" class as bool <HighlightNearbyItems>b__N(Entity).
                MethodInfo lambda = sysType
                    .GetNestedTypes(BindingFlags.NonPublic)
                    .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic))
                    .FirstOrDefault(m => m.Name.Contains("HighlightNearbyItems")
                        && m.ReturnType == typeof(bool)
                        && m.GetParameters().Length == 1
                        && typeof(Entity).IsAssignableFrom(m.GetParameters()[0].ParameterType));
                if (lambda == null) { capi.Logger.Warning("[TassHunting] pickup highlighter filter lambda not found; own-projectile filter inactive."); return; }

                harmony.Patch(lambda, postfix: new HarmonyMethod(typeof(PickupHighlighterCompat), nameof(FilterPostfix)));
                capi.Logger.Event("[TassHunting] pickup highlighter own-projectile filter active.");
            }
            catch (Exception ex)
            {
                capi.Logger.Warning("[TassHunting] pickup highlighter compat failed: {0}", ex.Message);
            }
        }

        // __0 = positional injection (the compiled lambda's parameter name is
        // compiler-defined, so name matching fails).
        public static void FilterPostfix(object __0, ref bool __result)
        {
            if (!__result || !(__0 is Entity val)) return;
            // Only constrain PROJECTILES; ground items keep the mod's own rules.
            if (val.Class == null || val.Class.IndexOf("projectile", StringComparison.OrdinalIgnoreCase) < 0) return;
            // RIDING a target (StickyArrow's synced sa_target attr): a marker
            // dancing on a live animal is noise, and a riding spear (collectible
            // since StickyArrow 0.1.1) would light the whole animal up as loot.
            // No highlight until the projectile is released (playtest 2026-07-19).
            if (val.WatchedAttributes.GetLong("sa_target", 0L) != 0L) { __result = false; return; }
            if (!HuntingModSystem.Cfg.HighlightOnlyOwnProjectiles) return;
            long me = _capi?.World?.Player?.Entity?.EntityId ?? 0;
            if (me == 0) return;
            long firedBy = val.WatchedAttributes.GetLong("firedBy", 0L);
            if (firedBy != me) __result = false; // unknown or someone else's: not yours
        }
    }
}
