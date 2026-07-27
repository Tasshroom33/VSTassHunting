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
        private Entity? _pig, _attacker;
        private float _maxHealth;
        private int _onHurtBase;

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
            HuntingModSystem.Cfg.BleedWoundSeconds = 6f;

            _sapi.Event.RegisterCallback(_ => RunLiveHits(), 1500);
        }

        private Entity? SpawnPig(double x, double y, double z)
        {
            var type = System.Linq.Enumerable.FirstOrDefault(_sapi.World.EntityTypes,
                t => t?.Code?.Path != null && t.Code.Path.StartsWith("pig-") && t.Code.Path.Contains("-adult-"));
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
                    _maxHealth, 3f, 2, cfg.BleedComboMultiplier, cfg.BleedMaxWounds);
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
                Done();
            }
            catch (Exception e) { Crash(e); }
        }
    }
}
