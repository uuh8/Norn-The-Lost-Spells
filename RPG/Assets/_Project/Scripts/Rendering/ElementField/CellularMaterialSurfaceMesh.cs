using System.Collections.Generic;
using Game.ElementField;
using Game.Materials;
using UnityEngine;

namespace Game.Rendering
{
    /// <summary>
    /// 把已提交细格的主相烘成一页外壳。Mesh 顶点以细格为单位、相对本页存储，世界格距只在 Draw 时乘一次。
    /// 相同液体共享高度角点；填满的内面省去，暴露侧面和底面保留体积。此缓存不回写容器数量或 Collider。
    /// </summary>
    internal sealed class CellularMaterialSurfaceMesh
    {
        internal const int LiquidSlots = 4, SlotCount = 7;
        private const int Size = CellularMaterialRenderer.PageSize;
        private readonly List<Vector3> _vertices = new List<Vector3>(Size * Size * Size * 8);
        private readonly List<Vector3> _normals = new List<Vector3>(Size * Size * Size * 8);
        private readonly List<Color32> _materialIds = new List<Color32>(Size * Size * Size * 8);
        private readonly List<int>[] _indices = new List<int>[SlotCount];
        private CellularMaterialRenderer _source;
        private uint _capacity;
        private bool _edgePage;

        internal CellularMaterialSurfaceMesh()
        {
            for (int i = 0; i < SlotCount; i++) _indices[i] = new List<int>(Size * Size * 12);
        }

        internal static int Slot(MaterialId material)
        {
            switch (material)
            {
                case MaterialId.Water: return 0;
                case MaterialId.Poison: return 1;
                case MaterialId.Sticky: return 2;
                case MaterialId.Oil: return 3;
                case MaterialId.Sand: return 4;
                case MaterialId.Ice: return 5;
                case MaterialId.DriedSlime: return 6;
                default: return -1;
            }
        }

        internal static MaterialPropertyBlock[] CreatePalette()
        {
            var colors = new[]
            {
                new Color(.12f,.58f,.78f), new Color(.22f,.69f,.19f), new Color(.64f,.36f,.67f), new Color(.20f,.15f,.095f),
                new Color(.74f,.55f,.27f), new Color(.42f,.78f,.91f), new Color(.51f,.44f,.20f)
            };
            var result = new MaterialPropertyBlock[SlotCount];
            for (int i = 0; i < result.Length; i++)
            {
                Color color = colors[i];
                var block = new MaterialPropertyBlock();
                block.SetColor("_BaseColor", color);
                block.SetColor("_ShallowColor", color);
                block.SetColor("_DeepColor", Color.Lerp(color, Color.black, .43f));
                block.SetColor("_FresnelColor", Color.Lerp(color, Color.white, .6f));
                // 旧材质可在顶点阶段抬高水面。极小 GMU 的薄边也与相邻页共用角点，这里只保留法线波纹。
                block.SetFloat("_CartoonSwellAmplitude", 0f);
                block.SetColor("_IceColor", color);
                block.SetColor("_ResidueColor", color);
                block.SetFloat("_Smoothness", i == 4 || i == 6 ? .15f : .78f);
                result[i] = block;
            }
            return result;
        }

        internal void Build(Mesh mesh, ElementChunkKey key, List<Vector3Int> cells,
            CellularMaterialRenderer source, uint capacity)
        {
            _source = source;
            _capacity = capacity;
            _edgePage = CellularMaterialRenderer.IsCoordinateEdge(key);
            _vertices.Clear(); _normals.Clear(); _materialIds.Clear();
            for (int slot = 0; slot < SlotCount; slot++) _indices[slot].Clear();
            var origin = new Vector3Int(key.X, key.Y, key.Z) * Size;
            for (int i = 0; i < cells.Count; i++)
            {
                var cell = cells[i];
                var sample = _source.Read(cell);
                if (sample.Quantity == 0) continue;
                AddCell(cell, cell - origin, sample);
                // FixedSolid Shader 使用 COLOR.r 的原始 ID 区分冰面和粗糙残留。共享同一 Mesh 的液体忽略此通道。
                var vertexMaterial = new Color32((byte)sample.Material, 0, 0, 255);
                while (_materialIds.Count < _vertices.Count) _materialIds.Add(vertexMaterial);
            }
            mesh.Clear();
            mesh.SetVertices(_vertices);
            mesh.SetNormals(_normals);
            mesh.SetColors(_materialIds);
            mesh.subMeshCount = SlotCount;
            for (int slot = 0; slot < SlotCount; slot++) mesh.SetTriangles(_indices[slot], slot, false);
            mesh.bounds = new Bounds(Vector3.one * (.5f * Size), Vector3.one * Size);
            _source = null;
        }

        private void AddCell(Vector3Int cell, Vector3Int local, CellularMaterialRenderer.Sample sample)
        {
            int slot = Slot(sample.Material);
            Vector3 p = local;
            Vector3 x = Vector3.right, z = Vector3.forward, up = Vector3.up;
            if (slot < LiquidSlots && (ulong)sample.Quantity * 4 <= _capacity && IsIsolated(cell))
            {
                AddDroplet(slot, p, sample.Quantity / (float)_capacity);
                return;
            }
            float h00 = Corner(cell, sample, -1, -1), h10 = Corner(cell, sample, 1, -1);
            float h01 = Corner(cell, sample, -1, 1), h11 = Corner(cell, sample, 1, 1);
            // 下层未满而上层已出现时，两层之间确有空隙，保留顶底面。完全接满才省内部水平面。
            if (Fill(sample) < 1f || Neighbor(cell, Vector3Int.up, out _).Quantity == 0)
            {
                var normal = new Vector3((h00 + h01 - h10 - h11)*.5f, 1f, (h00 + h10 - h01 - h11)*.5f).normalized;
                Quad(slot, p + up*h00, p + z + up*h01, p + x + z + up*h11, p + x + up*h10, normal);
            }
            if (Fill(Neighbor(cell, Vector3Int.down, out _)) < 1f)
                Quad(slot, p, p + x, p + x + z, p + z, Vector3.down);

            Wall(slot, sample.Material, cell, Vector3Int.left, p, p + z, h00, h01, 1, -1, 1, 1, Vector3.left);
            Wall(slot, sample.Material, cell, Vector3Int.right, p + x + z, p + x, h11, h10, -1, 1, -1, -1, x);
            Wall(slot, sample.Material, cell, Vector3Int.back, p + x, p, h10, h00, 1, 1, -1, 1, Vector3.back);
            Wall(slot, sample.Material, cell, Vector3Int.forward, p + z, p + x + z, h01, h11, -1, -1, 1, -1, z);
        }

        private CellularMaterialRenderer.Sample Neighbor(Vector3Int cell, Vector3Int delta, out Vector3Int neighbor)
        {
            neighbor = cell + delta;
            if (_edgePage && CellularMaterialRenderer.OffsetWrapped(cell, delta, neighbor)) return default;
            return _source.ReadSurface(neighbor);
        }

        private bool IsIsolated(Vector3Int cell) => !HasLiquid(cell, Vector3Int.left) && !HasLiquid(cell, Vector3Int.right)
            && !HasLiquid(cell, Vector3Int.up) && !HasLiquid(cell, Vector3Int.down)
            && !HasLiquid(cell, Vector3Int.forward) && !HasLiquid(cell, Vector3Int.back);
        private bool HasLiquid(Vector3Int cell, Vector3Int delta)
            => ElementCellMaterialRules.Motion(Neighbor(cell, delta, out _).Material) == ElementCellMotion.Liquid;

        private void AddDroplet(int slot, Vector3 cell, float fraction)
        {
            // 独立少量液体沿用原液滴模板。由 V=4πr³/3 解半径，球底落在本格底面；
            // 到四分之一格时半径仍小于半格，整滴不会越过未占用邻格，也不会铺成一张纸片。
            float radius = Mathf.Pow(fraction * (3f / (4f * Mathf.PI)), 1f / 3f);
            Vector3 center = cell + new Vector3(.5f, radius, .5f);
            int first = _vertices.Count;
            foreach (var unit in LiquidDropletTemplate.Vertices)
            {
                _vertices.Add(center + unit * radius);
                _normals.Add(unit);
            }
            var indices = _indices[slot];
            foreach (int index in LiquidDropletTemplate.Indices) indices.Add(first + index);
        }

        private float Fill(CellularMaterialRenderer.Sample sample)
        {
            if (sample.Quantity == 0) return 0f;
            // 沙和冰按规则占满本格，库存计量可以小于满格。渲染沿用同一占据规则，避免看起来可以穿过的墙。
            return ElementCellMaterialRules.OccupiesWholeCell(sample.Material) ? 1f : Mathf.Min(1f, sample.Quantity / (float)_capacity);
        }

        private float Corner(Vector3Int cell, CellularMaterialRenderer.Sample sample, int dx, int dz)
        {
            float own = Fill(sample);
            if (own == 0 || sample.Resting || ElementCellMaterialRules.Motion(sample.Material) != ElementCellMotion.Liquid) return own;
            // 每个角最多四个同层格参与。相邻面两端取得完全相同的值，跨页不会形成两条不相接的水线；
            // 仅在角点接触的两团液体不能因此被融合。平滑高度只用于显示，采集始终读取整数 GMU。
            float sum = 0f; int count = 0;
            if (Accumulate(cell, sample, ref sum, ref count)) return 1f;
            var sx = Neighbor(cell, new Vector3Int(dx, 0, 0), out var nx);
            var sz = Neighbor(cell, new Vector3Int(0, 0, dz), out var nz);
            bool hasX = sx.Material == sample.Material, hasZ = sz.Material == sample.Material;
            if (hasX && Accumulate(nx, sx, ref sum, ref count)) return 1f;
            if (hasZ && Accumulate(nz, sz, ref sum, ref count)) return 1f;
            float restingHeight = Mathf.Max(hasX && sx.Resting ? Fill(sx) : 0f, hasZ && sz.Resting ? Fill(sz) : 0f);
            if (hasX || hasZ)
            {
                var sd = Neighbor(cell, new Vector3Int(dx, 0, dz), out var diagonal);
                if (sd.Material == sample.Material)
                {
                    if (Accumulate(diagonal, sd, ref sum, ref count)) return 1f;
                    if (sd.Resting) restingHeight = Mathf.Max(restingHeight, Fill(sd));
                }
            }
            // 压缩面只有一个精确液位，动态格的岸边角点要接上这个高度。多个相邻水阶取高侧，
            // 低侧的高度差由真实侧壁补齐；跨所有者的同材液体由此可以安全省去共同内部面。
            return restingHeight > 0f ? restingHeight : sum / count;
        }

        private bool Accumulate(Vector3Int cell, CellularMaterialRenderer.Sample sample, ref float sum, ref int count)
        {
            float height = Fill(sample);
            sum += height; count++;
            // 上层有材料且下层已满，此处被连续体覆盖。把共享角固定在层边界，避免平滑后露出内部缝隙。
            return height >= 1f && Neighbor(cell, Vector3Int.up, out _).Quantity != 0;
        }

        private void Wall(int slot, MaterialId material, Vector3Int cell, Vector3Int delta, Vector3 a, Vector3 b,
            float topA, float topB, int ax, int az, int bx, int bz, Vector3 normal)
        {
            var adjacent = Neighbor(cell, delta, out var neighbor);
            if (material == adjacent.Material && slot < LiquidSlots && !adjacent.Resting) return;
            float bottomA = Mathf.Min(topA, Corner(neighbor, adjacent, ax, az));
            float bottomB = Mathf.Min(topB, Corner(neighbor, adjacent, bx, bz));
            if (bottomA >= topA && bottomB >= topB) return;
            Quad(slot, a + Vector3.up*bottomA, b + Vector3.up*bottomB, b + Vector3.up*topB, a + Vector3.up*topA, normal);
        }

        private void Quad(int slot, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
        {
            int first = _vertices.Count;
            _vertices.Add(a); _vertices.Add(b); _vertices.Add(c); _vertices.Add(d);
            _normals.Add(normal); _normals.Add(normal); _normals.Add(normal); _normals.Add(normal);
            var indices = _indices[slot];
            indices.Add(first); indices.Add(first + 1); indices.Add(first + 2);
            indices.Add(first); indices.Add(first + 2); indices.Add(first + 3);
        }
    }
}
