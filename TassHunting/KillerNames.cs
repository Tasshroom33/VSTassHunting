using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace TassHunting
{
    /// <summary>
    /// KILLER NAMES (owner order 2026-08-29): death messages and the damage log name the
    /// actual animal instead of "a wild animal", and a bleed-out death still names what
    /// really put the player down.
    ///
    /// WHY VANILLA SAYS "WILD ANIMAL" FOR EVERY DINO: the engine builds the killer's name in
    /// Entity.GetPrefixAndCreatureName, which looks up "prefixandcreature-" lang keys in the
    /// KILLER'S OWN mod domain (tyrannosauridae:, dromaeosauridae:, ...). The Legacy of the
    /// Phanerozoic packs DO ship those keys - but prefixed into the game: domain, where the
    /// engine never looks for a modded creature. Every dino misses and falls back to
    /// generic-wildanimal. The packs are unmaintained (owner 2026-08-29), so this repairs the
    /// outcome from our side without forking them, and as a RULE, not a species list: any
    /// modded creature whose prefixandcreature key misses gets its real display name (the
    /// item-creature entry the packs ship correctly - the name you see on mouseover) instead
    /// of "a wild animal". The KillerCommonNames config map then adds the owner's requested
    /// "scientific name - common known name" flourish; unmapped creatures still get their
    /// real name.
    ///
    /// WHY THE WITNESS EXISTS: the Downed mod (live on this server) cancels a lethal hit,
    /// holds the player alive, and later calls Die itself. Downed 2.5.1 keeps the original
    /// blow in a plain field (decompile-verified) and usually passes it on, but a relog while
    /// downed, ForceDown, or a mod swap loses it - the engine then knows nothing and prints
    /// "Player X died." So we witness every blow that would have been lethal under vanilla
    /// rules (health - damage at or below zero, the engine's own test and exactly the moment a
    /// down-not-dead mod steps in), keyed on the health model, never on any mod's name - the
    /// same DEC-0018 pattern TassFactions ships for kill credit, so the two mods tell one
    /// story. TassFactions' own witness and the engine's PlayerDeath event are untouched:
    /// everything here only changes the STRING that reaches chat, never the DamageSource,
    /// so the who-died-and-how chain other mods read stays exactly as it was.
    ///
    /// Server-side only: both messages are built on the server and broadcast as plain text,
    /// so clients without the mod (or with it) see the same fixed words. No shared content is
    /// mutated, no engine collection is touched.
    /// </summary>
    public static class KillerNames
    {
        /// <summary>One killing blow we watched land on a player, kept until they die.</summary>
        private class WitnessedBlow
        {
            public string Display = "";  // finished chat words for the killer
            public DateTime WhenUtc;
        }

        private static ICoreServerAPI sapi;
        private static readonly Dictionary<string, WitnessedBlow> blows = new Dictionary<string, WitnessedBlow>(StringComparer.Ordinal);
        private static bool patched;
        private static bool disarmedName, disarmedDeath;
        private static System.Func<object, object> connectedClientPlayer;

        // DIAGNOSTICS LAW: /tassdeathnames answers "is it working" without a log dive.
        private static int namedCreatures, witnessedBlows, restoredDeaths;
        private static string lastRestoredLine = "";

        public static void StartServer(ICoreServerAPI api, Harmony harmony)
        {
            sapi = api;
            // Static state must not carry a previous world's records (same reset law as
            // LeavesPassthrough): clear per world, and let a re-enabled feature re-arm.
            blows.Clear();
            disarmedName = false; disarmedDeath = false;
            namedCreatures = 0; witnessedBlows = 0; restoredDeaths = 0; lastRestoredLine = "";

            if (!patched)
            {
                // Better creature names wherever the engine asks for one (death broadcast AND
                // the damage log both funnel through this method, so both improve together).
                var nameTarget = AccessTools.Method(typeof(Entity), nameof(Entity.GetPrefixAndCreatureName));
                if (nameTarget != null)
                    harmony.Patch(nameTarget, postfix: new HarmonyMethod(typeof(KillerNames), nameof(NamePostfix)));
                else
                    api.Logger.Warning("[TassHunting] killer names: Entity.GetPrefixAndCreatureName not found - creatures keep vanilla naming (engine rename?).");

                // The death broadcast builder, for the witness path. Private method on an
                // engine-internal server class, so it is resolved by name and the feature
                // degrades loudly to vanilla wording if a game update renames it.
                var simType = AccessTools.TypeByName("Vintagestory.Server.ServerSystemEntitySimulation");
                var deathTarget = simType == null ? null : AccessTools.Method(simType, "GetDeathMessage");
                if (deathTarget != null)
                    harmony.Patch(deathTarget, prefix: new HarmonyMethod(typeof(KillerNames), nameof(DeathMessagePrefix)));
                else
                    api.Logger.Warning("[TassHunting] killer names: ServerSystemEntitySimulation.GetDeathMessage not found - bleed-out deaths keep vanilla wording (engine rename?).");

                patched = true;
            }

            // Witness attach, once per session per player. The delegate is bound to this
            // session's health behavior instance and is garbage-collected with the entity on
            // logout - nothing to persist. Do NOT guard this with an Entity.Attributes flag:
            // that tree is SAVED TO DISK and would block re-registration every session after
            // the first (the exact trap TassFactions documents on its own attach).
            api.Event.PlayerNowPlaying += OnNowPlaying;
            // One death consumes one record and respawn/disconnect drop it outright, so a
            // stale blow can never name a later death.
            api.Event.PlayerRespawn += p => Forget(p?.PlayerUID);
            api.Event.PlayerDisconnect += p => Forget(p?.PlayerUID);

            api.ChatCommands.Create("tassdeathnames")
                .WithDescription("TassHunting killer names: what death messages are doing")
                .RequiresPrivilege(Privilege.chat)
                .HandleWith(_ =>
                {
                    var cfg = HuntingModSystem.Cfg;
                    return TextCommandResult.Success(string.Format(
                        "[tasshunting killer names] {0}. This session: {1} creature names fixed (vanilla said wild animal), {2} killing blows witnessed, {3} bleed-out deaths renamed. Players with a remembered blow: {4}. Last renamed death: {5}. Turn off with KillerNamesEnabled in TassHunting.json.",
                        (cfg != null && cfg.KillerNamesEnabled) ? "on" : "off",
                        namedCreatures, witnessedBlows, restoredDeaths, blows.Count,
                        lastRestoredLine.Length == 0 ? "none yet" : lastRestoredLine));
                });
        }

        // ---- better creature names (the "wild animal" repair) ----

        public static void NamePostfix(Entity __instance, string languageCode, ref string __result)
        {
            if (disarmedName || sapi == null) return;
            var cfg = HuntingModSystem.Cfg;
            if (cfg == null || !cfg.KillerNamesEnabled) return;
            try
            {
                if (__instance == null || __instance is EntityPlayer || __instance.Code == null) return;
                string lang = languageCode ?? Lang.CurrentLocale;
                // Only step in where vanilla failed: a creature the engine could name keeps
                // its vanilla words (wolves keep being "a wolf", flavor lines keep firing).
                if (__result != Lang.GetL(lang, "generic-wildanimal")) return;

                string name = CleanCreatureName(__instance.GetName());
                if (name == null) return; // no real display name either - "a wild animal" stands

                string common = MatchCommonName(cfg.KillerCommonNames, __instance.Code);
                __result = Sanitize(common != null ? name + " - " + common : Article(name));
                namedCreatures++;
            }
            catch (Exception e)
            {
                disarmedName = true;
                sapi.Logger.Error("[TassHunting] killer names: creature naming failed and disarmed itself (vanilla wording resumes): " + e);
            }
        }

        /// <summary>First matching config pattern wins (same convention as
        /// and CreatureMeleeDamageMul): put specific species lines above family lines.</summary>
        private static string MatchCommonName(Dictionary<string, string> map, AssetLocation code)
        {
            if (map == null || map.Count == 0) return null;
            string full = code.ToShortString(), path = code.Path;
            foreach (var kv in map)
            {
                if (string.IsNullOrWhiteSpace(kv.Value)) continue;
                if (WildcardUtil.Match(kv.Key, full) || WildcardUtil.Match(kv.Key, path)) return kv.Value.Trim();
            }
            return null;
        }

        /// <summary>The mouseover display name, stripped to the species: "Tyrannosaurus
        /// (Juvenile Male)" becomes "Tyrannosaurus". Null when the creature has no real
        /// display name either (an unresolved lang lookup returns its own key - a colon or
        /// the raw item-creature text gives that away), so the caller keeps "a wild animal"
        /// rather than printing a lang key at a grieving player.</summary>
        private static string CleanCreatureName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (raw.IndexOf(':') >= 0) return null;
            int paren = raw.IndexOf('(');
            if (paren > 0) raw = raw.Substring(0, paren);
            raw = raw.Trim();
            if (raw.Length == 0 || raw.IndexOf("item-creature", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            return raw;
        }

        private static string Article(string name)
        {
            return ("aeiou".IndexOf(char.ToLowerInvariant(name[0])) >= 0 ? "an " : "a ") + name;
        }

        /// <summary>One raw angle bracket in a chat line breaks that tab's later lines until
        /// relog (VTML), so nothing user-configured or mod-shipped reaches chat unstripped.</summary>
        private static string Sanitize(string s)
        {
            if (s == null) return "";
            if (s.IndexOf('<') < 0 && s.IndexOf('>') < 0) return s;
            return s.Replace("<", "").Replace(">", "");
        }

        // ---- the killing-blow witness (bleed-out deaths keep their killer) ----

        private static void OnNowPlaying(IServerPlayer plr)
        {
            var ent = plr?.Entity;
            var health = ent?.GetBehavior<EntityBehaviorHealth>();
            if (health == null) return; // behavior not ready; a later join re-fire retries
            health.onDamaged += (dmg, src) => WitnessDamage(ent, health, dmg, src);
        }

        /// <summary>Runs inside the health behavior's delegate chain, which has no exception
        /// guard of its own - so the whole body is fenced: losing a death's name is cosmetic,
        /// breaking the damage path is not. The damage itself is never touched.</summary>
        private static float WitnessDamage(EntityPlayer victim, EntityBehaviorHealth health, float dmg, DamageSource src)
        {
            try
            {
                var cfg = HuntingModSystem.Cfg;
                if (cfg != null && cfg.KillerNamesEnabled && cfg.KillerWitnessMemorySeconds > 0f
                    && dmg > 0f && src != null && src.Type != EnumDamageType.Heal
                    && victim.Alive && health.Health - dmg <= 0f)
                {
                    // Only a blow with someone behind it is worth remembering. Frost, hunger
                    // and drowning keep ticking on a player a down-mod holds at a sliver of
                    // health - recording those would let the weather steal the death from
                    // the animal that actually put them down.
                    Entity cause = src.GetCauseEntity();
                    if (cause != null && cause != victim)
                    {
                        string display = cause is EntityPlayer attacker
                            ? (victim.World?.PlayerByUid(attacker.PlayerUID)?.PlayerName ?? attacker.GetName())
                            : cause.GetPrefixAndCreatureName(); // runs through NamePostfix, so dinos come out right here too
                        string uid = victim.PlayerUID;
                        if (!string.IsNullOrWhiteSpace(display) && !string.IsNullOrEmpty(uid))
                        {
                            blows[uid] = new WitnessedBlow { Display = Sanitize(display), WhenUtc = DateTime.UtcNow };
                            witnessedBlows++;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                sapi?.Logger.Warning("[TassHunting] killer names: could not witness a blow: {0}", e.Message);
            }
            return dmg;
        }

        private static void Forget(string uid)
        {
            if (!string.IsNullOrEmpty(uid)) blows.Remove(uid);
        }

        /// <summary>Take this player's remembered blow and forget it - always, even when it
        /// has aged out, so one death consumes one record. Null when nothing usable remains.</summary>
        private static WitnessedBlow Consume(string uid)
        {
            if (string.IsNullOrEmpty(uid) || !blows.TryGetValue(uid, out var blow)) return null;
            blows.Remove(uid);
            float memory = HuntingModSystem.Cfg?.KillerWitnessMemorySeconds ?? 0f;
            if (memory <= 0f) return null;
            return (DateTime.UtcNow - blow.WhenUtc).TotalSeconds > memory ? null : blow;
        }

        /// <summary>Does the engine's damage source name anything a player would recognise?
        /// False means the game lost it: no source at all, or no attacker plus one of the
        /// placeholder kinds a down-mod hands over when it kills on its own schedule. Real
        /// causes - a fall, hunger, fire, an attacker of any kind - all pass and keep their
        /// vanilla wording and flavor lines.</summary>
        private static bool NamesSomething(DamageSource src)
        {
            if (src == null) return false;
            if (src.GetCauseEntity() != null) return true;
            switch (src.Source)
            {
                case EnumDamageSource.Suicide:
                case EnumDamageSource.Internal:
                case EnumDamageSource.Unknown:
                case EnumDamageSource.Revive:
                case EnumDamageSource.Entity: // "an entity did it" with no entity attached says nothing
                    return false;
                default:
                    return true;
            }
        }

        // ---- the death broadcast builder ----

        /// <summary>Prefix on ServerSystemEntitySimulation.GetDeathMessage. The client
        /// parameter is the engine-internal ConnectedClient, taken as object and read by
        /// reflection so an engine refactor breaks this patch loudly at install, not at a
        /// player's death.</summary>
        public static bool DeathMessagePrefix(object client, DamageSource src, ref string __result)
        {
            if (disarmedDeath || sapi == null) return true;
            var cfg = HuntingModSystem.Cfg;
            if (cfg == null || !cfg.KillerNamesEnabled) return true;
            try
            {
                IServerPlayer plr = ResolvePlayer(client);
                if (plr == null) return true;
                WitnessedBlow blow = Consume(plr.PlayerUID); // consumed used or not - one death, one record
                if (NamesSomething(src)) return true;        // the engine still knows; vanilla wording (with fixed names) stands
                if (blow == null) return true;               // nothing witnessed either; vanilla "Player X died."
                __result = Sanitize(Lang.Get("Player {0} got killed by {1}", plr.PlayerName, blow.Display));
                lastRestoredLine = __result;
                restoredDeaths++;
                return false;
            }
            catch (Exception e)
            {
                disarmedDeath = true;
                sapi.Logger.Error("[TassHunting] killer names: bleed-out death naming failed and disarmed itself (vanilla wording resumes): " + e);
                return true;
            }
        }

        private static IServerPlayer ResolvePlayer(object client)
        {
            if (client == null) return null;
            if (connectedClientPlayer == null)
            {
                var t = client.GetType();
                var field = AccessTools.Field(t, "Player");
                if (field != null) connectedClientPlayer = o => field.GetValue(o);
                else
                {
                    var prop = AccessTools.Property(t, "Player");
                    if (prop != null) connectedClientPlayer = o => prop.GetValue(o);
                    else connectedClientPlayer = o => null;
                }
            }
            return connectedClientPlayer(client) as IServerPlayer;
        }
    }
}
