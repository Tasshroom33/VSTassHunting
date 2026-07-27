// WOUND-BASED BLEED (2026-07-27 rebuild; supersedes the 2026-07-22 arrow-count
// model): any SHARP hit opens a WOUND - arrow or thrown spear impact, spear
// stab, knife/sword/axe/sickle/scythe melee. Blunt never bleeds. Each wound's
// strength scales with the hit's damage TIER (the same number the engine uses
// for armor penetration, carried on every DamageSource), so flint -> copper ->
// bronze -> iron -> steel each buys visible bleed. Wounds close on their own
// after BleedWoundSeconds - except a wound whose arrow is still EMBEDDED
// (StickyProjectiles pins it open), so sticking arrows still matter without
// being the only thing that matters.
//
// Total bleed per tick is MULTIPLICATIVE in the wound count:
//   (flat + pct-of-max-health)  x  sum(wound strengths)  x  Combo^(wounds-1)
// capped at BleedMaxWounds (default 10) - pressing the attack compounds, which
// is the point: a deer full of arrows bleeds OUT instead of jogging away.
//
// ENGINE FACTS this build relies on (decompile-verified 1.22.5):
//   - EntityProjectileBase.impactOnEntity sends DamageSource{Type, DamageTier}
//     with SourceEntity = the projectile (EntityProjectileBase.cs:328-334).
//   - Vanilla MELEE always sends Type=BluntAttack with DamageTier=GetToolTier
//     (EntityAgent.cs:445-452) - so melee sharpness is classified by the
//     attacker's held TOOL KIND (EnumTool), while properly-typed piercing/
//     slashing sources (modded weapons, creature bites) are honored directly.
//   - EntityBehaviorHealth.OnEntityReceiveDamage is the one per-hit funnel for
//     every entity that has health; our postfix sees the FINAL damage after
//     other handlers (a zeroed hit opens no wound).
//
// The VISUAL half lives in BloodVisuals.cs and keys off the same watched
// attributes as before: "thbleed" (wound count), "thbleedtick", "thbleeddmg".
// Time lane: real seconds - combat pacing is player-experience pacing.

using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace TassHunting
{
    /// <summary>The bleed formulas, pure and harness-testable.</summary>
    public static class WoundMath
    {
        /// <summary>One wound's strength: class weight scaled by the hit's damage tier.</summary>
        public static float Strength(float classWeight, int tier, float tierStep)
        {
            return classWeight * (1f + tierStep * Math.Max(0, tier));
        }

        /// <summary>
        /// Damage per tick for a whole wound set. Hybrid base (flat + % of max health) keeps one
        /// curve honest for hares and bears; the combo power is the multiplicative payoff for
        /// landing MORE sharp hits, capped at comboCap wounds.
        /// </summary>
        public static float TotalPerTick(float flatPerTick, float pctMaxHealthPerTick, float maxHealth,
            float strengthSum, int woundCount, float comboMult, int comboCap)
        {
            if (woundCount <= 0 || strengthSum <= 0f) return 0f;
            float baseTick = flatPerTick + pctMaxHealthPerTick / 100f * maxHealth;
            int comboWounds = Math.Min(woundCount, Math.Max(1, comboCap));
            return baseTick * strengthSum * (float)Math.Pow(comboMult, comboWounds - 1);
        }
    }

    /// <summary>
    /// One entity's open wounds. Pure list logic (no world access) so the harness can hammer it:
    /// capped size, soonest-ending wound replaced at the cap, pinned wounds (arrow still embedded)
    /// never expire until released.
    /// </summary>
    public class WoundLedger
    {
        public sealed class Wound
        {
            public float Strength;
            public long ExpiresAtMs;
            public long PinProjectileId; // 0 = not pinned; else the embedded projectile's entity id
        }

        private readonly List<Wound> _wounds = new List<Wound>();

        public int Count => _wounds.Count;

        public float StrengthSum
        {
            get
            {
                float sum = 0f;
                for (int i = 0; i < _wounds.Count; i++) sum += _wounds[i].Strength;
                return sum;
            }
        }

        public void Add(float strength, long expiresAtMs, long pinProjectileId, int maxWounds)
        {
            if (_wounds.Count >= Math.Max(1, maxWounds))
            {
                // Replace the wound that would end soonest; a pinned wound effectively never ends.
                int worst = 0;
                long worstEnd = long.MaxValue;
                for (int i = 0; i < _wounds.Count; i++)
                {
                    long end = _wounds[i].PinProjectileId != 0 ? long.MaxValue : _wounds[i].ExpiresAtMs;
                    if (end < worstEnd) { worstEnd = end; worst = i; }
                }
                _wounds.RemoveAt(worst);
            }
            _wounds.Add(new Wound { Strength = strength, ExpiresAtMs = expiresAtMs, PinProjectileId = pinProjectileId });
        }

        /// <summary>
        /// Reconcile pins against the projectiles actually still embedded. A wound whose arrow
        /// worked loose (or whose projectile no longer exists) unpins and gets a fresh closing
        /// window - the arrow tearing out does not stop the bleeding on the spot.
        /// </summary>
        public void SyncPins(HashSet<long> stuckProjectileIds, long freshExpiryMs)
        {
            for (int i = 0; i < _wounds.Count; i++)
            {
                var w = _wounds[i];
                if (w.PinProjectileId != 0 && !stuckProjectileIds.Contains(w.PinProjectileId))
                {
                    w.PinProjectileId = 0;
                    w.ExpiresAtMs = freshExpiryMs;
                }
            }
        }

        /// <summary>Projectile ids currently pinning wounds open (for liveness checks).</summary>
        public List<long> SnapshotPins()
        {
            var pins = new List<long>();
            for (int i = 0; i < _wounds.Count; i++)
                if (_wounds[i].PinProjectileId != 0) pins.Add(_wounds[i].PinProjectileId);
            return pins;
        }

        /// <summary>Drop expired unpinned wounds. Returns true if anything closed.</summary>
        public bool ExpireStep(long nowMs)
        {
            bool removed = false;
            for (int i = _wounds.Count - 1; i >= 0; i--)
            {
                if (_wounds[i].PinProjectileId == 0 && _wounds[i].ExpiresAtMs <= nowMs)
                {
                    _wounds.RemoveAt(i);
                    removed = true;
                }
            }
            return removed;
        }

        public void Clear() => _wounds.Clear();
    }

    public class BleedSystem : ModSystem
    {
        private class State
        {
            public Entity Ent;
            public WoundLedger Ledger = new WoundLedger();
            public long NextTickMs;
        }

        private static readonly Dictionary<long, State> Active = new Dictionary<long, State>();
        private ICoreServerAPI sapi;

        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            api.Event.RegisterGameTickListener(Tick, 1000);
        }

        public override void Dispose()
        {
            lock (Active) Active.Clear();
            base.Dispose();
        }

        /// <summary>
        /// Classify a landed hit and open a wound if it was sharp. Called from the
        /// OnEntityReceiveDamage postfix with the FINAL damage (post armor/shields).
        /// </summary>
        public static void OnSharpHit(Entity victim, DamageSource src, float damage)
        {
            var cfg = HuntingModSystem.Cfg;
            if (cfg == null || !cfg.BleedEnabled || victim == null || src == null) return;
            if (victim.World?.Side != EnumAppSide.Server || !victim.Alive) return;
            if (damage < cfg.BleedMinDamage) return;
            // Our own ticks (Internal/Injury), hunger, poison, healing: never re-proc.
            if (src.Source == EnumDamageSource.Internal || src.Type == EnumDamageType.Heal) return;
            if (!cfg.BleedAffectsPlayers && victim is EntityPlayer) return;
            if (!HuntingModSystem.CanBleed(victim)) return;
            if (victim.GetBehavior<EntityBehaviorHealth>() == null) return;

            bool typedSharp = src.Type == EnumDamageType.PiercingAttack || src.Type == EnumDamageType.SlashingAttack;

            float weight;
            long pinId = 0;
            if (src.SourceEntity is EntityProjectileBase proj)
            {
                if (!typedSharp) return; // blunt projectiles (stones, beenades) do not bleed
                EnumTool? ptool = proj.ProjectileStack?.Collectible?.Tool;
                bool heavy = ptool == EnumTool.Spear || ptool == EnumTool.Javelin || ptool == EnumTool.Pike;
                weight = heavy ? cfg.BleedThrownSpearWoundWeight : cfg.BleedArrowWoundWeight;
                pinId = proj.EntityId; // if it sticks, StickyProjectiles pins this wound open
            }
            else
            {
                // Vanilla melee is ALWAYS typed Blunt (engine fact above), so sharpness comes from
                // the attacker's held tool kind; properly-typed hits (modded weapons, animal bites)
                // pass on their type alone. Fists, clubs, falls, fire: rejected.
                EnumTool? tool = (src.SourceEntity as EntityAgent)?.RightHandItemSlot?.Itemstack?.Collectible?.Tool;
                bool pierceTool = tool == EnumTool.Spear || tool == EnumTool.Pike || tool == EnumTool.Javelin;
                bool slashTool = tool == EnumTool.Knife || tool == EnumTool.Sword || tool == EnumTool.Axe
                              || tool == EnumTool.Sickle || tool == EnumTool.Scythe;
                if (!typedSharp && !pierceTool && !slashTool) return;
                bool pierce = pierceTool || (!slashTool && src.Type == EnumDamageType.PiercingAttack);
                weight = pierce ? cfg.BleedSpearStabWoundWeight : cfg.BleedSlashWoundWeight;
            }

            float strength = WoundMath.Strength(weight, src.DamageTier, cfg.BleedTierStep);
            long now = victim.World.ElapsedMilliseconds;
            lock (Active)
            {
                if (!Active.TryGetValue(victim.EntityId, out var st))
                {
                    st = new State { Ent = victim, NextTickMs = now + (long)(cfg.BleedTickSeconds * 1000f) };
                    Active[victim.EntityId] = st;
                }
                st.Ledger.Add(strength, now + (long)(cfg.BleedWoundSeconds * 1000f), pinId, cfg.BleedMaxWounds);
                victim.WatchedAttributes.SetInt("thbleed", st.Ledger.Count);
            }
        }

        /// <summary>
        /// StickyProjectiles reports the projectile entity ids currently embedded in a target.
        /// Idempotent: wounds for those ids stay pinned open; wounds whose arrow left get a fresh
        /// closing window. Called on every stick/release/timeout recount.
        /// </summary>
        public static void SyncArrowPins(Entity target, HashSet<long> stuckProjectileIds)
        {
            var cfg = HuntingModSystem.Cfg;
            if (cfg == null || target == null) return;
            long now = target.World?.ElapsedMilliseconds ?? 0;
            lock (Active)
            {
                if (Active.TryGetValue(target.EntityId, out var st))
                    st.Ledger.SyncPins(stuckProjectileIds, now + (long)(cfg.BleedWoundSeconds * 1000f));
            }
        }

        /// <summary>Current bleeders (entity + wound count). Copies under the lock.</summary>
        public static List<(Entity ent, int stacks)> SnapshotActive()
        {
            lock (Active)
            {
                var list = new List<(Entity, int)>(Active.Count);
                foreach (var kv in Active)
                    if (kv.Value.Ent != null && kv.Value.Ledger.Count > 0)
                        list.Add((kv.Value.Ent, kv.Value.Ledger.Count));
                return list;
            }
        }

        private void Tick(float dt)
        {
            var cfg = HuntingModSystem.Cfg;
            if (cfg == null || !cfg.BleedEnabled) return;
            long now = sapi.World.ElapsedMilliseconds;
            List<long> retire = null;

            lock (Active)
            {
                foreach (var kv in Active)
                {
                    var st = kv.Value;
                    if (st.Ent == null || !st.Ent.Alive || st.Ledger.Count == 0)
                    {
                        try { st.Ent?.WatchedAttributes.SetInt("thbleed", 0); } catch { }
                        (retire = retire ?? new List<long>()).Add(kv.Key); continue;
                    }

                    // Belt for arrows that hit but never stuck (and so never got a release recount):
                    // a pin whose projectile entity no longer exists unpins into a normal wound.
                    st.Ledger.SyncPins(CollectLiveProjectiles(st), now + (long)(cfg.BleedWoundSeconds * 1000f));

                    if (st.Ledger.ExpireStep(now))
                        st.Ent.WatchedAttributes.SetInt("thbleed", st.Ledger.Count);
                    if (st.Ledger.Count == 0)
                    {
                        st.Ent.WatchedAttributes.SetInt("thbleed", 0);
                        (retire = retire ?? new List<long>()).Add(kv.Key); continue;
                    }

                    if (now < st.NextTickMs) continue;
                    st.NextTickMs = now + (long)(cfg.BleedTickSeconds * 1000f);

                    var hb = st.Ent.GetBehavior<EntityBehaviorHealth>();
                    if (hb == null) { (retire = retire ?? new List<long>()).Add(kv.Key); continue; }

                    float total = WoundMath.TotalPerTick(cfg.BleedStaticPerTick, cfg.BleedPctMaxHealthPerTick,
                        hb.MaxHealth, st.Ledger.StrengthSum, st.Ledger.Count, cfg.BleedComboMultiplier, cfg.BleedMaxWounds);

                    // DEDICATED SPLATTER SIGNAL (0.9.3): the client keys DoT splatter off this
                    // monotonic counter, NOT the engine's onHurt bump (that path swallows ticks
                    // inside the 500ms invuln window - decompile-verified Entity.cs:935-953).
                    int tickN = st.Ent.WatchedAttributes.GetInt("thbleedtick", 0) + 1;
                    st.Ent.WatchedAttributes.SetInt("thbleedtick", tickN);
                    st.Ent.WatchedAttributes.SetFloat("thbleeddmg", total);
                    // Internal/Injury never re-procs OnSharpHit (gated there).
                    st.Ent.ReceiveDamage(new DamageSource { Source = EnumDamageSource.Internal, Type = EnumDamageType.Injury }, total);
                }
                if (retire != null) foreach (long id in retire) Active.Remove(id);
            }
        }

        private readonly HashSet<long> liveProjectiles = new HashSet<long>();

        /// <summary>Which of this entity's pinning projectiles still exist in the world.
        /// (StickyProjectiles' recount handles the normal release path; this catches arrows
        /// that hit without sticking, so their wounds fall back to a normal closing timer.)</summary>
        private HashSet<long> CollectLiveProjectiles(State st)
        {
            liveProjectiles.Clear();
            foreach (long id in st.Ledger.SnapshotPins())
            {
                if (sapi.World.GetEntityById(id) != null) liveProjectiles.Add(id);
            }
            return liveProjectiles;
        }

        /// <summary>Active wound count (narrator/debug + blood visuals).</summary>
        public static int StacksOn(long entityId)
        {
            lock (Active) return Active.TryGetValue(entityId, out var st) ? st.Ledger.Count : 0;
        }
    }

    /// <summary>
    /// The one hit funnel: every damage event on an entity with health passes through here, with
    /// the final (post-handler) damage value. Sharp ones open wounds.
    /// </summary>
    [HarmonyPatch(typeof(EntityBehaviorHealth), nameof(EntityBehaviorHealth.OnEntityReceiveDamage))]
    public static class Patch_BleedOnSharpHit
    {
        public static void Postfix(EntityBehaviorHealth __instance, DamageSource damageSource, float damage)
        {
            try { BleedSystem.OnSharpHit(__instance.entity, damageSource, damage); }
            catch (Exception) { /* bleed must never break damage handling */ }
        }
    }
}
