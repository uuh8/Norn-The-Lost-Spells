using System;
using System.Reflection;
using Game.Core;
using Game.Skills;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Run.Tests
{
    /// <summary>
    /// Client 读模型与 DS Owned 工作区是不同职责：这里仅导入权威值，不安装资格策略、施法事务或资源 Tick。
    /// RED 阶段通过 Reflection 查找新 API，缺实现应表现为清楚的测试失败，而非让整个项目无法编译。
    /// </summary>
    public sealed class OnlineInventoryPresentationSourceTests : InventoryTestFixture
    {
        private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private ItemCatalogIndex _catalog;
        private Type _sourceType;

        [SetUp]
        public void CreateOnlyMemoryCatalog()
        {
            var spell = Define("online.view.spell", ItemKind.Spell);
            spell.SpellDefinition.Kind = SpellKind.Modify;
            var bottle = Define("online.view.bottle", ItemKind.Container);
            var data = new SerializedObject(bottle);
            data.FindProperty("_containerCapacityGmu").intValue = 15000;
            data.ApplyModifiedPropertiesWithoutUndo();
            var throwable = Define("online.view.throwable", ItemKind.Consumable);
            var use = new SerializedObject(throwable); use.FindProperty("_useKind").intValue = (int)ItemUseKind.Throw;
            use.ApplyModifiedPropertiesWithoutUndo();
            _catalog = Store(Define("online.view.wand", ItemKind.Wand), spell,
                Define("online.view.bag", ItemKind.Backpack, bagWidth: 8, bagHeight: 8),
                Define("online.view.large-bag", ItemKind.Backpack, bagWidth: 4094), bottle,
                Define("online.view.elbow", ItemKind.Container, 2, 2, cells: new[] { Vector2Int.zero, Vector2Int.right, Vector2Int.up }),
                Define("online.view.ordinary", ItemKind.Consumable), Define("online.view.solid", ItemKind.Material), throwable).Catalog;
            _sourceType = typeof(InventoryReadModel).Assembly.GetType("Game.Run.OnlineInventoryPresentationSource");
            Assert.That(_sourceType, Is.Not.Null, "缺少纯线上库存 Presentation Source；不能在 Client 重建有写权限的 Owned 工作区。");
        }

        [Test]
        public void ImportKeepsAuthoritativeManaCooldownProgramRevisionAndSpellUses()
        {
            var source = Create(Snapshot());
            Assert.That(source.ReadOnly.InventoryId, Is.EqualTo("inventory"));
            Assert.That(source.ReadOnly.ActorId, Is.EqualTo("player"));
            Assert.That(source.ReadOnly.Version, Is.EqualTo(9));
            Assert.That(source.ReadOnly.Count, Is.EqualTo(2));
            Assert.That(source.ReadOnly.TryGetView("card", out var card), Is.True);
            Assert.That(card.Location.Kind, Is.EqualTo(ItemLocationKind.WandSlot));
            Assert.That(card.Location.OwnerId, Is.EqualTo("wand"));
            Assert.That(card.RemainingCharges, Is.EqualTo(3), "读模型不能像入局 Authority Factory 一样再次扣资格或次数。");
            Assert.That(source.WandsReadOnly.TryGetSlot(0, out var wand), Is.True);
            Assert.That(wand.CurrentMana, Is.EqualTo(37f), "重连应显示服务器当前 Mana，不能在 Client 构造默认满蓝。");
            Assert.That(wand.CooldownRemaining, Is.EqualTo(1.25f));
            Assert.That(source.WandsReadOnly.TryGetProgram("wand", out var program), Is.True);
            Assert.That(program.ProgramRevision, Is.EqualTo(17));
            Assert.That(program.Program.GetInstanceId(0), Is.EqualTo("card"));
            Assert.That(source.SpellUsesReadOnly.TryGetState("card", out var uses), Is.True);
            Assert.That(uses.RemainingCharges, Is.EqualTo(3)); Assert.That(uses.Eligible, Is.True);
        }

        [Test]
        public void StableReadInterfacesInstallNewValuesWithoutReplacingUiSource()
        {
            var source = Create(Snapshot()); var read = source.ReadOnly; var wands = source.WandsReadOnly;
            wands.TryGetProgram("wand", out var oldProgram);
            var next = Snapshot(2); Set(Wands(next).GetValue(0), "CurrentMana", 19f);
            Set(Wands(next).GetValue(0), "ProgramRevision", 22u);
            Set(next, "ResourceVersion", 13u); Assets(next).InventoryVersion = 10;
            Assets(next).Items[1].RemainingCharges = 1;
            Assert.That(Install(source, next, out _), Is.True);
            Assert.That(source.ReadOnly, Is.SameAs(read)); Assert.That(source.WandsReadOnly, Is.SameAs(wands));
            Assert.That(wands.TryGetState("wand", out var state), Is.True); Assert.That(state.CurrentMana, Is.EqualTo(19f));
            Assert.That(wands.TryGetProgram("wand", out var program), Is.True); Assert.That(program.ProgramRevision, Is.EqualTo(22));
            Assert.That(oldProgram.ProgramRevision, Is.EqualTo(17), "已捕获的旧程序快照不能被后来的网络安装原地覆盖。");
            Assert.That(read.TryGetView("card", out var card), Is.True); Assert.That(card.RemainingCharges, Is.EqualTo(1));
        }

        [Test]
        public void MutableIncomingArraysCannotRewritePublishedValues()
        {
            var incoming = Snapshot(); var source = Create(incoming);
            Assets(incoming).Items[1].RemainingCharges = 999;
            Set(Wands(incoming).GetValue(0), "CurrentMana", 999f);
            Set(SpellUses(incoming).GetValue(0), "Eligible", false);
            Assert.That(source.ReadOnly.TryGetView("card", out var card), Is.True); Assert.That(card.RemainingCharges, Is.EqualTo(3));
            source.WandsReadOnly.TryGetState("wand", out var wand); Assert.That(wand.CurrentMana, Is.EqualTo(37f));
            source.SpellUsesReadOnly.TryGetState("card", out var use); Assert.That(use.Eligible, Is.True);
        }

        [TestCase("RunId", "foreign-run")]
        [TestCase("PlayerId", "foreign-player")]
        [TestCase("InventoryId", "foreign-inventory")]
        [TestCase("GameplayConfigHash", "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
        public void ForeignIdentityNeverPublishesAReadModel(string field, string value)
        {
            var incoming = Snapshot(); Set(Assets(incoming), field, value);
            Assert.That(TryCreate(incoming, out var source, out var error), Is.False);
            Assert.That(source, Is.Null); Assert.That(error, Is.Not.Empty);
        }

        [Test]
        public void WrongEpochAndStaleSequenceCannotOverwriteCurrentState()
        {
            var source = Create(Snapshot(5));
            var old = Snapshot(4); Set(Wands(old).GetValue(0), "CurrentMana", 99f);
            Assert.That(Install(source, old, out _), Is.False);
            var previousConnection = Snapshot(6); Set(previousConnection, "ConnectionEpoch", 6u);
            Assert.That(Install(source, previousConnection, out _), Is.False);
            source.WandsReadOnly.TryGetState("wand", out var current); Assert.That(current.CurrentMana, Is.EqualTo(37f));
        }

        [Test]
        public void OrphanInstalledCardAndFlattenedBackpackAreRejected()
        {
            var incoming = Snapshot(); Assets(incoming).Items[1].ParentInstanceId = "missing-wand";
            Assert.That(TryCreate(incoming, out _, out _), Is.False);
            incoming = Snapshot(); Assets(incoming).Items[1].LocationKind = (int)ItemLocationKind.Backpack;
            Assets(incoming).Items[1].ParentInstanceId = "";
            Assert.That(TryCreate(incoming, out _, out _), Is.False,
                "业务平面 Backpack 槽不是实际 Bag/grid，Client 不能为了显示而发一只虚构背包。");
        }

        [Test]
        public void MissingResourceOrEligibilityStateNeverUsesClientDefaults()
        {
            var incoming = Snapshot(); Set(incoming, "Wands", Array.CreateInstance(Wands(incoming).GetType().GetElementType(), 0));
            Assert.That(TryCreate(incoming, out _, out _), Is.False, "不能因缺少服务器资源读数而默认满 Mana。");
            incoming = Snapshot(); Set(incoming, "SpellUses", Array.CreateInstance(SpellUses(incoming).GetType().GetElementType(), 0));
            Assert.That(TryCreate(incoming, out _, out _), Is.False, "不能在 Client 推导已取得本局资格。");
        }

        [Test]
        public void EmptyEquippedWandsKeepTheRealBackpackAndExposeSafeEmptyHands()
        {
            var incoming = CarriedSnapshot();
            var remaining = new System.Collections.Generic.List<OnlineOwnedItemSnapshot>();
            foreach (var item in incoming.Assets.Items)
                if (item.InstanceId != "wand" && item.InstanceId != "card") remaining.Add(item);
            incoming.Assets.Items = remaining.ToArray(); incoming.Assets.SelectedWandInstanceId = "";
            var remainingWands = new System.Collections.Generic.List<OnlineInventoryWandPresentationSnapshot>();
            foreach (var wand in incoming.Wands) if (wand.InstanceId != "wand") remainingWands.Add(wand);
            incoming.Wands = remainingWands.ToArray();
            var uses = new System.Collections.Generic.List<OnlineInventorySpellUsePresentationSnapshot>();
            foreach (var use in incoming.SpellUses) if (use.InstanceId != "card") uses.Add(use);
            incoming.SpellUses = uses.ToArray();
            using (var source = (OnlineInventoryPresentationSource)Create(incoming))
            {
                Assert.That(source.EquippedBackpackId, Is.EqualTo("bag"));
                Assert.That(source.ReadOnly.TryGetView("bottle", out _), Is.True);
                Assert.That(source.WandsReadOnly.TryGetSlot(0, out _), Is.False);
                Assert.That(source.WandsReadOnly.SelectedWandId, Is.Null.Or.Empty);
                Assert.That(source.ToolbeltReadOnly.SelectedItemId, Is.Null.Or.Empty);
                Assert.That(source.ToolbeltReadOnly.SelectedSlot, Is.EqualTo(-1));
            }
        }

        [Test]
        public void OrdinaryGroundItemUsesTheOriginalReadAndVisualProjection()
        {
            var incoming = GroundProjection(); var ground = incoming.Ground.Items[0];
            ground.ContentId = "online.view.ordinary"; ground.ContainerState = null;
            ground.Item.ContentId = ground.ContentId; ground.Item.ContainerState = null;
            using (var source = (OnlineInventoryPresentationSource)Create(incoming))
            {
                Assert.That(source.ReadOnly.TryGetView(GroundBottleId, out var item), Is.True);
                Assert.That(item.Kind, Is.EqualTo(ItemKind.Consumable));
                Assert.That(item.RemainingCharges, Is.Zero, "原物品规则只给Spell保存施法次数；普通消耗品不伪造次数。");
                Assert.That(item.Location.Kind, Is.EqualTo(ItemLocationKind.WorldItem));
                Assert.That(source.TryGetGroundPose(GroundBottleId, out _, out _), Is.True);
            }
        }

        [Test]
        public void CompleteGroundWandKeepsItsInstalledCardAndFiniteCharges()
        {
            var incoming = GroundProjection(); var ground = incoming.Ground.Items[0];
            ground.ContentId = "online.view.wand"; ground.ContainerState = null;
            var root = ViewItem(GroundBottleId, "online.view.wand", ItemLocationKind.WorldItem, slot: -1);
            root.Revision = ground.Revision; root.UseTemplateWandStats = true;
            var card = ViewItem("ground-card", "online.view.spell", ItemLocationKind.WandSlot, GroundBottleId);
            card.RemainingCharges = 7;
            var itemField = ground.GetType().GetField("Item"); var childrenField = ground.GetType().GetField("Children");
            Assert.That(itemField, Is.Not.Null, "通用地面必须携带完整根实例，不能按容器壳重建法杖。");
            Assert.That(childrenField, Is.Not.Null); itemField.SetValue(ground, root); childrenField.SetValue(ground, new[] { card });
            using (var source = (OnlineInventoryPresentationSource)Create(incoming))
            {
                Assert.That(source.ReadOnly.TryGetView(GroundBottleId, out var wand), Is.True);
                Assert.That(wand.Kind, Is.EqualTo(ItemKind.Wand));
                Assert.That(source.ReadOnly.TryGetView("ground-card", out var installed), Is.True);
                Assert.That(installed.Location.OwnerId, Is.EqualTo(GroundBottleId));
                Assert.That(installed.RemainingCharges, Is.EqualTo(7));
                Assert.That(source.SpellUsesReadOnly.TryGetState("ground-card", out _), Is.False,
                    "公开地面卡的次数不授予当前玩家本局施法资格。");
            }
        }

        [Test]
        public void InvalidReplacementLeavesPreviousInventoryAndResourcesIntact()
        {
            var source = Create(Snapshot());
            var incoming = Snapshot(2); Set(Wands(incoming).GetValue(0), "CurrentMana", float.NaN);
            Assets(incoming).Items[1].RemainingCharges = 1;
            Assert.That(Install(source, incoming, out _), Is.False);
            source.WandsReadOnly.TryGetState("wand", out var state); Assert.That(state.CurrentMana, Is.EqualTo(37f));
            source.ReadOnly.TryGetView("card", out var card); Assert.That(card.RemainingCharges, Is.EqualTo(3));
        }

        [Test]
        public void ReadSourceDoesNotHandleAuthorityRequestsOrRegenerateResources()
        {
            var source = Create(Snapshot()); int responses = 0;
            Action<CastResourceResultEvent> receive = _ => responses++;
            EventBus<CastResourceResultEvent>.Subscribe(receive);
            try
            {
                EventBus<CastResourceRequestedEvent>.Publish(new CastResourceRequestedEvent
                { InventoryId = "inventory", SourceWandId = "wand", UseV2 = true, CallerId = "view", RequestId = 1,
                    Operation = CastResourceOperation.PrepareRoot });
                EventBus<WandSelectionRequestedEvent>.Publish(new WandSelectionRequestedEvent { InventoryId = "inventory", SlotIndex = 1 });
                Assert.That(responses, Is.Zero, "Presentation Source 不注册执行命令或返回权威收据。");
                source.WandsReadOnly.TryGetState("wand", out var state); Assert.That(state.CurrentMana, Is.EqualTo(37f));
                Assert.That(source.WandsReadOnly.SelectedWandId, Is.EqualTo("wand"));
            }
            finally { EventBus<CastResourceResultEvent>.Unsubscribe(receive); }
        }

        [Test]
        public void MissingBagAndUnsupportedCapabilitiesFailClosed()
        {
            var source = Create(Snapshot());
            Assert.That(source.EquippedBackpackId, Is.Null);
            Assert.That(source.SpellStorageCapacity, Is.EqualTo(_catalog.SpellStorageCapacity),
                "随身法术专区是已有根位置7；提供目录容量不代表创建普通Bag或仓库权限。");
            Assert.That(source.CanAccessWarehouse, Is.False);
            Assert.That(source.WalletReadOnly, Is.Not.Null);
            Assert.That(source.WalletReadOnly.Currency, Is.EqualTo(10000L));
            Assert.That(source.WalletReadOnly.Version, Is.EqualTo(1u));
            Assert.That(source.TryCaptureWandResearch("wand", out _), Is.False);
            Assert.That(source.TryCopyContainer("card", new MaterialAmount[1], out _, out _, out _), Is.False);
            Assert.That(source.ToolbeltReadOnly.SelectedSlot, Is.Zero);
            Assert.That(source.ToolbeltReadOnly.TryGetBinding(0, out _), Is.False);
        }

        [Test]
        public void DisposeClearsPreviousReadReferencesAndRejectsLateSnapshots()
        {
            var source = Create(Snapshot()); var read = source.ReadOnly; var wands = source.WandsReadOnly;
            ((IDisposable)source).Dispose();
            Assert.That(read.Count, Is.Zero); Assert.That(read.TryGetView("card", out _), Is.False);
            Assert.That(wands.SelectedWandId, Is.Null); Assert.That(wands.TryGetState("wand", out _), Is.False);
            Assert.That(Install(source, Snapshot(2), out _), Is.False);
        }

        [Test]
        public void CompleteCarriedSnapshotDisplaysRealBagGridToolsAndOnlyActualSpellEligibility()
        {
            var source = Create(CarriedSnapshot());
            Assert.That(source.ReadOnly.Count, Is.EqualTo(9));
            Assert.That(source.EquippedBackpackId, Is.EqualTo("bag"));
            Assert.That(source.ReadOnly.TryGetView("bottle", out var bottle), Is.True);
            Assert.That(bottle.Location.Kind, Is.EqualTo(ItemLocationKind.Backpack));
            Assert.That(bottle.Location.OwnerId, Is.EqualTo("bag"));
            Assert.That(bottle.Location.X, Is.EqualTo(1)); Assert.That(bottle.Location.Y, Is.EqualTo(2));
            Assert.That(bottle.Location.QuarterTurns, Is.EqualTo(1));
            Assert.That(bottle.ContainerTotal, Is.EqualTo(150ul)); Assert.That(bottle.ContainerCapacity, Is.EqualTo(15000ul));
            Assert.That(source.ToolbeltReadOnly.TryGetBinding(6, out var tool), Is.True); Assert.That(tool, Is.EqualTo("tool"));
            Assert.That(source.ToolbeltReadOnly.TryGetBinding(0, out _), Is.False);
            Assert.That(source.ToolbeltReadOnly.TryGetBinding(7, out _), Is.False);
            Assert.That(source.WandsReadOnly.TryGetSlot(1, out _), Is.False, "备用杖不是第二根装备杖资源。");
            Assert.That(source.SpellUsesReadOnly.TryGetState("spare-card", out var spareUse), Is.True);
            Assert.That(spareUse.RemainingCharges, Is.EqualTo(7)); Assert.That(spareUse.Eligible, Is.False);
            Assert.That(source.SpellUsesReadOnly.TryGetState("storage", out var storageUse), Is.True);
            Assert.That(storageUse.RemainingCharges, Is.Zero); Assert.That(storageUse.Eligible, Is.True);
            Assert.That(source.SpellUsesReadOnly.TryGetState("bag", out _), Is.False);
            Assert.That(source.SpellUsesReadOnly.TryGetState("spare", out _), Is.False);
            var components = new MaterialAmount[2];
            Assert.That(source.TryCopyContainer("bottle", components, out var count, out var total, out var capacity), Is.True);
            Assert.That(count, Is.EqualTo(2)); Assert.That(total, Is.EqualTo(150ul)); Assert.That(capacity, Is.EqualTo(15000ul));
            Assert.That(components[0].MaterialKey, Is.EqualTo(1)); Assert.That(components[0].Quantity, Is.EqualTo(100ul));
            Assert.That(components[1].MaterialKey, Is.EqualTo(3)); Assert.That(components[1].Quantity, Is.EqualTo(50ul));
            Assert.That(source.TryCopyContainer("empty", Array.Empty<MaterialAmount>(), out count, out total, out capacity), Is.True);
            Assert.That(count, Is.Zero); Assert.That(total, Is.Zero); Assert.That(capacity, Is.EqualTo(15000ul));
            Assert.That(source.CanAccessWarehouse, Is.False);
            Assert.That(source.WalletReadOnly, Is.Not.Null);
            Assert.That(source.WalletReadOnly.Currency, Is.EqualTo(10000L));
            Assert.That(source.WalletReadOnly.Version, Is.EqualTo(1u));
        }

        [Test]
        public void CarriedSnapshotCopiesDtoComponentsAndKeepsOldViewsAcrossAtomicReplacement()
        {
            var first = CarriedSnapshot(); var source = Create(first);
            var read = source.ReadOnly; var tools = source.ToolbeltReadOnly;
            Assert.That(read.TryGetView("bottle", out var oldBottle), Is.True);
            FindItem(first, "bottle").ContainerState.Components[0].Quantity = 999;
            var copied = new MaterialAmount[2];
            Assert.That(source.TryCopyContainer("bottle", copied, out _, out var total, out _), Is.True);
            Assert.That(total, Is.EqualTo(150ul)); copied[0].Quantity = 123456;
            Assert.Throws<ArgumentException>(() => source.TryCopyContainer("bottle", new MaterialAmount[1], out _, out _, out _),
                "调用方缓冲不足不能静默丢掉第二种真实材料。");
            var next = CarriedSnapshot(2); FindItem(next, "bottle").ContainerState.Components = new[] { Component(1, 90) };
            next.Assets.InventoryVersion = 10; next.ResourceVersion = 13; next.Wands[0].CurrentMana = 19;
            Assert.That(Install(source, next, out _), Is.True);
            Assert.That(source.ReadOnly, Is.SameAs(read)); Assert.That(source.ToolbeltReadOnly, Is.SameAs(tools));
            Assert.That(oldBottle.ContainerTotal, Is.EqualTo(150ul), "之前取得的值快照不随下一批网络数据改变。");
            Assert.That(source.TryCopyContainer("bottle", copied, out var count, out total, out _), Is.True);
            Assert.That(count, Is.EqualTo(1)); Assert.That(total, Is.EqualTo(90ul)); Assert.That(copied[0].Quantity, Is.EqualTo(90ul));
            source.WandsReadOnly.TryGetState("wand", out var state); Assert.That(state.CurrentMana, Is.EqualTo(19f));
        }

        [Test]
        public void BackpackSpareWandUsesItsOwnInstanceCapacityWithoutCreatingEquippedResources()
        {
            var incoming = CarriedSnapshot(); var spare = FindItem(incoming, "spare");
            spare.UseTemplateWandStats = false;
            spare.WandStats = new OnlineOwnedWandStatsSnapshot
            { MaxMana = 150, ManaRegenPerSecond = 5, ProgramCapacity = 2, BaseDraws = 1, AlwaysCastContentIds = Array.Empty<string>() };
            FindItem(incoming, "spare-card").Slot = 1;
            var source = Create(incoming);
            Assert.That(source.ReadOnly.TryGetView("spare", out var view), Is.True);
            Assert.That(view.Rules.WandProgramCapacity, Is.EqualTo(2), "备用杖卡槽按实例容量而非模板1格验证。");
            spare.WandStats.ProgramCapacity = 32;
            Assert.That(source.ReadOnly.TryGetView("spare", out view), Is.True);
            Assert.That(view.Rules.WandProgramCapacity, Is.EqualTo(2));
            Assert.That(source.WandsReadOnly.TryGetState("spare", out var state), Is.True);
            Assert.That(state.SlotIndex, Is.EqualTo(-1)); Assert.That(state.Selected, Is.False); Assert.That(state.Carried, Is.True);
            Assert.That(source.WandsReadOnly.TryGetSlot(-1, out _), Is.False);
            Assert.That(source.SpellUsesReadOnly.TryGetState("spare-card", out var uses), Is.True);
            Assert.That(uses.RemainingCharges, Is.EqualTo(7));
        }

        [TestCase("missing-state")]
        [TestCase("missing-components")]
        [TestCase("schema-zero")]
        [TestCase("duplicate-component")]
        [TestCase("forbidden-material")]
        [TestCase("over-capacity")]
        [TestCase("collision")]
        [TestCase("missing-parent")]
        [TestCase("foreign-root")]
        [TestCase("solid-shell")]
        [TestCase("noncontainer-state")]
        [TestCase("missing-spell-use")]
        [TestCase("fake-spell-use")]
        public void InvalidCarriedReplacementKeepsWholePreviousSourceAndComponents(string defect)
        {
            var source = Create(CarriedSnapshot()); var read = source.ReadOnly;
            var next = CarriedSnapshot(2); var bottle = FindItem(next, "bottle");
            switch (defect)
            {
                case "missing-state": bottle.ContainerState = null; break;
                case "missing-components": bottle.ContainerState.Components = null; break;
                case "schema-zero": bottle.ContainerState.SchemaVersion = 0; break;
                case "duplicate-component": bottle.ContainerState.Components = new[] { Component(1, 1), Component(1, 2) }; break;
                case "forbidden-material": bottle.ContainerState.Components = new[] { Component(2, 1) }; break;
                case "over-capacity": bottle.ContainerState.Components = new[] { Component(1, ulong.MaxValue) }; break;
                case "collision": FindItem(next, "empty").X = 1; FindItem(next, "empty").Y = 2; break;
                case "missing-parent": bottle.ParentInstanceId = "missing"; break;
                case "foreign-root": FindItem(next, "bag").ParentInstanceId = "foreign-player"; break;
                case "solid-shell": bottle.ContentId = "online.view.solid"; bottle.ContainerState = null; break;
                case "noncontainer-state": FindItem(next, "bag").ContainerState = ContainerState(); break;
                case "missing-spell-use": next.SpellUses = Array.Empty<OnlineInventorySpellUsePresentationSnapshot>(); break;
                case "fake-spell-use": next.SpellUses[1].InstanceId = "bag"; break;
                default: Assert.Fail("未知测试缺陷"); break;
            }
            next.Wands[0].CurrentMana = 1;
            Assert.That(Install(source, next, out _), Is.False);
            Assert.That(source.ReadOnly, Is.SameAs(read)); Assert.That(read.Count, Is.EqualTo(9));
            Assert.That(source.EquippedBackpackId, Is.EqualTo("bag"));
            Assert.That(source.TryCopyContainer("bottle", new MaterialAmount[2], out _, out var total, out _), Is.True);
            Assert.That(total, Is.EqualTo(150ul));
            source.WandsReadOnly.TryGetState("wand", out var state); Assert.That(state.CurrentMana, Is.EqualTo(37f));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RotatedCarriedShapeUsesActualHoleAndRejectsOccupiedCell(bool collide)
        {
            var incoming = CarriedSnapshot(); FindItem(incoming, "bottle").ContentId = "online.view.elbow";
            var empty = FindItem(incoming, "empty"); empty.X = collide ? 2 : 1; empty.Y = 3;
            Assert.That(TryCreate(incoming, out _, out _), Is.EqualTo(!collide));
        }

        [Test]
        public void CarriedSourceRemainsPassiveRejectsOldEpochAndClearsToolsAndComponentsOnDispose()
        {
            var source = Create(CarriedSnapshot()); var read = source.ReadOnly; var tools = source.ToolbeltReadOnly;
            int responses = 0; Action<CastResourceResultEvent> receive = _ => responses++;
            EventBus<CastResourceResultEvent>.Subscribe(receive);
            try
            {
                EventBus<CastResourceRequestedEvent>.Publish(new CastResourceRequestedEvent
                { InventoryId = "inventory", SourceWandId = "wand", CallerId = "view", RequestId = 1, UseV2 = true });
                Assert.That(responses, Is.Zero, "完整容器读模型同样不能创建 Authority 服务订阅。");
                var old = CarriedSnapshot(2); old.ConnectionEpoch = 6;
                Assert.That(Install(source, old, out _), Is.False);
                Assert.That(((OnlineInventoryPresentationSource)source).TryApplyResources(6, 100, "wand", 17, 1, 0), Is.False);
                source.WandsReadOnly.TryGetState("wand", out var state); Assert.That(state.CurrentMana, Is.EqualTo(37f));
                ((IDisposable)source).Dispose();
                Assert.That(read.Count, Is.Zero); Assert.That(source.EquippedBackpackId, Is.Null);
                Assert.That(tools.TryGetBinding(6, out _), Is.False);
                Assert.That(source.TryCopyContainer("bottle", new MaterialAmount[2], out _, out _, out _), Is.False);
                Assert.That(Install(source, CarriedSnapshot(3), out _), Is.False);
            }
            finally { EventBus<CastResourceResultEvent>.Unsubscribe(receive); ((IDisposable)source).Dispose(); }
        }

        [TestCase(172, false)]
        [TestCase(4096, false)]
        [TestCase(172, true)]
        [TestCase(4096, true)]
        public void CompleteLargeCarriedSnapshotKeepsEveryItemAndActualSpellUses(int count, bool spellCargo)
        {
            var source = Create(LargeSnapshot(count, spellCargo));
            Assert.That(source.ReadOnly.Count, Is.EqualTo(count)); Assert.That(source.EquippedBackpackId, Is.EqualTo("bag"));
            Assert.That(source.ReadOnly.TryGetView("cargo." + (count - 4), out var item), Is.True);
            Assert.That(item.Kind, Is.EqualTo(spellCargo ? ItemKind.Spell : ItemKind.Consumable));
            Assert.That(source.SpellUsesReadOnly.TryGetState(item.InstanceId, out _), Is.EqualTo(spellCargo));
        }

        [Test]
        public void SnapshotOver4096ItemsNeverPublishesPartialSource()
        {
            Assert.That(TryCreate(LargeSnapshot(4097, false), out var source, out _), Is.False);
            Assert.That(source, Is.Null);
        }

        [Test]
        public void CanInstallChecksCurrentVersionFencesWithoutPublishingSequenceInventoryOrResources()
        {
            var source = Create(CarriedSnapshot());
            var method = _sourceType.GetMethod("CanInstall", BindingFlags.Public | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null, "预队列校验必须复用当前Source的版本栅栏，同时保持纯读取。");
            var next = CarriedSnapshot(2); next.Assets.InventoryVersion = 10; next.Wands[0].CurrentMana = 19;
            next.ResourceVersion = 13;
            object[] args = { next, null };
            Assert.That((bool)method.Invoke(source, args), Is.True, args[1] as string);
            Assert.That(((OnlineInventoryPresentationSource)source).SnapshotSequence, Is.EqualTo(1ul));
            Assert.That(source.ReadOnly.Version, Is.EqualTo(9u));
            source.WandsReadOnly.TryGetState("wand", out var before); Assert.That(before.CurrentMana, Is.EqualTo(37f));
            Assert.That(source.WandsReadOnly.ResourceVersion, Is.EqualTo(12u));
            next.SnapshotSequence = 999; next.Assets.InventoryVersion = 8;
            args = new object[] { next, null };
            Assert.That((bool)method.Invoke(source, args), Is.False, "有合法目录的高序号包也不能倒退当前库存版本。");
            Assert.That(args[1] as string, Is.Not.Empty);
            Assert.That(((OnlineInventoryPresentationSource)source).SnapshotSequence, Is.EqualTo(1ul));
            Assert.That(source.ReadOnly.Version, Is.EqualTo(9u));
            source.WandsReadOnly.TryGetState("wand", out var after); Assert.That(after.CurrentMana, Is.EqualTo(37f));
            Assert.That(source.TryCopyContainer("bottle", new MaterialAmount[2], out _, out var total, out _), Is.True);
            Assert.That(total, Is.EqualTo(150ul));
        }

        [Test]
        public void AuthoritativeToolChoiceSelectsRealContainerWhileKeepingLastWandResources()
        {
            var incoming = CarriedSnapshot(); SelectTool(incoming, 7);
            var source = Create(incoming);
            Assert.That(source.ToolbeltReadOnly.SelectedSlot, Is.EqualTo(9));
            Assert.That(source.ToolbeltReadOnly.SelectedItemId, Is.EqualTo("tool"));
            Assert.That(source.WandsReadOnly.SelectedWandId, Is.EqualTo("wand"));
            source.WandsReadOnly.TryGetState("wand", out var wand); Assert.That(wand.CurrentMana, Is.EqualTo(37f));
            Assert.That(source.TryCopyContainer("tool", new MaterialAmount[1], out _, out var total, out _), Is.True);
            Assert.That(total, Is.EqualTo(9ul));
            var read = source.ToolbeltReadOnly;
            var cleared = CarriedSnapshot(2); SelectTool(cleared, 0); cleared.SelectionVersion = 6;
            Assert.That(Install(source, cleared, out _), Is.True);
            Assert.That(source.ToolbeltReadOnly, Is.SameAs(read));
            Assert.That(read.SelectedSlot, Is.Zero); Assert.That(read.SelectedItemId, Is.EqualTo("wand"));
        }

        [TestCase("slot-outside")]
        [TestCase("unbound-slot")]
        [TestCase("noncontainer")]
        public void InvalidAuthoritativeToolChoiceCannotPublishAReadModel(string defect)
        {
            var incoming = CarriedSnapshot(); SelectTool(incoming, 7);
            if (defect == "slot-outside") SelectTool(incoming, 8);
            else if (defect == "unbound-slot") SelectTool(incoming, 6);
            else
            {
                    var item = FindItem(incoming, "tool"); item.ContentId = "online.view.ordinary"; item.ContainerState = null;
            }
            Assert.That(TryCreate(incoming, out var source, out _), Is.False); Assert.That(source, Is.Null);
        }

        [TestCase("choice-changed")]
        [TestCase("binding-changed")]
        [TestCase("last-wand-changed")]
        [TestCase("old-version")]
        public void SelectionVersionCannotHideAChangedToolBindingOrRegress(string defect)
        {
            var first = CarriedSnapshot(); SelectTool(first, 7); var source = Create(first);
            var next = CarriedSnapshot(2); SelectTool(next, 7); next.Assets.InventoryVersion = 10;
            switch (defect)
            {
                case "choice-changed": SelectTool(next, 0); break;
                case "binding-changed": FindItem(next, "tool").InstanceId = "replacement-tool"; break;
                case "last-wand-changed":
                    FindItem(next, "wand").InstanceId = "replacement-wand";
                    FindItem(next, "card").ParentInstanceId = "replacement-wand";
                    next.Assets.SelectedWandInstanceId = "replacement-wand"; next.Wands[0].InstanceId = "replacement-wand";
                    break;
                case "old-version": next.SelectionVersion = 4; break;
                default: Assert.Fail("未知测试缺陷"); break;
            }
            Assert.That(Install(source, next, out _), Is.False);
            Assert.That(source.ToolbeltReadOnly.SelectedItemId, Is.EqualTo("tool"));
            Assert.That(source.ToolbeltReadOnly.SelectedSlot, Is.EqualTo(9));
            Assert.That(source.WandsReadOnly.SelectedWandId, Is.EqualTo("wand"));
            Assert.That(source.ReadOnly.Version, Is.EqualTo(9u));
        }

        [Test]
        public void GroundProjectionMergesOnlyReadValuesAndDoesNotShareMutableComponentsOrPoses()
        {
            var incoming = GroundProjection();
            using (var source = (OnlineInventoryPresentationSource)Create(incoming))
            {
                Assert.That(source.GroundVersion, Is.EqualTo(2u)); Assert.That(source.ReadOnly.Count, Is.EqualTo(3));
                Assert.That(source.ReadOnly.TryGetView(GroundBottleId, out var bottle), Is.True);
                Assert.That(bottle.Location.Kind, Is.EqualTo(ItemLocationKind.WorldItem)); Assert.That(bottle.Location.OwnerId, Is.EqualTo(GroundOwnerId)); Assert.That(bottle.ContainerTotal, Is.EqualTo(150ul));
                Assert.That(source.TryGetGroundPose(GroundBottleId, out var position, out var rotation), Is.True);
                Assert.That(position, Is.EqualTo(Vector3.forward)); Assert.That(rotation, Is.EqualTo(Quaternion.identity));
                incoming.Ground.Items[0].ContainerState.Components[0].Quantity = 999; incoming.Ground.Items[0].Position = Vector3.one * 50;
                Assert.That(source.ReadOnly.TryGetView(GroundBottleId, out bottle), Is.True); Assert.That(bottle.ContainerTotal, Is.EqualTo(150ul));
                Assert.That(source.TryGetGroundPose(GroundBottleId, out position, out _), Is.True); Assert.That(position, Is.EqualTo(Vector3.forward));
                Assert.That(source.WandsReadOnly.SelectedWandId, Is.EqualTo("wand")); Assert.That(source.WarehouseCount, Is.Zero);
                Assert.That(source.WalletReadOnly, Is.Not.Null);
                Assert.That(source.WalletReadOnly.Currency, Is.EqualTo(10000L));
                Assert.That(source.WalletReadOnly.Version, Is.EqualTo(1u));
            }
        }

        [TestCase("duplicate-ground-id")]
        [TestCase("owned-id-collision")]
        [TestCase("zero-quantity")]
        [TestCase("over-capacity")]
        [TestCase("overflow-quantity")]
        [TestCase("duplicate-component")]
        [TestCase("missing-components")]
        [TestCase("zero-version")]
        public void InvalidGroundBatchCannotReplaceTheExistingOwnerOrGroundReadModel(string defect)
        {
            using (var source = (OnlineInventoryPresentationSource)Create(GroundProjection()))
            {
                var next = GroundProjection(2); next.Ground.Version = 3;
                switch (defect)
                {
                    case "duplicate-ground-id": next.Ground.Items = new[] { next.Ground.Items[0], next.Ground.Items[0] }; break;
                    case "owned-id-collision":
                        var bottle = ViewItem(GroundBottleId, "online.view.bottle", ItemLocationKind.EquippedTool, slot: 0);
                        bottle.ContainerState = ContainerState(); AppendItems(next, bottle); break;
                    case "zero-quantity": next.Ground.Items[0].ContainerState.Components[0].Quantity = 0; break;
                    case "over-capacity": next.Ground.Items[0].ContainerState.Components[0].Quantity = 15001; break;
                    case "overflow-quantity": next.Ground.Items[0].ContainerState.Components[0].Quantity = ulong.MaxValue; break;
                    case "duplicate-component": next.Ground.Items[0].ContainerState.Components[1].MaterialKey = 1; break;
                    case "missing-components": next.Ground.Items[0].ContainerState.Components = null; break;
                    case "zero-version": next.Ground.Version = 0; break;
                }
                Assert.That(source.TryInstall(next, out _), Is.False);
                Assert.That(source.SnapshotSequence, Is.EqualTo(1ul)); Assert.That(source.GroundVersion, Is.EqualTo(2u)); Assert.That(source.ReadOnly.Version, Is.EqualTo(9u));
                Assert.That(source.ReadOnly.Count, Is.EqualTo(3)); Assert.That(source.ReadOnly.TryGetView(GroundBottleId, out var unchanged), Is.True); Assert.That(unchanged.ContainerTotal, Is.EqualTo(150ul));
                Assert.That(source.WandsReadOnly.TryGetSlot(0, out var wand), Is.True); Assert.That(wand.CurrentMana, Is.EqualTo(37f));
            }
        }

        [TestCase("position")]
        [TestCase("rotation")]
        [TestCase("world-owner")]
        [TestCase("quantity")]
        [TestCase("revision")]
        public void TheSameGroundVersionCannotHideChangedInstanceFacts(string changed)
        {
            using (var source = (OnlineInventoryPresentationSource)Create(GroundProjection()))
            {
                var next = GroundProjection(2); var item = next.Ground.Items[0];
                if (changed == "position") item.Position.x = .00000001f;
                if (changed == "rotation") item.Rotation = Quaternion.Euler(0, 20, 0);
                if (changed == "world-owner") item.WorldOwnerId = new string('d', 32);
                if (changed == "quantity") item.ContainerState.Components[0].Quantity++;
                if (changed == "revision") item.Revision++;
                Assert.That(source.TryInstall(next, out _), Is.False, "相同地面版本不能修改任何实例事实，包括供后续Owner导出的资产Revision。");
                Assert.That(source.SnapshotSequence, Is.EqualTo(1ul)); Assert.That(source.GroundVersion, Is.EqualTo(2u));
                Assert.That(source.TryGetGroundPose(GroundBottleId, out var position, out _), Is.True); Assert.That(position, Is.EqualTo(Vector3.forward));
                Assert.That(source.ReadOnly.TryGetView(GroundBottleId, out var unchanged), Is.True); Assert.That(unchanged.ContainerTotal, Is.EqualTo(150ul));
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void KnownGroundCannotBeClearedByNullOrByChangingItsContentsAtTheSameVersion(bool nullGround)
        {
            using (var source = (OnlineInventoryPresentationSource)Create(GroundProjection()))
            {
                var next = GroundProjection(2);
                next.Ground = nullGround ? null : new OnlineGroundInventorySnapshot { Version = 2, Items = Array.Empty<OnlineGroundItemSnapshot>() };
                Assert.That(source.TryInstall(next, out _), Is.False); Assert.That(source.GroundVersion, Is.EqualTo(2u));
                Assert.That(source.ReadOnly.TryGetView(GroundBottleId, out _), Is.True); Assert.That(source.SnapshotSequence, Is.EqualTo(1ul));
            }
        }

        [Test]
        public void ANewerEmptyGroundRemovesTheViewAndAnOlderGroundCannotResurrectIt()
        {
            using (var source = (OnlineInventoryPresentationSource)Create(GroundProjection()))
            {
                var removed = GroundProjection(2); removed.Ground = new OnlineGroundInventorySnapshot { Version = 3, Items = Array.Empty<OnlineGroundItemSnapshot>() };
                Assert.That(source.TryInstall(removed, out _), Is.True); Assert.That(source.ReadOnly.TryGetView(GroundBottleId, out _), Is.False);
                Assert.That(source.TryGetGroundPose(GroundBottleId, out _, out _), Is.False);
                var stale = GroundProjection(3);
                Assert.That(source.TryInstall(stale, out _), Is.False); Assert.That(source.GroundVersion, Is.EqualTo(3u)); Assert.That(source.SnapshotSequence, Is.EqualTo(2ul));
                Assert.That(source.ReadOnly.TryGetView(GroundBottleId, out _), Is.False);
            }
        }

        [Test]
        public void OneCompleteSnapshotCanAtomicallyMoveTheGroundIdentityIntoTheOwnersRealBag()
        {
            using (var source = (OnlineInventoryPresentationSource)Create(GroundProjection()))
            {
                var admitted = GroundProjection(2); admitted.Assets.InventoryVersion = 10;
                admitted.Ground = new OnlineGroundInventorySnapshot { Version = 3, Items = Array.Empty<OnlineGroundItemSnapshot>() };
                var bottle = ViewItem(GroundBottleId, "online.view.bottle", ItemLocationKind.Backpack, "bag", -1, 2, 3);
                bottle.Revision = 4; bottle.ContainerState = ContainerState(Component(1, 100), Component(3, 50));
                AppendItems(admitted, ViewItem("bag", "online.view.bag", ItemLocationKind.EquippedBackpack), bottle);
                Assert.That(source.TryInstall(admitted, out _), Is.True);
                Assert.That(source.ReadOnly.TryGetView(GroundBottleId, out var owned), Is.True);
                Assert.That(owned.Location.Kind, Is.EqualTo(ItemLocationKind.Backpack)); Assert.That(owned.Location.OwnerId, Is.EqualTo("bag"));
                Assert.That(owned.ContainerTotal, Is.EqualTo(150ul)); Assert.That(source.TryGetGroundPose(GroundBottleId, out _, out _), Is.False);
                Assert.That(source.ReadOnly.Count, Is.EqualTo(4)); Assert.That(source.ReadOnly.Version, Is.EqualTo(10u)); Assert.That(source.GroundVersion, Is.EqualTo(3u));
            }
        }

        [Test]
        public void GroundContainerPlacementPreview_UsesCurrentBagWithoutMutatingSnapshot()
        {
            var incoming = CarriedSnapshot(); incoming.Ground = GroundProjection().Ground;
            using (var source = (OnlineInventoryPresentationSource)Create(incoming))
            {
                // 原网格通过这个只读能力画占据提示；预览不能搬动物品、扣料或授予拾取权限。
                var preview = source.ReadOnly as IInventoryPlacementPreview;
                Assert.That(preview, Is.Not.Null, "线上地面容器也必须复用原背包占据预览。");
                uint version = source.ReadOnly.Version;
                Assert.That(preview.PreviewPlacement(GroundBottleId, "bag", new GridPlacement(0, 0, 0), false, version), Is.EqualTo(InventoryFailureReason.None));
                Assert.That(preview.PreviewPlacement(GroundBottleId, "bag", new GridPlacement(3, 2, 0), false, version), Is.EqualTo(InventoryFailureReason.Occupied));
                Assert.That(preview.PreviewPlacement(GroundBottleId, "bag", new GridPlacement(8, 0, 0), false, version), Is.EqualTo(InventoryFailureReason.OutOfBounds));
                Assert.That(preview.PreviewPlacement(GroundBottleId, "bag", new GridPlacement(0, 0, 0), false, version - 1), Is.EqualTo(InventoryFailureReason.StaleVersion));
                Assert.That(preview.PreviewPlacement("bottle", "bag", new GridPlacement(0, 0, 0), false, version), Is.EqualTo(InventoryFailureReason.None),
                    "已有随身Item与地面Item复用同一个真实网格预览。");
                Assert.That(preview.PreviewPlacement(GroundBottleId, "missing-bag", new GridPlacement(0, 0, 0), false, version), Is.EqualTo(InventoryFailureReason.InvalidOwnership));
                Assert.That(source.ReadOnly.TryGetView(GroundBottleId, out var ground), Is.True);
                Assert.That(ground.Location.Kind, Is.EqualTo(ItemLocationKind.WorldItem));
                Assert.That(ground.ContainerTotal, Is.EqualTo(150ul));
                Assert.That(source.ReadOnly.Version, Is.EqualTo(version));
                Assert.That(source.GroundVersion, Is.EqualTo(2u));
                Assert.That(source.TryGetGroundPose(GroundBottleId, out _, out _), Is.True);
            }
        }

        private const string GroundBottleId = "cccccccccccccccccccccccccccccccc";
        private const string GroundOwnerId = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        private static OnlineInventoryPresentationSnapshot GroundProjection(ulong sequence = 1)
        {
            var value = (OnlineInventoryPresentationSnapshot)Snapshot(sequence);
            value.Ground = new OnlineGroundInventorySnapshot { Version = 2, Items = new[] { new OnlineGroundItemSnapshot
            {
                InstanceId = GroundBottleId, ContentId = "online.view.bottle", WorldOwnerId = GroundOwnerId, Revision = 4,
                Position = Vector3.forward, Rotation = Quaternion.identity, ContainerState = ContainerState(Component(1, 100), Component(3, 50))
            } } };
            var ground = value.Ground.Items[0];
            ground.Item = ViewItem(ground.InstanceId, ground.ContentId, ItemLocationKind.WorldItem, slot: -1);
            ground.Item.Revision = ground.Revision; ground.Item.ContainerState = ground.ContainerState;
            return value;
        }

        private static void SelectTool(OnlineInventoryPresentationSnapshot snapshot, uint slotPlusOne)
        {
            var field = snapshot.GetType().GetField("SelectedToolSlotPlusOne");
            Assert.That(field, Is.Not.Null, "工具选择事实必须显式进入同批完整快照。");
            field.SetValue(snapshot, slotPlusOne);
        }

        private static OnlineOwnedMaterialAmountSnapshot Component(byte key, ulong quantity)
            => new OnlineOwnedMaterialAmountSnapshot { MaterialKey = key, Quantity = quantity };
        private static OnlineOwnedContainerStateSnapshot ContainerState(params OnlineOwnedMaterialAmountSnapshot[] values)
            => new OnlineOwnedContainerStateSnapshot { SchemaVersion = 1, Components = values };
        private static OnlineOwnedItemSnapshot ViewItem(string id, string content, ItemLocationKind kind, string parent = "", int slot = 0,
            int x = 0, int y = 0, int turns = 0) => new OnlineOwnedItemSnapshot
            { InstanceId = id, ContentId = content, Revision = 1, LocationKind = (int)kind, ParentInstanceId = parent, Slot = slot, X = x, Y = y, QuarterTurns = turns };
        private static void AppendItems(OnlineInventoryPresentationSnapshot snapshot, params OnlineOwnedItemSnapshot[] additions)
        {
            int length = snapshot.Assets.Items.Length;
            Array.Resize(ref snapshot.Assets.Items, length + additions.Length);
            Array.Copy(additions, 0, snapshot.Assets.Items, length, additions.Length);
        }
        private static OnlineOwnedItemSnapshot FindItem(OnlineInventoryPresentationSnapshot snapshot, string id)
        {
            foreach (var item in snapshot.Assets.Items) if (item.InstanceId == id) return item;
            Assert.Fail("缺少测试实例：" + id); return null;
        }
        private static OnlineInventoryPresentationSnapshot CarriedSnapshot(ulong sequence = 1)
        {
            var value = (OnlineInventoryPresentationSnapshot)Snapshot(sequence); value.ServerTick = 40;
            var bottle = ViewItem("bottle", "online.view.bottle", ItemLocationKind.Backpack, "bag", -1, 1, 2, 1);
            bottle.ContainerState = ContainerState(Component(1, 100), Component(3, 50));
            var empty = ViewItem("empty", "online.view.bottle", ItemLocationKind.Backpack, "bag", -1, 3, 2);
            empty.ContainerState = ContainerState();
            var tool = ViewItem("tool", "online.view.bottle", ItemLocationKind.EquippedTool, slot: 6);
            tool.ContainerState = ContainerState(Component(1, 9));
            var spare = ViewItem("spare", "online.view.wand", ItemLocationKind.Backpack, "bag", -1, 5, 5);
            spare.UseTemplateWandStats = true;
            var spareCard = ViewItem("spare-card", "online.view.spell", ItemLocationKind.WandSlot, "spare"); spareCard.RemainingCharges = 7;
            var storage = ViewItem("storage", "online.view.spell", ItemLocationKind.SpellStorage, slot: 3);
            AppendItems(value, bottle, empty, tool, spare, spareCard, storage,
                ViewItem("bag", "online.view.bag", ItemLocationKind.EquippedBackpack));
            value.SpellUses = new[] { new OnlineInventorySpellUsePresentationSnapshot { InstanceId = "card", Eligible = true },
                new OnlineInventorySpellUsePresentationSnapshot { InstanceId = "spare-card", Eligible = false },
                new OnlineInventorySpellUsePresentationSnapshot { InstanceId = "storage", Eligible = true } };
            value.Wands = new[] { value.Wands[0], new OnlineInventoryWandPresentationSnapshot
                { InstanceId = "spare", ProgramRevision = 41, CurrentMana = 11, CooldownRemaining = .75f } };
            return value;
        }
        private static OnlineInventoryPresentationSnapshot LargeSnapshot(int count, bool spellCargo)
        {
            var value = (OnlineInventoryPresentationSnapshot)Snapshot();
            var additions = new OnlineOwnedItemSnapshot[count - 2];
            additions[0] = ViewItem("bag", "online.view.large-bag", ItemLocationKind.EquippedBackpack);
            var uses = new OnlineInventorySpellUsePresentationSnapshot[spellCargo ? count - 2 : 1];
            uses[0] = new OnlineInventorySpellUsePresentationSnapshot { InstanceId = "card", Eligible = true };
            for (int i = 1; i < additions.Length; i++)
            {
                string id = "cargo." + (i - 1);
                additions[i] = ViewItem(id, spellCargo ? "online.view.spell" : "online.view.ordinary", ItemLocationKind.Backpack, "bag", -1, i - 1);
                if (spellCargo) { additions[i].RemainingCharges = 3; uses[i] = new OnlineInventorySpellUsePresentationSnapshot { InstanceId = id }; }
            }
            AppendItems(value, additions); value.SpellUses = uses; return value;
        }

        private IInventoryPresentationSource Create(object snapshot)
        { Assert.That(TryCreate(snapshot, out var source, out var error), Is.True, error); return source; }

        private bool TryCreate(object snapshot, out IInventoryPresentationSource source, out string error)
        {
            var identityType = RunType("OnlineInventoryPresentationIdentity");
            var identity = Activator.CreateInstance(identityType, "run", "player", 1ul, "inventory", Hash, 7u, "DungeonWorld");
            var arguments = new[] { (object)_catalog, identity, snapshot, null, null };
            bool accepted = (bool)_sourceType.GetMethod("TryCreate", BindingFlags.Public | BindingFlags.Static).Invoke(null, arguments);
            source = (IInventoryPresentationSource)arguments[3]; error = (string)arguments[4]; return accepted;
        }

        private static bool Install(IInventoryPresentationSource source, object snapshot, out string error)
        {
            var arguments = new[] { snapshot, null };
            bool accepted = (bool)source.GetType().GetMethod("TryInstall").Invoke(source, arguments);
            error = (string)arguments[1]; return accepted;
        }

        private static object Snapshot(ulong sequence = 1)
        {
            var snapshot = Activator.CreateInstance(RunType("OnlineInventoryPresentationSnapshot"));
            Set(snapshot, "SchemaVersion", 1u); Set(snapshot, "ConnectionEpoch", 7u); Set(snapshot, "SnapshotSequence", sequence);
            Set(snapshot, "ResourceVersion", 12u); Set(snapshot, "SelectionVersion", 5u); Set(snapshot, "SpellUseVersion", 4u);
            Set(snapshot, "Assets", new OnlineOwnedAssetSnapshot
            {
                SchemaVersion = OnlineOwnedAssetSnapshot.CurrentSchemaVersion, ActorId = 1, RunId = "run", PlayerId = "player", InventoryId = "inventory",
                GameplayConfigHash = Hash, InventoryVersion = 9, SelectedWandInstanceId = "wand",
                // 合法Schema3快照显式提供统一金币；不靠客户端读本地档或推断注册奖励。
                Gold = 10000, CurrencyVersion = 1,
                Items = new[]
                {
                    new OnlineOwnedItemSnapshot { InstanceId = "wand", ContentId = "online.view.wand", Revision = 1,
                        LocationKind = (int)ItemLocationKind.EquippedWand, Slot = 0, UseTemplateWandStats = true },
                    new OnlineOwnedItemSnapshot { InstanceId = "card", ContentId = "online.view.spell", Revision = 1,
                        LocationKind = (int)ItemLocationKind.WandSlot, Slot = 0, ParentInstanceId = "wand", RemainingCharges = 3 }
                }
            });
            var wand = Activator.CreateInstance(RunType("OnlineInventoryWandPresentationSnapshot"));
            Set(wand, "InstanceId", "wand"); Set(wand, "CurrentMana", 37f); Set(wand, "CooldownRemaining", 1.25f); Set(wand, "ProgramRevision", 17u);
            var wands = Array.CreateInstance(wand.GetType(), 1); wands.SetValue(wand, 0); Set(snapshot, "Wands", wands);
            var spell = Activator.CreateInstance(RunType("OnlineInventorySpellUsePresentationSnapshot"));
            Set(spell, "InstanceId", "card"); Set(spell, "Eligible", true);
            var spells = Array.CreateInstance(spell.GetType(), 1); spells.SetValue(spell, 0); Set(snapshot, "SpellUses", spells);
            return snapshot;
        }
        private static OnlineOwnedAssetSnapshot Assets(object snapshot) => (OnlineOwnedAssetSnapshot)snapshot.GetType().GetField("Assets").GetValue(snapshot);
        private static Array Wands(object snapshot) => (Array)snapshot.GetType().GetField("Wands").GetValue(snapshot);
        private static Array SpellUses(object snapshot) => (Array)snapshot.GetType().GetField("SpellUses").GetValue(snapshot);
        private static Type RunType(string name)
        {
            var type = typeof(GameLog).Assembly.GetType("Game.Core." + name);
            Assert.That(type, Is.Not.Null, "缺少 Core 中性库存合同：" + name); return type;
        }
        private static void Set(object target, string name, object value) => target.GetType().GetField(name).SetValue(target, value);
    }
}
