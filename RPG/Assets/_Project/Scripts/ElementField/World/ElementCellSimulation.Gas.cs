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
            private readonly Vector3 _origin, _wind;
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
                _wind=profile.WindVelocity / size;
                if (!FluidSpawnRequestValidator.IsFinite(_wind)) throw new ArgumentException("风速超出当前元胞网格可表示的范围。");
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
                int first=(_world.FirstSide(cell)+(int)(_world._step%6))%6;
                for (int i=0;i<6 && _world.Movable(cell,material)!=0;i++)
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
                        Math.Max(0d,Vector3.Dot(_wind,direction))*concentration;
                    uint move=Credit(page,credit+face,rate,Math.Min(room,_world.Movable(cell,material)));
                    if (move==0) continue;
                    _world._editCount=0;
                    _world._edits[_world.Stage(cell)].After.Remove(material,move);
                    _world._edits[_world.Stage(target)].After.Add(material,move);
                    _world.Commit();
                }
                uint available=_world.Movable(cell,material);
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
                double accrued=page.Credits[index]+rate*_dt, whole=Math.Floor(accrued);
                page.Credits[index]=accrued-whole;
                if (whole<1) _world._retry=true;
                return (uint)Math.Min(limit,whole);
            }
        }
    }
}
