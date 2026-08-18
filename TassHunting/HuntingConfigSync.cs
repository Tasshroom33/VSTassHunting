// CONFIG SYNC (field report earwiq 2026-08-10). The config used to be whatever each
// installation's own TassHunting.json said - server and every client separately. That
// is invisible in single player (client and server share one process and one static
// Cfg), but on a dedicated server every GAMEPLAY dial a client reads locally could
// disagree with the server. The report was exactly that: the server ran
// HarvestAutoDrop=false, the friend's client still had the default true, so the
// friend's own client suppressed the carcass window (Patch_SuppressCarcassWindow runs
// client-side) while the server never spilled the loot - that player simply could not
// loot a corpse the host looted fine. The knife-hold timer has the same shape: the
// client times the hold and the server re-verifies with ITS multiplier, so divergent
// HarvestTimeMult values can make the hold complete on one side and not the other.
//
// THE RULE: the server's config is the world's config. On join the server sends its
// whole config; the client swaps it in, keeping only the fields marked
// [ClientPersonal] - presentation the ConfigLib panel already leaves editable on a
// server (bleeding box corner, blood look, colors, cue sound, highlighter filter).
// A NEW config field is server-ruled unless somebody marks it personal, which is the
// safe failure direction: forgetting the mark costs a client a cosmetic preference,
// never a gameplay desync.
//
// PACKET SHAPE: one JSON string, deliberately. A [ProtoMember] per field would hit
// the protobuf default-omission trap (a bool field initialized true never transmits
// false - the exact poison HarvestAutoDrop needs to transmit); a string is immune,
// and new fields ride along with zero packet churn.

using System;
using System.Reflection;
using Newtonsoft.Json;
using ProtoBuf;

namespace TassHunting
{
    /// <summary>Marks a HuntingConfig field as this player's own look-and-feel choice:
    /// the server's value never overwrites it. Everything unmarked is server-ruled.</summary>
    [AttributeUsage(AttributeTargets.Field)]
    public class ClientPersonalAttribute : Attribute { }

    [ProtoContract]
    public class HuntingConfigSyncPacket
    {
        [ProtoMember(1, IsRequired = true)]
        public string ConfigJson = "";
    }

    public static class HuntingConfigSync
    {
        public const string ChannelName = "tasshunting";

        public static string Serialize(HuntingConfig cfg) => JsonConvert.SerializeObject(cfg);

        /// <summary>
        /// The client's session config: the server's config with this player's
        /// [ClientPersonal] fields kept from their local one. Pure - the harness proves
        /// the merge without a server. Falls back to the local config (sanitized) if the
        /// server json does not parse, which beats playing with silently-wrong values.
        /// </summary>
        public static HuntingConfig BuildSessionConfig(string serverJson, HuntingConfig localCfg)
        {
            HuntingConfig merged = null;
            try { merged = JsonConvert.DeserializeObject<HuntingConfig>(serverJson); }
            catch (Exception) { }
            if (merged == null)
            {
                localCfg?.Sanitize();
                return localCfg;
            }
            CopyClientPersonal(localCfg, merged);
            merged.Sanitize();
            return merged;
        }

        /// <summary>Copy every [ClientPersonal] field from one config onto another.</summary>
        public static void CopyClientPersonal(HuntingConfig from, HuntingConfig to)
        {
            if (from == null || to == null) return;
            foreach (var field in typeof(HuntingConfig).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.GetCustomAttribute<ClientPersonalAttribute>() == null) continue;
                field.SetValue(to, field.GetValue(from));
            }
        }
    }
}
