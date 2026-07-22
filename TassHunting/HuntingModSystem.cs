using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace TassHunting
{
    /// <summary>
    /// Hunting AI and awareness tweaks (moved OUT of Tasshroom Hardcore Winter
    /// 2026-07-18 â€” hunting is not winter; every mod stays single-purpose):
    ///  - FLEE AWAY FROM THE HUNTER: hit from beyond seeking range, animals run
    ///    in a random direction within the 180-degree arc AWAY from the shooter
    ///    (vanilla runs blindly in the direction the animal happened to face â€”
    ///    sometimes straight at the gun). Harmony on AiTaskFleeEntity.TryInstaFlee.
    ///  - PREDATOR FOOTSTEP RANGES (asset patches): wolf stalk 10->22, wolf run
    ///    15->30, bear walk 15->30, bear charge 25->44 â€” audible before lethal.
    /// Future home for the awareness layer (goals Hunting 5).
    /// </summary>
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

        // ---- STICKY PROJECTILES (absorbed from StickyArrow 0.1.1, 2026-07-19,
        //      see StickyProjectiles.cs) ----
        // Master: arrows/spears ride the animal they hit instead of vanishing.
        public bool StickyProjectilesEnabled = true;
        // Riding projectile despawns after this long if the animal never dies.
        public float StickSeconds = 300f;
        // A stuck SPEAR can be grabbed back at vanilla touch range (arrows stay
        // uncollectible until released — walking near must not yank them out).
        public bool SpearTouchRetrieve = true;
        // Body-ellipse anchoring (goat-flank playtest): how deep past the body
        // surface the arrow embeds, and how wide the body is across vs along
        // the spine (collision boxes are square; real bodies aren't).
        public float StickEmbedFraction = 0.35f;
        public float StickBodyWidthFraction = 0.45f;

        // ---- ARCHERY (absorbed from AccurateArchery via the 0.0.5 asset
        //      patches; config-gated code since 0.3.0, see ArcheryTweaks.cs).
        //      0.6.2: bow accuracy flatten REMOVED - bows are pure vanilla
        //      (crude -0.05 .. recurve +0.3); only arrows are tuned now. ----

        // Per-material arrow break chance (0.6.1; replaces the 0.3.0-0.6.0
        // UnbreakableArrowsEnabled blanket zero, which had flattened the old
        // AccurateArchery per-line list). USER CURVE 2026-07-21, halving per
        // tech tier working back from steel-never-breaks:
        //   reed 32% -> neolithic 16% -> stone 8% -> copper 4% ->
        //   bronze 2% -> iron 1% -> steel 0%.
        // Keys match the arrow code suffix (arrow-<material>). Materials NOT
        // listed here (modded arrows) are left completely untouched - they
        // keep whatever their own mod ships. Values clamp 0..1.
        public bool ArrowBreakTuningEnabled = true;
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

        // ---- STACKING HYBRID BLEED (2026-07-19, see BleedSystem.cs; damage
        //      half. 0.6.0: visuals now in-house too - BloodTrail fully
        //      replaced, remove it from the stack) ----
        public bool BleedEnabled = true;
        public int BleedMaxStacks = 3;
        public float BleedTickSeconds = 10f;
        public float BleedStaticPerTick = 0.1f;        // flat hp per stack per tick
        public float BleedPctMaxHealthPerTick = 1f;    // % of max hp per stack per tick
        public float BleedDurationSeconds = 60f;       // per stack; at cap a new hit refreshes the shortest
        public int BleedChancePct = 80;                // per qualifying hit
        public float BleedDamageThreshold = 1f;        // min post-mitigation hit damage
        public bool BleedPlayerCausedOnly = true;
        public bool BleedAffectsPlayers = true;        // PvP: humans bleed too

        // ---- BLOOD VISUALS (0.6.0, see BloodVisuals.cs; replaces BloodTrail
        //      entirely). Server-authoritative spot ledger + water diffusion,
        //      per-player proximity-scoped sync - late joiners and players
        //      walking up to old blood see the same trail as everyone else. ----
        public bool BloodVisualsEnabled = true;
        // How long a ground blood spot stays followable (REAL seconds - same
        // law-7 combat-pacing carve-out as the bleed itself). DELIBERATELY not
        // matched to BloodTrail's 10s default: minutes-long trails are the
        // whole point of the synced ledger (late joiner follows the trail).
        public float BloodSpotLifetimeSeconds = 600f;
        // Server deposit cadence while something bleeds. 0.25s = 4 drips/sec
        // (0.6.9, nearest we go to BloodTrail's 12.5/s default: their drops
        // evaporated in 10s so they had to spray; ours persist and re-render,
        // so spatial density is what matters, and every drip is a synced spot).
        public float BloodDepositIntervalSeconds = 0.25f;
        // Merge threshold, NOT a density dial (density = drip interval): drips
        // closer together than this GROW the previous spot into a pool, so
        // stationary/dying animals pool instead of spamming spots. Demoted to
        // json-only 0.6.7 (user: reads as redundant next to drip rate).
        public float BloodSpotMinSpacingBlocks = 0.8f;
        public int BloodMaxSpots = 4096;          // server ledger cap, oldest pruned
        public float BloodRenderDistanceBlocks = 64f;
        public int BloodMaxRenderedSpots = 1200;  // client per-tick render budget
        public float CorpseBleedSeconds = 4f;     // death pool keeps growing this long (BT-default-adjacent, 0.6.9)
        public bool WaterBloodEnabled = true;     // blood in water diffuses as tiles

        // 0.6.3 look/feel dials (in-game panel via ConfigLib when installed)
        public string BloodColorHex = "#74080C";        // client: ground blood color
        public float WaterBloodDecayPerSecond = 0.12f;  // server: fraction of a water tile's blood lost per second
        public float WaterBloodSpreadPerSecond = 0.02f; // server: fraction leaked to each liquid neighbor per second

        // 0.6.4 (field regressions): blood visuals independent of the DoT proc
        public bool BloodOnHitEnabled = true;      // splash on every qualifying hit, proc or not
        public float BloodOnHitMinDamage = 0.5f;   // min post-mitigation damage for contact blood
        public float CorpseBloodScale = 1f;        // scales death pools + corpse bleed-out (0 = off)
        public float WaterBloodMaxOpacity = 0.6f;  // client: water stain tint ceiling

        // 0.6.5 (user: min/max particle + timing knobs were hardcoded; trail
        // amount needed the same lever corpse blood already had)
        public int BloodParticlesMin = 1;          // client: particles for the smallest blood spot (BT: 1)
        public int BloodParticlesMax = 6;          // client: particles for the biggest pool (BT running: 3-7)
        public float BloodRefreshSeconds = 4f;     // client: how often each spot redraws its particles
        public float BloodTrailScale = 1f;         // server: scales trail drips + hit splashes (0 = off)

        // 0.6.7 (user's tuning model: size min/max + drop rate + duration).
        // Replaces BloodSizeScale (redundant once real size bounds exist).
        // Bigger pools bias toward max, single drips toward min.
        public float BloodParticleSizeMin = 0.3f;  // client (BT default min 0.3)
        public float BloodParticleSizeMax = 0.8f;  // client (BT running max 0.8; our max = big pools)

        // ---- 0.7.0: the BloodTrail feature-gap batch (user: build 1-5;
        //      falling droplets was the "ours felt wrong" culprit) ----
        public bool BloodRainEnabled = true;            // server: rain shortens NEW blood's life
        public float BloodRainLifetimeSeconds = 300f;   // server: lifetime for blood deposited in rain (BT halved: 10 to 5)
        public float RunningBloodMult = 1.5f;           // server: sprinting animals bleed harder (1 = off; BT 3-7 particles running)
        public bool FallingDropletsEnabled = true;      // client: droplets visibly fall from elevated wounds; splat lands with them
        public float BloodScatter = 0.05f;              // client: droplet/splash scatter velocity (BT BloodSpread 0.05)

        // ---- 0.7.1 (playtest: line-not-spurts, water too heavy) ----
        // Trails render as a dotted LINE along the animal's path (client lays
        // drops between synced anchor spots); spurts happen ONLY on damage
        // beats (the shot, each bleed DoT tick).
        public float TrailDropsPerBlock = 3f;      // client: line density (bigger = more drops)
        // 0.7.3: fade belongs to the SPOT's end of life, not the particle
        // cycle (0.7.1 bug: every splat faded out per 4.6s cycle = slow blink)
        public float BloodFadeSeconds = 10f;       // client: the last N seconds of a spot fade + sink
        public float WaterClotSizeMin = 0.25f;     // client: water clot size range
        public float WaterClotSizeMax = 0.7f;
        public float WaterClotAmount = 1f;         // client: scales clot count (0 = none)

        // ---- WOUNDED SLOWDOWN (2026-07-19, see WoundedSlowdown.cs; replaces
        //      FleeExhaustion - all AI states, tiered, per the user's table) ----
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
                var loaded = api.LoadModConfig<HuntingConfig>("TassHunting.json")
                          ?? api.LoadModConfig<HuntingConfig>("TasshroomHunting.json");
                if (loaded != null) Cfg = loaded;
                Cfg.HarvestTimeMult = Vintagestory.API.MathTools.GameMath.Clamp(Cfg.HarvestTimeMult, 0.05f, 10f);
                api.StoreModConfig(Cfg, "TassHunting.json");
            }
            catch (Exception ex) { api.Logger.Warning("[TassHunting] config load failed: {0}", ex.Message); }

            lock (harmonyGate)
            {
                harmonyRefs++;
                if (harmony == null)
                {
                    harmony = new Harmony("tasshunting");
                    harmony.PatchAll(); // flee-away + harvest overhaul + sticky projectile attribute patches
                    TryPatchTrueAim(api);
                    StickyProjectiles.PatchInterpolationHook(api, harmony);
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
                    var p = ent as Vintagestory.GameContent.EntityProjectileBase;
                    if (p == null || !p.CanCollect(e)) return false;
                    if (ent.WatchedAttributes.GetLong("sa_target", 0L) != 0L) return false; // riding a target
                    if (Cfg.PickupOnlyOwnProjectiles
                        && ent.WatchedAttributes.GetLong("firedBy", 0L) != meId) return false;
                    return true;
                });

                foreach (var ent in found)
                {
                    var stack = (ent as Vintagestory.GameContent.EntityProjectileBase)?.OnCollected(e);
                    if (stack == null) continue;
                    if (!e.TryGiveItemStack(stack)) continue; // inventory full: leave it
                    sapi.World.PlaySoundAt(new AssetLocation("sounds/player/collect"), ent, null, true, 16f);
                    ent.Die(EnumDespawnReason.PickedUp);
                }
            }
        }

        /// <summary>PreInitialize is the surgical moment: FiredBy/Pos/Motion are
        /// set, the entity is not yet spawned. Explicit interface implementation,
        /// so the method is found by reflection rather than name-attribute.
        /// Applied process-wide; dormant on pure clients (FiredBy is null there).</summary>
        private void TryPatchTrueAim(ICoreAPI api)
        {
            try
            {
                var mi = System.Linq.Enumerable.FirstOrDefault(
                    typeof(Vintagestory.GameContent.EntityProjectile).GetMethods(
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic),
                    m => m.Name == "PreInitialize" || m.Name.EndsWith(".PreInitialize"));
                if (mi == null) { api.Logger.Warning("[TassHunting] PreInitialize not found; true-aim inactive."); return; }
                harmony.Patch(mi, postfix: new HarmonyMethod(typeof(HuntingModSystem), nameof(TrueAimPostfix)));
                api.Logger.Event("[TassHunting] true-aim spawn correction active.");
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
                PickupHighlighterCompat.TryPatch(api, harmony);
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
