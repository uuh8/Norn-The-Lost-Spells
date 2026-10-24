#if UNITY_EDITOR
using System.Collections;
using System.Reflection;
using Game.Combat;
using Game.Core;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Game.Character.Tests
{
    /// <summary>
    /// 在隔离 Scene 中装配真实在线 NavMesh 与正式敌人 Prefab，比较不同 Rendering 分组下的武器姿态。
    /// 不制作替代动画/静态命中盒；出手状态在生命周期 Start 之后进入，避免 Start 覆盖测试动作。
    /// </summary>
    public sealed class N4ServerAnimationPlayModeTests
    {
        private const float Delta = 1f / 30f;
        private const string SwordPrefab = "Assets/_Project/Art/Prefabs/BaseEnemy/SwordEnemy_Base.prefab";
        private const string MagicPrefab = "Assets/_Project/Art/Prefabs/BaseEnemy/MagicEnemy_Base.prefab";
        private Scene _scene, _previousScene;
        private GameObject _root;
        private CombatSimulationWorld _world;
        private SimulationMode _previousPhysics;
        private readonly EmptyRoster _roster = new EmptyRoster();
        private Vector3 _origin;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _previousPhysics = Physics.simulationMode; Physics.simulationMode = SimulationMode.Script;
            _previousScene = SceneManager.GetActiveScene();
            _scene = SceneManager.CreateScene("N4ServerAnimationIsolation"); SceneManager.SetActiveScene(_scene);
            _root = new GameObject("N4ServerAnimationFixture"); _root.SetActive(false);
            // 与已加载正式场景错开，NavMesh 与 Physics 查询仍使用真正的生产世界输入。
            _origin = new Vector3(3000, 0, 0); _root.transform.position = _origin;
            var worldPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/Online/MovementWorld.prefab");
            Assert.That(worldPrefab, Is.Not.Null); Object.Instantiate(worldPrefab, _root.transform);
            _world = new CombatSimulationWorld(); _root.SetActive(true);
            Physics.SyncTransforms(); yield return null;
            Assert.That(NavMesh.SamplePosition(_origin + new Vector3(-5, .2f, -5), out _, 3, NavMesh.AllAreas), Is.True,
                "必须装配真实 MovementWorld NavMesh，不能用缺导航的空场景遮蔽 Animator 验收");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_root != null) Object.Destroy(_root);
            yield return null;
            if (_previousScene.IsValid() && _previousScene.isLoaded) SceneManager.SetActiveScene(_previousScene);
            if (_scene.IsValid() && _scene.isLoaded) yield return SceneManager.UnloadSceneAsync(_scene);
            Physics.simulationMode = _previousPhysics;
        }

        [UnityTest]
        public IEnumerator SwordWeaponPoseMatchesAcrossThreeFramesAndOneCatchUpFrame()
        { yield return ComparePose(SwordPrefab, false); }

        [UnityTest]
        public IEnumerator MagicWeaponPoseMatchesAcrossThreeFramesAndOneCatchUpFrame()
        { yield return ComparePose(MagicPrefab, true); }

        private IEnumerator ComparePose(string path, bool ranged)
        {
            var a = CreateEnemy(path, new Vector3(-5, .2f, -5), 5);
            var b = CreateEnemy(path, new Vector3(5, .2f, -5), 6);
            // Start 完成后才配置战斗并进入 Attack，Prefab 真实 Animator/骨架全程保留。
            yield return null;
            EnterAttack(a, ranged); EnterAttack(b, ranged);
            Transform pivotA = Pivot(a, ranged), pivotB = Pivot(b, ranged);
            Pose initial = Capture(a.transform, pivotA);
            var frames = new Pose[3]; var catchUp = new Pose[3];
            for (uint tick = 1; tick <= 3; tick++)
            {
                a.StepAuthority(Delta, tick); frames[tick - 1] = Capture(a.transform, pivotA);
                yield return null;
            }
            // b 已跨过相同 Rendering 帧但没有 Authority Tick，自动动画必须仍然停住。
            AssertPose(initial, Capture(b.transform, pivotB), "未收到 Tick 的武器不能被 Rendering 帧自行推进");
            for (uint tick = 1; tick <= 3; tick++)
            { b.StepAuthority(Delta, tick); catchUp[tick - 1] = Capture(b.transform, pivotB); }
            for (int i = 0; i < frames.Length; i++) AssertPose(frames[i], catchUp[i], "相同权威 Tick 的姿态必须与 Rendering 分组无关");
            float movement = (frames[2].Position - initial.Position).magnitude;
            float rotation = Quaternion.Angle(frames[2].Rotation, initial.Rotation);
            Assert.That(movement > .0001f || rotation > .01f, Is.True,
                "真实武器骨骼必须变化；disabled Animator.Update 若未采样成功，不能用静止盒通过验收");
            Assert.That(a.Animator.enabled, Is.False); Assert.That(b.Animator.enabled, Is.False);
        }

        [UnityTest]
        public IEnumerator ActualSwordSwingDamagesOneHealthTargetAtMostOnce()
        {
            var enemy = (MeleeEnemyController)CreateEnemy(SwordPrefab, new Vector3(-5, .2f, -5), 5);
            yield return null; EnterAttack(enemy, false);
            var detector = enemy.GetComponentInChildren<MeleeHitDetector>(true); Assert.That(detector, Is.Not.Null);
            Transform pivot = Pivot(enemy, false);
            var target = new GameObject("N4ActualSwordTarget"); target.transform.SetParent(_root.transform, false);
            target.SetActive(false); target.transform.position = pivot.position;
            var health = target.AddComponent<HealthComponent>();
            typeof(HealthComponent).GetField("_teamId", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(health, (byte)1);
            var collider = target.AddComponent<BoxCollider>(); collider.size = Vector3.one * 4;
            target.SetActive(true); health.ConfigureExecutionMode(CombatExecutionMode.Authority, _world);
            // 目标 Collider 仍供真实 OverlapBox 查询，只忽略它对 CharacterController 的推挤。
            Physics.IgnoreCollision(collider, enemy.GetComponent<CharacterController>(), true);
            Physics.SyncTransforms(); float before = health.CurrentHp; int hits = 0;
            System.Action<DamageReceivedEvent> receive = value => { if (value.TargetId == health.Id) hits++; };
            EventBus<DamageReceivedEvent>.Subscribe(receive);
            try
            {
                int steps = Mathf.CeilToInt(enemy.AttackClipDurationSeconds / Delta) + 3;
                for (uint tick = 1; tick <= steps; tick++)
                {
                    enemy.StepAuthority(Delta, tick); _world.StepBeforePhysics(Delta, tick);
                    Physics.SyncTransforms(); Physics.Simulate(Delta); _world.StepAfterPhysics(Delta, tick);
                    yield return null;
                }
                Assert.That(hits, Is.EqualTo(1), "命中必须发生，且同一真实 swing 不得因多 Tick/Collider 重复扣血");
                Assert.That(health.CurrentHp, Is.LessThan(before));
            }
            finally { EventBus<DamageReceivedEvent>.Unsubscribe(receive); }
        }

        private EnemyControllerBase CreateEnemy(string path, Vector3 localPosition, ulong actor)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path); Assert.That(prefab, Is.Not.Null);
            var container = new GameObject("Authority enemy"); container.SetActive(false); container.transform.SetParent(_root.transform, false);
            Vector3 desired = _origin + localPosition;
            Assert.That(NavMesh.SamplePosition(desired, out var hit, 3, NavMesh.AllAreas), Is.True);
            var instance = Object.Instantiate(prefab, hit.position + Vector3.up * .05f, Quaternion.identity, container.transform);
            var enemy = instance.GetComponent<EnemyControllerBase>(); Assert.That(enemy, Is.Not.Null);
            enemy.ConfigureOnlineAuthority(_roster);
            var animator = instance.GetComponentInChildren<Animator>(true); Assert.That(animator, Is.Not.Null);
            AnimationClip clip = AttackClip(animator, enemy.Definition.Attack.AnimationStateName, out float stateSpeed);
            Assert.That(clip, Is.Not.Null, "正式 Attack State 必须使用实际 Motion Clip"); Assert.That(stateSpeed, Is.GreaterThan(0));
            // 与生产 Registry 一致：inactive 时装配 Combat，首次 Awake 就禁掉自动动画。
            // 测试不得额外 Rebind 或先让它自动播一帧，否则会掩盖 Headless 首次初始化问题。
            enemy.ConfigureOnlineCombat(_world, actor, clip.length / stateSpeed);
            container.SetActive(true); return enemy;
        }
        private static void EnterAttack(EnemyControllerBase enemy, bool ranged)
        {
            Animator animator = enemy.Animator; Assert.That(animator, Is.Not.Null);
            Assert.That(enemy.IsOnlineCombatAuthority, Is.True); Assert.That(animator.enabled, Is.False);
            if (ranged) enemy.StateMachine.ChangeState(((RangedEnemyController)enemy).RangedAttackState);
            else enemy.StateMachine.ChangeState(((MeleeEnemyController)enemy).AttackState);
        }
        private static AnimationClip AttackClip(Animator animator, string stateName, out float speed)
        {
            var overrides = animator.runtimeAnimatorController as AnimatorOverrideController;
            var controller = (overrides != null ? overrides.runtimeAnimatorController : animator.runtimeAnimatorController) as AnimatorController;
            Assert.That(controller, Is.Not.Null);
            for (int i = 0; i < controller.layers.Length; i++)
            {
                var state = FindState(controller.layers[i].stateMachine, stateName);
                if (state == null) continue;
                speed = state.speed; var clip = state.motion as AnimationClip;
                if (overrides != null && clip != null && overrides[clip] != null) clip = overrides[clip];
                return clip;
            }
            speed = 0; return null;
        }
        private static AnimatorState FindState(AnimatorStateMachine machine, string stateName)
        {
            foreach (var child in machine.states) if (child.state.name == stateName) return child.state;
            foreach (var child in machine.stateMachines)
            { var found = FindState(child.stateMachine, stateName); if (found != null) return found; }
            return null;
        }
        private static Transform Pivot(EnemyControllerBase enemy, bool ranged)
        {
            if (ranged)
                return (Transform)typeof(RangedEnemyController).GetField("_castOrigin", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(enemy);
            var detector = enemy.GetComponentInChildren<MeleeHitDetector>(true); Assert.That(detector, Is.Not.Null);
            var pivot = (Transform)typeof(MeleeHitDetector).GetField("_weaponPivot", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(detector);
            return pivot != null ? pivot : detector.transform;
        }
        private static Pose Capture(Transform root, Transform pivot)
        { Assert.That(pivot, Is.Not.Null); return new Pose(root.InverseTransformPoint(pivot.position), Quaternion.Inverse(root.rotation) * pivot.rotation); }
        private static void AssertPose(Pose expected, Pose actual, string message)
        {
            Assert.That(Vector3.Distance(expected.Position, actual.Position), Is.LessThan(.0001f), message);
            Assert.That(Quaternion.Angle(expected.Rotation, actual.Rotation), Is.LessThan(.01f), message);
        }
        private readonly struct Pose
        {
            internal readonly Vector3 Position;
            internal readonly Quaternion Rotation;
            internal Pose(Vector3 position, Quaternion rotation) { Position = position; Rotation = rotation; }
        }
        private sealed class EmptyRoster : ICombatActorRosterReadOnly
        {
            public int Count => 0;
            public bool TryGetActor(int index, out CombatActorSnapshot actor) { actor = default; return false; }
        }
    }
}
#endif
