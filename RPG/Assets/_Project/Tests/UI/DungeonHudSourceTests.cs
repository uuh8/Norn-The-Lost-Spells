using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Game.UI.Tests
{
    /// <summary>原Stage HUD继续承担显示，只更换只读来源；旧Run事件不能覆盖另一副本的目标或结束界面。</summary>
    public sealed class DungeonHudSourceTests
    {
        private class Source : IDungeonPresentationReadOnly
        {
            internal OnlineDungeonProgress Progress = new OnlineDungeonProgress { Revision = 2, ServerTick = 10,
                EncounterIndexPlusOne = 1, RemainingEnemies = 7, Phase = OnlineDungeonPhase.Active };
            internal bool Ready = true;
            public int EncounterCount => 2;
            public string GetDisplayName(int index) => index == 0 ? "线上Boss区域" : "后续区域";
            public string GetObjectiveText(int index) => index == 0 ? "离开安全区后击败Boss" : "进入下一处真实入口";
            public bool TryCaptureProgress(out OnlineDungeonProgress progress) { progress = Progress; return Ready; }
        }
        // 可选策略只提供显示选择；测试来源仍只读，不借助 Boss、网络或单机 Run Owner 改变玩法。
        private sealed class PolicySource : Source, IDungeonHudPresentationPolicy
        {
            internal bool EncounterHudVisible;
            public bool ShowEncounterHud => EncounterHudVisible;
        }
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly Source _a = new Source(), _b = new Source();
        private readonly PolicySource _policy = new PolicySource();
        private MethodInfo _bind;
        private Type _cache;
        private DemoRunHudController _hud;
        private Text _objective, _encounter, _remaining;
        private GameObject _gameplay;
        private GameObject _notification;
        private Text _notificationText;

        [SetUp] public void RequireReadOnlyComposition()
        {
            _a.Ready = _b.Ready = true;
            _policy.Ready = true; _policy.EncounterHudVisible = false;
            _policy.Progress = new OnlineDungeonProgress { Revision = 2, ServerTick = 10,
                EncounterIndexPlusOne = 1, RemainingEnemies = 1, Phase = OnlineDungeonPhase.Armed };
            _bind = typeof(DemoRunHudController).GetMethod("BindPresentationSource");
            Assert.That(_bind, Is.Not.Null, "原HUD需要中性Source，不能保留会Start/Death结算的单机Controller凑文本引用。");
            _cache = typeof(GameLog).Assembly.GetType("Game.Core.DungeonPresentation");
            Assert.That(_cache, Is.Not.Null, "晚出现的DDOL HUD需要Owner/World隔离的当前来源缓存。");
        }
        [TearDown] public void ClearOnlyThisFixture()
        {
            if (_hud != null) typeof(DemoRunHudController).GetMethod("OnDisable", Private).Invoke(_hud, null);
            if (_cache != null)
            {
                _cache.GetMethod("Clear").Invoke(null, new object[] { 101, _a });
                _cache.GetMethod("Clear").Invoke(null, new object[] { 101, _b });
                _cache.GetMethod("Clear").Invoke(null, new object[] { 202, _a });
                _cache.GetMethod("Clear").Invoke(null, new object[] { 202, _b });
                _cache.GetMethod("Clear").Invoke(null, new object[] { 101, _policy });
                _cache.GetMethod("Clear").Invoke(null, new object[] { 202, _policy });
            }
            for (int i = _objects.Count - 1; i >= 0; i--) if (_objects[i] != null) Object.DestroyImmediate(_objects[i]);
            _objects.Clear();
        }

        [Test] public void OriginalHudReadsOnlineMetadataWithoutSinglePlayerOwnerAndExplicitNullClearsIt()
        {
            CreateHud(); _bind.Invoke(_hud, new object[] { _a });
            Assert.That(_objective.text, Is.EqualTo("离开安全区后击败Boss"));
            Assert.That(_encounter.text, Does.Contain("线上Boss区域")); Assert.That(_encounter.text, Does.Contain("1/2"));
            Assert.That(_remaining.text, Does.Contain("7")); Assert.That(_gameplay.activeSelf, Is.True);
            _bind.Invoke(_hud, new object[] { null });
            Assert.That(_objective.text, Is.Empty); Assert.That(_encounter.text, Is.Empty); Assert.That(_remaining.text, Is.Empty);
            Assert.That(_gameplay.activeSelf, Is.False, "显式空Source不能残留旧区域或偷偷读取单机Controller。");
        }

        [Test] public void HiddenEncounterPolicyRemainsHiddenAcrossStartAndLiveProgress()
        {
            CreateHud(); _bind.Invoke(_hud, new object[] { _policy });
            Assert.That(_gameplay.activeSelf, Is.False,
                "Boss区域的原显隐策略在Armed阶段即生效，不能等Boss激活或先显示一帧重叠文本。");
            // 常驻 HUD 可能在绑定后才执行 Start；Start 与后续进度事件都不能重开被策略隐藏的叶树。
            typeof(DemoRunHudController).GetMethod("Start", Private).Invoke(_hud, null);
            Assert.That(_gameplay.activeSelf, Is.False);
            _policy.Progress = new OnlineDungeonProgress { Revision = 3, ServerTick = 20,
                EncounterIndexPlusOne = 1, RemainingEnemies = 1, Phase = OnlineDungeonPhase.Active };
            var progress = Event("Game.Run.EncounterProgressChangedEvent");
            progress.GetType().GetField("EncounterIndex").SetValue(progress, 0);
            progress.GetType().GetField("RemainingEnemyCount").SetValue(progress, 999);
            Invoke("OnEncounterProgressChanged", progress);
            Assert.That(_gameplay.activeSelf, Is.False, "更新合法进度只更新数据，不撤销当前区域的HUD策略。");
        }

        [Test] public void HiddenEncounterPolicyPreservesNotificationsAndOrdinarySourceRestoresHud()
        {
            CreateHud(); Assert.That(BindCache(101, 11, _policy), Is.True);
            _bind.Invoke(_hud, new object[] { _policy });
            Assert.That(_gameplay.activeSelf, Is.False);
            Assert.That(_hud.enabled, Is.True, "只隐藏Gameplay叶树；禁用Adapter会丢掉连接／施法通知。");
            Invoke("OnOnlineCastFeedback", new OnlineCastFeedbackEvent { Failure = OnlineCastFailure.ResourceRejected });
            Assert.That(_notification.activeSelf, Is.True);
            Assert.That(_notificationText.text, Is.EqualTo("当前法力或法术次数不足"));
            Invoke("OnOnlineMovementView", new OnlineMovementPresentationEvent { WorldSceneHandle = 11, Active = true, Frozen = true });
            Assert.That(_notificationText.text, Does.Contain("战斗已暂停"));
            Assert.That(_gameplay.activeSelf, Is.False);

            Assert.That(BindCache(101, 11, _a), Is.True); _bind.Invoke(_hud, new object[] { _a });
            Assert.That(_gameplay.activeSelf, Is.True, "换回未声明可选策略的旧来源，仍保持原有遭遇HUD行为。");
            Assert.That(_objective.text, Is.EqualTo("离开安全区后击败Boss"));
            Assert.That(_notification.activeSelf, Is.False, "来源更换同时清理旧连接通知。");
            _bind.Invoke(_hud, new object[] { null });
            Assert.That(_gameplay.activeSelf, Is.False);
        }

        [Test] public void ExplicitVisibleEncounterPolicyKeepsOrdinaryHudAndMetadata()
        {
            CreateHud(); _policy.EncounterHudVisible = true;
            _bind.Invoke(_hud, new object[] { _policy });
            Assert.That(_gameplay.activeSelf, Is.True);
            Assert.That(_encounter.text, Does.Contain("1/2"));
            Assert.That(_remaining.text, Does.Contain("1"));
        }

        [Test] public void OldRunFailureAndProgressEventsCannotOverwriteCurrentOnlineReadSource()
        {
            CreateHud(); _bind.Invoke(_hud, new object[] { _a });
            var failure = Event("Game.Run.RunStateChangedEvent");
            SetEnum(failure, "CurrentState", "Failed"); failure.GetType().GetField("CurrentEncounterIndex").SetValue(failure, 0);
            Invoke("OnRunStateChanged", failure); Assert.That(_gameplay.activeSelf, Is.True);
            var progress = Event("Game.Run.EncounterProgressChangedEvent");
            progress.GetType().GetField("EncounterIndex").SetValue(progress, 0); progress.GetType().GetField("RemainingEnemyCount").SetValue(progress, 999);
            Invoke("OnEncounterProgressChanged", progress); Assert.That(_remaining.text, Does.Contain("7")); Assert.That(_remaining.text, Does.Not.Contain("999"));
            Assert.That(_objective.text, Is.EqualTo("离开安全区后击败Boss"));
        }

        [Test] public void LateHudFindsOnlyItsWorldSourceAndOldOwnerCannotClearNewWorld()
        {
            Assert.That(BindCache(101, 11, _a), Is.True); Assert.That(BindCache(202, 22, _b), Is.True);
            _cache.GetMethod("Clear").Invoke(null, new object[] { 101, _a });
            Assert.That(_cache.GetProperty("Source").GetValue(null), Is.SameAs(_b));
            var args = new object[] { 11, null }; Assert.That(_cache.GetMethod("TryGet").Invoke(null, args), Is.False);
            args = new object[] { 22, null }; Assert.That(_cache.GetMethod("TryGet").Invoke(null, args), Is.True); Assert.That(args[1], Is.SameAs(_b));
        }

        [Test] public void SameOwnerOldRuntimeReferenceAndUnreadySourceCannotReplaceOrClearCurrentState()
        {
            Assert.That(BindCache(101, 11, _a), Is.True); Assert.That(BindCache(101, 11, _b), Is.True);
            _cache.GetMethod("Clear").Invoke(null, new object[] { 101, _a });
            Assert.That(_cache.GetProperty("Source").GetValue(null), Is.SameAs(_b));
            _a.Ready = false; Assert.That(BindCache(202, 22, _a), Is.False);
            Assert.That(_cache.GetProperty("Source").GetValue(null), Is.SameAs(_b));
            _cache.GetMethod("Clear").Invoke(null, new object[] { 101, _b }); Assert.That(_cache.GetProperty("Source").GetValue(null), Is.Null);
        }

        [Test] public void SinglePlayerAndWaitingHudIgnoreOnlineFeedback()
        {
            CreateHud();
            var rejected = new OnlineCastFeedbackEvent { Failure = OnlineCastFailure.ResourceRejected };
            Invoke("OnOnlineCastFeedback", rejected);
            Invoke("OnOnlineMovementView", new OnlineMovementPresentationEvent { Active = true, Frozen = true });
            Assert.That(_notification.activeSelf, Is.False);
            Assert.That(_notificationText.text, Is.Empty);
            _bind.Invoke(_hud, new object[] { null });
            Invoke("OnOnlineCastFeedback", rejected);
            Assert.That(_notification.activeSelf, Is.False, "显式等待 Source 不授权显示旧连接收据。");
        }

        [Test] public void OnlineRejectionUsesOriginalNotificationAndUnbindClearsOldText()
        {
            CreateHud(); Assert.That(BindCache(101, 11, _a), Is.True); _bind.Invoke(_hud, new object[] { _a });
            Invoke("OnOnlineCastFeedback", new OnlineCastFeedbackEvent { Failure = OnlineCastFailure.ResourceRejected });
            Assert.That(_notification.activeSelf, Is.True);
            Assert.That(_notificationText.text, Is.EqualTo("当前法力或法术次数不足"));
            // 正常视图/MenuOpen 变化不应把刚显示的拒绝提示重置成另一条常驻横幅。
            Invoke("OnOnlineMovementView", new OnlineMovementPresentationEvent { WorldSceneHandle = 11, Active = true, MenuOpen = true });
            Assert.That(_notificationText.text, Is.EqualTo("当前法力或法术次数不足"));
            _bind.Invoke(_hud, new object[] { null });
            Assert.That(_notification.activeSelf, Is.False);
            Assert.That(_notificationText.text, Is.Empty);
        }

        [Test] public void FrozenNotificationSurvivesOtherFeedbackAndDisableClearsIt()
        {
            CreateHud(); Assert.That(BindCache(101, 11, _a), Is.True); _bind.Invoke(_hud, new object[] { _a });
            Invoke("OnOnlineMovementView", new OnlineMovementPresentationEvent { WorldSceneHandle = 11, Active = true, Frozen = true });
            Assert.That(_notificationText.text, Does.Contain("战斗已暂停"));
            Invoke("OnOnlineCastFeedback", new OnlineCastFeedbackEvent { Failure = OnlineCastFailure.Interrupted });
            Assert.That(_notificationText.text, Does.Contain("战斗已暂停"));
            Invoke("OnOnlineMovementView", new OnlineMovementPresentationEvent { WorldSceneHandle = 11, Active = true, Frozen = false });
            Assert.That(_notificationText.text, Is.EqualTo("连接已恢复"));
            typeof(DemoRunHudController).GetMethod("OnDisable", Private).Invoke(_hud, null);
            Assert.That(_notification.activeSelf, Is.False);
            Assert.That(_notificationText.text, Is.Empty);
        }

        [Test] public void NotificationSubscriptionsEndWithHudLifecycle()
        {
            CreateHud(); Assert.That(BindCache(101, 11, _a), Is.True); _bind.Invoke(_hud, new object[] { _a });
            // EditMode 不自动驱动 inactive MonoBehaviour 生命周期；显式模拟 Enable/Disable，
            // 经真实 EventBus 验证订阅，而不以直接调用处理器冒充生命周期清理成功。
            typeof(DemoRunHudController).GetMethod("OnEnable", Private).Invoke(_hud, null);
            try
            {
                EventBus<OnlineCastFeedbackEvent>.Publish(new OnlineCastFeedbackEvent { Failure = OnlineCastFailure.InvalidInput });
                Assert.That(_notificationText.text, Is.EqualTo("瞄准位置无效，请重新瞄准"));
            }
            finally { typeof(DemoRunHudController).GetMethod("OnDisable", Private).Invoke(_hud, null); }
            EventBus<OnlineCastFeedbackEvent>.Publish(new OnlineCastFeedbackEvent { Failure = OnlineCastFailure.Interrupted });
            Assert.That(_notificationText.text, Is.Empty, "旧副本 HUD 已禁用后，迟到收据不能再填回它的文字。");
        }

        [Test] public void LateSourceAndReenabledHudReplayCurrentFrozenViewWithoutPolling()
        {
            CreateHud(); _bind.Invoke(_hud, new object[] { null });
            int requests = 0;
            Action<OnlineMovementViewRequestedEvent> answer = request =>
            {
                requests++; Assert.That(request.WorldSceneHandle, Is.EqualTo(11));
                EventBus<OnlineMovementPresentationEvent>.Publish(new OnlineMovementPresentationEvent
                    { WorldSceneHandle = request.WorldSceneHandle, Active = true, Frozen = true });
            };
            EventBus<OnlineMovementViewRequestedEvent>.Subscribe(answer);
            try
            {
                typeof(DemoRunHudController).GetMethod("OnEnable", Private).Invoke(_hud, null);
                Assert.That(requests, Is.Zero, "尚未安装可信 Source/World 时不能请求全局广播。");
                Assert.That(BindCache(101, 11, _a), Is.True); _bind.Invoke(_hud, new object[] { _a });
                Assert.That(requests, Is.EqualTo(1)); Assert.That(_notificationText.text, Does.Contain("战斗已暂停"));
                // Start 发生在 OnEnable 之后，不能把同一帧重放得到的 Frozen 提示重新隐藏。
                typeof(DemoRunHudController).GetMethod("Start", Private).Invoke(_hud, null);
                Assert.That(_notification.activeSelf, Is.True);
                typeof(DemoRunHudController).GetMethod("OnDisable", Private).Invoke(_hud, null);
                Assert.That(_notification.activeSelf, Is.False);
                typeof(DemoRunHudController).GetMethod("OnEnable", Private).Invoke(_hud, null);
                Assert.That(requests, Is.EqualTo(2)); Assert.That(_notification.activeSelf, Is.True);
                Assert.That(_notificationText.text, Does.Contain("战斗已暂停"));
            }
            finally
            {
                EventBus<OnlineMovementViewRequestedEvent>.Unsubscribe(answer);
                typeof(DemoRunHudController).GetMethod("OnDisable", Private).Invoke(_hud, null);
            }
        }

        [Test] public void OldWorldAndUnknownWorldViewsCannotClearCurrentFrozenNotice()
        {
            CreateHud(); Assert.That(BindCache(101, 11, _a), Is.True); _bind.Invoke(_hud, new object[] { _a });
            Assert.That(BindCache(202, 22, _b), Is.True); _bind.Invoke(_hud, new object[] { _b });
            Invoke("OnOnlineMovementView", new OnlineMovementPresentationEvent { WorldSceneHandle = 22, Active = true, Frozen = true });
            Invoke("OnOnlineMovementView", new OnlineMovementPresentationEvent { WorldSceneHandle = 11, Active = false });
            Invoke("OnOnlineMovementView", new OnlineMovementPresentationEvent { WorldSceneHandle = 0, Active = false });
            Assert.That(_notification.activeSelf, Is.True); Assert.That(_notificationText.text, Does.Contain("战斗已暂停"));
            Invoke("OnOnlineMovementView", new OnlineMovementPresentationEvent { WorldSceneHandle = 22, Active = true, Frozen = false });
            Assert.That(_notificationText.text, Is.EqualTo("连接已恢复"));
        }

        private bool BindCache(int owner, int world, Source source) => (bool)_cache.GetMethod("Bind").Invoke(null, new object[] { owner, world, source });
        private void CreateHud()
        {
            var root = Own(new GameObject("OriginalStageHud")); root.SetActive(false); _hud = root.AddComponent<DemoRunHudController>();
            _gameplay = Own(new GameObject("Gameplay")); _gameplay.transform.SetParent(root.transform);
            _objective = Label("Objective", root.transform); _encounter = Label("Encounter", root.transform); _remaining = Label("Remaining", root.transform);
            typeof(DemoRunHudController).GetField("_gameplayRoot", Private).SetValue(_hud, _gameplay);
            typeof(DemoRunHudController).GetField("_objectiveText", Private).SetValue(_hud, _objective);
            typeof(DemoRunHudController).GetField("_encounterText", Private).SetValue(_hud, _encounter);
            typeof(DemoRunHudController).GetField("_remainingEnemyText", Private).SetValue(_hud, _remaining);
            _notification = Own(new GameObject("OriginalNotification")); _notification.transform.SetParent(root.transform);
            _notificationText = Label("NotificationText", _notification.transform);
            typeof(DemoRunHudController).GetField("_notificationRoot", Private).SetValue(_hud, _notification);
            typeof(DemoRunHudController).GetField("_notificationText", Private).SetValue(_hud, _notificationText);
            _notification.SetActive(false);
        }
        private Text Label(string name, Transform parent) { var go = Own(new GameObject(name, typeof(RectTransform), typeof(Text))); go.transform.SetParent(parent); return go.GetComponent<Text>(); }
        private GameObject Own(GameObject value) { _objects.Add(value); return value; }
        private object Event(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            { var type = assembly.GetType(name); if (type != null) return Activator.CreateInstance(type); }
            Assert.Fail("Missing original Run event: " + name); return null;
        }
        private void Invoke(string name, object argument) => typeof(DemoRunHudController).GetMethod(name, Private).Invoke(_hud, new[] { argument });
        private void SetEnum(object value, string field, string name)
        { var member = value.GetType().GetField(field); member.SetValue(value, Enum.Parse(member.FieldType, name)); }
    }
}
