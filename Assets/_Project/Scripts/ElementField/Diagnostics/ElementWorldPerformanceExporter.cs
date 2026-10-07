using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Game.ElementField
{
    /// <summary>仅停止后执行的磁盘边界。独立目录拒绝覆盖，CSV 使用 InvariantCulture 与标准引号转义。</summary>
    public sealed class ElementWorldPerformanceExporter
    {
        private readonly ElementWorldPerformanceCapture _capture;
        private readonly string _scenario, _configuration, _hardware;
        public ElementWorldPerformanceExporter(ElementWorldPerformanceCapture capture, string scenario,
            string configuration, string hardware)
        { _capture = capture ?? throw new ArgumentNullException(nameof(capture)); _scenario = scenario; _configuration = configuration; _hardware = hardware; }

        public bool TryExport(string parentDirectory, string runId, out string resultDirectory, out string error)
        {
            resultDirectory = null;
            error = null;
            try
            {
                if (_capture.Running) throw new InvalidOperationException("停止采集后才能导出。");
                if (string.IsNullOrWhiteSpace(runId) || runId == "." || runId == ".."
                    || runId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                    || runId.IndexOf('/') >= 0 || runId.IndexOf('\\') >= 0)
                    throw new ArgumentException("runId 必须是单个有效目录名。");
                string directory = Path.Combine(Path.GetFullPath(parentDirectory), runId);
                if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("结果路径已存在，拒绝覆盖。");
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "configuration.json"), _configuration ?? "{}", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(directory, "manifest.json"), "{\n\"scenario\":" + Json(_scenario)
                    + ",\n\"hardware\":" + Json(_hardware)
                    + ",\n\"recorders\":" + Json(_capture.RecorderAvailability)
                    + ",\n\"incomplete\":" + (_capture.Incomplete ? "true" : "false")
                    + ",\n\"frameCapacity\":" + _capture.Frames.Length + ",\n\"eventCapacity\":" + _capture.Writes.Length
                    + ",\n\"frameTimingFeatureEnabled\":" + (_capture.TimingEnabled ? "true" : "false")
                    + ",\n\"cpuTimerFrequency\":" + _capture.CpuTimerFrequency
                    + ",\n\"gpuTimerFrequency\":" + _capture.GpuTimerFrequency
                    + ",\n\"vSyncsPerSecond\":" + _capture.VSyncsPerSecond
                    + ",\n\"validGpuTimingCount\":" + _capture.ValidGpuTimingCount
                    + ",\n\"missingGpuTimingCount\":" + _capture.MissingGpuTimingCount
                    + ",\n\"gpuWindow\":\"Independent GPU timestamps; phase indicates receipt, not execution. GPU window percentiles intentionally omitted.\"\n}", new UTF8Encoding(false));
                ExportFrames(Path.Combine(directory, "frames.csv"));
                ExportGpu(Path.Combine(directory, "gpu-frames.csv"));
                ExportWrites(Path.Combine(directory, "writes.csv"));
                ExportSummary(Path.Combine(directory, "summary.json"));
                resultDirectory = directory;
                return true;
            }
            catch (Exception exception) { error = exception.Message; return false; }
        }

        public static string Csv(string value) => "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";
        private static string Json(string value) => "\"" + (value ?? string.Empty).Replace("\\", "\\\\")
            .Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
        private static string N(double value) => ElementWorldPerformanceSchedule.Finite(value)
            ? value.ToString("R", CultureInfo.InvariantCulture) : "null";
        private static string M(PerformanceMetricSample sample) => sample.Valid ? N(sample.Value) : "null";

        private void ExportFrames(string path)
        {
            using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
            writer.WriteLine("frame,at,phase,intervalMs,mainMs,renderMs,gcBytes,worldTicks,fluidTicks,pendingSpawns,submitted,worldDebt,fluidDebt,residentChunks,activeChunks,poolValid,poolVersion,poolMutationVersion,poolRequestedAt,poolCompletedAt,alive,free,dropped,activityValid,activityVersion,activityRequestedAt,activityCompletedAt,awake,sleeping,interest,gameplayValid,snapshotVersion,gameplayRequestedAt,gameplayCompletedAt,gameplayPublishedAt,readbackErrors,archivedChunks,archivedRecords,archivedParticles,restoredParticles,dormantChunks,dormantRecords,dormantAmount,localDormancyArchives,dormantWakeRequests,lastDormantWakeReason,restorePreparing,restorePreparedParticles,retainedAliveSamples,retainedSpeedBelow01,retainedSpeedBelow05,retainedSpeedBelow10,retainedSpeedBelow25,retainedMaximumSpeed,pendingStreamingWrites,rejectedPendingWrites,archiveTransactions,restoreTransactions,archiveErrors,restoreErrors,archiveRollbacks,restoreRollbacks,capacityPressure,lastArchiveBytes,lastArchiveMs,lastArchiveLockedParticles,lastArchiveRecords,lastArchiveRollbackReason,lastArchiveStoreRejectionReason,lastRestoreParticles,lastRestoreMs,waterAuditValid,waterAuditEpoch,waterAuditSubmittedWatermark,waterAuditRequestedAt,waterAuditCompletedAt,waterExpectedGmu,waterGpuGmu,waterRamGmu,waterUnexplainedGmu,poisonAuditValid,poisonExpectedGmu,poisonGpuGmu,poisonRamGmu,poisonUnexplainedGmu,stickyAuditValid,stickyExpectedGmu,stickyGpuGmu,stickyRamGmu,stickyUnexplainedGmu,colliders,targets,exposureSaturated,burning,wet,poisoned,sticky,probeWater,processedWrites,rejectedWrites,reactionPairs,focused,regionCount,sourceCount,rejectedRegions,unitSourcesSkippedByBudget,projectileSourcesSkippedByBudget,pendingWakes,measureFire,measureWater,totalAllocatedMemory,totalReservedMemory,monoHeapMemory,monoUsedMemory,graphicsDriverMemory,archiveBuildMs,archiveCommitMs,materialAuditMs,archiveExcludeMs,archiveActiveCellsMs,archiveMixedSelectionMs,archiveUnlockUploadMs,archiveUnlockDispatchMs,archiveRebuildMs,archiveStoreMs");
            for (int i = 0; i < _capture.FrameCount; i++)
            {
                var f = _capture.Frames[i];
                writer.WriteLine(string.Join(",", f.Frame, N(f.At), (int)f.Phase, M(f.IntervalMs), M(f.MainMs), M(f.RenderMs), M(f.GcBytes),
                    f.WorldTicks, f.FluidTicks, f.PendingSpawns, f.Submitted, f.WorldDebt, f.FluidDebt, f.ResidentChunks, f.ActiveChunks,
                    f.Pool.Valid, f.Pool.Version, f.Pool.MutationVersion, N(f.Pool.RequestedAt), N(f.Pool.CompletedAt), f.Pool.Alive, f.Pool.Free, f.Pool.Dropped,
                    f.Activity.Valid, f.Activity.Version, N(f.Activity.RequestedAt), N(f.Activity.CompletedAt), f.Activity.Awake, f.Activity.Sleeping,
                    f.Activity.Interest, f.Gameplay.Valid, f.Gameplay.SnapshotVersion, N(f.Gameplay.RequestedAt), N(f.Gameplay.CompletedAt),
                    N(f.Gameplay.PublishedAt), f.Gameplay.ReadbackErrorCount,
                    f.ArchivedChunks, f.ArchivedRecords, f.ArchivedParticles, f.RestoredParticles,
                    f.DormantChunks, f.DormantRecords, f.DormantAmount, f.LocalDormancyArchives,
                    f.DormantWakeRequests, (byte)f.LastDormantWakeReason, f.RestorePreparing,
                    f.RestorePreparedParticles,
                    f.RetainedAliveSamples, f.RetainedSpeedBelow01, f.RetainedSpeedBelow05,
                    f.RetainedSpeedBelow10, f.RetainedSpeedBelow25, N(f.RetainedMaximumSpeed),
                    f.PendingStreamingWrites, f.RejectedPendingWrites, f.ArchiveTransactions, f.RestoreTransactions,
                    f.ArchiveErrors, f.RestoreErrors, f.ArchiveRollbacks, f.RestoreRollbacks, f.CapacityPressure,
                    f.LastArchiveBytes, N(f.LastArchiveMs), f.LastArchiveLockedParticles,
                    f.LastArchiveRecords, (byte)f.LastArchiveRollbackReason,
                    (byte)f.LastArchiveStoreRejectionReason,
                    f.LastRestoreParticles, N(f.LastRestoreMs),
                    f.WaterAudit.Valid, f.WaterAudit.Epoch, f.WaterAudit.SubmittedParticleWatermark,
                    N(f.WaterAudit.RequestedAt), N(f.WaterAudit.CompletedAt), f.WaterAudit.ExpectedGmu,
                    f.WaterAudit.GpuAuthorityGmu, f.WaterAudit.RamAuthorityGmu, f.WaterAudit.UnexplainedGmu,
                    f.PoisonAudit.Valid, f.PoisonAudit.ExpectedGmu, f.PoisonAudit.GpuAuthorityGmu,
                    f.PoisonAudit.RamAuthorityGmu, f.PoisonAudit.UnexplainedGmu,
                    f.StickyAudit.Valid, f.StickyAudit.ExpectedGmu, f.StickyAudit.GpuAuthorityGmu,
                    f.StickyAudit.RamAuthorityGmu, f.StickyAudit.UnexplainedGmu,
                    f.Colliders, f.Targets, f.ExposureSaturated,
                    N(f.Burning), N(f.Wet), N(f.Poisoned), N(f.Sticky), f.ProbeWater, f.ProcessedWrites, f.RejectedWrites, f.ReactionPairs, f.Focused,
                    f.ActivityRegions, f.ActivitySources, f.RejectedRegions,
                    f.UnitSourcesSkippedByBudget, f.ProjectileSourcesSkippedByBudget,
                    f.PendingWakes, f.MeasureFire, f.MeasureWater,
                    f.TotalAllocatedMemory, f.TotalReservedMemory, f.MonoHeapMemory,
                    f.MonoUsedMemory, f.GraphicsDriverMemory,
                    M(f.ArchiveBuildMs), M(f.ArchiveCommitMs), M(f.MaterialAuditMs),
                    M(f.ArchiveExcludeMs), M(f.ArchiveActiveCellsMs), M(f.ArchiveMixedSelectionMs),
                    M(f.ArchiveUnlockUploadMs), M(f.ArchiveUnlockDispatchMs),
                    M(f.ArchiveRebuildMs), M(f.ArchiveStoreMs)));
            }
        }

        private void ExportGpu(string path)
        {
            using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
            writer.WriteLine("frameStartTimestamp,firstSubmitTimestamp,cpuTimePresentCalled,cpuTimeFrameComplete,receivedAt,phaseAtReceipt,cpuMs,cpuMainThreadMs,cpuPresentWaitMs,cpuRenderThreadMs,gpuMs,syncInterval");
            for (int i = 0; i < _capture.GpuCount; i++)
            {
                var f = _capture.GpuFrames[i];
                writer.WriteLine(string.Join(",", f.Timestamp, f.FirstSubmitTimestamp,
                    f.CpuTimePresentCalled, f.CpuTimeFrameComplete, N(f.ReceivedAt),
                    (int)f.PhaseAtReceipt, N(f.CpuMs), N(f.CpuMainThreadMs),
                    N(f.CpuPresentWaitMs), N(f.CpuRenderThreadMs),
                    f.GpuValid ? N(f.GpuMs) : "null", f.SyncInterval));
            }
        }

        private void ExportWrites(string path)
        {
            using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
            writer.WriteLine("phase,sequence,dueSeconds,at,latenessSeconds,accepted,material,amount,x,y,z,radius");
            for (int i = 0; i < _capture.WriteCount; i++)
            {
                var w = _capture.Writes[i]; var r = w.Write.Request;
                writer.WriteLine(string.Join(",", (int)w.Phase, w.Write.Sequence, N(w.Write.DueSeconds), N(w.At), N(w.LatenessSeconds),
                    w.Accepted, (byte)r.MaterialKind, r.TotalAmount, N(r.WorldPosition.x), N(r.WorldPosition.y), N(r.WorldPosition.z), N(r.Radius)));
            }
        }

        private void ExportSummary(string path)
        {
            var samples = new PerformanceMetricSample[_capture.FrameCount];
            var scratch = new double[samples.Length];
            var text = new StringBuilder("{\n");
            string[] names = { "intervalMs", "mainMs", "renderMs", "gcBytes" };
            for (int metric = 0; metric < 4; metric++)
            {
                int count = 0;
                for (int i = 1; i < _capture.FrameCount; i++)
                {
                    var f = _capture.Frames[i];
                    // Recorder.LastValue 属于最近结束的帧，阶段第一帧剔除，避免混入 Settle 的工作。
                    if (f.Phase != ElementPerformancePhase.Measure || _capture.Frames[i - 1].Phase != ElementPerformancePhase.Measure) continue;
                    samples[count++] = metric == 0 ? f.IntervalMs : metric == 1 ? f.MainMs : metric == 2 ? f.RenderMs : f.GcBytes;
                }
                var p = ElementWorldPerformanceStatistics.Calculate(samples, count, scratch);
                if (metric > 0) text.Append(",\n");
                text.Append(Json(names[metric])).Append(':');
                if (!p.Valid) { text.Append("null"); continue; }
                text.Append("{\"count\":").Append(p.ValidCount).Append(",\"p50\":").Append(N(p.P50))
                    .Append(",\"p95\":").Append(N(p.P95)).Append(",\"p99\":").Append(N(p.P99))
                    .Append(",\"max\":").Append(N(p.Maximum)).Append('}');
            }
            text.Append("\n}");
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
        }
    }
}
