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
// "thbleedsecs" (2026-07-29) is added for PLAYERS only: seconds until the last
// wound closes, which is what the on-screen bleeding box counts down.
// Time lane: real seconds - combat pacing is player-experience pacing.
//
// DRESSINGS STOP IT (2026-07-29): a finished bandage or poultice closes every
// open wound on the target at once - see OnHealItemApplied for the engine
// contract that identifies "a dressing was just applied" without naming a
// single item code.
//
// WHAT SOFTENS A WOUND (2026-08-03, three rules that all key off the same hit):
//   ARMOR is measured, not queried. A prefix records the damage as it ARRIVED;
//   by the time our postfix runs, EntityBehaviorHealth has put it through
//   ApplyOnDamageDelegates, which is where vanilla armor and shields subtract
//   (ModSystemWearableStats.handleDamaged). The gap between the two IS the
//   protection, whatever produced it - vanilla armor, a shield, or a mod we
//   have never heard of. Armor that soaks most of a blow turns the edge
//   outright; otherwise the wound is only as big as what got through.
//   WHO SWUNG gets one multiplier per attacker class (rust beings, wild
//   creatures), 0 = that class never opens a wound. Classified by TAG through
//   HuntingModSystem.IsRustCreature, never by entity code.
//   SITTING STILL for an unbroken stretch halves both the bleed damage and the
//   wound clock while you stay down - see SitRule.

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
        /// The health the %-of-max-health term is allowed to see (owner design 2026-08-28):
        /// effHP = C * tanh(HP/C). For HP well under C, tanh(x) ~ x and this IS the raw health -
        /// vanilla's 15-66 range shifts by under a tenth - while a giant saturates toward C, so
        /// time-to-bleed-out grows with size instead of cancelling out of the arithmetic (the
        /// old rate was proportional to HP, making TTK constant: an 800 hp sauropod died to two
        /// flint spears in the same 2.5 minutes as a wolf). The size LADDER (wound seconds,
        /// odds) deliberately stays on raw health - only the damage rate levels off.
        /// 0 ceiling = off.
        /// </summary>
        public static float EffectiveHealth(float maxHealth, float ceiling)
        {
            if (ceiling <= 0f || maxHealth <= 0f) return maxHealth;
            return ceiling * (float)Math.Tanh(maxHealth / ceiling);
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
    /// SITTING STILL, as pure timestamp math so the harness can prove it without a client
    /// pressing the sit key. The rule: you must stay seated for an unbroken stretch before it
    /// helps at all, and standing up both ends the help instantly and ZEROES the credit, so the
    /// next sit starts the count over. The help is continuous (a rate, not a one-time cut),
    /// which is why bobbing up and down can never beat simply staying down - there is nothing
    /// to re-trigger.
    /// </summary>
    public static class SitRule
    {
        /// <summary>When the current unbroken sit began. 0 = not seated (credit lost).</summary>
        public static long Track(long seatedSinceMs, bool seated, long nowMs)
        {
            if (!seated) return 0L;
            return seatedSinceMs == 0 ? nowMs : seatedSinceMs;
        }

        /// <summary>Has this one unbroken sit lasted long enough to start helping?</summary>
        public static bool Helps(long seatedSinceMs, long nowMs, float requiredSeconds)
        {
            if (seatedSinceMs == 0) return false;
            return nowMs - seatedSinceMs >= (long)(Math.Max(0f, requiredSeconds) * 1000f);
        }

        /// <summary>
        /// Extra wound-clock milliseconds to burn for elapsedMs of real time. durationMult 0.5
        /// means the clock runs at double speed, so one real second burns one EXTRA second on
        /// top of the one it already burns. Clamped so a hand-edited 0 cannot divide by zero.
        /// </summary>
        public static long ExtraCloseMs(long elapsedMs, float durationMult)
        {
            if (elapsedMs <= 0) return 0L;
            float m = Math.Max(0.05f, Math.Min(1f, durationMult));
            return (long)(elapsedMs * (1f / m - 1f));
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

        /// <summary>
        /// Whole seconds until the LAST open wound closes by itself. 0 = nothing open.
        /// -1 = at least one wound is PINNED by an embedded projectile, which never times out
        /// while the arrow is in - the HUD shows that as "arrow still in" instead of a countdown.
        /// </summary>
        public int SecondsLeft(long nowMs)
        {
            if (_wounds.Count == 0) return 0;
            long latest = long.MinValue;
            for (int i = 0; i < _wounds.Count; i++)
            {
                if (_wounds[i].PinProjectileId != 0) return -1;
                if (_wounds[i].ExpiresAtMs > latest) latest = _wounds[i].ExpiresAtMs;
            }
            long ms = latest - nowMs;
            if (ms <= 0) return 0;
            return (int)((ms + 999) / 1000); // round up, so "1s left" never reads as 0
        }

        /// <summary>
        /// Pull every unpinned wound's closing time forward - sitting still, pressing on it.
        /// A PINNED wound has no closing time to pull; the arrow has to come out first.
        /// Returns true if anything moved.
        /// </summary>
        public bool Accelerate(long ms)
        {
            if (ms <= 0) return false;
            bool moved = false;
            for (int i = 0; i < _wounds.Count; i++)
            {
                if (_wounds[i].PinProjectileId != 0) continue;
                _wounds[i].ExpiresAtMs -= ms;
                moved = true;
            }
            return moved;
        }

        /// <summary>
        /// Re-set every unpinned wound to close at the SAME moment, scaled by how many are open
        /// (2026-08-22): baseMs x multiplier^(count-1). Each new wound therefore lengthens the
        /// whole set rather than only carrying its own clock, which is what "more wounds bleed
        /// longer" has to mean when wounds are interchangeable. Called on every open and on
        /// every unpin, so the set always agrees with its own size. A pinned wound has no
        /// closing time to set - the arrow has to come out first.
        /// </summary>
        public void RefreshExpiry(long nowMs, long baseMs, float multiplier)
        {
            int open = _wounds.Count;
            if (open <= 0) return;
            float mult = Math.Max(1f, multiplier);
            long span = (long)(baseMs * Math.Pow(mult, open - 1));
            for (int i = 0; i < _wounds.Count; i++)
            {
                if (_wounds[i].PinProjectileId != 0) continue;
                _wounds[i].ExpiresAtMs = nowMs + span;
            }
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
            public long SeatedSinceMs;  // 0 = not seated; else when this unbroken sit began
            public long LastStepMs;     // world clock at our last step, for real elapsed time
        }

        private static readonly Dictionary<long, State> Active = new Dictionary<long, State>();
        private ICoreServerAPI sapi;

        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            api.Event.RegisterGameTickListener(Tick, 1000);
            // Entities ENTERING the world can only carry stale bleed state - scrub it (see
            // OnEntityEnteredWorld). Both events are needed: chunk entities re-enter through
            // OnEntityLoaded, a rejoining player's entity re-enters through SpawnEntity with a
            // FRESH entity id (decompile-verified ServerMain.SpawnEntity_internal), so it only
            // ever fires OnEntitySpawn.
            api.Event.OnEntityLoaded += OnEntityEnteredWorld;
            api.Event.OnEntitySpawn += OnEntityEnteredWorld;
        }

        public override void Dispose()
        {
            lock (Active) Active.Clear();
            base.Dispose();
        }

        /// <summary>
        /// Is this a hit the bounce may judge: sharp by damage type, or sharp by the
        /// attacker's held tool (vanilla melee is always typed Blunt - the engine fact
        /// OnSharpHit documents). Blunt weapons, our own bleed ticks and heals never bounce.
        /// </summary>
        public static bool IsSharpHit(DamageSource src)
        {
            if (src == null || src.Type == EnumDamageType.Heal || src.Source == EnumDamageSource.Internal) return false;
            bool typedSharp = src.Type == EnumDamageType.PiercingAttack || src.Type == EnumDamageType.SlashingAttack;
            if (src.SourceEntity is EntityProjectileBase) return typedSharp;
            EnumTool? tool = (src.SourceEntity as EntityAgent)?.RightHandItemSlot?.Itemstack?.Collectible?.Tool;
            return typedSharp
                || tool == EnumTool.Spear || tool == EnumTool.Pike || tool == EnumTool.Javelin
                || tool == EnumTool.Knife || tool == EnumTool.Sword || tool == EnumTool.Axe
                || tool == EnumTool.Sickle || tool == EnumTool.Scythe;
        }

        /// <summary>
        /// Classify a landed hit and open a wound if it was sharp. Called from the
        /// OnEntityReceiveDamage postfix with the FINAL damage (post armor/shields) and with
        /// incomingDamage as the hit ARRIVED (pre armor/shields), the pair that tells us how
        /// much protection actually did.
        /// </summary>
        public static void OnSharpHit(Entity victim, DamageSource src, float damage, float incomingDamage)
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
                // A CLAW, BITE OR STING now has its own weight (2026-08-22). It used to borrow
                // the player's knife or spear number, which welded the two halves of the balance
                // together - doubling weapon weights for hunting also doubled every wolf bite.
                // Wielding nothing sharp is what tells them apart: a creature carries no tool and
                // its damage TYPE alone made the hit sharp.
                if (!pierceTool && !slashTool)
                {
                    weight = cfg.BleedCreatureWoundWeight;
                }
                else
                {
                    bool pierce = pierceTool || (!slashTool && src.Type == EnumDamageType.PiercingAttack);
                    weight = pierce ? cfg.BleedSpearStabWoundWeight : cfg.BleedSlashWoundWeight;
                }
            }

            // (The bounce - thick hide / armor, 0.14.39 - is decided BEFORE this runs, in
            // Patch_BleedOnSharpHit.Prefix: a bounced hit skips the whole health path, so a
            // hit that reaches here landed for real and always may wound.)

            // ---- WHO SWUNG: one multiplier per attacker class, 0 = that class never opens a
            // wound. GetCauseEntity is the SHOOTER for a projectile (CauseEntity = FiredBy,
            // EntityProjectileBase.cs:332) and the attacker for melee, so a player's arrow is
            // classified as the PLAYER and not as the arrow.
            float classMult = AttackerWoundMult(src, cfg);
            if (classMult <= 0f) return;
            weight *= classMult;

            // ---- WHAT STOPPED IT: armor measured, not queried (see the file header). Vanilla
            // rolls ONE armor slot per hit (20% head / 50% chest / 30% legs, handleDamaged:172),
            // so the absorbed share legitimately varies hit to hit - sometimes the blade finds
            // the gap. incomingDamage <= 0 means our prefix never ran (another mod's prefix
            // returned false ahead of it): that degrades to "armor changes nothing", never to a
            // free wound.
            float absorbed = 0f;
            if (incomingDamage > 0f)
                absorbed = Math.Max(0f, Math.Min(1f, 1f - damage / incomingDamage));
            if (absorbed >= cfg.BleedArmorNoWoundAbsorb) return;   // it turned the edge
            weight *= 1f - Math.Max(0f, Math.Min(1f, cfg.BleedArmorMitigation)) * absorbed;
            if (weight <= 0f) return;

            // ---- HOW MANY WOUNDS THIS HIT OPENS. A player's weapon always opens exactly one;
            // a creature rolls the odds of its own size rung, so a fox draws blood about half
            // the time and a polar bear opens two wounds half the time. Rolled OUTSIDE the lock.
            var rng = victim.World.Rand;
            int woundCount = RollWoundCount(src.GetCauseEntity(), cfg,
                (float)rng.NextDouble(), (float)rng.NextDouble());
            if (woundCount <= 0) return;   // it broke skin but never opened

            float strength = WoundMath.Strength(weight, src.DamageTier, cfg.BleedTierStep);
            long now = victim.World.ElapsedMilliseconds;
            // The clock belongs to the BODY that is bleeding, not to what hit it.
            long clockMs = (long)(WoundSecondsFor(victim, cfg) * 1000f);
            lock (Active)
            {
                if (!Active.TryGetValue(victim.EntityId, out var st))
                {
                    st = new State { Ent = victim, NextTickMs = now + (long)(cfg.BleedTickSeconds * 1000f) };
                    Active[victim.EntityId] = st;
                }
                for (int i = 0; i < woundCount; i++)
                    st.Ledger.Add(strength, now + clockMs, pinId, cfg.BleedMaxWounds);
                // Every open wound now runs to the same, longer moment - see RefreshExpiry.
                st.Ledger.RefreshExpiry(now, clockMs, cfg.BleedLengthMultiplier);
                Publish(victim, st.Ledger, now);
            }

            // Stamp WHO opened this wound onto the victim, durably (a UID string that survives the
            // attacker logging off or dying first). A bleed tick is anonymous - it carries no
            // attacker - so a bleed-out death is causeless to the engine and any "who killed them"
            // read at death would miss the bleeder. This is the ONE moment the attacker's identity
            // exists, so another mod (e.g. TassFactions bounties) can credit a bleed-out kill by
            // reading this stamp while "thbleed" is still non-zero. Players only; cleared when the
            // bleeding stops (PublishNone). GetCauseEntity is the shooter for a projectile, so an
            // arrow bleed is credited to the player, not the arrow.
            if (victim is EntityPlayer && src.GetCauseEntity() is EntityPlayer bleeder && !string.IsNullOrEmpty(bleeder.PlayerUID))
            {
                var wa = victim.WatchedAttributes;
                wa.SetString("tasshunt:bleedByUid", bleeder.PlayerUID);
                wa.SetString("tasshunt:bleedByName", bleeder.Player?.PlayerName ?? bleeder.GetName());
                wa.SetLong("tasshunt:bleedByMs", now);
            }
        }

        /// <summary>A creature's own max health, the key the whole size ladder turns on.
        /// 0 when it has no health behavior, which reads as the smallest rung.</summary>
        public static float MaxHealthOf(Entity ent) => ent?.GetBehavior<EntityBehaviorHealth>()?.MaxHealth ?? 0f;

        /// <summary>
        /// The rung a body sits on. MaxHealth is the inclusive upper bound; a rung of 0 or less
        /// is the open-topped last one. An empty ladder returns null and the caller falls back
        /// to the single BleedWoundSeconds, so a hand-cleared table degrades to the pre-2026-08-22
        /// behaviour rather than to no bleeding.
        /// </summary>
        public static BleedSizeTier TierFor(BleedSizeTier[] tiers, float maxHealth)
        {
            if (tiers == null || tiers.Length == 0) return null;
            for (int i = 0; i < tiers.Length; i++)
            {
                var t = tiers[i];
                if (t == null) continue;
                if (t.MaxHealth <= 0f || maxHealth <= t.MaxHealth) return t;
            }
            return tiers[tiers.Length - 1];
        }

        /// <summary>
        /// How long a wound stays open on THIS body. Players are one size and take a flat
        /// number; rust clots on its own ladder (all six drifter tiers weigh the same 140kg, so
        /// only health can tell them apart); everything else rides the size ladder.
        /// </summary>
        public static float WoundSecondsFor(Entity victim, HuntingConfig cfg)
        {
            if (cfg == null) return 45f;
            if (victim is EntityPlayer) return cfg.BleedPlayerWoundSeconds;
            float hp = MaxHealthOf(victim);
            if (HuntingModSystem.IsRustCreature(victim) && cfg.BleedRustTiers != null)
            {
                for (int i = 0; i < cfg.BleedRustTiers.Length; i++)
                {
                    var r = cfg.BleedRustTiers[i];
                    if (r == null) continue;
                    if (r.MaxHealth <= 0f || hp <= r.MaxHealth) return r.Seconds;
                }
            }
            var tier = TierFor(cfg.BleedSizeTiers, hp);
            return tier != null ? tier.Seconds : cfg.BleedWoundSeconds;
        }

        /// <summary>
        /// How many wounds this hit opens: 0, 1 or 2. A PLAYER's weapon that BITES always opens
        /// exactly one - a hunter's arrow failing to bleed a deer at random reads as the mod
        /// being broken, with no attacker size on screen to explain it. (Owner revision
        /// 2026-08-28: the upstream hide-glance gate MAY refuse the whole hit, but only past
        /// bear size, where the target on screen IS the explanation - and the arrow visibly
        /// bounces instead of silently not bleeding.) A creature rolls its rung's odds, so
        /// size shows up as how OFTEN it draws blood rather than how much each wound hurts.
        /// A hit with no attacker at all (a trap, a fall onto spikes) always wounds.
        /// </summary>
        public static int RollWoundCount(Entity attacker, HuntingConfig cfg, float roll1, float roll2)
        {
            if (cfg == null) return 1;
            if (attacker == null || attacker is EntityPlayer) return 1;
            var tier = TierFor(cfg.BleedSizeTiers, MaxHealthOf(attacker));
            if (tier == null) return 1;
            int n = roll1 < tier.Odds ? 1 : 0;
            if (n > 0 && roll2 < tier.SecondOdds) n = 2;
            return n;
        }

        /// <summary>
        /// Wound size multiplier for who dealt the hit: people keep their own weapon weights,
        /// rust beings and wild creatures each get their own dial (0 = never wounds). A hit with
        /// no attacker at all - a trap, a world hazard - is left alone.
        /// </summary>
        private static float AttackerWoundMult(DamageSource src, HuntingConfig cfg)
        {
            Entity cause = src.GetCauseEntity();
            if (cause == null || cause is EntityPlayer) return 1f;
            if (HuntingModSystem.IsRustCreature(cause)) return cfg.BleedRustAttackWoundMult;
            return cfg.BleedCreatureAttackWoundMult;
        }

        /// <summary>
        /// What the bleeding box on the client reads, straight off the player's own entity:
        /// "thbleed" (open wounds) and "thbleedsecs" (seconds until the last one closes, -1 while
        /// an embedded arrow pins one open). The countdown is written for PLAYERS ONLY and only
        /// when the number actually changed - no animal needs it, and a per-second attribute write
        /// on every bleeding animal would be one sync packet per animal per second for nothing.
        /// </summary>
        private static void Publish(Entity ent, WoundLedger led, long nowMs)
        {
            if (ent == null) return;
            var wa = ent.WatchedAttributes;
            if (wa == null) return;
            int wounds = led.Count;
            if (wa.GetInt("thbleed", 0) != wounds) wa.SetInt("thbleed", wounds);
            if (!(ent is EntityPlayer)) return;
            int secs = led.SecondsLeft(nowMs);
            if (wa.GetInt("thbleedsecs", 0) != secs) wa.SetInt("thbleedsecs", secs);
        }

        /// <summary>
        /// An entity entering the world has NO open wounds: the ledger is server-session
        /// memory, and this session never wounded it. But the ledger's published mirror
        /// ("thbleed" and friends) rides WatchedAttributes into the SAVE, so an entity that
        /// was bleeding when its world was exited - or its chunk unloaded - comes back wearing
        /// "still bleeding" state that nothing would ever clear: a bleed box that never counts
        /// down, no damage, and (before ClearWounds published unconditionally) no bandage
        /// could close it (field report 2026-08-18). Wiping on entry fixes worlds already
        /// poisoned by older builds, not just future exits. Any ledger entry under this id
        /// belongs to a previous session's dead object - drop that too.
        /// </summary>
        private static void OnEntityEnteredWorld(Entity ent)
        {
            var wa = ent?.WatchedAttributes;
            if (wa == null) return;
            lock (Active)
            {
                if (Active.TryGetValue(ent.EntityId, out var st) && !ReferenceEquals(st.Ent, ent))
                    Active.Remove(ent.EntityId);
            }
            if (wa.GetInt("thbleed", 0) != 0) wa.SetInt("thbleed", 0);
            if (wa.GetInt("thbleedsecs", 0) != 0) wa.SetInt("thbleedsecs", 0);
            // The tick counter and per-tick damage are only meaningful mid-session; removal
            // is synced (SyncedTreeAttribute.RemoveAttribute marks the tree dirty).
            if (wa.HasAttribute("thbleedtick")) wa.RemoveAttribute("thbleedtick");
            if (wa.HasAttribute("thbleeddmg")) wa.RemoveAttribute("thbleeddmg");
            if (wa.HasAttribute("tasshunt:bleedByUid"))
            {
                wa.RemoveAttribute("tasshunt:bleedByUid");
                wa.RemoveAttribute("tasshunt:bleedByName");
                wa.RemoveAttribute("tasshunt:bleedByMs");
            }
            // The player-credit stamp persists with the save but its clock does not: a new
            // session's ElapsedMilliseconds starts over, so any stamp from a previous session
            // is meaningless - drop it on entry like the bleed mirror above.
            if (ent.Attributes.HasAttribute("tasshunt:phitMs")) ent.Attributes.RemoveAttribute("tasshunt:phitMs");
        }

        /// <summary>Zero the published state for an entity that has stopped bleeding.</summary>
        private static void PublishNone(Entity ent)
        {
            if (ent?.WatchedAttributes == null) return;
            if (ent.WatchedAttributes.GetInt("thbleed", 0) != 0) ent.WatchedAttributes.SetInt("thbleed", 0);
            if (ent is EntityPlayer && ent.WatchedAttributes.GetInt("thbleedsecs", 0) != 0)
                ent.WatchedAttributes.SetInt("thbleedsecs", 0);
            // Bleeding stopped: drop the "who bled me" stamp so a later unrelated death can't be
            // mis-credited to the old bleeder (a reader also gates on thbleed>0, but clearing keeps
            // the attribute honest).
            if (ent.WatchedAttributes.HasAttribute("tasshunt:bleedByUid"))
            {
                ent.WatchedAttributes.RemoveAttribute("tasshunt:bleedByUid");
                ent.WatchedAttributes.RemoveAttribute("tasshunt:bleedByName");
                ent.WatchedAttributes.RemoveAttribute("tasshunt:bleedByMs");
            }
        }

        /// <summary>
        /// Close EVERY open wound on this entity at once - a dressing went on. Returns true if
        /// there was anything to close. An arrow still embedded does NOT re-open the wound: the
        /// bandage went on over it, and only a fresh sharp hit opens a new one.
        /// </summary>
        public static bool ClearWounds(Entity victim)
        {
            if (victim == null) return false;
            bool had;
            lock (Active)
            {
                had = Active.TryGetValue(victim.EntityId, out var st) && st.Ledger.Count > 0;
                if (had) st.Ledger.Clear();
                Active.Remove(victim.EntityId);
            }
            // Publish unconditionally (engine calls stay outside the lock): a save that
            // carried stale "still bleeding" attributes into this session has no ledger
            // entry, but the dressing must still wipe the phantom the player is looking at.
            PublishNone(victim);
            return had;
        }

        /// <summary>
        /// A bandage or poultice finished going on - stop the bleeding outright.
        ///
        /// ENGINE CONTRACT (decompile-verified 1.22.5): CollectibleBehaviorHealingItem.
        /// OnHeldInteractStop sends ONE DamageSource{Source=Internal, Type=Heal,
        /// Duration=EffectDurationSec, TicksPerDuration=Ticks} at the target the moment the
        /// application completes, server side only. EntityBehaviorHealth turns that into a
        /// heal-over-time effect, and every later heal TICK re-enters the same funnel with a bare
        /// DamageSource whose Duration is ZERO (EntityBehaviorHealth.ProcessDoTEffects builds it
        /// fresh) - so "Type is Heal AND Duration > 0" is exactly "a dressing was just applied",
        /// exactly once, and the ten seconds of healing that follow do not re-fire it. Keying on
        /// the engine contract instead of on item codes means every vanilla bandage and poultice
        /// AND any modded healing item built on the vanilla behavior all stop bleeding.
        /// </summary>
        public static void OnHealItemApplied(Entity victim, DamageSource src)
        {
            var cfg = HuntingModSystem.Cfg;
            if (cfg == null || !cfg.BleedStoppedByHealingItems) return;
            if (victim == null || src == null) return;
            if (victim.World?.Side != EnumAppSide.Server) return;
            if (src.Type != EnumDamageType.Heal || src.Duration <= TimeSpan.Zero) return;
            ClearWounds(victim);
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
            long clockMs = (long)(WoundSecondsFor(target, cfg) * 1000f);
            lock (Active)
            {
                if (Active.TryGetValue(target.EntityId, out var st))
                {
                    st.Ledger.SyncPins(stuckProjectileIds, now + clockMs);
                    // An arrow tearing out leaves the set the size it is; re-scale so the freed
                    // wound closes on the same schedule as its neighbours.
                    st.Ledger.RefreshExpiry(now, clockMs, cfg.BleedLengthMultiplier);
                }
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
                    // Despawned covers a chunk that unloaded and a player who logged off
                    // mid-bleed (decompile-verified: every DespawnEntity path sets it): the
                    // object in our dict is detached from the world - its serialized copy is
                    // already frozen - so ticking damage into it does nothing but waste work.
                    // Its stale saved attributes are scrubbed by OnEntityEnteredWorld when it
                    // comes back.
                    if (st.Ent == null || !st.Ent.Alive || st.Ledger.Count == 0
                        || st.Ent.State == EnumEntityState.Despawned)
                    {
                        try { PublishNone(st.Ent); } catch { }
                        (retire = retire ?? new List<long>()).Add(kv.Key); continue;
                    }

                    // Belt for arrows that hit but never stuck (and so never got a release recount):
                    // a pin whose projectile entity no longer exists unpins into a normal wound.
                    st.Ledger.SyncPins(CollectLiveProjectiles(st),
                        now + (long)(WoundSecondsFor(st.Ent, cfg) * 1000f));

                    // SITTING STILL: after an unbroken BleedSitSecondsRequired seated, the bleed
                    // damage and the wound clock both run at their sit multipliers for as long as
                    // you stay down. Standing up reverts both instantly AND zeroes the credit, so
                    // the next sit starts the count over. Measured off the world clock rather than
                    // the tick's dt, so a lagging or catching-up server cannot pay double.
                    long stepMs = st.LastStepMs == 0 ? 0 : now - st.LastStepMs;
                    st.LastStepMs = now;
                    bool sitHelps = false;
                    if (cfg.BleedSittingHelps)
                    {
                        st.SeatedSinceMs = SitRule.Track(st.SeatedSinceMs, HuntingModSystem.IsSeated(st.Ent), now);
                        sitHelps = SitRule.Helps(st.SeatedSinceMs, now, cfg.BleedSitSecondsRequired);
                        if (sitHelps) st.Ledger.Accelerate(SitRule.ExtraCloseMs(stepMs, cfg.BleedSitDurationMult));
                    }
                    else st.SeatedSinceMs = 0;

                    st.Ledger.ExpireStep(now);
                    if (st.Ledger.Count == 0)
                    {
                        PublishNone(st.Ent);
                        (retire = retire ?? new List<long>()).Add(kv.Key); continue;
                    }
                    // Every second: wound count if it moved, plus the player's countdown.
                    Publish(st.Ent, st.Ledger, now);

                    if (now < st.NextTickMs) continue;
                    st.NextTickMs = now + (long)(cfg.BleedTickSeconds * 1000f);

                    var hb = st.Ent.GetBehavior<EntityBehaviorHealth>();
                    if (hb == null) { (retire = retire ?? new List<long>()).Add(kv.Key); continue; }

                    float total = WoundMath.TotalPerTick(cfg.BleedStaticPerTick, cfg.BleedPctMaxHealthPerTick,
                        WoundMath.EffectiveHealth(hb.MaxHealth, cfg.BleedHealthCeiling),
                        st.Ledger.StrengthSum, st.Ledger.Count, cfg.BleedComboMultiplier, cfg.BleedMaxWounds);
                    if (sitHelps) total *= Math.Max(0f, cfg.BleedSitDamageMult);

                    // DEDICATED SPLATTER SIGNAL (0.9.3): the client keys DoT splatter off this
                    // monotonic counter, NOT the engine's onHurt bump (that path swallows ticks
                    // inside the 500ms invuln window - decompile-verified Entity.cs:935-953).
                    int tickN = st.Ent.WatchedAttributes.GetInt("thbleedtick", 0) + 1;
                    st.Ent.WatchedAttributes.SetInt("thbleedtick", tickN);
                    st.Ent.WatchedAttributes.SetFloat("thbleeddmg", total);
                    // Internal/Injury never re-procs OnSharpHit (gated there).
                    // TicksPerDuration=2 (with Duration zero): damage still applies INSTANTLY
                    // (EntityBehaviorHealth.TurnIntoDoTEffect requires Duration>0), but the
                    // onHurt bump at Entity.cs:1001 requires TicksPerDuration<2 - so our tick
                    // does NOT arm the 500ms invulnerable window. Without this, a bleeding
                    // animal was damage-immune ~1s in 6 and player arrows whiffed silently
                    // (no ping, no damage, no wound) while still sticking.
                    st.Ent.ReceiveDamage(new DamageSource
                    {
                        Source = EnumDamageSource.Internal,
                        Type = EnumDamageType.Injury,
                        TicksPerDuration = 2
                    }, total);
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

        /// <summary>
        /// PLAYER-CREDIT STAMP (0.14.25, field report "bones works but not well"): the bones
        /// rule needs to know a PLAYER was in this fight, and the killing blow alone lies three
        /// ways - an arrow whose FiredBy did not resolve server-side credits the ARROW (the
        /// engine quirk StickyProjectiles documents), a predator that lands the last bite on
        /// your 90%-worn-down quarry steals the kill, and a pit/fall death is sourceless. So
        /// every player-attributed hit - melee, arrow (FiredBy or the synced firedBy fallback,
        /// resolved to a real player so a bowtorn's bolts never count) - stamps the victim's
        /// server-side attributes with the hit time. The bones rule then spares any corpse a
        /// player hurt within its credit window. Bleed ticks are Internal and skipped here;
        /// the who-bled-me stamp already carries that credit.
        /// </summary>
        public static void StampPlayerHit(Entity victim, DamageSource src, float damage)
        {
            if (victim?.World == null || victim.World.Side != EnumAppSide.Server) return;
            if (victim is EntityPlayer || src == null || damage <= 0f) return;
            if (src.Source == EnumDamageSource.Internal || src.Type == EnumDamageType.Heal) return;

            bool byPlayer = src.GetCauseEntity() is EntityPlayer;
            if (!byPlayer && src.SourceEntity is EntityProjectileBase proj)
            {
                long fid = proj.FiredBy?.EntityId ?? proj.WatchedAttributes.GetLong("firedBy", 0L);
                byPlayer = fid != 0L && victim.World.GetEntityById(fid) is EntityPlayer;
            }
            if (!byPlayer) return;
            victim.Attributes.SetLong("tasshunt:phitMs", victim.World.ElapsedMilliseconds);
        }

        /// <summary>Was this entity hurt by a player within the last windowSeconds? Stale or
        /// future stamps (a previous session's clock) never count.</summary>
        public static bool RecentPlayerHit(Entity ent, float windowSeconds)
        {
            if (ent?.World == null || windowSeconds <= 0f) return false;
            long phit = ent.Attributes.GetLong("tasshunt:phitMs", 0L);
            if (phit <= 0L) return false;
            long now = ent.World.ElapsedMilliseconds;
            return now >= phit && now - phit <= (long)(windowSeconds * 1000f);
        }

        /// <summary>Active wound count (narrator/debug + blood visuals).</summary>
        public static int StacksOn(long entityId)
        {
            lock (Active) return Active.TryGetValue(entityId, out var st) ? st.Ledger.Count : 0;
        }

        /// <summary>
        /// Seconds until this entity's wounds close, for anything that needs to SEE the clock
        /// the size ladder handed out - the harness, and any diagnostic. -1 while an arrow pins
        /// one open, 0 when nothing is bleeding. Players publish the same number as
        /// "thbleedsecs"; animals never did, which is why this exists.
        /// </summary>
        public static int SecondsLeftOn(Entity ent)
        {
            if (ent?.World == null) return 0;
            lock (Active)
                return Active.TryGetValue(ent.EntityId, out var st)
                    ? st.Ledger.SecondsLeft(ent.World.ElapsedMilliseconds) : 0;
        }
    }

    /// <summary>
    /// The one hit funnel: every damage event on an entity with health passes through here, with
    /// the final (post-handler) damage value. Sharp ones open wounds - unless they bounce.
    /// </summary>
    [HarmonyPatch(typeof(EntityBehaviorHealth), nameof(EntityBehaviorHealth.OnEntityReceiveDamage))]
    public static class Patch_BleedOnSharpHit
    {
        public struct HitState
        {
            public float Incoming;
            public bool Bounced;
        }

        /// <summary>
        /// Captures the pre-armor damage, and since 0.14.39 decides the BOUNCE (owner ruling:
        /// bounce means no damage): a bounced hit skips the whole health path - no damage, no
        /// onDamaged delegates, no hurt flash, and the shared roll makes the stick gate drop
        /// the projectile recoverable. That skip is what makes "stone always bounces off
        /// armor" a real wall instead of chip damage. Victims are never players (ClassOf
        /// refuses them) and never creature-vs-creature (the bypass), so the player-side
        /// delegate chains - PvP shields, death witnesses, downed - never lose a hit to this.
        /// </summary>
        public static bool Prefix(EntityBehaviorHealth __instance, DamageSource damageSource, float damage, out HitState __state)
        {
            __state = new HitState { Incoming = damage };
            try
            {
                Entity victim = __instance?.entity;
                var cfg = HuntingModSystem.Cfg;
                if (cfg == null || !cfg.BounceEnabled || victim == null) return true;
                if (victim.World?.Side != EnumAppSide.Server || !victim.Alive || damage <= 0f) return true;
                if (!BleedSystem.IsSharpHit(damageSource)) return true; // blunt never bounces

                var proj = damageSource.SourceEntity as EntityProjectileBase;
                float chance = HideGlance.ChanceFor(victim, damageSource, proj, cfg, damage);
                if (chance <= 0f) return true;
                bool bounced = proj != null
                    ? HideGlance.RollOnce(proj.EntityId, chance, victim.World)
                    : victim.World.Rand.NextDouble() < chance;
                if (!bounced) return true;

                __state.Bounced = true;
                if (cfg.BloodDiagnostics)
                    victim.World.Logger.Notification("[TassHunting] bounce off {0} (chance {1:0.00}): no damage, no wound{2}",
                        victim.Code?.ToShortString(), chance,
                        proj != null ? ", projectile drops recoverable" : "");
                return false; // the hide or the plate turned it - the hit never happened
            }
            catch (Exception)
            {
                return true; // the bounce must never break damage handling
            }
        }

        public static void Postfix(EntityBehaviorHealth __instance, DamageSource damageSource, float damage, HitState __state)
        {
            if (__state.Bounced) return; // a non-event: no credit stamp, no wound, no heal hook
            try { BleedSystem.StampPlayerHit(__instance.entity, damageSource, damage); }
            catch (Exception) { /* credit is best-effort, never breaks damage */ }
            try { BleedSystem.OnSharpHit(__instance.entity, damageSource, damage, __state.Incoming); }
            catch (Exception) { /* bleed must never break damage handling */ }
            // The same funnel carries healing: a finished bandage/poultice closes every wound.
            try { BleedSystem.OnHealItemApplied(__instance.entity, damageSource); }
            catch (Exception) { /* ditto */ }
        }
    }
}
