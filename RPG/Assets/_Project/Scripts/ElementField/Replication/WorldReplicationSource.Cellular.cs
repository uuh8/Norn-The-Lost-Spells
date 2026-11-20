using System.Collections.Generic;
using Game.Core;

namespace Game.ElementField
{
    public sealed partial class WorldReplicationSource
    {
        private struct LegacyStamp
        {
            internal ElementWorldChunk Owner;
            internal uint SimulationVersion;
            internal ulong DirectWrite;
            internal bool Matches(ElementWorldChunk chunk) => ReferenceEquals(Owner, chunk) &&
                SimulationVersion == chunk.Grid.GetChunkVersion(0, 0, 0) && DirectWrite == chunk.DirectWriteVersion;
        }

        private WorldReplicaBuffer _lastBuffer;
        private ulong _lastCursor, _lastQueueVersion;
        private readonly Dictionary<ElementChunkKey, LegacyStamp> _legacy = new Dictionary<ElementChunkKey, LegacyStamp>();
        private readonly HashSet<WorldChunkKey> _pendingKeys = new HashSet<WorldChunkKey>();
        private readonly HashSet<WorldChunkKey> _changedKeys = new HashSet<WorldChunkKey>();

        private bool CanReuse(WorldReplicaBuffer destination) => _world != null && _world.IsInitialized && _world.UsesCellularWorld &&
            _world.ExecutionMode != WorldExecutionMode.Replica && !_world.IsWorldStepInProgress && !_world.IsSimulationFaulted &&
            !_world.HasUnpublishedNetworkCells &&
            destination != null && ReferenceEquals(destination, _lastBuffer) && destination.Header.WorldTick <= _world.WorldTick;

        public bool TryAdvanceUnchangedCapture(WorldReplicaBuffer committedBuffer)
        {
            if (!CanReuse(committedBuffer) || _world.NetworkCursor != _lastCursor || QueueChanged()) return false;
            int count = 0;
            foreach (var pair in _world.NetworkLegacyChunks)
            {
                if (!pair.Value.HasAnyElement) continue;
                if (!_legacy.TryGetValue(pair.Key, out var stamp) || !stamp.Matches(pair.Value)) return false;
                count++;
            }
            if (count != _legacy.Count) return false;
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
            foreach (var pair in _world.NetworkLegacyChunks)
                if (pair.Value.HasAnyElement && (!_legacy.TryGetValue(pair.Key, out var stamp) || !stamp.Matches(pair.Value)))
                    _changedKeys.Add(LegacyKey(pair.Key));
            foreach (var pair in _legacy)
                if (!_world.NetworkLegacyChunks.TryGetValue(pair.Key, out var current) || !current.HasAnyElement)
                    _changedKeys.Add(LegacyKey(pair.Key));
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

        private static WorldChunkKey LegacyKey(ElementChunkKey key) => new WorldChunkKey(WorldMaterialDomain.LegacyCell, key.X, key.Y, key.Z);
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
            _lastBuffer = buffer; _lastCursor = _world.NetworkCursor; _lastQueueVersion = _world.NetworkPending.Revision;
            _legacy.Clear(); _pendingKeys.Clear();
            foreach (var pair in _world.NetworkLegacyChunks)
            {
                var chunk = pair.Value;
                if (!chunk.HasAnyElement) continue;
                _legacy.Add(pair.Key, new LegacyStamp { Owner = chunk, SimulationVersion = chunk.Grid.GetChunkVersion(0, 0, 0),
                    DirectWrite = chunk.DirectWriteVersion });
            }
            if (!CollectPending(_pendingKeys)) _lastBuffer = null;
        }
    }
}
