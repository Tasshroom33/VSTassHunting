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
    }

    public class HuntingModSystem : ModSystem
    {
        public static HuntingConfig Cfg = new HuntingConfig();
        private Harmony harmony;

        public override void StartServerSide(ICoreServerAPI api)
        {
            try
            {
                var loaded = api.LoadModConfig<HuntingConfig>("TasshroomHunting.json");
                if (loaded != null) Cfg = loaded;
                else api.StoreModConfig(Cfg, "TasshroomHunting.json");
            }
            catch (Exception ex) { api.Logger.Warning("[TasshroomHunting] config load failed: {0}", ex.Message); }

            harmony = new Harmony("tasshroomhunting");
            harmony.PatchAll();
            api.Logger.Event("[TasshroomHunting] active (flee-away-from-hunter, predator footstep ranges).");
        }

        public override void Dispose()
        {
            try { harmony?.UnpatchAll("tasshroomhunting"); } catch { }
            harmony = null;
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
