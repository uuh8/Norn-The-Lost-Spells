using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Game.Combat;
using Game.Core;
using Game.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Game.Run.Tests
{
    /// <summary>
    /// 真实 PlayMode Scene/DDOL 回归。只创建本例内存区域和未初始化的只读 Session 引用，
    /// 不加载 Hub/P7/P8、不读写用户 Profile，也不把这些测试当作完整地图或网络验收。
    /// </summary>
    public sealed class CombatPresentationRuntimeTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly List<Scene> _worlds = new List<Scene>();
        private readonly List<CombatPresentationScope> _scopes = new List<CombatPresentationScope>();
        private CombatPresentationHost _initialHost, _createdHost;
        private float _timeScale;

        [UnitySetUp]
        public IEnumerator BeforeRuntimeCase()
        {
            _timeScale = Time.timeScale;
            _initialHost = CombatPresentationHost.Active;
            Assert.That(_initialHost == null || !_initialHost.IsBound, Is.True,
                "测试不能抢占其他真实区域的有效 lease；前一用例应先释放自己的 Context。");
            Assert.That(Resources.Load<GameObject>(CombatPresentationHost.ResourcePath), Is.Not.Null,
                "缺少实际导出的原 HUD Resource，不能用空 GameObject 冒充常驻资源复用。");
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator AfterRuntimeCase()
        {
            // 场景所有者先释放自己的 lease，再卸载世界；不 Clear 全局事件或清理其他测试的对象。
            for (int i = _scopes.Count - 1; i >= 0; i--)
                if (_scopes[i] != null) _scopes[i].Clear();
            for (int i = _worlds.Count - 1; i >= 0; i--)
                if (_worlds[i].IsValid() && _worlds[i].isLoaded) yield return SceneManager.UnloadSceneAsync(_worlds[i]);
            _scopes.Clear(); _worlds.Clear();
            if (_createdHost != null) Object.Destroy(_createdHost.gameObject);
            yield return null;
            // 本例从不设置 Time.timeScale；若此断言失败，应修释放责任而非在 TearDown 强行抹掉证据。
            Assert.That(Time.timeScale, Is.EqualTo(_timeScale));
            _createdHost = null; _initialHost = null;
        }

        [UnityTest]
        public IEnumerator RealWorldUnloadKeepsSingleDdOlHudAndDropsSceneReferences()
        {
            var a = CreateArea("A"); Bind(a);
            var host = CombatPresentationHost.Active; var hud = host.HudRoot;
            yield return null;
            Assert.That(host.transform.parent, Is.Null);
            Assert.That(host.gameObject.scene.name, Is.EqualTo("DontDestroyOnLoad"),
                "这里检查 Unity 真正的 DDOL Scene，不能把 EditMode 留着一个对象称为跨 Scene 常驻。");
            Assert.That(hud.scene, Is.EqualTo(host.gameObject.scene));
            Assert.That(Object.FindObjectsByType<CombatPresentationHost>(FindObjectsSortMode.None).Length, Is.EqualTo(1));
            AssertGameplayStayedInWorld(a, host);
            Assert.That(Time.timeScale, Is.EqualTo(_timeScale));

            a.Scope.Clear();
            AssertUnbound(host, hud);
            // 清 UI 不应销毁仍由世界拥有的实体；实际世界卸载才结束这些 Unity 对象。
            Assert.That(a.Actor != null && a.Source != null && a.Inventory != null, Is.True);
            yield return SceneManager.UnloadSceneAsync(a.World);
            Assert.That(a.Actor == null && a.Source == null && a.Inventory == null, Is.True,
                "若 Session/Actor 被搬进 DDOL，则原世界卸载后会错误地存活。");
            Assert.That(CombatPresentationHost.Active, Is.SameAs(host)); AssertUnbound(host, hud);

            var b = CreateArea("B"); Bind(b); yield return null;
            Assert.That(CombatPresentationHost.Active, Is.SameAs(host)); Assert.That(host.HudRoot, Is.SameAs(hud));
            Assert.That(host.Actor, Is.SameAs(b.Actor)); Assert.That(host.InventorySource, Is.SameAs(b.Source));
            AssertGameplayStayedInWorld(b, host);
            Assert.That(Field(hud.GetComponentInChildren<PlayerHealthBar>(true), "_playerHealth"), Is.SameAs(b.Health));
            b.Scope.Clear(); AssertUnbound(host, hud);
            Assert.That(Time.timeScale, Is.EqualTo(_timeScale));
        }

        [UnityTest]
        public IEnumerator DestroyedOldScopeAndWorldCannotClearNewWorldOrMoveItsActors()
        {
            var a = CreateArea("Old"); var b = CreateArea("Current");
            Bind(a); var host = CombatPresentationHost.Active; var hud = host.HudRoot;
            Bind(b); yield return null;
            Object.Destroy(a.Owner); yield return null;
            Assert.That(a.Scope == null, Is.True, "必须经过真实 OnDisable/Destroy，而非直接 Invoke 释放方法。");
            Assert.That(host.IsBound, Is.True); Assert.That(host.Actor, Is.SameAs(b.Actor));
            Assert.That(host.InventorySource, Is.SameAs(b.Source));
            Assert.That(Field(hud.GetComponentInChildren<HeldItemHud>(true), "_backpack"), Is.SameAs(b.Inventory));
            yield return SceneManager.UnloadSceneAsync(a.World);
            Assert.That(host.IsBound, Is.True); Assert.That(host.Actor, Is.SameAs(b.Actor));
            Assert.That(host.HudRoot, Is.SameAs(hud)); AssertGameplayStayedInWorld(b, host);

            Object.Destroy(b.Owner); yield return null;
            Assert.That(b.Scope == null, Is.True); AssertUnbound(host, hud);
            Assert.That(b.Actor != null && b.Source != null && b.Inventory != null, Is.True,
                "释放 HUD 不等于销毁游戏权威；它们仍由当前 World 的生命周期负责。");
            AssertGameplayStayedInWorld(b, host);
            Assert.That(Time.timeScale, Is.EqualTo(_timeScale));
        }

        [UnityTest]
        public IEnumerator RealLifecycleBindsLateDungeonSourceAndReleaseDropsItsReference()
        {
            var area = CreateArea("LateDungeonSource"); Bind(area); yield return null;
            var hud = CombatPresentationHost.Active.HudRoot;
            var stage = hud.GetComponentInChildren<DemoRunHudController>(true);
            Assert.That(stage, Is.Not.Null);
            var source = new DungeonSource();
            try
            {
                Assert.That(Field(stage, "_presentationSource"), Is.Null);
                Assert.That(DungeonPresentation.Bind(area.Owner.GetInstanceID(), area.World.handle, source), Is.True);
                yield return null;
                Assert.That(Field(stage, "_presentationSource"), Is.SameAs(source), "这里经过真实OnEnable订阅，不能手动Invoke掩盖运行时漏绑定。");
                var stageRoot = (GameObject)Field(stage, "_gameplayRoot");
                Assert.That(stageRoot.activeInHierarchy, Is.True);
                area.Scope.Clear(); yield return null;
                Assert.That(Field(stage, "_presentationSource"), Is.Null);
                Assert.That(stageRoot.activeInHierarchy, Is.False);
            }
            finally { DungeonPresentation.Clear(area.Owner.GetInstanceID(), source); }
        }

        private sealed class DungeonSource : IDungeonPresentationReadOnly
        {
            public int EncounterCount => 1;
            public string GetDisplayName(int index) => "实际生命周期测试区";
            public string GetObjectiveText(int index) => "等待副本权威";
            public bool TryCaptureProgress(out OnlineDungeonProgress progress)
            {
                progress = new OnlineDungeonProgress { Revision = 1, ServerTick = 1, EncounterIndexPlusOne = 1,
                    Phase = OnlineDungeonPhase.Armed, RemainingEnemies = 2 }; return true;
            }
        }

        private void Bind(Area area)
        {
            area.Scope.Bind(new CombatPresentationContext
            {
                Actor = area.Actor, WorldScene = area.World, InventorySource = area.Source,
                InventoryController = area.Inventory, CrosshairVisible = true
            });
            var host = CombatPresentationHost.Active;
            if (_initialHost == null && host != null) _createdHost = host;
            Assert.That(area.Scope.LastBindSucceeded && area.Scope.IsBound, Is.True);
        }

        private static void AssertUnbound(CombatPresentationHost host, GameObject hud)
        {
            Assert.That(host != null && hud != null, Is.True);
            Assert.That(host.IsBound, Is.False); Assert.That(host.Actor, Is.Null); Assert.That(host.InventorySource, Is.Null);
            Assert.That(host.HudRoot, Is.SameAs(hud)); Assert.That(hud.activeSelf, Is.False);
            // 直接检查保存的 Context/原组件字段，不能仅依赖 Unity destroyed-object 的假 null 掩盖旧托管引用。
            var context = (CombatPresentationContext)Field(host, "_context");
            Assert.That(context.Actor, Is.Null); Assert.That(context.InventorySource, Is.Null); Assert.That(context.InventoryController, Is.Null);
            Assert.That(Field(hud.GetComponentInChildren<PlayerHealthBar>(true), "_playerHealth"), Is.Null);
            Assert.That(Field(hud.GetComponentInChildren<PlayerStatusBar>(true), "_playerHealth"), Is.Null);
            var held = hud.GetComponentInChildren<HeldItemHud>(true);
            Assert.That(held.PresentationSource, Is.Null); Assert.That(Field(held, "_backpack"), Is.Null);
        }

        private static void AssertGameplayStayedInWorld(Area area, CombatPresentationHost host)
        {
            Assert.That(area.Actor.scene, Is.EqualTo(area.World)); Assert.That(area.Source.gameObject.scene, Is.EqualTo(area.World));
            Assert.That(area.Inventory.gameObject.scene, Is.EqualTo(area.World));
            Assert.That(area.Actor.transform.IsChildOf(host.transform), Is.False);
            Assert.That(area.Source.transform.IsChildOf(host.transform), Is.False);
        }

        private Area CreateArea(string label)
        {
            var world = SceneManager.CreateScene("CombatHudRuntime_" + label + "_" + Guid.NewGuid().ToString("N")); _worlds.Add(world);
            var owner = new GameObject("TemporaryPresentationScope"); SceneManager.MoveGameObjectToScene(owner, world);
            var actor = new GameObject("WorldOwnedActor"); SceneManager.MoveGameObjectToScene(actor, world);
            var health = actor.AddComponent<HealthComponent>();
            var sourceRoot = new GameObject("UninitializedMemorySession"); sourceRoot.SetActive(false);
            SceneManager.MoveGameObjectToScene(sourceRoot, world);
            // inactive 保证不运行 Session Awake/Start，因而不会创建本地档、StarterKit 或资源工作区。
            var source = sourceRoot.AddComponent<SinglePlayerInventorySession>();
            var inventory = sourceRoot.AddComponent<SinglePlayerInventoryController>();
            var scope = owner.AddComponent<CombatPresentationScope>(); _scopes.Add(scope);
            return new Area { World = world, Owner = owner, Actor = actor, Health = health, Source = source, Inventory = inventory, Scope = scope };
        }
        private static object Field(object target, string name) => target.GetType().GetField(name, Private).GetValue(target);
        private sealed class Area
        {
            internal Scene World;
            internal GameObject Owner, Actor;
            internal HealthComponent Health;
            internal SinglePlayerInventorySession Source;
            internal SinglePlayerInventoryController Inventory;
            internal CombatPresentationScope Scope;
        }
    }
}
