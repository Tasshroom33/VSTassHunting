using System;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace TassHunting
{
    /// <summary>
    /// HARVEST OVERHAUL (playtest 2026-07-19). Decompile-verified pipeline, 1.22.3:
    /// ItemKnife times the harvest hold CLIENT-side (OnHeldInteractStep) and
    /// re-verifies the same formula SERVER-side (OnHeldInteractStop) â€” both via
    /// IHarvestable.GetHarvestDuration, then the server calls SetHarvested ->
    /// GenerateDrops, which rolls loot into the behavior's InventoryGeneric.
    /// The carcass window (GuiDialogCreatureContents "carcasscontents") opens ONLY
    /// from EntityBehaviorHarvestable.OnInteract, on a SECOND right-click after the
    /// "harvested" attribute syncs. Corpse removal is EntityBehaviorDeadDecay
    /// .DecayNow (AllowDespawn + poof particles). Entity.Die writes the deathBy*
    /// attributes BEFORE the behaviors' OnEntityDeath loop, so a DeadDecay
    /// .OnEntityDeath postfix sees the loot multipliers GenerateDrops reads
    /// (the API-level TriggerEntityDeath event fires too EARLY for that).
    ///
    ///  - HarvestTimeMult: scales GetHarvestDuration (patched process-wide, so
    ///    client hold time and server verification always agree).
    ///  - Auto-drop: SetHarvested postfix spills the rolled loot at the corpse and
    ///    decays it instantly â€” the window never opens, nothing to click and drag.
    ///  - Empty-corpse removal: player kills PRE-ROLL their loot at death (killer
    ///    stands in for the harvester â€” no vanilla entity drop is tool-gated,
    ///    asset-grep-verified). Rolled empty => flagged harvested (no ghost
    ///    "harvest" prompt) + timed DecayNow, so the death animation stays
    ///    visible. Corpses that were never harvestable (bells, locusts â€” their
    ///    loot drops via Entity.Die->GetDrops at death) get the same timer.
    ///    Non-player kills (wolf takes a sheep) keep the vanilla corpse so the
    ///    player can still come skin it.
    /// </summary>
    public static class HarvestOverhaul
    {
        /// <summary>Spill the harvest inventory onto the ground and remove the
        /// corpse through the engine's own decay path. Server-side only.</summary>
        public static void SpillAndDecay(EntityBehaviorHarvestable bh, Entity entity)
        {
            var inv = bh.Inventory;
            if (inv != null && !inv.Empty)
            {
                // DropAll nulls + MarkDirty()s each slot, so the behavior's own
                // SlotModified listener syncs "harvestableInv" for us.
                inv.DropAll(entity.Pos.XYZ.Add(0.0, 0.25, 0.0));
            }
            try { entity.GetBehavior<EntityBehaviorDeadDecay>()?.DecayNow(); } catch { }
        }
    }

    /// <summary>Playtest #4: "harvest takes too long". Both the client hold and
    /// the server check multiply through here, so they can't disagree.</summary>
    [HarmonyPatch(typeof(EntityBehaviorHarvestable), nameof(EntityBehaviorHarvestable.GetHarvestDuration))]
    public static class Patch_HarvestDuration
    {
        public static void Postfix(ref float __result)
        {
            __result *= HuntingModSystem.Cfg.HarvestTimeMult;
        }
    }

    /// <summary>Playtest #2: no loot window â€” knife finishes, loot pops out,
    /// carcass poofs. SetHarvested also runs client-side from the knife's
    /// OnHeldInteractStop (GenerateDrops no-ops there), hence the side gate.</summary>
    [HarmonyPatch(typeof(EntityBehaviorHarvestable), nameof(EntityBehaviorHarvestable.SetHarvested))]
    public static class Patch_AutoDropHarvest
    {
        public static void Postfix(EntityBehaviorHarvestable __instance)
        {
            if (!HuntingModSystem.Cfg.HarvestAutoDrop) return;
            var entity = __instance.entity;
            if (entity == null || entity.World.Side != EnumAppSide.Server) return;
            HarvestOverhaul.SpillAndDecay(__instance, entity);
        }
    }

    /// <summary>Belt and braces for auto-drop: the window itself. Covers corpses
    /// harvested before this mod (right-click spills them instead of opening the
    /// dialog) and any path that leaves a harvested corpse standing.</summary>
    [HarmonyPatch(typeof(EntityBehaviorHarvestable), nameof(EntityBehaviorHarvestable.OnInteract))]
    public static class Patch_SuppressCarcassWindow
    {
        public static bool Prefix(EntityBehaviorHarvestable __instance, EntityAgent byEntity, Vec3d hitPosition, EnumInteractMode mode)
        {
            if (!HuntingModSystem.Cfg.HarvestAutoDrop) return true;
            if (mode != EnumInteractMode.Interact) return true;
            if (!__instance.IsHarvested) return true;
            var entity = __instance.entity;
            if (entity == null) return true;
            // Same reach guard the vanilla method uses (5 + 1 client fudge).
            if (entity.Pos.XYZ.Add(hitPosition).DistanceTo(byEntity.Pos.XYZ.Add(byEntity.LocalEyePos)) > 6f) return false;

            if (entity.World.Side == EnumAppSide.Server)
            {
                HarvestOverhaul.SpillAndDecay(__instance, entity);
            }
            return false; // never open the carcass window
        }
    }

    /// <summary>Playtest #1 + #3: corpses with nothing in them self-remove after a
    /// few seconds, globally â€” animals, bells, bowtorns, locusts, drifters.</summary>
    [HarmonyPatch(typeof(EntityBehaviorDeadDecay), "OnEntityDeath")]
    public static class Patch_EmptyCorpseRemoval
    {
        public static void Postfix(EntityBehaviorDeadDecay __instance, DamageSource damageSourceForDeath)
        {
            var cfg = HuntingModSystem.Cfg;
            if (!cfg.EmptyCorpseAutoRemove) return;
            var entity = __instance.entity;
            if (entity == null || entity.World.Side != EnumAppSide.Server) return;

            var harvestBh = entity.GetBehavior<EntityBehaviorHarvestable>();
            if (harvestBh != null)
            {
                var player = (damageSourceForDeath?.GetCauseEntity() as EntityPlayer)?.Player;
                if (player == null) return; // not a player kill: vanilla corpse
                harvestBh.GenerateDrops(player);
                if (harvestBh.Inventory == null || !harvestBh.Inventory.Empty) return; // real loot: wait for the knife
                entity.WatchedAttributes.SetBool("harvested", true);
            }

            int delayMs = (int)(GameMath.Clamp(cfg.EmptyCorpseRemoveSeconds, 1f, 300f) * 1000f);
            entity.World.RegisterCallback(dt =>
            {
                try { if (!entity.Alive) __instance.DecayNow(); } catch { }
            }, delayMs);
        }
    }
}
