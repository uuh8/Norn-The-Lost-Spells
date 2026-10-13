using Game.Core;
using NUnit.Framework;

namespace Game.Run.Tests
{
    /// <summary>专用槽是资产位置：测试容量、迁移和携带判定，而非只检查UI格子数量。</summary>
    public sealed class SpellStorageTests : InventoryTestFixture
    {
        private static ItemLocation Tray(int slot, string actor = "player") =>
            new ItemLocation((ItemLocationKind)7, actor, 0, 0, 0, slot);

        [Test] public void TwelveIndependentSpellSlotsKeepIdentityAndChargesWithoutBackpack()
        {
            var store = Store(Define("spell", ItemKind.Spell));
            for (int i = 0; i < 12; i++)
            {
                Add(store, "s" + i, "spell", i + 1);
                Assert.That(store.TryMove("s" + i, Tray(i)).Success, Is.True);
                Assert.That(store.TryGet("s" + i, out var spell), Is.True);
                Assert.That(spell.RemainingCharges, Is.EqualTo(i + 1));
            }
            Add(store, "extra", "spell", 8);
            uint version = store.Version;
            Assert.That(store.TryMove("extra", Tray(12)).Success, Is.False);
            Assert.That(store.TryMove("extra", Tray(0)).Reason, Is.EqualTo(InventoryFailureReason.Occupied));
            Assert.That(store.Version, Is.EqualTo(version)); At(store, "extra", ItemLocation.Warehouse);
        }

        [Test] public void TrayRejectsContainersAndGridMetadata()
        {
            var store = Store(Define("spell", ItemKind.Spell), Define("bottle", ItemKind.Container));
            Add(store, "s", "spell", 8); Add(store, "b", "bottle");
            Assert.That(store.TryMove("s", Tray(0)).Success, Is.True);
            Assert.That(store.TryMove("b", Tray(1)).Reason, Is.EqualTo(InventoryFailureReason.WrongKind));
            Assert.That(store.TryMove("s", new ItemLocation((ItemLocationKind)7, "player", 1, 0, 0, 1)).Success, Is.False);
        }

        [Test] public void SaveCloneAndCarriedScopeIncludeTray()
        {
            var store = Store(Define("spell", ItemKind.Spell)); Add(store, "s", "spell", 8);
            Assert.That(store.TryMove("s", Tray(11)).Success, Is.True);
            var profile = new SinglePlayerProfile("player", store, currency: 19);
            Assert.That(RaidProfileOps.IsCarried(profile, "s"), Is.True);
            var dto = ProfileSaveMapper.ToDto(profile, 0);
            Assert.That(dto.SchemaVersion, Is.EqualTo(ProfileSaveMapper.CurrentSchema));
            Assert.That(ProfileSaveMapper.TryRestore(dto, store.Catalog, out var restored).Success, Is.True);
            At(restored.Items, "s", Tray(11)); Assert.That(restored.Currency, Is.EqualTo(19));
            At(RaidProfileOps.Clone(profile).Items, "s", Tray(11));
            dto.Items[0].OwnerId = "other";
            Assert.That(ProfileSaveMapper.TryRestore(dto, store.Catalog, out _).Success, Is.False);
        }

        [Test] public void SchemaSixCannotSmuggleNewStorageLocation()
        {
            var store = Store(Define("spell", ItemKind.Spell)); Add(store, "s", "spell", 8);
            var dto = ProfileSaveMapper.ToDto(new SinglePlayerProfile("player", store), 0);
            dto.SchemaVersion = 6; dto.Items[0].LocationKind = (ItemLocationKind)7;
            dto.Items[0].OwnerId = "player"; dto.Items[0].SlotIndex = 0;
            Assert.That(ProfileSaveMapper.TryRestore(dto, store.Catalog, out _).Success, Is.False);
        }

        [Test] public void SeventhToolBindingUsesFinalHeldSlotAndSurvivesSave()
        {
            var store = Store(Define("bag", ItemKind.Backpack), Define("bottle", ItemKind.Container));
            Add(store, "bag", "bag"); Add(store, "b", "bottle");
            Assert.That(store.TryMove("bag", new ItemLocation(ItemLocationKind.EquippedBackpack, "player", 0, 0, 0, 0)).Success, Is.True);
            Assert.That(store.TryMove("b", Bag()).Success, Is.True);
            var profile = new SinglePlayerProfile("player", store); var tools = new ToolbeltService(profile, "test");
            Assert.That(tools.TryBind(6, "b", tools.ConfigurationVersion), Is.True);
            Assert.That(tools.TrySelect(9), Is.True); Assert.That(tools.SelectedItemId, Is.EqualTo("b"));
            var dto = ProfileSaveMapper.ToDto(profile, 0);
            Assert.That(ProfileSaveMapper.TryRestore(dto, store.Catalog, out var restored).Success, Is.True);
            Assert.That(new ToolbeltService(restored, "restored").TryGetBinding(6, out var id), Is.True);
            Assert.That(id, Is.EqualTo("b"));
            dto.SchemaVersion = 6; dto.ToolBindings = new[] { "b", null, null, null, null, null };
            Assert.That(ProfileSaveMapper.TryRestore(dto, store.Catalog, out restored).Success, Is.True);
            var migrated = new ToolbeltService(restored, "old");
            Assert.That(migrated.TryGetBinding(0, out _), Is.True); Assert.That(migrated.TryGetBinding(6, out _), Is.False);
        }

        [Test] public void AlchemyCategoryKeepsOldNumericValuesAndCannotUseWithoutBehavior()
        {
            Assert.That((int)ItemKind.Spell, Is.Zero); Assert.That((int)ItemKind.Consumable, Is.EqualTo(5));
            var store = Store(Define("bag", ItemKind.Backpack), Define("herb", (ItemKind)6));
            Add(store, "bag", "bag"); Add(store, "herb", "herb");
            Assert.That(store.TryMove("bag", new ItemLocation(ItemLocationKind.EquippedBackpack, "player", 0, 0, 0, 0)).Success, Is.True);
            Assert.That(store.TryMove("herb", Bag()).Success, Is.True);
            var tools = new ToolbeltService(new SinglePlayerProfile("player", store), "test");
            Assert.That(tools.TryBind(0, "herb", tools.ConfigurationVersion), Is.False);
        }

        [Test] public void OccupiedTrayDragSwapsWholeInstancesAndRejectsStaleVersion()
        {
            var store = Store(Define("spell", ItemKind.Spell)); Add(store, "a", "spell", 3); Add(store, "b", "spell", 9);
            Assert.That(store.TryMove("a", Tray(0)).Success, Is.True); Assert.That(store.TryMove("b", Tray(1)).Success, Is.True);
            uint version = store.Version;
            var method = typeof(InventoryMoveOps).GetMethod("TryPlaceSpell"); Assert.That(method, Is.Not.Null);
            var result = (InventoryOperationResult)method.Invoke(null, new object[] { store, "a", Tray(1), version });
            Assert.That(result.Success, Is.True); At(store, "a", Tray(1)); At(store, "b", Tray(0));
            Assert.That(store.TryGet("b", out var retained), Is.True); Assert.That(retained.RemainingCharges, Is.EqualTo(9));
            result = (InventoryOperationResult)method.Invoke(null, new object[] { store, "a", Tray(0), version });
            Assert.That(result.Reason, Is.EqualTo(InventoryFailureReason.StaleVersion)); At(store, "a", Tray(1));
        }
    }

    public sealed class SpellStorageRaidTests : RaidTestFixture
    {
        [TestCase(RaidOutcome.Success)] [TestCase(RaidOutcome.Death)] [TestCase(RaidOutcome.Crash)]
        public void ActualDepartureAndSettlementIncludeOrdinaryAndProtectedTraySpells(RaidOutcome outcome)
        {
            Build();
            Assert.That(Profile.Items.TryMove("ordinary", new ItemLocation((ItemLocationKind)7, "player", 0, 0, 0, 0)).Success, Is.True);
            Assert.That(Profile.Items.TryMove("protected", new ItemLocation((ItemLocationKind)7, "player", 0, 0, 0, 1)).Success, Is.True);
            Depart();
            At(Profile.Items, "ordinary", new ItemLocation((ItemLocationKind)7, Profile.ActorId, 0, 0, 0, 0));
            Assert.That(Profile.PendingRaid.IsHubAsset("ordinary"), Is.False);
            Assert.That(new RaidSettlementService().TrySettle(Profile, Repository,
                new RaidSettlementRequest(Profile.PendingRaid.RaidId, outcome), out var hub, out _).Success, Is.True);
            Assert.That(hub.Items.TryGet("ordinary", out _), Is.EqualTo(outcome == RaidOutcome.Success));
            Assert.That(hub.Items.TryGet("protected", out var saved), Is.True);
            Assert.That(saved.RemainingCharges, Is.EqualTo(3));
            At(hub.Items, "protected", outcome == RaidOutcome.Success ? new ItemLocation((ItemLocationKind)7, "player", 0, 0, 0, 1) : ItemLocation.Warehouse);
        }

        [Test] public void FailedLoadRollbackRestoresTrayOwnerPositionAndCharges()
        {
            Build(); Assert.That(Profile.Items.TryMove("ordinary", new ItemLocation((ItemLocationKind)7, "player", 0, 0, 0, 11)).Success, Is.True);
            Depart(false);
            Assert.That(new RaidPreparationService().TryRollback(Profile, Repository, out var restored).Success, Is.True);
            At(restored.Items, "ordinary", new ItemLocation((ItemLocationKind)7, "player", 0, 0, 0, 11));
            Assert.That(restored.Items.TryGet("ordinary", out var item), Is.True); Assert.That(item.RemainingCharges, Is.EqualTo(5));
        }
    }
}
