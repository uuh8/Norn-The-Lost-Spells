using System;
using System.Reflection;
using Game.Core;
using Game.UI;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Run.Tests
{
    /// <summary>检查主菜单旧Play入口也只申请身份验证，避免留下可直接LoadScene的旁路。</summary>
    public sealed class AccountEntryNavigationTests
    {
        private int _requests;
        private OnlineLobbyAction _action;
        private string _destination;
        private OnlineLobbyRequestEvent _lastRequest;
        [SetUp] public void ResetCapturedNavigation() { _requests = 0; _action = default; _destination = null; _lastRequest = default; }
        private void OnRequest(OnlineLobbyRequestEvent request) { _requests++; _action = request.Action; _destination = request.Destination; _lastRequest = request; }
        [Test] public void StartGame_RequiresFreshBusinessValidation()
        {
            var root = new GameObject("Menu entry test");
            EventBus<OnlineLobbyRequestEvent>.Subscribe(OnRequest);
            try
            {
                var menu = root.AddComponent<DemoMainMenuController>(); menu.StartGame();
                Assert.That(_requests, Is.EqualTo(1)); Assert.That(_action, Is.EqualTo(OnlineLobbyAction.EnterHub));
            }
            finally { EventBus<OnlineLobbyRequestEvent>.Unsubscribe(OnRequest); Object.DestroyImmediate(root); }
        }

        [Test] public void FreshHubValidationCarriesTheOriginalConfiguredSceneTarget()
        {
            var root = new GameObject("Account Hub target test");
            EventBus<OnlineLobbyRequestEvent>.Subscribe(OnRequest);
            try
            {
                var menu = root.AddComponent<DemoMainMenuController>();
                const string target = "Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity";
                typeof(DemoMainMenuController).GetField("_hubSceneName", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(menu, target);
                menu.StartGame();
                Assert.That(_action, Is.EqualTo(OnlineLobbyAction.EnterHub));
                Assert.That(_destination, Is.EqualTo(target), "业务验证与加载前只读要求必须使用原菜单将加载的同一个Hub，不能另猜场景。");
            }
            finally { EventBus<OnlineLobbyRequestEvent>.Unsubscribe(OnRequest); UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test] public void HubEntryIntentIdentifiesOnlyTheMenuThatRequestedIt()
        {
            var root = new GameObject("Account Hub requesting menu");
            EventBus<OnlineLobbyRequestEvent>.Subscribe(OnRequest);
            try
            {
                var menu = root.AddComponent<DemoMainMenuController>(); menu.StartGame();
                var field = typeof(OnlineLobbyRequestEvent).GetField("RequestOwnerId");
                Assert.That(field, Is.Not.Null, "多个菜单消费者不能共享无关联的Hub Ready通知。");
                Assert.That(field.GetValue(_lastRequest), Is.EqualTo(menu.GetInstanceID()));
            }
            finally { EventBus<OnlineLobbyRequestEvent>.Unsubscribe(OnRequest); UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test] public void ReadyKeepsTheCapturedTargetAndCannotBeTakenByAnotherMenu()
        {
            var firstRoot = new GameObject("First account Hub requester");
            var secondRoot = new GameObject("Second account Hub requester");
            try
            {
                var first = firstRoot.AddComponent<DemoMainMenuController>();
                var second = secondRoot.AddComponent<DemoMainMenuController>();
                const string original = "SinglePlayer_Hub", changed = "OtherHub";
                var targetField = typeof(DemoMainMenuController).GetField("_hubSceneName", BindingFlags.Instance | BindingFlags.NonPublic);
                targetField.SetValue(first, original); targetField.SetValue(second, original);
                first.StartGame(); second.StartGame();
                targetField.SetValue(first, changed); // 模拟HTTP等待中配置变化，不能让Ready装载未预arm的新目标。
                var ready = Ready(first.GetInstanceID(), original);
                Assert.That(TryReadReadyTarget(first, ready, out var target), Is.True);
                Assert.That(target, Is.EqualTo(original));
                Assert.That(TryReadReadyTarget(second, ready, out _), Is.False, "同目标的另一个菜单也不能消费本人通知。");
                Assert.That(TryReadReadyTarget(first, Ready(first.GetInstanceID(), changed), out _), Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(firstRoot); UnityEngine.Object.DestroyImmediate(secondRoot); }
        }

        private static object Ready(int owner, string target)
        {
            object value = default(AccountHubEntryReadyEvent);
            var ownerField = typeof(AccountHubEntryReadyEvent).GetField("RequestOwnerId");
            var targetField = typeof(AccountHubEntryReadyEvent).GetField("TargetScene");
            Assert.That(ownerField, Is.Not.Null); Assert.That(targetField, Is.Not.Null);
            ownerField.SetValue(value, owner); targetField.SetValue(value, target); return value;
        }
        private static bool TryReadReadyTarget(DemoMainMenuController menu, object ready, out string target)
        {
            // 使用生产加载回调共用的预校验；不启动Scene加载或伪造另一个业务Profile。
            var method = typeof(DemoMainMenuController).GetMethod("TryGetReadyHubTarget", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            object[] args = { ready, null }; bool accepted = (bool)method.Invoke(menu, args); target = args[1] as string; return accepted;
        }

        [Test] public void ArmedFirstHubBlocksOldProfileBeforeSessionEnableAndSurvivesIdentityFailure()
        {
            ResetHubScope();
            // EditMode Runner可能使用无路径/无名临时Scene；夹具自己建立合法目标，不让生产接受空目标。
            var fixtureScene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            fixtureScene.name = "AccountEntry_Prearm_" + Guid.NewGuid().ToString("N");
            var root = new GameObject("Inactive first account Hub"); root.SetActive(false);
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, fixtureScene);
            try
            {
                const int owner = 26037;
                Assert.That(OnlineAccountInventoryHubScope.TryPublish(owner, Profile(), out var error), Is.True, error);
                Assert.That(fixtureScene.name, Does.StartWith("AccountEntry_Prearm_"));
                Assert.That(TryArm(owner, fixtureScene.name), Is.True);
                // 先arm再创建Session，模拟新Scene自己的Awake/OnEnable，不从旧档取得任何资产或规则。
                var session = root.AddComponent<SinglePlayerInventorySession>();
                Assert.That(session.AccountInventoryReadOnly, Is.True);
                typeof(SinglePlayerInventorySession).GetMethod("OnEnable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(session, null);
                Assert.That(session.ReadOnly, Is.Null);
                Assert.That(session.ProfileStoragePath, Is.Null);
                Assert.That(session.Save().Status, Is.EqualTo(ProfilePersistenceStatus.WriteBlocked));
                OnlineAccountInventoryHubScope.Clear(owner);
                Assert.That(session.AccountInventoryReadOnly, Is.True, "身份失效仍保留该目标只读要求，不能回落旧档/初始模板。");
                Assert.That(OnlineAccountInventoryHubScope.TryCopyForScene((int)root.scene.handle, out _), Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                if (fixtureScene.IsValid()) UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(fixtureScene);
                ResetHubScope();
            }
        }

        [TestCase(true, "")] [TestCase(false, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
        public void LockedOrActiveAccountCannotArmFirstHub(bool locked, string runId)
        {
            ResetHubScope();
            try
            {
                var profile = Profile(); profile.AssetsLocked = locked; profile.ActiveRunId = runId;
                Assert.That(OnlineAccountInventoryHubScope.TryPublish(26037, profile, out var error), Is.True, error);
                Assert.That(TryArm(26037, "SinglePlayer_Hub"), Is.False, "不能把带租约Profile误作可用Hub资产。");
            }
            finally { ResetHubScope(); }
        }

        private static bool TryArm(int owner, string target)
        {
            var method = typeof(OnlineAccountInventoryHubScope).GetMethod("TryArmHubEntry", BindingFlags.Public | BindingFlags.Static);
            Assert.That(method, Is.Not.Null, "首次场景激活前需要既有Scope的目标要求，加载后才prepare已经晚于旧Session启用。");
            var args = new object[] { owner, target, null };
            return (bool)method.Invoke(null, args);
        }
        private static OnlineAccountInventorySnapshot Profile() => new OnlineAccountInventorySnapshot
        {
            SchemaVersion = OnlineAccountInventorySnapshot.CurrentSchemaVersion, Version = 7, Gold = 12347,
            PlayerId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", ContentManifestHash = new string('a', 64),
            Items = Array.Empty<OnlineOwnedItemSnapshot>()
        };
        private static void ResetHubScope() => typeof(OnlineAccountInventoryHubScope)
            .GetMethod("Reset", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
    }
}
