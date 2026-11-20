using Game.Core;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 天然液体的 Scene Authoring：世界初始化后直接填充权威格子。
    /// 静止湖泊不需要先生成 PBF 粒子或等待沉降；需要开始流动的区域可显式勾选。
    /// </summary>
    public sealed class GridLiquidInitialVolume : MonoBehaviour
    {
        [SerializeField] private LiquidWorldRuntime _liquids;
        [SerializeField] private MaterialId _material = MaterialId.Water;
        [SerializeField] private Bounds _worldBounds = new Bounds(Vector3.zero, Vector3.one);
        [SerializeField, Range(0.01f, 1f)] private float _fill = 1f;
        [SerializeField] private bool _startActive;
        internal LiquidWorldRuntime Owner => _liquids;
        internal ElementWorldRuntime MigrationWorld { get; set; }
        internal void ReadForMigration(CellularInitialContents plan)
        {
            if (!float.IsFinite(_fill) || _fill < 0 || _fill > 1) throw new System.InvalidOperationException("旧液体填充比例无效。");
            var bounds = plan.CoarseBounds(_worldBounds);
            foreach (var cell in bounds.allPositionsWithin)
            {
                float size = plan.Volume.CellSize;
                Vector3 minimum = plan.Origin + (Vector3)cell * size;
                Vector3 overlap = Vector3.Max(Vector3.zero, Vector3.Min(_worldBounds.max, minimum + Vector3.one * size)
                    - Vector3.Max(_worldBounds.min, minimum));
                uint quantity = (uint)Mathf.RoundToInt(plan.Volume.CapacityGmu * _fill *
                    (overlap.x * overlap.y * overlap.z / (size * size * size)));
                plan.Add(cell, _material, quantity, !_startActive);
            }
        }

        private void Start()
        {
            if (ElementWorldRuntime.IsReplicaAuthor(_liquids) || MigrationWorld != null && MigrationWorld.OwnsImportedAuthor(this)) return;
            if (_liquids == null || !_liquids.IsFluidInitialized)
            {
                GameLog.Error("Grid Liquid Initial Volume 缺少已初始化液体世界。", "ElementField");
                return;
            }
            if (_worldBounds.size.x <= 0f || _worldBounds.size.y <= 0f
                || _worldBounds.size.z <= 0f) return;
            Vector3Int minimum = ElementWorldCoordinates.WorldToGlobalCell(
                _worldBounds.min, _liquids.Origin, _liquids.CellSize);
            Vector3Int maximum = ElementWorldCoordinates.WorldToGlobalCell(
                _worldBounds.max - Vector3.one * (_liquids.CellSize * .0001f),
                _liquids.Origin, _liquids.CellSize);
            int rejected = 0;
            for (int z = minimum.z; z <= maximum.z; z++)
            for (int y = minimum.y; y <= maximum.y; y++)
            for (int x = minimum.x; x <= maximum.x; x++)
            {
                var cell = new Vector3Int(x, y, z);
                Vector3 cellMinimum = _liquids.Origin + (Vector3)cell * _liquids.CellSize;
                Vector3 overlap = Vector3.Max(Vector3.zero,
                    Vector3.Min(_worldBounds.max, cellMinimum + Vector3.one * _liquids.CellSize)
                    - Vector3.Max(_worldBounds.min, cellMinimum));
                // Bounds 是世界体积：粗格只部分相交时按重叠体积记账，不能因为放大格子
                // 就把 .25m 高的初始水块填成 .5m 高。亚 GMU 的部分按整数精度舍入。
                float fraction = overlap.x * overlap.y * overlap.z
                    / (_liquids.CellSize * _liquids.CellSize * _liquids.CellSize);
                uint quantity = (uint)Mathf.RoundToInt(_liquids.CellCapacityGmu * _fill * fraction);
                if (quantity != 0 && !_liquids.TryAddInitial(cell, _material,
                        quantity, _startActive)) rejected++;
            }
            if (rejected > 0)
                GameLog.Warn($"Grid Liquid 初始区域有 {rejected} 格未接纳；请检查地形、重叠体积和 Chunk 预算。",
                    "ElementField");
        }
    }
}
