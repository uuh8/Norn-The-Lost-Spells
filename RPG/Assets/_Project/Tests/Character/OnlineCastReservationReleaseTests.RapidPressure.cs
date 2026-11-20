using System;
using Game.Core;
using Game.Run;
using NUnit.Framework;
using UnityEngine;

namespace Game.Character.Tests
{
    public sealed partial class OnlineCastReservationReleaseTests
    {
        [Test]
        public void WarmRapidReprogramAndThreeHundredTwentyLowManaCastsDoNotRetainInventoryLocks()
        {
            var actor = CreateActor(1, true, _dungeon.PlayerSpawn(0));
            ulong command = 0;
            // 先让 Draw/ExecutionRevision 经历真实出手，再正式改槽。不能只新建一个 Rev2 空状态。
            for (int i = 0; i < 3; i++)
            {
                AdvanceIdleSeconds(8.1f);
                Assert.That(CastNextAndSettle(actor, ++command).Result, Is.EqualTo(OnlineCastResult.Released));
            }
            ReprogramRapidFireToSlotFive(actor);
            int empty = 0, released = 0;
            for (int i = 0; i < 320; i++)
            {
                var terminal = CastNextAndSettle(actor, ++command);
                if (terminal.Result == OnlineCastResult.Released) released++;
                else
                {
                    Assert.That(terminal.Failure, Is.EqualTo(OnlineCastFailure.EmptyOutput), "压力例必须走实际零输出，不能以任意拒绝代替。");
                    empty++;
                }
                AssertReleasedResourcesAndFreshInventoryMove(actor, "低蓝轮次 " + i);
            }
            Assert.That(empty, Is.GreaterThanOrEqualTo(3));
            Assert.That(released, Is.GreaterThanOrEqualTo(1));
            AdvanceIdleSeconds(8.1f);
            Assert.That(State(actor).Mana, Is.EqualTo(120).Within(.001));
            AssertReleasedResourcesAndFreshInventoryMove(actor, "停手恢复");
        }

        [Test]
        public void RapidFinalFireAndFollowingModifierOnlyCastsReleaseEveryRealReservation()
        {
            var actor = CreateActor(1, true, _dungeon.PlayerSpawn(0));
            ReprogramRapidFireToSlotFive(actor);
            ulong command = 0;
            // 长按会被前置 Modifier 消耗在低蓝循环中，不能确保耗尽 Fire；此例每发先自然回满蓝。
            for (int remaining = 12; remaining >= 0; remaining--)
            {
                AdvanceIdleSeconds(8.1f);
                var terminal = CastNextAndSettle(actor, ++command);
                Assert.That(terminal.Result, Is.EqualTo(OnlineCastResult.Released), "Fire " + (remaining + 1) + "→" + remaining);
                if (remaining > 0) Assert.That(Charges(actor, actor.Fire), Is.EqualTo(remaining));
                else Assert.That(actor.Owner.TryGetItem(actor.Fire, out _), Is.False);
                AssertReleasedResourcesAndFreshInventoryMove(actor, "Fire余量 " + remaining);
            }
            Assert.That(_outputs.Count, Is.EqualTo(13)); Assert.That(_observations.Count, Is.EqualTo(13));
            Assert.That(State(actor).Program.Revision, Is.GreaterThan(2));
            for (int i = 0; i < 6; i++)
            {
                var terminal = CastNextAndSettle(actor, ++command);
                Assert.That(terminal.Result, Is.EqualTo(OnlineCastResult.Rejected));
                Assert.That(terminal.Failure, Is.EqualTo(OnlineCastFailure.EmptyOutput));
                AssertReleasedResourcesAndFreshInventoryMove(actor, "纯Modifier轮次 " + i);
            }
            Assert.That(_outputs.Count, Is.EqualTo(13), "纯Modifier不能凭空生成输出或重新发放已消耗卡。");
        }

        private void ReprogramRapidFireToSlotFive(Actor actor)
        {
            var state = State(actor);
            var edit = InventoryIntent(actor, OnlineInventoryCommandKind.PlaceCard, actor.Fire);
            edit.SourceWandId = edit.TargetWandId = actor.Wand;
            edit.SourceProgramRevision = edit.TargetProgramRevision = checked((uint)state.Program.Revision);
            edit.TargetSlot = 5;
            Assert.That(actor.Driver.ExecuteInventoryCommand(edit).Success, Is.True);
            Assert.That(State(actor).Program.Revision, Is.EqualTo(2));
            AdvanceIdleSeconds(.25f);
        }

        private void AdvanceIdleSeconds(float seconds)
        {
            for (int i = 0; i < Mathf.CeilToInt(seconds / TickSeconds); i++) StepAll(TickSeconds);
        }

        private OnlineCastReceipt CastNextAndSettle(Actor actor, ulong command)
        {
            var state = State(actor);
            var intent = new OnlineCastIntent { CommandId = command, ConnectionEpoch = 1,
                WandInstanceId = actor.Wand, ProgramRevision = checked((uint)state.Program.Revision),
                ClaimedOrigin = actor.Root.transform.position, AimPoint = actor.Root.transform.position + Vector3.forward * 20 };
            Assert.That(actor.Driver.TryQueue(intent, out var queued), Is.True, queued.Failure.ToString());
            // 队列可能还在等待上一轮 reload；用正式状态给有限上界，不人为清冷却/票据。
            float release = Mathf.Max(_definition.ReleaseSeconds(false), _definition.ReleaseSeconds(true));
            int budget = Mathf.CeilToInt((Mathf.Max(state.CastRemaining, state.ReloadRemaining) + release + 1) / TickSeconds);
            for (int i = 0; i < budget; i++)
            {
                StepAll(TickSeconds);
                for (int j = actor.Receipts.Count - 1; j >= 0; j--)
                    if (actor.Receipts[j].CommandId == command && actor.Receipts[j].Result != OnlineCastResult.Queued)
                        return actor.Receipts[j];
            }
            Assert.Fail("原Driver未在动作/冷却预算内给出终态，Command=" + command);
            return default;
        }

        private void AssertReleasedResourcesAndFreshInventoryMove(Actor actor, string stage)
        {
            Assert.That(actor.Driver.TryCaptureAuthorityInventoryDiagnostic(Math.Max(1u, _tick), out var snapshot), Is.True);
            Assert.That(snapshot.DriverBusyCaptured, Is.True);
            Assert.That(snapshot.TimelineActive, Is.False, stage);
            Assert.That(snapshot.Queued, Is.False, stage);
            Assert.That(snapshot.OwnerBusy.Captured, Is.True);
            Assert.That(snapshot.OwnerBusy.ItemsResourceBusy, Is.False, stage);
            Assert.That(snapshot.OwnerBusy.CastPending, Is.False, stage);
            Assert.That(snapshot.OwnerBusy.ActiveReservations, Is.Zero, stage);
            var move = MoveLoose(actor);
            Assert.That(move.Success, Is.True, stage + " 新MoveOwned=" + move.Failure);
        }
    }
}
