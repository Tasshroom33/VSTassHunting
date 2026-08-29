using System;
using System.Diagnostics;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace TassHuntingCompatHarness
{
    /// <summary>
    /// CRAFT SPIKE PROFILER (TASSHUNTING_CRAFTSPIKE=1, client only). v3 proved every
    /// "drop into grid" click costs ~360-400ms in the click itself (tick-dcentity) plus
    /// 3 InventoryUpdate packets at ~120-165ms each (readpacket31). Both halves funnel
    /// through DidModifyItemSlot -> FindMatchingRecipe. This decomposes that time with
    /// stopwatch patches so the expensive LINE names itself:
    ///  - FindMatchingRecipe total (calls + ms)
    ///  - GridRecipe.Matches cumulative (the per-candidate check)
    ///  - FastSearchCraftingRecipeIngredient.SatisfiesAsIngredient cumulative (the key walk)
    ///  - GridRecipe.GenerateOutputStack cumulative (output stack creation)
    ///  - InventoryNetworkUtil.UpdateFromPacket total (the packet-31 half, logged when slow)
    /// CraftSpikeClientHarnessSystem resets and dumps these counters around every click.
    /// </summary>
    public static class SpikeCounters
    {
        public static long FmrTicks, FmrCalls, MatchTicks, MatchCalls, SatTicks, SatCalls, GenTicks, GenCalls;

        public static void Reset()
        {
            FmrTicks = FmrCalls = MatchTicks = MatchCalls = SatTicks = SatCalls = GenTicks = GenCalls = 0;
        }

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        public static string Dump() => string.Format(
            "FindMatchingRecipe {0:0.0}ms/{1}x, Matches {2:0.0}ms/{3}x, SatisfiesAsIngredient {4:0.0}ms/{5}x, GenerateOutputStack {6:0.0}ms/{7}x",
            Ms(FmrTicks), FmrCalls, Ms(MatchTicks), MatchCalls, Ms(SatTicks), SatCalls, Ms(GenTicks), GenCalls);
    }

    public class CraftSpikeProfilerSystem : ModSystem
    {
        private Harmony _harmony;
        internal static ICoreClientAPI Capi;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

        public override void StartClientSide(ICoreClientAPI api)
        {
            if (Environment.GetEnvironmentVariable("TASSHUNTING_CRAFTSPIKE") != "1") return;
            Capi = api;
            try
            {
                _harmony = new Harmony("tasshunting-craftspike-profiler");
                PatchTimed("Vintagestory.Common.InventoryCraftingGrid", "FindMatchingRecipe", null, nameof(FmrPost));
                PatchTimed("Vintagestory.API.Common.GridRecipe", "Matches",
                    new[] { typeof(IPlayer), typeof(IWorldAccessor), typeof(ItemSlot[]), typeof(int) }, nameof(MatchPost));
                PatchTimed("Vintagestory.API.Common.FastSearchCraftingRecipeIngredient", "SatisfiesAsIngredient", null, nameof(SatPost));
                PatchTimed("Vintagestory.API.Common.GridRecipe", "GenerateOutputStack", null, nameof(GenPost));
                PatchTimed("Vintagestory.Common.InventoryNetworkUtil", "UpdateFromPacket",
                    new[] { typeof(IWorldAccessor), AccessTools.TypeByName("Packet_InventoryUpdate") }, nameof(PktPost));
                api.Logger.Notification("[craftspike] profiler patches applied");
            }
            catch (Exception e) { api.Logger.Error("[craftspike] profiler patch failed: {0}", e); }
        }

        private void PatchTimed(string typeName, string methodName, Type[] sig, string postName)
        {
            var t = AccessTools.TypeByName(typeName) ?? throw new Exception("type not found: " + typeName);
            var m = sig == null ? AccessTools.Method(t, methodName) : AccessTools.Method(t, methodName, sig);
            if (m == null) throw new Exception("method not found: " + typeName + "." + methodName);
            _harmony.Patch(m,
                prefix: new HarmonyMethod(typeof(CraftSpikeProfilerSystem), nameof(TimePre)),
                postfix: new HarmonyMethod(typeof(CraftSpikeProfilerSystem), postName));
        }

        public static void TimePre(ref long __state) => __state = Stopwatch.GetTimestamp();

        public static void FmrPost(long __state) { SpikeCounters.FmrTicks += Stopwatch.GetTimestamp() - __state; SpikeCounters.FmrCalls++; }
        public static void MatchPost(long __state) { SpikeCounters.MatchTicks += Stopwatch.GetTimestamp() - __state; SpikeCounters.MatchCalls++; }
        public static void SatPost(long __state) { SpikeCounters.SatTicks += Stopwatch.GetTimestamp() - __state; SpikeCounters.SatCalls++; }
        public static void GenPost(long __state) { SpikeCounters.GenTicks += Stopwatch.GetTimestamp() - __state; SpikeCounters.GenCalls++; }

        public static void PktPost(long __state, object __0)
        {
            double ms = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
            if (ms > 20) Capi?.Logger.Notification("[craftspike] UpdateFromPacket took {0:0.0}ms", ms);
        }

        public override void Dispose()
        {
            _harmony?.UnpatchAll("tasshunting-craftspike-profiler");
            Capi = null;
        }
    }
}
