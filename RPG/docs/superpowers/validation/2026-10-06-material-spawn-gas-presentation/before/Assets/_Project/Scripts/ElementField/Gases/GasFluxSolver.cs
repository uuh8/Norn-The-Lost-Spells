using System;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 气体是浓度场：按六邻面交换真实 GMU，Steam 的上升偏好来自 Profile。
    /// Current 整轮只读，Next 预约每面的容量，最后只提交触及槽位；无出口时不扣量。
    /// </summary>
    public sealed class GasFluxSolver
    {
        private static readonly Vector3Int[] Directions =
        { Vector3Int.up, Vector3Int.down, Vector3Int.left, Vector3Int.right, Vector3Int.forward, Vector3Int.back };
        private readonly GasVolumeStore _store;
        private readonly MaterialCellActivityQueue _activity;
        private readonly Func<Vector3Int, bool> _blocked;
        private readonly GasMaterialBehavior[] _behaviors;
        private readonly Vector3Int[] _work;
        private readonly Func<Vector3Int, MaterialId, float, uint> _ventRate;
        public ulong VentedQuantity { get; private set; }
        private readonly ulong[] _ventedByMaterial=new ulong[256];
        internal ulong VentedMaterialQuantity(MaterialId material) => _ventedByMaterial[(byte)material];
        public int ProcessedCellsLastTick { get; private set; }
        public GasFluxSolver(GasVolumeStore store, MaterialCellActivityQueue activity,
            GasMaterialBehavior[] behaviors, int maximumCells, Func<Vector3Int, bool> blocked,
            Func<Vector3Int, MaterialId, float, uint> ventRate = null)
        {
            if (store == null || activity == null || maximumCells < 1 || blocked == null
                || behaviors == null || behaviors.Length != store.MaterialCount)
                throw new ArgumentException("Gas Solver 的数量／活动／行为配置不完整。");
            _store = store; _activity = activity; _blocked = blocked;
            _behaviors = (GasMaterialBehavior[])behaviors.Clone(); _work = new Vector3Int[maximumCells];
            for (int i = 0; i < behaviors.Length; i++)
                if (behaviors[i].Material != store.GetMaterial(i)
                    || !float.IsFinite(behaviors[i].DiffusionPerSecond) || behaviors[i].DiffusionPerSecond < 0
                    || !float.IsFinite(behaviors[i].BuoyancyPerSecond))
                    throw new ArgumentException("Gas 行为必须与 Store 材料顺序匹配且系数有效。");
            _ventRate = ventRate;
        }
        public void Step(float deltaTime)
        {
            ProcessedCellsLastTick = 0;
            if (!float.IsFinite(deltaTime) || deltaTime <= 0f || _store.TotalQuantity == 0) return;
            _activity.BeginStep();
            int count = Math.Min(_work.Length, _activity.Count);
            for (int i = 0; i < count; i++)
                if (_activity.TryDequeue(out Vector3Int cell)) _work[ProcessedCellsLastTick++] = cell;
            _store.BeginFluxTick();
            for (int i = 0; i < ProcessedCellsLastTick; i++)
            {
                Vector3Int source = _work[i];
                if (_blocked(source)) continue;
                for (int m = 0; m < _behaviors.Length; m++)
                {
                    GasMaterialBehavior behavior = _behaviors[m];
                    if (!_store.TryGetQuantity(source, behavior.Material, out uint original)) continue;
                    uint remaining = original;
                    for (int side = 0; side < Directions.Length && remaining > 0; side++)
                    {
                        Vector3Int target = source + Directions[side];
                        // 冻结边界暂停通量，不伪造永久墙；返场后的恢复扫描再次推进。
                        if (!_activity.Contains(target)) continue;
                        _store.TryGetQuantity(target, behavior.Material, out uint other);
                        double diffusion = ((double)original - other) * behavior.DiffusionPerSecond / 6d;
                        double rising = Directions[side].y * behavior.BuoyancyPerSecond
                            * (Directions[side].y * behavior.BuoyancyPerSecond > 0 ? original : other);
                        double desired = Math.Max(0d, (diffusion + rising) * deltaTime);
                        uint proposed = (uint)Math.Min(remaining, Math.Floor(desired));
                        // 整数通量不足 1 GMU 时，本轮无法移动。先算数量，再花有限 Terrain 预算，
                        // 避免大量稀薄/平衡气体为六个不会使用的邻面执行 Physics 查询与预热。
                        if(proposed==0 || _blocked(target)) continue;
                        uint moved = _store.Transfer(source, target, behavior.Material, proposed);
                        if (moved == 0) continue;
                        remaining -= moved; _activity.WakeNeighbors(source); _activity.WakeNeighbors(target);
                    }
                    if (_ventRate != null)
                    {
                        uint removed = _store.DrainNext(source, behavior.Material,
                            Math.Min(remaining, _ventRate(source, behavior.Material, deltaTime)));
                        VentedQuantity += removed;
                        _ventedByMaterial[(byte)behavior.Material]+=removed;
                        if (removed > 0) _activity.WakeNeighbors(source);
                    }
                }
            }
            _store.CommitFluxTick();
        }
    }
}
