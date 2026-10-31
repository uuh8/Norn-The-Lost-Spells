using System.Reflection;
using Game.Combat;
using Game.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI.Tests
{
    /// <summary>验证同一套正式 HUD 换数据源，而不是用第二套联机控件绕开原组件。</summary>
    public sealed class LocalActorHudBindingTests
    {
        private GameObject _root, _a, _b;
        private PlayerHealthBar _hp;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("SharedHud"); _root.SetActive(false);
            _hp = _root.AddComponent<PlayerHealthBar>();
            _a = Actor("A", 42); _b = Actor("B", 73);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_root); Object.DestroyImmediate(_a); Object.DestroyImmediate(_b);
            EventBus<DamageReceivedEvent>.Clear(); EventBus<StatusChangedEvent>.Clear(); EventBus<HealthStateChangedEvent>.Clear();
        }

        [Test]
        public void BindInitializesFromReplicaAndRejectsPreviousActorDamage()
        {
            Bind(_hp, _a.GetComponent<HealthComponent>());
            Assert.That(Get<float>(_hp, "_targetFill"), Is.EqualTo(.42f).Within(.001f));
            Bind(_hp, _b.GetComponent<HealthComponent>());
            Invoke(_hp, "OnDamageReceived", new DamageReceivedEvent { TargetId = _a.GetInstanceID(), RemainingHp = 1 });
            Assert.That(Get<float>(_hp, "_targetFill"), Is.EqualTo(.73f).Within(.001f));
        }

        [Test]
        public void StateOnlyReplicaCorrectionReachesOriginalHealthBar()
        {
            // 完整状态恢复没有 Damage 事件；late join / 丢包恢复也必须显示真实 HP。
            Set(_hp, "_playerHealth", _a.GetComponent<HealthComponent>());
            Invoke(_hp, "OnEnable");
            try
            {
                _a.GetComponent<HealthComponent>().ApplyReplicatedState(new HealthReplicaSnapshot { CurrentHp = 11, MaxHp = 100 });
                // 数字在状态到达时刷新，而不是在每帧 Update 中格式化字符串。
                Assert.That(Get<float>(_hp, "_targetFill"), Is.EqualTo(.11f).Within(.001f));
            }
            finally { Invoke(_hp, "OnDisable"); }
        }

        [Test]
        public void RebindingManaReadsServerStateWithoutSpendingOrRegenerating()
        {
            var mana = _a.AddComponent<ManaComponent>(); mana.ConfigureExecutionMode(CombatExecutionMode.Replica);
            mana.ApplyReplicatedState(new ManaReplicaSnapshot { CurrentMana = 23, MaxMana = 80 });
            var bar = _root.AddComponent<PlayerManaBar>(); Bind(bar, mana);
            Assert.That(Get<float>(bar, "_targetFill"), Is.EqualTo(23f / 80).Within(.001f));
            Assert.That(mana.CurrentMana, Is.EqualTo(23));
        }

        [Test]
        public void LateStatusBindingReconstructsIconsAndUnbindClearsThem()
        {
            var status = _a.AddComponent<StatusController>(); status.ConfigureExecutionMode(CombatExecutionMode.Replica);
            status.ApplyReplicatedState(new StatusReplicaSnapshot { Burning = 35, MoveSpeedMultiplier = 1 });
            var bar = _root.AddComponent<PlayerStatusBar>();
            var iconRoot = new GameObject("Burning", typeof(RectTransform)); iconRoot.transform.SetParent(_root.transform);
            var icon = iconRoot.AddComponent<StatusIconView>();
            var labelRoot = new GameObject("Percent", typeof(RectTransform), typeof(Text)); labelRoot.transform.SetParent(iconRoot.transform);
            Set(icon, "_percentageText", labelRoot.GetComponent<Text>()); Set(bar, "_burningView", icon);
            Bind(bar, _a.GetComponent<HealthComponent>());
            // 父 Binder 可能先于子组件 Awake；晚到的生命周期不能把有效读模型清掉。
            Invoke(bar, "Awake");
            Assert.That(labelRoot.GetComponent<Text>().text, Is.EqualTo("35%"));
            Bind(bar, null);
            Assert.That(iconRoot.activeSelf, Is.False);
        }

        [Test]
        public void SharedBindingUsesOriginalComponentsAndClearsOnUnbind()
        {
            var type = typeof(PlayerHealthBar).Assembly.GetType("Game.UI.LocalActorHudBinding");
            Assert.That(type, Is.Not.Null, "缺少共享 HUD 绑定器，不应再创建独立 Online HP 控制器。");
            var binding = _root.AddComponent(type);
            Set(binding, "_healthBar", _hp);
            Invoke(binding, "Bind", _a);
            Assert.That(Get<HealthComponent>(_hp, "_playerHealth"), Is.SameAs(_a.GetComponent<HealthComponent>()));
            Invoke(binding, "Bind", (object)null);
            Assert.That(Get<HealthComponent>(_hp, "_playerHealth"), Is.Null);
        }

        private static GameObject Actor(string name, float hp)
        {
            var actor = new GameObject(name); var health = actor.AddComponent<HealthComponent>(); Invoke(health, "Awake");
            health.ConfigureExecutionMode(CombatExecutionMode.Replica);
            health.ApplyReplicatedState(new HealthReplicaSnapshot { CurrentHp = hp, MaxHp = 100 }); return actor;
        }
        private static void Bind(object bar, object actor)
        {
            var method = bar.GetType().GetMethod("Bind", BindingFlags.Public | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null, "原 HUD 需要离散 Bind API，保留单机序列化引用并支持联机 Actor。");
            method.Invoke(bar, new[] { actor });
        }
        private static void Set(object target, string field, object value) => target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value);
        private static T Get<T>(object target, string field) => (T)target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target);
        private static void Invoke(object target, string name, params object[] values) => target.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Invoke(target, values);
    }
}
