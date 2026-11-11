using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Game.Multiplayer.Tests
{
    /// <summary>只验证联机挂起/返回的根激活快照；临时空Scene不载入正式Hub，也不触碰Profile或业务服务。</summary>
    public sealed class OnlineRoomHubLifecycleTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private Scene _originalScene, _hub;
        private GameObject _clientRoot, _owner, _runtime, _originallyInactive;
        private OnlineRoomClient _client;
        private HubSiblingRootLifecycleProbe _lifecycle;

        [SetUp] public void CreateOnlyTransientRoots()
        {
            _originalScene = SceneManager.GetActiveScene();
            // NewScene(Additive)会因TestRunner当前未保存的Untitled Scene而拒绝；Preview只建内存场景，
            // 不替换、保存或关闭原Editor Scene，沿用本仓库EditMode场景夹具的隔离方式。
            _hub = EditorSceneManager.NewPreviewScene();
            // 客户端宿主从未激活，测试只调用挂起/恢复，不取得房间、网络或全局World生命周期。
            _clientRoot = new GameObject("HubLifecycleClientFixture"); _clientRoot.SetActive(false);
            SceneManager.MoveGameObjectToScene(_clientRoot, _hub);
            _client = _clientRoot.AddComponent<OnlineRoomClient>();
            _owner = CreateHubRoot("HubSessionFixture");
            _runtime = CreateHubRoot("WorldItems_Runtime_Fixture");
            _originallyInactive = CreateHubRoot("OriginallyInactiveFixture"); _originallyInactive.SetActive(false);
            // Multiplayer测试不直接引用Game.Run，因此用最小Unity回调复现Session的真实边界：
            // OnDisable会停用并列Runtime根，OnEnable重新启用它；断言仍针对真实OnlineRoomClient的往返结果。
            _lifecycle = _owner.AddComponent<HubSiblingRootLifecycleProbe>(); _lifecycle.RuntimeRoot = _runtime;
        }

        [TearDown] public void ReleaseOnlyTransientScene()
        {
            if (_originalScene.IsValid() && _originalScene.isLoaded) SceneManager.SetActiveScene(_originalScene);
            if (_hub.IsValid() && _hub.isLoaded) EditorSceneManager.ClosePreviewScene(_hub);
            if (_clientRoot != null) Object.DestroyImmediate(_clientRoot);
        }

        [TestCase(false, TestName = "HubRoundTripPreservesRuntimeRootWhenSessionDisablesFirst")]
        [TestCase(true, TestName = "HubRoundTripPreservesRuntimeRootWhenRuntimeDisablesFirst")]
        public void RoundTripPreservesActivationAcrossSiblingDisableCallbacks(bool runtimeFirst)
        {
            _owner.transform.SetSiblingIndex(runtimeFirst ? 1 : 0);
            _runtime.transform.SetSiblingIndex(runtimeFirst ? 0 : 1);
            _originallyInactive.transform.SetSiblingIndex(2);
            var ordered = _hub.GetRootGameObjects();
            Assert.That(Array.IndexOf(ordered, _runtime) < Array.IndexOf(ordered, _owner), Is.EqualTo(runtimeFirst),
                "夹具必须实际覆盖两种根遍历顺序，不能让成功顺序掩盖回调修改后续根状态的问题。");

            Invoke("SuspendHub", _hub);
            Assert.That(_lifecycle.DisableCount, Is.GreaterThan(0), "RED必须经过真实Unity OnDisable联动，不能只手动改布尔值。");
            Assert.That(_owner.activeSelf || _runtime.activeSelf || _originallyInactive.activeSelf, Is.False,
                "在线世界运行前，所有原Hub根都应暂停。");
            var capturedRoots = (GameObject[])typeof(OnlineRoomClient).GetField("_hubRoots", Private).GetValue(_client);
            var capturedActive = (bool[])typeof(OnlineRoomClient).GetField("_hubRootActive", Private).GetValue(_client);
            bool runtimeWasActive = capturedActive[Array.IndexOf(capturedRoots, _runtime)];
            // Preview Scene不能成为Editor当前Active Scene。本例验证的是捕获根的启停往返，
            // 因此只将恢复入口的Scene目标换回原Active Scene；根数组/状态仍完全来自真实SuspendHub，
            // RestoreHub实际改动的仍仅是本夹具根，不会遍历或停用原Editor Scene的任何对象。
            typeof(OnlineRoomClient).GetField("_suspendedHub", Private).SetValue(_client, _originalScene);
            Invoke("RestoreHub");

            Assert.That(runtimeWasActive, Is.True, "快照必须记录挂起前的Runtime根，而非前一个根OnDisable改写后的状态。");
            Assert.That(_owner.activeInHierarchy, Is.True);
            Assert.That(_runtime.activeInHierarchy, Is.True, "返回Hub后，原有和后续Drop复用的Runtime父根都必须可见。");
            Assert.That(_originallyInactive.activeSelf, Is.False, "修复不能把作者原本关闭的根一并启用。");
        }

        private GameObject CreateHubRoot(string name)
        {
            var root = new GameObject(name); SceneManager.MoveGameObjectToScene(root, _hub); return root;
        }
        private void Invoke(string name, params object[] arguments)
        {
            var method = typeof(OnlineRoomClient).GetMethod(name, Private);
            Assert.That(method, Is.Not.Null); method.Invoke(_client, arguments);
        }
    }

    /// <summary>仅测试使用的并列根生命周期；ExecuteAlways使EditMode也派发真实Unity启停回调。</summary>
    [ExecuteAlways]
    public sealed class HubSiblingRootLifecycleProbe : MonoBehaviour
    {
        public GameObject RuntimeRoot;
        public int DisableCount { get; private set; }
        private void OnEnable() { if (RuntimeRoot != null) RuntimeRoot.SetActive(true); }
        private void OnDisable() { DisableCount++; if (RuntimeRoot != null) RuntimeRoot.SetActive(false); }
    }
}
