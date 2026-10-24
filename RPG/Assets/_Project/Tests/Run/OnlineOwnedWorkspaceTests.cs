using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Combat;
using Game.Core;
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
            _catalog = Store(wand, spell).Catalog;
        }

        [TearDown]
        public void ReleaseWorkspaces()
        {
            for (int i = _workspaces.Count - 1; i >= 0; i--) _workspaces[i].Dispose();
            _workspaces.Clear();
        }

        [Test]
        public void OldSnapshotWithoutOwnedSchema_IsRejectedInsteadOfGrantingUnlimitedUses()
        {
            var seed = Seed(); Set(seed, "SchemaVersion", 0u);
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

        private object Seed(string inventory = "scope")
        {
            object seed = NewCore("OnlineOwnedAssetSnapshot");
            Set(seed, "SchemaVersion", 1u); Set(seed, "RunId", "run"); Set(seed, "PlayerId", "player");
            Set(seed, "InventoryId", inventory); Set(seed, "ActorId", 1ul); Set(seed, "InventoryVersion", 7u);
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
}
