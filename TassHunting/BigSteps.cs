using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace TassHunting
{
    /// <summary>
    /// BIG STEPS (owner field report 2026-08-28: "I can only hear dino foot sounds when
    /// looking at them; as soon as I turn away they are silent").
    ///
    /// ENGINE FACT (decompile-verified 1.22.5, AnimationManager.OnClientFrame): the animator
    /// only advances frames while entity.IsRendered || IsShadowRendered. Creature footsteps
    /// are FRAME-TRIGGERED animationSounds, so an off-screen animal's walk cycle freezes and
    /// the frames that carry the steps never fire. Every creature has this; wolves' steps are
    /// range ~12-22 and nobody noticed, a rex's range-67 stomp cutting out when it leaves the
    /// camera is unmissable - and tactically deadly, since the thing you most need to hear is
    /// the one behind you.
    ///
    /// The fix synthesizes steps for UNRENDERED movers only (rendered entities keep their
    /// real animation-driven steps - the two can never double up), reusing everything from
    /// the creature's own metadata, no hardcoded species or sounds:
    ///  - WHICH gait: the active animation code is server-synced state, unaffected by the
    ///    render gate - so walk plays walk sounds and run plays run sounds;
    ///  - WHAT it sounds like: that animation's own AnimationSounds (location, pitch curve,
    ///    volume, range), alternating through the entries like alternating feet;
    ///  - HOW OFTEN: the entries' frame gaps at the pack's 30fps, over AnimationSpeed;
    ///  - WHO qualifies: any animation whose step range reaches BigStepsMinRange - "if its
    ///    steps were designed to carry, they carry behind your back too." Small creatures
    ///    never qualify and cost nothing.
    /// </summary>
    public class BigSteps : ModSystem
    {
        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

        private ICoreClientAPI capi;
        private long tickId;

        private class Walker
        {
            public long NextStepMs;
            public int SoundIdx;
            public double LastX, LastZ;
            public long LastSeenMs;
        }

        private readonly Dictionary<long, Walker> walkers = new Dictionary<long, Walker>();
        private long played; // diagnostics

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            tickId = api.Event.RegisterGameTickListener(Tick, 100);
        }

        public override void Dispose()
        {
            try { if (capi != null && tickId != 0) capi.Event.UnregisterGameTickListener(tickId); } catch { }
            walkers.Clear();
            base.Dispose();
        }

        private void Tick(float dt)
        {
            try
            {
                var cfg = HuntingModSystem.Cfg;
                if (cfg == null || !cfg.BigStepsBehindYou) return;
                var plr = capi.World.Player?.Entity;
                if (plr == null) return;
                long now = capi.World.ElapsedMilliseconds;
                float minRange = Math.Max(1f, cfg.BigStepsMinRange);

                foreach (var ent in capi.World.LoadedEntities.Values)
                {
                    if (!(ent is EntityAgent) || ent is EntityPlayer || !ent.Alive) continue;
                    // Rendered (or shadow-rendered) = the real animation is ticking and playing
                    // its own frame sounds; we only ever cover the engine's blind spot.
                    if (ent.IsRendered || ent.IsShadowRendered) continue;

                    var anims = ent.AnimManager?.ActiveAnimationsByAnimCode;
                    if (anims == null || anims.Count == 0) continue;

                    // The loudest qualifying active animation wins (a running rex has both
                    // walk-ish and run states in flux; the one with the carrying steps is
                    // the one you would hear).
                    AnimationMetaData best = null; float bestRange = 0f;
                    foreach (var kv in anims)
                    {
                        var meta = kv.Value;
                        var snds = meta?.AnimationSounds;
                        if (snds == null || snds.Length == 0) continue;
                        float r = 0f;
                        foreach (var s in snds) if (s?.Attributes.Location != null) r = Math.Max(r, s.Attributes.Range);
                        if (r >= minRange && r > bestRange) { bestRange = r; best = meta; }
                    }
                    if (best == null) continue;

                    double dx = ent.Pos.X - plr.Pos.X, dz = ent.Pos.Z - plr.Pos.Z;
                    if (dx * dx + dz * dz > bestRange * bestRange) continue;

                    if (!walkers.TryGetValue(ent.EntityId, out var w))
                    {
                        walkers[ent.EntityId] = w = new Walker
                        { NextStepMs = now, LastX = ent.Pos.X, LastZ = ent.Pos.Z, LastSeenMs = now };
                    }
                    w.LastSeenMs = now;

                    // Only actual movement steps: a standing animal whose walk anim lingers
                    // in the blend must not thump in place.
                    double mx = ent.Pos.X - w.LastX, mz = ent.Pos.Z - w.LastZ;
                    w.LastX = ent.Pos.X; w.LastZ = ent.Pos.Z;
                    if (mx * mx + mz * mz < 0.0004) continue; // < ~0.2 blocks/s at this cadence

                    if (now < w.NextStepMs) continue;

                    var sounds = best.AnimationSounds;
                    var snd = sounds[w.SoundIdx % sounds.Length];
                    w.SoundIdx++;
                    if (snd?.Attributes.Location == null) continue;

                    // Cadence from the pack's own keyframes: gap between successive step
                    // frames at the 30fps animation base, over the animation's speed.
                    float frameGap = sounds.Length > 1
                        ? Math.Abs(sounds[(w.SoundIdx) % sounds.Length].Frame - snd.Frame)
                        : 20f;
                    if (frameGap < 1f) frameGap = 20f;
                    float speed = best.AnimationSpeed <= 0f ? 1f : best.AnimationSpeed;
                    w.NextStepMs = now + (long)(frameGap / 30f / speed * 1000f);

                    capi.World.PlaySoundAt(snd.Attributes.Location,
                        ent.Pos.X, ent.Pos.InternalY, ent.Pos.Z, null,
                        snd.Attributes.Pitch?.nextFloat() ?? 1f,
                        snd.Attributes.Range,
                        snd.Attributes.Volume?.nextFloat() ?? 1f);
                    played++;
                    if (cfg.BloodDiagnostics && played % 20 == 1)
                        capi.Logger.Notification("[TassHunting] big steps: {0} offscreen steps played (latest {1}, anim {2}, range {3:0})",
                            played, ent.Code?.ToShortString(), best.Code, bestRange);
                }

                // prune walkers for entities gone or long rendered
                if (walkers.Count > 32)
                {
                    List<long> gone = null;
                    foreach (var kv in walkers)
                        if (now - kv.Value.LastSeenMs > 15000) (gone = gone ?? new List<long>()).Add(kv.Key);
                    if (gone != null) foreach (long id in gone) walkers.Remove(id);
                }
            }
            catch (Exception) { /* sound flavor must never hurt the client tick */ }
        }
    }
}
