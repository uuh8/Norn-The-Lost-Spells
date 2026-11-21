using Game.Core;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class ElementWorldRuntime
    {
        internal const string CellularReplicationUnavailable =
            "Cellular 联机世界缺少可信基底、格距或容量配置。";

        // 在 inactive prefab 上也读取同一份世界配置。等模拟开始或等到第一格被挖才判断，
        // 会让客户端先 ACK 一份缺少地形覆盖的 Baseline，后续 Delta 无法补回这些历史修改。
        internal bool SupportsConfiguredOnlineReplication()
        {
            var cellular = ResolveCellularContentProfile();
            if (cellular == null) return _cellularContentProfile == null && (_profile == null || _profile.CellularWorld == null);
            return _profile != null && cellular.Capacity > 0 && _profile.ChunkSize > 0 && float.IsFinite(_profile.CellSize) &&
                _profile.CellSize > 0 && CellularBaseVersion != 0;
        }

        internal int CellularCaptureRowLimit(int firstPage,int directoryCapacity,int maximumWords)
        {
            var cellular=ResolveCellularContentProfile();
            if(cellular==null || _profile==null || directoryCapacity<=0)return firstPage;
            // 每份动态细格至多主材与两种气体。Legacy 火在另一原生域独占一行，两种上限都来自同一正式地图。
            // 单块正文与目录再提供独立上限。这里仅计算许可行数，后端溢出页按实际占用申请。
            int perChunk=maximumWords/WorldChunkPayloadCodec.CellWords,edge=_profile.ChunkSize;
            long legacyRows=(long)Mathf.Min(directoryCapacity,_profile.MaximumResidentChunks)*
                (long)System.Math.Min(perChunk,(double)edge*edge*edge);
            long rows=System.Math.Min(3L*cellular.MaximumCells+legacyRows,(long)directoryCapacity*perChunk);
            return (int)System.Math.Max(firstPage,System.Math.Min(int.MaxValue,rows));
        }

        private ulong _cellularBaseFingerprint;
        internal ulong CellularBaseVersion
        {
            get
            {
                if (_cellularBaseFingerprint != 0) return _cellularBaseFingerprint;
                var profile = ResolveCellularContentProfile();
                if (profile == null || _profile == null) return 0;
                // 指纹只取各端同一份不可变内容，不读 Unity InstanceID、进程 hash 或场景状态。
                // 数量采用 uint，整数/浮点的原始字和顺序固定，避免文化格式化差异。
                ulong value = 14695981039346656037UL;
                ulong map = CellularMapBaseFingerprint;
                Mix(ref value, (uint)map); Mix(ref value, (uint)(map >> 32));
                string content = CellularMapContentId;
                Mix(ref value, (uint)(content?.Length ?? 0));
                if (content != null) foreach (char letter in content) Mix(ref value, letter);
                Mix(ref value, (uint)_profile.ChunkSize); Mix(ref value, WorldChunkPayloadCodec.FloatWord(_profile.CellSize));
                Mix(ref value, (uint)profile.Capacity); Mix(ref value, (uint)profile.RegionCount);
                for (int i = 0; i < profile.RegionCount; i++)
                {
                    var region = profile.Region(i);
                    MixBox(ref value, region.Cells, region.Material);
                }
                var water = ResolveCellularRestingRegions(profile);
                Mix(ref value, (uint)(water?.Length ?? 0));
                if (water != null) foreach (var region in water) MixBox(ref value, region.Cells, region.Material);
                _cellularBaseFingerprint = value == 0 ? 1 : value;
                return _cellularBaseFingerprint;
            }
        }

        private static void Mix(ref ulong hash, uint word)
        {
            unchecked { for (int i = 0; i < 4; i++) { hash = (hash ^ (word & 255)) * 1099511628211UL; word >>= 8; } }
        }

        private static void MixBox(ref ulong hash, BoundsInt box, MaterialId material)
        {
            Mix(ref hash, (byte)material);
            for (int axis = 0; axis < 3; axis++) { Mix(ref hash, unchecked((uint)box.min[axis])); Mix(ref hash, (uint)box.size[axis]); }
        }

        /// <summary>
        /// 只枚举真实Store，不取Presentation Interest；Source先验证完整遍历再复制。
        /// Dictionary的具体类型枚举器是struct，不通过IEnumerable装箱，不创建临时keys数组。
        /// </summary>
        internal bool VisitReplicationState(ref WorldCaptureWriter writer)
        {
            if (!writer.Geometry(WorldMaterialDomain.LegacyCell, Origin, CellSize, ChunkSize)) return false;
            foreach (var pair in _store.Chunks)
            {
                ElementWorldChunk chunk = pair.Value;
                int size = chunk.ChunkSize;
                for (int i = 0; i < chunk.CellCount; i++)
                {
                    if (!writer.Inspect()) return false;
                    ElementCell cell = chunk.CurrentCells[i];
                    if (cell.IsEmpty) continue;
                    var local = new Vector3Int(i % size, i / size % size, i / (size * size));
                    Vector3Int native = ElementWorldCoordinates.ComposeGlobalCell(chunk.Key, local, size);
                    if (!writer.Cell(WorldMaterialDomain.LegacyCell, WorldMaterialStorage.Resident, native,
                        (byte)cell.MaterialKind, cell.Amount)) return false;
                }
            }
            if (_writeQueue != null)
                for (int i = 0; i < _writeQueue.Count; i++)
                {
                    _writeQueue.TryPeek(i, out ElementWriteRequest request, out long retryTick);
                    if (!writer.Pending(UsesCellularWorld ? WorldMaterialDomain.Cellular : WorldMaterialDomain.LegacyCell,
                        i, in request, request.TotalAmount, 0, 0, retryTick)) return false;
                }
            if (CellularSimulation != null)
            {
                if (!writer.Geometry(WorldMaterialDomain.Cellular, Origin, CellSize, ChunkSize, CellularBaseVersion, CellularCellCapacity)
                    || !_cellularTerrain.CaptureOverrides(ref writer, ChunkSize)
                    || !RestingLiquids.CaptureChanges(ref writer, ChunkSize)) return false;
                for (int i = 0; i < CellularSimulation.OccupiedCellCount; i++)
                {
                    if (!writer.Inspect()) return false;
                    if (!CellularSimulation.TryGetOccupiedCoordinate(i, out var cell)) continue;
                    TryGetCellularContents(cell, out var primary, out uint amount, out uint steam, out uint toxic);
                    if (!writer.Cell(WorldMaterialDomain.Cellular, WorldMaterialStorage.Resident, cell, (byte)primary, amount)
                        || !writer.Cell(WorldMaterialDomain.Cellular, WorldMaterialStorage.Resident, cell, (byte)MaterialId.Steam, steam)
                        || !writer.Cell(WorldMaterialDomain.Cellular, WorldMaterialStorage.Resident, cell, (byte)MaterialId.ToxicVapor, toxic))
                        return false;
                }
                return true;
            }
            if (_gridLiquidRuntime != null && !_gridLiquidRuntime.VisitReplicationState(ref writer)) return false;
            if (_fixedSolidRuntime != null && !_fixedSolidRuntime.VisitReplicationState(ref writer)) return false;
            return _gasRuntime == null || _gasRuntime.VisitReplicationState(ref writer);
        }
    }
}
