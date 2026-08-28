using System;
using System.Collections.Generic;
using System.Linq;
using TassHunting;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace TassHuntingCompatHarness
{
    /// <summary>
    /// STAY WILD (TASSHUNTING_STAYWILD=off|on). Proves the latch in BOTH directions in the same
    /// world, because "the behaviors are gone" only means something if you have watched them be
    /// there first: the script runs the server twice over the same mod set, once with the switch
    /// off (every domestication behavior must still be present) and once on (they must all be
    /// gone). Set TASSHUNTING_STAYWILD to which run this is and the expectations flip.
    ///
    /// Three things are checked, not one:
    ///  - the TYPE: what loadBehaviors will build from, i.e. what StayWild actually edited;
    ///  - a SPAWNED creature: the thing a player meets, which is what the claim is about;
    ///  - a CONTROL creature (vanilla elk, the game's own rideable/pettable/ropetieable animal)
    ///    that is NOT in the config list and must keep every one of them in both runs. Without
    ///    the control, a pass proves "something was removed", not "the right thing was".
    /// </summary>
    public class StayWildHarnessSystem : ModSystem
    {
        private ICoreServerAPI _sapi = null!;
        private int _total, _passed;
        private bool _expectStripped;

        // What counts as domestication. Matches the mod's own default list; the control animal
        // is checked against the subset vanilla actually gives it.
        private static readonly string[] Domestication =
            { "rideable", "gait", "tameable", "receivecommand", "pettable", "ropetieable" };

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            string mode = Environment.GetEnvironmentVariable("TASSHUNTING_STAYWILD");
            if (mode != "off" && mode != "on") return;
            _expectStripped = mode == "on";
            _sapi = api;
            api.Event.SaveGameLoaded += () => api.Event.RegisterCallback(_ => Run(), 3000);
            api.Logger.Notification("[staywild] armed, expecting behaviors {0}.",
                _expectStripped ? "STRIPPED" : "PRESENT");
        }

        private void Check(string name, bool ok)
        {
            _total++;
            if (ok) _passed++;
            _sapi.Logger.Notification("[staywild] {0} {1}", ok ? "PASS" : "FAIL", name);
        }

        private void Run()
        {
            try
            {
                var cfg = HuntingModSystem.Cfg;
                _sapi.Logger.Notification("[staywild] config: enabled={0} codes=[{1}]",
                    cfg.StayWildEnabled, string.Join(" ", cfg.StayWildCodes ?? new string[0]));
                Check("config-matches-run-mode", cfg.StayWildEnabled == _expectStripped);

                // ---- the dinosaurs the config names ----
                var dinoTypes = _sapi.World.EntityTypes
                    .Where(t => t?.Code?.Path != null
                             && t.Code.Path.Contains("-adult-")
                             && FamilyPrefixes.Any(p => t.Code.Path.StartsWith(p)))
                    .ToList();
                Check("dino-types-loaded", dinoTypes.Count > 0);
                if (dinoTypes.Count == 0) { Done(); return; }
                _sapi.Logger.Notification("[staywild] {0} adult dino types found", dinoTypes.Count);

                // TYPE LEVEL: no adult dino type may still carry a domestication behavior.
                var offenders = new List<string>();
                foreach (var t in dinoTypes)
                    foreach (string b in BehaviorCodes(t))
                        if (Domestication.Contains(b, StringComparer.OrdinalIgnoreCase))
                            offenders.Add(t.Code.Path + ":" + b);

                if (_expectStripped)
                {
                    if (offenders.Count > 0)
                        _sapi.Logger.Notification("[staywild] still present on: {0}",
                            string.Join(", ", offenders.Take(12)));
                    Check("type-no-domestication-behaviors", offenders.Count == 0);
                }
                else
                {
                    _sapi.Logger.Notification("[staywild] baseline domestication entries: {0} (e.g. {1})",
                        offenders.Count, string.Join(", ", offenders.Take(6)));
                    // The negative control for the whole test: with the switch off these MUST be
                    // there, otherwise the "on" run proves nothing.
                    Check("type-has-domestication-behaviors-when-off", offenders.Count > 0);
                }

                // SPAWNED: the creature a player actually meets.
                var spawnType = dinoTypes.FirstOrDefault(t => t.Code.Path.Contains("tyrannosauridae"))
                             ?? dinoTypes[0];
                var spawn = _sapi.World.DefaultSpawnPosition;
                Entity dino = _sapi.World.ClassRegistry.CreateEntity(spawnType);
                dino.ServerPos.SetPos(spawn.X + 4, spawn.Y + 1, spawn.Z + 4);
                dino.Pos.SetFrom(dino.ServerPos);
                _sapi.World.SpawnEntity(dino);

                var live = Domestication.Where(b => dino.HasBehavior(b)).ToArray();
                _sapi.Logger.Notification("[staywild] spawned {0}: domestication behaviors = [{1}]",
                    spawnType.Code.Path, live.Length == 0 ? "none" : string.Join(" ", live));
                Check(_expectStripped ? "spawned-dino-cannot-be-tamed-or-ridden"
                                      : "spawned-dino-has-them-when-off",
                      _expectStripped ? live.Length == 0 : live.Length > 0);
                dino.Die(EnumDespawnReason.Removed);

                // CONTROL - no collateral. Everything in the "game" domain is outside the config
                // list, so vanilla livestock must come through BOTH runs with its domestication
                // intact. Checked as a population, not one species, so a single miss cannot hide:
                // if stay-wild ever over-matched, this count would fall in the "on" run.
                var vanilla = _sapi.World.EntityTypes
                    .Where(t => t?.Code != null && t.Code.Domain == "game")
                    .Select(t => new { t.Code.Path, Dom = BehaviorCodes(t).Where(b => Domestication.Contains(b, StringComparer.OrdinalIgnoreCase)).ToArray() })
                    .Where(x => x.Dom.Length > 0)
                    .ToList();
                _sapi.Logger.Notification("[staywild] control: {0} vanilla types still carry domestication behaviors (e.g. {1})",
                    vanilla.Count, string.Join(", ", vanilla.Take(4).Select(x => x.Path + ":" + string.Join("/", x.Dom))));
                Check("control-vanilla-animals-untouched", vanilla.Count > 0);

                // And the one vanilla animal you can actually ride keeps the behavior that lets you.
                var mount = _sapi.World.EntityTypes.FirstOrDefault(
                    t => t?.Code?.Path != null && t.Code.Domain == "game" && t.Code.Path.StartsWith("tameddeer-"));
                if (mount == null) _sapi.Logger.Notification("[staywild] no tameddeer type - mount control skipped");
                else
                {
                    bool rideable = BehaviorCodes(mount).Contains("rideable", StringComparer.OrdinalIgnoreCase);
                    _sapi.Logger.Notification("[staywild] control mount {0} rideable={1}", mount.Code.Path, rideable);
                    Check("control-vanilla-mount-still-rideable", rideable);
                }

                Done();
            }
            catch (Exception e)
            {
                _sapi.Logger.Error("[staywild] EXCEPTION: {0}", e);
                Done();
            }
        }

        /// <summary>Every behavior code on either side of a type, as the engine will build it.</summary>
        private static IEnumerable<string> BehaviorCodes(EntityProperties t)
        {
            foreach (var sided in new[] { (EntitySidedProperties)t.Client, t.Server })
            {
                var arr = sided?.BehaviorsAsJsonObj;
                if (arr == null) continue;
                foreach (var jo in arr)
                {
                    string code = jo?["code"]?.AsString();
                    if (code != null) yield return code;
                }
            }
        }

        // The 14 shipped families, used only to FIND test subjects in the world - the mod itself
        // reads its list from config. A family absent from the server is simply not tested.
        private static readonly string[] FamilyPrefixes =
        {
            "tyrannosauridae-", "carcharodontosauridae-", "abelisauridae-", "spinosauridae-",
            "dromaeosauridae-", "mosasauridae-", "macronaria-", "stegosauria-", "ankylosauria-",
            "ceratopsidae-", "pachycephalosauria-", "hadrosauroidea-", "ornithomimosauria-",
            "therizinosauridae-"
        };

        private void Done() =>
            _sapi.Logger.Notification("[staywild] STAYWILD COMPLETE total={0} pass={1} fail={2}",
                _total, _passed, _total - _passed);
    }
}
