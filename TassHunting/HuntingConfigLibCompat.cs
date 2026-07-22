// In-game config GUI via ConfigLib (same field-proven pattern as
// TasshroomHardcoreWinter's THWConfigLibCompat and BodyFatConfigLibCompat).
//
// SOFT dependency rules (engineering law 1): HuntingModSystem only calls Init
// when the configlib mod is enabled; every ConfigLib/ImGui type stays inside
// this class and Init is NoInlining, so those assemblies are only loaded on
// that call path. Without configlib the mod runs exactly as before.
//
// Knob selection per the user (2026-07-21): bleed DURATION, flat damage per
// tick, and the percent-of-max-health half of the hybrid are panel knobs;
// MAX STACKS stays hardcoded-feeling (json-file only, not in the panel).
//
// Live-ness: sliders edit the LIVE static HuntingModSystem.Cfg, which every
// system reads per call - in single player (client+server share the process
// statics) most dials apply immediately. Load-time features (arrow break
// tuning, predator AI, deposit tick interval) apply on world rejoin - noted
// inline. On a remote server the panel edits only the client side.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ConfigLib;
using ImGuiNET;
using Vintagestory.API.Client;

namespace TassHunting
{
    public static class HuntingConfigLibCompat
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Init(ICoreClientAPI capi)
        {
            capi.ModLoader.GetModSystem<ConfigLibModSystem>().RegisterCustomConfig(
                "Tass Hunting",
                (id, buttons) => Draw(capi, buttons));
            capi.Logger.Event("[TassHunting] ConfigLib panel registered.");
        }

        private static void Draw(ICoreClientAPI capi, ControlButtons buttons)
        {
            var cfg = HuntingModSystem.Cfg;
            if (cfg == null) return;

            if (buttons.Save) capi.StoreModConfig(cfg, "TassHunting.json");
            if (buttons.Restore)
            {
                var loaded = capi.LoadModConfig<HuntingConfig>("TassHunting.json");
                if (loaded != null) HuntingModSystem.Cfg = loaded; // nothing caches Cfg - swap is safe
                return;
            }
            if (buttons.Defaults)
            {
                HuntingModSystem.Cfg = new HuntingConfig();
                return;
            }

            if (!capi.IsSinglePlayer)
                ImGui.TextWrapped("Note: on a remote server these edits only affect your client side.");

            if (ImGui.CollapsingHeader("Bleed damage over time"))
            {
                Checkbox("Bleeding enabled", () => cfg.BleedEnabled, v => cfg.BleedEnabled = v);
                SliderFloat("Bleed duration (seconds per stack)", () => cfg.BleedDurationSeconds, v => cfg.BleedDurationSeconds = v, 5f, 300f);
                SliderFloat("Flat damage per tick per stack", () => cfg.BleedStaticPerTick, v => cfg.BleedStaticPerTick = v, 0f, 2f);
                SliderFloat("Percent of max health per tick per stack", () => cfg.BleedPctMaxHealthPerTick, v => cfg.BleedPctMaxHealthPerTick = v, 0f, 10f);
                SliderFloat("Seconds between bleed ticks", () => cfg.BleedTickSeconds, v => cfg.BleedTickSeconds = v, 1f, 60f);
                SliderInt("Bleed chance percent per qualifying hit", () => cfg.BleedChancePct, v => cfg.BleedChancePct = v, 0, 100);
                SliderFloat("Minimum hit damage to proc", () => cfg.BleedDamageThreshold, v => cfg.BleedDamageThreshold = v, 0f, 10f);
                Checkbox("Player-caused hits only", () => cfg.BleedPlayerCausedOnly, v => cfg.BleedPlayerCausedOnly = v);
                Checkbox("Players bleed too (PvP)", () => cfg.BleedAffectsPlayers, v => cfg.BleedAffectsPlayers = v);
                ImGui.TextWrapped("Max concurrent stacks is intentionally not a panel knob (json file only).");
            }

            if (ImGui.CollapsingHeader("Blood visuals"))
            {
                Checkbox("Blood visuals enabled", () => cfg.BloodVisualsEnabled, v => cfg.BloodVisualsEnabled = v);
                SliderFloat("Blood spot lifetime (seconds)", () => cfg.BloodSpotLifetimeSeconds, v => cfg.BloodSpotLifetimeSeconds = v, 30f, 3600f);
                SliderFloat("Spot spacing (blocks; closer drips grow a pool)", () => cfg.BloodSpotMinSpacingBlocks, v => cfg.BloodSpotMinSpacingBlocks = v, 0.2f, 4f);
                SliderFloat("Corpse bleed-out seconds", () => cfg.CorpseBleedSeconds, v => cfg.CorpseBleedSeconds = v, 0f, 60f);
                SliderFloat("Blood size scale", () => cfg.BloodSizeScale, v => cfg.BloodSizeScale = v, 0.25f, 3f);
                ColorHex("Blood color", () => cfg.BloodColorHex, v => cfg.BloodColorHex = v);
                SliderFloat("Render distance (blocks)", () => cfg.BloodRenderDistanceBlocks, v => cfg.BloodRenderDistanceBlocks = v, 16f, 128f);
                SliderInt("Max rendered spots (client budget)", () => cfg.BloodMaxRenderedSpots, v => cfg.BloodMaxRenderedSpots = v, 100, 4000);
                SliderInt("Max ledger spots (server cap)", () => cfg.BloodMaxSpots, v => cfg.BloodMaxSpots = v, 256, 16384);
                SliderFloat("Deposit interval seconds (rejoin to apply)", () => cfg.BloodDepositIntervalSeconds, v => cfg.BloodDepositIntervalSeconds = v, 0.1f, 2f);
            }

            if (ImGui.CollapsingHeader("Blood in water"))
            {
                Checkbox("Water blood enabled", () => cfg.WaterBloodEnabled, v => cfg.WaterBloodEnabled = v);
                SliderFloat("Decay per second (fraction of a tile's blood)", () => cfg.WaterBloodDecayPerSecond, v => cfg.WaterBloodDecayPerSecond = v, 0.02f, 0.5f);
                SliderFloat("Spread per second (to each liquid neighbor)", () => cfg.WaterBloodSpreadPerSecond, v => cfg.WaterBloodSpreadPerSecond = v, 0f, 0.1f);
            }

            if (ImGui.CollapsingHeader("Archery"))
            {
                Checkbox("Arrow break tuning enabled (rejoin to apply)", () => cfg.ArrowBreakTuningEnabled, v => cfg.ArrowBreakTuningEnabled = v);
                Checkbox("True-aim spawn correction", () => cfg.TrueAimSpawnEnabled, v => cfg.TrueAimSpawnEnabled = v);
                if (cfg.ArrowBreakChanceByMaterial != null && cfg.ArrowBreakChanceByMaterial.Count > 0)
                {
                    ImGui.TextWrapped("Break chance per arrow material, 0 = never breaks (rejoin to apply):");
                    var keys = new List<string>(cfg.ArrowBreakChanceByMaterial.Keys);
                    keys.Sort(StringComparer.Ordinal);
                    foreach (string key in keys)
                    {
                        string k = key;
                        SliderFloat("  " + k, () => cfg.ArrowBreakChanceByMaterial[k], v => cfg.ArrowBreakChanceByMaterial[k] = v, 0f, 1f);
                    }
                }
            }

            if (ImGui.CollapsingHeader("Predators and AI"))
            {
                Checkbox("Animals flee AWAY from the hunter", () => cfg.FleeAwayFromHunterEnabled, v => cfg.FleeAwayFromHunterEnabled = v);
                Checkbox("Predator overhaul (rejoin to apply)", () => cfg.PredatorOverhaulEnabled, v => cfg.PredatorOverhaulEnabled = v);
                Checkbox("Wounded slowdown", () => cfg.WoundedSlowdownEnabled, v => cfg.WoundedSlowdownEnabled = v);
            }

            if (ImGui.CollapsingHeader("Sticky projectiles"))
            {
                Checkbox("Arrows/spears ride the animal they hit", () => cfg.StickyProjectilesEnabled, v => cfg.StickyProjectilesEnabled = v);
                SliderFloat("Riding projectile lifetime (seconds)", () => cfg.StickSeconds, v => cfg.StickSeconds = v, 30f, 900f);
                Checkbox("Spears grabbable back at touch range", () => cfg.SpearTouchRetrieve, v => cfg.SpearTouchRetrieve = v);
            }

            if (ImGui.CollapsingHeader("Harvest and pickup"))
            {
                SliderFloat("Knife harvest time multiplier", () => cfg.HarvestTimeMult, v => cfg.HarvestTimeMult = v, 0.05f, 2f);
                Checkbox("Harvest drops loot on the ground", () => cfg.HarvestAutoDrop, v => cfg.HarvestAutoDrop = v);
                Checkbox("Empty corpses self-remove", () => cfg.EmptyCorpseAutoRemove, v => cfg.EmptyCorpseAutoRemove = v);
                SliderFloat("Empty corpse removal delay (seconds)", () => cfg.EmptyCorpseRemoveSeconds, v => cfg.EmptyCorpseRemoveSeconds = v, 1f, 120f);
                SliderFloat("Projectile pickup radius (0 = vanilla only)", () => cfg.ProjectilePickupRadius, v => cfg.ProjectilePickupRadius = v, 0f, 16f);
                Checkbox("Only vacuum your own projectiles", () => cfg.PickupOnlyOwnProjectiles, v => cfg.PickupOnlyOwnProjectiles = v);
            }
        }

        // ImGui needs ref locals; bridge with getter/setter pairs that only
        // write back on an actual edit.
        private static void SliderFloat(string label, Func<float> get, Action<float> set, float min, float max)
        {
            float value = get();
            if (ImGui.SliderFloat(label, ref value, min, max)) set(value);
        }

        private static void SliderInt(string label, Func<int> get, Action<int> set, int min, int max)
        {
            int value = get();
            if (ImGui.SliderInt(label, ref value, min, max)) set(value);
        }

        private static void Checkbox(string label, Func<bool> get, Action<bool> set)
        {
            bool value = get();
            if (ImGui.Checkbox(label, ref value)) set(value);
        }

        private static void ColorHex(string label, Func<string> get, Action<string> set)
        {
            var vec = HexToVec3(get());
            if (ImGui.ColorEdit3(label, ref vec)) set(Vec3ToHex(vec));
        }

        private static System.Numerics.Vector3 HexToVec3(string hex)
        {
            try
            {
                string h = (hex ?? "").TrimStart('#');
                if (h.Length != 6) return new System.Numerics.Vector3(0.45f, 0.03f, 0.05f);
                return new System.Numerics.Vector3(
                    Convert.ToInt32(h.Substring(0, 2), 16) / 255f,
                    Convert.ToInt32(h.Substring(2, 2), 16) / 255f,
                    Convert.ToInt32(h.Substring(4, 2), 16) / 255f);
            }
            catch { return new System.Numerics.Vector3(0.45f, 0.03f, 0.05f); }
        }

        private static string Vec3ToHex(System.Numerics.Vector3 v)
        {
            int r = Math.Clamp((int)(v.X * 255f + 0.5f), 0, 255);
            int g = Math.Clamp((int)(v.Y * 255f + 0.5f), 0, 255);
            int b = Math.Clamp((int)(v.Z * 255f + 0.5f), 0, 255);
            return $"#{r:X2}{g:X2}{b:X2}";
        }
    }
}
