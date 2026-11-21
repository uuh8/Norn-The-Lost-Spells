using System;
using System.Collections.Generic;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class ElementCellSimulation
    {
        private GasEnvironment _gasEnvironment;
        internal bool HasGasEnvironment => _gasEnvironment != null;
        public ulong VentedGasQuantity(MaterialId material) => _gasEnvironment?.Vented[(byte)material] ?? 0;
        public ulong DissipatedGasQuantity(MaterialId material) => _gasEnvironment?.Dissipated[(byte)material] ?? 0;
        internal void ConfigureGasEnvironment(GasWorldProfile profile, Vector3 origin, float size, int chunkSize,
            Func<Vector3,MaterialId,double> ventRate, Action<MaterialId,ulong> dissipated)
        {
            if (_residentCount != 0 || _gasEnvironment != null) throw new InvalidOperationException("气体环境必须在初次投放前配置。");
            _gasEnvironment = new GasEnvironment(this,profile,origin,size,chunkSize,ventRate,dissipated);
        }
        internal void BeginGasEnvironmentTick(float dt, ElementWorldActivityRegions regions, bool hasVents, bool hasVentBounds = false)
            => _gasEnvironment?.Begin(dt,regions,hasVents,hasVentBounds);
        internal void WakeGasInBounds(Bounds bounds) => _gasEnvironment?.WakeInBounds(bounds);
        internal void StepGasDilution() => _gasEnvironment?.ClearDilute();

        private sealed partial class GasEnvironment
        {
            private readonly ElementCellSimulation _world;
            private readonly GasWorldProfile _profile;
            private readonly Vector3 _origin;
            private readonly double[] _windAlongFace=new double[6];
            private readonly float _size;
            private readonly int _edge;
            private readonly Func<Vector3,MaterialId,double> _vent;
            private readonly Action<MaterialId,ulong> _consume;
            private readonly GasMaterialBehavior[] _behaviors = new GasMaterialBehavior[2];
            internal readonly ulong[] Vented = new ulong[256], Dissipated = new ulong[256];
            private ElementWorldActivityRegions _domain;
            private float _dt;
            private bool _hasVents;
            private int _gasWork;
            private readonly List<Vector3Int> _waiting = new List<Vector3Int>();

            internal GasEnvironment(ElementCellSimulation world, GasWorldProfile profile, Vector3 origin, float size,
                int edge, Func<Vector3,MaterialId,double> vent, Action<MaterialId,ulong> consume)
            {
                if (profile == null || !float.IsFinite(size) || size <= 0 || edge < 1 ||
                    !FluidSpawnRequestValidator.IsFinite(profile.WindVelocity) || profile.MaximumCellsPerTick < 1 ||
                    profile.DiluteChunksPerTick < 1 || profile.DiluteChunksPerTick > 8 ||
                    !float.IsFinite(profile.DiluteDelaySeconds) || profile.DiluteDelaySeconds < 1 ||
                    !float.IsFinite(profile.DiluteMaximumChunkConcentration) || profile.DiluteMaximumChunkConcentration < 0 ||
                    profile.DiluteMaximumChunkConcentration > .02f || profile.CellCapacityGmu == 0 ||
                    profile.DiluteMaximumCellGmu > profile.CellCapacityGmu * .05d)
                    throw new ArgumentException("GasWorldProfile 的输运／消散配置无效。");
                _world=world; _profile=profile; _origin=origin; _size=size; _edge=edge;
                Vector3 wind=profile.WindVelocity / size;
                if (!FluidSpawnRequestValidator.IsFinite(wind)) throw new ArgumentException("风速超出当前元胞网格可表示的范围。");
                for(int face=0;face<6;face++) _windAlongFace[face]=Math.Max(0d,Vector3.Dot(wind,
                    face<4?Sides[face]:face==4?Vector3Int.up:Vector3Int.down));
                _vent=vent; _consume=consume;
                for (int i=0;i<profile.MaterialCount;i++)
                {
                    var b=profile.GetMaterial(i);
                    if (b.Material!=MaterialId.Steam && b.Material!=MaterialId.ToxicVapor ||
                        !float.IsFinite(b.DiffusionPerSecond) || b.DiffusionPerSecond < 0 || !float.IsFinite(b.BuoyancyPerSecond))
                        throw new ArgumentException("GasWorldProfile 含无效气体行为。");
                    int index=b.Material==MaterialId.Steam?0:1;
                    if (_behaviors[index].Material!=MaterialId.Empty) throw new ArgumentException("气体行为重复。");
                    _behaviors[index]=b;
                }
            }

            private Vector3 Center(Vector3Int cell) => _origin+((Vector3)cell+Vector3.one*.5f)*_size;
            private bool Active(Vector3Int cell) => _domain==null || _domain.Contains(Center(cell));

            internal bool TryBeginCell(Vector3Int cell)
            {
                if (_dt <= 0 || !Active(cell)) return false;
                if (_gasWork > 0) { _gasWork--; return true; }
                // 预算以格为单位，同格两种气体能在同一轮各用各的 GMU。未获预算的格保留本轮到达顺序，
                // 下一轮优先于本轮已获预算且仍在累计分数通量的格，避免恒定队列排列造成饥饿。
                _world._retry = true;
                _waiting.Add(cell);
                return false;
            }

            internal void PromoteWaitingCells()
            {
                for (int i = _waiting.Count - 1; i >= 0; i--)
                {
                    if (!_world._indices.TryGetValue(_waiting[i], out int index)) continue;
                    ref Slot slot = ref _world._slots[index];
                    if (!slot.Queued || _world._first == index) continue;
                    _world.Unlink(index);
                    slot.Queued = true;
                    slot.Previous = -1;
                    slot.Next = _world._first;
                    _world._slots[_world._first].Previous = index;
                    _world._first = index;
                    _world._activeCount++;
                }
                _waiting.Clear();
            }

            internal void Advance(Vector3Int cell, MaterialId material)
            {
                if (!_byCell.TryGetValue(cell,out int slot)) return;
                int species=material==MaterialId.Steam?0:1;
                var b=_behaviors[species];
                int credit=slot*14+species*7;
                var page=_byChunk[ChunkOf(cell)];
                Contents original=_world.Read(cell);
                uint concentration=original.Quantity(material);
                // 在一次连续的六面通量事务中，仅本方法能减少这份源额度。成功后本地扣减，避免每面反复查同一格。
                uint available=_world.Movable(cell,material);
                bool canReleaseSource=available==concentration && original.Amount==0 &&
                    (species==0 ? original.Vapor==0 : original.Steam==0);
                int first=(_world.FirstSide(cell)+(int)(_world._step%6))%6;
                for (int i=0;i<6 && available!=0;i++)
                {
                    int face=(first+i)%6;
                    Vector3Int direction=face<4?Sides[face]:face==4?Vector3Int.up:Vector3Int.down;
                    if (!Offset(cell,direction,out var target) || !Active(target) || _world.Blocked(target))
                    { page.Credits[credit+face]=0; continue; }
                    var sink=_world.Read(target);
                    uint room=sink.Free(_world.CellCapacity);
                    if (room==0) { page.Credits[credit+face]=0; continue; }
                    // 不同液面留下的气体容积不同，把目标浓度换算成源格等效 GMU 再求梯度。
                    double other=sink.Quantity(material)*(double)original.GasSpace(_world.CellCapacity)/sink.GasSpace(_world.CellCapacity);
                    double drift=direction.y*b.BuoyancyPerSecond;
                    double rate=(concentration-other)*b.DiffusionPerSecond/6d + drift*(drift>0?concentration:other) +
                        _windAlongFace[face]*concentration;
                    // 满槽时一笔“原格留有余量、另建目标格”的小额通量不能提交。纯气格能在等量迁移时
                    // 复用自己的槽，因而仅在开放真空、确实无槽的组合下等待此面按正常速率挣足整份 GMU。
                    bool packet=_world.FreeResidentSlots==0 && sink.Empty && canReleaseSource;
                    uint move=packet ? FullPacketCredit(page,credit+face,rate,available) :
                        Credit(page,credit+face,rate,Math.Min(room,available));
                    if (move==0) continue;
                    _world._editCount=0;
                    _world._edits[_world.Stage(cell)].After.Remove(material,move);
                    _world._edits[_world.Stage(target)].After.Add(material,move);
                    if (_world.Commit()) available-=move;
                    else if (packet) page.Credits[credit+face]=-(double)move; // 通知背压仍保留已满的面通量。
                }
                if (available==0) return;
                uint remove=Credit(page,credit+6,_hasVents && _vent!=null?_vent(Center(cell),material):0,available);
                if (remove==0) return;
                _world._editCount=0;
                _world._edits[_world.Stage(cell)].After.Remove(material,remove);
                if (_world.Commit()) Vented[(byte)material]+=remove;
            }

            private uint Credit(GasChunk page,int index,double rate,uint limit)
            {
                if (rate<=0 || limit==0) { page.Credits[index]=0; return 0; }
                double carry=page.Credits[index];
                // 恢复空槽后按正常 GMU 粒度扩散。只继承亚整数余量，不把等待整格搬迁的旧整数突然排入新槽。
                if (carry<0) { carry=-carry; carry-=Math.Floor(carry); }
                double accrued=carry+rate*_dt, whole=Math.Floor(accrued);
                page.Credits[index]=accrued-whole;
                if (whole<1) _world._retry=true;
                return (uint)Math.Min(limit,whole);
            }

            private uint FullPacketCredit(GasChunk page,int index,double rate,uint quantity)
            {
                if (rate<=0) { page.Credits[index]=0; return 0; }
                double accrued=Math.Abs(page.Credits[index])+rate*_dt;
                if (accrued<quantity)
                {
                    // 负号只区分“满槽时尚未提交的积分”，物质量始终只有世界真值中的正 GMU。
                    // 面被挡住、变满、梯度反转、物种离开原格时，现有分支会清除此额外积分。
                    page.Credits[index]=-accrued; _world._retry=true; return 0;
                }
                page.Credits[index]=accrued-Math.Floor(accrued);
                return quantity;
            }
        }
    }
}
