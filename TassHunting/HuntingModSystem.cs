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

    public class HuntingConfig
    {
        public int Version = 1;
        public bool FleeAwayFromHunterEnabled = true;
        // With Item Pickup Highlighter installed: only YOUR projectiles highlight
        // (enemy-thrown stones/arrows stay unmarked). Client-side.
        public bool HighlightOnlyOwnProjectiles = true;

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

        // ---- HARVEST OVERHAUL (playtest 2026-07-19, see HarvestOverhaul.cs) ----
        // Knife harvest hold time multiplier (0.5 = half of vanilla).
        public float HarvestTimeMult = 0.5f;
        // Finished harvest spills loot on the ground and poofs the corpse â€”
        // the carcass window never opens.
        public bool HarvestAutoDrop = true;
        // Player kills roll their loot at death; empty roll (or never-harvestable
        // corpses like bells/locusts) => corpse self-removes after the delay.
        public bool EmptyCorpseAutoRemove = true;
        public float EmptyCorpseRemoveSeconds = 10f;

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
        public float BleedStaticPerTick = 0.05f;       // flat hp per stack per tick
        public float BleedPctMaxHealthPerTick = 0.5f;  // % of max hp per stack per tick
        public bool BleedAffectsPlayers = true;        // PvP: stuck arrows bleed humans too
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
        public bool BloodEffectsForRustCreatures = false;

        // ---- BLOOD VISUALS (see BloodVisuals.cs). In-house blood system:
        //      a spot ledger + water diffusion; the current build renders it
        //      client-locally (the sync layer is parked, see BloodVisuals). ----
        public bool BloodVisualsEnabled = true;
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
        public float BloodRenderDistanceBlocks = 64f;
        public int BloodMaxRenderedSpots = 1200;  // client per-tick render budget
        public bool WaterBloodEnabled = true;     // blood in water diffuses as tiles

        // ---- CORPSE BLOOD (0.11.0, user 2026-07-22: "darken, hold, then fade
        //      BUT if its a corpse then the blood spreads out wider" +
        //      "introduce a corpse config setting again and do corpse blood").
        //      A death pool is the SAME blood look as a trail drop, but the pool
        //      radius is multiplied by CorpseSpreadMult so a kill leaves a wide
        //      spreading pool, not a tight dot. Toggle off to suppress death
        //      pools entirely (bleed trails while alive are unaffected). ----
        public bool CorpseBloodEnabled = true;    // client: render the death pool spread
        public float CorpseSpreadMult = 2.5f;     // client: death pool radius x this (1 = same as a trail spot)
        public float CorpsePoolLifetimeSeconds = 120f; // client: how long the death pool lingers before it dries away (its OWN duration, not the trail's)
        public bool BloodDiagnostics = false;          // client: log what each corpse pool emits (count/size/lifetime), for tuning

        // ---- BLOOD LOOK (0.8.0 - USER-SPEC PANEL: four sections, ONE standard
        //      particle vocabulary per category, everything else json-only.
        //      "Something like this instead of the thousand knobs we have.") ----
        // The two visual systems (user-identified): TRAILS = ground decals
        // (line drops, pools, hit marks). SPLATTER = airborne juice (spurt
        // pulses on the shot / DoT beat, plus falling droplets).
        // Defaults are the user's own field-tuned 2026-07-22 values.
        public BloodParticleLook BloodTrails = new BloodParticleLook
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
        public BloodParticleLook BloodSplatter = new BloodParticleLook
        {
            Enabled = true,
            SizeMin = 0.503f, SizeMax = 1.0f,
            QtyMin = 6, QtyMax = 15,         // particles per spurt pulse (damage-scaled within)
            SpreadMin = 0.4f, SpreadMax = 1.5f, // launch speed range
            LifetimeMin = 45f, LifetimeMax = 60f  // == trails; all blood lasts alike
        };
        // Water Effect
        public bool TintSurroundingWater = true;  // client: render the blood-in-water murk
        // Rain clear speed 0..2 (affects newly deposited blood, trails and
        // splatter marks alike): 0 = rain never clears blood, 1 = rain cuts
        // lifetime in half, 2 = to a third.
        public float RainClearSpeed = 1f;
        // Bleed Damage section extra
        public bool SpawnSplatterOnDamage = true; // splatter on qualifying hits + DoT ticks

        // json-only dials (deliberately NOT in the panel per the 0.8.0 spec)
        public string BloodColorHex = "#74080C";        // FRESH blood (bright)
        // AGED blood (dark, dried): ground spots lerp from fresh -> aged over
        // their lifetime (field 2026-07-22). Set equal to BloodColorHex to
        // disable the age darkening.
        public string BloodColorAgedHex = "#3A0406";
        public float BloodOnHitMinDamage = 0.5f;
        public float BloodTrailScale = 1f;              // server: trail/hit deposit amount
        public float RunningBloodMult = 1.5f;           // server: sprint bleed boost
        public float WaterBloodDecayPerSecond = 0.12f;  // server: water field fade
        public float WaterBloodSpreadPerSecond = 0.02f; // server: water field spread
        public float WaterBloodMaxOpacity = 0.50f;      // client: water blood particle opacity (0.50 = 50%). Direct %, tune from panel.
        public float WaterClotAmount = 1f;              // client: how many murk puffs per beat (sediment recipe fixes their size)

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
        private static TagSetFast rustTags;
        private static bool rustTagsResolved;

        private static void EnsureRustTags(Entity ent)
        {
            if (rustTagsResolved) return;
            var api = ent?.World?.Api;
            var reg = api?.EntityTagRegistry;
            if (reg == null) return; // registries not up yet - retry next call
            reg.TryCreateTagSet(out rustTags, RustTagNames);
            rustTagsResolved = true; // resolved once; empty set if names not found
        }

        public static bool IsRustCreature(Entity ent)
        {
            if (ent == null) return false;
            EnsureRustTags(ent);
            if (!rustTagsResolved || rustTags.IsEmpty) return false;
            return ent.Tags.Overlaps(in rustTags);
        }

        /// <summary>Should this entity show red blood? Rust creatures only when
        /// the player opted in; everything else (animals, players) always shows
        /// blood as before. Sticky arrows and bleed DoT are unaffected by this;
        /// it gates the VISUAL blood only.</summary>
        public static bool ShowsBlood(Entity ent)
        {
            if (ent == null) return false;
            if (IsRustCreature(ent)) return Cfg.BloodEffectsForRustCreatures;
            return true;
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
                Cfg.HarvestTimeMult = Vintagestory.API.MathTools.GameMath.Clamp(Cfg.HarvestTimeMult, 0.05f, 10f);
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
            if (api.Side != EnumAppSide.Server) return;
            try { PredatorAI.ApplyServer(api); }
            catch (Exception ex) { api.Logger.Error("[TassHunting] PredatorAI apply failed: {0}", ex); }
        }

        private ICoreServerAPI sapi;
        private long pickupTickId;

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            StickyProjectiles.StartServer(api);
            if (Cfg.ProjectilePickupRadius > 0f)
                pickupTickId = api.Event.RegisterGameTickListener(PickupTick, 400);
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

                foreach (var ent in found)
                {
                    if (ent is Vintagestory.GameContent.EntityProjectileBase proj)
                    {
                        var stack = proj.OnCollected(e);
                        if (stack == null) continue;
                        if (!e.TryGiveItemStack(stack)) continue; // inventory full: leave it
                        sapi.World.PlaySoundAt(new AssetLocation("sounds/player/collect"), ent, null, true, 16f);
                        ent.Die(EnumDespawnReason.PickedUp);
                    }
                    else if (ent is EntityItem head && head.Itemstack != null)
                    {
                        var stack = head.Itemstack;
                        if (!e.TryGiveItemStack(stack)) continue; // inventory full: leave it
                        sapi.World.PlaySoundAt(new AssetLocation("sounds/player/collect"), ent, null, true, 16f);
                        ent.Die(EnumDespawnReason.PickedUp);
                    }
                }
            }
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
                api.Logger.Event("[TassHunting] true-aim spawn correction active (patched {0}).", mi.Name);
            }
            catch (Exception ex) { api.Logger.Warning("[TassHunting] true-aim patch failed: {0}", ex.Message); }
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
