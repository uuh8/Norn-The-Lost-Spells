using System;
using Game.Materials;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class FluidChunkStreamingRuntimeTests
    {
        private GameObject _owner;
        private FluidChunkStreamingRuntime _runtime;
        private FakeBackend _backend;
        private FakeDepositSink _downstream;
        private FakeReadbackScheduler _readback;
        private FakeRestoreStatusScheduler _restoreStatus;
        private FakeGameplay _gameplay;

        [SetUp]
        public void SetUp()
        {
            _owner = new GameObject("FluidChunkStreamingRuntimeTests");
            _runtime = _owner.AddComponent<FluidChunkStreamingRuntime>();
            _backend = new FakeBackend(4);
            _downstream = new FakeDepositSink();
            _readback = new FakeReadbackScheduler();
            _restoreStatus = new FakeRestoreStatusScheduler();
            _gameplay = new FakeGameplay();
            var settings = new FluidChunkStreamingSettings(
                true, 0, 1f, 8, 16, 4, 1, true, 1);
            Assert.That(_runtime.TryInitialize(
                in settings, _backend, _downstream, _readback, _restoreStatus, _gameplay, _gameplay,
                Vector3.zero, 1f, 2, 0, 0), Is.True);
            _runtime.SetInterestChunkAt(new ElementChunkKey(0, 0, 0), 0d);
        }

        [TearDown]
        public void TearDown()
        {
            if (_owner != null) UnityEngine.Object.DestroyImmediate(_owner);
        }

        [Test]
        public void GraceDefersArchive_ThenCallbackCommitsOnlyOnNextTick()
        {
            _backend.FreeParticles = 4;
            _runtime.TickForTests(.99d);
            Assert.That(_backend.AcquireCount, Is.Zero);

            _runtime.TickForTests(1d);
            Assert.That(_backend.AcquireCount, Is.EqualTo(1));
            Assert.That(_runtime.State, Is.EqualTo(FluidChunkLifecycleState.Archiving));
            _readback.Complete(false, new FluidGpuArchiveSample(
                new Vector3(5.25f, .25f, .25f), MaterialId.Water, Vector3.right,
                FluidGpuLayout.AliveFlag | FluidActivityFlags.ArchiveLocked));

            Assert.That(_backend.CommitCount, Is.Zero,
                "Readback callback 只能登记结果，不能直接切换世界权威。");
            _runtime.TickForTests(1.1d);

            Assert.That(_backend.CommitCount, Is.EqualTo(1));
            Assert.That(_backend.ReleaseCount, Is.EqualTo(1));
            Assert.That(_runtime.ArchivedChunkCount, Is.EqualTo(1));
            Assert.That(_runtime.ArchivedCellRecordCount, Is.EqualTo(1));
            Assert.That(_runtime.ArchivedParticleCount, Is.EqualTo(1));
            Assert.That(_runtime.LastArchiveReadbackBytes,
                Is.EqualTo(4 * FluidGpuArchiveSample.Stride));
            Assert.That(_runtime.LastArchiveDurationMilliseconds, Is.GreaterThanOrEqualTo(0f));
            Assert.That(_runtime.CurrentTransactionId, Is.Zero);
            Assert.That(_runtime.State, Is.EqualTo(FluidChunkLifecycleState.Cold));
        }

        [Test]
        public void CapacityPressureStartsImmediately_AndReadbackErrorCancelsExactlyOnce()
        {
            _backend.FreeParticles = 1;
            _runtime.TickForTests(0d);
            Assert.That(_backend.AcquireCount, Is.EqualTo(1));

            _readback.Complete(true);
            Assert.That(_backend.CancelCount, Is.Zero);
            _runtime.TickForTests(.1d);

            Assert.That(_backend.CancelCount, Is.EqualTo(1));
            Assert.That(_backend.ReleaseCount, Is.EqualTo(1));
            Assert.That(_backend.CommitCount, Is.Zero);
            Assert.That(_runtime.ArchiveReadbackErrorCount, Is.EqualTo(1));
            Assert.That(_runtime.ArchiveRollbackCount, Is.EqualTo(1));
            Assert.That(_runtime.CapacityPressureCount, Is.EqualTo(1));
        }

        [Test]
        public void RequestedMaterialAuditWaitsUntilReactionLedgerIsPrepared()
        {
            _backend.AuditPreparationReady = false;
            _runtime.RequestMaterialAudit(MaterialId.Water, 1u, 0d);

            _runtime.TickForTests(.1d);

            Assert.That(_backend.AuditPreparationCount, Is.EqualTo(1));
            Assert.That(_backend.AcquireCount, Is.Zero);

            _backend.AuditPreparationReady = true;
            _runtime.TickForTests(.2d);

            Assert.That(_backend.AuditPreparationCount, Is.EqualTo(2));
            Assert.That(_backend.AcquireCount, Is.EqualTo(1));
        }

        [Test]
        public void ArchiveInFlight_DelaysRemoteWriteButForwardsWarmWrite()
        {
            _backend.FreeParticles = 0;
            _runtime.TickForTests(0d);

            var remote = new ElementWriteRequest(
                new Vector3(5f, 0f, 0f), MaterialId.Poison, 16, .1f, false);
            var warm = new ElementWriteRequest(
                new Vector3(.5f, .5f, .5f), MaterialId.Water, 8, .1f, false);

            Assert.That(_runtime.TryEnqueueDeposit(in remote), Is.True);
            Assert.That(_runtime.TryEnqueueDeposit(in warm), Is.True);
            Assert.That(_runtime.PendingWriteCount, Is.EqualTo(1));
            Assert.That(_downstream.AcceptedCount, Is.EqualTo(1));
            Assert.That(_downstream.Last.MaterialKind, Is.EqualTo(MaterialId.Water));
        }

        [Test]
        public void BackendLeaseFailure_DoesNotCancelOrReleaseUnownedLease()
        {
            _backend.FreeParticles = 0;
            _backend.AllowAcquire = false;
            _runtime.TickForTests(0d);

            Assert.That(_backend.AcquireCount, Is.EqualTo(1));
            Assert.That(_backend.CancelCount, Is.Zero);
            Assert.That(_backend.ReleaseCount, Is.Zero);
            Assert.That(_runtime.State, Is.EqualTo(FluidChunkLifecycleState.GpuResident));
        }

        [Test]
        public void ColdChunkEnteringWarmRegion_RestoresAndRemovesArchiveAfterCommit()
        {
            _backend.FreeParticles = 4;
            _runtime.TickForTests(1d);
            _readback.Complete(false, new FluidGpuArchiveSample(
                new Vector3(5.25f, .25f, .25f), MaterialId.Water, Vector3.zero,
                FluidGpuLayout.AliveFlag | FluidActivityFlags.ArchiveLocked));
            _runtime.TickForTests(1.1d);
            Assert.That(_runtime.ArchivedChunkCount, Is.EqualTo(1));

            _runtime.SetInterestChunkAt(new ElementChunkKey(2, 0, 0), 1.2d);
            _runtime.TickForTests(1.2d);
            Assert.That(_backend.RestoreAcquireCount, Is.EqualTo(1));
            Assert.That(_backend.LastRestoreParticleCount, Is.EqualTo(1));
            Assert.That(_runtime.State, Is.EqualTo(FluidChunkLifecycleState.Restoring));

            _restoreStatus.Complete(new FluidGpuTransferStatus(
                1u, 3u, 1u, _backend.LastRestoreTransactionId), false);
            Assert.That(_backend.RestoreCommitCount, Is.Zero);
            _gameplay.Publish(5u);
            _runtime.TickForTests(1.3d);

            Assert.That(_backend.RestoreCommitCount, Is.EqualTo(1));
            Assert.That(_runtime.ArchivedChunkCount, Is.Zero);
            Assert.That(_runtime.State, Is.EqualTo(FluidChunkLifecycleState.GpuResident));
            Assert.That(_runtime.RestoredParticleCount, Is.EqualTo(1));
            Assert.That(_runtime.LastRestoreParticleCount, Is.EqualTo(1));
            Assert.That(_runtime.LastRestoreDurationMilliseconds, Is.GreaterThanOrEqualTo(0f));
        }

        [Test]
        public void Restore_KeepsArchiveAuthorityUntilGameplayTopologyCatchesUp()
        {
            ArchiveRemoteWater();
            _runtime.SetInterestChunkAt(new ElementChunkKey(2, 0, 0), 1.2d);
            _runtime.TickForTests(1.2d);
            _restoreStatus.Complete(new FluidGpuTransferStatus(
                1u, 3u, 1u, _backend.LastRestoreTransactionId), false);

            _gameplay.Publish(4u);
            _runtime.TickForTests(1.3d);
            Assert.That(_runtime.ArchivedChunkCount, Is.EqualTo(1));
            Assert.That(_runtime.TryGetAmount(new Vector3Int(5, 0, 0), MaterialId.Water,
                out byte archivedAmount), Is.True);
            Assert.That(archivedAmount, Is.EqualTo(8));

            _gameplay.Publish(5u);
            _runtime.TickForTests(1.4d);
            Assert.That(_runtime.ArchivedChunkCount, Is.Zero);
        }

        [Test]
        public void Restore_WaitsForGameplayHandoffBeforeStartingAnotherArchive()
        {
            ArchiveRemoteWater();
            int archivesBeforeRestore = _backend.AcquireCount;
            _runtime.SetInterestChunkAt(new ElementChunkKey(2, 0, 0), 1.2d);
            _runtime.TickForTests(1.2d);
            _restoreStatus.Complete(new FluidGpuTransferStatus(
                1u, 3u, 1u, _backend.LastRestoreTransactionId), false);
            _gameplay.Publish(4u);
            _backend.FreeParticles = 0;
            _runtime.TickForTests(1.3d);
            _runtime.TickForTests(3d);

            Assert.That(_backend.AcquireCount, Is.EqualTo(archivesBeforeRestore),
                "旧RAM镜像尚未删除时不能合入新Archive，否则RemoveChunk会误删新增材料。");
            Assert.That(_runtime.ArchivedChunkCount, Is.EqualTo(1));
            _gameplay.Publish(5u);
            _runtime.TickForTests(3.1d);
            Assert.That(_runtime.ArchivedChunkCount, Is.Zero);
            Assert.That(_backend.AcquireCount, Is.GreaterThan(archivesBeforeRestore),
                "Topology交接完成后应恢复正常压力归档，不能永久停止回收。");
        }

        [Test]
        public void ColdWrite_IsQueuedAndReplayedExactlyOnceAfterRestore()
        {
            ArchiveRemoteWater();
            var request = new ElementWriteRequest(
                new Vector3(5.25f, .25f, .25f), MaterialId.Poison, 16, .2f, true,
                Vector3.right, Vector3.up);
            Assert.That(_runtime.TryEnqueueDeposit(in request), Is.True);
            Assert.That(_downstream.AcceptedCount, Is.Zero);

            _runtime.SetInterestChunkAt(new ElementChunkKey(2, 0, 0), 1.2d);
            _runtime.TickForTests(1.2d);
            _restoreStatus.Complete(new FluidGpuTransferStatus(
                1u, 3u, 1u, _backend.LastRestoreTransactionId), false);
            _gameplay.Publish(5u);
            _runtime.TickForTests(1.3d);

            Assert.That(_downstream.AcceptedCount, Is.EqualTo(1));
            Assert.That(_downstream.Last.WorldPosition, Is.EqualTo(request.WorldPosition));
            Assert.That(_downstream.Last.InitialVelocity, Is.EqualTo(request.InitialVelocity));
            Assert.That(_downstream.Last.SurfaceNormal, Is.EqualTo(request.SurfaceNormal));
            _runtime.TickForTests(1.4d);
            Assert.That(_downstream.AcceptedCount, Is.EqualTo(1));
        }

        [Test]
        public void IdleTickAndWarmQuery_DoNotAllocateManagedMemory()
        {
            _gameplay.Publish(1u);
            _runtime.TickForTests(.1d);
            _runtime.TryGetAmount(Vector3Int.zero, MaterialId.Water, out _);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 256; i++)
            {
                _runtime.TickForTests(.1d);
                _runtime.TryGetAmount(Vector3Int.zero, MaterialId.Water, out _);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.That(allocated, Is.Zero);
        }

        [Test]
        public void PendingQueueOverflow_IsVisibleAndDoesNotOverwriteAcceptedRequests()
        {
            _backend.FreeParticles = 0;
            _runtime.TickForTests(0d);
            for (int i = 0; i < 4; i++)
            {
                var accepted = new ElementWriteRequest(
                    new Vector3(5f + i, 0f, 0f), MaterialId.Water, 8, .1f, false);
                Assert.That(_runtime.TryEnqueueDeposit(in accepted), Is.True);
            }
            var rejected = new ElementWriteRequest(
                new Vector3(9f, 0f, 0f), MaterialId.Poison, 8, .1f, false);

            Assert.That(_runtime.TryEnqueueDeposit(in rejected), Is.False);
            Assert.That(_runtime.PendingWriteCount, Is.EqualTo(4));
            Assert.That(_runtime.RejectedPendingWriteCount, Is.EqualTo(1));
        }

        [Test]
        public void DownstreamCapacityBackpressure_QueuesAndReplaysOriginalWrite()
        {
            _downstream.AllowAccept = false;
            var request = new ElementWriteRequest(
                new Vector3(.25f, .25f, .25f), MaterialId.Water, 8, .1f, false,
                Vector3.right, Vector3.up);

            Assert.That(_runtime.TryEnqueueDeposit(in request), Is.True);
            Assert.That(_runtime.PendingWriteCount, Is.EqualTo(1));
            Assert.That(_downstream.AcceptedCount, Is.Zero);

            _downstream.AllowAccept = true;
            _runtime.TickForTests(.1d);
            Assert.That(_runtime.PendingWriteCount, Is.Zero);
            Assert.That(_downstream.AcceptedCount, Is.EqualTo(1));
            Assert.That(_downstream.Last.WorldPosition, Is.EqualTo(request.WorldPosition));
            Assert.That(_downstream.Last.InitialVelocity, Is.EqualTo(request.InitialVelocity));
        }

        [Test]
        public void RestoreReadbackError_RollsBackAndKeepsArchiveAuthority()
        {
            ArchiveRemoteWater();
            _runtime.SetInterestChunkAt(new ElementChunkKey(2, 0, 0), 1.2d);
            _runtime.TickForTests(1.2d);

            _restoreStatus.Complete(default, true);
            _runtime.TickForTests(1.3d);

            Assert.That(_backend.RestoreRollbackCount, Is.EqualTo(1));
            Assert.That(_runtime.RestoreReadbackErrorCount, Is.EqualTo(1));
            Assert.That(_runtime.RestoreRollbackCount, Is.EqualTo(1));
            Assert.That(_runtime.ArchivedChunkCount, Is.EqualTo(1));
            Assert.That(_runtime.RestoredParticleCount, Is.Zero);
        }

        [Test]
        public void PressureArchive_RetainedSleepingChunkStaysDormantUntilWriteRequestsWake()
        {
            _backend.FreeParticles = 0;
            _runtime.TickForTests(0d);
            Assert.That(_backend.LastIncludeSleepingRetained, Is.True);
            uint sleeping = FluidActivityFlags.Alive | FluidActivityFlags.InterestActive
                | FluidActivityFlags.Sleeping | FluidActivityFlags.ArchiveLocked;
            _readback.Complete(false, new FluidGpuArchiveSample(
                new Vector3(.25f, .25f, .25f), MaterialId.Water, Vector3.zero, sleeping));
            _runtime.TickForTests(.1d);
            _runtime.TickForTests(.2d);
            Assert.That(_backend.RestoreAcquireCount, Is.Zero,
                "本地稳定表示若立即恢复，就无法释放同一战场的 particle slot。");

            var request = new ElementWriteRequest(
                new Vector3(.25f, .25f, .25f), MaterialId.Poison, 8, .1f, false);
            Assert.That(_runtime.TryEnqueueDeposit(in request), Is.True);
            _runtime.TickForTests(.3d);
            Assert.That(_backend.RestoreAcquireCount, Is.EqualTo(1));
            Assert.That(_runtime.PendingWriteCount, Is.EqualTo(1));
            Assert.That(_runtime.DormantWakeRequestCount, Is.EqualTo(1),
                "同一 Chunk 的待恢复请求必须去重，不能在 Replay 每 Tick 重复累计。");
        }

        [Test]
        public void PressureArchive_ArchivesSleepingSubsetWhenSameCellContainsAwakeParticle()
        {
            _backend.FreeParticles = 0;
            _runtime.TickForTests(0d);
            uint lockedSleeping = FluidActivityFlags.Alive | FluidActivityFlags.InterestActive
                | FluidActivityFlags.Sleeping | FluidActivityFlags.ArchiveLocked;
            _readback.Complete(false,
                new FluidGpuArchiveSample(
                    new Vector3(.25f, .25f, .25f), MaterialId.Water,
                    Vector3.zero, lockedSleeping),
                new FluidGpuArchiveSample(
                    new Vector3(.5f, .25f, .25f), MaterialId.Water,
                    Vector3.zero, FluidGpuLayout.ActiveFlag));

            _runtime.TickForTests(.1d);

            Assert.That(_backend.CancelCount, Is.Zero);
            Assert.That(_backend.CommitCount, Is.EqualTo(1));
            Assert.That(_backend.ExcludedArchiveParticleCount, Is.Zero);
            Assert.That(_runtime.DormantChunkCount, Is.EqualTo(1));
            Assert.That(_runtime.DormantAmount, Is.EqualTo(8u));
        }

        [Test]
        public void PressureArchive_CommitsSleepingCellBesideAwakeCellInSameChunk()
        {
            _backend.FreeParticles = 0;
            _runtime.TickForTests(0d);
            uint lockedSleeping = FluidActivityFlags.Alive | FluidActivityFlags.InterestActive
                | FluidActivityFlags.Sleeping | FluidActivityFlags.ArchiveLocked;
            _readback.Complete(false,
                new FluidGpuArchiveSample(
                    new Vector3(.25f, .25f, .25f), MaterialId.Water,
                    Vector3.zero, lockedSleeping),
                new FluidGpuArchiveSample(
                    new Vector3(1.25f, .25f, .25f), MaterialId.Water,
                    Vector3.zero, FluidGpuLayout.ActiveFlag));

            _runtime.TickForTests(.1d);

            Assert.That(_backend.ExcludedArchiveParticleCount, Is.Zero);
            Assert.That(_backend.CommitCount, Is.EqualTo(1));
            Assert.That(_runtime.DormantChunkCount, Is.EqualTo(1));
            Assert.That(_runtime.DormantCellRecordCount, Is.EqualTo(1));
        }

        [Test]
        public void PressureArchive_CommitsSleepingSubsetAndIndependentColdChunkTogether()
        {
            _backend.FreeParticles = 0;
            _runtime.TickForTests(0d);
            uint lockedSleeping = FluidActivityFlags.Alive | FluidActivityFlags.InterestActive
                | FluidActivityFlags.Sleeping | FluidActivityFlags.ArchiveLocked;
            _readback.Complete(false,
                new FluidGpuArchiveSample(
                    new Vector3(.25f, .25f, .25f), MaterialId.Water,
                    Vector3.zero, lockedSleeping),
                new FluidGpuArchiveSample(
                    new Vector3(.5f, .25f, .25f), MaterialId.Water,
                    Vector3.zero, FluidGpuLayout.ActiveFlag),
                new FluidGpuArchiveSample(
                    new Vector3(5.25f, .25f, .25f), MaterialId.Poison,
                    Vector3.zero, FluidGpuLayout.AliveFlag | FluidActivityFlags.ArchiveLocked));

            _runtime.TickForTests(.1d);

            Assert.That(_backend.ExcludedArchiveParticleCount, Is.Zero);
            Assert.That(_backend.CommitCount, Is.EqualTo(1));
            Assert.That(_backend.CancelCount, Is.Zero);
            Assert.That(_runtime.ArchivedChunkCount, Is.EqualTo(2));
            Assert.That(_runtime.ArchivedCellRecordCount, Is.EqualTo(2));
            Assert.That(_runtime.ArchivedParticleCount, Is.EqualTo(2));
            Assert.That(_runtime.DormantChunkCount, Is.EqualTo(1));
            Assert.That(_runtime.TryGetAmount(
                new Vector3Int(5, 0, 0), MaterialId.Poison, out _), Is.False,
                "Cold 离屏 Chunk 暂停 Gameplay，只有重新进入 Warm Region 后才参与查询。");
        }

        [Test]
        public void DormantAndGpuAmounts_MergePerCellWithoutDuplicateEnumeration()
        {
            _backend.FreeParticles = 0;
            _runtime.TickForTests(0d);
            uint lockedSleeping = FluidActivityFlags.Alive | FluidActivityFlags.InterestActive
                | FluidActivityFlags.Sleeping | FluidActivityFlags.ArchiveLocked;
            _readback.Complete(false, new FluidGpuArchiveSample(
                new Vector3(.25f, .25f, .25f), MaterialId.Water,
                Vector3.zero, lockedSleeping));
            _runtime.TickForTests(.1d);
            _gameplay.Amount = 4;
            _gameplay.SampleCell = Vector3Int.zero;
            _gameplay.Publish(2u);

            Assert.That(_runtime.TryGetAmount(
                Vector3Int.zero, MaterialId.Water, out byte amount), Is.True);
            Assert.That(amount, Is.EqualTo(12));
            var cells = new LiquidMaterialCellSample[4];
            int count = _runtime.CopyOccupiedCells(MaterialId.Water, cells);
            Assert.That(count, Is.EqualTo(1));
            Assert.That(cells[0].GlobalCell, Is.EqualTo(Vector3Int.zero));
            Assert.That(cells[0].Amount, Is.EqualTo(12));
        }

        [TestCase(0, 0, true)]
        [TestCase(1, 0, true)]
        [TestCase(-1, 0, true)]
        [TestCase(1, 1, false)]
        [TestCase(2, 0, false)]
        public void LiquidContact_WakesDormantReactionPartnerOnlyWithinAxialContact(
            int x, int z, bool expectedWake)
        {
            _backend.FreeParticles = 0;
            _runtime.TickForTests(0d);
            uint flags = FluidActivityFlags.Alive | FluidActivityFlags.InterestActive
                | FluidActivityFlags.Sleeping | FluidActivityFlags.ArchiveLocked;
            _readback.Complete(false, new FluidGpuArchiveSample(
                new Vector3(.25f, .25f, .25f), MaterialId.Sticky, Vector3.zero, flags));
            _runtime.TickForTests(.1d);
            _gameplay.Amount = 8;
            _gameplay.SampleCell = new Vector3Int(x, 0, z);
            _gameplay.Publish(2u);

            Assert.That(_runtime.DormantAmount, Is.EqualTo(8u), "先确认测试已建立RAM Sticky权威。");
            _runtime.RequestLiquidReactionContactWakes(MaterialId.Water, MaterialId.Sticky);

            Assert.That(_runtime.DormantWakeRequestCount, Is.EqualTo(expectedWake ? 1u : 0u));
            Assert.That(_runtime.DormantAmount, Is.EqualTo(8u),
                "接触检查只排队Restore，不能提前消费或删除RAM权威。");
        }

        [Test]
        public void LiquidContact_DoesNotWakeUnrelatedDormantMaterial()
        {
            _backend.FreeParticles = 0;
            _runtime.TickForTests(0d);
            uint flags = FluidActivityFlags.Alive | FluidActivityFlags.InterestActive
                | FluidActivityFlags.Sleeping | FluidActivityFlags.ArchiveLocked;
            _readback.Complete(false, new FluidGpuArchiveSample(
                new Vector3(.25f, .25f, .25f), MaterialId.Poison, Vector3.zero, flags));
            _runtime.TickForTests(.1d);
            _gameplay.Amount = 8;
            _gameplay.Publish(2u);
            _runtime.RequestLiquidReactionContactWakes(MaterialId.Water, MaterialId.Sticky);
            Assert.That(_runtime.DormantWakeRequestCount, Is.Zero);
        }

        [Test]
        public void SameMaterialWrite_CanOverlayDormantCellsWithoutRestore()
        {
            _backend.FreeParticles = 0;
            _runtime.TickForTests(0d);
            uint lockedSleeping = FluidActivityFlags.Alive | FluidActivityFlags.InterestActive
                | FluidActivityFlags.Sleeping | FluidActivityFlags.ArchiveLocked;
            _readback.Complete(false, new FluidGpuArchiveSample(
                new Vector3(.25f, .25f, .25f), MaterialId.Water,
                Vector3.zero, lockedSleeping));
            _runtime.TickForTests(.1d);
            var request = new ElementWriteRequest(
                new Vector3(.25f, .25f, .25f), MaterialId.Water, 8, .1f, false);

            Assert.That(_runtime.TryEnqueueDeposit(in request), Is.True);
            Assert.That(_downstream.AcceptedCount, Is.EqualTo(1));
            Assert.That(_runtime.PendingWriteCount, Is.Zero);
            Assert.That(_runtime.DormantWakeRequestCount, Is.Zero);
        }

        [Test]
        public void RestorePreparation_ExpandsAtMostConfiguredParticlesPerTick()
        {
            _backend.FreeParticles = 4;
            _runtime.TickForTests(1d);
            uint locked = FluidActivityFlags.Alive | FluidActivityFlags.ArchiveLocked;
            _readback.Complete(false,
                new FluidGpuArchiveSample(new Vector3(5.1f, .1f, .1f), MaterialId.Water, Vector3.zero, locked),
                new FluidGpuArchiveSample(new Vector3(5.2f, .1f, .1f), MaterialId.Water, Vector3.zero, locked),
                new FluidGpuArchiveSample(new Vector3(5.3f, .1f, .1f), MaterialId.Water, Vector3.zero, locked));
            _runtime.TickForTests(1.1d);
            _runtime.SetInterestChunkAt(new ElementChunkKey(2, 0, 0), 1.2d);

            _runtime.TickForTests(1.2d);
            Assert.That(_backend.RestoreAcquireCount, Is.Zero);
            _runtime.TickForTests(1.3d);
            Assert.That(_backend.RestoreAcquireCount, Is.Zero);
            _runtime.TickForTests(1.4d);
            Assert.That(_backend.RestoreAcquireCount, Is.EqualTo(1));
            Assert.That(_backend.LastRestoreParticleCount, Is.EqualTo(3));
        }

        private void ArchiveRemoteWater()
        {
            _backend.FreeParticles = 4;
            _runtime.TickForTests(1d);
            _readback.Complete(false, new FluidGpuArchiveSample(
                new Vector3(5.25f, .25f, .25f), MaterialId.Water, Vector3.zero,
                FluidGpuLayout.AliveFlag | FluidActivityFlags.ArchiveLocked));
            _runtime.TickForTests(1.1d);
            Assert.That(_runtime.ArchivedChunkCount, Is.EqualTo(1));
        }

        private sealed class FakeDepositSink : IFluidDepositSink
        {
            public bool IsFluidInitialized => true;
            public bool AllowAccept = true;
            public int AcceptedCount { get; private set; }
            public ElementWriteRequest Last { get; private set; }

            public bool TryEnqueueDeposit(in ElementWriteRequest request)
            {
                if (!AllowAccept) return false;
                Last = request;
                AcceptedCount++;
                return true;
            }
        }

        private sealed class FakeBackend : IFluidChunkTransferBackend, IFluidMaterialAuditPreparation
        {
            private readonly int _capacity;
            public uint FreeParticles;
            public uint AwakeParticles;
            public uint SleepingParticles = 4u;
            public bool HasValidActivity = true;
            public bool AllowAcquire = true;
            public int AcquireCount;
            public int CommitCount;
            public int CancelCount;
            public int ReleaseCount;
            public int RestoreAcquireCount;
            public int RestoreCommitCount;
            public int RestoreRollbackCount;
            public int ExcludedArchiveParticleCount;
            public int LastRestoreParticleCount;
            public uint LastRestoreTransactionId;
            public bool AuditPreparationReady = true;
            public int AuditPreparationCount;

            public FakeBackend(int capacity) { _capacity = capacity; FreeParticles = (uint)capacity; }
            public bool TryGetParticleCapacity(out int capacity) { capacity = _capacity; return true; }
            public bool TryGetFreeParticleCount(out uint free) { free = FreeParticles; return true; }
            public bool TryGetActivityCounts(out uint awake, out uint sleeping)
            {
                awake = AwakeParticles;
                sleeping = SleepingParticles;
                return HasValidActivity;
            }

            public bool TryPrepareMaterialAudit()
            {
                AuditPreparationCount++;
                return AuditPreparationReady;
            }

            public bool TryAcquireArchiveReadbackLease(
                Bounds retainedBounds, Vector3 worldOrigin, float cellSize, int chunkSize,
                bool includeSleepingRetained, uint transactionId, out FluidArchiveReadbackLease lease)
            {
                AcquireCount++;
                LastIncludeSleepingRetained = includeSleepingRetained;
                if (!AllowAcquire) { lease = default; return false; }
                var scales = LiquidMaterialAmountScaleSnapshot.Create(new[]
                {
                    new LiquidMaterialAmountScale(MaterialId.Water, 8),
                    new LiquidMaterialAmountScale(MaterialId.Poison, 8),
                    new LiquidMaterialAmountScale(MaterialId.Sticky, 8),
                });
                lease = new FluidArchiveReadbackLease(
                    null, _capacity, retainedBounds, worldOrigin, cellSize, chunkSize,
                    transactionId, FluidGpuLayout.LayoutVersion, 1u, scales);
                return true;
            }

            public bool LastIncludeSleepingRetained { get; private set; }

            public void CommitArchiveAndRelease(uint transactionId) { CommitCount++; }
            public bool TryExcludeArchiveParticleIndices(uint[] particleIndices, int count)
            {
                ExcludedArchiveParticleCount += count;
                return true;
            }
            public void CancelArchive(uint transactionId) { CancelCount++; }
            public bool TryAcquireRestoreStatusLease(
                NativeArray<FluidGpuRestoreParticle> particles, int particleCount,
                int gameplayReserveParticles, uint transactionId,
                out FluidRestoreStatusReadbackLease lease)
            {
                RestoreAcquireCount++;
                LastRestoreParticleCount = particleCount;
                LastRestoreTransactionId = transactionId;
                lease = new FluidRestoreStatusReadbackLease(null, particleCount, transactionId);
                return true;
            }
            public void CommitRestore(
                uint transactionId, int particleCount, out uint publishedTopologyVersion)
            {
                RestoreCommitCount++;
                publishedTopologyVersion = 5u;
            }
            public void RollbackRestore(uint transactionId, int particleCount) { RestoreRollbackCount++; }
            public void ReleaseTransferReadbackLease() { ReleaseCount++; }
        }

        private sealed class FakeReadbackScheduler : IFluidArchiveReadbackScheduler
        {
            private NativeArray<FluidGpuArchiveSample> _destination;
            private Action<bool> _completed;
            public bool IsPending { get; private set; }

            public void Request(
                ref NativeArray<FluidGpuArchiveSample> destination,
                GraphicsBuffer source,
                Action<bool> completed)
            {
                _destination = destination;
                _completed = completed;
                IsPending = true;
            }

            public void Complete(bool hasError, FluidGpuArchiveSample sample = default)
            {
                if (!IsPending) throw new InvalidOperationException("No readback is pending.");
                if (!hasError) _destination[0] = sample;
                Action<bool> callback = _completed;
                _completed = null;
                IsPending = false;
                callback(hasError);
            }

            public void Complete(bool hasError, params FluidGpuArchiveSample[] samples)
            {
                if (!IsPending) throw new InvalidOperationException("No readback is pending.");
                if (!hasError)
                {
                    for (int i = 0; i < samples.Length && i < _destination.Length; i++)
                        _destination[i] = samples[i];
                }
                Action<bool> callback = _completed;
                _completed = null;
                IsPending = false;
                callback(hasError);
            }

            public void WaitForCompletion() { }
        }

        private sealed class FakeRestoreStatusScheduler : IFluidRestoreStatusReadbackScheduler
        {
            private NativeArray<FluidGpuTransferStatus> _destination;
            private Action<bool> _completed;
            public bool IsPending { get; private set; }

            public void Request(
                ref NativeArray<FluidGpuTransferStatus> destination,
                GraphicsBuffer source,
                Action<bool> completed)
            {
                _destination = destination;
                _completed = completed;
                IsPending = true;
            }

            public void Complete(FluidGpuTransferStatus status, bool hasError)
            {
                _destination[0] = status;
                Action<bool> callback = _completed;
                _completed = null;
                IsPending = false;
                callback(hasError);
            }

            public void WaitForCompletion() { }
        }

        private sealed class FakeGameplay : ILiquidOccupancyReadOnly, IFluidGameplayTopologyReadOnly
        {
            public byte Amount;
            public Vector3Int SampleCell;
            public bool HasValidSnapshot { get; private set; }
            public Bounds SnapshotBounds => new Bounds(Vector3.zero, Vector3.one * 100f);
            public uint SnapshotVersion => TopologyVersion;
            public uint TopologyVersion { get; private set; }
            public void Publish(uint topology) { TopologyVersion = topology; HasValidSnapshot = true; }
            public bool TryGetAmount(Vector3Int cell, MaterialId material, out byte amount)
            { amount = Amount; return HasValidSnapshot; }
            public bool TryGetAmountUnitsPerParticle(MaterialId material, out uint units)
            { units = 8u; return true; }
            public int CopyOccupiedCells(MaterialId material, LiquidMaterialCellSample[] destination)
            {
                if (!HasValidSnapshot || Amount == 0 || destination.Length == 0) return 0;
                destination[0] = new LiquidMaterialCellSample(SampleCell, Amount);
                return 1;
            }
        }
    }
}
