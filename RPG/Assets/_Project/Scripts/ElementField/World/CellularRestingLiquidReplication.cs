using System;
using System.Collections.Generic;
using Game.Core;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class CellularRestingLiquidStore
    {
        private readonly List<int> _captureRegions = new List<int>();
        private Action<int> _captureRegion;
        internal bool HasUnpublishedReplicationChanges => _changed.Count != 0;
        internal MaterialId AuthoredMaterial(Vector3Int cell)
        {
            int index = _index.Length == 0 ? -1 : Find(0, cell);
            return index < 0 ? MaterialId.Empty : _regions[index].Material;
        }

        internal bool CaptureChanges(ref WorldCaptureWriter writer, int chunkSize)
        {
            // 初始湖只由可信基底描述。经过编辑的区域跳过仍为初值的整片叶，动态归档液体只枚举当前存活的页。
            for (int i = 0; i < _regions.Length; i++)
            {
                if (!writer.Inspect()) return false;
                if (_regionVersions[i] != 0 && !CaptureBranch(_roots[i], _regions[i].Cells, _regions[i].Material,
                    WorldCellularSpanKind.AuthoredLiquid, CellCapacity, chunkSize, ref writer)) return false;
            }
            foreach (var page in _coldPages.Values)
                foreach (int index in page.Regions)
                {
                    var cold = _cold[index - _regions.Length];
                    if (!CaptureBranch(cold.Root, cold.Definition.Cells, cold.Definition.Material,
                        WorldCellularSpanKind.ColdLiquid, 0, chunkSize, ref writer)) return false;
                }
            return true;
        }

        private bool CaptureBranch(int index, BoundsInt cells, MaterialId material, WorldCellularSpanKind kind,
            uint original, int chunkSize, ref WorldCaptureWriter writer)
        {
            if (!writer.Inspect()) return false;
            var node = _nodes[index];
            if (node.Low == 0) return node.Amount == original ||
                writer.Span(kind, cells, (byte)material, original, node.Amount, chunkSize);
            Split(cells, out var left, out var right);
            return CaptureBranch(node.Low, left, material, kind, original, chunkSize, ref writer)
                && CaptureBranch(node.High, right, material, kind, original, chunkSize, ref writer);
        }

        internal bool CaptureChunkChanges(BoundsInt chunk, int chunkSize, ref WorldCaptureWriter writer)
        {
            _captureRegions.Clear();
            if (_captureRegion == null) _captureRegion = _captureRegions.Add;
            VisitTouchingRegions(chunk, _captureRegion);
            foreach (int index in _captureRegions)
            {
                if (!writer.Inspect()) return false;
                bool authored = index < _regions.Length;
                if (authored && _regionVersions[index] == 0) continue;
                var definition = GetRegion(index);
                if (!CaptureClipped(RootOf(index), definition.Cells, chunk, definition.Material,
                    authored ? WorldCellularSpanKind.AuthoredLiquid : WorldCellularSpanKind.ColdLiquid,
                    authored ? CellCapacity : 0, chunkSize, ref writer)) return false;
            }
            return true;
        }

        private bool CaptureClipped(int nodeId, BoundsInt box, BoundsInt chunk, MaterialId material,
            WorldCellularSpanKind kind, uint original, int chunkSize, ref WorldCaptureWriter writer)
        {
            if (!writer.Inspect()) return false;
            var lo = Vector3Int.Max(box.min, chunk.min); var hi = Vector3Int.Min(box.max, chunk.max);
            if (lo.x >= hi.x || lo.y >= hi.y || lo.z >= hi.z) return true;
            var node = _nodes[nodeId];
            if (node.Low == 0) return node.Amount == original || writer.Span(kind, new BoundsInt(lo, hi - lo),
                (byte)material, original, node.Amount, chunkSize);
            Split(box, out var left, out var right);
            return CaptureClipped(node.Low, left, chunk, material, kind, original, chunkSize, ref writer)
                && CaptureClipped(node.High, right, chunk, material, kind, original, chunkSize, ref writer);
        }

        // 供 Replica 私有的静水显示树安装已验证的真量。沿用数量树与区域版本，没有 Solver、唤醒或投放队列。
        internal void SetReplicaQuantity(Vector3Int cell, MaterialId material, uint quantity)
        {
            int region = FindRegion(cell);
            uint current = region < 0 ? 0 : Read(region, cell);
            if (current == quantity && (quantity == 0 || GetRegion(region).Material == material)) return;
            if (current != 0 && quantity != 0 && GetRegion(region).Material != material)
            {
                WriteReplicaCell(region, cell, current, 0);
                region = FindRegion(cell);
                current = 0;
            }
            if (quantity != 0 && (region < 0 || GetRegion(region).Material != material))
                region = ClaimCold(cell, material, region);
            if (region < 0) throw new InvalidOperationException("Replica 静水写入未包含可用区域。");
            WriteReplicaCell(region, cell, current, quantity);
        }

        private void WriteReplicaCell(int region, Vector3Int cell, uint before, uint after)
        {
            var entry = GetRegion(region);
            Write(RootOf(region), entry.Cells, cell, after);
            _totals[(byte)entry.Material] = checked(_totals[(byte)entry.Material] - before + after);
            Version++;
            if (region < _regions.Length) _regionVersions[region] = Version;
            else UpdateCold(region, before, after);
        }
    }
}
