using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TassHunting;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace TassHuntingCompatHarness
{
    /// <summary>
    /// KILLER NAMES (TASSHUNTING_KILLERNAMES=run). Proves the death-message naming in BOTH
    /// directions in one boot: with the feature on, a spawned tyrannosaurus must name itself
    /// "Tyrannosaurus - Tyrant Lizard King" through the engine's own
    /// Entity.GetPrefixAndCreatureName (the exact call the death broadcast and the damage log
    /// make), a family-only dino gets its family common name, and a vanilla wolf keeps its
    /// vanilla words; with the feature flipped off mid-run, the same rex must read
    /// "a wild animal" again - the field symptom restored, so a pass proves the fix and not
    /// a coincidence. The killing-blow witness rules that are easy to get wrong (one death
    /// consumes one record, expiry, and which damage sources count as "names nothing") are
    /// driven directly through KillerNames' internals by reflection, since a headless server
    /// has no real player to bleed out.
    /// </summary>
    public class KillerNamesHarnessSystem : ModSystem
    {
        private ICoreServerAPI _sapi = null!;
        private int _total, _passed;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            if (Environment.GetEnvironmentVariable("TASSHUNTING_KILLERNAMES") != "run") return;
            _sapi = api;
            api.Event.SaveGameLoaded += () => api.Event.RegisterCallback(_ => Run(), 3000);
            api.Logger.Notification("[killernames] armed.");
        }

        private void Check(string name, bool ok, string? detail = null)
        {
            _total++;
            if (ok) _passed++;
            _sapi.Logger.Notification("[killernames] {0} {1}{2}", ok ? "PASS" : "FAIL", name,
                detail == null ? "" : " (" + detail + ")");
        }

        private void Run()
        {
            try
            {
                var cfg = HuntingModSystem.Cfg;
                Check("feature-on-at-boot", cfg != null && cfg.KillerNamesEnabled);
                string generic = Lang.Get("generic-wildanimal");

                // ---- the naming layer, through the engine's own call ----
                string? rexName = NameOfSpawned("tyrannosauridae-tyrannosaurus-adult-male");
                Check("rex-named-with-species-override", rexName == "Tyrannosaurus - T-Rex", rexName);

                string? tarboName = NameOfSpawned("tyrannosauridae-tarbosaurus-adult-male");
                Check("family-only-dino-gets-family-name", tarboName == "Tarbosaurus - Rex", tarboName);

                string? raptorName = NameOfSpawned("dromaeosauridae-velociraptor-adult-male");
                if (raptorName == null) _sapi.Logger.Notification("[killernames] no velociraptor type - raptor check skipped");
                else Check("velociraptor-family-name", raptorName == "Velociraptor - Raptor", raptorName);

                string? wolfName = NameOfSpawned("wolf-eurasian-adult-male");
                Check("vanilla-wolf-untouched", wolfName == "a wolf", wolfName);

                // BOTH DIRECTIONS: flip the switch off and the field symptom must come back.
                cfg!.KillerNamesEnabled = false;
                string? rexOff = NameOfSpawned("tyrannosauridae-tyrannosaurus-adult-male");
                Check("switch-off-restores-wild-animal", rexOff == generic, rexOff);
                cfg.KillerNamesEnabled = true;
                string? rexOn = NameOfSpawned("tyrannosauridae-tyrannosaurus-adult-male");
                Check("switch-back-on-names-again", rexOn == "Tyrannosaurus - T-Rex", rexOn);

                // ---- the death-message patch is really attached ----
                var simType = AccessTools.TypeByName("Vintagestory.Server.ServerSystemEntitySimulation");
                var deathMethod = simType == null ? null : AccessTools.Method(simType, "GetDeathMessage");
                var patches = deathMethod == null ? null : Harmony.GetPatchInfo(deathMethod);
                Check("death-message-patch-attached",
                    patches != null && patches.Prefixes.Any(p => p.owner == "tasshunting"));

                // ---- witness bookkeeping rules, driven through the real internals ----
                var kn = typeof(HuntingModSystem).Assembly.GetType("TassHunting.KillerNames");
                var blowType = kn?.GetNestedType("WitnessedBlow", BindingFlags.NonPublic);
                var blowsField = kn == null ? null : AccessTools.Field(kn, "blows");
                var consume = kn == null ? null : AccessTools.Method(kn, "Consume");
                var namesSomething = kn == null ? null : AccessTools.Method(kn, "NamesSomething");
                Check("witness-internals-reachable",
                    blowType != null && blowsField != null && consume != null && namesSomething != null);

                if (blowType != null && blowsField != null && consume != null)
                {
                    var blows = (IDictionary)blowsField.GetValue(null)!;

                    // One death consumes one record: first take returns it, second finds nothing.
                    blows["uid-live"] = MakeBlow(blowType, "a test wolf", DateTime.UtcNow);
                    Check("witness-consumed-once", consume.Invoke(null, new object[] { "uid-live" }) != null);
                    Check("witness-second-take-empty", consume.Invoke(null, new object[] { "uid-live" }) == null);

                    // An aged-out blow is dropped, not used - and dropped for good.
                    blows["uid-stale"] = MakeBlow(blowType, "a stale bear",
                        DateTime.UtcNow.AddSeconds(-(HuntingModSystem.Cfg.KillerWitnessMemorySeconds + 60)));
                    Check("witness-expired-is-null", consume.Invoke(null, new object[] { "uid-stale" }) == null);
                    Check("witness-expired-still-consumed", !blows.Contains("uid-stale"));
                }

                if (namesSomething != null)
                {
                    bool NS(DamageSource? s) => (bool)namesSomething.Invoke(null, new object?[] { s })!;
                    Check("null-source-names-nothing", !NS(null));
                    Check("placeholder-source-names-nothing",
                        !NS(new DamageSource { Source = EnumDamageSource.Internal, Type = EnumDamageType.Injury }));
                    Check("bare-entity-source-names-nothing",
                        !NS(new DamageSource { Source = EnumDamageSource.Entity, Type = EnumDamageType.BluntAttack }));
                    Check("fall-still-names-itself",
                        NS(new DamageSource { Source = EnumDamageSource.Fall, Type = EnumDamageType.Gravity }));
                }

                Done();
            }
            catch (Exception e)
            {
                _sapi.Logger.Error("[killernames] EXCEPTION: {0}", e);
                Done();
            }
        }

        private static object MakeBlow(Type blowType, string display, DateTime when)
        {
            object blow = Activator.CreateInstance(blowType)!;
            AccessTools.Field(blowType, "Display").SetValue(blow, display);
            AccessTools.Field(blowType, "WhenUtc").SetValue(blow, when);
            return blow;
        }

        /// <summary>Spawn the named creature, ask the engine for its killed-by words the way
        /// the death broadcast does, despawn it. Null when the type is not loaded.</summary>
        private string? NameOfSpawned(string codePath)
        {
            var type = _sapi.World.EntityTypes.FirstOrDefault(t => t?.Code?.Path == codePath);
            if (type == null) return null;
            var spawn = _sapi.World.DefaultSpawnPosition;
            Entity ent = _sapi.World.ClassRegistry.CreateEntity(type);
            ent.ServerPos.SetPos(spawn.X + 4, spawn.Y + 1, spawn.Z + 4);
            ent.Pos.SetFrom(ent.ServerPos);
            _sapi.World.SpawnEntity(ent);
            string name;
            try { name = ent.GetPrefixAndCreatureName(); }
            finally { ent.Die(EnumDespawnReason.Removed); }
            return name;
        }

        private void Done() =>
            _sapi.Logger.Notification("[killernames] KILLERNAMES COMPLETE total={0} pass={1} fail={2}",
                _total, _passed, _total - _passed);
    }
}
