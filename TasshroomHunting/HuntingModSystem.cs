using System;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace TasshroomHunting
{
    /// <summary>
    /// Hunting AI and awareness tweaks (moved OUT of Tasshroom Hardcore Winter
    /// 2026-07-18 — hunting is not winter; every mod stays single-purpose):
    ///  - FLEE AWAY FROM THE HUNTER: hit from beyond seeking range, animals run
    ///    in a random direction within the 180-degree arc AWAY from the shooter
    ///    (vanilla runs blindly in the direction the animal happened to face —
    ///    sometimes straight at the gun). Harmony on AiTaskFleeEntity.TryInstaFlee.
    ///  - PREDATOR FOOTSTEP RANGES (asset patches): wolf stalk 10->22, wolf run
    ///    15->30, bear walk 15->30, bear charge 25->44 — audible before lethal.
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
    }

    public class HuntingModSystem : ModSystem
    {
        public static HuntingConfig Cfg = new HuntingConfig();
        private Harmony harmony;

        public override void Start(ICoreAPI api)
        {
            // Config on BOTH sides (the highlighter shim is client-side).
            try
            {
                var loaded = api.LoadModConfig<HuntingConfig>("TasshroomHunting.json");
                if (loaded != null) Cfg = loaded;
                else api.StoreModConfig(Cfg, "TasshroomHunting.json");
            }
            catch (Exception ex) { api.Logger.Warning("[TasshroomHunting] config load failed: {0}", ex.Message); }
        }

        private ICoreServerAPI sapi;
        private long pickupTickId;

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            harmony = new Harmony("tasshroomhunting");
            harmony.PatchAll();
            TryPatchTrueAim(api);
            if (Cfg.ProjectilePickupRadius > 0f)
                pickupTickId = api.Event.RegisterGameTickListener(PickupTick, 400);
            api.Logger.Event("[TasshroomHunting] active (flee-away-from-hunter, predator footstep ranges, projectile pickup radius {0}).", Cfg.ProjectilePickupRadius);
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
        /// so the method is found by reflection rather than name-attribute.</summary>
        private void TryPatchTrueAim(ICoreServerAPI api)
        {
            try
            {
                var mi = System.Linq.Enumerable.FirstOrDefault(
                    typeof(Vintagestory.GameContent.EntityProjectile).GetMethods(
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic),
                    m => m.Name == "PreInitialize" || m.Name.EndsWith(".PreInitialize"));
                if (mi == null) { api.Logger.Warning("[TasshroomHunting] PreInitialize not found; true-aim inactive."); return; }
                harmony.Patch(mi, postfix: new HarmonyMethod(typeof(HuntingModSystem), nameof(TrueAimPostfix)));
                api.Logger.Event("[TasshroomHunting] true-aim spawn correction active.");
            }
            catch (Exception ex) { api.Logger.Warning("[TasshroomHunting] true-aim patch failed: {0}", ex.Message); }
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
                harmony = harmony ?? new Harmony("tasshroomhunting");
                PickupHighlighterCompat.TryPatch(api, harmony);
            }
        }

        public override void Dispose()
        {
            try { if (sapi != null && pickupTickId != 0) sapi.Event.UnregisterGameTickListener(pickupTickId); } catch { }
            try { harmony?.UnpatchAll("tasshroomhunting"); } catch { }
            harmony = null; sapi = null; pickupTickId = 0;
        }
    }

    /// <summary>
    /// Vanilla's OnEntityHurt already knows the shooter (targetEntity = damage
    /// cause) — TryInstaFlee just doesn't use its position in the blind branch
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
            // Non-null after the call means the NORMAL in-range flee branch ran —
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
