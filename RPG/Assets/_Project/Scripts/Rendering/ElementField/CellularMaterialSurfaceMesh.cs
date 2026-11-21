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
    internal sealed partial class CellularMaterialSurfaceMesh
    {
        internal const int LiquidSlots = 4, SlotCount = 7;
        private const int Size = CellularMaterialRenderer.PageSize;
        private readonly List<Vector3> _vertices = new List<Vector3>(Size * Size * Size * 8);
        private readonly List<Vector3> _normals = new List<Vector3>(Size * Size * Size * 8);
        private readonly List<Vector4> _arrivals = new List<Vector4>(Size * Size * Size * 8);
        private Vector4 _currentArrival;
        private readonly List<Color32> _materialIds = new List<Color32>(Size * Size * Size * 8);
        private readonly List<int>[] _indices = new List<int>[SlotCount];
        internal int IndexCount(int slot) => _indices[slot].Count;
        private CellularMaterialRenderer _source;
        private List<Vector3> _outVertices, _outNormals;
        private List<int> _outIndices;
        private uint _capacity;
        private bool _edgePage;
        // 重烘一页时，多个角点、侧壁和顶部反复读取同一邻格。缓存仅属于当前同步 Build，
        // 版本随每次 Build 前进，返回时不保留可被下一次世界提交读到的旧样本。
        private const int HaloSize = Size + 2;
        private readonly uint[] _sampleVersions = new uint[HaloSize * HaloSize * HaloSize];
        private readonly CellularMaterialRenderer.Sample[] _halo = new CellularMaterialRenderer.Sample[HaloSize * HaloSize * HaloSize];
        private Vector3Int _origin;
        private uint _version;

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
                // 旧材质可在顶点阶段抬高水面。极小 GMU 的薄边也与相邻页共用角点，这里只保留法线波纹。
                block.SetFloat("_CartoonSwellAmplitude", 0f);
                block.SetFloat("_UseCellularDepth", 1f);
                block.SetColor("_IceColor", color);
                block.SetColor("_ResidueColor", color);
                block.SetFloat("_Smoothness", i == 4 || i == 6 ? .15f : .78f);
                result[i] = block;
            }
            // 热页和压缩静区共用下面的材质参数。侧壁受光、反射和水面光斑连续，
            // 即使主体颜色接近，也能从粗糙度和法线起伏区分水、毒液、黏液与油。
            SetLiquidAppearance(result[Slot(MaterialId.Water)], new Color(.025f,.21f,.34f),
                new Color(.68f,.88f,.98f), 42f, .09f, .10f, .46f);
            SetLiquidAppearance(result[Slot(MaterialId.Poison)], new Color(.045f,.20f,.055f),
                new Color(.65f,.88f,.42f), 30f, .06f, .075f, .24f);
            SetLiquidAppearance(result[Slot(MaterialId.Sticky)], new Color(.24f,.095f,.26f),
                new Color(.86f,.68f,.91f), 22f, .035f, .04f, .17f);
            SetLiquidAppearance(result[Slot(MaterialId.Oil)], new Color(.035f,.023f,.018f),
                new Color(.74f,.73f,.78f), 80f, .025f, .025f, .68f);
            return result;
        }

        private static void SetLiquidAppearance(MaterialPropertyBlock block, Color shade, Color rim,
            float gloss, float waveStrength, float rippleLight, float reflection)
        {
            block.SetColor("_DeepColor", shade);
            block.SetColor("_FresnelColor", rim);
            block.SetFloat("_CartoonGloss", gloss);
            block.SetFloat("_CartoonWaveStrength", waveStrength);
            block.SetFloat("_CartoonRippleLight", rippleLight);
            block.SetFloat("_SurfaceReflection", reflection);
        }

        internal void Build(Mesh mesh, ElementChunkKey key, List<Vector3Int> cells,
            CellularMaterialRenderer source, uint capacity)
        {
            _source = source;
            _capacity = capacity;
            _vertices.Clear(); _normals.Clear(); _materialIds.Clear(); _arrivals.Clear();
            for (int slot = 0; slot < SlotCount; slot++) _indices[slot].Clear();
            var origin = new Vector3Int(key.X, key.Y, key.Z) * Size;
            ResetHalo(origin, key);
            _outVertices = _vertices; _outNormals = _normals;
            for (int i = 0; i < cells.Count; i++)
            {
                var cell = cells[i];
                var sample = _source.Read(cell);
                if (sample.Quantity == 0) continue;
                _outIndices = _indices[Slot(sample.Material)];
                _currentArrival = default;
                AddCell(cell, cell - origin, sample);
                while (_arrivals.Count < _vertices.Count) _arrivals.Add(_currentArrival);
                AppendColors(_materialIds, _vertices, sample.Material, cell.y - origin.y, false);
            }
            mesh.Clear();
            mesh.SetVertices(_vertices);
            mesh.SetNormals(_normals);
            mesh.SetColors(_materialIds);
            mesh.SetUVs(3, _arrivals);
            mesh.subMeshCount = SlotCount;
            // Clear 已清空各 submesh 的 index。只上传非空材质，纯水/纯沙页跳过六次空缓冲原生调用。
            for (int slot = 0; slot < SlotCount; slot++)
                if (_indices[slot].Count != 0) mesh.SetTriangles(_indices[slot], slot, false);
            mesh.bounds = new Bounds(Vector3.one * (.5f * Size), Vector3.one * (Size + 2));
            _source = null;
        }

        internal static void AppendColors(List<Color32> colors, List<Vector3> vertices, MaterialId material, float baseY, bool full)
        {
            // COLOR.r 保留固相 ID。g 存格内表面相对底部的深度，截到一格，
            // 让变浅的岸边在暗场景也能呈现厚薄；无需逐像素追踪湖底或额外采样相机深度。
            bool liquid = ElementCellMaterialRules.Motion(material) == ElementCellMotion.Liquid;
            var value = new Color32((byte)material, full ? (byte)255 : (byte)0, 0, 255);
            while (colors.Count < vertices.Count)
            {
                if (liquid && !full) value.g = (byte)Mathf.RoundToInt(255 * Mathf.Clamp01(vertices[colors.Count].y - baseY));
                colors.Add(value);
            }
        }

        internal void AppendColdLiquid(Vector3Int cell, Vector3Int regionOrigin, CellularMaterialRenderer source,
            uint capacity, CellularMaterialRenderer.Sample sample, List<Vector3> vertices, List<Vector3> normals, List<int> indices)
        {
            // 满盒保持原有合并面。部分液量叶节点规模为一格，和动态页共用角点、高度、岸线与邻面规则。
            var page = new ElementChunkKey(cell.x >> 3, cell.y >> 3, cell.z >> 3);
            if (_source == null || cell.x - _origin.x < 0 || cell.x - _origin.x >= Size ||
                cell.y - _origin.y < 0 || cell.y - _origin.y >= Size || cell.z - _origin.z < 0 || cell.z - _origin.z >= Size)
                ResetHalo(new Vector3Int(page.X, page.Y, page.Z) * Size, page);
            _source = source; _capacity = capacity;
            _outVertices = vertices; _outNormals = normals; _outIndices = indices;
            AddCell(cell, cell - regionOrigin, sample);
        }

        internal void EndColdRegion() => _source = null;

        private void ResetHalo(Vector3Int origin, ElementChunkKey key)
        {
            _origin = origin;
            _edgePage = CellularMaterialRenderer.IsCoordinateEdge(key);
            if (unchecked(++_version) != 0) return;
            System.Array.Clear(_sampleVersions, 0, _sampleVersions.Length);
            System.Array.Clear(_wallVersions, 0, _wallVersions.Length);
            _version = 1;
        }

        private void AddCell(Vector3Int cell, Vector3Int local, CellularMaterialRenderer.Sample sample)
        {
            int slot = Slot(sample.Material);
            Vector3 p = local;
            Vector3 x = Vector3.right, z = Vector3.forward, up = Vector3.up;
            if (HasInsetSandFootprint(sample.Material, sample.Quantity, _capacity))
            {
                bool exposedBottom = Fill(Neighbor(cell, Vector3Int.down, out _)) < 1f;
                AppendInsetSand(p, sample.Quantity, _capacity, exposedBottom, _outVertices, _outNormals, _outIndices);
                return;
            }
            if (slot < LiquidSlots && (ulong)sample.Quantity * 4 <= _capacity && IsIsolated(cell))
            {
                _currentArrival = _source.DropletArrival(cell, sample);
                AddDroplet(p, sample.Quantity / (float)_capacity, _source.DetailedShoreAt(cell));
                return;
            }
            float h00 = Corner(cell, sample, -1, -1), h10 = Corner(cell, sample, 1, -1);
            float h01 = Corner(cell, sample, -1, 1), h11 = Corner(cell, sample, 1, 1);
            if (slot < LiquidSlots && sample.Quantity < _capacity && _source.DetailedShoreAt(cell) && Neighbor(cell, Vector3Int.up, out _).Quantity == 0 &&
                TryAppendShore(cell, p, sample, h00, h01, h11, h10)) return;
            // 下层未满而上层已出现时，两层之间确有空隙，保留顶底面。完全接满才省内部水平面。
            if (Fill(sample) < 1f || Fill(Neighbor(cell, Vector3Int.up, out _)) == 0f)
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
            int x = neighbor.x - _origin.x + 1, y = neighbor.y - _origin.y + 1, z = neighbor.z - _origin.z + 1;
            // 邻页上的液体侧壁还可能向外再读一格。这些稀少的二层 Halo 直接走原采样。
            if ((uint)x >= HaloSize || (uint)y >= HaloSize || (uint)z >= HaloSize)
                return _source.ReadSurface(neighbor);
            int index = x + HaloSize * (y + HaloSize * z);
            if (_sampleVersions[index] != _version)
            {
                _sampleVersions[index] = _version;
                _halo[index] = _source.ReadSurface(neighbor);
            }
            return _halo[index];
        }

        private bool IsIsolated(Vector3Int cell) => !HasLiquid(cell, Vector3Int.left) && !HasLiquid(cell, Vector3Int.right)
            && !HasLiquid(cell, Vector3Int.up) && !HasLiquid(cell, Vector3Int.down)
            && !HasLiquid(cell, Vector3Int.forward) && !HasLiquid(cell, Vector3Int.back);
        private bool HasLiquid(Vector3Int cell, Vector3Int delta)
            => ElementCellMaterialRules.Motion(Neighbor(cell, delta, out _).Material) == ElementCellMotion.Liquid;

        private void AddDroplet(Vector3 cell, float fraction, bool detailed)
        {
            // 独立少量液体沿用原液滴模板。由 V=4πr³/3 解半径，球底落在本格底面；
            // 到四分之一格时半径仍小于半格，整滴不会越过未占用邻格，也不会铺成一张纸片。
            float radius = Mathf.Pow(fraction * (3f / (4f * Mathf.PI)), 1f / 3f);
            Vector3 center = cell + new Vector3(.5f, radius, .5f);
            int first = _outVertices.Count;
            var vertices = detailed ? LiquidDropletTemplate.Vertices : LiquidDropletTemplate.DistantVertices;
            var indices = detailed ? LiquidDropletTemplate.Indices : LiquidDropletTemplate.DistantIndices;
            foreach (var unit in vertices)
            {
                _outVertices.Add(center + unit * radius);
                _outNormals.Add(unit);
            }
            foreach (int index in indices) _outIndices.Add(first + index);
        }

        internal static bool HasInsetSandFootprint(MaterialId material, uint amount, uint capacity)
            => material == MaterialId.Sand && ElementCellMaterialRules.HasInsetGrainFootprint(amount, capacity);

        // 这里返回会盖住整条格边/格面的高度。中央的少量沙不到四壁，它的真实形状另由
        // AppendInsetSand 生成。如果把沙团的高度用于邻面剔除，跨页时会在隔壁的砂墙上挖出黑缝。
        private float Fill(CellularMaterialRenderer.Sample sample)
        {
            return FullFootprintHeight(sample, _capacity);
        }

        internal static float FullFootprintHeight(CellularMaterialRenderer.Sample sample, uint capacity)
        {
            if (sample.Quantity == 0 || HasInsetSandFootprint(sample.Material, sample.Quantity, capacity)) return 0f;
            if (sample.Material != MaterialId.Sand && ElementCellMaterialRules.OccupiesWholeCell(sample.Material)) return 1f;
            return Mathf.Min(1f, sample.Quantity / (float)capacity);
        }

        internal static void AppendInsetSand(Vector3 cell, uint quantity, uint capacity, bool exposedBottom,
            List<Vector3> vertices, List<Vector3> normals, List<int> indices)
        {
            // 沙与对应 Collider 共用 dimensions，水平居中、底贴本格。GMU 减半则真实体积减半。
            // 一团最多 24 个平面顶点，不创建粒子、Rigidbody 或独立 Draw Call。
            Vector3 size = ElementCellMaterialRules.GrainDimensions(quantity, capacity);
            Vector3 p = cell + new Vector3((1f - size.x) * .5f, 0f, (1f - size.z) * .5f);
            Vector3 x = Vector3.right * size.x, y = Vector3.up * size.y, z = Vector3.forward * size.z;
            AddQuad(vertices, normals, indices, p + y, p + z + y, p + x + z + y, p + x + y, Vector3.up);
            if (exposedBottom) AddQuad(vertices, normals, indices, p, p + x, p + x + z, p + z, Vector3.down);
            AddQuad(vertices, normals, indices, p, p + z, p + z + y, p + y, Vector3.left);
            AddQuad(vertices, normals, indices, p + x + z, p + x, p + x + y, p + x + z + y, Vector3.right);
            AddQuad(vertices, normals, indices, p + x, p, p + y, p + x + y, Vector3.back);
            AddQuad(vertices, normals, indices, p + z, p + x + z, p + x + z + y, p + z + y, Vector3.forward);
        }

        private float Corner(Vector3Int cell, CellularMaterialRenderer.Sample sample, int dx, int dz)
        {
            float own = Fill(sample);
            if (own == 0 || sample.Resting && sample.Quantity >= _capacity || ElementCellMaterialRules.Motion(sample.Material) != ElementCellMotion.Liquid) return own;
            // 每个角最多四个同层格参与。相邻面两端取得完全相同的值，跨页不会形成两条不相接的水线；
            // 仅在角点接触的两团液体不能因此被融合。平滑高度只用于显示，采集始终读取整数 GMU。
            float sum = 0f; int count = 0;
            if (Accumulate(cell, sample, ref sum, ref count)) return 1f;
            var sx = Neighbor(cell, new Vector3Int(dx, 0, 0), out var nx);
            var sz = Neighbor(cell, new Vector3Int(0, 0, dz), out var nz);
            bool hasX = sx.Material == sample.Material, hasZ = sz.Material == sample.Material;
            if (hasX && Accumulate(nx, sx, ref sum, ref count)) return 1f;
            if (hasZ && Accumulate(nz, sz, ref sum, ref count)) return 1f;
            float restingHeight = hasX && sx.Resting && sx.Quantity >= _capacity || hasZ && sz.Resting && sz.Quantity >= _capacity ? 1f : 0f;
            if (hasX || hasZ)
            {
                var sd = Neighbor(cell, new Vector3Int(dx, 0, dz), out var diagonal);
                if (sd.Material == sample.Material)
                {
                    if (Accumulate(diagonal, sd, ref sum, ref count)) return 1f;
                    if (sd.Resting && sd.Quantity >= _capacity) restingHeight = 1f;
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
            return height >= 1f && Fill(Neighbor(cell, Vector3Int.up, out _)) > 0f;
        }

        private void Wall(int slot, MaterialId material, Vector3Int cell, Vector3Int delta, Vector3 a, Vector3 b,
            float topA, float topB, int ax, int az, int bx, int bz, Vector3 normal)
        {
            var adjacent = Neighbor(cell, delta, out var neighbor);
            if (material == adjacent.Material && slot < LiquidSlots && (!adjacent.Resting || adjacent.Quantity < _capacity)) return;
            float bottomA = Mathf.Min(topA, Corner(neighbor, adjacent, ax, az));
            float bottomB = Mathf.Min(topB, Corner(neighbor, adjacent, bx, bz));
            if (bottomA >= topA && bottomB >= topB) return;
            Quad(slot, a + Vector3.up*bottomA, b + Vector3.up*bottomB, b + Vector3.up*topB, a + Vector3.up*topA, normal);
        }

        private void Quad(int slot, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
            => AddQuad(_outVertices, _outNormals, _outIndices, a, b, c, d, normal);

        private static void AddQuad(List<Vector3> vertices, List<Vector3> normals, List<int> indices,
            Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
        {
            int first = vertices.Count;
            vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);
            normals.Add(normal); normals.Add(normal); normals.Add(normal); normals.Add(normal);
            indices.Add(first); indices.Add(first + 1); indices.Add(first + 2);
            indices.Add(first); indices.Add(first + 2); indices.Add(first + 3);
        }
    }
}
