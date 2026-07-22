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

            // ---- BLOOD (0.8.0): exactly the user-spec four sections with the
            //      ONE standard particle vocabulary. Everything else json-only.

            if (ImGui.CollapsingHeader("Blood Trails"))
            {
                Checkbox("Enable blood trails##trails", () => cfg.BloodTrails.Enabled, v => cfg.BloodTrails.Enabled = v);
                SliderFloat("Particle size, min##trails", () => cfg.BloodTrails.SizeMin, v => cfg.BloodTrails.SizeMin = v, 0.05f, 2f);
                SliderFloat("Particle size, max##trails", () => cfg.BloodTrails.SizeMax, v => cfg.BloodTrails.SizeMax = v, 0.05f, 2f);
                SliderInt("Particle qty, min##trails", () => cfg.BloodTrails.QtyMin, v => cfg.BloodTrails.QtyMin = v, 1, 12);
                SliderInt("Particle qty, max##trails", () => cfg.BloodTrails.QtyMax, v => cfg.BloodTrails.QtyMax = v, 1, 12);
                Help("Qty = drops per block of trail, and particles per pool. Heavier bleeding pushes toward max.");
                SliderFloat("Particle spread, min##trails", () => cfg.BloodTrails.SpreadMin, v => cfg.BloodTrails.SpreadMin = v, 0f, 1f);
                SliderFloat("Particle spread, max##trails", () => cfg.BloodTrails.SpreadMax, v => cfg.BloodTrails.SpreadMax = v, 0f, 1f);
                SliderFloat("Particle lifetime, min (sec)##trails", () => cfg.BloodTrails.LifetimeMin, v => cfg.BloodTrails.LifetimeMin = v, 10f, 3600f);
                SliderFloat("Particle lifetime, max (sec)##trails", () => cfg.BloodTrails.LifetimeMax, v => cfg.BloodTrails.LifetimeMax = v, 10f, 3600f);
                Help("Each drop lives a random time in this range, so trails dry up drop by drop, oldest parts first.");
            }

            if (ImGui.CollapsingHeader("Blood Splatter"))
            {
                Checkbox("Enable splatter##splat", () => cfg.BloodSplatter.Enabled, v => cfg.BloodSplatter.Enabled = v);
                SliderFloat("Particle size, min##splat", () => cfg.BloodSplatter.SizeMin, v => cfg.BloodSplatter.SizeMin = v, 0.02f, 1f);
                SliderFloat("Particle size, max##splat", () => cfg.BloodSplatter.SizeMax, v => cfg.BloodSplatter.SizeMax = v, 0.02f, 1f);
                SliderInt("Particle qty, min##splat", () => cfg.BloodSplatter.QtyMin, v => cfg.BloodSplatter.QtyMin = v, 1, 40);
                SliderInt("Particle qty, max##splat", () => cfg.BloodSplatter.QtyMax, v => cfg.BloodSplatter.QtyMax = v, 1, 40);
                SliderFloat("Particle spread, min##splat", () => cfg.BloodSplatter.SpreadMin, v => cfg.BloodSplatter.SpreadMin = v, 0.1f, 4f);
                SliderFloat("Particle spread, max##splat", () => cfg.BloodSplatter.SpreadMax, v => cfg.BloodSplatter.SpreadMax = v, 0.1f, 4f);
                Help("Spread = how hard the blood launches from the wound (arcs up and over).");
                SliderFloat("Particle lifetime, min (sec)##splat", () => cfg.BloodSplatter.LifetimeMin, v => cfg.BloodSplatter.LifetimeMin = v, 0.2f, 5f);
                SliderFloat("Particle lifetime, max (sec)##splat", () => cfg.BloodSplatter.LifetimeMax, v => cfg.BloodSplatter.LifetimeMax = v, 0.2f, 5f);
            }

            if (ImGui.CollapsingHeader("Water Effect"))
            {
                Checkbox("Tint surrounding water", () => cfg.TintSurroundingWater, v => cfg.TintSurroundingWater = v);
                Checkbox("Softer water particles", () => cfg.SoftWaterParticles, v => cfg.SoftWaterParticles = v);
                Help("On = soft sediment puffs. Off = cubes.");
                SliderFloat("Rain clear speed", () => cfg.RainClearSpeed, v => cfg.RainClearSpeed = v, 0f, 2f);
                Help("How fast rain clears fresh blood. 0 = rain never clears it, 1 = half lifetime, 2 = a third.");
            }

            if (ImGui.CollapsingHeader("Bleed Damage"))
            {
                Checkbox("Enable bleed damage", () => cfg.BleedEnabled, v => cfg.BleedEnabled = v);
                Checkbox("Spawn splatter on damage", () => cfg.SpawnSplatterOnDamage, v => cfg.SpawnSplatterOnDamage = v);
                SliderFloat("Bleed time (sec)", () => cfg.BleedDurationSeconds, v => cfg.BleedDurationSeconds = v, 5f, 300f);
                SliderFloat("Damage per tick", () => cfg.BleedStaticPerTick, v => cfg.BleedStaticPerTick = v, 0f, 2f);
                SliderFloat("Extra damage, % of max HP", () => cfg.BleedPctMaxHealthPerTick, v => cfg.BleedPctMaxHealthPerTick = v, 0f, 10f);
                SliderFloat("Tick every (sec)", () => cfg.BleedTickSeconds, v => cfg.BleedTickSeconds = v, 1f, 60f);
                SliderInt("Bleed chance (%)", () => cfg.BleedChancePct, v => cfg.BleedChancePct = v, 0, 100);
                SliderFloat("Min damage to start", () => cfg.BleedDamageThreshold, v => cfg.BleedDamageThreshold = v, 0f, 10f);
                Checkbox("Player attacks only", () => cfg.BleedPlayerCausedOnly, v => cfg.BleedPlayerCausedOnly = v);
                Checkbox("Players can bleed (PvP)", () => cfg.BleedAffectsPlayers, v => cfg.BleedAffectsPlayers = v);
            }
            if (ImGui.CollapsingHeader("Archery"))
            {
                Checkbox("Arrows can break", () => cfg.ArrowBreakTuningEnabled, v => cfg.ArrowBreakTuningEnabled = v);
                Checkbox("Drop arrowhead when an arrow breaks", () => cfg.DropArrowheadOnBreak, v => cfg.DropArrowheadOnBreak = v);
                Help("A broken metal or stone arrow leaves its arrowhead to recover. Crude, reed and bone arrows leave nothing.");
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
