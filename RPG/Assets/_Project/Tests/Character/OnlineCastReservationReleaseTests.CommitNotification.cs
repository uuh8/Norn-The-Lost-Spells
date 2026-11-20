using System;
using System.Collections.Generic;
using Game.Combat;
using Game.Core;
using Game.Run;
using NUnit.Framework;
using UnityEngine;

namespace Game.Character.Tests
{
    public sealed partial class OnlineCastReservationReleaseTests
    {
        [TestCase(true)]
        [TestCase(false)]
        public void CommittedInventoryNotificationFailureDoesNotOrphanActualCasterOutput(bool rapid)
        {
            // 这是明确注入异常的诊断回归，不证明用户现场真的存在抛错订阅者。
            // 故障点必须在正式资源 Commit 之后：通知消费者的异常不能让 Caster 把
            // 已支付的一发误当作未提交并撤掉 OutputReleased，留下永久的库存忙票据。
            var actor = CreateActor(1, rapid, _dungeon.PlayerSpawn(0));
            if (rapid) ReprogramRapidFireToSlotFive(actor);
            int initialFire = rapid ? 13 : 29;
            float expectedManaAfter = rapid ? 60 : 80;
            float expectedFullMana = rapid ? 120 : 100;
            uint programRevision = rapid ? 2u : 1u;
            Assert.That(Charges(actor, actor.Fire), Is.EqualTo(initialFire));
            Assert.That(State(actor).Program.Revision, Is.EqualTo(programRevision));
            Assert.That(State(actor).Mana, Is.EqualTo(expectedFullMana).Within(.001f));

            int injectedFailures = 0;
            uint committedVersion = 0;
            float committedMana = -1;
            // 出生事实在 Commit 后才公开，所以故障点从自有 Scene/CombatWorld 只读捕获
            // 已 Instantiate 的真实 Projectile；不手动发布 OutputReleased 或代替产品取消票据。
            var bornBeforeNotification = new List<GameObject>();
            Action<InventoryChangedEvent> failOnceAfterCommit = changed =>
            {
                if (changed.InventoryId != actor.Inventory || injectedFailures != 0
                    || Charges(actor, actor.Fire) != initialFire - 1) return;
                injectedFailures++;
                committedVersion = changed.Version;
                committedMana = State(actor).Mana;
                foreach (var root in _scene.GetRootGameObjects())
                {
                    if (_initialRoots.Contains(root.GetInstanceID())) continue;
                    var projectile = root.GetComponent<ProjectileBase>();
                    if (projectile != null && ReferenceEquals(typeof(ProjectileBase)
                            .GetProperty("SimulationWorld", PrivateInstance).GetValue(projectile), _world))
                        bornBeforeNotification.Add(root);
                }
                throw new InvalidOperationException("Injected committed inventory notification failure");
            };

            EventBus<InventoryChangedEvent>.Subscribe(failOnceAfterCommit);
            try
            {
                Queue(actor);
                Assert.That(MoveLoose(actor).Failure, Is.EqualTo(OnlineInventoryCommandFailure.Busy),
                    "起手队列必须保留原 Busy 正控制，不能绕过 Driver 直接写 Owner。");
                float release = Mathf.Max(_definition.ReleaseSeconds(false), _definition.ReleaseSeconds(true));
                for (int i = 0; i < Mathf.CeilToInt(release / TickSeconds) + 2; i++) StepAll(TickSeconds);

                Assert.That(injectedFailures, Is.EqualTo(1), "必须确实经过本 Owner 已提交 Fire 的故障点。");
                Assert.That(committedVersion, Is.GreaterThan(0u));
                Assert.That(committedMana, Is.EqualTo(expectedManaAfter).Within(.001f));
                Assert.That(bornBeforeNotification.Count, Is.EqualTo(1), "异常发生前应已生成唯一的真实 Projectile。");
                Assert.That(Charges(actor, actor.Fire), Is.EqualTo(initialFire - 1), "已提交的原 Fire 只能消费一次。");

                // 停手后只推进既有 Authority Tick；不再 Queue，不销毁弹体，不取消/清理资源预约。
                // 八秒足够两个正式模板回满 Mana，不能靠时间流逝把已扣次数补回来。
                AdvanceIdleSeconds(8f);
                var move = MoveLoose(actor);
                Assert.That(move.Success, Is.True,
                    "已提交库存通知异常不能永久锁住新的 MoveOwned，实际拒绝=" + move.Failure);
                AssertMovedLoose(actor);
                Assert.That(actor.Owner.V2CastResourcesReadOnly.ActiveReservations, Is.Zero);
                Assert.That(State(actor).Mana, Is.EqualTo(expectedFullMana).Within(.001f));
                AssertRealRelease(actor, initialFire - 1, expectedManaAfter);
                Assert.That(_outputs.Count, Is.EqualTo(1));
                Assert.That(_observations.Count, Is.EqualTo(1));
                Assert.That(_outputs[0].Output, Is.SameAs(bornBeforeNotification[0]),
                    "应提交最初生成的同一个输出，不能靠重施法、补发投射物或退款使测试通过。");
                Assert.That(_outputs[0].Output.activeInHierarchy, Is.True);
                Assert.That(injectedFailures, Is.EqualTo(1));
            }
            finally
            {
                EventBus<InventoryChangedEvent>.Unsubscribe(failOnceAfterCommit);
                // RED 可能在网络出生事实发布前结束；基类的 _outputs 此时还不知道这个实例。
                // 只在所有行为断言结束后清理捕获的自有对象，不能用销毁回调帮测试提前解锁。
                foreach (var output in bornBeforeNotification) DestroyOwned(output);
            }
        }

        [TestCase("result")]
        [TestCase("observation")]
        [TestCase("output")]
        public void CommittedFactFailureStillReachesLaterAuthoritativeConsumers(string boundary)
        {
            // 故意比 CreateActor 更早注册：Complete 收据的故障必须在 Adapter 前，
            // OutputReleased 的故障必须在 Owner 前。只在整个 Invoke 外 catch 仍会漏掉
            // 后面的真正消费者，不能以“异常不再向外冒泡”冒充事务与世界事实一致。
            Actor actor = null;
            int injectedFailures = 0, deliveredAfterFailure = 0;
            float committedMana = -1;
            var bornAtFailure = new List<GameObject>();
            Action failOnceAfterCommit = () =>
            {
                if (injectedFailures != 0 || actor == null || Charges(actor, actor.Fire) != 12) return;
                injectedFailures++;
                committedMana = State(actor).Mana;
                foreach (var root in _scene.GetRootGameObjects())
                {
                    if (_initialRoots.Contains(root.GetInstanceID())) continue;
                    var projectile = root.GetComponent<ProjectileBase>();
                    if (projectile != null && ReferenceEquals(typeof(ProjectileBase)
                            .GetProperty("SimulationWorld", PrivateInstance).GetValue(projectile), _world))
                        bornAtFailure.Add(root);
                }
                throw new InvalidOperationException("Injected committed " + boundary + " notification failure");
            };
            Action<CastResourceResultEvent> resultFailure = value =>
            {
                if (actor != null && value.InventoryId == actor.Inventory && value.SourceWandId == actor.Wand
                    && value.Operation == CastResourceOperation.Complete && value.Success) failOnceAfterCommit();
            };
            Action<SpellExecutionObservedEvent> observationFailure = value =>
            {
                if (actor != null && value.InventoryId == actor.Inventory && value.SourceWandId == actor.Wand
                    && value.Kind == SpellExecutionObservationKind.OutputReleased) failOnceAfterCommit();
            };
            Action<CombatOutputSpawnedEvent> outputFailure = value =>
            {
                if (actor != null && value.ActorId == actor.Id && value.CommandId == 1) failOnceAfterCommit();
            };
            Action<CastResourceResultEvent> laterResult = value =>
            {
                if (actor != null && value.InventoryId == actor.Inventory && value.SourceWandId == actor.Wand
                    && value.Operation == CastResourceOperation.Complete && value.Success) deliveredAfterFailure++;
            };
            Action<SpellExecutionObservedEvent> laterObservation = value =>
            {
                if (actor != null && value.InventoryId == actor.Inventory && value.SourceWandId == actor.Wand
                    && value.Kind == SpellExecutionObservationKind.OutputReleased) deliveredAfterFailure++;
            };
            Action<CombatOutputSpawnedEvent> laterOutput = value =>
            {
                if (actor != null && value.ActorId == actor.Id && value.CommandId == 1) deliveredAfterFailure++;
            };

            if (boundary == "result") EventBus<CastResourceResultEvent>.Subscribe(resultFailure);
            else if (boundary == "observation") EventBus<SpellExecutionObservedEvent>.Subscribe(observationFailure);
            else EventBus<CombatOutputSpawnedEvent>.Subscribe(outputFailure);
            try
            {
                actor = CreateActor(1, true, _dungeon.PlayerSpawn(0));
                ReprogramRapidFireToSlotFive(actor);
                Assert.That(State(actor).Program.Revision, Is.EqualTo(2));
                Assert.That(Charges(actor, actor.Fire), Is.EqualTo(13));
                Assert.That(State(actor).Mana, Is.EqualTo(120).Within(.001f));
                // 此尾订阅排在故障、Owner/Adapter之后。它只观察同一事实，没有补发或修复能力。
                if (boundary == "result") EventBus<CastResourceResultEvent>.Subscribe(laterResult);
                else if (boundary == "observation") EventBus<SpellExecutionObservedEvent>.Subscribe(laterObservation);
                else EventBus<CombatOutputSpawnedEvent>.Subscribe(laterOutput);

                Queue(actor);
                Assert.That(MoveLoose(actor).Failure, Is.EqualTo(OnlineInventoryCommandFailure.Busy));
                float release = Mathf.Max(_definition.ReleaseSeconds(false), _definition.ReleaseSeconds(true));
                for (int i = 0; i < Mathf.CeilToInt(release / TickSeconds) + 2; i++) StepAll(TickSeconds);
                Assert.That(injectedFailures, Is.EqualTo(1), boundary + " 必须真正触发已提交事实的异常。");
                Assert.That(committedMana, Is.EqualTo(60).Within(.001f));
                Assert.That(Charges(actor, actor.Fire), Is.EqualTo(12));
                Assert.That(bornAtFailure.Count, Is.EqualTo(1));

                AdvanceIdleSeconds(8f);
                var move = MoveLoose(actor);
                Assert.That(move.Success, Is.True, boundary + " 异常后的新 MoveOwned=" + move.Failure);
                AssertMovedLoose(actor);
                Assert.That(deliveredAfterFailure, Is.EqualTo(1),
                    "故障之后的消费者必须恰好收到一次同一事实，不能只catch整个多播Invoke。");
                Assert.That(actor.Owner.V2CastResourcesReadOnly.ActiveReservations, Is.Zero);
                Assert.That(State(actor).Mana, Is.EqualTo(120).Within(.001f));
                AssertRealRelease(actor, 12, 60);
                Assert.That(_outputs.Count, Is.EqualTo(1));
                Assert.That(_observations.Count, Is.EqualTo(1));
                Assert.That(_outputs[0].Output, Is.SameAs(bornAtFailure[0]));
                Assert.That(_outputs[0].Output.activeInHierarchy, Is.True);
            }
            finally
            {
                EventBus<CastResourceResultEvent>.Unsubscribe(resultFailure);
                EventBus<CastResourceResultEvent>.Unsubscribe(laterResult);
                EventBus<SpellExecutionObservedEvent>.Unsubscribe(observationFailure);
                EventBus<SpellExecutionObservedEvent>.Unsubscribe(laterObservation);
                EventBus<CombatOutputSpawnedEvent>.Unsubscribe(outputFailure);
                EventBus<CombatOutputSpawnedEvent>.Unsubscribe(laterOutput);
                foreach (var output in bornAtFailure) DestroyOwned(output);
            }
        }
    }
}
