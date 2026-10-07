using System;
using System.Collections.Generic;
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
        private readonly Vector3 _windCells;
        private readonly Dictionary<Vector3Int,int> _creditSlots;
        private readonly Vector3Int[] _creditKeys;
        private readonly double[] _credits;
        private int _creditCount,_creditCursor;
        private uint _step;
        public ulong VentedQuantity { get; private set; }
        private readonly ulong[] _ventedByMaterial=new ulong[256];
        internal ulong VentedMaterialQuantity(MaterialId material) => _ventedByMaterial[(byte)material];
        public int ProcessedCellsLastTick { get; private set; }
        public GasFluxSolver(GasVolumeStore store, MaterialCellActivityQueue activity,
            GasMaterialBehavior[] behaviors, int maximumCells, Func<Vector3Int, bool> blocked,
            Func<Vector3Int, MaterialId, float, uint> ventRate = null,
            Vector3 windVelocity = default, float cellSize = 1f, int creditCapacity = 1024)
        {
            if (store == null || activity == null || maximumCells < 1 || blocked == null
                || behaviors == null || behaviors.Length != store.MaterialCount
                || !FluidSpawnRequestValidator.IsFinite(windVelocity) || !float.IsFinite(cellSize) || cellSize<=0 || creditCapacity<1)
                throw new ArgumentException("Gas Solver 的数量／活动／行为配置不完整。");
            _store = store; _activity = activity; _blocked = blocked;
            _behaviors = (GasMaterialBehavior[])behaviors.Clone(); _work = new Vector3Int[maximumCells];
            for (int i = 0; i < behaviors.Length; i++)
                if (behaviors[i].Material != store.GetMaterial(i)
                    || !float.IsFinite(behaviors[i].DiffusionPerSecond) || behaviors[i].DiffusionPerSecond < 0
                    || !float.IsFinite(behaviors[i].BuoyancyPerSecond))
                    throw new ArgumentException("Gas 行为必须与 Store 材料顺序匹配且系数有效。");
            _ventRate = ventRate;
            _windCells=windVelocity/cellSize;
            _creditSlots=new Dictionary<Vector3Int,int>(creditCapacity);
            _creditKeys=new Vector3Int[creditCapacity];
            _credits=new double[checked(creditCapacity*behaviors.Length*Directions.Length)];
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
            _step++;
            for (int i = 0; i < ProcessedCellsLastTick; i++)
            {
                Vector3Int source = _work[i];
                if (_blocked(source)) continue;
                int creditSlot=-1;
                for (int m = 0; m < _behaviors.Length; m++)
                {
                    GasMaterialBehavior behavior = _behaviors[m];
                    if (!_store.TryGetQuantity(source, behavior.Material, out uint original)) continue;
                    if(creditSlot<0) creditSlot=CreditSlot(source);
                    uint remaining = original;
                    int first=FirstFace(source,_step,m);
                    for (int visit = 0; visit < Directions.Length && remaining > 0; visit++)
                    {
                        int side=(first+visit)%Directions.Length;
                        Vector3Int target = source + Directions[side];
                        // 冻结边界暂停通量，不伪造永久墙；返场后的恢复扫描再次推进。
                        if (!_activity.Contains(target)) continue;
                        _store.TryGetQuantity(target, behavior.Material, out uint other);
                        double diffusion = ((double)original - other) * behavior.DiffusionPerSecond / 6d;
                        double rising = Directions[side].y * behavior.BuoyancyPerSecond
                            * (Directions[side].y * behavior.BuoyancyPerSecond > 0 ? original : other);
                        double advection=Math.Max(0f,Vector3.Dot(_windCells,Directions[side]))*original;
                        double desired=Math.Max(0d,(diffusion+rising+advection)*deltaTime);
                        int credit=(creditSlot*_behaviors.Length+m)*Directions.Length+side;
                        if(desired==0) { _credits[credit]=0; continue; }
                        // Credit 只保存不足 1 GMU 的时间小数；它没有材料所有权。
                        // 即使本 Tick 不能移动 1 GMU，也保持活动，避免稀薄气体永久变成静止小方块。
                        double accumulated=desired+_credits[credit];
                        uint proposed=(uint)Math.Min(remaining,Math.Floor(accumulated));
                        _credits[credit]=accumulated-Math.Floor(accumulated);
                        // 整数通量不足 1 GMU 时，本轮无法移动。先算数量，再花有限 Terrain 预算，
                        // 避免大量稀薄/平衡气体为六个不会使用的邻面执行 Physics 查询与预热。
                        if(proposed==0) { _activity.Wake(source); continue; }
                        // 完整通量遇已知墙/满容量时不重复唤醒静止库存。开门、目标流出或转换
                        // 已有局部 WakeNeighbors；未知 Terrain 的延期也会保留恢复请求。
                        if(_blocked(target)) continue;
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
        private static int FirstFace(Vector3Int cell,uint tick,int material)
        {
            // 整数 GMU 的首个可用面会先消费余量。坐标/Tick 的 Hash 打散访问优先级，
            // 不再总向 Up 偏移；Diffusion 仍受浓度梯度控制，不把 Random 当作虚构风力。
            // 确定性 Hash 无全局 Unity Random 状态、分配或冻结时间补算。
            unchecked
            {
                uint hash=(uint)cell.x*73856093u^(uint)cell.y*19349663u^(uint)cell.z*83492791u^tick*2654435761u^(uint)material*374761393u;
                hash^=hash>>16; hash*=2246822519u; hash^=hash>>13; hash*=3266489917u; hash^=hash>>16;
                return (int)(hash%6);
            }
        }
        private int CreditSlot(Vector3Int cell)
        {
            if(_creditSlots.TryGetValue(cell,out int slot)) return slot;
            if(_creditCount<_creditKeys.Length) slot=_creditCount++;
            else
            {
                slot=_creditCursor; _creditCursor=(_creditCursor+1)%_creditKeys.Length;
                _creditSlots.Remove(_creditKeys[slot]);
                Array.Clear(_credits,slot*_behaviors.Length*Directions.Length,_behaviors.Length*Directions.Length);
            }
            // 有界替换只舍弃 sub-GMU Credit，不删除真实库存，也不积累墙外/冻结时间的补算。
            _creditKeys[slot]=cell; _creditSlots.Add(cell,slot); return slot;
        }
    }
}
