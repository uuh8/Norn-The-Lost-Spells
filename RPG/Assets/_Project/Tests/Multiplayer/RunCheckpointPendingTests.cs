using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Shared = Game.NetworkProtocol;

namespace Game.Multiplayer.Tests
{
    public sealed class RunCheckpointPendingTests
    {
        [TestCase(400, "InvalidRunCheckpointJson")]
        [TestCase(409, "RunCheckpointCompleteInvalid")]
        [TestCase(409, "RunCheckpointProfileVersionConflict")]
        [TestCase(409, "InvalidCombatContainer")]
        [TestCase(413, "RunCheckpointPayloadTooLarge")]
        public void CheckpointRejectionDiagnosticKeepsOnlyHttpStatusAndAllowlistedCode(long status, string code)
        {
            // 响应可携带未知字段；诊断只提取已审阅的Code，绝不能把服务器正文/凭据拼进state或日志。
            string body = "{\"Code\":\"" + code + "\",\"Secret\":\"never-print-this\"}";
            Assert.That(RejectionDiagnostic(status, body), Is.EqualTo("CheckpointCandidateRejected/HTTP" + status + "/" + code));
        }

        [TestCase("unknown")]
        [TestCase("prefix")]
        [TestCase("newline")]
        [TestCase("wrong-case")]
        [TestCase("malformed")]
        [TestCase("missing")]
        [TestCase("oversized")]
        [TestCase("null")]
        public void CheckpointRejectionDiagnosticDoesNotEchoUntrustedOrUnboundedResponse(string defect)
        {
            string body = defect == "unknown" ? "{\"Code\":\"never-print-this\"}"
                : defect == "prefix" ? "{\"Code\":\"RunCheckpointCompleteInvalid/never-print-this\"}"
                : defect == "newline" ? "{\"Code\":\"RunCheckpointCompleteInvalid\\nnever-print-this\"}"
                : defect == "wrong-case" ? "{\"Code\":\"runcheckpointcompleteinvalid\"}"
                : defect == "malformed" ? "{never-print-this"
                : defect == "missing" ? "{\"Secret\":\"never-print-this\"}"
                : defect == "oversized" ? "{\"Code\":\"RunCheckpointCompleteInvalid\",\"Extra\":\"" + new string('x', 1024) + "\"}"
                : null;
            Assert.That(RejectionDiagnostic(409, body), Is.EqualTo("CheckpointCandidateRejected/HTTP409/UnrecognizedCode"));
        }

        private static string RejectionDiagnostic(long status, string body)
        {
            var method = typeof(RoomInstanceBootstrap).GetMethod("CheckpointRejectionDiagnostic", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "明确拒绝必须能保留受控错误码，不能只剩不可诊断的CandidateRejected。");
            return (string)method.Invoke(null, new object[] { status, body });
        }

        private static CapturedRunCheckpoint Value(ulong retirement = 17)
        {
            Assert.That(RunAssetCheckpointCapture.TryBuild(RunAssetCheckpointCaptureTests.Input(), out var value, out var error), Is.True, error);
            value.RetirementSequence = retirement; return value;
        }
        private static Shared.OnlineRunAssetCheckpointReceipt Receipt(CapturedRunCheckpoint value)
            => new Shared.OnlineRunAssetCheckpointReceipt { CheckpointId = value.Value.CheckpointId,
                InstanceId = value.Value.InstanceId, RunId = value.Value.RunId, RoomEpoch = value.Value.RoomEpoch,
                Sequence = value.Value.Sequence, PayloadHash = new string('f', 64) };
        private static Shared.OnlineRunAssetCheckpointHeadReply Head(CapturedRunCheckpoint value)
            => new Shared.OnlineRunAssetCheckpointHeadReply { Receipt = Receipt(value), IsComplete = true,
                // 模拟真实Unity读取HTTP JSON后的inline默认壳；比较器应只规范协议中明确缺失的可选状态。
                Checkpoint = JsonUtility.FromJson<Shared.OnlineRunAssetCheckpoint>(JsonUtility.ToJson(value.Value)) };

        [Test] public void PendingBodyStaysFixedAcrossLaterDtoChangesAndCannotBeReplacedBeforeConfirmation()
        {
            var pending = new RunCheckpointPending(); var source = Value();
            Assert.That(pending.TryPrepare(source, out var error), Is.True, error);
            string body = pending.Body; source.Value.Members[0].Wands[0].CurrentMana = 999;
            Assert.That(pending.Body, Is.EqualTo(body)); StringAssert.Contains("12.25", pending.Body);
            var replacement = Value(); replacement.Value.CheckpointId = new string('9', 32); replacement.Value.Sequence = 2;
            Assert.That(pending.TryPrepare(replacement, out _), Is.False); Assert.That(pending.Body, Is.EqualTo(body));
            Assert.That(pending.LastAcknowledgedRetirementSequence, Is.Zero);
        }
        [Test] public void ReceiptAloneCannotAcknowledgeRetirementsAndCompleteReorderedHeadCanConfirm()
        {
            var pending = new RunCheckpointPending(); var source = Value(17);
            Assert.That(pending.TryPrepare(source, out _), Is.True);
            Assert.That(pending.ObserveReceipt(Receipt(source)), Is.True);
            Assert.That(pending.HasPending, Is.True); Assert.That(pending.LastAcknowledgedRetirementSequence, Is.Zero);
            var head = Head(source); Array.Reverse(head.Checkpoint.Members);
            Assert.That(pending.ConfirmReadback(head), Is.True, "完整事实必须按值核对，SQL规范排序不构成资产变化。");
            Assert.That(pending.HasPending, Is.False); Assert.That(pending.LastConfirmedSequence, Is.EqualTo(1));
            Assert.That(pending.LastAcknowledgedRetirementSequence, Is.EqualTo(17));
        }
        [Test] public void AChangedSavedQuantityWithTheSameHeaderAndReceiptCannotBeAcknowledged()
        {
            var pending = new RunCheckpointPending(); var source = Value(); Assert.That(pending.TryPrepare(source, out _), Is.True);
            Assert.That(pending.ObserveReceipt(Receipt(source)), Is.True);
            var altered = Head(source); altered.Checkpoint.Members[0].Wands[0].CurrentMana += 1;
            Assert.That(pending.ConfirmReadback(altered), Is.False); Assert.That(pending.HasPending, Is.True);
            Assert.That(pending.LastAcknowledgedRetirementSequence, Is.Zero);
        }
        [TestCase("run")] [TestCase("epoch")] [TestCase("sequence")] [TestCase("hash")]
        public void ForeignOrInvalidReceiptDoesNotAdvanceThePendingCandidate(string changed)
        {
            var pending = new RunCheckpointPending(); var source = Value(); Assert.That(pending.TryPrepare(source, out _), Is.True);
            var receipt = Receipt(source);
            if (changed == "run") receipt.RunId = new string('0', 32);
            if (changed == "epoch") receipt.RoomEpoch++;
            if (changed == "sequence") receipt.Sequence++;
            if (changed == "hash") receipt.PayloadHash = "not-a-hash";
            Assert.That(pending.ObserveReceipt(receipt), Is.False); Assert.That(pending.HasPending, Is.True);
        }
        [TestCase(false)] [TestCase(true)]
        public void ChangedGoldOrCurrencyVersionCannotAcknowledgeTheCapturedFacts(bool revision)
        {
            var source = Value(); var pending = new RunCheckpointPending();
            Assert.That(pending.TryPrepare(source, out _), Is.True);
            Assert.That(pending.ObserveReceipt(Receipt(source)), Is.True);
            var changed = Head(source);
            if (revision) changed.Checkpoint.Members[0].Assets.CurrencyVersion++;
            else changed.Checkpoint.Members[0].Assets.Gold++;
            Assert.That(pending.ConfirmReadback(changed), Is.False);
            Assert.That(pending.LastAcknowledgedRetirementSequence, Is.Zero);
            Assert.That(pending.HasPending, Is.True);
            Assert.That(pending.ConfirmReadback(Head(source)), Is.True);
        }
        [Test] public void UnknownPostResultOrQueryFailureCannotBeTreatedAsADefinitiveRejection()
        {
            var pending = new RunCheckpointPending(); var source = Value(); Assert.That(pending.TryPrepare(source, out _), Is.True);
            Assert.That(pending.ResolveRejectedPost(0, 404), Is.False);
            Assert.That(pending.ResolveRejectedPost(409, 0), Is.False);
            Assert.That(pending.HasPending, Is.True);
            Assert.That(pending.ResolveRejectedPost(409, 404), Is.True);
            Assert.That(pending.HasPending, Is.False); Assert.That(pending.LastAcknowledgedRetirementSequence, Is.Zero);
            var replacement = Value(); replacement.Value.CheckpointId = new string('9', 32); replacement.Value.Sequence = 2;
            Assert.That(pending.TryPrepare(replacement, out _), Is.True);
        }
        [Test] public void AlreadyRecordedOrIncompleteHeadCannotBeDiscardedAsAnUnrecordedRequest()
        {
            var pending = new RunCheckpointPending(); var source = Value(); Assert.That(pending.TryPrepare(source, out _), Is.True);
            Assert.That(pending.ObserveReceipt(Receipt(source)), Is.True);
            Assert.That(pending.ResolveRejectedPost(409, 404), Is.False);
            var head = Head(source); head.IsComplete = false;
            Assert.That(pending.ConfirmReadback(head), Is.False); Assert.That(pending.HasPending, Is.True);
        }
        [Test] public void LargeWorldEpochSurvivesTheActualUnityJsonReadbackBoundary()
        {
            var pending = new RunCheckpointPending(); var source = Value();
            source.Value.World.WorldEpoch = ulong.MaxValue - 19;
            Assert.That(pending.TryPrepare(source, out _), Is.True);
            Assert.That(pending.ObserveReceipt(Receipt(source)), Is.True);
            var head = Head(source);
            Assert.That(head.Checkpoint.World.WorldEpoch, Is.EqualTo(ulong.MaxValue - 19));
            Assert.That(pending.ConfirmReadback(head), Is.True);
        }
    }
}
