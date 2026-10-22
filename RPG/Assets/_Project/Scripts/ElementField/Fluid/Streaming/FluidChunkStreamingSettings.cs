using System;

namespace Game.ElementField
{
    public enum FluidChunkLifecycleState : byte
    {
        GpuResident = 0,
        Archiving = 1,
        Cold = 2,
        Restoring = 3,
    }

    /// <summary>
    /// 流体 Chunk Streaming 的运行时不可变配置。ScriptableObject 只负责 Authoring，
    /// Runtime 使用这个快照，避免运行中修改 Inspector 让事务前后采用不同容量规则。
    /// </summary>
    public readonly struct FluidChunkStreamingSettings
    {
        public readonly bool Enabled;
        public readonly int WarmPaddingChunks;
        public readonly float ArchiveGraceSeconds;
        public readonly int MaximumArchivedChunks;
        public readonly int MaximumArchivedCellRecords;
        public readonly int MaximumPendingWrites;
        public readonly int GameplaySpawnReserveParticles;
        public readonly bool EnableLocalDormancy;
        public readonly int MaximumRestoreParticlesPerFrame;

        public FluidChunkStreamingSettings(
            bool enabled,
            int warmPaddingChunks,
            float archiveGraceSeconds,
            int maximumArchivedChunks,
            int maximumArchivedCellRecords,
            int maximumPendingWrites,
            int gameplaySpawnReserveParticles)
            : this(enabled, warmPaddingChunks, archiveGraceSeconds, maximumArchivedChunks,
                maximumArchivedCellRecords, maximumPendingWrites, gameplaySpawnReserveParticles,
                false, 256)
        {
        }

        public FluidChunkStreamingSettings(
            bool enabled,
            int warmPaddingChunks,
            float archiveGraceSeconds,
            int maximumArchivedChunks,
            int maximumArchivedCellRecords,
            int maximumPendingWrites,
            int gameplaySpawnReserveParticles,
            bool enableLocalDormancy,
            int maximumRestoreParticlesPerFrame)
        {
            if (warmPaddingChunks < 0) throw new ArgumentOutOfRangeException(nameof(warmPaddingChunks));
            if (archiveGraceSeconds < 0f || float.IsNaN(archiveGraceSeconds) || float.IsInfinity(archiveGraceSeconds))
                throw new ArgumentOutOfRangeException(nameof(archiveGraceSeconds));
            if (maximumArchivedChunks <= 0) throw new ArgumentOutOfRangeException(nameof(maximumArchivedChunks));
            if (maximumArchivedCellRecords <= 0) throw new ArgumentOutOfRangeException(nameof(maximumArchivedCellRecords));
            if (maximumPendingWrites <= 0) throw new ArgumentOutOfRangeException(nameof(maximumPendingWrites));
            if (gameplaySpawnReserveParticles < 0)
                throw new ArgumentOutOfRangeException(nameof(gameplaySpawnReserveParticles));
            if (maximumRestoreParticlesPerFrame <= 0)
                throw new ArgumentOutOfRangeException(nameof(maximumRestoreParticlesPerFrame));

            Enabled = enabled;
            WarmPaddingChunks = warmPaddingChunks;
            ArchiveGraceSeconds = archiveGraceSeconds;
            MaximumArchivedChunks = maximumArchivedChunks;
            MaximumArchivedCellRecords = maximumArchivedCellRecords;
            MaximumPendingWrites = maximumPendingWrites;
            GameplaySpawnReserveParticles = gameplaySpawnReserveParticles;
            EnableLocalDormancy = enableLocalDormancy;
            MaximumRestoreParticlesPerFrame = maximumRestoreParticlesPerFrame;
        }
    }
}
