using Unity.Collections;
using UnityEngine;

namespace Game.ElementField
{
    internal readonly struct FluidArchiveReadbackLease
    {
        public readonly GraphicsBuffer Samples;
        public readonly int ParticleCapacity;
        public readonly Bounds RetainedBounds;
        public readonly Vector3 WorldOrigin;
        public readonly float CellSize;
        public readonly int ChunkSize;
        public readonly uint TransactionId;
        public readonly uint LayoutVersion;
        public readonly uint TopologyVersion;
        public readonly LiquidMaterialAmountScaleSnapshot AmountScales;
        public readonly ulong AuditEpoch;
        public readonly uint AuditMutationVersion;
        public readonly ulong[] ExpectedGmuByMaterial;
        public readonly bool AuditEligible;
        public readonly ulong SubmittedParticleWatermark;
        public readonly double AuditRequestedAt;

        public FluidArchiveReadbackLease(GraphicsBuffer samples, int particleCapacity,
            Bounds retainedBounds, Vector3 worldOrigin, float cellSize, int chunkSize,
            uint transactionId, uint layoutVersion, uint topologyVersion,
            LiquidMaterialAmountScaleSnapshot amountScales)
            : this(samples, particleCapacity, retainedBounds, worldOrigin, cellSize, chunkSize,
                transactionId, layoutVersion, topologyVersion, amountScales, 0u, 0u, null, false,
                0u, 0d)
        {
        }

        public FluidArchiveReadbackLease(GraphicsBuffer samples, int particleCapacity,
            Bounds retainedBounds, Vector3 worldOrigin, float cellSize, int chunkSize,
            uint transactionId, uint layoutVersion, uint topologyVersion,
            LiquidMaterialAmountScaleSnapshot amountScales, ulong auditEpoch,
            uint auditMutationVersion, ulong[] expectedGmuByMaterial, bool auditEligible,
            ulong submittedParticleWatermark, double auditRequestedAt)
        {
            Samples = samples; ParticleCapacity = particleCapacity; RetainedBounds = retainedBounds;
            WorldOrigin = worldOrigin; CellSize = cellSize; ChunkSize = chunkSize;
            TransactionId = transactionId; LayoutVersion = layoutVersion;
            TopologyVersion = topologyVersion; AmountScales = amountScales;
            AuditEpoch = auditEpoch; AuditMutationVersion = auditMutationVersion;
            ExpectedGmuByMaterial = expectedGmuByMaterial; AuditEligible = auditEligible;
            SubmittedParticleWatermark = submittedParticleWatermark;
            AuditRequestedAt = auditRequestedAt;
        }
    }

    internal readonly struct FluidRestoreStatusReadbackLease
    {
        public readonly GraphicsBuffer Status;
        public readonly int ParticleCount;
        public readonly uint TransactionId;

        public FluidRestoreStatusReadbackLease(
            GraphicsBuffer status, int particleCount, uint transactionId)
        {
            Status = status;
            ParticleCount = particleCount;
            TransactionId = transactionId;
        }
    }

    internal interface IFluidChunkTransferBackend
    {
        bool TryGetParticleCapacity(out int particleCapacity);
        bool TryGetFreeParticleCount(out uint freeParticleCount);
        bool TryGetActivityCounts(out uint awakeParticleCount, out uint sleepingParticleCount);
        bool TryAcquireArchiveReadbackLease(Bounds retainedBounds, Vector3 worldOrigin,
            float cellSize, int chunkSize, bool includeSleepingRetained,
            uint transactionId, out FluidArchiveReadbackLease lease);
        bool TryExcludeArchiveParticleIndices(uint[] particleIndices, int count);
        void CommitArchiveAndRelease(uint transactionId);
        void CancelArchive(uint transactionId);
        bool TryAcquireRestoreStatusLease(
            NativeArray<FluidGpuRestoreParticle> particles,
            int particleCount,
            int gameplayReserveParticles,
            uint transactionId,
            out FluidRestoreStatusReadbackLease lease);
        void CommitRestore(uint transactionId, int particleCount, out uint publishedTopologyVersion);
        void RollbackRestore(uint transactionId, int particleCount);
        void ReleaseTransferReadbackLease();
    }

    /// <summary>
    /// Material Audit 的可选准备步骤。反应账本只在诊断明确请求时回读，避免把
    /// AsyncGPUReadback 的托管开销放进正常 Simulation 热路径。
    /// </summary>
    internal interface IFluidMaterialAuditPreparation
    {
        bool TryPrepareMaterialAudit();
    }
}
