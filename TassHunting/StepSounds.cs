using System;
using Vintagestory.API.Common;
using Vintagestory.API.Util;

namespace TassHunting
{
    /// <summary>
    /// STEP SOUND OVERRIDE (owner field report 2026-08-28: the dino packs' step files sound
    /// "like scratching sand... why not just thump thump"). The packs ship swishy scrape
    /// recordings for their footsteps; this rewrites heavy step entries to a better sound at
    /// load, keeping everything else the packs tuned - the keyframe timing, the per-entry
    /// volume (run louder than walk), the pitch wobble, and the carry range. Both the real
    /// animation steps and BigSteps' behind-you steps read this same metadata, so one rewrite
    /// fixes both.
    ///
    /// StepSoundOverride maps wildcard entity codes to a sound location. Guard rails so it
    /// can never touch what it should not:
    ///  - only entries whose sound path contains "step" (roars, wing flaps, eat sounds are
    ///    animationSounds too and stay theirs);
    ///  - only entries whose designed range reaches BigStepsMinRange - the same "heavy"
    ///    line the behind-you steps use, so vanilla wolves (range 12-22) keep their own
    ///    steps even under a catch-all "*" entry.
    /// Applied to the parsed client animation metadata on both sides; the server's copy is
    /// what joining clients receive, so the server config rules multiplayer.
    /// </summary>
    public static class StepSounds
    {
        public static void Apply(ICoreAPI api)
        {
            var cfg = HuntingModSystem.Cfg;
            var map = cfg?.StepSoundOverride;
            if (map == null || map.Count == 0) return;
            float minRange = Math.Max(1f, cfg.BigStepsMinRange);

            int types = 0, entries = 0;
            foreach (var et in api.World.EntityTypes)
            {
                if (et?.Code == null) continue;
                var anims = et.Client?.Animations;
                if (anims == null) continue;
                string full = et.Code.ToShortString(), path = et.Code.Path;
                string loc = null;
                foreach (var kv in map)
                {
                    if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(kv.Value)
                        && (WildcardUtil.Match(kv.Key, full) || WildcardUtil.Match(kv.Key, path)))
                    { loc = kv.Value; break; }
                }
                if (loc == null) continue;

                bool touched = false;
                foreach (var meta in anims)
                {
                    var snds = meta?.AnimationSounds;
                    if (snds == null) continue;
                    foreach (var snd in snds)
                    {
                        var sloc = snd?.Attributes.Location;
                        if (sloc?.Path == null) continue;
                        if (!sloc.Path.Contains("step")) continue;          // steps only
                        if (snd.Attributes.Range < minRange) continue;      // heavy steps only
                        snd.Attributes.Location = new AssetLocation(loc);
                        entries++; touched = true;
                    }
                }
                if (touched) types++;
            }
            if (types > 0)
                api.Logger.Event("[TassHunting] step sounds ({0}): {1} heavy-step entries re-pointed on {2} entity types.",
                    api.Side, entries, types);
        }
    }
}
