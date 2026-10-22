using System;
using Unity.Profiling;
using UnityEngine;

namespace Game.ElementField
{
    public enum ElementPerformancePhase { Idle, Warmup, Prefill, Settle, Measure, Drain, Finished }

    public struct ElementPerformanceFrame
    {
        public int Frame;
        public double At;
        public ElementPerformancePhase Phase;
        public bool Focused;
        public PerformanceMetricSample IntervalMs, MainMs, RenderMs, GcBytes;
        public PerformanceMetricSample ArchiveBuildMs, ArchiveCommitMs, MaterialAuditMs;
        public PerformanceMetricSample ArchiveExcludeMs, ArchiveActiveCellsMs, ArchiveMixedSelectionMs;
        public PerformanceMetricSample ArchiveUnlockUploadMs, ArchiveUnlockDispatchMs, ArchiveRebuildMs, ArchiveStoreMs;
        public int WorldTicks, FluidTicks, PendingSpawns, ResidentChunks, ActiveChunks;
        public ulong Submitted, WorldDebt, FluidDebt, ProcessedWrites, RejectedWrites, ReactionPairs;
        public int ArchivedChunks, ArchivedRecords, PendingStreamingWrites;
        public ulong ArchivedParticles, RestoredParticles;
        public int DormantChunks, DormantRecords, RestorePreparedParticles;
        public ulong DormantAmount;
        public bool RestorePreparing;
        public uint LocalDormancyArchives, DormantWakeRequests;
        public FluidDormantWakeReason LastDormantWakeReason;
        public int RetainedAliveSamples, RetainedSpeedBelow01, RetainedSpeedBelow05;
        public int RetainedSpeedBelow10, RetainedSpeedBelow25;
        public float RetainedMaximumSpeed;
        public uint ArchiveTransactions, RestoreTransactions, ArchiveErrors, RestoreErrors;
        public uint ArchiveRollbacks, RestoreRollbacks, RejectedPendingWrites, CapacityPressure;
        public int LastArchiveBytes, LastRestoreParticles;
        public int LastArchiveLockedParticles, LastArchiveRecords;
        public FluidArchiveRollbackReason LastArchiveRollbackReason;
        public FluidArchiveStoreRejectionReason LastArchiveStoreRejectionReason;
        public float LastArchiveMs, LastRestoreMs;
        public GpuFluidPoolDiagnostics Pool;
        public FluidMaterialAuditSnapshot WaterAudit, PoisonAudit, StickyAudit;
        public GpuFluidActivityDiagnostics Activity;
        public FluidGameplayDiagnostics Gameplay;
        public int Colliders, Targets;
        public bool ExposureSaturated;
        public float Burning, Wet, Poisoned, Sticky;
        public byte ProbeWater;
        public int ActivityRegions, ActivitySources, RejectedRegions, PendingWakes;
        public int UnitSourcesSkippedByBudget, ProjectileSourcesSkippedByBudget;
        public byte MeasureFire, MeasureWater;
        public long TotalAllocatedMemory, TotalReservedMemory, MonoHeapMemory;
        public long MonoUsedMemory, GraphicsDriverMemory;
    }

    public struct ElementPerformanceGpuFrame
    {
        public ulong Timestamp;
        public ulong FirstSubmitTimestamp, CpuTimePresentCalled, CpuTimeFrameComplete;
        public double ReceivedAt;
        public ElementPerformancePhase PhaseAtReceipt;
        public double CpuMs, CpuMainThreadMs, CpuPresentWaitMs, CpuRenderThreadMs, GpuMs;
        public uint SyncInterval;
        public bool GpuValid;
    }

    public struct ElementPerformanceWriteResult
    {
        public ElementPerformancePhase Phase;
        public ElementPerformanceWrite Write;
        public double At, LatenessSeconds;
        public bool Accepted;
    }

    /// <summary>固定容量原始数据仓库。写满时标记不完整并停止，保留最早的失败证据。</summary>
    public sealed class ElementWorldPerformanceCapture : IDisposable
    {
        public readonly ElementPerformanceFrame[] Frames;
        public readonly ElementPerformanceGpuFrame[] GpuFrames;
        public readonly ElementPerformanceWriteResult[] Writes;
        public int FrameCount { get; private set; }
        public int GpuCount { get; private set; }
        public int WriteCount { get; private set; }
        public bool Running { get; private set; }
        public bool Incomplete { get; private set; }
        public bool TimingEnabled { get; private set; }
        public ulong CpuTimerFrequency { get; private set; }
        public ulong GpuTimerFrequency { get; private set; }
        public float VSyncsPerSecond { get; private set; }
        public int ValidGpuTimingCount { get; private set; }
        public int MissingGpuTimingCount { get; private set; }
        public string RecorderAvailability { get; private set; }
        private ProfilerRecorder _main, _render, _gc, _archiveBuild, _archiveCommit, _materialAudit;
        private ProfilerRecorder _archiveExclude, _archiveActiveCells, _archiveMixedSelection;
        private ProfilerRecorder _archiveUnlockUpload, _archiveUnlockDispatch, _archiveRebuild, _archiveStore;
        private readonly FrameTiming[] _timing = new FrameTiming[4];
        private ulong _latestTimestamp;
        private bool _enabled;
        private int _startedFrame;
        private double _previousAt;

        public ElementWorldPerformanceCapture(int frameCapacity = 262144, int eventCapacity = 16384)
        {
            if (frameCapacity <= 0 || eventCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(frameCapacity));
            Frames = new ElementPerformanceFrame[frameCapacity];
            GpuFrames = new ElementPerformanceGpuFrame[frameCapacity];
            Writes = new ElementPerformanceWriteResult[eventCapacity];
        }

        public void Start(bool enabled)
        {
            if (Running) throw new InvalidOperationException("采集已启动。");
            _enabled = enabled;
            Running = true;
            _startedFrame = Time.frameCount;
            _previousAt = Time.realtimeSinceStartupAsDouble;
            TimingEnabled = enabled && FrameTimingManager.IsFeatureEnabled();
            CpuTimerFrequency = TimingEnabled ? FrameTimingManager.GetCpuTimerFrequency() : 0u;
            GpuTimerFrequency = TimingEnabled ? FrameTimingManager.GetGpuTimerFrequency() : 0u;
            VSyncsPerSecond = TimingEnabled ? FrameTimingManager.GetVSyncsPerSecond() : 0u;
            if (enabled)
            {
                _main = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", 1);
                _render = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Render Thread", 1);
                _gc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", 1);
                _archiveBuild = ProfilerRecorder.StartNew(
                    ProfilerCategory.Scripts, "GpuFluid.Streaming.ArchiveBuild", 1);
                _archiveCommit = ProfilerRecorder.StartNew(
                    ProfilerCategory.Scripts, "GpuFluid.Streaming.ArchiveCommit", 1);
                _materialAudit = ProfilerRecorder.StartNew(
                    ProfilerCategory.Scripts, "GpuFluid.Streaming.MaterialAudit", 1);
                _archiveExclude = ProfilerRecorder.StartNew(
                    ProfilerCategory.Scripts, "GpuFluid.Streaming.ArchiveExclude", 1);
                _archiveActiveCells = ProfilerRecorder.StartNew(
                    ProfilerCategory.Scripts, "GpuFluid.Streaming.ArchiveActiveCells", 1);
                _archiveMixedSelection = ProfilerRecorder.StartNew(
                    ProfilerCategory.Scripts, "GpuFluid.Streaming.ArchiveMixedSelection", 1);
                _archiveUnlockUpload = ProfilerRecorder.StartNew(
                    ProfilerCategory.Scripts, "GpuFluid.Streaming.ArchiveUnlockUpload", 1);
                _archiveUnlockDispatch = ProfilerRecorder.StartNew(
                    ProfilerCategory.Scripts, "GpuFluid.Streaming.ArchiveUnlockDispatch", 1);
                _archiveRebuild = ProfilerRecorder.StartNew(
                    ProfilerCategory.Scripts, "GpuFluid.Streaming.ArchiveRebuild", 1);
                _archiveStore = ProfilerRecorder.StartNew(
                    ProfilerCategory.Scripts, "GpuFluid.Streaming.ArchiveStore", 1);
            }
            RecorderAvailability = $"Main Thread={_main.Valid}; Render Thread={_render.Valid}; "
                + $"GC Allocated In Frame={_gc.Valid}; FrameTiming={TimingEnabled}; "
                + $"CpuTimerHz={CpuTimerFrequency}; GpuTimerHz={GpuTimerFrequency}; VSyncHz={VSyncsPerSecond}; "
                + "CPU recorders=last completed frame; GPU frames carry independent timestamps and phase at receipt.";
        }

        public void RecordFrame(ref ElementPerformanceFrame frame)
        {
            if (!Running || !_enabled) return;
            if (FrameCount == Frames.Length) { FailCapacity(); return; }
            bool fullFrame = Time.frameCount > _startedFrame + 1;
            // 使用单调墙钟差保留失焦/暂停空档；Time.deltaTime 可能被 Unity 的时间策略截断。
            frame.IntervalMs = new PerformanceMetricSample((frame.At - _previousAt) * 1000d, fullFrame);
            _previousAt = frame.At;
            frame.Focused = Application.isFocused;
            frame.MainMs = Read(_main, ProfilerMarkerDataUnit.TimeNanoseconds, 1e-6, fullFrame);
            frame.RenderMs = Read(_render, ProfilerMarkerDataUnit.TimeNanoseconds, 1e-6, fullFrame);
            frame.GcBytes = Read(_gc, ProfilerMarkerDataUnit.Bytes, 1d, fullFrame);
            frame.ArchiveBuildMs = Read(
                _archiveBuild, ProfilerMarkerDataUnit.TimeNanoseconds, 1e-6, fullFrame);
            frame.ArchiveCommitMs = Read(
                _archiveCommit, ProfilerMarkerDataUnit.TimeNanoseconds, 1e-6, fullFrame);
            frame.MaterialAuditMs = Read(
                _materialAudit, ProfilerMarkerDataUnit.TimeNanoseconds, 1e-6, fullFrame);
            frame.ArchiveExcludeMs = Read(
                _archiveExclude, ProfilerMarkerDataUnit.TimeNanoseconds, 1e-6, fullFrame);
            frame.ArchiveActiveCellsMs = Read(
                _archiveActiveCells, ProfilerMarkerDataUnit.TimeNanoseconds, 1e-6, fullFrame);
            frame.ArchiveMixedSelectionMs = Read(
                _archiveMixedSelection, ProfilerMarkerDataUnit.TimeNanoseconds, 1e-6, fullFrame);
            frame.ArchiveUnlockUploadMs = Read(
                _archiveUnlockUpload, ProfilerMarkerDataUnit.TimeNanoseconds, 1e-6, fullFrame);
            frame.ArchiveUnlockDispatchMs = Read(
                _archiveUnlockDispatch, ProfilerMarkerDataUnit.TimeNanoseconds, 1e-6, fullFrame);
            frame.ArchiveRebuildMs = Read(
                _archiveRebuild, ProfilerMarkerDataUnit.TimeNanoseconds, 1e-6, fullFrame);
            frame.ArchiveStoreMs = Read(
                _archiveStore, ProfilerMarkerDataUnit.TimeNanoseconds, 1e-6, fullFrame);
            Frames[FrameCount++] = frame;
            if (!TimingEnabled) return;
            FrameTimingManager.CaptureFrameTimings();
            uint count = FrameTimingManager.GetLatestTimings(4, _timing);
            // GetLatestTimings 的顺序不作为合同；先按旧水位筛选整批，再一次性抬高水位。
            ulong maximum = _latestTimestamp;
            for (int i = 0; i < count; i++)
            {
                FrameTiming timing = _timing[i];
                if (timing.frameStartTimestamp == 0 || timing.frameStartTimestamp <= _latestTimestamp) continue;
                if (GpuCount == GpuFrames.Length) { FailCapacity(); return; }
                GpuFrames[GpuCount++] = new ElementPerformanceGpuFrame
                {
                    Timestamp = timing.frameStartTimestamp, ReceivedAt = frame.At, PhaseAtReceipt = frame.Phase,
                    FirstSubmitTimestamp = timing.firstSubmitTimestamp,
                    CpuTimePresentCalled = timing.cpuTimePresentCalled,
                    CpuTimeFrameComplete = timing.cpuTimeFrameComplete,
                    CpuMs = timing.cpuFrameTime,
                    CpuMainThreadMs = timing.cpuMainThreadFrameTime,
                    CpuPresentWaitMs = timing.cpuMainThreadPresentWaitTime,
                    CpuRenderThreadMs = timing.cpuRenderThreadFrameTime,
                    GpuMs = timing.gpuFrameTime,
                    SyncInterval = timing.syncInterval,
                    GpuValid = timing.gpuFrameTime > 0 && ElementWorldPerformanceSchedule.Finite(timing.gpuFrameTime)
                };
                if (timing.gpuFrameTime > 0 && ElementWorldPerformanceSchedule.Finite(timing.gpuFrameTime))
                    ValidGpuTimingCount++;
                else
                    MissingGpuTimingCount++;
                maximum = Math.Max(maximum, timing.frameStartTimestamp);
            }
            _latestTimestamp = maximum;
        }

        public void RecordWrite(in ElementPerformanceWriteResult write)
        {
            if (!Running) return;
            if (WriteCount == Writes.Length) { FailCapacity(); return; }
            Writes[WriteCount++] = write;
        }

        public void MarkIncomplete() => Incomplete = true;
        private void FailCapacity() { Incomplete = true; Stop(); }
        public void Stop() { Running = false; Dispose(); }
        public void Dispose()
        {
            _main.Dispose(); _render.Dispose(); _gc.Dispose();
            _archiveBuild.Dispose(); _archiveCommit.Dispose(); _materialAudit.Dispose();
            _archiveExclude.Dispose(); _archiveActiveCells.Dispose(); _archiveMixedSelection.Dispose();
            _archiveUnlockUpload.Dispose(); _archiveUnlockDispatch.Dispose();
            _archiveRebuild.Dispose(); _archiveStore.Dispose();
        }

        private static PerformanceMetricSample Read(ProfilerRecorder recorder, ProfilerMarkerDataUnit unit,
            double multiplier, bool fullFrame)
        {
            bool valid = fullFrame && recorder.Valid && recorder.UnitType == unit && recorder.Count > 0;
            return new PerformanceMetricSample(valid ? recorder.LastValue * multiplier : 0, valid);
        }
    }
}
