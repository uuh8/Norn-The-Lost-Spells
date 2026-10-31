#if UNITY_EDITOR
using System.Collections;
using Game.Character;
using Game.Core;
using Game.Multiplayer;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Game.Run.Tests
{
    /// <summary>
    /// 检查生产 Replica 装配与原 Wizard Animator 的连接：持续运动应真正进入 Run 并推进动画时间，
    /// 停止后应返回 Idle。只注入已获授权的运动表现值，不启动网络、施法权威或本机输入。
    /// 此夹具刻意不调用 Animator.Update/Play/CrossFade 来代替 locomotion，避免测试自己把原动画播起来。
    /// </summary>
    public sealed class RemoteLocomotionPresentationPlayModeTests
    {
        private static readonly int RunHash = Animator.StringToHash("Run_MagicWand");
        private static readonly int IdleHash = Animator.StringToHash("Idle_Bow");
        private Scene _scene, _previousScene;
        private GameObject _root;
        private NetworkActorRegistry _registry;
        private WizardController _wizard;
        private Animator _animator;
        private PlayerMovementSnapshot _groundedPose;
        private float _previousTimeScale;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _previousTimeScale = Time.timeScale;
            Time.timeScale = 1f;
            _previousScene = SceneManager.GetActiveScene();
            _scene = SceneManager.CreateScene("RemoteLocomotionPresentationIsolation");
            SceneManager.SetActiveScene(_scene);
            _root = new GameObject("OwnedRemoteLocomotionPresentationFixture");
            var profile = Resources.Load<OnlineMovementProfile>("Online/MovementProfile");
            Assert.That(profile, Is.Not.Null);

            // 复用生产 Registry：inactive hierarchy 中先配置 Replica，再触发 Awake，
            // 同时保留真实 Prefab/Controller/Avatar 和 AlwaysAnimate 设置；不制作简化的测试 Animator。
            _registry = new NetworkActorRegistry(profile, _root.transform);
            var slot = _registry.EnsurePlayer(1, PlayerExecutionMode.Replica);
            slot.Connected = true;
            slot.Epoch = 1;
            _wizard = slot.Player as WizardController;
            Assert.That(_wizard, Is.Not.Null, "当前生产玩家必须由真实 Wizard Prefab 装配。");
            _animator = _wizard.Animator;
            Assert.That(_animator, Is.Not.Null);
            Assert.That(_animator.runtimeAnimatorController, Is.Not.Null);
            Assert.That(_animator.enabled, Is.True, "Replica 需要正常的 Unity Animator 生命周期。");
            Assert.That(_animator.cullingMode, Is.EqualTo(AnimatorCullingMode.AlwaysAnimate));

            // 远离已打开场景的碰撞体，地面事实由 Snapshot 安装，不让单机地形决定本测试结果。
            // 保持根位置不动可明确证明：验证的是 Replica 表现入口，而非偷偷运行本地 CC/FSM。
            _groundedPose = new PlayerMovementSnapshot
            {
                Position = new Vector3(3000f, 100f, 3000f),
                Rotation = Quaternion.identity,
                TargetFacing = Vector3.forward,
                Locomotion = PlayerLocomotionKind.Grounded,
                IsGrounded = true,
                GroundNormal = Vector3.up,
                VerticalVelocity = -2f
            };
            _wizard.RestoreMovementSnapshot(_groundedPose);
            _wizard.PresentReplicaMovement(0f);
            // Start 只运行一次，必须先经过正常 PlayerLoop，再开始判定动画状态。
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _registry?.Neutralize();
            _registry?.Dispose();
            if (_root != null) Object.Destroy(_root);
            yield return null;
            if (_previousScene.IsValid() && _previousScene.isLoaded)
                SceneManager.SetActiveScene(_previousScene);
            if (_scene.IsValid() && _scene.isLoaded)
                yield return SceneManager.UnloadSceneAsync(_scene);
            Time.timeScale = _previousTimeScale;
        }

        [UnityTest]
        public IEnumerator ReplicaMovementAdvancesOriginalRunAnimationAndStoppingReturnsToIdle()
        {
            yield return PresentUntilState(1f, RunHash);
            float before = _animator.GetCurrentAnimatorStateInfo(0).normalizedTime;
            yield return PresentForSeconds(1f, .5f);

            var running = _animator.GetCurrentAnimatorStateInfo(0);
            Assert.That(running.shortNameHash, Is.EqualTo(RunHash));
            Assert.That(running.normalizedTime, Is.GreaterThan(before + .1f),
                "仅设置 speed 参数不能证明动画已播放；真实 Run State 的 normalizedTime 必须持续推进。");
            Assert.That(_wizard.transform.position, Is.EqualTo(_groundedPose.Position),
                "Replica 动画验收不得通过运行本机 Simulate/CharacterController.Move 获得位移。");

            yield return PresentUntilState(0f, IdleHash);
            Assert.That(_animator.GetCurrentAnimatorStateInfo(0).shortNameHash, Is.EqualTo(IdleHash));
        }

        [UnityTest]
        public IEnumerator RemoteCastReleaseReturnsWalkingReplicaToOriginalRunAnimation()
        {
            yield return PresentUntilState(1f, RunHash);
            var definition = Resources.Load<OnlineCombatDefinition>("Online/CombatDefinition");
            Assert.That(definition, Is.Not.Null);
            var driver = _wizard.gameObject.AddComponent<OnlineCastDriver>();
            // localOwner=false：不创建选择法杖的本机 Input 订阅，只消费远端批准的动作/收据。
            driver.InitializeClient(2, 1, definition, false);
            int attackHash = definition.AnimationHash(false);
            driver.ApplyReplicaAction(attackHash, 1, .1f);
            yield return PresentUntilState(1f, attackHash);
            Assert.That(_wizard.IsOnlineActionPresentationActive, Is.True);

            // 收据只交还 Animator 所有权，不调用 CastWand、不扣资源，也不切换运动 FSM。
            driver.ApplyReceipt(new OnlineCastReceipt
            {
                ActorId = 2,
                ConnectionEpoch = 1,
                CommandId = 1,
                Result = OnlineCastResult.Released
            });
            Assert.That(_wizard.IsOnlineActionPresentationActive, Is.False);
            yield return PresentUntilState(1f, RunHash);
            float before = _animator.GetCurrentAnimatorStateInfo(0).normalizedTime;
            yield return PresentForSeconds(1f, .5f);
            Assert.That(_animator.GetCurrentAnimatorStateInfo(0).shortNameHash, Is.EqualTo(RunHash));
            Assert.That(_animator.GetCurrentAnimatorStateInfo(0).normalizedTime, Is.GreaterThan(before + .1f),
                "施法释放后应恢复持续走路，不能只 CrossFade 一次后停留在某个姿势。");
        }

        private void Present(float moveAmount)
        {
            _wizard.RestoreMovementSnapshot(_groundedPose);
            _wizard.PresentReplicaMovement(moveAmount);
        }

        private IEnumerator PresentUntilState(float moveAmount, int expectedHash)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 2.0;
            do
            {
                Present(moveAmount);
                yield return null;
                // 等待 Transition 真正结束，不能把 NextState 已点名 Run 当作已经稳定跑步。
                if (!_animator.IsInTransition(0) &&
                    _animator.GetCurrentAnimatorStateInfo(0).shortNameHash == expectedHash)
                    yield break;
            }
            while (Time.realtimeSinceStartupAsDouble < deadline);
            Assert.Fail("原 Replica Animator 未在 2 秒内稳定进入目标状态；expected=" + expectedHash +
                ", actual=" + _animator.GetCurrentAnimatorStateInfo(0).shortNameHash +
                ", speed=" + _animator.speed + ", transition=" + _animator.IsInTransition(0));
        }

        private IEnumerator PresentForSeconds(float moveAmount, float seconds)
        {
            double end = Time.timeAsDouble + seconds;
            double deadline = Time.realtimeSinceStartupAsDouble + 5.0;
            do
            {
                Present(moveAmount);
                yield return null;
            }
            while (Time.timeAsDouble < end && Time.realtimeSinceStartupAsDouble < deadline);
            Assert.That(Time.timeAsDouble, Is.GreaterThanOrEqualTo(end),
                "Animator 生命周期需要正常推进的游戏时间，不能在暂停状态等待到超时后伪报通过。");
        }
    }
}
#endif
