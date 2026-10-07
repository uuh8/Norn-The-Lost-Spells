using Game.Materials;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class FluidChunkArchiveStoreTests
    {
        [Test]
        public void Commit_WhenCapacityIsInsufficient_PreservesPreviousArchive()
        {
            var store = new FluidChunkArchiveStore(2, 1);
            Assert.That(store.TryCommit(Builder(MaterialId.Water, Vector3.zero), 1u), Is.True);
            Assert.That(store.TryCommit(Builder(MaterialId.Poison, new Vector3(8f, 0f, 0f)), 2u), Is.False);
            Assert.That(store.LastRejectionReason,
                Is.EqualTo(FluidArchiveStoreRejectionReason.RecordCapacity));
            Assert.That(store.TryGetAmount(Vector3Int.zero, MaterialId.Water, out uint amount), Is.True);
            Assert.That(amount, Is.EqualTo(8u));
            Assert.That(store.ChunkCount, Is.EqualTo(1));
        }

        [Test]
        public void RemoveChunk_TombstoneKeepsOtherRecordsQueryable()
        {
            var store = new FluidChunkArchiveStore(4, 4);
            Assert.That(store.TryCommit(Builder(MaterialId.Water, Vector3.zero), 1u), Is.True);
            Assert.That(store.TryCommit(Builder(MaterialId.Poison, new Vector3(8f, 0f, 0f)), 2u), Is.True);
            store.RemoveChunk(new ElementChunkKey(0, 0, 0));
            Assert.That(store.TryGetAmount(new Vector3Int(8, 0, 0), MaterialId.Poison, out uint amount), Is.True);
            Assert.That(amount, Is.EqualTo(4u));
        }

        [Test]
        public void Commit_ClassifiesRetainedChunkAsDormantAndAdvancesVersion()
        {
            var store = new FluidChunkArchiveStore(4, 4);
            var retained = new FluidChunkRegion(
                new ElementChunkKey(-1, -1, -1), new ElementChunkKey(1, 1, 1));

            Assert.That(store.TryCommit(
                Builder(MaterialId.Water, Vector3.zero), 7u, in retained), Is.True);
            Assert.That(store.TryGetChunkKind(
                new ElementChunkKey(0, 0, 0), out FluidArchivedChunkKind kind), Is.True);
            Assert.That(kind, Is.EqualTo(FluidArchivedChunkKind.Dormant));
            Assert.That(store.DormantChunkCount, Is.EqualTo(1));
            Assert.That(store.DormantRecordCount, Is.EqualTo(1));
            Assert.That(store.DormantAmount, Is.EqualTo(8u));
            Assert.That(store.Version, Is.EqualTo(1u));

            store.RemoveChunk(new ElementChunkKey(0, 0, 0));
            Assert.That(store.DormantChunkCount, Is.Zero);
            Assert.That(store.DormantRecordCount, Is.Zero);
            Assert.That(store.DormantAmount, Is.Zero);
            Assert.That(store.Version, Is.EqualTo(2u));
        }

        [Test]
        public void Commit_RepeatedDormantCellMergesAmountWithoutDuplicatingRecord()
        {
            var store = new FluidChunkArchiveStore(4, 4);
            var retained = new FluidChunkRegion(
                new ElementChunkKey(-1, -1, -1), new ElementChunkKey(1, 1, 1));

            Assert.That(store.TryCommit(
                Builder(MaterialId.Water, Vector3.zero), 1u, in retained), Is.True);
            Assert.That(store.TryCommit(
                Builder(MaterialId.Water, Vector3.zero), 2u, in retained), Is.True);

            Assert.That(store.TryGetAmount(
                Vector3Int.zero, MaterialId.Water, out uint amount), Is.True);
            Assert.That(amount, Is.EqualTo(16u));
            Assert.That(store.ChunkCount, Is.EqualTo(1));
            Assert.That(store.RecordCount, Is.EqualTo(1));
            Assert.That(store.DormantRecordCount, Is.EqualTo(1));
            Assert.That(store.DormantAmount, Is.EqualTo(16u));
            Assert.That(store.Version, Is.EqualTo(2u));
        }

        [Test]
        public void Commit_WhenActivityMovesAway_ReclassifiesDormantChunkAsColdAndMergesAmount()
        {
            var store = new FluidChunkArchiveStore(4, 4);
            var aroundOrigin = new FluidChunkRegion(
                new ElementChunkKey(-1, -1, -1), new ElementChunkKey(1, 1, 1));
            var farAway = new FluidChunkRegion(
                new ElementChunkKey(10, -1, -1), new ElementChunkKey(12, 1, 1));

            Assert.That(store.TryCommit(
                Builder(MaterialId.Water, Vector3.zero), 1u, in aroundOrigin), Is.True);
            Assert.That(store.TryCommit(
                Builder(MaterialId.Water, Vector3.zero), 2u, in farAway), Is.True);

            Assert.That(store.TryGetChunkKind(
                new ElementChunkKey(0, 0, 0), out FluidArchivedChunkKind kind), Is.True);
            Assert.That(kind, Is.EqualTo(FluidArchivedChunkKind.Cold));
            Assert.That(store.TryGetAmount(Vector3Int.zero, MaterialId.Water, out uint amount), Is.True);
            Assert.That(amount, Is.EqualTo(16u));
            Assert.That(store.DormantChunkCount, Is.Zero);
            Assert.That(store.DormantRecordCount, Is.Zero);
            Assert.That(store.DormantAmount, Is.Zero);
        }

        [Test]
        public void Commit_ColdChunkCannotBecomeDormantWithoutRestore()
        {
            var store = new FluidChunkArchiveStore(4, 4);
            var aroundOrigin = new FluidChunkRegion(
                new ElementChunkKey(-1, -1, -1), new ElementChunkKey(1, 1, 1));
            Assert.That(store.TryCommit(Builder(MaterialId.Water, Vector3.zero), 1u), Is.True);

            Assert.That(store.TryCommit(
                Builder(MaterialId.Water, Vector3.zero), 2u, in aroundOrigin), Is.False);
            Assert.That(store.LastRejectionReason,
                Is.EqualTo(FluidArchiveStoreRejectionReason.ChunkKindConflict));
        }

        [Test]
        public void MaterialConflictQuery_UsesActualCellRangeWithinChunk()
        {
            var store = new FluidChunkArchiveStore(4, 4);
            var retained = new FluidChunkRegion(
                new ElementChunkKey(-1, -1, -1), new ElementChunkKey(1, 1, 1));
            Assert.That(store.TryCommit(
                Builder(MaterialId.Water, Vector3.zero), 1u, in retained), Is.True);
            Assert.That(store.TryCommit(
                Builder(MaterialId.Poison, new Vector3(5f, 0f, 0f)), 2u, in retained), Is.True);

            var chunk = new ElementChunkKey(0, 0, 0);
            Assert.That(store.ContainsMaterialOtherThanInRange(
                chunk, Vector3Int.zero, Vector3Int.one, MaterialId.Water), Is.False);
            Assert.That(store.ContainsMaterialOtherThanInRange(
                chunk, new Vector3Int(4, 0, 0), new Vector3Int(6, 1, 1), MaterialId.Water), Is.True);
        }

        [Test]
        public void AccumulateMaterialAudit_AddsEveryOccupiedRecordExactlyOnce()
        {
            var store = new FluidChunkArchiveStore(4, 4);
            Assert.That(store.TryCommit(Builder(MaterialId.Water, Vector3.zero), 1u), Is.True);
            Assert.That(store.TryCommit(Builder(MaterialId.Poison, new Vector3(8f, 0f, 0f)), 2u), Is.True);
            var audit = new FluidMaterialAudit();
            audit.Begin(1, 2, store.Version);

            Assert.That(store.TryAccumulateMaterialAudit(audit), Is.True);

            Assert.That(audit.GetSnapshot(MaterialId.Water, 1, 2, store.Version).RamAuthorityGmu,
                Is.EqualTo(8u));
            Assert.That(audit.GetSnapshot(MaterialId.Poison, 1, 2, store.Version).RamAuthorityGmu,
                Is.EqualTo(4u));
        }

        private static FluidChunkArchiveBuilder Builder(MaterialId material, Vector3 position)
        {
            var samples = new NativeArray<FluidGpuArchiveSample>(1, Allocator.Temp);
            try
            {
                samples[0] = new FluidGpuArchiveSample(position, material, Vector3.zero,
                    FluidGpuLayout.AliveFlag | FluidActivityFlags.ArchiveLocked);
                var builder = new FluidChunkArchiveBuilder(1);
                var scales = LiquidMaterialAmountScaleSnapshot.Create(new[]
                {
                    new LiquidMaterialAmountScale(MaterialId.Water, 8u),
                    new LiquidMaterialAmountScale(MaterialId.Poison, 4u),
                });
                Assert.That(builder.TryRebuild(samples, new FluidArchiveBuildContext(Vector3.zero, 1f, 8, scales)), Is.True);
                return builder;
            }
            finally { samples.Dispose(); }
        }
    }
}
