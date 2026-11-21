using System;
using System.IO;
using System.Text;
using Game.Materials;
using UnityEngine;

namespace Game.Diagnostics
{
    public sealed partial class CellularNativeFrameBenchmark
    {
        private const int PitWidth = 3, PitBreadth = 2, PitCellCount = PitWidth * PitBreadth;
        [Serializable] private sealed class PitReport
        {
            public Vector3Int FirstCell;
            public int Width = PitWidth, Breadth = PitBreadth, Depth = 1;
            public int OpenedCells, EmptyCellsBefore, WetCellsAfter;
            public long GlobalWaterBefore, GlobalWaterAtOpening, GlobalWaterAfter, WaterBeforeInPitGmu, WaterAtOpeningInPitGmu, FilledWaterGmu;
            public long ExcavationTick, OpenPhotoTick;
            public bool Conserved, BottomIntact, EmptyAtOpening;
            public float MeanHeadroomAfterMm;
            public string BeforeSection, OpeningSection, AfterSection;
            public string[] HitColliders = new string[PitCellCount], RemovedMaterials = new string[PitCellCount];
            public int[] BeforeWaterPerCell = new int[PitCellCount], OpenWaterPerCell = new int[PitCellCount],
                AfterWaterPerCell = new int[PitCellCount];
        }

        private uint WaterAt(Vector3Int cell)
        {
            uint water = 0;
            if (_world.TryGetCellularContents(cell, out var primary, out var amount, out _, out _) && primary == MaterialId.Water)
                water += amount;
            if (_world.CellularRestingLiquids.TryGetCell(cell, out var cold, out var quantity) && cold == MaterialId.Water)
                water += quantity;
            return water;
        }

        private void LocateBottlePit(BottleDistribution water)
        {
            // 只选现有水滩边缘的天然顶层。所有列均须有下一层土石，优先选一半为干土的开口，
            // 因此第一张可以看到露出的坑底，后续帧能测量这组原本干燥的土格接收了多少水。
            int y = Mathf.FloorToInt((_ground.max.y - _world.Origin.y) / _world.CellSize - .001f);
            long best = long.MinValue;
            foreach (var occupied in water.Columns.Keys)
            for (int dz = 1 - PitBreadth; dz <= 0; dz++)
            for (int dx = 1 - PitWidth; dx <= 0; dx++)
            {
                var corner = new Vector3Int(occupied.x + dx, y, occupied.y + dz);
                int wet = 0; long amount = 0;
                bool valid = true;
                for (int i = 0; i < PitCellCount; i++)
                {
                    var cell = PitCell(corner, i);
                    if (!SoilOrRock(_world.GetTerrainMaterial(cell)) || !SoilOrRock(_world.GetTerrainMaterial(cell + Vector3Int.down)))
                        valid = false;
                    uint q = WaterAt(cell + Vector3Int.up);
                    if (q > 0) { wet++; amount += q; }
                }
                if (!valid || wet == 0 || wet == PitCellCount) continue;
                long score = amount - Math.Abs(wet - PitCellCount / 2) * 10000L;
                if (score > best || score == best && (corner.z < _bottles.Excavation.FirstCell.z ||
                    corner.z == _bottles.Excavation.FirstCell.z && corner.x < _bottles.Excavation.FirstCell.x))
                { best = score; _bottles.Excavation.FirstCell = corner; }
            }
            if (best == long.MinValue) throw new InvalidOperationException("真实水滩旁没有可开挖且有实体坑底的 3×2 顶层。");
        }

        private static Vector3Int PitCell(Vector3Int corner, int index) => corner + new Vector3Int(index % PitWidth, 0, index / PitWidth);
        private static bool SoilOrRock(MaterialId material) => material == MaterialId.Soil || material == MaterialId.Rock;

        private void OpenBottlePit()
        {
            var pit = _bottles.Excavation;
            for (int i = 0; i < PitCellCount; i++)
            {
                Vector3Int cell = PitCell(pit.FirstCell, i);
                Vector3 center = _world.Origin + ((Vector3)cell + Vector3.one * .5f) * _world.CellSize;
                Physics.SyncTransforms();
                if (!Physics.Raycast(center + Vector3.up, Vector3.down, out var hit, 1.5f, ~0, QueryTriggerInteraction.Ignore) ||
                    hit.normal.y < .9f || !_world.TryExcavateTerrain(hit, out var removed) || !SoilOrRock(removed) ||
                    _world.GetTerrainMaterial(cell) != MaterialId.Empty || !SoilOrRock(_world.GetTerrainMaterial(cell + Vector3Int.down)))
                    throw new InvalidOperationException($"第 {i + 1} 次正式射线开挖没有得到预期地表格 {cell} 和保留的坑底。");
                pit.HitColliders[i] = hit.collider.name; pit.RemovedMaterials[i] = removed.ToString(); pit.OpenedCells++;
            }
            pit.ExcavationTick = _world.WorldTick;
        }

        private void SaveBottleSection(string label, BottleFrame frame)
        {
            var pit = _bottles.Excavation;
            bool before = label == "water-1032";
            bool opening = label == "water-pit-open";
            int[] quantities = before ? pit.BeforeWaterPerCell : opening ? pit.OpenWaterPerCell : pit.AfterWaterPerCell;
            int sum = 0, empty = 0, wet = 0;
            bool floor = true;
            for (int i = 0; i < PitCellCount; i++)
            {
                var cell = PitCell(pit.FirstCell, i);
                quantities[i] = (int)WaterAt(cell); sum += quantities[i];
                if (quantities[i] > 0) wet++;
                if (_world.GetTerrainMaterial(cell) == MaterialId.Empty) empty++;
                floor &= SoilOrRock(_world.GetTerrainMaterial(cell + Vector3Int.down));
            }
            string path = Path.ChangeExtension(frame.Screenshot, ".svg");
            WriteSectionDrawing(path, label);
            if (before)
            {
                pit.GlobalWaterBefore = frame.Materials[0].GlobalGmu;
                pit.WaterBeforeInPitGmu = sum; pit.EmptyCellsBefore = empty; pit.BeforeSection = path;
            }
            else if (opening)
            {
                pit.OpenPhotoTick = frame.WorldTick; pit.GlobalWaterAtOpening = frame.Materials[0].GlobalGmu;
                pit.WaterAtOpeningInPitGmu = sum; pit.OpeningSection = path;
                pit.EmptyAtOpening = pit.OpenPhotoTick == pit.ExcavationTick && sum == 0 && empty == PitCellCount && floor;
            }
            else
            {
                pit.GlobalWaterAfter = frame.Materials[0].GlobalGmu; pit.FilledWaterGmu = sum;
                pit.WetCellsAfter = wet; pit.BottomIntact = floor; pit.AfterSection = path;
                pit.MeanHeadroomAfterMm = 1000f * _world.CellSize * (1f - sum / ((float)PitCellCount * _world.CellularCellCapacity));
                pit.Conserved = pit.EmptyCellsBefore == 0 && pit.EmptyAtOpening && pit.WaterBeforeInPitGmu == 0 &&
                    pit.GlobalWaterAtOpening == pit.GlobalWaterBefore && empty == PitCellCount &&
                    floor && frame.Materials[0].SubmittedGmu == 1032 && pit.GlobalWaterAfter == pit.GlobalWaterBefore &&
                    pit.GlobalWaterBefore - _bottles.Frames[0].Materials[0].GlobalGmu == 1032;
            }
        }

        // 独立数据图只代表真实截面。镜头下原有土墙继续遮挡地下格，玩家截图保留这种遮挡。
        private void WriteSectionDrawing(string path, string label)
        {
            var origin = _bottles.Excavation.FirstCell;
            var svg = new StringBuilder("<svg xmlns='http://www.w3.org/2000/svg' width='690' height='260' viewBox='0 0 690 260'><rect width='690' height='260' fill='#16212b'/><g font-family='sans-serif' fill='#fff'>");
            svg.Append($"<text x='16' y='23' font-size='16'>{label} · Home · {PitWidth}×{PitBreadth}×1 · 1 cell = {_world.CellSize * 100:0} cm · quantities in GMU</text>");
            for (int z = 0; z < PitBreadth; z++)
            {
                int start = z * 335 + 60;
                svg.Append($"<text x='{start}' y='47'>XY slice at Z = {origin.z + z}</text>");
                for (int y = 1; y >= -1; y--)
                {
                    int top = 57 + (1 - y) * 47;
                    svg.Append($"<text x='{start - 47}' y='{top + 26}' font-size='13'>Y {origin.y + y}</text>");
                    for (int x = -2; x <= 3; x++)
                    {
                        var cell = origin + new Vector3Int(x, y, z); int left = start + (x + 2) * 44;
                        var terrain = _world.GetTerrainMaterial(cell); uint amount = WaterAt(cell);
                        string color = terrain == MaterialId.Soil ? "#84644b" : terrain == MaterialId.Rock ? "#666975" : "#283746";
                        svg.Append($"<rect x='{left}' y='{top}' width='44' height='44' fill='{color}'/>");
                        int pixels = Mathf.RoundToInt(amount * 44f / _world.CellularCellCapacity);
                        if (pixels > 0) svg.Append($"<rect x='{left}' y='{top + 44 - pixels}' width='44' height='{pixels}' fill='#19799e'/>");
                        string border = y == 0 && x >= 0 && x < PitWidth ? "#fbd36b" : "#50606a";
                        svg.Append($"<rect x='{left}' y='{top}' width='44' height='44' fill='none' stroke='{border}'/>");
                        if (amount > 0) svg.Append($"<text x='{left + 22}' y='{top + 27}' text-anchor='middle' font-size='13'>{amount}</text>");
                    }
                }
                svg.Append($"<text x='{start}' y='222' font-size='13'>X = {origin.x - 2} … {origin.x + 3}</text>");
            }
            svg.Append("<text x='16' y='249' font-size='13'>Blue = water · brown/gray = soil/rock · outlined yellow = the six excavated cells</text></g></svg>");
            File.WriteAllText(path, svg.ToString());
        }
    }
}
