using System;
using System.Reflection;
using Game.Combat;
using Game.Core;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Character.Tests
{
    /// <summary>回归 Frozen 中不偷记点击、Overlay 与 locomotion 的 Animator 所有权，以及阶段复制没有 Gameplay 权限。</summary>
    public sealed class N4OnlinePresentationTests
    {
        private GameObject _root;
        private WizardController _wizard;
        private OnlineCastDriver _driver;

        [TearDown]
        public void Cleanup()
        {
            if (_driver != null) Invoke(_driver, "OnDisable");
            if (_root != null) Object.DestroyImmediate(_root);
        }

        private void CreateClient()
        {
            _root = new GameObject("N4 client presentation isolation"); _root.SetActive(false);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Prefabs/WizardPlayer.prefab");
            var instance = Object.Instantiate(prefab, _root.transform);
            _wizard = instance.GetComponent<WizardController>();
            // EditMode 不运行输入/自动 Update，只安装真实 Prefab 与其 Awake 所需组件。
            _wizard.ConfigureExecutionMode(PlayerExecutionMode.Replica); Invoke(_wizard, "Awake");
            var health = instance.GetComponent<HealthComponent>(); Invoke(health, "Awake");
            _driver = instance.AddComponent<OnlineCastDriver>();
            var definition = AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>("Assets/_Project/Resources/Online/CombatDefinition.asset");
            Assert.That(definition, Is.Not.Null);
            _driver.InitializeClient(1, 1, definition, true);
            _driver.InstallClientSetup(new string('a', 32), 1, .8f);
        }

        [Test]
        public void FrozenWithoutTicksRejectsNewClickAndClearsPreviouslyQueuedClick()
        {
            CreateClient(); _driver.CaptureAttackPress(); Assert.That(Queued, Is.True);
            // 不调用任何 Step；真实 Frozen 的关键正是 Driver 不再收到 Gameplay Tick。
            _driver.SetBlocked(true); Assert.That(Queued, Is.False);
            _driver.CaptureAttackPress(); Assert.That(Queued, Is.False, "Frozen 的 performed 回调不得留给恢复后发射");
            _driver.SetBlocked(false); Assert.That(Queued, Is.False);
            _driver.CaptureAttackPress(); Assert.That(Queued, Is.True, "解除门禁后的新按下应立即有效");
        }

        [Test]
        public void BlockedLocalActionReleasesAnimatorOwnershipWithoutChangingMovementFsm()
        {
            CreateClient(); var movement = _wizard.StateMachine.CurrentState;
            _driver.PresentAction(true);
            Assert.That(_wizard.IsOnlineActionPresentationActive, Is.True);
            _driver.SetBlocked(true);
            Assert.That(_wizard.IsOnlineActionPresentationActive, Is.False);
            Assert.That(_wizard.StateMachine.CurrentState, Is.SameAs(movement), "动画取消不能切换或重新执行移动 FSM");
        }

        [Test]
        public void RemoteResourceRejectionAlsoReturnsAnimatorToLocomotion()
        {
            CreateClient(); _driver.InitializeClient(2, 3, AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>("Assets/_Project/Resources/Online/CombatDefinition.asset"), false);
            _driver.PresentAction(true);
            _driver.ApplyReceipt(new OnlineCastReceipt { ActorId = 2, ConnectionEpoch = 3, CommandId = 123,
                Result = OnlineCastResult.Rejected, Failure = OnlineCastFailure.ResourceRejected });
            Assert.That(_wizard.IsOnlineActionPresentationActive, Is.False, "远端没有本机 _active Command，也必须取消被拒绝的动作");
        }

        [Test]
        public void SinglePlayerNeverAcceptsOnlineAnimatorOwnership()
        {
            CreateClient(); _wizard.ConfigureExecutionMode(PlayerExecutionMode.SinglePlayer);
            _wizard.SetOnlineActionPresentation(true);
            Assert.That(_wizard.IsOnlineActionPresentationActive, Is.False);
        }

        [Test]
        public void ReplicaBossPhaseIsMonotonicAndDoesNotActivateCombatOrInvulnerability()
        {
            _root = new GameObject("N4 replica phase fixture"); _root.SetActive(false);
            // 此接口只能改阶段读数，不依赖 Animator、Roster、Program，也不得启动 FSM 或无敌状态。
            var boss = _root.AddComponent<WizardBossController>(); boss.ConfigureReplica();
            boss.ApplyReplicatedPhase(3); boss.ApplyReplicatedPhase(2); boss.ApplyReplicatedPhase(0); boss.ApplyReplicatedPhase(9);
            Assert.That(boss.CurrentPhase, Is.EqualTo(BossPhase.Phase3));
            Assert.That(boss.CurrentStateKind, Is.EqualTo(BossRuntimeStateKind.None));
            Assert.That(boss.IsEncounterActive, Is.False); Assert.That(boss.IsActionLocked, Is.False);
        }

        private bool Queued => (bool)typeof(OnlineCastDriver).GetField("_queued", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(_driver);
        private static void Invoke(object target, string name)
        {
            Type type = target.GetType(); MethodInfo method = null;
            while (type != null && method == null) { method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); type = type.BaseType; }
            Assert.That(method, Is.Not.Null, name); method.Invoke(target, null);
        }
    }
}
