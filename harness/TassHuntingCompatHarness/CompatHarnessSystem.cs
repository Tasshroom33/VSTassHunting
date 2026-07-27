using System;
using System.Linq;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace TassHuntingCompatHarness
{
    /// <summary>
    /// Self-driving Butchering-compat test (NOT shipped; server-side companion, same pattern as the
    /// TassFactions harness). With TassHunting AND Butchering both loaded, when the first player joins
    /// it runs the real pipeline end to end on the live server:
    ///   spawn pig -> kill it AS that player -> assert the corpse is pristine (no dropsgenerated /
    ///   harvested flags: TassHunting's pre-roll must stand down for butcherable animals) -> invoke
    ///   Butchering's empty-hand pickup for real -> assert the carcass item exists, its AnimalDrops
    ///   JSON parses as BlockDropItemStack[], every entry passes the butcher table's shape contract
    ///   (resolvable Code, non-null Quantity, no ResolvedItemstack), and at least one non-skinning
    ///   entry would yield at the table.
    /// Results are plain PASS/FAIL log lines ending with a BUTCHERCOMPAT COMPLETE summary, grepped by
    /// Run-ButcherCompat.ps1.
    /// </summary>
    public class CompatHarnessSystem : ModSystem
    {
        // Mirrors BlockEntityButcherWorkstation.SkinningRackExclusives (Butchering 1.13.5): entries the
        // TABLE filters out. Used only to decide which entries count toward "table would yield".
        private static readonly string[] SkinningExclusives = { "hide-", "fat", "feather", "fleece-", "wool-", "hair-", "fur-", "pelt-", "ivory-" };

        private ICoreServerAPI _sapi = null!;
        private bool _ran;
        private int _total, _passed;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            _sapi = api;
            api.Event.PlayerNowPlaying += OnPlayerNowPlaying;
            api.Logger.Notification("[butchercompat] armed - waiting for a player to join.");
        }

        private void Check(string name, bool ok)
        {
            _total++;
            if (ok) _passed++;
            _sapi.Logger.Notification("[butchercompat] {0} {1}", ok ? "PASS" : "FAIL", name);
        }

        private void Done()
        {
            _sapi.Logger.Notification("[butchercompat] BUTCHERCOMPAT COMPLETE total={0} pass={1} fail={2}", _total, _passed, _total - _passed);
        }

        private void Crash(Exception e)
        {
            _sapi.Logger.Error("[butchercompat] EXCEPTION: {0}", e);
            Check("no-exception", false);
            Done();
        }

        private void OnPlayerNowPlaying(IServerPlayer player)
        {
            if (_ran) return;
            _ran = true;
            _sapi.Event.RegisterCallback(_ => Phase1Spawn(player), 3000);
        }

        private void Phase1Spawn(IServerPlayer player)
        {
            try
            {
                Check("butchering-loaded", _sapi.ModLoader.IsModEnabled("butchering"));
                Check("tasshunting-loaded", _sapi.ModLoader.IsModEnabled("tasshunting"));

                // Rule, not instance: entity codes change between game versions (pig-wild-male became
                // pig-eurasian-adult-male in 1.22), so scan the loaded types for any adult pig.
                EntityProperties? type = _sapi.World.EntityTypes
                    .FirstOrDefault(t => t?.Code?.Path != null
                        && t.Code.Path.StartsWith("pig-")
                        && t.Code.Path.Contains("-adult-"))
                    ?? _sapi.World.EntityTypes.FirstOrDefault(t => t?.Code?.Path != null && t.Code.Path.StartsWith("pig"));
                Check("pig-type-exists", type != null);
                if (type == null) { Done(); return; }
                _sapi.Logger.Notification("[butchercompat] using entity type {0}", type.Code);

                Entity pig = _sapi.World.ClassRegistry.CreateEntity(type);
                var p = player.Entity.Pos;
                pig.ServerPos.SetPos(p.X + 2, p.Y, p.Z);
                pig.Pos.SetFrom(pig.ServerPos);
                _sapi.World.SpawnEntity(pig);

                _sapi.Event.RegisterCallback(_ => Phase2Kill(player, pig), 1500);
            }
            catch (Exception e) { Crash(e); }
        }

        private void Phase2Kill(IServerPlayer player, Entity pig)
        {
            try
            {
                // Butchering attaches its behavior via entity patches; both must be present or the
                // whole scenario is moot.
                Check("pig-has-butcherable", pig.HasBehavior("butcherable"));
                Check("pig-has-harvestable", pig.GetBehavior("harvestable") != null);

                // A genuine player kill: the damage source resolves to the joined player's entity,
                // exactly what TassHunting's death-time pre-roll gate looks for.
                pig.Die(EnumDespawnReason.Death, new DamageSource
                {
                    Source = EnumDamageSource.Player,
                    SourceEntity = player.Entity,
                    Type = EnumDamageType.SlashingAttack
                });

                _sapi.Event.RegisterCallback(_ => Phase3Pickup(player, pig), 1200);
            }
            catch (Exception e) { Crash(e); }
        }

        private void Phase3Pickup(IServerPlayer player, Entity pig)
        {
            try
            {
                Check("pig-died", !pig.Alive);
                // THE FIX under test: butcherable corpses must stay pristine - no pre-roll, no flags.
                Check("no-dropsgenerated-flag", !pig.WatchedAttributes.GetBool("dropsgenerated", false));
                Check("no-harvested-flag", !pig.WatchedAttributes.GetBool("harvested", false));

                EntityBehavior? butcherable = pig.GetBehavior("butcherable");
                Check("butcherable-instance", butcherable != null);
                if (butcherable == null) { Done(); return; }

                // Butchering's real pickup path: empty right hand, Interact mode. This is the exact
                // method that serialized-crash bugs and the harvested-gate live in.
                EnumHandling handled = EnumHandling.PassThrough;
                butcherable.OnInteract(player.Entity, player.Entity.RightHandItemSlot, new Vec3d(0, 0, 0), EnumInteractMode.Interact, ref handled);

                _sapi.Event.RegisterCallback(_ => Phase4Verify(player, pig), 800);
            }
            catch (Exception e) { Crash(e); }
        }

        private void Phase4Verify(IServerPlayer player, Entity pig)
        {
            try
            {
                // General rule: find the carcass by its AnimalDrops attribute, not by item code.
                // Per-inventory try/catch: InventoryPlayerCreative's enumerator NREs for a survival
                // player, and any one broken inventory must not abort the search.
                ItemStack? carcass = null;
                foreach (var inv in player.InventoryManager.Inventories.Values)
                {
                    if (inv == null || carcass != null) continue;
                    try
                    {
                        foreach (var slot in inv)
                        {
                            if (slot?.Itemstack?.Attributes?.HasAttribute("AnimalDrops") == true)
                            {
                                carcass = slot.Itemstack;
                                break;
                            }
                        }
                    }
                    catch (NullReferenceException) { /* creative inventory outside creative mode */ }
                }
                Check("carcass-picked-up", carcass != null);
                Check("corpse-despawned", _sapi.World.GetEntityById(pig.EntityId) == null);
                if (carcass == null) { Done(); return; }

                string json = carcass.Attributes.GetString("AnimalDrops", "");
                Check("animaldrops-not-null-string", !string.IsNullOrEmpty(json) && json != "null");

                BlockDropItemStack[]? drops = null;
                bool parsed = false;
                try
                {
                    drops = JsonConvert.DeserializeObject<BlockDropItemStack[]>(json);
                    parsed = true;
                }
                catch (Exception e)
                {
                    _sapi.Logger.Error("[butchercompat] AnimalDrops deserialize threw: {0}", e.Message);
                }
                Check("animaldrops-parses-as-array", parsed && drops != null && drops.Length > 0);
                if (drops == null) { Done(); return; }

                // The butcher table's input contract (BlockEntityButcherWorkstation/Table, 1.13.5):
                // every entry needs a resolvable Code, a non-null Quantity, and no ResolvedItemstack,
                // or the table NREs / silently drops output.
                bool shapeOk = drops.Length > 0;
                bool tableWouldYield = false;
                foreach (var d in drops)
                {
                    if (d == null || d.Code == null || string.IsNullOrEmpty(d.Code.Path) || d.Quantity == null || d.ResolvedItemstack != null)
                    {
                        shapeOk = false;
                        _sapi.Logger.Error("[butchercompat] bad drop entry: {0}", d?.Code?.ToString() ?? "null");
                        continue;
                    }
                    bool resolves = d.Resolve(_sapi.World, "butchercompat ", carcass.Collectible.Code);
                    if (!resolves)
                    {
                        shapeOk = false;
                        _sapi.Logger.Error("[butchercompat] unresolvable drop code: {0}", d.Code);
                    }
                    bool skinningOnly = SkinningExclusives.Any(s => d.Code.Path.StartsWith(s));
                    if (!skinningOnly && resolves && d.Quantity.avg > 0) tableWouldYield = true;
                    d.ResolvedItemstack = null;
                }
                Check("animaldrops-shape-safe", shapeOk);
                Check("table-would-yield-output", tableWouldYield);

                Done();
            }
            catch (Exception e) { Crash(e); }
        }
    }
}
