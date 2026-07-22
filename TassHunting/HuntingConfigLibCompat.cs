// In-game config GUI via ConfigLib (same field-proven pattern as
// TasshroomHardcoreWinter's THWConfigLibCompat and BodyFatConfigLibCompat).
//
// SOFT dependency rules (engineering law 1): HuntingModSystem only calls Init
// when the configlib mod is enabled; every ConfigLib/ImGui type stays inside
// this class and Init is NoInlining, so those assemblies are only loaded on
// that call path. Without configlib the mod runs exactly as before.
//
// LABELING RULES (user pass 2026-07-21: "hard to read/understand as a human"):
// short player-language labels (the panel is narrow and clips long ones), no
// spec jargon, explanations on their own wrapped help lines, SERVER gameplay
// sections separated from CLIENT look sections, blood trails separated from
// corpse blood. Max bleed stacks stays json-only by user decision.
//
// Live-ness: sliders edit the LIVE static HuntingModSystem.Cfg, which every
// system reads per call - in single player nearly every dial applies
// immediately (the drip-rate tick self-paces from config since 0.6.5). The
// few load-time features say "needs world rejoin" in their help line. On a
// remote server the panel edits only the client side.

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
                ImGui.TextWrapped("You are on a server: these edits only change YOUR side. The server's own config decides gameplay.");

            if (ImGui.CollapsingHeader("Bleeding"))
            {
                Checkbox("Enable bleeding", () => cfg.BleedEnabled, v => cfg.BleedEnabled = v);
                SliderFloat("Bleed time (sec)", () => cfg.BleedDurationSeconds, v => cfg.BleedDurationSeconds = v, 5f, 300f);
                SliderFloat("Damage per tick", () => cfg.BleedStaticPerTick, v => cfg.BleedStaticPerTick = v, 0f, 2f);
                SliderFloat("Extra damage, % of max HP", () => cfg.BleedPctMaxHealthPerTick, v => cfg.BleedPctMaxHealthPerTick = v, 0f, 10f);
                Help("Each tick deals the flat damage PLUS this percent of the animal's max health - small and big animals both feel it.");
                SliderFloat("Tick every (sec)", () => cfg.BleedTickSeconds, v => cfg.BleedTickSeconds = v, 1f, 60f);
                SliderInt("Bleed chance (%)", () => cfg.BleedChancePct, v => cfg.BleedChancePct = v, 0, 100);
                SliderFloat("Min damage to start", () => cfg.BleedDamageThreshold, v => cfg.BleedDamageThreshold = v, 0f, 10f);
                Checkbox("Player attacks only", () => cfg.BleedPlayerCausedOnly, v => cfg.BleedPlayerCausedOnly = v);
                Checkbox("Players can bleed (PvP)", () => cfg.BleedAffectsPlayers, v => cfg.BleedAffectsPlayers = v);
                Help("Repeat hits can stack bleeds. The stack cap lives in TassHunting.json only.");
            }

            if (ImGui.CollapsingHeader("Blood trails"))
            {
                Checkbox("Enable blood", () => cfg.BloodVisualsEnabled, v => cfg.BloodVisualsEnabled = v);
                SliderFloat("Trail blood amount", () => cfg.BloodTrailScale, v => cfg.BloodTrailScale = v, 0f, 3f);
                Help("How much blood a wounded animal leaves behind. 0 = no trail.");
                Checkbox("Blood on hit", () => cfg.BloodOnHitEnabled, v => cfg.BloodOnHitEnabled = v);
                SliderFloat("Min damage for hit blood", () => cfg.BloodOnHitMinDamage, v => cfg.BloodOnHitMinDamage = v, 0f, 5f);
                SliderFloat("Drip every (sec)", () => cfg.BloodDepositIntervalSeconds, v => cfg.BloodDepositIntervalSeconds = v, 0.25f, 2f);
                Help("Lower = denser trail. A standing animal merges its drips into one growing pool.");
                SliderFloat("Blood lasts (sec)", () => cfg.BloodSpotLifetimeSeconds, v => cfg.BloodSpotLifetimeSeconds = v, 30f, 3600f);
                SliderFloat("Particle size, min", () => cfg.BloodParticleSizeMin, v => cfg.BloodParticleSizeMin = v, 0.05f, 2f);
                SliderFloat("Particle size, max", () => cfg.BloodParticleSizeMax, v => cfg.BloodParticleSizeMax = v, 0.05f, 2.5f);
                Help("Single drips sit near the min size, big pools near the max.");
                SliderInt("Particles per spot, min", () => cfg.BloodParticlesMin, v => cfg.BloodParticlesMin = v, 1, 8);
                SliderInt("Particles per spot, max", () => cfg.BloodParticlesMax, v => cfg.BloodParticlesMax = v, 1, 12);
                SliderInt("Max blood spots in world", () => cfg.BloodMaxSpots, v => cfg.BloodMaxSpots = v, 256, 16384);
            }

            if (ImGui.CollapsingHeader("Corpse blood"))
            {
                SliderFloat("Corpse blood amount", () => cfg.CorpseBloodScale, v => cfg.CorpseBloodScale = v, 0f, 3f);
                Help("How much a kill bleeds out where it died. 0 = no death pool.");
                SliderFloat("Bleed-out time (sec)", () => cfg.CorpseBleedSeconds, v => cfg.CorpseBleedSeconds = v, 0f, 60f);
            }

            if (ImGui.CollapsingHeader("Blood in water"))
            {
                Checkbox("Enable water blood", () => cfg.WaterBloodEnabled, v => cfg.WaterBloodEnabled = v);
                SliderFloat("Fade speed", () => cfg.WaterBloodDecayPerSecond, v => cfg.WaterBloodDecayPerSecond = v, 0.02f, 0.5f);
                SliderFloat("Spread speed", () => cfg.WaterBloodSpreadPerSecond, v => cfg.WaterBloodSpreadPerSecond = v, 0f, 0.1f);
                Help("Blood in water spreads to neighboring water and fades out. Higher fade = shorter-lived stains.");
            }

            if (ImGui.CollapsingHeader("Blood look (your screen only)"))
            {
                ColorHex("Blood color", () => cfg.BloodColorHex, v => cfg.BloodColorHex = v);
                SliderFloat("Redraw every (sec)", () => cfg.BloodRefreshSeconds, v => cfg.BloodRefreshSeconds = v, 1f, 15f);
                Help("Blood is drawn with particles that refresh on this cycle. Lower = steadier look, slightly more particle load.");
                SliderFloat("Water stain opacity", () => cfg.WaterBloodMaxOpacity, v => cfg.WaterBloodMaxOpacity = v, 0.05f, 1f);
                SliderFloat("View distance (blocks)", () => cfg.BloodRenderDistanceBlocks, v => cfg.BloodRenderDistanceBlocks = v, 16f, 128f);
                SliderInt("Max spots drawn", () => cfg.BloodMaxRenderedSpots, v => cfg.BloodMaxRenderedSpots = v, 100, 4000);
            }

            if (ImGui.CollapsingHeader("Archery"))
            {
                Checkbox("Arrows can break", () => cfg.ArrowBreakTuningEnabled, v => cfg.ArrowBreakTuningEnabled = v);
                Checkbox("Arrows fly from your crosshair", () => cfg.TrueAimSpawnEnabled, v => cfg.TrueAimSpawnEnabled = v);
                Help("Fixes close shots landing high (vanilla spawns the arrow behind your head).");
                if (cfg.ArrowBreakChanceByMaterial != null && cfg.ArrowBreakChanceByMaterial.Count > 0)
                {
                    ImGui.TextWrapped("Break chance on impact per arrow type. 0 = never breaks, 0.25 = breaks 1 in 4. Needs world rejoin.");
                    var keys = new List<string>(cfg.ArrowBreakChanceByMaterial.Keys);
                    keys.Sort(StringComparer.Ordinal);
                    foreach (string key in keys)
                    {
                        string k = key;
                        SliderFloat(k + " arrow", () => cfg.ArrowBreakChanceByMaterial[k], v => cfg.ArrowBreakChanceByMaterial[k] = v, 0f, 1f);
                    }
                }
            }

            if (ImGui.CollapsingHeader("Animals"))
            {
                Checkbox("Animals flee away from you", () => cfg.FleeAwayFromHunterEnabled, v => cfg.FleeAwayFromHunterEnabled = v);
                Help("Vanilla sometimes makes a shot animal run straight at the shooter.");
                Checkbox("Tougher predators", () => cfg.PredatorOverhaulEnabled, v => cfg.PredatorOverhaulEnabled = v);
                Help("Bears charge from further and never give up; wolves pack up. Needs world rejoin.");
                Checkbox("Wounded animals slow down", () => cfg.WoundedSlowdownEnabled, v => cfg.WoundedSlowdownEnabled = v);
            }

            if (ImGui.CollapsingHeader("Stuck arrows and spears"))
            {
                Checkbox("Arrows stick in animals", () => cfg.StickyProjectilesEnabled, v => cfg.StickyProjectilesEnabled = v);
                SliderFloat("Stuck arrow lifetime (sec)", () => cfg.StickSeconds, v => cfg.StickSeconds = v, 30f, 900f);
                Help("A stuck arrow disappears after this long if the animal never dies.");
                Checkbox("Grab stuck spears back", () => cfg.SpearTouchRetrieve, v => cfg.SpearTouchRetrieve = v);
            }

            if (ImGui.CollapsingHeader("Harvest and pickup"))
            {
                SliderFloat("Harvest time (x vanilla)", () => cfg.HarvestTimeMult, v => cfg.HarvestTimeMult = v, 0.05f, 2f);
                Help("0.5 = knife work takes half as long.");
                Checkbox("Loot drops on the ground", () => cfg.HarvestAutoDrop, v => cfg.HarvestAutoDrop = v);
                Checkbox("Remove empty corpses", () => cfg.EmptyCorpseAutoRemove, v => cfg.EmptyCorpseAutoRemove = v);
                SliderFloat("Remove after (sec)", () => cfg.EmptyCorpseRemoveSeconds, v => cfg.EmptyCorpseRemoveSeconds = v, 1f, 120f);
                SliderFloat("Arrow pickup range (0 = off)", () => cfg.ProjectilePickupRadius, v => cfg.ProjectilePickupRadius = v, 0f, 16f);
                Help("Walk within this range of your landed arrows and spears to collect them automatically.");
                Checkbox("Pick up your own only", () => cfg.PickupOnlyOwnProjectiles, v => cfg.PickupOnlyOwnProjectiles = v);
            }
        }

        // ---- helpers ----

        private static void Help(string text)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.62f, 0.62f, 0.62f, 1f));
            ImGui.TextWrapped(text);
            ImGui.PopStyleColor();
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
