using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Game.Combat;
using Game.Core;
using Game.Run;
using Game.Skills;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Game.Character.Tests
{
    public sealed class WizardBossTeleportTests
    {
        private static readonly Vector3 TestOrigin=new Vector3(10000f,10000f,10000f);
        private readonly List<Object> _createdObjects = new List<Object>();
        private int _telegraphCount;
        private int _departedCount;
        private int _arrivedCount;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            Time.timeScale = 1f;
            // NUnit默认复用Fixture实例；事件计数属于单个用例，不能继承上一例的已传送次数。
            _telegraphCount = _departedCount = _arrivedCount = 0;
            EventBus<BossTeleportEvent>.Subscribe(OnBossTeleport);
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            EventBus<BossTeleportEvent>.Unsubscribe(OnBossTeleport);
            EventBus<BossTeleportEvent>.Clear();
            for (int i = _createdObjects.Count - 1; i >= 0; i--)
            {
                if (_createdObjects[i] != null)
                {
                    Object.Destroy(_createdObjects[i]);
                }
            }

            _createdObjects.Clear();
            yield return null;
        }

        [UnityTest]
        public IEnumerator Teleport_LocksDestinationUntilTelegraphThenMovesAndStartsCooldown()
        {
            BossDefinition definition = CreateTeleportDefinition();
            WizardBossController boss = CreateBoss(definition);
            Vector3 destination = TestOrigin + new Vector3(9f, 0f, -6f);
            ReplaceSampler(
                boss,
                new BossTeleportSampler(
                    new FixedWorldQuery(destination, false),
                    new Vector3[4]));
            Transform target = CreateTarget(TestOrigin + new Vector3(4f, 0f, 0f));
            Vector3 source = boss.transform.position;

            Assert.That(boss.TryActivate(target), Is.True);
            yield return WaitForState(
                boss,
                BossRuntimeStateKind.Teleport,
                1f);

            Assert.That(boss.IsActionLocked, Is.True);
            Assert.That(boss.transform.position, Is.EqualTo(source));
            Assert.That(_telegraphCount, Is.EqualTo(1));

            yield return new WaitForSeconds(0.06f);

            Assert.That(boss.transform.position, Is.EqualTo(destination));
            Assert.That(_departedCount, Is.EqualTo(1));
            Assert.That(_arrivedCount, Is.EqualTo(1));

            yield return WaitUntilTeleportCompletes(boss, 1f);

            Assert.That(boss.IsActionLocked, Is.False);
            Assert.That(boss.TeleportCooldownRemaining, Is.GreaterThan(0f));
        }

        [UnityTest]
        public IEnumerator Teleport_WhenNoCandidateIsSafe_StaysAndReleasesActionLock()
        {
            BossDefinition definition = CreateTeleportDefinition();
            definition.DecisionInterval = 1f;
            WizardBossController boss = CreateBoss(definition);
            ReplaceSampler(
                boss,
                new BossTeleportSampler(
                    new FixedWorldQuery(Vector3.zero, true),
                    new Vector3[4]));
            Transform target = CreateTarget(TestOrigin + new Vector3(4f, 0f, 0f));
            Vector3 source = boss.transform.position;

            Assert.That(boss.TryActivate(target), Is.True);
            yield return null;
            yield return null;

            Assert.That(boss.transform.position, Is.EqualTo(source));
            Assert.That(boss.IsActionLocked, Is.False);
            Assert.That(
                boss.CurrentStateKind,
                Is.EqualTo(BossRuntimeStateKind.Decision));
            Assert.That(_departedCount, Is.Zero);
            Assert.That(_arrivedCount, Is.Zero);
        }

        private BossDefinition CreateTeleportDefinition()
        {
            BossDefinition definition =
                ScriptableObject.CreateInstance<BossDefinition>();
            definition.StopDistance = 50f;
            // Decision会先按战斗距离横移，再选择Teleport；此用例只测传送事务，隔离该Steering。
            definition.MoveSpeed = 0f;
            // 这是传送事务的隔离用例，不让地形穿透修正/重力混入“位置必须锁定”的断言。
            // 生产Boss仍使用真实重力；此处临时SO与远离场景几何的坐标只影响测试对象。
            definition.GravityAcceleration = 0f;
            definition.GroundedStickSpeed = 0f;
            definition.DecisionInterval = 0.01f;
            definition.TeleportTelegraphDuration = 0.05f;
            definition.TeleportRecoveryDuration = 0.05f;
            definition.Phase2.EnterAtOrBelowHealthRatio = 1f;
            definition.Phase2.TeleportWeight = 100f;
            definition.Phase2.TeleportCooldown = 3f;
            definition.Phase3.EnterAtOrBelowHealthRatio = 0f;
            definition.Programs =
                System.Array.Empty<BossSpellProgramDefinition>();
            _createdObjects.Add(definition);
            return definition;
        }

        private WizardBossController CreateBoss(BossDefinition definition)
        {
            GameObject bossObject = new GameObject("Teleport Boss");
            bossObject.SetActive(false);
            bossObject.transform.position = TestOrigin;
            bossObject.AddComponent<CharacterController>();
            bossObject.AddComponent<HealthComponent>();
            bossObject.AddComponent<StatusController>();
            bossObject.AddComponent<SpellCaster>();
            WizardBossController boss =
                bossObject.AddComponent<WizardBossController>();
            SetPrivateField(boss, "_definition", definition);
            bossObject.SetActive(true);
            _createdObjects.Add(bossObject);
            return boss;
        }

        private Transform CreateTarget(Vector3 position)
        {
            GameObject target = new GameObject("Teleport Target");
            target.transform.position = position;
            target.AddComponent<CharacterController>();
            target.AddComponent<HealthComponent>();
            target.AddComponent<StatusController>();
            _createdObjects.Add(target);
            return target.transform;
        }

        private static void ReplaceSampler(
            WizardBossController boss,
            BossTeleportSampler sampler)
        {
            SetPrivateField(boss, "_teleportSampler", sampler);
        }

        private static void SetPrivateField(
            object target,
            string fieldName,
            object value)
        {
            FieldInfo field = target.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            field.SetValue(target, value);
        }

        private static IEnumerator WaitForState(
            WizardBossController boss,
            BossRuntimeStateKind expected,
            float timeout)
        {
            float deadline = Time.realtimeSinceStartup + timeout;
            while (boss.CurrentStateKind != expected &&
                   Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(boss.CurrentStateKind, Is.EqualTo(expected));
        }

        private static IEnumerator WaitUntilTeleportCompletes(
            WizardBossController boss,
            float timeout)
        {
            float deadline = Time.realtimeSinceStartup + timeout;
            while (boss.CurrentStateKind == BossRuntimeStateKind.Teleport &&
                   Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(
                boss.CurrentStateKind,
                Is.Not.EqualTo(BossRuntimeStateKind.Teleport));
        }

        private void OnBossTeleport(BossTeleportEvent teleportEvent)
        {
            switch (teleportEvent.Stage)
            {
                case BossTeleportStage.Telegraph:
                    _telegraphCount++;
                    break;
                case BossTeleportStage.Departed:
                    _departedCount++;
                    break;
                case BossTeleportStage.Arrived:
                    _arrivedCount++;
                    break;
            }
        }

        private sealed class FixedWorldQuery : IBossTeleportWorldQuery
        {
            private readonly Vector3 _destination;
            private readonly bool _blocked;

            public FixedWorldQuery(Vector3 destination, bool blocked)
            {
                _destination = destination;
                _blocked = blocked;
            }

            public bool TrySampleNavMesh(
                Vector3 candidate,
                float maxDistance,
                int areaMask,
                out Vector3 sampledPosition)
            {
                sampledPosition = _destination + Vector3.up;
                return true;
            }

            public bool TryProjectToGround(
                Vector3 sampledPosition,
                int groundMask,
                out Vector3 groundPoint,
                out Vector3 groundNormal)
            {
                groundPoint = _destination;
                groundNormal = Vector3.up;
                return true;
            }

            public bool IsCapsuleBlocked(
                Vector3 bottom,
                Vector3 top,
                float radius,
                int blockingMask,
                Transform ignoredRoot)
            {
                return _blocked;
            }
        }
    }
}
