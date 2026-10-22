using System;
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
    /// 整局 Scene Adapter 的 PlayMode 契约。
    /// 第一条 RED 只锁定初始化职责：配置所有 Encounter，但只允许第一关 Armed。
    /// </summary>
    public sealed class DemoRunControllerTests
    {
        private readonly List<UnityEngine.Object> _createdObjects =
            new List<UnityEngine.Object>();

        [SetUp]
        public void SetUp()
        {
            EventBus<DeathEvent>.Clear();
            EventBus<EncounterStartedEvent>.Clear();
            EventBus<EncounterProgressChangedEvent>.Clear();
            EventBus<EncounterCompletedEvent>.Clear();
            EventBus<RunStateChangedEvent>.Clear();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            for (int i = _createdObjects.Count - 1; i >= 0; i--)
            {
                UnityEngine.Object createdObject = _createdObjects[i];
                if (createdObject == null)
                {
                    continue;
                }

                if (createdObject is GameObject gameObject)
                {
                    // 先触发 OnDisable 解除 EventBus，再等待 Destroy 的帧末销毁。
                    gameObject.SetActive(false);
                }

                UnityEngine.Object.Destroy(createdObject);
            }

            _createdObjects.Clear();
            yield return null;

            EventBus<DeathEvent>.Clear();
            EventBus<EncounterStartedEvent>.Clear();
            EventBus<EncounterProgressChangedEvent>.Clear();
            EventBus<EncounterCompletedEvent>.Clear();
            EventBus<RunStateChangedEvent>.Clear();
        }

        [UnityTest]
        public IEnumerator Start_AutomaticallyInitializesConfiguredSceneRunOnce()
        {
            DemoEncounterController encounterA =
                CreateEncounter("Encounter A", out _);
            DemoEncounterController encounterB =
                CreateEncounter("Encounter B", out _);
            DemoRunDefinition definition = CreateDefinition(2);

            GameObject runObject = CreateGameObject("Demo Run Controller");
            runObject.SetActive(false);
            DemoRunController controller = runObject.AddComponent<DemoRunController>();
            SetPrivateField(controller, "_definition", definition);
            SetPrivateField(controller, "_encounters", new[] { encounterA, encounterB });
            SetPrivateField(controller, "_playerHealth", CreateHealth("Player"));

            int runningEventCount = 0;
            EventBus<RunStateChangedEvent>.Subscribe(runEvent =>
            {
                if (runEvent.PreviousState == RunState.NotStarted &&
                    runEvent.CurrentState == RunState.Running)
                {
                    runningEventCount++;
                }
            });

            // 激活后等待一帧，模拟 Scene 中已序列化对象进入 Unity Start 生命周期。
            // 本测试不主动调用 Initialize：如果只有测试夹具能启动整局，真实 Scene 的 Trigger 就永远没有 Armed 目标。
            runObject.SetActive(true);
            yield return null;

            Assert.That(controller.State, Is.EqualTo(RunState.Running));
            Assert.That(controller.CurrentEncounterIndex, Is.Zero);
            Assert.That(encounterA.State, Is.EqualTo(EncounterState.Armed));
            Assert.That(encounterB.State, Is.EqualTo(EncounterState.Waiting));
            Assert.That(runningEventCount, Is.EqualTo(1),
                "Scene 生命周期只能自动启动整局一次，避免重复初始化 Encounter 与重复发布 Running Event。");
        }

        [UnityTest]
        public IEnumerator Initialize_ArmsOnlyFirstEncounterAndPublishesRunningStateOnce()
        {
            DemoEncounterController encounterA =
                CreateEncounter("Encounter A", out _);
            DemoEncounterController encounterB =
                CreateEncounter("Encounter B", out _);
            DemoRunDefinition definition = CreateDefinition(2);

            GameObject runObject = CreateGameObject("Demo Run Controller");
            runObject.SetActive(false);
            DemoRunController controller = runObject.AddComponent<DemoRunController>();
            SetPrivateField(controller, "_definition", definition);
            SetPrivateField(controller, "_encounters", new[] { encounterA, encounterB });
            SetPrivateField(controller, "_playerHealth", CreateHealth("Player"));
            runObject.SetActive(true);

            int runningEventCount = 0;
            EventBus<RunStateChangedEvent>.Subscribe(runEvent =>
            {
                if (runEvent.PreviousState == RunState.NotStarted &&
                    runEvent.CurrentState == RunState.Running)
                {
                    runningEventCount++;
                }
            });

            Assert.That(controller.Initialize(), Is.True);
            Assert.That(controller.Initialize(), Is.False,
                "重复初始化必须被拒绝，避免重复配置 Encounter 与重复发布 Running Event。 ");
            Assert.That(controller.State, Is.EqualTo(RunState.Running));
            Assert.That(controller.CurrentEncounterIndex, Is.Zero);
            Assert.That(encounterA.State, Is.EqualTo(EncounterState.Armed));
            Assert.That(encounterB.State, Is.EqualTo(EncounterState.Waiting));
            Assert.That(runningEventCount, Is.EqualTo(1));

            yield return null;
        }

        [UnityTest]
        public IEnumerator CompletingCurrentEncounter_AdvancesOnceAndArmsNextEncounter()
        {
            DemoEncounterController encounterA =
                CreateEncounter("Encounter A", out HealthComponent enemyA);
            DemoEncounterController encounterB =
                CreateEncounter("Encounter B", out _);
            DemoRunDefinition definition = CreateDefinition(2);

            GameObject runObject = CreateGameObject("Demo Run Controller");
            runObject.SetActive(false);
            DemoRunController controller = runObject.AddComponent<DemoRunController>();
            SetPrivateField(controller, "_definition", definition);
            SetPrivateField(controller, "_encounters", new[] { encounterA, encounterB });
            SetPrivateField(controller, "_playerHealth", CreateHealth("Player"));
            runObject.SetActive(true);

            Assert.That(controller.Initialize(), Is.True);
            Assert.That(encounterA.TryBeginEncounter(), Is.True,
                "只有先真正开始第一战，RunProgressTracker 才能接受对应的完成事件。 ");

            EventBus<DeathEvent>.Publish(new DeathEvent
            {
                TargetId = enemyA.gameObject.GetInstanceID()
            });

            Assert.That(encounterA.State, Is.EqualTo(EncounterState.Cleared));
            Assert.That(controller.CurrentEncounterIndex, Is.EqualTo(1));
            Assert.That(encounterB.State, Is.EqualTo(EncounterState.Armed));

            // 重复死亡属于可能发生的外部噪声，不能让整局越过尚未开始的第二战。
            EventBus<DeathEvent>.Publish(new DeathEvent
            {
                TargetId = enemyA.gameObject.GetInstanceID()
            });

            Assert.That(controller.CurrentEncounterIndex, Is.EqualTo(1));
            Assert.That(encounterB.State, Is.EqualTo(EncounterState.Armed));

            yield return null;
        }

        [UnityTest]
        public IEnumerator CompletingFinalEncounter_EntersCompletedAndPublishesTerminalStateOnce()
        {
            // 单 Encounter 是终局分支的最小测试场景：清理它以后已经没有下一战可以 Arm。
            DemoEncounterController finalEncounter =
                CreateEncounter("Final Encounter", out HealthComponent finalEnemy);
            DemoRunDefinition definition = CreateDefinition(1);

            GameObject runObject = CreateGameObject("Demo Run Controller");
            runObject.SetActive(false);
            DemoRunController controller = runObject.AddComponent<DemoRunController>();
            SetPrivateField(controller, "_definition", definition);
            SetPrivateField(controller, "_encounters", new[] { finalEncounter });
            SetPrivateField(controller, "_playerHealth", CreateHealth("Player"));
            runObject.SetActive(true);

            int completedEventCount = 0;
            EventBus<RunStateChangedEvent>.Subscribe(runEvent =>
            {
                if (runEvent.PreviousState == RunState.Running &&
                    runEvent.CurrentState == RunState.Completed)
                {
                    completedEventCount++;
                }
            });

            Assert.That(controller.Initialize(), Is.True);
            Assert.That(finalEncounter.TryBeginEncounter(), Is.True);

            EventBus<DeathEvent>.Publish(new DeathEvent
            {
                TargetId = finalEnemy.gameObject.GetInstanceID()
            });

            Assert.That(finalEncounter.State, Is.EqualTo(EncounterState.Cleared));
            Assert.That(controller.State, Is.EqualTo(RunState.Completed));
            Assert.That(controller.CurrentEncounterIndex, Is.Zero,
                "完成最终战后索引保留在最后一战，终局由 RunState 表达，而不是制造越界索引。");
            Assert.That(completedEventCount, Is.EqualTo(1),
                "胜利 UI 应通过唯一一次 Running -> Completed 事件打开，而不是每帧轮询状态。");

            // 重复死亡事件属于外部噪声；终局和胜利事件都必须保持幂等。
            EventBus<DeathEvent>.Publish(new DeathEvent
            {
                TargetId = finalEnemy.gameObject.GetInstanceID()
            });

            Assert.That(controller.State, Is.EqualTo(RunState.Completed));
            Assert.That(completedEventCount, Is.EqualTo(1));

            yield return null;
        }

        [UnityTest]
        public IEnumerator CompletingFinalEncounter_AwaitStageExit_PublishesStageClearedWithoutVictory()
        {
            DemoEncounterController finalEncounter =
                CreateEncounter("Final Encounter", out HealthComponent finalEnemy);
            DemoRunDefinition definition = CreateDefinition(
                1,
                RunCompletionMode.AwaitStageExit);

            GameObject runObject = CreateGameObject("Demo Run Controller");
            runObject.SetActive(false);
            DemoRunController controller = runObject.AddComponent<DemoRunController>();
            SetPrivateField(controller, "_definition", definition);
            SetPrivateField(controller, "_encounters", new[] { finalEncounter });
            SetPrivateField(controller, "_playerHealth", CreateHealth("Player"));
            runObject.SetActive(true);

            int stageClearedEventCount = 0;
            int completedEventCount = 0;
            EventBus<RunStateChangedEvent>.Subscribe(runEvent =>
            {
                if (runEvent.CurrentState == RunState.StageCleared)
                {
                    stageClearedEventCount++;
                }
                else if (runEvent.CurrentState == RunState.Completed)
                {
                    completedEventCount++;
                }
            });

            Assert.That(controller.Initialize(), Is.True);
            Assert.That(finalEncounter.TryBeginEncounter(), Is.True);

            EventBus<DeathEvent>.Publish(new DeathEvent
            {
                TargetId = finalEnemy.gameObject.GetInstanceID()
            });

            Assert.That(controller.State, Is.EqualTo(RunState.StageCleared));
            Assert.That(stageClearedEventCount, Is.EqualTo(1));
            Assert.That(completedEventCount, Is.Zero,
                "地图一清场只开放 Reward/Portal，不能触发 P7 Victory。");
            yield return null;
        }

        [UnityTest]
        public IEnumerator PlayerDeath_EntersFailedAndPublishesTerminalStateOnce()
        {
            DemoEncounterController encounter =
                CreateEncounter("Encounter", out HealthComponent enemy);
            DemoRunDefinition definition = CreateDefinition(1);
            HealthComponent player = CreateHealth("Player");

            GameObject runObject = CreateGameObject("Demo Run Controller");
            runObject.SetActive(false);
            DemoRunController controller = runObject.AddComponent<DemoRunController>();
            SetPrivateField(controller, "_definition", definition);
            SetPrivateField(controller, "_encounters", new[] { encounter });
            SetPrivateField(controller, "_playerHealth", player);
            runObject.SetActive(true);

            int failedEventCount = 0;
            EventBus<RunStateChangedEvent>.Subscribe(runEvent =>
            {
                if (runEvent.PreviousState == RunState.Running &&
                    runEvent.CurrentState == RunState.Failed)
                {
                    failedEventCount++;
                }
            });

            Assert.That(controller.Initialize(), Is.True);

            // Enemy Death 不能触发失败；只有配置的 Player Health ID 才拥有这条语义。
            EventBus<DeathEvent>.Publish(new DeathEvent
            {
                TargetId = enemy.gameObject.GetInstanceID()
            });
            Assert.That(controller.State, Is.EqualTo(RunState.Running));

            EventBus<DeathEvent>.Publish(new DeathEvent { TargetId = player.Id });
            EventBus<DeathEvent>.Publish(new DeathEvent { TargetId = player.Id });

            Assert.That(controller.State, Is.EqualTo(RunState.Failed));
            Assert.That(failedEventCount, Is.EqualTo(1));

            yield return null;
        }

        private DemoEncounterController CreateEncounter(
            string name,
            out HealthComponent enemy)
        {
            GameObject enemyObject = CreateGameObject($"{name} Enemy");
            enemy = enemyObject.AddComponent<HealthComponent>();

            GameObject encounterObject = CreateGameObject(name);
            encounterObject.SetActive(false);
            DemoEncounterController encounter =
                encounterObject.AddComponent<DemoEncounterController>();
            SetPrivateField(encounter, "_enemyMembers", new[] { enemy });
            SetPrivateField(encounter, "_gates", Array.Empty<ArenaGate>());
            encounterObject.SetActive(true);
            return encounter;
        }

        private DemoRunDefinition CreateDefinition(
            int encounterCount,
            RunCompletionMode completionMode = RunCompletionMode.TerminalVictory)
        {
            DemoRunDefinition definition = ScriptableObject.CreateInstance<DemoRunDefinition>();
            _createdObjects.Add(definition);

            var encounters = new DemoEncounterDefinition[encounterCount];
            for (int i = 0; i < encounters.Length; i++)
            {
                encounters[i] = new DemoEncounterDefinition();
            }

            SetPrivateField(definition, "_encounters", encounters);
            SetPrivateField(definition, "_completionMode", completionMode);
            return definition;
        }

        private HealthComponent CreateHealth(string name)
        {
            return CreateGameObject(name).AddComponent<HealthComponent>();
        }

        private GameObject CreateGameObject(string name)
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
                $"测试约定的 private serialized field '{fieldName}' 不存在。 ");
            field.SetValue(target, value);
        }
    }
}
