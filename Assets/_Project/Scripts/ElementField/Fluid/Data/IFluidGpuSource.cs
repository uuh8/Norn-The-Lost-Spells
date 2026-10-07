using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

[assembly: InternalsVisibleTo("Game.ElementField.Tests")]

namespace Game.ElementField
{
    /// <summary>
    /// CPU 侧只增不减的“本次运行可能出现过哪些液体”提示。它允许 Presentation 跳过确定为空的材质，
    /// 但出现未知编号时必须保守地认为全部可能存在，避免未来扩展被旧 Renderer 错误裁掉。
    /// </summary>
    public static class FluidMaterialPresenceMask
    {
        public static uint Include(uint mask, uint materialId) =>
            materialId < 32u ? mask | (1u << (int)materialId) : uint.MaxValue;

        public static bool MayContain(uint mask, uint materialId) =>
            materialId >= 32u || (mask & (1u << (int)materialId)) != 0u;
    }

    /// <summary>
    /// 提供给 Rendering 的只读 GPU 流体快照。它只暴露已经创建的 Buffer，
    /// 不提供写入入口，避免 Presentation 反向依赖或改写 Gameplay Simulation。
    /// </summary>
    public readonly struct FluidGpuSnapshot
    {
        public readonly GraphicsBuffer Positions;
        public readonly GraphicsBuffer PredictedPositions;
        public readonly GraphicsBuffer Velocities;
        public readonly GraphicsBuffer Metadata;
        public readonly GraphicsBuffer SpatialEntries;
        public readonly GraphicsBuffer CellRanges;
        public readonly GraphicsBuffer SpatialCells;
        public readonly GraphicsBuffer LiquidMaterialParameters;
        public readonly int ParticleCapacity;
        public readonly int HashTableCapacity;
        public readonly float SmoothingRadius;
        public readonly float ParticleMass;
        public readonly Bounds ActiveBounds;
        public readonly Bounds ResidentRenderBounds;
        public readonly uint LayoutVersion;
        // 只描述粒子 slot 的 spawn/reuse 拓扑，不替代 LayoutVersion；Rendering 用不等比较即可安全跨 uint wrap。
        public readonly uint TopologyVersion;
        // 每个完成提交的 Fixed Tick 增长一次；Renderer 可复用上一份 Mesh，避免同一模拟状态重复重建。
        public readonly uint SimulationVersion;
        // 单调提示而非精确计数：false 一定为空，true 表示本次运行曾提交过该材质。
        public readonly uint MaterialPresenceMask;

        public FluidGpuSnapshot(
            GraphicsBuffer positions,
            GraphicsBuffer predictedPositions,
            GraphicsBuffer velocities,
            GraphicsBuffer metadata,
            GraphicsBuffer spatialEntries,
            GraphicsBuffer cellRanges,
            GraphicsBuffer spatialCells,
            int particleCapacity,
            int hashTableCapacity,
            float smoothingRadius,
            float particleMass,
            Bounds activeBounds,
            uint layoutVersion,
            uint topologyVersion)
            : this(
                positions,
                predictedPositions,
                velocities,
                metadata,
                spatialEntries,
                cellRanges,
                spatialCells,
                null,
                particleCapacity,
                hashTableCapacity,
                smoothingRadius,
                particleMass,
                activeBounds,
                activeBounds,
                layoutVersion,
                topologyVersion,
                0u,
                uint.MaxValue)
        {
        }

        public FluidGpuSnapshot(
            GraphicsBuffer positions,
            GraphicsBuffer predictedPositions,
            GraphicsBuffer velocities,
            GraphicsBuffer metadata,
            GraphicsBuffer spatialEntries,
            GraphicsBuffer cellRanges,
            GraphicsBuffer spatialCells,
            GraphicsBuffer liquidMaterialParameters,
            int particleCapacity,
            int hashTableCapacity,
            float smoothingRadius,
            float particleMass,
            Bounds activeBounds,
            uint layoutVersion,
            uint topologyVersion)
            : this(
                positions,
                predictedPositions,
                velocities,
                metadata,
                spatialEntries,
                cellRanges,
                spatialCells,
                liquidMaterialParameters,
                particleCapacity,
                hashTableCapacity,
                smoothingRadius,
                particleMass,
                activeBounds,
                activeBounds,
                layoutVersion,
                topologyVersion,
                0u,
                uint.MaxValue)
        {
        }

        public FluidGpuSnapshot(
            GraphicsBuffer positions,
            GraphicsBuffer predictedPositions,
            GraphicsBuffer velocities,
            GraphicsBuffer metadata,
            GraphicsBuffer spatialEntries,
            GraphicsBuffer cellRanges,
            GraphicsBuffer spatialCells,
            GraphicsBuffer liquidMaterialParameters,
            int particleCapacity,
            int hashTableCapacity,
            float smoothingRadius,
            float particleMass,
            Bounds activeBounds,
            Bounds residentRenderBounds,
            uint layoutVersion,
            uint topologyVersion,
            uint simulationVersion = 0u,
            uint materialPresenceMask = uint.MaxValue)
        {
            Positions = positions;
            PredictedPositions = predictedPositions;
            Velocities = velocities;
            Metadata = metadata;
            SpatialEntries = spatialEntries;
            CellRanges = cellRanges;
            SpatialCells = spatialCells;
            LiquidMaterialParameters = liquidMaterialParameters;
            ParticleCapacity = particleCapacity;
            HashTableCapacity = hashTableCapacity;
            SmoothingRadius = smoothingRadius;
            ParticleMass = particleMass;
            ActiveBounds = activeBounds;
            ResidentRenderBounds = residentRenderBounds;
            LayoutVersion = layoutVersion;
            TopologyVersion = topologyVersion;
            SimulationVersion = simulationVersion;
            MaterialPresenceMask = materialPresenceMask;
        }
    }

    /// <summary>
    /// 记录 GPU 粒子可能驻留过的世界范围。它只增不减：角色离开后粒子会退出
    /// Interest/Solver，但 Presentation 仍需要覆盖这些 Alive 且冻结的粒子。
    /// </summary>
    public struct FluidResidentBoundsTracker
    {
        private Bounds _bounds;
        private bool _hasBounds;

        public readonly bool HasBounds => _hasBounds;
        public readonly Bounds Bounds => _hasBounds ? _bounds : default;

        public void Include(Bounds bounds)
        {
            if (!IsFinite(bounds.center) || !IsPositiveFinite(bounds.size))
                throw new ArgumentOutOfRangeException(nameof(bounds));

            if (!_hasBounds)
            {
                _bounds = bounds;
                _hasBounds = true;
                return;
            }

            _bounds.Encapsulate(bounds.min);
            _bounds.Encapsulate(bounds.max);
        }

        public void IncludeSpawn(Vector3 center, float requestRadius, float particleSupportRadius)
        {
            if (!IsFinite(center)
                || !IsNonNegativeFinite(requestRadius)
                || !IsNonNegativeFinite(particleSupportRadius))
            {
                throw new ArgumentOutOfRangeException(nameof(center));
            }

            float extent = requestRadius + particleSupportRadius;
            Include(new Bounds(center, Vector3.one * (extent * 2f)));
        }

        private static bool IsFinite(Vector3 value) =>
            IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);

        private static bool IsPositiveFinite(Vector3 value) =>
            value.x > 0f && value.y > 0f && value.z > 0f && IsFinite(value);

        private static bool IsNonNegativeFinite(float value) =>
            value >= 0f && IsFinite(value);

        private static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);
    }

    /// <summary>
    /// Rendering 等消费者通过该 Contract 获取 GPU 数据；不能从此接口生成粒子或注册 Element 写入。
    /// </summary>
    public interface IFluidGpuSource
    {
        bool IsFluidInitialized { get; }
        bool TryGetGpuSnapshot(out FluidGpuSnapshot snapshot);
    }

    /// <summary>
    /// 粒子 topology/material-membership 的 CPU 发布时钟。Queue enqueue 只是未来工作，不能改变此版本；
    /// Runtime 只有在非空 Spawn/Consume/Convert Dispatch 已提交到 Graphics Queue 后才发布。
    /// </summary>
    internal struct FluidTopologyVersionTracker
    {
        internal FluidTopologyVersionTracker(uint initialVersion)
        {
            PublishedVersion = initialVersion;
        }

        internal uint PublishedVersion { get; private set; }

        internal void PublishAfterSpawnDispatch(int requestCount)
        {
            if (requestCount <= 0)
                return;

            // 消费者只做 != 比较，因此 unchecked wrap 仍表示一次可观察的 topology 变化。
            unchecked
            {
                PublishedVersion++;
            }
        }

        internal void PublishAfterConsumeDispatch(int commandCount)
        {
            PublishAfterSpawnDispatch(commandCount);
        }

        internal void PublishAfterConvertDispatch(int commandCount)
        {
            PublishAfterSpawnDispatch(commandCount);
        }
    }

    /// <summary>
    /// C#/HLSL 共享的 Buffer 布局常量。把 Stride 集中在这里，避免两端各自猜测造成 VRAM 数据错位。
    /// </summary>
    public static class FluidGpuLayout
    {
        public const int Float4Stride = 16;
        public const int UInt2Stride = 8;
        public const int UInt4Stride = 16;
        public const int Int4Stride = 16;
        public const int SpawnRequestStride = 64;
        public const int LiquidMaterialParameterStride = 32;
        public const int ConsumeRequestStride = 48;
        public const int ConvertRequestStride = 48;
        public const int ColliderProxyStride = 64;
        public const int CollisionContactManifoldStride = 64;
        public const int CounterCount = 4;
        public const int ActivityCounterCount = 3;
        public const int MaterialAuditSlotCount = byte.MaxValue + 1;
        public const int AwakeActivityCounterIndex = 0;
        public const int SleepingActivityCounterIndex = 1;
        public const int InterestActivityCounterIndex = 2;
        public const int FreeCountCounterIndex = 0;
        public const int ActiveCountCounterIndex = 1;
        public const int DroppedParticleCountCounterIndex = 2;
        public const int NumericalErrorCounterIndex = 3;
        public const uint AliveFlag = FluidActivityFlags.Alive;
        public const uint InterestActiveFlag = FluidActivityFlags.InterestActive;
        public const uint RequiresSimulationFlag = FluidActivityFlags.RequiresSimulation;
        public const uint SleepingFlag = FluidActivityFlags.Sleeping;
        public const uint RemoteSpawnActiveFlag = FluidActivityFlags.RemoteSpawnActive;
        public const uint ArchiveLockedFlag = FluidActivityFlags.ArchiveLocked;
        public const uint RestoreLoadingFlag = FluidActivityFlags.RestoreLoading;
        public const int ArchiveSampleStride = FluidGpuArchiveSample.Stride;
        // 兼容既有 Test/工具构造“默认 awake 粒子”；新 Production 必须使用四个精确命名。
        public const uint ActiveFlag = AliveFlag | InterestActiveFlag | RequiresSimulationFlag;
        // v9 增加 ArchiveLocked/RestoreLoading；Metadata Stride 不变，所有消费者必须同步 flag 语义。
        public const uint LayoutVersion = 9u;
    }

    /// <summary>
    /// 与 HLSL uint2 对齐的值类型。Metadata 的 X 保存 materialId，Y 保存 active flags。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct FluidGpuUInt2
    {
        public readonly uint X;
        public readonly uint Y;

        public FluidGpuUInt2(uint x, uint y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public readonly struct FluidGpuUInt4
    {
        public readonly uint X;
        public readonly uint Y;
        public readonly uint Z;
        public readonly uint W;

        public FluidGpuUInt4(uint x, uint y, uint z, uint w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }
    }

    /// <summary>
    /// Spatial Hash Build 时固定的真实 cell。X/Y/Z 是 Floor(position/h)，W 是 active snapshot；
    /// PBF Iteration 即使修改 PredictedPosition 也继续消费它，保证一次 Substep 只排序一次时 Range 语义不漂移。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct FluidGpuInt4
    {
        public readonly int X;
        public readonly int Y;
        public readonly int Z;
        public readonly int W;

        public FluidGpuInt4(int x, int y, int z, int w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }
    }

    /// <summary>
    /// 每粒子最多保留四个独立接触法向；不能把它们求和成一个 Vector，因为相反法向会抵消，
    /// 墙角的 +X/+Y 也必须分别约束。每个 float4 的 w 是 active flag，固定总计 64 bytes。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct FluidCollisionContactManifold
    {
        public readonly Vector4 Contact0;
        public readonly Vector4 Contact1;
        public readonly Vector4 Contact2;
        public readonly Vector4 Contact3;

        public FluidCollisionContactManifold(
            Vector4 contact0,
            Vector4 contact1,
            Vector4 contact2,
            Vector4 contact3)
        {
            Contact0 = contact0;
            Contact1 = contact1;
            Contact2 = contact2;
            Contact3 = contact3;
        }
    }

    /// <summary>
    /// GPU 侧 Spawn Request：三个旧 Vector 保持原顺序，末尾追加 PackingParameters，固定 64 bytes，
    /// 使方向/半径与整型 material/seed/flags 在 HLSL 中保持无歧义的读取方式。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct FluidGpuSpawnRequest
    {
        public readonly Vector4 PositionRadius;
        public readonly Vector4 Velocity;
        public readonly FluidGpuUInt4 MaterialSeedFlagsParticleCount;
        public readonly Vector4 PackingParameters;

        public FluidGpuSpawnRequest(in FluidSpawnRequest request)
        {
            PositionRadius = new Vector4(
                request.WorldPosition.x,
                request.WorldPosition.y,
                request.WorldPosition.z,
                request.Radius);
            Velocity = new Vector4(
                request.InitialVelocity.x,
                request.InitialVelocity.y,
                request.InitialVelocity.z,
                0f);
            MaterialSeedFlagsParticleCount = new FluidGpuUInt4(
                request.MaterialId,
                request.Seed,
                request.Flags,
                request.ParticleCount);
            PackingParameters = new Vector4(request.RestSpacing, 0f, 0f, 0f);
        }
    }

    /// <summary>
    /// Spawn 的 flag 是 uint 常量而非 enum，保持既有 FluidSpawnRequest 的 uint Contract 不变。
    /// </summary>
    public static class FluidSpawnFlags
    {
        public const uint UseLinearFalloff = 1u;
        public const uint DensityPacked = 1u << 1;
    }

    /// <summary>
    /// CPU 入队边界的 Pure Validation。GPU Kernel 仍保留独立的防御性上限，
    /// 因为外部工具、测试或未来的错误调用可能绕过 Runtime Queue 直接写 Spawn Buffer。
    /// </summary>
    internal static class FluidSpawnRequestValidator
    {
        internal static bool IsValidForParticleCapacity(
            in FluidSpawnRequest request,
            int particleCapacity)
        {
            return particleCapacity > 0
                && request.ParticleCount > 0u
                && request.ParticleCount <= (uint)particleCapacity
                && IsNonNegativeFinite(request.Radius)
                && ((request.Flags & FluidSpawnFlags.DensityPacked) == 0u
                    ? IsNonNegativeFinite(request.RestSpacing)
                    : IsPositiveFinite(request.RestSpacing))
                && IsFinite(request.WorldPosition)
                && IsFinite(request.InitialVelocity);
        }

        internal static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsNonNegativeFinite(float value)
        {
            // Legacy ElementField 已把 Radius=0 定义为 point deposit；GPU offset 乘以 0 同样安全。
            return value >= 0f && !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsPositiveFinite(float value)
        {
            return value > 0f && IsFinite(value);
        }
    }
}
