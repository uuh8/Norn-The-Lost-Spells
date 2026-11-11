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
        public void NaturalCastReleaseKeepsTheOriginalAttackAnimatorExit()
        {
            CreateClient();
            _root.SetActive(true);
            var definition = AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>("Assets/_Project/Resources/Online/CombatDefinition.asset");
            var animator = _wizard.Animator;
            animator.Rebind();
            _driver.PresentAction(false);
            // 出手进度为 .4，原 Attack01 到 .625 才进入正常 EXIT；权威收据不能提前切 Idle。
            animator.Play(definition.AnimationHash(false), 0, .4f); animator.Update(0f);
            typeof(OnlineCastDriver).GetField("_active", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(_driver, new OnlineCastIntent { CommandId = 71, ConnectionEpoch = 1 });
            _driver.ApplyReceipt(new OnlineCastReceipt { ActorId = 1, ConnectionEpoch = 1, CommandId = 71,
                Result = OnlineCastResult.Released });
            animator.Update(.001f);
            Assert.That(_wizard.IsOnlineActionPresentationActive, Is.False);
            var visible = animator.IsInTransition(0) ? animator.GetNextAnimatorStateInfo(0) : animator.GetCurrentAnimatorStateInfo(0);
            Assert.That(visible.shortNameHash, Is.EqualTo(definition.AnimationHash(false)),
                "Gameplay 已释放后让原 Attack01 EXIT 收尾，出手收据不应把剩余姿态强切为 Idle/Run。");
        }

        [Test]
        public void NaturalCastReleaseDoesNotSnapTheAimVisualBackToItsMovementRoot()
        {
            CreateClient();
            var visual = _wizard.Animator.transform;
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var original = (Quaternion)typeof(OnlineCastDriver).GetField("_baseVisualLocalRotation", flags).GetValue(_driver);
            visual.localRotation = Quaternion.AngleAxis(90, Vector3.up) * original;
            // 由真实动作入口取得表现所有权；不直接伪造bool而遗漏它持有的上一帧world姿态。
            _driver.PresentAction(false);
            _wizard.FinishOnlineActionPresentation();
            var before = visual.localRotation;
            // 没有活跃 Timeline 等价于上一 Tick 已自然出手。原实现每次在这里直接归零90度。
            Invoke(_driver, "LateUpdate");
            Assert.That(Quaternion.Angle(before, visual.localRotation), Is.LessThan(89f),
                "自然交还 locomotion 必须平滑归还模型朝向，不能每发将 localRotation 瞬间硬归零。");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CastFacingSurvivesTheActualMovementSnapshotRotationCorrection(bool released)
        {
            CreateClient();
            _wizard.ConfigureExecutionMode(PlayerExecutionMode.Prediction);
            _root.SetActive(true);
            Invoke(_wizard.GroundChecker, "Awake");
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var timeline = (AuthorityActionTimeline)typeof(OnlineCastDriver).GetField("_timeline", flags).GetValue(_driver);
            Assert.That(timeline.TryStart(71, 1f, 1f), Is.True);
            _driver.PresentAction(false);
            // 零转向速度隔离“期望插值”与“父节点校正意外带来的跳转”：本帧无权主动转身，
            // 因而画面若仍转动，只可能是直接继承了Reconcile写入的物理根rotation。
            typeof(PlayerControllerBase).GetField("_rotationSpeed", flags).SetValue(_wizard, 0f);
            Invoke(_driver, "LateUpdate");
            var visual = _wizard.Animator.transform;
            Quaternion before = visual.rotation;
            if (released) { timeline.Cancel(); _wizard.FinishOnlineActionPresentation(); }
            var snapshot = _wizard.CaptureMovementSnapshot();
            snapshot.Rotation = Quaternion.AngleAxis(90f, Vector3.up) * snapshot.Rotation;
            _wizard.RestoreMovementSnapshot(snapshot);
            Assert.That(Quaternion.Angle(_wizard.transform.rotation, snapshot.Rotation), Is.LessThan(.01f),
                "物理根必须接受真实Authority校正，不能为了隐藏抖动忽略Snapshot。");
            Invoke(_driver, "LateUpdate");
            Assert.That(Quaternion.Angle(before, visual.rotation), Is.LessThan(.01f),
                "施法与自然归还都应从上一Render帧的world facing插值，不能把根节点校正当成本机模型转身。");
        }

        [Test]
        public void CancelledCastFacingReleasesItsCacheAndNextCastStartsFromTheNewPose()
        {
            CreateClient();
            _wizard.ConfigureExecutionMode(PlayerExecutionMode.Prediction);
            _root.SetActive(true);
            Invoke(_wizard.GroundChecker, "Awake");
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(PlayerControllerBase).GetField("_rotationSpeed", flags).SetValue(_wizard, 0f);
            var timeline = (AuthorityActionTimeline)typeof(OnlineCastDriver).GetField("_timeline", flags).GetValue(_driver);
            var visual = _wizard.Animator.transform;
            var original = visual.localRotation;
            Assert.That(timeline.TryStart(71, 1f, 1f), Is.True);
            _driver.PresentAction(false);
            Invoke(_driver, "LateUpdate");
            _driver.SetBlocked(true);
            Assert.That(timeline.IsActive, Is.False);
            Assert.That(Quaternion.Angle(visual.localRotation, original), Is.LessThan(.01f));
            _wizard.transform.rotation = Quaternion.AngleAxis(45f, Vector3.up);
            Invoke(_driver, "LateUpdate");
            Assert.That(Quaternion.Angle(visual.localRotation, original), Is.LessThan(.01f),
                "明确取消后不能继续用旧world缓存抵消正常locomotion转身。");
            _driver.SetBlocked(false);
            Assert.That(timeline.TryStart(72, 1f, 1f), Is.True);
            Quaternion nextStart = visual.rotation;
            _driver.PresentAction(false);
            // 下一发取得新的world起点；随后同一帧发生校正也不能把第一发旧姿态带回来。
            var snapshot = _wizard.CaptureMovementSnapshot();
            snapshot.Rotation = Quaternion.AngleAxis(90f, Vector3.up);
            _wizard.RestoreMovementSnapshot(snapshot);
            Invoke(_driver, "LateUpdate");
            Assert.That(Quaternion.Angle(visual.rotation, nextStart), Is.LessThan(.01f));
        }

        [Test]
        public void PressStartsPredictionInTheFirstEligibleTickWithoutWaitingForAReceipt()
        {
            CreateClient();
            _wizard.ConfigureExecutionMode(PlayerExecutionMode.Prediction);
            _root.SetActive(true);
            Invoke(_wizard.GroundChecker, "Awake");
            int intents = 0;
            _driver.IntentProduced += _ => intents++;
            _driver.CaptureAttackPress();
            Assert.That(Queued, Is.True);
            Assert.That(intents, Is.Zero, "performed只锁存输入；网络动作统一在下一Tick产生。");
            _driver.StepPrediction(1f / 30f, 10, false);
            Assert.That(intents, Is.EqualTo(1));
            Assert.That(_wizard.IsOnlineActionPresentationActive, Is.True,
                "没有注入Queued或Released收据：本机动作应在首个合法Tick已启动。");
            var timeline = (AuthorityActionTimeline)typeof(OnlineCastDriver)
                .GetField("_timeline", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_driver);
            Assert.That(timeline.ElapsedSeconds, Is.Zero, "启动帧不虚构已经过的出手时间。");
            _driver.StepPrediction(1f / 30f, 11, false);
            Assert.That(intents, Is.EqualTo(1), "后续Tick只能推进同一动作，不能重发输入或重启动画。");
            Assert.That(timeline.ElapsedSeconds, Is.EqualTo(1f / 30f).Within(.000001f));
        }

        [Test]
        public void NaturalAirCastReleaseSurvivesTheNextPredictionMovementTick()
        {
            var definition = PrepareNaturallyReleasedAirCast();
            Vector3 before = _wizard.transform.position;
            // 真实玩家路径：本机施法释放后，下一网络步先跑移动 Simulate，最后才推进 Cast Prediction。
            // 不能只 Animator.Update：抢占发生在移动 Tick 内的 SyncCharacterAnimatorParameters。
            _wizard.Simulate(new PlayerSimulationFrame(1, 1, Vector2.right, 0,
                false, false, false, false), 1f / 30f);
            _wizard.Animator.Update(.001f);
            Assert.That(_wizard.StateMachine.CurrentState, Is.SameAs(_wizard.AirborneState));
            Assert.That(_wizard.transform.position.x, Is.GreaterThan(before.x), "保护动画尾段不能暂停水平移动。");
            Assert.That(_wizard.transform.position.y, Is.LessThan(before.y), "保护动画尾段不能停止重力积分。");
            Assert.That(_wizard.IsOnlineActionPresentationActive, Is.False, "释放后不能继续占用活跃施法 Overlay。");
            var visible = _wizard.Animator.IsInTransition(0)
                ? _wizard.Animator.GetNextAnimatorStateInfo(0) : _wizard.Animator.GetCurrentAnimatorStateInfo(0);
            Assert.That(visible.shortNameHash, Is.EqualTo(definition.AnimationHash(true)),
                "仍在空中的真实下一移动 Tick 不得在原 JumpAttack EXIT 前强切 JumpAir。");
        }

        [Test]
        public void NaturalAirCastTailCanStillBeCancelledAndReplaced()
        {
            var definition = PrepareNaturallyReleasedAirCast();
            // 释放已经把 live Overlay 置 false。显式取消仍须清理尾段，不能被相同 bool 的早退吞掉。
            _driver.SetBlocked(true);
            _wizard.Animator.Update(.001f);
            var cancelled = _wizard.Animator.IsInTransition(0)
                ? _wizard.Animator.GetNextAnimatorStateInfo(0) : _wizard.Animator.GetCurrentAnimatorStateInfo(0);
            Assert.That(cancelled.shortNameHash, Is.EqualTo(Animator.StringToHash("JumpAir_MagicWand")));
            _driver.SetBlocked(false);
            _driver.PresentAction(true);
            _wizard.Animator.Update(.001f);
            Assert.That(_wizard.IsOnlineActionPresentationActive, Is.True);
            var next = _wizard.Animator.IsInTransition(0)
                ? _wizard.Animator.GetNextAnimatorStateInfo(0) : _wizard.Animator.GetCurrentAnimatorStateInfo(0);
            Assert.That(next.shortNameHash, Is.EqualTo(definition.AnimationHash(true)),
                "旧尾段取消后不能挡住下一次真实动作表现。");
        }

        private OnlineCombatDefinition PrepareNaturallyReleasedAirCast()
        {
            CreateClient();
            // Replica 的 Simulate 会直接返回；必须使用 Prediction 才能穿过真实运动 FSM、CC 和 Animator 同步。
            _wizard.ConfigureExecutionMode(PlayerExecutionMode.Prediction);
            _root.SetActive(true);
            Invoke(_wizard.GroundChecker, "Awake");
            _wizard.RestoreMovementSnapshot(new PlayerMovementSnapshot
            {
                Position = new Vector3(1000, 100, 1000), Rotation = Quaternion.identity,
                TargetFacing = Vector3.forward, VerticalVelocity = -1f, Locomotion = PlayerLocomotionKind.Airborne,
                IsGrounded = false, GroundNormal = Vector3.up, ExtraJumpsRemaining = 1, AirDashesRemaining = 1
            });
            var definition = AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>("Assets/_Project/Resources/Online/CombatDefinition.asset");
            _wizard.Animator.Rebind();
            _driver.PresentAction(true);
            // 当前配置的出手点对应约 .4，原 JumpAttack 的 EXIT 在 .625；这里停在出手后、退出前。
            _wizard.Animator.Play(definition.AnimationHash(true), 0, .4f);
            _wizard.Animator.Update(0f);
            typeof(OnlineCastDriver).GetField("_active", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(_driver, new OnlineCastIntent { CommandId = 72, ConnectionEpoch = 1 });
            _driver.ApplyReceipt(new OnlineCastReceipt { ActorId = 1, ConnectionEpoch = 1, CommandId = 72,
                Result = OnlineCastResult.Released });
            Assert.That(_wizard.IsOnlineActionPresentationActive, Is.False);
            Assert.That(_wizard.GroundChecker.IsGrounded, Is.False);
            return definition;
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
