using System;
using System.Linq;
using TassHunting;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace TassHuntingCompatHarness
{
    /// <summary>
    /// BIG GAME (TASSHUNTING_BIGGAME=1): the bleed health ceiling and the hide-glance gate,
    /// proven BOTH directions in one run. Engine-only on purpose - the feature is a size RULE,
    /// so the proof spawns a vanilla pig and dials its max health up and down rather than
    /// depending on any dino pack being installed.
    ///
    /// Pure half: the tanh curves at their landmark points (vanilla identity, saturation,
    /// zero-band, toughness multiplier, hard cap, power-shot halving).
    /// Live half, statistical: hundreds of real sharp hits through the whole damage pipeline -
    ///  - a 30 hp body must take EVERY hit (zero band is deterministic, not merely unlikely);
    ///  - a 400 hp body must glance at the curve's rate within 3-sigma;
    ///  - a 66 hp (bear-size) body must glance rarely but measurably;
    ///  - the published per-tick bleed damage on a 400 hp body must drop ~3.8x when the
    ///    ceiling is on vs off (the same wound, both directions of the ceiling switch).
    /// </summary>
    public class BigGameHarnessSystem : ModSystem
    {
        private ICoreServerAPI _sapi = null!;
        private int _total, _passed;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            if (Environment.GetEnvironmentVariable("TASSHUNTING_BIGGAME") != "1") return;
            _sapi = api;
            api.Event.SaveGameLoaded += () => api.Event.RegisterCallback(_ => Run(), 3000);
            api.Logger.Notification("[biggame] armed.");
        }

        private void Check(string name, bool ok, string detail = null)
        {
            _total++;
            if (ok) _passed++;
            _sapi.Logger.Notification("[biggame] {0} {1}{2}", ok ? "PASS" : "FAIL", name,
                detail == null ? "" : " (" + detail + ")");
        }

        private void Done() =>
            _sapi.Logger.Notification("[biggame] BIGGAME COMPLETE total={0} pass={1} fail={2}",
                _total, _passed, _total - _passed);

        private static bool Near(float a, float b, float tol) => Math.Abs(a - b) <= tol;

        private void Run()
        {
            try
            {
                // Known ground: the harness pins every dial it measures, so a stray ModConfig
                // on the test server cannot skew the statistics.
                var cfg = HuntingModSystem.Cfg;
                cfg.BleedEnabled = true;
                cfg.BleedHealthCeiling = 100f;
                cfg.GlanceStartHealth = 45f;
                cfg.GlanceRampHealth = 200f;
                cfg.GlanceMaxChance = 0.5f;
                cfg.GlanceChanceCeiling = 0.8f;
                cfg.PowerShotPunchesThrough = true;
                cfg.GlanceSharpnessBase = 4f;
                cfg.GlanceSharpnessStep = 0.12f;
                cfg.GlanceSharpnessFloor = 0.35f;
                // The live loops hit at damage 1.0 - below the sharpness base, factor exactly 1 -
                // so the glance statistics measure the pure size curve.
                cfg.GlanceToughness.Clear();
                cfg.BleedMinDamage = 0.5f;
                // A sourceless hit rolls the CREATURE odds ladder for its wound count (hp 0 ->
                // bottom rung, 50%), which would be indistinguishable from a glance to this
                // harness's no-wound detector. One all-sizes rung at odds 1 makes every
                // non-glanced hit wound deterministically, so no-wound = glanced, exactly.
                cfg.BleedSizeTiers = new[] { new BleedSizeTier { MaxHealth = 0f, Seconds = 45f, Odds = 1f, SecondOdds = 0f } };

                PureChecks(cfg);
                DamageMulChecks(cfg);
                TerritoryChecks(cfg);
                FoodChainChecks(cfg);
                LiveGlance(cfg);   // async tail: ends with BonesChecks -> Done
            }
            catch (Exception e)
            {
                _sapi.Logger.Error("[biggame] EXCEPTION: {0}", e);
                Done();
            }
        }

        private void PureChecks(HuntingConfig cfg)
        {
            // Ceiling curve: off-switch, vanilla identity, saturation.
            Check("eff-ceiling-0-is-identity", WoundMath.EffectiveHealth(400f, 0f) == 400f);
            float hare = WoundMath.EffectiveHealth(15f, 100f);
            Check("eff-hare-within-1pct", hare > 14.85f && hare <= 15f, $"eff(15)={hare:0.000}");
            float wolf = WoundMath.EffectiveHealth(44f, 100f);
            Check("eff-wolf-within-7pct", wolf > 40.9f && wolf <= 44f, $"eff(44)={wolf:0.00}");
            float sauro = WoundMath.EffectiveHealth(800f, 100f);
            Check("eff-sauropod-saturates", sauro > 99f && sauro < 100f, $"eff(800)={sauro:0.00}");

            // Glance curve landmarks. tanh(1.025)=0.7719 -> rex 0.386.
            Check("glance-wolf-exact-zero", HideGlance.Chance(44f, 45f, 200f, 0.5f, 1f, 0.8f, false) == 0f);
            Check("glance-deer-exact-zero", HideGlance.Chance(30f, 45f, 200f, 0.5f, 1f, 0.8f, false) == 0f);
            float bear = HideGlance.Chance(66f, 45f, 200f, 0.5f, 1f, 0.8f, false);
            Check("glance-bear-about-5pct", Near(bear, 0.0524f, 0.004f), $"{bear:0.0000}");
            float rex = HideGlance.Chance(250f, 45f, 200f, 0.5f, 1f, 0.8f, false);
            Check("glance-rex-about-39pct", Near(rex, 0.386f, 0.01f), $"{rex:0.000}");
            float anky = HideGlance.Chance(400f, 45f, 200f, 0.5f, 1.5f, 0.8f, false);
            Check("glance-toughness-multiplies", Near(anky, 0.708f, 0.01f), $"{anky:0.000}");
            float clamped = HideGlance.Chance(400f, 45f, 200f, 0.5f, 5f, 0.8f, false);
            Check("glance-hard-cap-holds", clamped == 0.8f, $"{clamped:0.000}");
            float punched = HideGlance.Chance(250f, 45f, 200f, 0.5f, 1f, 0.8f, true);
            Check("glance-powershot-halves", Near(punched, rex / 2f, 0.002f), $"{punched:0.000}");

            // Sharpness (0.14.19): flint anchor exact, steel a third off, floor for huge bites,
            // below-anchor clamps to 1 so weak hits never glance MORE than flint.
            Check("sharp-flint-is-1", HideGlance.Sharpness(4f, 4f, 0.12f, 0.35f) == 1f);
            float steel = HideGlance.Sharpness(7f, 4f, 0.12f, 0.35f);
            Check("sharp-steel-0.64", Near(steel, 0.64f, 0.001f), $"{steel:0.000}");
            Check("sharp-bite-floors", HideGlance.Sharpness(24f, 4f, 0.12f, 0.35f) == 0.35f);
            Check("sharp-weak-clamps-to-1", HideGlance.Sharpness(1f, 4f, 0.12f, 0.35f) == 1f);
            // Combined: steel spear vs the mapped anky = 0.708 * 0.64 = 0.453.
            float steelAnky = HideGlance.Chance(400f, 45f, 200f, 0.5f, 1.5f * steel, 0.8f, false);
            Check("sharp-steel-vs-anky-45pct", Near(steelAnky, 0.453f, 0.01f), $"{steelAnky:0.000}");
        }

        /// <summary>
        /// CreatureMeleeDamageMul (0.14.21), engine-only, both directions on the type JSON:
        /// the vanilla wolf's melee halves under a wolf-* pattern, the hyena (control) stays
        /// byte-identical, and a pattern matching nothing must not throw (it warns).
        /// </summary>
        private void DamageMulChecks(HuntingConfig cfg)
        {
            float Melee(string prefix)
            {
                var et = _sapi.World.EntityTypes.FirstOrDefault(
                    t => t?.Code?.Path != null && t.Code.Domain == "game" && t.Code.Path.StartsWith(prefix));
                if (et?.Server?.BehaviorsAsJsonObj == null) return -1f;
                foreach (var jo in et.Server.BehaviorsAsJsonObj)
                {
                    var tok = jo?.Token as Newtonsoft.Json.Linq.JObject;
                    if (tok?["code"]?.ToString() != "taskai") continue;
                    if (!(tok["aitasks"] is Newtonsoft.Json.Linq.JArray aitasks)) continue;
                    foreach (var jt in aitasks)
                        if (jt is Newtonsoft.Json.Linq.JObject task && task["code"]?.ToString() == "meleeattack" && task["damage"] != null)
                            return (float)task["damage"].ToObject(typeof(float));
                }
                return -1f;
            }

            float wolfBefore = Melee("wolf-"), hyenaBefore = Melee("hyena-");
            Check("dmgmul-wolf-baseline-found", wolfBefore > 0f, $"wolf melee {wolfBefore}");
            cfg.CreatureMeleeDamageMul = new System.Collections.Generic.Dictionary<string, float>
            {
                { "wolf-*", 0.5f }, { "no-such-creature-*", 0.5f }
            };
            CreatureDamageMul.Apply(_sapi);
            float wolfAfter = Melee("wolf-"), hyenaAfter = Melee("hyena-");
            Check("dmgmul-wolf-halved", Near(wolfAfter, wolfBefore * 0.5f, 0.01f), $"{wolfBefore} -> {wolfAfter}");
            Check("dmgmul-control-hyena-untouched", hyenaAfter == hyenaBefore, $"hyena melee {hyenaBefore}");
            cfg.CreatureMeleeDamageMul.Clear();
        }

        /// <summary>
        /// FoodChain.Apply (0.14.24): wolf given sheep as prey must carry it in its ungated
        /// hunt seeks AND its melee whitelist; anger-gated tasks stay untouched; hyena control
        /// unchanged; applying twice appends nothing new (idempotence).
        /// </summary>
        private void FoodChainChecks(HuntingConfig cfg)
        {
            // gate filter: "in" = has whenInEmotionState, "governed" = whenNot contains
            // saturated, "ungoverned" = neither (the packs' never-full seeks)
            int CountCode(string prefix, string taskCode, string gate, string prey)
            {
                var et = _sapi.World.EntityTypes.FirstOrDefault(
                    t => t?.Code?.Path != null && t.Code.Domain == "game" && t.Code.Path.StartsWith(prefix));
                if (et?.Server?.BehaviorsAsJsonObj == null) return -1;
                int n = 0;
                foreach (var jo in et.Server.BehaviorsAsJsonObj)
                {
                    var tok = jo?.Token as Newtonsoft.Json.Linq.JObject;
                    if (tok?["code"]?.ToString() != "taskai") continue;
                    if (!(tok["aitasks"] is Newtonsoft.Json.Linq.JArray tasks)) continue;
                    foreach (var jt in tasks)
                    {
                        if (!(jt is Newtonsoft.Json.Linq.JObject t) || t["code"]?.ToString() != taskCode) continue;
                        bool gatedIn = !string.IsNullOrEmpty(t["whenInEmotionState"]?.ToString());
                        bool governed = (t["whenNotInEmotionState"]?.ToString() ?? "").Contains("saturated");
                        string g = gatedIn ? "in" : governed ? "governed" : "ungoverned";
                        if (g != gate && gate != "any") continue;
                        if (t["entityCodes"] is Newtonsoft.Json.Linq.JArray codes)
                            foreach (var c in codes) if (c?.ToString() == prey) n++;
                    }
                }
                return n;
            }

            cfg.HuntAppend = new System.Collections.Generic.Dictionary<string, string[]>
            {
                { "wolf-*", new[] { "sheep-*" } }
            };
            FoodChain.Apply(_sapi);
            int governedHits = CountCode("wolf-", "seekentity", "governed", "sheep-*");
            int meleeHits = CountCode("wolf-", "meleeattack", "any", "sheep-*");
            Check("chain-wolf-hunts-sheep-when-hungry", governedHits > 0, $"{governedHits} saturation-gated hunts");
            Check("chain-never-full-seeks-untouched", CountCode("wolf-", "seekentity", "ungoverned", "sheep-*") == 0,
                "prey must ride the hunger-governed task only");
            Check("chain-wolf-melee-can-bite-sheep", meleeHits > 0, $"{meleeHits} melee lists");
            Check("chain-anger-lists-untouched", CountCode("wolf-", "seekentity", "in", "sheep-*") == 0);
            Check("chain-control-hyena-untouched", CountCode("hyena-", "seekentity", "any", "sheep-*") == 0);
            FoodChain.Apply(_sapi); // idempotence: same map again must append nothing
            Check("chain-reapply-appends-nothing", CountCode("wolf-", "seekentity", "governed", "sheep-*") == governedHits);
            cfg.HuntAppend.Clear();
        }

        /// <summary>
        /// Bones (0.14.24), live, both directions: a sourceless kill with the switch on flags
        /// the corpse for despawn (DecayNow ran); the same kill on a body carrying the
        /// player-bleed stamp keeps its corpse. AllowDespawn is the observable: DecayNow's
        /// first act is setting it true, and a kept corpse holds it false.
        /// </summary>
        private void BonesChecks(HuntingConfig cfg, Action done)
        {
            cfg.NonPlayerKillsLeaveBones = true;
            cfg.NonPlayerKillBonesDelaySeconds = 1f;
            cfg.PlayerKillCreditSeconds = 120f;
            var wildKill = SpawnPig();
            var hunterKill = SpawnPig();
            var windowKill = SpawnPig();   // player hit it recently, something else finished it
            var staleKill = SpawnPig();    // player hit it long ago - credit expired
            var mealKill = SpawnPig();     // killed BY a wolf: the kill must feed the killer
            var wolfType = _sapi.World.EntityTypes.FirstOrDefault(
                t => t?.Code?.Path != null && t.Code.Domain == "game" && t.Code.Path.StartsWith("wolf-") && t.Code.Path.Contains("male"));
            Entity wolf = null;
            if (wolfType != null)
            {
                wolf = _sapi.World.ClassRegistry.CreateEntity(wolfType);
                var sp = _sapi.World.DefaultSpawnPosition;
                wolf.ServerPos.SetPos(sp.X + 6, sp.Y + 1, sp.Z + 6);
                wolf.Pos.SetFrom(wolf.ServerPos);
                _sapi.World.SpawnEntity(wolf);
            }
            if (wildKill == null || hunterKill == null || windowKill == null || staleKill == null || mealKill == null || wolf == null)
            { Check("bones-pigs-spawned", false); done(); return; }
            long now = _sapi.World.ElapsedMilliseconds;
            hunterKill.WatchedAttributes.SetString("tasshunt:bleedByUid", "harness-player");
            windowKill.Attributes.SetLong("tasshunt:phitMs", now - 5000);      // 5s ago: inside window
            staleKill.Attributes.SetLong("tasshunt:phitMs", now - 999_000);    // ~17min ago: expired
            wildKill.ReceiveDamage(Sharp(), 9999f);
            hunterKill.ReceiveDamage(Sharp(), 9999f);
            windowKill.ReceiveDamage(Sharp(), 9999f);
            staleKill.ReceiveDamage(Sharp(), 9999f);
            // wolf-attributed kill: the bones ruling must arm the wolf's "saturated" state
            // (chance 1 on vanilla wolves - deterministic), because the carcass it would have
            // eaten is gone. Checked synchronously: the postfix runs inside ReceiveDamage.
            var emoBefore = wolf.GetBehavior<Vintagestory.GameContent.EntityBehaviorEmotionStates>();
            Check("bones-killer-not-full-before", emoBefore != null && !emoBefore.IsInEmotionState("saturated"));
            mealKill.ReceiveDamage(new DamageSource
            {
                Source = EnumDamageSource.Entity,
                SourceEntity = wolf,
                Type = EnumDamageType.PiercingAttack,
                DamageTier = 0,
                IgnoreInvFrames = true
            }, 9999f);
            Check("bones-kill-feeds-the-killer",
                emoBefore != null && emoBefore.IsInEmotionState("saturated"),
                "wolf saturated by its own kill turning to bones");
            wolf.Die(EnumDespawnReason.Removed);
            _sapi.Event.RegisterCallback(_ =>
            {
                try
                {
                    Check("bones-wild-kill-decays",
                        !wildKill.Alive && ((Vintagestory.API.Common.EntityAgent)wildKill).AllowDespawn,
                        $"alive={wildKill.Alive}");
                    Check("bones-bled-out-quarry-keeps-corpse",
                        !hunterKill.Alive && !((Vintagestory.API.Common.EntityAgent)hunterKill).AllowDespawn,
                        $"alive={hunterKill.Alive}");
                    Check("bones-recent-player-hit-keeps-corpse",
                        !windowKill.Alive && !((Vintagestory.API.Common.EntityAgent)windowKill).AllowDespawn,
                        "kill-steal / pit credit inside the window");
                    Check("bones-stale-credit-decays",
                        !staleKill.Alive && ((Vintagestory.API.Common.EntityAgent)staleKill).AllowDespawn,
                        "expired credit is no credit");
                }
                catch (Exception e) { _sapi.Logger.Error("[biggame] EXCEPTION: {0}", e); }
                cfg.NonPlayerKillsLeaveBones = false;
                done();
            }, 2500);
        }

        /// <summary>
        /// Territory.Apply (0.14.22), engine-only on the type JSON, both directions: a vanilla
        /// bear made territorial must end up with the player in its aggressivearoundentities
        /// trigger list, a raised guard radius, raised anger memory, and a raised anger-chase;
        /// the wolf (control, not in either list) must stay byte-identical on those fields.
        /// </summary>
        private void TerritoryChecks(HuntingConfig cfg)
        {
            Newtonsoft.Json.Linq.JObject Behavior(string prefix, string code)
            {
                var et = _sapi.World.EntityTypes.FirstOrDefault(
                    t => t?.Code?.Path != null && t.Code.Domain == "game" && t.Code.Path.StartsWith(prefix));
                if (et?.Server?.BehaviorsAsJsonObj == null) return null;
                foreach (var jo in et.Server.BehaviorsAsJsonObj)
                {
                    var tok = jo?.Token as Newtonsoft.Json.Linq.JObject;
                    if (tok?["code"]?.ToString() == code) return tok;
                }
                return null;
            }
            float StateVal(Newtonsoft.Json.Linq.JObject emo, string state, string field)
            {
                if (!(emo?["states"] is Newtonsoft.Json.Linq.JArray states)) return -1f;
                foreach (var st in states)
                    if (st is Newtonsoft.Json.Linq.JObject s && s["code"]?.ToString() == state && s[field] != null)
                        return (float)s[field].ToObject(typeof(float));
                return -1f;
            }
            bool StateHasPlayer(Newtonsoft.Json.Linq.JObject emo, string state)
            {
                if (!(emo?["states"] is Newtonsoft.Json.Linq.JArray states)) return false;
                foreach (var st in states)
                    if (st is Newtonsoft.Json.Linq.JObject s && s["code"]?.ToString() == state
                        && s["entityCodes"] is Newtonsoft.Json.Linq.JArray codes)
                        foreach (var c in codes) if (c?.ToString() == "player") return true;
                return false;
            }
            float SeekRange(string prefix)
            {
                var taskai = Behavior(prefix, "taskai");
                if (!(taskai?["aitasks"] is Newtonsoft.Json.Linq.JArray tasks)) return -1f;
                foreach (var jt in tasks)
                    if (jt is Newtonsoft.Json.Linq.JObject t && t["code"]?.ToString() == "seekentity"
                        && (t["whenInEmotionState"]?.ToString() ?? "").Contains("aggressiveondamage")
                        && t["seekingRange"] != null)
                        return (float)t["seekingRange"].ToObject(typeof(float));
                return -1f;
            }

            float wolfSeekBefore = SeekRange("wolf-");
            var bearEmoBefore = Behavior("bear-", "emotionstates");
            Check("terr-bear-has-emotions", bearEmoBefore != null);
            Check("terr-bear-no-player-guard-before", !StateHasPlayer(bearEmoBefore, "aggressivearoundentities"));

            cfg.RetaliationCodes = new[] { "no-such-thing-*" };
            cfg.TerritorialCodes = new[] { "bear-*" };
            cfg.RetaliationSeekRange = 40f;
            cfg.RetaliationMaxFollowTimeSec = 120f;
            cfg.RetaliationMemorySeconds = 180f;
            cfg.TerritoryRadius = 25f;
            Territory.Apply(_sapi);

            var bearEmo = Behavior("bear-", "emotionstates");
            Check("terr-bear-guards-players", StateHasPlayer(bearEmo, "aggressivearoundentities"));
            float radius = StateVal(bearEmo, "aggressivearoundentities", "notifyRange");
            Check("terr-bear-guard-radius-25", radius >= 25f, $"notifyRange {radius}");
            float mem = StateVal(bearEmo, "aggressiveondamage", "duration");
            Check("terr-bear-memory-180", mem >= 180f, $"duration {mem}");
            float bearSeek = SeekRange("bear-");
            Check("terr-bear-chase-40", bearSeek >= 40f, $"seekingRange {bearSeek}");
            float wolfSeekAfter = SeekRange("wolf-");
            Check("terr-control-wolf-untouched", wolfSeekAfter == wolfSeekBefore, $"wolf seek {wolfSeekBefore}");
            Check("terr-wolf-no-player-guard", !StateHasPlayer(Behavior("wolf-", "emotionstates"), "aggressivearoundentities"));
            cfg.RetaliationCodes = new string[0];
            cfg.TerritorialCodes = new string[0];
        }

        // A sourceless piercing hit, the RejoinHarness pattern: no attacker entity needed,
        // attacker-class multiplier 1, always sharp. IgnoreInvFrames because this harness
        // lands hundreds of hits in a tight loop - the engine's 500ms invulnerability window
        // would otherwise swallow all but ~1 hit per half second and every count would lie.
        private static DamageSource Sharp() => new DamageSource
        {
            Source = EnumDamageSource.Unknown,
            Type = EnumDamageType.PiercingAttack,
            DamageTier = 0,
            IgnoreInvFrames = true
        };

        private Entity SpawnPig()
        {
            var type = _sapi.World.EntityTypes.FirstOrDefault(
                t => t?.Code?.Path != null && t.Code.Path.StartsWith("pig-") && t.Code.Path.Contains("-adult-"));
            if (type == null) return null;
            var spawn = _sapi.World.DefaultSpawnPosition;
            Entity e = _sapi.World.ClassRegistry.CreateEntity(type);
            e.ServerPos.SetPos(spawn.X + 3, spawn.Y + 1, spawn.Z + 3);
            e.Pos.SetFrom(e.ServerPos);
            _sapi.World.SpawnEntity(e);
            return e;
        }

        /// <summary>Set the body's size as the bleed code sees it (MaxHealthOf reads MaxHealth).</summary>
        private static void SetSize(Entity ent, float maxHp)
        {
            var hb = ent.GetBehavior<EntityBehaviorHealth>();
            hb.BaseMaxHealth = maxHp;
            hb.MaxHealth = maxHp;
            hb.Health = maxHp;
        }

        /// <summary>
        /// Land n sharp hits; count how many opened NO wound (= glanced). Detection is
        /// ClearWounds' return value - false means the ledger had nothing to close - so it
        /// needs no sync wait and self-resets for the next hit. Heals every hit so the body
        /// never dies mid-experiment.
        /// </summary>
        private int GlancedOutOf(Entity victim, int n)
        {
            var hb = victim.GetBehavior<EntityBehaviorHealth>();
            int glanced = 0;
            for (int i = 0; i < n; i++)
            {
                victim.ReceiveDamage(Sharp(), 1.0f);
                if (!BleedSystem.ClearWounds(victim)) glanced++;
                hb.Health = hb.MaxHealth;
            }
            return glanced;
        }

        private void LiveGlance(HuntingConfig cfg)
        {
            var pig = SpawnPig();
            Check("pig-spawned", pig != null);
            if (pig == null) { Done(); return; }

            // Zero band is DETERMINISTIC: a deer-size body takes every single hit.
            SetSize(pig, 30f);
            int g30 = GlancedOutOf(pig, 150);
            Check("live-30hp-never-glances", g30 == 0, $"{g30}/150 glanced");

            // Bear band: ~5.2%, 400 hits, 3 sigma ~ [7,35] -> accept 3..45.
            SetSize(pig, 66f);
            int g66 = GlancedOutOf(pig, 400);
            Check("live-66hp-glances-rarely", g66 >= 3 && g66 <= 45, $"{g66}/400 glanced (~{g66 / 4.0:0.0}%, expect ~5.2%)");

            // Giant band: plain curve at 400 hp = 0.5*tanh(355/200) = 47.2%.
            // 400 hits, 3 sigma ~ +-7.5% -> accept 39.7..54.7%.
            SetSize(pig, 400f);
            int g400 = GlancedOutOf(pig, 400);
            float pct = g400 / 400f;
            Check("live-400hp-glances-at-curve", pct >= 0.397f && pct <= 0.547f, $"{g400}/400 glanced ({pct:P1}, expect 47.2%)");

            // Ceiling both directions on the SAME body: open one wound with glance disabled,
            // read the published per-tick damage after a real tick, flip the ceiling, repeat.
            cfg.GlanceMaxChance = 0f; // no glance interference for the damage half
            BleedSystem.ClearWounds(pig);
            pig.ReceiveDamage(Sharp(), 1.0f);
            _sapi.Event.RegisterCallback(_ =>
            {
                try
                {
                    float withCeiling = pig.WatchedAttributes.GetFloat("thbleeddmg", -1f);
                    // creature-weight 0.75 wound: (0.02 + 0.0025*eff(400,100)=99.98) * 0.75 = 0.2024
                    Check("tick-damage-with-ceiling", Near(withCeiling, 0.2024f, 0.03f), $"{withCeiling:0.0000} expect ~0.2024");

                    cfg.BleedHealthCeiling = 0f; // the other direction: raw 400 hp
                    BleedSystem.ClearWounds(pig);
                    pig.ReceiveDamage(Sharp(), 1.0f);
                    _sapi.Event.RegisterCallback(__ =>
                    {
                        try
                        {
                            float without = pig.WatchedAttributes.GetFloat("thbleeddmg", -1f);
                            // (0.02 + 0.0025*400) * 0.75 = 0.765
                            Check("tick-damage-without-ceiling", Near(without, 0.765f, 0.08f), $"{without:0.0000} expect ~0.765");
                            Check("ceiling-cuts-damage-about-3.8x",
                                withCeiling > 0f && without / withCeiling > 3.0f && without / withCeiling < 4.6f,
                                $"ratio {(withCeiling > 0f ? without / withCeiling : -1f):0.00}");
                            pig.Die(EnumDespawnReason.Removed);
                            BonesChecks(cfg, Done);
                        }
                        catch (Exception e) { _sapi.Logger.Error("[biggame] EXCEPTION: {0}", e); Done(); }
                    }, 4500);
                }
                catch (Exception e) { _sapi.Logger.Error("[biggame] EXCEPTION: {0}", e); Done(); }
            }, 4500);
        }
    }
}
