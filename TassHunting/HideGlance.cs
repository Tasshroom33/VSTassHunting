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
    /// THE CHANCE, since 0.14.38 (owner order 2026-08-29 "hard define thick hide vs armor"):
    ///
    ///   bounce = (hide + sizeCurve x toughness) x sharpness x powershot + armor,  clamped to
    ///   GlanceChanceCeiling
    ///
    /// Three hard-separated sources, because they lose to different things:
    ///  - SIZE (the original tanh curve): zero at and below GlanceStartHealth, rising toward
    ///    GlanceMaxChance. Health is a size proxy - the engine has no creature armor stat.
    ///    GlanceToughness stays its hide-QUALITY corrector (soft-bodied sauropod down).
    ///  - THICK HIDE (GlanceHideBase, wildcard codes to a flat chance): skin thick enough to
    ///    turn a shot regardless of body size - a small dino still repels some arrows. Sharp
    ///    metal and power shots cut through it, exactly like they cut the size term.
    ///  - ARMOR (GlanceArmorBase, wildcard codes to a flat chance): bone plate. Immune to
    ///    sharpness AND to power shots - no blade quality helps against plate; only the hard
    ///    ceiling caps it, so nothing is ever arrow-proof - there is always a spear that bites.
    ///
    /// Both maps empty = exactly the pre-0.14.38 curve, so vanilla animals are untouched by
    /// construction. Power shots halve the hide+size half only - the patient shot is the
    /// answer to thick hide, and deliberately NOT the answer to plate.
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
        /// SHARPNESS (owner approval 2026-08-28): sharper metal bites deeper. Keyed on the hit's
        /// DAMAGE, not its damage tier - vanilla spears are tier 0 from flint through blackbronze
        /// and arrows carry no tier at all, so tier cannot tell the materials apart; the damage
        /// number IS the material ladder (flint 4.0 ... steel 7.0, asset-verified 1.22.5). At or
        /// below the base (flint) the factor is exactly 1, so the tuned base curve is untouched;
        /// each point of damage above it shaves GlanceSharpnessStep off the glance, floored at
        /// GlanceSharpnessFloor - plate stays plate, no blade turns it to paper. Big creature
        /// bites (a rex at 24) sit on the floor: crushing force beats armor.
        /// </summary>
        public static float Sharpness(float hitDamage, float baseDamage, float step, float floor)
        {
            float f = 1f - step * (hitDamage - baseDamage);
            return GameMath.Clamp(f, GameMath.Clamp(floor, 0f, 1f), 1f);
        }

        /// <summary>
        /// The three sources composed - pure, exercised directly by the harness. sizeChance
        /// arrives already toughness-scaled but UNsharpened and UNclamped; hide and size lose
        /// to sharpness and power shots together, armor loses to neither.
        /// </summary>
        public static float Combined(float hideBase, float armorBase, float sizeChance,
            float sharpness, bool powerShot, float hardCeiling)
        {
            float pierce = sharpness * (powerShot ? 0.5f : 1f);
            float c = (Math.Max(0f, hideBase) + Math.Max(0f, sizeChance)) * pierce + Math.Max(0f, armorBase);
            return GameMath.Clamp(c, 0f, GameMath.Clamp(hardCeiling, 0f, 1f));
        }

        /// <summary>
        /// The glance chance for this victim against this hit (proj null for melee; hitDamage is
        /// the hit's damage, pre-armor where available). Players never glance - PvP arrows
        /// always bite regardless of modded player health pools.
        /// </summary>
        public static float ChanceFor(Entity victim, EntityProjectileBase proj, HuntingConfig cfg, float hitDamage)
        {
            if (cfg == null || victim == null || victim is EntityPlayer) return 0f;
            float hp = BleedSystem.MaxHealthOf(victim);
            bool punch = cfg.PowerShotPunchesThrough
                && proj != null && proj.WatchedAttributes.GetBool("tasshunt:powershot");
            float sharp = Sharpness(hitDamage, cfg.GlanceSharpnessBase, cfg.GlanceSharpnessStep, cfg.GlanceSharpnessFloor);
            // Size half: toughness-scaled, ceiling deferred to Combined (1f = no clamp here),
            // sharpness and power shot deferred too so armor stays outside their reach.
            float size = Chance(hp, cfg.GlanceStartHealth, cfg.GlanceRampHealth, cfg.GlanceMaxChance,
                ToughnessFor(victim, cfg), 1f, false);
            return Combined(MapValueFor(victim, cfg.GlanceHideBase, 0f),
                MapValueFor(victim, cfg.GlanceArmorBase, 0f),
                size, sharp, punch, cfg.GlanceChanceCeiling);
        }

        /// <summary>First matching GlanceToughness entry wins; no entry = 1 (the plain curve).</summary>
        public static float ToughnessFor(Entity victim, HuntingConfig cfg)
            => MapValueFor(victim, cfg?.GlanceToughness, 1f);

        /// <summary>First matching wildcard entry wins (full code and bare path, the map
        /// convention everywhere in this mod); no entry = the fallback.</summary>
        public static float MapValueFor(Entity victim, Dictionary<string, float> map, float fallback)
        {
            if (map == null || map.Count == 0 || victim?.Code == null) return fallback;
            string full = victim.Code.ToShortString();
            string path = victim.Code.Path;
            foreach (var kv in map)
            {
                if (WildcardUtil.Match(kv.Key, full) || WildcardUtil.Match(kv.Key, path)) return kv.Value;
            }
            return fallback;
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
