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
        private struct NetworkCell
        {
            internal MaterialId Material;
            internal uint Amount,Steam,Toxic;
        }
        private sealed class NetworkPage
        {
            internal readonly Dictionary<Vector3Int,NetworkCell> Points=new Dictionary<Vector3Int,NetworkCell>();
            internal readonly Dictionary<Vector3Int,MaterialId> Terrain=new Dictionary<Vector3Int,MaterialId>();
        }
        private Dictionary<ElementChunkKey,NetworkPage> _networkPages;
        private Stack<NetworkPage> _networkPagePool;
        internal ElementWriteQueue NetworkPending => _writeQueue;
        internal ulong NetworkCursor => _networkCursor;
        internal bool HasUnpublishedNetworkCells => CellularSimulation.ChangedCellCount != 0 || RestingLiquids.HasUnpublishedReplicationChanges;

        internal void EnableNetworkChangeJournal()
        {
            if (_networkChunkChanges != null) return;
            _networkChunkChanges = new WorldChunkKey[Math.Max(256, Math.Min(32768, _cellDefinition.MaximumCells))];
            _networkBatch = new HashSet<ElementChunkKey>();
            _networkPages=new Dictionary<ElementChunkKey,NetworkPage>();
            _networkPagePool=new Stack<NetworkPage>();
            _store.ObserveMaterialChanges(RecordLegacyNetworkChunk);
        }

        internal void BeginNetworkCaptureBatch() => _store.AdvanceCaptureBatch();
        private void RecordLegacyNetworkChunk(ElementChunkKey key) => AppendNetworkChange(key, WorldMaterialDomain.LegacyCell);

        // 完整 Baseline 或日志溢出的全量恢复只重建已出现的点/挖掘覆盖。静水仍来自原压缩所有者。
        // 页内保存最新已提交的值，之后每个脏格只向权威表查询一次，避免一次运动两遍扫描每块 8³ 个空地址。
        internal void IndexFullCellularCapture(WorldReplicaBuffer snapshot)
        {
            foreach(var entry in _networkPages)RecycleNetworkPage(entry.Value);
            _networkPages.Clear();
            for(int i=0;i<snapshot.Header.CellCount;i++)
            {
                var row=snapshot.ReadCellRow(i);if(row.Domain!=WorldMaterialDomain.Cellular)continue;
                ElementWorldCoordinates.GlobalCellToChunkAndLocal(row.Cell,ChunkSize,out var key,out _);
                var page=NetworkPageFor(key);
                page.Points.TryGetValue(row.Cell,out var value);
                var material=(MaterialId)row.MaterialId;
                if(material==MaterialId.Steam)value.Steam=row.Quantity;
                else if(material==MaterialId.ToxicVapor)value.Toxic=row.Quantity;
                else {value.Material=material;value.Amount=row.Quantity;}
                page.Points[row.Cell]=value;
            }
            for(int i=0;i<snapshot.Header.SpanCount;i++)
            {
                var span=snapshot.ReadSpanRow(i);if(span.Kind!=WorldCellularSpanKind.Terrain)continue;
                ElementWorldCoordinates.GlobalCellToChunkAndLocal(span.Cells.position,ChunkSize,out var key,out _);
                var page=NetworkPageFor(key);
                // CaptureOverrides 当前输出单格；若将来合并同类覆盖，仍能按单原生块精确重建。
                foreach(var cell in span.Cells.allPositionsWithin)page.Terrain[cell]=(MaterialId)span.MaterialId;
            }
        }

        private NetworkPage NetworkPageFor(ElementChunkKey key)
        {
            if(_networkPages.TryGetValue(key,out var page))return page;
            page=_networkPagePool.Count!=0?_networkPagePool.Pop():new NetworkPage();
            _networkPages.Add(key,page);return page;
        }
        private void RecycleNetworkPage(NetworkPage page)
        {
            page.Points.Clear();page.Terrain.Clear();
            if(_networkPagePool.Count<32)_networkPagePool.Push(page);
        }
        private void ReleaseNetworkPageIfEmpty(ElementChunkKey key,NetworkPage page)
        {
            if(page.Points.Count!=0 || page.Terrain.Count!=0)return;
            _networkPages.Remove(key);RecycleNetworkPage(page);
        }

        private void RecordCellularNetworkBatch(List<Vector3Int> cells)
        {
            if (_networkChunkChanges == null || cells.Count == 0) return;
            _networkBatch.Clear();
            foreach (var cell in cells)
            {
                ElementWorldCoordinates.GlobalCellToChunkAndLocal(cell, ChunkSize, out var key, out _);
                if(CellularSimulation.TryReadContents(cell,out var material,out uint amount,out uint steam,out uint toxic))
                    NetworkPageFor(key).Points[cell]=new NetworkCell{Material=material,Amount=amount,Steam=steam,Toxic=toxic};
                else if(_networkPages.TryGetValue(key,out var page) && page.Points.Remove(cell))
                    ReleaseNetworkPageIfEmpty(key,page);
                if (_networkBatch.Add(key)) AppendNetworkChange(key);
            }
        }

        private void RecordCellularNetworkCell(Vector3Int cell)
        {
            if (_networkChunkChanges == null) return;
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(cell, ChunkSize, out var key, out _);
            if(_cellularTerrain.TryGetNetworkOverride(cell,out var material))NetworkPageFor(key).Terrain[cell]=material;
            else if(_networkPages.TryGetValue(key,out var page) && page.Terrain.Remove(cell))ReleaseNetworkPageIfEmpty(key,page);
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
            var pageKey=new ElementChunkKey(key.X,key.Y,key.Z);
            if(_networkPages.TryGetValue(pageKey,out var page))
            {
                foreach(var point in page.Points)
                {
                    var c=point.Key;var value=point.Value;
                    if(!writer.Inspect() || !writer.Cell(key.Domain,WorldMaterialStorage.Resident,c,(byte)value.Material,value.Amount)
                        || !writer.Cell(key.Domain,WorldMaterialStorage.Resident,c,(byte)MaterialId.Steam,value.Steam)
                        || !writer.Cell(key.Domain,WorldMaterialStorage.Resident,c,(byte)MaterialId.ToxicVapor,value.Toxic))return false;
                }
                foreach(var terrain in page.Terrain)
                    if(!writer.Span(WorldCellularSpanKind.Terrain,new BoundsInt(terrain.Key,Vector3Int.one),(byte)terrain.Value,0,0,ChunkSize))return false;
            }
            // 半开整数 Bounds 不能包含 int.MaxValue 本身，动态点仍然可以。循环端点用 long，
            // 静水树只接受可表示的半开区间。常用 8³ 页仅查询 512 格，与历史挖掘范围无关。
            long x = (long)key.X * ChunkSize, y = (long)key.Y * ChunkSize, z = (long)key.Z * ChunkSize;
            long hiX = Math.Min((long)int.MaxValue + 1, x + ChunkSize), hiY = Math.Min((long)int.MaxValue + 1, y + ChunkSize),
                hiZ = Math.Min((long)int.MaxValue + 1, z + ChunkSize);
            x = Math.Max(int.MinValue, x); y = Math.Max(int.MinValue, y); z = Math.Max(int.MinValue, z);
            var lo = new Vector3Int((int)x, (int)y, (int)z);
            var hi = new Vector3Int((int)Math.Min(int.MaxValue, hiX), (int)Math.Min(int.MaxValue, hiY), (int)Math.Min(int.MaxValue, hiZ));
            return RestingLiquids.CaptureChunkChanges(new BoundsInt(lo, hi - lo), ChunkSize, ref writer);
        }
    }

    public sealed partial class ElementCellSimulation
    {
        // 导出同一时刻的物质主槽和两个独立气体通道，只查一次库存索引。该 API 不持有任何写权。
        internal bool TryReadContents(Vector3Int cell,out MaterialId primary,out uint amount,out uint steam,out uint toxic)
        {
            var value=Read(cell);primary=value.Bulk;amount=value.Amount;steam=value.Steam;toxic=value.Vapor;
            return !value.Empty;
        }
    }
}
