using System;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace TassHuntingCompatHarness
{
    /// <summary>
    /// BORING LOOT COMPAT (2026-07-30, from Allstar13521's mod list). Boring Loot is content-only -
    /// no DLL, no patches - but it REPLACES the drops on rust creatures, and on drifters it does so
    /// by rewriting the HARVESTABLE behaviour's dropsByType. That is the exact array TassHunting's
    /// death-time pre-roll rolls through GenerateDrops and then strips resolved stacks from, which
    /// is the same surface that broke Butchering's carcass pickup in 0.12.4.
    ///
    /// What has to hold with both mods loaded, on a real player kill of a drifter:
    ///   - the harvestable drops really are Boring Loot's (proves its hard-coded behaviour INDEX
    ///     still lands on "harvestable" - if a future mod shifts that index its patch silently
    ///     writes to the wrong behaviour and drifters quietly keep vanilla loot)
    ///   - the pre-roll produces its gear fragment rather than coming up empty
    ///   - a corpse holding loot is NOT flagged harvested and NOT fast-decayed, so the knife
    ///     prompt survives and the player can still take it
    ///   - no ResolvedItemstack is left on the jsonDrops array afterwards (the Butchering hazard)
    ///   - finishing the harvest spills those fragments on the ground
    ///
    /// Results: PASS/FAIL lines ending in "BORINGLOOT COMPLETE total= pass= fail=".
    /// </summary>
    public class BoringLootHarnessSystem : ModSystem
    {
        // Plain reflection rather than Harmony: this harness project does not reference it, and a
        // single protected field does not justify pulling it in.
        private static readonly FieldInfo JsonDropsField =
            typeof(EntityBehaviorHarvestable).GetField("jsonDrops", BindingFlags.Instance | BindingFlags.NonPublic);

        private static BlockDropItemStack[] JsonDropsRef(EntityBehaviorHarvestable bh) =>
            JsonDropsField?.GetValue(bh) as BlockDropItemStack[];

        private ICoreServerAPI _sapi = null!;
        private bool _ran;
        private int _total, _passed;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            if (Environment.GetEnvironmentVariable("TASSHUNTING_BORINGLOOTTEST") != "1") return;
            _sapi = api;
            api.Event.PlayerNowPlaying += OnPlayerNowPlaying;
            api.Logger.Notification("[boringloot] armed - waiting for a player to join.");
        }

        private void Check(string name, bool ok)
        {
            _total++;
            if (ok) _passed++;
            _sapi.Logger.Notification("[boringloot] {0} {1}", ok ? "PASS" : "FAIL", name);
        }

        private void Note(string msg) => _sapi.Logger.Notification("[boringloot] {0}", msg);

        private void Done() =>
            _sapi.Logger.Notification("[boringloot] BORINGLOOT COMPLETE total={0} pass={1} fail={2}", _total, _passed, _total - _passed);

        private void Crash(Exception e)
        {
            _sapi.Logger.Error("[boringloot] EXCEPTION: {0}", e);
            Check("no-exception", false);
            Done();
        }

        private void OnPlayerNowPlaying(IServerPlayer player)
        {
            if (_ran) return;
            _ran = true;
            _sapi.Event.RegisterCallback(_ => Phase1Spawn(player), 4000);
        }

        private Entity _drifter = null!;

        private void Phase1Spawn(IServerPlayer player)
        {
            try
            {
                Check("boringloot-loaded", _sapi.ModLoader.IsModEnabled("boringloot"));
                Check("tasshunting-loaded", _sapi.ModLoader.IsModEnabled("tasshunting"));

                // Rule not instance: take whatever normal drifter variant this build ships.
                var type = _sapi.World.EntityTypes.FirstOrDefault(t => t?.Code?.Path == "drifter-normal")
                        ?? _sapi.World.EntityTypes.FirstOrDefault(t => t?.Code?.Path?.StartsWith("drifter-") == true);
                Check("drifter-type-exists", type != null);
                if (type == null) { Done(); return; }
                Note($"using entity type {type.Code}");

                _drifter = _sapi.World.ClassRegistry.CreateEntity(type);
                var p = player.Entity.Pos;
                _drifter.ServerPos.SetPos(p.X + 2, p.Y, p.Z);
                _drifter.Pos.SetFrom(_drifter.ServerPos);
                _sapi.World.SpawnEntity(_drifter);

                _sapi.Event.RegisterCallback(_ => Phase2Inspect(player), 1500);
            }
            catch (Exception e) { Crash(e); }
        }

        /// <summary>Before killing anything: is Boring Loot's patch actually on the harvestable
        /// behaviour? Its path hard-codes behaviour index 9, so this is the leg that catches a
        /// silent miss.</summary>
        private void Phase2Inspect(IServerPlayer player)
        {
            try
            {
                var bh = _drifter.GetBehavior<EntityBehaviorHarvestable>();
                Check("drifter-has-harvestable", bh != null);
                if (bh == null) { Done(); return; }

                var drops = JsonDropsRef(bh);
                Check("harvestable-has-drops", drops != null && drops.Length > 0);
                if (drops == null || drops.Length == 0) { Done(); return; }

                bool anyBoringLoot = false;
                foreach (var d in drops)
                {
                    string code = d?.Code?.ToString() ?? "?";
                    Note($"  harvestable drop: {code}");
                    if (code.StartsWith("boringloot:", StringComparison.OrdinalIgnoreCase)) anyBoringLoot = true;
                }
                // If this fails with Boring Loot installed, its behaviour-index patch landed
                // somewhere else and drifters are quietly still on vanilla loot.
                Check("boringloot-owns-the-harvestable-drops", anyBoringLoot);

                Phase3Kill(player);
            }
            catch (Exception e) { Crash(e); }
        }

        private void Phase3Kill(IServerPlayer player)
        {
            try
            {
                _drifter.Die(EnumDespawnReason.Death, new DamageSource
                {
                    Source = EnumDamageSource.Player,
                    SourceEntity = player.Entity,
                    Type = EnumDamageType.SlashingAttack
                });
                _sapi.Event.RegisterCallback(_ => Phase4AfterDeath(player), 1500);
            }
            catch (Exception e) { Crash(e); }
        }

        private void Phase4AfterDeath(IServerPlayer player)
        {
            try
            {
                Check("drifter-died", !_drifter.Alive);

                var bh = _drifter.GetBehavior<EntityBehaviorHarvestable>();
                if (bh == null) { Check("harvestable-survives-death", false); Done(); return; }

                // TassHunting pre-rolls the harvest loot at death for player kills.
                Check("preroll-ran", _drifter.WatchedAttributes.GetBool("dropsgenerated", false));

                bool hasLoot = bh.Inventory != null && !bh.Inventory.Empty;
                int count = 0;
                if (bh.Inventory != null)
                    for (int i = 0; i < bh.Inventory.Count; i++)
                        if (!bh.Inventory[i].Empty)
                        {
                            count += bh.Inventory[i].StackSize;
                            Note($"  pre-rolled: {bh.Inventory[i].Itemstack?.Collectible?.Code} x{bh.Inventory[i].StackSize}");
                        }

                // Boring Loot ships avg 1 var 0, so a drifter can never roll empty. If this ever
                // fails, its drop rates went variable and our empty-corpse path starts firing on
                // drifters - which is the moment this compat needs looking at again.
                Check("preroll-produced-loot", hasLoot && count > 0);

                // A corpse holding loot must stay for the knife: not flagged harvested, not
                // fast-decayed by the empty-corpse timer.
                Check("loot-corpse-not-flagged-harvested", !_drifter.WatchedAttributes.GetBool("harvested", false));

                // The Butchering-class hazard: resolved stacks left on the shared jsonDrops array
                // make other mods' JSON serialisation of it throw.
                var drops = JsonDropsRef(bh);
                bool anyResolved = false;
                if (drops != null) foreach (var d in drops) if (d?.ResolvedItemstack != null) anyResolved = true;
                Check("jsondrops-left-unresolved", !anyResolved);

                // EmptyCorpseRemoveSeconds defaults to 10; wait past it and confirm the corpse with
                // loot in it is still standing.
                float wait = TassHunting.HuntingModSystem.Cfg.EmptyCorpseRemoveSeconds + 3f;
                Note($"waiting {wait:0}s to confirm a loot-bearing corpse is not fast-decayed");
                _sapi.Event.RegisterCallback(_ => Phase5Decay(player, bh), (int)(wait * 1000f));
            }
            catch (Exception e) { Crash(e); }
        }

        private void Phase5Decay(IServerPlayer player, EntityBehaviorHarvestable bh)
        {
            try
            {
                bool stillThere = _sapi.World.GetEntityById(_drifter.EntityId) != null;
                Check("loot-corpse-survives-empty-corpse-timer", stillThere);
                if (!stillThere) { Done(); return; }

                // Finish the harvest the way the knife does. TassHunting's auto-drop postfix should
                // spill the pre-rolled fragments on the ground and decay the corpse.
                int before = CountFragmentsNear();
                bh.SetHarvested(player);
                _sapi.Event.RegisterCallback(_ => Phase6Spill(before), 1500);
            }
            catch (Exception e) { Crash(e); }
        }

        private int CountFragmentsNear()
        {
            int n = 0;
            foreach (var e in _sapi.World.GetEntitiesAround(_drifter.Pos.XYZ, 6f, 6f, x => x is EntityItem))
            {
                var code = ((EntityItem)e).Itemstack?.Collectible?.Code?.ToString() ?? "";
                if (code.StartsWith("boringloot:", StringComparison.OrdinalIgnoreCase)) n += ((EntityItem)e).Itemstack!.StackSize;
            }
            return n;
        }

        private void Phase6Spill(int before)
        {
            try
            {
                int after = CountFragmentsNear();
                Note($"boringloot items on the ground: before harvest {before}, after {after}");
                Check("harvest-spills-boringloot-drops", after > before);
                Done();
            }
            catch (Exception e) { Crash(e); }
        }
    }
}
