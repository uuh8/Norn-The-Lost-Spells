using System.Collections;
using System.Reflection;
using Game.Core;
using Game.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif
using Object = UnityEngine.Object;

namespace Game.Run.Tests
{
    /// <summary>
    /// 真实异步Scene载入覆盖Awake→OnEnable→sceneLoaded→Start，不Invoke生产生命周期方法。
    /// 使用实际导出的原Tab/HUD资源；不启动网络、账号、Token或执行工作区，只复现表现装配时序。
    /// </summary>
    public sealed class EarlyCombatPresentationLifecycleTests
    {
        private const string Fixture = "Assets/_Project/Tests/Fixtures/RuntimeComposition/EarlyCombatPresentation.unity";
        private Scene _scene;
        private CombatPresentationHost _initialHost, _createdHost;
        private EarlyCombatPresentationPublisher _publisher;
        private int _sourceOwner;

        [UnityTearDown]
        public IEnumerator CleanupOnlyThisFixture()
        {
            if (_scene.IsValid() && _scene.isLoaded)
            {
                foreach (var root in _scene.GetRootGameObjects())
                    foreach (var scope in root.GetComponentsInChildren<CombatPresentationScope>(true)) scope.Clear();
                OnlineInventoryPresentation.Clear(_sourceOwner);
                yield return SceneManager.UnloadSceneAsync(_scene);
            }
            var current = CombatPresentationHost.Active;
            if (_initialHost == null && current != null) _createdHost = current;
            if (_createdHost != null) Object.Destroy(_createdHost.gameObject);
            yield return null;
        }

        [UnityTest]
        public IEnumerator AwakeActorBeforeSceneLoadedStillComposesOriginalTabAndVisiblePanel()
        {
#if !UNITY_EDITOR
            Assert.Ignore("该Fixture使用EditorSceneManager.LoadSceneInPlayMode，不加入正式Player场景列表。");
            yield break;
#else
            _sourceOwner = 0; _createdHost = null; _initialHost = CombatPresentationHost.Active;
            Assert.That(_initialHost == null || !_initialHost.IsBound, Is.True, "不得抢占其他真实战斗区域。");
            Assert.That(LocalActorPresentation.Actor == null, Is.True, "开始测试前不能残留其他本机角色。");
            Assert.That(AssetDatabase.LoadAssetAtPath<SceneAsset>(Fixture), Is.Not.Null,
                "请用Create Early Presentation Lifecycle Fixture显式生成测试Scene，不能拿内存Scene代替真实加载。");
            Assert.That(Resources.Load<GameObject>(CombatPresentationScope.InventoryResourcePath), Is.Not.Null);
            _scene = EditorSceneManager.LoadSceneInPlayMode(Fixture, new LoadSceneParameters(LoadSceneMode.Additive));
            // LoadSceneInPlayMode返回Scene不等于其Awake/OnEnable已完成；跨真实帧等待Unity完成加载。
            for (int i = 0; i < 60 && !_scene.isLoaded; i++) yield return null;
            Assert.That(_scene.isLoaded, Is.True, "最小Fixture未完成加载。");
            CombatPresentationScope scope = null;
            foreach (var root in _scene.GetRootGameObjects())
            {
                var candidate = root.GetComponent<EarlyCombatPresentationPublisher>(); if (candidate != null) _publisher = candidate;
                var owner = root.GetComponent<CombatPresentationScope>(); if (owner != null) scope = owner;
            }
            Assert.That(_publisher != null && scope != null, Is.True);
            Assert.That(_publisher.Published, Is.True);
            Assert.That(_publisher.SceneWasLoadedWhenPublished, Is.False,
                "必须实际覆盖Scene未loaded的Awake发布；若前置不成立，不能把后面的GREEN称为复现修复。");
            Assert.That(LocalActorPresentation.Actor, Is.SameAs(_publisher.gameObject));
            yield return null;
            Assert.That(scope.LastBindSucceeded && scope.IsBound, Is.True,
                "真实sceneLoaded后应先恢复Actor与等待中的原Tab，不能依赖未来恰好还有一次Source事件补救。");

            // 模拟实际网络在场景加载后才交付Source的次序。此纯等待源没有资产或命令权限，不伪造账号/物品。
            var source = new WaitingSource(); _sourceOwner = _publisher.GetInstanceID();
            OnlineInventoryPresentation.Bind(_sourceOwner, _publisher.gameObject, source);
            yield return null; yield return null;
            var host = CombatPresentationHost.Active;
            if (_initialHost == null && host != null) _createdHost = host;
            Assert.That(scope.LastBindSucceeded && scope.IsBound, Is.True,
                "Awake阶段拒绝绑定后必须在真实Scene就绪边界重放Actor，Source到达不能永远丢在空Context。");
            Assert.That(host, Is.Not.Null); Assert.That(host.Actor, Is.SameAs(_publisher.gameObject));
            Assert.That(host.InventorySource, Is.SameAs(source));
            var inventories = scope.GetComponentsInChildren<SinglePlayerInventoryController>(true);
            Assert.That(inventories.Length, Is.EqualTo(1), "需实际创建原Tab，不是只把Host租约标记为成功。");
            var inventory = inventories[0]; Assert.That(inventory.gameObject.scene, Is.EqualTo(_scene));
            Assert.That(inventory.PresentationSource, Is.SameAs(source)); Assert.That(inventory.Session, Is.Null);
            Assert.That(inventory.PresentationIsReadOnly, Is.True);
            inventory.Open(); yield return null; yield return null;
            Canvas.ForceUpdateCanvases();
            Assert.That(inventory.IsOpen, Is.True);
            var panel = (GameObject)typeof(SinglePlayerInventoryController).GetField("_panelRoot",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(inventory);
            Assert.That(panel != null && panel.activeInHierarchy, Is.True);
            var canvas = panel.GetComponentInParent<Canvas>(); Assert.That(canvas != null && canvas.isActiveAndEnabled, Is.True);
            var corners = new Vector3[4]; ((RectTransform)panel.transform).GetWorldCorners(corners);
            Camera camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            Vector2 minimum = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
            Vector2 maximum = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
            Vector2 size = maximum - minimum; Vector2 center = (minimum + maximum) * .5f;
            Assert.That(size.x, Is.GreaterThan(100f), "原Panel必须有真实屏幕宽度，不能把白细条当成Tab显示。");
            Assert.That(size.y, Is.GreaterThan(100f));
            Assert.That(center.x, Is.InRange(0f, (float)Screen.width)); Assert.That(center.y, Is.InRange(0f, (float)Screen.height));
#endif
        }

        private sealed class WaitingSource : IInventoryPresentationSource
        {
            public IInventoryReadOnly ReadOnly => null;
            public IWandLoadoutReadOnly WandsReadOnly => null;
            public IToolbeltReadOnly ToolbeltReadOnly => null;
            public ISpellUseReadOnly SpellUsesReadOnly => null;
            public IWalletReadOnly WalletReadOnly => null;
            public bool UseSpellProgrammingV2 => true;
            public bool CanAccessWarehouse => false;
            public string WorldSceneKey => "test-only-early-lifecycle";
            public string EquippedBackpackId => null;
            public int SpellStorageCapacity => 0;
            public bool HasUnsavedChanges => false;
            public string InitializationMessage => "测试等待库存；不创建本地资产。";
            public bool TryCaptureWandResearch(string id, out WandResearchSnapshot snapshot) { snapshot = null; return false; }
            public bool TryCopyContainer(string id, MaterialAmount[] buffer, out int count, out ulong total, out ulong capacity)
            { count = 0; total = capacity = 0; return false; }
        }
    }
}
