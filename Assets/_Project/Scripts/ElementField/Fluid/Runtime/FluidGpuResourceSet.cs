using System;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 单次初始化后长期复用的 GPU Buffer 集合。SoA（Structure of Arrays）让不同 Kernel
    /// 只绑定需要的属性流；这些显存资源不依赖 Finalizer，必须由 Runtime 显式 Dispose。
    /// </summary>
    public sealed class FluidGpuResourceSet : IDisposable
    {
        public int ParticleCapacity { get; }
        public int HashTableCapacity { get; }
        public int MaxSpawnRequests { get; }
        public int MaxFluidColliders { get; }

        public GraphicsBuffer Positions { get; private set; }
        public GraphicsBuffer PredictedPositions { get; private set; }
        public GraphicsBuffer Velocities { get; private set; }
        public GraphicsBuffer DensityLambda { get; private set; }
        public GraphicsBuffer Metadata { get; private set; }
        public GraphicsBuffer DeltaPositions { get; private set; }
        public GraphicsBuffer DeltaVelocities { get; private set; }
        public GraphicsBuffer Vorticities { get; private set; }
        public GraphicsBuffer SpatialCells { get; private set; }
        public GraphicsBuffer SpatialEntries { get; private set; }
        public GraphicsBuffer CellRanges { get; private set; }
        public GraphicsBuffer FreeIndices { get; private set; }
        public GraphicsBuffer Counters { get; private set; }
        public GraphicsBuffer SpawnRequests { get; private set; }
        public GraphicsBuffer ConsumeRequests { get; private set; }
        public GraphicsBuffer ConvertRequests { get; private set; }
        public GraphicsBuffer ReactionParticleDeltas { get; private set; }
        public GraphicsBuffer ColliderProxies { get; private set; }
        public GraphicsBuffer CollisionContacts { get; private set; }
        public GraphicsBuffer GameplaySamples { get; private set; }
        public GraphicsBuffer StableTickCounters { get; private set; }
        public GraphicsBuffer WakeRequests { get; private set; }
        public GraphicsBuffer ActivityCounters { get; private set; }
        public GraphicsBuffer SolverDispatchArgs { get; private set; }
        public GraphicsBuffer HashDispatchArgs { get; private set; }
        public GraphicsBuffer LiquidMaterialParameters { get; private set; }
        public GraphicsBuffer ArchiveSamples { get; private set; }
        public GraphicsBuffer ArchiveUnlockIndices { get; private set; }
        public GraphicsBuffer RestoreParticles { get; private set; }
        public GraphicsBuffer RestoreReservedIndices { get; private set; }
        public GraphicsBuffer TransferStatus { get; private set; }

        public FluidGpuResourceSet(
            int particleCapacity,
            int hashTableCapacity,
            int maxSpawnRequests,
            int maxFluidColliders = 64,
            FluidGpuLiquidMaterialParameters[] liquidMaterialParameters = null)
        {
            if (particleCapacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(particleCapacity));
            if (hashTableCapacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(hashTableCapacity));
            if (maxSpawnRequests <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxSpawnRequests));
            if (maxFluidColliders <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxFluidColliders));

            ParticleCapacity = particleCapacity;
            HashTableCapacity = hashTableCapacity;
            MaxSpawnRequests = maxSpawnRequests;
            MaxFluidColliders = maxFluidColliders;

            try
            {
                Positions = CreateStructured(particleCapacity, FluidGpuLayout.Float4Stride);
                PredictedPositions = CreateStructured(particleCapacity, FluidGpuLayout.Float4Stride);
                Velocities = CreateStructured(particleCapacity, FluidGpuLayout.Float4Stride);
                DensityLambda = CreateStructured(particleCapacity, FluidGpuLayout.UInt2Stride);
                Metadata = CreateStructured(particleCapacity, FluidGpuLayout.UInt2Stride);
                DeltaPositions = CreateStructured(particleCapacity, FluidGpuLayout.Float4Stride);
                DeltaVelocities = CreateStructured(particleCapacity, FluidGpuLayout.Float4Stride);
                Vorticities = CreateStructured(particleCapacity, FluidGpuLayout.Float4Stride);
                SpatialCells = CreateStructured(particleCapacity, FluidGpuLayout.Int4Stride);
                SpatialEntries = CreateStructured(particleCapacity, FluidGpuLayout.UInt2Stride);
                CellRanges = CreateStructured(hashTableCapacity, FluidGpuLayout.UInt2Stride);
                FreeIndices = CreateStructured(particleCapacity, sizeof(uint));
                Counters = CreateStructured(FluidGpuLayout.CounterCount, sizeof(uint));
                SpawnRequests = CreateStructured(maxSpawnRequests, FluidGpuLayout.SpawnRequestStride);
                ConsumeRequests = CreateStructured(maxSpawnRequests, FluidGpuLayout.ConsumeRequestStride);
                ConvertRequests = CreateStructured(maxSpawnRequests, FluidGpuLayout.ConvertRequestStride);
                ReactionParticleDeltas = CreateStructured(
                    FluidGpuLayout.MaterialAuditSlotCount, sizeof(int));
                // Cumulative ledger starts at zero once per Runtime lifetime; reactions only add signed deltas.
                ReactionParticleDeltas.SetData(new int[FluidGpuLayout.MaterialAuditSlotCount]);
                ColliderProxies = CreateStructured(maxFluidColliders, FluidGpuLayout.ColliderProxyStride);
                CollisionContacts = CreateStructured(
                    particleCapacity,
                    FluidGpuLayout.CollisionContactManifoldStride);
                // 单个 packed float4 同时承载 Position.xyz 与 tag，AsyncGPUReadback 只需一个 Request。
                GameplaySamples = CreateStructured(particleCapacity, FluidGpuLayout.Float4Stride);
                StableTickCounters = CreateStructured(particleCapacity, sizeof(uint));
                WakeRequests = CreateStructured(particleCapacity, sizeof(uint));
                ActivityCounters = CreateStructured(FluidGpuLayout.ActivityCounterCount, sizeof(uint));
                SolverDispatchArgs = new GraphicsBuffer(
                    GraphicsBuffer.Target.IndirectArguments,
                    3,
                    sizeof(uint));
                HashDispatchArgs = new GraphicsBuffer(
                    GraphicsBuffer.Target.IndirectArguments,
                    3,
                    sizeof(uint));
                LiquidMaterialParameters = CreateStructured(
                    LiquidMaterialSettingsTable.Capacity,
                    FluidGpuLayout.LiquidMaterialParameterStride);
                FluidGpuLiquidMaterialParameters[] materialRows = liquidMaterialParameters
                    ?? new FluidGpuLiquidMaterialParameters[LiquidMaterialSettingsTable.Capacity];
                if (materialRows.Length != LiquidMaterialSettingsTable.Capacity)
                    throw new ArgumentException("Liquid Material GPU table must contain 256 rows.", nameof(liquidMaterialParameters));
                // GraphicsBuffer 初始显存未定义；显式上传 zero rows，测试/Debug 绕过 Production Table 时
                // 才能可靠进入 Shader 的 legacy-fixture fallback，而不是读取上一块 VRAM 残值。
                LiquidMaterialParameters.SetData(materialRows);
                ArchiveSamples = CreateStructured(particleCapacity, FluidGpuLayout.ArchiveSampleStride);
                // Archive 排除列表来自 AsyncGPUReadback 后的 CPU 判定。LockBufferForWrite 让 CPU
                // 直接写入映射上传区，避免 SetData 为复用同一 GraphicsBuffer 等待 GPU 管线清空。
                ArchiveUnlockIndices = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    GraphicsBuffer.UsageFlags.LockBufferForWrite,
                    particleCapacity,
                    sizeof(uint));
                RestoreParticles = CreateStructured(particleCapacity, FluidGpuRestoreParticle.Stride);
                RestoreReservedIndices = CreateStructured(particleCapacity, sizeof(uint));
                TransferStatus = CreateStructured(1, FluidGpuTransferStatus.Stride);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>
        /// OnDisable/OnDestroy 可以重复到达。每次 Release 后立即置 null，
        /// 因而不会再次访问已 Dispose 的 native Buffer。
        /// </summary>
        public void Dispose()
        {
            Positions = DisposeBuffer(Positions);
            PredictedPositions = DisposeBuffer(PredictedPositions);
            Velocities = DisposeBuffer(Velocities);
            DensityLambda = DisposeBuffer(DensityLambda);
            Metadata = DisposeBuffer(Metadata);
            DeltaPositions = DisposeBuffer(DeltaPositions);
            DeltaVelocities = DisposeBuffer(DeltaVelocities);
            Vorticities = DisposeBuffer(Vorticities);
            SpatialCells = DisposeBuffer(SpatialCells);
            SpatialEntries = DisposeBuffer(SpatialEntries);
            CellRanges = DisposeBuffer(CellRanges);
            FreeIndices = DisposeBuffer(FreeIndices);
            Counters = DisposeBuffer(Counters);
            SpawnRequests = DisposeBuffer(SpawnRequests);
            ConsumeRequests = DisposeBuffer(ConsumeRequests);
            ConvertRequests = DisposeBuffer(ConvertRequests);
            ReactionParticleDeltas = DisposeBuffer(ReactionParticleDeltas);
            ColliderProxies = DisposeBuffer(ColliderProxies);
            CollisionContacts = DisposeBuffer(CollisionContacts);
            GameplaySamples = DisposeBuffer(GameplaySamples);
            StableTickCounters = DisposeBuffer(StableTickCounters);
            WakeRequests = DisposeBuffer(WakeRequests);
            ActivityCounters = DisposeBuffer(ActivityCounters);
            SolverDispatchArgs = DisposeBuffer(SolverDispatchArgs);
            HashDispatchArgs = DisposeBuffer(HashDispatchArgs);
            LiquidMaterialParameters = DisposeBuffer(LiquidMaterialParameters);
            ArchiveSamples = DisposeBuffer(ArchiveSamples);
            ArchiveUnlockIndices = DisposeBuffer(ArchiveUnlockIndices);
            RestoreParticles = DisposeBuffer(RestoreParticles);
            RestoreReservedIndices = DisposeBuffer(RestoreReservedIndices);
            TransferStatus = DisposeBuffer(TransferStatus);
        }

        private static GraphicsBuffer CreateStructured(int count, int stride)
        {
            return new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, stride);
        }

        private static GraphicsBuffer DisposeBuffer(GraphicsBuffer buffer)
        {
            if (buffer == null)
                return null;

            buffer.Dispose();
            return null;
        }
    }
}
