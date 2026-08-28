using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace TassHunting
{
    /// <summary>
    /// STICKY PROJECTILES. Arrows and spears STICK to the creatures they hit:
    /// the projectile rides the animal at the spot and angle it hit, turns with
    /// the body, and drops recoverable when the animal dies or the stick times
    /// out. Spears can be grabbed back at vanilla touch range while riding.
    ///
    /// THE RIDING RENDERER (single-writer, root-caused 2026-07-19,
    /// decompile-proven):
    /// (a) client entity.Pos is a MAILBOX - SystemNetworkProcess
    ///     .HandleSinglePacket writes every position packet RAW into Pos at
    ///     15ms tick cadence; it only becomes the smooth render position after
    ///     that entity's OWN EntityBehaviorInterpolatePosition lerps it later
    ///     the same frame;
    /// (b) ClientEventManager.RegisterRenderer inserts new renderers BEFORE
    ///     existing entries of equal RenderOrder (all interpolators are 0.0) -
    ///     the LATER-spawned projectile renders FIRST, so reading target.Pos
    ///     from the projectile's own pass gets raw-server/stale values
    ///     alternately (the "two locations" flicker).
    /// Therefore: a PREFIX skips a riding projectile's own interpolation
    /// entirely, and a POSTFIX on the TARGET's interpolation pass re-anchors
    /// every rider to the position that pass wrote IN THE SAME CALL. Ordering
    /// cannot exist as a concept; the arrow glides exactly like the animal.
    ///
    /// Config gates (all runtime): StickyProjectilesEnabled (master: off =
    /// vanilla die/drop on hit), SpearTouchRetrieve, StickSeconds.
    /// </summary>
    internal class StickyProjectiles
    {
        internal static StickyProjectiles Instance;

        private ICoreServerAPI sapi;
        private long tickId;

        private class StuckArrow
        {
            public long ArrowId;
            public long TargetId;
            public double LocalX, LocalY, LocalZ; // surface anchor in the target's body frame
            public float YawOff;                  // projectile yaw relative to target body yaw
            public float Roll;                    // vanilla puts the vertical angle in ROLL
            public float SecondsLeft;
            public float SaveIn;                  // countdown to next sa_secs persist
            public float GraceSeconds;            // rehydrated riders wait this long for their target's chunk
        }

        private readonly Dictionary<long, StuckArrow> stuck = new Dictionary<long, StuckArrow>();

        internal static void StartServer(ICoreServerAPI api)
        {
            Instance = new StickyProjectiles { sapi = api };
            Instance.tickId = api.Event.RegisterGameTickListener(Instance.OnTick, 50);
            // RELOG FIX (field 2026-07-21: "that arrow is still stuck but never
            // drops and you lose the arrow forever"): the ride REGISTRY is
            // in-memory, but the ride PARAMETERS live in watched attributes and
            // persist with the save - so a reloaded arrow kept riding visually
            // while no server record existed to ever release or expire it.
            // Rehydrate the record from the attributes when the entity loads.
            api.Event.OnEntityLoaded += Instance.OnEntityLoaded;
        }

        internal static void StopServer()
        {
            try { if (Instance?.sapi != null && Instance.tickId != 0) Instance.sapi.Event.UnregisterGameTickListener(Instance.tickId); } catch { }
            try { if (Instance?.sapi != null) Instance.sapi.Event.OnEntityLoaded -= Instance.OnEntityLoaded; } catch { }
            Instance?.stuck.Clear();
            RidersByTarget.Clear();
            Instance = null;
        }

        /// <summary>Rebuild the server ride record for a projectile loaded from
        /// the save. Timer resumes from the persisted sa_secs (old saves from
        /// before this fix default to a fresh StickSeconds - they finally get a
        /// release path instead of riding forever). GraceSeconds covers the
        /// target's chunk loading a beat later; if the target never appears the
        /// release path drops the arrow recoverable.</summary>
        private void OnEntityLoaded(Entity entity)
        {
            try
            {
                var arrow = entity as EntityProjectileBase;
                if (arrow == null) return;
                long targetId = arrow.WatchedAttributes.GetLong("sa_target", 0L);
                if (targetId == 0L || stuck.ContainsKey(arrow.EntityId)) return;

                stuck[arrow.EntityId] = new StuckArrow
                {
                    ArrowId = arrow.EntityId,
                    TargetId = targetId,
                    LocalX = arrow.WatchedAttributes.GetFloat("sa_lx"),
                    LocalY = arrow.WatchedAttributes.GetFloat("sa_ly"),
                    LocalZ = arrow.WatchedAttributes.GetFloat("sa_lz"),
                    YawOff = arrow.WatchedAttributes.GetFloat("sa_yawoff"),
                    Roll = arrow.WatchedAttributes.GetFloat("sa_roll"),
                    SecondsLeft = arrow.WatchedAttributes.GetFloat("sa_secs", Math.Max(10f, HuntingModSystem.Cfg.StickSeconds)),
                    GraceSeconds = 5f
                };
                SyncBleedFor(targetId); // relog: restore this animal's arrow-bleed
            }
            catch { }
        }

        /// <summary>The interpolation hook is a manual patch (the behavior class
        /// sits in the global namespace, resolve by name). Called once per
        /// process from HuntingModSystem's guarded patch block.</summary>
        internal static void PatchInterpolationHook(ICoreAPI api, Harmony harmony)
        {
            // FULLY try/catch'd (compat sweep 2026-07-22): a Harmony REJECTION of
            // the patch (not just a missing method - e.g. another mod already
            // transpiled OnRenderFrame, or a signature/instance mismatch) throws
            // a HarmonyException; unguarded, it propagated out of mod load. Matches
            // the TryPatchTrueAim pattern: a failure disables the feature (riding
            // arrows fall back to raw packet positions) and logs, never crashes.
            try
            {
                var t = AccessTools.TypeByName("EntityBehaviorInterpolatePosition")
                     ?? AccessTools.TypeByName("Vintagestory.GameContent.EntityBehaviorInterpolatePosition");
                var mi = t?.GetMethod("OnRenderFrame");
                if (mi == null)
                {
                    api.Logger.Warning("[TassHunting] EntityBehaviorInterpolatePosition.OnRenderFrame not found - riding projectiles will use raw packet positions.");
                    return;
                }
                harmony.Patch(mi,
                    prefix: new HarmonyMethod(typeof(StickyProjectiles), nameof(RideRenderPrefix)),
                    postfix: new HarmonyMethod(typeof(StickyProjectiles), nameof(RideRenderPostfix)));
            }
            catch (Exception ex)
            {
                api.Logger.Warning("[TassHunting] riding-projectile interpolation patch failed ({0}) - riding arrows use raw packet positions.", ex.Message);
            }
        }

        // CLIENT-side rider registry: target entity id -> projectile ids riding
        // it. Touched only from the render pass (the interpolate behavior is
        // client-only; its constructor throws server-side, decompile-verified).
        private static readonly Dictionary<long, List<long>> RidersByTarget = new Dictionary<long, List<long>>();

        public static bool RideRenderPrefix(object __instance)
        {
            var arrow = (__instance as EntityBehavior)?.entity as EntityProjectileBase;
            if (arrow == null) return true;
            long tid = arrow.WatchedAttributes.GetLong("sa_target", 0L);
            if (tid == 0L) return true;

            List<long> riders;
            if (!RidersByTarget.TryGetValue(tid, out riders))
            {
                // Rare housekeeping: drop registry entries whose target entity is
                // gone (its render pass stopped running, so nobody else cleans up).
                if (RidersByTarget.Count > 32)
                {
                    var dead = new List<long>();
                    foreach (long id in RidersByTarget.Keys)
                        if (arrow.World.GetEntityById(id) == null) dead.Add(id);
                    foreach (long id in dead) RidersByTarget.Remove(id);
                }
                RidersByTarget[tid] = riders = new List<long>(2);
            }
            if (!riders.Contains(arrow.EntityId)) riders.Add(arrow.EntityId);
            return false; // while riding, the TARGET's render pass owns this Pos
        }

        public static void RideRenderPostfix(object __instance)
        {
            var target = (__instance as EntityBehavior)?.entity;
            if (target == null) return;
            List<long> riders;
            if (!RidersByTarget.TryGetValue(target.EntityId, out riders)) return;

            for (int i = riders.Count - 1; i >= 0; i--)
            {
                var arrow = target.World.GetEntityById(riders[i]);
                if (arrow == null || arrow.WatchedAttributes.GetLong("sa_target", 0L) != target.EntityId)
                {
                    riders.RemoveAt(i); // released or despawned
                    continue;
                }
                // target.Pos was written by the interpolation lerp in THIS call.
                float t = target.Pos.Yaw;
                float lx = arrow.WatchedAttributes.GetFloat("sa_lx");
                float ly = arrow.WatchedAttributes.GetFloat("sa_ly");
                float lz = arrow.WatchedAttributes.GetFloat("sa_lz");
                double cos = Math.Cos(t), sin = Math.Sin(t);
                double wx = lx * cos + lz * sin;
                double wz = -lx * sin + lz * cos;
                arrow.Pos.SetPos(target.Pos.X + wx, target.Pos.Y + ly, target.Pos.Z + wz);
                arrow.Pos.Yaw = t + arrow.WatchedAttributes.GetFloat("sa_yawoff");
                arrow.Pos.Pitch = 0f;
                arrow.Pos.Roll = arrow.WatchedAttributes.GetFloat("sa_roll");
            }
            if (riders.Count == 0) RidersByTarget.Remove(target.EntityId);
        }

        internal void Attach(EntityProjectileBase arrow, Entity target)
        {
            // Impact orientation from the FLIGHT motion, using VANILLA's exact
            // formula (SetRotationFromMotion, decompile-verified): yaw carries a
            // +PI flip, the VERTICAL angle lives in ROLL, pitch is always 0.
            var m = arrow.Pos.Motion;
            double n = m.Length();
            float impactYaw, impactRoll;
            if (n > 0.01)
            {
                impactYaw = (float)Math.PI + (float)Math.Atan2(m.X / n, m.Z / n);
                impactRoll = -(float)Math.Asin(GameMath.Clamp((0.0 - m.Y) / n, -1.0, 1.0));
            }
            else { impactYaw = arrow.Pos.Yaw; impactRoll = arrow.Pos.Roll; }

            // BODY-ELLIPSE ANCHOR (playtest 2026-07-19: arrows floated outside a
            // goat's flank). VS collision boxes are SQUARE in plan while the
            // visual body is a narrow ellipse - long along the spine, skinny
            // across - so a circle-radius anchor leaves side shots hanging in
            // air. Model the body as an ellipse in the target's local frame:
            // semi-axis a = box half (spine, local Z), b = a x BodyWidthFraction
            // (across, local X). ellipseR = distance center->surface along the
            // reverse-flight ray - this correctly scales the SURFACE POINT with
            // the body (side shot on a bear anchors farther out than on a hare).
            double wy = GameMath.Clamp(arrow.Pos.Y - target.Pos.Y,
                target.CollisionBox?.Y1 ?? 0f, target.CollisionBox?.Y2 ?? 1f);
            double widthFrac = GameMath.Clamp(HuntingModSystem.Cfg.StickBodyWidthFraction, 0.15f, 1f);
            double a = 0.3;
            if (target.CollisionBox != null)
                a = Math.Max(target.CollisionBox.XSize, target.CollisionBox.ZSize) * 0.5;
            double b = a * widthFrac;

            double hx = 0, hz = -1;
            double horiz = Math.Sqrt(m.X * m.X + m.Z * m.Z);
            if (horiz > 0.001) { hx = -m.X / horiz; hz = -m.Z / horiz; } // back toward the shooter

            float ty = target.Pos.Yaw;
            double tcos = Math.Cos(ty), tsin = Math.Sin(ty);
            // Reverse flight direction in the target's local frame (R(-t));
            // local +Z = spine/facing, local X = across the body.
            double ldx = hx * tcos - hz * tsin;
            double ldz = hx * tsin + hz * tcos;
            // Radius where a unit ray from center exits the body ellipse.
            double ellipseR = 1.0 / Math.Sqrt((ldx * ldx) / (b * b) + (ldz * ldz) / (a * a) + 1e-9);
            // ABSOLUTE EMBED (2026-07-23): the arrow bites a FIXED depth, not a
            // fraction of the body, so the bite is identical on a bear and a
            // pampas deer (a fixed-length arrow otherwise looked swallowed on
            // small game - the whole bug). r = surface - depth. On a body thinner
            // than the depth (hare/pampas flank), r goes negative: the anchor
            // sits AT/THROUGH center and the tip pokes out the far side - a
            // visible pass-through, which beats an arrow lost inside a thin box
            // (user preference). The cap stops the tip flying a full body-length
            // past center. Proven with geometry over every test box + baby sizes.
            double depth = Math.Max(0.0, HuntingModSystem.Cfg.StickEmbedDepth);
            double passCap = Math.Max(0f, HuntingModSystem.Cfg.StickPassThroughCap);
            double r = ellipseR - depth;
            double floor = -ellipseR * passCap; // most the tip may overshoot center
            if (r < floor) r = floor;
            if (HuntingModSystem.Cfg.BloodDiagnostics)
                target.World.Logger.Notification(
                    "[TassHunting] stick anchor: box a={0:0.000} b={1:0.000}, surfaceR={2:0.000}, depth={3:0.000} -> r={4:0.000} ({5})",
                    a, b, ellipseR, ellipseR - r, r, r < 0 ? "THROUGH - tip out far side" : "seated inside");

            var s = new StuckArrow
            {
                ArrowId = arrow.EntityId,
                TargetId = target.EntityId,
                LocalX = ldx * r,
                LocalY = wy,
                LocalZ = ldz * r,
                YawOff = impactYaw - ty,
                Roll = impactRoll,
                SecondsLeft = Math.Max(10f, HuntingModSystem.Cfg.StickSeconds)
            };
            stuck[arrow.EntityId] = s;
            SyncBleedFor(target.EntityId); // arrow embedded -> one more bleed stack

            arrow.WatchedAttributes.SetBool("stuck", true);
            // Ride parameters travel WITH the projectile (watched attributes sync
            // automatically): each client's render hook derives position locally.
            // Server tick stays authoritative for state, saves and modless clients.
            arrow.WatchedAttributes.SetLong("sa_target", target.EntityId);
            arrow.WatchedAttributes.SetFloat("sa_lx", (float)s.LocalX);
            arrow.WatchedAttributes.SetFloat("sa_ly", (float)s.LocalY);
            arrow.WatchedAttributes.SetFloat("sa_lz", (float)s.LocalZ);
            arrow.WatchedAttributes.SetFloat("sa_yawoff", s.YawOff);
            arrow.WatchedAttributes.SetFloat("sa_roll", s.Roll);
            arrow.WatchedAttributes.SetFloat("sa_secs", s.SecondsLeft); // relog resumes the timer
            ApplyRide(arrow, target, s);
        }

        private static void ApplyRide(Entity arrow, Entity target, StuckArrow s)
        {
            float t = target.Pos.Yaw;
            double cos = Math.Cos(t), sin = Math.Sin(t);
            // local -> world: R(t)
            double wx = s.LocalX * cos + s.LocalZ * sin;
            double wz = -s.LocalX * sin + s.LocalZ * cos;
            arrow.Pos.SetPos(target.Pos.X + wx, target.Pos.Y + s.LocalY, target.Pos.Z + wz);
            arrow.Pos.Yaw = t + s.YawOff;
            arrow.Pos.Pitch = 0f;
            arrow.Pos.Roll = s.Roll;
            arrow.Pos.Motion.Set(0.0, 0.0, 0.0);
            // The projectile's own tick recomputes+overwrites this flag from
            // collision state - reassert so the CLIENT keeps treating the
            // projectile as stuck (no rotation-from-motion, no gravity).
            //
            // READ BEFORE WRITE (2026-07-30). This runs 20x a second for every
            // embedded arrow, and SyncedTreeAttribute.SetBool marks the path dirty
            // and queues a sync EVERY time, even when the value did not change
            // (decompile-verified) - so a few arrows in a few animals were pushing
            // a steady stream of pointless attribute packets down the same
            // connection inventory updates use. Writing only on the actual flip
            // reaches the identical end state for a fraction of the traffic.
            if (!arrow.WatchedAttributes.GetBool("stuck", false))
                arrow.WatchedAttributes.SetBool("stuck", true);
        }

        private readonly List<long> toRemove = new List<long>();

        /// <summary>Recount which projectiles are stuck in a target and sync the bleed system's
        /// wound pins (2026-07-27 wound model: an embedded arrow keeps ITS wound open; releases
        /// let that wound start closing normally).</summary>
        private void SyncBleedFor(long targetId)
        {
            if (targetId == 0L) return;
            var ids = new System.Collections.Generic.HashSet<long>();
            foreach (var kv in stuck) if (kv.Value.TargetId == targetId) ids.Add(kv.Key);
            var target = sapi.World.GetEntityById(targetId);
            if (target != null) BleedSystem.SyncArrowPins(target, ids);
        }

        private void OnTick(float dt)
        {
            if (stuck.Count == 0) return;
            toRemove.Clear();
            var affectedTargets = new HashSet<long>();

            foreach (var kv in stuck)
            {
                var s = kv.Value;
                var arrow = sapi.World.GetEntityById(s.ArrowId);
                if (arrow == null || !arrow.Alive) { toRemove.Add(kv.Key); affectedTargets.Add(s.TargetId); continue; }

                var target = sapi.World.GetEntityById(s.TargetId);

                // STAY UNTIL DEATH: while the animal is alive and loaded, the stick
                // timer is FROZEN - the arrow never works loose, it bleeds until
                // the kill (handled by the target-dead branch below). The timeout
                // still counts down when the target is MISSING (fled/unloaded), so
                // StickSeconds is the fallback cap that stops lost arrows leaking.
                bool freezeTimer = HuntingModSystem.Cfg.StickUntilDeath
                    && target != null && target.Alive
                    // Players are never pinned forever: a downed-state mod keeps
                    // a "dead" player Alive, and a revived player must not walk
                    // away permanently wearing an arrow - the normal timer runs.
                    && !(target is EntityPlayer);
                if (!freezeTimer) s.SecondsLeft -= dt;

                if (s.SecondsLeft <= 0f)
                {
                    // WORKS LOOSE (field 2026-07-22: timed-out arrows vanished
                    // instead of dropping). Same RELEASE as the death path -
                    // detach, resume gravity, fall to the ground RECOVERABLE.
                    // (The old Die(Expire) despawned it, giving nothing to pick
                    // up - only animal death dropped arrows.)
                    arrow.WatchedAttributes.SetBool("stuck", false);
                    arrow.WatchedAttributes.RemoveAttribute("sa_target");
                    arrow.Pos.Motion.Set(0.0, -0.02, 0.0);
                    toRemove.Add(kv.Key);
                    affectedTargets.Add(s.TargetId);
                    continue;
                }

                // persist the timer so a relog resumes instead of resetting
                s.SaveIn -= dt;
                if (s.SaveIn <= 0f)
                {
                    s.SaveIn = 5f;
                    arrow.WatchedAttributes.SetFloat("sa_secs", s.SecondsLeft);
                }

                if (target == null && s.GraceSeconds > 0f)
                {
                    s.GraceSeconds -= dt; // rehydrated: target's chunk may load a beat later
                    continue;
                }
                if (target == null || !target.Alive)
                {
                    // Release: clear the stuck flag so gravity resumes - the
                    // projectile falls with the kill and lands recoverable.
                    arrow.WatchedAttributes.SetBool("stuck", false);
                    arrow.WatchedAttributes.RemoveAttribute("sa_target"); // client stops driving it
                    arrow.Pos.Motion.Set(0.0, -0.02, 0.0);
                    toRemove.Add(kv.Key);
                    affectedTargets.Add(s.TargetId);
                    continue;
                }

                ApplyRide(arrow, target, s);
            }

            foreach (long id in toRemove) { stuck.Remove(id); }
            // arrows left some animals -> recount their stacks (0 = bleed ends)
            foreach (long tid in affectedTargets) SyncBleedFor(tid);
        }
    }

    /// <summary>
    /// Vanilla touch-collect (1.5 block radius) checks only "settled + alive +
    /// zero motion" - a RIDING arrow passes and gets yanked out of the animal
    /// when you walk near it (playtest 2026-07-18). Riding ARROWS are not
    /// collectible until released. SPEARS are the exception (playtest
    /// 2026-07-19: your melee/ranged all-in-one must be retrievable): a stuck
    /// spear can be grabbed back at vanilla touch range. The extended pickup
    /// vacuum skips riders via its own sa_target check, so spear retrieval
    /// stays a deliberate melee-range grab, never magnetic.
    /// </summary>
    [HarmonyPatch(typeof(EntityProjectileBase), "CanCollect")]
    public static class Patch_NoCollectWhileRiding
    {
        public static void Postfix(EntityProjectileBase __instance, Entity byEntity, ref bool __result)
        {
            if (!__result) return;
            long ridingTargetId = __instance.WatchedAttributes.GetLong("sa_target", 0L);
            if (ridingTargetId == 0L) return;
            if (HuntingModSystem.Cfg.SpearTouchRetrieve
                && __instance.Code?.Path?.Contains("spear") == true) return; // grab your spear back
            // ARROW IN A PLAYER (user request 2026-07-28): a body is not a
            // pincushion. The stuck player may always pull an arrow out of
            // themselves, and the SHOOTER may reclaim their arrow from a player
            // at touch range. Everyone else keeps the no-yank rule, and arrows
            // in ANIMALS stay untouchable until released (mid-hunt walk-past
            // must never strip a bleeding animal's arrows).
            if (HuntingModSystem.Cfg.PlayerArrowTouchRetrieve && byEntity is EntityPlayer asker)
            {
                if (asker.EntityId == ridingTargetId) return; // out of my own body, always
                var ridingTarget = __instance.World.GetEntityById(ridingTargetId);
                if (ridingTarget is EntityPlayer
                    && !string.IsNullOrEmpty(asker.PlayerUID)
                    && asker.PlayerUID == __instance.WatchedAttributes.GetString("tassOwner", null))
                    return; // the shooter reclaims from a player
            }
            __result = false;
        }
    }

    /// <summary>
    /// While a projectile rides a target, its own OnGameTick must not run: it
    /// recomputes Stuck from collision state (air = false) and overwrites the
    /// synced flag every 25ms, after which the CLIENT re-derives rotation from
    /// zero motion (the old "flat arrow facing me" bug). Keyed on the SYNCED
    /// sa_target attribute so dedicated-server remote clients skip it too.
    /// </summary>
    [HarmonyPatch(typeof(EntityProjectile), "OnGameTick")]
    public static class Patch_SkipTickWhileRiding
    {
        public static bool Prefix(EntityProjectile __instance)
        {
            return __instance.WatchedAttributes.GetLong("sa_target", 0L) == 0L;
        }
    }

    /// <summary>
    /// DamageProjectile decides the projectile's FATE after an entity hit
    /// (durability hit, then die-or-drop - decompile-verified). We keep the
    /// durability hit, then ATTACH instead of dying. Damage was already dealt
    /// by DealDamage before this runs, so gameplay damage is untouched.
    /// </summary>
    [HarmonyPatch(typeof(EntityProjectileBase), "DamageProjectile")]
    public static class Patch_ArrowSticksInsteadOfVanishing
    {
        public static bool Prefix(EntityProjectileBase __instance, Entity target)
        {
            if (!HuntingModSystem.Cfg.StickyProjectilesEnabled) return true; // vanilla fate
            if (__instance.World.Side != EnumAppSide.Server) return true;
            if (target == null) return true;
            // NEVER stick to the SHOOTER (field 2026-07-22): VS registers an
            // early-flight collision against the firer - the "double hit" quirk
            // - and without this guard shooting the ground stuck the arrow to
            // the player. Fall through to vanilla die/drop for a self-hit.
            // FiredBy can be null server-side; the synced "firedBy" attribute is
            // the engine's own fallback (EntityProjectileBase.FromBytes).
            long shooterId = __instance.FiredBy?.EntityId ?? __instance.WatchedAttributes.GetLong("firedBy", 0L);
            if (shooterId != 0L && target.EntityId == shooterId) return true;
            // Arrows AND spears stick; thrown stones keep vanilla behavior.
            if (__instance.Code == null) return true;
            string path = __instance.Code.Path;
            bool isArrow = path.Contains("arrow");
            if (!isArrow && !path.Contains("spear")) return true;

            // HIDE GLANCE (owner design 2026-08-28): the bleed gate's roll for this same hit
            // (one roll per projectile, whoever asks first - see HideGlance). Deflected =
            // no durability hit, no break roll, no arrowhead, no stick: the projectile
            // survives intact and ImpactOnEntity (our caller) zeroes its motion right after
            // this returns, so it drops recoverable at the animal's feet.
            // (Fallback asker: for a projectile the bleed gate normally rolls first with the
            // true final damage; this chance only decides when that gate never saw the hit.)
            float glance = HideGlance.ChanceFor(target, __instance, HuntingModSystem.Cfg, __instance.Damage);
            if (glance > 0f && HideGlance.RollOnce(__instance.EntityId, glance, __instance.World))
            {
                if (HuntingModSystem.Cfg.BloodDiagnostics)
                    __instance.World.Logger.Notification(
                        "[TassHunting] {0} glanced off {1} (chance {2:0.00}) - drops recoverable, no stick",
                        path, target.Code?.ToShortString(), glance);
                return false; // skip vanilla die/drop AND our stick - the arrow just falls
            }

            // Durability hit (unchanged - vanilla dealt gameplay damage already).
            bool brokeByDurability = false;
            if (__instance.DamageStackOnImpact && __instance.ProjectileStack != null)
            {
                __instance.ProjectileStack.Collectible.DamageItem(
                    target.World, target, new DummySlot(__instance.ProjectileStack));
                brokeByDurability = __instance.ProjectileStack.Collectible.GetRemainingDurability(__instance.ProjectileStack) <= 0;
            }

            // BREAK ROLL FIRST (fixed 2026-07-22: the old code only broke on 0
            // durability and IGNORED the break-chance roll - so a high-break-chance
            // arrow that hit an animal never broke, it just STUCK, then timed out
            // and vanished with no arrowhead. That was the "100% copper break
            // dropped nothing, swallowed by the wolf" bug). Vanilla's break test
            // (EntityProjectileBase.DamageProjectile, decompile-verified) is:
            //   dies if durability<=0 OR Rand >= DropOnImpactChance.
            // DropOnImpactChance = 1 - breakChanceOnImpact (ItemBow.cs:249), and
            // our per-material ArrowBreakChance config feeds that breakChance. So
            // an arrow that BREAKS is destroyed (Die -> Patch_ArrowheadOnBreak
            // drops game:arrowhead-{material} at the impact point), does NOT stick,
            // and adds NO bleed stack - it "glanced off bone" (user 2026-07-22).
            // Spears never break-roll (no arrowhead item); they always stick.
            if (isArrow)
            {
                bool broke = brokeByDurability
                    || __instance.World.Rand.NextDouble() >= __instance.DropOnImpactChance;
                if (HuntingModSystem.Cfg.BloodDiagnostics)
                    __instance.World.Logger.Notification(
                        "[TassHunting] arrow hit: {0}, DropOnImpactChance={1:0.00} (breakChance={2:0.00}) -> {3}",
                        path, __instance.DropOnImpactChance, 1f - __instance.DropOnImpactChance,
                        broke ? "BREAK (drop head, no stick)" : "STICK (+1 bleed)");
                if (broke)
                {
                    __instance.Die(EnumDespawnReason.Death); // -> arrowhead drop; no stick, no bleed
                    return false;
                }
            }
            else if (brokeByDurability)
            {
                // A spear whose durability ran out just breaks (no arrowhead, no
                // stick) - do not embed a snapped weapon in the animal.
                __instance.Die(EnumDespawnReason.Death);
                return false;
            }

            // Survived the break roll: it rides the target and contributes a bleed
            // stack (bleed = count of intact stuck arrows).
            StickyProjectiles.Instance?.Attach(__instance, target);
            return false; // skip vanilla die/drop - the projectile now rides the target
        }
    }
}
