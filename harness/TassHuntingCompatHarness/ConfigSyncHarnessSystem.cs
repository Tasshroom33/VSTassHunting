using System;
using System.IO;
using System.Linq;
using System.Reflection;
using TassHunting;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace TassHuntingCompatHarness
{
    /// <summary>
    /// Config sync + zero-means-vanilla checks (TASSHUNTING_CFGSYNCTEST=1; headless, no
    /// client). Proves the pure halves of the earwiq 2026-08-10 fix: the server config
    /// rules every gameplay field, [ClientPersonal] look-and-feel fields survive the
    /// merge, HarvestTimeMult 0 now means vanilla, bad server json falls back to local,
    /// and the sync packet survives a real protobuf round trip (the default-omission
    /// trap is why the packet is one json string).
    /// PASS/FAIL lines ending in "CFGSYNC COMPLETE total= pass= fail=".
    /// The end-to-end half (a real remote client with a divergent local file) is
    /// Run-HarvestSyncClientTest.ps1.
    /// </summary>
    public class ConfigSyncHarnessSystem : ModSystem
    {
        private ICoreServerAPI _sapi = null!;
        private int _total, _passed;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            if (Environment.GetEnvironmentVariable("TASSHUNTING_CFGSYNCTEST") != "1") return;
            _sapi = api;
            api.Event.SaveGameLoaded += () => api.Event.RegisterCallback(_ => Run(), 3000);
            api.Logger.Notification("[cfgsync] armed.");
        }

        private void Check(string name, bool ok)
        {
            _total++;
            if (ok) _passed++;
            _sapi.Logger.Notification("[cfgsync] {0} {1}", ok ? "PASS" : "FAIL", name);
        }

        private static bool Near(float a, float b) => Math.Abs(a - b) < 0.0005f;

        private void Run()
        {
            try
            {
                // ---- zero means vanilla (earwiq set 0.00 expecting vanilla) ----
                var c = new HuntingConfig { HarvestTimeMult = 0f };
                c.Sanitize();
                Check("sanitize-zero-means-vanilla", Near(c.HarvestTimeMult, 1f));
                c.HarvestTimeMult = -3f; c.Sanitize();
                Check("sanitize-negative-means-vanilla", Near(c.HarvestTimeMult, 1f));
                c.HarvestTimeMult = 0.3f; c.Sanitize();
                Check("sanitize-keeps-real-values", Near(c.HarvestTimeMult, 0.3f));
                c.HarvestTimeMult = 0.05f; c.Sanitize();
                Check("sanitize-keeps-fast-floor", Near(c.HarvestTimeMult, 0.05f));
                c.HarvestTimeMult = 99f; c.Sanitize();
                Check("sanitize-caps-high", Near(c.HarvestTimeMult, 10f));

                // ---- the merge: server rules gameplay, the player keeps their look ----
                // The server half is EXACTLY the reported config: autodrop off, mult 0.
                var server = new HuntingConfig
                {
                    HarvestAutoDrop = false,
                    HarvestTimeMult = 0f,
                    EmptyCorpseAutoRemove = true,
                    BleedWoundSeconds = 77f,
                    PowerShotExtraDrawSeconds = 2.5f
                };
                var local = new HuntingConfig
                {
                    BleedHudPosition = "RightBottom",
                    BloodVisualsEnabled = false,
                    PowerShotDrawCue = false
                };
                var merged = HuntingConfigSync.BuildSessionConfig(HuntingConfigSync.Serialize(server), local);
                Check("merge-server-rules-autodrop", merged.HarvestAutoDrop == false);
                Check("merge-server-mult-sanitized", Near(merged.HarvestTimeMult, 1f));
                Check("merge-server-rules-gameplay", merged.EmptyCorpseAutoRemove
                    && Near(merged.BleedWoundSeconds, 77f)
                    && Near(merged.PowerShotExtraDrawSeconds, 2.5f));
                Check("merge-keeps-personal-hud", merged.BleedHudPosition == "RightBottom");
                Check("merge-keeps-personal-visuals", merged.BloodVisualsEnabled == false
                    && merged.PowerShotDrawCue == false);

                // Bad server json must fall back to the local config, sanitized.
                var fallback = HuntingConfigSync.BuildSessionConfig("{ not json",
                    new HuntingConfig { HarvestTimeMult = 0f, BleedHudPosition = "RightTop" });
                Check("merge-bad-json-falls-back", fallback != null
                    && fallback.BleedHudPosition == "RightTop" && Near(fallback.HarvestTimeMult, 1f));

                // ---- the attribute census: gameplay unmarked, look-and-feel marked ----
                bool Personal(string field) =>
                    typeof(HuntingConfig).GetField(field)?.GetCustomAttribute<ClientPersonalAttribute>() != null;
                Check("census-gameplay-server-ruled",
                    !Personal(nameof(HuntingConfig.HarvestAutoDrop))
                    && !Personal(nameof(HuntingConfig.HarvestTimeMult))
                    && !Personal(nameof(HuntingConfig.EmptyCorpseAutoRemove))
                    && !Personal(nameof(HuntingConfig.BleedWoundSeconds))
                    && !Personal(nameof(HuntingConfig.PowerShotDamageMult)));
                Check("census-look-is-personal",
                    Personal(nameof(HuntingConfig.BleedHudPosition))
                    && Personal(nameof(HuntingConfig.BloodTrails))
                    && Personal(nameof(HuntingConfig.PowerShotDrawCue))
                    && Personal(nameof(HuntingConfig.BloodColorHex)));

                // ---- the packet through real protobuf, carrying a config full of
                // non-default falses - the exact values the wire loves to drop ----
                var pkt = new HuntingConfigSyncPacket { ConfigJson = HuntingConfigSync.Serialize(server) };
                using var ms = new MemoryStream();
                ProtoBuf.Serializer.Serialize(ms, pkt);
                ms.Position = 0;
                var back = ProtoBuf.Serializer.Deserialize<HuntingConfigSyncPacket>(ms);
                var rebuilt = HuntingConfigSync.BuildSessionConfig(back.ConfigJson, new HuntingConfig());
                Check("packet-roundtrip-carries-false", rebuilt.HarvestAutoDrop == false
                    && Near(rebuilt.BleedWoundSeconds, 77f));
            }
            catch (Exception e)
            {
                _sapi.Logger.Error("[cfgsync] EXCEPTION: {0}", e);
                Check("no-exception", false);
            }
            _sapi.Logger.Notification("[cfgsync] CFGSYNC COMPLETE total={0} pass={1} fail={2}",
                _total, _passed, _total - _passed);
        }
    }
}
