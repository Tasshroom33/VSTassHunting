using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace TassHuntingCompatHarness
{
    /// <summary>
    /// END-TO-END TEST FOR THE ARROW VACUUM (2026-07-30, written for the "items disappearing"
    /// field report). Needs a REAL connected player, because the thing under test is
    /// EntityPlayer.TryGiveItemStack -> PlayerInventoryManager, which does not exist without one -
    /// hence the dedicated-server-plus-real-client runner, not a headless suite.
    ///
    /// THE BUG IT PINS. TryGiveItemstack returns true when ANY amount moved, and drains the stack
    /// it is handed as it consumes it. The old vacuum despawned the ground entity whenever that
    /// bool came back true, so a stack that only PARTLY fit had its remainder deleted. The engine
    /// despawns on the STACK being drained (StackSize &lt;= 0), never on the bool.
    ///
    /// Legs, each an inventory state the vacuum has to get right:
    ///   room for part of it  -> item survives on the ground holding the remainder  (THE BUG)
    ///   no room at all       -> item untouched, nothing moved
    ///   room for all of it   -> item despawns, player holds all of it
    ///   any successful grab  -> the engine's "onitemcollected" event fires, so other mods see it
    ///   a landed projectile  -> same contract through the projectile branch
    ///
    /// Results are PASS/FAIL lines ending in "PICKUPTEST COMPLETE total= pass= fail=".
    /// </summary>
    public class PickupHarnessSystem : ModSystem
    {
        private const string ArrowheadCode = "arrowhead-flint";
        private const int GroundCount = 8;   // what we drop
        private const int RoomFor = 3;       // how much space we leave

        /// <summary>
        /// How far from the player we drop. THIS NUMBER IS THE WHOLE TEST.
        ///
        /// Vanilla's own touch-collect (EntityBehaviorCollectEntities) sweeps 1.5 blocks and does
        /// the same job correctly, so anything dropped inside that radius gets picked up by the
        /// ENGINE and the harness learns nothing about our vacuum. The first version of this file
        /// dropped at 1.0 and passed 15/15 against a deliberately BROKEN build for exactly that
        /// reason - it was grading vanilla.
        ///
        /// So: outside 1.5, inside the vacuum's ProjectilePickupRadius (default 4). Three blocks
        /// sits clear of both edges.
        /// </summary>
        private const double DropDistance = 3.0;

        private ICoreServerAPI _sapi = null!;
        private bool _ran;
        private int _total, _passed;

        private Item _head = null!;
        private int _maxStack;
        private int _collectEvents;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            if (Environment.GetEnvironmentVariable("TASSHUNTING_PICKUPTEST") != "1") return;
            _sapi = api;
            api.Event.PlayerNowPlaying += OnPlayerNowPlaying;
            // The restored engine step: every successful vacuum grab must publish this, or mods
            // that track pickups stay blind to anything the vacuum takes.
            api.Event.RegisterEventBusListener(OnCollectedEvent, 0.5, "onitemcollected");
            api.Logger.Notification("[pickuptest] armed - waiting for a player to join.");
        }

        private void OnCollectedEvent(string eventName, ref EnumHandling handling, IAttribute data) => _collectEvents++;

        private void Check(string name, bool ok)
        {
            _total++;
            if (ok) _passed++;
            _sapi.Logger.Notification("[pickuptest] {0} {1}", ok ? "PASS" : "FAIL", name);
        }

        private void Note(string msg) => _sapi.Logger.Notification("[pickuptest] {0}", msg);

        private void Done() =>
            _sapi.Logger.Notification("[pickuptest] PICKUPTEST COMPLETE total={0} pass={1} fail={2}", _total, _passed, _total - _passed);

        private void Crash(Exception e)
        {
            _sapi.Logger.Error("[pickuptest] EXCEPTION: {0}", e);
            Check("no-exception", false);
            Done();
        }

        private void OnPlayerNowPlaying(IServerPlayer player)
        {
            if (_ran) return;
            _ran = true;
            _sapi.Event.RegisterCallback(_ => Setup(player), 4000);
        }

        // ---- inventory control ---------------------------------------------------------------

        /// <summary>Player inventories the vacuum's TryGiveItemStack can actually reach. Creative
        /// is excluded by the engine in survival, character slots cannot hold arrowheads - but we
        /// take whatever GetBestSuitedSlot would walk rather than guessing, so "no room" really
        /// means no room.</summary>
        private IEnumerable<IInventory> TargetInventories(IServerPlayer player)
        {
            foreach (var inv in player.InventoryManager.Inventories.Values)
            {
                // The collection can carry nulls (seen live 2026-07-30, second entry).
                if (inv == null || !(inv is InventoryBasePlayer)) continue;
                string id = inv.InventoryID ?? "";
                // creative: not reachable in survival. character: armour, cannot hold arrowheads.
                // ground + mouse: NOT storage - writing a stack into the ground inventory THROWS IT
                // INTO THE WORLD (that is what it is for), which the vacuum then collects, which
                // refills it... first run of this harness saw the player end up holding 136 heads
                // off 33 collect events because of exactly that loop. Leave both alone; the
                // no-room leg proves whether they can take anything.
                if (id.Contains("creative") || id.Contains("character")) continue;
                if (id.Contains("ground") || id.Contains("mouse")) continue;
                yield return inv;
            }
        }

        /// <summary>Name every player inventory and whether we fill it, so a surprise in the
        /// numbers points at the inventory that caused it instead of needing a second run.</summary>
        private void LogInventories(IServerPlayer player)
        {
            var used = new List<IInventory>();
            foreach (var t in TargetInventories(player)) used.Add(t);

            foreach (var inv in player.InventoryManager.Inventories.Values)
            {
                if (inv == null) { Note("  inventory <null entry> - left alone"); continue; }
                try
                {
                    bool fill = false;
                    foreach (var t in used) if (ReferenceEquals(t, inv)) { fill = true; break; }
                    Note($"  inventory {inv.InventoryID ?? "?"} ({inv.GetType().Name}, {inv.Count} slots)"
                       + $" - {(fill ? "FILLED by harness" : "left alone")}");
                }
                catch (Exception ex) { Note($"  inventory <unreadable: {ex.GetType().Name}> - left alone"); }
            }
        }

        /// <summary>Fill every reachable slot with arrowheads, then carve exactly `room` space out
        /// of one slot. Returns that slot, or null if the player has no usable slot at all.</summary>
        private ItemSlot FillLeavingRoom(IServerPlayer player, int room)
        {
            ItemSlot carved = null;
            foreach (var inv in TargetInventories(player))
            {
                for (int i = 0; i < inv.Count; i++)
                {
                    var slot = inv[i];
                    slot.Itemstack = new ItemStack(_head, _maxStack);
                    slot.MarkDirty();
                }
            }
            if (room > 0)
            {
                foreach (var inv in TargetInventories(player))
                {
                    if (inv.Count == 0) continue;
                    carved = inv[0];
                    carved.Itemstack = new ItemStack(_head, Math.Max(1, _maxStack - room));
                    carved.MarkDirty();
                    break;
                }
            }
            return carved;
        }

        private void ClearInventories(IServerPlayer player)
        {
            foreach (var inv in TargetInventories(player))
                for (int i = 0; i < inv.Count; i++)
                {
                    inv[i].Itemstack = null;
                    inv[i].MarkDirty();
                }
        }

        /// <summary>Total arrowheads the player is holding across every reachable inventory.</summary>
        private int CountHeld(IServerPlayer player)
        {
            int n = 0;
            foreach (var inv in TargetInventories(player))
                for (int i = 0; i < inv.Count; i++)
                    if (inv[i].Itemstack?.Collectible == _head) n += inv[i].StackSize;
            return n;
        }

        /// <summary>
        /// Spawn a stack next to the player and return THAT entity - identified by taking the id
        /// set before and after, not by "last arrowhead in the radius". The loose version cost a
        /// wrong verdict on 2026-07-30: the negative-control run reported the ground item as still
        /// alive when the buggy build had in fact despawned it, because the harness was watching
        /// a different entity.
        /// </summary>
        private EntityItem DropNearPlayer(IServerPlayer player, int count)
        {
            var pos = player.Entity.Pos.XYZ.Add(DropDistance, 0.2, 0.0);

            var before = new HashSet<long>();
            foreach (var e in ArrowheadItemsNear(pos)) before.Add(e.EntityId);

            _sapi.World.SpawnItemEntity(new ItemStack(_head, count), pos);

            foreach (var e in ArrowheadItemsNear(pos))
                if (!before.Contains(e.EntityId)) return e;
            return null;
        }

        private List<EntityItem> ArrowheadItemsNear(Vec3d pos)
        {
            var list = new List<EntityItem>();
            foreach (var e in _sapi.World.GetEntitiesAround(pos, 6f, 6f, x => x is EntityItem))
            {
                var ei = (EntityItem)e;
                if (ei.Itemstack?.Collectible == _head) list.Add(ei);
            }
            return list;
        }

        /// <summary>Everything the verdict rests on, in one line. Alive alone is not enough - a
        /// despawn shows up as ShouldDespawn and a DespawnReason well before the entity is gone
        /// from the world, and the surrounding item count catches a harness that is watching the
        /// wrong thing.</summary>
        private string GroundState(EntityItem ent, string label)
        {
            var near = ArrowheadItemsNear(_player.Entity.Pos.XYZ.Add(DropDistance, 0.2, 0.0));
            var sizes = new List<string>();
            foreach (var e in near) sizes.Add($"#{e.EntityId}:{e.Itemstack?.StackSize ?? 0}");
            return $"{label}: tracked #{ent.EntityId} alive={ent.Alive} shouldDespawn={ent.ShouldDespawn}"
                 + $" reason={(ent.DespawnReason?.Reason.ToString() ?? "none")} stack={ent.Itemstack?.StackSize ?? 0}"
                 + $" | arrowhead items nearby: {(sizes.Count == 0 ? "none" : string.Join(", ", sizes))}";
        }

        /// <summary>The entity is gone, by any of the three signals the engine uses.</summary>
        private static bool IsGone(EntityItem ent) =>
            !ent.Alive || ent.ShouldDespawn || ent.DespawnReason != null;

        // ---- the legs ------------------------------------------------------------------------

        private IServerPlayer _player = null!;
        private EntityItem _ground = null!;
        private ItemSlot _carved = null!;

        private void Setup(IServerPlayer player)
        {
            try
            {
                _player = player;
                _head = _sapi.World.GetItem(new AssetLocation("game", ArrowheadCode));
                Check("arrowhead-item-exists", _head != null);
                if (_head == null) { Done(); return; }
                _maxStack = Math.Max(2, _head.MaxStackSize);

                Check("vacuum-enabled", TassHunting.HuntingModSystem.Cfg.ProjectilePickupRadius > 0f);
                Note($"arrowhead max stack {_maxStack}, dropping {GroundCount}, leaving room for {RoomFor}");
                LogInventories(player);

                LegPartial();
            }
            catch (Exception e) { Crash(e); }
        }

        /// <summary>THE BUG. Room for 3 of 8. The 5 that do not fit must stay on the ground.</summary>
        private void LegPartial()
        {
            _carved = FillLeavingRoom(_player, RoomFor);
            Check("setup-carved-a-slot", _carved != null);
            if (_carved == null) { Done(); return; }

            _collectEvents = 0;
            _ground = DropNearPlayer(_player, GroundCount);
            Check("setup-ground-item-spawned", _ground != null && _ground.Itemstack?.StackSize == GroundCount);
            if (_ground == null) { Done(); return; }

            // vacuum ticks at 400ms; give it several passes
            _sapi.Event.RegisterCallback(_ => CheckPartial(), 2500);
        }

        private void CheckPartial()
        {
            try
            {
                Note(GroundState(_ground, "after partial pickup") + $", carved slot={_carved.StackSize}/{_maxStack}");
                int left = _ground.Itemstack?.StackSize ?? 0;

                // THE REGRESSION GUARD. Old code: entity despawned, the other 5 destroyed with it.
                Check("partial-leaves-remainder-on-ground", !IsGone(_ground) && left == GroundCount - RoomFor);
                Check("partial-fills-the-slot", _carved.StackSize == _maxStack);
                Check("partial-still-fires-collect-event", _collectEvents > 0);

                _ground.Die(EnumDespawnReason.Removed);
                LegNoRoom();
            }
            catch (Exception e) { Crash(e); }
        }

        /// <summary>No room at all: nothing moves, the item is left completely alone.</summary>
        private void LegNoRoom()
        {
            FillLeavingRoom(_player, 0);
            _collectEvents = 0;
            _ground = DropNearPlayer(_player, GroundCount);
            if (_ground == null) { Check("noroom-ground-item-spawned", false); Done(); return; }
            _sapi.Event.RegisterCallback(_ => CheckNoRoom(), 2500);
        }

        private void CheckNoRoom()
        {
            try
            {
                Note(GroundState(_ground, "after full-inventory pass") + $", events={_collectEvents}");
                int left = _ground.Itemstack?.StackSize ?? 0;
                Check("noroom-item-untouched", !IsGone(_ground) && left == GroundCount);
                Check("noroom-fires-no-collect-event", _collectEvents == 0);

                _ground.Die(EnumDespawnReason.Removed);
                LegAllFits();
            }
            catch (Exception e) { Crash(e); }
        }

        /// <summary>Room for everything: the normal case. Item despawns, player holds it all.</summary>
        private void LegAllFits()
        {
            ClearInventories(_player);
            _collectEvents = 0;
            _ground = DropNearPlayer(_player, GroundCount);
            if (_ground == null) { Check("allfits-ground-item-spawned", false); Done(); return; }
            _sapi.Event.RegisterCallback(_ => CheckAllFits(), 2500);
        }

        private void CheckAllFits()
        {
            try
            {
                Note(GroundState(_ground, "after clear-inventory pass") + $", events={_collectEvents}");
                int held = CountHeld(_player);
                Note($"  player holds {held}");
                Check("allfits-item-despawned", IsGone(_ground));
                Check("allfits-player-got-everything", held == GroundCount);
                Check("allfits-fires-collect-event", _collectEvents > 0);

                LegProjectile();
            }
            catch (Exception e) { Crash(e); }
        }

        /// <summary>The projectile branch of the same Collect(): a landed arrow gets vacuumed and
        /// the entity goes away.</summary>
        private void LegProjectile()
        {
            try
            {
                ClearInventories(_player);
                _collectEvents = 0;

                EntityProperties arrowType = null;
                foreach (var et in _sapi.World.EntityTypes)
                    if (et.Code?.Path?.StartsWith("arrow-") == true) { arrowType = et; break; }
                Check("arrow-entity-type-exists", arrowType != null);
                if (arrowType == null) { Done(); return; }

                var arrow = _sapi.World.ClassRegistry.CreateEntity(arrowType) as Vintagestory.GameContent.EntityProjectileBase;
                if (arrow == null) { Check("arrow-entity-created", false); Done(); return; }

                var item = _sapi.World.GetItem(new AssetLocation(arrowType.Code.Domain, arrowType.Code.Path));
                arrow.ProjectileStack = item != null ? new ItemStack(item, 1) : null;
                arrow.Collectible = true;              // base collectibility, as vanilla sets on landing
                // PickupOnlyOwnProjectiles is ON by default, and the vacuum reads the engine's
                // "firedBy" stamp to decide whose arrow this is. A synthetic arrow has no stamp,
                // so without this the vacuum correctly refuses it and the leg fails for a reason
                // that has nothing to do with what it is testing.
                arrow.WatchedAttributes.SetLong("firedBy", _player.Entity.EntityId);
                arrow.ServerPos.SetPos(_player.Entity.Pos.XYZ.Add(DropDistance, 0.2, 0.0));
                arrow.Pos.SetFrom(arrow.ServerPos);
                arrow.Pos.Motion.Set(0, 0, 0);         // settled: CanCollect requires near-zero motion
                _sapi.World.SpawnEntity(arrow);
                _projectile = arrow;

                // CanCollect also gates on the engine's post-launch collect delay, so wait it out.
                _sapi.Event.RegisterCallback(_ => CheckProjectile(), 3000);
            }
            catch (Exception e) { Crash(e); }
        }

        private Vintagestory.GameContent.EntityProjectileBase _projectile = null!;

        private void CheckProjectile()
        {
            try
            {
                bool gone = !_projectile.Alive || _projectile.ShouldDespawn;
                Note($"after projectile pass: arrow gone={gone}, events={_collectEvents}");
                Check("projectile-vacuumed", gone);
                Check("projectile-fires-collect-event", _collectEvents > 0);
                Done();
            }
            catch (Exception e) { Crash(e); }
        }
    }
}
