using System;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace TassHunting
{
    /// <summary>
    /// ARROW OWNERSHIP (user request 2026-07-28): a fired projectile belongs to its
    /// shooter for ArrowOwnerLockSeconds - other players' walk-over pickup ignores
    /// it, the shooter always may collect, and after the window anyone may (so a
    /// departed player's arrows never become permanent unliftable litter).
    ///
    /// The engine already stamps the shooter ("firedBy" entity id, set in
    /// EntityProjectileBase.Initialize) but its CanCollect never looks at WHO is
    /// asking - that is the whole gap. These patches stamp the durable PlayerUID
    /// at launch and add the missing owner gate at pickup.
    ///
    /// Clock: server uptime with a backwards-clock guard - after a server restart
    /// the lock simply releases (a two-minute window is not worth persisting a
    /// calendar; uptime stamps from a previous boot read as "in the future" and
    /// must never re-lock, the lesson of the THW rejoin-cold bug).
    /// </summary>
    [HarmonyPatch(typeof(EntityProjectileBase), nameof(EntityProjectileBase.Initialize))]
    public static class Patch_StampArrowOwner
    {
        public static void Postfix(EntityProjectileBase __instance)
        {
            try
            {
                if (__instance.Api?.Side != EnumAppSide.Server) return;
                // FiredBy is only non-null at the ORIGINAL launch (the bow sets it
                // before spawn); a chunk-reload Initialize leaves it null, so an
                // existing stamp is never overwritten with a fresh clock.
                if (!(__instance.FiredBy is EntityPlayer p)) return;
                string uid = p.PlayerUID;
                if (string.IsNullOrEmpty(uid)) return;
                var wa = __instance.WatchedAttributes;
                wa.SetString("tassOwner", uid);
                wa.SetLong("tassFiredMs", __instance.World.ElapsedMilliseconds);
            }
            catch (Exception e)
            {
                __instance?.World?.Logger?.Warning("[TassHunting] owner stamp failed: {0}", e.Message);
            }
        }
    }

    [HarmonyPatch(typeof(EntityProjectileBase), "CanCollect")]
    public static class Patch_ArrowOwnerLock
    {
        public static void Postfix(EntityProjectileBase __instance, Entity byEntity, ref bool __result)
        {
            if (!__result) return;
            float lockSec = HuntingModSystem.Cfg?.ArrowOwnerLockSeconds ?? 0f;
            if (lockSec <= 0f) return;
            var wa = __instance.WatchedAttributes;
            string owner = wa.GetString("tassOwner", null);
            if (string.IsNullOrEmpty(owner)) return;                // no owner (mob-thrown): unlocked
            // An arrow riding YOU is always yours to remove: the shooter's
            // ownership window must never pin an arrow in a player's body
            // against their will.
            if (byEntity != null && wa.GetLong("sa_target", 0L) == byEntity.EntityId) return;
            string askerUid = (byEntity as EntityPlayer)?.PlayerUID;
            if (askerUid != null && askerUid == owner) return;      // the shooter always may
            long fired = wa.GetLong("tassFiredMs", 0L);
            long now = __instance.World.ElapsedMilliseconds;
            if (fired <= 0L || fired > now) return;                 // no stamp, or pre-restart stamp: released
            if (now - fired < (long)(lockSec * 1000f)) __result = false;
        }
    }
}
