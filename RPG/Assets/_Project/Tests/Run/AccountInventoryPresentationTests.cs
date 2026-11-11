using System;
using System.Reflection;
using Game.Core;
using Game.Character;
using Game.UI;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Run.Tests
{
    /// <summary>账号Hub桥只读显示；不读取旧档，不创建Run、资格、物品奖励或实际HTTP。</summary>
    public sealed class AccountInventoryPresentationTests : InventoryTestFixture
    {
        private ItemCatalogIndex _catalog;
        private const string Player = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        [SetUp] public void Catalog()
        {
            _catalog = Store(Define("account.bag", ItemKind.Backpack, bagWidth: 8, bagHeight: 8),
                Define("account.card", ItemKind.Spell), Define("account.wand", ItemKind.Wand),
                Define("account.bottle", ItemKind.Container)).Catalog;
        }

        [Test]
        public void AccountToolChoiceIsSharedAcrossIndependentViewsWithoutChangingConfirmedAssets()
        {
            var value = ToolSnapshot();
            using var source = (AccountInventoryPresentationSource)Create(value);
            using var secondView = (AccountInventoryPresentationSource)Create(value);
            ShareHeldSelection(secondView, source);
            uint version = source.Version; uint selection = source.SelectionVersion;
            Assert.That(SelectTool(source, 0), Is.True);
            Assert.That(source.SelectedSlot, Is.EqualTo(HeldItemSlots.WandCount));
            Assert.That(secondView.SelectedItemId, Is.EqualTo("bottle"), "Tab、仓库和地面投影必须共享同一个Scene内选择。");
            Assert.That(secondView.SelectionVersion, Is.EqualTo(source.SelectionVersion));
            Assert.That(source.SelectionVersion, Is.GreaterThan(selection));
            Assert.That(source.Version, Is.EqualTo(version)); Assert.That(source.ReadOnly.Version, Is.EqualTo(version));
            Assert.That(source.TryCopyContainer("bottle", new MaterialAmount[8], out _, out var total, out _), Is.True);
            Assert.That(total, Is.EqualTo(17ul)); Assert.That(source.WandsReadOnly.SelectedWandId, Is.EqualTo("wand"));
            Assert.That(source is IOnlineInventoryCommandContext, Is.False);
            secondView.Dispose();
            Assert.That(source.SelectedItemId, Is.EqualTo("bottle"), "关闭一个Canvas不能清掉其他消费者仍持有的选择。");
        }

        [Test]
        public void AccountProfileRefreshPreservesRealToolSlotAndClearsItWhenTheItemLeaves()
        {
            var value = ToolSnapshot();
            using var before = (AccountInventoryPresentationSource)Create(value);
            Assert.That(SelectTool(before, 0), Is.True);
            value.Version++;
            using var refreshed = (AccountInventoryPresentationSource)Create(value);
            ShareHeldSelection(refreshed, before);
            before.Dispose();
            Assert.That(refreshed.SelectedItemId, Is.EqualTo("bottle"));
            uint selected = refreshed.SelectionVersion;
            value.Version++;
            var bottle = value.Items[value.Items.Length - 1];
            bottle.LocationKind = (int)ItemLocationKind.Backpack; bottle.ParentInstanceId = "bag"; bottle.Slot = -1; bottle.X = 3;
            using var afterMove = (AccountInventoryPresentationSource)Create(value);
            ShareHeldSelection(afterMove, refreshed);
            Assert.That(afterMove.SelectedItemId, Is.EqualTo("wand"));
            Assert.That(afterMove.SelectedSlot, Is.Zero);
            Assert.That(afterMove.SelectionVersion, Is.GreaterThan(selected));
            Assert.That(SelectTool(afterMove, 0), Is.False);
            Assert.That(afterMove.ReadOnly.TryGetView("bottle", out var actual), Is.True);
            Assert.That(actual.Location.Kind, Is.EqualTo(ItemLocationKind.Backpack));
        }

        [Test]
        public void AccountToolChoiceRejectsEmptySlotsAndCannotCrossAccountOrDisposedSources()
        {
            using var source = (AccountInventoryPresentationSource)Create(ToolSnapshot());
            Assert.That(SelectTool(source, -1), Is.False); Assert.That(SelectTool(source, 7), Is.False);
            Assert.That(SelectTool(source, 1), Is.False); Assert.That(source.SelectedItemId, Is.EqualTo("wand"));
            var other = ToolSnapshot(); other.PlayerId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            using var foreign = (AccountInventoryPresentationSource)Create(other);
            Assert.That(ShareHeldSelection(foreign, source), Is.False);
            source.Dispose(); Assert.That(SelectTool(source, 0), Is.False);
        }

        private OnlineAccountInventorySnapshot ToolSnapshot()
        {
            var value = (OnlineAccountInventorySnapshot)Snapshot(12347);
            var bottle = Item("bottle", "account.bottle", (int)ItemLocationKind.EquippedTool, "", 0);
            bottle.RemainingCharges = 0; // 只有Spell具有次数；原通用Item帮助函数默认4次不能用于真实容器。
            bottle.ContainerState = new OnlineOwnedContainerStateSnapshot { SchemaVersion = 1,
                Components = new[] { new OnlineOwnedMaterialAmountSnapshot { MaterialKey = 1, Quantity = 17 } } };
            Array.Resize(ref value.Items, value.Items.Length + 1); value.Items[value.Items.Length - 1] = bottle;
            return value;
        }

        private static bool SelectTool(AccountInventoryPresentationSource source, int slot)
        {
            var method = source.GetType().GetMethod("TrySelectToolForPresentation", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "账号工具选择需要独立只读Presentation入口，不能再借旧Session资产写口。");
            return (bool)method.Invoke(source, new object[] { slot });
        }
        private static bool ShareHeldSelection(AccountInventoryPresentationSource source, AccountInventoryPresentationSource previous)
        {
            var method = source.GetType().GetMethod("TryShareHeldSelectionFrom", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "独立Canvas读模型必须共享Session选择，不能各有一套手持状态。");
            return (bool)method.Invoke(source, new object[] { previous });
        }

        [Test]
        public void HubHeldInputSelectsTheConfirmedAccountToolAndKeepsAllLocalGameplayWritesBlocked()
        {
            const int owner = 70523;
            var scene = EditorSceneManager.NewPreviewScene(); scene.name = "AccountHeldInput_" + Guid.NewGuid().ToString("N");
            var root = new GameObject("Account held input session"); root.SetActive(false); SceneManager.MoveGameObjectToScene(root, scene);
            var actor = new GameObject("Account held input actor"); SceneManager.MoveGameObjectToScene(actor, scene);
            float scale = Time.timeScale;
            typeof(OnlineAccountInventoryHubScope).GetMethod("Reset", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            using var source = (AccountInventoryPresentationSource)Create(ToolSnapshot());
            try
            {
                Assert.That(OnlineAccountInventoryHubScope.TryPublish(owner, ToolSnapshot(), out var error), Is.True, error);
                Assert.That(OnlineAccountInventoryHubScope.TryArmHubEntry(owner, scene.name, out error), Is.True, error);
                // PreviewScene不在SceneManager.sceneCount里；沿正式Session的Scene入口先消费本次Hub声明，
                // 再安装只读桥，不能把“尚未绑定Scope”的fixture失败误判为数字键拒绝了已确认账号。
                Assert.That(OnlineAccountInventoryHubScope.IsRequiredForScene(scene), Is.True);
                var session = root.AddComponent<SinglePlayerInventorySession>();
                Assert.That(session.RequireAccountInventoryReadOnly(scene.handle), Is.True);
                typeof(SinglePlayerInventorySession).GetField("_accountGroundSource", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(session, source);
                typeof(SinglePlayerInventorySession).GetField("WorldActor", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(session, actor.transform);
                root.SetActive(true);
                var input = actor.AddComponent<HeldItemInputController>(); input.ConfigurePractice(session);
                Assert.That(session.TryGetAccountHeldPresentation(out var installed), Is.True, "真实输入前必须已有同Scene账号投影。");
                Assert.That(installed, Is.SameAs(source));
                Time.timeScale = 1;
                input.Select(HeldItemSlots.WandCount);
                Assert.That(source.SelectedItemId, Is.EqualTo("bottle"), "真实数字键入口不能再因旧Session.ReadOnly为空而失效。");
                Assert.That(input.CanUseWorld, Is.False); Assert.That(input.CanPour, Is.False);
                Assert.That(session.ReadOnly, Is.Null); Assert.That(session.WorldActionsAllowed, Is.False);
                Assert.That(session.Save().Status, Is.EqualTo(ProfilePersistenceStatus.WriteBlocked));
                Time.timeScale = 0; input.Select(HeldItemSlots.WandCount + 1);
                Assert.That(source.SelectedItemId, Is.EqualTo("bottle"));
                Time.timeScale = 1;
                Assert.That(OnlineAccountInventoryHubScope.TryMarkAssetsLocked(owner, Player, "cccccccccccccccccccccccccccccccc"), Is.True);
                input.Select(HeldItemSlots.WandCount);
                Assert.That(input.CanUseWorld, Is.False); Assert.That(session.ReadOnly, Is.Null);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene); Time.timeScale = scale;
                typeof(OnlineAccountInventoryHubScope).GetMethod("Reset", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            }
        }

        [TestCase(0L)] [TestCase(12347L)] [TestCase(long.MaxValue)]
        public void FullAccountSourceShowsRealWalletCarriedAndWarehouseWithoutRunIdentity(long gold)
        {
            var value = Snapshot(gold); var source = Create(value);
            try
            {
                Assert.That(source.WalletReadOnly.Currency, Is.EqualTo(gold));
                Assert.That(source.ReadOnly.ActorId, Is.EqualTo(Player));
                Assert.That(source.ReadOnly.Version, Is.EqualTo(9));
                Assert.That(source.ReadOnly.InventoryId, Does.StartWith("account."));
                Assert.That(source.ReadOnly.Count, Is.EqualTo(5));
                Assert.That(source.ReadOnly.WarehouseCount, Is.EqualTo(1));
                Assert.That(source.CanAccessWarehouse, Is.True);
                Assert.That(source.ReadOnly.TryGetView("installed", out var installed), Is.True);
                Assert.That(installed.Location.OwnerId, Is.EqualTo("wand"));
                Assert.That(installed.RemainingCharges, Is.EqualTo(4));
                Assert.That(source.ReadOnly.TryGetView("bag-card", out var carried), Is.True);
                Assert.That(carried.Location.OwnerId, Is.EqualTo("bag"));
                Assert.That(source is IOnlineInventoryCommandContext, Is.False, "Hub不是伪造Run/Actor，不能获得DS意图权限。");
                Assert.That(source.HasUnsavedChanges, Is.False);
            }
            finally { (source as IDisposable)?.Dispose(); }
        }

        [Test] public void SourceOwnsValuesAndDoesNotKeepMutableProfileItems()
        {
            var value = Snapshot(12347); var source = Create(value);
            try
            {
                Set(value, "Gold", 0L);
                ((OnlineOwnedItemSnapshot[])Get(value, "Items"))[2].RemainingCharges = 999;
                Assert.That(source.WalletReadOnly.Currency, Is.EqualTo(12347));
                Assert.That(source.ReadOnly.TryGetView("installed", out var card), Is.True);
                Assert.That(card.RemainingCharges, Is.EqualTo(4));
            }
            finally { (source as IDisposable)?.Dispose(); }
        }

        [TestCase(true, "")] [TestCase(false, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
        public void LockedOrStillRunningAccountCannotBecomeUnlockedHubSource(bool locked, string run)
        {
            var value = Snapshot(12347); Set(value, "AssetsLocked", locked); Set(value, "ActiveRunId", run);
            Assert.That(TryCreate(value, out var source), Is.False); Assert.That(source, Is.Null);
        }

        [TestCase(-1L, 9u)] [TestCase(100L, 0u)]
        public void UnknownVersionOrNegativeBalanceCannotBecomeRealAccountInventory(long gold, uint version)
        {
            var value = Snapshot(gold); Set(value, "Version", version);
            Assert.That(TryCreate(value, out var source), Is.False); Assert.That(source, Is.Null);
        }

        [Test] public void BrokenParentIsRejectedInsteadOfDroppingTheMissingItem()
        {
            var value = Snapshot(12347);
            ((OnlineOwnedItemSnapshot[])Get(value, "Items"))[2].ParentInstanceId = "missing-wand";
            Assert.That(TryCreate(value, out var source), Is.False); Assert.That(source, Is.Null);
        }

        [Test] public void NonCanonicalWideLocationCannotAliasWarehouseThroughByteNarrowing()
        {
            var value = Snapshot(12347);
            ((OnlineOwnedItemSnapshot[])Get(value, "Items"))[4].LocationKind = 256;
            Assert.That(TryCreate(value, out var source), Is.False, "LocationKind的原始整数必须先校验，不能窄化后变成合法仓库。");
            Assert.That(source, Is.Null);
        }

        [Test] public void MaximumAccountVersionStillBuildsTheActualFirstWandProgram()
        {
            var value = Snapshot(12347); Set(value, "Version", uint.MaxValue);
            var source = Create(value);
            try
            {
                Assert.That(source.ReadOnly.Version, Is.EqualTo(uint.MaxValue));
                Assert.That(source.WandsReadOnly.TryGetProgram("wand", out var program), Is.True,
                    "合法最大版本不能碰到新投影的缓存初值而跳过实际已装卡程序。");
                Assert.That(program, Is.Not.Null);
            }
            finally { (source as IDisposable)?.Dispose(); }
        }

        [Test] public void DisposedSourceRemovesReadCapabilitiesInsteadOfFallingBackToLocalOrZero()
        {
            var source = Create(Snapshot(12347)); (source as IDisposable)?.Dispose();
            Assert.That(source.ReadOnly, Is.Null); Assert.That(source.WalletReadOnly, Is.Null);
        }

        [Test] public void SpecifiedHubReadOnlyGuardSurvivesEnableAndBlocksLocalSave()
        {
            var root = new GameObject("Inactive account Hub write guard test"); root.SetActive(false);
            SinglePlayerInventorySession session = null;
            int notifications = 0;
            Action<OnlineAccountInventoryHubChangedEvent> observer = _ => notifications++;
            EventBus<OnlineAccountInventoryHubChangedEvent>.Subscribe(observer);
            try
            {
                session = root.AddComponent<SinglePlayerInventorySession>();
                var method = typeof(SinglePlayerInventorySession).GetMethod("RequireAccountInventoryReadOnly");
                Assert.That(method, Is.Not.Null, "没有跨OnEnable保留的Hub旧写保护，恢复Root后会再次写本地资产。");
                Assert.That((bool)method.Invoke(session, new object[] { (int)root.scene.handle }), Is.True);
                typeof(SinglePlayerInventorySession).GetMethod("OnEnable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(session, null);
                Assert.That(session.Save().Status, Is.EqualTo(ProfilePersistenceStatus.WriteBlocked));
                Assert.That(typeof(SinglePlayerInventorySession).GetProperty("AccountInventoryReadOnly").GetValue(session), Is.EqualTo(true));
                // 夹具在 inactive 根上手动调用过 OnEnable，EditMode 销毁不承诺补发 OnDisable。
                // 成对驱动本实例退订，再证明下一次账号通知仍能发给其他消费者，不清全局 EventBus 掩盖泄漏。
                typeof(SinglePlayerInventorySession).GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(session, null);
                UnityEngine.Object.DestroyImmediate(root);
                Assert.DoesNotThrow(() => EventBus<OnlineAccountInventoryHubChangedEvent>.Publish(
                    new OnlineAccountInventoryHubChangedEvent { SceneHandle = -1 }));
                Assert.That(notifications, Is.EqualTo(1), "只释放本夹具Session，不能顺带删除其他有效订阅者。");
            }
            finally
            {
                if (session != null)
                    typeof(SinglePlayerInventorySession).GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(session, null);
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                EventBus<OnlineAccountInventoryHubChangedEvent>.Unsubscribe(observer);
            }
        }

        [Test] public void CoreConsumerAndControllerOfferInactiveHubPreparationWithoutNetworkDependency()
        {
            var type = typeof(GameLog).Assembly.GetType("Game.Core.IAccountInventoryHubConsumer");
            Assert.That(type, Is.Not.Null, "inactive Hub需要Core中立接缝，不能Multiplayer反向引用UI/Run。");
            Assert.That(type.IsAssignableFrom(typeof(SinglePlayerInventoryController)), Is.True);
            Assert.That(type.GetMethod("TryPrepareAccountInventory"), Is.Not.Null);
        }

        [Test] public void SpecifiedAccountHubCoordinatorCannotCheckpointTheLegacyRepository()
        {
            var root = new GameObject("Inactive account Hub coordinator test"); root.SetActive(false);
            try
            {
                var session = root.AddComponent<SinglePlayerInventorySession>();
                Assert.That(session.RequireAccountInventoryReadOnly((int)root.scene.handle), Is.True);
                var coordinator = root.AddComponent<SinglePlayerRunCoordinator>();
                typeof(SinglePlayerRunCoordinator).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(coordinator, session);
                Assert.That(coordinator.ActionsAllowed, Is.False, "账号Hub不得继续授权旧单机资产事务。");
                Assert.That(coordinator.Checkpoint().Status, Is.EqualTo(ProfilePersistenceStatus.WriteBlocked),
                    "即使inactive Hub没有加载旧Profile，检查点也必须先识别作用域，不能访问旧Repository。");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test] public void SpecifiedAccountHubCanChooseMatchThroughBothOriginalPortalsWithoutLocalWrites()
        {
            float scale = Time.timeScale; var cursor = Cursor.lockState; bool visible = Cursor.visible;
            foreach (var portalType in new[] { typeof(HubExpeditionPortal), typeof(HubPracticePortal) })
            {
                var context = new GameObject("Account Hub choice context"); context.SetActive(false);
                var actor = new GameObject("Account Hub choice actor");
                var door = new GameObject("Account Hub original choice portal");
                MonoBehaviour portal = null; int requested = 0, matched = 0;
                Action<DungeonEntryChoiceRequestedEvent> onRequest = e => requested++;
                Action<DungeonMatchRequestedEvent> onMatch = e => matched++;
                try
                {
                    var session = context.AddComponent<SinglePlayerInventorySession>();
                    var run = context.AddComponent<SinglePlayerRunCoordinator>();
                    var pause = context.AddComponent<RunPauseCoordinator>();
                    SetComponentField(run, "_session", session); SetComponentField(run, "_pause", pause);
                    typeof(SinglePlayerRunCoordinator).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(run, null);
                    var collider = actor.AddComponent<BoxCollider>();
                    door.AddComponent<BoxCollider>().isTrigger = true;
                    // 隔离视觉占位避免加载正式Prefab；交互仍走原Portal.Awake创建的真实ChoiceGate。
                    new GameObject("UnifiedPortalVisual").transform.SetParent(door.transform, false);
                    portal = (MonoBehaviour)door.AddComponent(portalType);
                    portalType.GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(portal, null);
                    SetComponentField(portal, "_session", session); SetComponentField(portal, "_player", actor.transform);
                    if (portal is HubPracticePortal) SetComponentField(portal, "_scenePath", "Assets/_Project/Scenes/P7_DemoRun.unity");
                    portalType.GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(portal, null);
                    portalType.GetMethod("OnEnable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(portal, null);
                    Physics.SyncTransforms();
                    var target = (IPortalInteractionTarget)portal;
                    Assert.That(target.CanInteract(collider), Is.True, "先确认原门空间/角色/阶段资格有效，避免把夹具错误当账号故障。");
                    Assert.That(session.RequireAccountInventoryReadOnly((int)context.scene.handle), Is.True);
                    Assert.That(run.ActionsAllowed, Is.False); // 旧资产写锁必须保留。
                    EventBus<DungeonEntryChoiceRequestedEvent>.Subscribe(onRequest);
                    EventBus<DungeonMatchRequestedEvent>.Subscribe(onMatch);
                    Assert.That(target.CanInteract(collider), Is.True, portalType.Name + "不能把旧档写锁当成禁止打开无写选择。");
                    Assert.That(target.Interact(collider), Is.True);
                    Assert.That(requested, Is.EqualTo(1));
                    EventBus<DungeonEntryChoiceSubmittedEvent>.Publish(new DungeonEntryChoiceSubmittedEvent
                    { PortalId = portal.gameObject.GetInstanceID(), Actor = collider, Mode = DungeonEntryMode.Matchmaking });
                    Assert.That(matched, Is.EqualTo(1));
                    Assert.That(run.HasWorld, Is.False);
                    Assert.That(run.Depart(), Is.False);
                    Assert.That(session.Save().Status, Is.EqualTo(ProfilePersistenceStatus.WriteBlocked));
                    Assert.That(run.Checkpoint().Status, Is.EqualTo(ProfilePersistenceStatus.WriteBlocked));
                }
                finally
                {
                    EventBus<DungeonEntryChoiceRequestedEvent>.Unsubscribe(onRequest);
                    EventBus<DungeonMatchRequestedEvent>.Unsubscribe(onMatch);
                    if (portal != null) portalType.GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(portal, null);
                    UnityEngine.Object.DestroyImmediate(door); UnityEngine.Object.DestroyImmediate(actor); UnityEngine.Object.DestroyImmediate(context);
                    Time.timeScale = scale; Cursor.lockState = cursor; Cursor.visible = visible;
                }
            }
        }

        private static void SetComponentField(MonoBehaviour component, string name, object value)
            => component.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(component, value);

        private object Snapshot(long gold)
        {
            var type = typeof(GameLog).Assembly.GetType("Game.Core.OnlineAccountInventorySnapshot");
            Assert.That(type, Is.Not.Null, "全账号只读Profile不能压缩为旧Lobby显示行或携带闭包。");
            var value = Activator.CreateInstance(type);
            Set(value, "SchemaVersion", 1u); Set(value, "PlayerId", Player); Set(value, "Version", 9u);
            Set(value, "Gold", gold); Set(value, "ContentManifestHash", new string('a', 64));
            Set(value, "AssetsLocked", false); Set(value, "ActiveRunId", ""); Set(value, "SelectedWandInstanceId", "wand");
            Set(value, "Items", new[]
            {
                Item("bag", "account.bag", 4, "", 0), Item("wand", "account.wand", 3, "", 0, true),
                Item("installed", "account.card", 2, "wand", 0), Item("bag-card", "account.card", 1, "bag", -1),
                Item("warehouse-card", "account.card", 0, "", 0)
            });
            return value;
        }

        private static OnlineOwnedItemSnapshot Item(string id, string content, int kind, string parent, int slot, bool template = false)
            => new OnlineOwnedItemSnapshot { InstanceId = id, ContentId = content, Revision = 2,
                LocationKind = kind, ParentInstanceId = parent, Slot = slot, RemainingCharges = template || kind == 4 ? 0 : 4,
                UseTemplateWandStats = template };

        private IInventoryPresentationSource Create(object snapshot)
        {
            Assert.That(TryCreate(snapshot, out var source), Is.True, _lastCreateError); return source;
        }
        private string _lastCreateError;
        private bool TryCreate(object snapshot, out IInventoryPresentationSource source)
        {
            var type = typeof(SinglePlayerProfile).Assembly.GetType("Game.Run.AccountInventoryPresentationSource");
            Assert.That(type, Is.Not.Null); var method = type.GetMethod("TryCreate", BindingFlags.Public | BindingFlags.Static);
            Assert.That(method, Is.Not.Null); object[] args = { _catalog, snapshot, null, null };
            bool result = (bool)method.Invoke(null, args); source = args[2] as IInventoryPresentationSource;
            _lastCreateError = args[3] as string; return result;
        }
        private static void Set(object value, string name, object fieldValue)
        { var field = value.GetType().GetField(name); Assert.That(field, Is.Not.Null, name); field.SetValue(value, fieldValue); }
        private static object Get(object value, string name) => value.GetType().GetField(name).GetValue(value);
    }
}
