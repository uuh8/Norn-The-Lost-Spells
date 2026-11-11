using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using Game.Core;
using Game.Skills;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Run.Tests
{
    /// <summary>
    /// 共享地面必须是Owner之间同一容器的真实归属转交，而非每人各留一份地面副本。
    /// 使用真实Catalog、Workspace、材料锁和完整Owner导出；不需要Scene、Physics或真实玩家存档。
    /// </summary>
    public sealed class OnlineGroundItemStoreTests : InventoryTestFixture
    {
        private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private const string BottleId = "cccccccccccccccccccccccccccccccc";

        [Test] public void PeacefulGroundRestorationKeepsTheOriginalBottleIdentityCompositionAndPose()
        {
            var owner = Owner(1, withBottle: true);
            Assert.That(_ground.Drop(owner, BottleId, new Vector3(3, 2, 1), Quaternion.Euler(0, 37, 0)), Is.EqualTo(OnlineInventoryCommandFailure.None));
            var saved = _ground.Capture(); var restored = new OnlineGroundItemStore(_catalog);
            try
            {
                var method = typeof(OnlineGroundItemStore).GetMethod("TryRestorePractice"); Assert.That(method, Is.Not.Null);
                var args = new object[] { saved, null };
                Assert.That((bool)method.Invoke(restored, args), Is.True, args[1] as string);
                Assert.That(restored.TryGet(BottleId, out var actual), Is.True);
                Assert.That(actual.WorldOwnerId, Is.EqualTo(saved.Items[0].WorldOwnerId));
                Assert.That(actual.Position, Is.EqualTo(saved.Items[0].Position));
                Assert.That(actual.Rotation, Is.EqualTo(saved.Items[0].Rotation)); AssertParts(actual.ContainerState);
            }
            finally { restored.Dispose(); }
        }

        [Test] public void InvalidPeacefulGroundCannotPartiallyInstallAnyOriginalInstance()
        {
            var owner = Owner(1, withBottle: true); _ground.Drop(owner, BottleId, Vector3.forward, Quaternion.identity);
            var saved = _ground.Capture(); saved.Items = new[] { saved.Items[0], saved.Items[0] };
            var restored = new OnlineGroundItemStore(_catalog);
            try
            {
                var method = typeof(OnlineGroundItemStore).GetMethod("TryRestorePractice"); Assert.That(method, Is.Not.Null);
                var args = new object[] { saved, null }; Assert.That((bool)method.Invoke(restored, args), Is.False);
                Assert.That(restored.Count, Is.Zero); Assert.That(restored.Version, Is.EqualTo(1));
            }
            finally { restored.Dispose(); }
        }
        private ItemCatalogIndex _catalog;
        private OnlineGroundItemStore _ground;
        private readonly List<OnlineOwnedWorkspace> _owners = new List<OnlineOwnedWorkspace>();

        [SetUp]
        public void CreateRealMemoryCatalogAndOneSharedGroundOwner()
        {
            var bottle = Define("ground.bottle", ItemKind.Container);
            var card = Define("ground.card", ItemKind.Spell); card.SpellDefinition.Kind = SpellKind.Modify;
            var data = new SerializedObject(bottle); data.FindProperty("_containerCapacityGmu").intValue = 15000; data.ApplyModifiedPropertiesWithoutUndo();
            _catalog = Store(Define("ground.wand", ItemKind.Wand), bottle,
                Define("ground.bar", ItemKind.Container, 2, 1),
                Define("ground.elbow", ItemKind.Container, 2, 2, cells: new[] { Vector2Int.zero, Vector2Int.right, Vector2Int.up }),
                Define("ground.bag", ItemKind.Backpack, bagWidth: 8, bagHeight: 8),
                Define("ground.tiny-bag", ItemKind.Backpack),
                Define("ground.vertical-bag", ItemKind.Backpack, bagWidth: 1, bagHeight: 2),
                Define("ground.filler", ItemKind.Consumable), Define("ground.ice", ItemKind.Material), card).Catalog;
            _ground = new OnlineGroundItemStore(_catalog);
        }

        [TearDown]
        public void DisposeOnlyThisFixturesOwnersAndGround()
        {
            for (int i = _owners.Count - 1; i >= 0; i--) _owners[i].Dispose();
            _owners.Clear(); _ground?.Dispose();
        }


        private OnlineInventoryCommandFailure RegisterLoot(string key, string content, Vector3 position, out string id)
        {
            var method = typeof(OnlineGroundItemStore).GetMethod("TryRegisterLoot");
            Assert.That(method, Is.Not.Null, "缺少DS稳定key空瓶注册入口，不能伪造玩家库存Drop。");
            object[] args = { key, content, position, Quaternion.identity, null };
            var failure = (OnlineInventoryCommandFailure)method.Invoke(_ground, args); id = args[4] as string; return failure;
        }

        [Test]
        public void ContainerLootBoundUsesCatalogShapeStackAndBackpackRules()
        {
            Assert.That(DungeonLootPlanner.CalculateContainerLootBound(_catalog), Is.EqualTo(296));
            var bottle = Define("proof.stack", ItemKind.Container, maxStack: 4);
            var bag = Define("proof.bag", ItemKind.Backpack, bagWidth: 8, bagHeight: 8);
            var original = Store(bottle, bag).Catalog;
            // 容器按真实实例冻结为一件一份；authoring 的堆叠值不能绕过该规则。
            Assert.That(original.TryGetRules("proof.stack", out var bottleRules), Is.True);
            Assert.That(bottleRules.MaxStack, Is.EqualTo(1));
            // 修改 SO 后重建真实 Index 才能看到新背包面积；旧快照仍保持原规则。
            var changed = new SerializedObject(bag);
            changed.FindProperty("_backpackWidth").intValue = 32;
            changed.FindProperty("_backpackHeight").intValue = 8;
            changed.ApplyModifiedPropertiesWithoutUndo();
            var rebuilt = Store(bottle, bag).Catalog;
            Assert.That(DungeonLootPlanner.CalculateContainerLootBound(original), Is.EqualTo(296));
            Assert.That(DungeonLootPlanner.CalculateContainerLootBound(rebuilt), Is.EqualTo(1064));
            var unsupported = Store(Define("proof.large-bottle", ItemKind.Container),
                Define("proof.large-bag", ItemKind.Backpack, bagWidth: 128, bagHeight: 128)).Catalog;
            Assert.Throws<ArgumentException>(() => DungeonLootPlanner.CalculateContainerLootBound(unsupported));
            Assert.That(OnlineGroundInventorySnapshot.MaximumItems, Is.EqualTo(16384));
        }

        [Test]
        public void RegisteredLootIsEmptyAndStaysDeduplicatedAfterPickup()
        {
            var owner = Owner(1); uint inventory = owner.InventoryVersion;
            Assert.That(RegisterLoot("p7.run.0.0.3.0", "ground.bottle", Vector3.forward, out var id), Is.EqualTo(OnlineInventoryCommandFailure.None));
            Assert.That(owner.InventoryVersion, Is.EqualTo(inventory));
            Assert.That(_ground.TryGet(id, out var item), Is.True); Assert.That(item.ContainerState.SchemaVersion, Is.EqualTo(1u));
            Assert.That(item.ContainerState.Components, Is.Empty); uint ground = _ground.Version;
            Assert.That(Run(owner, new OnlineInventoryCommandIntent { CommandId = 1, ConnectionEpoch = 7, ExpectedInventoryVersion = inventory,
                Kind = OnlineInventoryCommandKind.PickupContainer, ItemId = id, WorldOwnerId = item.WorldOwnerId, AutoPlace = true }).Success, Is.True);
            Assert.That(_ground.Count, Is.Zero); uint picked = _ground.Version;
            Assert.That(RegisterLoot("p7.run.0.0.3.0", "ground.bottle", Vector3.right * 99, out var same), Is.EqualTo(OnlineInventoryCommandFailure.None));
            Assert.That(same, Is.EqualTo(id)); Assert.That(_ground.Count, Is.Zero); Assert.That(_ground.Version, Is.EqualTo(picked));
            Assert.That(owner.TryGetItem(id, out var owned), Is.True); Assert.That(owned.Composition.Total, Is.Zero);
            Assert.That(picked, Is.GreaterThan(ground));
        }

        [Test]
        public void LootRegistrationRefusesFortyNinthKeyWithoutEvictingExistingItems()
        {
            var ids = new string[48];
            for (int i = 0; i < ids.Length; i++)
                Assert.That(RegisterLoot("p7.run." + i, "ground.bottle", Vector3.forward, out ids[i]), Is.EqualTo(OnlineInventoryCommandFailure.None));
            uint version = _ground.Version;
            Assert.That(RegisterLoot("p7.run.overflow", "ground.bottle", Vector3.forward, out _), Is.EqualTo(OnlineInventoryCommandFailure.NoCapacity));
            Assert.That(_ground.Count, Is.EqualTo(48)); Assert.That(_ground.Version, Is.EqualTo(version));
            for (int i = 0; i < ids.Length; i++)
            {
                Assert.That(_ground.TryGet(ids[i], out var item), Is.True, "超限拒绝不能淘汰任一已有真实实例。");
                Assert.That(item.InstanceId, Is.EqualTo(ids[i]));
                Assert.That(_ground.TryGetLootRegistration("p7.run." + i, out var registered), Is.True);
                Assert.That(registered, Is.EqualTo(ids[i]), "全部原稳定key继续关联同一实例。");
            }
        }

        [Test]
        public void NonContainerLootRegistrationDoesNotMutateGround()
        {
            uint version = _ground.Version;
            Assert.That(RegisterLoot("p7.run.bad", "ground.filler", Vector3.forward, out _), Is.EqualTo(OnlineInventoryCommandFailure.InvalidInput));
            Assert.That(_ground.Count, Is.Zero); Assert.That(_ground.Version, Is.EqualTo(version));
        }

        [Test]
        public void MixedBottleMovesFromAToGroundToBWithTheSameIdentityComponentsAndExportableRevision()
        {
            var a = Owner(1, withBottle: true); var b = Owner(2); uint av = a.InventoryVersion, bv = b.InventoryVersion, gv = _ground.Version;
            Assert.That(Run(a, Drop(a, 1)).Success, Is.True);
            Assert.That(a.TryGetItem(BottleId, out _), Is.False); Assert.That(_ground.Count, Is.EqualTo(1));
            Assert.That(_ground.TryGet(BottleId, out var dropped), Is.True);
            Assert.That(dropped.Revision, Is.EqualTo(4u)); Assert.That(dropped.ContentId, Is.EqualTo("ground.bottle")); AssertParts(dropped.ContainerState);
            Assert.That(a.InventoryVersion, Is.GreaterThan(av)); Assert.That(b.InventoryVersion, Is.EqualTo(bv)); Assert.That(_ground.Version, Is.GreaterThan(gv));
            // Capture必须是独立值，诊断/网络层改DTO不能回写共享Owner中的真实材料。
            dropped.ContainerState.Components[0].Quantity = 999;
            Assert.That(_ground.TryGet(BottleId, out dropped), Is.True); AssertParts(dropped.ContainerState);
            Assert.That(Run(b, Pickup(b, 1, dropped.WorldOwnerId)).Success, Is.True);
            Assert.That(_ground.Count, Is.Zero); Assert.That(a.TryGetItem(BottleId, out _), Is.False); Assert.That(b.TryGetItem(BottleId, out var bottle), Is.True);
            Assert.That(bottle.Composition.GetQuantity(1), Is.EqualTo(100ul)); Assert.That(bottle.Composition.GetQuantity(3), Is.EqualTo(50ul));
            var exported = Snapshot(b); var item = Find(exported, BottleId);
            Assert.That(item.Revision, Is.EqualTo(4u)); Assert.That(item.ParentInstanceId, Is.EqualTo("bag.2")); Assert.That(item.LocationKind, Is.EqualTo((int)ItemLocationKind.Backpack)); AssertParts(item.ContainerState);
            Assert.That(OnlineOwnedWorkspaceFactory.TryCreate(_catalog, exported.Assets, Hash, out var restored, out var error), Is.True, error);
            _owners.Add(restored); Assert.That(restored.TryGetItem(BottleId, out var restoredBottle), Is.True); Assert.That(restoredBottle.Composition.Total, Is.EqualTo(150ul));
        }

        [Test]
        public void TwoOwnersCompetingForOneGroundBottleCanCommitOnlyOneFirstPickup()
        {
            var a = Owner(1, withBottle: true); var b = Owner(2); Assert.That(Run(a, Drop(a, 1)).Success, Is.True);
            Assert.That(_ground.TryGet(BottleId, out var item), Is.True);
            var winner = Pickup(b, 1, item.WorldOwnerId); Assert.That(Run(b, winner).Success, Is.True);
            uint av = a.InventoryVersion, bv = b.InventoryVersion, gv = _ground.Version;
            Assert.That(Run(a, Pickup(a, 2, item.WorldOwnerId)).Success, Is.False);
            Assert.That(Run(b, winner).Success, Is.True, "成功重放只返回原收据，不是第二次首拾。");
            Assert.That(a.InventoryVersion, Is.EqualTo(av)); Assert.That(b.InventoryVersion, Is.EqualTo(bv)); Assert.That(_ground.Version, Is.EqualTo(gv));
            Assert.That(a.TryGetItem(BottleId, out _), Is.False); Assert.That(b.TryGetItem(BottleId, out var owned), Is.True); Assert.That(owned.Composition.Total, Is.EqualTo(150ul));
            Assert.That(_ground.Count, Is.Zero);
        }

        [TestCase("wrong-world")]
        [TestCase("wrong-world-owner")]
        [TestCase("expired-epoch")]
        [TestCase("wrong-backpack")]
        [TestCase("out-of-reach")]
        public void InvalidPickupIdentityContextOrEpochCannotMoveEitherSide(string defect)
        {
            var a = Owner(1, withBottle: true); var b = Owner(2); Assert.That(Run(a, Drop(a, 1)).Success, Is.True);
            Assert.That(_ground.TryGet(BottleId, out var item), Is.True); var intent = Pickup(b, 1, item.WorldOwnerId); var context = Context();
            if (defect == "wrong-world") context.WorldId = "another-world";
            if (defect == "wrong-world-owner") intent.WorldOwnerId = new string('f', 32);
            if (defect == "wrong-backpack") { intent.AutoPlace = false; intent.TargetBackpackId = "bag.1"; }
            if (defect == "out-of-reach") context.ActorPosition = Vector3.right * 10;
            if (defect == "expired-epoch")
            {
                Assert.That(b.ExecuteInventoryCommand(new OnlineInventoryCommandIntent { CommandId = 1, ConnectionEpoch = 8,
                    ExpectedInventoryVersion = b.InventoryVersion, Kind = OnlineInventoryCommandKind.SelectWand, ItemId = "wand.2", TargetSlot = 0 }).Success, Is.True);
                intent.CommandId = 2;
            }
            uint bv = b.InventoryVersion, gv = _ground.Version;
            Assert.That(b.ExecuteInventoryCommand(intent, context).Success, Is.False);
            Assert.That(b.InventoryVersion, Is.EqualTo(bv)); Assert.That(_ground.Version, Is.EqualTo(gv)); Assert.That(_ground.Count, Is.EqualTo(1));
            Assert.That(b.TryGetItem(BottleId, out _), Is.False); Assert.That(_ground.TryGet(BottleId, out var unchanged), Is.True); AssertParts(unchanged.ContainerState);
        }

        [Test]
        public void ASecondDropGetsANewWorldOwnerAndOldPickupReceiptsNeverRefillOrCaptureIt()
        {
            var a = Owner(1, withBottle: true); var b = Owner(2); Assert.That(Run(a, Drop(a, 1)).Success, Is.True);
            Assert.That(_ground.TryGet(BottleId, out var first), Is.True); var pickup = Pickup(b, 1, first.WorldOwnerId);
            Assert.That(Run(b, pickup).Success, Is.True); Assert.That(Run(b, Drop(b, 2)).Success, Is.True);
            Assert.That(_ground.TryGet(BottleId, out var second), Is.True); Assert.That(second.WorldOwnerId, Is.Not.EqualTo(first.WorldOwnerId));
            uint bv = b.InventoryVersion, gv = _ground.Version;
            Assert.That(Run(b, pickup).Success, Is.True); Assert.That(b.TryGetItem(BottleId, out _), Is.False);
            Assert.That(Run(a, Pickup(a, 2, first.WorldOwnerId)).Success, Is.False);
            Assert.That(_ground.Version, Is.EqualTo(gv)); Assert.That(b.InventoryVersion, Is.EqualTo(bv));
            Assert.That(Run(a, Pickup(a, 3, second.WorldOwnerId)).Success, Is.True);
            Assert.That(_ground.Count, Is.Zero); Assert.That(a.TryGetItem(BottleId, out var owned), Is.True); Assert.That(owned.Composition.Total, Is.EqualTo(150ul));
        }

        [TestCase("busy", false)]
        [TestCase("busy", true)]
        [TestCase("frozen", false)]
        [TestCase("frozen", true)]
        [TestCase("world-frozen", false)]
        [TestCase("world-frozen", true)]
        [TestCase("no-bag", true)]
        [TestCase("full-bag", true)]
        public void FailedDropOrPickupKeepsBothSidesQuantitiesLocationsAndVersions(string defect, bool pickingUp)
        {
            var a = Owner(1, withBottle: true);
            var b = Owner(2, bagContent: defect == "no-bag" ? null : defect == "full-bag" ? "ground.tiny-bag" : "ground.bag", fullBag: defect == "full-bag");
            if (pickingUp) Assert.That(Run(a, Drop(a, 1)).Success, Is.True);
            var target = pickingUp ? b : a; var profile = Profile(target); bool locked = false;
            if (defect == "busy")
                locked = (bool)typeof(ItemOwnershipStore).GetMethod("TryLockMaterial", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(profile.Items, new object[] { profile.Items.Version });
            if (defect == "busy") Assert.That(locked, Is.True, "用真实材料预约建立Busy，不伪造错误码。");
            if (defect == "frozen") typeof(SinglePlayerProfile).GetProperty("RaidActionsFrozen", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(profile, true);
            try
            {
                var intent = Drop(a, 1);
                if (pickingUp) { Assert.That(_ground.TryGet(BottleId, out var groundItem), Is.True); intent = Pickup(b, 1, groundItem.WorldOwnerId); }
                var context = Context(); if (defect == "world-frozen") context.CanAdvance = false;
                uint av = a.InventoryVersion, bv = b.InventoryVersion, gv = _ground.Version; int groundCount = _ground.Count;
                Assert.That(target.ExecuteInventoryCommand(intent, context).Success, Is.False);
                Assert.That(a.InventoryVersion, Is.EqualTo(av)); Assert.That(b.InventoryVersion, Is.EqualTo(bv)); Assert.That(_ground.Version, Is.EqualTo(gv)); Assert.That(_ground.Count, Is.EqualTo(groundCount));
                Assert.That(b.TryGetItem(BottleId, out _), Is.False);
                if (pickingUp) { Assert.That(_ground.TryGet(BottleId, out var current), Is.True); AssertParts(current.ContainerState); }
                else { Assert.That(a.TryGetItem(BottleId, out var current), Is.True); Assert.That(current.Composition.Total, Is.EqualTo(150ul)); }
            }
            finally
            {
                if (locked) typeof(ItemOwnershipStore).GetMethod("FinishMaterial", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(profile.Items, new object[] { false });
                if (defect == "frozen") typeof(SinglePlayerProfile).GetProperty("RaidActionsFrozen", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(profile, false);
            }
        }

        [Test]
        public void RebindingTheSameGroundAndPeekingReceiptsDoesNotResetAnyAssetOrVersion()
        {
            var a = Owner(1, withBottle: true); var b = Owner(2); var drop = Drop(a, 1);
            Assert.That(a.TryGetRecordedInventoryCommand(drop, out _), Is.False); var result = Run(a, drop); Assert.That(result.Success, Is.True);
            uint av = a.InventoryVersion, gv = _ground.Version;
            a.ConfigureGroundItems(_ground); b.ConfigureGroundItems(_ground);
            Assert.That(a.TryGetRecordedInventoryCommand(drop, out var recorded), Is.True); Assert.That(recorded.Success, Is.True); Assert.That(recorded.InventoryVersion, Is.EqualTo(result.InventoryVersion));
            var conflict = drop; conflict.AutoPlace = true;
            Assert.That(a.TryGetRecordedInventoryCommand(conflict, out recorded), Is.True); Assert.That(recorded.Failure, Is.EqualTo(OnlineInventoryCommandFailure.CommandConflict));
            Assert.That(Run(a, drop).Success, Is.True); Assert.That(a.InventoryVersion, Is.EqualTo(av)); Assert.That(_ground.Version, Is.EqualTo(gv)); Assert.That(_ground.Count, Is.EqualTo(1));
            using (var other = new OnlineGroundItemStore(_catalog)) Assert.Throws<InvalidOperationException>(() => a.ConfigureGroundItems(other));
            Assert.That(_ground.TryGet(BottleId, out var item), Is.True); var next = Pickup(b, 1, item.WorldOwnerId); next.ConnectionEpoch = 8;
            Assert.That(Run(b, next).Success, Is.True); Assert.That(b.TryGetItem(BottleId, out var owned), Is.True); Assert.That(owned.Composition.Total, Is.EqualTo(150ul));
        }

        [Test]
        public void AutomaticPickupUsesRealRotationWhenOnlyAVerticalFootprintFits()
        {
            var a = Owner(1, withBottle: true, bottleContent: "ground.bar"); var b = Owner(2, bagContent: "ground.vertical-bag");
            Assert.That(Run(a, Drop(a, 1)).Success, Is.True); Assert.That(_ground.TryGet(BottleId, out var item), Is.True);
            Assert.That(Run(b, Pickup(b, 1, item.WorldOwnerId)).Success, Is.True);
            var picked = Find(Snapshot(b), BottleId); Assert.That(picked.X, Is.Zero); Assert.That(picked.Y, Is.Zero); Assert.That(picked.QuarterTurns, Is.EqualTo(1)); Assert.That(picked.Slot, Is.EqualTo(-1)); AssertParts(picked.ContainerState);
        }

        [Test]
        public void AutomaticPickupDoesNotTreatAnUnoccupiedShapeHoleAsACollision()
        {
            var a = Owner(1, withBottle: true, bottleContent: "ground.elbow"); var b = Owner(2, holeFiller: true);
            Assert.That(Run(a, Drop(a, 1)).Success, Is.True); Assert.That(_ground.TryGet(BottleId, out var item), Is.True);
            Assert.That(Run(b, Pickup(b, 1, item.WorldOwnerId)).Success, Is.True);
            var picked = Find(Snapshot(b), BottleId); Assert.That(picked.X, Is.Zero); Assert.That(picked.Y, Is.Zero); Assert.That(picked.QuarterTurns, Is.Zero);
            Assert.That(b.TryGetItem("filler.2.0", out _), Is.True);
        }

        [Test]
        public void ManualPickupKeepsTheRequestedRealBackpackCellAndRotation()
        {
            var a = Owner(1, withBottle: true); var b = Owner(2); Assert.That(Run(a, Drop(a, 1)).Success, Is.True); Assert.That(_ground.TryGet(BottleId, out var item), Is.True);
            var intent = Pickup(b, 1, item.WorldOwnerId); intent.AutoPlace = false; intent.TargetBackpackId = "bag.2"; intent.GridX = 5; intent.GridY = 6; intent.QuarterTurns = 3;
            Assert.That(Run(b, intent).Success, Is.True); var picked = Find(Snapshot(b), BottleId);
            Assert.That(picked.ParentInstanceId, Is.EqualTo("bag.2")); Assert.That(picked.X, Is.EqualTo(5)); Assert.That(picked.Y, Is.EqualTo(6)); Assert.That(picked.QuarterTurns, Is.EqualTo(3)); AssertParts(picked.ContainerState);
        }

        [Test]
        public void ThrowingPostCommitNotificationCannotLoseTheSuccessfulReceiptOrRepeatTheGroundTransfer()
        {
            var owner = Owner(1, withBottle: true); var request = Drop(owner, 1); int notifications = 0;
            Action<InventoryChangedEvent> throwing = change =>
            {
                if (change.InventoryId != owner.InventoryId) return;
                notifications++; throw new InvalidOperationException("ground notification test subscriber");
            };
            EventBus<InventoryChangedEvent>.Subscribe(throwing);
            try
            {
                // 归属提交与收据写入前不能调用外部订阅者。模型完成后再由组合层显式通知，
                // 订阅异常只记录已提交事实，不能让调用者误以为转移失败再执行一次。
                var committed = Run(owner, request); Assert.That(committed.Success, Is.True); Assert.That(notifications, Is.Zero);
                Assert.That(owner.TryGetRecordedInventoryCommand(request, out var recorded), Is.True); Assert.That(recorded.Success, Is.True);
                uint inventoryVersion = owner.InventoryVersion, groundVersion = _ground.Version;
                Assert.That(_ground.TryGet(BottleId, out var before), Is.True); AssertParts(before.ContainerState);
                LogAssert.Expect(LogType.Error, new Regex("共享容器已提交；通知订阅者异常："));
                Assert.DoesNotThrow(() => owner.NotifyGroundInventoryCommitted());
                Assert.That(notifications, Is.EqualTo(1));
                Assert.That(owner.TryGetRecordedInventoryCommand(request, out recorded), Is.True);
                Assert.That(recorded.Success, Is.True); Assert.That(recorded.InventoryVersion, Is.EqualTo(committed.InventoryVersion));
                Assert.That(Run(owner, request).Success, Is.True);
                Assert.That(owner.InventoryVersion, Is.EqualTo(inventoryVersion)); Assert.That(_ground.Version, Is.EqualTo(groundVersion));
                Assert.That(_ground.Count, Is.EqualTo(1)); Assert.That(owner.TryGetItem(BottleId, out _), Is.False);
                Assert.That(_ground.TryGet(BottleId, out var unchanged), Is.True); AssertParts(unchanged.ContainerState);
                Assert.That(unchanged.WorldOwnerId, Is.EqualTo(before.WorldOwnerId)); Assert.That(notifications, Is.EqualTo(1));
            }
            finally { EventBus<InventoryChangedEvent>.Unsubscribe(throwing); }
        }

        [Test]
        public void SharedMaterialWholePickupHasOneWinnerAndStableRegistrationCannotGrantItAgain()
        {
            var a = Owner(1); var b = Owner(2);
            // 原十二个 NPC 容器 key 与唯一原固定冰来源分别有界，冰不能挤占容器来源的额度。
            for (int i = 0; i < 12; i++) Assert.That(RegisterLoot("p7.npc." + i, "ground.bottle", Vector3.forward, out _), Is.EqualTo(OnlineInventoryCommandFailure.None));
            Assert.That(RegisterMaterial("p7.explore.0", 256, out var id), Is.EqualTo(OnlineInventoryCommandFailure.None));
            Assert.That(_ground.Count, Is.EqualTo(13));
            Assert.That(_ground.TryGet(id, out var spawned), Is.True); Assert.That(GroundMaterialQuantity(spawned), Is.EqualTo(256ul));
            var intent = Pickup(a, 1, spawned.WorldOwnerId); intent.ItemId = id;
            var first = Run(a, intent); Assert.That(first.Success, Is.True); Assert.That(first.Transferred, Is.EqualTo(256ul));
            Assert.That(a.TryGetItem(id, out var owned), Is.True); Assert.That(owned.Material.Quantity, Is.EqualTo(256ul));
            Assert.That(_ground.Count, Is.EqualTo(12)); uint version = a.InventoryVersion, ground = _ground.Version;
            Assert.That(Run(a, intent).Transferred, Is.EqualTo(256ul)); Assert.That(a.InventoryVersion, Is.EqualTo(version));
            var competing = Pickup(b, 1, spawned.WorldOwnerId); competing.ItemId = id;
            Assert.That(Run(b, competing).Failure, Is.EqualTo(OnlineInventoryCommandFailure.NotOwned));
            Assert.That(RegisterMaterial("p7.explore.0", 256, out var same), Is.EqualTo(OnlineInventoryCommandFailure.None));
            Assert.That(same, Is.EqualTo(id)); Assert.That(_ground.Count, Is.EqualTo(12)); Assert.That(_ground.Version, Is.EqualTo(ground));
            Assert.That(RegisterMaterial("unconfigured-second-fixed-source", 256, out _), Is.EqualTo(OnlineInventoryCommandFailure.NoCapacity));
            Assert.That(_ground.Count, Is.EqualTo(12)); Assert.That(_ground.Version, Is.EqualTo(ground));
            var capture = Snapshot(a); Assert.That(GroundMaterialQuantity(Find(capture, id)), Is.EqualTo(256ul));
        }

        [Test]
        public void SharedMaterialAutoPickupPreservesOriginalPartialMergeRemainderIdentityAndExactBalance()
        {
            var a = Owner(1, materialQuantity: 900); var b = Owner(2);
            Assert.That(RegisterMaterial("p7.explore.0", 256, out var id), Is.EqualTo(OnlineInventoryCommandFailure.None));
            Assert.That(_ground.TryGet(id, out var spawned), Is.True);
            var request = Pickup(a, 1, spawned.WorldOwnerId); request.ItemId = id;
            var merged = Run(a, request); Assert.That(merged.Success, Is.True); Assert.That(merged.Transferred, Is.EqualTo(124ul));
            Assert.That(a.TryGetItem("solid.1", out var target), Is.True); Assert.That(target.Material.Quantity, Is.EqualTo(1024ul));
            Assert.That(a.TryGetItem(id, out _), Is.False); Assert.That(_ground.TryGet(id, out var remainder), Is.True);
            Assert.That(remainder.WorldOwnerId, Is.EqualTo(spawned.WorldOwnerId)); Assert.That(remainder.Revision, Is.GreaterThan(spawned.Revision));
            Assert.That(GroundMaterialQuantity(remainder), Is.EqualTo(132ul)); uint gv = _ground.Version, av = a.InventoryVersion;
            Assert.That(Run(a, request).Transferred, Is.EqualTo(124ul)); Assert.That(_ground.Version, Is.EqualTo(gv));
            var full = Pickup(a, 2, spawned.WorldOwnerId); full.ItemId = id; full.AutoPlace = false; full.TargetBackpackId = "bag.1";
            Assert.That(Run(a, full).Success, Is.False); Assert.That(a.InventoryVersion, Is.EqualTo(av)); Assert.That(_ground.Version, Is.EqualTo(gv));
            var pickup = Pickup(b, 1, spawned.WorldOwnerId); pickup.ItemId = id;
            Assert.That(Run(b, pickup).Transferred, Is.EqualTo(132ul)); Assert.That(b.TryGetItem(id, out var rest), Is.True);
            Assert.That(rest.Material.Quantity, Is.EqualTo(132ul)); Assert.That(_ground.Count, Is.Zero);
            Assert.That(target.Material.Quantity + rest.Material.Quantity, Is.EqualTo(1156ul));
        }

        private OnlineInventoryCommandFailure RegisterMaterial(string key, ulong quantity, out string id)
        {
            var method = typeof(OnlineGroundItemStore).GetMethod("TryRegisterMaterialLoot"); Assert.That(method, Is.Not.Null);
            object[] args = { key, "ground.ice", quantity, Vector3.forward, Quaternion.identity, null };
            var failure = (OnlineInventoryCommandFailure)method.Invoke(_ground, args); id = args[5] as string; return failure;
        }
        private static void AssignGroundMaterial(object row, ulong quantity)
        {
            var type = typeof(GameLog).Assembly.GetType("Game.Core.OnlineOwnedMaterialStateSnapshot"); Assert.That(type, Is.Not.Null);
            var state = Activator.CreateInstance(type); type.GetField("SchemaVersion").SetValue(state, 1u);
            type.GetField("MaterialKey").SetValue(state, (byte)5); type.GetField("Quantity").SetValue(state, quantity);
            var field = row.GetType().GetField("MaterialState"); Assert.That(field, Is.Not.Null); field.SetValue(row, state);
        }
        private static ulong GroundMaterialQuantity(object row)
        {
            var field = row.GetType().GetField("MaterialState"); Assert.That(field, Is.Not.Null);
            var state = field.GetValue(row); Assert.That(state, Is.Not.Null); return (ulong)state.GetType().GetField("Quantity").GetValue(state);
        }

        [Test]
        public void GeneralConsumableTransfersThroughOneSharedGroundWithoutDuplicatingIdentity()
        {
            var a = Owner(1, fullBag: true); var b = Owner(2);
            const string id = "filler.1.0";
            Assert.That(_ground.Drop(a, id, Vector3.forward, Quaternion.identity), Is.EqualTo(OnlineInventoryCommandFailure.None));
            Assert.That(a.TryGetItem(id, out _), Is.False);
            Assert.That(_ground.TryGet(id, out var dropped), Is.True);
            var legacy = Pickup(b, 1, dropped.WorldOwnerId); legacy.ItemId = id;
            Assert.That(Run(b, legacy).Failure, Is.EqualTo(OnlineInventoryCommandFailure.InvalidInput),
                "旧8仍只支持容器/固体；普通Item必须走已批准的新15，不能靠放宽旧命令通过测试。");
            Assert.That(_ground.TryGet(id, out _), Is.True);
            var pickup = Pickup(b, 2, dropped.WorldOwnerId); pickup.ItemId = id; pickup.Kind = OnlineInventoryCommandKind.PickupItem;
            Assert.That(Run(b, pickup).Success, Is.True);
            Assert.That(_ground.TryGet(id, out _), Is.False);
            Assert.That(b.TryGetItem(id, out var actual), Is.True);
            Assert.That(actual.InstanceId, Is.EqualTo(id));
            Assert.That(actual.ContentId, Is.EqualTo("ground.filler"));
            Assert.That(Run(a, pickup).Success, Is.False, "同一世界实例只有一个玩家取得，迟到请求不能复制资产。");
        }

        [Test]
        public void LastEquippedWandCanBeDroppedAndOwnerStillExportsAnEmptyHand()
        {
            var a = Owner(1);
            Assert.That(_ground.Drop(a, "wand.1", Vector3.forward, Quaternion.identity), Is.EqualTo(OnlineInventoryCommandFailure.None));
            Assert.That(a.WandsReadOnly.SelectedWandId, Is.Null);
            Assert.That(a.TryCapturePresentation(7, 1, 40, out var snapshot), Is.True);
            Assert.That(snapshot.Wands, Is.Empty);
            Assert.That(snapshot.Assets.SelectedWandInstanceId, Is.Null.Or.Empty);
            Assert.That(snapshot.Assets.Items.Length, Is.EqualTo(1), "装备包仍属于原角色，空手不应导致整份Owner快照失效。");
        }

        [Test]
        public void GroundWandTreePreservesSpentManaProgramAndPaidQualificationAcrossOwners()
        {
            var a = Owner(1, withCard: true); var b = Owner(2);
            Assert.That(a.WandsReadOnly.TryGetProgram("wand.1", out var program), Is.True);
            var wands = (WandLoadoutService)typeof(OnlineOwnedWorkspace).GetField("_wands", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(a);
            var spend = wands.Execute(new WandResourceRequestedEvent { InventoryId = a.InventoryId, CallerId = "ground-transfer",
                RequestId = 1, SourceWandId = "wand.1", ManaCost = 35, IsRootCast = true, ProgramRevision = program.ProgramRevision });
            Assert.That(spend.Success, Is.True);
            Assert.That(a.TryGetWandExecution("wand.1", out var execution), Is.True);
            Assert.That(_ground.Drop(a, "wand.1", Vector3.forward, Quaternion.identity), Is.EqualTo(OnlineInventoryCommandFailure.None));
            Assert.That(a.TryGetItem("card.1", out _), Is.False, "安装卡随原杖树迁出，不能留悬空父链或留另一份资产。");
            Assert.That(_ground.TryGet("wand.1", out var dropped), Is.True);
            var pickup = Pickup(b, 1, dropped.WorldOwnerId); pickup.Kind = (OnlineInventoryCommandKind)15; pickup.ItemId = "wand.1";
            Assert.That(Run(b, pickup).Success, Is.True);
            Assert.That(b.TryGetItem("card.1", out var card), Is.True);
            Assert.That(card.RemainingCharges, Is.Zero);
            Assert.That(b.WandsReadOnly.TryGetState("wand.1", out var state), Is.True);
            Assert.That(state.CurrentMana, Is.EqualTo(65)); Assert.That(state.CooldownRemaining, Is.EqualTo(spend.CooldownRemaining));
            Assert.That(b.TryGetWandExecution("wand.1", out var restored), Is.True);
            Assert.That(restored.Program.Revision, Is.EqualTo(execution.Program.Revision));
            var snapshot = Snapshot(b);
            Assert.That(Find(snapshot, "card.1").ParentInstanceId, Is.EqualTo("wand.1"));
            Assert.That(Array.Find(snapshot.SpellUses, use => use.InstanceId == "card.1").Eligible, Is.True,
                "资格随同一实例转交，已支付的最后一次不能在新Owner重新扣费。");
        }

        [Test]
        public void GenericGroundSpellAutomaticPickupPrefersExistingSpellStorageBeforeBag()
        {
            var a = Owner(1, withCard: true); var b = Owner(2);
            Assert.That(_ground.Drop(a, "card.1", Vector3.forward, Quaternion.identity), Is.EqualTo(OnlineInventoryCommandFailure.None));
            Assert.That(_ground.TryGet("card.1", out var dropped), Is.True);
            var pickup = Pickup(b, 1, dropped.WorldOwnerId); pickup.Kind = (OnlineInventoryCommandKind)15; pickup.ItemId = "card.1";
            Assert.That(Run(b, pickup).Success, Is.True);
            Assert.That(Find(Snapshot(b), "card.1").LocationKind, Is.EqualTo((int)ItemLocationKind.SpellStorage));
            Assert.That(b.TryGetItem("card.1", out var state), Is.True); Assert.That(state.RemainingCharges, Is.Zero);
        }

        private OnlineOwnedWorkspace Owner(int number, bool withBottle = false, string bagContent = "ground.bag", bool fullBag = false, string bottleContent = "ground.bottle", bool holeFiller = false, ulong materialQuantity = 0, bool withCard = false)
        {
            var items = new List<OnlineOwnedItemSnapshot>();
            var wand = Entry("wand." + number, "ground.wand", ItemLocationKind.EquippedWand); wand.UseTemplateWandStats = true; items.Add(wand);
            if (withCard) { var card = Entry("card." + number, "ground.card", ItemLocationKind.WandSlot, wand.InstanceId); card.RemainingCharges = 1; items.Add(card); }
            if (bagContent != null) items.Add(Entry("bag." + number, bagContent, ItemLocationKind.EquippedBackpack));
            if (withBottle) { var bottle = Entry(BottleId, bottleContent, ItemLocationKind.Backpack, "bag." + number, -1); bottle.ContainerState = Parts(); items.Add(bottle); }
            if (materialQuantity > 0) { var solid = Entry("solid." + number, "ground.ice", ItemLocationKind.Backpack, "bag." + number, -1); AssignGroundMaterial(solid, materialQuantity); items.Add(solid); }
            if (fullBag || holeFiller) items.Add(Entry("filler." + number + ".0", "ground.filler", ItemLocationKind.Backpack, "bag." + number, -1, holeFiller ? 1 : 0, holeFiller ? 1 : 0));
            var seed = new OnlineOwnedAssetSnapshot { SchemaVersion = OnlineOwnedAssetSnapshot.CurrentSchemaVersion, RunId = "ground-run", PlayerId = "player." + number,
                InventoryId = "inventory." + number, ActorId = (ulong)number, InventoryVersion = 7, CurrencyVersion = 1, Gold = 0,
                GameplayConfigHash = Hash, SelectedWandInstanceId = "wand." + number, Items = items.ToArray() };
            Assert.That(OnlineOwnedWorkspaceFactory.TryCreate(_catalog, seed, Hash, out var owner, out var error), Is.True, error);
            _owners.Add(owner); owner.ConfigureGroundItems(_ground); return owner;
        }
        private static OnlineOwnedItemSnapshot Entry(string id, string content, ItemLocationKind kind, string parent = "", int slot = 0, int x = 0, int y = 0)
            => new OnlineOwnedItemSnapshot { InstanceId = id, ContentId = content, Revision = 4, LocationKind = (int)kind, ParentInstanceId = parent, Slot = slot, X = x, Y = y };
        private static OnlineOwnedContainerStateSnapshot Parts() => new OnlineOwnedContainerStateSnapshot { SchemaVersion = 1, Components = new[]
            { new OnlineOwnedMaterialAmountSnapshot { MaterialKey = 1, Quantity = 100 }, new OnlineOwnedMaterialAmountSnapshot { MaterialKey = 3, Quantity = 50 } } };
        private static void AssertParts(OnlineOwnedContainerStateSnapshot state)
        { Assert.That(state.SchemaVersion, Is.EqualTo(1u)); Assert.That(state.Components.Length, Is.EqualTo(2)); Assert.That(state.Components[0].MaterialKey, Is.EqualTo(1)); Assert.That(state.Components[0].Quantity, Is.EqualTo(100ul)); Assert.That(state.Components[1].MaterialKey, Is.EqualTo(3)); Assert.That(state.Components[1].Quantity, Is.EqualTo(50ul)); }
        private OnlineContainerAuthorityContext Context() => new OnlineContainerAuthorityContext { CanAdvance = true, WorldId = _ground.WorldId, ActorPosition = Vector3.zero, TargetPosition = Vector3.forward, TargetRotation = Quaternion.identity };
        private OnlineInventoryCommandReceipt Run(OnlineOwnedWorkspace owner, in OnlineInventoryCommandIntent intent) => owner.ExecuteInventoryCommand(intent, Context());
        private static OnlineInventoryCommandIntent Drop(OnlineOwnedWorkspace owner, ulong id) => new OnlineInventoryCommandIntent { CommandId = id, ConnectionEpoch = 7, ExpectedInventoryVersion = owner.InventoryVersion, Kind = OnlineInventoryCommandKind.DropContainer, ItemId = BottleId };
        private static OnlineInventoryCommandIntent Pickup(OnlineOwnedWorkspace owner, ulong id, string worldOwner) => new OnlineInventoryCommandIntent { CommandId = id, ConnectionEpoch = 7, ExpectedInventoryVersion = owner.InventoryVersion, Kind = OnlineInventoryCommandKind.PickupContainer, ItemId = BottleId, WorldOwnerId = worldOwner, AutoPlace = true };
        private static SinglePlayerProfile Profile(OnlineOwnedWorkspace owner) => (SinglePlayerProfile)typeof(OnlineOwnedWorkspace).GetField("_profile", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(owner);
        private static OnlineInventoryPresentationSnapshot Snapshot(OnlineOwnedWorkspace owner) { Assert.That(owner.TryCapturePresentation(7, 1, 40, out var snapshot), Is.True); return snapshot; }
        private static OnlineOwnedItemSnapshot Find(OnlineInventoryPresentationSnapshot snapshot, string id)
        { foreach (var item in snapshot.Assets.Items) if (item.InstanceId == id) return item; Assert.Fail("Owner完整导出缺失真实实例：" + id); return null; }
    }
}
