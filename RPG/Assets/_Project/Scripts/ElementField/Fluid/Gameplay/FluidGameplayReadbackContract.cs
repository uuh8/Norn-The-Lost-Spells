using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    internal static class FluidReadbackIntervalPolicy
    {
        internal const float MinimumInterval = 0.05f;
        internal const float DefaultInterval = 0.25f;

        internal static float Sanitize(float configured)
        {
            if (float.IsNaN(configured) || float.IsInfinity(configured))
                return DefaultInterval;
            return Mathf.Max(MinimumInterval, configured);
        }
    }

    internal readonly struct FluidGameplayReadbackLease
    {
        internal readonly GraphicsBuffer PackedSamples;
        internal readonly FluidGameplayReadbackMetadata Metadata;

        internal FluidGameplayReadbackLease(
            GraphicsBuffer packedSamples,
            in FluidGameplayReadbackMetadata metadata)
        {
            PackedSamples = packedSamples;
            Metadata = metadata;
        }
    }

    internal interface IFluidGameplayReadbackSource
    {
        bool TryGetGameplayParticleCapacity(out int particleCapacity);

        bool TryAcquireGameplayReadbackLease(
            Vector3 worldOrigin,
            float cellSize,
            out FluidGameplayReadbackLease lease);

        void ReleaseGameplayReadbackLease();
    }

    /// <summary>
    /// Bridge 的 pure 状态机把 callback、LateUpdate 与 OnDestroy 三条路径合并为 exactly-once release。
    /// </summary>
    internal struct FluidReadbackRequestState
    {
        private bool _inFlight;
        private bool _completed;
        private bool _hasError;
        private bool _leaseHeld;
        private bool _destroyWaitUsed;

        internal bool IsInFlight => _inFlight;
        internal bool IsCompleted => _completed;

        internal bool TryBegin()
        {
            if (_inFlight || _leaseHeld)
                return false;

            _inFlight = true;
            _completed = false;
            _hasError = false;
            _leaseHeld = true;
            _destroyWaitUsed = false;
            return true;
        }

        internal void RecordCompletion(bool hasError)
        {
            if (!_inFlight || _completed)
                return;

            _hasError = hasError;
            _completed = true;
        }

        internal bool TryConsumeCompletion(out bool hasError)
        {
            if (!_completed)
            {
                hasError = false;
                return false;
            }

            hasError = _hasError;
            _completed = false;
            _inFlight = false;
            return true;
        }

        internal bool TryBeginDestroyWait()
        {
            if (!_inFlight || _completed || _destroyWaitUsed)
                return false;

            _destroyWaitUsed = true;
            return true;
        }

        internal bool TryCancelBeforeSubmission()
        {
            if (!_inFlight || _completed)
                return false;

            _inFlight = false;
            _completed = false;
            _hasError = false;
            return true;
        }

        internal bool TryReleaseLease()
        {
            if (!_leaseHeld)
                return false;

            _leaseHeld = false;
            return true;
        }
    }

    /// <summary>Source 侧 lease 防止 AsyncGPUReadback 完成前释放其 GraphicsBuffer。</summary>
    internal struct FluidReadbackLeaseTracker
    {
        private bool _leaseHeld;
        private bool _releasePending;

        internal bool IsReleasePending => _releasePending;

        internal bool TryAcquire()
        {
            if (_leaseHeld || _releasePending)
                return false;
            _leaseHeld = true;
            return true;
        }

        internal bool RequestRelease()
        {
            _releasePending = true;
            return !_leaseHeld;
        }

        internal bool ReleaseLease()
        {
            if (!_leaseHeld)
                return false;
            _leaseHeld = false;
            return _releasePending;
        }

        internal void CancelPendingRelease()
        {
            _releasePending = false;
        }

        internal void Reset()
        {
            _leaseHeld = false;
            _releasePending = false;
        }
    }

    /// <summary>
    /// Exposure 的两份数据可能属于不同时间：Legacy Bounds 是当前帧，Liquid Bounds 是低频 Readback 的冻结快照。
    /// 保留两个独立 Physics Query，避免把相距很远的区域合成一个巨大 OverlapBox 后让中间无关 Collider
    /// 抢占固定容量 NonAlloc Buffer。Query 0 永远是 Legacy/Fire，确保 Burning 的权威路径优先。
    /// </summary>
    internal readonly struct ElementExposureBroadphasePlan
    {
        private readonly Bounds _legacyBounds;
        private readonly Bounds _liquidBounds;

        internal ElementExposureBroadphasePlan(
            Bounds legacyBounds,
            Bounds liquidBounds,
            int queryCount)
        {
            _legacyBounds = legacyBounds;
            _liquidBounds = liquidBounds;
            QueryCount = queryCount;
        }

        internal int QueryCount { get; }

        internal Bounds GetQueryBounds(int index)
        {
            return index == 0 ? _legacyBounds : _liquidBounds;
        }
    }

    internal static class ElementExposureBroadphasePlanner
    {
        internal static ElementExposureBroadphasePlan Plan(
            WaterSimulationMode mode,
            Bounds legacyBounds,
            bool hasLiquidSnapshot,
            Bounds liquidBounds)
        {
            bool needsLiquidQuery = mode == WaterSimulationMode.GpuPbf
                && hasLiquidSnapshot
                && liquidBounds != legacyBounds;
            return new ElementExposureBroadphasePlan(
                legacyBounds,
                liquidBounds,
                needsLiquidQuery ? 2 : 1);
        }
    }

    internal static class FluidSnapshotPublishPolicy
    {
        internal static bool ShouldPublish(
            bool requestHasError,
            bool stagingRebuildSucceeded)
        {
            return !requestHasError && stagingRebuildSucceeded;
        }
    }
}
