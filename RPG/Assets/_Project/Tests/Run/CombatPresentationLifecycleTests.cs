using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Combat;
using Game.Core;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Game.Run.Tests
{
    /// <summary>验证共用 HUD 的资源、身份和释放边界；不初始化存档、资源 Tick 或第二份玩家资产。</summary>
    public sealed class CombatPresentationLifecycleTests
    {
        private const BindingFlags PublicStatic = BindingFlags.Public | BindingFlags.Static;
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly List<Scene> _scenes = new List<Scene>();
        private Type _hostType, _contextType, _scopeType;
        private Component _createdHost;

        [SetUp]
        public void RequireLifecycleContract()
        {
            _hostType = typeof(HeldItemHud).Assembly.GetType("Game.UI.CombatPresentationHost");
            _contextType = typeof(HeldItemHud).Assembly.GetType("Game.UI.CombatPresentationContext");
            _scopeType = typeof(HeldItemHud).Assembly.GetType("Game.UI.CombatPresentationScope");
            Assert.That(_hostType, Is.Not.Null, "缺少按需常驻的共用 HUD Host，不能继续给每张地图复制一份界面。");
            Assert.That(_contextType, Is.Not.Null); Assert.That(_scopeType, Is.Not.Null);
            Assert.That(Active(), Is.Null, "测试开始时不应遗留其他用例创建的 Host。");
        }

        [TearDown]
        public void ReleaseOnlyFixtureObjects()
        {
            // 本测试的 Host 来自首次成功 Acquire；不删除用户 Scene 或其他全局输入对象。
            if (_createdHost != null) Object.DestroyImmediate(_createdHost.gameObject);
            _createdHost = null;
            for (int i = _scenes.Count - 1; i >= 0; i--)
                if (_scenes[i].IsValid() && _scenes[i].isLoaded) EditorSceneManager.ClosePreviewScene(_scenes[i]);
            _scenes.Clear();
        }

        [Test]
        public void DifferentCombatAreasReuseHostAndHudWithoutMovingGameplayObjects()
        {
            var a = Area("FirstArea"); var b = Area("SecondArea");
            var previousNetworkActor = LocalActorPresentation.Actor;
            object firstLease = Acquire(a.Owner, Context(a));
            var host = Active(); var hud = Get<GameObject>(host, "HudRoot");
            Assert.That(hud, Is.Not.Null, "必须实际加载导出的 Presentation/CombatHud 资源，而非只生成一个空 Host。");
            object secondLease = Acquire(b.Owner, Context(b));
            Assert.That(Active(), Is.SameAs(host));
            Assert.That(Get<GameObject>(host, "HudRoot"), Is.SameAs(hud));
            Assert.That(Get<GameObject>(host, "Actor"), Is.SameAs(b.Actor));
            Assert.That(a.Actor.scene, Is.EqualTo(a.World)); Assert.That(b.Actor.scene, Is.EqualTo(b.World));
            Assert.That(a.Source.gameObject.scene, Is.EqualTo(a.World)); Assert.That(b.Source.gameObject.scene, Is.EqualTo(b.World));
            Assert.That(a.Actor.transform.parent, Is.Null); Assert.That(b.Source.transform.parent, Is.Null,
                "DontDestroyOnLoad 只作用于纯 UI 根，不能把 Session 或 Actor reparent 到常驻 Host。");
            Assert.That(LocalActorPresentation.Actor, Is.SameAs(previousNetworkActor),
                "单机 HUD 的 Scope 不能改写网络组合层的 Actor/CameraTarget 缓存。");
            Release(firstLease); Release(secondLease);
        }

        [Test]
        public void OldLeaseCannotUpdateOrReleaseNewOwner()
        {
            var a = Area("OldOwner"); var b = Area("CurrentOwner");
            var oldStage = LegacyStage(a, "OldStage", true, true, false);
            var currentStage = LegacyStage(b, "CurrentStage", true, true, false);
            object oldLease = Acquire(a.Owner, Context(a));
            object currentLease = Acquire(b.Owner, Context(b));
            Assert.That(oldStage.Adapter.enabled, Is.True); Assert.That(oldStage.Gameplay.activeSelf, Is.True);
            Assert.That(currentStage.Adapter.enabled, Is.False); Assert.That(currentStage.Gameplay.activeSelf, Is.False);
            Assert.That(_hostType.GetMethod("TryUpdate", PublicStatic).Invoke(null, new[] { oldLease, Context(a) }), Is.False,
                "旧 Owner 的延迟更新不能抢回新 Context。");
            Release(oldLease);
            Assert.That(Get<bool>(Active(), "IsBound"), Is.True);
            Assert.That(Get<GameObject>(Active(), "Actor"), Is.SameAs(b.Actor));
            Assert.That(Get<IInventoryPresentationSource>(Active(), "InventorySource"), Is.SameAs(b.Source));
            Assert.That(currentStage.Adapter.enabled, Is.False); Assert.That(currentStage.Gameplay.activeSelf, Is.False,
                "旧lease释放不能恢复当前World被共同HUD接管的Stage。");
            Release(currentLease);
            Assert.That(currentStage.Adapter.enabled, Is.True); Assert.That(currentStage.Gameplay.activeSelf, Is.True);
        }

        [Test]
        public void ReleaseClearsActorAndSceneReadReferencesButKeepsReusableHud()
        {
            var a = Area("BoundArea");
            object lease = Acquire(a.Owner, Context(a));
            var host = Active(); var hud = Get<GameObject>(host, "HudRoot");
            var health = hud.GetComponentInChildren<PlayerHealthBar>(true);
            var status = hud.GetComponentInChildren<PlayerStatusBar>(true);
            var held = hud.GetComponentInChildren<HeldItemHud>(true);
            Assert.That(health, Is.Not.Null); Assert.That(status, Is.Not.Null); Assert.That(held, Is.Not.Null);
            Assert.That(Field(health, "_playerHealth"), Is.SameAs(a.Health));
            Assert.That(held.PresentationSource, Is.SameAs(a.Source));
            Assert.That(Field(held, "_backpack"), Is.SameAs(a.Inventory));

            Release(lease);
            Assert.That(Get<bool>(host, "IsBound"), Is.False);
            Assert.That(Get<GameObject>(host, "Actor"), Is.Null);
            Assert.That(Get<IInventoryPresentationSource>(host, "InventorySource"), Is.Null);
            Assert.That(Field(health, "_playerHealth"), Is.Null); Assert.That(Field(status, "_playerHealth"), Is.Null);
            Assert.That(held.PresentationSource, Is.Null); Assert.That(Field(held, "_backpack"), Is.Null);
            Assert.That(hud != null, Is.True); Assert.That(hud.activeSelf, Is.False,
                "释放当前场景数据时隐藏并保留同一份纯 UI，不能重新生成各地图自己的 HUD。");
            object next = Acquire(a.Owner, Context(a));
            Assert.That(Get<GameObject>(Active(), "HudRoot"), Is.SameAs(hud));
            Release(next);
        }

        [Test]
        public void DestroyedOldScopeReleasesOnlyItsOwnLease()
        {
            var a = Area("OldScope"); var b = Area("NewScope");
            var left = a.Owner.AddComponent(_scopeType); var right = b.Owner.AddComponent(_scopeType);
            _scopeType.GetMethod("Bind").Invoke(left, new[] { Context(a) });
            _createdHost = Active(); Assert.That(_createdHost, Is.Not.Null);
            _scopeType.GetMethod("Bind").Invoke(right, new[] { Context(b) });
            Assert.That(Get<bool>(right, "IsBound"), Is.True);
            // EditMode 显式驱动真实 OnDisable 释放入口，不依赖是否已进入 Play 的生命周期差异。
            _scopeType.GetMethod("OnDisable", PrivateInstance).Invoke(left, null);
            Object.DestroyImmediate(a.Owner);
            Assert.That(Get<GameObject>(Active(), "Actor"), Is.SameAs(b.Actor));
            Assert.That(Get<bool>(right, "IsBound"), Is.True);
            _scopeType.GetMethod("Clear").Invoke(right, null);
            Assert.That(Get<bool>(Active(), "IsBound"), Is.False);
        }

        [Test]
        public void RepeatedIdenticalContextKeepsOriginalInventoryOpen()
        {
            var area = Area("RepeatedContext"); object context = Context(area);
            object lease = Acquire(area.Owner, context);
            var host = Active(); var hud = Get<GameObject>(host, "HudRoot");
            try
            {
                // 只安排 UI 开闭状态，不初始化 Session、不模拟资产命令；检验离散 Run 重复事实不会中断 Tab/拖拽。
                typeof(SinglePlayerInventoryController).GetField("_open", PrivateInstance).SetValue(area.Inventory, true);
                Assert.That(_hostType.GetMethod("TryUpdate", PublicStatic).Invoke(null, new[] { lease, context }), Is.True);
                Assert.That(area.Inventory.IsOpen, Is.True,
                    "完全相同 Context 只是重复事实，不应 Close 原背包或重绑纯表现树。");
                Assert.That(Active(), Is.SameAs(host)); Assert.That(Get<GameObject>(host, "HudRoot"), Is.SameAs(hud));
            }
            finally { Release(lease); }
            Assert.That(area.Inventory.IsOpen, Is.False, "真正释放当前 lease 时仍应正常关闭原背包并释放输入。");
        }

        [Test]
        public void UnsafeLegacyCompositionRejectsWithoutClosingUnacquiredInventoryOrKeepingLease()
        {
            var area = Area("RejectedComposition");
            var mixed = new GameObject("LegacyHudMixedWithGameplay");
            SceneManager.MoveGameObjectToScene(mixed, area.World);
            mixed.AddComponent<HealthComponent>(); mixed.AddComponent<PlayerHealthBar>();
            typeof(SinglePlayerInventoryController).GetField("_open", PrivateInstance).SetValue(area.Inventory, true);
            var args = new object[] { area.Owner, Context(area), null };
            try
            {
                bool accepted = true;
                // 原 HUD 带 Gameplay Owner 时需要可读拒绝；不能先关原面板，或把抑制异常抛给正常玩家流程。
                Assert.DoesNotThrow(() => accepted = (bool)_hostType.GetMethod("TryAcquire", PublicStatic).Invoke(null, args));
                Assert.That(accepted, Is.False);
                Assert.That(mixed.activeSelf, Is.True, "抑制预检失败必须保留原界面。");
                Assert.That(area.Inventory.IsOpen, Is.True, "未成功取得 Context 的原背包不归 Host 关闭。");
                Assert.That((bool)args[2].GetType().GetProperty("IsValid").GetValue(args[2]), Is.False);
                var host = Active();
                if (host != null)
                {
                    Assert.That(Get<bool>(host, "IsBound"), Is.False);
                    Assert.That(Get<GameObject>(host, "Actor"), Is.Null);
                    Assert.That(Get<IInventoryPresentationSource>(host, "InventorySource"), Is.Null);
                    Assert.That(Get<GameObject>(host, "HudRoot").activeSelf, Is.False);
                }
            }
            finally { _createdHost = Active(); }
        }

        [Test]
        public void FailedContextReplacementClearsLeaseButDoesNotCloseUnacquiredNewController()
        {
            var area = Area("RejectedUpdate"); object lease = Acquire(area.Owner, Context(area));
            var nextRoot = new GameObject("UnacquiredNewInventory"); nextRoot.SetActive(false);
            SceneManager.MoveGameObjectToScene(nextRoot, area.World);
            var nextSource = nextRoot.AddComponent<SinglePlayerInventorySession>();
            var nextInventory = nextRoot.AddComponent<SinglePlayerInventoryController>();
            typeof(SinglePlayerInventoryController).GetField("_open", PrivateInstance).SetValue(nextInventory, true);
            var mixed = new GameObject("ReplacementHudMixedWithGameplay"); SceneManager.MoveGameObjectToScene(mixed, area.World);
            mixed.AddComponent<HealthComponent>(); mixed.AddComponent<PlayerHealthBar>();
            object next = Context(area);
            _contextType.GetField("InventorySource").SetValue(next, nextSource);
            _contextType.GetField("InventoryController").SetValue(next, nextInventory);
            bool accepted = true;
            Assert.DoesNotThrow(() => accepted = (bool)_hostType.GetMethod("TryUpdate", PublicStatic).Invoke(null, new[] { lease, next }));
            Assert.That(accepted, Is.False);
            Assert.That(nextInventory.IsOpen, Is.True, "更新失败时新控制器尚未交接给 Host，不能 Close 它的面板。");
            Assert.That(mixed.activeSelf, Is.True);
            Assert.That(Get<bool>(Active(), "IsBound"), Is.False);
            Assert.That(Get<GameObject>(Active(), "Actor"), Is.Null);
            Assert.That(Get<IInventoryPresentationSource>(Active(), "InventorySource"), Is.Null);
            Release(lease);
            Assert.That(nextInventory.IsOpen, Is.True, "失败前的旧 lease 随后释放也不能触碰未取得的新背包。");
        }

        [Test]
        public void DungeonHudReplaysLateAcquisitionAndClearsSceneSourceOnRelease()
        {
            var area = Area("DungeonHudReplay"); var source = new DungeonSource();
            Assert.That(DungeonPresentation.Bind(area.Owner.GetInstanceID(), area.World.handle, source), Is.True);
            try
            {
                object lease = Acquire(area.Owner, Context(area));
                var stage = Get<GameObject>(Active(), "HudRoot").GetComponentInChildren<DemoRunHudController>(true);
                Assert.That(stage, Is.Not.Null, "共用Resource应实际复用原关卡提示Controller，而不是另写一套线上样式。");
                Assert.That(Field(stage, "_presentationSource"), Is.SameAs(source), "网络进度先到、HUD后到也必须重放当前World的读源。");
                Release(lease);
                Assert.That(Field(stage, "_presentationSource"), Is.Null, "DDOL只能保留纯表现，离开时不能继续持有场景Runtime。");
            }
            finally { DungeonPresentation.Clear(area.Owner.GetInstanceID(), source); }
        }

        [Test]
        public void DungeonHudAcceptsOnlyCurrentWorldSourceAndRefreshesOnLateBind()
        {
            var area = Area("CurrentDungeon"); var other = Area("OtherDungeon");
            object lease = Acquire(area.Owner, Context(area));
            var stage = Get<GameObject>(Active(), "HudRoot").GetComponentInChildren<DemoRunHudController>(true);
            Assert.That(stage, Is.Not.Null);
            var wrong = new DungeonSource(); var current = new DungeonSource();
            // 普通MonoBehaviour在EditMode不会自动执行OnEnable；本组只测事件路由，显式驱动真实入口，
            // 真正Unity生命周期另由CombatPresentationRuntimeTests的PlayMode用例验证。
            _hostType.GetMethod("OnEnable", PrivateInstance).Invoke(Active(), null);
            try
            {
                Assert.That(Field(stage, "_presentationSource"), Is.Null);
                DungeonPresentation.Bind(other.Owner.GetInstanceID(), other.World.handle, wrong);
                Assert.That(Field(stage, "_presentationSource"), Is.Null, "其他additive Scene的全局通知不能污染当前关卡提示。");
                DungeonPresentation.Bind(area.Owner.GetInstanceID(), area.World.handle, current);
                Assert.That(Field(stage, "_presentationSource"), Is.SameAs(current));
                DungeonPresentation.Clear(other.Owner.GetInstanceID(), wrong);
                Assert.That(Field(stage, "_presentationSource"), Is.SameAs(current), "旧Runtime迟到Dispose不得清掉新World的读源。");
                DungeonPresentation.Clear(area.Owner.GetInstanceID(), current);
                Assert.That(Field(stage, "_presentationSource"), Is.Null);
            }
            finally
            {
                _hostType.GetMethod("OnDisable", PrivateInstance).Invoke(Active(), null);
                Release(lease); DungeonPresentation.Clear(area.Owner.GetInstanceID(), current);
                DungeonPresentation.Clear(other.Owner.GetInstanceID(), wrong);
            }
        }

        [TestCase(true, true, false)]
        [TestCase(false, false, true)]
        public void LegacyStageSuppressionHidesBothLeavesWithoutStoppingSinglePlayerOwnerOrSiblingHealth(
            bool originallyEnabled, bool gameplayActive, bool notificationActive)
        {
            var area = Area("StageLeafOwnership");
            var run = area.Owner.AddComponent<DemoRunController>();
            var stage = LegacyStage(area, "OriginalStage", originallyEnabled, gameplayActive, notificationActive);
            var sibling = new GameObject("OriginalActorHealthSibling"); sibling.transform.SetParent(stage.Root.transform, false);
            var siblingHealth = sibling.AddComponent<HealthComponent>();
            typeof(DemoRunHudController).GetField("_runController", PrivateInstance).SetValue(stage.Adapter, run);
            var suppression = new CombatHudLegacySuppression();
            try
            {
                suppression.Suppress(area.World, default, null);
                Assert.That(stage.Gameplay.activeSelf, Is.False, "Stage的三条区域文本必须交给同一份DDOL HUD，不能留在Scene内重叠显示。");
                Assert.That(stage.Notification.activeSelf, Is.False);
                Assert.That(stage.Adapter.enabled, Is.False, "Adapter位于叶树外，必须一同暂停表现订阅，防止它重新打开通知。");
                Assert.That(stage.Root.activeSelf, Is.True, "不能关闭包含兄弟内容的Stage祖先或Canvas。");
                Assert.That(run.enabled, Is.True); Assert.That(area.Owner.activeSelf, Is.True, "单机Run Owner不归HUD抑制器管理。");
                Assert.That(sibling.activeSelf, Is.True); Assert.That(siblingHealth.enabled, Is.True);
            }
            finally { suppression.Restore(); }
            Assert.That(stage.Adapter.enabled, Is.EqualTo(originallyEnabled)); Assert.That(stage.Gameplay.activeSelf, Is.EqualTo(gameplayActive));
            Assert.That(stage.Notification.activeSelf, Is.EqualTo(notificationActive), "恢复原activeSelf，而非把原本隐藏的通知强行打开。");
        }

        [Test]
        public void UnsafeStageLeafRejectsBeforeMutatingAnyValidLegacyStage()
        {
            var area = Area("StageAtomicPreflight");
            var first = LegacyStage(area, "FirstSafeStage", true, true, true);
            var unsafeStage = LegacyStage(area, "UnsafeStage", true, true, false);
            unsafeStage.Gameplay.AddComponent<HealthComponent>();
            var suppression = new CombatHudLegacySuppression();
            try
            {
                Assert.Throws<InvalidOperationException>(() => suppression.Suppress(area.World, default, null),
                    "Stage的表现叶树误含Gameplay Owner时，必须在任何enabled/active变更前整体拒绝。");
                Assert.That(first.Adapter.enabled, Is.True); Assert.That(first.Gameplay.activeSelf, Is.True);
                Assert.That(first.Notification.activeSelf, Is.True, "后一个错误Stage不能让前一个合法Stage留下半抑制状态。");
                Assert.That(unsafeStage.Adapter.enabled, Is.True); Assert.That(unsafeStage.Gameplay.activeSelf, Is.True);
                Assert.That(unsafeStage.Notification.activeSelf, Is.False);
            }
            finally { suppression.Restore(); }
            Assert.That(first.Adapter.enabled, Is.True); Assert.That(first.Gameplay.activeSelf, Is.True);
            Assert.That(first.Notification.activeSelf, Is.True);
        }

        [Test]
        public void SuppressedStageIgnoresCallbacksAlreadyCapturedByCurrentEventDispatch()
        {
            var area = Area("StageMulticastSnapshot"); var stage = LegacyStage(area, "OldStage", true, true, false);
            stage.Adapter.BindPresentationSource(new DungeonSource { Phase = OnlineDungeonPhase.Active });
            var suppression = new CombatHudLegacySuppression();
            Action<EncounterProgressChangedEvent> suppressFirst = _ => suppression.Suppress(area.World, default, null);
            // 先登记接管回调，再登记原Stage：同一次Publish已取到多播委托快照，退订不能改写此快照。
            EventBus<EncounterProgressChangedEvent>.Subscribe(suppressFirst);
            typeof(DemoRunHudController).GetMethod("OnEnable", PrivateInstance).Invoke(stage.Adapter, null);
            try
            {
                EventBus<EncounterProgressChangedEvent>.Publish(new EncounterProgressChangedEvent { EncounterIndex = 0, RemainingEnemyCount = 2 });
                Assert.That(stage.Adapter.enabled, Is.False); Assert.That(stage.Gameplay.activeSelf, Is.False);
                Assert.That(stage.Notification.activeSelf, Is.False);
                // 普通MonoBehaviour在EditMode没有自动OnDisable，故意保留已登记旧回调，
                // 继续用真实事件验证每个入口都尊重enabled，而非只在ShowNotification末端隐藏。
                EventBus<RunStateChangedEvent>.Publish(new RunStateChangedEvent { CurrentState = RunState.Running });
                Assert.That(stage.Gameplay.activeSelf, Is.False); Assert.That(stage.Notification.activeSelf, Is.False);
                EventBus<EncounterStartedEvent>.Publish(new EncounterStartedEvent { EncounterIndex = 0, EnemyCount = 2 });
                Assert.That(stage.Gameplay.activeSelf, Is.False); Assert.That(stage.Notification.activeSelf, Is.False);
            }
            finally
            {
                EventBus<EncounterProgressChangedEvent>.Unsubscribe(suppressFirst);
                typeof(DemoRunHudController).GetMethod("OnDisable", PrivateInstance).Invoke(stage.Adapter, null);
                suppression.Restore();
            }
        }

        private StageObjects LegacyStage(AreaObjects area, string name, bool enabled, bool gameplayActive, bool notificationActive)
        {
            var root = new GameObject(name); root.SetActive(false); SceneManager.MoveGameObjectToScene(root, area.World);
            var adapter = root.AddComponent<DemoRunHudController>();
            var gameplay = new GameObject("GameplayHUD"); gameplay.transform.SetParent(root.transform, false);
            var notification = new GameObject("NotificationRoot"); notification.transform.SetParent(root.transform, false);
            typeof(DemoRunHudController).GetField("_gameplayRoot", PrivateInstance).SetValue(adapter, gameplay);
            typeof(DemoRunHudController).GetField("_notificationRoot", PrivateInstance).SetValue(adapter, notification);
            adapter.enabled = enabled; gameplay.SetActive(gameplayActive); notification.SetActive(notificationActive); root.SetActive(true);
            return new StageObjects { Root = root, Adapter = adapter, Gameplay = gameplay, Notification = notification };
        }

        private sealed class StageObjects
        {
            internal GameObject Root, Gameplay, Notification;
            internal DemoRunHudController Adapter;
        }

        private sealed class DungeonSource : IDungeonPresentationReadOnly
        {
            internal OnlineDungeonPhase Phase = OnlineDungeonPhase.Armed;
            public int EncounterCount => 1;
            public string GetDisplayName(int index) => "当前区域";
            public string GetObjectiveText(int index) => "击败当前区域敌人";
            public bool TryCaptureProgress(out OnlineDungeonProgress progress)
            {
                progress = new OnlineDungeonProgress { Revision = 1, EncounterIndexPlusOne = 1,
                    Phase = Phase, RemainingEnemies = 2 };
                return true;
            }
        }

        private object Acquire(GameObject owner, object context)
        {
            var args = new object[] { owner, context, null };
            bool accepted = (bool)_hostType.GetMethod("TryAcquire", PublicStatic).Invoke(null, args);
            _createdHost = Active();
            Assert.That(accepted, Is.True, "真实导出的 Resources/Presentation/CombatHud 或 Context 未就绪，不能伪称已装配。");
            Assert.That(_createdHost, Is.Not.Null); return args[2];
        }
        private void Release(object lease) => _hostType.GetMethod("Release", PublicStatic).Invoke(null, new[] { lease });
        private Component Active() => _hostType == null ? null : (Component)_hostType.GetProperty("Active", PublicStatic).GetValue(null);
        private static T Get<T>(object target, string name) => (T)target.GetType().GetProperty(name).GetValue(target);
        private static object Field(object target, string name) => target.GetType().GetField(name, PrivateInstance).GetValue(target);

        private object Context(AreaObjects area)
        {
            object context = Activator.CreateInstance(_contextType);
            _contextType.GetField("Actor").SetValue(context, area.Actor);
            _contextType.GetField("WorldScene").SetValue(context, area.World);
            _contextType.GetField("InventorySource").SetValue(context, area.Source);
            _contextType.GetField("InventoryController").SetValue(context, area.Inventory);
            _contextType.GetField("CrosshairVisible").SetValue(context, true);
            return context;
        }

        private AreaObjects Area(string name)
        {
            var scene = EditorSceneManager.NewPreviewScene(); _scenes.Add(scene);
            var owner = new GameObject(name + "Owner"); SceneManager.MoveGameObjectToScene(owner, scene);
            var actor = new GameObject(name + "Actor"); actor.SetActive(false); SceneManager.MoveGameObjectToScene(actor, scene);
            var health = actor.AddComponent<HealthComponent>();
            var sourceRoot = new GameObject(name + "Session"); sourceRoot.SetActive(false); SceneManager.MoveGameObjectToScene(sourceRoot, scene);
            var source = sourceRoot.AddComponent<SinglePlayerInventorySession>();
            var inventory = sourceRoot.AddComponent<SinglePlayerInventoryController>();
            // Session 没有 Awake/Initialize，不读取用户档；本组只验证其只读引用的生命周期。
            return new AreaObjects { Owner = owner, Actor = actor, Source = source, Inventory = inventory, Health = health, World = scene };
        }
        private sealed class AreaObjects
        {
            internal GameObject Owner, Actor;
            internal SinglePlayerInventorySession Source;
            internal SinglePlayerInventoryController Inventory;
            internal HealthComponent Health;
            internal Scene World;
        }
    }
}
