using System;
using System.Reflection;
using Game.Core;
using NUnit.Framework;
using UnityEngine;
using Shared = Game.NetworkProtocol;

namespace Game.Multiplayer.Tests
{
    /// <summary>检查真实CommitOwner深拷贝，而不是DescribeChunks字符串或Client可见Interest子集。</summary>
    public sealed class RunWorldCheckpointCaptureTests
    {
        private static WorldReplicationCoordinator Coordinator()
            => new WorldReplicationCoordinator(7, 9, 4, 2048, 100, 4, 100000);
        private static WorldReplicationCommitOwner Owner(WorldReplicationCoordinator value)
            => (WorldReplicationCommitOwner)typeof(WorldReplicationCoordinator)
                .GetField("_owner", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value);
        private static WorldReplicaBuffer Source(long tick, bool occupied = true)
        {
            var value = new WorldReplicaBuffer(100, 4, 100000);
            value.Geometries[0] = new WorldGridGeometry(WorldMaterialDomain.Liquid, Vector3.zero, 1, 16);
            if (occupied) value.Cells[0] = new WorldMaterialCellSample(WorldMaterialDomain.Liquid,
                WorldMaterialStorage.Dynamic, new Vector3Int(-1, 0, 0), 2, 17);
            value.Header = new WorldReplicaHeader(tick, occupied ? 1 : 0, 0, 1);
            return value;
        }
        private static object Read(object value, string name)
        {
            Assert.That(value, Is.Not.Null);
            var field = value.GetType().GetField(name); Assert.That(field, Is.Not.Null, name);
            return field.GetValue(value);
        }
        private static object MaterialChunk(object snapshot)
        {
            // 原Owner另保留同域0块的Geometry；这里按真实Key找负坐标材料块，不能假定数组首项就是材料。
            foreach (var chunk in (Array)Read(snapshot, "Chunks"))
                if ((int)Read(chunk, "X") == -1) return chunk;
            Assert.Fail("负坐标原生材料块丢失。"); return null;
        }
        private static bool Capture(WorldReplicationCoordinator source, string worldId, out object value)
        {
            var method = typeof(WorldReplicationCoordinator).GetMethod("TryCaptureCheckpoint");
            Assert.That(method, Is.Not.Null, "完整检查点必须读真实已提交payload，不能仅记录描述元数据。");
            var arguments = new object[] { worldId, null };
            bool success = (bool)method.Invoke(source, arguments); value = arguments[1]; return success;
        }

        [Test] public void UncapturedOrStoppedOwnerDoesNotPublishAnEmptyReplacement()
        {
            var source = Coordinator();
            Assert.That(Capture(source, "world-7", out var empty), Is.False); Assert.That(empty, Is.Null);
            Assert.That(Owner(source).TryCaptureCommit(Source(1)), Is.True); source.Stop();
            Assert.That(Capture(source, "world-7", out empty), Is.False); Assert.That(empty, Is.Null);
        }
        [Test] public void FullCommittedPayloadKeepsNativeAddressQuantityAndDistinctMaterialTick()
        {
            var source = Coordinator(); var owner = Owner(source);
            Assert.That(owner.TryCaptureCommit(Source(11)), Is.True);
            Assert.That(Capture(source, "world-7", out var snapshot), Is.True);
            Assert.That(Read(snapshot, "WorldId"), Is.EqualTo("world-7"));
            Assert.That(Read(snapshot, "WorldEpoch"), Is.EqualTo(7UL));
            Assert.That(Read(snapshot, "WorldTick"), Is.EqualTo(11L));
            Assert.That(Read(snapshot, "CommitSequence"), Is.EqualTo(owner.CommitSequence));
            Assert.That(Read(snapshot, "CanRestoreAuthorityRules"), Is.False);
            var chunks = (Array)Read(snapshot, "Chunks"); Assert.That(chunks.Length, Is.EqualTo(2));
            var chunk = MaterialChunk(snapshot);
            Assert.That(Read(chunk, "X"), Is.EqualTo(-1));
            Assert.That(Read(chunk, "ServerTick"), Is.EqualTo(11L));
            Assert.That(Read(chunk, "BaseRevision"), Is.EqualTo(0UL));
            var words = (uint[])Read(chunk, "Words");
            Assert.That(WorldChunkPayloadCodec.Validate(words, words.Length,
                new WorldChunkKey(WorldMaterialDomain.Liquid, -1, 0, 0)), Is.True);
            Assert.That(words[words.Length - 1], Is.EqualTo(17u));
            Assert.That(Read(chunk, "Checksum"), Is.EqualTo(WorldChunkPayloadCodec.ComputeChecksum(words, words.Length)));
        }
        [Test] public void CallerMutationCannotChangeLiveOwnerOrAnotherFrozenCandidate()
        {
            var source = Coordinator(); Assert.That(Owner(source).TryCaptureCommit(Source(4)), Is.True);
            Assert.That(Capture(source, "world-7", out var first), Is.True);
            var firstWords = (uint[])Read(MaterialChunk(first), "Words");
            firstWords[firstWords.Length - 1] = 999;
            Assert.That(Capture(source, "world-7", out var second), Is.True);
            var secondWords = (uint[])Read(MaterialChunk(second), "Words");
            Assert.That(secondWords[secondWords.Length - 1], Is.EqualTo(17u));
            Assert.That(firstWords, Is.Not.SameAs(secondWords));
        }
        [Test] public void HistoricalKeyRemainsAsTombstoneAfterMaterialDisappears()
        {
            var source = Coordinator(); var owner = Owner(source);
            Assert.That(owner.TryCaptureCommit(Source(4)), Is.True);
            Assert.That(Capture(source, "world-7", out var before), Is.True);
            Assert.That(owner.TryCaptureCommit(Source(5, false)), Is.True);
            Assert.That(Capture(source, "world-7", out var after), Is.True);
            var chunks = (Array)Read(after, "Chunks"); Assert.That(chunks.Length, Is.EqualTo(2));
            var chunk = MaterialChunk(after); Assert.That(Read(chunk, "IsResident"), Is.False);
            Assert.That(((uint[])Read(chunk, "Words")).Length, Is.Zero);
            Assert.That(Read(chunk, "ServerTick"), Is.EqualTo(5L));
            Assert.That((ulong)Read(after, "CommitSequence"), Is.GreaterThan((ulong)Read(before, "CommitSequence")));
            Assert.That(Read(MaterialChunk(before), "IsResident"), Is.True);
        }
        [Test] public void ARealEmptyCommitIsDifferentFromNoCapture()
        {
            var source = Coordinator(); var owner = Owner(source);
            var empty = Source(3, false); empty.Header = new WorldReplicaHeader(3, 0, 0, 0);
            Assert.That(owner.TryCaptureCommit(empty), Is.True);
            Assert.That(Capture(source, "world-7", out var snapshot), Is.True);
            Assert.That(Read(snapshot, "WorldTick"), Is.EqualTo(3L));
            Assert.That(Read(snapshot, "CommitSequence"), Is.EqualTo(0UL));
            Assert.That(((Array)Read(snapshot, "Chunks")).Length, Is.Zero);
        }
        [TestCase(null)] [TestCase("")] [TestCase(" ")]
        public void MissingWorldIdentityCannotProduceACompleteCheckpoint(string worldId)
        {
            var source = Coordinator(); Assert.That(Owner(source).TryCaptureCommit(Source(1)), Is.True);
            Assert.That(Capture(source, worldId, out var snapshot), Is.False); Assert.That(snapshot, Is.Null);
        }

        private static Shared.OnlineRunAssetCheckpointWorld CaptureWorld(WorldReplicationCoordinator coordinator)
        {
            Assert.That(Capture(coordinator, "world-7", out var value), Is.True);
            return (Shared.OnlineRunAssetCheckpointWorld)value;
        }
        private static bool ConfirmWorld(WorldReplicationCoordinator coordinator, Shared.OnlineRunAssetCheckpointWorld world)
        {
            var method = typeof(WorldReplicationCoordinator).GetMethod("ConfirmCheckpointWorld");
            Assert.That(method, Is.Not.Null, "只有完整读回确认过的World才能成为下次持久化连续性的锚点。");
            return (bool)method.Invoke(coordinator, new object[] { world });
        }
        private static Shared.OnlineRunAssetCheckpointChunk FindChunk(Shared.OnlineRunAssetCheckpointWorld world, int x)
        {
            foreach (var chunk in world.Chunks)
                if (chunk.Domain == (uint)WorldMaterialDomain.Liquid && chunk.X == x && chunk.Y == 0 && chunk.Z == 0) return chunk;
            return null;
        }
        private static bool RecordRetirement(WorldReplicationCoordinator coordinator, WorldChunkPayloadHeader terminal)
        {
            var method = typeof(WorldReplicationCoordinator).GetMethod("RecordCheckpointRetirement",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "持久化删除证明必须记录Owner真正退休前的terminal Header，不能推测版本。");
            return (bool)method.Invoke(coordinator, new object[] { terminal });
        }
        private static int RetirementWatchCount(WorldReplicationCoordinator coordinator)
        {
            var property = typeof(WorldReplicationCoordinator).GetProperty("CheckpointRetirementWatchCount",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(property, Is.Not.Null);
            return (int)property.GetValue(coordinator);
        }
        private static WorldChunkPayloadHeader RemoveAndRetireMaterial(WorldReplicationCoordinator coordinator,
            long tick, bool recordWitness = true)
        {
            var owner = Owner(coordinator);
            Assert.That(owner.TryCaptureCommit(Source(tick, false)), Is.True);
            for (int i = 0; i < owner.ChunkCount; i++)
                if (owner.GetChunkHeader(i).Key == new WorldChunkKey(WorldMaterialDomain.Liquid, -1, 0, 0))
                {
                    var terminal = owner.GetChunkHeader(i);
                    Assert.That(owner.TryRetireInactiveChunk(i, out _), Is.True);
                    if (recordWitness) Assert.That(RecordRetirement(coordinator, terminal), Is.True);
                    return terminal;
                }
            Assert.Fail("fixture的真实删除Key不存在，不能把未执行退休当成已验证。"); return default;
        }

        [Test]
        public void ConfirmedResidentMissingAfterTrustedRetirementProducesContinuityTombstone()
        {
            var coordinator = Coordinator(); var owner = Owner(coordinator);
            Assert.That(owner.TryCaptureCommit(Source(1)), Is.True);
            var confirmed = CaptureWorld(coordinator);
            Assert.That(ConfirmWorld(coordinator, confirmed), Is.True);
            var terminal = RemoveAndRetireMaterial(coordinator, 2);
            var candidate = CaptureWorld(coordinator); var proof = FindChunk(candidate, -1);
            Assert.That(proof, Is.Not.Null, "已确认的resident不能在下一份Head直接缺席；需要一次显式连续性墓碑。");
            Assert.That(proof.IsResident, Is.False); Assert.That(proof.Generation, Is.EqualTo(terminal.Generation));
            Assert.That(proof.Revision, Is.EqualTo(terminal.Revision)); Assert.That(proof.BaseRevision, Is.Zero);
            Assert.That(proof.SnapshotId, Is.EqualTo(candidate.CommitSequence));
            Assert.That(proof.ServerTick, Is.EqualTo(candidate.WorldTick)); Assert.That(proof.WordCount, Is.Zero);
            Assert.That(proof.Words, Is.Empty); Assert.That(proof.Checksum, Is.EqualTo(2166136261u));
            Assert.That(owner.ChunkCount, Is.EqualTo(1), "持久化连续性证明不能重新占运行时metadata槽。");
            Assert.That(owner.GetChunkHeader(0).IsResident, Is.True);
        }

        [Test]
        public void UnconfirmedCaptureDoesNotCreateAContinuityAnchor()
        {
            var coordinator = Coordinator(); var owner = Owner(coordinator);
            Assert.That(owner.TryCaptureCommit(Source(1)), Is.True);
            var offered = CaptureWorld(coordinator);
            RemoveAndRetireMaterial(coordinator, 2);
            var next = CaptureWorld(coordinator);
            Assert.That(FindChunk(next, -1), Is.Null);
            Assert.That(ConfirmWorld(coordinator, offered), Is.False, "旧offered即使曾真实捕获，也不能替代最近候选的完整读回。");
        }

        [Test]
        public void AnUnconfirmedOrAlteredTombstoneCannotAdvanceTheConfirmedAnchor()
        {
            var coordinator = Coordinator(); var owner = Owner(coordinator);
            Assert.That(owner.TryCaptureCommit(Source(1)), Is.True);
            Assert.That(ConfirmWorld(coordinator, CaptureWorld(coordinator)), Is.True);
            RemoveAndRetireMaterial(coordinator, 2);
            var unconfirmed = CaptureWorld(coordinator);
            Assert.That(FindChunk(unconfirmed, -1), Is.Not.Null);
            FindChunk(unconfirmed, -1).Revision++;
            Assert.That(ConfirmWorld(coordinator, unconfirmed), Is.False);
            Assert.That(owner.TryCaptureCommit(Source(3, false)), Is.True);
            var next = CaptureWorld(coordinator);
            Assert.That(FindChunk(next, -1), Is.Not.Null, "未确认/语义拒绝不能让下一次正文丢掉仍需要的删除证明。");
            Assert.That(FindChunk(next, -1).Revision, Is.EqualTo(2));
        }

        [Test]
        public void ConfirmedTombstoneCanDisappearFromTheNextCheckpoint()
        {
            var coordinator = Coordinator(); var owner = Owner(coordinator);
            Assert.That(owner.TryCaptureCommit(Source(1)), Is.True);
            Assert.That(ConfirmWorld(coordinator, CaptureWorld(coordinator)), Is.True);
            RemoveAndRetireMaterial(coordinator, 2);
            var removed = CaptureWorld(coordinator);
            Assert.That(FindChunk(removed, -1), Is.Not.Null);
            Assert.That(ConfirmWorld(coordinator, removed), Is.True);
            Assert.That(owner.TryCaptureCommit(Source(3, false)), Is.True);
            var next = CaptureWorld(coordinator);
            Assert.That(FindChunk(next, -1), Is.Null, "已读回确认的墓碑无需永久累积在持久化Rows中。");
            Assert.That(owner.ChunkCount, Is.EqualTo(1));
        }

        [TestCase("null")] [TestCase("world")] [TestCase("epoch")] [TestCase("words")]
        public void ConfirmationRejectsAnythingExceptTheExactLatestOfferedWorld(string defect)
        {
            var coordinator = Coordinator(); Assert.That(Owner(coordinator).TryCaptureCommit(Source(1)), Is.True);
            var offered = CaptureWorld(coordinator);
            var attempted = JsonUtility.FromJson<Shared.OnlineRunAssetCheckpointWorld>(JsonUtility.ToJson(offered));
            if (defect == "null") attempted = null;
            else if (defect == "world") attempted.WorldId = "another-world";
            else if (defect == "epoch") attempted.WorldEpoch++;
            else FindChunk(attempted, -1).Words[17]++;
            Assert.That(ConfirmWorld(coordinator, attempted), Is.False);
            Assert.That(ConfirmWorld(coordinator, offered), Is.True, "非法确认不能毒化之后的合法确认。");
        }

        [Test]
        public void ConfirmationAcceptsCanonicalReorderingButRejectsOlderOfferedWorld()
        {
            var coordinator = Coordinator(); var owner = Owner(coordinator);
            Assert.That(owner.TryCaptureCommit(Source(1)), Is.True);
            var old = CaptureWorld(coordinator);
            Assert.That(owner.TryCaptureCommit(Source(2)), Is.True);
            var current = CaptureWorld(coordinator);
            Assert.That(ConfirmWorld(coordinator, old), Is.False);
            Array.Reverse(current.Chunks);
            Assert.That(ConfirmWorld(coordinator, current), Is.True);
            Assert.That(ConfirmWorld(coordinator, old), Is.False);
        }

        [Test]
        public void OfferedWorldIdentityCannotBeSwitchedByAnExternalCaptureCall()
        {
            var coordinator = Coordinator(); Assert.That(Owner(coordinator).TryCaptureCommit(Source(1)), Is.True);
            var current = CaptureWorld(coordinator);
            Assert.That(Capture(coordinator, "another-world", out var rejected), Is.False);
            Assert.That(rejected, Is.Null);
            Assert.That(ConfirmWorld(coordinator, current), Is.True,
                "错误WorldId的外部调用不能替换最近offered或污染当前World锚点。");
        }

        [Test]
        public void CallerMutationAfterConfirmationCannotEraseThePrivateResidentAnchor()
        {
            var coordinator = Coordinator(); var owner = Owner(coordinator);
            Assert.That(owner.TryCaptureCommit(Source(1)), Is.True);
            var offered = CaptureWorld(coordinator);
            Assert.That(ConfirmWorld(coordinator, offered), Is.True);
            FindChunk(offered, -1).IsResident = false; FindChunk(offered, -1).Words[17] = 999;
            RemoveAndRetireMaterial(coordinator, 2);
            var next = CaptureWorld(coordinator);
            Assert.That(FindChunk(next, -1), Is.Not.Null); Assert.That(FindChunk(next, -1).IsResident, Is.False);
            Assert.That(FindChunk(next, -1).Revision, Is.EqualTo(2));
        }

        [Test]
        public void ACurrentOwnerKeyAlwaysWinsOverAnOldConfirmedResident()
        {
            var coordinator = Coordinator(); var owner = Owner(coordinator);
            Assert.That(owner.TryCaptureCommit(Source(1)), Is.True);
            Assert.That(ConfirmWorld(coordinator, CaptureWorld(coordinator)), Is.True);
            RemoveAndRetireMaterial(coordinator, 2);
            Assert.That(owner.TryCaptureCommit(Source(3)), Is.True);
            var recreated = CaptureWorld(coordinator); int count = 0;
            foreach (var chunk in recreated.Chunks) if (chunk.X == -1) count++;
            var live = FindChunk(recreated, -1);
            Assert.That(count, Is.EqualTo(1)); Assert.That(live.IsResident, Is.True);
            Assert.That(live.Generation, Is.GreaterThan(1)); Assert.That(live.Words[17], Is.EqualTo(17));
        }

        [Test]
        public void ContinuityTombstoneRejectsRevisionOverflowWithoutChangingTheLiveOwner()
        {
            var coordinator = Coordinator(); var owner = Owner(coordinator);
            Assert.That(owner.TryCaptureCommit(Source(1)), Is.True);
            // 直接布置合法的版本耗尽值，避免运行ulong.MaxValue次提交；正文仍由真实Owner捕获/校验。
            var headers = (WorldChunkPayloadHeader[])typeof(WorldReplicationCommitOwner)
                .GetField("_headers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner);
            for (int i = 0; i < owner.ChunkCount; i++)
                if (headers[i].Key.X == -1)
                {
                    var h = headers[i];
                    headers[i] = new WorldChunkPayloadHeader(h.WorldEpoch, h.SnapshotId, h.Key, h.Generation,
                        h.BaseRevision, ulong.MaxValue, h.ServerTick, h.WordCount, h.Checksum, h.IsResident);
                }
            Assert.That(ConfirmWorld(coordinator, CaptureWorld(coordinator)), Is.True);
            RemoveAndRetireMaterial(coordinator, 2);
            ulong sequence = owner.CommitSequence; int count = owner.ChunkCount;
            Assert.That(Capture(coordinator, "world-7", out var rejected), Is.False);
            Assert.That(rejected, Is.Null); Assert.That(owner.CommitSequence, Is.EqualTo(sequence));
            Assert.That(owner.ChunkCount, Is.EqualTo(count)); Assert.That(coordinator.Faulted, Is.False);
        }

        [TestCase(false)] [TestCase(true)]
        public void ContinuityProofUsesTheActualTerminalVersionAfterUpdatesAndRecreation(bool recreate)
        {
            var coordinator = Coordinator(); var owner = Owner(coordinator);
            Assert.That(owner.TryCaptureCommit(Source(1)), Is.True);
            Assert.That(ConfirmWorld(coordinator, CaptureWorld(coordinator)), Is.True);
            for (int tick = 2; tick <= 4; tick++)
            {
                var updated = Source(tick);
                updated.Cells[0] = new WorldMaterialCellSample(WorldMaterialDomain.Liquid,
                    WorldMaterialStorage.Dynamic, new Vector3Int(-1, 0, 0), 2, (uint)(17 + tick));
                Assert.That(owner.TryCaptureCommit(updated), Is.True);
            }
            var terminal = RemoveAndRetireMaterial(coordinator, 5);
            if (recreate)
            {
                Assert.That(owner.TryCaptureCommit(Source(6)), Is.True);
                var updated = Source(7);
                updated.Cells[0] = new WorldMaterialCellSample(WorldMaterialDomain.Liquid,
                    WorldMaterialStorage.Dynamic, new Vector3Int(-1, 0, 0), 2, 31);
                Assert.That(owner.TryCaptureCommit(updated), Is.True);
                terminal = RemoveAndRetireMaterial(coordinator, 8);
            }
            var captured = CaptureWorld(coordinator); var proof = FindChunk(captured, -1);
            Assert.That(proof, Is.Not.Null); Assert.That(proof.IsResident, Is.False);
            Assert.That(proof.Generation, Is.EqualTo(terminal.Generation));
            Assert.That(proof.Revision, Is.EqualTo(terminal.Revision), "不能把上次Head的Revision+1伪装成本次真实删除版本。");
            Assert.That(proof.SnapshotId, Is.EqualTo(captured.CommitSequence));
            Assert.That(proof.ServerTick, Is.EqualTo(captured.WorldTick)); Assert.That(proof.BaseRevision, Is.Zero);
        }

        [Test]
        public void RetirementFloorAloneCannotInventADeletionWitnessForTheConfirmedKey()
        {
            var coordinator = Coordinator(); var owner = Owner(coordinator);
            Assert.That(owner.TryCaptureCommit(Source(1)), Is.True);
            Assert.That(ConfirmWorld(coordinator, CaptureWorld(coordinator)), Is.True);
            var terminal = RemoveAndRetireMaterial(coordinator, 2, false);
            Assert.That(owner.RetiredGenerationFloor, Is.GreaterThan(terminal.Generation));
            Assert.That(Capture(coordinator, "world-7", out var rejected), Is.False);
            Assert.That(rejected, Is.Null, "全局floor不是这个Key具体terminal版本的证据。");
            Assert.That(RecordRetirement(coordinator, terminal), Is.True);
            Assert.That(Capture(coordinator, "world-7", out _), Is.True);
        }

        [Test]
        public void RetirementWhileTheOfferedHeadAwaitsReadbackSurvivesConfirmation()
        {
            var coordinator = Coordinator(); var owner = Owner(coordinator);
            Assert.That(owner.TryCaptureCommit(Source(1)), Is.True);
            var offered = CaptureWorld(coordinator);
            var terminal = RemoveAndRetireMaterial(coordinator, 2);
            // 未发起第二次offered；HTTP读回仍在确认第一份冻结resident，而live已完成删除/退休。
            Assert.That(ConfirmWorld(coordinator, offered), Is.True);
            var candidate = CaptureWorld(coordinator); var proof = FindChunk(candidate, -1);
            Assert.That(proof, Is.Not.Null); Assert.That(proof.Generation, Is.EqualTo(terminal.Generation));
            Assert.That(proof.Revision, Is.EqualTo(terminal.Revision));
        }

        private static WorldReplicaBuffer BatchSource(long tick, int firstKey, int count)
        {
            var value = new WorldReplicaBuffer(128, 0, 1000000);
            value.Geometries[0] = new WorldGridGeometry(WorldMaterialDomain.Liquid, Vector3.zero, 1, 16);
            for (int i = 0; i < count; i++)
                value.Cells[i] = new WorldMaterialCellSample(WorldMaterialDomain.Liquid,
                    WorldMaterialStorage.Dynamic, new Vector3Int((firstKey + i) * 16, 0, 0), 2, 17);
            value.Header = new WorldReplicaHeader(tick, count, 0, 1); return value;
        }

        [Test]
        public void UnconfirmedMigrationKeepsOnlyTheConfirmedAndLatestOfferedResidentWatchSet()
        {
            var coordinator = new WorldReplicationCoordinator(7, 9, 128, 2048, 128, 0, 1000000);
            var owner = Owner(coordinator); long tick = 1;
            Assert.That(owner.TryCaptureCommit(BatchSource(tick, 1, 127)), Is.True);
            Assert.That(ConfirmWorld(coordinator, CaptureWorld(coordinator)), Is.True);
            Assert.That(RetirementWatchCount(coordinator), Is.EqualTo(128));
            Shared.OnlineRunAssetCheckpointWorld last = null;
            // 未确认期间经过1016个新地址；每组127个resident加同域Geometry块，实际逼近256 watch上界。
            for (int group = 1; group <= 8; group++)
            {
                Assert.That(owner.TryCaptureCommit(BatchSource(++tick, 0, 0)), Is.True);
                for (int i = owner.ChunkCount - 1; i >= 0; i--)
                {
                    var terminal = owner.GetChunkHeader(i); if (terminal.IsResident) continue;
                    Assert.That(owner.TryRetireInactiveChunk(i, out _), Is.True);
                    Assert.That(RecordRetirement(coordinator, terminal), Is.True);
                }
                Assert.That(owner.TryCaptureCommit(BatchSource(++tick, group * 127 + 1, 127)), Is.True);
                last = CaptureWorld(coordinator);
                Assert.That(RetirementWatchCount(coordinator), Is.EqualTo(255));
                Assert.That(last.Chunks.Length, Is.EqualTo(255)); Assert.That(owner.ChunkCount, Is.EqualTo(128));
            }
            Assert.That(ConfirmWorld(coordinator, last), Is.True);
            Assert.That(RetirementWatchCount(coordinator), Is.EqualTo(128), "读回确认后不再需要的旧resident witness应及时退出watch。");
        }
    }
}
