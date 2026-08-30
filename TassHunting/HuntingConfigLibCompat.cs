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
            // Restore/Defaults go through ReapplyServerRuled: on a remote server the
            // synced server config keeps ruling the gameplay fields (only the
            // look-and-feel fields actually reset), so a panel button cannot desync
            // this client from the world it is playing on. In single player
            // LastServerConfigJson is null and these behave exactly as before.
            if (buttons.Restore)
            {
                var loaded = capi.LoadModConfig<HuntingConfig>("TassHunting.json");
                if (loaded != null) HuntingModSystem.Cfg = HuntingModSystem.ReapplyServerRuled(loaded); // nothing caches Cfg - swap is safe
                return;
            }
            if (buttons.Defaults)
            {
                HuntingModSystem.Cfg = HuntingModSystem.ReapplyServerRuled(new HuntingConfig());
                return;
            }

            // ENFORCEMENT: on a multiplayer server the GAMEPLAY/BALANCE settings are
            // the SERVER'S - since the config sync (earwiq report 2026-08-10) the
            // server's values land in Cfg at join, so the greyed-out sections below
            // now SHOW the world's real settings instead of this client's ignored
            // local copy. (The 0.11.15 grey-out assumed clients never read gameplay
            // fields; the harvest fields ARE read client-side, which is exactly why
            // the sync exists.) The VISUAL sections (blood look, colors, water tint,
            // corpse decal, the bleeding box) are [ClientPersonal] and stay editable.
            bool serverDecides = !capi.IsSinglePlayer; // true on a multiplayer client
            if (serverDecides)
                ImGui.TextWrapped("You are on a server. Greyed-out settings show the server's own values, received when you joined - the server decides them. The look-and-feel settings are yours to change.");

            // ---- BLOOD (0.8.0): exactly the user-spec four sections with the
            //      ONE standard particle vocabulary. Everything else json-only.

            // Client-side blood look, governs ALL blood below (trails, splatter,
            // pool, water) - so it sits above the four sections, not greyed out on
            // a server. Rust creatures (drifters, locusts) still take arrows and
            // bleed damage; this only controls whether they show red blood.
            Checkbox("Enable blood effects for rust creatures", () => cfg.BloodEffectsForRustCreatures, v => cfg.BloodEffectsForRustCreatures = v);
            Help("Off (default): drifters, locusts and other rust beings show no red blood, since they are not living animals. They still take stuck arrows and bleed damage. On: they bleed red like animals.");

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
                SliderFloat("Particle lifetime, min (sec)##trails", () => cfg.BloodTrails.LifetimeMin, v => cfg.BloodTrails.LifetimeMin = v, 1f, 60f);
                SliderFloat("Particle lifetime, max (sec)##trails", () => cfg.BloodTrails.LifetimeMax, v => cfg.BloodTrails.LifetimeMax = v, 1f, 60f);
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

            if (ImGui.CollapsingHeader("Corpse Blood"))
            {
                ImGui.PushID("corpse");
                Checkbox("Pool of blood under a kill", () => cfg.CorpseBloodEnabled, v => cfg.CorpseBloodEnabled = v);
                Help("A dead animal leaves a pool of blood on the ground.");
                SliderFloat("Pool size", () => cfg.CorpseSpreadMult, v => cfg.CorpseSpreadMult = v, 1f, 6f);
                Help("How big the pool is. 1 = small, higher = a wide pool.");
                SliderFloat("Pool stays (sec)", () => cfg.CorpsePoolLifetimeSeconds, v => cfg.CorpsePoolLifetimeSeconds = v, 5f, 600f);
                Help("How long the pool stays on the ground before it dries up.");
                Checkbox("Debug log (blood + arrow fates)", () => cfg.BloodDiagnostics, v => cfg.BloodDiagnostics = v);
                Help("Off normally. On, the server log records blood pool details and what happened to every arrow: bounced, stuck, worked loose, released by a kill, broke, picked up, or expired on the ground - with where. Turn on to hunt a missing arrow, then turn back off.");
                ImGui.PopID();
            }

            if (ImGui.CollapsingHeader("Water Effect"))
            {
                Checkbox("Tint surrounding water", () => cfg.TintSurroundingWater, v => cfg.TintSurroundingWater = v);
                SliderFloat("Blood opacity in water", () => cfg.WaterBloodMaxOpacity, v => cfg.WaterBloodMaxOpacity = v, 0.02f, 1f);
                Help("How see-through the blood in water is. 0.10 = 10% (faint tint), 1.0 = solid. Applies live.");
                SliderFloat("Rain clear speed", () => cfg.RainClearSpeed, v => cfg.RainClearSpeed = v, 0f, 2f);
                Help("How fast rain clears fresh blood. 0 = rain never clears it, 1 = half lifetime, 2 = a third.");
            }

            if (ImGui.CollapsingHeader("Bleed Damage over Time"))
            {
                // "Spawn splatter on damage" and the bleeding box are the CLIENT-side
                // dials here (they drive what you see); the rest is server-decided damage.
                Checkbox("Spawn splatter on damage", () => cfg.SpawnSplatterOnDamage, v => cfg.SpawnSplatterOnDamage = v);
                Checkbox("Red flash on bleed damage", () => cfg.BleedTickHurtFlash, v => cfg.BleedTickHurtFlash = v);
                Help("Bleeding creatures blink red on every bleed tick, like a normal hit does. Purely a look - it never makes them immune the way a real hit's flash does.");
                Checkbox("Show bleeding box", () => cfg.BleedHudEnabled, v => cfg.BleedHudEnabled = v);
                Help("A small panel on screen while YOU are bleeding: a blood drop, how many wounds are open and how long until they close.");
                Combo("Bleeding box corner", BleedHudPositions, () => cfg.BleedHudPosition, v => cfg.BleedHudPosition = v);
                Help("Where that panel sits. Left middle by default, set a little below the middle so it stays clear of the XSkills effects panel.");
                BeginServer(serverDecides);
                Checkbox("Enable bleed damage", () => cfg.BleedEnabled, v => cfg.BleedEnabled = v);
                Help("Sharp hits open wounds: arrows, thrown spears, spear stabs, knife/sword/axe slashes. Blunt never bleeds. Better metal = stronger wound; every extra wound multiplies the whole bleed. An arrow left in the animal keeps its wound open.");
                SliderFloat("Damage per wound per tick", () => cfg.BleedStaticPerTick, v => cfg.BleedStaticPerTick = v, 0f, 2f);
                SliderFloat("Extra damage, % of max HP", () => cfg.BleedPctMaxHealthPerTick, v => cfg.BleedPctMaxHealthPerTick = v, 0f, 10f);
                SliderFloat("Tick every (sec)", () => cfg.BleedTickSeconds, v => cfg.BleedTickSeconds = v, 1f, 60f);
                SliderFloat("Longer per extra wound", () => cfg.BleedLengthMultiplier, v => cfg.BleedLengthMultiplier = v, 1f, 2f);
                Help("Every extra open wound makes the whole bleed last this much longer. 1.25 = a quarter longer each time. It multiplies with the damage payoff below, so raising both climbs fast.");
                SliderFloat("Your wounds close after (sec)", () => cfg.BleedPlayerWoundSeconds, v => cfg.BleedPlayerWoundSeconds = v, 5f, 300f);
                Help("How long YOUR wounds stay open. Animals and rust use their own times, set by how much health they have - see BleedSizeTiers and BleedRustTiers in the config file.");
                SliderFloat("Fallback wound time (sec)", () => cfg.BleedWoundSeconds, v => cfg.BleedWoundSeconds = v, 5f, 300f);
                Help("Only used for something the size list cannot place. Normally nothing.");
                SliderFloat("Smallest hit that wounds", () => cfg.BleedMinDamage, v => cfg.BleedMinDamage = v, 0f, 5f);
                SliderFloat("Bonus per metal tier", () => cfg.BleedTierStep, v => cfg.BleedTierStep = v, 0f, 1f);
                Help("Wound strength = 1 + this x tier. At 0.25: flint 1.25x, copper 1.5x, bronze 1.75x, iron 2x, steel 2.25x.");
                SliderFloat("Each extra wound multiplies bleed by", () => cfg.BleedComboMultiplier, v => cfg.BleedComboMultiplier = v, 1f, 2f);
                SliderInt("Max wounds per animal", () => cfg.BleedMaxWounds, v => cfg.BleedMaxWounds = v, 1, 20);
                SliderFloat("Arrow wound size", () => cfg.BleedArrowWoundWeight, v => cfg.BleedArrowWoundWeight = v, 0f, 3f);
                SliderFloat("Thrown spear wound size", () => cfg.BleedThrownSpearWoundWeight, v => cfg.BleedThrownSpearWoundWeight = v, 0f, 3f);
                SliderFloat("Spear stab wound size", () => cfg.BleedSpearStabWoundWeight, v => cfg.BleedSpearStabWoundWeight = v, 0f, 3f);
                SliderFloat("Slash wound size (knife, sword, axe)", () => cfg.BleedSlashWoundWeight, v => cfg.BleedSlashWoundWeight = v, 0f, 3f);
                SliderFloat("Claw and bite wound size", () => cfg.BleedCreatureWoundWeight, v => cfg.BleedCreatureWoundWeight = v, 0f, 3f);
                Help("What an animal's claws or teeth are worth. Separate from your weapons above, so tuning how well your knife bleeds an animal never changes how hard a wolf bleeds you.");
                Checkbox("Players can bleed (PvP + animal bites)", () => cfg.BleedAffectsPlayers, v => cfg.BleedAffectsPlayers = v);
                Checkbox("Bandages stop bleeding", () => cfg.BleedStoppedByHealingItems, v => cfg.BleedStoppedByHealingItems = v);
                Help("Finishing a bandage or a poultice closes every open wound on whoever it was used on, yourself or an animal you patched up. Off = you wait the wounds out.");
                Checkbox("Sitting still helps", () => cfg.BleedSittingHelps, v => cfg.BleedSittingHelps = v);
                Help("Sit down and hold it. After the seconds below, the bleeding does half damage and the wounds close in half the time, for as long as you stay down. Stand up and it stops at once and the count starts over, so hopping up and down gains you nothing. An arrow still in you has to come out first.");
                SliderFloat("Sit this long first (sec)", () => cfg.BleedSitSecondsRequired, v => cfg.BleedSitSecondsRequired = v, 0f, 30f);
                SliderFloat("Bleed damage while sitting", () => cfg.BleedSitDamageMult, v => cfg.BleedSitDamageMult = v, 0f, 1f);
                Help("0.5 = half damage. 0 = the bleeding does nothing while you sit.");
                SliderFloat("Wound time while sitting", () => cfg.BleedSitDurationMult, v => cfg.BleedSitDurationMult = v, 0.05f, 1f);
                Help("0.5 = wounds close in half the time. 0.25 = a quarter.");
                SliderFloat("Rust attacks wound size", () => cfg.BleedRustAttackWoundMult, v => cfg.BleedRustAttackWoundMult = v, 0f, 2f);
                Help("How badly drifters, locusts and other rust beings cut you. 0 = they never make you bleed. Only their sharp ones ever did: corrupt, nightmare and double-headed drifters, bronze and sawblade locusts.");
                SliderFloat("Animal attacks wound size", () => cfg.BleedCreatureAttackWoundMult, v => cfg.BleedCreatureAttackWoundMult = v, 0f, 2f);
                Help("The same for wolves, bears, hyenas and foxes. 0 = animal bites never make you bleed. Your own weapons are not affected by either of these.");
                SliderFloat("Armor shrinks the wound", () => cfg.BleedArmorMitigation, v => cfg.BleedArmorMitigation = v, 0f, 1f);
                Help("The wound is only as big as the part of the hit your armor let through. 1 = full effect, 0 = armor makes no difference to bleeding. Armor never helps with a wound that is already open - that is what bandages are for.");
                SliderFloat("Armor stops the cut above", () => cfg.BleedArmorNoWoundAbsorb, v => cfg.BleedArmorNoWoundAbsorb = v, 0.1f, 1f);
                Help("Armor that soaks at least this share of a blow turns the edge and you do not bleed at all. 0.85 = it has to stop 85 percent. 1 = never. The game rolls one armor piece per hit, so a blow that finds a gap can still cut you.");
                EndServer(serverDecides);
            }
            if (ImGui.CollapsingHeader("Archery"))
            {
                BeginServer(serverDecides);
                Checkbox("Arrows can break", () => cfg.ArrowBreakTuningEnabled, v => cfg.ArrowBreakTuningEnabled = v);
                Checkbox("Drop arrowhead when an arrow breaks", () => cfg.DropArrowheadOnBreak, v => cfg.DropArrowheadOnBreak = v);
                Help("A broken metal or stone arrow leaves its arrowhead to recover. Crude, reed and bone arrows leave nothing.");
                Checkbox("Arrows fly from your crosshair", () => cfg.TrueAimSpawnEnabled, v => cfg.TrueAimSpawnEnabled = v);
                Help("Fixes close shots landing high (vanilla spawns the arrow behind your head).");
                Checkbox("Power shot", () => cfg.PowerShotEnabled, v => cfg.PowerShotEnabled = v);
                Help("Keep the bow drawn past your full-accuracy moment (about half a second for most players) and the arrow hits harder. Vanilla gives longer draws accuracy only - this gives patience a damage payoff too.");
                SliderFloat("Extra hold needed (sec)", () => cfg.PowerShotExtraDrawSeconds, v => cfg.PowerShotExtraDrawSeconds = v, 0.25f, 5f);
                SliderFloat("Power shot damage multiplier", () => cfg.PowerShotDamageMult, v => cfg.PowerShotDamageMult = v, 1f, 2f);
                Checkbox("Click sound when power shot is ready", () => cfg.PowerShotDrawCue, v => cfg.PowerShotDrawCue = v);
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
                EndServer(serverDecides);
            }

            if (ImGui.CollapsingHeader("Animals"))
            {
                BeginServer(serverDecides);
                SliderFloat("Predator speed multiplier", () => cfg.PredatorSpeedMult, v => cfg.PredatorSpeedMult = v, 0.5f, 2f);
                Help("All adult predators (wolves, bears, foxes, hyenas - anything the game tags as an adult predator) move this much faster. 1 = vanilla speed. Needs world rejoin.");
                SliderFloat("Predator climb speed", () => cfg.PredatorStepUpSpeed, v => cfg.PredatorStepUpSpeed = v, 0f, 0.2f);
                Help("How fast adult predators step up blocks when they chase. The game default is 0.07 (about 4 blocks of height per second) and running speed never changes it, so sprinting up stairs or a steep hill is a free escape - especially with a step-up mod. 0.10 to 0.12 keeps predators on your heels uphill without touching flat-ground speed. 0 = game default. Needs world rejoin.");
                Checkbox("Animals flee away from you", () => cfg.FleeAwayFromHunterEnabled, v => cfg.FleeAwayFromHunterEnabled = v);
                Help("Vanilla sometimes makes a shot animal run straight at the shooter.");
                Checkbox("Tougher predators", () => cfg.PredatorOverhaulEnabled, v => cfg.PredatorOverhaulEnabled = v);
                Help("Bears charge from further and never give up; wolves pack up. Needs world rejoin.");
                Checkbox("Wounded animals slow down", () => cfg.WoundedSlowdownEnabled, v => cfg.WoundedSlowdownEnabled = v);
                Checkbox("Some animals stay wild", () => cfg.StayWildEnabled, v => cfg.StayWildEnabled = v);
                Help("The creatures listed under StayWildCodes in the config file can never be tamed, petted, roped or ridden, whatever else you install. Names them by code, so it is a config-file list. Needs world rejoin.");
                Checkbox("Tree leaves are walk-through", () => cfg.LeavesPassthroughEnabled, v => cfg.LeavesPassthroughEnabled = v);
                Help("Branchy tree leaves lose their solid box, the way most leaves already are - everything walks through every leaf block. Big animals stop snagging on tree tops when they chase, and nobody can stand in a tree out of their reach. Chopping and drops stay as before. Needs world rejoin.");
                SliderFloat("Angry animals chase this far (blocks)", () => cfg.RetaliationSeekRange, v => cfg.RetaliationSeekRange = v, 10f, 200f);
                SliderFloat("Angry animals press the chase (sec)", () => cfg.RetaliationMaxFollowTimeSec, v => cfg.RetaliationMaxFollowTimeSec = v, 10f, 600f);
                SliderFloat("Anger lasts (sec)", () => cfg.RetaliationMemorySeconds, v => cfg.RetaliationMemorySeconds = v, 10f, 600f);
                SliderFloat("Territory radius (blocks)", () => cfg.TerritoryRadius, v => cfg.TerritoryRadius = v, 4f, 60f);
                Help("For the creatures named in RetaliationCodes / TerritorialCodes in the config file: how far they hunt whoever hurt them, how long they keep it up, how long they stay angry, and (territorial ones) how close a player can get before they start the fight themselves. Needs world rejoin.");
                EndServer(serverDecides);
            }

            if (ImGui.CollapsingHeader("Big game"))
            {
                BeginServer(serverDecides);
                SliderFloat("Bleed levels off above (health)", () => cfg.BleedHealthCeiling, v => cfg.BleedHealthCeiling = v, 0f, 500f);
                Help("Bleed damage grows with an animal's health up to about here, then levels off - huge modded creatures bleed at a big-animal rate instead of dying to two spears like everything else. 0 = old behavior, bleed keeps growing with health forever.");
                Checkbox("Shots bounce off thick hide and armor", () => cfg.BounceEnabled, v => cfg.BounceEnabled = v);
                Help("Two kinds of tough animal, both config-file lists (lists cannot be edited from this panel): thick hide (the dinos, bears, moose, elder boars) and bone armor (ankylosaurs, stegosaurs, horned dinos). Your metal decides the odds - against hide, stone bounces half the time down to steel a third; against armor, stone ALWAYS bounces and even steel bounces 3 in 4. A bounced shot does nothing at all - no damage, no wound - and the arrow drops at the animal's feet to pick back up. Animals fighting animals never bounce, and blunt weapons never bounce.");
                Checkbox("Power shots punch through hide", () => cfg.PowerShotPunchesThrough, v => cfg.PowerShotPunchesThrough = v);
                Help("A full heavy draw counts one metal tier better against thick hide - and steel gets one further step. Armor ignores power shots.");
                EndServer(serverDecides);
            }

            if (ImGui.CollapsingHeader("Stuck arrows and spears"))
            {
                BeginServer(serverDecides);
                Checkbox("Arrows stick in animals", () => cfg.StickyProjectilesEnabled, v => cfg.StickyProjectilesEnabled = v);
                Checkbox("Arrows stay until the animal dies", () => cfg.StickUntilDeath, v => cfg.StickUntilDeath = v);
                Help("On = arrows never fall out of a live animal; they stay and keep bleeding it until the kill, then drop. Off = they work loose after the lifetime below.");
                SliderFloat("Stuck arrow lifetime (sec)", () => cfg.StickSeconds, v => cfg.StickSeconds = v, 30f, 900f);
                Help("How long a stuck arrow lasts if the animal never dies. With 'stay until death' on, this only applies to an animal that fled and vanished.");
                Checkbox("Grab stuck spears back", () => cfg.SpearTouchRetrieve, v => cfg.SpearTouchRetrieve = v);
                SliderFloat("Your arrows stay yours (sec)", () => cfg.ArrowOwnerLockSeconds, v => cfg.ArrowOwnerLockSeconds = v, 0f, 900f);
                Help("Other players cannot walk-over collect your fired arrows for this long. You always can. 0 turns it off.");
                Checkbox("Pull arrows out of players", () => cfg.PlayerArrowTouchRetrieve, v => cfg.PlayerArrowTouchRetrieve = v);
                Help("An arrow stuck in a player can be pulled out at touch range by its shooter or by the stuck player. Arrows in animals stay in until they release.");
                EndServer(serverDecides);
            }

            if (ImGui.CollapsingHeader("Sounds"))
            {
                Checkbox("Hear heavy steps behind you", () => cfg.BigStepsBehindYou, v => cfg.BigStepsBehindYou = v);
                Help("The game only plays footsteps for creatures on your screen. This keeps the heavy ones stomping audibly when they are behind you, using their own step sounds and pace.");
                SliderFloat("Counts as heavy above (step carry range)", () => cfg.BigStepsMinRange, v => cfg.BigStepsMinRange = v, 5f, 100f);
                Help("Steps designed to carry at least this many blocks count as heavy - for the behind-you steps AND the sound swap below. Wolves carry ~20, the dinosaurs 49-67.");
                BeginServer(serverDecides);
                SliderFloat("Deepen heavy steps by body size", () => cfg.StepSoundDeepen, v => cfg.StepSoundDeepen = v, 0f, 3f);
                Help("Higher = deeper. Swapped step sounds drop in pitch with the animal's size - at 2 a tyrannosaur plays at quarter pitch and the biggest animals bottom out at a fifth. Deeper also means softer attack, so pair high values with the volume below. Wolf-sized is never touched. 0 = as recorded. Needs world rejoin.");
                SliderFloat("Swapped step volume", () => cfg.StepSoundVolumeMult, v => cfg.StepSoundVolumeMult = v, 0.1f, 4f);
                Help("Loudness of the swapped step sounds on top of each animal's own tuning. Turn up to put the punch back when steps are pitched deep. Needs world rejoin.");
                EndServer(serverDecides);
                Help("Which sound the heavy steps use is the StepSoundOverride list in the config file (creature names to a sound path) - lists cannot be edited from this panel.");
            }

            if (ImGui.CollapsingHeader("Nature red in tooth"))
            {
                BeginServer(serverDecides);
                Checkbox("Animal kills leave bones, not corpses", () => cfg.NonPlayerKillsLeaveBones, v => cfg.NonPlayerKillsLeaveBones = v);
                Help("When something dies with no player behind the kill, the body decays on the spot into its bones block instead of lying there harvestable. Your own kills - including animals that bleed out of your wounds - always keep their corpse and loot.");
                SliderFloat("Bones appear after (sec)", () => cfg.NonPlayerKillBonesDelaySeconds, v => cfg.NonPlayerKillBonesDelaySeconds = v, 1f, 60f);
                SliderFloat("Your kill counts for (sec)", () => cfg.PlayerKillCreditSeconds, v => cfg.PlayerKillCreditSeconds = v, 0f, 600f);
                Help("If you hurt a creature within this many seconds of its death, the corpse is yours even when something else lands the killing blow or it falls into your pit. 0 = only the killing blow and bleeding out count.");
                EndServer(serverDecides);
            }

            if (ImGui.CollapsingHeader("Death messages"))
            {
                BeginServer(serverDecides);
                Checkbox("Name the real killer", () => cfg.KillerNamesEnabled, v => cfg.KillerNamesEnabled = v);
                Help("Death messages and the damage log say what actually got you - killed by Tyrannosaurus - Tyrant King instead of killed by a wild animal. Which common name goes after the dash is the KillerCommonNames list in the config file - lists cannot be edited from this panel.");
                SliderFloat("A killing blow is remembered (sec)", () => cfg.KillerWitnessMemorySeconds, v => cfg.KillerWitnessMemorySeconds = v, 0f, 7200f);
                Help("Going down and bleeding out later still names what downed you, as long as the death comes within this long. 0 = off: those deaths just say the player died.");
                EndServer(serverDecides);
            }

            if (ImGui.CollapsingHeader("Harvest and pickup"))
            {
                BeginServer(serverDecides);
                SliderFloat("Harvest time (x vanilla)", () => cfg.HarvestTimeMult, v => cfg.HarvestTimeMult = v, 0.05f, 2f);
                Help("0.5 = knife work takes half as long, 1 = vanilla. In the config file, 0 also means vanilla.");
                Checkbox("Loot drops on the ground", () => cfg.HarvestAutoDrop, v => cfg.HarvestAutoDrop = v);
                Checkbox("Remove empty corpses", () => cfg.EmptyCorpseAutoRemove, v => cfg.EmptyCorpseAutoRemove = v);
                SliderFloat("Remove after (sec)", () => cfg.EmptyCorpseRemoveSeconds, v => cfg.EmptyCorpseRemoveSeconds = v, 1f, 120f);
                SliderFloat("Arrow pickup range (0 = off)", () => cfg.ProjectilePickupRadius, v => cfg.ProjectilePickupRadius = v, 0f, 16f);
                Help("Walk within this range of your landed arrows and spears to collect them automatically.");
                Checkbox("Pick up your own only", () => cfg.PickupOnlyOwnProjectiles, v => cfg.PickupOnlyOwnProjectiles = v);
                EndServer(serverDecides);
            }

            if (ImGui.CollapsingHeader("Misc"))
            {
                ImGui.PushID("misc");
                ColorHex("Fresh blood color", () => cfg.BloodColorHex, v => cfg.BloodColorHex = v);
                ColorHex("Aged blood color", () => cfg.BloodColorAgedHex, v => cfg.BloodColorAgedHex = v);
                Help("Blood comes out the fresh color and darkens toward the aged color over its lifetime. Set both the same to disable age-darkening.");
                ImGui.PopID();
            }
        }

        // ---- helpers ----

        // On a multiplayer client, wrap a server-decided section so its widgets
        // grey out and cannot be dragged (ImGui.BeginDisabled). In single player
        // these are no-ops and everything stays editable. Always paired.
        private static void BeginServer(bool serverDecides)
        {
            if (serverDecides) ImGui.BeginDisabled();
        }

        private static void EndServer(bool serverDecides)
        {
            if (serverDecides) ImGui.EndDisabled();
        }

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

        // Screen corners offered for the bleeding box. Display text on the left, the
        // value stored in the config on the right - the config keeps plain names so a
        // hand-edited json stays readable.
        private static readonly string[][] BleedHudPositions =
        {
            new[] { "Left middle", "LeftMiddle" },
            new[] { "Left top", "LeftTop" },
            new[] { "Left bottom", "LeftBottom" },
            new[] { "Right middle", "RightMiddle" },
            new[] { "Right top", "RightTop" },
            new[] { "Right bottom", "RightBottom" },
            new[] { "Top middle", "CenterTop" },
            new[] { "Bottom middle", "CenterBottom" },
        };

        private static void Combo(string label, string[][] options, Func<string> get, Action<string> set)
        {
            string current = get() ?? "";
            int index = 0;
            var labels = new string[options.Length];
            for (int i = 0; i < options.Length; i++)
            {
                labels[i] = options[i][0];
                if (string.Equals(options[i][1], current, StringComparison.OrdinalIgnoreCase)) index = i;
            }
            if (ImGui.Combo(label, ref index, labels, labels.Length)) set(options[index][1]);
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
