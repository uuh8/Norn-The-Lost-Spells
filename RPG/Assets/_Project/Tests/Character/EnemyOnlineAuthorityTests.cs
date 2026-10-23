using System.Collections.Generic;
using System.Reflection;
using Game.Combat;
using Game.Core;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Character.Tests
{
    /// <summary>
    /// 验证实际 EnemyPerception 与显式 Authority 入口；Roster 只是内存中的只读数据源，
    /// 目标选择与推进均执行正式代码，避免测试复制一份生产算法。
    /// </summary>
    public sealed class EnemyOnlineAuthorityTests
    {
        private readonly List<Object> _created = new List<Object>();
        private PlayerControllerBase _previousCurrent;

        [SetUp]
        public void SetUp() => _previousCurrent = PlayerControllerBase.Current;

        [TearDown]
        public void TearDown()
        {
            SetCurrent(_previousCurrent);
            for (int i = _created.Count - 1; i >= 0; i--)
                if (_created[i] != null) Object.DestroyImmediate(_created[i]);
            _created.Clear();
        }

        [Test]
        public void Perception_SelectsNearestHorizontalActor_AndUsesSnapshotPosition()
        {
            EnemyPerception perception = CreatePerception();
            CombatActorSnapshot far = CreateActor(20, new Vector3(6f, 0f, 0f));
            CombatActorSnapshot near = CreateActor(30, new Vector3(3f, 2f, 0f));
            near.Transform.position = new Vector3(99f, 0f, 0f);
            perception.BindRoster(new ArrayRoster(far, near));

            perception.Tick();

            Assert.That(perception.TargetActorId, Is.EqualTo(30ul));
            Assert.That(perception.Target, Is.SameAs(near.Transform));
            Assert.That(perception.TargetPosition, Is.EqualTo(new Vector3(3f, 2f, 0f)));
            Assert.That(perception.DistanceToTarget, Is.EqualTo(3f));
        }

        [Test]
        public void Perception_EqualDistanceUsesSmallerActorId_IndependentlyOfRosterOrder()
        {
            EnemyPerception perception = CreatePerception();
            CombatActorSnapshot highId = CreateActor(80, new Vector3(3f, 0f, 4f));
            CombatActorSnapshot lowId = CreateActor(10, new Vector3(-3f, 0f, -4f));
            perception.BindRoster(new ArrayRoster(highId, lowId));

            perception.Tick();

            Assert.That(perception.TargetActorId, Is.EqualTo(10ul));
        }

        [Test]
        public void Perception_KeepsValidLockWithinLoseCylinder_DespiteCloserNewActor()
        {
            EnemyPerception perception = CreatePerception();
            ArrayRoster roster = new ArrayRoster(CreateActor(40, new Vector3(4f, 0f, 0f)));
            perception.BindRoster(roster);
            perception.Tick();
            roster.Actors[0].Position = new Vector3(14f, 7f, 0f);
            roster.Actors = new[] { roster.Actors[0], CreateActor(2, Vector3.right) };

            perception.Tick();

            Assert.That(perception.TargetActorId, Is.EqualTo(40ul));
            Assert.That(perception.DistanceToTarget, Is.EqualTo(14f));
            Assert.That(perception.VerticalDistanceToTarget, Is.EqualTo(7f));
        }

        [TestCase("dead")]
        [TestCase("disconnected")]
        [TestCase("untargetable")]
        [TestCase("same-team")]
        [TestCase("height")]
        [TestCase("radius")]
        public void Perception_InvalidLockedActorIsReplacedImmediately(string invalidReason)
        {
            EnemyPerception perception = CreatePerception();
            ArrayRoster roster = new ArrayRoster(CreateActor(40, Vector3.right));
            perception.BindRoster(roster);
            perception.Tick();
            CombatActorSnapshot invalid = roster.Actors[0];
            switch (invalidReason)
            {
                case "dead": invalid.IsAlive = false; break;
                case "disconnected": invalid.IsConnected = false; break;
                case "untargetable": invalid.Targetable = false; break;
                case "same-team": invalid.TeamId = 0; break;
                case "height": invalid.Position = new Vector3(1f, 9f, 0f); break;
                case "radius": invalid.Position = new Vector3(17f, 0f, 0f); break;
            }
            roster.Actors = new[] { invalid, CreateActor(9, new Vector3(5f, 0f, 0f)) };

            perception.Tick();

            Assert.That(perception.TargetActorId, Is.EqualTo(9ul));
        }

        [Test]
        public void Perception_RejectsNewActorOutsideDetectCylinder_AndClearsLostLock()
        {
            EnemyPerception perception = CreatePerception();
            ArrayRoster roster = new ArrayRoster(CreateActor(40, Vector3.right));
            perception.BindRoster(roster);
            perception.Tick();
            roster.Actors = new[]
            {
                CreateActor(2, new Vector3(13f, 0f, 0f)),
                CreateActor(3, new Vector3(1f, 7f, 0f))
            };

            perception.Tick();

            Assert.That(perception.HasTarget, Is.False);
            Assert.That(perception.TargetActorId, Is.Zero);
            Assert.That(perception.Target, Is.Null);
            Assert.That(perception.DistanceToTarget, Is.EqualTo(float.PositiveInfinity));
        }

        [Test]
        public void Perception_BoundEmptyRosterDoesNotUseLastEnabledSinglePlayer()
        {
            EnemyPerception perception = CreatePerception();
            GameObject playerObject = CreateObject("Single player fallback");
            WarriorController player = playerObject.AddComponent<WarriorController>();
            SetCurrent(player);
            perception.BindRoster(new ArrayRoster());

            perception.Tick();

            Assert.That(perception.HasTarget, Is.False);
        }

        [Test]
        public void Perception_UnboundRosterPreservesSinglePlayerFallback()
        {
            EnemyPerception perception = CreatePerception();
            GameObject playerObject = CreateObject("Single player fallback");
            WarriorController player = playerObject.AddComponent<WarriorController>();
            player.transform.position = Vector3.right;
            SetCurrent(player);

            perception.Tick();

            Assert.That(perception.HasTarget, Is.True);
            Assert.That(perception.Target, Is.SameAs(player.transform));
        }

        [Test]
        public void Authority_ExplicitStepUsesProvidedDelta_AndRejectsDuplicateOrOlderTick()
        {
            MeleeEnemyController enemy = CreateEnemy(true);
            enemy.ConfigureOnlineAuthority(new ArrayRoster());
            enemy.AttackCooldownCounter = 1f;

            enemy.StepAuthority(0.1f, 100);
            Vector3 afterFirstStep = enemy.transform.position;
            enemy.StepAuthority(0.1f, 100);
            enemy.StepAuthority(0.1f, 99);

            Assert.That(enemy.AttackCooldownCounter, Is.EqualTo(0.9f).Within(0.0001f));
            Assert.That(enemy.SimulationDeltaTime, Is.EqualTo(0.1f));
            Assert.That(enemy.transform.position, Is.EqualTo(afterFirstStep));
            enemy.StepAuthority(0.2f, 101);
            Assert.That(enemy.AttackCooldownCounter, Is.EqualTo(0.7f).Within(0.0001f));
        }

        [Test]
        public void Authority_OrdinaryUpdateDoesNotRunPerceptionBetweenExplicitTicks()
        {
            MeleeEnemyController enemy = CreateEnemy(true);
            enemy.ConfigureOnlineAuthority(new ArrayRoster(CreateActor(3, Vector3.right)));

            InvokeBase(enemy, "Update");

            Assert.That(enemy.Perception.HasTarget, Is.False);
        }

        [Test]
        public void Replica_NeitherOrdinaryUpdateNorAuthorityStepRunsAiOrMovement()
        {
            MeleeEnemyController enemy = CreateEnemy(true);
            enemy.ConfigureOnlineAuthority(new ArrayRoster(CreateActor(3, Vector3.right)));
            enemy.ConfigureOnlineReplica();
            enemy.Perception.BindRoster(new ArrayRoster(CreateActor(5, Vector3.right)));
            enemy.AttackCooldownCounter = 1f;
            Vector3 initialPosition = enemy.transform.position;

            InvokeBase(enemy, "Update");
            enemy.StepAuthority(0.2f, 1);

            Assert.That(enemy.Perception.HasTarget, Is.False);
            Assert.That(enemy.AttackCooldownCounter, Is.EqualTo(1f));
            Assert.That(enemy.transform.position, Is.EqualTo(initialPosition));
        }

        private EnemyPerception CreatePerception() => new EnemyPerception(CreateEnemy(false));

        private MeleeEnemyController CreateEnemy(bool initialize)
        {
            GameObject enemyObject = CreateObject("Online enemy test");
            MeleeEnemyController enemy = enemyObject.AddComponent<MeleeEnemyController>();
            EnemyDefinition definition = ScriptableObject.CreateInstance<EnemyDefinition>();
            _created.Add(definition);
            AttackDefinition attack = ScriptableObject.CreateInstance<AttackDefinition>();
            _created.Add(attack);
            attack.AnimationStateName = "EnemyAttack";
            definition.Attack = attack;
            typeof(EnemyControllerBase).GetField("_definition", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(enemy, definition);
            if (initialize)
            {
                // 此组测试只验证推进权和时间，不制作 NavMesh；禁用 Agent 避免把无地图告警混入断言。
                enemyObject.GetComponent<UnityEngine.AI.NavMeshAgent>().enabled = false;
                enemyObject.SetActive(true);
                if (enemy.Perception == null)
                    typeof(MeleeEnemyController).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(enemy, null);
            }
            return enemy;
        }

        private CombatActorSnapshot CreateActor(ulong actorId, Vector3 position)
        {
            GameObject actor = CreateObject("Combat actor");
            actor.transform.position = position;
            return new CombatActorSnapshot
            {
                ActorId = actorId, Position = position, TeamId = 1,
                IsAlive = true, IsConnected = true, Targetable = true, Transform = actor.transform
            };
        }

        private GameObject CreateObject(string name)
        {
            GameObject instance = new GameObject(name);
            instance.SetActive(false);
            _created.Add(instance);
            return instance;
        }

        private static void SetCurrent(PlayerControllerBase player) =>
            typeof(PlayerControllerBase).GetProperty("Current", BindingFlags.Public | BindingFlags.Static)
                .SetValue(null, player);

        private static void InvokeBase(EnemyControllerBase enemy, string name) =>
            typeof(EnemyControllerBase).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(enemy, null);

        private sealed class ArrayRoster : ICombatActorRosterReadOnly
        {
            public CombatActorSnapshot[] Actors;
            public int Count => Actors.Length;
            public ArrayRoster(params CombatActorSnapshot[] actors) => Actors = actors;
            public bool TryGetActor(int index, out CombatActorSnapshot actor)
            {
                actor = Actors[index];
                return true;
            }
        }
    }
}
