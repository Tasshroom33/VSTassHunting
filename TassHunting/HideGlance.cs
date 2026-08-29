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
    /// BOUNCE - THICK HIDE vs ARMOR (owner spec 2026-08-29, replaces the health-curve glance
    /// of 0.14.18-0.14.38 wholesale: "either an animal has armor or thick hide, no need to
    /// complicate the calc").
    ///
    /// An animal is classified, or it is not - no size math:
    ///  - ARMOR (ArmorCreatures): bone plate. Bounce by metal tier: stone ALWAYS bounces,
    ///    copper 90%, bronze 85%, iron 80%, steel 75%. Power shots do nothing. A stone-age
    ///    hunter genuinely cannot arrow an ankylosaurus - bring metal or a club (blunt hits
    ///    never bounce; they never bled either).
    ///  - THICK HIDE (ThickHideCreatures): most dinos, bears, moose, elder boars. Stone 50%
    ///    down to steel 30%; a power shot counts one tier better, and past steel there is one
    ///    more rung (HideBouncePastSteel) so a steel power shot still gains.
    ///  - Unlisted (every other vanilla animal, all players): never bounces - untouched.
    ///
    /// A BOUNCE IS A NON-EVENT (owner ruling): the prefix on the health behavior skips the
    /// hit entirely - no damage, no wound, no stick, no hurt flash - and the projectile drops
    /// recoverable at the animal's feet. Without that, "100% bounce" armor would still die to
    /// direct-damage chip and the wall would be a lie.
    ///
    /// ANIMAL FIGHTS BYPASS (owner ruling): a hit whose cause is a living non-player - a rex
    /// biting an anky, a wolf on a boar - never rolls. Predators must stay able to eat
    /// armored prey; crushing force beats plate. Player hits and world hits (traps, spikes)
    /// roll normally.
    ///
    /// Metal tier comes from the weapon or arrowhead MATERIAL token (config map), with a
    /// damage-band fallback for modded weapons that name no known material.
    /// </summary>
    public static class HideGlance
    {
        public enum BodyClass { None, ThickHide, Armor }

        /// <summary>The tier ladder, in order. Indexes into the config chance maps by name;
        /// one virtual rung past steel exists for hide power shots only.</summary>
        public static readonly string[] TierNames = { "stone", "copper", "bronze", "iron", "steel" };

        // Damage-band fallback for weapons whose material token is unmapped (modded gear).
        // Aligned to the vanilla ladders: flint spear 4.0, copper 5.0, tin bronze 6.0,
        // iron 6.8, steel 7.0.
        private static readonly float[] TierDamageCeilings = { 4.6f, 5.4f, 6.5f, 6.95f };

        /// <summary>Armor wins when an animal is on both lists - plate over skin.</summary>
        public static BodyClass ClassOf(Entity victim, HuntingConfig cfg)
        {
            if (cfg == null || victim == null || victim is EntityPlayer || victim.Code == null) return BodyClass.None;
            if (MatchesAny(victim, cfg.ArmorCreatures)) return BodyClass.Armor;
            if (MatchesAny(victim, cfg.ThickHideCreatures)) return BodyClass.ThickHide;
            return BodyClass.None;
        }

        private static bool MatchesAny(Entity victim, string[] patterns)
        {
            if (patterns == null || patterns.Length == 0) return false;
            string full = victim.Code.ToShortString(), path = victim.Code.Path;
            foreach (string p in patterns)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (WildcardUtil.Match(p, full) || WildcardUtil.Match(p, path)) return true;
            }
            return false;
        }

        /// <summary>The material token of a weapon or arrow: the last dash segment of its
        /// item code ("spear-generic-steel" -> "steel", "arrow-flint" -> "flint").</summary>
        public static string MaterialTokenOf(CollectibleObject col)
        {
            string path = col?.Code?.Path;
            if (string.IsNullOrEmpty(path)) return null;
            int cut = path.LastIndexOf('-');
            return (cut >= 0 && cut < path.Length - 1 ? path.Substring(cut + 1) : path).ToLowerInvariant();
        }

        /// <summary>Tier index 0..4. Unknown material falls back to the damage bands, so a
        /// modded super-weapon counts as the metal its numbers say it is.</summary>
        public static int TierOfMaterial(string materialToken, float hitDamage, HuntingConfig cfg)
        {
            if (materialToken != null && cfg?.BounceMetalTierByMaterial != null
                && cfg.BounceMetalTierByMaterial.TryGetValue(materialToken, out string name))
            {
                int idx = Array.IndexOf(TierNames, name);
                if (idx >= 0) return idx;
            }
            for (int i = 0; i < TierDamageCeilings.Length; i++)
                if (hitDamage <= TierDamageCeilings[i]) return i;
            return TierNames.Length - 1;
        }

        /// <summary>
        /// The whole table, pure. Armor reads its tier straight and ignores power shots; hide
        /// counts a power shot one tier better, with the past-steel rung as the top step so
        /// steel still gains something.
        /// </summary>
        public static float ChanceOf(BodyClass cls, int tier, bool powerShot, HuntingConfig cfg)
        {
            if (cfg == null || cls == BodyClass.None) return 0f;
            tier = GameMath.Clamp(tier, 0, TierNames.Length - 1);
            if (cls == BodyClass.Armor)
                return Lookup(cfg.ArmorBounceByMetal, TierNames[tier]);
            if (powerShot)
            {
                tier++;
                if (tier >= TierNames.Length) return GameMath.Clamp(cfg.HideBouncePastSteel, 0f, 1f);
            }
            return Lookup(cfg.HideBounceByMetal, TierNames[tier]);
        }

        private static float Lookup(Dictionary<string, float> map, string tierName)
        {
            if (map == null || !map.TryGetValue(tierName, out float v)) return 0f;
            return GameMath.Clamp(v, 0f, 1f);
        }

        /// <summary>
        /// The bounce chance for one hit. src may be null when called from the stick gate
        /// (the projectile carries its shooter); proj null for melee. Bypass rules live here:
        /// unclassified victims, players, blunt hits, and any hit whose cause is a living
        /// non-player creature never bounce.
        /// </summary>
        public static float ChanceFor(Entity victim, DamageSource src, EntityProjectileBase proj,
            HuntingConfig cfg, float hitDamage)
        {
            if (cfg == null || !cfg.BounceEnabled) return 0f;
            BodyClass cls = ClassOf(victim, cfg);
            if (cls == BodyClass.None) return 0f;

            // ANIMAL FIGHTS BYPASS: the cause is the shooter for a projectile, the attacker
            // for melee. A living non-player cause = creature business, no bounce.
            Entity cause = proj != null ? proj.FiredBy : src?.GetCauseEntity();
            if (cause != null && !(cause is EntityPlayer)) return 0f;

            string token = proj != null
                ? MaterialTokenOf(proj.ProjectileStack?.Collectible)
                : MaterialTokenOf((src?.SourceEntity as EntityAgent)?.RightHandItemSlot?.Itemstack?.Collectible);
            int tier = TierOfMaterial(token, hitDamage, cfg);
            bool punch = cfg.PowerShotPunchesThrough
                && proj != null && proj.WatchedAttributes.GetBool("tasshunt:powershot");
            return ChanceOf(cls, tier, punch, cfg);
        }

        // ---- one roll per projectile hit, shared between the damage gate and the stick gate ----
        // The engine calls DealDamage (-> our health-behavior prefix) before DamageProjectile
        // (-> stick prefix), but the registry is deliberately order-agnostic: whichever gate
        // asks FIRST rolls, the other reads the stored verdict.

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
