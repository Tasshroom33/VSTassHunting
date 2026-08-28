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
                LiveGlance(cfg);
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
                            Done();
                        }
                        catch (Exception e) { _sapi.Logger.Error("[biggame] EXCEPTION: {0}", e); Done(); }
                    }, 4500);
                }
                catch (Exception e) { _sapi.Logger.Error("[biggame] EXCEPTION: {0}", e); Done(); }
            }, 4500);
        }
    }
}
