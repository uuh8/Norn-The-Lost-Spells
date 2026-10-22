using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Game.Combat;
using Game.Core;

namespace Game.Combat.Tests
{
    /// <summary>
    /// 覆盖 StatusController 作为 Unity Adapter 的职责：强度存储、自然衰减、
    /// Runtime 集成、事件发布、减速与 DoT。纯 Runtime 的公式细节由独立测试负责。
    /// </summary>
    public class StatusControllerTests
    {
        // 测试按需创建临时 ScriptableObject，避免依赖 Project 中某个可被调参的真实资产。
        private static StatusDefinition Def(StatusKind kind, float decay = 0f)
        {
            StatusDefinition definition = ScriptableObject.CreateInstance<StatusDefinition>();
            definition.Kind = kind;
            definition.DisplayName = kind.ToString();
            definition.NaturalDecayPerSecond = decay;
            return definition;
        }

        private static StatusDefinition StickyDef()
        {
            StatusDefinition definition = Def(StatusKind.Sticky);
            definition.AffectsMoveSpeed = true;
            definition.MaxMoveSpeedSlowRatio = 0.4f;
            return definition;
        }

        private static StatusController Controller(params StatusDefinition[] definitions)
        {
            // 每个测试拥有独立 GameObject，结束时销毁，防止状态和 EventBus 观察相互污染。
            GameObject go = new GameObject("status-test");
            StatusController controller = go.AddComponent<StatusController>();
            controller.SetDefinitionsForTests(definitions);
            return controller;
        }

        private static ElementReactionProfile ReactionProfile()
        {
            // 明确写出所有数值，使断言可以由 rate * time 等公式直接推导。
            ElementReactionProfile profile = ScriptableObject.CreateInstance<ElementReactionProfile>();
            profile.Extinguish = new ExtinguishTuning
            {
                FormalThreshold = 10f,
                FormalRatePerSecond = 50f,
                LowRatePerSecond = 10f,
            };
            profile.WetCleanse = new WetCleanseTuning { BudgetPerSecondAtFullWet = 100f };
            profile.ToxicCombustion = new ToxicCombustionTuning
            {
                FireThreshold = 25f,
                PoisonThreshold = 20f,
                WindUpSeconds = 0.2f,
                MaxPoisonConsume = 40f,
                MinEffectiveConsume = 5f,
                BaseDamage = 8f,
                DamagePerPoison = 0.4f,
                Radius = 2.5f,
                CooldownSeconds = 1f,
            };
            profile.IgniteGoo = new IgniteGooTuning
            {
                FireThreshold = 20f,
                GooThreshold = 10f,
                WetInhibitThreshold = 10f,
                RequiredFireCapacity = 10f,
                GooConsumePerSecond = 40f,
                FirePerGoo = 1.25f,
                CooldownSeconds = 0.5f,
            };
            return profile;
        }

        [Test]
        public void ApplyStatus_AddsIntensityAndClampsTo100()
        {
            StatusController controller = Controller(Def(StatusKind.Burning));

            controller.ApplyStatus(StatusKind.Burning, 40f, 10, 1);
            controller.ApplyStatus(StatusKind.Burning, 70f, 10, 1);

            Assert.IsTrue(controller.HasStatus(StatusKind.Burning));
            Assert.AreEqual(100f, controller.GetIntensity(StatusKind.Burning), 1e-4f);
            Object.DestroyImmediate(controller.gameObject);
        }

        [Test]
        public void Tick_ReducesIntensityByNaturalDecay()
        {
            StatusController controller = Controller(Def(StatusKind.Burning, 8f));

            controller.ApplyStatus(StatusKind.Burning, 100f, 10, 1);
            controller.TickForTests(1f);

            Assert.AreEqual(92f, controller.GetIntensity(StatusKind.Burning), 1e-4f);
            Object.DestroyImmediate(controller.gameObject);
        }

        [Test]
        public void Tick_RemovesStatusWhenIntensityReachesZero()
        {
            StatusController controller = Controller(Def(StatusKind.Burning, 10f));

            controller.ApplyStatus(StatusKind.Burning, 5f, 10, 1);
            controller.TickForTests(1f);

            Assert.IsFalse(controller.HasStatus(StatusKind.Burning));
            Assert.AreEqual(0f, controller.GetIntensity(StatusKind.Burning), 1e-4f);
            Object.DestroyImmediate(controller.gameObject);
        }

        [Test]
        public void SustainedStatus_HoldsNaturalDecayUntilWindowExpires()
        {
            // 0.3 秒 Hold 覆盖第一个 0.2 秒 Tick，并覆盖第二个 Tick 的前 0.1 秒；
            // 第二个 Tick 剩余 0.1 秒才恢复 8/s 衰减，因此只减少 0.8。
            StatusController controller = Controller(Def(StatusKind.Wet, 8f));

            controller.ApplySustainedStatus(StatusKind.Wet, 40f, 10, 1, 0.3f);
            controller.TickForTests(0.2f);
            Assert.AreEqual(40f, controller.GetIntensity(StatusKind.Wet), 1e-4f);

            controller.TickForTests(0.2f);
            Assert.AreEqual(39.2f, controller.GetIntensity(StatusKind.Wet), 1e-4f);
            Object.DestroyImmediate(controller.gameObject);
        }

        [Test]
        public void RepeatedSustainedApply_RefreshesHoldWithoutAddingDurations()
        {
            // 第二次持续施加把剩余 0.1 秒刷新为 0.3 秒，而不是累加成 0.4 秒。
            // 随后经过 0.25 + 0.1 秒，最后 0.05 秒应恢复 8/s 衰减，即减少 0.4。
            StatusController controller = Controller(Def(StatusKind.Wet, 8f));

            controller.ApplySustainedStatus(StatusKind.Wet, 10f, 10, 1, 0.3f);
            controller.TickForTests(0.2f);
            controller.ApplySustainedStatus(StatusKind.Wet, 10f, 10, 1, 0.3f);
            controller.TickForTests(0.25f);
            Assert.AreEqual(20f, controller.GetIntensity(StatusKind.Wet), 1e-4f);

            controller.TickForTests(0.1f);
            Assert.AreEqual(19.6f, controller.GetIntensity(StatusKind.Wet), 1e-4f);
            Object.DestroyImmediate(controller.gameObject);
        }

        [Test]
        public void SustainedStatus_DoesNotBlockElementReactionConsumption()
        {
            // Hold 只屏蔽 NaturalDecay；正式 Extinguish 仍按 50/s 连续中和双方。
            ElementReactionProfile profile = ReactionProfile();
            StatusController controller = Controller(
                Def(StatusKind.Burning, 8f),
                Def(StatusKind.Wet, 8f));
            controller.SetReactionProfileForTests(profile);

            controller.ApplySustainedStatus(StatusKind.Burning, 80f, 10, 1, 1f);
            controller.ApplySustainedStatus(StatusKind.Wet, 30f, 11, 2, 1f);
            controller.TickForTests(0.2f);

            Assert.AreEqual(70f, controller.GetIntensity(StatusKind.Burning), 1e-4f);
            Assert.AreEqual(20f, controller.GetIntensity(StatusKind.Wet), 1e-4f);
            Object.DestroyImmediate(controller.gameObject);
            Object.DestroyImmediate(profile);
        }

        [Test]
        public void Reaction_ApplyOnlyStartsThenTickContinuouslyExtinguishes()
        {
            // ApplyStatus 只建立 Process；0.6 秒 Tick 才完成 30 点正式中和。
            ElementReactionProfile profile = ReactionProfile();
            StatusController controller = Controller(
                Def(StatusKind.Burning),
                Def(StatusKind.Wet));
            controller.SetReactionProfileForTests(profile);

            controller.ApplyStatus(StatusKind.Burning, 80f, 10, 1);
            controller.ApplyStatus(StatusKind.Wet, 30f, 11, 2);

            Assert.AreEqual(80f, controller.GetIntensity(StatusKind.Burning), 1e-4f);
            Assert.AreEqual(30f, controller.GetIntensity(StatusKind.Wet), 1e-4f);

            controller.TickForTests(0.6f);

            Assert.AreEqual(50f, controller.GetIntensity(StatusKind.Burning), 1e-4f);
            Assert.AreEqual(0f, controller.GetIntensity(StatusKind.Wet), 1e-4f);
            Object.DestroyImmediate(controller.gameObject);
            Object.DestroyImmediate(profile);
        }

        [Test]
        public void WetCleanse_UsesDefinitionMultipliersAndPrioritizesPoison()
        {
            // 同时测试 Profile 的公共预算与 StatusDefinition 的逐状态 multiplier 能正确汇合。
            ElementReactionProfile profile = ReactionProfile();
            StatusDefinition poison = Def(StatusKind.Poisoned);
            poison.WetCleanseable = true;
            poison.WetCleanseMultiplier = 0.75f;
            StatusDefinition sticky = StickyDef();
            sticky.WetCleanseable = true;
            sticky.WetCleanseMultiplier = 1.25f;
            StatusController controller = Controller(Def(StatusKind.Wet), poison, sticky);
            controller.SetReactionProfileForTests(profile);
            controller.ApplyStatus(StatusKind.Wet, 40f, 1, 1);
            controller.ApplyStatus(StatusKind.Poisoned, 30f, 2, 2);
            controller.ApplyStatus(StatusKind.Sticky, 30f, 3, 3);

            controller.TickForTests(1f);

            Assert.AreEqual(0f, controller.GetIntensity(StatusKind.Poisoned), 1e-4f);
            Assert.AreEqual(30f, controller.GetIntensity(StatusKind.Sticky), 1e-4f);
            Object.DestroyImmediate(controller.gameObject);
            Object.DestroyImmediate(profile);
        }

        [Test]
        public void ReactionSignal_IsPublishedAsDiscreteEvent()
        {
            // 连续 Tick 不应连续广播；Extinguish Started 只在阶段边沿发布一次。
            ElementReactionProfile profile = ReactionProfile();
            StatusController controller = Controller(Def(StatusKind.Burning), Def(StatusKind.Wet));
            controller.SetReactionProfileForTests(profile);
            int eventCount = 0;
            ElementReactionEvent received = default;
            void OnReaction(ElementReactionEvent e)
            {
                eventCount++;
                received = e;
            }

            EventBus<ElementReactionEvent>.Subscribe(OnReaction);
            controller.ApplyStatus(StatusKind.Burning, 80f, 10, 1);
            controller.ApplyStatus(StatusKind.Wet, 30f, 11, 2);
            controller.TickForTests(0.1f);
            controller.TickForTests(0.1f);
            EventBus<ElementReactionEvent>.Unsubscribe(OnReaction);

            Assert.AreEqual(1, eventCount, "应恰好收到一次离散 Started 事件");
            Assert.AreEqual(controller.gameObject.GetInstanceID(), received.TargetId);
            Assert.AreEqual(ElementReactionId.Extinguish, received.Reaction);
            Assert.AreEqual(ElementReactionPhase.Started, received.Phase);
            Assert.AreEqual(controller.transform.position, received.WorldPosition);
            Object.DestroyImmediate(controller.gameObject);
            Object.DestroyImmediate(profile);
        }

        [Test]
        public void Sticky_ProducesMoveSpeedMultiplier()
        {
            // 100% Sticky：1 - 0.4 = 0.6；50% Sticky：1 - 0.4*0.5 = 0.8。
            StatusController controller = Controller(StickyDef());

            controller.ApplyStatus(StatusKind.Sticky, 100f, 10, 1);
            Assert.AreEqual(0.6f, controller.MoveSpeedMultiplier, 1e-4f);
            Object.DestroyImmediate(controller.gameObject);

            controller = Controller(StickyDef());
            controller.ApplyStatus(StatusKind.Sticky, 50f, 10, 1);
            Assert.AreEqual(0.8f, controller.MoveSpeedMultiplier, 1e-4f);
            Object.DestroyImmediate(controller.gameObject);
        }

        [Test]
        public void SustainedSticky_AlwaysUsesMaximumSlowWhileLeaseIsActive()
        {
            StatusController controller = Controller(StickyDef());

            controller.ApplySustainedStatus(StatusKind.Sticky, 10f, 10, 1, 0.3f);

            Assert.AreEqual(0.6f, controller.MoveSpeedMultiplier, 1e-4f);
            Object.DestroyImmediate(controller.gameObject);
        }

        [Test]
        public void SustainedSticky_ReturnsToIntensityScaledSlowAfterLeaseExpires()
        {
            StatusController controller = Controller(StickyDef());

            controller.ApplySustainedStatus(StatusKind.Sticky, 20f, 10, 1, 0.1f);
            Assert.AreEqual(0.6f, controller.MoveSpeedMultiplier, 1e-4f);

            controller.TickForTests(0.1f);

            Assert.AreEqual(0.92f, controller.MoveSpeedMultiplier, 1e-4f);
            Object.DestroyImmediate(controller.gameObject);
        }

        [UnityTest]
        public IEnumerator Poisoned_DealsDotWithoutHitReaction()
        {
            // HealthComponent/MonoBehaviour 生命周期需要 PlayMode；这里验证 DoT 仍走统一 DamageRequest 漏斗。
            yield return new EnterPlayMode();

            GameObject go = new GameObject("poison-target");
            HealthComponent health = go.AddComponent<HealthComponent>();
            StatusController controller = go.AddComponent<StatusController>();

            StatusDefinition poison = Def(StatusKind.Poisoned);
            poison.DealsDamage = true;
            poison.DamageInterval = 1f;
            poison.BaseDamagePerTick = 4f;
            poison.DamageType = DamageType.Magical;
            poison.TriggerHitReaction = false;

            controller.SetDefinitionsForTests(poison);
            controller.ApplyStatus(StatusKind.Poisoned, 100f, 99, 2);

            float before = health.CurrentHp;
            controller.TickForTests(1f);
            float after = health.CurrentHp;

            Object.Destroy(go);
            Object.Destroy(poison);
            yield return null;
            yield return new ExitPlayMode();

            Assert.AreEqual(before - 4f, after, 1e-4f);
        }

        [UnityTest]
        public IEnumerator Invulnerability_BlocksNewStatusUntilReleased()
        {
            yield return new EnterPlayMode();

            GameObject go = new GameObject("protected-status-target");
            HealthComponent health = go.AddComponent<HealthComponent>();
            StatusController controller = go.AddComponent<StatusController>();

            health.SetInvulnerable(true);
            controller.ApplyStatus(StatusKind.Burning, 45f, 10, 2);
            bool appliedWhileProtected =
                controller.HasStatus(StatusKind.Burning);

            health.SetInvulnerable(false);
            controller.ApplyStatus(StatusKind.Burning, 45f, 10, 2);
            bool appliedAfterRelease =
                controller.HasStatus(StatusKind.Burning);

            Object.Destroy(go);
            yield return null;
            yield return new ExitPlayMode();

            Assert.That(appliedWhileProtected, Is.False,
                "无敌保护期不能预先叠加 Burning/Poison 等状态。");
            Assert.That(appliedAfterRelease, Is.True,
                "解除保护后必须恢复正常 Status 写入。");
        }
    }
}
