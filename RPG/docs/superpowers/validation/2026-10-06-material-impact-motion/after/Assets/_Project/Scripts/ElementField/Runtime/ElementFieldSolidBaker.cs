using Game.Core;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 把初始化时的场景 Collider Rasterize 成紧凑 SolidMask。Bake 后 Simulation 只读 bool 数组，
    /// 不会在每个 Cell、每个 Tick 调用 Physics，从而把昂贵的空间查询移出热路径。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ElementFieldSolidBaker : MonoBehaviour
    {
        [SerializeField] private LayerMask _solidLayerMask;
        [SerializeField] private bool _includeDefaultEnvironment = true;
        // 初始化/Bake 复用查询缓冲；Default 层的普通 Cube 不要求额外设置 Layer。
        // 动态 Rigidbody 与角色 Collider 不得进入静态缓存，否则角色离开后会留下隐形墙。
        private readonly Collider[] _environmentHits = new Collider[64];
        private readonly RaycastHit[] _supportHits = new RaycastHit[64];
        private FixedSolidWorldRuntime _materialSolids;
        private Vector3 _materialOrigin;
        private float _materialCellSize;
        public void SetMaterialSolids(FixedSolidWorldRuntime solids, Vector3 origin, float size)
        { _materialSolids = solids; _materialOrigin = origin; _materialCellSize = size; }
        public bool SampleEnvironmentSolidWorldCell(Vector3Int cell, Vector3 origin, float size)
            => IsStaticSolidCell(cell, origin, size);
        /// <summary>
        /// 保守测试整块空气：无静态 Collider 才能证明内部所有细格无墙。
        /// 有命中仍需回退细格查询，不能把薄墙周围空气全判实心；调用方负责预算和缓存。
        /// </summary>
        public bool SampleEnvironmentSolidWorldBox(Vector3 minimum,Vector3 size)
            => IsEnvironmentSolid(minimum+size*.5f,size*.5f);
        public bool SampleSolidWorldCell(Vector3Int cell, Vector3 origin, float size)
        {
            if (IsStaticSolidCell(cell, origin, size)) return true;
            if (_materialSolids == null || _materialSolids.TotalQuantity == 0 || _materialCellSize <= 0) return false;
            // Grid Liquid 可以使用更粗的格。中心点只能覆盖其中一个细格，会漏掉偏心冰块；
            // 对整个粗格覆盖的 World Cell 做 OR，保持保守阻挡。当前液体 ResolutionScale <= 4，
            // 此循环最多检查 64 格，且只在 Terrain Cache 首次采样／局部失效时执行。
            Vector3 minimum = origin + (Vector3)cell * size;
            Vector3 maximum = minimum + Vector3.one * size;
            Vector3Int min = Vector3Int.FloorToInt((minimum - _materialOrigin) / _materialCellSize);
            Vector3Int max = Vector3Int.CeilToInt((maximum - _materialOrigin) / _materialCellSize) - Vector3Int.one;
            for (int z = min.z; z <= max.z; z++) for (int y = min.y; y <= max.y; y++) for (int x = min.x; x <= max.x; x++)
                if (_materialSolids.BlocksCell(new Vector3Int(x, y, z))) return true;
            return false;
        }

        /// <summary>只在支撑缓存首次使用/地形失效时调用；NonAlloc复用结果缓冲。</summary>
        public bool TrySampleLiquidSupport(Vector3 rayOrigin, float distance, out float height, out Vector3 normal)
        {
            height = float.NegativeInfinity; normal = Vector3.up;
            int mask = _solidLayerMask.value | (_includeDefaultEnvironment ? 1 : 0);
            int count = Physics.RaycastNonAlloc(rayOrigin, Vector3.down, _supportHits, distance, mask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = _supportHits[i]; Collider collider = hit.collider;
                if (collider == null || collider.attachedRigidbody != null || collider is CharacterController
                    || collider.GetComponentInParent<Game.Combat.StatusController>() != null
                    || hit.normal.y < .25f || hit.point.y <= height) continue;
                if (collider.GetComponent<GeneratedMaterialSolidCollider>() != null && _materialSolids != null)
                {
                    // 部分残留的底面抬升已由 Liquid 的 GMU 占据计算。Raycast 再取它的 Collider
                    // 会把相同高度算两次；冰仍作为整格支撑，所以保留冰的真实接触平面。
                    var materialCell=ElementWorldCoordinates.WorldToGlobalCell(hit.point-Vector3.up*(_materialCellSize*.001f),
                        _materialOrigin,_materialCellSize);
                    if (!_materialSolids.BlocksCell(materialCell)) continue;
                }
                height = hit.point.y; normal = hit.normal;
            }
            // 饱和时不能把被截断的命中列表当作完整环境，交给保守Grid回退。
            return count < _supportHits.Length && float.IsFinite(height);
        }

        /// <summary>
        /// 供有界的静态可视网格重建查询地形。归档液体可能没有 Resident Gameplay Chunk，
        /// 不能把没有缓存误认为没有地板；只读 Physics 不分配或唤醒任何元素 Chunk。
        /// 调用方必须缓存结果，不能在每帧或求解器每 Tick 无条件扫描。
        /// </summary>
        public bool IsStaticSolidCell(Vector3Int cell, Vector3 origin, float cellSize)
        {
            Vector3 center = origin + ((Vector3)cell + Vector3.one * .5f) * cellSize;
            return IsEnvironmentSolid(center, Vector3.one * (cellSize * .49f));
        }

        private bool IsEnvironmentSolid(Vector3 center, Vector3 halfExtents)
        {
            int configuredEnvironment = _solidLayerMask.value | (_includeDefaultEnvironment ? 1 : 0);
            if (configuredEnvironment == 0) return false;
            int count = Physics.OverlapBoxNonAlloc(center, halfExtents, _environmentHits,
                Quaternion.identity, configuredEnvironment, QueryTriggerInteraction.Ignore);
            bool solid = false;
            for (int i = 0; i < count; i++)
            {
                Collider hit = _environmentHits[i];
                _environmentHits[i] = null;
                solid |= hit != null && hit.attachedRigidbody == null
                    && !(hit is CharacterController)
                    && hit.GetComponent<GeneratedMaterialSolidCollider>() == null
                    && hit.GetComponentInParent<Game.Combat.StatusController>() == null;
            }
            return solid;
        }

        public int Bake(ElementGrid grid, Vector3 origin, float cellSize)
        {
            if (grid == null)
                return 0;

            grid.ClearSolidMask();
            if (_solidLayerMask.value == 0 && !_includeDefaultEnvironment)
            {
                GameLog.Warn(
                    "ElementFieldSolidBaker has an empty LayerMask; no Cell will be marked Solid.",
                    "ElementField");
                return 0;
            }

            int solidCount = 0;
            Vector3 halfExtents = Vector3.one * (cellSize * 0.49f);
            Vector3Int dimensions = grid.Dimensions;
            for (int z = 0; z < dimensions.z; z++)
            for (int y = 0; y < dimensions.y; y++)
            for (int x = 0; x < dimensions.x; x++)
            {
                var coordinate = new Vector3Int(x, y, z);
                Vector3 center = origin + new Vector3(
                    (x + 0.5f) * cellSize,
                    (y + 0.5f) * cellSize,
                    (z + 0.5f) * cellSize);
                bool isSolid = IsEnvironmentSolid(center, halfExtents);
                grid.SetSolid(coordinate, isSolid);
                if (isSolid)
                    solidCount++;
            }

            return solidCount;
        }

        /// <summary>
        /// 为稀疏世界中新创建的单个 Chunk 烘焙 SolidMask。与有限 Grid 的区别是：Local Cell
        /// 不能直接用于世界定位，必须先通过 global = chunkKey * chunkSize + local 还原 Global Cell。
        /// 该方法只在 Chunk 首次分配时调用，不进入每个 Simulation Tick。
        /// </summary>
        public int BakeWorldChunk(ElementWorldChunk chunk, Vector3 origin, float cellSize)
        {
            if (chunk == null)
                return 0;

            chunk.Grid.ClearSolidMask();
            if (_solidLayerMask.value == 0 && !_includeDefaultEnvironment)
            {
                GameLog.Warn(
                    "ElementFieldSolidBaker has an empty LayerMask; no World Cell will be marked Solid.",
                    "ElementField");
                return 0;
            }

            int solidCount = 0;
            Vector3 halfExtents = Vector3.one * (cellSize * 0.49f);
            int chunkSize = chunk.ChunkSize;
            for (int z = 0; z < chunkSize; z++)
            for (int y = 0; y < chunkSize; y++)
            for (int x = 0; x < chunkSize; x++)
            {
                var localCell = new Vector3Int(x, y, z);
                Vector3Int globalCell = ElementWorldCoordinates.ComposeGlobalCell(
                    chunk.Key,
                    localCell,
                    chunkSize);
                Vector3 center = origin + new Vector3(
                    (globalCell.x + 0.5f) * cellSize,
                    (globalCell.y + 0.5f) * cellSize,
                    (globalCell.z + 0.5f) * cellSize);
                // 动态冰不写入永久地形缓存，但新创建的 World Chunk 必须读取当前冰占据。
                // 冰移除时 World 会再次更新此格，避免留下永久隐形墙。
                bool isSolid = SampleSolidWorldCell(globalCell, origin, cellSize);
                int index = ElementFieldCoordinates.ToIndex(localCell, chunk.Grid.Dimensions);
                chunk.SolidMask[index] = isSolid;
                if (isSolid)
                    solidCount++;
            }

            return solidCount;
        }
    }
}
