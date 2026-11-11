using System;
using System.Reflection;
using Game.Core;
using NUnit.Framework;
using UnityEngine;

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
    }
}
