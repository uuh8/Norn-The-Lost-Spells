using System;
using System.Reflection;
using Game.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Game.UI.Tests
{
    /// <summary>验证 UI 只消费对应角色的服务器事实，以及持续快照不会重建数字对象。</summary>
    public sealed class OnlineCombatHudTests
    {
        private GameObject _root;
        private Component _hud;
        private Type _type;
        private Text _hp, _mana, _boss, _charges, _cooldown, _phase;
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

        [SetUp]
        public void SetUp()
        {
            _type = typeof(PlayerHealthBar).Assembly.GetType("Game.UI.OnlineCombatHudController");
            Assert.That(_type, Is.Not.Null, "在线 HUD 尚未实现；RED 应反映缺失的真实表现消费者。");
            _root = new GameObject("OnlineHudTest"); _root.SetActive(false);
            _hud = _root.AddComponent(_type);
            Set("_localRoot", Child("Local").gameObject);
            Set("_bossRoot", Child("Boss").gameObject);
            Set("_hpFill", Image("HpFill")); Set("_manaFill", Image("ManaFill")); Set("_bossFill", Image("BossFill"));
            Set("_hpValue", _hp = Label("HpValue")); Set("_manaValue", _mana = Label("ManaValue"));
            Set("_bossValue", _boss = Label("BossValue")); Set("_chargesValue", _charges = Label("ChargesValue"));
            Set("_cooldownValue", _cooldown = Label("CooldownValue")); Set("_phaseValue", _phase = Label("PhaseValue"));
            Invoke("Awake");
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            EventBus<OnlineCombatHudStateEvent>.Clear();
        }

        [Test]
        public void RemotePlayerSnapshotDoesNotReplaceLocalHealthOrResources()
        {
            Apply(Local());
            Apply(new OnlineCombatHudStateEvent { ActorId = 2, CurrentHp = 1, MaxHp = 100, CurrentMana = 1, MaxMana = 100 });
            Assert.That(Read(_hp), Is.EqualTo("90/100"));
            Assert.That(Read(_mana), Is.EqualTo("35/80"));
            Assert.That(Read(_charges), Is.EqualTo("29"));
        }

        [Test]
        public void BossFactsAndLocalFactsHaveIndependentLabelsAndFractionalCooldown()
        {
            Apply(Local());
            Apply(new OnlineCombatHudStateEvent { ActorId = 7, IsBoss = true, CurrentHp = 1250, MaxHp = 2000, BossPhase = 2 });
            Assert.That(Read(_hp), Is.EqualTo("90/100"));
            Assert.That(Read(_boss), Is.EqualTo("1250/2000"));
            Assert.That(Read(_phase), Is.EqualTo("2"));
            Assert.That(Read(_cooldown), Is.EqualTo("1.3"), "冷却向上取到十分之一秒，不能显示尚未实际可用的 0.0。");
            var state = Local(); state.Unlimited = true; Apply(state);
            Assert.That(Read(_charges), Is.EqualTo("∞"));
        }

        [Test]
        public void InactiveBossFactsStayHiddenAndDoNotReplaceEncounterLabels()
        {
            Apply(Local());
            var bossRoot = _root.transform.Find("Boss").gameObject;
            var boss = new OnlineCombatHudStateEvent
            { ActorId = 7, IsBoss = true, CurrentHp = 1000, MaxHp = 1000, BossPhase = 0 };
            // 收到副本实体快照并不表示 encounter 已开始；未参战时不能泄露顶部 Boss HUD。
            Apply(boss);
            Assert.That(bossRoot.activeSelf, Is.False, "入场出生的未激活 Boss 不应显示 BossBar。");
            Assert.That(Read(_boss), Is.Empty, "未激活事实也不应刷新隐藏的 Boss 数字。");
            Assert.That(Read(_phase), Is.Empty);

            boss.BossPhase = 2; boss.CurrentHp = 800;
            Apply(boss);
            Assert.That(bossRoot.activeSelf, Is.True, "首次实际 encounter 事实才授予显示资格。");
            Assert.That(Read(_boss), Is.EqualTo("800/1000"));
            Assert.That(Read(_phase), Is.EqualTo("2"));

            // 即使随后收到带其他 HP 的 inactive 快照，也只能撤销显示，不能覆盖参战数字。
            boss.BossPhase = 0; boss.CurrentHp = 500;
            Apply(boss);
            Assert.That(bossRoot.activeSelf, Is.False, "参战资格被撤销后必须隐藏 BossBar。");
            Assert.That(Read(_boss), Is.EqualTo("800/1000"));
            Assert.That(Read(_phase), Is.EqualTo("2"));
            Assert.That(Read(_hp), Is.EqualTo("90/100"), "Boss 显示资格不能清除本机 HUD。");
        }

        [Test]
        public void DeadBossHidesHudEvenWhenItsPreviousPhaseIsStillPresent()
        {
            var boss = new OnlineCombatHudStateEvent
            { ActorId = 7, IsBoss = true, CurrentHp = 1000, MaxHp = 1000, BossPhase = 2 };
            Apply(boss);
            var bossRoot = _root.transform.Find("Boss").gameObject;
            Assert.That(bossRoot.activeSelf, Is.True);
            // 阶段描述可能仍保留最后一阶段；死亡 HP 事实已足以撤销显示资格。
            boss.CurrentHp = 0; Apply(boss);
            Assert.That(bossRoot.activeSelf, Is.False, "Boss 已死亡后，不能永久留下空的 BossBar。");
        }

        [Test]
        public void UnchangedRoundedValuesReuseTheSameDigitObjectsAndStrings()
        {
            Apply(Local());
            var digits = _hp.GetComponentsInChildren<Text>(true);
            var texts = new string[digits.Length];
            for (int i = 0; i < digits.Length; i++) texts[i] = digits[i].text;
            int objects = _root.GetComponentsInChildren<Transform>(true).Length;
            var state = Local(); state.CurrentHp = 89.1f;
            for (int n = 0; n < 120; n++) Apply(state);
            Assert.That(_root.GetComponentsInChildren<Transform>(true).Length, Is.EqualTo(objects));
            for (int i = 0; i < digits.Length; i++) Assert.That(ReferenceEquals(digits[i].text, texts[i]), Is.True);
            Assert.That(Read(_hp), Is.EqualTo("90/100"));
        }

        [Test]
        public void DisableReleasesEventSubscriptionInsteadOfUpdatingHiddenOldScene()
        {
            Invoke("OnEnable"); EventBus<OnlineCombatHudStateEvent>.Publish(Local());
            Assert.That(Read(_hp), Is.EqualTo("90/100"));
            Invoke("OnDisable");
            var next = Local(); next.CurrentHp = 0; EventBus<OnlineCombatHudStateEvent>.Publish(next);
            Assert.That(Read(_hp), Is.EqualTo("90/100"));
        }

        private static OnlineCombatHudStateEvent Local() => new OnlineCombatHudStateEvent
        { ActorId = 1, IsLocal = true, CurrentHp = 89.9f, MaxHp = 100, CurrentMana = 34.1f, MaxMana = 80, Charges = 29, Cooldown = 1.21f };
        private RectTransform Child(string name)
        { var go = new GameObject(name, typeof(RectTransform)); go.transform.SetParent(_root.transform, false); return go.GetComponent<RectTransform>(); }
        private Image Image(string name) => Child(name).gameObject.AddComponent<Image>();
        private Text Label(string name)
        { var text = Child(name).gameObject.AddComponent<Text>(); text.fontSize = 20; text.rectTransform.sizeDelta = new Vector2(300, 30); return text; }
        private void Set(string field, object value) => _type.GetField(field, Fields).SetValue(_hud, value);
        private void Invoke(string method) => _type.GetMethod(method, Fields).Invoke(_hud, null);
        private void Apply(OnlineCombatHudStateEvent state) => _type.GetMethod("ApplyState").Invoke(_hud, new object[] { state });
        private static string Read(Text parent)
        {
            string result = parent.text;
            foreach (var child in parent.GetComponentsInChildren<Text>(true))
                if (child != parent && child.enabled) result += child.text;
            return result;
        }
    }
}
