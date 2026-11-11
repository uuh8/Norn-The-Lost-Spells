using System;
using System.Collections.Generic;
using Game.Core;
using Game.Skills;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Run.Tests
{
    /// <summary>
    /// 验证资产身份与唯一归属；预期来自总案的副本/失败规则，不通过被测实现计算。
    /// 临时 SO 仅作为真实 Authoring 输入，不修改 Project 中的配置或当前场景。
    /// </summary>
    public sealed class ItemOwnershipStoreTests
    {
        private readonly List<UnityEngine.Object> _assets = new List<UnityEngine.Object>();
        private SpellDefinition _waterSpell;
        private ItemDefinition _spell, _wand, _backpack, _container;
        private ItemCatalog _catalog;
        private ItemOwnershipStore _store;

        [SetUp]
        public void SetUp()
        {
            _waterSpell = Track(ScriptableObject.CreateInstance<SpellDefinition>());
            _spell = Define("spell.water", ItemKind.Spell, _waterSpell);
            _wand = Define("wand.basic", ItemKind.Wand);
            _backpack = Define("backpack.basic", ItemKind.Backpack);
            _container = Define("container.basic", ItemKind.Container);
            _catalog = Catalog(_spell, _wand, _backpack, _container);
            Assert.That(_catalog.TryCreateIndex(out ItemCatalogIndex index, out string error), Is.True, error);
            _store = new ItemOwnershipStore(index);
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = _assets.Count - 1; i >= 0; i--)
                UnityEngine.Object.DestroyImmediate(_assets[i]);
            _assets.Clear();
        }

        [Test]
        public void SameDefinition_TwoInstancesRemainIndependent()
        {
            Add("a", "spell.water", 3);
            Add("b", "spell.water", 8);
            Assert.That(_store.TryAdd(new RuntimeItemState("a", "spell.water", 99), ItemLocation.Warehouse), Is.False);
            Assert.That(_store.TryGet("a", out RuntimeItemState a), Is.True);
            Assert.That(_store.TryGet("b", out RuntimeItemState b), Is.True);
            Assert.That(a.RemainingCharges, Is.EqualTo(3));
            Assert.That(b.RemainingCharges, Is.EqualTo(8));
            Assert.That(a, Is.Not.SameAs(b));
            Assert.That(_store.Count, Is.EqualTo(2));
        }

        [Test]
        public void MoveOneDuplicate_PreservesOtherInstanceAndCharges()
        {
            Add("a", "spell.water", 3);
            Add("b", "spell.water", 8);
            var destination = World("pickup-a");
            Assert.That(_store.TryMove("a", destination).Success, Is.True);
            AssertLocation("a", destination);
            AssertLocation("b", ItemLocation.Warehouse);
            _store.TryGet("b", out RuntimeItemState b);
            Assert.That(b.RemainingCharges, Is.EqualTo(8));
            Assert.That(_store.Count, Is.EqualTo(2));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" ")]
        public void AddInvalidInstanceId_LeavesStoreUnchanged(string id)
        {
            Assert.That(_store.TryAdd(new RuntimeItemState(id, "spell.water", 3), ItemLocation.Warehouse), Is.False);
            AssertEmpty();
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" ")]
        [TestCase("spell.unknown")]
        [TestCase("Spell.Water")]
        public void AddInvalidOrUnknownContentId_LeavesStoreUnchanged(string contentId)
        {
            Assert.That(_store.TryAdd(new RuntimeItemState("a", contentId, 3), ItemLocation.Warehouse), Is.False);
            AssertEmpty();
        }

        [Test]
        public void AddNegativeCharges_LeavesStoreUnchanged()
        {
            Assert.That(_store.TryAdd(new RuntimeItemState("a", "spell.water", -1), ItemLocation.Warehouse), Is.False);
            AssertEmpty();
        }

        [Test]
        public void AddNullItem_LeavesStoreUnchanged()
        {
            Assert.That(_store.TryAdd(null, ItemLocation.Warehouse), Is.False);
            AssertEmpty();
        }

        [Test]
        public void UnlimitedSpell_UsesExplicitFlagAndRetainsIdentity()
        {
            var item = new RuntimeItemState("unlimited-a", "spell.water", 0, true);
            Assert.That(_store.TryAdd(item, ItemLocation.Warehouse), Is.True);
            _store.TryGet("unlimited-a", out RuntimeItemState owned);
            Assert.That(owned.HasUnlimitedCharges, Is.True);
            Assert.That(owned.RemainingCharges, Is.EqualTo(0));
            Assert.That(owned.InstanceId, Is.EqualTo("unlimited-a"));
        }

        [Test]
        public void CreateFreshInstances_GeneratesDistinctIds_RecoveryKeepsSuppliedId()
        {
            RuntimeItemState a = RuntimeItemState.Create("spell.water", 3);
            RuntimeItemState b = RuntimeItemState.Create("spell.water", 8);
            Assert.That(a.InstanceId, Is.Not.EqualTo(b.InstanceId));
            Assert.That(Guid.TryParseExact(a.InstanceId, "N", out _), Is.True);
            Assert.That(_store.TryAdd(new RuntimeItemState(a.InstanceId, a.ContentId, 2), ItemLocation.Warehouse), Is.True);
            _store.TryGet(a.InstanceId, out RuntimeItemState restored);
            Assert.That(restored.InstanceId, Is.EqualTo(a.InstanceId));
            Assert.That(restored.RemainingCharges, Is.EqualTo(2));
        }

        [Test]
        public void CatalogDuplicateContentId_RejectsWholeIndex()
        {
            ItemDefinition duplicate = Define("spell.water", ItemKind.Spell, _waterSpell);
            ItemCatalog invalid = Catalog(_spell, duplicate);
            Assert.That(invalid.TryCreateIndex(out ItemCatalogIndex index, out string error), Is.False);
            Assert.That(index, Is.Null);
            Assert.That(error, Is.Not.Empty);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" ")]
        public void CatalogInvalidContentId_RejectsWholeIndex(string contentId)
        {
            ItemCatalog invalid = Catalog(_spell, Define(contentId, ItemKind.Wand));
            Assert.That(invalid.TryCreateIndex(out ItemCatalogIndex index, out _), Is.False);
            Assert.That(index, Is.Null);
        }

        [Test]
        public void CatalogNullDefinition_RejectsWholeIndex()
        {
            Assert.That(Catalog(_spell, null).TryCreateIndex(out ItemCatalogIndex index, out _), Is.False);
            Assert.That(index, Is.Null);
        }

        [Test]
        public void CatalogSpellWithoutInstruction_RejectsWholeIndex()
        {
            Assert.That(Catalog(Define("spell.invalid", ItemKind.Spell)).TryCreateIndex(out _, out _), Is.False);
        }

        [Test]
        public void CatalogInvalidKind_RejectsWholeIndex()
        {
            Assert.That(Catalog(Define("invalid", (ItemKind)255)).TryCreateIndex(out _, out _), Is.False);
        }

        [Test]
        public void CatalogIndex_ContinuesSharingReadOnlySpellDefinition()
        {
            Assert.That(_catalog.TryCreateIndex(out ItemCatalogIndex index, out _), Is.True);
            Assert.That(index.TryGet("spell.water", out ItemDefinition definition), Is.True);
            Assert.That(definition.SpellDefinition, Is.SameAs(_waterSpell));
            Assert.That(index.Count, Is.EqualTo(4));
        }

        [Test]
        public void AddMissingParent_DoesNotPublishOrphan()
        {
            Assert.That(_store.TryAdd(new RuntimeItemState("a", "spell.water", 3), Slot("missing-wand", 0)), Is.False);
            AssertEmpty();
        }

        [Test]
        public void MoveMissingParent_PreservesOriginalLocationAndVersion()
        {
            Add("a", "spell.water", 3);
            uint version = _store.Version;
            Assert.That(_store.TryMove("a", Slot("missing", 0)).Reason, Is.EqualTo(InventoryFailureReason.InvalidOwnership));
            AssertLocation("a", ItemLocation.Warehouse);
            Assert.That(_store.Version, Is.EqualTo(version));
        }

        [Test]
        public void MoveToSelf_PreservesOriginalLocation()
        {
            Add("bag", "backpack.basic");
            Assert.That(_store.TryMove("bag", Bag("bag")).Reason, Is.EqualTo(InventoryFailureReason.InvalidOwnership));
            AssertLocation("bag", ItemLocation.Warehouse);
        }

        [Test]
        public void MoveParentIntoDescendant_RejectsCycleBeforeAnyWrite()
        {
            Add("bag", "backpack.basic");
            Assert.That(_store.TryAdd(new RuntimeItemState("wand", "wand.basic", 0), Bag("bag")), Is.True);
            Assert.That(_store.TryAdd(new RuntimeItemState("spell", "spell.water", 3), Slot("wand", 0)), Is.True);
            uint version = _store.Version;
            Assert.That(_store.TryMove("bag", Slot("wand", 1)).Reason, Is.EqualTo(InventoryFailureReason.InvalidOwnership));
            AssertLocation("bag", ItemLocation.Warehouse);
            AssertLocation("wand", Bag("bag"));
            AssertLocation("spell", Slot("wand", 0));
            Assert.That(_store.Version, Is.EqualTo(version));
        }

        [Test]
        public void WrongParentKind_RejectsSpellSlot()
        {
            Add("bottle", "container.basic");
            Add("spell", "spell.water", 3);
            Assert.That(_store.TryMove("spell", Slot("bottle", 0)).Reason, Is.EqualTo(InventoryFailureReason.WrongKind));
            AssertLocation("spell", ItemLocation.Warehouse);
        }

        [Test]
        public void WrongItemKind_CannotInstallContainerAsSpell()
        {
            Add("wand", "wand.basic");
            Add("bottle", "container.basic");
            Assert.That(_store.TryMove("bottle", Slot("wand", 0)).Reason, Is.EqualTo(InventoryFailureReason.WrongKind));
            AssertLocation("bottle", ItemLocation.Warehouse);
        }

        [Test]
        public void OccupiedWandSlot_PreservesBothLocations()
        {
            Add("wand", "wand.basic");
            Add("a", "spell.water", 3);
            Add("b", "spell.water", 8);
            Assert.That(_store.TryMove("a", Slot("wand", 0)).Success, Is.True);
            uint version = _store.Version;
            Assert.That(_store.TryMove("b", Slot("wand", 0)).Reason, Is.EqualTo(InventoryFailureReason.Occupied));
            AssertLocation("a", Slot("wand", 0));
            AssertLocation("b", ItemLocation.Warehouse);
            Assert.That(_store.Version, Is.EqualTo(version));
        }

        [TestCase(-1, 0, 0)]
        [TestCase(0, -1, 0)]
        [TestCase(0, 0, -1)]
        [TestCase(0, 0, 4)]
        public void InvalidGridMetadata_LeavesSourceUnchanged(int x, int y, int rotation)
        {
            Add("bag", "backpack.basic");
            Add("a", "spell.water", 3);
            var destination = new ItemLocation(ItemLocationKind.Backpack, "bag", x, y, rotation, -1);
            Assert.That(_store.TryMove("a", destination).Reason, Is.EqualTo(InventoryFailureReason.OutOfBounds));
            AssertLocation("a", ItemLocation.Warehouse);
        }

        [TestCase(-1)]
        [TestCase(3)]
        public void InvalidEquippedWandSlot_LeavesSourceUnchanged(int slot)
        {
            Add("wand", "wand.basic");
            var destination = new ItemLocation(ItemLocationKind.EquippedWand, "player", 0, 0, 0, slot);
            Assert.That(_store.TryMove("wand", destination).Reason, Is.EqualTo(InventoryFailureReason.OutOfBounds));
            AssertLocation("wand", ItemLocation.Warehouse);
        }

        [Test]
        public void MoveSameLocation_IsSuccessfulWithoutVersionChange()
        {
            Add("a", "spell.water", 3);
            uint version = _store.Version;
            InventoryOperationResult result = _store.TryMove("a", ItemLocation.Warehouse);
            Assert.That(result.Success, Is.True);
            Assert.That(result.Version, Is.EqualTo(version));
            Assert.That(_store.Version, Is.EqualTo(version));
        }

        [Test]
        public void SuccessfulMutation_IncrementsVersionOnce()
        {
            Add("a", "spell.water", 3);
            Assert.That(_store.Version, Is.EqualTo(1));
            InventoryOperationResult result = _store.TryMove("a", World("pickup-a"));
            Assert.That(result.Success, Is.True);
            Assert.That(result.Version, Is.EqualTo(2));
            Assert.That(_store.Version, Is.EqualTo(2));
        }

        [Test]
        public void MoveUnknownInstance_ReportsNotFoundWithoutChange()
        {
            Assert.That(_store.TryMove("missing", ItemLocation.Warehouse).Reason, Is.EqualTo(InventoryFailureReason.NotFound));
            AssertEmpty();
        }

        [Test]
        public void InvalidLocationKind_ReportsInvalidStateWithoutChange()
        {
            Add("a", "spell.water", 3);
            var location = new ItemLocation((ItemLocationKind)255, "invalid", 0, 0, 0, -1);
            Assert.That(_store.TryMove("a", location).Reason, Is.EqualTo(InventoryFailureReason.InvalidState));
            AssertLocation("a", ItemLocation.Warehouse);
        }

        // 跨Owner第一小段只测试同一容器在两份真实Store之间移交；无网络/场景/存档/新物品发放。
        [Test]
        public void CrossOwnerContainerMovesViaGroundWithTheSameInstanceAndComposition()
        {
            AddTransferBottle(); var ground = new ItemOwnershipStore(_store.Catalog); var receiver = TransferReceiver("b");
            _store.TryGet("transfer-bottle", out var original); uint compositionVersion = original.Composition.Version;
            uint a = _store.Version, b = receiver.Version;
            Assert.That(Transfer(_store, ground, Bag("transfer-bag"), World("world-incarnation")).Success, Is.True);
            Assert.That(_store.TryGet("transfer-bottle", out _), Is.False);
            Assert.That(ground.TryGet("transfer-bottle", out var dropped), Is.True); Assert.That(dropped, Is.SameAs(original));
            Assert.That(Transfer(ground, receiver, World("world-incarnation"), Bag("b-bag")).Success, Is.True);
            Assert.That(ground.Count, Is.Zero); Assert.That(receiver.TryGet("transfer-bottle", out var picked), Is.True);
            Assert.That(picked, Is.SameAs(original)); Assert.That(picked.Composition, Is.SameAs(original.Composition));
            Assert.That(picked.Composition.Total, Is.EqualTo(27ul)); Assert.That(picked.Composition.GetQuantity(1), Is.EqualTo(20ul));
            Assert.That(picked.Composition.GetQuantity(3), Is.EqualTo(7ul)); Assert.That(picked.Composition.Version, Is.EqualTo(compositionVersion));
            Assert.That(_store.Version, Is.EqualTo(a + 1)); Assert.That(receiver.Version, Is.EqualTo(b + 1)); Assert.That(ground.Version, Is.EqualTo(2));
        }

        [Test]
        public void CrossOwnerFirstSuccessfulPickupWinsWithoutASecondCopy()
        {
            AddTransferBottle(); var ground = new ItemOwnershipStore(_store.Catalog); var b = TransferReceiver("b"); var c = TransferReceiver("c");
            Assert.That(Transfer(_store, ground, Bag("transfer-bag"), World("drop-1")).Success, Is.True);
            Assert.That(Transfer(ground, b, World("drop-1"), Bag("b-bag")).Success, Is.True);
            uint version = c.Version;
            Assert.That(Transfer(ground, c, World("drop-1"), Bag("c-bag")).Success, Is.False);
            Assert.That(c.Version, Is.EqualTo(version)); Assert.That(c.TryGet("transfer-bottle", out _), Is.False);
            Assert.That(b.TryGet("transfer-bottle", out var bottle), Is.True); Assert.That(bottle.Composition.Total, Is.EqualTo(27ul));
        }

        [Test]
        public void CrossOwnerFailedCapacityLeavesBothVersionsAndQuantityUntouched()
        {
            AddTransferBottle(); var ground = new ItemOwnershipStore(_store.Catalog); var b = TransferReceiver("b");
            Assert.That(Transfer(_store, ground, Bag("transfer-bag"), World("drop-1")).Success, Is.True);
            Assert.That(b.TryAdd(new RuntimeItemState("blocking", "container.basic", 0), Bag("b-bag")), Is.True);
            uint gv = ground.Version, bv = b.Version;
            Assert.That(Transfer(ground, b, World("drop-1"), Bag("b-bag")).Success, Is.False);
            Assert.That(ground.Version, Is.EqualTo(gv)); Assert.That(b.Version, Is.EqualTo(bv));
            Assert.That(ground.TryGet("transfer-bottle", out var bottle), Is.True); Assert.That(bottle.Composition.Total, Is.EqualTo(27ul));
            Assert.That(b.TryGet("transfer-bottle", out _), Is.False);
            // 此夹具的默认包只有一格；先真实移走占格物，再向同一合法空格拾取，验证失败没有遗留准备锁。
            Assert.That(b.TryMove("blocking", ItemLocation.Warehouse).Success, Is.True);
            Assert.That(Transfer(ground, b, World("drop-1"), Bag("b-bag")).Success, Is.True);
        }

        [TestCase(true)] [TestCase(false)]
        public void CrossOwnerStaleEitherVersionDoesNotMoveTheBottle(bool sourceStale)
        {
            AddTransferBottle(); var ground = new ItemOwnershipStore(_store.Catalog); uint av = _store.Version, gv = ground.Version;
            var result = Transfer(_store, ground, Bag("transfer-bag"), World("drop-1"), sourceStale ? av + 1 : av, sourceStale ? gv : gv + 1);
            Assert.That(result.Reason, Is.EqualTo(InventoryFailureReason.StaleVersion));
            Assert.That(_store.Version, Is.EqualTo(av)); Assert.That(ground.Version, Is.EqualTo(gv)); Assert.That(ground.Count, Is.Zero);
        }

        [Test]
        public void CrossOwnerWrongGroundIncarnationCannotPickupTheCurrentItem()
        {
            AddTransferBottle(); var ground = new ItemOwnershipStore(_store.Catalog); var b = TransferReceiver("b");
            Assert.That(Transfer(_store, ground, Bag("transfer-bag"), World("new-drop")).Success, Is.True);
            Assert.That(Transfer(ground, b, World("old-drop"), Bag("b-bag")).Reason, Is.EqualTo(InventoryFailureReason.InvalidOwnership));
            Assert.That(ground.TryGetLocation("transfer-bottle", out var location), Is.True); Assert.That(location.OwnerId, Is.EqualTo("new-drop"));
            Assert.That(b.TryGet("transfer-bottle", out _), Is.False);
        }

        [Test]
        public void CrossOwnerCannotTransferWhileSourceMaterialTransactionHoldsTheLock()
        {
            AddTransferBottle(); var ground = new ItemOwnershipStore(_store.Catalog);
            Assert.That(_store.TryLockMaterial(_store.Version), Is.True);
            try { Assert.That(Transfer(_store, ground, Bag("transfer-bag"), World("drop-1")).Reason, Is.EqualTo(InventoryFailureReason.ResourceBusy)); }
            finally { _store.FinishMaterial(false); }
            Assert.That(Transfer(_store, ground, Bag("transfer-bag"), World("drop-1")).Success, Is.True);
        }

        [Test]
        public void CrossOwnerDuplicateTargetIdentityDoesNotDeleteTheSource()
        {
            AddTransferBottle(); var ground = new ItemOwnershipStore(_store.Catalog);
            Assert.That(ground.TryAdd(new RuntimeItemState("transfer-bottle","container.basic",0),World("other-drop")), Is.True);
            Assert.That(Transfer(_store, ground, Bag("transfer-bag"), World("drop-1")).Reason, Is.EqualTo(InventoryFailureReason.DuplicateIdentity));
            Assert.That(_store.TryGet("transfer-bottle", out var original), Is.True); Assert.That(original.Composition.Total, Is.EqualTo(27ul));
        }

        private ItemOwnershipStore TransferReceiver(string actor)
        {
            var result = new ItemOwnershipStore(_store.Catalog);
            Assert.That(result.TryAdd(new RuntimeItemState(actor+"-bag","backpack.basic",0),new ItemLocation(ItemLocationKind.EquippedBackpack,actor,0,0,0,0)),Is.True);
            return result;
        }
        private void AddTransferBottle()
        {
            Assert.That(_store.TryAdd(new RuntimeItemState("transfer-bag","backpack.basic",0),new ItemLocation(ItemLocationKind.EquippedBackpack,"a",0,0,0,0)), Is.True);
            Assert.That(_store.Catalog.TryGetRules("container.basic",out var rules), Is.True);
            var mix = new ContainerComposition(rules.ContainerCapacity,rules.ContainerMaterials);
            mix.AddReserved(new[] {new MaterialAmount(1,20),new MaterialAmount(3,7)},2);
            Assert.That(_store.TryAdd(new RuntimeItemState("transfer-bottle","container.basic",0,composition:mix),Bag("transfer-bag")),Is.True);
        }
        private static InventoryOperationResult Transfer(ItemOwnershipStore from, ItemOwnershipStore to, ItemLocation source, ItemLocation target,
            uint? sourceVersion=null,uint? targetVersion=null)
        {
            var method=typeof(ItemOwnershipStore).GetMethod("TryTransferContainerTo");
            Assert.That(method,Is.Not.Null,"必须有复用原准备锁的真实跨Store提交，不能先删后加或复制物品。");
            return (InventoryOperationResult)method.Invoke(from,new object[]{to,"transfer-bottle",source,target,sourceVersion??from.Version,targetVersion??to.Version});
        }

        private void Add(string id, string contentId, int charges = 0)
        {
            Assert.That(_store.TryAdd(new RuntimeItemState(id, contentId, charges), ItemLocation.Warehouse), Is.True);
        }

        private void AssertEmpty()
        {
            Assert.That(_store.Count, Is.EqualTo(0));
            Assert.That(_store.Version, Is.EqualTo(0));
        }

        private void AssertLocation(string id, ItemLocation expected)
        {
            Assert.That(_store.TryGetLocation(id, out ItemLocation actual), Is.True);
            Assert.That(actual, Is.EqualTo(expected));
        }

        private static ItemLocation Slot(string owner, int slot) =>
            new ItemLocation(ItemLocationKind.WandSlot, owner, 0, 0, 0, slot);
        private static ItemLocation Bag(string owner) =>
            new ItemLocation(ItemLocationKind.Backpack, owner, 0, 0, 0, -1);
        private static ItemLocation World(string owner) =>
            new ItemLocation(ItemLocationKind.WorldItem, owner, 0, 0, 0, -1);

        private T Track<T>(T asset) where T : UnityEngine.Object
        {
            _assets.Add(asset);
            return asset;
        }

        private ItemDefinition Define(string id, ItemKind kind, SpellDefinition spell = null)
        {
            var definition = Track(ScriptableObject.CreateInstance<ItemDefinition>());
            var serialized = new SerializedObject(definition);
            serialized.FindProperty("_contentId").stringValue = id;
            serialized.FindProperty("_kind").intValue = (int)kind;
            serialized.FindProperty("_spellDefinition").objectReferenceValue = spell;
            if (kind == ItemKind.Wand)
            {
                var wand = Track(ScriptableObject.CreateInstance<WandDefinition>());
                var parameters = new SerializedObject(wand); parameters.FindProperty("_programCapacity").intValue = 1;
                parameters.ApplyModifiedPropertiesWithoutUndo(); serialized.FindProperty("_wandDefinition").objectReferenceValue = wand;
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return definition;
        }

        private ItemCatalog Catalog(params ItemDefinition[] definitions)
        {
            var catalog = Track(ScriptableObject.CreateInstance<ItemCatalog>());
            var serialized = new SerializedObject(catalog);
            SerializedProperty entries = serialized.FindProperty("_definitions");
            entries.arraySize = definitions.Length;
            for (int i = 0; i < definitions.Length; i++)
                entries.GetArrayElementAtIndex(i).objectReferenceValue = definitions[i];
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return catalog;
        }
    }
}
