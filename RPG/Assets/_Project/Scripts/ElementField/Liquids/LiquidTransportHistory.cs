using System;
using System.Collections.Generic;
using Game.Core;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public readonly struct LiquidCellPresentationSample
    {
        public readonly uint Quantity;
        public readonly Vector3 OffsetCells, VelocityWorld;
        public LiquidCellPresentationSample(uint quantity, Vector3 offset, Vector3 velocity)
        { Quantity = quantity; OffsetCells = offset; VelocityWorld = velocity; }
    }
    public interface ILiquidTransportReadOnly
    {
        uint TransportEpoch { get; }
        float TransportInterval { get; }
        bool TryGetPresentationSample(Vector3Int cell, MaterialId material, bool previous, out LiquidCellPresentationSample sample);
        int CopyTransportChangedCells(Vector3Int[] destination);
        float GetGameplayBaseHeight(Vector3Int cell);
        bool TryGetTerrainSupport(Vector3Int cell, out LiquidTerrainSupport support);
    }

    /// <summary>
    /// 在本Tick第一次改写一个格前冻结旧数量/姿态；记录范围只含活动改写，不扫描Resident湖底。
    /// Current按已提交源读取，稳定Patch由Rendering快照负责，不能把Stable GMU复制成活动粒子。
    /// </summary>
    public sealed class LiquidTransportHistory
    {
        private readonly Dictionary<Vector3Int, int> _indices;
        private readonly Vector3Int[] _cells;
        private readonly LiquidCellPresentationSample[] _previous;
        // 网络窗口从Solver开始时的已提交量起算；原单机Previous仍从本Tick第一次改写前起算。
        // 两种时间边界明确分离，既不把喷口注入误当丢量，也不把F前的旧水重新播放回来。
        private readonly LiquidCellPresentationSample[] _motionPrevious;
        // Solver守恒边界与画面接缝是两件事：瓶中已扣除的新量在Solver前已经存在，必须计入
        // _motionPrevious核验守恒；但上一显示窗口尚未含这批水，visual Previous只能用提交前的
        // 原Current。此数组只供只读sidecar输出，不回写Store、不修改原单机_previous。
        private readonly LiquidCellPresentationSample[] _visualPrevious;
        private sealed class BirthJournal
        {
            private readonly Dictionary<Vector3Int,int> _indices;
            private readonly LiquidCellPresentationSample[] _before;
            private readonly uint[] _added;
            private readonly bool[] _valid;
            private readonly int _capacity,_materials;
            private int _count;
            internal BirthJournal(int capacity,int materials)
            {
                _capacity=capacity;_materials=materials;_indices=new Dictionary<Vector3Int,int>(capacity);
                _before=new LiquidCellPresentationSample[capacity*materials];
                _added=new uint[capacity*materials];_valid=new bool[capacity*materials];
            }
            internal void Clear(){_indices.Clear();_count=0;}
            internal void Invalidate(Vector3Int cell)
            {
                if(!_indices.TryGetValue(cell,out int row))return;
                for(int m=0;m<_materials;m++)_valid[row*_materials+m]=false;
            }
            internal void Record(Vector3Int cell,int material,LiquidCellPresentationSample before,uint added)
            {
                if(added==0)return;
                if(!_indices.TryGetValue(cell,out int row))
                {
                    // 只读证据容量不足则不授予出生特例。未登记行照原baseline发送，接缝会安全
                    // snap，不丢GMU、不扩容，也不能把一次失败登记覆盖其它已经提交的行。
                    if(_count==_capacity)return;
                    row=_count++;_indices.Add(cell,row);
                    Array.Clear(_added,row*_materials,_materials);Array.Clear(_valid,row*_materials,_materials);
                }
                int index=row*_materials+material;
                if(_added[index]==0){_before[index]=before;_valid[index]=true;}
                else if((ulong)_before[index].Quantity+_added[index]!=before.Quantity)_valid[index]=false;
                if(uint.MaxValue-_added[index]<added){_valid[index]=false;return;}
                _added[index]+=added;
            }
            internal bool TryRead(Vector3Int cell,int material,uint baseline,out LiquidCellPresentationSample previous)
            {
                previous=default;if(!_indices.TryGetValue(cell,out int row))return false;
                int index=row*_materials+material;
                if(!_valid[index] || _added[index]==0 || (ulong)_before[index].Quantity+_added[index]!=baseline)return false;
                previous=_before[index];return true;
            }
        }
        // 库存提交可以在BeginTick之前发生；Pending注入则发生在BeginTick之后。
        // 两份固定Journal在唯一Solver baseline断点交换，使二者同样只作用于紧接的一轮Solver。
        // active在下一BeginTick释放，未被Solver触及的出生不会靠此接口获得运动资格或跨窗续租。
        private BirthJournal _pendingBirths,_activeBirths;
        private bool _knownBirthWrite;
        private Vector3Int _knownBirthCell;
        // 单机仍使用原始History。以下固定工作区只决定哪些端点可以发布为网络表现，
        // 不修改Store、Solver姿态或原始Previous样本，也不成为第二个数量Owner。
        private readonly LiquidCellPresentationSample[] _current;
        private readonly int[] _parents;
        private readonly bool[] _solverEligible, _solverTouched, _captureExcluded;
        private readonly uint[] _canonical;
        private readonly long[] _groupBalance;
        private readonly int[] _materialSlots = new int[256];
        private readonly int _materialCount;
        private readonly LiquidWorldRuntime _source;
        private int _count;
        private bool _solverCaptureScope, _motionOverflow, _motionBaselinePrepared;
        public uint Epoch { get; private set; }
        public uint OverflowCount { get; private set; }
        public LiquidTransportHistory(LiquidWorldRuntime source, int capacity, int materialCount)
        {
            _source = source; _materialCount = materialCount;
            _indices = new Dictionary<Vector3Int, int>(capacity); _cells = new Vector3Int[capacity];
            _previous = new LiquidCellPresentationSample[capacity * materialCount];
            _motionPrevious = new LiquidCellPresentationSample[capacity * materialCount];
            _visualPrevious = new LiquidCellPresentationSample[capacity * materialCount];
            _pendingBirths=new BirthJournal(capacity,materialCount);_activeBirths=new BirthJournal(capacity,materialCount);
            _current = new LiquidCellPresentationSample[capacity * materialCount];
            _canonical = new uint[capacity * materialCount];
            _groupBalance = new long[capacity * materialCount];
            _parents = new int[capacity]; _solverEligible = new bool[capacity]; _solverTouched = new bool[capacity]; _captureExcluded = new bool[capacity];
            for (int i = 0; i < _materialSlots.Length; i++) _materialSlots[i] = -1;
            for (int i = 0; i < materialCount; i++) _materialSlots[(byte)source.GetLiquidMaterial(i)] = i;
        }

        public void BeginTick()
        {
            _activeBirths.Clear();
            _indices.Clear(); _count = 0; _solverCaptureScope = false; _motionOverflow = false; _motionBaselinePrepared=false; Epoch++;
        }

        /// <summary>
        /// 只包住已知外部加量的Store.TryAdd，不能包住整笔事务/Stable交接或任意回调。
        /// scope仅保护既有出生证据免被本次已知Stage误判为未知改量，并不登记出生；只有原Owner
        /// Commit成功后的RecordCommittedBirth才登记。Undo始终在scope外，保守撤掉被碰过的旧证据。
        /// </summary>
        internal void BeginKnownBirthWrite(Vector3Int cell)
        {
            if(_knownBirthWrite || _solverCaptureScope)throw new System.InvalidOperationException("出生写入scope不能嵌套或在Solver中开启。");
            _knownBirthWrite=true;_knownBirthCell=cell;
        }
        internal void EndKnownBirthWrite(){_knownBirthWrite=false;}
        internal void RecordCommittedBirth(Vector3Int cell,MaterialId material,LiquidCellPresentationSample before,uint added)
        {
            int slot=_materialSlots[(byte)material];if(slot<0 || added==0)return;
            _pendingBirths.Record(cell,slot,before,added);
        }
        private LiquidCellPresentationSample VisualPrevious(Vector3Int cell,int material,LiquidCellPresentationSample baseline)
            =>_activeBirths.TryRead(cell,material,baseline.Quantity,out var before)?before:baseline;

        /// <summary>
        /// 只在Pending与Stable.Step结束、Solver尚未开始的唯一断点调用。此时外部数量已经归属World，
        /// 从该值起核对Solver组守恒，避免持续注入使整条搬运链永久失去运动资格。
        /// 不覆盖单机Previous，不扫描Resident湖底；只重读本Tick已触及的固定容量行。
        /// 同Tick重复调用不重新授予资格，Solver开始后的F/反应写入仍须使整组失效。
        /// </summary>
        internal void PrepareSolverMotionBaseline()
        {
            if(_motionBaselinePrepared || _solverCaptureScope)return;
            _motionBaselinePrepared=true;
            var spare=_activeBirths;_activeBirths=_pendingBirths;_pendingBirths=spare;_pendingBirths.Clear();
            for(int i=0;i<_count;i++)
            {
                _parents[i]=i;_solverEligible[i]=true;_solverTouched[i]=false;
                for(int m=0;m<_materialCount;m++)
                {
                    int offset=i*_materialCount+m;
                    _motionPrevious[offset]=_source.ReadCurrentPresentation(_cells[i],_source.GetLiquidMaterial(m));
                    _visualPrevious[offset]=VisualPrevious(_cells[i],m,_motionPrevious[offset]);
                }
            }
        }

        /// <summary>
        /// 只有真实Solver积分/成对搬运可以进入此scope。返回旧状态供try/finally恢复；
        /// Solver内部调用Stable吸收/展开时也须临时退出，函数位于Solver内不等于运动语义。
        /// </summary>
        public bool SetSolverCaptureScope(bool enabled)
        {
            bool previous = _solverCaptureScope; _solverCaptureScope = enabled; return previous;
        }

        public void CaptureBefore(Vector3Int cell)
        {
            // 包括F、反应、普通转换、Undo和未知直接写入。不能因最终数量碰巧恢复成相同值
            // 就重新授予出生身份；同格多笔真正Commit的加量则由明确known-add scope保留首份旧pose。
            if(!_solverCaptureScope && !(_knownBirthWrite && _knownBirthCell==cell))
            {_pendingBirths.Invalidate(cell);_activeBirths.Invalidate(cell);}
            if (_indices.TryGetValue(cell, out int existing))
            {
                // Solver窗口开始后的F、反应或Stable写入，使整条搬运链失去资格。
                // 仍保留原Previous，避免更改既有单机表现语义。
                if (!_solverCaptureScope) _solverEligible[FindRoot(existing)] = false;
                else _solverTouched[FindRoot(existing)]=true;
                return;
            }
            if (_count == _cells.Length) { OverflowCount++; _motionOverflow = true; return; }
            int index = _count++; _indices.Add(cell, index); _cells[index] = cell;
            _parents[index] = index; _solverEligible[index] = _solverCaptureScope; _solverTouched[index]=_solverCaptureScope;
            for (int m = 0; m < _source.MaterialCount; m++)
            {
                int offset=index*_source.MaterialCount+m;
                _previous[offset]=_motionPrevious[offset]=_source.ReadCurrentPresentation(cell,_source.GetLiquidMaterial(m));
                _visualPrevious[offset]=_motionBaselinePrepared?VisualPrevious(cell,m,_motionPrevious[offset]):_motionPrevious[offset];
            }
        }

        /// <summary>
        /// 必须在Store确认搬运成功后调用；两个端点已由Store的before回调冻结。
        /// Union包含本Tick先前的链条，跨Chunk也不能拆开；未跨格的积分保留单格组。
        /// </summary>
        public void RecordSolverTransfer(Vector3Int source, Vector3Int destination)
        {
            if (!_solverCaptureScope || !_indices.TryGetValue(source, out int a)
                || !_indices.TryGetValue(destination, out int b))
            {
                // 一端未进入History意味着不能证明组完整，宁可整份sidecar失败，也不能猜补旧量。
                _motionOverflow = true; return;
            }
            a = FindRoot(a); b = FindRoot(b);
            if (a == b) return;
            bool eligible = _solverEligible[a] && _solverEligible[b];
            bool touched = _solverTouched[a] || _solverTouched[b];
            if (a > b) { int swap = a; a = b; b = swap; }
            _parents[b] = a; _solverEligible[a] = eligible; _solverTouched[a]=touched;
        }

        private int FindRoot(int index)
        {
            int root = index;
            while (_parents[root] != root) root = _parents[root];
            while (_parents[index] != index)
            { int next = _parents[index]; _parents[index] = root; index = next; }
            return root;
        }

        /// <summary>
        /// 用本轮完整数量快照校验Dynamic端点，Stable混量不能伪装成相同Current。
        /// 按组、按材料再次检查前后GMU相等；漏接的新迁移路径会安全失去表现资格。
        /// 预检成功才写Rows，失败由调用方将Count清零，不发布半组，也不Fault数量世界。
        /// </summary>
        internal bool TryCaptureMotion(WorldLiquidMotionCaptureBuffer destination, WorldReplicaBuffer quantities)
        {
            if (_motionOverflow || _solverCaptureScope) return false;
            int values = _count * _materialCount;
            System.Array.Clear(_canonical, 0, values);
            System.Array.Clear(_groupBalance, 0, values);
            System.Array.Clear(_captureExcluded, 0, _count);
            for (int i = 0; i < _count; i++)
            {
                int root = FindRoot(i);
                if (!_solverEligible[root] || !_solverTouched[root]) _captureExcluded[root] = true;
                for (int m = 0; m < _materialCount; m++)
                {
                    int offset = i * _materialCount + m;
                    var current = _source.ReadCurrentPresentation(_cells[i], _source.GetLiquidMaterial(m));
                    _current[offset] = current;
                    _groupBalance[root * _materialCount + m] += (long)_motionPrevious[offset].Quantity - current.Quantity;
                }
            }
            // 一次扫描数量snapshot，再对History作索引查询；不对每一运动行重复扫描整片湖泊。
            for (int i = 0; i < quantities.Header.CellCount; i++)
            {
                var sample = quantities.Cells[i];
                if (sample.Domain != WorldMaterialDomain.Liquid || !_indices.TryGetValue(sample.Cell, out int index)) continue;
                int material = _materialSlots[sample.MaterialId];
                if (material < 0) { _captureExcluded[FindRoot(index)] = true; continue; }
                int offset = index * _materialCount + material;
                if (uint.MaxValue - _canonical[offset] < sample.Quantity) return false;
                _canonical[offset] += sample.Quantity;
            }
            for (int i = 0; i < _count; i++)
            {
                int root = FindRoot(i);
                for (int m = 0; m < _materialCount; m++)
                    if (_canonical[i * _materialCount + m] != _current[i * _materialCount + m].Quantity
                        || _groupBalance[root * _materialCount + m] != 0)
                        _captureExcluded[root] = true;
            }
            int count = 0;
            for (int i = 0; i < _count; i++)
            {
                if (_captureExcluded[FindRoot(i)]) continue;
                for (int m = 0; m < _materialCount; m++)
                    if (_visualPrevious[i * _materialCount + m].Quantity != 0 || _current[i * _materialCount + m].Quantity != 0)
                        if (++count > destination.Rows.Length || count > destination.GroupIds.Length) return false;
            }
            int write = 0;
            for (int i = 0; i < _count; i++)
            {
                int root = FindRoot(i);
                if (_captureExcluded[root]) continue;
                for (int m = 0; m < _materialCount; m++)
                {
                    var previous = _visualPrevious[i * _materialCount + m]; var current = _current[i * _materialCount + m];
                    if (previous.Quantity == 0 && current.Quantity == 0) continue;
                    destination.Rows[write] = new WorldLiquidMotionSample(_cells[i], (byte)_source.GetLiquidMaterial(m),
                        previous.Quantity, current.Quantity, previous.OffsetCells, previous.VelocityWorld,
                        current.OffsetCells, current.VelocityWorld);
                    destination.GroupIds[write++] = root;
                }
            }
            destination.Count = write; return true;
        }
        public LiquidCellPresentationSample ReadPrevious(Vector3Int cell, MaterialId material)
        {
            if (_indices.TryGetValue(cell, out int index))
                for (int m = 0; m < _source.MaterialCount; m++)
                    if (_source.GetLiquidMaterial(m) == material) return _previous[index * _source.MaterialCount + m];
            return _source.ReadCurrentPresentation(cell, material);
        }
        public bool CopyFrozenRow(Vector3Int cell,LiquidCellPresentationSample[] destination)
        {
            if(!_indices.TryGetValue(cell,out int index))return false;
            System.Array.Copy(_previous,index*_source.MaterialCount,destination,0,_source.MaterialCount);return true;
        }
        public int CopyChangedCells(Vector3Int[] destination)
        { int n = Mathf.Min(_count, destination.Length); System.Array.Copy(_cells, destination, n); return n; }
    }
}
