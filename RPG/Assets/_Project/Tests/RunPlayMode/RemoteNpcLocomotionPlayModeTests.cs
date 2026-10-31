#if UNITY_EDITOR
using System;
using System.Collections;
using System.Reflection;
using Game.Character;
using Game.Combat;
using Game.Core;
using Game.Multiplayer;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Game.Run.Tests
{
    /// <summary>
    /// 从正式 P8 配置建立真实 Boss Replica，经过生产 OnlineWorldBootstrap.LateUpdate 更新原 Animator。
    /// 这个回归专门捕捉“普通 Enemy 分支写了 speed，Boss 分支只安装 Transform”的遗漏。
    /// 这里只替代网络收包，不替代 Registry、插值器、角色或 Animator，也不手动播放 Run 动画。
    /// </summary>
    public sealed class RemoteNpcLocomotionPlayModeTests
    {
        private static readonly int RunHash = Animator.StringToHash("Run_MagicWand");
        private static readonly int SpeedHash = Animator.StringToHash("speed");
        private static readonly int MoveXHash = Animator.StringToHash("moveX");
        private static readonly int MoveZHash = Animator.StringToHash("moveZ");
        private static readonly Vector3 Origin = new Vector3(23000f, 3000f, 17000f);
        private static readonly Vector3 Velocity = new Vector3(1.5f, 0f, 1.5f);
        private const double SnapshotInterval = 1d / 30d;
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        private Scene _scene, _previousScene;
        private GameObject _actors, _worldRoot;
        private NetworkActorRegistry _registry, _ordinaryRegistry;
        private OnlineWorldBootstrap _world;
        private NetworkActorRegistry.Slot _slot;
        private WizardBossController _boss;
        private Animator _animator;
        private Action _presentWorld;
        private uint _tick, _sequence;
        private double _lastSentAt;
        private float _previousTimeScale;
        private bool _stationary;
        private Vector3 _stationaryPosition;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _previousTimeScale = Time.timeScale;
            Time.timeScale = 1f;
            _previousScene = SceneManager.GetActiveScene();
            _scene = SceneManager.CreateScene("RemoteNpcLocomotionIsolation");
            SceneManager.SetActiveScene(_scene);

            var profile = Resources.Load<OnlineMovementProfile>("Online/MovementProfile");
            var combat = Resources.Load<OnlineCombatDefinition>("Online/CombatDefinition");
            var catalog = Resources.Load<OnlineDungeonCatalog>("Online/DungeonCatalog");
            Assert.That(profile, Is.Not.Null);
            Assert.That(combat, Is.Not.Null);
            Assert.That(catalog, Is.Not.Null);
            Assert.That(catalog.TryGet("p8", out var dungeon), Is.True);
            Assert.That(dungeon.TryGetNpc(5, out _), Is.True, "正式 P8 的首个 NPC 必须仍是配置中的 Actor5。");

            _actors = new GameObject("OwnedRemoteNpcActors");
            _registry = new NetworkActorRegistry(profile, _actors.transform, combat,
                new CombatSimulationWorld(), dungeon);
            _slot = _registry.EnsureEnemy(5, false);
            _boss = _slot.Boss;
            Assert.That(_boss, Is.Not.Null, "必须覆盖正式 P8 Boss，不能把普通 Enemy 当作 Boss 替身。");
            _slot.Connected = true;
            _slot.Epoch = 1;
            _animator = _boss.GetComponentInChildren<Animator>(true);
            Assert.That(_animator, Is.Not.Null);
            Assert.That(_animator.enabled, Is.True);
            Assert.That(_animator.runtimeAnimatorController, Is.Not.Null);
            Assert.That(_animator.cullingMode, Is.EqualTo(AnimatorCullingMode.AlwaysAnimate));

            // 组合根保持 inactive，避免 Awake/Update 争用在线单例或尝试认证；实际 Actor 单独保持 active，
            // 由 Unity PlayerLoop 正常求值 Animator。委托调用的仍是生产 LateUpdate 全部分支，
            // 因而测试不会通过直接调用未来的 Boss 表现 API 绕过本次真正遗漏的组合层接线。
            _worldRoot = new GameObject("InactiveRemoteNpcWorldFixture");
            _worldRoot.SetActive(false);
            _world = _worldRoot.AddComponent<OnlineWorldBootstrap>();
            typeof(OnlineWorldBootstrap).GetField("_profile", Private).SetValue(_world, profile);
            typeof(OnlineWorldBootstrap).GetField("<Registry>k__BackingField", Private).SetValue(_world, _registry);
            var lateUpdate = typeof(OnlineWorldBootstrap).GetMethod("LateUpdate", Private);
            Assert.That(lateUpdate, Is.Not.Null);
            _presentWorld = (Action)Delegate.CreateDelegate(typeof(Action), _world, lateUpdate);

            // 两个端点定义已知的世界水平速度，Rotation 为 identity，因此 moveX/moveZ 都应为正。
            // 后面继续按 30Hz 投递同一条轨迹，避免断流自动归零掩盖“持续走路”是否成立。
            _tick = 30;
            _sequence = 0;
            _stationary = false;
            _lastSentAt = Time.realtimeSinceStartupAsDouble;
            AddState(_tick, _lastSentAt - SnapshotInterval);
            AddState(++_tick, _lastSentAt);
            _presentWorld();
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _registry?.Neutralize();
            _registry?.Dispose();
            _ordinaryRegistry?.Neutralize();
            _ordinaryRegistry?.Dispose();
            if (_actors != null) Object.Destroy(_actors);
            if (_worldRoot != null) Object.Destroy(_worldRoot);
            yield return null;
            if (_previousScene.IsValid() && _previousScene.isLoaded)
                SceneManager.SetActiveScene(_previousScene);
            if (_scene.IsValid() && _scene.isLoaded)
                yield return SceneManager.UnloadSceneAsync(_scene);
            Time.timeScale = _previousTimeScale;
        }

        [UnityTest]
        public IEnumerator WorldReplicaSnapshotsDriveOrdinaryEnemyRunAndStopWithOriginalMeterPerSecondParameters()
        {
            var profile = Resources.Load<OnlineMovementProfile>("Online/MovementProfile");
            var combat = Resources.Load<OnlineCombatDefinition>("Online/CombatDefinition");
            var catalog = Resources.Load<OnlineDungeonCatalog>("Online/DungeonCatalog");
            Assert.That(catalog.TryGet("p7", out var dungeon), Is.True);
            _ordinaryRegistry = new NetworkActorRegistry(profile, _actors.transform, combat,
                new CombatSimulationWorld(), dungeon);
            _slot = _ordinaryRegistry.EnsureEnemy(5, false);
            Assert.That(_slot.Enemy, Is.Not.Null, "本回归额外覆盖正式 P7 普通敌人，不能只靠 Boss 入口通过。");
            // 正式P7的NPC以Dormant导出。生产OnState会同时安装Connected并激活Root，
            // 本夹具直接向插值器喂包，必须补上同一生命周期边界；单写Connected不会让Animator进入PlayerLoop。
            Assert.That(_ordinaryRegistry.SetNpcActive(5, true), Is.True);
            Assert.That(_slot.Root.activeInHierarchy, Is.True);
            _slot.Epoch = 1;
            typeof(OnlineWorldBootstrap).GetField("<Registry>k__BackingField", Private).SetValue(_world, _ordinaryRegistry);
            var animator = _slot.Enemy.Animator;
            Assert.That(animator, Is.Not.Null);
            int runHash = Animator.StringToHash("Run_SingleTwohandSword");
            int idleHash = Animator.StringToHash("Idle_SingleTwohandSword");
            _tick = 30; _sequence = 0; _lastSentAt = Time.realtimeSinceStartupAsDouble;
            AddState(_tick, _lastSentAt - SnapshotInterval);
            AddState(++_tick, _lastSentAt);

            yield return PresentUntilState(animator, 0, runHash);
            Assert.That(animator.GetFloat(SpeedHash), Is.EqualTo(_slot.Interpolation.PlanarSpeed).Within(.001f),
                "普通 Enemy 与原单机采用同样的米/秒参数，不能在组合层把它截成 0/1。");
            float before = animator.GetCurrentAnimatorStateInfo(0).normalizedTime;
            double end = Time.timeAsDouble + .3d;
            double deadline = Time.realtimeSinceStartupAsDouble + 5d;
            do
            {
                PresentMovingSnapshots();
                var installedPosition = _slot.Body.position;
                yield return null;
                Assert.That(_slot.Body.position, Is.EqualTo(installedPosition));
            }
            while (Time.timeAsDouble < end && Time.realtimeSinceStartupAsDouble < deadline);
            Assert.That(Time.timeAsDouble, Is.GreaterThanOrEqualTo(end));
            Assert.That(animator.GetCurrentAnimatorStateInfo(0).shortNameHash, Is.EqualTo(runHash));
            Assert.That(animator.GetCurrentAnimatorStateInfo(0).normalizedTime, Is.GreaterThan(before + .1f));

            // 停止位置取最后一份已发送端点，不能取仍落后于它的Render位置而制造一次测试自己的回拉。
            _stationary = true;
            _stationaryPosition = Origin + Velocity * ((_tick - 30) / 30f);
            yield return PresentUntilState(animator, 0, idleHash);
            Assert.That(animator.GetFloat(SpeedHash), Is.Zero);
            Assert.That(_slot.Enemy.IsOnlineReplica, Is.True);
            Assert.That(_slot.Enemy.IsOnlineAuthority, Is.False);
        }

        [UnityTest]
        public IEnumerator BossWalkingParametersKeepApprovedUpperBodyCastPlayingWithoutGameplayActionLock()
        {
            yield return PresentUntilState(_animator, 0, RunHash);
            int castHash = Animator.StringToHash(_boss.Definition.CastAnimatorTrigger);
            int upperCastHash = Animator.StringToHash("UpperBodyCast");
            _boss.ApplyReplicaAction(castHash, 2, 0f);
            yield return PresentUntilState(_animator, 1, upperCastHash);
            float before = _animator.GetCurrentAnimatorStateInfo(1).normalizedTime;
            double end = Time.timeAsDouble + .1d;
            double deadline = Time.realtimeSinceStartupAsDouble + 5d;
            do
            {
                PresentMovingSnapshots();
                yield return null;
            }
            while (Time.timeAsDouble < end && Time.realtimeSinceStartupAsDouble < deadline);
            Assert.That(Time.timeAsDouble, Is.GreaterThanOrEqualTo(end));
            Assert.That(_animator.GetCurrentAnimatorStateInfo(1).shortNameHash, Is.EqualTo(upperCastHash));
            Assert.That(_animator.GetCurrentAnimatorStateInfo(1).normalizedTime, Is.GreaterThan(before + .02f));
            Assert.That(_animator.GetCurrentAnimatorStateInfo(0).shortNameHash, Is.EqualTo(RunHash));
            Assert.That(_animator.GetFloat(SpeedHash), Is.GreaterThan(.1f));
            Assert.That(_boss.IsActionLocked, Is.False,
                "远端已批准的上身施法与腿部走位可以并行，不借用 Gameplay Action Lock 冻住两者。");
            Assert.That(_boss.CurrentStateKind, Is.EqualTo(BossRuntimeStateKind.Inactive));
        }

        [Test]
        public void PlanarVelocityUsesSnapshotWorldDirectionAndClearsWithSpeedOnFreezeSilenceAndReset()
        {
            var playback = new RemoteMovementInterpolation();
            playback.TryAdd(VelocityState(30, 1, Vector3.zero), 5d);
            playback.TryAdd(VelocityState(33, 2, new Vector3(.3f, 7f, .4f)), 5.1d);
            playback.TrySample(5.15d, .1f, out _);
            Assert.That(playback.PlanarVelocity.x, Is.EqualTo(3f).Within(.001f));
            Assert.That(playback.PlanarVelocity.y, Is.Zero, "跳跃竖直位移不参与水平移动动画。");
            Assert.That(playback.PlanarVelocity.z, Is.EqualTo(4f).Within(.001f));
            Assert.That(playback.PlanarSpeed, Is.EqualTo(5f).Within(.001f));

            var frozen = VelocityState(33, 3, new Vector3(.3f, 7f, .4f));
            frozen.Frozen = true;
            playback.TryAdd(frozen, 5.151d);
            Assert.That(playback.PlanarVelocity, Is.EqualTo(Vector3.zero));
            Assert.That(playback.PlanarSpeed, Is.Zero);

            playback.Reset();
            playback.TryAdd(VelocityState(30, 1, Vector3.zero), 6d);
            playback.TryAdd(VelocityState(33, 2, new Vector3(.3f, 0f, .4f)), 6.1d);
            playback.TrySample(6.15d, .1f, out _);
            Assert.That(playback.PlanarSpeed, Is.GreaterThan(0f));
            playback.TrySample(6.5d, .1f, out _);
            Assert.That(playback.PlanarVelocity, Is.EqualTo(Vector3.zero));
            Assert.That(playback.PlanarSpeed, Is.Zero);
            playback.Reset();
            Assert.That(playback.PlanarVelocity, Is.EqualTo(Vector3.zero));
            Assert.That(playback.PlanarSpeed, Is.Zero);
        }

        [Test]
        public void BossTeleportAnimationGateUsesPlaybackTickAndNeverMovesRootOrAcquiresGameplayLock()
        {
            Vector3 position = _boss.transform.position;
            _boss.ApplyReplicatedTeleport(0, position, position + Vector3.right * 10f, 60);
            _boss.PresentReplicaMovement(Velocity, 1.99d);
            Assert.That(_animator.GetFloat(SpeedHash), Is.GreaterThan(.1f),
                "可靠 Telegraph 提前到达时，不能冻结尚未播放到 Telegraph Tick 的旧走路历史。");
            _boss.PresentReplicaMovement(Velocity, 2.01d);
            Assert.That(_animator.GetFloat(SpeedHash), Is.Zero);
            _boss.ApplyReplicatedTeleport(2, position, position + Vector3.right * 10f, 78);
            _boss.PresentReplicaMovement(Velocity * 100f, 2.59d);
            Assert.That(_animator.GetFloat(SpeedHash), Is.Zero,
                "Arrived 已收到也不能提前把尚在跨传送Tick的巨大位移当作跑步速度。");
            Assert.That(_animator.GetFloat(MoveXHash), Is.Zero);
            Assert.That(_animator.GetFloat(MoveZHash), Is.Zero);
            _boss.PresentReplicaMovement(Velocity, 2.61d);
            Assert.That(_animator.GetFloat(SpeedHash), Is.GreaterThan(.1f));
            Assert.That(_boss.transform.position, Is.EqualTo(position));
            Assert.That(_boss.IsActionLocked, Is.False);
            Assert.That(_boss.IsEncounterActive, Is.False);

            _boss.ApplyReplicatedTeleport(0, position, position + Vector3.right, 90);
            _boss.PresentReplicaMovement(Velocity, 3.01d);
            Assert.That(_animator.GetFloat(SpeedHash), Is.Zero);
            _boss.ApplyReplicatedTeleport(3, position, position + Vector3.right, 91);
            _boss.PresentReplicaMovement(Velocity, 3.02d);
            Assert.That(_animator.GetFloat(SpeedHash), Is.GreaterThan(.1f), "Cancelled 必须交还表现参数。");

            _boss.ApplyReplicatedTeleport(0, position, position + Vector3.right, 120);
            _boss.PresentReplicaMovement(Velocity, 6d);
            Assert.That(_animator.GetFloat(SpeedHash), Is.GreaterThan(.1f),
                "重连若错过Arrived，表现抑制仍须按共享SO的模拟时长有界退出，不能永久粘住。");
        }

        [UnityTest]
        public IEnumerator WorldReplicaSnapshotsDriveBossDirectionalRunWithoutAuthoritySimulation()
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 2d;
            do
            {
                PresentMovingSnapshots();
                yield return null;
                if (!_animator.IsInTransition(0) &&
                    _animator.GetCurrentAnimatorStateInfo(0).shortNameHash == RunHash)
                    break;
            }
            while (Time.realtimeSinceStartupAsDouble < deadline);

            Assert.That(_animator.GetFloat(SpeedHash), Is.GreaterThan(.1f),
                "Boss 位置快照已在移动，但组合层没有把段速度交给原 Animator。");
            Assert.That(_animator.GetFloat(MoveXHash), Is.GreaterThan(.1f),
                "Boss 的原 2D Blend Tree 需要本地水平方向，不能只给 speed 一个布尔值。");
            Assert.That(_animator.GetFloat(MoveZHash), Is.GreaterThan(.1f));
            Assert.That(_animator.GetCurrentAnimatorStateInfo(0).shortNameHash, Is.EqualTo(RunHash));

            float before = _animator.GetCurrentAnimatorStateInfo(0).normalizedTime;
            double end = Time.timeAsDouble + .5d;
            deadline = Time.realtimeSinceStartupAsDouble + 5d;
            do
            {
                PresentMovingSnapshots();
                var installedPosition = _boss.transform.position;
                yield return null;
                Assert.That(_boss.transform.position, Is.EqualTo(installedPosition),
                    "没有新表现安装时，Replica 的普通 Update/Animator 不得自行移动根或执行权威导航。");
            }
            while (Time.timeAsDouble < end && Time.realtimeSinceStartupAsDouble < deadline);

            Assert.That(Time.timeAsDouble, Is.GreaterThanOrEqualTo(end));
            Assert.That(_animator.GetCurrentAnimatorStateInfo(0).shortNameHash, Is.EqualTo(RunHash));
            Assert.That(_animator.GetCurrentAnimatorStateInfo(0).normalizedTime, Is.GreaterThan(before + .1f),
                "Run 状态的 normalizedTime 必须持续推进，不能仅凭参数或一次进入就宣称移动动画恢复。");
            Assert.That(_boss.ExecutionMode, Is.EqualTo(CombatExecutionMode.Replica));
            Assert.That(_boss.CurrentStateKind, Is.EqualTo(BossRuntimeStateKind.Inactive),
                "Awake 建立的惰性状态必须保持不变，表现安装不授予开战或导航权限。");
            Assert.That(_boss.IsEncounterActive, Is.False);
            Assert.That(_boss.IsActionLocked, Is.False,
                "表现修复不能通过获取 Gameplay Action Lock 或启动 Boss FSM 来间接播放动画。");
        }

        private void PresentMovingSnapshots()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            while (_lastSentAt + SnapshotInterval <= now)
            {
                _lastSentAt += SnapshotInterval;
                AddState(++_tick, now);
            }
            _presentWorld();
        }

        private void AddState(uint tick, double receivedAt)
        {
            var state = new MovementStateFrame
            {
                ActorId = 5, ConnectionEpoch = 1, SnapshotSequence = ++_sequence, ServerTick = tick,
                IsEnemy = true, Connected = true, Targetable = true,
                Movement = new PlayerMovementSnapshot
                {
                    Position = _stationary ? _stationaryPosition : Origin + Velocity * ((tick - 30) / 30f),
                    Rotation = Quaternion.identity, IsGrounded = true, GroundNormal = Vector3.up
                }
            };
            Assert.That(_slot.Interpolation.TryAdd(state, receivedAt), Is.True);
        }

        private IEnumerator PresentUntilState(Animator animator, int layer, int expectedHash)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 2d;
            do
            {
                PresentMovingSnapshots();
                yield return null;
                if (!animator.IsInTransition(layer) &&
                    animator.GetCurrentAnimatorStateInfo(layer).shortNameHash == expectedHash)
                    yield break;
            }
            while (Time.realtimeSinceStartupAsDouble < deadline);
            Assert.Fail("原NPC Animator未进入目标状态；layer=" + layer + ", expected=" + expectedHash +
                ", actual=" + animator.GetCurrentAnimatorStateInfo(layer).shortNameHash +
                ", move=" + animator.GetFloat(SpeedHash));
        }

        private static MovementStateFrame VelocityState(uint tick, uint sequence, Vector3 position) => new MovementStateFrame
        {
            ActorId = 5, ConnectionEpoch = 1, SnapshotSequence = sequence, ServerTick = tick,
            IsEnemy = true, Connected = true,
            Movement = new PlayerMovementSnapshot { Position = position, Rotation = Quaternion.identity }
        };
    }
}
#endif
