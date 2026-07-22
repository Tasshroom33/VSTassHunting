using System;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace TassHunting
{
    /// <summary>
    /// ARROWHEAD ON BREAK (0.9.4, user request, config-gated default off): when
    /// an arrow BREAKS on impact, drop its matching arrowhead item at the break
    /// point so the head can be walked over and recovered.
    ///
    /// Break detection (decompile-verified, EntityProjectileBase.DamageProjectile
    /// + EntityProjectile.IsColliding): both the entity-hit break and the
    /// block-hit break end in Entity.Die(EnumDespawnReason.Death). Pickup uses
    /// PickedUp, sticky-ride timeout uses Expire (we set that explicitly), chunk
    /// unload uses Unload - so gating on Death alone cleanly means "it broke".
    ///
    /// Head mapping: vanilla ships arrowhead-{material} for flint, obsidian,
    /// copper, tinbronze, bismuthbronze, blackbronze, gold, silver, iron,
    /// meteoriciron, steel. It does NOT exist for crude, erel (reed) or bone -
    /// those drop nothing (a reed/bone arrow leaves no reusable tip). We only
    /// drop when the item actually resolves, so modded arrow materials that ship
    /// their own arrowhead-{material} are covered automatically, and those that
    /// do not simply drop nothing.
    /// </summary>
    // Die() is declared on the BASE Entity class, NOT on EntityProjectileBase -
    // Harmony resolves a [HarmonyPatch(type, name)] against the DECLARING type,
    // so targeting EntityProjectileBase threw "Undefined target method" and
    // aborted the whole mod load (fixed 0.9.10). Patch Entity.Die and filter to
    // arrow projectiles inside the postfix; the virtual call still fires here.
    [HarmonyPatch(typeof(Entity), "Die")]
    public static class Patch_ArrowheadOnBreak
    {
        public static void Postfix(Entity __instance, EnumDespawnReason reason)
        {
            try
            {
                var cfg = HuntingModSystem.Cfg;
                if (cfg == null || !cfg.DropArrowheadOnBreak) return;
                if (reason != EnumDespawnReason.Death) return;          // Death == broke; ignore pickup/expire/unload
                if (!(__instance is EntityProjectileBase)) return;      // only projectiles
                if (__instance.World == null || __instance.World.Side != EnumAppSide.Server) return;

                string path = __instance.Code?.Path;
                if (path == null || !path.StartsWith("arrow-")) return; // arrows only (spears have no head item)

                string material = path.Substring("arrow-".Length);
                var headItem = __instance.World.GetItem(new AssetLocation("game", "arrowhead-" + material));
                if (headItem == null) return; // crude / erel / bone and unknown mods: no head to recover

                var pos = __instance.SidedPos;
                if (pos == null) return;
                __instance.World.SpawnItemEntity(new ItemStack(headItem, 1), pos.XYZ.Add(0.0, 0.1, 0.0));
            }
            catch { }
        }
    }
}
