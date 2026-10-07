using System;
using System.Collections.Generic;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 有界的局部重力／侧向扩散近似；每次 Move 在 Store 内同时扣、加。
    /// 目前不求整片湖泊的压力，稳定大水体由后续 Stable Region 承担。
    /// </summary>
    public sealed class LiquidFluxSolver
    {
        private static readonly Vector3Int[] Sides =
            { Vector3Int.right, Vector3Int.forward, Vector3Int.left, Vector3Int.back };
        private readonly LiquidVolumeStore _store;
        private readonly LiquidActivityScheduler _activity;
        private readonly StableLiquidRegionRuntime _stable;
        private readonly MaterialId[] _materials;
        private readonly int[] _downRates, _sideRates, _sideDeadbands;
        private readonly int[] _spreadReserves;
        private readonly float[] _relativeDensities;
        private readonly LiquidTerrainSupportCache _terrain;
        private readonly LiquidTransportState _ground;
        private readonly LiquidTransportHistory _history;
        private readonly float[] _drags, _groundSpeeds;
        private readonly Vector3Int[] _groundTargets=new Vector3Int[4];
        private Vector4 _groundHeights;
        private readonly struct FallKey : IEquatable<FallKey>
        {
            public readonly Vector3Int Cell;
            public readonly MaterialId Material;
            public FallKey(Vector3Int cell, MaterialId material) { Cell = cell; Material = material; }
            public bool Equals(FallKey other) => Cell == other.Cell && Material == other.Material;
            public override bool Equals(object obj) => obj is FallKey other && Equals(other);
            public override int GetHashCode() => unchecked(Cell.GetHashCode() * 397 ^ (int)Material);
        }
        private struct FallState
        {
            public float Offset, Velocity, SafeDistance, SampleTime;
            public Vector3 HorizontalVelocity,HorizontalOffset;
            public int AdvancedTick;
        }
        private readonly Dictionary<FallKey, FallState> _falling;
        private readonly int _maximumFalling;
        private readonly float _cellSize, _gravity, _terminalVelocity;
        private float _deltaTime, _sampleTime;
        public float MotionHorizon => _deltaTime;
        private int _tick;

        public LiquidFluxSolver(LiquidVolumeStore store, LiquidActivityScheduler activity,
            StableLiquidRegionRuntime stable,
            MaterialId[] materials, int[] downRates, int[] sideRates, int[] sideDeadbands,
            float cellSize = .25f, float gravity = 9.81f, float terminalVelocity = 12f,
            int maximumFalling = 4096, int[] spreadReserves = null, float[] relativeDensities = null,
            LiquidTerrainSupportCache terrain = null, float[] drags = null, float[] groundSpeeds = null,
            LiquidTransportHistory history = null)
        {
            _store = store;
            _activity = activity;
            _stable = stable;
            _materials = materials;
            _downRates = downRates;
            _sideRates = sideRates;
            _sideDeadbands = sideDeadbands;
            _spreadReserves = spreadReserves;
            _relativeDensities = relativeDensities;
            _terrain = terrain; _drags = drags; _groundSpeeds = groundSpeeds; _history = history;
            _ground = new LiquidTransportState(maximumFalling);
            _cellSize = cellSize; _gravity = gravity; _terminalVelocity = terminalVelocity;
            _maximumFalling = maximumFalling;
            _falling = new Dictionary<FallKey, FallState>(maximumFalling);
        }

        public int Step(int maximumCells, float deltaTime = .1f, float sampleTime = 0f)
        {
            if (deltaTime <= 0f) return 0;
            _deltaTime = deltaTime; _sampleTime = sampleTime;
            _activity.BeginStep();
            // 本轮只处理开始时已有的队列；新扰动留给下一 Tick，避免一帧贯穿整湖。
            int count = Mathf.Min(maximumCells, _activity.Count);
            for (int i = 0; i < count; i++)
            {
                if (!_activity.TryDequeue(out Vector3Int cell)) break;
                for (int material = 0; material < _materials.Length; material++)
                    FlowMaterial(cell, material);
            }
            _tick++;
            return count;
        }

        public bool TryGetFallingMotion(Vector3Int cell, MaterialId material,
            out LiquidFallingMotion motion)
        {
            motion = default;
            if (!_store.TryGetQuantity(cell, material, out uint quantity) || quantity == 0
                || !_falling.TryGetValue(new FallKey(cell, material), out FallState state)) return false;
            motion = new LiquidFallingMotion(state.Offset, state.Velocity, _gravity,
                state.SafeDistance, state.SampleTime,state.HorizontalOffset,state.HorizontalVelocity);
            return true;
        }

        public bool TryGetGroundVelocity(Vector3Int cell, MaterialId material, out Vector3 velocity)
        {
            velocity = Vector3.zero;
            if(!_store.TryGetQuantity(cell,material,out uint q) || q==0)return false;
            if(_falling.TryGetValue(new FallKey(cell,material),out FallState fall))
            {velocity=fall.HorizontalVelocity+Vector3.down*fall.Velocity;return true;}
            if (!_ground.TryRead(cell, material, _tick, out LiquidTransportState.State state)) return false;
            velocity = state.Velocity; return true;
        }

        private Vector3Int GroundTarget(Vector3Int source, Vector3Int direction)
        {
            Vector3Int target = source + direction;
            if (_terrain == null || _terrain.TryGet(target, out _)) return target;
            // 搜索有限邻列支撑层，沿斜面迁移到正确高度，不能只在同一y格横向扩散。
            for (int dy = -1; dy >= -2; dy--)
                if (_terrain.TryGet(target + Vector3Int.up * dy, out _)) return target + Vector3Int.up * dy;
            for (int dy = 1; dy <= 2; dy++)
                if (_terrain.TryGet(target + Vector3Int.up * dy, out _)) return target + Vector3Int.up * dy;
            return target;
        }

        private void FlowMaterial(Vector3Int source, int materialIndex)
        {
            MaterialId material = _materials[materialIndex];
            var fallKey = new FallKey(source, material);
            if (!_store.TryGetQuantity(source, material, out uint quantity) || quantity == 0)
            { _falling.Remove(fallKey); return; }
            _history?.CaptureBefore(source);
            // 无支撑液体按连续位移落下，不在空中执行“地面摊开”的四向扩散。
            // 速度只为活动落水保存；平静水池仍然只有 GMU，没有逐粒子物理负担。
            if (TryFall(source, material, quantity, fallKey)) return;
            if(_falling.TryGetValue(fallKey,out FallState landed))
            {
                // 着地后保留切向动量，下一段Ground积分按新Normal去掉法向分量。
                _ground.Put(source,material,new LiquidTransportState.State
                {Velocity=landed.HorizontalVelocity+Vector3.down*landed.Velocity,Tick=_tick-1});
            }
            _falling.Remove(fallKey);
            Vector3Int below = source + Vector3Int.down;
            int proposedDown = Mathf.Min((int)quantity, _downRates[materialIndex]);
            int downCapacity = _store.FreeCapacity(below);
            if (downCapacity < proposedDown && _stable != null && _stable.RegionCount > 0)
                downCapacity = Mathf.Max(downCapacity,
                    _stable.AvailableAbsorption(below, material));
            int down = Mathf.Min(proposedDown, downCapacity);
            if (down > 0) Move(source, below, material, down);
            if(_relativeDensities!=null)
                for(int otherIndex=0;otherIndex<_materials.Length;otherIndex++)
                {
                    if(_relativeDensities[materialIndex]<=_relativeDensities[otherIndex]
                        || !_store.TryGetQuantity(source,material,out quantity)
                        || !_store.TryGetQuantity(below,_materials[otherIndex],out uint lighter))continue;
                    uint exchange=(uint)Mathf.Min(_downRates[materialIndex],Mathf.Min((int)quantity,(int)lighter));
                    if(_store.TryExchange(source,below,material,_materials[otherIndex],exchange))
                    {_activity.WakeNeighbors(source);_activity.WakeNeighbors(below);}
                }

            LiquidTerrainSupport support = default;
            bool onGround = _terrain != null && _terrain.TryGet(source, out support);
            bool onSlope=onGround && support.Normal.y<.999f;
            float pressureRelaxation=onSlope?1-Mathf.Exp(-8*_deltaTime/(1+(_drags!=null?_drags[materialIndex]:1.5f))):1;
            LiquidTransportState.State groundState = default;
            if (onGround)
            {
                bool existing = _ground.TryRead(source, material, _tick, out groundState);
                if (existing && groundState.Tick == _tick) { _activity.Wake(source); return; }
                float head = support.Height + _store.OccupancyAt(source) / (float)_store.CapacityGmu * _cellSize;
                Vector3 pressure = Vector3.zero;
                for (int i = 0; i < 4; i++)
                {
                    Vector3Int target = GroundTarget(source, Sides[i]);
                    _groundTargets[i]=target;_groundHeights[i]=_terrain.BaseHeight(target);
                    if (_store.IsSolid(target)) continue;
                    float otherHead = _groundHeights[i] + _store.OccupancyAt(target) / (float)_store.CapacityGmu * _cellSize;
                    pressure += (Vector3)Sides[i] * Mathf.Clamp((head - otherHead) / _cellSize, -2f, 2f) * (_gravity * .25f);
                }
                groundState.Velocity = LiquidTransportMath.IntegrateGroundVelocity(groundState.Velocity,
                    support.Normal, pressure, _gravity, _drags != null ? _drags[materialIndex] : 1.5f,
                    _deltaTime, _groundSpeeds != null ? _groundSpeeds[materialIndex] : 2f);
                groundState.Tick = _tick;
                // Ground Warp只能沿可接收的方向前推；墙前去掉被阻挡的水平分量，不能视觉穿墙。
                for(int i=0;i<4;i++)if(Vector3.Dot(groundState.Velocity,Sides[i])>0
                    && (_store.IsSolid(_groundTargets[i]) || _store.FreeCapacity(_groundTargets[i])==0))
                {if(Sides[i].x!=0)groundState.Velocity.x=0;else groundState.Velocity.z=0;}
                groundState.Velocity.y=-(support.Normal.x*groundState.Velocity.x+support.Normal.z*groundState.Velocity.z)/support.Normal.y;
                _ground.Put(source, material, groundState);
            }

            int offset = _tick & 3;
            for (int i = 0; i < 4; i++)
            {
                if (!_store.TryGetQuantity(source, material, out quantity) || quantity == 0) break;
                int directionIndex = (i + offset) & 3;
                Vector3Int target = onGround ? _groundTargets[directionIndex] : source + Sides[directionIndex];
                // 只从更满的水平格流向较空的格；轮换方向抵消固定扫描顺序的偏向。
                _store.TryGetQuantity(target, material, out uint other);
                int difference = (int)_store.OccupancyAt(source) - (int)_store.OccupancyAt(target);
                if (onGround)
                    difference = Mathf.RoundToInt((support.Height - _groundHeights[directionIndex])
                        / _cellSize * _store.CapacityGmu) + difference;
                // 坡脚粗格的一个角仍碰到斜坡，SolidMask会把整格底层占住；中心却已在地面。
                // 向相接低索引格释放这份Rasterization悬高，不能让平地聚团阈值堵住出口。
                bool rasterDescent=onGround && target.y<source.y
                    && _terrain.CellBottom(source)-support.Height>_cellSize*.5f
                    && _groundHeights[directionIndex]<=support.Height+_cellSize*.1f;
                if(rasterDescent)difference=Mathf.Max(difference,_store.CapacityGmu);
                // 8/255 GMU 级的水位差肉眼很难察觉；允许低幅不平衡可阻止微量
                // 往复流动把整个湖面逐格唤醒。数量仍留在原格，完全不丢失。
                float transported = onGround ? Mathf.Max(0f, Vector3.Dot(groundState.Velocity, Sides[directionIndex]))
                    * _deltaTime / _cellSize * quantity + groundState.Remainder[directionIndex] : 0f;
                if(onSlope)
                {
                    // 斜面压力与动量都以有界通量逐步搬运，不能让小量每Tick整格跳走。
                    float maximumFraction=Mathf.Min(.45f,(_groundSpeeds!=null?_groundSpeeds[materialIndex]:2)*_deltaTime/_cellSize);
                    transported=Mathf.Min(quantity*maximumFraction,Mathf.Max(transported-groundState.Remainder[directionIndex],Mathf.Max(0,difference)*.5f*pressureRelaxation))
                        +groundState.Remainder[directionIndex];
                }
                // 先保存量化余数。小于1GMU的坡面通量需跨Tick累积，不能在continue时遗失。
                if(onGround)groundState.Remainder[directionIndex]=transported-Mathf.Floor(transported);
                if (difference <= _sideDeadbands[materialIndex] && transported < 1f) continue;
                int sideCapacity = _store.FreeCapacity(target);
                if (_stable != null && _stable.RegionCount > 0
                    && _stable.OwnsMaterial(target, material))
                    sideCapacity = _stable.AvailableHorizontalAbsorption(source,
                        target, material, quantity);
                int amount = Mathf.Min(Mathf.Min(_sideRates[materialIndex], onSlope?Mathf.FloorToInt(transported):Mathf.Max(difference / 2, Mathf.FloorToInt(transported))),
                    sideCapacity);
                amount=Mathf.Min(amount,(int)quantity);
                int reserve = _spreadReserves != null ? _spreadReserves[materialIndex] : 0;
                if (reserve > 0 && !onSlope && !rasterDescent && _store.OccupancyAt(target)==0
                    && !(_stable != null && _stable.OwnsMaterial(target, material)))
                {
                    // 卡通聚团近似：新空格的最小团量受本材料通量上限约束。
                    // 避免不断唤醒几 GMU 的纸片；重力、已有湿格均衡、湖泊注入照常运行。
                    // Store 成对扣加，画面夸张不通过丢失数量实现。
                    int seed = Mathf.Max(1,Mathf.Min(_sideRates[materialIndex],(_store.CapacityGmu+3)/4));
                    int available = (int)quantity - reserve;
                    if (available < seed || sideCapacity < seed) continue;
                    amount = Mathf.Min(amount,available);
                    if(amount<seed)continue;
                }
                if (amount > 0 && Move(source, target, material, amount) && onGround)
                {
                    // 本Tick目标只能继承姿态，不二次积分；下一Tick再按其自己的坡面Normal修正。
                    var inherited = groundState; inherited.Remainder = Vector4.zero;
                    _ground.Put(target, material, inherited);
                }
            }
            if (onGround) _ground.Put(source, material, groundState);
            // 平地聚团阈值不能让少量水永久停在坡上；有可行速度的剩余量继续积累通量。
            if(onSlope && groundState.Velocity.sqrMagnitude>1e-6f && _store.TryGetQuantity(source,material,out uint remaining) && remaining>0)
                _activity.Wake(source);
        }

        private bool TryFall(Vector3Int source, MaterialId material, uint quantity, FallKey key)
        {
            bool existing = _falling.TryGetValue(key, out FallState state);
            if (existing && state.AdvancedTick == _tick)
            {
                // 目标格可能早就在本 Tick 队列中；跳过二次积分后仍须保留下一 Tick 的 Wake。
                _activity.Wake(source); return true;
            }
            if (_store.FreeCapacity(source + Vector3Int.down) < quantity
                || (_stable != null && _stable.HasForeignOccupancy(source+Vector3Int.down,material))) return false;
            if (!existing && _falling.Count == _maximumFalling)
            {
                // 过载时保留数量并继续排队，不能扩容字典产生 GC 或退回空中横向摊开。
                _activity.Wake(source); return true;
            }
            int free = 0;
            for (int i = 1; i <= 8; i++)
            {
                var target = source + Vector3Int.down * i;
                if (_store.FreeCapacity(target) < quantity
                    || (_stable != null && (_stable.OwnsMaterial(target, material)
                        || _stable.HasForeignOccupancy(target,material)))) break;
                free++;
            }
            if (free == 0) return false;
            if(!existing && _ground.TryRead(source,material,_tick,out LiquidTransportState.State inherited))
            {
                if(inherited.Tick==_tick){_activity.Wake(source);return true;}
                // Ground Drag只在有支撑时生效；离坡后继承动量，空气中保持横向速度并继续受重力。
                state.HorizontalVelocity=new Vector3(inherited.Velocity.x,0,inherited.Velocity.z);
                state.Velocity=Mathf.Max(0,-inherited.Velocity.y);
            }
            // 梯形速度积分：位移 v*dt + 0.5*g*dt²，避免固定“一 Tick 一格”。
            float velocity = Mathf.Min(_terminalVelocity, state.Velocity + _gravity * _deltaTime);
            float distance = (state.Velocity + velocity) * .5f * _deltaTime / _cellSize;
            float offset = state.Offset + distance;
            int cells = Mathf.Min(free, Mathf.Min(8, Mathf.FloorToInt(offset)));
            Vector3Int destination = source + Vector3Int.down * cells;
            AdvanceAirAxis(source,ref destination,material,quantity,ref state,0,cells);
            AdvanceAirAxis(source,ref destination,material,quantity,ref state,2,cells);
            bool moved=destination!=source;
            if (moved && !_store.TryTransfer(source, destination, material, quantity))
            { _activity.Wake(source); return true; }
            if (moved) { _falling.Remove(key); _activity.WakeNeighbors(source); }
            state.Offset = Mathf.Clamp(offset - cells, 0f, .9999f);
            state.Velocity = velocity;
            state.AdvancedTick = _tick;
            state.SampleTime = _sampleTime;
            // Shader 只能前推已验证的空间，不能让插值视觉穿过地板或湖面。
            // 横向跨列后必须重查目标列净空，旧列的安全距离不能作为新列的视觉预测依据。
            int remaining=0;
            for(int i=1;i<=8;i++)
            {var next=destination+Vector3Int.down*i;if(_store.FreeCapacity(next)<quantity
                || (_stable!=null && (_stable.OwnsMaterial(next,material) || _stable.HasForeignOccupancy(next,material))))break;remaining++;}
            state.SafeDistance = Mathf.Max(0f, (remaining - state.Offset) * _cellSize);
            _falling[new FallKey(destination, material)] = state;
            _store.MarkMotionChanged(destination, material);
            _activity.WakeNeighbors(destination);
            return true;
        }

        private void AdvanceAirAxis(Vector3Int source,ref Vector3Int destination,MaterialId material,uint quantity,
            ref FallState state,int axis,int verticalCells)
        {
            float speed=state.HorizontalVelocity[axis];if(Mathf.Abs(speed)<.00001f)return;
            Vector3Int direction=axis==0?Vector3Int.right:Vector3Int.forward;if(speed<0)direction=-direction;
            // 分别检查离开列和落到列，不能用一次对角搬运跳过墙角或跨进Stable的未展开区域。
            bool clear=true;
            for(int dy=0;dy<=verticalCells;dy++)
            {
                Vector3Int next=source+Vector3Int.down*dy+direction;
                if(_store.FreeCapacity(next)<quantity || (_stable!=null && (_stable.OwnsMaterial(next,material)
                    || _stable.HasForeignOccupancy(next,material)))){clear=false;break;}
            }
            if(!clear){state.HorizontalVelocity[axis]=0;state.HorizontalOffset[axis]=0;return;}
            state.HorizontalOffset[axis]+=speed*_deltaTime/_cellSize;
            int shift=state.HorizontalOffset[axis]>=1?1:state.HorizontalOffset[axis]<=-1?-1:0;
            if(shift==0)return;
            Vector3Int target=destination+(axis==0?Vector3Int.right:Vector3Int.forward)*shift;
            if(_store.FreeCapacity(target)<quantity || (_stable!=null && (_stable.OwnsMaterial(target,material)
                || _stable.HasForeignOccupancy(target,material)))){state.HorizontalVelocity[axis]=0;state.HorizontalOffset[axis]=0;return;}
            destination=target;state.HorizontalOffset[axis]-=shift;
        }

        private bool Move(Vector3Int source, Vector3Int target, MaterialId material, int amount)
        {
            if (_stable != null && _stable.TryAbsorb(source, target, material, (uint)amount))
                return true;
            if (_stable != null && _stable.OwnsMaterial(target, material)) return false;
            if (_stable != null && !_stable.TryPrepareMaterialContact(target,material))
            {
                // 展开预算不足时保留源格并继续排队，不能穿进静水表面或遗失重试机会。
                _activity.Wake(source); return false;
            }
            if (!_store.TryTransfer(source, target, material, (uint)amount)) return false;
            _activity.WakeNeighbors(source);
            _activity.WakeNeighbors(target);
            return true;
        }
    }
}
