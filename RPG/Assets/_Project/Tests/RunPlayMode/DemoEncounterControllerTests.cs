using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Game.Combat;
using Game.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Run.Tests
{
    /// <summary>
    /// DemoEncounterController 是依赖 MonoBehaviour 生命周期的 Scene Adapter，
    /// 因此这些测试必须在真正的 PlayMode 测试程序集内运行。
    /// 测试重点不是 Enemy AI 或美术表现，而是纯 Tracker 结果是否只产生一次正确的 Unity Side Effect。
    /// </summary>
    public sealed class DemoEncounterControllerTests
    {
        private readonly List<GameObject> _createdObjects = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            // EventBus 使用 static delegate 保存订阅者。每项测试先清空，确保结果不受上一项测试影响。
            EventBus<DeathEvent>.Clear();
            EventBus<EncounterStartedEvent>.Clear();
            EventBus<EncounterProgressChangedEvent>.Clear();
            EventBus<EncounterCompletedEvent>.Clear();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            for (int i = _createdObjects.Count - 1; i >= 0; i--)
            {
                GameObject createdObject = _createdObjects[i];
                if (createdObject == null)
                {
                    continue;
                }

                // SetActive(false) 会同步触发 OnDisable，让 Controller 在销毁前解除 EventBus 订阅。
                // Destroy 在 PlayMode 中延迟到帧末执行，所以随后还要等待一帧完成清理。
                createdObject.SetActive(false);
                Object.Destroy(createdObject);
            }

            _createdObjects.Clear();
            yield return null;

            EventBus<DeathEvent>.Clear();
            EventBus<EncounterStartedEvent>.Clear();
            EventBus<EncounterProgressChangedEvent>.Clear();
            EventBus<EncounterCompletedEvent>.Clear();
        }

        [UnityTest]
        public IEnumerator Initialize_PreparesWaitingEncounterAndKeepsEnemiesInactive()
        {
            HealthComponent enemyA = CreateEnemy("Enemy A");
            HealthComponent enemyB = CreateEnemy("Enemy B");
            ArenaGate gate = CreateGate();
            DemoEncounterController controller = CreateController(enemyA, enemyB, gate);

            controller.Initialize(0, 2, new DemoEncounterDefinition());

            Assert.That(controller.State, Is.EqualTo(EncounterState.Waiting));
            Assert.That(controller.RemainingEnemyCount, Is.EqualTo(2));
            Assert.That(enemyA.gameObject.activeSelf, Is.False);
            Assert.That(enemyB.gameObject.activeSelf, Is.False);
            Assert.That(gate.IsClosed, Is.False,
                "尚未进入战斗时 Gate 应保持开放，避免未 Armed 的区域成为物理软锁。 ");

            yield return null;
        }

        [UnityTest]
        public IEnumerator Begin_RequiresArmedAndProducesStartSideEffectsExactlyOnce()
        {
            HealthComponent enemy = CreateEnemy("Enemy");
            ArenaGate gate = CreateGate();
            DemoEncounterController controller = CreateController(enemy, gate);
            int startedCount = 0;
            EventBus<EncounterStartedEvent>.Subscribe(_ => startedCount++);
            controller.Initialize(0, 1, new DemoEncounterDefinition());

            Assert.That(controller.TryBeginEncounter(), Is.False);
            Assert.That(controller.Arm(), Is.True);
            Assert.That(controller.TryBeginEncounter(), Is.True);
            Assert.That(controller.TryBeginEncounter(), Is.False);

            Assert.That(controller.State, Is.EqualTo(EncounterState.Active));
            Assert.That(enemy.gameObject.activeSelf, Is.True);
            Assert.That(gate.IsClosed, Is.True);
            Assert.That(startedCount, Is.EqualTo(1));

            yield return null;
        }

        [UnityTest]
        public IEnumerator DeathEvents_IgnoreExternalAndDuplicateIdsThenClearExactlyOnce()
        {
            HealthComponent enemyA = CreateEnemy("Enemy A");
            HealthComponent enemyB = CreateEnemy("Enemy B");
            ArenaGate gate = CreateGate();
            DemoEncounterController controller = CreateController(enemyA, enemyB, gate);
            int completedCount = 0;
            int progressEventCount = 0;
            int lastRemainingCount = -1;
            EventBus<EncounterCompletedEvent>.Subscribe(_ => completedCount++);
            EventBus<EncounterProgressChangedEvent>.Subscribe(progressEvent =>
            {
                progressEventCount++;
                lastRemainingCount = progressEvent.RemainingEnemyCount;
            });
            controller.Initialize(0, 1, new DemoEncounterDefinition());
            controller.Arm();
            controller.TryBeginEncounter();

            EventBus<DeathEvent>.Publish(new DeathEvent { TargetId = 987654 });
            EventBus<DeathEvent>.Publish(new DeathEvent { TargetId = GetRuntimeTargetId(enemyA) });
            EventBus<DeathEvent>.Publish(new DeathEvent { TargetId = GetRuntimeTargetId(enemyA) });

            Assert.That(controller.RemainingEnemyCount, Is.EqualTo(1));
            Assert.That(progressEventCount, Is.EqualTo(1),
                "外部或重复 Death ID 不能让 HUD 的权威快照重复递减。");
            Assert.That(lastRemainingCount, Is.EqualTo(1));
            Assert.That(gate.IsClosed, Is.True);
            Assert.That(completedCount, Is.Zero);

            EventBus<DeathEvent>.Publish(new DeathEvent { TargetId = GetRuntimeTargetId(enemyB) });
            EventBus<DeathEvent>.Publish(new DeathEvent { TargetId = GetRuntimeTargetId(enemyB) });

            Assert.That(controller.State, Is.EqualTo(EncounterState.Cleared));
            Assert.That(controller.RemainingEnemyCount, Is.Zero);
            Assert.That(progressEventCount, Is.EqualTo(2));
            Assert.That(lastRemainingCount, Is.Zero);
            Assert.That(gate.IsClosed, Is.False);
            Assert.That(completedCount, Is.EqualTo(1));

            yield return null;
        }

        [UnityTest]
        public IEnumerator DisabledController_UnsubscribesFromDeathEvents()
        {
            HealthComponent enemy = CreateEnemy("Enemy");
            ArenaGate gate = CreateGate();
            DemoEncounterController controller = CreateController(enemy, gate);
            controller.Initialize(0, 1, new DemoEncounterDefinition());
            controller.Arm();
            controller.TryBeginEncounter();

            controller.enabled = false;
            EventBus<DeathEvent>.Publish(new DeathEvent { TargetId = GetRuntimeTargetId(enemy) });

            Assert.That(controller.State, Is.EqualTo(EncounterState.Active));
            Assert.That(controller.RemainingEnemyCount, Is.EqualTo(1));
            Assert.That(gate.IsClosed, Is.True);

            yield return null;
        }

        [UnityTest]
        public IEnumerator EntryTrigger_IgnoresOtherLayersAndBeginsArmedEncounterOnlyOnce()
        {
            HealthComponent enemy = CreateEnemy("Enemy");
            ArenaGate gate = CreateGate();
            DemoEncounterController controller = CreateController(enemy, gate);
            controller.Initialize(0, 1, new DemoEncounterDefinition());
            Assert.That(controller.Arm(), Is.True);

            // 这里故意通过 Assembly Qualified Name 查找尚未实现的组件。
            // RED 阶段会得到一条清晰的 NUnit 断言，而不是 CS0246 导致整个 Unity 工程无法编译。
            System.Type triggerType = System.Type.GetType("Game.Run.EncounterEntryTrigger, Game.Run");
            Assert.That(triggerType, Is.Not.Null,
                "Game.Run 中应提供 EncounterEntryTrigger，把 Player 进入区域的物理回调转成 Encounter 开始请求。");

            GameObject triggerObject = CreateObject("Encounter Entry Trigger");
            BoxCollider triggerCollider = triggerObject.AddComponent<BoxCollider>();
            triggerCollider.isTrigger = true;
            Component trigger = triggerObject.AddComponent(triggerType);

            const int playerLayer = 8;
            const int otherLayer = 9;
            SetRuntimePrivateField(trigger, "_encounter", controller);
            SetRuntimePrivateField(trigger, "_playerLayers", (LayerMask)(1 << playerLayer));

            GameObject enteringObject = CreateObject("Entering Collider");
            BoxCollider enteringCollider = enteringObject.AddComponent<BoxCollider>();
            int startedCount = 0;
            EventBus<EncounterStartedEvent>.Subscribe(_ => startedCount++);

            enteringObject.layer = otherLayer;
            InvokeTriggerEnter(trigger, enteringCollider);
            Assert.That(controller.State, Is.EqualTo(EncounterState.Armed),
                "Enemy、Projectile 等非 Player Layer 对象经过入口时不能开始战斗。");
            Assert.That(startedCount, Is.Zero);

            enteringObject.layer = playerLayer;
            InvokeTriggerEnter(trigger, enteringCollider);
            InvokeTriggerEnter(trigger, enteringCollider);

            Assert.That(controller.State, Is.EqualTo(EncounterState.Active));
            Assert.That(startedCount, Is.EqualTo(1),
                "同一玩家的多个 Collider 或重复进入回调不能重复发布 EncounterStartedEvent。");

            yield return null;
        }

        private DemoEncounterController CreateController(params Object[] references)
        {
            GameObject controllerObject = CreateObject("Encounter Controller");

            // 先保持 inactive，完成 serialized field 注入后再激活。
            // 这模拟 Scene 反序列化完成后才进入 OnEnable 的真实加载顺序，避免先订阅、后配置的时序歧义。
            controllerObject.SetActive(false);
            DemoEncounterController controller = controllerObject.AddComponent<DemoEncounterController>();

            var enemies = new List<HealthComponent>();
            var gates = new List<ArenaGate>();
            for (int i = 0; i < references.Length; i++)
            {
                if (references[i] is HealthComponent health)
                {
                    enemies.Add(health);
                }
                else if (references[i] is ArenaGate gate)
                {
                    gates.Add(gate);
                }
            }

            // PlayMode 测试程序集不依赖 UnityEditor。Reflection 只在测试构造夹具时使用，
            // 不进入游戏 Runtime 热路径，也不会改变 Production API 的封装边界。
            SetPrivateField(controller, "_enemyMembers", enemies.ToArray());
            SetPrivateField(controller, "_gates", gates.ToArray());
            controllerObject.SetActive(true);
            return controller;
        }

        private HealthComponent CreateEnemy(string name)
        {
            GameObject enemyObject = CreateObject(name);
            return enemyObject.AddComponent<HealthComponent>();
        }

        private ArenaGate CreateGate()
        {
            GameObject gateObject = CreateObject("Gate");
            BoxCollider blockingCollider = gateObject.AddComponent<BoxCollider>();
            ArenaGate gate = gateObject.AddComponent<ArenaGate>();
            SetPrivateField(gate, "_blockingColliders", new Collider[] { blockingCollider });
            return gate;
        }

        private static int GetRuntimeTargetId(HealthComponent health)
        {
            // HealthComponent.Awake 会把 Id 设为同一 GameObject 的 Instance ID；
            // 这里直接使用该来源值，构造与正式 DeathEvent 完全一致的 TargetId。
            return health.gameObject.GetInstanceID();
        }

        private GameObject CreateObject(string name)
        {
            var gameObject = new GameObject(name);
            _createdObjects.Add(gameObject);
            return gameObject;
        }

        private static void SetPrivateField<TTarget, TValue>(
            TTarget target,
            string fieldName,
            TValue value)
        {
            FieldInfo field = typeof(TTarget).GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.That(field, Is.Not.Null,
                $"测试约定的 private serialized field '{fieldName}' 不存在，Production API 可能已经发生迁移。 ");
            field.SetValue(target, value);
        }

        private static void SetRuntimePrivateField(
            Component target,
            string fieldName,
            object value)
        {
            FieldInfo field = target.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.That(field, Is.Not.Null,
                $"测试约定的 private serialized field '{fieldName}' 不存在，Entry Trigger 的 Authoring Contract 可能已经迁移。");
            field.SetValue(target, value);
        }

        private static void InvokeTriggerEnter(Component trigger, Collider enteringCollider)
        {
            MethodInfo callback = trigger.GetType().GetMethod(
                "OnTriggerEnter",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.That(callback, Is.Not.Null,
                "EncounterEntryTrigger 必须实现 Unity Physics 的 OnTriggerEnter(Collider) 消息。");
            callback.Invoke(trigger, new object[] { enteringCollider });
        }
    }
}
