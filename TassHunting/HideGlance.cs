using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace TassHunting
{
    /// <summary>
    /// HIDE GLANCE (owner design 2026-08-28): past a size threshold, a body sometimes turns the
    /// edge - the arrow or spear bounces off instead of biting. One roll per hit decides BOTH
    /// halves: no wound (BleedSystem.OnSharpHit returns before the ledger) and no stick
    /// (Patch_ArrowSticksInsteadOfVanishing skips Attach AND the break roll, so the deflected
    /// projectile survives intact - ImpactOnEntity zeroes its motion after our prefix returns,
    /// decompile-verified 1.22.5, and it drops recoverable at the animal's feet). A stuck arrow
    /// that opened no wound, or a bounced arrow that bled, would each read as a bug; the shared
    /// roll makes them impossible.
    ///
    /// The chance curve is the same tanh family as the bleed ceiling: zero at and below
    /// GlanceStartHealth (default 45 - every vanilla animal smaller than a bear always takes the
    /// hit), then rising smoothly toward GlanceMaxChance. Health is a SIZE proxy, not armor -
    /// the engine has no creature armor stat - so GlanceToughness is the per-creature correction:
    /// wildcard entity codes to multipliers (plated ankylosaur up, soft-bodied sauropod down).
    /// After the multiplier the result is clamped to GlanceChanceCeiling so no config combination
    /// ever makes something arrow-proof - there is always a spear that bites.
    ///
    /// Power shots (PowerShotPunchesThrough): a full heavy draw halves the glance chance - the
    /// patient shot is the answer to thick hide, not more RNG.
    /// </summary>
    public static class HideGlance
    {
        /// <summary>Pure curve, exercised directly by the harness.</summary>
        public static float Chance(float maxHealth, float startHealth, float rampHealth,
            float maxChance, float toughnessMult, float hardCeiling, bool powerShot)
        {
            if (maxChance <= 0f || maxHealth <= startHealth) return 0f;
            float ramp = Math.Max(1f, rampHealth);
            float g = maxChance * (float)Math.Tanh((maxHealth - startHealth) / ramp);
            g *= Math.Max(0f, toughnessMult);
            if (powerShot) g *= 0.5f;
            return GameMath.Clamp(g, 0f, GameMath.Clamp(hardCeiling, 0f, 1f));
        }

        /// <summary>
        /// The glance chance for this victim against this projectile (null for melee). Players
        /// never glance - PvP arrows always bite regardless of modded player health pools.
        /// </summary>
        public static float ChanceFor(Entity victim, EntityProjectileBase proj, HuntingConfig cfg)
        {
            if (cfg == null || victim == null || victim is EntityPlayer) return 0f;
            float hp = BleedSystem.MaxHealthOf(victim);
            bool punch = cfg.PowerShotPunchesThrough
                && proj != null && proj.WatchedAttributes.GetBool("tasshunt:powershot");
            return Chance(hp, cfg.GlanceStartHealth, cfg.GlanceRampHealth, cfg.GlanceMaxChance,
                ToughnessFor(victim, cfg), cfg.GlanceChanceCeiling, punch);
        }

        /// <summary>First matching GlanceToughness entry wins; no entry = 1 (the plain curve).</summary>
        public static float ToughnessFor(Entity victim, HuntingConfig cfg)
        {
            var map = cfg?.GlanceToughness;
            if (map == null || map.Count == 0 || victim?.Code == null) return 1f;
            string full = victim.Code.ToShortString();
            string path = victim.Code.Path;
            foreach (var kv in map)
            {
                if (WildcardUtil.Match(kv.Key, full) || WildcardUtil.Match(kv.Key, path)) return kv.Value;
            }
            return 1f;
        }

        // ---- one roll per projectile hit, shared between the bleed gate and the stick gate ----
        // The engine calls DealDamage (-> OnSharpHit) before DamageProjectile (-> stick prefix),
        // but the registry is deliberately order-agnostic: whichever gate asks FIRST rolls, the
        // other reads the stored verdict. Covers hits the bleed gate never sees (damage under
        // BleedMinDamage, another mod's prefix swallowing the damage call).

        private static readonly Dictionary<long, (bool glanced, long atMs)> Rolls
            = new Dictionary<long, (bool, long)>();
        private const long StaleMs = 10000;

        public static bool RollOnce(long projectileId, float chance, IWorldAccessor world)
        {
            long now = world.ElapsedMilliseconds;
            lock (Rolls)
            {
                if (Rolls.Count > 32)
                {
                    List<long> stale = null;
                    foreach (var kv in Rolls)
                        if (now - kv.Value.atMs > StaleMs) (stale = stale ?? new List<long>()).Add(kv.Key);
                    if (stale != null) foreach (long id in stale) Rolls.Remove(id);
                }
                if (Rolls.TryGetValue(projectileId, out var r) && now - r.atMs <= StaleMs) return r.glanced;
                bool glanced = world.Rand.NextDouble() < chance;
                Rolls[projectileId] = (glanced, now);
                return glanced;
            }
        }
    }
}
