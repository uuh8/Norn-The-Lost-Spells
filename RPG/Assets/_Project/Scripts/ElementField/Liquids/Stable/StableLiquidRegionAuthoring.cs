using Game.Core;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>已知封闭湖盆的场景数据。直接建立稳定 owner，不经历逐格填满再压缩。</summary>
    [DefaultExecutionOrder(-40)]
    public sealed class StableLiquidRegionAuthoring : MonoBehaviour
    {
        [SerializeField] private LiquidWorldRuntime _liquids;
        [SerializeField] private MaterialId _material = MaterialId.Water;
        [SerializeField] private Bounds _worldBounds = new Bounds(Vector3.zero, Vector3.one);
        [SerializeField, Tooltip("可选：用 Trigger BoxCollider 编辑湖盆搜索体积。移动、缩放物体会更新 Bounds；不会作为 Solid 地形。")]
        private BoxCollider _boundsCollider;
        [SerializeField, Range(0f, 1f)] private float _initialFill = .75f;
        [SerializeField, Range(0f, 1f)] private float _stepStartFraction = 1f;
        [SerializeField, Min(0)] private int _stepRiseCells;
        [SerializeField] private bool _deriveFootprintFromSolid;
        [SerializeField, Tooltip("从 Solid 地形逐列读取湖底高度；同时自动读取岸线占地。")]
        private bool _deriveBottomFromSolid;
        [SerializeField, Tooltip("沿 XZ Chunk 预分片，局部排水或地形变化只展开相关分片。自然湖泊应启用。")]
        private bool _sectorizeByChunk;
        [Header("Runtime Debug (Read Only)")]
        [SerializeField] private int _regionId = -1;

        public int RegionId => _regionId;
        public LiquidWorldRuntime Liquids => _liquids;
        public Bounds AuthoringBounds => _boundsCollider != null ? _boundsCollider.bounds : _worldBounds;

        private void OnDrawGizmosSelected()
        {
            // 预览搜索体积而非承诺水位；Initial Fill 是容量比例，变深湖盆中不等于高度比例。
            Bounds bounds = AuthoringBounds;
            Gizmos.color = _material == MaterialId.Water ? Color.cyan : _material == MaterialId.Poison
                ? Color.green : new Color(.7f, .35f, 1f);
            Gizmos.DrawWireCube(bounds.center, bounds.size);
        }

        private void Start()
        {
            _regionId = -1;
            if (_liquids == null || !_liquids.IsFluidInitialized
                || !_liquids.AllowsStableRegion(_material))
            {
                GameLog.Error("稳定液体区域缺少已初始化的 Grid Liquid 或材料无稳定资格。",
                    "ElementField");
                return;
            }
            if (_boundsCollider != null && (!_boundsCollider.isTrigger || !IsAxisAligned(_boundsCollider.transform)))
            {
                GameLog.Error("湖盆 Bounds Collider 必须为 Trigger 且与世界坐标轴对齐；旋转会把 AABB 放大。", "ElementField");
                return;
            }
            Bounds authoringBounds = AuthoringBounds;
            Vector3Int minimum = ElementWorldCoordinates.WorldToGlobalCell(
                authoringBounds.min, _liquids.Origin, _liquids.CellSize);
            Vector3Int maximum = ElementWorldCoordinates.WorldToGlobalCell(
                authoringBounds.max - Vector3.one * (_liquids.CellSize * .0001f),
                _liquids.Origin, _liquids.CellSize) + Vector3Int.one;
            if (maximum.x <= minimum.x || maximum.y <= minimum.y || maximum.z <= minimum.z)
            {
                GameLog.Error("稳定液体区域 Bounds 无效。", "ElementField");
                return;
            }
            if (_deriveBottomFromSolid)
            {
                RegisterSolidHeightfield(minimum, maximum);
                return;
            }
            if (_deriveFootprintFromSolid)
            {
                RegisterSolidFootprint(minimum, maximum);
                return;
            }
            int stepX = _stepRiseCells > 0
                ? Mathf.Clamp(minimum.x + Mathf.RoundToInt((maximum.x - minimum.x)
                    * _stepStartFraction), minimum.x, maximum.x)
                : maximum.x;
            LiquidCapacityCurve curve;
            try { curve = new LiquidCapacityCurve(minimum, maximum, stepX,
                _stepRiseCells, _liquids.CellCapacityGmu); }
            catch (System.ArgumentOutOfRangeException)
            {
                GameLog.Error("稳定液体区域的台阶或容量配置无效。", "ElementField");
                return;
            }
            ulong initial = (ulong)System.Math.Round(curve.Capacity * (double)_initialFill);
            if (!_liquids.TryRegisterStableRegion(_material, minimum, maximum,
                stepX, _stepRiseCells, initial, out int id))
            {
                GameLog.Error("稳定液体区域注册失败：检查重叠、Solid 和区域容量预算。",
                    "ElementField");
                return;
            }
            _regionId = id;
        }

        public static bool IsAxisAligned(Transform boundsTransform)
        {
            // 轴交换/180°旋转仍是 AABB；倾斜旋转不能用 bounds 近似一个封闭湖盆。
            Vector3 r = boundsTransform.right, u = boundsTransform.up, f = boundsTransform.forward;
            return Mathf.Max(Mathf.Abs(r.x), Mathf.Abs(r.y), Mathf.Abs(r.z)) > .9999f
                && Mathf.Max(Mathf.Abs(u.x), Mathf.Abs(u.y), Mathf.Abs(u.z)) > .9999f
                && Mathf.Max(Mathf.Abs(f.x), Mathf.Abs(f.y), Mathf.Abs(f.z)) > .9999f;
        }

        private void RegisterSolidFootprint(Vector3Int minimum, Vector3Int maximum)
        {
            long width = (long)maximum.x - minimum.x;
            long depth = (long)maximum.z - minimum.z;
            long height = (long)maximum.y - minimum.y;
            if (_stepRiseCells != 0 || width > int.MaxValue / depth
                || width * depth > _liquids.MaximumStableRegionCells / height)
            {
                GameLog.Error("不规则湖盆目前只支持平底，且 Bounds 必须在稳定区域格数预算内。",
                    "ElementField");
                return;
            }

            int horizontalWidth = (int)width;
            var footprint = new bool[(int)(width * depth)];
            int included = 0;
            // World Bounds 给出搜索框；每列最底格必须是空格，其下必须有 Solid 地板。
            // 墙体占据的列不参与容量，因而自由面按岸线凹凸形成不规则轮廓。
            for (int z = minimum.z; z < maximum.z; z++)
            for (int x = minimum.x; x < maximum.x; x++)
            {
                var bottom = new Vector3Int(x, minimum.y, z);
                if (_liquids.IsSolidForSurface(bottom)
                    || !_liquids.IsSolidForSurface(bottom + Vector3Int.down)) continue;
                for (int y = minimum.y + 1; y < maximum.y; y++)
                {
                    if (!_liquids.IsSolidForSurface(new Vector3Int(x, y, z))) continue;
                    GameLog.Error("不规则湖盆存在中层 Solid；当前容量曲线只支持整列空格。",
                        "ElementField");
                    return;
                }
                footprint[(z - minimum.z) * horizontalWidth + x - minimum.x] = true;
                included++;
            }
            if (included == 0 || !HasSealedSides(minimum, maximum, footprint,
                horizontalWidth))
            {
                GameLog.Error("不规则湖盆为空或侧面未封闭；请检查 Solid Layer、Bounds 和岸线墙体。",
                    "ElementField");
                return;
            }
            LiquidCapacityCurve curve;
            try { curve = new LiquidCapacityCurve(minimum, maximum, footprint,
                _liquids.CellCapacityGmu); }
            catch (System.ArgumentOutOfRangeException)
            {
                GameLog.Error("不规则湖盆容量曲线无效。", "ElementField");
                return;
            }
            ulong initial = (ulong)System.Math.Round(curve.Capacity * (double)_initialFill);
            if (!_liquids.TryRegisterStableFootprint(_material, minimum, maximum,
                footprint, initial, out int id))
            {
                GameLog.Error("不规则湖盆注册失败：检查重叠、动态液体和 Surface Patch 预算。",
                    "ElementField");
                return;
            }
            _regionId = id;
        }

        private void RegisterSolidHeightfield(Vector3Int minimum, Vector3Int maximum)
        {
            long width = (long)maximum.x - minimum.x;
            long depth = (long)maximum.z - minimum.z;
            long height = (long)maximum.y - minimum.y;
            long maximumCells = _sectorizeByChunk
                ? (long)_liquids.MaximumStableRegionCells * _liquids.MaximumStableRegions
                : _liquids.MaximumStableRegionCells;
            if (_stepRiseCells != 0 || width > int.MaxValue / depth
                || width * depth > maximumCells / height)
            {
                GameLog.Error("变深湖盆 Bounds 或稳定区域格数预算无效。", "ElementField");
                return;
            }
            int horizontalWidth = (int)width;
            var floorByColumn = new int[(int)(width * depth)];
            System.Array.Fill(floorByColumn, int.MaxValue);
            int included = 0;
            for (int z = minimum.z; z < maximum.z; z++)
            for (int x = minimum.x; x < maximum.x; x++)
            {
                int floor = int.MaxValue;
                for (int y = minimum.y; y < maximum.y; y++)
                {
                    var cell = new Vector3Int(x, y, z);
                    if (_liquids.IsSolidForSurface(cell))
                    {
                        if (floor == int.MaxValue) continue;
                        GameLog.Error("变深湖盆列存在中层顶板；只支持地板上方连续空格。",
                            "ElementField");
                        return;
                    }
                    if (floor != int.MaxValue) continue;
                    if (!_liquids.IsSolidForSurface(cell + Vector3Int.down)) break;
                    floor = y;
                }
                if (floor == int.MaxValue) continue;
                if (_liquids.IsSolidForSurface(new Vector3Int(x, maximum.y, z)))
                {
                    GameLog.Error("变深湖盆顶层没有自由水面；请调低 Bounds 或移除顶板。",
                        "ElementField");
                    return;
                }
                floorByColumn[(z - minimum.z) * horizontalWidth + x - minimum.x] = floor;
                included++;
            }
            if (included == 0 || !IsConnectedHeightfield(floorByColumn,
                    horizontalWidth, included)
                || !HasSealedHeightfieldSides(minimum, maximum,
                    floorByColumn, horizontalWidth))
            {
                GameLog.Error("变深湖盆为空、占地不连通或岸线开口；请检查 Solid Layer 和 Bounds。",
                    "ElementField");
                return;
            }
            LiquidCapacityCurve curve;
            try { curve = new LiquidCapacityCurve(minimum, maximum, floorByColumn,
                _liquids.CellCapacityGmu); }
            catch (System.ArgumentOutOfRangeException)
            {
                GameLog.Error("变深湖盆容量曲线无效。", "ElementField");
                return;
            }
            ulong initial = (ulong)System.Math.Round(curve.Capacity * (double)_initialFill);
            bool registered = _sectorizeByChunk
                ? _liquids.TryRegisterSectorizedStableHeightfield(_material, minimum,
                    maximum, floorByColumn, initial, out _regionId)
                : _liquids.TryRegisterStableHeightfield(_material, minimum, maximum,
                    floorByColumn, initial, out _regionId);
            if (!registered)
            {
                GameLog.Error("变深湖盆注册失败：检查动态占用、Solid、分片格数、区域和 Patch 预算。",
                    "ElementField");
                return;
            }
        }

        private static bool IsConnectedHeightfield(int[] floors, int width,
            int included)
        {
            // 两片互不相通的池不能共享一个水位与数量账本。注册期遍历一次占地，
            // 避免运行时为了修补错误形状再把整个湖展开成动态格。
            int first = -1;
            for (int i = 0; i < floors.Length; i++)
                if (floors[i] != int.MaxValue) { first = i; break; }
            if (first < 0) return false;
            var seen = new bool[floors.Length];
            var queue = new int[included];
            queue[0] = first;
            seen[first] = true;
            int head = 0, tail = 1;
            while (head < tail)
            {
                int index = queue[head++];
                int x = index % width;
                if (x > 0 && floors[index - 1] != int.MaxValue && !seen[index - 1])
                { seen[index - 1] = true; queue[tail++] = index - 1; }
                if (x + 1 < width && floors[index + 1] != int.MaxValue && !seen[index + 1])
                { seen[index + 1] = true; queue[tail++] = index + 1; }
                if (index >= width && floors[index - width] != int.MaxValue
                    && !seen[index - width])
                { seen[index - width] = true; queue[tail++] = index - width; }
                if (index + width < floors.Length && floors[index + width] != int.MaxValue
                    && !seen[index + width])
                { seen[index + width] = true; queue[tail++] = index + width; }
            }
            return tail == included;
        }

        private bool HasSealedHeightfieldSides(Vector3Int minimum, Vector3Int maximum,
            int[] floorByColumn, int width)
        {
            for (int z = minimum.z; z < maximum.z; z++)
            for (int x = minimum.x; x < maximum.x; x++)
            {
                int floor = floorByColumn[(z - minimum.z) * width + x - minimum.x];
                if (floor == int.MaxValue) continue;
                for (int y = floor; y < maximum.y; y++)
                {
                    if (!HeightfieldNeighborSealed(x - 1, y, z, minimum, maximum,
                            floorByColumn, width)
                        || !HeightfieldNeighborSealed(x + 1, y, z, minimum, maximum,
                            floorByColumn, width)
                        || !HeightfieldNeighborSealed(x, y, z - 1, minimum, maximum,
                            floorByColumn, width)
                        || !HeightfieldNeighborSealed(x, y, z + 1, minimum, maximum,
                            floorByColumn, width)) return false;
                }
            }
            return true;
        }

        private bool HeightfieldNeighborSealed(int x, int y, int z, Vector3Int minimum,
            Vector3Int maximum, int[] floorByColumn, int width)
        {
            if (x >= minimum.x && x < maximum.x && z >= minimum.z && z < maximum.z
                && floorByColumn[(z - minimum.z) * width + x - minimum.x] <= y)
                return true;
            // 相邻较浅列的地板构成低水位岸线；超过其地板后两列共享水面。
            return _liquids.IsSolidForSurface(new Vector3Int(x, y, z));
        }

        private bool HasSealedSides(Vector3Int minimum, Vector3Int maximum,
            bool[] footprint, int width)
        {
            for (int z = minimum.z; z < maximum.z; z++)
            for (int x = minimum.x; x < maximum.x; x++)
            {
                if (!footprint[(z - minimum.z) * width + x - minimum.x]) continue;
                if (!IsSealedNeighbor(x - 1, z, minimum, maximum, footprint, width)
                    || !IsSealedNeighbor(x + 1, z, minimum, maximum, footprint, width)
                    || !IsSealedNeighbor(x, z - 1, minimum, maximum, footprint, width)
                    || !IsSealedNeighbor(x, z + 1, minimum, maximum, footprint, width))
                    return false;
            }
            return true;
        }

        private bool IsSealedNeighbor(int x, int z, Vector3Int minimum,
            Vector3Int maximum, bool[] footprint, int width)
        {
            if (x >= minimum.x && x < maximum.x && z >= minimum.z && z < maximum.z
                && footprint[(z - minimum.z) * width + x - minimum.x]) return true;
            // 初始体积上方也要有墙，避免日后注水使压缩区域穿透尚未编码的岸线。
            for (int y = minimum.y; y < maximum.y; y++)
                if (!_liquids.IsSolidForSurface(new Vector3Int(x, y, z))) return false;
            return true;
        }
    }
}
