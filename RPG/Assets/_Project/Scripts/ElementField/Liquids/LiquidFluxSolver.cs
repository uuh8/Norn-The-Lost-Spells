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
        public float Gravity=>_gravity;
        public float TerminalVelocity=>_terminalVelocity;
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
            // 此scope仅给只读网络表现标记来源，原调度数量、积分顺序和dt均保持不变。
            // finally避免异常留下“后续F写入也是Solver运动”的错误资格。
            bool previousScope = _history != null && _history.SetSolverCaptureScope(true);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    if (!_activity.TryDequeue(out Vector3Int cell)) break;
                    for (int material = 0; material < _materials.Length; material++)
                        FlowMaterial(cell, material);
                }
            }
            finally { _history?.SetSolverCaptureScope(previousScope); }
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

        internal int AvailableInitialMotionSlots=>Mathf.Max(0,_maximumFalling-_falling.Count);
        internal bool CanAllocateInitialMotion(Vector3Int cell,MaterialId material)=>
            _falling.ContainsKey(new FallKey(cell,material)) || AvailableInitialMotionSlots>0;
        internal bool CanPrepareInitialMotion(Vector3Int cell,MaterialId material,out bool newSlot)
        {
            newSlot=!_falling.ContainsKey(new FallKey(cell,material));
            return _store.TryGetQuantity(cell,material,out uint quantity) && quantity>0;
        }
        /// <summary>
        /// 原数量已提交后的运动补充；addedQuantity只用于动量加权，0表示调用者替换整格初速度。
        /// 这不是GMU来源。事务入口已预检固定容量，不能在扣瓶后才发现字典没位置。
        /// </summary>
        internal void CommitPreparedInitialMotion(Vector3Int cell,MaterialId material,Vector3 velocity,uint addedQuantity)
        {
            var key=new FallKey(cell,material);bool existed=_falling.TryGetValue(key,out var state);
            _store.TryGetQuantity(cell,material,out uint total);
            uint prior=addedQuantity>0 && total>addedQuantity?total-addedQuantity:0;
            if(prior>0)
            {
                Vector3 old=existed?state.HorizontalVelocity+Vector3.down*state.Velocity:Vector3.zero;
                if(!existed && _ground.TryRead(cell,material,_tick,out var ground))old=ground.Velocity;
                velocity=(old*prior+velocity*addedQuantity)/total;
                // 新水从本格喷口位置进入，不能整份继承旧水的格内偏移；按相同GMU权重合并质心。
                state.Offset*=prior/(float)total;state.HorizontalOffset*=prior/(float)total;
            }
            else state=default;
            state.HorizontalVelocity=new Vector3(velocity.x,0,velocity.z);state.Velocity=-velocity.y;
            state.AdvancedTick=_tick-1;state.SampleTime=_sampleTime;state.SafeDistance=0;
            _falling[key]=state;_store.MarkMotionChanged(cell,material);_activity.WakeNeighbors(cell);
        }
        public bool TrySetInitialVelocity(Vector3Int cell,MaterialId material,Vector3 velocity,uint addedQuantity=0)
        {
            if(!float.IsFinite(velocity.x) || !float.IsFinite(velocity.y) || !float.IsFinite(velocity.z)
                || !float.IsFinite(velocity.sqrMagnitude) || velocity.sqrMagnitude>4096f
                || !CanPrepareInitialMotion(cell,material,out bool needed) || needed && AvailableInitialMotionSlots==0)return false;
            CommitPreparedInitialMotion(cell,material,velocity,addedQuantity);return true;
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
                    {
                        // 密度交换同时搬两种材料；两格与其中全部材料共同构成完整表现组。
                        _history?.RecordSolverTransfer(source, below);
                        _activity.WakeNeighbors(source);_activity.WakeNeighbors(below);
                    }
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
                    // Reserve既表示源格的保留量，也决定新湿格所需的最小团量。
                    // 旧“1/4格”seed在正式水参数下是112GMU，使30/100GMU小量水即使
                    // 降低Reserve仍完全不扩散。这里随材料Reserve下降，但至少跨过Deadband，
                    // 避免把同量水摊成大量无法继续流动的1GMU尾格；再受既有通量上限约束。
                    // 重力、坡面、已有湿格均衡与Stable吸收仍走各自原路径，Store继续成对扣加。
                    int seed = Mathf.Min(_sideRates[materialIndex],Mathf.Max(_sideDeadbands[materialIndex]+1,reserve));
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
            // 初速度可以向上；只有向下且下一格不接收时才进入原接地分支，不能把瓶口上扬直接钳成零。
            bool upward=existing && (state.Velocity<0 || state.Offset<0);
            if (!upward && !AirCellAccepts(source+Vector3Int.down,material,quantity)) return false;
            if (!existing && _falling.Count == _maximumFalling)
            {
                // 过载时保留数量并继续排队，不能扩容字典产生 GC 或退回空中横向摊开。
                _activity.Wake(source); return true;
            }
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
            int direction=offset<0?-1:1,free=0;
            for(int i=1;i<=8;i++)
            {if(!AirCellAccepts(source+Vector3Int.down*(i*direction),material,quantity))break;free++;}
            int wanted=Mathf.Min(8,Mathf.FloorToInt(Mathf.Abs(offset)));
            int cells=Mathf.Min(free,wanted)*direction;
            Vector3Int destination = source + Vector3Int.down * cells;
            bool diagonalBlocked=ClampAirDiagonal(source,material,quantity,ref state,cells);
            AdvanceAirAxis(source,ref destination,material,quantity,ref state,0,cells);
            AdvanceAirAxis(source,ref destination,material,quantity,ref state,2,cells);
            if(diagonalBlocked)state.HorizontalVelocity=Vector3.zero;
            bool moved=destination!=source;
            if (moved && !_store.TryTransfer(source, destination, material, quantity))
            { _activity.Wake(source); return true; }
            if (moved)
            {
                _history?.RecordSolverTransfer(source, destination);
                _falling.Remove(key); _activity.WakeNeighbors(source);
            }
            // 已走完全部净空后，格内余量也不能继续向障碍方向偏移；否则未满一格的上扬会先穿进天花板。
            bool blocked=free<=wanted;
            state.Offset=blocked?0:Mathf.Clamp(offset-cells,-.9999f,.9999f);
            state.Velocity=blocked?0:velocity;
            state.AdvancedTick = _tick;
            state.SampleTime = _sampleTime;
            // Shader 只能前推已验证的空间，不能让插值视觉穿过地板或湖面。
            // 横向跨列后必须重查目标列净空，旧列的安全距离不能作为新列的视觉预测依据。
            int remaining=0,predictionDirection=state.Velocity<0?-1:1;
            for(int i=1;i<=8;i++)
            {var next=destination+Vector3Int.down*(i*predictionDirection);if(_store.FreeCapacity(next)<quantity
                || (_stable!=null && (_stable.OwnsMaterial(next,material) || _stable.HasForeignOccupancy(next,material))))break;remaining++;}
            state.SafeDistance = Mathf.Max(0f, (remaining - Mathf.Abs(state.Offset)) * _cellSize);
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
            state.HorizontalOffset[axis]+=speed*_deltaTime/_cellSize;
            int shifts=Mathf.Min(8,Mathf.FloorToInt(Mathf.Abs(state.HorizontalOffset[axis])));
            // 8m/s在0.1s内是3.2个0.25m格。旧实现只搬1格，Offset会越积越大。
            // 每轴最多8步，每步覆盖起飞列到落点列的竖直区间；固定上界防止高速输入造成无界遍历。
            for(int step=0;step<Mathf.Max(1,shifts);step++)
            {
                Vector3Int horizontalOrigin=destination-Vector3Int.down*verticalCells;
                bool clear=true;int sign=verticalCells<0?-1:1;
                for(int dy=0;dy<=Mathf.Abs(verticalCells);dy++)
                    if(!AirCellAccepts(horizontalOrigin+Vector3Int.down*(dy*sign)+direction,material,quantity)){clear=false;break;}
                if(!clear){state.HorizontalVelocity[axis]=0;state.HorizontalOffset[axis]=0;return;}
                if(shifts==0)return;
                destination+=direction;state.HorizontalOffset[axis]-=speed<0?-1:1;
            }
            state.HorizontalOffset[axis]=Mathf.Clamp(state.HorizontalOffset[axis],-.9999f,.9999f);
        }

        private bool AirCellAccepts(Vector3Int cell,MaterialId material,uint quantity)=>_store.FreeCapacity(cell)>=quantity
            && (_stable==null || !_stable.OwnsMaterial(cell,material) && !_stable.HasForeignOccupancy(cell,material));

        private bool ClampAirDiagonal(Vector3Int source,MaterialId material,uint quantity,ref FallState state,int verticalCells)
        {
            Vector3 desired=state.HorizontalOffset+state.HorizontalVelocity*(_deltaTime/_cellSize);
            int x=Mathf.Clamp((int)desired.x,-8,8),z=Mathf.Clamp((int)desired.z,-8,8);
            if(x==0 || z==0)return false;
            int steps=Mathf.Max(Mathf.Abs(x),Mathf.Abs(z));
            // 先检验真实对角经过的列，随后再用原轴向走廊检查墙角；最多8步，每步最多3列×9格。
            // 只检查L形两条边会错过正好位于对角上的薄墙，不能以终点空闲代替连续路径合法。
            for(int i=1;i<=steps;i++)
            {
                int dx=Mathf.FloorToInt(Mathf.Abs(x)*i/(float)steps)*(x<0?-1:1);
                int dz=Mathf.FloorToInt(Mathf.Abs(z)*i/(float)steps)*(z<0?-1:1);
                int previousX=Mathf.FloorToInt(Mathf.Abs(x)*(i-1)/(float)steps)*(x<0?-1:1);
                int previousZ=Mathf.FloorToInt(Mathf.Abs(z)*(i-1)/(float)steps)*(z<0?-1:1);
                bool clear=AirColumnClear(source,dx,dz,verticalCells,material,quantity);
                // 对角同时越过两条格边时也检查两侧格，避免恰擦墙角的穿透；每次最多3列，仍有固定上界。
                if(clear && dx!=previousX && dz!=previousZ)
                    clear=AirColumnClear(source,previousX,dz,verticalCells,material,quantity)
                        && AirColumnClear(source,dx,previousZ,verticalCells,material,quantity);
                if(!clear)
                {
                    int allowedX=Mathf.FloorToInt(Mathf.Abs(x)*(i-1)/(float)steps)*(x<0?-1:1);
                    int allowedZ=Mathf.FloorToInt(Mathf.Abs(z)*(i-1)/(float)steps)*(z<0?-1:1);
                    state.HorizontalVelocity=new Vector3(allowedX-state.HorizontalOffset.x,0,allowedZ-state.HorizontalOffset.z)*(_cellSize/_deltaTime);
                    return true;
                }
            }
            return false;
        }
        private bool AirColumnClear(Vector3Int source,int dx,int dz,int verticalCells,MaterialId material,uint quantity)
        {
            for(int dy=0;dy<=Mathf.Abs(verticalCells);dy++)
                if(!AirCellAccepts(source+new Vector3Int(dx,dy*(verticalCells<0?1:-1),dz),material,quantity))return false;
            return true;
        }

        private bool Move(Vector3Int source, Vector3Int target, MaterialId material, int amount)
        {
            if (_stable != null && TryAbsorbStable(source, target, material, (uint)amount))
                return true;
            if (_stable != null && _stable.OwnsMaterial(target, material)) return false;
            if (_stable != null && !TryPrepareStableContact(target,material))
            {
                // 展开预算不足时保留源格并继续排队，不能穿进静水表面或遗失重试机会。
                _activity.Wake(source); return false;
            }
            if (!_store.TryTransfer(source, target, material, (uint)amount)) return false;
            _history?.RecordSolverTransfer(source, target);
            _activity.WakeNeighbors(source);
            _activity.WakeNeighbors(target);
            return true;
        }

        // Stable吸收和展开改变数量的存储Owner；其Store回调须按外部改写处理。
        // 两个包装只切换History资格，原Stable操作的参数、次序、返回值与回滚行为都保留。
        private bool TryAbsorbStable(Vector3Int source, Vector3Int target, MaterialId material, uint quantity)
        {
            bool previousScope = _history != null && _history.SetSolverCaptureScope(false);
            try { return _stable.TryAbsorb(source, target, material, quantity); }
            finally { _history?.SetSolverCaptureScope(previousScope); }
        }

        private bool TryPrepareStableContact(Vector3Int target, MaterialId material)
        {
            bool previousScope = _history != null && _history.SetSolverCaptureScope(false);
            try { return _stable.TryPrepareMaterialContact(target, material); }
            finally { _history?.SetSolverCaptureScope(previousScope); }
        }
    }
}
