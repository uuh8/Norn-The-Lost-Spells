using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Combat;
using Game.Core;
using Game.Skills;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Run.Tests
{
    /// <summary>
    /// 从业务入局值建立真实 Owned 服务，验证次数、法力、父链和 EventBus 隔离。
    /// 新 API 在 RED 阶段用 Reflection 查找，缺实现表现为测试断言失败，不阻断整个 Unity 编译。
    /// </summary>
    public sealed class OnlineOwnedWorkspaceTests : InventoryTestFixture
    {
        private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private readonly List<IDisposable> _workspaces = new List<IDisposable>();
        private ItemCatalogIndex _catalog;

        [SetUp]
        public void BuildCatalog()
        {
            var wand = Define("online.wand", ItemKind.Wand);
            var data = new SerializedObject(wand.WandDefinition);
            data.FindProperty("_manaRegenPerSecond").floatValue = 10;
            data.ApplyModifiedPropertiesWithoutUndo();
            var spell = Define("online.fire", ItemKind.Spell);
            spell.SpellDefinition.ManaCost = 20;
            // 真交易服务会校验产出组件；临时 Prefab 使用真实 ProjectileBase，避免把无效配置当施法成功。
            spell.SpellDefinition.ProjectilePrefab = Track(new GameObject("Online owned test projectile", typeof(Rigidbody), typeof(BoxCollider), typeof(Arrow)));
            var bag = Define("online.bag", ItemKind.Backpack, bagWidth: 8, bagHeight: 8);
            var largeBag = Define("online.large-bag", ItemKind.Backpack, bagWidth: 4094);
            var bottle = Define("online.bottle", ItemKind.Container);
            var largeBottle = Define("online.large-bottle", ItemKind.Container);
            var elbow = Define("online.elbow", ItemKind.Container, 2, 2, cells: new[]
                { Vector2Int.zero, Vector2Int.right, Vector2Int.up });
            var ordinary = Define("online.ordinary", ItemKind.Consumable);
            var throwable = Define("online.throwable", ItemKind.Consumable);
            ConfigureInt(bottle, "_containerCapacityGmu", 15000);
            ConfigureInt(largeBottle, "_containerCapacityGmu", int.MaxValue);
            ConfigureInt(throwable, "_useKind", (int)ItemUseKind.Throw);
            _catalog = Store(wand, spell, bag, largeBag, bottle, largeBottle, elbow, ordinary, throwable).Catalog;
        }

        [TearDown]
        public void ReleaseWorkspaces()
        {
            for (int i = _workspaces.Count - 1; i >= 0; i--) _workspaces[i].Dispose();
            _workspaces.Clear();
        }

        private static void ConfigureWriteGate(OnlineOwnedWorkspace workspace, Func<bool> gate)
        {
            var method = typeof(OnlineOwnedWorkspace).GetMethod("ConfigureAuthorityWriteGate", new[] { typeof(Func<bool>) });
            Assert.That(method, Is.Not.Null, "权威原始写入口需要统一的只读 CanAdvance 门。");
            method.Invoke(workspace, new object[] { gate });
        }

        [Test]
        public void ClosedAuthorityGateRejectsDirectAndEventBusCastWithoutReservingResources()
        {
            var workspace = (OnlineOwnedWorkspace)Create(Seed()); uint inventory = workspace.InventoryVersion;
            ConfigureWriteGate(workspace, () => false);
            Assert.That(workspace.WorldActionsAllowed, Is.False);
            Assert.That(Execute(workspace, Request(workspace, CastResourceOperation.PrepareRoot, 1)).Success, Is.False);
            int receipts = 0; CastResourceResultEvent received = default;
            Action<CastResourceResultEvent> onResult = value => { receipts++; received = value; };
            EventBus<CastResourceResultEvent>.Subscribe(onResult);
            try
            {
                EventBus<CastResourceRequestedEvent>.Publish(Request(workspace, CastResourceOperation.PrepareRoot, 2));
                Assert.That(receipts, Is.EqualTo(1)); Assert.That(received.Success, Is.False);
            }
            finally { EventBus<CastResourceResultEvent>.Unsubscribe(onResult); }
            Assert.That(workspace.V2CastResourcesReadOnly.ActiveReservations, Is.Zero);
            Assert.That(State(workspace).Mana, Is.EqualTo(100));
            Assert.That(Item(workspace, "card").RemainingCharges, Is.EqualTo(3));
            Assert.That(workspace.InventoryVersion, Is.EqualTo(inventory));
        }

        [Test]
        public void ClosedAuthorityGatePreservesPreparedCastWithoutCommitCancelOrRegeneration()
        {
            var workspace = (OnlineOwnedWorkspace)Create(Seed());
            bool open = true; ConfigureWriteGate(workspace, () => open);
            var prepared = Execute(workspace, Request(workspace, CastResourceOperation.PrepareRoot, 1));
            Assert.That(prepared.Success, Is.True); uint inventory = workspace.InventoryVersion;
            var before = State(workspace); open = false;
            Assert.That(Execute(workspace, Request(workspace, CastResourceOperation.Complete, 2, prepared.TicketId)).Success, Is.False);
            Assert.That(Execute(workspace, Request(workspace, CastResourceOperation.Cancel, 3, prepared.TicketId)).Success, Is.False);
            Step(workspace, 100, 1);
            Assert.That(workspace.V2CastResourcesReadOnly.ActiveReservations, Is.EqualTo(1));
            Assert.That(workspace.V2CastResourcesReadOnly.TryGetLayer(prepared.TicketId, 0, out _), Is.True);
            Assert.That(State(workspace).Mana, Is.EqualTo(before.Mana));
            Assert.That(State(workspace).ResourceVersion, Is.EqualTo(before.ResourceVersion));
            Assert.That(Item(workspace, "card").RemainingCharges, Is.EqualTo(3));
            Assert.That(workspace.InventoryVersion, Is.EqualTo(inventory));
        }

        [Test]
        public void ClosedAuthorityGateRetainsCommittedCastReceiptAndOutputReleaseFact()
        {
            var workspace = (OnlineOwnedWorkspace)Create(Seed());
            bool open = true; ConfigureWriteGate(workspace, () => open);
            var prepared = Execute(workspace, Request(workspace, CastResourceOperation.PrepareRoot, 1));
            Assert.That(prepared.Success, Is.True);
            var complete = Request(workspace, CastResourceOperation.Complete, 2, prepared.TicketId);
            var committed = Execute(workspace, complete); Assert.That(committed.Success, Is.True);
            var before = State(workspace); uint inventory = workspace.InventoryVersion; open = false;
            var replay = Execute(workspace, complete);
            Assert.That(replay.Success, Is.True); Assert.That(replay.TicketId, Is.EqualTo(committed.TicketId));
            Assert.That(replay.RequestId, Is.EqualTo(committed.RequestId)); Assert.That(replay.Reason, Is.EqualTo(committed.Reason));
            Step(workspace, 100, 1);
            // 已经释放的输出是现场事实；暂停新写入不能把该事实抹掉或把提交改成退款。
            ObserveRootRelease(workspace, prepared.TicketId);
            Assert.That(State(workspace).Mana, Is.EqualTo(before.Mana));
            Assert.That(State(workspace).ResourceVersion, Is.EqualTo(before.ResourceVersion));
            Assert.That(Item(workspace, "card").RemainingCharges, Is.EqualTo(2));
            Assert.That(workspace.InventoryVersion, Is.EqualTo(inventory));
            Assert.That(workspace.WorldActionsAllowed, Is.False);
        }

        [TestCase(0u)]
        [TestCase(1u)]
        [TestCase(4u)]
        public void OldOrUnknownOwnedSchema_IsRejectedInsteadOfGrantingUnlimitedUses(uint schema)
        {
            var seed = Seed(); Set(seed, "SchemaVersion", schema);
            Assert.That(TryCreate(seed, out _, out string error), Is.False);
            Assert.That(error, Is.Not.Empty);
        }

        [Test]
        public void DifferentGameplayConfigHash_IsRejectedBeforeWorkspacePublication()
        {
            var seed = Seed(); Set(seed, "GameplayConfigHash", new string('f', 64));
            Assert.That(TryCreate(seed, out object workspace, out _), Is.False);
            Assert.That(workspace, Is.Null);
        }

        [Test]
        public void InstalledSpellWithMissingParent_IsRejectedAtomically()
        {
            var seed = Seed(); Set(Items(seed).GetValue(1), "ParentInstanceId", "foreign-wand");
            Assert.That(TryCreate(seed, out object workspace, out _), Is.False);
            Assert.That(workspace, Is.Null);
        }

        [Test]
        public void SelectedWandMustBelongToImportedEquippedSlots()
        {
            var seed = Seed(); Set(seed, "SelectedWandInstanceId", "foreign-wand");
            Assert.That(TryCreate(seed, out _, out _), Is.False);
        }

        [Test]
        public void WandWithoutExplicitTemplateMarkerOrInstanceStats_IsRejected()
        {
            var seed = Seed(); Set(Items(seed).GetValue(0), "UseTemplateWandStats", false);
            Assert.That(TryCreate(seed, out _, out _), Is.False);
        }

        [Test]
        public void ImportPreservesIdentityFiniteChargesAndSelectedWandWithoutReadingLocalProfile()
        {
            object seed = Seed(); object workspace = Create(seed);
            RuntimeItemState card = Item(workspace, "card");
            Assert.That(card.ContentId, Is.EqualTo("online.fire"));
            Assert.That(card.RemainingCharges, Is.EqualTo(3));
            Assert.That(card.HasUnlimitedCharges, Is.False);
            Assert.That(State(workspace).Mana, Is.EqualTo(100));
            var read = (IWandLoadoutReadOnly)Property(workspace, "WandsReadOnly");
            Assert.That(read.SelectedWandId, Is.EqualTo("wand"));
            Set(Items(seed).GetValue(1), "RemainingCharges", 999);
            Assert.That(Item(workspace, "card").RemainingCharges, Is.EqualTo(3), "发布后业务 DTO 不能继续改写权威资源");
        }

        [Test]
        public void FiniteZeroChargeSpell_PreparesNoGameplayOutputOrFreeMana()
        {
            var seed = Seed(); Set(Items(seed).GetValue(1), "RemainingCharges", 0);
            object workspace = Create(seed);
            // 原 RED 误把 Prepare.Success 当作有输出权限。V2 可成功建立空 Graph；真正权限在冻结 Layer 中。
            var root = Execute(workspace, Request(workspace, CastResourceOperation.PrepareRoot, 1));
            Assert.That(root.Success, Is.True);
            var resources = (ICastResourceReadOnly)Property(workspace, "V2CastResourcesReadOnly");
            Assert.That(resources.TryGetLayer(root.TicketId, 0, out var layer), Is.True);
            Assert.That(layer.CommandCount, Is.Zero, "有限 0 次卡不能授权任何 Gameplay 产出");
            Assert.That(Execute(workspace, Request(workspace, CastResourceOperation.Complete, 2, root.TicketId)).Success, Is.True);
            Assert.That(Item(workspace, "card").HasUnlimitedCharges, Is.False);
            Assert.That(Item(workspace, "card").RemainingCharges, Is.Zero);
            Assert.That(State(workspace).Mana, Is.EqualTo(100));
        }

        [Test]
        public void InstanceWandStatsOverrideTemplateWithoutMutatingSharedDefinition()
        {
            var seed = Seed(); object wand = Items(seed).GetValue(0);
            Set(wand, "UseTemplateWandStats", false);
            object stats = NewCore("OnlineOwnedWandStatsSnapshot");
            Set(stats, "MaxMana", 150f); Set(stats, "ManaRegenPerSecond", 5f);
            Set(stats, "BaseCastDelay", .2f); Set(stats, "BaseReloadTime", .4f);
            Set(stats, "BaseSpreadDegrees", 0f); Set(stats, "ProgramCapacity", 1);
            Set(stats, "BaseDraws", 1); Set(stats, "Shuffle", false);
            Set(stats, "AlwaysCastContentIds", Array.Empty<string>()); Set(wand, "WandStats", stats);
            object workspace = Create(seed);
            Assert.That(State(workspace).Mana, Is.EqualTo(150));
            Assert.That(_catalog.TryGetWandStats("online.wand", out var template), Is.True);
            Assert.That(template.MaxMana, Is.EqualTo(100));
        }

        [Test]
        public void ForeignWandRequest_IsRejectedWithoutSpendingOwnedResources()
        {
            object workspace = Create(Seed()); var request = Request(workspace, CastResourceOperation.PrepareRoot, 1);
            request.SourceWandId = "foreign-wand";
            Assert.That(Execute(workspace, request).Success, Is.False);
            Assert.That(State(workspace).Mana, Is.EqualTo(100));
            Assert.That(Item(workspace, "card").RemainingCharges, Is.EqualTo(3));
        }

        [Test]
        public void DuplicateComplete_ConsumesManaAndFiniteUsesOnce()
        {
            object workspace = Create(Seed()); var root = Execute(workspace, Request(workspace, CastResourceOperation.PrepareRoot, 1));
            Assert.That(root.Success, Is.True);
            var complete = Request(workspace, CastResourceOperation.Complete, 2, root.TicketId);
            Assert.That(Execute(workspace, complete).Success, Is.True);
            Assert.That(Execute(workspace, complete).Success, Is.True);
            Assert.That(State(workspace).Mana, Is.EqualTo(80));
            Assert.That(Item(workspace, "card").RemainingCharges, Is.EqualTo(2));
        }

        [Test]
        public void RepeatedOrOlderAuthorityTick_DoesNotRegenerateManaTwice()
        {
            object workspace = Create(Seed()); var root = Execute(workspace, Request(workspace, CastResourceOperation.PrepareRoot, 1));
            Assert.That(root.Success, Is.True);
            Assert.That(Execute(workspace, Request(workspace, CastResourceOperation.Complete, 2, root.TicketId)).Success, Is.True);
            Step(workspace, .5f, 1); Assert.That(State(workspace).Mana, Is.EqualTo(85));
            Step(workspace, .5f, 1); Step(workspace, .5f, 0);
            Assert.That(State(workspace).Mana, Is.EqualTo(85));
            Step(workspace, .5f, 2); Assert.That(State(workspace).Mana, Is.EqualTo(90));
        }

        [Test]
        public void EventBusOnlyMatchingInventoryHandlesRequestAndPublishesReceipt()
        {
            object a = Create(Seed("scope.a")), b = Create(Seed("scope.b"));
            int receipts = 0; CastResourceResultEvent result = default;
            Action<CastResourceResultEvent> receive = value => { receipts++; result = value; };
            EventBus<CastResourceResultEvent>.Subscribe(receive);
            try
            {
                EventBus<CastResourceRequestedEvent>.Publish(Request(a, CastResourceOperation.PrepareRoot, 1));
                Assert.That(receipts, Is.EqualTo(1)); Assert.That(result.Success, Is.True);
                EventBus<CastResourceRequestedEvent>.Publish(Request(a, CastResourceOperation.Complete, 2, result.TicketId));
                Assert.That(receipts, Is.EqualTo(2)); Assert.That(result.Success, Is.True);
                Assert.That(State(a).Mana, Is.EqualTo(80)); Assert.That(State(b).Mana, Is.EqualTo(100));
                Assert.That(Item(b, "card").RemainingCharges, Is.EqualTo(3));
            }
            finally { EventBus<CastResourceResultEvent>.Unsubscribe(receive); }
        }

        [Test]
        public void CarriedBagAndMixedBottle_KeepTrustedCapacityIdentityAndIndependentComponents()
        {
            var seed = CarriedBottle(Amount(1, 10000), Amount(3, 5000));
            var workspace = Create(seed);
            var bottle = Item(workspace, "bottle");
            Assert.That(bottle.ContentId, Is.EqualTo("online.bottle"));
            Assert.That(bottle.Composition.Capacity, Is.EqualTo(15000ul));
            Assert.That(bottle.Composition.Total, Is.EqualTo(15000ul));
            Assert.That(bottle.Composition.GetQuantity(1), Is.EqualTo(10000ul));
            Assert.That(bottle.Composition.GetQuantity(3), Is.EqualTo(5000ul));
            // 边界发布之后，网络 DTO 的对象、数组和元素都不再拥有修改权威组分的能力。
            seed.Items[2].ContainerState.Components[0].Quantity = 1;
            seed.Items[2].ContainerState.Components[1] = Amount(8, 1);
            seed.Items[2].ContainerState.Components = Array.Empty<OnlineOwnedMaterialAmountSnapshot>();
            Assert.That(bottle.Composition.GetQuantity(1), Is.EqualTo(10000ul));
            Assert.That(bottle.Composition.GetQuantity(3), Is.EqualTo(5000ul));
            Assert.That(bottle.Composition.Total, Is.EqualTo(15000ul));
            Assert.That(Item(workspace, "card").RemainingCharges, Is.EqualTo(3));
        }

        [Test]
        public void ExplicitSchemaOneEmptyBottle_IsAcceptedWithoutInventingContents()
        {
            var workspace = Create(CarriedBottle());
            Assert.That(Item(workspace, "bottle").Composition, Is.Not.Null);
            Assert.That(Item(workspace, "bottle").Composition.Total, Is.Zero);
            Assert.That(Item(workspace, "bottle").Composition.Capacity, Is.EqualTo(15000ul));
        }

        [Test]
        public void ContainerQuantitiesAboveFloatIntegerPrecision_AreImportedExactly()
        {
            var seed = CarriedBottle(Amount(1, 16777217), Amount(3, 1073741823));
            seed.Items[2].ContentId = "online.large-bottle";
            var composition = Item(Create(seed), "bottle").Composition;
            Assert.That(composition.GetQuantity(1), Is.EqualTo(16777217ul));
            Assert.That(composition.GetQuantity(3), Is.EqualTo(1073741823ul));
            Assert.That(composition.Total, Is.EqualTo(1090519040ul));
            Assert.That(composition.Capacity, Is.EqualTo(2147483647ul));
        }

        [Test]
        public void TwoBottlesReferencingOneDtoState_ReceiveIndependentRuntimeCompositions()
        {
            var seed = CarriedBottle(Amount(1, 7));
            var second = OwnedItem("second", "online.bottle", ItemLocationKind.Backpack, "bag", -1, 1);
            second.ContainerState = seed.Items[2].ContainerState;
            Append(seed, second);
            var workspace = Create(seed);
            Assert.That(Item(workspace, "bottle").Composition, Is.Not.SameAs(Item(workspace, "second").Composition));
            second.ContainerState.Components[0].Quantity = 99;
            Assert.That(Item(workspace, "bottle").Composition.Total, Is.EqualTo(7ul));
            Assert.That(Item(workspace, "second").Composition.Total, Is.EqualTo(7ul));
        }

        [TestCase("missing-state")]
        [TestCase("schema-zero")]
        [TestCase("schema-two")]
        [TestCase("empty-shell")]
        [TestCase("missing-components")]
        [TestCase("null-component")]
        [TestCase("duplicate-key")]
        [TestCase("zero-quantity")]
        [TestCase("unknown-zero")]
        [TestCase("unknown-nine")]
        [TestCase("forbidden-fire")]
        [TestCase("over-capacity")]
        [TestCase("ulong-overflow")]
        [TestCase("too-many-components")]
        public void InvalidContainerState_IsRejectedAtomicallyWithoutDefaultingOrClamping(string defect)
        {
            var seed = CarriedBottle(Amount(1, 1));
            var bottle = seed.Items[2];
            switch (defect)
            {
                case "missing-state": bottle.ContainerState = null; break;
                case "schema-zero": bottle.ContainerState.SchemaVersion = 0; break;
                case "schema-two": bottle.ContainerState.SchemaVersion = 2; break;
                case "empty-shell": bottle.ContainerState = new OnlineOwnedContainerStateSnapshot(); break;
                case "missing-components": bottle.ContainerState.Components = null; break;
                case "null-component": bottle.ContainerState.Components[0] = null; break;
                case "duplicate-key": bottle.ContainerState.Components = new[] { Amount(1, 1), Amount(1, 2) }; break;
                case "zero-quantity": bottle.ContainerState.Components[0].Quantity = 0; break;
                case "unknown-zero": bottle.ContainerState.Components[0].MaterialKey = 0; break;
                case "unknown-nine": bottle.ContainerState.Components[0].MaterialKey = 9; break;
                case "forbidden-fire": bottle.ContainerState.Components[0].MaterialKey = 2; break;
                case "over-capacity": bottle.ContainerState.Components[0].Quantity = 15001; break;
                case "ulong-overflow": bottle.ContainerState.Components = new[] { Amount(1, ulong.MaxValue), Amount(3, 1) }; break;
                case "too-many-components": bottle.ContainerState.Components = new[]
                    { Amount(1, 1), Amount(2, 1), Amount(3, 1), Amount(4, 1), Amount(5, 1), Amount(6, 1), Amount(7, 1), Amount(8, 1), Amount(1, 1) }; break;
                default: Assert.Fail("未知测试缺陷"); break;
            }
            Reject(seed);
        }

        [TestCase(0)]
        [TestCase(1)]
        public void NonContainerCannotCarryContainerState_EvenAnExplicitEmptyState(int itemIndex)
        {
            var seed = (OnlineOwnedAssetSnapshot)Seed();
            seed.Items[itemIndex].ContainerState = Container();
            Reject(seed);
        }

        [Test]
        public void CarriedSolidMaterialWithoutExplicitQuantityState_IsRejectedInsteadOfImportingAnEmptyShell()
        {
            // 本切片只有容器组分合同，尚未声明固体物品的真实 Key/Quantity；
            // 合法背包位置不能让缺失数量的材料壳绕过资产验证，更不能默认补量。
            Assert.That(_catalog.TryGet("online.wand", out var wand), Is.True);
            Assert.That(_catalog.TryGet("online.fire", out var spell), Is.True);
            Assert.That(_catalog.TryGet("online.bag", out var bag), Is.True);
            var solid = Define("online.solid", ItemKind.Material);
            _catalog = Store(wand, spell, bag, solid).Catalog;
            var seed = (OnlineOwnedAssetSnapshot)Seed();
            Append(seed, OwnedItem("bag", "online.bag", ItemLocationKind.EquippedBackpack),
                OwnedItem("solid", "online.solid", ItemLocationKind.Backpack, "bag", -1));
            Reject(seed);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void SpareWandAndItsInstalledCard_AreReachableThroughTheRealCarriedBag(bool useTemplate)
        {
            var seed = (OnlineOwnedAssetSnapshot)Seed();
            var spare = OwnedItem("spare", "online.wand", ItemLocationKind.Backpack, "bag", -1, 2);
            spare.UseTemplateWandStats = useTemplate;
            if (!useTemplate) spare.WandStats = InstanceStats();
            Append(seed, OwnedItem("spare-card", "online.fire", ItemLocationKind.WandSlot, "spare"), spare,
                OwnedItem("bag", "online.bag", ItemLocationKind.EquippedBackpack));
            seed.Items[2].RemainingCharges = 11;
            var workspace = Create(seed);
            Assert.That(Item(workspace, "spare-card").RemainingCharges, Is.EqualTo(11));
            Assert.That(_catalog.TryGetWandStats(Item(workspace, "spare"), out var stats), Is.True);
            Assert.That(stats.MaxMana, Is.EqualTo(useTemplate ? 100f : 150f));
            Assert.That(((IWandLoadoutReadOnly)Property(workspace, "WandsReadOnly")).SelectedWandId, Is.EqualTo("wand"));
        }

        [Test]
        public void SelectedWandCannotBeABackpackSpareEvenWhenItsParentChainIsValid()
        {
            var seed = (OnlineOwnedAssetSnapshot)Seed();
            var spare = OwnedItem("spare", "online.wand", ItemLocationKind.Backpack, "bag", -1);
            spare.UseTemplateWandStats = true;
            Append(seed, spare, OwnedItem("bag", "online.bag", ItemLocationKind.EquippedBackpack));
            seed.SelectedWandInstanceId = "spare";
            Reject(seed);
        }

        [TestCase("online.bottle", 0)]
        [TestCase("online.bottle", 6)]
        [TestCase("online.throwable", 6)]
        public void EquippedTool_UsesTrustedUseKindAndKeepsItsPlayerEquipmentRoot(string contentId, int slot)
        {
            var seed = (OnlineOwnedAssetSnapshot)Seed();
            var tool = OwnedItem("tool", contentId, ItemLocationKind.EquippedTool, slot: slot);
            if (contentId == "online.bottle") tool.ContainerState = Container(Amount(1, 23));
            Append(seed, tool);
            var item = Item(Create(seed), "tool");
            Assert.That(item.ContentId, Is.EqualTo(contentId));
            if (contentId == "online.bottle") Assert.That(item.Composition.Total, Is.EqualTo(23ul));
        }

        [TestCase(-1)]
        [TestCase(7)]
        public void EquippedToolOutsideSevenTrustedSlots_IsRejected(int slot)
        {
            var seed = (OnlineOwnedAssetSnapshot)Seed();
            Append(seed, OwnedItem("tool", "online.throwable", ItemLocationKind.EquippedTool, slot: slot));
            Reject(seed);
        }

        [Test]
        public void ItemWithoutUseCapabilityCannotBecomeAnEquippedTool()
        {
            var seed = (OnlineOwnedAssetSnapshot)Seed();
            Append(seed, OwnedItem("tool", "online.ordinary", ItemLocationKind.EquippedTool));
            Reject(seed);
        }

        [Test]
        public void RotatedContainerHole_AllowsAnotherItemInTheActualUnoccupiedCell()
        {
            var seed = CarriedBottle();
            seed.Items[2].ContentId = "online.elbow";
            seed.Items[2].QuarterTurns = 1;
            // L形旋转后占(1,0)/(1,1)/(0,0)，(0,1)是空洞；包围框不能代替真实占格。
            Append(seed, OwnedItem("inside-hole", "online.ordinary", ItemLocationKind.Backpack, "bag", -1, 0, 1));
            Assert.That(Item(Create(seed), "inside-hole").ContentId, Is.EqualTo("online.ordinary"));
        }

        [TestCase("collision")]
        [TestCase("bounds")]
        [TestCase("missing-parent")]
        [TestCase("null-parent")]
        [TestCase("empty-parent")]
        [TestCase("wrong-parent-kind")]
        [TestCase("foreign-root")]
        [TestCase("bag-slot")]
        [TestCase("bag-grid")]
        [TestCase("backpack-slot")]
        [TestCase("wand-slot")]
        [TestCase("rotation")]
        [TestCase("duplicate-identity")]
        public void InvalidCarriedOwnershipOrGrid_IsRejectedBeforeAnyWorkspaceIsPublished(string defect)
        {
            var seed = CarriedBottle();
            switch (defect)
            {
                case "collision": Append(seed, OwnedItem("other", "online.ordinary", ItemLocationKind.Backpack, "bag", -1)); break;
                case "bounds": seed.Items[2].X = 8; break;
                case "missing-parent": seed.Items[2].ParentInstanceId = "missing"; break;
                case "null-parent": seed.Items[2].ParentInstanceId = null; break;
                case "empty-parent": seed.Items[2].ParentInstanceId = ""; break;
                case "wrong-parent-kind": seed.Items[2].ParentInstanceId = "wand"; break;
                case "foreign-root": seed.Items[3].ParentInstanceId = "other-player"; break;
                case "bag-slot": seed.Items[3].Slot = 1; break;
                case "bag-grid": seed.Items[3].X = 1; break;
                case "backpack-slot": seed.Items[2].Slot = 0; break;
                case "wand-slot": seed.Items[0].Slot = 3; break;
                case "rotation": seed.Items[2].QuarterTurns = 4; break;
                case "duplicate-identity": seed.Items[2].InstanceId = "wand"; break;
                default: Assert.Fail("未知测试缺陷"); break;
            }
            Reject(seed);
        }

        [TestCase(ItemLocationKind.Warehouse)]
        [TestCase(ItemLocationKind.WorldItem)]
        [TestCase(ItemLocationKind.ThrownContainer)]
        [TestCase(ItemLocationKind.SpellStorage)]
        public void NonCarriedRootLocations_AreRejectedRatherThanImportedAsPlayerEquipment(ItemLocationKind kind)
        {
            var seed = CarriedBottle();
            seed.Items[2].LocationKind = (int)kind;
            seed.Items[2].ParentInstanceId = "";
            seed.Items[2].Slot = kind == ItemLocationKind.SpellStorage ? 0 : -1;
            Reject(seed);
        }

        [TestCase(172)]
        [TestCase(4096)]
        public void LegalCarriedClosureBeyondOldFlatLimit_ImportsAllInstances(int count)
        {
            var seed = LargeCarriedClosure(count);
            var workspace = Create(seed);
            // 检查整批身份，避免导入器只保留首尾项却仍通过容量边界用例。
            for (int i = 0; i < count - 3; i++)
                Assert.That(((OnlineOwnedWorkspace)workspace).TryGetItem("cargo." + i, out _), Is.True, "cargo." + i);
            Assert.That(Item(workspace, "bag").ContentId, Is.EqualTo("online.large-bag"));
            Assert.That(Item(workspace, "card").RemainingCharges, Is.EqualTo(3));
        }

        [Test]
        public void ClosureOver4096Items_IsRejectedAtomically()
        {
            Reject(LargeCarriedClosure(4097));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void PresentationCaptureIncludesRealBagToolsSpareWandAndIndependentContainerStates(bool useTemplate)
        {
            var seed = CarriedBottle(Amount(1, 100), Amount(3, 50));
            seed.Items[2].X = 1; seed.Items[2].Y = 2; seed.Items[2].QuarterTurns = 1;
            var empty = OwnedItem("empty", "online.bottle", ItemLocationKind.Backpack, "bag", -1, 3, 2);
            empty.ContainerState = Container();
            var tool = OwnedItem("tool", "online.bottle", ItemLocationKind.EquippedTool, slot: 6);
            tool.ContainerState = Container(Amount(1, 9));
            var spare = OwnedItem("spare", "online.wand", ItemLocationKind.Backpack, "bag", -1, 5, 5);
            spare.UseTemplateWandStats = useTemplate;
            if (!useTemplate) spare.WandStats = InstanceStats();
            var spareCard = OwnedItem("spare-card", "online.fire", ItemLocationKind.WandSlot, "spare");
            spareCard.RemainingCharges = 7;
            Append(seed, empty, tool, spare, spareCard);
            var workspace = (OnlineOwnedWorkspace)Create(seed);
            Assert.That(workspace.TryCapturePresentation(7, 1, 40, out var first), Is.True);
            Assert.That(first.Assets.Items.Length, Is.EqualTo(8));
            Assert.That(first.Wands.Length, Is.EqualTo(2), "v11完整Owner读数包含装备杖与包内备用杖；备用杖不因此取得装备/施法资格。");
            foreach (string id in new[] { "wand", "spare" })
            {
                // 按真实身份逐根对照生产服务，不依赖Store枚举/数组顺序，不能用复制同一满蓝值凑够两条。
                var matches = Array.FindAll(first.Wands, value => value.InstanceId == id);
                Assert.That(matches.Length, Is.EqualTo(1), id + "必须恰有一份当前资源事实。");
                Assert.That(workspace.WandsReadOnly.TryGetState(id, out var actualState), Is.True, id);
                Assert.That(workspace.WandsReadOnly.TryGetProgram(id, out var actualProgram), Is.True, id);
                Assert.That(matches[0].ProgramRevision, Is.EqualTo(actualProgram.ProgramRevision).And.GreaterThan(0u));
                Assert.That(matches[0].CurrentMana, Is.EqualTo(actualState.CurrentMana));
                Assert.That(matches[0].CooldownRemaining, Is.EqualTo(actualState.CooldownRemaining));
            }
            Assert.That(first.Assets.SelectedWandInstanceId, Is.EqualTo("wand"));
            Assert.That(CapturedItem(first, "spare").LocationKind, Is.EqualTo((int)ItemLocationKind.Backpack));
            Assert.That(first.SpellUses.Length, Is.EqualTo(2), "包、瓶、工具和备用杖不能被当成法术资格项。");
            var bottle = CapturedItem(first, "bottle");
            Assert.That(bottle.ParentInstanceId, Is.EqualTo("bag"));
            Assert.That(bottle.LocationKind, Is.EqualTo((int)ItemLocationKind.Backpack));
            Assert.That(bottle.X, Is.EqualTo(1)); Assert.That(bottle.Y, Is.EqualTo(2)); Assert.That(bottle.QuarterTurns, Is.EqualTo(1));
            Assert.That(bottle.ContainerState.SchemaVersion, Is.EqualTo(1));
            Assert.That(bottle.ContainerState.Components.Length, Is.EqualTo(2));
            Assert.That(bottle.ContainerState.Components[0].Quantity, Is.EqualTo(100ul));
            Assert.That(bottle.ContainerState.Components[1].Quantity, Is.EqualTo(50ul));
            Assert.That(CapturedItem(first, "empty").ContainerState.Components, Is.Empty);
            Assert.That(CapturedItem(first, "bag").ContainerState, Is.Null);
            Assert.That(CapturedItem(first, "tool").Slot, Is.EqualTo(6));
            var capturedSpare = CapturedItem(first, "spare");
            Assert.That(capturedSpare.UseTemplateWandStats, Is.EqualTo(useTemplate));
            if (useTemplate) Assert.That(capturedSpare.WandStats, Is.Null);
            else Assert.That(capturedSpare.WandStats.MaxMana, Is.EqualTo(150f));
            Assert.That(workspace.TryCapturePresentation(7, 2, 41, out var second), Is.True);
            Assert.That(CapturedItem(second, "bottle").ContainerState, Is.Not.SameAs(bottle.ContainerState));
            bottle.ContainerState.Components[0].Quantity = 999;
            Assert.That(CapturedItem(second, "bottle").ContainerState.Components[0].Quantity, Is.EqualTo(100ul));
            Assert.That(Item(workspace, "bottle").Composition.GetQuantity(1), Is.EqualTo(100ul));
            workspace.Dispose();
            Assert.That(workspace.TryCapturePresentation(7, 3, 42, out _), Is.False);
        }

        [TestCase(172, false)]
        [TestCase(4096, false)]
        [TestCase(172, true)]
        [TestCase(4096, true)]
        public void PresentationCaptureKeepsEveryCarriedItemAndOnlyActualSpellUses(int count, bool spellCargo)
        {
            var seed = LargeCarriedClosure(count);
            if (spellCargo)
                for (int i = 3; i < seed.Items.Length; i++)
                { seed.Items[i].ContentId = "online.fire"; seed.Items[i].RemainingCharges = 3; }
            var workspace = (OnlineOwnedWorkspace)Create(seed);
            Assert.That(workspace.TryCapturePresentation(7, 1, 40, out var snapshot), Is.True);
            Assert.That(snapshot.Assets.Items.Length, Is.EqualTo(count));
            Assert.That(snapshot.SpellUses.Length, Is.EqualTo(spellCargo ? count - 2 : 1));
            Assert.That(snapshot.Wands.Length, Is.EqualTo(1));
            Assert.That(CapturedItem(snapshot, "cargo." + (count - 4)).ContentId,
                Is.EqualTo(spellCargo ? "online.fire" : "online.ordinary"));
        }

        [Test]
        public void PresentationCaptureRetainsExistingSpellStorageAfterAuthoritativeCardMove()
        {
            var workspace = (OnlineOwnedWorkspace)Create(Seed());
            Assert.That(workspace.WandsReadOnly.TryGetProgram("wand", out var program), Is.True);
            var receipt = workspace.ExecuteInventoryCommand(new OnlineInventoryCommandIntent
            {
                CommandId = 1, ConnectionEpoch = 7, Kind = OnlineInventoryCommandKind.PlaceCard,
                ItemId = "card", ExpectedInventoryVersion = workspace.InventoryVersion,
                SourceWandId = "wand", SourceProgramRevision = program.ProgramRevision, ToSpellStorage = true, TargetSlot = 2
            });
            Assert.That(receipt.Failure, Is.EqualTo(OnlineInventoryCommandFailure.None));
            Assert.That(workspace.TryCapturePresentation(7, 1, 40, out var snapshot), Is.True);
            var card = CapturedItem(snapshot, "card");
            Assert.That(card.LocationKind, Is.EqualTo((int)ItemLocationKind.SpellStorage));
            Assert.That(card.ParentInstanceId, Is.Empty); Assert.That(card.Slot, Is.EqualTo(2));
            Assert.That(snapshot.SpellUses.Length, Is.EqualTo(1));
        }

        [Test]
        public void SelectingBoundContainerKeepsLastWandAndAmountsBlocksCastingAndStillTicksMana()
        {
            var workspace = CreateToolWorkspace();
            var root = Execute(workspace, Request(workspace, CastResourceOperation.PrepareRoot, 1));
            Assert.That(root.Success, Is.True);
            Assert.That(Execute(workspace, Request(workspace, CastResourceOperation.Complete, 2, root.TicketId)).Success, Is.True);
            Assert.That(State(workspace).Mana, Is.EqualTo(80f));
            ObserveRootRelease(workspace, root.TicketId);
            var choice = ToolIntent(workspace, 1);
            Assert.That(workspace.ExecuteInventoryCommand(choice).Success, Is.True);
            Assert.That(workspace.WandsReadOnly.SelectedWandId, Is.EqualTo("wand"));
            Assert.That(workspace.WorldActionsAllowed, Is.False, "选择容器后OwnedCast不能把保留的最后法杖当作当前手持。");
            Assert.That(Execute(workspace, Request(workspace, CastResourceOperation.PrepareRoot, 3)).Success, Is.False);
            Step(workspace, .5f, 1); Assert.That(State(workspace).Mana, Is.EqualTo(85f), "工具模式仍推进已有法杖资源，不暂停回蓝。");
            Assert.That(Item(workspace, "card").RemainingCharges, Is.EqualTo(2));
            Assert.That(workspace.TryCapturePresentation(7, 1, 40, out var snapshot), Is.True);
            Assert.That(ToolChoice(snapshot), Is.EqualTo(7u));
            var tool = CapturedItem(snapshot, "tool");
            Assert.That(tool.LocationKind, Is.EqualTo((int)ItemLocationKind.EquippedTool)); Assert.That(tool.Slot, Is.EqualTo(6));
            Assert.That(tool.ContainerState.Components[0].Quantity, Is.EqualTo(17ul));
            Assert.That(snapshot.Assets.SelectedWandInstanceId, Is.EqualTo("wand"));
        }

        [Test]
        public void ToolChoiceUsesReceiptDedupPersistsAcrossEpochAndClearsWhenReselectingSameWand()
        {
            var workspace = CreateToolWorkspace(); var intent = ToolIntent(workspace, 1);
            Assert.That(workspace.ExecuteInventoryCommand(intent).Success, Is.True);
            Assert.That(workspace.TryCapturePresentation(7, 1, 40, out var selected), Is.True);
            Assert.That(workspace.ExecuteInventoryCommand(intent).Success, Is.True);
            Assert.That(workspace.TryCapturePresentation(8, 1, 41, out var reconnected), Is.True);
            Assert.That(ToolChoice(reconnected), Is.EqualTo(7u));
            Assert.That(reconnected.SelectionVersion, Is.EqualTo(selected.SelectionVersion), "重发与同DS重连都不能重置或重复推进选择版本。");
            var wand = new OnlineInventoryCommandIntent { CommandId = 1, ConnectionEpoch = 8,
                ExpectedInventoryVersion = workspace.InventoryVersion, Kind = OnlineInventoryCommandKind.SelectWand, ItemId = "wand", TargetSlot = 0 };
            Assert.That(workspace.ExecuteInventoryCommand(wand).Success, Is.True);
            Assert.That(workspace.TryCapturePresentation(8, 2, 42, out var cleared), Is.True);
            Assert.That(ToolChoice(cleared), Is.Zero); Assert.That(cleared.SelectionVersion, Is.GreaterThan(selected.SelectionVersion));
            Assert.That(workspace.WorldActionsAllowed, Is.True);
            Assert.That(workspace.WandsReadOnly.SelectedWandId, Is.EqualTo("wand"));
        }

        [Test]
        public void BusyToolSelectionCannotBecomeSuccessfulWhenTheSameCommandIsRetriedLater()
        {
            var workspace = CreateToolWorkspace();
            var root = Execute(workspace, Request(workspace, CastResourceOperation.PrepareRoot, 1));
            Assert.That(root.Success, Is.True);
            var intent = ToolIntent(workspace, 1);
            Assert.That(workspace.ExecuteInventoryCommand(intent).Failure, Is.EqualTo(OnlineInventoryCommandFailure.Busy));
            Assert.That(Execute(workspace, Request(workspace, CastResourceOperation.Complete, 2, root.TicketId)).Success, Is.True);
            ObserveRootRelease(workspace, root.TicketId);
            Assert.That(workspace.ExecuteInventoryCommand(intent).Failure, Is.EqualTo(OnlineInventoryCommandFailure.Busy));
            Assert.That(workspace.TryCapturePresentation(7, 1, 40, out var snapshot), Is.True);
            Assert.That(ToolChoice(snapshot), Is.Zero);
            Assert.That(workspace.ExecuteInventoryCommand(ToolIntent(workspace, 2)).Success, Is.True);
        }

        [TestCase("wrong-id")]
        [TestCase("wrong-slot")]
        [TestCase("slot-outside")]
        [TestCase("noncontainer")]
        [TestCase("bag-container")]
        [TestCase("stale-version")]
        public void ToolSelectionRequiresThisActorsExactUsableToolAndCurrentInventory(string defect)
        {
            var seed = CarriedBottle();
            var tool = OwnedItem("tool", "online.bottle", ItemLocationKind.EquippedTool, slot: 6); tool.ContainerState = Container();
            Append(seed, tool, OwnedItem("throwable", "online.throwable", ItemLocationKind.EquippedTool, slot: 1));
            var workspace = (OnlineOwnedWorkspace)Create(seed); var intent = ToolIntent(workspace, 1);
            switch (defect)
            {
                case "wrong-id": intent.ItemId = "foreign"; break;
                case "wrong-slot": intent.TargetSlot = 5; break;
                case "slot-outside": intent.TargetSlot = 7; break;
                case "noncontainer": intent.ItemId = "throwable"; intent.TargetSlot = 1; break;
                case "bag-container": intent.ItemId = "bottle"; break;
                case "stale-version": intent.ExpectedInventoryVersion++; break;
                default: Assert.Fail("未知测试缺陷"); break;
            }
            if (defect == "noncontainer")
            {
                // 通用Item在v11已按原UseKind允许手持选择；选中不等于新增了普通物品投掷或容器操作。
                var receipt = workspace.ExecuteInventoryCommand(intent);
                Assert.That(receipt.Success, Is.True);
                Assert.That(ToolChoice(BindingSnapshot(workspace)), Is.EqualTo(2u));
                Assert.That(workspace.WorldActionsAllowed, Is.False, "手持普通工具也不能暗中使用最后法杖施法。");
                uint inventory = workspace.InventoryVersion, selection = workspace.SelectionVersion;
                var before = BindingSnapshot(workspace);
                ulong bottleAmount = Item(workspace, "bottle").Composition.Total;
                ulong toolAmount = Item(workspace, "tool").Composition.Total;
                int charges = Item(workspace, "card").RemainingCharges;
                Assert.That(workspace.ExecuteInventoryCommand(intent).Success, Is.True, "相同选择只重放原收据。");
                Assert.That(workspace.SelectionVersion, Is.EqualTo(selection));

                workspace.ConfigureContainerActions(Track(ScriptableObject.CreateInstance<ContainerUseDefinition>()), "owned-container-world");
                using var materialWorld = new ContainerWorldProbe(500);
                using var throws = new OnlineThrowProbe(workspace);
                var collect = TransferIntent(workspace, 2, OnlineInventoryCommandKind.CollectMaterial, 1);
                collect.ItemId = "throwable"; collect.TargetSlot = 1;
                var pour = TransferIntent(workspace, 3, OnlineInventoryCommandKind.PourContainer, 1);
                pour.ItemId = "throwable"; pour.TargetSlot = 1;
                var launch = ThrowIntent(workspace, 4); launch.ItemId = "throwable"; launch.TargetSlot = 1;
                foreach (var result in new[] { Transfer(workspace, collect), Transfer(workspace, pour), Transfer(workspace, launch, ThrowContext()) })
                {
                    Assert.That(result.Failure, Is.EqualTo(OnlineInventoryCommandFailure.NotOwned),
                        "合法上下文下必须由容器类型/当前选择门拒绝，不借NotReady/限频掩盖。");
                    Assert.That(result.Transferred, Is.Zero);
                }
                var after = BindingSnapshot(workspace);
                Assert.That(workspace.InventoryVersion, Is.EqualTo(inventory));
                Assert.That(workspace.SelectionVersion, Is.EqualTo(selection));
                Assert.That(after.Assets.Gold, Is.EqualTo(before.Assets.Gold));
                Assert.That(Item(workspace, "bottle").Composition.Total, Is.EqualTo(bottleAmount));
                Assert.That(Item(workspace, "tool").Composition.Total, Is.EqualTo(toolAmount));
                Assert.That(Item(workspace, "card").RemainingCharges, Is.EqualTo(charges));
                Assert.That(Item(workspace, "throwable").Composition, Is.Null);
                AssertBindingLocation(workspace, "throwable", ItemLocationKind.EquippedTool, "player", 1);
                Assert.That(materialWorld.Quantity, Is.EqualTo(500ul));
                Assert.That(materialWorld.Reserves, Is.Zero); Assert.That(materialWorld.Commits, Is.Zero);
                Assert.That(throws.Prepares, Is.Zero); Assert.That(throws.Launches, Is.Zero);
                return;
            }
            Assert.That(workspace.ExecuteInventoryCommand(intent).Success, Is.False);
            Assert.That(workspace.WorldActionsAllowed, Is.True);
        }

        [Test]
        public void BindingBackpackBottleMovesTheRealInstanceToEquipmentWithoutSelectingIt()
        {
            var workspace = (OnlineOwnedWorkspace)Create(CarriedBottle(Amount(1, 100), Amount(3, 50)));
            var original = Item(workspace, "bottle"); uint version = workspace.InventoryVersion, selection = workspace.SelectionVersion;
            var receipt = workspace.ExecuteInventoryCommand(BindIntent(workspace, 1, "bottle", 6));
            Assert.That(receipt.Success, Is.True);
            Assert.That(receipt.InventoryVersion, Is.GreaterThan(version)); Assert.That(receipt.Transferred, Is.Zero);
            Assert.That(receipt.TransferFailure, Is.EqualTo(MaterialTransferFailure.None));
            AssertBindingLocation(workspace, "bottle", ItemLocationKind.EquippedTool, "player", 6);
            Assert.That(Item(workspace, "bottle"), Is.SameAs(original), "绑定是同一真实实例的位置事务，不新建瓶或组分。");
            Assert.That(original.Composition.GetQuantity(1), Is.EqualTo(100ul)); Assert.That(original.Composition.GetQuantity(3), Is.EqualTo(50ul));
            var snapshot = BindingSnapshot(workspace);
            Assert.That(snapshot.Assets.Items.Length, Is.EqualTo(4)); Assert.That(ToolChoice(snapshot), Is.Zero);
            Assert.That(workspace.SelectionVersion, Is.EqualTo(selection)); Assert.That(workspace.WorldActionsAllowed, Is.True);
            Assert.That(workspace.WandsReadOnly.SelectedWandId, Is.EqualTo("wand"));
            // 网络使用InventoryVersion，不能要求它恰好等于内部初始为0的ToolConfigurationVersion。
            Assert.That(workspace.ExecuteInventoryCommand(new OnlineInventoryCommandIntent
            { CommandId = 2, ConnectionEpoch = 7, Kind = OnlineInventoryCommandKind.SelectTool,
                ExpectedInventoryVersion = workspace.InventoryVersion, ItemId = "bottle", TargetSlot = 6 }).Success, Is.True);
            Assert.That(ToolChoice(BindingSnapshot(workspace)), Is.EqualTo(7u));
        }

        [Test]
        public void BindingOverOccupiedToolUsesTheSpaceFreedByIncomingBottleEvenWhenTheBackpackIsFull()
        {
            var workspace = (OnlineOwnedWorkspace)Create(BindingSeed(fullBackpack: true));
            uint selection = workspace.SelectionVersion;
            Assert.That(workspace.ExecuteInventoryCommand(BindIntent(workspace, 1, "bottle", 6)).Success, Is.True);
            AssertBindingLocation(workspace, "bottle", ItemLocationKind.EquippedTool, "player", 6);
            AssertBindingLocation(workspace, "tool", ItemLocationKind.Backpack, "bag", -1);
            var outgoing = BindingProfile(workspace).Items;
            Assert.That(outgoing.TryGetLocation("tool", out var location), Is.True);
            Assert.That(location.X, Is.Zero); Assert.That(location.Y, Is.Zero);
            Assert.That(Item(workspace, "bottle").Composition.Total, Is.EqualTo(1000ul)); Assert.That(Item(workspace, "tool").Composition.Total, Is.EqualTo(17ul));
            Assert.That(BindingSnapshot(workspace).Assets.Items.Length, Is.EqualTo(68));
            Assert.That(workspace.SelectionVersion, Is.EqualTo(selection)); Assert.That(ToolChoice(BindingSnapshot(workspace)), Is.Zero);
        }

        [Test]
        public void BindingRejectsAtomicallyWhenTheOutgoingToolsRealShapeCannotFitTheFreedCell()
        {
            // 8x8正式尺寸只空出入装备瓶的1格；旧L形容器需要3格，不能靠覆盖/复制旧物完成替换。
            var workspace = (OnlineOwnedWorkspace)Create(BindingSeed(fullBackpack: true, shapedOutgoing: true));
            var before = BindingSnapshot(workspace); uint version = workspace.InventoryVersion;
            var receipt = workspace.ExecuteInventoryCommand(BindIntent(workspace, 1, "bottle", 6));
            Assert.That(receipt.Failure, Is.EqualTo(OnlineInventoryCommandFailure.NoCapacity));
            Assert.That(workspace.InventoryVersion, Is.EqualTo(version)); Assert.That(workspace.SelectionVersion, Is.EqualTo(before.SelectionVersion));
            AssertBindingLocation(workspace, "bottle", ItemLocationKind.Backpack, "bag", -1);
            AssertBindingLocation(workspace, "tool", ItemLocationKind.EquippedTool, "player", 6);
            Assert.That(Item(workspace, "bottle").Composition.Total, Is.EqualTo(1000ul)); Assert.That(Item(workspace, "tool").Composition.Total, Is.EqualTo(17ul));
            Assert.That(BindingSnapshot(workspace).Assets.Items.Length, Is.EqualTo(before.Assets.Items.Length));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MovingAnEquippedBottleSwapsRealSlotsAndKeepsSelectionOnItsOriginalSlot(bool selectSourceSlot)
        {
            var seed = BindingSeed(); var other = OwnedItem("other", "online.bottle", ItemLocationKind.EquippedTool, slot: 1);
            other.ContainerState = Container(Amount(3, 29)); Append(seed, other);
            var workspace = (OnlineOwnedWorkspace)Create(seed);
            var select = ToolIntent(workspace, 1); if (selectSourceSlot) { select.ItemId = "other"; select.TargetSlot = 1; }
            Assert.That(workspace.ExecuteInventoryCommand(select).Success, Is.True); uint selection = workspace.SelectionVersion;
            Assert.That(workspace.ExecuteInventoryCommand(BindIntent(workspace, 2, "other", 6)).Success, Is.True);
            AssertBindingLocation(workspace, "other", ItemLocationKind.EquippedTool, "player", 6);
            AssertBindingLocation(workspace, "tool", ItemLocationKind.EquippedTool, "player", 1);
            var snapshot = BindingSnapshot(workspace);
            Assert.That(ToolChoice(snapshot), Is.EqualTo(selectSourceSlot ? 2u : 7u));
            Assert.That(workspace.SelectionVersion, Is.EqualTo(selection + 1)); Assert.That(workspace.WorldActionsAllowed, Is.False);
            Assert.That(snapshot.Assets.SelectedWandInstanceId, Is.EqualTo("wand"));
            Assert.That(Item(workspace, "other").Composition.GetQuantity(3), Is.EqualTo(29ul)); Assert.That(Item(workspace, "tool").Composition.Total, Is.EqualTo(17ul));
            // 继续以当前槽的新实例操作，证明内部选择ID没有追着入装备实例跳槽，或留着已换走的旧ID。
            ConfigureBindingContainerActions(workspace); Step(workspace, .1f, 1);
            using (var world = new ContainerWorldProbe(10000))
            {
                var collect = TransferIntent(workspace, 3, OnlineInventoryCommandKind.CollectMaterial, 1);
                collect.ItemId = selectSourceSlot ? "tool" : "other"; collect.TargetSlot = selectSourceSlot ? 1 : 6;
                Assert.That(Transfer(workspace, collect).Success, Is.True);
            }
        }

        [Test]
        public void ReplacingTheSelectedSlotKeepsItsIndexUpdatesSelectionVersionAndClearsTransferBudget()
        {
            var workspace = (OnlineOwnedWorkspace)Create(BindingSeed()); ConfigureBindingContainerActions(workspace);
            Assert.That(workspace.ExecuteInventoryCommand(ToolIntent(workspace, 1)).Success, Is.True); Step(workspace, .1f, 1);
            uint selection = workspace.SelectionVersion;
            Assert.That(workspace.ExecuteInventoryCommand(BindIntent(workspace, 2, "bottle", 6)).Success, Is.True);
            Assert.That(ToolChoice(BindingSnapshot(workspace)), Is.EqualTo(7u)); Assert.That(workspace.SelectionVersion, Is.EqualTo(selection + 1));
            using (var world = new ContainerWorldProbe(10000))
            {
                var collect = TransferIntent(workspace, 3, OnlineInventoryCommandKind.CollectMaterial, 1); collect.ItemId = "bottle";
                Assert.That(Transfer(workspace, collect).Failure, Is.EqualTo(OnlineInventoryCommandFailure.RateLimited));
                Assert.That(world.RequestIds, Is.Empty);
                Step(workspace, .1f, 2); collect.CommandId = 4; collect.TransferBudget = 800;
                Assert.That(Transfer(workspace, collect).Success, Is.True);
                Assert.That(Item(workspace, "bottle").Composition.Total, Is.EqualTo(1800ul)); Assert.That(Item(workspace, "tool").Composition.Total, Is.EqualTo(17ul));
            }
        }

        [Test]
        public void BindingAnUnselectedSlotKeepsTheCurrentSelectionAndItsAvailableTransferBudget()
        {
            var workspace = (OnlineOwnedWorkspace)Create(BindingSeed()); ConfigureBindingContainerActions(workspace);
            Assert.That(workspace.ExecuteInventoryCommand(ToolIntent(workspace, 1)).Success, Is.True); Step(workspace, .1f, 1);
            uint selection = workspace.SelectionVersion;
            Assert.That(workspace.ExecuteInventoryCommand(BindIntent(workspace, 2, "bottle", 1)).Success, Is.True);
            Assert.That(ToolChoice(BindingSnapshot(workspace)), Is.EqualTo(7u)); Assert.That(workspace.SelectionVersion, Is.EqualTo(selection));
            using (var world = new ContainerWorldProbe(10000))
            {
                Assert.That(Transfer(workspace, TransferIntent(workspace, 3, OnlineInventoryCommandKind.CollectMaterial, 800)).Success, Is.True);
                Assert.That(Item(workspace, "tool").Composition.Total, Is.EqualTo(817ul)); Assert.That(Item(workspace, "bottle").Composition.Total, Is.EqualTo(1000ul));
            }
        }

        [Test]
        public void BindingUsesTheExistingReceiptWindowAndEpochWithoutDuplicatingOrReselectingTheItem()
        {
            var workspace = (OnlineOwnedWorkspace)Create(CarriedBottle(Amount(1, 100))); var intent = BindIntent(workspace, 1, "bottle", 6);
            var first = workspace.ExecuteInventoryCommand(intent); Assert.That(first.Success, Is.True); uint version = workspace.InventoryVersion;
            Assert.That(workspace.ExecuteInventoryCommand(intent).InventoryVersion, Is.EqualTo(first.InventoryVersion));
            Assert.That(workspace.ExecuteInventoryCommand(intent).Success, Is.True);
            var conflict = intent; conflict.TargetSlot = 5;
            Assert.That(workspace.ExecuteInventoryCommand(conflict).Failure, Is.EqualTo(OnlineInventoryCommandFailure.CommandConflict));
            var reconnect = BindIntent(workspace, 1, "bottle", 6); reconnect.ConnectionEpoch = 8;
            Assert.That(workspace.ExecuteInventoryCommand(reconnect).Success, Is.True);
            Assert.That(workspace.InventoryVersion, Is.EqualTo(version), "绑定到既有同槽是幂等无变化，不重复移动或自动选中。");
            intent.CommandId = 99; intent.ExpectedInventoryVersion = version;
            Assert.That(workspace.ExecuteInventoryCommand(intent).Failure, Is.EqualTo(OnlineInventoryCommandFailure.StaleCommand));
            AssertBindingLocation(workspace, "bottle", ItemLocationKind.EquippedTool, "player", 6);
            Assert.That(workspace.TryCapturePresentation(8, 1, 40, out var snapshot), Is.True);
            Assert.That(ToolChoice(snapshot), Is.Zero); Assert.That(Item(workspace, "bottle").Composition.Total, Is.EqualTo(100ul));
        }

        [TestCase("noncontainer")]
        [TestCase("warehouse")]
        [TestCase("foreign-owner")]
        [TestCase("unknown")]
        [TestCase("stale")]
        [TestCase("slot-outside")]
        [TestCase("frozen")]
        public void BindingRequiresThisPlayersCarriedContainerCurrentVersionAndWritableWorkspace(string defect)
        {
            var workspace = (OnlineOwnedWorkspace)Create(CarriedBottle(Amount(1, 100))); var profile = BindingProfile(workspace);
            if (defect == "warehouse") Assert.That(profile.Items.TryMove("bottle", ItemLocation.Warehouse).Success, Is.True);
            if (defect == "foreign-owner") Assert.That(profile.Items.TryMove("bottle", new ItemLocation(ItemLocationKind.EquippedTool, "other-player", 0, 0, 0, 0)).Success, Is.True);
            if (defect == "frozen") typeof(SinglePlayerProfile).GetProperty("RaidActionsFrozen", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(profile, true);
            var intent = BindIntent(workspace, 1, "bottle", 6);
            if (defect == "noncontainer") intent.ItemId = "card";
            if (defect == "unknown") intent.ItemId = "foreign-missing";
            if (defect == "stale") intent.ExpectedInventoryVersion++;
            if (defect == "slot-outside") intent.TargetSlot = 7;
            uint version = workspace.InventoryVersion, selection = workspace.SelectionVersion;
            Assert.That(profile.Items.TryGetLocation("bottle", out var original), Is.True);
            Assert.That(workspace.ExecuteInventoryCommand(intent).Success, Is.False);
            Assert.That(workspace.InventoryVersion, Is.EqualTo(version)); Assert.That(workspace.SelectionVersion, Is.EqualTo(selection));
            Assert.That(profile.Items.TryGetLocation("bottle", out var unchanged), Is.True); Assert.That(unchanged, Is.EqualTo(original));
            Assert.That(Item(workspace, "bottle").Composition.Total, Is.EqualTo(100ul));
        }

        [Test]
        public void CollectThenPourUsingTheSameConfirmedVersionRequiresANewOwnerVersionBeforeThePourCanCommit()
        {
            var workspace = CreateContainerActionWorkspace();
            using (var world = new ContainerWorldProbe(10000))
            {
                Step(workspace, .1f, 1);
                // 两路输入可以各按10Hz提出意图，但同一份已确认版本不能同时授权两笔写入。
                // 不预测未来版本，也不把迟到的H静默rebase成新的资产请求。
                var collect = TransferIntent(workspace, 2, OnlineInventoryCommandKind.CollectMaterial, 800);
                var pour = TransferIntent(workspace, 3, OnlineInventoryCommandKind.PourContainer, 100);
                Assert.That(pour.ExpectedInventoryVersion, Is.EqualTo(collect.ExpectedInventoryVersion));
                var collected = Transfer(workspace, collect);
                Assert.That(collected.Success, Is.True); Assert.That(collected.InventoryVersion, Is.GreaterThan(collect.ExpectedInventoryVersion));
                Assert.That(Item(workspace, "tool").Composition.Total, Is.EqualTo(817ul)); Assert.That(world.Quantity, Is.EqualTo(9200ul));
                var stale = Transfer(workspace, pour);
                Assert.That(stale.Failure, Is.EqualTo(OnlineInventoryCommandFailure.StaleVersion));
                Assert.That(stale.TransferFailure, Is.EqualTo(MaterialTransferFailure.None)); Assert.That(stale.Transferred, Is.Zero);
                Assert.That(Item(workspace, "tool").Composition.Total, Is.EqualTo(817ul)); Assert.That(world.Quantity, Is.EqualTo(9200ul));
                Assert.That(world.Reserves, Is.EqualTo(1)); Assert.That(world.Commits, Is.EqualTo(1));
                Assert.That(workspace.TryCapturePresentation(7, 1, 40, out var confirmedOwner), Is.True);
                pour.CommandId = 4; pour.ExpectedInventoryVersion = confirmedOwner.Assets.InventoryVersion;
                var poured = Transfer(workspace, pour);
                Assert.That(poured.Success, Is.True); Assert.That(poured.Transferred, Is.EqualTo(100ul));
                Assert.That(Item(workspace, "tool").Composition.Total, Is.EqualTo(717ul)); Assert.That(world.Quantity, Is.EqualTo(9300ul));
                Assert.That(world.Reserves, Is.EqualTo(2)); Assert.That(world.Commits, Is.EqualTo(2));
                Assert.That(world.Quantity + Item(workspace, "tool").Composition.Total, Is.EqualTo(10017ul));
            }
        }

        [TestCase("source-wand")]
        [TestCase("target-wand")]
        [TestCase("source-revision")]
        [TestCase("target-revision")]
        [TestCase("storage")]
        [TestCase("budget")]
        [TestCase("aim-origin")]
        [TestCase("aim-direction")]
        public void BindingRejectsUnrelatedWireFieldsInsteadOfSilentlyIgnoringThem(string extra)
        {
            var workspace = (OnlineOwnedWorkspace)Create(CarriedBottle()); var intent = BindIntent(workspace, 1, "bottle", 6);
            switch (extra)
            {
                case "source-wand": intent.SourceWandId = "wand"; break;
                case "target-wand": intent.TargetWandId = "wand"; break;
                case "source-revision": intent.SourceProgramRevision = 1; break;
                case "target-revision": intent.TargetProgramRevision = 1; break;
                case "storage": intent.ToSpellStorage = true; break;
                case "budget": intent.TransferBudget = 1; break;
                case "aim-origin": intent.AimOrigin.x = .00000001f; break;
                case "aim-direction": intent.AimDirection = Vector3.forward; break;
            }
            uint version = workspace.InventoryVersion;
            Assert.That(workspace.ExecuteInventoryCommand(intent).Failure, Is.EqualTo(OnlineInventoryCommandFailure.InvalidInput));
            Assert.That(workspace.InventoryVersion, Is.EqualTo(version)); AssertBindingLocation(workspace, "bottle", ItemLocationKind.Backpack, "bag", -1);
        }

        [Test]
        public void BusyBindingReceiptCannotTurnSuccessfulAfterTheOriginalCastFinishes()
        {
            var workspace = (OnlineOwnedWorkspace)Create(CarriedBottle());
            var root = Execute(workspace, Request(workspace, CastResourceOperation.PrepareRoot, 1)); Assert.That(root.Success, Is.True);
            var intent = BindIntent(workspace, 1, "bottle", 6);
            Assert.That(workspace.ExecuteInventoryCommand(intent).Failure, Is.EqualTo(OnlineInventoryCommandFailure.Busy));
            Assert.That(Execute(workspace, Request(workspace, CastResourceOperation.Complete, 2, root.TicketId)).Success, Is.True);
            ObserveRootRelease(workspace, root.TicketId);
            Assert.That(workspace.ExecuteInventoryCommand(intent).Failure, Is.EqualTo(OnlineInventoryCommandFailure.Busy));
            AssertBindingLocation(workspace, "bottle", ItemLocationKind.Backpack, "bag", -1);
            Assert.That(workspace.ExecuteInventoryCommand(BindIntent(workspace, 2, "bottle", 6)).Success, Is.True);
        }

        [Test]
        public void MovingTheSelectedToolIntoAnEmptySlotUsesOriginalWandFallbackAndPublishesAnInstallableSource()
        {
            var seed = BindingSeed(); var secondWand = OwnedItem("second-wand", "online.wand", ItemLocationKind.EquippedWand, slot: 1);
            secondWand.UseTemplateWandStats = true; Append(seed, secondWand); seed.SelectedWandInstanceId = "second-wand";
            var workspace = (OnlineOwnedWorkspace)Create(seed); ConfigureBindingContainerActions(workspace);
            Assert.That(workspace.ExecuteInventoryCommand(ToolIntent(workspace, 1)).Success, Is.True); Step(workspace, .1f, 1);
            Assert.That(workspace.WandsReadOnly.SelectedWandId, Is.EqualTo("second-wand"));
            Assert.That(workspace.TryCapturePresentation(7, 1, 40, out var before), Is.True);
            var identity = new OnlineInventoryPresentationIdentity("run", "player", 1, "scope", Hash, 7, "test-world");
            Assert.That(OnlineInventoryPresentationSource.TryCreate(_catalog, identity, before, out var source, out var error), Is.True, error);
            using (source)
            {
                Assert.That(workspace.ExecuteInventoryCommand(BindIntent(workspace, 2, "tool", 1)).Success, Is.True);
                AssertBindingLocation(workspace, "tool", ItemLocationKind.EquippedTool, "player", 1);
                Assert.That(workspace.TryCapturePresentation(7, 2, 41, out var after), Is.True);
                // 原SelectFallback扫描装备杖0..2，故应选第一根wand，而不是跟瓶跳至新工具槽或保留最后second-wand。
                Assert.That(ToolChoice(after), Is.Zero); Assert.That(after.Assets.SelectedWandInstanceId, Is.EqualTo("wand"));
                Assert.That(after.SelectionVersion, Is.EqualTo(before.SelectionVersion + 1)); Assert.That(workspace.WorldActionsAllowed, Is.True);
                Assert.That((ulong)typeof(OnlineOwnedWorkspace).GetField("_collectBudget", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(workspace), Is.Zero);
                Assert.That((ulong)typeof(OnlineOwnedWorkspace).GetField("_pourBudget", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(workspace), Is.Zero);
                Assert.That(source.TryInstall(after, out error), Is.True, error);
                Assert.That(source.ToolbeltReadOnly.SelectedSlot, Is.Zero); Assert.That(source.ToolbeltReadOnly.SelectedItemId, Is.EqualTo("wand"));
            }
        }

        private static OnlineInventoryCommandIntent BindIntent(OnlineOwnedWorkspace workspace, ulong command, string id, int slot)
            => new OnlineInventoryCommandIntent { CommandId = command, ConnectionEpoch = 7,
                ExpectedInventoryVersion = workspace.InventoryVersion, Kind = OnlineInventoryCommandKind.BindTool, ItemId = id, TargetSlot = slot };
        private static SinglePlayerProfile BindingProfile(OnlineOwnedWorkspace workspace)
            => (SinglePlayerProfile)typeof(OnlineOwnedWorkspace).GetField("_profile", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(workspace);
        private static OnlineInventoryPresentationSnapshot BindingSnapshot(OnlineOwnedWorkspace workspace)
        { Assert.That(workspace.TryCapturePresentation(7, 1, 40, out var snapshot), Is.True); return snapshot; }
        private static void AssertBindingLocation(OnlineOwnedWorkspace workspace, string id, ItemLocationKind kind, string owner, int slot)
        {
            Assert.That(BindingProfile(workspace).Items.TryGetLocation(id, out var location), Is.True);
            Assert.That(location.Kind, Is.EqualTo(kind)); Assert.That(location.OwnerId, Is.EqualTo(owner)); Assert.That(location.SlotIndex, Is.EqualTo(slot));
        }
        private OnlineOwnedAssetSnapshot BindingSeed(bool fullBackpack = false, bool shapedOutgoing = false)
        {
            var seed = CarriedBottle(Amount(1, 1000));
            var tool = OwnedItem("tool", shapedOutgoing ? "online.elbow" : "online.bottle", ItemLocationKind.EquippedTool, slot: 6);
            tool.ContainerState = Container(Amount(1, 17)); Append(seed, tool);
            if (fullBackpack)
                for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
                    if (x != 0 || y != 0) Append(seed, OwnedItem("binding.filler." + x + "." + y, "online.ordinary", ItemLocationKind.Backpack, "bag", -1, x, y));
            return seed;
        }
        private void ConfigureBindingContainerActions(OnlineOwnedWorkspace workspace)
        {
            var definition = Track(ScriptableObject.CreateInstance<ContainerUseDefinition>()); var data = new SerializedObject(definition);
            data.FindProperty("_collectionRate").floatValue = 8000; data.FindProperty("_collectionRadius").floatValue = .5f;
            data.FindProperty("_pourRate").floatValue = 2000; data.FindProperty("_depositRadius").floatValue = .2f;
            data.FindProperty("_gasPourMultiplier").floatValue = 4; data.FindProperty("_gasDepositRadius").floatValue = .8f;
            data.FindProperty("_maximumPourBatchGmu").intValue = 1024; data.ApplyModifiedPropertiesWithoutUndo();
            workspace.ConfigureContainerActions(definition, "owned-container-world");
        }

        private OnlineOwnedWorkspace CreateToolWorkspace()
        {
            var seed = (OnlineOwnedAssetSnapshot)Seed();
            var tool = OwnedItem("tool", "online.bottle", ItemLocationKind.EquippedTool, slot: 6);
            tool.ContainerState = Container(Amount(1, 17)); Append(seed, tool);
            return (OnlineOwnedWorkspace)Create(seed);
        }

        [Test]
        public void ContainerActionsUseRealReservationsAndKeepOwnerPlusWorldQuantitiesConserved()
        {
            var workspace = CreateContainerActionWorkspace();
            using (var world = new ContainerWorldProbe(10000))
            {
                Step(workspace, .1f, 1);
                var collected = Transfer(workspace, TransferIntent(workspace, 2, OnlineInventoryCommandKind.CollectMaterial, 800));
                Assert.That(collected.Success, Is.True); Assert.That(collected.Transferred, Is.EqualTo(800ul));
                Assert.That(collected.TransferFailure, Is.EqualTo(MaterialTransferFailure.None));
                Assert.That(Item(workspace, "tool").Composition.Total, Is.EqualTo(817ul));
                Assert.That(world.Quantity, Is.EqualTo(9200ul)); Assert.That(world.Reserves, Is.EqualTo(1)); Assert.That(world.Commits, Is.EqualTo(1));
                var poured = Transfer(workspace, TransferIntent(workspace, 3, OnlineInventoryCommandKind.PourContainer, 100));
                Assert.That(poured.Success, Is.True); Assert.That(poured.Transferred, Is.EqualTo(100ul));
                Assert.That(Item(workspace, "tool").Composition.Total, Is.EqualTo(717ul)); Assert.That(world.Quantity, Is.EqualTo(9300ul));
                Assert.That(world.Quantity + Item(workspace, "tool").Composition.Total, Is.EqualTo(10017ul));
                Assert.That(world.LastCollectRadius, Is.EqualTo(.5f)); Assert.That(world.LastPourRadius, Is.EqualTo(.2f));
                Assert.That(world.SourceIds.Count, Is.EqualTo(1), "采集Exchange携带来源；倾倒Exchange不冒充网络请求身份。");
            }
        }

        [TestCase("budget")]
        [TestCase("aim-origin")]
        [TestCase("aim-direction")]
        public void ContainerActionReceiptReplaysWithoutReexecutingAndRejectsChangedWireIntent(string conflict)
        {
            var workspace = CreateContainerActionWorkspace();
            using (var world = new ContainerWorldProbe(10000))
            {
                Step(workspace, .1f, 1);
                var intent = TransferIntent(workspace, 2, OnlineInventoryCommandKind.CollectMaterial, 800);
                var first = Transfer(workspace, intent); Assert.That(first.Success, Is.True);
                Assert.That((ulong)Property(workspace, "CollectedGmu"), Is.EqualTo(800ul), "只累计真实成功提交的采集GMU。");
                var movedAndFrozen = ContainerContext(); movedAndFrozen.CanAdvance = false; movedAndFrozen.ActorPosition = Vector3.one * 100;
                var replay = Transfer(workspace, intent, movedAndFrozen);
                Assert.That(replay.Success, Is.True); Assert.That(replay.Transferred, Is.EqualTo(800ul));
                Assert.That((ulong)Property(workspace, "CollectedGmu"), Is.EqualTo(800ul), "原ledger重放不能重复推进出局进度。");
                Assert.That(world.RequestIds.Count, Is.EqualTo(1), "相同wire意图重试只回原收据，Actor/context变化不能重执行。");
                if (conflict == "budget") intent.TransferBudget = 799;
                else if (conflict == "aim-origin") intent.AimOrigin.x = .00000001f;
                else intent.AimDirection.x = .00000001f;
                Assert.That(Transfer(workspace, intent).Failure, Is.EqualTo(OnlineInventoryCommandFailure.CommandConflict),
                    "向量分量必须精确比较，不能借Unity近似相等把新意图当作重试。");
                Assert.That(world.Quantity, Is.EqualTo(9200ul)); Assert.That(Item(workspace, "tool").Composition.Total, Is.EqualTo(817ul));
            }
        }

        [TestCase(OnlineInventoryCommandKind.CollectMaterial)]
        [TestCase(OnlineInventoryCommandKind.PourContainer)]
        public void GeometryRejectionCancelsRealReservationRollsBackWorldAndConsumesAttemptBudget(OnlineInventoryCommandKind kind)
        {
            var workspace = CreateContainerActionWorkspace();
            using (var world = new ContainerWorldProbe(10000) { GeometryFailure = true })
            {
                Step(workspace, .1f, 1); ulong budget = kind == OnlineInventoryCommandKind.CollectMaterial ? 800ul : 200ul;
                var rejected = Transfer(workspace, TransferIntent(workspace, 2, kind, budget));
                Assert.That(rejected.Success, Is.False); Assert.That(rejected.TransferFailure, Is.EqualTo(MaterialTransferFailure.GeometryRejected));
                Assert.That(rejected.Transferred, Is.Zero); Assert.That(world.Reserves, Is.EqualTo(1)); Assert.That(world.Commits, Is.Zero);
                Assert.That(world.Quantity, Is.EqualTo(10000ul)); Assert.That(Item(workspace, "tool").Composition.Total, Is.EqualTo(17ul));
                world.GeometryFailure = false;
                var noCredit = Transfer(workspace, TransferIntent(workspace, 3, kind, budget));
                Assert.That(noCredit.Failure, Is.EqualTo(OnlineInventoryCommandFailure.RateLimited));
                Assert.That(noCredit.TransferFailure, Is.EqualTo(MaterialTransferFailure.None)); Assert.That(world.Reserves, Is.EqualTo(1));
                Step(workspace, .1f, 2);
                Assert.That(Transfer(workspace, TransferIntent(workspace, 4, kind, budget)).Success, Is.True, "取消预留后下一个合法批次不应残留资源锁。");
                Assert.That(world.Quantity + Item(workspace, "tool").Composition.Total, Is.EqualTo(10017ul));
            }
        }

        [TestCase(OnlineInventoryCommandKind.CollectMaterial)]
        [TestCase(OnlineInventoryCommandKind.PourContainer)]
        public void MissingWorldHandlerPreservesBothSidesAndDoesNotAccumulateFailedBudget(OnlineInventoryCommandKind kind)
        {
            var workspace = CreateContainerActionWorkspace(); Step(workspace, .1f, 1);
            ulong budget = kind == OnlineInventoryCommandKind.CollectMaterial ? 800ul : 200ul;
            var failed = Transfer(workspace, TransferIntent(workspace, 2, kind, budget));
            Assert.That(failed.Success, Is.False); Assert.That(failed.TransferFailure, Is.EqualTo(MaterialTransferFailure.WorldUnavailable));
            Assert.That(failed.Transferred, Is.Zero); Assert.That(Item(workspace, "tool").Composition.Total, Is.EqualTo(17ul));
            var exhausted = Transfer(workspace, TransferIntent(workspace, 3, kind, budget));
            Assert.That(exhausted.Failure, Is.EqualTo(OnlineInventoryCommandFailure.RateLimited));
            Assert.That(exhausted.TransferFailure, Is.EqualTo(MaterialTransferFailure.None));
        }

        [Test]
        public void PreparationFailureConsumesAttemptBudgetWithoutCallingWorldAndReplaysTheOriginalFailure()
        {
            var workspace = CreateContainerActionWorkspace();
            using (var world = new ContainerWorldProbe(10000))
            {
                Step(workspace, .1f, 1);
                var intent = TransferIntent(workspace, 2, OnlineInventoryCommandKind.CollectMaterial, 800);
                var context = ContainerContext(); context.PreparationFailure = MaterialTransferFailure.NoMaterial;
                // DS未采到材料时没有合法目标；领域保留精确失败，不能把默认位置交给World进行转移。
                var rejected = Transfer(workspace, intent, context);
                Assert.That(rejected.Success, Is.False); Assert.That(rejected.Transferred, Is.Zero);
                Assert.That(rejected.TransferFailure, Is.EqualTo(MaterialTransferFailure.NoMaterial));
                Assert.That(world.RequestIds, Is.Empty); Assert.That(world.Reserves, Is.Zero);
                Assert.That(world.Quantity, Is.EqualTo(10000ul)); Assert.That(Item(workspace, "tool").Composition.Total, Is.EqualTo(17ul));
                var replay = Transfer(workspace, intent, ContainerContext());
                Assert.That(replay.Failure, Is.EqualTo(rejected.Failure)); Assert.That(replay.TransferFailure, Is.EqualTo(rejected.TransferFailure));
                Assert.That(replay.Transferred, Is.Zero); Assert.That(world.RequestIds, Is.Empty);
                var exhausted = Transfer(workspace, TransferIntent(workspace, 3, OnlineInventoryCommandKind.CollectMaterial, 800));
                Assert.That(exhausted.Failure, Is.EqualTo(OnlineInventoryCommandFailure.RateLimited));
                Assert.That(exhausted.TransferFailure, Is.EqualTo(MaterialTransferFailure.None)); Assert.That(world.RequestIds, Is.Empty);
            }
        }

        [TestCase("stale")]
        [TestCase("unselected")]
        [TestCase("wrong-owner")]
        [TestCase("frozen")]
        [TestCase("wrong-world")]
        public void InvalidContainerActionContextNeverTouchesTheWorldOrOwnedQuantity(string defect)
        {
            var workspace = CreateContainerActionWorkspace(defect != "unselected");
            using (var world = new ContainerWorldProbe(10000))
            {
                Step(workspace, .1f, 1);
                var intent = TransferIntent(workspace, 2, OnlineInventoryCommandKind.CollectMaterial, 800); var context = ContainerContext();
                if (defect == "stale") intent.ExpectedInventoryVersion++;
                else if (defect == "wrong-owner") intent.ItemId = "foreign-player-container";
                else if (defect == "frozen") context.CanAdvance = false;
                else if (defect == "wrong-world") context.WorldId = "different-world";
                Assert.That(Transfer(workspace, intent, context).Success, Is.False);
                Assert.That(world.RequestIds, Is.Empty); Assert.That(world.Quantity, Is.EqualTo(10000ul));
                Assert.That(Item(workspace, "tool").Composition.Total, Is.EqualTo(17ul));
            }
        }

        [Test]
        public void AuthorityTickBudgetIsBoundedAndDuplicateTickCannotRefillCollectOrPourCredit()
        {
            var workspace = CreateContainerActionWorkspace();
            using (var world = new ContainerWorldProbe(100000))
            {
                Step(workspace, 100, 1);
                Assert.That(Transfer(workspace, TransferIntent(workspace, 2, OnlineInventoryCommandKind.CollectMaterial, 4096)).Transferred, Is.EqualTo(4096ul));
                Assert.That(Transfer(workspace, TransferIntent(workspace, 3, OnlineInventoryCommandKind.PourContainer, 1024)).Transferred, Is.EqualTo(1024ul));
                Step(workspace, 100, 1);
                Assert.That(Transfer(workspace, TransferIntent(workspace, 4, OnlineInventoryCommandKind.CollectMaterial, 1)).Failure, Is.EqualTo(OnlineInventoryCommandFailure.RateLimited));
                Assert.That(Transfer(workspace, TransferIntent(workspace, 5, OnlineInventoryCommandKind.PourContainer, 1)).Failure, Is.EqualTo(OnlineInventoryCommandFailure.RateLimited));
                Step(workspace, .1f, 2);
                Assert.That(Transfer(workspace, TransferIntent(workspace, 6, OnlineInventoryCommandKind.CollectMaterial, 800)).Transferred, Is.EqualTo(800ul));
                Assert.That(Transfer(workspace, TransferIntent(workspace, 7, OnlineInventoryCommandKind.PourContainer, 201)).Failure, Is.EqualTo(OnlineInventoryCommandFailure.RateLimited));
                Assert.That(world.Quantity + Item(workspace, "tool").Composition.Total, Is.EqualTo(100017ul));
            }
        }

        [Test]
        public void ActualSelectionChangesClearBudgetWhileARepeatedSelectionReceiptDoesNot()
        {
            var workspace = CreateContainerActionWorkspace();
            using (var world = new ContainerWorldProbe(10000))
            {
                Step(workspace, .1f, 1);
                Assert.That(workspace.ExecuteInventoryCommand(ToolIntent(workspace, 1)).Success, Is.True);
                Assert.That(Transfer(workspace, TransferIntent(workspace, 2, OnlineInventoryCommandKind.CollectMaterial, 800)).Success, Is.True);
                Step(workspace, .1f, 2);
                var wand = new OnlineInventoryCommandIntent { CommandId = 3, ConnectionEpoch = 7, ExpectedInventoryVersion = workspace.InventoryVersion,
                    Kind = OnlineInventoryCommandKind.SelectWand, ItemId = "wand", TargetSlot = 0 };
                Assert.That(workspace.ExecuteInventoryCommand(wand).Success, Is.True);
                Assert.That(workspace.ExecuteInventoryCommand(ToolIntent(workspace, 4)).Success, Is.True);
                Assert.That(Transfer(workspace, TransferIntent(workspace, 5, OnlineInventoryCommandKind.CollectMaterial, 1)).Failure, Is.EqualTo(OnlineInventoryCommandFailure.RateLimited));
                Step(workspace, .1f, 3);
                Assert.That(Transfer(workspace, TransferIntent(workspace, 6, OnlineInventoryCommandKind.CollectMaterial, 800)).Success, Is.True);
            }
        }

        [Test]
        public void ReconnectingSameWorkspaceKeepsBudgetAndInternalTransferSourceInsteadOfGrantingCredit()
        {
            var workspace = CreateContainerActionWorkspace();
            using (var world = new ContainerWorldProbe(10000))
            {
                Step(workspace, .1f, 1);
                Assert.That(Transfer(workspace, TransferIntent(workspace, 2, OnlineInventoryCommandKind.CollectMaterial, 800)).Success, Is.True);
                var retry = TransferIntent(workspace, 1, OnlineInventoryCommandKind.CollectMaterial, 800); retry.ConnectionEpoch = 8;
                Assert.That(Transfer(workspace, retry).Failure, Is.EqualTo(OnlineInventoryCommandFailure.RateLimited));
                Step(workspace, .1f, 2); retry.CommandId = 2; retry.ExpectedInventoryVersion = workspace.InventoryVersion;
                Assert.That(Transfer(workspace, retry).Success, Is.True);
                Assert.That(world.SourceIds.Count, Is.EqualTo(2)); Assert.That(world.SourceIds[1], Is.EqualTo(world.SourceIds[0]));
                Assert.That(world.RequestIds[1], Is.GreaterThan(world.RequestIds[0]));
                var old = TransferIntent(workspace, 999, OnlineInventoryCommandKind.CollectMaterial, 1);
                Assert.That(Transfer(workspace, old).Failure, Is.EqualTo(OnlineInventoryCommandFailure.StaleCommand));
                Assert.That(world.SourceIds.Count, Is.EqualTo(2));
            }
        }

        [Test]
        public void GasPourUsesOriginalMultiplierAndGasDepositRadiusFromTrustedDefinition()
        {
            var workspace = CreateContainerActionWorkspace(gas: true);
            using (var world = new ContainerWorldProbe(10000, 7))
            {
                Step(workspace, .1f, 1);
                var result = Transfer(workspace, TransferIntent(workspace, 2, OnlineInventoryCommandKind.PourContainer, 800));
                Assert.That(result.Success, Is.True); Assert.That(result.Transferred, Is.EqualTo(800ul));
                Assert.That(world.LastPourRadius, Is.EqualTo(.8f)); Assert.That(Item(workspace, "tool").Composition.Total, Is.EqualTo(200ul));
                Assert.That(world.Quantity, Is.EqualTo(10800ul));
            }
        }

        [Test]
        public void OnlineThrowKeepsTheSameServerItemAndProjectsOnlyCarriedOwnerAssets()
        {
            var workspace = CreateOnlineThrowWorkspace(); var bottle = Item(workspace, "tool");
            uint version = workspace.InventoryVersion, selection = workspace.SelectionVersion;
            using var flight = new OnlineThrowProbe(workspace);
            Assert.That(Transfer(workspace, ThrowIntent(workspace, 2), ThrowContext()).Success, Is.True);
            Assert.That(flight.Prepares, Is.EqualTo(1)); Assert.That(flight.Launches, Is.EqualTo(1));
            Assert.That(Item(workspace, "tool"), Is.SameAs(bottle));
            Assert.That(bottle.Composition.Total, Is.EqualTo(17ul));
            Assert.That(ThrowStore(workspace).TryGetLocation("tool", out var location), Is.True);
            Assert.That(location.Kind, Is.EqualTo(ItemLocationKind.ThrownContainer));
            Assert.That(location.OwnerId, Is.EqualTo(flight.FlightId)); Assert.That(ThrowService(workspace).PendingFlights, Is.EqualTo(1));
            Assert.That(workspace.InventoryVersion, Is.GreaterThan(version));
            Assert.That(workspace.SelectionVersion, Is.GreaterThan(selection));
            Assert.That(workspace.TryCapturePresentation(7, 1, 1, out var snapshot), Is.True);
            Assert.That(ToolChoice(snapshot), Is.Zero);
            foreach (var item in snapshot.Assets.Items) Assert.That(item.InstanceId, Is.Not.EqualTo("tool"));
            var identity = new OnlineInventoryPresentationIdentity("run", "player", 1, "scope", Hash, 7, "test-world");
            Assert.That(OnlineInventoryPresentationSource.TryCreate(_catalog, identity, snapshot, out var source, out var error), Is.True, error);
            using (source) Assert.That(source.ReadOnly.TryGetView("tool", out _), Is.False);
        }

        [Test]
        public void OnlineThrowExactReceiptReplayDoesNotPrepareOrLaunchTheBottleAgain()
        {
            var workspace = CreateOnlineThrowWorkspace(); using var flight = new OnlineThrowProbe(workspace);
            var intent = ThrowIntent(workspace, 2); var first = Transfer(workspace, intent, ThrowContext());
            Assert.That(first.Success, Is.True); uint version = workspace.InventoryVersion;
            var replay = Transfer(workspace, intent, default);
            Assert.That(replay.Success, Is.True); Assert.That(replay.InventoryVersion, Is.EqualTo(first.InventoryVersion));
            Assert.That(workspace.InventoryVersion, Is.EqualTo(version));
            Assert.That(flight.Prepares, Is.EqualTo(1)); Assert.That(flight.Launches, Is.EqualTo(1));
            Assert.That(ThrowService(workspace).PendingFlights, Is.EqualTo(1));
        }

        [Test]
        public void TwoWandsFourContainersCanThrowAndSwitchThroughRealWireSnapshotsWithoutGhostSelection()
        {
            var seed = (OnlineOwnedAssetSnapshot)Seed("two-wands-four-containers");
            seed.RunId = new string('a', 32); seed.PlayerId = new string('b', 32);
            string firstWand = 1.ToString("x32"), secondWand = 3.ToString("x32");
            seed.Items[0].InstanceId = firstWand; seed.Items[1].InstanceId = 2.ToString("x32");
            seed.Items[1].ParentInstanceId = firstWand;
            var second = OwnedItem(secondWand, "online.wand", ItemLocationKind.EquippedWand, slot: 1);
            second.UseTemplateWandStats = true; Append(seed, second); seed.SelectedWandInstanceId = secondWand;
            for (int slot = 0; slot < 4; slot++)
            {
                var bottle = OwnedItem((slot + 4).ToString("x32"), "online.bottle", ItemLocationKind.EquippedTool, slot: slot);
                bottle.ContainerState = Container(Amount(1, (ulong)(17 + slot))); Append(seed, bottle);
            }
            var workspace = (OnlineOwnedWorkspace)Create(seed); ConfigureBindingContainerActions(workspace);
            Assert.That(_catalog.TryGet("online.bottle", out var definition), Is.True);
            var data = new SerializedObject(definition);
            data.FindProperty("_worldModel").objectReferenceValue = Track(new GameObject("Four real container proxy model"));
            data.ApplyModifiedPropertiesWithoutUndo();
            var identity = new OnlineInventoryPresentationIdentity(seed.RunId, seed.PlayerId, 1, seed.InventoryId, Hash, 7, "test-world");
            Assert.That(workspace.TryCapturePresentation(7, 1, 40, out var initial), Is.True);
            Assert.That(OnlineInventoryPresentationSource.TryCreate(_catalog, identity, WireSnapshot(initial), out var source, out var error), Is.True, error);
            using (source)
            using (var flight = new OnlineThrowProbe(workspace))
            {
                ulong command = 1, sequence = 1;
                for (int slot = 0; slot < 4; slot++)
                {
                    string id = (slot + 4).ToString("x32");
                    Assert.That(workspace.ExecuteInventoryCommand(new OnlineInventoryCommandIntent { CommandId = command++, ConnectionEpoch = 7,
                        ExpectedInventoryVersion = source.ReadOnly.Version, Kind = OnlineInventoryCommandKind.SelectTool,
                        ItemId = id, TargetSlot = slot }).Success, Is.True);
                    Assert.That(workspace.TryCapturePresentation(7, ++sequence, 41u + (uint)slot * 2, out var selected), Is.True);
                    Assert.That(source.TryInstall(WireSnapshot(selected), out error), Is.True, error);
                    Assert.That(source.ToolbeltReadOnly.SelectedItemId, Is.EqualTo(id));
                    uint selection = selected.SelectionVersion;
                    var throwing = new OnlineInventoryCommandIntent { CommandId = command++, ConnectionEpoch = 7,
                        ExpectedInventoryVersion = source.ReadOnly.Version, Kind = OnlineInventoryCommandKind.ThrowContainer,
                        ItemId = id, TargetSlot = slot, AimDirection = Vector3.forward };
                    var receipt = Transfer(workspace, throwing, ThrowContext()); Assert.That(receipt.Success, Is.True);
                    Assert.That(source.ToolbeltReadOnly.SelectedItemId, Is.EqualTo(id), "未安装完整Owner前不能因成功收据乐观伪造空手。");
                    Assert.That(workspace.TryCapturePresentation(7, ++sequence, 42u + (uint)slot * 2, out var after), Is.True);
                    Assert.That(after.SelectionVersion, Is.EqualTo(selection + 1));
                    Assert.That(source.TryInstall(WireSnapshot(after), out error), Is.True, error);
                    Assert.That(source.ToolbeltReadOnly.SelectedItemId, Is.EqualTo(secondWand));
                    Assert.That(source.ToolbeltReadOnly.SelectedSlot, Is.EqualTo(1));
                    Assert.That(source.ToolbeltReadOnly.TryGetBinding(slot, out _), Is.False);
                    Assert.That(source.ReadOnly.TryGetView(id, out _), Is.False);
                    for (int remaining = slot + 1; remaining < 4; remaining++)
                        Assert.That(source.ToolbeltReadOnly.TryGetBinding(remaining, out var stillCarried)
                            && stillCarried == (remaining + 4).ToString("x32"), Is.True);
                    Assert.That(Transfer(workspace, throwing, default).InventoryVersion, Is.EqualTo(receipt.InventoryVersion));
                    Assert.That(flight.Launches, Is.EqualTo(slot + 1));
                }
                Assert.That(ThrowService(workspace).PendingFlights, Is.EqualTo(4));
                Assert.That(source.ReadOnly.Count, Is.EqualTo(3), "两个真实杖及其一张卡保留，四瓶仅在DS原在途Store存在。");
            }
        }

        private static OnlineInventoryPresentationSnapshot WireSnapshot(OnlineInventoryPresentationSnapshot snapshot)
        {
            // 使用正式Codec验证消息边界，不为Run测试增添Gameplay->Networking依赖，也不放宽GUID格式。
            var type = Type.GetType("Game.Networking.InventorySnapshotCodec, Game.Networking"); Assert.That(type, Is.Not.Null);
            var codec = Activator.CreateInstance(type);
            var encode = type.GetMethod("TryEncode", new[] { typeof(OnlineInventoryPresentationSnapshot), typeof(byte[]).MakeByRefType() });
            object[] encoded = { snapshot, null };
            Assert.That((bool)encode.Invoke(codec, encoded), Is.True, "正式Owner快照必须可编码，不能令DS发布失败后Frozen。");
            object[] decoded = { new ArraySegment<byte>((byte[])encoded[1]), null };
            Assert.That((bool)type.GetMethod("TryDecode").Invoke(codec, decoded), Is.True);
            return (OnlineInventoryPresentationSnapshot)decoded[1];
        }

        [Test]
        public void ThrowingWithoutAnEquippedWandClearsTheVacatedSelectionAndAllowsAnotherContainer()
        {
            var workspace = CreateUnequippedWandToolWorkspace();
            Assert.That(workspace.ExecuteInventoryCommand(BindIntent(workspace, 1, "bottle", 1)).Success, Is.True);
            Assert.That(workspace.ExecuteInventoryCommand(ToolIntent(workspace, 2)).Success, Is.True);
            Assert.That(workspace.TryCapturePresentation(7, 1, 40, out var before), Is.True);
            var identity = new OnlineInventoryPresentationIdentity("run", "player", 1, "scope", Hash, 7, "test-world");
            Assert.That(OnlineInventoryPresentationSource.TryCreate(_catalog, identity, before, out var source, out var error), Is.True, error);
            using (source)
            using (var flight = new OnlineThrowProbe(workspace))
            {
                var intent = ThrowIntent(workspace, 3);
                Assert.That(Transfer(workspace, intent, ThrowContext()).Success, Is.True);
                Assert.That(workspace.TryCapturePresentation(7, 2, 41, out var after), Is.True);
                // 包内备用杖仍是真实资产，但不能充当装备回退。只有空手快照才能撤销旧瓶HUD与旧库存版本。
                Assert.That(after.SelectedToolSlotPlusOne, Is.Zero);
                Assert.That(after.Assets.SelectedWandInstanceId, Is.Null.Or.Empty);
                Assert.That(after.SelectionVersion, Is.EqualTo(before.SelectionVersion + 1));
                Assert.That(source.TryInstall(after, out error), Is.True, error);
                Assert.That(source.ToolbeltReadOnly.SelectedSlot, Is.EqualTo(-1));
                Assert.That(source.ReadOnly.TryGetView("tool", out _), Is.False);
                Assert.That(source.ReadOnly.TryGetView("wand", out var spare), Is.True);
                Assert.That(spare.Location.Kind, Is.EqualTo(ItemLocationKind.Backpack));

                var select = ToolIntent(workspace, 4); select.ItemId = "bottle"; select.TargetSlot = 1;
                select.ExpectedInventoryVersion = source.ReadOnly.Version;
                Assert.That(workspace.ExecuteInventoryCommand(select).Success, Is.True);
                Assert.That(workspace.TryCapturePresentation(7, 3, 42, out var selected), Is.True);
                Assert.That(source.TryInstall(selected, out error), Is.True, error);
                Assert.That(source.ToolbeltReadOnly.SelectedItemId, Is.EqualTo("bottle"));
                Assert.That(Transfer(workspace, intent, default).Success, Is.True);
                Assert.That(flight.Launches, Is.EqualTo(1), "未知收据重放不能再次投掷，也不能覆盖后续已确认选择。");
                Assert.That(workspace.TryCapturePresentation(8, 1, 43, out var reconnected), Is.True);
                Assert.That(reconnected.SelectedToolSlotPlusOne, Is.EqualTo(2));
                foreach (var item in reconnected.Assets.Items) Assert.That(item.InstanceId, Is.Not.EqualTo("tool"));
            }
        }

        [Test]
        public void MovingSelectedContainerToAnEmptyToolSlotWithoutWandPublishesEmptyHand()
        {
            var workspace = CreateUnequippedWandToolWorkspace();
            Assert.That(workspace.ExecuteInventoryCommand(ToolIntent(workspace, 1)).Success, Is.True);
            Assert.That(workspace.TryCapturePresentation(7, 1, 40, out var before), Is.True);
            var identity = new OnlineInventoryPresentationIdentity("run", "player", 1, "scope", Hash, 7, "test-world");
            Assert.That(OnlineInventoryPresentationSource.TryCreate(_catalog, identity, before, out var source, out var error), Is.True, error);
            using (source)
            {
                Assert.That(workspace.ExecuteInventoryCommand(BindIntent(workspace, 2, "tool", 1)).Success, Is.True);
                AssertBindingLocation(workspace, "tool", ItemLocationKind.EquippedTool, "player", 1);
                Assert.That(workspace.TryCapturePresentation(7, 2, 41, out var after), Is.True);
                Assert.That(after.SelectedToolSlotPlusOne, Is.Zero);
                Assert.That(after.SelectionVersion, Is.EqualTo(before.SelectionVersion + 1));
                Assert.That(source.TryInstall(after, out error), Is.True, error);
                Assert.That(source.ToolbeltReadOnly.SelectedSlot, Is.EqualTo(-1));
                var select = ToolIntent(workspace, 3); select.TargetSlot = 1;
                Assert.That(workspace.ExecuteInventoryCommand(select).Success, Is.True);
            }
        }

        [Test]
        public void FailedThrowWithoutWandKeepsSelectionAndItsReceiptCannotBecomeSuccess()
        {
            var workspace = CreateUnequippedWandToolWorkspace();
            Assert.That(workspace.ExecuteInventoryCommand(ToolIntent(workspace, 1)).Success, Is.True);
            uint version = workspace.InventoryVersion, selection = workspace.SelectionVersion;
            var intent = ThrowIntent(workspace, 2);
            // 不注册物理代理准备者，原事务必须失败并释放Reservation，不能为了清HUD先移走真实实例。
            Assert.That(Transfer(workspace, intent, ThrowContext()).Success, Is.False);
            AssertBindingLocation(workspace, "tool", ItemLocationKind.EquippedTool, "player", 6);
            Assert.That(workspace.InventoryVersion, Is.EqualTo(version));
            Assert.That(workspace.SelectionVersion, Is.EqualTo(selection));
            using var flight = new OnlineThrowProbe(workspace);
            Assert.That(Transfer(workspace, intent, ThrowContext()).Success, Is.False);
            Assert.That(flight.Prepares, Is.Zero);
            Assert.That(workspace.TryCapturePresentation(7, 1, 40, out var snapshot), Is.True);
            var identity = new OnlineInventoryPresentationIdentity("run", "player", 1, "scope", Hash, 7, "test-world");
            Assert.That(OnlineInventoryPresentationSource.TryCreate(_catalog, identity, snapshot, out var source, out var error), Is.True, error);
            using (source) Assert.That(source.ToolbeltReadOnly.SelectedItemId, Is.EqualTo("tool"));
        }

        private OnlineOwnedWorkspace CreateUnequippedWandToolWorkspace()
        {
            var seed = BindingSeed();
            // 保留原杖/卡父链与资源；只把唯一装备杖放回真实背包，形成玩家可合法到达的无装备杖状态。
            foreach (var item in seed.Items)
                if (item.InstanceId == "wand")
                { item.LocationKind = (int)ItemLocationKind.Backpack; item.ParentInstanceId = "bag"; item.Slot = -1; item.X = 3; }
            seed.SelectedWandInstanceId = string.Empty;
            var workspace = (OnlineOwnedWorkspace)Create(seed); ConfigureBindingContainerActions(workspace);
            Assert.That(_catalog.TryGet("online.bottle", out var definition), Is.True);
            var data = new SerializedObject(definition);
            data.FindProperty("_worldModel").objectReferenceValue = Track(new GameObject("Unarmed container throw fixture model"));
            data.ApplyModifiedPropertiesWithoutUndo();
            return workspace;
        }

        [Test]
        public void OnlineThrowImpactFailureAndPartialBatchesRetainRemainderUntilOneFinalResolution()
        {
            var workspace = CreateOnlineThrowWorkspace(2050); using var flight = new OnlineThrowProbe(workspace);
            using var world = new ContainerWorldProbe(0); world.GeometryFailure = true;
            int resolved = 0; ulong deposited = 0;
            Action<ContainerTransitResolvedEvent> onResolved = value =>
            { if (value.InventoryId == workspace.InventoryId) { resolved++; deposited += value.Quantity; } };
            EventBus<ContainerTransitResolvedEvent>.Subscribe(onResolved);
            try
            {
                Assert.That(Transfer(workspace, ThrowIntent(workspace, 2), ThrowContext()).Success, Is.True);
                uint version = workspace.InventoryVersion;
                var impact = new ContainerImpactEvent { InventoryId = workspace.InventoryId, WorldId = "owned-container-world",
                    FlightId = flight.FlightId, Position = Vector3.forward * 3 };
                EventBus<ContainerImpactEvent>.Publish(impact);
                Assert.That(Item(workspace, "tool").Composition.Total, Is.EqualTo(2050ul));
                Assert.That(workspace.InventoryVersion, Is.EqualTo(version)); Assert.That(world.Quantity, Is.Zero);
                Assert.That(ThrowService(workspace).PendingFlights, Is.EqualTo(1));
                world.GeometryFailure = false;
                EventBus<ContainerImpactEvent>.Publish(impact);
                Assert.That(Item(workspace, "tool").Composition.Total, Is.EqualTo(1026ul));
                Assert.That(world.Quantity, Is.EqualTo(1024ul)); Assert.That(resolved, Is.Zero);
                Assert.That(workspace.TryCapturePresentation(7, 1, 1, out _), Is.True);
                EventBus<ContainerImpactEvent>.Publish(impact);
                Assert.That(Item(workspace, "tool").Composition.Total, Is.EqualTo(2ul));
                EventBus<ContainerImpactEvent>.Publish(impact);
                Assert.That(workspace.TryGetItem("tool", out _), Is.False);
                Assert.That(ThrowService(workspace).PendingFlights, Is.Zero);
                Assert.That(world.Quantity, Is.EqualTo(2050ul)); Assert.That(resolved, Is.EqualTo(1)); Assert.That(deposited, Is.EqualTo(2050ul));
                EventBus<ContainerImpactEvent>.Publish(impact);
                Assert.That(world.Quantity, Is.EqualTo(2050ul)); Assert.That(resolved, Is.EqualTo(1));
                Assert.That(flight.Prepares, Is.EqualTo(1));
            }
            finally { EventBus<ContainerTransitResolvedEvent>.Unsubscribe(onResolved); }
        }

        [Test]
        public void OnlineThrowSurvivesNewConnectionEpochWithoutRestoringCarriedBottle()
        {
            var workspace = CreateOnlineThrowWorkspace(); using var flight = new OnlineThrowProbe(workspace);
            Assert.That(Transfer(workspace, ThrowIntent(workspace, 2), ThrowContext()).Success, Is.True);
            var original = Item(workspace, "tool"); uint version = workspace.InventoryVersion;
            Assert.That(workspace.TryCapturePresentation(8, 1, 2, out var reconnect), Is.True);
            foreach (var item in reconnect.Assets.Items) Assert.That(item.InstanceId, Is.Not.EqualTo("tool"));
            var select = ToolIntent(workspace, 1); select.ConnectionEpoch = 8;
            Assert.That(workspace.ExecuteInventoryCommand(select).Failure, Is.EqualTo(OnlineInventoryCommandFailure.NotOwned));
            Assert.That(Item(workspace, "tool"), Is.SameAs(original)); Assert.That(original.Composition.Total, Is.EqualTo(17ul));
            Assert.That(workspace.InventoryVersion, Is.EqualTo(version)); Assert.That(ThrowService(workspace).PendingFlights, Is.EqualTo(1));
            Assert.That(flight.Prepares, Is.EqualTo(1)); Assert.That(flight.Launches, Is.EqualTo(1));
        }

        [Test]
        public void OnlineThrowClosedGatePreservesBottleAndUnknownThrownRowsCannotDisappearFromCapture()
        {
            var workspace = CreateOnlineThrowWorkspace(); using var flight = new OnlineThrowProbe(workspace);
            ConfigureWriteGate(workspace, () => false); uint version = workspace.InventoryVersion;
            Assert.That(Transfer(workspace, ThrowIntent(workspace, 2), ThrowContext()).Failure, Is.EqualTo(OnlineInventoryCommandFailure.NotReady));
            Assert.That(flight.Prepares, Is.Zero); Assert.That(Item(workspace, "tool").Composition.Total, Is.EqualTo(17ul));
            Assert.That(workspace.InventoryVersion, Is.EqualTo(version));
            // 仅用于夹具制造坏现场：没有原 Flight 账本的在途行必须拒绝整份快照，不能被位置过滤藏掉。
            var store = ThrowStore(workspace); var bottle = Item(workspace, "tool");
            Assert.That(store.TryPrepareMaterialItem(bottle,
                new ItemLocation(ItemLocationKind.ThrownContainer, "untracked-flight", 0, 0, 0, -1), false, out var prepared), Is.True);
            store.CompletePrepared(prepared, true);
            Assert.That(workspace.TryCapturePresentation(7, 1, 1, out _), Is.False);
            Assert.That(workspace.TryGetItem("tool", out _), Is.True); Assert.That(bottle.Composition.Total, Is.EqualTo(17ul));
        }

        private OnlineOwnedWorkspace CreateOnlineThrowWorkspace(ulong quantity = 17)
        {
            var workspace = CreateContainerActionWorkspace();
            if (quantity > 17) Item(workspace, "tool").Composition.AddReserved(new[] { new MaterialAmount(1, quantity - 17) }, 1);
            Assert.That(_catalog.TryGet("online.bottle", out var definition), Is.True);
            var data = new SerializedObject(definition);
            data.FindProperty("_worldModel").objectReferenceValue = Track(new GameObject("Online throw fixture model"));
            data.ApplyModifiedPropertiesWithoutUndo();
            return workspace;
        }
        private static ItemOwnershipStore ThrowStore(OnlineOwnedWorkspace workspace)
            => ((SinglePlayerProfile)typeof(OnlineOwnedWorkspace).GetField("_profile", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(workspace)).Items;
        private static ContainerUseService ThrowService(OnlineOwnedWorkspace workspace)
            => (ContainerUseService)typeof(OnlineOwnedWorkspace).GetField("_containerUses", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(workspace);
        private static OnlineInventoryCommandIntent ThrowIntent(OnlineOwnedWorkspace workspace, ulong command)
            => TransferIntent(workspace, command, (OnlineInventoryCommandKind)9, 0);
        private static OnlineContainerAuthorityContext ThrowContext()
        {
            object context = ContainerContext();
            Set(context, "ThrowOrigin", Vector3.forward * .8f + Vector3.up * 1.2f);
            Set(context, "ThrowVelocity", Vector3.forward * 24 + Vector3.up * 6);
            return (OnlineContainerAuthorityContext)context;
        }
        private sealed class OnlineThrowProbe : IDisposable, IContainerThrowProxy
        {
            private readonly OnlineOwnedWorkspace _workspace;
            public int Prepares, Launches;
            public string FlightId;
            public OnlineThrowProbe(OnlineOwnedWorkspace workspace)
            { _workspace = workspace; EventBus<ContainerThrownEvent>.Subscribe(Prepare); }
            private void Prepare(ContainerThrownEvent value)
            {
                var exchange = value.Exchange;
                if (exchange == null || exchange.Proxy != null || exchange.Request.InventoryId != _workspace.InventoryId) return;
                var method = typeof(OnlineOwnedWorkspace).GetMethod("TryGetContainerThrowPresentation");
                Assert.That(method, Is.Not.Null, "受控表现只能准备本工作区真实选中容器。");
                object[] args = { exchange, null, null };
                Assert.That((bool)method.Invoke(_workspace, args), Is.True);
                Assert.That(args[1], Is.EqualTo("online.bottle")); Assert.That(args[2], Is.Not.Null);
                Prepares++; FlightId = exchange.FlightId; exchange.Proxy = this;
            }
            public void Launch() { Launches++; }
            public void Cancel() { }
            public void Dispose() { EventBus<ContainerThrownEvent>.Unsubscribe(Prepare); }
        }

        [Test]
        public void SolidMaterialStateImportsCapturesAndInstallsTheExactQuantity()
        {
            var seed = SolidSeed(256); var workspace = (OnlineOwnedWorkspace)Create(seed);
            Assert.That(Item(workspace, "solid").Material.MaterialKey, Is.EqualTo(5));
            Assert.That(Item(workspace, "solid").Material.Quantity, Is.EqualTo(256ul));
            Assert.That(workspace.TryCapturePresentation(7, 1, 1, out var snapshot), Is.True);
            var state = SolidState(CapturedItem(snapshot, "solid"));
            Assert.That(state.GetType().GetField("SchemaVersion").GetValue(state), Is.EqualTo(1u));
            Assert.That(state.GetType().GetField("Quantity").GetValue(state), Is.EqualTo(256ul));
            var identity = new OnlineInventoryPresentationIdentity("run", "player", 1, "scope", Hash, 7, "test-world");
            Assert.That(OnlineInventoryPresentationSource.TryCreate(_catalog, identity, snapshot, out var source, out var error), Is.True, error);
            using (source)
            {
                Assert.That(source.ReadOnly.TryGetView("solid", out var view), Is.True);
                Assert.That(view.MaterialKey, Is.EqualTo(5)); Assert.That(view.MaterialQuantity, Is.EqualTo(256ul));
                Set(SolidState(seed.Items[seed.Items.Length - 1]), "Quantity", 1ul);
                Set(state, "Quantity", 2ul);
                Assert.That(Item(workspace, "solid").Material.Quantity, Is.EqualTo(256ul));
                Assert.That(source.ReadOnly.TryGetView("solid", out view), Is.True); Assert.That(view.MaterialQuantity, Is.EqualTo(256ul));
            }
        }

        [Test]
        public void SolidMaterialRejectsMissingInvalidOrConflictingExplicitState()
        {
            foreach (string defect in new[] { "missing", "schema", "key", "zero", "capacity", "container", "wand" })
            {
                var seed = SolidSeed(256); var row = seed.Items[seed.Items.Length - 1]; var state = SolidState(row);
                switch (defect)
                {
                    case "missing": Set(row, "MaterialState", null); break;
                    case "schema": Set(state, "SchemaVersion", 0u); break;
                    case "key": Set(state, "MaterialKey", (byte)1); break;
                    case "zero": Set(state, "Quantity", 0ul); break;
                    case "capacity": Set(state, "Quantity", 1025ul); break;
                    case "container": row.ContainerState = Container(); break;
                    case "wand": row.WandStats = InstanceStats(); break;
                }
                Assert.That(TryCreate(seed, out _, out var error), Is.False, defect); Assert.That(error, Is.Not.Empty);
            }
            var nonMaterial = SolidSeed(256); var material = SolidState(nonMaterial.Items[nonMaterial.Items.Length - 1]);
            Set(nonMaterial.Items[0], "MaterialState", material);
            Assert.That(TryCreate(nonMaterial, out _, out _), Is.False, "法杖不能同时携带固体状态。");
        }

        [Test]
        public void ReleaseSolidUsesFirstBackpackMaterialAndCommitsOnlyOneFixedHundredPerIntent()
        {
            var workspace = (OnlineOwnedWorkspace)Create(SolidSeed(256, second: true));
            workspace.ConfigureContainerActions(Track(ScriptableObject.CreateInstance<ContainerUseDefinition>()), "owned-container-world");
            using var world = new ContainerWorldProbe(0, 5); world.GeometryFailure = true;
            var failed = SolidIntent(workspace, 1); uint before = workspace.InventoryVersion;
            Assert.That(Transfer(workspace, failed, SolidContext()).Success, Is.False);
            Assert.That(Item(workspace, "solid").Material.Quantity, Is.EqualTo(256ul)); Assert.That(workspace.InventoryVersion, Is.EqualTo(before));
            world.GeometryFailure = false;
            var intent = SolidIntent(workspace, 2); var committed = Transfer(workspace, intent, SolidContext());
            Assert.That(committed.Success, Is.True); Assert.That(committed.Transferred, Is.EqualTo(100ul));
            Assert.That(Item(workspace, "solid").Material.Quantity, Is.EqualTo(156ul));
            Assert.That(Item(workspace, "solid.second").Material.Quantity, Is.EqualTo(300ul));
            uint version = workspace.InventoryVersion;
            Assert.That(Transfer(workspace, intent, default).Transferred, Is.EqualTo(100ul));
            Assert.That(workspace.InventoryVersion, Is.EqualTo(version)); Assert.That(world.Quantity, Is.EqualTo(100ul));
            Assert.That(Transfer(workspace, failed, SolidContext()).Success, Is.False, "旧失败同ID恢复后仍只重放。");
            var next = SolidIntent(workspace, 1); next.ConnectionEpoch = 8;
            Assert.That(Transfer(workspace, next, SolidContext()).Transferred, Is.EqualTo(100ul));
            Assert.That(Item(workspace, "solid").Material.Quantity, Is.EqualTo(56ul)); Assert.That(world.Quantity, Is.EqualTo(200ul));
        }

        [Test]
        public void FirstBackpackMaterialIsTheSameAfterOwnerInstallWithoutA256ItemCutoff()
        {
            var workspace = (OnlineOwnedWorkspace)Create(SolidSeed(256, second: true, fillers: 260));
            Assert.That(FirstSolid(workspace).InstanceId, Is.EqualTo("solid"));
            Assert.That(workspace.TryCapturePresentation(7, 1, 1, out var snapshot), Is.True);
            Assert.That(snapshot.Assets.Items.Length, Is.GreaterThan(256));
            var identity = new OnlineInventoryPresentationIdentity("run", "player", 1, "scope", Hash, 7, "test-world");
            Assert.That(OnlineInventoryPresentationSource.TryCreate(_catalog, identity, snapshot, out var source, out var error), Is.True, error);
            using (source) Assert.That(FirstSolid(source).InstanceId, Is.EqualTo("solid"));
        }

        private OnlineOwnedAssetSnapshot SolidSeed(ulong quantity, bool second = false, int fillers = 0)
        {
            if (!_catalog.TryGet("online.solid", out _))
            {
                var solid = Define("online.solid", ItemKind.Material); ConfigureInt(solid, "_materialItemCapacityGmu", 1024);
                _catalog.TryGet("online.wand", out var wand); _catalog.TryGet("online.fire", out var spell);
                _catalog.TryGet("online.ordinary", out var ordinary); _catalog.TryGet("online.bottle", out var bottle);
                _catalog = Store(wand, spell, ordinary, bottle, solid,
                    Define("online.solid-bag", ItemKind.Backpack, bagWidth: 20, bagHeight: 20)).Catalog;
            }
            var seed = (OnlineOwnedAssetSnapshot)Seed();
            Append(seed, OwnedItem("bag", "online.solid-bag", ItemLocationKind.EquippedBackpack));
            for (int i = 0; i < fillers; i++) Append(seed, OwnedItem("filler." + i, "online.ordinary", ItemLocationKind.Backpack, "bag", -1, i % 20, i / 20));
            var first = OwnedItem("solid", "online.solid", ItemLocationKind.Backpack, "bag", -1, fillers % 20, fillers / 20);
            SetSolidState(first, quantity); Append(seed, first);
            if (second)
            {
                var next = OwnedItem("solid.second", "online.solid", ItemLocationKind.Backpack, "bag", -1, (fillers + 1) % 20, (fillers + 1) / 20);
                SetSolidState(next, 300); Append(seed, next);
            }
            return seed;
        }
        private static void SetSolidState(object row, ulong quantity)
        {
            var state = NewCore("OnlineOwnedMaterialStateSnapshot"); Set(state, "SchemaVersion", 1u);
            Set(state, "MaterialKey", (byte)5); Set(state, "Quantity", quantity); Set(row, "MaterialState", state);
        }
        private static object SolidState(object row)
        { var field = row.GetType().GetField("MaterialState"); Assert.That(field, Is.Not.Null); var value = field.GetValue(row); Assert.That(value, Is.Not.Null); return value; }
        private static InventoryItemSnapshot FirstSolid(object value)
        {
            var method = value.GetType().GetMethod("TryGetFirstBackpackMaterial"); Assert.That(method, Is.Not.Null);
            object[] args = { null }; Assert.That((bool)method.Invoke(value, args), Is.True); return (InventoryItemSnapshot)args[0];
        }
        private static OnlineInventoryCommandIntent SolidIntent(OnlineOwnedWorkspace workspace, ulong command)
            => new OnlineInventoryCommandIntent { CommandId = command, ConnectionEpoch = 7, ExpectedInventoryVersion = workspace.InventoryVersion,
                Kind = (OnlineInventoryCommandKind)10, ItemId = "solid" };
        private static OnlineContainerAuthorityContext SolidContext()
            => new OnlineContainerAuthorityContext { CanAdvance = true, WorldId = "owned-container-world", ActorPosition = Vector3.zero,
                TargetPosition = Vector3.forward * 1.5f + Vector3.up * .25f };

        [Test]
        public void OnlineAttachmentConfigurationRoundTripsClearsAndReplaysWithoutAnotherEdit()
        {
            var workspace = CreateAttachmentWorkspace(7); uint before = workspace.InventoryVersion;
            var intent = AttachmentIntent(workspace, 1, "bottle", 20, true);
            var configured = workspace.ExecuteInventoryCommand(intent);
            Assert.That(configured.Success, Is.True); Assert.That(workspace.InventoryVersion, Is.GreaterThan(before));
            Assert.That(Item(workspace, "card").Attachment.ContainerId, Is.EqualTo("bottle"));
            Assert.That(Item(workspace, "card").Attachment.Dose, Is.EqualTo(20ul));
            Assert.That(Item(workspace, "bottle").Composition.Total, Is.EqualTo(7ul), "配置不预扣供料。");
            uint version = workspace.InventoryVersion;
            Assert.That(workspace.ExecuteInventoryCommand(intent).Success, Is.True);
            Assert.That(workspace.InventoryVersion, Is.EqualTo(version));
            object changed = intent; Set(changed, "AttachmentDose", 21ul);
            Assert.That(workspace.ExecuteInventoryCommand((OnlineInventoryCommandIntent)changed).Failure,
                Is.EqualTo(OnlineInventoryCommandFailure.CommandConflict));
            Assert.That(workspace.TryCapturePresentation(7, 1, 1, out var snapshot), Is.True);
            var incoming = AttachmentDto(CapturedItem(snapshot, "card"));
            Assert.That(incoming.GetType().GetField("SchemaVersion").GetValue(incoming), Is.EqualTo(1u));
            var identity = new OnlineInventoryPresentationIdentity("run", "player", 1, workspace.InventoryId, Hash, 7, "test-world");
            Assert.That(OnlineInventoryPresentationSource.TryCreate(_catalog, identity, snapshot, out var source, out var error), Is.True, error);
            using (source)
            {
                Set(incoming, "Dose", 1ul);
                Assert.That(source.ReadOnly.TryGetView("card", out var view), Is.True);
                Assert.That(view.Attachment.Dose, Is.EqualTo(20ul));
                Assert.That(Item(workspace, "card").Attachment.Dose, Is.EqualTo(20ul));
                var clear = AttachmentIntent(workspace, 2, "", 0, false);
                Assert.That(workspace.ExecuteInventoryCommand(clear).Success, Is.True);
                Assert.That(Item(workspace, "card").Attachment, Is.Null);
                Assert.That(workspace.TryCapturePresentation(7, 2, 2, out var cleared), Is.True);
                Assert.That(source.TryInstall(cleared, out error), Is.True, error);
                Assert.That(source.ReadOnly.TryGetView("card", out view), Is.True); Assert.That(view.Attachment, Is.Null);
                Assert.That(Item(workspace, "bottle").Composition.Total, Is.EqualTo(7ul));
            }
        }

        [Test]
        public void OnlineAttachmentRejectsToolSourcesBusyFrozenAndForeignCommandFieldsWithoutLosingSettings()
        {
            var seed = CarriedBottle(Amount(1, 7));
            var tool = OwnedItem("tool", "online.bottle", ItemLocationKind.EquippedTool, slot: 6);
            tool.ContainerState = Container(Amount(1, 9)); Append(seed, tool);
            var workspace = CreateAttachmentWorkspace(seed);
            Assert.That(workspace.ExecuteInventoryCommand(AttachmentIntent(workspace, 1, "bottle", 20, true)).Success, Is.True);
            uint version = workspace.InventoryVersion;
            Assert.That(workspace.ExecuteInventoryCommand(AttachmentIntent(workspace, 2, "tool", 20, true)).Success, Is.False);
            Assert.That(Item(workspace, "card").Attachment.ContainerId, Is.EqualTo("bottle"));
            object wrongKind = AttachmentIntent(workspace, 3, "bottle", 20, true);
            Set(wrongKind, "Kind", OnlineInventoryCommandKind.SelectWand); Set(wrongKind, "ItemId", "wand");
            Assert.That(workspace.ExecuteInventoryCommand((OnlineInventoryCommandIntent)wrongKind).Failure,
                Is.EqualTo(OnlineInventoryCommandFailure.InvalidInput));
            var prepared = Execute(workspace, AttachmentCastRequest(workspace, CastResourceOperation.PrepareRoot, 1));
            Assert.That(prepared.Success, Is.True);
            Assert.That(workspace.ExecuteInventoryCommand(AttachmentIntent(workspace, 4, "", 0, false)).Failure,
                Is.EqualTo(OnlineInventoryCommandFailure.Busy));
            Assert.That(Execute(workspace, AttachmentCastRequest(workspace, CastResourceOperation.Cancel, 2, prepared.TicketId)).Success, Is.True);
            ConfigureWriteGate(workspace, () => false);
            Assert.That(workspace.ExecuteInventoryCommand(AttachmentIntent(workspace, 5, "", 0, false)).Failure,
                Is.EqualTo(OnlineInventoryCommandFailure.NotReady));
            Assert.That(workspace.InventoryVersion, Is.EqualTo(version));
            Assert.That(Item(workspace, "card").Attachment.Dose, Is.EqualTo(20ul));
            Assert.That(Item(workspace, "bottle").Composition.Total, Is.EqualTo(7ul));
            // 非发射卡即使归当前玩家，也不能借相同配置入口获得附件。
            var nonEmitterSeed = CarriedBottle(Amount(1, 7));
            Assert.That(_catalog.TryGet("online.fire", out var definition), Is.True);
            definition.SpellDefinition.Kind = Game.Skills.SpellKind.Modify;
            var nonEmitter = CreateAttachmentWorkspace(nonEmitterSeed);
            Assert.That(nonEmitter.ExecuteInventoryCommand(AttachmentIntent(nonEmitter, 1, "bottle", 20, true)).Success, Is.False);
            Assert.That(Item(nonEmitter, "card").Attachment, Is.Null);
        }

        [Test]
        public void OnlineAttachmentPartialEmptyAndInvalidSourceKeepBaseCastAndOnlyChargeApprovedMaterial()
        {
            foreach (string scenario in new[] { "partial", "empty", "missing" })
            {
                ulong initial = scenario == "empty" ? 0ul : 7ul;
                var seed = initial == 0 ? CarriedBottle() : CarriedBottle(Amount(1, initial));
                SetAttachmentDto(seed.Items[1], scenario == "missing" ? "missing-source" : "bottle", 20);
                var workspace = CreateAttachmentWorkspace(seed);
                var prepared = Execute(workspace, AttachmentCastRequest(workspace, CastResourceOperation.PrepareRoot, 1));
                Assert.That(prepared.Success, Is.True, scenario);
                Assert.That(workspace.V2CastResourcesReadOnly.TryGetMaterialOutput(prepared.TicketId, 0, 0, out var output), Is.True);
                Assert.That(output.Status, Is.EqualTo(scenario == "partial" ? SpellMaterialSupplyStatus.Partial
                    : scenario == "empty" ? SpellMaterialSupplyStatus.Empty : SpellMaterialSupplyStatus.InvalidSource));
                Assert.That(output.Budget.Total, Is.EqualTo(scenario == "partial" ? 7ul : 0ul));
                Assert.That(Item(workspace, "bottle").Composition.Total, Is.EqualTo(initial), "Prepare只预约，不提前扣量。");
                var complete = AttachmentCastRequest(workspace, CastResourceOperation.Complete, 2, prepared.TicketId);
                Assert.That(Execute(workspace, complete).Success, Is.True, scenario);
                Assert.That(Execute(workspace, complete).Success, Is.True);
                Assert.That(State(workspace).Mana, Is.EqualTo(80)); Assert.That(Item(workspace, "card").RemainingCharges, Is.EqualTo(2));
                Assert.That(Item(workspace, "bottle").Composition.Total, Is.EqualTo(scenario == "partial" ? 0ul : initial));
                Assert.That(Item(workspace, "card").Attachment, Is.Not.Null, "源失效或空瓶不自动清除设置。");
            }
        }

        [Test]
        public void ImportedAttachmentKeepsAnInvalidSourceAndRejectsMalformedOrNonSpellState()
        {
            var seed = CarriedBottle(); SetAttachmentDto(seed.Items[1], "no-longer-owned", 25);
            var workspace = CreateAttachmentWorkspace(seed);
            Assert.That(Item(workspace, "card").Attachment.ContainerId, Is.EqualTo("no-longer-owned"));
            Set(AttachmentDto(seed.Items[1]), "Dose", 1ul);
            Assert.That(Item(workspace, "card").Attachment.Dose, Is.EqualTo(25ul));
            Assert.That(workspace.TryCapturePresentation(7, 1, 1, out var captured), Is.True);
            Assert.That(AttachmentDto(CapturedItem(captured, "card")).GetType().GetField("ContainerId").GetValue(
                AttachmentDto(CapturedItem(captured, "card"))), Is.EqualTo("no-longer-owned"));
            foreach (string defect in new[] { "schema", "dose", "source", "non-spell" })
            {
                var invalid = CarriedBottle(); SetAttachmentDto(invalid.Items[1], "missing-source", 25);
                var state = AttachmentDto(invalid.Items[1]);
                if (defect == "schema") Set(state, "SchemaVersion", 0u);
                if (defect == "dose") Set(state, "Dose", 0ul);
                if (defect == "source") Set(state, "ContainerId", "");
                if (defect == "non-spell") Set(invalid.Items[0], "AttachmentState", state);
                Assert.That(TryCreate(invalid, out _, out var error), Is.False, defect); Assert.That(error, Is.Not.Empty);
            }
        }

        [Test]
        public void OnlineMaterialOutputRoutingAndFinalGatePreservePendingBudgetAndRetryWithoutAnotherTake()
        {
            var seed = CarriedBottle(Amount(1, 7)); SetAttachmentDto(seed.Items[1], "bottle", 20);
            var workspace = CreateAttachmentWorkspace(seed); bool open = true; ConfigureWriteGate(workspace, () => open);
            var prepared = Execute(workspace, AttachmentCastRequest(workspace, CastResourceOperation.PrepareRoot, 1));
            Assert.That(prepared.Success, Is.True);
            Assert.That(workspace.V2CastResourcesReadOnly.TryGetMaterialOutput(prepared.TicketId, 0, 0, out var output), Is.True);
            Assert.That(Execute(workspace, AttachmentCastRequest(workspace, CastResourceOperation.Complete, 2, prepared.TicketId)).Success, Is.True);
            var outputs = ((WandExecutionTransactionService)typeof(OnlineOwnedWorkspace).GetField("_casts", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(workspace)).MaterialOutputs;
            Assert.That(outputs.ActiveOutputs, Is.EqualTo(1)); Assert.That(Item(workspace, "bottle").Composition.Total, Is.Zero);
            using var world = new ContainerWorldProbe(0); world.GeometryFailure = true;
            var request = new SpellMaterialOutputEvent { InventoryId = workspace.InventoryId, OutputId = output.OutputId, Position = Vector3.forward };
            EventBus<SpellMaterialOutputEvent>.Publish(request);
            Assert.That(world.Reserves, Is.EqualTo(1)); Assert.That(world.Quantity, Is.Zero); Assert.That(outputs.ActiveOutputs, Is.EqualTo(1));
            uint version = workspace.InventoryVersion; open = false;
            var ended = request; ended.Ended = true;
            EventBus<SpellMaterialOutputEvent>.Publish(ended);
            Assert.That(outputs.Execute(ended).Resolved, Is.False, "直接调用服务也必须服从最终写入门。");
            EventBus<SpellMaterialOutputEvent>.Publish(request);
            Assert.That(world.Reserves, Is.EqualTo(1)); Assert.That(outputs.ActiveOutputs, Is.EqualTo(1));
            Assert.That(outputs.LostQuantity(1), Is.Zero); Assert.That(workspace.InventoryVersion, Is.EqualTo(version));
            world.GeometryFailure = false; open = true;
            EventBus<SpellMaterialOutputEvent>.Publish(request);
            Assert.That(world.Quantity, Is.EqualTo(7ul)); Assert.That(outputs.ActiveOutputs, Is.Zero);
            EventBus<SpellMaterialOutputEvent>.Publish(request);
            Assert.That(world.Quantity, Is.EqualTo(7ul)); Assert.That(world.Commits, Is.EqualTo(1));
            Assert.That(Item(workspace, "bottle").Composition.Total, Is.Zero); Assert.That(workspace.InventoryVersion, Is.EqualTo(version));
        }

        private int _attachmentWorkspaceIndex;
        private OnlineOwnedWorkspace CreateAttachmentWorkspace(ulong quantity)
            => CreateAttachmentWorkspace(quantity == 0 ? CarriedBottle() : CarriedBottle(Amount(1, quantity)));
        private OnlineOwnedWorkspace CreateAttachmentWorkspace(OnlineOwnedAssetSnapshot seed)
        {
            seed.InventoryId = "attachment.scope." + (++_attachmentWorkspaceIndex);
            var workspace = (OnlineOwnedWorkspace)Create(seed);
            workspace.ConfigureContainerActions(Track(ScriptableObject.CreateInstance<ContainerUseDefinition>()), "owned-container-world");
            return workspace;
        }
        private static OnlineInventoryCommandIntent AttachmentIntent(OnlineOwnedWorkspace workspace, ulong command, string source, ulong dose, bool enabled)
        {
            object value = new OnlineInventoryCommandIntent { CommandId = command, ConnectionEpoch = 7,
                ExpectedInventoryVersion = workspace.InventoryVersion, Kind = (OnlineInventoryCommandKind)11, ItemId = "card" };
            Set(value, "AttachmentContainerId", source); Set(value, "AttachmentDose", dose); Set(value, "AttachmentEnabled", enabled);
            return (OnlineInventoryCommandIntent)value;
        }
        private static void SetAttachmentDto(object row, string source, ulong dose)
        {
            var state = NewCore("OnlineOwnedSpellAttachmentStateSnapshot");
            Set(state, "SchemaVersion", 1u); Set(state, "ContainerId", source); Set(state, "Dose", dose); Set(state, "Enabled", true);
            Set(row, "AttachmentState", state);
        }
        private static object AttachmentDto(object row)
        {
            var field = row.GetType().GetField("AttachmentState"); Assert.That(field, Is.Not.Null);
            var value = field.GetValue(row); Assert.That(value, Is.Not.Null); return value;
        }
        private static CastResourceRequestedEvent AttachmentCastRequest(OnlineOwnedWorkspace workspace, CastResourceOperation operation, ulong request, string ticket = null)
        { var value = Request(workspace, operation, request, ticket); value.MaterialWorldId = "owned-container-world"; return value; }

        private OnlineOwnedWorkspace CreateContainerActionWorkspace(bool select = true, bool gas = false)
        {
            var seed = (OnlineOwnedAssetSnapshot)Seed();
            var tool = OwnedItem("tool", "online.bottle", ItemLocationKind.EquippedTool, slot: 6);
            tool.ContainerState = gas ? Container(Amount(7, 1000)) : Container(Amount(1, 17)); Append(seed, tool);
            var workspace = (OnlineOwnedWorkspace)Create(seed);
            var definition = Track(ScriptableObject.CreateInstance<ContainerUseDefinition>());
            var data = new SerializedObject(definition);
            data.FindProperty("_collectionRate").floatValue = 8000; data.FindProperty("_collectionRadius").floatValue = .5f;
            data.FindProperty("_pourRate").floatValue = 2000; data.FindProperty("_depositRadius").floatValue = .2f;
            data.FindProperty("_gasPourMultiplier").floatValue = 4; data.FindProperty("_gasDepositRadius").floatValue = .8f;
            data.FindProperty("_maximumPourBatchGmu").intValue = 1024; data.ApplyModifiedPropertiesWithoutUndo();
            var configure = typeof(OnlineOwnedWorkspace).GetMethod("ConfigureContainerActions");
            Assert.That(configure, Is.Not.Null, "Owned工作区需要受控容器服务配置，不可读取单机存档或另造资产。");
            configure.Invoke(workspace, new object[] { definition, "owned-container-world" });
            if (select) Assert.That(workspace.ExecuteInventoryCommand(ToolIntent(workspace, 1)).Success, Is.True);
            return workspace;
        }
        private static OnlineInventoryCommandIntent TransferIntent(OnlineOwnedWorkspace workspace, ulong command, OnlineInventoryCommandKind kind, ulong budget)
            => new OnlineInventoryCommandIntent { CommandId = command, ConnectionEpoch = 7, ExpectedInventoryVersion = workspace.InventoryVersion,
                Kind = kind, ItemId = "tool", TargetSlot = 6, TransferBudget = budget, AimDirection = Vector3.forward };
        private static OnlineContainerAuthorityContext ContainerContext() => new OnlineContainerAuthorityContext
            { CanAdvance = true, WorldId = "owned-container-world", ActorPosition = Vector3.zero, TargetPosition = Vector3.forward };
        private static OnlineInventoryCommandReceipt Transfer(OnlineOwnedWorkspace workspace, OnlineInventoryCommandIntent intent)
            => Transfer(workspace, intent, ContainerContext());
        private static OnlineInventoryCommandReceipt Transfer(OnlineOwnedWorkspace workspace, OnlineInventoryCommandIntent intent, OnlineContainerAuthorityContext context)
        {
            MethodInfo method = null;
            foreach (var candidate in typeof(OnlineOwnedWorkspace).GetMethods(BindingFlags.Public | BindingFlags.Instance))
                if (candidate.Name == "ExecuteInventoryCommand" && candidate.GetParameters().Length == 2) { method = candidate; break; }
            Assert.That(method, Is.Not.Null, "数量命令必须接收DS上下文，并与既有收据共用一个权威账本。");
            return (OnlineInventoryCommandReceipt)method.Invoke(workspace, new object[] { intent, context });
        }

        /// <summary>测试世界只替代空间Owner，真实库存预约仍由两个Run服务创建；模拟Stage、几何拒绝与逆序恢复。</summary>
        private sealed class ContainerWorldProbe : IDisposable
        {
            public ulong Quantity;
            public bool GeometryFailure;
            public int Reserves, Commits;
            public float LastCollectRadius, LastPourRadius;
            public readonly List<string> SourceIds = new List<string>();
            public readonly List<ulong> RequestIds = new List<ulong>();
            private readonly byte _key;
            public ContainerWorldProbe(ulong quantity, byte key = 1)
            {
                Quantity = quantity; _key = key;
                EventBus<MaterialWorldTransferRequestedEvent>.Subscribe(Collect);
                EventBus<MaterialWorldDepositRequestedEvent>.Subscribe(Pour);
            }
            private void Collect(MaterialWorldTransferRequestedEvent command)
            {
                var exchange = command.Exchange; if (exchange == null || exchange.Handled || exchange.Request.WorldId != "owned-container-world") return;
                exchange.Handled = true; LastCollectRadius = exchange.Request.Radius;
                SourceIds.Add(exchange.Request.SourceId); RequestIds.Add(exchange.Request.RequestId);
                var result = exchange.Result; ulong amount = Math.Min(Quantity, Math.Min(exchange.Request.Budget, exchange.Destination.FreeCapacity));
                if (amount == 0) { result.Failure = MaterialTransferFailure.NoMaterial; exchange.Result = result; return; }
                if (!exchange.Destination.TryReserve(new[] { new MaterialAmount(_key, amount) }, 1, out result.Failure)) { exchange.Result = result; return; }
                Reserves++; Quantity -= amount;
                if (GeometryFailure) { Quantity += amount; exchange.Destination.Cancel(); result.Failure = MaterialTransferFailure.GeometryRejected; }
                else { exchange.Destination.Commit(); Commits++; result.Transferred = amount; }
                exchange.Destination.Cancel(); exchange.Result = result;
            }
            private void Pour(MaterialWorldDepositRequestedEvent command)
            {
                var exchange = command.Exchange; if (exchange == null || exchange.Handled || exchange.WorldId != "owned-container-world") return;
                exchange.Handled = true; LastPourRadius = exchange.Radius;
                ulong amount = 0;
                for (int i = 0; i < exchange.Count; i++) { Assert.That(exchange.Amounts[i].MaterialKey, Is.EqualTo(_key)); amount += exchange.Amounts[i].Quantity; }
                if (!exchange.Source.TryReserve(exchange.Amounts, exchange.Count, out var failure)) { exchange.Failure = failure; return; }
                Reserves++; Quantity += amount;
                if (GeometryFailure) { Quantity -= amount; exchange.Source.Cancel(); exchange.Failure = MaterialTransferFailure.GeometryRejected; }
                else { exchange.Source.Commit(); Commits++; exchange.Transferred = amount; }
                exchange.Source.Cancel();
            }
            public void Dispose()
            {
                EventBus<MaterialWorldTransferRequestedEvent>.Unsubscribe(Collect);
                EventBus<MaterialWorldDepositRequestedEvent>.Unsubscribe(Pour);
            }
        }
        private static void ObserveRootRelease(OnlineOwnedWorkspace workspace, string ticket)
        {
            // V2的Complete只提交资源，真实输出尚未释放时ticket仍属于活动执行。
            // 纯Run夹具须补正式Caster的OutputReleased观察，不能把生产Busy门改成忽略未结束产出。
            Assert.That(workspace.V2CastResourcesReadOnly.ActiveReservations, Is.EqualTo(1));
            EventBus<SpellExecutionObservedEvent>.Publish(new SpellExecutionObservedEvent
            {
                InventoryId = workspace.InventoryId, SourceWandId = "wand", CallerId = "test", TicketId = ticket,
                ProgramRevision = (uint)State(workspace).Program.Revision, LayerId = 0, OutputId = 0,
                Kind = SpellExecutionObservationKind.OutputReleased, SimulationTime = 1
            });
            Assert.That(workspace.V2CastResourcesReadOnly.ActiveReservations, Is.Zero);
        }
        private static OnlineInventoryCommandIntent ToolIntent(OnlineOwnedWorkspace workspace, ulong command)
            => new OnlineInventoryCommandIntent { CommandId = command, ConnectionEpoch = 7,
                ExpectedInventoryVersion = workspace.InventoryVersion, Kind = (OnlineInventoryCommandKind)3, ItemId = "tool", TargetSlot = 6 };
        private static uint ToolChoice(OnlineInventoryPresentationSnapshot snapshot)
        {
            var field = snapshot.GetType().GetField("SelectedToolSlotPlusOne");
            Assert.That(field, Is.Not.Null, "完整快照必须明确携带工具选择，0保留原法杖选择。");
            return (uint)field.GetValue(snapshot);
        }

        private static OnlineOwnedItemSnapshot CapturedItem(OnlineInventoryPresentationSnapshot snapshot, string id)
        {
            foreach (var item in snapshot.Assets.Items) if (item.InstanceId == id) return item;
            Assert.Fail("快照遗漏真实实例：" + id); return null;
        }

        private static void ConfigureInt(ItemDefinition definition, string field, int value)
        {
            var serialized = new SerializedObject(definition);
            serialized.FindProperty(field).intValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static OnlineOwnedMaterialAmountSnapshot Amount(byte key, ulong quantity)
            => new OnlineOwnedMaterialAmountSnapshot { MaterialKey = key, Quantity = quantity };
        private static OnlineOwnedContainerStateSnapshot Container(params OnlineOwnedMaterialAmountSnapshot[] components)
            => new OnlineOwnedContainerStateSnapshot { SchemaVersion = 1, Components = components };
        private static OnlineOwnedItemSnapshot OwnedItem(string id, string contentId, ItemLocationKind kind,
            string parent = "", int slot = 0, int x = 0, int y = 0, int turns = 0)
            => new OnlineOwnedItemSnapshot { InstanceId = id, ContentId = contentId, Revision = 1,
                LocationKind = (int)kind, ParentInstanceId = parent, Slot = slot, X = x, Y = y, QuarterTurns = turns };
        private static void Append(OnlineOwnedAssetSnapshot seed, params OnlineOwnedItemSnapshot[] additions)
        {
            int previous = seed.Items.Length;
            Array.Resize(ref seed.Items, previous + additions.Length);
            Array.Copy(additions, 0, seed.Items, previous, additions.Length);
        }
        private OnlineOwnedAssetSnapshot CarriedBottle(params OnlineOwnedMaterialAmountSnapshot[] components)
        {
            var seed = (OnlineOwnedAssetSnapshot)Seed();
            var bottle = OwnedItem("bottle", "online.bottle", ItemLocationKind.Backpack, "bag", -1);
            bottle.ContainerState = Container(components);
            // 故意让子项先于包出现；真实TryRestore必须整批建身份后才验证父链。
            Append(seed, bottle, OwnedItem("bag", "online.bag", ItemLocationKind.EquippedBackpack));
            return seed;
        }
        private OnlineOwnedAssetSnapshot LargeCarriedClosure(int count)
        {
            var seed = (OnlineOwnedAssetSnapshot)Seed();
            var additions = new OnlineOwnedItemSnapshot[count - 2];
            additions[0] = OwnedItem("bag", "online.large-bag", ItemLocationKind.EquippedBackpack);
            for (int i = 1; i < additions.Length; i++)
                additions[i] = OwnedItem("cargo." + (i - 1), "online.ordinary", ItemLocationKind.Backpack, "bag", -1, i - 1);
            Append(seed, additions);
            return seed;
        }
        private static OnlineOwnedWandStatsSnapshot InstanceStats() => new OnlineOwnedWandStatsSnapshot
        {
            MaxMana = 150, ManaRegenPerSecond = 5, BaseCastDelay = .2f, BaseReloadTime = .4f,
            ProgramCapacity = 1, BaseDraws = 1, AlwaysCastContentIds = Array.Empty<string>()
        };
        private void Reject(OnlineOwnedAssetSnapshot seed)
        {
            Assert.That(TryCreate(seed, out object workspace, out string error), Is.False);
            Assert.That(workspace, Is.Null, "非法项不能发布部分工作区或订阅全局事件");
            Assert.That(error, Is.Not.Empty);
        }

        private object Seed(string inventory = "scope")
        {
            object seed = NewCore("OnlineOwnedAssetSnapshot");
            Set(seed, "SchemaVersion", OnlineOwnedAssetSnapshot.CurrentSchemaVersion); Set(seed, "RunId", "run"); Set(seed, "PlayerId", "player");
            Set(seed, "InventoryId", inventory); Set(seed, "ActorId", 1ul); Set(seed, "InventoryVersion", 7u);
            // Schema3的合法冻结夹具显式携带真实余额/DS修订；生产端不得为旧包默认补零或版本1。
            Set(seed, "Gold", 10000L); Set(seed, "CurrencyVersion", 1u);
            Set(seed, "GameplayConfigHash", Hash); Set(seed, "SelectedWandInstanceId", "wand");
            object wand = NewCore("OnlineOwnedItemSnapshot"), card = NewCore("OnlineOwnedItemSnapshot");
            Set(wand, "InstanceId", "wand"); Set(wand, "ContentId", "online.wand"); Set(wand, "Revision", 1u);
            Set(wand, "LocationKind", 3); Set(wand, "Slot", 0); Set(wand, "ParentInstanceId", "");
            Set(wand, "UseTemplateWandStats", true);
            Set(card, "InstanceId", "card"); Set(card, "ContentId", "online.fire"); Set(card, "Revision", 1u);
            Set(card, "LocationKind", 2); Set(card, "Slot", 0); Set(card, "ParentInstanceId", "wand");
            Set(card, "RemainingCharges", 3); Set(card, "HasUnlimitedCharges", false);
            var items = Array.CreateInstance(wand.GetType(), 2); items.SetValue(wand, 0); items.SetValue(card, 1);
            Set(seed, "Items", items); return seed;
        }

        private static object NewCore(string name)
        {
            Type type = typeof(GameLog).Assembly.GetType("Game.Core." + name);
            Assert.That(type, Is.Not.Null, "缺少 Core 中性 Owned 入局数据契约：" + name);
            return Activator.CreateInstance(type);
        }
        private static void Set(object value, string name, object fieldValue)
        {
            var field = value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public);
            Assert.That(field, Is.Not.Null, "Owned DTO 缺少字段：" + name); field.SetValue(value, fieldValue);
        }
        private static Array Items(object seed) => (Array)seed.GetType().GetField("Items").GetValue(seed);
        private bool TryCreate(object seed, out object workspace, out string error)
        {
            var type = typeof(SinglePlayerProfile).Assembly.GetType("Game.Run.OnlineOwnedWorkspaceFactory");
            Assert.That(type, Is.Not.Null, "缺少隔离 Owned 工作区工厂");
            var method = type.GetMethod("TryCreate", BindingFlags.Public | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            object[] args = { _catalog, seed, Hash, null, null }; bool success = (bool)method.Invoke(null, args);
            workspace = args[3]; error = (string)args[4];
            if (workspace is IDisposable disposable) _workspaces.Add(disposable);
            return success;
        }
        private object Create(object seed)
        { Assert.That(TryCreate(seed, out object workspace, out string error), Is.True, error); return workspace; }
        private static object Property(object workspace, string name)
        { var property = workspace.GetType().GetProperty(name); Assert.That(property, Is.Not.Null); return property.GetValue(workspace); }
        private static RuntimeItemState Item(object workspace, string id)
        {
            object[] args = { id, null };
            Assert.That((bool)workspace.GetType().GetMethod("TryGetItem").Invoke(workspace, args), Is.True);
            return (RuntimeItemState)args[1];
        }
        private static WandExecutionSnapshot State(object workspace)
        {
            object[] args = { "wand", null };
            Assert.That((bool)workspace.GetType().GetMethod("TryGetWandExecution").Invoke(workspace, args), Is.True);
            return (WandExecutionSnapshot)args[1];
        }
        private static CastResourceRequestedEvent Request(object workspace, CastResourceOperation operation, ulong sequence, string ticket = null)
        {
            var state = State(workspace);
            return new CastResourceRequestedEvent { InventoryId = (string)Property(workspace, "InventoryId"), SourceWandId = "wand",
                CallerId = "test", RequestId = sequence, ProgramRevision = (uint)state.Program.Revision, ExecutionRevision = state.Draw.ExecutionRevision,
                InventoryVersion = (uint)Property(workspace, "InventoryVersion"), ResourceVersion = state.ResourceVersion,
                UseV2 = true, Operation = operation, TicketId = ticket, LayerIndex = 0 };
        }
        private static CastResourceResultEvent Execute(object workspace, CastResourceRequestedEvent request)
            => (CastResourceResultEvent)workspace.GetType().GetMethod("Execute").Invoke(workspace, new object[] { request });
        private static void Step(object workspace, float deltaTime, uint tick)
            => workspace.GetType().GetMethod("StepAuthority").Invoke(workspace, new object[] { deltaTime, tick });
    }
    /// <summary>
    /// Home不重新发一根满蓝法杖：用真实Owner施法、受控Catalog和Client读模型验证跨DS接续。
    /// 新接缝暂用Reflection定位，使RED是明确的缺行为断言，避免新合同尚未落盘就阻断整个工程编译。
    /// </summary>
    public sealed class OnlineWandResearchContinuityTests : InventoryTestFixture
    {
        private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private const string Player = "11111111111111111111111111111111";
        private readonly List<IDisposable> _owners = new List<IDisposable>();
        private ItemCatalogIndex _catalog;
        private ulong _request;
        private uint _tick;

        [SetUp] public void PrepareResearchCatalog()
        {
            var wand = Define("research.wand", ItemKind.Wand);
            var fire = Define("research.fire", ItemKind.Spell);
            var free = Define("research.fixed", ItemKind.Spell);
            var modifier = Define("research.mod", ItemKind.Spell);
            modifier.SpellDefinition.Kind = SpellKind.Modify;
            var prefab = Track(new GameObject("Research continuity projectile", typeof(Rigidbody), typeof(BoxCollider), typeof(Arrow)));
            fire.SpellDefinition.ProjectilePrefab = prefab; fire.SpellDefinition.ManaCost = 10;
            free.SpellDefinition.ProjectilePrefab = prefab; free.SpellDefinition.ManaCost = 0;
            _catalog = Store(wand, fire, free, modifier).Catalog;
        }

        [TearDown] public void ReleaseResearchOwners()
        { for (int i = _owners.Count - 1; i >= 0; i--) _owners[i].Dispose(); _owners.Clear(); }

        [Test] public void PeacefulPracticeKeepsTheOriginalQualificationAndStillPaysRealEmitResources()
        {
            var seed = Seed(); var items = new List<OnlineOwnedItemSnapshot>(seed.Items);
            items.Add(Card("qualifier", "research.mod", 1, 1)); seed.Items = items.ToArray();
            var owner = Create(seed, true);
            Assert.That(owner.TryGetItem("qualifier", out var qualification), Is.True);
            Assert.That(qualification.RemainingCharges, Is.EqualTo(1), "Home不消费下一次Raid资格。");
            var replay = Cast(owner);
            Assert.That(owner.TryGetItem("qualifier", out qualification), Is.True);
            Assert.That(qualification.RemainingCharges, Is.EqualTo(1));
            Assert.That(State(owner).Mana, Is.EqualTo(90));
            Assert.That(replay.Inputs.RemainingUses[replay.Graph.Program.Count - 1], Is.EqualTo(-1));
            Assert.That(owner.TryGetItem("card", out var paid), Is.True); Assert.That(paid.RemainingCharges, Is.EqualTo(9));
        }

        [Test] public void AuthorityResearchCapturesRealDrawInputsLibraryAndThreeOriginalReplays()
        {
            var owner = Create(Seed()); var tickets = new List<string>();
            for (int i = 0; i < 4; i++) tickets.Add(Cast(owner).TicketId);
            var captured = Capture(owner); var entries = ArrayField(captured, "Wands");
            Assert.That(entries.Length, Is.EqualTo(1));
            Assert.That((float)Field(entries.GetValue(0), "Mana"), Is.EqualTo(State(owner).Mana));
            Assert.That((long)Field(Field(entries.GetValue(0), "Draw"), "ExecutionRevision"), Is.EqualTo(4));
            var library = ArrayField(captured, "Library"); Assert.That(library.Length, Is.EqualTo(1));
            Assert.That(Field(library.GetValue(0), "InstanceId"), Is.EqualTo("loose"));
            var replays = ArrayField(captured, "Replays"); Assert.That(replays.Length, Is.EqualTo(3));
            for (int i = 0; i < 3; i++) Assert.That(Field(replays.GetValue(i), "TicketId"), Is.EqualTo(tickets[i + 1]));
        }

        [Test] public void CanonicalContinuityRoundTripPreservesTheNextAuthorityCast()
        {
            var live = Create(Seed()); Cast(live);
            var restoredData = Decode(Encode(Capture(live)));
            Assert.That(live.TryCapturePresentation(7, 1, 1, out var assets), Is.True);
            assets.Assets.RunId = "new-home-run"; assets.Assets.InventoryId = "new-home-owner";
            // 业务重新入局有自己的Inventory水位，原牌堆不是该版本的替身。
            assets.Assets.InventoryVersion += 100;
            var restored = Create(assets.Assets, true);
            Assert.That(Restore(restored, restoredData, out var error), Is.True, error);
            AssertSameDraw(State(live), State(restored));
            var expected = Cast(live); var actual = Cast(restored);
            AssertSameDraw(State(live), State(restored));
            Assert.That(actual.Graph.Outputs.Count, Is.EqualTo(expected.Graph.Outputs.Count));
            for (int i = 0; i < actual.Graph.Outputs.Count; i++)
            {
                Assert.That(actual.Graph.Outputs[i].SourceCardIndex, Is.EqualTo(expected.Graph.Outputs[i].SourceCardIndex));
                Assert.That(actual.Graph.Outputs[i].Command.Damage, Is.EqualTo(expected.Graph.Outputs[i].Command.Damage));
            }
            Assert.That(restored.TryGetItem("card", out var paid), Is.True); Assert.That(paid.RemainingCharges, Is.EqualTo(8));
        }

        [Test] public void ExhaustedCardHistoryPreservesItsOriginalProgramIndicesAndReleaseFacts()
        {
            var owner = Create(Seed(1)); var recorded = Cast(owner);
            Assert.That(owner.TryGetItem("card", out _), Is.False);
            var raw = Decode(Encode(Capture(owner)));
            var source = Source(owner); Assert.That(Install(source, raw, out var error), Is.True, error);
            Assert.That(Latest(source, out var replay), Is.True);
            Assert.That(replay.TicketId, Is.EqualTo(recorded.TicketId));
            Assert.That(replay.Graph.Program.Count, Is.EqualTo(recorded.Graph.Program.Count));
            for (int i = 0; i < replay.Graph.Program.Count; i++)
            {
                Assert.That(replay.Graph.Program.Instructions[i].SourceSlot, Is.EqualTo(recorded.Graph.Program.Instructions[i].SourceSlot));
                Assert.That(replay.Graph.Program.Instructions[i].InstanceId, Is.EqualTo(recorded.Graph.Program.Instructions[i].InstanceId));
            }
            Assert.That(replay.Trace.Steps.Count, Is.EqualTo(recorded.Trace.Steps.Count));
            Assert.That(replay.WorldStatus, Is.EqualTo(WandWorldExecutionStatus.Released));
            CollectionAssert.AreEqual(recorded.ReleasedOutputs, replay.ReleasedOutputs);
            Assert.That(replay.UsesPaid[0].InstanceId, Is.EqualTo("card"));
        }

        [Test] public void ClientResearchInstallsOriginalReadModelsWithoutCreatingAnAuthorityWorkspace()
        {
            var owner = Create(Seed()); Cast(owner); var snapshot = Capture(owner);
            var source = Source(owner); owner.Dispose();
            Assert.That(Install(source, snapshot, out var error), Is.True, error);
            Assert.That(source.TryCaptureWandResearch("wand", out var research), Is.True);
            Assert.That(research.Execution.Program.Instructions[0].SourceSlot, Is.EqualTo(2), "ProgramIndex不可被空槽号代替。");
            Assert.That(research.Library.Instructions[0].InstanceId, Is.EqualTo("loose"));
            Assert.That(research.AssumesRaidEntry, Is.False); Assert.That(research.Execution.Mana, Is.EqualTo(90));
            Assert.That(research.Execution.Draw.ExecutionRevision, Is.EqualTo(1));
            int replies = 0; Action<CastResourceResultEvent> observe = _ => replies++;
            EventBus<CastResourceResultEvent>.Subscribe(observe);
            try { EventBus<CastResourceRequestedEvent>.Publish(new CastResourceRequestedEvent
                { InventoryId = source.InventoryId, SourceWandId = "wand", CallerId = "read-only", RequestId = 1, UseV2 = true }); }
            finally { EventBus<CastResourceResultEvent>.Unsubscribe(observe); }
            Assert.That(replies, Is.Zero); Assert.That(source.TryCaptureWandResearch("wand", out var unchanged), Is.True);
            Assert.That(unchanged.Execution.Draw, Is.SameAs(research.Execution.Draw));
        }

        [TestCase("player")] [TestCase("stats")] [TestCase("installed")] [TestCase("draw")]
        [TestCase("nan")] [TestCase("scope")] [TestCase("trace")] [TestCase("budget")]
        public void InvalidResearchCannotReplaceAnyPartOfTheInstalledReadModel(string invalid)
        {
            var owner = Create(Seed()); Cast(owner); var captured = Capture(owner); var source = Source(owner);
            Assert.That(Install(source, captured, out _), Is.True); source.TryCaptureWandResearch("wand", out var before);
            var bad = Decode(Encode(captured)); var wand = ArrayField(bad, "Wands").GetValue(0);
            if (invalid == "player") SetField(bad, "PlayerId", "another-player");
            if (invalid == "stats") SetField(Field(wand, "Stats"), "MaxMana", 999f);
            if (invalid == "installed") ((string[])Field(wand, "InstalledIds"))[2] = "loose";
            if (invalid == "draw") SetField(Field(wand, "Draw"), "Deck", new[] { 999 });
            if (invalid == "nan") SetField(wand, "Mana", float.NaN);
            var history = ArrayField(bad, "Replays").GetValue(0);
            if (invalid == "scope") SetField(ArrayField(history, "Scopes").GetValue(0), "ParentScopeId", 0);
            if (invalid == "trace") SetField(ArrayField(history, "Trace").GetValue(0), "CardIndex", 999);
            if (invalid == "budget") SetField(bad, "Replays", Array.CreateInstance(ArrayField(bad, "Replays").GetType().GetElementType(), 4));
            Assert.That(Install(source, bad, out var error), Is.False); Assert.That(error, Is.Not.Empty);
            Assert.That(source.TryCaptureWandResearch("wand", out var after), Is.True);
            Assert.That(after.Execution.Draw, Is.SameAs(before.Execution.Draw)); Assert.That(after.Execution.Mana, Is.EqualTo(before.Execution.Mana));
        }

        [Test] public void OwnerEnvelopeInstallsResearchAndInventoryTogetherAndClosesWithJsonInlineDefault()
        {
            var owner = Create(Seed()); Cast(owner); var source = Source(owner);
            Assert.That(Install(source, Capture(owner), out _), Is.True); uint previous = source.ReadOnly.Version;
            Cast(owner);
            Assert.That(owner.ConfigureHomeResearchAccess(() => true), Is.True);
            Assert.That(owner.ExecuteInventoryCommand(new OnlineInventoryCommandIntent { CommandId = 1, ConnectionEpoch = 7,
                ExpectedInventoryVersion = owner.InventoryVersion, Kind = OnlineInventoryCommandKind.BeginWandResearch }).Success, Is.True);
            Assert.That(owner.TryCapturePresentation(7, 2, 2, out var incoming), Is.True);
            Assert.That(incoming.ResearchOpen, Is.True); Assert.That(incoming.ResearchSessionVersion, Is.EqualTo(2));
            var research = incoming.Research;
            var rootScope = ArrayField(ArrayField(research, "Replays").GetValue(0), "Scopes").GetValue(0);
            SetField(rootScope, "ParentScopeId", 0);
            Assert.That(source.TryInstall(incoming, out _), Is.False);
            Assert.That(source.ReadOnly.Version, Is.EqualTo(previous));
            Assert.That(source.ReadOnly.TryGetView("card", out var oldCard), Is.True); Assert.That(oldCard.RemainingCharges, Is.EqualTo(9));
            SetField(rootScope, "ParentScopeId", -1);
            Assert.That(source.TryInstall(incoming, out var error), Is.True, error);
            Assert.That(source.TryCaptureWandResearch("wand", out var installed), Is.True);
            Assert.That(installed.Execution.Draw.ExecutionRevision, Is.EqualTo(2));
            Assert.That(source.ReadOnly.TryGetView("card", out var current), Is.True); Assert.That(current.RemainingCharges, Is.EqualTo(8));
            Assert.That(owner.ExecuteInventoryCommand(new OnlineInventoryCommandIntent { CommandId = 2, ConnectionEpoch = 7,
                ExpectedInventoryVersion = owner.InventoryVersion, Kind = OnlineInventoryCommandKind.EndWandResearch }).Success, Is.True);
            Assert.That(owner.TryCapturePresentation(7, 3, 3, out var closed), Is.True);
            Assert.That(closed.ResearchSessionVersion, Is.EqualTo(3));
            // 真实库存Wire按物品Kind映射容器等可空字段，只有研究正文使用JSON；不能整份Owner直接JSON化，
            // 否则会给非容器物品也造出ContainerState。这里准确覆盖研究inline默认对象的Schema0边界。
            Type researchType = typeof(GameLog).Assembly.GetType("Game.Core.OnlineWandResearchStateSnapshot");
            SetField(closed, "Research", JsonUtility.FromJson(JsonUtility.ToJson(Activator.CreateInstance(researchType)), researchType));
            Assert.That(source.TryInstall(closed, out error), Is.True, error);
            Assert.That(source.TryCaptureWandResearch("wand", out _), Is.False);
        }

        [TestCase("new")] [TestCase("sold")] [TestCase("program")] [TestCase("stats")] [TestCase("unchanged")]
        public void LegitimateHubAssetChangesRestoreOnlyTheOriginalMatchingWandIntersection(string change)
        {
            var owner = Create(Seed()); Cast(owner); var saved = Capture(owner);
            Assert.That(owner.TryCapturePresentation(7, 1, 1, out var next), Is.True); next.Assets.InventoryVersion += 100;
            var items = new List<OnlineOwnedItemSnapshot>(next.Assets.Items);
            if (change == "new") items.Add(new OnlineOwnedItemSnapshot { InstanceId = "new-wand", ContentId = "research.wand", Revision = 1,
                ParentInstanceId = "", LocationKind = (int)ItemLocationKind.EquippedWand, Slot = 1, UseTemplateWandStats = true });
            if (change == "sold") { items.RemoveAll(item => item.InstanceId == "wand" || item.InstanceId == "card"); next.Assets.SelectedWandInstanceId = ""; }
            if (change == "program") foreach (var item in items) if (item.InstanceId == "card")
                { item.LocationKind = (int)ItemLocationKind.SpellStorage; item.ParentInstanceId = ""; item.Slot = 1; }
            if (change == "stats") foreach (var item in items) if (item.InstanceId == "wand") item.WandStats.MaxMana = 110;
            next.Assets.Items = items.ToArray(); var target = Create(next.Assets);
            Assert.That(Restore(target, saved, out var error), Is.True, error);
            if (change == "sold") Assert.That(target.TryGetWandExecution("wand", out _), Is.False);
            else Assert.That(State(target).Mana, Is.EqualTo(change == "program" ? 100 : change == "stats" ? 110 : 90));
            if (change == "new") { Assert.That(target.TryGetWandExecution("new-wand", out var fresh), Is.True); Assert.That(fresh.Mana, Is.EqualTo(100)); }
            Assert.That(Latest(target, out var history), Is.True); Assert.That(history.Inputs.AvailableMana, Is.EqualTo(100));
        }

        [TestCase(true)] [TestCase(false)]
        public void PeacefulGroundWandKeepsRealResourcesAcrossReturnAndStartsFreshOnlyForANewLogin(bool sameLogin)
        {
            var owner = Create(Seed()); Cast(owner); var before = State(owner);
            var originalGround = new OnlineGroundItemStore(_catalog); _owners.Add(originalGround);
            owner.ConfigureGroundItems(originalGround);
            Assert.That(originalGround.Drop(owner, "wand", Vector3.forward, Quaternion.identity), Is.EqualTo(OnlineInventoryCommandFailure.None));
            var saved = (OnlineWandResearchStateSnapshot)Capture(owner);
            Assert.That(originalGround.TryCapturePracticeContinuity(saved, out var error), Is.True, error);
            saved = (OnlineWandResearchStateSnapshot)Decode(Encode(saved));
            Assert.That(owner.TryCapturePresentation(7, 1, 1, out var remaining), Is.True);
            remaining.Assets.RunId = "new-home-run"; remaining.Assets.InventoryId = "new-home-owner";
            var next = Create(remaining.Assets); var restoredGround = new OnlineGroundItemStore(_catalog); _owners.Add(restoredGround);
            Assert.That(restoredGround.TryRestorePracticeWithContinuity(originalGround.Capture(), sameLogin ? saved : null, out error), Is.True, error);
            next.ConfigureGroundItems(restoredGround);
            Assert.That(restoredGround.TryGet("wand", out var dropped), Is.True);
            var receipt = next.ExecuteInventoryCommand(new OnlineInventoryCommandIntent
            {
                CommandId = 1, ConnectionEpoch = 7, ExpectedInventoryVersion = next.InventoryVersion,
                Kind = OnlineInventoryCommandKind.PickupItem, ItemId = "wand", WorldOwnerId = dropped.WorldOwnerId,
                TargetLocationKind = (int)ItemLocationKind.EquippedWand, TargetSlot = 0
            }, new OnlineContainerAuthorityContext { CanAdvance = true, WorldId = restoredGround.WorldId,
                ActorPosition = Vector3.zero, TargetPosition = dropped.Position, TargetRotation = dropped.Rotation });
            Assert.That(receipt.Success, Is.True, receipt.Failure.ToString());
            Assert.That(next.TryGetItem("card", out var card), Is.True); Assert.That(card.RemainingCharges, Is.EqualTo(9));
            if (sameLogin) AssertSameDraw(before, State(next));
            else { Assert.That(State(next).Mana, Is.EqualTo(100)); Assert.That(State(next).Draw.ExecutionRevision, Is.Zero); }
            Assert.That(restoredGround.Count, Is.Zero);
        }

        [Test] public void PracticeRestoreRefusesAUsedWorkspaceWithoutRefillingResources()
        {
            var owner = Create(Seed()); Cast(owner); var snapshot = Capture(owner);
            Assert.That(owner.TryCapturePresentation(7, 1, 1, out var current), Is.True);
            var target = Create(current.Assets, true); target.StepAuthority(.01f, 1);
            var before = State(target);
            Assert.That(Restore(target, snapshot, out _), Is.False);
            Assert.That(State(target).Mana, Is.EqualTo(before.Mana));
            Assert.That(State(target).Draw.ExecutionRevision, Is.EqualTo(before.Draw.ExecutionRevision));
        }

        [Test] public void ResearchSessionUsesTheOriginalLedgerWithoutChangingInventoryOrResources()
        {
            var owner = Create(Seed()); ConfigureResearch(owner, () => true);
            Assert.That(Property<bool>(owner, "ResearchOpen"), Is.False); Assert.That(Property<uint>(owner, "ResearchSessionVersion"), Is.EqualTo(1));
            uint version = owner.InventoryVersion, resources = State(owner).ResourceVersion;
            var begin = ResearchIntent(owner, 1, 16); Assert.That(owner.ExecuteInventoryCommand(begin).Success, Is.True);
            Assert.That(owner.ExecuteInventoryCommand(begin).Success, Is.True);
            Assert.That(Property<uint>(owner, "ResearchSessionVersion"), Is.EqualTo(2)); Assert.That(Property<bool>(owner, "ResearchPaused"), Is.True);
            Assert.That(owner.InventoryVersion, Is.EqualTo(version)); Assert.That(State(owner).ResourceVersion, Is.EqualTo(resources));
            Assert.That(owner.TryCapturePresentation(7, 1, 0, out var open), Is.True);
            Assert.That((bool)Field(open, "ResearchOpen"), Is.True); Assert.That(Field(open, "Research"), Is.Not.Null);
            var end = ResearchIntent(owner, 2, 17); end.ExpectedInventoryVersion = version - 1;
            Assert.That(owner.ExecuteInventoryCommand(end).Success, Is.True); Assert.That(owner.ExecuteInventoryCommand(end).Success, Is.True);
            Assert.That(Property<bool>(owner, "ResearchOpen"), Is.False); Assert.That(Property<uint>(owner, "ResearchSessionVersion"), Is.EqualTo(3));
            Assert.That(owner.InventoryVersion, Is.EqualTo(version)); Assert.That(State(owner).ResourceVersion, Is.EqualTo(resources));
            Assert.That(owner.TryCapturePresentation(7, 2, 0, out var closed), Is.True);
            Assert.That((uint)Field(closed, "ResearchSessionVersion"), Is.EqualTo(3)); Assert.That(Field(closed, "Research"), Is.Null);
        }

        [TestCase("outside")] [TestCase("normal")] [TestCase("item")] [TestCase("aim")] [TestCase("budget")] [TestCase("future")]
        public void ResearchBeginRejectsMissingTrustedPracticeAccessAndNonNeutralPayload(string bad)
        {
            var owner = Create(Seed(), bad != "normal");
            if (bad != "normal") ConfigureResearch(owner, () => bad != "outside");
            var begin = ResearchIntent(owner, 1, 16);
            if (bad == "item") begin.ItemId = "wand";
            if (bad == "aim") begin.AimDirection = Vector3.forward;
            if (bad == "budget") begin.TransferBudget = 1;
            if (bad == "future") begin.ExpectedInventoryVersion++;
            Assert.That(owner.ExecuteInventoryCommand(begin).Success, Is.False);
            Assert.That(Property<bool>(owner, "ResearchOpen"), Is.False); Assert.That(owner.InventoryVersion, Is.EqualTo(7));
        }

        [Test] public void OpenResearchBlocksWorldMutationButKeepsOriginalStructuralCardEditing()
        {
            var owner = Create(Seed()); bool inside = true; ConfigureResearch(owner, () => inside);
            Assert.That(owner.ExecuteInventoryCommand(ResearchIntent(owner, 1, 16)).Success, Is.True);
            Assert.That(owner.WorldActionsAllowed, Is.False);
            var before = State(owner);
            Assert.That(owner.Execute(new CastResourceRequestedEvent { InventoryId = owner.InventoryId, SourceWandId = "wand", CallerId = "research",
                RequestId = 1, UseV2 = true, Operation = CastResourceOperation.PrepareRoot, ProgramRevision = (uint)before.Program.Revision,
                ExecutionRevision = before.Draw.ExecutionRevision, ResourceVersion = before.ResourceVersion, InventoryVersion = owner.InventoryVersion }).Success, Is.False);
            var material = ResearchIntent(owner, 2, 4); material.ItemId = "card";
            Assert.That(owner.ExecuteInventoryCommand(material).Failure, Is.EqualTo(OnlineInventoryCommandFailure.Busy));
            var move = new OnlineInventoryCommandIntent { CommandId = 3, ConnectionEpoch = 7, ExpectedInventoryVersion = owner.InventoryVersion,
                Kind = OnlineInventoryCommandKind.PlaceCard, ItemId = "card", SourceWandId = "wand", SourceProgramRevision = (uint)before.Program.Revision,
                ToSpellStorage = true, TargetSlot = 1 };
            Assert.That(owner.ExecuteInventoryCommand(move).Success, Is.True);
            inside = false; move.CommandId = 4; move.ExpectedInventoryVersion = owner.InventoryVersion;
            Assert.That(owner.ExecuteInventoryCommand(move).Success, Is.False, "开窗后的每次编辑仍查当前DS区域。");
            Assert.That(State(owner).Mana, Is.EqualTo(before.Mana));
        }

        [Test] public void ResearchBeginCannotPauseAnAlreadyPreparedCastAndForcedCloseIsIdempotent()
        {
            var owner = Create(Seed()); ConfigureResearch(owner, () => true); var before = State(owner);
            var prepared = owner.Execute(new CastResourceRequestedEvent { InventoryId = owner.InventoryId, SourceWandId = "wand", CallerId = "pending",
                RequestId = 1, UseV2 = true, Operation = CastResourceOperation.PrepareRoot, ProgramRevision = (uint)before.Program.Revision,
                ExecutionRevision = before.Draw.ExecutionRevision, ResourceVersion = before.ResourceVersion, InventoryVersion = owner.InventoryVersion });
            Assert.That(prepared.Success, Is.True);
            Assert.That(owner.ExecuteInventoryCommand(ResearchIntent(owner, 1, 16)).Failure, Is.EqualTo(OnlineInventoryCommandFailure.Busy));
            var cancel = new CastResourceRequestedEvent { InventoryId = owner.InventoryId, SourceWandId = "wand", CallerId = "pending",
                RequestId = 2, UseV2 = true, Operation = CastResourceOperation.Cancel, TicketId = prepared.TicketId, ProgramRevision = (uint)before.Program.Revision };
            Assert.That(owner.Execute(cancel).Success, Is.True);
            Assert.That(owner.ExecuteInventoryCommand(ResearchIntent(owner, 2, 16)).Success, Is.True);
            Method(owner, "ForceCloseResearch").Invoke(owner, null); Method(owner, "ForceCloseResearch").Invoke(owner, null);
            Assert.That(Property<bool>(owner, "ResearchOpen"), Is.False); Assert.That(Property<uint>(owner, "ResearchSessionVersion"), Is.EqualTo(3));
        }

        private static OnlineInventoryCommandIntent ResearchIntent(OnlineOwnedWorkspace owner, ulong command, int kind)
            => new OnlineInventoryCommandIntent { CommandId = command, ConnectionEpoch = 7, ExpectedInventoryVersion = owner.InventoryVersion,
                Kind = (OnlineInventoryCommandKind)kind, ItemId = "" };
        private static void ConfigureResearch(OnlineOwnedWorkspace owner, Func<bool> geometry)
        { Assert.That((bool)Method(owner, "ConfigureHomeResearchAccess").Invoke(owner, new object[] { geometry }), Is.True); }
        private static T Property<T>(object target, string name)
        { var value = target.GetType().GetProperty(name); Assert.That(value, Is.Not.Null, "缺研究会话只读事实：" + name); return (T)value.GetValue(target); }

        [TestCase("unknown")] [TestCase("duplicate")] [TestCase("oversize")]
        public void CanonicalResearchCodecRejectsUnknownDuplicateOrOversizeJson(string invalid)
        {
            var owner = Create(Seed()); string json = Encode(Capture(owner));
            if (invalid == "unknown") json = json.Insert(1, "\"Unexpected\":true,");
            if (invalid == "duplicate") json = json.Insert(1, "\"SchemaVersion\":1,");
            if (invalid == "oversize") json = new string(' ', 24 * 1024 * 1024 + 1);
            var method = Codec("TryDecode"); object[] args = { json, null, null };
            Assert.That((bool)method.Invoke(null, args), Is.False); Assert.That(args[1], Is.Null); Assert.That(args[2], Is.Not.Empty);
        }

        private OnlineOwnedAssetSnapshot Seed(int charges = 10) => new OnlineOwnedAssetSnapshot
        {
            SchemaVersion = OnlineOwnedAssetSnapshot.CurrentSchemaVersion, RunId = "home-run", PlayerId = Player,
            ActorId = 1, InventoryId = "home-owner", GameplayConfigHash = Hash, InventoryVersion = 7, CurrencyVersion = 1,
            SelectedWandInstanceId = "wand", Items = new[]
            {
                new OnlineOwnedItemSnapshot { InstanceId = "wand", ContentId = "research.wand", ParentInstanceId = "", Revision = 1,
                    LocationKind = (int)ItemLocationKind.EquippedWand, Slot = 0, WandStats = new OnlineOwnedWandStatsSnapshot
                    { MaxMana = 100, ManaRegenPerSecond = 0, BaseCastDelay = .2f, BaseReloadTime = .7f, BaseDraws = 1,
                        ProgramCapacity = 3, Shuffle = true, AlwaysCastContentIds = new[] { "research.fixed" } } },
                Card("card", "research.fire", charges, 2),
                new OnlineOwnedItemSnapshot { InstanceId = "loose", ContentId = "research.fire", ParentInstanceId = "", Revision = 1,
                    LocationKind = (int)ItemLocationKind.SpellStorage, Slot = 0, RemainingCharges = 3 }
            }
        };
        private static OnlineOwnedItemSnapshot Card(string id, string content, int charges, int slot) => new OnlineOwnedItemSnapshot
        { InstanceId = id, ContentId = content, ParentInstanceId = "wand", Revision = 1, LocationKind = (int)ItemLocationKind.WandSlot,
            RemainingCharges = charges, Slot = slot };
        private OnlineOwnedWorkspace Create(OnlineOwnedAssetSnapshot snapshot, bool practice = true)
        {
            OnlineOwnedWorkspace owner;
            if (practice)
            {
                var method = typeof(OnlineOwnedWorkspaceFactory).GetMethod("TryCreatePractice");
                Assert.That(method, Is.Not.Null, "和平Home必须通过可信purpose选择原实验资格语义。");
                object[] args = { _catalog, snapshot, Hash, null, null };
                Assert.That((bool)method.Invoke(null, args), Is.True, args[4] as string); owner = (OnlineOwnedWorkspace)args[3];
            }
            else Assert.That(OnlineOwnedWorkspaceFactory.TryCreate(_catalog, snapshot, Hash, out owner, out _), Is.True);
            _owners.Add(owner); return owner;
        }
        private WandExecutionReplay Cast(OnlineOwnedWorkspace owner)
        {
            owner.StepAuthority(1, ++_tick); var state = State(owner);
            var request = new CastResourceRequestedEvent { InventoryId = owner.InventoryId, SourceWandId = "wand", CallerId = "research",
                RequestId = ++_request, UseV2 = true, ProgramRevision = (uint)state.Program.Revision,
                ExecutionRevision = state.Draw.ExecutionRevision, InventoryVersion = owner.InventoryVersion,
                ResourceVersion = state.ResourceVersion, Operation = CastResourceOperation.PrepareRoot };
            var prepared = owner.Execute(request); Assert.That(prepared.Success, Is.True, prepared.Reason.ToString());
            request.Operation = CastResourceOperation.Complete; request.TicketId = prepared.TicketId; request.RequestId = ++_request;
            Assert.That(owner.Execute(request).Success, Is.True);
            Assert.That(Latest(owner, out var replay), Is.True);
            for (int i = 0; i < replay.Graph.Outputs.Count; i++) EventBus<SpellExecutionObservedEvent>.Publish(new SpellExecutionObservedEvent
            { InventoryId = owner.InventoryId, SourceWandId = "wand", CallerId = "research", TicketId = prepared.TicketId,
                ProgramRevision = (uint)replay.Graph.Program.Revision, LayerId = replay.Graph.Outputs[i].ScopeId, OutputId = i,
                SimulationTime = _tick, Kind = SpellExecutionObservationKind.OutputReleased });
            Assert.That(owner.V2CastResourcesReadOnly.ActiveReservations, Is.Zero); return replay;
        }
        private OnlineInventoryPresentationSource Source(OnlineOwnedWorkspace owner)
        {
            Assert.That(owner.TryCapturePresentation(7, 1, 1, out var snapshot), Is.True);
            Assert.That(OnlineInventoryPresentationSource.TryCreate(_catalog, new OnlineInventoryPresentationIdentity(owner.RunId, Player,
                1, owner.InventoryId, Hash, 7, "home"), snapshot, out var source, out var error), Is.True, error);
            _owners.Add(source); return source;
        }
        private static WandExecutionSnapshot State(OnlineOwnedWorkspace owner)
        { Assert.That(owner.TryGetWandExecution("wand", out var state), Is.True); return state; }
        private static object Capture(OnlineOwnedWorkspace owner)
        { object[] args = { null }; Assert.That((bool)Method(owner, "TryCaptureResearchState").Invoke(owner, args), Is.True); return args[0]; }
        private static bool Restore(OnlineOwnedWorkspace owner, object state, out string error)
        { object[] args = { state, null }; bool result = (bool)Method(owner, "TryRestorePracticeContinuity").Invoke(owner, args); error = (string)args[1]; return result; }
        private static bool Install(OnlineInventoryPresentationSource source, object state, out string error)
        { object[] args = { state, null }; bool result = (bool)Method(source, "TryInstallResearchState").Invoke(source, args); error = (string)args[1]; return result; }
        private static bool Latest(object source, out WandExecutionReplay replay)
        { object[] args = { "wand", null }; bool result = (bool)Method(source, "TryGetLatestReplay").Invoke(source, args); replay = (WandExecutionReplay)args[1]; return result; }
        private static MethodInfo Method(object target, string name)
        { var method = target.GetType().GetMethod(name); Assert.That(method, Is.Not.Null, "缺少可信研究连续性接缝：" + name); return method; }
        private static MethodInfo Codec(string name)
        { var type = typeof(OnlineOwnedWorkspace).Assembly.GetType("Game.Run.OnlineWandResearchStateCodec"); Assert.That(type, Is.Not.Null); return type.GetMethod(name); }
        private static string Encode(object state)
        { object[] args = { state, null, null }; Assert.That((bool)Codec("TryEncode").Invoke(null, args), Is.True, args[2] as string); return (string)args[1]; }
        private static object Decode(string json)
        { object[] args = { json, null, null }; Assert.That((bool)Codec("TryDecode").Invoke(null, args), Is.True, args[2] as string); return args[1]; }
        private static object Field(object value, string name) => value.GetType().GetField(name).GetValue(value);
        private static void SetField(object value, string name, object content) => value.GetType().GetField(name).SetValue(value, content);
        private static Array ArrayField(object value, string name) => (Array)Field(value, name);
        private static void AssertSameDraw(WandExecutionSnapshot expected, WandExecutionSnapshot actual)
        {
            Assert.That(actual.Mana, Is.EqualTo(expected.Mana)); Assert.That(actual.CastRemaining, Is.EqualTo(expected.CastRemaining));
            Assert.That(actual.ReloadRemaining, Is.EqualTo(expected.ReloadRemaining));
            Assert.That(actual.Program.Revision, Is.EqualTo(expected.Program.Revision));
            Assert.That(actual.Draw.ExecutionRevision, Is.EqualTo(expected.Draw.ExecutionRevision));
            Assert.That(actual.Draw.CycleIndex, Is.EqualTo(expected.Draw.CycleIndex)); Assert.That(actual.Draw.RandomState, Is.EqualTo(expected.Draw.RandomState));
            Assert.That(actual.Draw.CycleReloadDelta, Is.EqualTo(expected.Draw.CycleReloadDelta));
            CollectionAssert.AreEqual(expected.Draw.Deck, actual.Draw.Deck); CollectionAssert.AreEqual(expected.Draw.Discard, actual.Draw.Discard);
        }
    }
}
