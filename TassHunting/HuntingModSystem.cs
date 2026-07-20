using System;
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
        //      patches; config-gated code since 0.3.0, see ArcheryTweaks.cs) ----
        public bool BowAccuracyEnabled = true;       // all bows: rangedWeaponsAcc 1.0
        public bool UnbreakableArrowsEnabled = true; // all arrows: breakChanceOnImpact 0

        // ---- STACKING HYBRID BLEED (2026-07-19, see BleedSystem.cs; replaces
        //      BloodTrail's damage - keep that mod for its particles, set its
        //      BleedDamageEnabled false) ----
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
            api.Logger.Event("[TassHunting] 0.3.0 active (sticky projectiles {0}, spear grab-back {1}, flee-away-from-hunter, predator footstep ranges, projectile pickup radius {2}, harvest overhaul: time x{3}, autodrop {4}, empty-corpse removal {5}).",
                Cfg.StickyProjectilesEnabled, Cfg.SpearTouchRetrieve, Cfg.ProjectilePickupRadius, Cfg.HarvestTimeMult, Cfg.HarvestAutoDrop, Cfg.EmptyCorpseAutoRemove);
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
