using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace TassHunting
{
    // Hunting AI and awareness tweaks live in this assembly:
    //  - FLEE AWAY FROM THE SHOOTER (Patch_FleeAwayFromHunter below): a hit from
    //    beyond seeking range makes animals run in a random direction within the
    //    180-degree arc AWAY from the shooter, instead of vanilla's blind run in
    //    whatever direction the animal happened to face (sometimes straight at
    //    you).
    //  - PREDATOR FOOTSTEP RANGES (asset patches): wolf stalk 10->22, wolf run
    //    15->30, bear walk 15->30, bear charge 25->44 - audible before lethal.

    /// <summary>The one standard particle vocabulary: every blood category
    /// (trails, splatter) exposes exactly these eight dials.</summary>
    public class BloodParticleLook
    {
        public bool Enabled = true;
        public float SizeMin, SizeMax;
        public int QtyMin, QtyMax;
        public float SpreadMin, SpreadMax;
        public float LifetimeMin, LifetimeMax; // seconds
    }

    /// <summary>
    /// One rung of the bleed size ladder, keyed on MAX HEALTH (2026-08-22 rework).
    /// Health, not weight: every creature has it, it varies per variant for animals AND rust
    /// (all six drifter tiers weigh an identical 140kg, so weight cannot tell them apart), and
    /// 324 of 820 entity types never set a weight at all. It also agrees with the damage side,
    /// which is already a percentage of max health - so a body's clock and its bleed rate can
    /// no longer disagree the way weight did (a 650kg moose carries 21 HP and was drawing a
    /// bear-sized clock on a deer-sized health pool).
    /// MaxHealth is the INCLUSIVE upper bound of the rung; 0 or less means "no upper bound",
    /// which the last rung uses.
    /// </summary>
    public class BleedSizeTier
    {
        public float MaxHealth;
        public float Seconds;     // how long a wound stays open on a body this size
        public float Odds;        // chance a hit from this creature opens a wound at all
        public float SecondOdds;  // chance the same hit opens a SECOND wound
    }

    /// <summary>A rust rung: rust bodies clot on their own schedule, so only the clock differs.
    /// The ODDS still come from the size ladder, so a 54 HP double-headed drifter hits like the
    /// extra-large rung it belongs to while still clotting on the rust clock.</summary>
    public class BleedRustTier
    {
        public float MaxHealth;
        public float Seconds;
    }

    public class HuntingConfig
    {
        // A file written by an older build carries a lower number and gets migrated; a fresh
        // config is born current, so a new install never logs an upgrade it did not need.
        public int Version = CurrentVersion;
        public bool FleeAwayFromHunterEnabled = true;
        // With Item Pickup Highlighter installed: only YOUR projectiles highlight
        // (enemy-thrown stones/arrows stay unmarked). Client-side.
        // [ClientPersonal] marks a field as the player's own look-and-feel choice:
        // on a multiplayer client the server's config replaces everything else at
        // join (see HuntingConfigSync). Unmarked = the server rules it.
        [ClientPersonal] public bool HighlightOnlyOwnProjectiles = true;

        // Extended pickup for LANDED arrows/spears (vanilla collect range is a
        // touch-range 1.5 blocks, decompile-verified). 0 = vanilla only.
        public float ProjectilePickupRadius = 4f;
        // Only vacuum projectiles YOU fired (matches the highlighter filter);
        // walking over someone else's still collects the vanilla way.
        public bool PickupOnlyOwnProjectiles = true;

        // TRUE AIM (playtest 2026-07-18: "have to aim at their feet when close").
        // Vanilla spawns the projectile 0.21 blocks horizontally BEHIND the
        // player at full eye height (decompile-verified) - above the descending
        // camera ray, so close shots land high. This re-seats the spawn ONTO the
        // aim ray: eye position + 0.3 along the flight direction. Bows and
        // spears; player-fired only.
        public bool TrueAimSpawnEnabled = true;

        // ---- PREDATOR OVERHAUL (see PredatorAI.cs) ----
        public bool PredatorOverhaulEnabled = true;
        // All ADULT predators (vanilla "predator"+"adult" entity tags: wolves, bears, foxes,
        // hyenas, modded creatures that tag themselves) move this much faster. 1 = off.
        public float PredatorSpeedMult = 1.2f;
        // Apex predators: always charge, never flee, spot you from range.
        public string[] ApexCodes = { "bear-black", "bear-brown", "bear-polar" };
        public float ApexSeekRange = 30f;        // unprovoked (vanilla 16)
        public float ApexAggroSeekRange = 40f;   // after you hurt it (vanilla 30)
        public float ApexMaxFollowTimeSec = 240f;// chase timer (vanilla 60)
        public float ApexIdleStopRange = 30f;    // wakes/stands seeing you (vanilla 10/5)
        // Pack hunters: swarm together, hit-and-run alone, flee only when solo.
        public string[] PackCodes = { "wolf", "hyena" };
        public float PackRadius = 24f;           // packmate = same species within this
        public bool SoloHitAndRun = true;
        public bool PackSuppressFlee = true;
        public float PackAggroSeekRange = 25f;   // vanilla 15
        public float PackMaxFollowTimeSec = 240f;

        // ---- BIG GAME (owner design 2026-08-28, see HideGlance.cs) ----
        // Bleed damage grows with an animal's max health up to about this value, then levels
        // off (effHP = C * tanh(HP/C)). Below ~half the ceiling the curve IS the old straight
        // line, so vanilla animals are untouched; a 400 hp modded giant bleeds at a big-animal
        // rate instead of a monster rate. 0 = off (bleed keeps growing forever, old behavior).
        public float BleedHealthCeiling = 100f;
        // Past this much max health, hide has a chance to turn a blade: no stick, no wound.
        // Default sits just under a bear (66) - every smaller vanilla animal always takes the
        // hit, which keeps the 0.14.16 "player weapons always wound" promise where a random
        // miss would read as a bug (owner revised the rule 2026-08-28: refusal is allowed
        // exactly when the target's size explains it on screen).
        public float GlanceStartHealth = 45f;
        // How much health past the threshold it takes for the chance to approach its maximum.
        public float GlanceRampHealth = 200f;
        public float GlanceMaxChance = 0.5f;
        // Absolute clamp applied AFTER the per-creature multiplier below: nothing is ever
        // arrow-proof, whatever the config says - there is always a spear that bites.
        public float GlanceChanceCeiling = 0.8f;
        // A full heavy draw halves the glance chance - the patient shot beats thick hide.
        public bool PowerShotPunchesThrough = true;
        // SHARPNESS (2026-08-28): sharper metal glances less. Keyed on the hit's DAMAGE (the
        // real material ladder: flint spear 4.0 ... steel 7.0) - damage TIER is dead data here,
        // vanilla spears are tier 0 flint through blackbronze and arrows have no tier at all.
        // At or below Base (flint) the glance curve applies in full; each damage point above it
        // removes Step from the glance, never below Floor - plate stays plate. A rex-size bite
        // (24 damage) sits on the floor: crushing force beats armor.
        public float GlanceSharpnessBase = 4f;
        public float GlanceSharpnessStep = 0.12f;
        public float GlanceSharpnessFloor = 0.35f;
        // CREATURE MELEE DAMAGE (2026-08-28, see CreatureDamageMul.cs): wildcard entity codes
        // to multipliers on their melee bite, applied to the meleeattack tasks at load. Built
        // for modded law-breakers (the dino survey found two species biting at double their
        // roster's own damage-vs-health curve); empty by default, first matching entry wins.
        // e.g. { "*-lajasvenator-*": 0.35 } - health, speed and everything else untouched.
        public Dictionary<string, float> CreatureMeleeDamageMul = new Dictionary<string, float>();

        // ---- RETALIATION + TERRITORY (2026-08-28, see Territory.cs) ----
        // Creatures in RetaliationCodes remember being hurt: anger memory, chase range and
        // chase persistence raised to the numbers below (values only ever go up). Creatures
        // in TerritorialCodes ADDITIONALLY start the fight themselves when a player enters
        // TerritoryRadius - they hold ground, and only cool down after the player is truly
        // gone for the memory duration. Both lists wildcards, both empty by default.
        public string[] RetaliationCodes = new string[0];
        public string[] TerritorialCodes = new string[0];
        public float RetaliationSeekRange = 40f;        // anger-chase radius (typical shipped value: 20)
        public float RetaliationMaxFollowTimeSec = 120f;// how long it presses the chase (typical: 30)
        public float RetaliationMemorySeconds = 180f;   // how long it stays angry (typical: 60)
        public float TerritoryRadius = 12f;             // territorial only: guard radius around itself
        // Per-creature correction, because health measures SIZE, not armor (the engine has no
        // creature armor stat): wildcard entity codes to multipliers on the glance chance.
        // e.g. { "ankylosauria-*": 1.5, "macronaria-*": 0.6 } - plates up, soft hide down.
        // First matching entry wins. Empty = the plain size curve.
        public Dictionary<string, float> GlanceToughness = new Dictionary<string, float>();

        // ---- STAY WILD (see StayWild.cs) ----
        // Named creatures can never be tamed, petted, roped, owned or ridden - the
        // domestication behaviors are taken off their entity types at load, so a companion
        // mod installed later cannot hand them back. Off by default: it changes creatures
        // this mod otherwise has no opinion about.
        public bool StayWildEnabled = false;
        // Which creatures. Wildcards, matched against the full entity code AND the bare
        // path, so "tyrannosauridae:*" and "tyrannosauridae-*" both name that family.
        // Empty = nothing (the switch needs somebody to point at).
        public string[] StayWildCodes = new string[0];
        // Which behaviors count as domestication. Defaults cover vanilla riding and leashing
        // plus the two Jaunt/PetAI behaviors the dino packs add when those mods are present.
        // "ownable" is deliberately NOT here: on its own it only verifies an existing
        // ownership record, and removing it would orphan animals somebody already owns.
        public string[] StayWildBehaviors = { "rideable", "gait", "tameable", "receivecommand", "pettable", "ropetieable" };

        // ---- HARVEST OVERHAUL (playtest 2026-07-19, see HarvestOverhaul.cs) ----
        // Knife harvest hold time multiplier (0.5 = half of vanilla; 0 = leave
        // vanilla timing alone, matching the mod's "0 = off" convention - field
        // report 2026-08-10 set 0.00 expecting vanilla and got a twentieth).
        public float HarvestTimeMult = 0.5f;
        // Finished harvest spills loot on the ground and poofs the corpse â€”
        // the carcass window never opens.
        public bool HarvestAutoDrop = true;
        // Player kills roll their loot at death; empty roll (or never-harvestable
        // corpses like bells/locusts) => corpse self-removes after the delay.
        public bool EmptyCorpseAutoRemove = true;
        public float EmptyCorpseRemoveSeconds = 10f;

        // ---- POWER SHOT (see PowerShot.cs): draw past full accuracy for bonus damage ----
        public bool PowerShotEnabled = true;
        // Extra hold time past YOUR full-accuracy moment (stat-derived, ~0.54s default player).
        public float PowerShotExtraDrawSeconds = 1.0f;
        // Damage multiplier for a power shot. 1.25 = 25% more.
        public float PowerShotDamageMult = 1.25f;
        // Quiet click for the shooter the moment the extra hold pays off.
        [ClientPersonal] public bool PowerShotDrawCue = true;

        // ---- STICKY PROJECTILES (see StickyProjectiles.cs) ----
        // Master: arrows/spears ride the animal they hit instead of vanishing.
        public bool StickyProjectilesEnabled = true;
        // How long an arrow stays embedded - and (2026-07-22) THE BLEED TIMER:
        // an embedded arrow bleeds the animal for this long, then works loose.
        // (When StickUntilDeath is on, this is only the FALLBACK cap for an
        // animal that fled and never died - see below.)
        public float StickSeconds = 60f;
        // ARROWS STAY UNTIL DEATH (user request 2026-07-22): while the animal is
        // ALIVE and loaded, arrows never work loose - they stay embedded and keep
        // bleeding (bleed = stuck-arrow count) right up until the kill, then all
        // drop recoverable. StickSeconds still applies as a SAFETY CAP only for a
        // target that is gone/unloaded (a fled or despawned animal), so arrows in
        // a lost animal do not ride forever and leak bleed stacks.
        public bool StickUntilDeath = false;
        // A stuck SPEAR can be grabbed back at vanilla touch range (arrows stay
        // uncollectible until released - walking near must not yank them out).
        public bool SpearTouchRetrieve = true;
        // ---- ARROW OWNERSHIP (see ArrowOwnership.cs) ----
        // Your fired arrows are YOURS for this many seconds: other players'
        // walk-over pickup ignores them, you always may collect your own, and
        // after the window anyone may. 0 turns the lock off.
        public float ArrowOwnerLockSeconds = 120f;
        // An arrow stuck in a PLAYER can be pulled out by hand at touch range -
        // by its shooter, or by the stuck player themselves. Arrows in animals
        // stay untouchable until released. Off = the stick timer is the only way out.
        public bool PlayerArrowTouchRetrieve = true;
        // Body-ellipse anchoring (goat-flank playtest): how WIDE the body is
        // across vs along the spine (collision boxes are square; real bodies
        // aren't). Still a fraction - this shapes the ellipse, it is not a depth.
        public float StickBodyWidthFraction = 0.45f;
        // ABSOLUTE EMBED DEPTH in blocks (2026-07-23, replaces the old
        // StickEmbedFraction). The bug it fixes: embedding by a FRACTION of the
        // body made the absolute bite scale with animal size - a fixed-length
        // arrow looked swallowed on a pampas deer/hare and planted on a bear
        // (proven with geometry across every test box). An arrow physically bites
        // a roughly FIXED depth regardless of what it hit, so this is one absolute
        // number for the whole animal kingdom - no per-species/baby tuning, since
        // Attach reads each target's live CollisionBox. Default 0.12 (~head + a
        // little shaft). On a body thinner than this (hare/pampas flank, side
        // hits) the anchor drives to/through center so the TIP POKES OUT THE FAR
        // SIDE - a visible pass-through is better than an arrow lost inside a thin
        // box (user 2026-07-23, game-feel-over-realism). StickPassThroughCap is
        // the json-only backstop that stops the tip flying a full body-length out.
        public float StickEmbedDepth = 0.12f;
        public float StickPassThroughCap = 1.0f; // json-only: max tip overshoot past center, in body-radii

        // ---- ARCHERY (see ArcheryTweaks.cs). Bows are pure vanilla
        //      (accuracy crude -0.05 .. recurve +0.3); only arrows are tuned. ----

        // Per-material arrow break chance. Curve halves per tech tier working
        // back from steel-never-breaks:
        //   reed 32% -> neolithic 16% -> stone 8% -> copper 4% ->
        //   bronze 2% -> iron 1% -> steel 0%.
        // Keys match the arrow code suffix (arrow-<material>). Materials NOT
        // listed here (modded arrows) are left completely untouched - they
        // keep whatever their own mod ships. Values clamp 0..1.
        public bool ArrowBreakTuningEnabled = true;
        // 0.9.4 (user request; 0.9.5 default ON - "common sense to have it,
        // off is for the one-off person that doesnt want it"): when an arrow
        // breaks on impact, drop the matching arrowhead item so you can recover
        // the head. Only materials with an arrowhead-<material> item drop one;
        // crude, erel (reed) and bone have no head and drop nothing (matches
        // reality: a knapped/metal head survives a snapped shaft, a reed/bone
        // arrow does not leave a reusable tip).
        public bool DropArrowheadOnBreak = true;
        public Dictionary<string, float> ArrowBreakChanceByMaterial = new Dictionary<string, float>
        {
            // neolithic
            ["erel"] = 0.32f,   // reed practice arrow
            ["crude"] = 0.16f,
            ["bone"] = 0.16f,
            // stone
            ["flint"] = 0.08f,
            ["obsidian"] = 0.08f,
            // copper age (castables)
            ["copper"] = 0.04f,
            ["gold"] = 0.04f,
            ["silver"] = 0.04f,
            // bronze age
            ["tinbronze"] = 0.02f,
            ["bismuthbronze"] = 0.02f,
            ["blackbronze"] = 0.02f,
            // iron age
            ["iron"] = 0.01f,
            ["meteoriciron"] = 0.01f,
            // steel
            ["steel"] = 0f,
        };

        // ---- STACKING HYBRID BLEED (see BleedSystem.cs; the damage half).
        //      The visual half lives in BloodVisuals.cs - this mod renders its
        //      own blood, no third-party blood mod needed. ----
        // ARROW-DRIVEN BLEED (2026-07-22): bleed exists ONLY while arrows are
        // stuck. Each embedded arrow = one sustained stack; the stick timer
        // (StickSeconds) IS the bleed timer. No cap, no chance roll, no hit-type
        // gate. Balance: 0.05 flat + 0.5% max-HP per stack, 3s tick, 60s stick.
        public bool BleedEnabled = true;
        public float BleedTickSeconds = 3f;            // tick cadence
        // 2026-08-22: both cut roughly in half. A single wound is now something you notice and
        // dress rather than a burst of damage; the danger comes from stacking (see the two
        // multipliers below) and from the wound running LONGER.
        public float BleedStaticPerTick = 0.02f;       // flat hp per wound per tick
        public float BleedPctMaxHealthPerTick = 0.25f; // % of max hp per wound per tick
        public bool BleedAffectsPlayers = true;        // PvP + creature bites bleed humans too

        // ---- WOUND MODEL (2026-07-27): sharp hits open wounds; tier + combo scale them ----
        // A hit below this final damage opens no wound (grazes, fully-absorbed hits).
        public float BleedMinDamage = 0.5f;
        // FALLBACK clock only, since 2026-08-22: the real clock comes from the size ladder
        // below, keyed on the bleeding body's max health. This is what a body with no health
        // behavior at all would get, and what the whole system falls back to if the ladder is
        // emptied - so a hand-cleared table degrades to the old single-number behaviour instead
        // of to no bleeding at all.
        public float BleedWoundSeconds = 45f;

        // ---- THE SIZE LADDER (2026-08-22). One table, read from both ends: your rung sets how
        // long YOU bleed, and how reliably you wound someone else when you attack. Small things
        // still bleed you - they just often fail to (owner call: the rework makes bleeds
        // survivable enough that a fox drawing blood half the time is fine). Player weapons
        // never roll; a hunter's arrow always wounds (see BleedSystem.OnSharpHit).
        public BleedSizeTier[] BleedSizeTiers = {
            new BleedSizeTier { MaxHealth = 8f,  Seconds = 12f, Odds = 0.50f, SecondOdds = 0f },
            new BleedSizeTier { MaxHealth = 25f, Seconds = 30f, Odds = 0.75f, SecondOdds = 0f },
            new BleedSizeTier { MaxHealth = 50f, Seconds = 50f, Odds = 1.00f, SecondOdds = 0.25f },
            new BleedSizeTier { MaxHealth = 0f,  Seconds = 80f, Odds = 1.00f, SecondOdds = 0.50f },
        };
        // Rust clocks. Vanilla's drifter ladder by health: normal 12, deep 16, tainted 22,
        // corrupt 30, nightmare 40, double-headed 54. NOTE these are for rust YOU stab - normal,
        // deep and tainted swing BLUNT and can never open a wound on you at all.
        public BleedRustTier[] BleedRustTiers = {
            new BleedRustTier { MaxHealth = 14f, Seconds = 20f },
            new BleedRustTier { MaxHealth = 25f, Seconds = 30f },
            new BleedRustTier { MaxHealth = 0f,  Seconds = 45f },
        };
        // Players are all one size, so they get a flat clock rather than a rung.
        public float BleedPlayerWoundSeconds = 30f;
        // Every extra open wound multiplies how long the whole set stays open. Pairs with
        // BleedComboMultiplier below - the two COMPOUND, so 1.25 x 1.25 is 1.5625 per wound.
        public float BleedLengthMultiplier = 1.25f;
        // Wound strength per damage tier of the hit: strength = weight * (1 + step * tier).
        // 0.25 -> flint 1.25x, copper 1.5x, bronze 1.75x, iron 2x, steel 2.25x.
        public float BleedTierStep = 0.25f;
        // Each additional open wound multiplies the WHOLE bleed by this (the combo payoff).
        public float BleedComboMultiplier = 1.25f;
        // Hard cap on open wounds per animal; also caps the combo exponent.
        public int BleedMaxWounds = 10;
        // Wound weight by how the hit was delivered (rule by damage type + tool kind, not items).
        // DOUBLED 2026-08-22 to absorb the halved per-tick damage, so hunting keeps its bite.
        // These are PLAYER WEAPONS ONLY now - claws and bites have their own dial below, so
        // tuning the hunt can never buff every wolf on the server (it used to: a wolf's bite
        // borrowed BleedSlashWoundWeight and a locust's sting borrowed the spear number).
        public float BleedArrowWoundWeight = 2f;
        public float BleedThrownSpearWoundWeight = 3f;
        public float BleedSpearStabWoundWeight = 2f;
        public float BleedSlashWoundWeight = 1.5f;
        // What a claw, bite or sting is worth. Its own number since 2026-08-22.
        public float BleedCreatureWoundWeight = 0.75f;

        // ---- SITTING, ARMOR AND WHO SWUNG (2026-08-03) ----
        // Sit still and, after an unbroken stretch, both the bleed damage and the wound
        // clock run at half for as long as you stay down. Standing up ends it instantly and
        // zeroes the credit - a fresh sit starts the count over, and because the help is a
        // rate rather than a one-time cut there is nothing to farm by bobbing up and down.
        // An arrow still in you pins its wound: no amount of sitting closes that one.
        public bool BleedSittingHelps = true;
        public float BleedSitSecondsRequired = 5f;   // unbroken seconds seated before it helps
        public float BleedSitDamageMult = 0.5f;      // bleed damage while it is helping
        public float BleedSitDurationMult = 0.5f;    // wound time left while it is helping
        // Wound size by attacker class; 0 = that class never opens a wound. Rust = anything
        // tagged rust-creature or mechanical (drifters, locusts, bells and modded kin);
        // creature = everything else alive that is not a player. Players keep the weapon
        // weights above. Only the sharp ones ever wounded at all: among drifters that is
        // corrupt, nightmare and double-headed; among locusts the bronze and sawblade ones.
        public float BleedRustAttackWoundMult = 1f;
        public float BleedCreatureAttackWoundMult = 1f;
        // How much armor shrinks the wound, measured from what it actually absorbed (see
        // BleedSystem's header). 1 = the wound is only as big as the part of the hit that got
        // through; 0 = armor makes no difference to bleeding, as before 2026-08-03.
        public float BleedArmorMitigation = 1f;
        // Armor that absorbs at least this share of a blow turns the edge: no wound at all.
        // 1 = never, leaving BleedMinDamage as the only way a hit fails to wound.
        public float BleedArmorNoWoundAbsorb = 0.85f;

        // ---- DRESSINGS AND THE BLEEDING BOX (2026-07-29) ----
        // SERVER: a finished bandage or poultice closes every open wound on whoever it was
        // applied to. Keyed on the engine's healing-item contract, so modded dressings count too
        // (see BleedSystem.OnHealItemApplied).
        public bool BleedStoppedByHealingItems = true;
        // CLIENT: the on-screen bleeding box - blood-drop icon, open wound count and the
        // countdown to it closing, in the same shape as the XSkills effect box. Deliberately
        // just that: no hover description panel (user call 2026-07-29, it read as clutter).
        // The red hurt flash on every bleed tick (0.14.20). Purely client-side cosmetic -
        // it animates the same RenderColor fade the engine uses for real hits, WITHOUT
        // arming the 500ms invulnerability window (see BloodVisuals: the vanilla flash and
        // the i-frames are one clock; ours is only the light half of it).
        [ClientPersonal] public bool BleedTickHurtFlash = true;
        [ClientPersonal] public bool BleedHudEnabled = true;
        // Corner the box sits in. One of: LeftTop, LeftMiddle, LeftBottom, RightTop,
        // RightMiddle, RightBottom, CenterTop, CenterBottom. Left middle by user call
        // 2026-07-29. The XSkills effect frame sits at left middle too, so the
        // LeftMiddle anchor hangs 50px below the true middle (user call 2026-08-19)
        // and the two never stack; BleedHudOffsetY still applies on top.
        [ClientPersonal] public string BleedHudPosition = "LeftMiddle";
        // Nudge the box in pixels from that corner (json-only).
        [ClientPersonal] public int BleedHudOffsetX = 0;
        [ClientPersonal] public int BleedHudOffsetY = 0;
        // WHO SHOWS RED BLOOD (user 2026-07-23): animals and players show blood as
        // they always have; RUST CREATURES are the only new distinction, gated by
        // one plain checkbox (default OFF - red blood off a rust being reads
        // wrong). Sticky arrows and the bleed DoT still apply to EVERY entity
        // (combat, not viscera); this gates the VISUAL blood only. Player VISUAL
        // blood is deliberately NOT its own toggle - the existing "Players can
        // bleed (PvP)" (BleedAffectsPlayers) already covers the player case at the
        // damage layer; a second player switch would just confuse.
        //   NOTE: a NEW vanilla/modded rust being we do not yet name is treated
        //   as an animal (shows blood) until its name is added - it never breaks,
        //   it just is not recognized as rust yet. That is the accepted tradeoff
        //   for a name-the-entity classifier (see HuntingModSystem.IsRustCreature).
        [ClientPersonal] public bool BloodEffectsForRustCreatures = false;

        // ---- BLOOD VISUALS (see BloodVisuals.cs). In-house blood system:
        //      a spot ledger + water diffusion; the current build renders it
        //      client-locally (the sync layer is parked, see BloodVisuals). ----
        [ClientPersonal] public bool BloodVisualsEnabled = true;
        // How long the spot RECORD is kept, seconds. The visible blood lifetime
        // is BloodTrails.Lifetime (per-drop) - this just needs to outlast it so
        // the record isn't culled before its particles finish. Match the max
        // trail lifetime.
        public float BloodSpotLifetimeSeconds = 60f;
        // Server deposit cadence while something bleeds. 0.25s = 4 drips/sec.
        // Our drops persist and re-render, so spatial density (spacing along
        // the path) is what reads, not raw emission rate.
        public float BloodDepositIntervalSeconds = 0.25f;
        // Merge threshold, NOT a density dial (density = drip interval): drips
        // closer together than this GROW the previous spot into a pool, so
        // stationary/dying animals pool instead of spamming spots. Demoted to
        // json-only 0.6.7 (user: reads as redundant next to drip rate).
        public float BloodSpotMinSpacingBlocks = 0.8f;
        public int BloodMaxSpots = 4096;          // server ledger cap, oldest pruned
        [ClientPersonal] public float BloodRenderDistanceBlocks = 64f;
        [ClientPersonal] public int BloodMaxRenderedSpots = 1200;  // client per-tick render budget
        [ClientPersonal] public bool WaterBloodEnabled = true;     // blood in water diffuses as tiles

        // ---- CORPSE BLOOD (0.11.0, user 2026-07-22: "darken, hold, then fade
        //      BUT if its a corpse then the blood spreads out wider" +
        //      "introduce a corpse config setting again and do corpse blood").
        //      A death pool is the SAME blood look as a trail drop, but the pool
        //      radius is multiplied by CorpseSpreadMult so a kill leaves a wide
        //      spreading pool, not a tight dot. Toggle off to suppress death
        //      pools entirely (bleed trails while alive are unaffected). ----
        [ClientPersonal] public bool CorpseBloodEnabled = true;    // client: render the death pool spread
        [ClientPersonal] public float CorpseSpreadMult = 2.5f;     // client: death pool radius x this (1 = same as a trail spot)
        [ClientPersonal] public float CorpsePoolLifetimeSeconds = 120f; // client: how long the death pool lingers before it dries away (its OWN duration, not the trail's)
        [ClientPersonal] public bool BloodDiagnostics = false;          // client: log what each corpse pool emits (count/size/lifetime), for tuning

        // ---- BLOOD LOOK (0.8.0 - USER-SPEC PANEL: four sections, ONE standard
        //      particle vocabulary per category, everything else json-only.
        //      "Something like this instead of the thousand knobs we have.") ----
        // The two visual systems (user-identified): TRAILS = ground decals
        // (line drops, pools, hit marks). SPLATTER = airborne juice (spurt
        // pulses on the shot / DoT beat, plus falling droplets).
        // Defaults are the user's own field-tuned 2026-07-22 values.
        [ClientPersonal] public BloodParticleLook BloodTrails = new BloodParticleLook
        {
            Enabled = true,
            SizeMin = 0.598f, SizeMax = 1.048f,
            QtyMin = 2, QtyMax = 5,
            SpreadMin = 0.05f, SpreadMax = 0.2f,
            LifetimeMin = 45f, LifetimeMax = 60f
        };
        // Splatter is the SAME blood as trails - same lifetime; it just starts
        // higher and more explosive (bigger spread/launch, more particles).
        // Lifetime matched to BloodTrails per the user (all blood lasts alike).
        [ClientPersonal] public BloodParticleLook BloodSplatter = new BloodParticleLook
        {
            Enabled = true,
            SizeMin = 0.503f, SizeMax = 1.0f,
            QtyMin = 6, QtyMax = 15,         // particles per spurt pulse (damage-scaled within)
            SpreadMin = 0.4f, SpreadMax = 1.5f, // launch speed range
            LifetimeMin = 45f, LifetimeMax = 60f  // == trails; all blood lasts alike
        };
        // Water Effect
        [ClientPersonal] public bool TintSurroundingWater = true;  // client: render the blood-in-water murk
        // Rain clear speed 0..2 (affects newly deposited blood, trails and
        // splatter marks alike): 0 = rain never clears blood, 1 = rain cuts
        // lifetime in half, 2 = to a third.
        [ClientPersonal] public float RainClearSpeed = 1f;
        // Bleed Damage section extra
        [ClientPersonal] public bool SpawnSplatterOnDamage = true; // splatter on qualifying hits + DoT ticks

        // json-only dials (deliberately NOT in the panel per the 0.8.0 spec)
        [ClientPersonal] public string BloodColorHex = "#74080C";        // FRESH blood (bright)
        // AGED blood (dark, dried): ground spots lerp from fresh -> aged over
        // their lifetime (field 2026-07-22). Set equal to BloodColorHex to
        // disable the age darkening.
        [ClientPersonal] public string BloodColorAgedHex = "#3A0406";
        public float BloodOnHitMinDamage = 0.5f;
        public float BloodTrailScale = 1f;              // server: trail/hit deposit amount
        public float RunningBloodMult = 1.5f;           // server: sprint bleed boost
        public float WaterBloodDecayPerSecond = 0.12f;  // server: water field fade
        public float WaterBloodSpreadPerSecond = 0.02f; // server: water field spread
        [ClientPersonal] public float WaterBloodMaxOpacity = 0.50f;      // client: water blood particle opacity (0.50 = 50%). Direct %, tune from panel.
        [ClientPersonal] public float WaterClotAmount = 1f;              // client: how many murk puffs per beat (sediment recipe fixes their size)

        // ---- WOUNDED SLOWDOWN (see WoundedSlowdown.cs): a per-tier health
        //      table that slows creature movement in ALL AI states ----
        public bool WoundedSlowdownEnabled = true;
        public WoundedSlowTier[] WoundedSlowTiers = {
            new WoundedSlowTier { HealthPctMax = 10f, SlowPct = 50f },
            new WoundedSlowTier { HealthPctMax = 20f, SlowPct = 40f },
            new WoundedSlowTier { HealthPctMax = 30f, SlowPct = 30f },
            new WoundedSlowTier { HealthPctMax = 40f, SlowPct = 20f },
            new WoundedSlowTier { HealthPctMax = 50f, SlowPct = 10f },
        };

        /// <summary>
        /// THE CURRENT CONFIG GENERATION. Bumped when a rebalance makes the old values
        /// incoherent rather than merely different, so Migrate can reset exactly those fields.
        /// </summary>
        public const int CurrentVersion = 2;

        /// <summary>
        /// Bring an older config up to the current generation. Returns what it did, or null.
        ///
        /// WHY THIS OVERWRITES TUNED VALUES: the 2026-08-22 bleed rework halved the per-tick
        /// damage and doubled the weapon weights in the SAME pass, and added two multipliers
        /// that compound. An existing file keeps its old 0.05/0.5 damage under the new
        /// multipliers and the new length ladder, which is far harsher than either the old or
        /// the new design - the one outcome nobody chose. So the interlocking balance fields
        /// are reset together and the change is logged loudly. Fields outside that set (blood
        /// look, harvest, archery, sticky arrows) are never touched.
        /// </summary>
        public string Migrate()
        {
            if (Version >= CurrentVersion) return null;
            Version = CurrentVersion;
            BleedStaticPerTick = 0.02f;
            BleedPctMaxHealthPerTick = 0.25f;
            BleedComboMultiplier = 1.25f;
            BleedArrowWoundWeight = 2f;
            BleedThrownSpearWoundWeight = 3f;
            BleedSpearStabWoundWeight = 2f;
            BleedSlashWoundWeight = 1.5f;
            return "bleed rebalance: per-tick damage, combo and weapon weights reset to the new "
                 + "defaults (they interlock with the new size ladder - old values under the new "
                 + "multipliers would hit far harder than either design intended)";
        }

        /// <summary>Every hand-edited-value rule in one place, applied after EVERY
        /// load path: file load, ConfigLib restore/defaults, and the server sync
        /// (HuntingConfigSync.BuildSessionConfig).</summary>
        public void Sanitize()
        {
            // 0 (or less) = leave vanilla harvest timing alone. Same "0 = off"
            // convention as the rest of the config; before this, 0.00 clamped to
            // 0.05 and gave a twentieth of vanilla (field report 2026-08-10).
            if (HarvestTimeMult <= 0f) HarvestTimeMult = 1f;
            else HarvestTimeMult = Vintagestory.API.MathTools.GameMath.Clamp(HarvestTimeMult, 0.05f, 10f);

            // Hand-deleted lists come back as null through the deserializer; stay-wild reads
            // both every load path (file, ConfigLib restore, server sync), so normalize here.
            if (StayWildCodes == null) StayWildCodes = new string[0];
            if (StayWildBehaviors == null) StayWildBehaviors = new string[0];

            // Big game: keep every glance number inside its meaningful range on all load paths.
            if (BleedHealthCeiling < 0f) BleedHealthCeiling = 0f;
            if (GlanceStartHealth < 0f) GlanceStartHealth = 0f;
            GlanceRampHealth = Math.Max(1f, GlanceRampHealth);
            GlanceMaxChance = Vintagestory.API.MathTools.GameMath.Clamp(GlanceMaxChance, 0f, 1f);
            GlanceChanceCeiling = Vintagestory.API.MathTools.GameMath.Clamp(GlanceChanceCeiling, 0f, 1f);
            if (GlanceToughness == null) GlanceToughness = new Dictionary<string, float>();
            if (GlanceSharpnessBase < 0f) GlanceSharpnessBase = 0f;
            if (GlanceSharpnessStep < 0f) GlanceSharpnessStep = 0f;
            GlanceSharpnessFloor = Vintagestory.API.MathTools.GameMath.Clamp(GlanceSharpnessFloor, 0f, 1f);
            if (CreatureMeleeDamageMul == null) CreatureMeleeDamageMul = new Dictionary<string, float>();
            if (RetaliationCodes == null) RetaliationCodes = new string[0];
            if (TerritorialCodes == null) TerritorialCodes = new string[0];
            RetaliationSeekRange = Vintagestory.API.MathTools.GameMath.Clamp(RetaliationSeekRange, 0f, 200f);
            RetaliationMaxFollowTimeSec = Vintagestory.API.MathTools.GameMath.Clamp(RetaliationMaxFollowTimeSec, 0f, 3600f);
            RetaliationMemorySeconds = Vintagestory.API.MathTools.GameMath.Clamp(RetaliationMemorySeconds, 0f, 3600f);
            TerritoryRadius = Vintagestory.API.MathTools.GameMath.Clamp(TerritoryRadius, 0f, 60f);
        }
    }

    public class HuntingModSystem : ModSystem
    {
        public static HuntingConfig Cfg = new HuntingConfig();

        /// <summary>RUST/TEMPORAL beings, keyed on the engine's own ENTITY TAGS -
        /// not a class list, not code strings (decompile-verified against 1.22.3,
        /// and it lands on the [[rule-not-instance]] law: key on the attribute).
        /// Every rust being in vanilla carries one of these two top-level JSON
        /// tags: organic-rust ones (drifter, shiver, BOWTORN) are "rust-creature";
        /// metal ones (locust, bell, eidolon) are "mechanical". Every animal
        /// carries "animal" and neither of these (verified across 33 animal
        /// JSONs). This subsumes what was a 5-type list + a bowtorn code hack -
        /// bowtorn is "rust-creature" like the rest, so the special case is gone.
        ///
        /// WHY TAGS BEAT the Type check: robust to class renames, AUTO-CATCHES any
        /// modded being that tags itself rust-creature/mechanical, one cheap
        /// bitmask op (Vector256 AND) vs 6 reflective type tests, and Entity.Tags
        /// is synced to the client (Entity.cs:48-51), where blood spawns.
        ///
        /// WE OWN NOTHING: this only READS the entity's tags at blood-spawn time -
        /// no patching, no behavior injection (that is how Footprints does it and
        /// it would stomp other mods editing those entities).
        ///
        /// Tag INDICES are reassigned every game start and are NOT stable
        /// (Entity.cs:51), so we resolve the set by NAME through EntityTagRegistry
        /// once (lazily, after registries load), cache it, and never hardcode a
        /// bit position. If the names ever fail to resolve the set is empty and
        /// everything reads as an animal (shows blood) - it degrades, never
        /// crashes. If VS adds a new rust tag we do not name, that being shows
        /// blood until the name is added - the accepted, non-breaking tradeoff.</summary>
        private static readonly string[] RustTagNames = { "rust-creature", "mechanical" };
        // FLESH tags = "this thing has blood to spill" (real 1.22 vocabulary,
        // enumerated from the game's own entity JSONs). animal + huntable cover
        // wildlife/livestock; human + humanoid cover people/traders/villagers.
        // Everything that CANNOT bleed - dropped items, logs, clay, arrows,
        // boats, structures, bots - carries none of these (items carry NO tag at
        // all and have no health behavior). This is the fix for the "burning
        // logs/clay make blood" bug: EntityItem takes FIRE damage every tick near
        // a lit kiln (EntityItem.ReceiveDamage -> base sets onHurtCounter/onHurt),
        // which the client blood system watched with no creature filter. Now blood
        // requires a flesh tag, so an item's fire-damage beat is ignored.
        private static readonly string[] BleedTagNames = { "animal", "huntable", "human", "humanoid" };
        private static TagSetFast rustTags, bleedTags;
        private static bool rustTagsResolved, bleedTagsResolved;

        private static void EnsureTags(Entity ent)
        {
            if (rustTagsResolved && bleedTagsResolved) return;
            var reg = ent?.World?.Api?.EntityTagRegistry;
            if (reg == null) return; // registries not up yet - retry next call
            reg.TryCreateTagSet(out rustTags, RustTagNames);
            reg.TryCreateTagSet(out bleedTags, BleedTagNames);
            rustTagsResolved = bleedTagsResolved = true; // resolved once; empty if names not found
        }

        public static bool IsRustCreature(Entity ent)
        {
            if (ent == null) return false;
            EnsureTags(ent);
            if (!rustTagsResolved || rustTags.IsEmpty) return false;
            return ent.Tags.Overlaps(in rustTags);
        }

        /// <summary>Can this entity actually bleed? Players always; anything with a
        /// FLESH tag (animal/huntable/human/humanoid). A health-bearing creature
        /// that predates the 1.22 tag system (older/modded mob with NO tags at all)
        /// still bleeds via the fallback - but a dropped item/log/clay has no health
        /// behavior AND no tags, so it never does. Rust is handled separately in
        /// ShowsBlood (checked first, toggle-gated).</summary>
        public static bool CanBleed(Entity ent)
        {
            if (ent == null) return false;
            if (ent is EntityPlayer) return true; // people bleed
            EnsureTags(ent);
            bool tagsUsable = bleedTagsResolved && !bleedTags.IsEmpty;
            if (tagsUsable && ent.Tags.Overlaps(in bleedTags)) return true;
            // Health-behavior fallback: a real mob with a health behavior bleeds; an
            // item/log/clay (no health behavior) never does. Guarded by IsEmpty when
            // tags ARE usable, so a TAGGED non-creature (boat=inanimate w/ health) is
            // still excluded; if the flesh tags failed to resolve at all (tagsUsable
            // false), fall back to health alone so a tag hiccup never stops animals
            // from bleeding - it degrades to "creatures bleed", never "nothing does".
            if ((!tagsUsable || ent.Tags.IsEmpty) && ent.GetBehavior<EntityBehaviorHealth>() != null) return true;
            return false;
        }

        /// <summary>
        /// Is this entity sitting still? Two ways to be seated and both count: the vanilla
        /// "Sit down" action (EnumEntityAction.FloorSit, read off ServerControls - the copy the
        /// server keeps and the engine's own activity/animation code trusts, EntityAgent.cs:587),
        /// or occupying a seat. A seat with no entity behind it is furniture and always counts;
        /// a seat that IS an entity (raft, tamed elk) counts only while that entity is basically
        /// still, so galloping across the map does not close anyone's wounds. Read through
        /// MountSupplier.OnEntity rather than IMountableSeat.Entity: the vanilla EntitySeat
        /// implementation of the latter casts unguarded and would throw for a seat we did not
        /// anticipate. Animals never satisfy either arm, so this costs them two field reads.
        ///   NOTE the sit flag originates on the client, like every other control. It gates
        /// wound TIMING and nothing else, which is the most it should ever gate.
        /// </summary>
        public static bool IsSeated(Entity ent)
        {
            var agent = ent as EntityAgent;
            if (agent == null) return false;
            var seat = agent.MountedOn;
            if (seat != null)
            {
                var carrier = seat.MountSupplier?.OnEntity;
                return carrier == null || carrier.Pos.Motion.LengthSq() < 0.0001;
            }
            return agent.ServerControls?.FloorSitting == true;
        }

        /// <summary>Should this entity show red blood? Only things that CAN bleed -
        /// players and non-rust creatures. Rust creatures only when the player opted
        /// in. Items, logs, clay, arrows, boats, structures show nothing (they have
        /// no flesh). Sticky arrows and bleed DoT are unaffected; this gates the
        /// VISUAL blood only.</summary>
        public static bool ShowsBlood(Entity ent)
        {
            if (ent == null) return false;
            if (IsRustCreature(ent)) return Cfg.BloodEffectsForRustCreatures;
            return CanBleed(ent);
        }

        // ONE Harmony application per PROCESS, not per ModSystem instance: in
        // single player the client and the local server each get their own
        // instance in the SAME process, and Harmony patches are process-wide â€”
        // patching from both would run every postfix twice (duration x0.25).
        // Applying in Start() (runs on both sides) instead of StartServerSide
        // also puts the harvest patches on REMOTE clients of a dedicated
        // server, where the client times the knife hold.
        private static Harmony harmony;
        private static int harmonyRefs;
        private static readonly object harmonyGate = new object();

        public override void Start(ICoreAPI api)
        {
            // Config on BOTH sides (harvest timing + the highlighter shim are
            // client-side). Re-store after load so new fields show up in the file.
            // 0.3.0 rename: TassHunting.json, falling back once to the legacy
            // TasshroomHunting.json so existing dials survive the rename.
            try
            {
                // Start from FRESH DEFAULTS every load (compat sweep 2026-07-22):
                // Cfg is a static, so without this a corrupt second-world config
                // (load throws below) would keep the PREVIOUS world's tuned values
                // instead of falling back to defaults. Reset first, then load.
                Cfg = new HuntingConfig();
                var loaded = api.LoadModConfig<HuntingConfig>("TassHunting.json")
                          ?? api.LoadModConfig<HuntingConfig>("TasshroomHunting.json");
                if (loaded != null) Cfg = loaded;
                string migrated = Cfg.Migrate();
                if (migrated != null) api.Logger.Notification("[TassHunting] config upgraded - {0}", migrated);
                Cfg.Sanitize();
                api.StoreModConfig(Cfg, "TassHunting.json");
            }
            catch (Exception ex) { api.Logger.Warning("[TassHunting] config load failed: {0}", ex.Message); }

            // The whole Harmony block is try/catch'd: PatchAll or any manual hook
            // throwing (a bad attribute target, or another mod having transpiled a
            // shared method) must NOT abort mod load - the mod degrades to
            // whatever patched successfully. Each manual hook also guards itself.
            lock (harmonyGate)
            {
                harmonyRefs++;
                if (harmony == null)
                {
                    try
                    {
                        harmony = new Harmony("tasshunting");
                        harmony.PatchAll(); // flee-away + harvest overhaul + sticky projectile attribute patches
                        TryPatchTrueAim(api);
                        StickyProjectiles.PatchInterpolationHook(api, harmony);
                        // Startup probes: a reflection/signature-matched patch that
                        // matches NOTHING (a future VS rename) must SURFACE in the
                        // log, not silently disable a feature (diagnostics law).
                        if (Patch_WoundedSlowdown.MatchedCount == 0)
                            api.Logger.Warning("[TassHunting] wounded-slowdown matched 0 speed methods - feature inactive (VS traverser signatures may have changed).");
                        ProbeAiFields(api);
                    }
                    catch (Exception ex)
                    {
                        api.Logger.Error("[TassHunting] Harmony patching failed: {0} - some features may be inactive.", ex.Message);
                    }
                }
            }
        }

        /// <summary>Entity AI numbers and item attributes are rewritten here -
        /// assets are loaded and byType-resolved, no entities have initialized
        /// yet. Archery runs BOTH sides (item attributes exist per side, like
        /// the JSON patches it replaced); AI only on the server (taskai is a
        /// server behavior).</summary>
        public override void AssetsFinalize(ICoreAPI api)
        {
            try { ArcheryTweaks.Apply(api); }
            catch (Exception ex) { api.Logger.Error("[TassHunting] archery tweaks failed: {0}", ex); }
            // Stay-wild runs BOTH sides: the domestication behaviors live in the client
            // behavior list too, and that copy is what would draw a rider and take controls.
            try { StayWild.Apply(api); }
            catch (Exception ex) { api.Logger.Error("[TassHunting] stay-wild apply failed: {0}", ex); }
            if (api.Side != EnumAppSide.Server) return;
            try { PredatorAI.ApplyServer(api); }
            catch (Exception ex) { api.Logger.Error("[TassHunting] PredatorAI apply failed: {0}", ex); }
            try { PredatorAI.ApplySpeed(api); }
            catch (Exception ex) { api.Logger.Error("[TassHunting] predator speed apply failed: {0}", ex); }
            try { CreatureDamageMul.Apply(api); }
            catch (Exception ex) { api.Logger.Error("[TassHunting] creature damage apply failed: {0}", ex); }
            try { Territory.Apply(api); }
            catch (Exception ex) { api.Logger.Error("[TassHunting] territory apply failed: {0}", ex); }
        }

        private ICoreServerAPI sapi;
        private long pickupTickId;

        // VACUUM COUNTERS (diagnostics law, 2026-07-30). The arrow vacuum is the only
        // place this mod touches a player's inventory, so when someone reports items
        // going missing these three numbers say straight away whether it was involved:
        //  - collected: pickups that put something in a player's inventory
        //  - partial:   pickups where only part of a stack fit; the rest stayed on the
        //               ground (this is what the old code used to DELETE)
        //  - no room:   pickups refused outright, nothing moved, item untouched
        // Read them in game with /tasspickup.
        private int pickupCollected, pickupPartial, pickupNoRoom;

        /// <summary>Client side: the server's config json as received this session
        /// (null in single player and before the packet). ConfigLib restore/defaults
        /// re-apply through this so a panel action cannot desync gameplay from the
        /// server mid-session.</summary>
        public static string LastServerConfigJson;

        /// <summary>Re-derive the session config from a fresh local base: on a remote
        /// server the stored server config rules the gameplay fields again; otherwise
        /// the local base stands (sanitized).</summary>
        public static HuntingConfig ReapplyServerRuled(HuntingConfig localBase)
        {
            if (string.IsNullOrEmpty(LastServerConfigJson)) { localBase?.Sanitize(); return localBase; }
            return HuntingConfigSync.BuildSessionConfig(LastServerConfigJson, localBase);
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            StickyProjectiles.StartServer(api);

            // CONFIG SYNC (field report earwiq 2026-08-10, see HuntingConfigSync):
            // the server's config is the world's config. Sent at join AND at
            // now-playing - the handler is idempotent and the double-send costs one
            // small string, cheaper than betting on either event's channel timing.
            try
            {
                var channel = api.Network.RegisterChannel(HuntingConfigSync.ChannelName)
                    .RegisterMessageType<HuntingConfigSyncPacket>();
                void Send(IServerPlayer plr)
                {
                    try
                    {
                        channel.SendPacket(new HuntingConfigSyncPacket { ConfigJson = HuntingConfigSync.Serialize(Cfg) }, plr);
                    }
                    catch (Exception ex) { api.Logger.Warning("[TassHunting] config sync send failed: {0}", ex.Message); }
                }
                api.Event.PlayerJoin += Send;
                api.Event.PlayerNowPlaying += Send;
            }
            catch (Exception ex) { api.Logger.Warning("[TassHunting] config sync channel failed: {0}", ex.Message); }
            if (Cfg.ProjectilePickupRadius > 0f)
                pickupTickId = api.Event.RegisterGameTickListener(PickupTick, 400);

            api.ChatCommands.Create("tasspickup")
                .WithDescription("TassHunting arrow vacuum: what it has collected and what it left on the ground")
                .RequiresPrivilege(Vintagestory.API.Server.Privilege.chat)
                .HandleWith(_ => TextCommandResult.Success(
                    string.Format(
                        "[tasshunting pickup] radius {0} blocks, own arrows only {1}. This session: {2} collected, {3} part-fit (rest left on the ground), {4} refused for no room. Set ProjectilePickupRadius to 0 in TassHunting.json to switch the vacuum off entirely.",
                        Cfg.ProjectilePickupRadius, Cfg.PickupOnlyOwnProjectiles,
                        pickupCollected, pickupPartial, pickupNoRoom)));

            api.Logger.Event("[TassHunting] {0} active (sticky projectiles {1}, spear grab-back {2}, flee-away-from-hunter, predator footstep ranges, projectile pickup radius {3}, harvest overhaul: time x{4}, autodrop {5}, empty-corpse removal {6}, blood visuals {7}, water blood {8}).",
                Mod.Info.Version, Cfg.StickyProjectilesEnabled, Cfg.SpearTouchRetrieve, Cfg.ProjectilePickupRadius, Cfg.HarvestTimeMult, Cfg.HarvestAutoDrop, Cfg.EmptyCorpseAutoRemove, Cfg.BloodVisualsEnabled, Cfg.WaterBloodEnabled);
        }

        /// <summary>Extended projectile pickup: settled arrows/spears within the
        /// configured radius get collected through the ENGINE'S own contract
        /// (CanCollect -> OnCollected -> TryGiveItemStack), so durability, stack
        /// resolution and the collect delay all behave exactly like walking over
        /// them. Riding arrows (sa_target set by StickyArrow) are skipped.</summary>
        private void PickupTick(float dt)
        {
            float radius = Cfg.ProjectilePickupRadius;
            if (radius <= 0f || sapi == null) return;

            foreach (var plr in sapi.World.AllOnlinePlayers)
            {
                var e = (plr as IServerPlayer)?.Entity;
                if (e == null || !e.Alive) continue;
                if (plr.WorldData?.CurrentGameMode == EnumGameMode.Spectator) continue;
                long meId = e.EntityId;

                var found = sapi.World.GetEntitiesAround(e.Pos.XYZ, radius, radius, ent =>
                {
                    // Landed arrows/spears (projectiles) ...
                    var p = ent as Vintagestory.GameContent.EntityProjectileBase;
                    if (p != null)
                    {
                        if (!p.CanCollect(e)) return false;
                        if (ent.WatchedAttributes.GetLong("sa_target", 0L) != 0L) return false; // riding a target
                        if (Cfg.PickupOnlyOwnProjectiles
                            && ent.WatchedAttributes.GetLong("firedBy", 0L) != meId) return false;
                        return true;
                    }
                    // ... AND dropped arrowheads from broken arrows, at the SAME
                    // radius (user 2026-07-22). A head is a ground ItemEntity, not
                    // a projectile - it carries no firedBy, so PickupOnlyOwn cannot
                    // gate it; arrowhead debris is rare and always yours to grab.
                    // CanCollect honors the 1s post-spawn pickup delay (EntityItem)
                    // so it does not vacuum the head the instant it drops.
                    if (ent is EntityItem ei && ei.CanCollect(e)
                        && ei.Itemstack?.Collectible?.Code?.Path is string ip
                        && ip.StartsWith("arrowhead", StringComparison.OrdinalIgnoreCase))
                        return true;
                    return false;
                });

                foreach (var ent in found) Collect(ent, e);
            }
        }

        /// <summary>
        /// One ground pickup, step for step the way the engine does it
        /// (EntityBehaviorCollectEntities.OnFoundCollectible, decompile-verified 1.22.5).
        ///
        /// THE BUG THIS REPLACES (field report 2026-07-30, "items disappearing"): the old
        /// code despawned the ground entity whenever TryGiveItemStack returned TRUE. That
        /// bool does not mean "all of it fit" - PlayerInventoryManager.TryGiveItemstack
        /// returns true when ANY amount moved, and it drains the stack you hand it as it
        /// goes. So a stack that only PARTLY fit had its remainder deleted along with the
        /// entity. The engine never uses that bool to decide despawn: it despawns only
        /// once the source stack is drained (StackSize &lt;= 0), which is the only honest
        /// signal that nothing is left on the ground.
        ///
        /// The two engine steps the old code also skipped are back: the collectible's own
        /// OnCollected hook, and the "onitemcollected" event other mods listen on - a
        /// vacuumed arrow now looks exactly like a walked-over one to everything else.
        /// </summary>
        private void Collect(Entity ent, EntityPlayer e)
        {
            var stack = ent.OnCollected(e);
            if (stack == null || stack.StackSize <= 0) return;

            var announced = stack.Clone();      // pre-pickup contents, for the event
            bool gave = e.TryGiveItemStack(stack); // NOTE: drains stack as it consumes it

            if (stack.StackSize <= 0)
            {
                ent.Die(EnumDespawnReason.PickedUp);
            }
            else
            {
                // Some or none of it fit. The entity stays on the ground with what is
                // left. An EntityItem keeps its stack IN a watched attribute
                // (EntityItem.Itemstack is WatchedAttributes["itemstack"]) and we just
                // mutated it in place, so resend it or the client keeps showing the old
                // count.
                if (gave)
                {
                    ent.WatchedAttributes.MarkPathDirty("itemstack");
                    pickupPartial++;
                }
                else pickupNoRoom++;
            }

            if (!gave) return;

            stack.Collectible?.OnCollected(stack, e);
            var evt = new TreeAttribute();
            evt["itemstack"] = new ItemstackAttribute(announced);
            evt["byentityid"] = new LongAttribute(e.EntityId);
            sapi.Event.PushEvent("onitemcollected", evt);
            sapi.World.PlaySoundAt(new AssetLocation("sounds/player/collect"), ent, null, true, 16f);
            pickupCollected++;
        }

        /// <summary>Startup probe (compat sweep 2026-07-22, diagnostics law): the
        /// AI patches read vanilla private fields BY NAME at runtime via Traverse
        /// (targetEntity/targetPos/targetYaw/entity). A VS rename makes those reads
        /// return null and the patches guard to a SILENT no-op - flee-redirect and
        /// hit-and-run just stop working with no signal. Check the field names
        /// exist on their declaring types once at load and WARN on any miss, so a
        /// VS update surfaces in the log instead of silently disabling a feature.</summary>
        private static void ProbeAiFields(ICoreAPI api)
        {
            try
            {
                (System.Type t, string f)[] probes = new[]
                {
                    (typeof(Vintagestory.GameContent.AiTaskBaseTargetable), "targetEntity"),
                    (typeof(Vintagestory.API.Common.AiTaskBase), "entity"),
                };
                foreach (var (t, f) in probes)
                    if (t != null && AccessTools.Field(t, f) == null)
                        api.Logger.Warning("[TassHunting] AI field '{0}.{1}' not found - flee-redirect / hit-and-run may be inactive (VS AI internals may have changed).", t.Name, f);
            }
            catch (Exception ex) { api.Logger.Warning("[TassHunting] AI field probe failed: {0}", ex.Message); }
        }

        /// <summary>PreInitialize is the surgical moment: FiredBy/Pos/Motion are
        /// set, the entity is not yet spawned. Explicit interface implementation,
        /// so the method is found by reflection rather than name-attribute.
        /// Applied process-wide; dormant on pure clients (FiredBy is null there).</summary>
        private void TryPatchTrueAim(ICoreAPI api)
        {
            try
            {
                // MUST prefer the EXPLICIT interface impl (compat sweep 2026-07-22).
                // typeof(EntityProjectile).GetMethods returns BOTH:
                //  - the inherited EntityProjectileBase.PreInitialize virtual, which
                //    is EMPTY and is NOT what the engine calls at spawn, and
                //  - the explicit IProjectile.PreInitialize impl (name ENDS WITH
                //    ".PreInitialize") which does the real init and IS what both
                //    engine call sites dispatch to (via the interface).
                // A bare FirstOrDefault with an OR predicate could bind the empty
                // base virtual (nondeterministic enumeration order) - then the
                // postfix attaches to a method never invoked at spawn and true-aim
                // silently never fires. Take the explicit impl first, base only as
                // a last-resort fallback.
                var methods = typeof(Vintagestory.GameContent.EntityProjectile).GetMethods(
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                var mi = System.Linq.Enumerable.FirstOrDefault(methods, m => m.Name.EndsWith(".PreInitialize"))
                      ?? System.Linq.Enumerable.FirstOrDefault(methods, m => m.Name == "PreInitialize");
                if (mi == null) { api.Logger.Warning("[TassHunting] PreInitialize not found; true-aim inactive."); return; }
                harmony.Patch(mi, postfix: new HarmonyMethod(typeof(HuntingModSystem), nameof(TrueAimPostfix)));
                // Power shot rides the SAME verified binding: this is the one PreInitialize the
                // engine actually calls at spawn, and the last moment Damage can be scaled.
                harmony.Patch(mi, postfix: new HarmonyMethod(typeof(HuntingModSystem), nameof(PowerShotPostfix)));
                api.Logger.Event("[TassHunting] true-aim spawn correction + power shot active (patched {0}).", mi.Name);
            }
            catch (Exception ex) { api.Logger.Warning("[TassHunting] true-aim patch failed: {0}", ex.Message); }
        }

        public static void PowerShotPostfix(object __instance)
        {
            try { PowerShot.ApplyToProjectile(__instance); }
            catch (Exception) { /* power shot must never break projectile spawning */ }
        }

        public static void TrueAimPostfix(object __instance)
        {
            if (!Cfg.TrueAimSpawnEnabled) return;
            var p = __instance as Vintagestory.GameContent.EntityProjectileBase;
            var shooter = p?.FiredBy as EntityPlayer;
            if (p == null || shooter == null) return;
            var m = p.Pos.Motion;
            double len = m.Length();
            if (len < 0.01) return;
            double f = 0.3 / len; // 0.3 blocks forward along the aim ray
            p.Pos.SetPos(
                shooter.Pos.X + m.X * f,
                shooter.Pos.Y + shooter.LocalEyePos.Y + m.Y * f,
                shooter.Pos.Z + m.Z * f);
        }

        public override void StartClientSide(Vintagestory.API.Client.ICoreClientAPI api)
        {
            // CONFIG SYNC receive: on a remote server the world's gameplay settings
            // replace this client's local ones the moment the server's packet lands;
            // the [ClientPersonal] look-and-feel fields stay this player's own. In
            // single player client and server share the one static Cfg already - skip,
            // so the panel's live edits are never re-pinned to a stale join snapshot.
            LastServerConfigJson = null; // fresh world: no snapshot from the last one
            try
            {
                api.Network.RegisterChannel(HuntingConfigSync.ChannelName)
                    .RegisterMessageType<HuntingConfigSyncPacket>()
                    .SetMessageHandler<HuntingConfigSyncPacket>(pkt =>
                    {
                        try
                        {
                            if (api.IsSinglePlayer || string.IsNullOrEmpty(pkt?.ConfigJson)) return;
                            LastServerConfigJson = pkt.ConfigJson;
                            Cfg = HuntingConfigSync.BuildSessionConfig(pkt.ConfigJson, Cfg);
                            api.Logger.Notification("[TassHunting] gameplay settings synced from the server; look-and-feel settings stay yours.");
                        }
                        catch (Exception ex) { api.Logger.Warning("[TassHunting] config sync apply failed: {0}", ex.Message); }
                    });
            }
            catch (Exception ex) { api.Logger.Warning("[TassHunting] config sync channel failed: {0}", ex.Message); }

            if (api.ModLoader.IsModEnabled("itempickuphighlighter"))
            {
                // Call-site try/catch too (compat sweep 2026-07-22), mirroring the
                // ConfigLib guard below - both soft-dep entry points are now
                // defended at the call site regardless of future edits inside the
                // compat class.
                try { PickupHighlighterCompat.TryPatch(api, harmony); }
                catch (Exception ex) { api.Logger.Warning("[TassHunting] pickup highlighter compat failed: {0}", ex.Message); }
            }

            // In-game config GUI - SOFT dependency: only touch ConfigLib types
            // when the mod is present (the compat class is NoInlining-guarded).
            if (api.ModLoader.IsModEnabled("configlib"))
            {
                try { HuntingConfigLibCompat.Init(api); }
                catch (Exception ex) { api.Logger.Warning("[TassHunting] ConfigLib integration failed: {0}", ex.Message); }
            }
        }

        public override void Dispose()
        {
            try { if (sapi != null && pickupTickId != 0) sapi.Event.UnregisterGameTickListener(pickupTickId); } catch { }
            if (sapi != null) StickyProjectiles.StopServer();
            sapi = null; pickupTickId = 0;
            lock (harmonyGate)
            {
                harmonyRefs--;
                if (harmonyRefs <= 0)
                {
                    try { harmony?.UnpatchAll("tasshunting"); } catch { }
                    harmony = null; harmonyRefs = 0;
                }
            }
        }
    }

    /// <summary>
    /// Vanilla's OnEntityHurt already knows the shooter (targetEntity = damage
    /// cause) â€” TryInstaFlee just doesn't use its position in the blind branch
    /// (decompile-verified). The prefix captures the shooter before the branch
    /// nulls it; the postfix redirects the run into the away arc.
    /// </summary>
    [HarmonyPatch(typeof(AiTaskFleeEntity), "TryInstaFlee")]
    public static class Patch_FleeAwayFromHunter
    {
        public static void Prefix(AiTaskFleeEntity __instance, out Entity __state)
        {
            __state = Traverse.Create(__instance).Field("targetEntity").GetValue<Entity>();
        }

        public static void Postfix(AiTaskFleeEntity __instance, Entity __state)
        {
            if (!HuntingModSystem.Cfg.FleeAwayFromHunterEnabled) return;
            if (__state == null) return; // shooter genuinely unknown: vanilla stands
            var tv = Traverse.Create(__instance);
            // Non-null after the call means the NORMAL in-range flee branch ran â€”
            // it already flees properly; only the blind branch (which nulled it)
            // needs redirecting.
            if (tv.Field("targetEntity").GetValue<Entity>() != null) return;
            var entity = tv.Field("entity").GetValue<EntityAgent>();
            if (entity == null) return;

            double dx = entity.Pos.X - __state.Pos.X;
            double dz = entity.Pos.Z - __state.Pos.Z;
            if (dx * dx + dz * dz < 0.0001) return;
            float awayYaw = (float)Math.Atan2(dx, dz);               // engine yaw: X=sin, Z=cos
            float yaw = awayYaw + (float)((entity.World.Rand.NextDouble() - 0.5) * Math.PI); // random within the away 180
            tv.Field("targetPos").SetValue(new Vec3d(
                entity.Pos.X + Math.Sin(yaw) * 200.0,
                entity.Pos.Y,
                entity.Pos.Z + Math.Cos(yaw) * 200.0));
            tv.Field("targetYaw").SetValue(yaw);
        }
    }
}
