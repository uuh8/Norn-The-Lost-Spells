using System.Reflection;
using Game.Core;
using Game.Materials;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    /// <summary>实际数量Store与只读motion sidecar对照；不能以插值好看替代数量和生命周期不变量。</summary>
    public sealed class WorldReplicaLiquidMotionTests
    {
        private WorldReplicaStore _store;
        private WorldReplicaLiquidReadModel _source;
        private LiquidWorldProfile _profile;
        private LiquidBehaviorProfile _water;
        private readonly Vector3Int _cell = new Vector3Int(1, 2, 1);
        private static readonly WorldChunkKey Key = new WorldChunkKey(WorldMaterialDomain.Liquid, 0, 0, 0);

        [SetUp]
        public void SetUp()
        {
            _profile = ScriptableObject.CreateInstance<LiquidWorldProfile>();
            _water = ScriptableObject.CreateInstance<LiquidBehaviorProfile>();
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":8,\"_maximumActiveCells\":256,\"_resolutionScale\":2,\"_unitVolumeMultiplier\":4}", _profile);
            typeof(LiquidWorldProfile).GetField("_materials", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_profile, new[] { _water });
            var geometry = new WorldGridGeometry(WorldMaterialDomain.Liquid, Vector3.zero, .5f, 4);
            _store = new WorldReplicaStore(7, 8, 256, 0, 256);
            Assert.That(_store.ConfigureGeometry(geometry), Is.True);
            Assert.That(_store.ConfigureMaterials(WorldMaterialDomain.Liquid, 510, new[] { (byte)MaterialId.Water }), Is.True);
            _source = new WorldReplicaLiquidReadModel(_store, geometry, _profile, .25f, null);
            _source.ResetConnection(3);
            Install(Key, 1, new WorldMaterialCellSample(WorldMaterialDomain.Liquid, WorldMaterialStorage.Resident, _cell, (byte)MaterialId.Water, 20));
        }

        [TearDown]
        public void TearDown() { Object.DestroyImmediate(_profile); Object.DestroyImmediate(_water); }

        [Test]
        public void FirstWindowSnapsThenContiguousMotionUsesTheSynchronizedPhase()
        {
            Assert.That(_source.TryPublish(Frame(1, 10), 10.025), Is.True);
            Assert.That(_source.TryGetPresentationAlpha(1, out float first), Is.True);
            Assert.That(first, Is.EqualTo(1), "首份历史不能让已经显示的Baseline倒退。下一连续窗口才正常插值。");
            Assert.That(_source.TryPublish(Frame(2, 10.1), 10.125), Is.True);
            Assert.That(_source.TryGetPresentationAlpha(2, out float alpha), Is.True);
            Assert.That(alpha, Is.EqualTo(.25f).Within(.0001f));
            Assert.That(_source.TryGetPresentationSample(_cell, MaterialId.Water, false, out var sample), Is.True);
            Assert.That(sample.OffsetCells, Is.EqualTo(new Vector3(0, -.2f, 0)));
            Assert.That(sample.VelocityWorld, Is.EqualTo(Vector3.down));
            Assert.That(_store.TotalQuantity(WorldMaterialDomain.Liquid), Is.EqualTo(20UL));
        }

        [Test]
        public void QuantityChangeImmediatelyRemovesMotionHistoryInsteadOfDrawingCollectedWater()
        {
            PublishMovingWindow(); uint before = _source.PresentationRevision;
            Install(Key, 2, new WorldMaterialCellSample(WorldMaterialDomain.Liquid, WorldMaterialStorage.Resident, _cell, (byte)MaterialId.Water, 7));
            Assert.That(_source.TryGetPresentationSample(_cell, MaterialId.Water, true, out var sample), Is.True);
            Assert.That(sample.Quantity, Is.EqualTo(7)); Assert.That(sample.OffsetCells, Is.EqualTo(Vector3.zero));
            Assert.That(_source.TryGetPresentationAlpha(2, out _), Is.False);
            Assert.That(_source.PresentationRevision, Is.Not.EqualTo(before));
            Assert.That(_store.TotalQuantity(WorldMaterialDomain.Liquid), Is.EqualTo(7UL));
        }

        [Test]
        public void UnrelatedCellRevisionDoesNotStarveAStillMatchingMotionWindow()
        {
            PublishMovingWindow();
            Install(Key, 2,
                new WorldMaterialCellSample(WorldMaterialDomain.Liquid, WorldMaterialStorage.Resident, _cell, (byte)MaterialId.Water, 20),
                new WorldMaterialCellSample(WorldMaterialDomain.Liquid, WorldMaterialStorage.Resident, new Vector3Int(2, 2, 1), (byte)MaterialId.Water, 9));
            _source.AdvancePresentation(10.15, false);
            Assert.That(_source.TryGetPresentationAlpha(2, out float alpha), Is.True);
            Assert.That(alpha, Is.EqualTo(.5f).Within(.0001f));
            Assert.That(_store.TotalQuantity(WorldMaterialDomain.Liquid), Is.EqualTo(29UL));
        }

        [Test]
        public void RejectedQuantityWitnessDoesNotReplaceThePublishedWindow()
        {
            PublishMovingWindow(); var invalid = Frame(3, 10.2);
            invalid.Rows[0] = Sample(_cell, 20, 21, 0);
            Assert.That(_source.TryPublish(invalid, 10.225), Is.False);
            Assert.That(_source.PresentationWindowId, Is.EqualTo(2));
            Assert.That(_source.TryGetPresentationAlpha(2, out float alpha), Is.True);
            Assert.That(alpha, Is.EqualTo(.25f).Within(.0001f));
        }

        [Test]
        public void CrossChunkHistoryKeepsAnEmptySourceOnlyAfterItsRealTombstoneIsInstalled()
        {
            var targetCell = new Vector3Int(4, 2, 1); var target = new WorldChunkKey(WorldMaterialDomain.Liquid, 1, 0, 0);
            Tombstone(Key, 2); Install(target, 1, new WorldMaterialCellSample(WorldMaterialDomain.Liquid, WorldMaterialStorage.Resident, targetCell, (byte)MaterialId.Water, 20));
            var frame = Frame(1, 10); frame.AnchorCount = 2; frame.RowCount = 2;
            frame.Anchors[0] = new WorldLiquidMotionAnchor(Key, 1, 2, 1, false);
            frame.Anchors[1] = new WorldLiquidMotionAnchor(target, 1, 1, 1, true);
            frame.Rows[0] = Sample(_cell, 20, 0, 0); frame.Rows[1] = Sample(targetCell, 0, 20, 1);
            Assert.That(_source.TryPublish(frame, 10), Is.True);
            var changed = new Vector3Int[4]; Assert.That(_source.CopyTransportChangedCells(changed), Is.EqualTo(2));
            CollectionAssert.Contains(changed, _cell);
            Assert.That(_source.TryGetPresentationSample(_cell, MaterialId.Water, true, out var old), Is.True);
            Assert.That(old.Quantity, Is.EqualTo(20));
            Assert.That(_source.TryGetPresentationSample(_cell, MaterialId.Water, false, out _), Is.False);
            Assert.That(_store.TotalQuantity(WorldMaterialDomain.Liquid), Is.EqualTo(20UL));
        }

        [Test]
        public void InterestEvictionDoesNotPretendToBeAnAuthoritativeEmptyChunk()
        {
            Assert.That(_store.TryEvictInterest(Key), Is.True);
            var frame = Frame(1, 10); frame.Anchors[0] = new WorldLiquidMotionAnchor(Key, 1, 1, 1, false);
            frame.Rows[0] = Sample(_cell, 20, 0, 0);
            Assert.That(_source.TryPublish(frame, 10), Is.False);
            Assert.That(_source.PresentationWindowId, Is.Zero);
        }

        [Test]
        public void FrozenPlaybackHoldsItsPhaseAndResumeDoesNotReplayTheElapsedDebt()
        {
            PublishMovingWindow(); _source.AdvancePresentation(10.15, true);
            Assert.That(_source.TryGetPresentationAlpha(2, out float frozen), Is.True);
            _source.AdvancePresentation(20, true);
            Assert.That(_source.TryGetPresentationAlpha(2, out float later), Is.True);
            Assert.That(later, Is.EqualTo(frozen));
            _source.AdvancePresentation(20.05, false);
            Assert.That(_source.TryGetPresentationAlpha(2, out _), Is.False, "恢复只接受新的实时窗口，不追赶冻结期间的历史运动。");
        }

        [Test]
        public void ExpiredMotionFallsBackToCurrentQuantityWithoutContinuingExtrapolation()
        {
            PublishMovingWindow(); _source.AdvancePresentation(10.5, false);
            Assert.That(_source.TryGetPresentationAlpha(2, out _), Is.False);
            Assert.That(_source.TryGetPresentationSample(_cell, MaterialId.Water, true, out var sample), Is.True);
            Assert.That(sample.Quantity, Is.EqualTo(20)); Assert.That(sample.VelocityWorld, Is.EqualTo(Vector3.zero));
        }

        [Test]
        public void ResetConnectionClearsHistoryAndRejectsThePreviousConnectionEpoch()
        {
            PublishMovingWindow(); _source.ResetConnection(4);
            Assert.That(_source.PresentationWindowId, Is.Zero);
            Assert.That(_source.TryPublish(Frame(3, 10.2), 10.225), Is.False);
            var current = Frame(1, 20); current.ConnectionEpoch = 4;
            Assert.That(_source.TryPublish(current, 20), Is.True);
        }

        [Test]
        public void PublishedMotionOwnsItsCopyAndDoesNotReadTheReusedNetworkBuffer()
        {
            var first = Frame(1, 10); Assert.That(_source.TryPublish(first, 10), Is.True);
            var next = Frame(2, 10.1); Assert.That(_source.TryPublish(next, 10.125), Is.True);
            next.Rows[0] = Sample(_cell, 1, 1, 0); next.RowCount = 0; next.Sequence = 200;
            Assert.That(_source.TryGetPresentationSample(_cell, MaterialId.Water, true, out var sample), Is.True);
            Assert.That(sample.Quantity, Is.EqualTo(20)); Assert.That(_source.PresentationWindowId, Is.EqualTo(2));
        }

        private void PublishMovingWindow()
        { Assert.That(_source.TryPublish(Frame(1, 10), 10.025), Is.True); Assert.That(_source.TryPublish(Frame(2, 10.1), 10.125), Is.True); }

        private WorldLiquidMotionFrame Frame(ulong sequence, double sampleTime)
        {
            var frame = new WorldLiquidMotionFrame { WorldEpoch = 7, ConnectionEpoch = 3, Sequence = sequence,
                MaterialTick = (uint)(100 + sequence), SampleTime = sampleTime, Interval = .1f, ChunkSize = 4, AnchorCount = 1, RowCount = 1 };
            frame.Anchors[0] = new WorldLiquidMotionAnchor(Key, 1, 1, 1, true);
            frame.Rows[0] = Sample(_cell, 20, 20, 0); return frame;
        }

        private static WorldLiquidMotionSample Sample(Vector3Int cell, uint previous, uint current, int anchor)
            => new WorldLiquidMotionSample(cell, (byte)MaterialId.Water, previous, current,
                previous > 0 ? new Vector3(0, -.1f, 0) : Vector3.zero, previous > 0 ? Vector3.down : Vector3.zero,
                current > 0 ? new Vector3(0, -.2f, 0) : Vector3.zero, current > 0 ? Vector3.down : Vector3.zero, anchor);

        private void Install(WorldChunkKey key, ulong revision, params WorldMaterialCellSample[] cells)
        {
            var words = new uint[11 + cells.Length * WorldChunkPayloadCodec.CellWords];
            words[0] = WorldChunkPayloadCodec.Magic; words[1] = (uint)cells.Length; words[3] = 1;
            WorldChunkPayloadCodec.WriteGeometry(words, 4, new WorldGridGeometry(WorldMaterialDomain.Liquid, Vector3.zero, .5f, 4));
            for (int i = 0; i < cells.Length; i++) WorldChunkPayloadCodec.WriteCell(words, 11 + i * WorldChunkPayloadCodec.CellWords, cells[i]);
            var header = new WorldChunkPayloadHeader(7, revision, key, 1, 0, revision, 100,
                words.Length, WorldChunkPayloadCodec.ComputeChecksum(words, words.Length), true);
            Assert.That(_store.TryInstall(header, words, words.Length), Is.True);
        }

        private void Tombstone(WorldChunkKey key, ulong revision)
        {
            var header = new WorldChunkPayloadHeader(7, revision, key, 1, 0, revision, 100, 0, 2166136261u, false);
            Assert.That(_store.TryInstall(header, null, 0), Is.True);
        }
    }
}
