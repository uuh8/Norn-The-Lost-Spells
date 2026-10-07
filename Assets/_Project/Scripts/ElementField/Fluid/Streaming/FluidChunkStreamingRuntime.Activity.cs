using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class FluidChunkStreamingRuntime : IFluidVisibleArchiveReadOnly
    {
        private readonly ElementWorldActivityRegions _warmActivityRegions = new ElementWorldActivityRegions();
        private readonly ElementWorldActivityRegions _nextActivityRegions = new ElementWorldActivityRegions();
        private readonly ElementWorldActivityRegions _frozenActivityRegions = new ElementWorldActivityRegions();
        private bool _hasActivityRegions;
        private readonly FluidChunkWakeQueue _wakeQueue = new FluidChunkWakeQueue(128);
        public int PendingWakeCount => _wakeQueue.Count + (_hasRequestedRestoreChunk ? 1 : 0);
        public uint RejectedWakeCount => _wakeQueue.RejectedCount;

        private void PromoteQueuedWake()
        {
            if (_hasRequestedRestoreChunk) return;
            while (_wakeQueue.TryDequeue(out ElementChunkKey key, out FluidDormantWakeReason reason))
            {
                if (!IsRetainedChunk(key) || !_store.ContainsChunk(key)) continue;
                _requestedRestoreChunk = key;
                _hasRequestedRestoreChunk = true;
                _debugLastDormantWakeReason = reason;
                break;
            }
        }

        internal void SetWorldActivityRegions(ElementWorldActivityRegions source)
        {
            if (!_isInitialized) return;
            _nextActivityRegions.Clear();
            float padding = _settings.WarmPaddingChunks * _cellSize * _chunkSize * 2f;
            for (int i = 0; i < source.Count; i++)
            {
                Bounds bounds = source.GetBounds(i);
                bounds.Expand(padding);
                _nextActivityRegions.TryAdd(bounds);
            }
            bool changed = _warmActivityRegions.Count != _nextActivityRegions.Count;
            for (int i = 0; !changed && i < _nextActivityRegions.Count; i++)
                changed = _nextActivityRegions.GetBounds(i) != _warmActivityRegions.GetBounds(i);
            _hasActivityRegions = true;
            if (!changed) return;
            _warmActivityRegions.CopyFrom(_nextActivityRegions);
            _archiveAfterInterestChange = true;
            _interestChangedAt = Time.realtimeSinceStartupAsDouble;
        }

        private bool IsRetainedChunk(ElementChunkKey key) => _hasActivityRegions
            ? _warmActivityRegions.Intersects(FluidChunkArchiveStore.ChunkBounds(key, _worldOrigin, _cellSize * _chunkSize))
            : _retainedRegion.Contains(key);

        private bool IsFrozenRetained(Vector3 position, Bounds fallback) => _frozenActivityRegions.Count > 0
            ? _frozenActivityRegions.Contains(position) : fallback.Contains(position);

        private bool TrySelectColdChunk(out ElementChunkKey key) => _hasActivityRegions
            ? _store.TryFindColdChunk(_warmActivityRegions, _worldOrigin, _cellSize * _chunkSize, out key)
            : _store.TryFindNearestChunk(_interestChunk, in _retainedRegion,
                FluidArchivedChunkKind.Cold, false, out key);

        public int CopyVisibleArchiveCells(MaterialId material, Bounds visible, LiquidMaterialCellSample[] destination)
        {
            if (!_isInitialized || destination == null) return 0;
            // Camera 读 RAM 的 Cold/Dormant 代理不请求 Restore，纯 Presentation 不能改变物理成本。
            return _store.CopyRegionCells(null, visible, true, _worldOrigin, _cellSize * _chunkSize, material, destination);
        }
    }
}
