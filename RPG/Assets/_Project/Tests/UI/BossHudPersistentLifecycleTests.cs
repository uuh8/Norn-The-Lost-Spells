using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Game.UI.Tests
{
    /// <summary>纯 HUD 常驻后 Start 只执行一次；用重复启用回归跨区域显示清理，不创建 Boss 玩法。</summary>
    public sealed class BossHudPersistentLifecycleTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameObject _owner, _visual;
        private BossHealthBarController _controller;
        private Image _fill;

        [SetUp]
        public void CreateOnlyPresentationObjects()
        {
            _owner = new GameObject("PersistentBossHudTest"); _owner.SetActive(false);
            _controller = _owner.AddComponent<BossHealthBarController>();
            _visual = new GameObject("BossVisual", typeof(RectTransform)); _visual.transform.SetParent(_owner.transform, false);
            var fill = new GameObject("HealthFill", typeof(RectTransform), typeof(Image)); fill.transform.SetParent(_visual.transform, false);
            _fill = fill.GetComponent<Image>(); _fill.type = Image.Type.Filled;
            Set("_hudRoot", _visual); Set("_healthFill", _fill);
            // EditMode 只显式驱动被测生命周期；不启动 Scene、Boss AI 或全局常驻 Host。
            Call("OnEnable"); Call("Start");
        }

        [TearDown]
        public void ReleaseOnlyFixtureSubscriptionsAndObjects()
        {
            if (_controller != null) Call("OnDisable");
            if (_owner != null) Object.DestroyImmediate(_owner);
        }

        [Test]
        public void DisableHidesOldBossChildBeforePersistentHudIsReused()
        {
            Initialize(42, 230f, 1000f, 3);
            Assert.That(_visual.activeSelf, Is.True);
            Call("OnDisable");
            Assert.That(_visual.activeSelf, Is.False,
                "父 HUD 停用不等于子节点 authored activeSelf 已清；下一区域重用时不能带出旧 Boss 条。");
            Assert.That(Field<bool>("_isInitialized"), Is.False);
            Assert.That(Field<int>("_bossId"), Is.EqualTo(0));
        }

        [Test]
        public void ReenableWithoutSecondStartWaitsForNewBossThenUsesItsSnapshot()
        {
            Initialize(42, 230f, 1000f, 3);
            Call("OnDisable"); Call("OnEnable");
            // DontDestroyOnLoad 的同一 UI 不会在第二次进入区域时重跑 Start，清理必须在释放时完成。
            Assert.That(_visual.activeSelf, Is.False, "新 Context 尚无 Boss 初始化时，旧画面必须保持隐藏。");
            Initialize(77, 400f, 500f, 1);
            Assert.That(_visual.activeSelf, Is.True);
            Assert.That(Field<int>("_bossId"), Is.EqualTo(77));
            Assert.That(Field<byte>("_displayedPhase"), Is.EqualTo(1));
            Assert.That(_fill.fillAmount, Is.EqualTo(.8f).Within(.001f));
        }

        [Test]
        public void BoundWorldRejectsOldAndUnscopedBossInitialization()
        {
            BindWorld(101);
            Initialize(42, 230f, 1000f, 3, 202);
            Assert.That(_visual.activeSelf, Is.False, "旧区域的初始化/重放不具备当前 Context 的显示资格。");
            Initialize(43, 230f, 1000f, 3);
            Assert.That(_visual.activeSelf, Is.False, "Host 已指定区域时，缺少区域身份的初始化同样拒绝。");
            Initialize(77, 400f, 500f, 1, 101);
            Assert.That(_visual.activeSelf, Is.True);
            Assert.That(Field<int>("_bossId"), Is.EqualTo(77));
        }

        [Test]
        public void RebindingSameWorldPreservesBossWhileChangingWorldClearsItsIdentity()
        {
            BindWorld(101); Initialize(42, 230f, 1000f, 3, 101);
            BindWorld(101);
            Assert.That(_visual.activeSelf, Is.True, "同区域的幂等 Context 更新不能清掉仍然有效的 Boss 初始化。");
            Assert.That(_fill.fillAmount, Is.EqualTo(.23f).Within(.001f));
            BindWorld(202);
            Assert.That(_visual.activeSelf, Is.False);
            Assert.That(Field<int>("_bossId"), Is.EqualTo(0));
            Initialize(42, 230f, 1000f, 3, 101);
            Assert.That(_visual.activeSelf, Is.False);
            Initialize(77, 400f, 500f, 1, 202);
            Assert.That(_visual.activeSelf, Is.True);
            Assert.That(Field<int>("_bossId"), Is.EqualTo(77));
        }

        private void BindWorld(int handle)
        {
            var method = typeof(BossHealthBarController).GetMethod("BindWorld", BindingFlags.Instance | BindingFlags.Public,
                null, new[] { typeof(int) }, null);
            Assert.That(method, Is.Not.Null, "常驻 Boss HUD 必须绑定当前 World 的身份，不能订阅并接受所有区域初始化。");
            method.Invoke(_controller, new object[] { handle });
        }

        private void Initialize(int id, float hp, float maximum, byte phase, int world = 0)
        {
            var method = typeof(BossHealthBarController).GetMethod("OnInitialized", Private);
            // 此测试无需扩展程序集引用；通过被测入口的已有参数类型构造只读初始化快照。
            var type = method.GetParameters()[0].ParameterType;
            var snapshot = Activator.CreateInstance(type);
            type.GetField("BossId").SetValue(snapshot, id);
            type.GetField("CurrentHp").SetValue(snapshot, hp);
            type.GetField("MaxHp").SetValue(snapshot, maximum);
            type.GetField("CurrentPhase").SetValue(snapshot, phase);
            type.GetField("WorldSceneHandle").SetValue(snapshot, world);
            method.Invoke(_controller, new[] { snapshot });
        }
        private void Call(string name) => typeof(BossHealthBarController).GetMethod(name, Private).Invoke(_controller, null);
        private void Set(string name, object value) => typeof(BossHealthBarController).GetField(name, Private).SetValue(_controller, value);
        private T Field<T>(string name) => (T)typeof(BossHealthBarController).GetField(name, Private).GetValue(_controller);
    }
}
