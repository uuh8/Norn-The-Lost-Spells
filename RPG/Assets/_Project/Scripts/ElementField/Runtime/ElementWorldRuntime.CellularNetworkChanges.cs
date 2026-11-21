using System;
using System.Collections.Generic;
using Game.Core;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class ElementWorldRuntime
    {
        private WorldChunkKey[] _networkChunkChanges;
        private HashSet<ElementChunkKey> _networkBatch;
        private ulong _networkCursor;
        internal ElementWriteQueue NetworkPending => _writeQueue;
        internal ulong NetworkCursor => _networkCursor;
        internal bool HasUnpublishedNetworkCells => CellularSimulation.ChangedCellCount != 0 || RestingLiquids.HasUnpublishedReplicationChanges;

        internal void EnableNetworkChangeJournal()
        {
            if (_networkChunkChanges != null) return;
            _networkChunkChanges = new WorldChunkKey[Math.Max(256, Math.Min(32768, _cellDefinition.MaximumCells))];
            _networkBatch = new HashSet<ElementChunkKey>();
            _store.ObserveMaterialChanges(RecordLegacyNetworkChunk);
        }

        internal void BeginNetworkCaptureBatch() => _store.AdvanceCaptureBatch();
        private void RecordLegacyNetworkChunk(ElementChunkKey key) => AppendNetworkChange(key, WorldMaterialDomain.LegacyCell);

        private void RecordCellularNetworkBatch(List<Vector3Int> cells)
        {
            if (_networkChunkChanges == null || cells.Count == 0) return;
            _networkBatch.Clear();
            foreach (var cell in cells)
            {
                ElementWorldCoordinates.GlobalCellToChunkAndLocal(cell, ChunkSize, out var key, out _);
                if (_networkBatch.Add(key)) AppendNetworkChange(key);
            }
        }

        private void RecordCellularNetworkCell(Vector3Int cell)
        {
            if (_networkChunkChanges == null) return;
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(cell, ChunkSize, out var key, out _);
            AppendNetworkChange(key);
        }

        private void AppendNetworkChange(ElementChunkKey key, WorldMaterialDomain domain = WorldMaterialDomain.Cellular)
        {
            _networkChunkChanges[(int)(_networkCursor % (ulong)_networkChunkChanges.Length)] = new WorldChunkKey(domain, key.X, key.Y, key.Z);
            _networkCursor++;
        }

        internal bool CollectNetworkChanges(ulong since, HashSet<WorldChunkKey> result)
        {
            if (_networkChunkChanges == null || since > _networkCursor || _networkCursor - since > (ulong)_networkChunkChanges.Length)
                return false;
            while (since != _networkCursor)
            {
                var key = _networkChunkChanges[(int)(since++ % (ulong)_networkChunkChanges.Length)];
                result.Add(key);
            }
            return true;
        }

        internal bool VisitChangedNetworkChunks(HashSet<WorldChunkKey> changes, ref WorldCaptureWriter writer)
        {
            var geometry = new WorldGridGeometry(WorldMaterialDomain.Cellular, Origin, CellSize, ChunkSize, CellularBaseVersion, CellularCellCapacity);
            if (!writer.Geometry(WorldMaterialDomain.LegacyCell, Origin, CellSize, ChunkSize)
                || !writer.Geometry(geometry.Domain, Origin, CellSize, ChunkSize, geometry.BaseVersion, geometry.CellCapacity)) return false;
            foreach (var key in changes)
            {
                if (key.Domain == WorldMaterialDomain.LegacyCell)
                {
                    if (!_store.TryGetChunk(new ElementChunkKey(key.X, key.Y, key.Z), out var legacy) || !legacy.HasAnyElement) continue;
                    for (int i = 0; i < legacy.CellCount; i++)
                    {
                        if (!writer.Inspect()) return false;
                        var value = legacy.CurrentCells[i];
                        if (value.IsEmpty) continue;
                        var local = new Vector3Int(i % ChunkSize, i / ChunkSize % ChunkSize, i / (ChunkSize * ChunkSize));
                        if (!writer.Cell(key.Domain, WorldMaterialStorage.Resident, ElementWorldCoordinates.ComposeGlobalCell(legacy.Key, local, ChunkSize),
                            (byte)value.MaterialKind, value.Amount)) return false;
                    }
                }
                else if (!VisitCellularNetworkChunk(key, ref writer)) return false;
            }
            // Pending 的 QueueIndex 是全局 FIFO 序号。重写每个点名块时连同属于它的全部请求一起替换。
            for (int i = 0; i < _writeQueue.Count; i++)
            {
                if (!writer.Inspect()) return false;
                _writeQueue.TryPeek(i, out var request, out long retry);
                if (!WorldChunkPayloadCodec.TryPositionKey(geometry, request.WorldPosition, out var key)) return false;
                if (changes.Contains(key) && !writer.Pending(geometry.Domain, i, in request, request.TotalAmount, 0, 0, retry)) return false;
            }
            return true;
        }

        private bool VisitCellularNetworkChunk(WorldChunkKey key, ref WorldCaptureWriter writer)
        {
            // 半开整数 Bounds 不能包含 int.MaxValue 本身，动态点仍然可以。循环端点用 long，
            // 静水树只接受可表示的半开区间。常用 8³ 页仅查询 512 格，与历史挖掘范围无关。
            long x = (long)key.X * ChunkSize, y = (long)key.Y * ChunkSize, z = (long)key.Z * ChunkSize;
            long hiX = Math.Min((long)int.MaxValue + 1, x + ChunkSize), hiY = Math.Min((long)int.MaxValue + 1, y + ChunkSize),
                hiZ = Math.Min((long)int.MaxValue + 1, z + ChunkSize);
            x = Math.Max(int.MinValue, x); y = Math.Max(int.MinValue, y); z = Math.Max(int.MinValue, z);
            for (long cz = z; cz < hiZ; cz++)
                for (long cy = y; cy < hiY; cy++)
                    for (long cx = x; cx < hiX; cx++)
                    {
                        if (!writer.Inspect()) return false;
                        var c = new Vector3Int((int)cx, (int)cy, (int)cz);
                        if (_cellularTerrain.TryGetNetworkOverride(c, out var terrain) &&
                            !writer.Span(WorldCellularSpanKind.Terrain, new BoundsInt(c, Vector3Int.one), (byte)terrain, 0, 0, ChunkSize)) return false;
                        if (!TryGetCellularContents(c, out var primary, out uint amount, out uint steam, out uint toxic)) continue;
                        if (!writer.Cell(key.Domain, WorldMaterialStorage.Resident, c, (byte)primary, amount)
                            || !writer.Cell(key.Domain, WorldMaterialStorage.Resident, c, (byte)MaterialId.Steam, steam)
                            || !writer.Cell(key.Domain, WorldMaterialStorage.Resident, c, (byte)MaterialId.ToxicVapor, toxic)) return false;
                    }
            var lo = new Vector3Int((int)x, (int)y, (int)z);
            var hi = new Vector3Int((int)Math.Min(int.MaxValue, hiX), (int)Math.Min(int.MaxValue, hiY), (int)Math.Min(int.MaxValue, hiZ));
            return RestingLiquids.CaptureChunkChanges(new BoundsInt(lo, hi - lo), ChunkSize, ref writer);
        }
    }
}
