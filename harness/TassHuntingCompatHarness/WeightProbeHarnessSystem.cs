using System;
using System.Collections.Generic;
using System.Linq;
using TassHunting;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace TassHuntingCompatHarness
{
    /// <summary>
    /// What does EntityProperties.Weight actually read at runtime (TASSHUNTING_WEIGHTPROBE=1)?
    /// The size-tiered bleed design rests on it, and vanilla declares weight two ways - a flat
    /// "weight" and a per-variant "weightByType" (bear-sun 57 ... bear-polar 705). A ladder keyed
    /// on a number that collapses to one value per SPECIES, or silently falls back to the 25f
    /// default, would be worthless - so this dumps the real resolved number per entity type
    /// before a line of the feature is written.
    /// </summary>
    public class WeightProbeHarnessSystem : ModSystem
    {
        private ICoreServerAPI _sapi = null!;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            if (Environment.GetEnvironmentVariable("TASSHUNTING_WEIGHTPROBE") != "1") return;
            _sapi = api;
            api.Event.SaveGameLoaded += () => api.Event.RegisterCallback(_ => Run(), 3000);
            api.Logger.Notification("[weightprobe] armed.");
        }

        private void Run()
        {
            try
            {
                var types = _sapi.World.EntityTypes;
                _sapi.Logger.Notification("[weightprobe] {0} entity types loaded", types.Count);

                // Does per-variant weight survive to the runtime property? Bear is the test:
                // five variants, five declared weights, one species.
                foreach (var t in types.Where(t => t?.Code?.Path != null && t.Code.Path.StartsWith("bear-"))
                                       .OrderBy(t => t.Weight))
                    _sapi.Logger.Notification("[weightprobe] VARIANT {0} = {1} kg", t.Code.Path, t.Weight);

                // The ladder's landmarks, adults only.
                string[] want = { "hare-", "fox-", "raccoon-", "chicken-", "gazelle-", "wolf-", "hyena-",
                                  "sheep-", "pig-", "goat-", "deer-", "elk-", "moose-",
                                  "drifter-", "shiver", "bowtorn", "locust", "player" };
                foreach (string w in want)
                {
                    var t = types.FirstOrDefault(t => t?.Code?.Path != null && t.Code.Path.StartsWith(w)
                                                      && (t.Code.Path.Contains("adult") || !t.Code.Path.Contains("baby")));
                    if (t != null)
                        _sapi.Logger.Notification("[weightprobe] {0,-28} = {1,7} kg", t.Code.Path, t.Weight);
                    else
                        _sapi.Logger.Notification("[weightprobe] {0,-28} = NOT FOUND", w);
                }

                // How many types would land in a bracket purely by the 25f engine default?
                // That is the modded-creature blind spot the ladder has to tolerate.
                int atDefault = types.Count(t => t != null && Math.Abs(t.Weight - 25f) < 0.0001f);
                _sapi.Logger.Notification("[weightprobe] types sitting exactly on the 25kg default: {0}/{1}",
                    atDefault, types.Count);

                // Spread, so bracket lines can be drawn against reality rather than guesswork.
                var live = types.Where(t => t != null && t.Weight > 0).OrderBy(t => t.Weight).ToList();
                if (live.Count > 0)
                {
                    _sapi.Logger.Notification("[weightprobe] lightest {0} ({1} kg), heaviest {2} ({3} kg)",
                        live[0].Code?.Path, live[0].Weight, live[^1].Code?.Path, live[^1].Weight);
                    foreach (int pct in new[] { 25, 50, 75, 90 })
                    {
                        var t = live[Math.Min(live.Count - 1, live.Count * pct / 100)];
                        _sapi.Logger.Notification("[weightprobe] p{0} = {1} kg ({2})", pct, t.Weight, t.Code?.Path);
                    }
                }
                _sapi.Logger.Notification("[weightprobe] WEIGHTPROBE COMPLETE");
            }
            catch (Exception e)
            {
                _sapi.Logger.Error("[weightprobe] EXCEPTION: {0}", e);
                _sapi.Logger.Notification("[weightprobe] WEIGHTPROBE COMPLETE");
            }
        }
    }
}
