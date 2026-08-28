using System;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
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
        /// <summary>
        /// SIZE-DEEPENED PITCH (owner 2026-08-28: "sounds higher pitch like horse gallop
        /// than a large thump from an 8t animal"). One shared thump sample cannot carry
        /// every body size at native pitch, so the pitch is scaled by body HEIGHT:
        /// mult = clamp(deepen / height^0.7, 0.4, 1). At deepen 1.2: a 4-block rex plays
        /// at ~0.45 (a boom), a 2-block anky ~0.74, a 7-block sauropod hits the 0.4 floor,
        /// and anything wolf-sized computes above 1 and clamps to unchanged. Baked into
        /// the entry's own pitch curve at rewrite time (a NEW NatFloat - the default curve
        /// is a SHARED static instance and mutating it would poison every sound in the
        /// game), so the animation player and the behind-you steps both deepen alike.
        /// </summary>
        public static float PitchMult(float bodyHeight, float deepen)
        {
            if (deepen <= 0f || bodyHeight <= 0f) return 1f;
            return GameMath.Clamp(deepen / (float)Math.Pow(bodyHeight, 0.7), 0.4f, 1f);
        }

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

                float pitchMult = PitchMult(et.CollisionBoxSize?.Y ?? 0f, cfg.StepSoundDeepen);
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
                        if (pitchMult < 0.999f)
                        {
                            var old = snd.Attributes.Pitch;
                            snd.Attributes.Pitch = NatFloat.create(EnumDistribution.UNIFORM,
                                (old?.avg ?? 1f) * pitchMult, old?.var ?? 0.02f);
                        }
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
