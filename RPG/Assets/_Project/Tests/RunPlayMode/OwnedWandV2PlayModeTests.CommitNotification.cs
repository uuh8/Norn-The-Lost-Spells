#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Game.Combat;
using Game.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Run.Tests
{
    public sealed partial class OwnedWandV2PlayModeTests
    {
        [UnityTest]
        public IEnumerator CommittedInventoryNotificationFailureKeepsSinglePlayerOutputAndReleasesReservation()
        {
            // 此例验证单机会话与已提交事实的边界，不把人工注入异常当成历史玩家截图的根因。
            // 原 Sandbox 卡为 Unlimited；先用生产命令移走它，再在本例自有内存 Store 中放入
            // 有限 Fire，保证本次真的发生 13→12 与 InventoryChanged，而非空跑通知测试。
            _adapter.Select(0);
            Assert.That(_adapter.TryCapture(out var original, out _), Is.True);
            string inventoryId = _session.ReadOnly.InventoryId;
            string originalFire = original.Program.GetInstanceId(0);
            string finiteFire = "single-player.commit-notification.fire";
            Assert.That(_session.Profile.Items.TryGet(originalFire, out var unlimited), Is.True);
            Assert.That(unlimited.ContentId, Is.EqualTo("wandtest.fire"));
            Assert.That(unlimited.HasUnlimitedCharges, Is.True);

            var edits = new List<InventoryCommandResultEvent>();
            var observations = new List<SpellExecutionObservedEvent>();
            var beforeProjectiles = new HashSet<int>();
            List<ProjectileBase> bornAtNotification = null;
            int injectedFailures = 0;
            float manaAtNotification = -1;
            Action<InventoryCommandResultEvent> onEdit = result =>
            {
                if (result.InventoryId == inventoryId && (result.RequestId == 1 || result.RequestId == 2)) edits.Add(result);
            };
            Action<SpellExecutionObservedEvent> onObserved = observed =>
            {
                if (observed.InventoryId == inventoryId && observed.SourceWandId == original.SourceWandId)
                    observations.Add(observed);
            };
            Action<InventoryChangedEvent> failOnceAfterCommit = changed =>
            {
                if (changed.InventoryId != inventoryId || injectedFailures != 0
                    || !_session.Profile.Items.TryGet(finiteFire, out var fire) || fire.RemainingCharges != 12) return;
                injectedFailures++;
                _session.WandsReadOnly.TryGetSlot(0, out var paid);
                manaAtNotification = paid.CurrentMana;
                bornAtNotification = CaptureNewSinglePlayerCommitProjectiles(beforeProjectiles);
                throw new InvalidOperationException("Injected single-player committed inventory notification failure");
            };

            EventBus<InventoryCommandResultEvent>.Subscribe(onEdit);
            EventBus<SpellExecutionObservedEvent>.Subscribe(onObserved);
            try
            {
                EventBus<InventoryCommandEvent>.Publish(new InventoryCommandEvent
                {
                    InventoryId = inventoryId, RequestId = 1, ItemId = originalFire,
                    Kind = InventoryCommandKind.Place, Destination = InventoryDestination.SpellStorage,
                    OwnerId = _session.Profile.ActorId, SlotIndex = 0, ExpectedVersion = _session.ReadOnly.Version
                });
                Assert.That(edits.Count, Is.EqualTo(1));
                Assert.That(edits[0].Success, Is.True, edits[0].Reason.ToString());
                Assert.That(edits[0].ItemId, Is.EqualTo(originalFire));
                Assert.That(_session.Profile.Items.TryGetLocation(originalFire, out var storedOriginal), Is.True);
                Assert.That(storedOriginal.Kind, Is.EqualTo(ItemLocationKind.SpellStorage));
                Assert.That(_session.Profile.Items.TryAdd(new RuntimeItemState(finiteFire, "wandtest.fire", 13),
                    new ItemLocation(ItemLocationKind.WandSlot, original.SourceWandId, 0, 0, 0, 0)), Is.True);
                // Select 沿原服务同步新程序；不直接改 Draw/Mana/冷却来跳过重新编程的约束。
                _adapter.Select(0);
                yield return new WaitForSeconds(1f);
                Assert.That(_adapter.TryCapture(out var program, out var context), Is.True);
                Assert.That(program.Program.GetInstanceId(0), Is.EqualTo(finiteFire));
                Assert.That(_session.WandsReadOnly.TryGetSlot(0, out var ready), Is.True);
                Assert.That(ready.CurrentMana, Is.EqualTo(100).Within(.001f));
                Assert.That(ready.CooldownRemaining, Is.Zero);
                foreach (var root in _scene.GetRootGameObjects())
                    if (root.GetComponent<ProjectileBase>() != null) beforeProjectiles.Add(root.GetInstanceID());

                EventBus<InventoryChangedEvent>.Subscribe(failOnceAfterCommit);
                LogAssert.Expect(LogType.Warning, new Regex("Injected single-player committed inventory notification failure"));
                int emitted = _caster.CastOwnedProgram(program, context, new Vector3(200, 100, 200),
                    new Vector3(200, 100, 230), 1, 777, _wizard.CharacterController);
                Assert.That(injectedFailures, Is.EqualTo(1), "必须在有限 Fire 已提交后触发实际通知异常。");
                Assert.That(_session.Profile.Items.TryGet(finiteFire, out var paidFire), Is.True);
                Assert.That(paidFire.RemainingCharges, Is.EqualTo(12));
                Assert.That(paidFire.HasUnlimitedCharges, Is.False);
                Assert.That(manaAtNotification, Is.EqualTo(80).Within(.001f));
                Assert.That(bornAtNotification, Is.Not.Null);
                Assert.That(bornAtNotification.Count, Is.EqualTo(1));
                Assert.That(emitted, Is.EqualTo(1), "通知异常不能把已提交并出生的一发改报为生成失败。");
                Assert.That(_adapter.LastRootCommitted, Is.True);
                var afterProjectiles = CaptureNewSinglePlayerCommitProjectiles(beforeProjectiles);
                Assert.That(afterProjectiles.Count, Is.EqualTo(1));
                Assert.That(afterProjectiles[0], Is.SameAs(bornAtNotification[0]));
                Assert.That(afterProjectiles[0].gameObject.activeInHierarchy, Is.True);
                Assert.That(afterProjectiles[0].ExecutionMode, Is.EqualTo(CombatExecutionMode.SinglePlayer));

                Assert.That(_session.ExecutionReplays.TryGetLatest(program.SourceWandId, out var replay), Is.True);
                Assert.That(replay.Graph.Outputs.Count, Is.EqualTo(1));
                Assert.That(replay.Graph.Outputs[0].SourceInstanceId, Is.EqualTo(finiteFire));
                Assert.That(replay.WorldStatus, Is.EqualTo(WandWorldExecutionStatus.Released));
                Assert.That(replay.ReleasedOutputs[0], Is.True);
                Assert.That(replay.Summary.ManaAfter, Is.EqualTo(80));
                Assert.That(replay.UsesPaid.Count, Is.EqualTo(1));
                Assert.That(replay.UsesPaid[0].InstanceId, Is.EqualTo(finiteFire));
                Assert.That(replay.UsesPaid[0].Count, Is.EqualTo(1));
                Assert.That(observations.Count, Is.EqualTo(1));
                Assert.That(observations[0].TicketId, Is.EqualTo(replay.TicketId));
                Assert.That(observations[0].Kind, Is.EqualTo(SpellExecutionObservationKind.OutputReleased));
                Assert.That(_session.V2CastResourcesReadOnly.ActiveReservations, Is.Zero,
                    "单机编辑不以活票据数量为门，必须独立验证资源票据确实已终结。");
                Assert.That(_session.Profile.Items.IsResourceBusy, Is.False);

                // 使用新的真实编辑命令与版本验证明确收据；不能用 Store.TryMove 代替玩家命令链。
                EventBus<InventoryCommandEvent>.Publish(new InventoryCommandEvent
                {
                    InventoryId = inventoryId, RequestId = 2, ItemId = finiteFire,
                    Kind = InventoryCommandKind.Place, Destination = InventoryDestination.SpellStorage,
                    OwnerId = _session.Profile.ActorId, SlotIndex = 1, ExpectedVersion = _session.ReadOnly.Version
                });
                Assert.That(edits.Count, Is.EqualTo(2));
                Assert.That(edits[1].RequestId, Is.EqualTo(2ul));
                Assert.That(edits[1].ItemId, Is.EqualTo(finiteFire));
                Assert.That(edits[1].Success, Is.True, edits[1].Reason.ToString());
                Assert.That(_session.Profile.Items.TryGetLocation(finiteFire, out var moved), Is.True);
                Assert.That(moved.Kind, Is.EqualTo(ItemLocationKind.SpellStorage));
                Assert.That(moved.OwnerId, Is.EqualTo(_session.Profile.ActorId));
                Assert.That(moved.SlotIndex, Is.EqualTo(1));
                Assert.That(_session.Profile.Items.TryGet(finiteFire, out var afterEdit), Is.True);
                Assert.That(afterEdit.RemainingCharges, Is.EqualTo(12));
                Assert.That(_session.Profile.Items.TryGet(originalFire, out var unchangedOriginal), Is.True);
                Assert.That(unchangedOriginal.HasUnlimitedCharges, Is.True);
                Assert.That(unchangedOriginal.RemainingCharges, Is.EqualTo(unlimited.RemainingCharges));
                Assert.That(_session.V2CastResourcesReadOnly.ActiveReservations, Is.Zero);
                Assert.That(injectedFailures, Is.EqualTo(1));
            }
            finally
            {
                EventBus<InventoryChangedEvent>.Unsubscribe(failOnceAfterCommit);
                EventBus<SpellExecutionObservedEvent>.Unsubscribe(onObserved);
                EventBus<InventoryCommandResultEvent>.Unsubscribe(onEdit);
                // Projectile 由原 Owned context 放入本 Sandbox Scene，沿既有 UnityTearDown 卸载；
                // 不在行为断言之前销毁对象，也不手动 Observe/Cancel/清全局 EventBus 来解锁。
            }
        }

        private List<ProjectileBase> CaptureNewSinglePlayerCommitProjectiles(HashSet<int> existing)
        {
            var result = new List<ProjectileBase>();
            foreach (var root in _scene.GetRootGameObjects())
            {
                if (existing.Contains(root.GetInstanceID())) continue;
                var projectile = root.GetComponent<ProjectileBase>();
                if (projectile != null) result.Add(projectile);
            }
            return result;
        }
    }
}
#endif
