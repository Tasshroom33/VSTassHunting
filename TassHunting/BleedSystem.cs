// STACKING HYBRID BLEED: the DAMAGE half of the bleed feature. The VISUAL
// half lives in BloodVisuals.cs (spot ledger + water diffusion). This mod
// renders its own blood, so no third-party blood mod is needed.
//
// Model per the user's spec: each qualifying hit (piercing/slashing, past a
// damage threshold, chance roll) adds a bleed STACK (cap configurable, default
// 3; at cap the shortest-remaining stack refreshes). Every tick each stack
// deals STATIC + PCT-OF-MAX-HEALTH damage - the hybrid covers small and large
// animals in one curve (hare isn't obliterated by flat damage, bear actually
// feels % damage).
//
// Time lane: bleeds tick in REAL seconds - combat pacing is player-experience
// pacing (law 7 carve-out, same call as THW's ColdResponseSeconds).

using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace TassHunting
{
    public class BleedSystem : ModSystem
    {
        private class State
        {
            public Entity Ent;
            public int Stacks;        // = number of arrows currently stuck in this animal
            public long NextTickMs;
        }

        private static readonly Dictionary<long, State> Active = new Dictionary<long, State>();
        private ICoreServerAPI sapi;

        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            api.Event.RegisterGameTickListener(Tick, 1000);
        }

        public override void Dispose()
        {
            lock (Active) Active.Clear();
            base.Dispose();
        }

        /// <summary>ARROW-DRIVEN BLEED (2026-07-22 model): bleed exists ONLY
        /// while arrows are stuck. StickyProjectiles calls this with the live
        /// count of arrows embedded in the animal; each embedded arrow is one
        /// sustained stack (no cap, no chance roll, no hit-type gate). When an
        /// arrow falls out / times out the count drops; at 0 the bleed ends.
        /// The stick timer (StickSeconds) IS the bleed timer.</summary>
        public static void SetArrowStacks(Entity victim, int arrowCount)
        {
            var cfg = HuntingModSystem.Cfg;
            if (cfg == null || !cfg.BleedEnabled || victim == null) return;
            if (victim.World?.Side != EnumAppSide.Server) return;
            if (!cfg.BleedAffectsPlayers && victim is EntityPlayer) return;
            if (victim.GetBehavior<EntityBehaviorHealth>() == null) return;

            long now = victim.World.ElapsedMilliseconds;
            lock (Active)
            {
                if (arrowCount <= 0)
                {
                    if (Active.Remove(victim.EntityId))
                        try { victim.WatchedAttributes.SetInt("thbleed", 0); } catch { }
                    return;
                }
                if (!Active.TryGetValue(victim.EntityId, out var st))
                {
                    st = new State { Ent = victim, NextTickMs = now + (long)(cfg.BleedTickSeconds * 1000f) };
                    Active[victim.EntityId] = st;
                }
                st.Stacks = arrowCount;
                victim.WatchedAttributes.SetInt("thbleed", arrowCount);
            }
        }

        /// <summary>Current bleeders (entity + stack count). Copies under the
        /// lock; caller iterates freely.</summary>
        public static List<(Entity ent, int stacks)> SnapshotActive()
        {
            lock (Active)
            {
                var list = new List<(Entity, int)>(Active.Count);
                foreach (var kv in Active)
                    if (kv.Value.Ent != null && kv.Value.Stacks > 0)
                        list.Add((kv.Value.Ent, kv.Value.Stacks));
                return list;
            }
        }

        private void Tick(float dt)
        {
            var cfg = HuntingModSystem.Cfg;
            if (cfg == null || !cfg.BleedEnabled) return;
            long now = sapi.World.ElapsedMilliseconds;
            List<long> retire = null;

            lock (Active)
            {
                foreach (var kv in Active)
                {
                    var st = kv.Value;
                    if (st.Stacks <= 0 || st.Ent == null || !st.Ent.Alive)
                    {
                        try { st.Ent?.WatchedAttributes.SetInt("thbleed", 0); } catch { }
                        (retire = retire ?? new List<long>()).Add(kv.Key); continue;
                    }
                    if (now < st.NextTickMs) continue;
                    st.NextTickMs = now + (long)(cfg.BleedTickSeconds * 1000f);

                    var hb = st.Ent.GetBehavior<EntityBehaviorHealth>();
                    if (hb == null) { (retire = retire ?? new List<long>()).Add(kv.Key); continue; }
                    float perStack = cfg.BleedStaticPerTick + cfg.BleedPctMaxHealthPerTick / 100f * hb.MaxHealth;
                    float total = perStack * st.Stacks;
                    // DEDICATED SPLATTER SIGNAL (0.9.3): the client keys DoT
                    // splatter off this monotonic counter, NOT the engine's
                    // onHurt bump. Decompile-verified (Entity.cs:935-953): the
                    // onHurt path is gated by a 500ms invuln window + a
                    // TicksPerDuration check, so an internal Injury tick landing
                    // near a hit is silently swallowed - the reported "DoT has
                    // no splatter" bug. This attribute is set UNCONDITIONALLY on
                    // the exact tick beat and auto-syncs to clients.
                    int tickN = st.Ent.WatchedAttributes.GetInt("thbleedtick", 0) + 1;
                    st.Ent.WatchedAttributes.SetInt("thbleedtick", tickN);
                    st.Ent.WatchedAttributes.SetFloat("thbleeddmg", total);
                    // Injury/Internal never re-procs TryProc (piercing/slashing gate).
                    st.Ent.ReceiveDamage(new DamageSource { Source = EnumDamageSource.Internal, Type = EnumDamageType.Injury }, total);
                }
                if (retire != null) foreach (long id in retire) Active.Remove(id);
            }
        }

        /// <summary>Active stack count = arrows stuck (narrator/debug + blood).</summary>
        public static int StacksOn(long entityId)
        {
            lock (Active) return Active.TryGetValue(entityId, out var st) ? st.Stacks : 0;
        }
    }
}
