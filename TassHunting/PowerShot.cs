// POWER SHOT (2026-07-27, user spec): vanilla bows are flat damage past the 0.65s
// minimum draw - time buys only accuracy (BaseAimingAccuracy: accuracy ramps at
// 1.7 x rangedWeaponsSpeed per second to a cap of 1 - 0.075/rangedWeaponsAcc, so a
// default player is fully accurate at ~0.54s). This adds the payoff for patience:
// hold PowerShotExtraDrawSeconds (default 1s) PAST your own full-accuracy time and
// the arrow hits for PowerShotDamageMult (default 1.25x). The threshold uses the
// SAME stats vanilla uses, so slow-draw classes get proportionally later power
// shots - rule, not hardcoded seconds.
//
// Wiring: an ItemBow.OnHeldInteractStop prefix reads the true draw time and
// stashes the multiplier per shooter; the projectile's PreInitialize postfix
// (registered alongside true-aim in HuntingModSystem - the ONE binding that is
// actually called at spawn, see TryPatchTrueAim) consumes the stash and scales
// Damage before the entity spawns. A quiet click tells the shooter the moment
// the power threshold is reached.

using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace TassHunting
{
    public static class PowerShot
    {
        /// <summary>Consume window: a real shot's PreInitialize follows the bow release within the
        /// same tick. Anything older is a release that spawned no arrow (empty quiver) - purged so
        /// it can never boost a later quick shot.</summary>
        private const long ConsumeWindowMs = 1000;

        private static readonly Dictionary<long, (float mult, long atMs)> Pending = new Dictionary<long, (float, long)>();
        private static readonly Dictionary<long, float> LastStepSeconds = new Dictionary<long, float>();

        /// <summary>
        /// Seconds of aim until accuracy is maxed, mirroring BaseAimingAccuracy exactly:
        /// cap = min(1 - 0.075/acc, 1), ramp = 1.7 x speed x weaponMul per second.
        /// </summary>
        public static float FullAccuracySeconds(float accStat, float speedStat, float weaponSpeedMul)
        {
            if (accStat <= 0f) accStat = 1f;
            float cap = Math.Min(1f - 0.075f / accStat, 1f);
            float ramp = 1.7f * speedStat * weaponSpeedMul;
            if (ramp <= 0.001f || cap <= 0f) return 0.544f; // default-player fallback
            return cap / ramp;
        }

        /// <summary>This shooter's power-shot draw threshold for the held bow.</summary>
        public static float ThresholdSeconds(EntityAgent shooter, ItemSlot bowSlot)
        {
            float acc = shooter.Stats.GetBlended("rangedWeaponsAcc");
            float speed = shooter.Stats.GetBlended("rangedWeaponsSpeed");
            float mul = bowSlot?.Itemstack?.ItemAttributes?["rangedWeaponsSpeedMul"].AsFloat(1f) ?? 1f;
            return FullAccuracySeconds(acc, speed, mul) + HuntingModSystem.Cfg.PowerShotExtraDrawSeconds;
        }

        // ---- Stash (pure enough for the harness to exercise) ---------------------------------

        public static void Stash(long shooterId, float mult, long nowMs)
        {
            lock (Pending) Pending[shooterId] = (mult, nowMs);
        }

        /// <summary>The multiplier for this shooter's just-spawned arrow, or 1 if none/stale.</summary>
        public static float Consume(long shooterId, long nowMs)
        {
            lock (Pending)
            {
                float result = 1f;
                if (Pending.TryGetValue(shooterId, out var p))
                {
                    if (nowMs - p.atMs <= ConsumeWindowMs) result = p.mult;
                    Pending.Remove(shooterId);
                }
                return result;
            }
        }

        /// <summary>PreInitialize postfix half (registered next to true-aim): scale the arrow.</summary>
        public static void ApplyToProjectile(object projectileInstance)
        {
            var cfg = HuntingModSystem.Cfg;
            if (cfg == null || !cfg.PowerShotEnabled) return;
            var p = projectileInstance as EntityProjectileBase;
            var shooter = p?.FiredBy;
            if (p == null || shooter == null || p.World?.Side != EnumAppSide.Server) return;

            float mult = Consume(shooter.EntityId, p.World.ElapsedMilliseconds);
            if (mult > 1f) p.Damage *= mult;
        }

        /// <summary>Client-side draw cue bookkeeping: true exactly when the threshold is crossed.</summary>
        public static bool CrossedThreshold(long entityId, float secondsUsed, float threshold)
        {
            LastStepSeconds.TryGetValue(entityId, out float prev);
            LastStepSeconds[entityId] = secondsUsed;
            if (secondsUsed < prev) prev = 0f; // new draw started
            return prev < threshold && secondsUsed >= threshold;
        }
    }

    /// <summary>Release: record whether this draw earned the power multiplier.</summary>
    [HarmonyPatch(typeof(ItemBow), nameof(ItemBow.OnHeldInteractStop))]
    public static class Patch_PowerShotRelease
    {
        public static void Prefix(float secondsUsed, ItemSlot slot, EntityAgent byEntity)
        {
            var cfg = HuntingModSystem.Cfg;
            if (cfg == null || !cfg.PowerShotEnabled || cfg.PowerShotDamageMult <= 1f) return;
            if (byEntity?.World == null || byEntity.World.Side != EnumAppSide.Server) return;
            if (secondsUsed < PowerShot.ThresholdSeconds(byEntity, slot)) return;
            PowerShot.Stash(byEntity.EntityId, cfg.PowerShotDamageMult, byEntity.World.ElapsedMilliseconds);
        }
    }

    /// <summary>Draw cue: a quiet click for the shooter the moment the extra hold pays off.</summary>
    [HarmonyPatch(typeof(ItemBow), nameof(ItemBow.OnHeldInteractStep))]
    public static class Patch_PowerShotDrawCue
    {
        public static void Postfix(bool __result, float secondsUsed, ItemSlot slot, EntityAgent byEntity)
        {
            var cfg = HuntingModSystem.Cfg;
            if (cfg == null || !cfg.PowerShotEnabled || !cfg.PowerShotDrawCue || !__result) return;
            if (byEntity?.World == null || byEntity.World.Side != EnumAppSide.Client) return;
            if (!PowerShot.CrossedThreshold(byEntity.EntityId, secondsUsed, PowerShot.ThresholdSeconds(byEntity, slot))) return;
            byEntity.World.PlaySoundAt(new AssetLocation("sounds/effect/woodswitch"), byEntity, null, false, 8f, 0.4f);
        }
    }
}
