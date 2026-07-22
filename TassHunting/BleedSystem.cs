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
            public List<long> Expiries = new List<long>();
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

        /// <summary>Called from the health-damage postfix: roll and add a stack.</summary>
        public static void TryProc(Entity victim, DamageSource src, float damage)
        {
            var cfg = HuntingModSystem.Cfg;
            if (cfg == null || !cfg.BleedEnabled || victim == null) return;
            if (victim.World?.Side != EnumAppSide.Server || !victim.Alive) return;
            if (damage < cfg.BleedDamageThreshold) return;
            if (src == null || (src.Type != EnumDamageType.PiercingAttack && src.Type != EnumDamageType.SlashingAttack)) return;
            if (cfg.BleedPlayerCausedOnly && !(src.GetCauseEntity() is EntityPlayer)) return;
            if (!cfg.BleedAffectsPlayers && victim is EntityPlayer) return;
            if (victim.GetBehavior<EntityBehaviorHealth>() == null) return;
            if (victim.World.Rand.Next(0, 100) >= cfg.BleedChancePct) return;

            long now = victim.World.ElapsedMilliseconds;
            long expiry = now + (long)(cfg.BleedDurationSeconds * 1000f);
            lock (Active)
            {
                if (!Active.TryGetValue(victim.EntityId, out var st))
                {
                    st = new State { Ent = victim, NextTickMs = now + (long)(cfg.BleedTickSeconds * 1000f) };
                    Active[victim.EntityId] = st;
                }
                int cap = Math.Max(1, cfg.BleedMaxStacks);
                if (st.Expiries.Count >= cap)
                {
                    int shortest = 0;
                    for (int i = 1; i < st.Expiries.Count; i++)
                        if (st.Expiries[i] < st.Expiries[shortest]) shortest = i;
                    st.Expiries[shortest] = expiry; // at cap: refresh, don't grow
                }
                else st.Expiries.Add(expiry);
                // 0.9.0 client-local visuals: the stack count syncs as a plain
                // watched attribute; the client lays the drip trail from it.
                victim.WatchedAttributes.SetInt("thbleed", st.Expiries.Count);
            }
        }

        /// <summary>Current bleeders (entity + stack count) for the blood-visual
        /// deposit tick. Copies under the lock; caller iterates freely.</summary>
        public static List<(Entity ent, int stacks)> SnapshotActive()
        {
            lock (Active)
            {
                var list = new List<(Entity, int)>(Active.Count);
                foreach (var kv in Active)
                    if (kv.Value.Ent != null && kv.Value.Expiries.Count > 0)
                        list.Add((kv.Value.Ent, kv.Value.Expiries.Count));
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
                    int before = st.Expiries.Count;
                    st.Expiries.RemoveAll(e => e <= now);
                    if (st.Expiries.Count == 0 || st.Ent == null || !st.Ent.Alive)
                    {
                        try { st.Ent?.WatchedAttributes.SetInt("thbleed", 0); } catch { }
                        (retire = retire ?? new List<long>()).Add(kv.Key); continue;
                    }
                    if (st.Expiries.Count != before)
                        st.Ent.WatchedAttributes.SetInt("thbleed", st.Expiries.Count);
                    if (now < st.NextTickMs) continue;
                    st.NextTickMs = now + (long)(cfg.BleedTickSeconds * 1000f);

                    var hb = st.Ent.GetBehavior<EntityBehaviorHealth>();
                    if (hb == null) { (retire = retire ?? new List<long>()).Add(kv.Key); continue; }
                    float perStack = cfg.BleedStaticPerTick + cfg.BleedPctMaxHealthPerTick / 100f * hb.MaxHealth;
                    float total = perStack * st.Expiries.Count;
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

        /// <summary>Active stack count (narrator/debug use).</summary>
        public static int StacksOn(long entityId)
        {
            lock (Active) return Active.TryGetValue(entityId, out var st) ? st.Expiries.Count : 0;
        }
    }

    [HarmonyPatch(typeof(EntityBehaviorHealth), "OnEntityReceiveDamage")]
    public static class Patch_BleedProc
    {
        [HarmonyPostfix]
        private static void Postfix(EntityBehaviorHealth __instance, DamageSource damageSource, ref float damage)
        {
            // 0.9.0: visuals are fully client-local (they key off the engine's
            // synced onHurt attributes) - this hook only feeds the DoT.
            try { BleedSystem.TryProc(__instance.entity, damageSource, damage); }
            catch { }
        }
    }
}
