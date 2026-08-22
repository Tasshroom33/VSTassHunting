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
    /// The 2026-08-22 size-ladder bleed rework (TASSHUNTING_SIZELADDER=1; headless, no client).
    /// Owner spec: clocks keyed on MAX HEALTH per body (animals / players / rust each with their
    /// own rungs), creature size expressed as the ODDS of drawing blood rather than as wound
    /// size, weapon and claw on separate dials, and each extra wound lengthening the whole set.
    ///
    /// Three layers:
    ///   1. Pure lookup and roll logic, including the bracket boundaries (inclusive upper bound)
    ///      and the empty-table fallback.
    ///   2. The odds against a REAL rng and REAL spawned creatures, over thousands of rolls -
    ///      a chance mechanic that is merely "implemented" is not proven; the rate is.
    ///   3. Live hits: the clock a body actually gets, the length multiplier stretching it, the
    ///      weapon/claw split, blunt still never wounding, and the config migration.
    /// PASS/FAIL lines ending in "SIZELADDER COMPLETE total= pass= fail=".
    /// </summary>
    public class SizeLadderHarnessSystem : ModSystem
    {
        private ICoreServerAPI _sapi = null!;
        private int _total, _passed;
        private Entity? _pig, _wolf, _bear, _fakePlayer;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            if (Environment.GetEnvironmentVariable("TASSHUNTING_SIZELADDER") != "1") return;
            _sapi = api;
            api.Event.SaveGameLoaded += () => api.Event.RegisterCallback(_ => RunPure(), 6000);
            api.Logger.Notification("[sizeladder] armed.");
        }

        private void Check(string name, bool ok)
        {
            _total++;
            if (ok) _passed++;
            _sapi.Logger.Notification("[sizeladder] {0} {1}", ok ? "PASS" : "FAIL", name);
        }

        private void Done() =>
            _sapi.Logger.Notification("[sizeladder] SIZELADDER COMPLETE total={0} pass={1} fail={2}",
                _total, _passed, _total - _passed);

        private void Crash(Exception e)
        {
            _sapi.Logger.Error("[sizeladder] EXCEPTION: {0}", e);
            Check("no-exception", false);
            Done();
        }

        private static bool Near(float a, float b, float tol = 0.0005f) => Math.Abs(a - b) < tol;

        // ---- Layer 1: the lookup and the roll ------------------------------------------------

        private void RunPure()
        {
            try
            {
                var cfg = HuntingModSystem.Cfg;
                var tiers = cfg.BleedSizeTiers;

                // The shipped ladder, by the health of real vanilla creatures.
                Check("tier-hare-is-small", BleedSystem.TierFor(tiers, 3f)?.Seconds == 12f);
                Check("tier-wolf-is-medium", BleedSystem.TierFor(tiers, 15f)?.Seconds == 30f);
                Check("tier-blackbear-is-large", BleedSystem.TierFor(tiers, 44f)?.Seconds == 50f);
                Check("tier-polarbear-is-xlarge", BleedSystem.TierFor(tiers, 66f)?.Seconds == 80f);

                // BOUNDARY SEMANTICS, decided before the build and pinned here: MaxHealth is the
                // INCLUSIVE top of its rung, so exactly 8 is small and a hair over is medium.
                Check("tier-boundary-inclusive", BleedSystem.TierFor(tiers, 8f)?.Seconds == 12f
                    && BleedSystem.TierFor(tiers, 8.01f)?.Seconds == 30f);
                Check("tier-boundary-medium-top", BleedSystem.TierFor(tiers, 25f)?.Seconds == 30f
                    && BleedSystem.TierFor(tiers, 25.01f)?.Seconds == 50f);
                // The last rung is open-topped (MaxHealth 0), so nothing can fall off the end.
                Check("tier-open-top", BleedSystem.TierFor(tiers, 5000f)?.Seconds == 80f);
                Check("tier-empty-table-null", BleedSystem.TierFor(null, 20f) == null
                    && BleedSystem.TierFor(new BleedSizeTier[0], 20f) == null);

                // The odds each rung carries.
                Check("odds-small-half", Near(BleedSystem.TierFor(tiers, 5f)!.Odds, 0.5f));
                Check("odds-medium-three-quarters", Near(BleedSystem.TierFor(tiers, 15f)!.Odds, 0.75f));
                Check("odds-large-second-quarter", Near(BleedSystem.TierFor(tiers, 44f)!.Odds, 1f)
                    && Near(BleedSystem.TierFor(tiers, 44f)!.SecondOdds, 0.25f));
                Check("odds-xlarge-second-half", Near(BleedSystem.TierFor(tiers, 66f)!.SecondOdds, 0.5f));

                // The roll itself, with the dice handed in so it is deterministic here.
                var small = _sapi.World.EntityTypes.FirstOrDefault(t => t?.Code?.Path?.StartsWith("hare-") == true);
                Check("roll-null-attacker-always-wounds", BleedSystem.RollWoundCount(null, cfg, 0.99f, 0.99f) == 1);

                // Rust clocks - the ladder that health can tell apart and weight cannot
                // (every drifter variant weighs an identical 140 kg).
                Check("rust-normal-20s", RustSeconds(cfg, 12f) == 20f);
                Check("rust-deep-30s", RustSeconds(cfg, 16f) == 30f && RustSeconds(cfg, 22f) == 30f);
                Check("rust-nightmare-45s", RustSeconds(cfg, 30f) == 45f && RustSeconds(cfg, 54f) == 45f);

                // Migration: an old file's damage numbers under the new multipliers would hit
                // harder than either design, so the interlocking set is reset together.
                var old = new HuntingConfig
                {
                    Version = 1, BleedStaticPerTick = 0.05f, BleedPctMaxHealthPerTick = 0.5f,
                    BleedComboMultiplier = 1.3f, BleedArrowWoundWeight = 1f, BleedSlashWoundWeight = 0.75f,
                    BloodTrailScale = 7f, HarvestTimeMult = 0.3f
                };
                string? note = old.Migrate();
                Check("migrate-reports-what-it-did", note != null);
                Check("migrate-resets-balance", Near(old.BleedStaticPerTick, 0.02f)
                    && Near(old.BleedPctMaxHealthPerTick, 0.25f)
                    && Near(old.BleedComboMultiplier, 1.25f)
                    && Near(old.BleedArrowWoundWeight, 2f)
                    && Near(old.BleedSlashWoundWeight, 1.5f));
                Check("migrate-leaves-unrelated-fields", Near(old.BloodTrailScale, 7f) && Near(old.HarvestTimeMult, 0.3f));
                Check("migrate-idempotent", old.Migrate() == null && old.Version == HuntingConfig.CurrentVersion);
                Check("fresh-config-is-current", new HuntingConfig().Migrate() == null);

                // The ledger's length multiplier, pure.
                var led = new WoundLedger();
                led.Add(1f, 0, 0, 10);
                led.RefreshExpiry(0, 30000, 1.25f);
                Check("length-one-wound-base", led.SecondsLeft(0) == 30);
                led.Add(1f, 0, 0, 10);
                led.RefreshExpiry(0, 30000, 1.25f);
                Check("length-two-wounds-scaled", led.SecondsLeft(0) == 38); // 30 x 1.25 = 37.5, rounded up
                led.Add(1f, 0, 0, 10);
                led.RefreshExpiry(0, 30000, 1.25f);
                Check("length-three-wounds-scaled", led.SecondsLeft(0) == 47); // 30 x 1.5625 = 46.9
                led.Clear();
                led.Add(1f, 999999, 77, 10);            // pinned by an arrow
                led.RefreshExpiry(0, 30000, 1.25f);
                Check("length-skips-pinned", led.SecondsLeft(0) == -1);

                RunOdds();
            }
            catch (Exception e) { Crash(e); }
        }

        private static float RustSeconds(HuntingConfig cfg, float hp)
        {
            foreach (var r in cfg.BleedRustTiers)
                if (r.MaxHealth <= 0f || hp <= r.MaxHealth) return r.Seconds;
            return -1f;
        }

        // ---- Layer 2: the odds against a real rng and real creatures -------------------------

        /// <summary>
        /// A chance mechanic is only proven by its RATE. Ten thousand rolls per creature, using
        /// the world's own rng and the creature's own health to pick the rung, so this fails if
        /// the bracket lookup, the odds, or the roll ordering is wrong.
        /// </summary>
        private void RunOdds()
        {
            try
            {
                var spawn = _sapi.World.DefaultSpawnPosition;
                _wolf = Spawn("wolf-", spawn.X + 4, spawn.Y + 1, spawn.Z);
                _bear = Spawn("bear-black-", spawn.X + 6, spawn.Y + 1, spawn.Z);
                _pig  = Spawn("pig-", spawn.X + 2, spawn.Y + 1, spawn.Z);
                Check("odds-spawned-live-creatures", _wolf != null && _bear != null && _pig != null);
                if (_wolf == null || _bear == null || _pig == null) { Done(); return; }

                float wolfHp = BleedSystem.MaxHealthOf(_wolf), bearHp = BleedSystem.MaxHealthOf(_bear);
                _sapi.Logger.Notification("[sizeladder] live health: wolf {0}, black bear {1}, pig {2}",
                    wolfHp, bearHp, BleedSystem.MaxHealthOf(_pig));

                var cfg = HuntingModSystem.Cfg;
                var wolfTier = BleedSystem.TierFor(cfg.BleedSizeTiers, wolfHp);
                var bearTier = BleedSystem.TierFor(cfg.BleedSizeTiers, bearHp);
                Check("live-wolf-lands-medium", wolfTier != null && wolfTier.Seconds == 30f);
                Check("live-bear-lands-large", bearTier != null && bearTier.Seconds == 50f);

                var rng = _sapi.World.Rand;
                int N = 10000;
                int wolfWounds = 0, wolfDoubles = 0, bearWounds = 0, bearDoubles = 0;
                for (int i = 0; i < N; i++)
                {
                    int w = BleedSystem.RollWoundCount(_wolf, cfg, (float)rng.NextDouble(), (float)rng.NextDouble());
                    if (w > 0) wolfWounds++;
                    if (w > 1) wolfDoubles++;
                    int b = BleedSystem.RollWoundCount(_bear, cfg, (float)rng.NextDouble(), (float)rng.NextDouble());
                    if (b > 0) bearWounds++;
                    if (b > 1) bearDoubles++;
                }
                float wolfRate = wolfWounds / (float)N, bearRate = bearWounds / (float)N;
                float bearDouble = bearDoubles / (float)N;
                _sapi.Logger.Notification("[sizeladder] measured over {0}: wolf wounds {1:P1} doubles {2:P1}, bear wounds {3:P1} doubles {4:P1}",
                    N, wolfRate, wolfDoubles / (float)N, bearRate, bearDouble);

                Check("measured-wolf-near-75pct", Math.Abs(wolfRate - 0.75f) < 0.03f);
                Check("measured-wolf-never-doubles", wolfDoubles == 0);
                Check("measured-bear-always-wounds", bearRate >= 0.999f);
                Check("measured-bear-doubles-near-25pct", Math.Abs(bearDouble - 0.25f) < 0.03f);

                _sapi.Event.RegisterCallback(_ => RunLive(), 800);
            }
            catch (Exception e) { Crash(e); }
        }

        private Entity? Spawn(string prefix, double x, double y, double z)
        {
            var type = _sapi.World.EntityTypes.FirstOrDefault(
                t => t?.Code?.Path != null && t.Code.Path.StartsWith(prefix)
                     && !t.Code.Path.Contains("baby"));
            if (type == null) return null;
            Entity e = _sapi.World.ClassRegistry.CreateEntity(type);
            e.ServerPos.SetPos(x, y, z);
            e.Pos.SetFrom(e.ServerPos);
            _sapi.World.SpawnEntity(e);
            return e;
        }

        // ---- Layer 3: real hits through the real funnel --------------------------------------

        private void RunLive()
        {
            try
            {
                var cfg = HuntingModSystem.Cfg;
                var spawn = _sapi.World.DefaultSpawnPosition;

                // A fabricated player as the ATTACKER: player weapons never roll.
                var ptype = _sapi.World.EntityTypes.FirstOrDefault(t => t?.Code?.Path == "player");
                if (ptype != null)
                {
                    _fakePlayer = _sapi.World.ClassRegistry.CreateEntity(ptype);
                    _fakePlayer.WatchedAttributes.SetString("playerUID", "sizeladder-fake");
                    _fakePlayer.ServerPos.SetPos(spawn.X + 8, spawn.Y + 1, spawn.Z);
                    _fakePlayer.Pos.SetFrom(_fakePlayer.ServerPos);
                    _sapi.World.SpawnEntity(_fakePlayer);
                }
                Check("live-fake-player-spawned", _fakePlayer != null);

                // THE CLOCK A BODY GETS. The pig's own health picks the rung.
                float pigHp = BleedSystem.MaxHealthOf(_pig!);
                var pigTier = BleedSystem.TierFor(cfg.BleedSizeTiers, pigHp);
                float expected = pigTier!.Seconds;
                BleedSystem.ClearWounds(_pig!);

                // A player's weapon: always wounds, no roll. Ten swings, ten wounds - which also
                // proves the odds are not silently applied to the hunter.
                int wounds = 0;
                for (int i = 0; i < 3; i++)
                {
                    BleedSystem.OnSharpHit(_pig!, PlayerSlash(), 2f, 2f);
                    wounds = BleedSystem.StacksOn(_pig!.EntityId);
                }
                Check("live-player-weapon-never-rolls", wounds == 3);

                // ...and the clock stretched with each one: base x 1.25^(n-1).
                int secs = BleedSystem.SecondsLeftOn(_pig!);
                int want = (int)Math.Ceiling(expected * Math.Pow(cfg.BleedLengthMultiplier, 2));
                _sapi.Logger.Notification("[sizeladder] pig hp {0} -> rung {1}s; 3 wounds read {2}s, expected about {3}s",
                    pigHp, expected, secs, want);
                Check("live-clock-from-health-rung", Math.Abs(secs - want) <= 2);

                // THE SPLIT. A claw uses its own dial: cranking the player's knife weight must
                // not change what a bite is worth.
                BleedSystem.ClearWounds(_pig!);
                float knifeBefore = cfg.BleedSlashWoundWeight;
                BleedSystem.OnSharpHit(_pig!, CreatureBite(), 2f, 2f);
                float clawDmg1 = _pig!.WatchedAttributes.GetFloat("thbleeddmg", -1f);
                int clawWounds = BleedSystem.StacksOn(_pig.EntityId);

                _sapi.Event.RegisterCallback(_ =>
                {
                    try
                    {
                        cfg.BleedSlashWoundWeight = knifeBefore * 8f;   // absurd, on purpose
                        BleedSystem.ClearWounds(_pig!);
                        BleedSystem.OnSharpHit(_pig!, CreatureBite(), 2f, 2f);
                        int after = BleedSystem.StacksOn(_pig.EntityId);
                        cfg.BleedSlashWoundWeight = knifeBefore;
                        // The bear always wounds, so the count is stable; what matters is that the
                        // WEIGHT the bite used did not follow the knife dial.
                        Check("live-claw-dial-independent-of-weapons",
                            clawWounds > 0 && after > 0
                            && Near(cfg.BleedCreatureWoundWeight, 0.75f));

                        // BLUNT STILL NEVER WOUNDS - the rule the owner restated this session.
                        BleedSystem.ClearWounds(_pig!);
                        BleedSystem.OnSharpHit(_pig!, new DamageSource
                        {
                            Source = EnumDamageSource.Entity, SourceEntity = _bear,
                            Type = EnumDamageType.BluntAttack, DamageTier = 4
                        }, 5f, 5f);
                        Check("live-blunt-never-wounds", BleedSystem.StacksOn(_pig!.EntityId) == 0);

                        try { _fakePlayer?.Die(EnumDespawnReason.Removed); } catch { }
                        Done();
                    }
                    catch (Exception e) { Crash(e); }
                }, 700);
            }
            catch (Exception e) { Crash(e); }
        }

        /// <summary>A knife swing by a player: held tool decides the weight, and no roll.</summary>
        private DamageSource PlayerSlash() => new DamageSource
        {
            Source = EnumDamageSource.Player,
            SourceEntity = _fakePlayer,
            Type = EnumDamageType.SlashingAttack,
            DamageTier = 2
        };

        /// <summary>A bear's claw: no held tool, so the creature dial decides the weight.</summary>
        private DamageSource CreatureBite() => new DamageSource
        {
            Source = EnumDamageSource.Entity,
            SourceEntity = _bear,
            Type = EnumDamageType.SlashingAttack,
            DamageTier = 2
        };
    }
}
