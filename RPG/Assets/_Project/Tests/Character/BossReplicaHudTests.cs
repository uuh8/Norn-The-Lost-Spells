using System.Reflection;
using Game.Combat;
using Game.Core;
using Game.Run;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Character.Tests
{
    public sealed class BossReplicaHudTests
    {
        private GameObject _parent;
        private WizardBossController _boss;
        private HealthComponent _health;
        private bool _replaySubscribed;
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        [SetUp]
        public void SetUp()
        {
            _replaySubscribed = false;
            _parent = new GameObject("ReplicaBossFixture"); _parent.SetActive(false);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Prefabs/Boss/Boss.prefab");
            var instance = Object.Instantiate(prefab, _parent.transform);
            _boss = instance.GetComponent<WizardBossController>(); _health = instance.GetComponent<HealthComponent>();
            typeof(HealthComponent).GetMethod("Awake", Private).Invoke(_health, null);
            _health.ConfigureExecutionMode(CombatExecutionMode.Replica);
            typeof(WizardBossController).GetField("_health", Private).SetValue(_boss, _health);
            _boss.ConfigureReplica();
            _health.ApplyReplicatedState(new HealthReplicaSnapshot { CurrentHp = 800, MaxHp = 1000 });
        }
        [TearDown]
        public void TearDown()
        {
            if (_replaySubscribed && _boss != null)
                typeof(WizardBossController).GetMethod("OnDisable", Private).Invoke(_boss, null);
            Object.DestroyImmediate(_parent);
            EventBus<BossHudInitializedEvent>.Clear(); EventBus<BossHudStateChangedEvent>.Clear();
            EventBus<BossPhaseChangedEvent>.Clear();
        }

        [Test]
        public void FirstActivePhaseOneInitializesOriginalBossHudOnce()
        {
            int initialized = 0;
            BossHudInitializedEvent view = default;
            EventBus<BossHudInitializedEvent>.Subscribe(state => { initialized++; view = state; });
            _boss.ApplyReplicatedPhase(0); Assert.That(initialized, Is.Zero);
            _boss.ApplyReplicatedPhase(1); _boss.ApplyReplicatedPhase(1);
            Assert.That(initialized, Is.EqualTo(1), "默认阶段已是1也必须初始化正式HUD，不能只在Phase2才显示。");
            Assert.That(view.BossId, Is.EqualTo(_health.Id)); Assert.That(view.CurrentHp, Is.EqualTo(800));
        }

        [Test]
        public void StateOnlyHealthCorrectionAndDeathReachSharedHudWithoutDamageEvent()
        {
            int damage = 0; BossHudStateChangedEvent view = default;
            System.Action<DamageReceivedEvent> onDamage = _ => damage++;
            EventBus<DamageReceivedEvent>.Subscribe(onDamage);
            EventBus<BossHudStateChangedEvent>.Subscribe(state => view = state);
            try
            {
                _boss.ApplyReplicatedPhase(2);
                Assert.That(view.Active, Is.True); Assert.That(view.CurrentHp, Is.EqualTo(800));
                _health.ApplyReplicatedState(new HealthReplicaSnapshot { CurrentHp = 0, MaxHp = 1000 });
                _boss.ApplyReplicatedPhase(2);
                Assert.That(view.Active, Is.False); Assert.That(view.CurrentHp, Is.Zero);
                Assert.That(damage, Is.Zero, "校正UI不能重播受击、血液或音效。");
            }
            finally { EventBus<DamageReceivedEvent>.Unsubscribe(onDamage); }
        }

        [Test]
        public void ReplayRequiresMatchingWorldAndConfirmedActiveReplicaWithoutRepeatingPhaseGameplay()
        {
            EnableReplay();
            int initialized = 0, phases = 0, teleports = 0; BossHudInitializedEvent view = default;
            EventBus<BossHudInitializedEvent>.Subscribe(state => { initialized++; view = state; });
            EventBus<BossPhaseChangedEvent>.Subscribe(_ => phases++);
            System.Action<BossTeleportEvent> onTeleport = _ => teleports++;
            EventBus<BossTeleportEvent>.Subscribe(onTeleport);
            try
            {
                RequestReplay(_boss.gameObject.scene.handle);
                Assert.That(initialized, Is.Zero, "尚无有效 Phase 确认的 Replica 不能因 UI 请求而开战。");
                _boss.ApplyReplicatedPhase(2); initialized = 0; phases = 0;
                int wrongScene = _boss.gameObject.scene.handle ^ 0x40000000;
                RequestReplay(wrongScene); Assert.That(initialized, Is.Zero);
                RequestReplay(_boss.gameObject.scene.handle); RequestReplay(_boss.gameObject.scene.handle);
                Assert.That(initialized, Is.EqualTo(2), "晚创建或重新启用的 HUD 可反复取得当前只读初始化快照。");
                // Scene.handle 在当前 Unity 的 API 类型与协议里的 int 不同；显式比较约定的 int 值，
                // 避免 NUnit 对两个打印值相同、装箱后类型不同的句柄误判为业务身份不一致。
                Assert.That(view.WorldSceneHandle, Is.EqualTo((int)_boss.gameObject.scene.handle));
                Assert.That(view.CurrentHp, Is.EqualTo(800)); Assert.That(view.CurrentPhase, Is.EqualTo(2));
                Assert.That(phases, Is.Zero, "HUD replay 不得再次发布阶段变更或触发无敌/程序解锁。");
                Assert.That(teleports, Is.Zero); Assert.That(_boss.IsEncounterActive, Is.False);
                Assert.That(_boss.CurrentStateKind, Is.EqualTo(BossRuntimeStateKind.None));
                Assert.That(_health.CurrentHp, Is.EqualTo(800));
            }
            finally { EventBus<BossTeleportEvent>.Unsubscribe(onTeleport); }
        }

        [Test]
        public void PhaseZeroDeathAndDisabledReplicaCannotReplayAnActiveHud()
        {
            EnableReplay(); int initialized = 0;
            EventBus<BossHudInitializedEvent>.Subscribe(_ => initialized++);
            _boss.ApplyReplicatedPhase(1); initialized = 0;
            _boss.ApplyReplicatedPhase(0); RequestReplay(_boss.gameObject.scene.handle);
            Assert.That(initialized, Is.Zero, "已有阶段值不代表最近一份服务器事实仍 Active。");
            _boss.ApplyReplicatedPhase(1); RequestReplay(_boss.gameObject.scene.handle);
            Assert.That(initialized, Is.EqualTo(1)); initialized = 0;
            _health.ApplyReplicatedState(new HealthReplicaSnapshot { CurrentHp = 0, MaxHp = 1000 });
            _boss.ApplyReplicatedPhase(1); RequestReplay(_boss.gameObject.scene.handle);
            Assert.That(initialized, Is.Zero);
            _health.ApplyReplicatedState(new HealthReplicaSnapshot { CurrentHp = 800, MaxHp = 1000 });
            RequestReplay(_boss.gameObject.scene.handle);
            Assert.That(initialized, Is.Zero, "不能仅凭 HP 恢复就沿用死亡前的 Active；还需新的合法阶段事实。");
            _boss.ApplyReplicatedPhase(1); RequestReplay(_boss.gameObject.scene.handle);
            Assert.That(initialized, Is.EqualTo(1)); initialized = 0;
            typeof(WizardBossController).GetMethod("OnDisable", Private).Invoke(_boss, null); _replaySubscribed = false;
            RequestReplay(_boss.gameObject.scene.handle);
            Assert.That(initialized, Is.Zero, "停用 Boss 必须取消请求订阅，不能继续给后续 Scope 重放旧状态。");
        }

        [Test]
        public void SinglePlayerReplayNeedsActiveEncounterAndAuthorityIgnoresClientUiRequest()
        {
            EnableReplay(); int initialized = 0;
            EventBus<BossHudInitializedEvent>.Subscribe(_ => initialized++);
            // 这里只安排执行职责和 Encounter 事实，不调用 TryActivate，不创建目标或推进 Gameplay。
            typeof(WizardBossController).GetField("_executionMode", Private).SetValue(_boss, CombatExecutionMode.SinglePlayer);
            RequestReplay(_boss.gameObject.scene.handle); Assert.That(initialized, Is.Zero);
            typeof(WizardBossController).GetField("_isEncounterActive", Private).SetValue(_boss, true);
            RequestReplay(_boss.gameObject.scene.handle); Assert.That(initialized, Is.EqualTo(1));
            typeof(WizardBossController).GetField("_executionMode", Private).SetValue(_boss, CombatExecutionMode.Authority);
            RequestReplay(_boss.gameObject.scene.handle); Assert.That(initialized, Is.EqualTo(1),
                "Dedicated 权威模拟不能消费 Client UI 重放请求。");
        }

        private void EnableReplay()
        {
            // inactive prefab 保持不运行 AI/导航；EditMode 显式驱动真实订阅生命周期。
            typeof(WizardBossController).GetMethod("OnEnable", Private).Invoke(_boss, null); _replaySubscribed = true;
        }
        private static void RequestReplay(int sceneHandle)
        {
            var type = typeof(BossHudInitializedEvent).Assembly.GetType("Game.Run.BossHudReplayRequestedEvent");
            Assert.That(type, Is.Not.Null, "缺少按 WorldSceneHandle 请求只读 Boss HUD 快照的 Run 事件。");
            object request = System.Activator.CreateInstance(type);
            type.GetField("WorldSceneHandle").SetValue(request, sceneHandle);
            typeof(EventBus<>).MakeGenericType(type).GetMethod("Publish").Invoke(null, new[] { request });
        }
    }
}
