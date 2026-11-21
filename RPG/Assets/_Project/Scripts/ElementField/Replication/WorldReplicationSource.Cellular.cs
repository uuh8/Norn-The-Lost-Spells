using System.Collections.Generic;
using Game.Core;

namespace Game.ElementField
{
    public sealed partial class WorldReplicationSource
    {
        private WorldReplicaBuffer _lastBuffer;
        private ulong _lastCursor, _lastQueueVersion;
        private readonly HashSet<WorldChunkKey> _pendingKeys = new HashSet<WorldChunkKey>();
        private readonly HashSet<WorldChunkKey> _changedKeys = new HashSet<WorldChunkKey>();

        private bool CanReuse(WorldReplicaBuffer destination) => _world != null && _world.IsInitialized && _world.UsesCellularWorld &&
            _world.ExecutionMode != WorldExecutionMode.Replica && !_world.IsWorldStepInProgress && !_world.IsSimulationFaulted &&
            !_world.HasUnpublishedNetworkCells &&
            destination != null && ReferenceEquals(destination, _lastBuffer) && destination.Header.WorldTick <= _world.WorldTick;

        public bool TryAdvanceUnchangedCapture(WorldReplicaBuffer committedBuffer)
        {
            if (!CanReuse(committedBuffer) || _world.NetworkCursor != _lastCursor || QueueChanged()) return false;
            var header = committedBuffer.Header;
            committedBuffer.Header = new WorldReplicaHeader(_world.WorldTick, header.CellCount, header.PendingCount, header.GeometryCount, header.SpanCount);
            return true;
        }

        public bool TryCaptureChangedChunks(WorldReplicaBuffer destination, out int changedChunkCount, out WorldCaptureFailure failure)
        {
            changedChunkCount = 0; failure = WorldCaptureFailure.None;
            if (!CanReuse(destination)) return false;
            _changedKeys.Clear();
            if (!_world.CollectNetworkChanges(_lastCursor, _changedKeys)) return false;
            if (QueueChanged())
            {
                foreach (var key in _pendingKeys) _changedKeys.Add(key);
                if (!CollectPending(_changedKeys)) return false;
            }
            if (_changedKeys.Count == 0 || _changedKeys.Count > destination.ChangedChunks.Length) return false;
            var preflight = new WorldCaptureWriter(destination, false);
            if (!_world.VisitChangedNetworkChunks(_changedKeys, ref preflight))
            {
                // 极大的稀疏 Chunk 点查工作量可能比全场稀疏枚举更大，此时全捕获仍有机会在预算内完成。
                if (preflight.Failure != WorldCaptureFailure.InspectionBudget) failure = preflight.Failure;
                return false;
            }
            var copy = new WorldCaptureWriter(destination, true);
            _world.VisitChangedNetworkChunks(_changedKeys, ref copy);
            foreach (var key in _changedKeys) destination.ChangedChunks[changedChunkCount++] = key;
            destination.Header = new WorldReplicaHeader(_world.WorldTick, copy.CellCount, copy.PendingCount, copy.GeometryCount, copy.SpanCount);
            RememberCellularCapture(destination);
            return true;
        }

        private bool QueueChanged() => _lastQueueVersion != _world.NetworkPending.Revision &&
            (_pendingKeys.Count != 0 || _world.NetworkPending.Count != 0);

        private bool CollectPending(HashSet<WorldChunkKey> keys)
        {
            var geometry = new WorldGridGeometry(WorldMaterialDomain.Cellular, _world.Origin, _world.CellSize, _world.ChunkSize,
                _world.CellularBaseVersion, _world.CellularCellCapacity);
            var fifo = _world.NetworkPending;
            for (int i = 0; i < fifo.Count; i++)
            {
                if (!fifo.TryPeek(i, out var request) || !WorldChunkPayloadCodec.TryPositionKey(geometry, request.WorldPosition, out var key))
                    return false;
                keys.Add(key);
            }
            return true;
        }

        private void RememberCellularCapture(WorldReplicaBuffer buffer)
        {
            _world.EnableNetworkChangeJournal();
            // 首次/队列真变时记录旧目的块，下一批有移动或清空才同时替换旧、新页。
            // 地形、冷水、动态元胞和旧 Fire 都在权威写口登记域地址，空闲 Tick 不扫描任一材料 Store。
            bool refreshPending=!ReferenceEquals(buffer,_lastBuffer) || _lastQueueVersion!=_world.NetworkPending.Revision;
            _lastBuffer=buffer;_lastCursor=_world.NetworkCursor;_lastQueueVersion=_world.NetworkPending.Revision;
            if(refreshPending)
            {
                _pendingKeys.Clear();
                if(!CollectPending(_pendingKeys))_lastBuffer=null;
            }
            _world.BeginNetworkCaptureBatch();
        }
    }
}
