using System;
using TassHunting;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace TassHuntingCompatHarness
{
    /// <summary>
    /// Self-driving test for the 2026-07-27 wound-based bleed (TASSHUNTING_BLEEDTEST=1; no client
    /// needed). Two layers:
    ///   1. Pure math/ledger checks on WoundMath + WoundLedger (tier scaling, combo cap,
    ///      replacement at the cap, pin semantics).
    ///   2. Live checks on a real server: spawned pigs, synthetic sharp/blunt damage through the
    ///      real Harmony hook path, tick damage compared against the formula, wound expiry.
    /// PASS/FAIL log lines ending in "BLEEDTEST COMPLETE total= pass= fail=".
    /// </summary>
    public class BleedHarnessSystem : ModSystem
    {
        private ICoreServerAPI _sapi = null!;
        private int _total, _passed;
        private Entity? _pig, _attacker, _drifter;
        private float _maxHealth;
        private int _onHurtBase;
        // Stand-in for worn armor: multiplies an incoming sharp/blunt hit exactly where real
        // armor does (EntityBehaviorHealth.onDamaged, the hook ModSystemWearableStats uses), and
        // filters by damage type exactly as vanilla does, so bleed's own Injury ticks pass
        // through untouched. 1 = wearing nothing.
        private float _armorFactor = 1f;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            if (Environment.GetEnvironmentVariable("TASSHUNTING_BLEEDTEST") != "1") return;
            _sapi = api;
            api.Event.SaveGameLoaded += () => api.Event.RegisterCallback(_ => RunPure(), 8000);
            api.Logger.Notification("[bleedtest] armed.");
        }

        private void Check(string name, bool ok)
        {
            _total++;
            if (ok) _passed++;
            _sapi.Logger.Notification("[bleedtest] {0} {1}", ok ? "PASS" : "FAIL", name);
        }

        private void Done() =>
            _sapi.Logger.Notification("[bleedtest] BLEEDTEST COMPLETE total={0} pass={1} fail={2}", _total, _passed, _total - _passed);

        private void Crash(Exception e)
        {
            _sapi.Logger.Error("[bleedtest] EXCEPTION: {0}", e);
            Check("no-exception", false);
            Done();
        }

        private static bool Near(float a, float b) => Math.Abs(a - b) < 0.0005f;

        /// <summary>
        /// Pin the 2026-08-22 size ladder flat for the wound-MATH legs: one clock on every rung,
        /// every hit wounds, no second wound, no length growth. Those legs test the damage
        /// formula, armour and sitting - a 75% coin flip in the middle of them would make them
        /// flaky rather than strict, and the odds and the ladder have their own suite
        /// (Run-SizeLadderTest.ps1) that proves them against 10,000 real rolls.
        /// </summary>
        private static void PinLadder(float seconds)
        {
            var cfg = HuntingModSystem.Cfg;
            if (cfg.BleedSizeTiers != null)
                foreach (var t in cfg.BleedSizeTiers) { t.Seconds = seconds; t.Odds = 1f; t.SecondOdds = 0f; }
            if (cfg.BleedRustTiers != null)
                foreach (var r in cfg.BleedRustTiers) r.Seconds = seconds;
            cfg.BleedPlayerWoundSeconds = seconds;
            cfg.BleedLengthMultiplier = 1f;
            cfg.BleedWoundSeconds = seconds;
        }

        /// <summary>One creature wound's strength at the given tier: the claw dial, not a weapon
        /// weight, since every attacker in these legs swings with bare claws.</summary>
        private static float ClawStrength(int tier)
        {
            var cfg = HuntingModSystem.Cfg;
            return cfg.BleedCreatureWoundWeight * (1f + cfg.BleedTierStep * tier);
        }

        // ---- Layer 1: pure formulas and ledger rules -----------------------------------------

        private void RunPure()
        {
            try
            {
                Check("math-tier-scaling", Near(WoundMath.Strength(1f, 1, 0.25f), 1.25f)
                    && Near(WoundMath.Strength(1.5f, 3, 0.25f), 2.625f)
                    && Near(WoundMath.Strength(0.75f, 0, 0.25f), 0.75f)
                    && Near(WoundMath.Strength(1f, -5, 0.25f), 1f)); // bad tier clamps to 0

                // deer 21hp, one flint arrow: (0.05 + 0.005*21) * 1.25 = 0.19375
                Check("math-single-wound", Near(WoundMath.TotalPerTick(0.05f, 0.5f, 21f, 1.25f, 1, 1.3f, 10), 0.19375f));
                // three flint arrows: base 0.155 * 3.75 * 1.3^2 = 0.9823..
                Check("math-combo", Near(WoundMath.TotalPerTick(0.05f, 0.5f, 21f, 3.75f, 3, 1.3f, 10), 0.155f * 3.75f * 1.69f));
                // combo exponent caps at BleedMaxWounds even if count is larger
                Check("math-combo-cap", Near(
                    WoundMath.TotalPerTick(0.05f, 0.5f, 21f, 10f, 15, 1.3f, 10),
                    WoundMath.TotalPerTick(0.05f, 0.5f, 21f, 10f, 10, 1.3f, 10)));
                Check("math-empty", WoundMath.TotalPerTick(0.05f, 0.5f, 21f, 0f, 0, 1.3f, 10) == 0f);

                var led = new WoundLedger();
                for (int i = 0; i < 12; i++) led.Add(1f, 1000 + i, 0, 10);
                Check("ledger-cap", led.Count == 10);

                led.Clear();
                led.Add(1f, 5000, 77, 10);   // pinned by projectile 77
                led.Add(2f, 1000, 0, 10);    // expires early
                Check("ledger-sum", Near(led.StrengthSum, 3f));
                led.ExpireStep(2000);
                Check("ledger-expiry", led.Count == 1 && Near(led.StrengthSum, 1f));
                led.ExpireStep(999999);
                Check("ledger-pin-survives", led.Count == 1); // pinned wound never times out
                led.SyncPins(new System.Collections.Generic.HashSet<long>(), 3000); // arrow gone
                led.ExpireStep(2500);
                Check("ledger-unpin-grace", led.Count == 1);  // fresh window after release
                led.ExpireStep(3500);
                Check("ledger-unpin-expires", led.Count == 0);

                // At the cap, the soonest-ending wound is replaced; pinned wounds are kept.
                led.Clear();
                led.Add(1f, 100, 55, 2);  // pinned
                led.Add(1f, 100, 0, 2);   // unpinned, soonest-ending
                led.Add(9f, 200, 0, 2);   // arrives at cap
                Check("ledger-replace-soonest", led.Count == 2 && Near(led.StrengthSum, 10f)
                    && led.SnapshotPins().Count == 1);

                // The countdown the bleeding box shows: whole seconds until the LAST wound
                // closes, rounded up so "half a second left" never reads as zero; -1 while an
                // arrow pins one open (that wound has no closing time); 0 with nothing open.
                led.Clear();
                Check("ledger-secs-none", led.SecondsLeft(0) == 0);
                led.Add(1f, 10000, 0, 10);
                led.Add(1f, 4500, 0, 10);
                Check("ledger-secs-latest-wound", led.SecondsLeft(1000) == 9);
                Check("ledger-secs-rounds-up", led.SecondsLeft(9500) == 1);
                Check("ledger-secs-never-negative", led.SecondsLeft(20000) == 0);
                led.Add(1f, 5000, 42, 10); // arrow still in
                Check("ledger-secs-pinned", led.SecondsLeft(1000) == -1);

                // Sitting still (2026-08-03). The seated FLAG needs a real client pressing the
                // sit key, but the RULE is pure timestamp math and is proven here: an unbroken
                // stretch before it helps, instant loss on standing up, no credit carried into
                // the next sit, and a pinned wound that no amount of sitting closes.
                Check("sit-needs-unbroken-time", !SitRule.Helps(SitRule.Track(0, true, 1000), 4000, 5f)
                    && SitRule.Helps(SitRule.Track(0, true, 1000), 6000, 5f));
                long sat = SitRule.Track(0, true, 1000);
                sat = SitRule.Track(sat, true, 4000);
                Check("sit-keeps-its-start", sat == 1000);
                sat = SitRule.Track(sat, false, 4500);       // stood up one second short
                Check("sit-standing-zeroes-credit", sat == 0);
                sat = SitRule.Track(sat, true, 5000);        // sat straight back down
                Check("sit-restart-earns-nothing", sat == 5000 && !SitRule.Helps(sat, 9000, 5f)
                    && SitRule.Helps(sat, 10000, 5f));
                Check("sit-half-time-burns-double", SitRule.ExtraCloseMs(1000, 0.5f) == 1000
                    && SitRule.ExtraCloseMs(1000, 1f) == 0
                    && SitRule.ExtraCloseMs(0, 0.5f) == 0);
                Check("sit-degenerate-mult-safe", SitRule.ExtraCloseMs(1000, 0f) == 19000);

                led.Clear();
                led.Add(1f, 10000, 0, 10);
                led.Accelerate(3000);
                Check("ledger-accelerate", led.SecondsLeft(0) == 7);
                led.Clear();
                led.Add(1f, 10000, 88, 10);                  // arrow still in
                led.Accelerate(60000);
                led.ExpireStep(999999);
                Check("ledger-accelerate-skips-pinned", led.Count == 1);

                // Power shot: threshold math mirrors BaseAimingAccuracy exactly.
                Check("powershot-default-full-acc", Near(PowerShot.FullAccuracySeconds(1f, 1f, 1f), 0.925f / 1.7f));
                Check("powershot-slow-draw-scales", Near(PowerShot.FullAccuracySeconds(1f, 0.5f, 1f), 2f * (0.925f / 1.7f))
                    && Near(PowerShot.FullAccuracySeconds(1f, 1f, 0.5f), 2f * (0.925f / 1.7f)));
                Check("powershot-degenerate-fallback", Near(PowerShot.FullAccuracySeconds(0f, 0f, 0f), 0.544f));

                // Stash/consume: fresh consumes once, stale never boosts a later quick shot.
                PowerShot.Stash(9001, 1.25f, 1000);
                Check("powershot-consume-fresh", Near(PowerShot.Consume(9001, 1500), 1.25f));
                Check("powershot-consume-once", Near(PowerShot.Consume(9001, 1600), 1f));
                PowerShot.Stash(9001, 1.25f, 1000);
                Check("powershot-stale-purged", Near(PowerShot.Consume(9001, 2500), 1f));

                // Draw cue fires exactly once per crossing, and re-arms on a fresh draw.
                Check("powershot-cue-not-early", !PowerShot.CrossedThreshold(9002, 0.5f, 1.54f));
                Check("powershot-cue-on-cross", PowerShot.CrossedThreshold(9002, 1.6f, 1.54f));
                Check("powershot-cue-not-again", !PowerShot.CrossedThreshold(9002, 1.7f, 1.54f));
                Check("powershot-cue-rearms", !PowerShot.CrossedThreshold(9002, 0.2f, 1.54f)
                    && PowerShot.CrossedThreshold(9002, 1.6f, 1.54f));

                // Predator speed: an adult wolf's chase task must be vanilla 0.045 times the
                // configured multiplier - pinned to the shipped vanilla value so a silently
                // inactive speed pass cannot slip through as a pass.
                float wolfChase = ReadTaskMovespeed("wolf-", "-adult-", "seekentity");
                Check("predspeed-wolf-chase-scaled",
                    Near(wolfChase, 0.045f * HuntingModSystem.Cfg.PredatorSpeedMult));

                RunLiveSetup();
            }
            catch (Exception e) { Crash(e); }
        }

        // ---- Layer 2: the real server path ---------------------------------------------------

        private void RunLiveSetup()
        {
            var spawn = _sapi.World.DefaultSpawnPosition;
            _pig = SpawnPig(spawn.X + 2, spawn.Y + 1, spawn.Z);
            _attacker = SpawnPig(spawn.X + 4, spawn.Y + 1, spawn.Z);
            Check("live-spawned", _pig != null && _attacker != null);
            if (_pig == null || _attacker == null) { Done(); return; }

            // Short wound life so expiry is testable in-run; the mutation only affects this
            // throwaway test server's in-memory config.
            PinLadder(6f);

            // Wire the fake armor in once, inert (factor 1) until the armor stage sets it.
            var hbSetup = _pig.GetBehavior<EntityBehaviorHealth>();
            if (hbSetup != null)
                hbSetup.onDamaged += (dmg, src) =>
                    src.Type == EnumDamageType.PiercingAttack || src.Type == EnumDamageType.SlashingAttack
                        || src.Type == EnumDamageType.BluntAttack
                        ? dmg * _armorFactor : dmg;

            _sapi.Event.RegisterCallback(_ => RunLiveHits(), 1500);
        }

        /// <summary>First matching entity type's first taskai task of the given code: movespeed.</summary>
        private float ReadTaskMovespeed(string pathPrefix, string pathContains, string taskCode)
        {
            var et = System.Linq.Enumerable.FirstOrDefault(_sapi.World.EntityTypes,
                t => t?.Code?.Path != null && t.Code.Path.StartsWith(pathPrefix) && t.Code.Path.Contains(pathContains));
            var behaviors = et?.Server?.BehaviorsAsJsonObj;
            if (behaviors == null) return -1f;
            foreach (var jo in behaviors)
            {
                if (!(jo?.Token is Newtonsoft.Json.Linq.JObject t) || t["code"]?.ToString() != "taskai") continue;
                if (!(t["aitasks"] is Newtonsoft.Json.Linq.JArray tasks)) continue;
                foreach (var jt in tasks)
                {
                    if (jt is Newtonsoft.Json.Linq.JObject task && task["code"]?.ToString() == taskCode && task["movespeed"] != null)
                        return (float)task["movespeed"];
                }
            }
            return -1f;
        }

        private Entity? SpawnPig(double x, double y, double z) => Spawn("pig-", "-adult-", x, y, z);

        private Entity? Spawn(string pathPrefix, string pathContains, double x, double y, double z)
        {
            var type = System.Linq.Enumerable.FirstOrDefault(_sapi.World.EntityTypes,
                t => t?.Code?.Path != null && t.Code.Path.StartsWith(pathPrefix) && t.Code.Path.Contains(pathContains));
            if (type == null) return null;
            Entity e = _sapi.World.ClassRegistry.CreateEntity(type);
            e.ServerPos.SetPos(x, y, z);
            e.Pos.SetFrom(e.ServerPos);
            _sapi.World.SpawnEntity(e);
            return e;
        }

        private DamageSource Sharp(int tier) => new DamageSource
        {
            Source = EnumDamageSource.Entity,
            SourceEntity = _attacker,
            Type = EnumDamageType.PiercingAttack,
            DamageTier = tier
        };

        /// <summary>The same hit, dealt by a rust being (a spawned drifter).</summary>
        private DamageSource RustSharp(int tier) => new DamageSource
        {
            Source = EnumDamageSource.Entity,
            SourceEntity = _drifter,
            Type = EnumDamageType.PiercingAttack,
            DamageTier = tier
        };

        private void RunLiveHits()
        {
            try
            {
                var hb = _pig!.GetBehavior<EntityBehaviorHealth>();
                Check("live-has-health", hb != null);
                _maxHealth = hb?.MaxHealth ?? 0f;

                // Hits go through the REAL damage path (ReceiveDamage -> health behavior -> our
                // Harmony postfix -> wound) and must be spaced past the engine's 500ms
                // invulnerability window - a hit inside it does no damage and so must not wound
                // either (first run of this test proved exactly that, unspaced).
                _pig.ReceiveDamage(Sharp(2), 1.5f);
                _sapi.Event.RegisterCallback(_ =>
                {
                    try
                    {
                        Check("live-first-wound", _pig.WatchedAttributes.GetInt("thbleed", 0) == 1);
                        _pig.ReceiveDamage(Sharp(2), 1.5f);
                        _sapi.Event.RegisterCallback(_2 =>
                        {
                            try
                            {
                                Check("live-two-wounds", _pig.WatchedAttributes.GetInt("thbleed", 0) == 2);
                                // Blunt hit must not wound (spaced, so it really lands).
                                _pig.ReceiveDamage(new DamageSource { Source = EnumDamageSource.Entity, SourceEntity = _attacker, Type = EnumDamageType.BluntAttack, DamageTier = 2 }, 1.5f);
                                _sapi.Event.RegisterCallback(_3 =>
                                {
                                    try
                                    {
                                        // Graze below the wound threshold must not wound either.
                                        _pig.ReceiveDamage(Sharp(2), 0.1f);
                                        Check("live-blunt-and-graze-ignored", _pig.WatchedAttributes.GetInt("thbleed", 0) == 2);
                                        // Bleed ticks between here and the tick check must NOT bump the
                                        // engine's hurt counter - that bump arms the 500ms damage-immunity
                                        // window that made arrows whiff against bleeding animals.
                                        _onHurtBase = _pig.WatchedAttributes.GetInt("onHurtCounter", 0);
                                        _sapi.Event.RegisterCallback(_4 => RunLiveTickCheck(), 4000);
                                    }
                                    catch (Exception e) { Crash(e); }
                                }, 700);
                            }
                            catch (Exception e) { Crash(e); }
                        }, 700);
                    }
                    catch (Exception e) { Crash(e); }
                }, 700);
            }
            catch (Exception e) { Crash(e); }
        }

        private void RunLiveTickCheck()
        {
            try
            {
                var cfg = HuntingModSystem.Cfg;
                // Two pierce wounds, tier 2, weight 1: strength 1.5 each, sum 3, combo^1.
                float expected = WoundMath.TotalPerTick(cfg.BleedStaticPerTick, cfg.BleedPctMaxHealthPerTick,
                    _maxHealth, ClawStrength(2) * 2f, 2, cfg.BleedComboMultiplier, cfg.BleedMaxWounds);
                float reported = _pig!.WatchedAttributes.GetFloat("thbleeddmg", -1f);
                Check("live-tick-damage-matches-formula", Near(reported, expected));
                Check("live-tick-counter-moved", _pig.WatchedAttributes.GetInt("thbleedtick", 0) >= 1);
                Check("live-ticks-dont-arm-invuln", _pig.WatchedAttributes.GetInt("onHurtCounter", 0) == _onHurtBase);

                var hb = _pig.GetBehavior<EntityBehaviorHealth>();
                Check("live-health-dropped", hb != null && hb.Health < _maxHealth);

                // Wounds close after the (shortened) window; bleed ends.
                _sapi.Event.RegisterCallback(_ => RunLiveExpiry(), 6500);
            }
            catch (Exception e) { Crash(e); }
        }

        private void RunLiveExpiry()
        {
            try
            {
                Check("live-wounds-closed", _pig!.WatchedAttributes.GetInt("thbleed", 0) == 0);
                // Re-open one wound, then dress it: a finished bandage/poultice sends exactly
                // this shape (Heal type with a Duration), and it must close every wound.
                _pig.ReceiveDamage(Sharp(2), 1.5f);
                _sapi.Event.RegisterCallback(_ => RunLiveBandage(), 700);
            }
            catch (Exception e) { Crash(e); }
        }

        /// <summary>
        /// The dressing path, through the real damage funnel. A healing item sends one
        /// Heal-typed DamageSource carrying a Duration; the per-tick heals that follow carry
        /// none, so only the application closes wounds - checked here both ways.
        /// </summary>
        private void RunLiveBandage()
        {
            try
            {
                Check("live-bandage-setup-wound", _pig!.WatchedAttributes.GetInt("thbleed", 0) == 1);

                // A bare heal TICK (no Duration) must leave the wound alone.
                _pig.ReceiveDamage(new DamageSource
                {
                    Source = EnumDamageSource.Internal,
                    Type = EnumDamageType.Heal
                }, 0.5f);
                Check("live-heal-tick-keeps-wound", _pig.WatchedAttributes.GetInt("thbleed", 0) == 1);

                // The application itself (vanilla poultice values: 4 health over 10s, 10 ticks).
                _pig.ReceiveDamage(new DamageSource
                {
                    Source = EnumDamageSource.Internal,
                    Type = EnumDamageType.Heal,
                    Duration = TimeSpan.FromSeconds(10),
                    TicksPerDuration = 10
                }, 4f);
                Check("live-bandage-stops-bleeding", _pig.WatchedAttributes.GetInt("thbleed", 0) == 0);
                Check("live-bandage-clears-ledger", BleedSystem.StacksOn(_pig.EntityId) == 0);
                _sapi.Event.RegisterCallback(_ => RunLiveAttackerClass(), 300);
            }
            catch (Exception e) { Crash(e); }
        }

        /// <summary>Full health and no open wounds, so each guard test starts from the same
        /// place and nothing dies mid-run.</summary>
        private void Reset()
        {
            BleedSystem.ClearWounds(_pig!);
            var hb = _pig!.GetBehavior<EntityBehaviorHealth>();
            if (hb != null) hb.Health = hb.MaxHealth;
        }

        /// <summary>
        /// WHO SWUNG. The attacker pig is a creature (not a player, not rust), so the creature
        /// dial governs its hits; a spawned drifter is rust and takes the rust dial. Both are
        /// checked at 0 (never wounds) and back at 1, through the real damage funnel. Hits are
        /// spaced past the engine's 500ms invulnerability window - a hit inside it lands for
        /// nothing and would prove nothing.
        /// </summary>
        private void RunLiveAttackerClass()
        {
            try
            {
                var spawn = _sapi.World.DefaultSpawnPosition;
                _drifter = Spawn("drifter-", "", spawn.X + 6, spawn.Y + 1, spawn.Z);
                Check("live-rust-classifier", _drifter != null
                    && HuntingModSystem.IsRustCreature(_drifter)
                    && !HuntingModSystem.IsRustCreature(_pig!));

                var cfg = HuntingModSystem.Cfg;
                Reset();
                cfg.BleedCreatureAttackWoundMult = 0f;
                _pig!.ReceiveDamage(Sharp(2), 1.5f);
                Check("live-creature-class-zero-blocks", _pig.WatchedAttributes.GetInt("thbleed", 0) == 0);

                _sapi.Event.RegisterCallback(_ =>
                {
                    try
                    {
                        cfg.BleedCreatureAttackWoundMult = 1f;
                        Reset();
                        _pig.ReceiveDamage(Sharp(2), 1.5f);
                        Check("live-creature-class-restored", _pig.WatchedAttributes.GetInt("thbleed", 0) == 1);

                        _sapi.Event.RegisterCallback(_2 =>
                        {
                            try
                            {
                                cfg.BleedRustAttackWoundMult = 0f;
                                Reset();
                                _pig.ReceiveDamage(RustSharp(2), 1.5f);
                                Check("live-rust-class-zero-blocks", _pig.WatchedAttributes.GetInt("thbleed", 0) == 0);

                                _sapi.Event.RegisterCallback(_3 =>
                                {
                                    try
                                    {
                                        cfg.BleedRustAttackWoundMult = 1f;
                                        Reset();
                                        _pig.ReceiveDamage(RustSharp(2), 1.5f);
                                        Check("live-rust-class-restored", _pig.WatchedAttributes.GetInt("thbleed", 0) == 1);
                                        _sapi.Event.RegisterCallback(_4 => RunLiveArmor(), 700);
                                    }
                                    catch (Exception e) { Crash(e); }
                                }, 700);
                            }
                            catch (Exception e) { Crash(e); }
                        }, 700);
                    }
                    catch (Exception e) { Crash(e); }
                }, 700);
            }
            catch (Exception e) { Crash(e); }
        }

        /// <summary>
        /// ARMOR, measured rather than queried. The fake armor sits on the same onDamaged hook
        /// vanilla armor uses, so this exercises the real prefix-to-postfix path: 88% absorbed
        /// turns the edge outright (and the hit still lands for 0.6, well past the smallest-hit
        /// threshold, so it is the ARMOR rule being proven and not that one), while 50% absorbed
        /// opens a wound of exactly half strength.
        /// </summary>
        private void RunLiveArmor()
        {
            try
            {
                var cfg0 = HuntingModSystem.Cfg;
                // CONTROL first, or "no wound" proves nothing: the exact same 88%-absorbed hit
                // with the turn-the-edge rule switched off HAS to open a wound. That pins the
                // blame for the next check on the armor rule and not on the hit being too small
                // for BleedMinDamage (0.6 through vs a 0.5 threshold is a thin margin).
                Reset();
                _armorFactor = 0.12f;
                cfg0.BleedArmorNoWoundAbsorb = 1f;   // never turn the edge
                _pig!.ReceiveDamage(Sharp(2), 5f);   // 5.0 in, 0.6 out
                Check("live-armor-control-wound-opens", _pig.WatchedAttributes.GetInt("thbleed", 0) == 1);
                var hbEdge = _pig.GetBehavior<EntityBehaviorHealth>();
                Check("live-armor-hit-still-landed", hbEdge != null && hbEdge.Health < hbEdge.MaxHealth);

                _sapi.Event.RegisterCallback(_c =>
                {
                    try
                    {
                        Reset();
                        cfg0.BleedArmorNoWoundAbsorb = 0.85f;   // back to the shipped rule
                        _pig.ReceiveDamage(Sharp(2), 5f);
                        Check("live-armor-turns-the-edge", _pig.WatchedAttributes.GetInt("thbleed", 0) == 0);
                        _sapi.Event.RegisterCallback(_ => RunLiveArmorHalf(), 700);
                    }
                    catch (Exception e) { Crash(e); }
                }, 700);
            }
            catch (Exception e) { Crash(e); }
        }

        private void RunLiveArmorHalf()
        {
            try
            {
                Reset();
                _armorFactor = 0.5f;
                _pig!.ReceiveDamage(Sharp(2), 4f);   // 4.0 in, 2.0 out
                Check("live-armor-half-wound-opens", _pig.WatchedAttributes.GetInt("thbleed", 0) == 1);
                _armorFactor = 1f;                   // bleed ticks must not be re-reduced
                _sapi.Event.RegisterCallback(_ => RunLiveArmorTick(), 4000);
            }
            catch (Exception e) { Crash(e); }
        }

        private void RunLiveArmorTick()
        {
            try
            {
                var cfg = HuntingModSystem.Cfg;
                // One pierce wound, tier 2, weight 1, half of it absorbed: 0.5 * (1 + 0.25*2) = 0.75.
                float expected = WoundMath.TotalPerTick(cfg.BleedStaticPerTick, cfg.BleedPctMaxHealthPerTick,
                    _maxHealth, ClawStrength(2) * 0.5f, 1, cfg.BleedComboMultiplier, cfg.BleedMaxWounds);
                Check("live-armor-halves-tick-damage", Near(_pig!.WatchedAttributes.GetFloat("thbleeddmg", -1f), expected));
                _sapi.Event.RegisterCallback(_ => RunLiveSit(), 700);
            }
            catch (Exception e) { Crash(e); }
        }

        /// <summary>
        /// SITTING STILL, driven end to end without a client: the sit flag goes straight onto the
        /// pig's ServerControls, which is the same state the "Sit down" key produces on a real
        /// player (EntityControls.FloorSitting -> AttemptToggleAction, which simply sets the flag
        /// when nothing handles the action). The SECOND pig is the control - same wound, same
        /// moment, never sits - so "the wound closed early" cannot be the ordinary wound timer
        /// running out. With a 15s wound and the shipped 5s/half/half rule, the seated pig's
        /// clock is spent 5s at normal speed and the remaining 10s at double, closing at about
        /// t=10; the control still has 4s left when both are checked at t=11.5.
        /// </summary>
        private void RunLiveSit()
        {
            try
            {
                var cfg = HuntingModSystem.Cfg;
                PinLadder(15f);
                cfg.BleedSittingHelps = true;
                cfg.BleedSitSecondsRequired = 5f;
                cfg.BleedSitDamageMult = 0.5f;
                cfg.BleedSitDurationMult = 0.5f;

                Reset();
                BleedSystem.ClearWounds(_attacker!);
                var hbCtl = _attacker!.GetBehavior<EntityBehaviorHealth>();
                if (hbCtl != null) hbCtl.Health = hbCtl.MaxHealth;

                _pig!.ReceiveDamage(Sharp(2), 1.5f);
                _attacker.ReceiveDamage(Sharp(2), 1.5f);
                Check("live-sit-both-wounded", _pig.WatchedAttributes.GetInt("thbleed", 0) == 1
                    && _attacker.WatchedAttributes.GetInt("thbleed", 0) == 1);

                ((EntityAgent)_pig).ServerControls.FloorSitting = true;
                Check("live-sit-flag-visible-server-side",
                    HuntingModSystem.IsSeated(_pig) && !HuntingModSystem.IsSeated(_attacker));

                float full = WoundMath.TotalPerTick(cfg.BleedStaticPerTick, cfg.BleedPctMaxHealthPerTick,
                    _maxHealth, ClawStrength(2), 1, cfg.BleedComboMultiplier, cfg.BleedMaxWounds);

                _sapi.Event.RegisterCallback(_ =>
                {
                    try
                    {
                        // Four seconds down: still short of the five it takes, so the tick that
                        // just landed has to be full strength.
                        Check("live-sit-not-yet-helping",
                            Near(_pig.WatchedAttributes.GetFloat("thbleeddmg", -1f), full));

                        _sapi.Event.RegisterCallback(_2 =>
                        {
                            try
                            {
                                Check("live-sit-halves-damage",
                                    Near(_pig.WatchedAttributes.GetFloat("thbleeddmg", -1f), full * 0.5f));

                                _sapi.Event.RegisterCallback(_3 =>
                                {
                                    try
                                    {
                                        Check("live-sit-closes-early", _pig.WatchedAttributes.GetInt("thbleed", 0) == 0);
                                        Check("live-sit-control-still-bleeding", _attacker.WatchedAttributes.GetInt("thbleed", 0) == 1);
                                        ((EntityAgent)_pig).ServerControls.FloorSitting = false;
                                        Check("live-sit-standing-up-reads-through", !HuntingModSystem.IsSeated(_pig));
                                        RunLivePlayerPublish();
                                    }
                                    catch (Exception e) { Crash(e); }
                                }, 3500);
                            }
                            catch (Exception e) { Crash(e); }
                        }, 4000);
                    }
                    catch (Exception e) { Crash(e); }
                }, 4000);
            }
            catch (Exception e) { Crash(e); }
        }

        /// <summary>
        /// The PLAYER publish path - what the bleeding box actually reads (field report
        /// 2026-08-19: box not showing). Every other leg wounds pigs; players have their
        /// own branches (BleedAffectsPlayers gate, the "thbleedsecs" countdown that only
        /// players get, the bleed-cause stamp). Fabricated offline player, the 0.13.5
        /// harness trick: PlayerUID is just a watched attribute, entity-identity checks
        /// work headless; despawned right after use because the engine spams caught NREs
        /// for its missing IPlayer. Its SpawnEntity also runs the 0.14.13 join scrub
        /// first, so this is the field chain in order: enter world -> scrub -> wound ->
        /// publish.
        /// </summary>
        private void RunLivePlayerPublish()
        {
            try
            {
                var cfg = HuntingModSystem.Cfg;
                cfg.BleedAffectsPlayers = true;
                PinLadder(60f);

                var spawn = _sapi.World.DefaultSpawnPosition;
                var ptype = System.Linq.Enumerable.FirstOrDefault(_sapi.World.EntityTypes,
                    t => t?.Code?.Path == "player");
                Check("live-player-type-found", ptype != null);
                if (ptype == null) { Done(); return; }
                Entity fake = _sapi.World.ClassRegistry.CreateEntity(ptype);
                fake.WatchedAttributes.SetString("playerUID", "bleedtest-fake-player");
                fake.ServerPos.SetPos(spawn.X + 8, spawn.Y + 1, spawn.Z);
                fake.Pos.SetFrom(fake.ServerPos);
                _sapi.World.SpawnEntity(fake);

                _sapi.Event.RegisterCallback(_ =>
                {
                    try
                    {
                        // Straight into the mod's own hit funnel entry (the exact call the
                        // damage postfix makes) - a fabricated player cannot take real
                        // nonzero damage headless: EntityPlayer.OnHurt does
                        // World.PlayerByUid(uid).LanguageCode for the damage log and NREs
                        // on the null IPlayer (decompile EntityPlayer.cs:1280). The full
                        // engine damage path on a REAL player is the hud smoke's job.
                        BleedSystem.OnSharpHit(fake, Sharp(2), 1.5f, 1.5f);
                        _sapi.Event.RegisterCallback(_2 =>
                        {
                            try
                            {
                                // What the box reads, present and sane after the join scrub.
                                Check("live-player-wound-publishes", fake.WatchedAttributes.GetInt("thbleed", 0) == 1);
                                Check("live-player-countdown-publishes", fake.WatchedAttributes.GetInt("thbleedsecs", 0) > 0);
                                // And a dressing zeroes both, so the box closes.
                                BleedSystem.OnHealItemApplied(fake, new DamageSource
                                {
                                    Source = EnumDamageSource.Internal,
                                    Type = EnumDamageType.Heal,
                                    Duration = TimeSpan.FromSeconds(10),
                                    TicksPerDuration = 10
                                });
                                Check("live-player-dressing-clears", fake.WatchedAttributes.GetInt("thbleed", 0) == 0
                                    && fake.WatchedAttributes.GetInt("thbleedsecs", 0) == 0);
                                try { fake.Die(EnumDespawnReason.Removed); } catch { }
                                Done();
                            }
                            catch (Exception e) { Crash(e); }
                        }, 1200);
                    }
                    catch (Exception e) { Crash(e); }
                }, 800);
            }
            catch (Exception e) { Crash(e); }
        }
    }
}
