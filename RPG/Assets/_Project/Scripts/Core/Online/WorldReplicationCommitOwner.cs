using System;
using System.Collections.Generic;

namespace Game.Core
{
    public enum WorldCommitOwnerFailure : byte { None, InvalidInput, Capacity, InspectionBudget, Occupancy, Journal, BaselineRequired, DestinationCapacity }

    /// <summary>
    /// 世界唯一复制提交Owner。调用方在一次材料提交边界捕获Source后调用一次，不按连接扫描世界。
    /// 先找原生Chunk，再在有界小块内规范化Stable/Dynamic；Geometry和Pending同占据一起参与差异。
    /// 当前目录按地址增长；每块只为真实正文分配缓冲，短历史仅保留证明 Delta 连续性所需的头。
    /// Journal成功之前不发布任何payload；失败需装配层
    /// 暂停复制并重试/重建，不允许越过失败提交继续假装客户端拥有完整历史。所有访问限所属主线程。
    /// Baseline 与已证明连续的 Delta 都从当前冻结正文生成完整替换，Peer 单独持有在途副本。
    /// </summary>
    public sealed class WorldReplicationCommitOwner
    {
        private readonly WorldChunkCommitJournal _journal;
        private WorldChunkKey[] _keys = Array.Empty<WorldChunkKey>(), _candidateKeys = Array.Empty<WorldChunkKey>();
        private WorldChunkPayloadHeader[] _headers = Array.Empty<WorldChunkPayloadHeader>();
        private readonly WorldChunkPayloadHeader[] _historyHeaders;
        // 环形窗口只从最旧位置淘汰，Journal 保证同一 key/generation 的 Revision 每次严格加一。
        // 若 base -> base+1 仍在窗口，其后的同代记录必然全部还在；按这条首链查询可免扫其它块的历史。
        private readonly HashSet<(WorldChunkKey Key,ulong Generation,ulong BaseRevision)> _historyLinks;
        private uint[][] _published = Array.Empty<uint[]>(), _candidate = Array.Empty<uint[]>();
        private int[] _counts = Array.Empty<int>(), _changedSlots = Array.Empty<int>(), _scheduled = Array.Empty<int>();
        private bool[] _active = Array.Empty<bool>();
        private uint[] _scheduledBatch = Array.Empty<uint>();
        private uint _batch;
        private WorldChunkChange[] _changes = Array.Empty<WorldChunkChange>();
        private WorldChunkChange[] _committedChanges = Array.Empty<WorldChunkChange>();
        private struct RowList { internal int First, Last, Count; }
        private RowList[] _cellRows = Array.Empty<RowList>(), _pendingRows = Array.Empty<RowList>(), _spanRows = Array.Empty<RowList>();
        private int[] _nextCell = Array.Empty<int>(), _nextPending = Array.Empty<int>(), _nextSpan = Array.Empty<int>();
        private readonly Dictionary<WorldChunkKey,int> _indices, _newIndices;
        private readonly HashSet<ulong> _pendingIds;
        private readonly Dictionary<ulong,WorldChunkKey> _pendingOwners;
        private readonly int _chunkLimit, _wordLimit;
        private int _scheduledCount;
        private readonly WorldMaterialCellSample[] _cellScratch;
        private readonly WorldMaterialOccupancySnapshot _occupancy;
        private readonly WorldGridGeometry[] _geometries=new WorldGridGeometry[5];
        private readonly bool[] _hasGeometry=new bool[5];
        private WorldGridGeometry _committedCellularGeometry;
        private readonly WorldGridGeometry[] _lastGeometry=new WorldGridGeometry[5];
        private readonly bool[] _previousDomains=new bool[5];
        private readonly HashSet<WorldChunkKey> _requestedKeys;
        private readonly int _inspectionBudget, _pendingCapacity;
        private int _count, _historyHead, _historyCount, _budget;
        private long _lastTick=-1;
        public ulong WorldEpoch => _journal.WorldEpoch;
        public ulong CommitSequence => _journal.CommitSequence;
        /// <summary>最近一次成功材料捕获的WorldTick；空提交不推进CommitSequence，仍有独立采样时间。</summary>
        public long LastCapturedWorldTick => _lastTick;
        // 只用于最近一次已提交的同一权威源已逐项确认没有数量、队列和地形变化的情况。
        // 更新采样时间，不生成 Journal 项或新 Revision，迟绑定 Baseline 仍读原冻结正文。
        public bool TryAdvanceUnchangedCapture(long worldTick)
        {
            if (_lastTick < 0 || worldTick < _lastTick) return Reject(WorldCommitOwnerFailure.InvalidInput);
            _lastTick = worldTick; LastChangeCount=0; LastFailure = WorldCommitOwnerFailure.None; return true;
        }
        public int ChunkCount => _count;
        /// <summary>仅最近成功 Capture 的逻辑变化，失败重试不覆盖此视图；地址不依赖退休时可搬移的目录下标。</summary>
        public int LastChangeCount { get; private set; }
        public WorldChunkChange GetLastChange(int index) => (uint)index<(uint)LastChangeCount ?
            _committedChanges[index] : throw new ArgumentOutOfRangeException(nameof(index));
        public int InactiveChunkCount { get; private set; }
        public int IndexOf(WorldChunkKey key) => _indices.TryGetValue(key,out int index) ? index : -1;
        /// <summary>真实进入payload历史ring的变更记录数；Capture/空提交/Tick均不能冒充日志覆盖。</summary>
        public ulong HistoryRecordsWritten {get;private set;}
        public int HistoryCapacity=>_historyHeaders.Length;
        /// <summary>已分配的当前/候选 uint 元素数，含复用余量；独立于传输页上限，供容量回归核实。</summary>
        public long PayloadCapacityWords { get; private set; }
        public ulong RetiredGenerationFloor=>_journal.RetiredGenerationFloor;
        /// <summary>只读当前仍可寻址的完整Header，不分配payload，也不修改最近Capture失败证据。</summary>
        public bool TryGetCurrentHeader(WorldChunkKey key,out WorldChunkPayloadHeader header)
        {
            if(_indices.TryGetValue(key,out int i)){header=_headers[i];return true;}
            header=default;return false;
        }
        public WorldCommitOwnerFailure LastFailure {get;private set;}

        public WorldReplicationCommitOwner(ulong epoch,int chunkCapacity,int cellCapacity,int pendingCapacity,int historyCapacity,int maxWordsPerChunk,int inspectionBudget)
        {
            if(chunkCapacity<=0 || cellCapacity<=0 || pendingCapacity<0 || historyCapacity<=0 || maxWordsPerChunk<11 || inspectionBudget<=0)throw new ArgumentOutOfRangeException(nameof(chunkCapacity));
            _journal=new WorldChunkCommitJournal(epoch,chunkCapacity,Math.Max(historyCapacity,chunkCapacity),inspectionBudget);
            _chunkLimit=chunkCapacity;_wordLimit=maxWordsPerChunk;
            int initial=Math.Min(32,chunkCapacity);
            _indices=new Dictionary<WorldChunkKey,int>(initial);_newIndices=new Dictionary<WorldChunkKey,int>(initial);
            _pendingIds=new HashSet<ulong>(pendingCapacity);_pendingOwners=new Dictionary<ulong,WorldChunkKey>(pendingCapacity);
            _historyHeaders=new WorldChunkPayloadHeader[historyCapacity];
            _historyLinks=new HashSet<(WorldChunkKey,ulong,ulong)>(historyCapacity);
            EnsureSlots(initial);
            _cellScratch=new WorldMaterialCellSample[cellCapacity];_occupancy=new WorldMaterialOccupancySnapshot(cellCapacity,inspectionBudget);
            _inspectionBudget=inspectionBudget;_pendingCapacity=pendingCapacity;
            _requestedKeys=new HashSet<WorldChunkKey>(initial);
        }
        private void EnsureSlots(int needed)
        {
            int old=_keys.Length;if(needed<=old)return;
            int size=(int)Math.Min(_chunkLimit,Math.Max((long)needed,2L*old));
            Array.Resize(ref _keys,size);Array.Resize(ref _candidateKeys,size);Array.Resize(ref _headers,size);
            Array.Resize(ref _published,size);Array.Resize(ref _candidate,size);Array.Resize(ref _counts,size);
            Array.Resize(ref _scheduled,size);Array.Resize(ref _scheduledBatch,size);Array.Resize(ref _changedSlots,size);
            Array.Resize(ref _active,size);Array.Resize(ref _changes,size);Array.Resize(ref _committedChanges,size);
            Array.Resize(ref _cellRows,size);Array.Resize(ref _pendingRows,size);Array.Resize(ref _spanRows,size);
            for(int i=old;i<size;i++)_published[i]=_candidate[i]=Array.Empty<uint>();
        }
        // 在本次实际触达的块内建立按原枚举顺序的索引链。一份全量恢复也只分类一次输入，
        // 避免每个持久块重扫全图历史挖洞、水格及 FIFO；零作为链尾，数组中存 index+1。
        private static void AddRow(ref RowList rows,int index,int[] next)
        {
            next[index]=0;
            if(rows.Last!=0)next[rows.Last-1]=index+1;else rows.First=index+1;
            rows.Last=index+1;rows.Count++;
        }
        private static void EnsureRows(ref int[] rows,int count)
        {if(rows.Length<count)Array.Resize(ref rows,Math.Max(count,rows.Length*2));}
        private void Schedule(int slot)
        {
            if(_scheduledBatch[slot]==_batch)return;
            _scheduledBatch[slot]=_batch;_scheduled[_scheduledCount++]=slot;_counts[slot]=0;_active[slot]=false;
            _cellRows[slot]=_pendingRows[slot]=_spanRows[slot]=default;
        }
        public WorldChunkPayloadHeader GetChunkHeader(int index)
        {if(index<0 || index>=_count)throw new ArgumentOutOfRangeException(nameof(index));return _headers[index];}

        /// <summary>
        /// 可信Coordinator已撤销此Key全部订阅/冻结引用并将Leave排入可靠通道后，显式退休非resident槽。
        /// Capture不会自动调用此方法，也不会以metadata紧张为由偷删tombstone或真实材料数量。
        /// 成功时以旧末槽压缩index；movedFromIndex返回旧末槽索引，无搬动时为-1，供Coordinator同步搬动
        /// per-Peer订阅/ACK/游标。它们必须在同一主线程边界完成，不能跨帧观察一半的新索引。
        /// Journal完成非resident、Generation溢出和扫描预算预检后，下面只交换预分配缓冲与值；
        /// 失败不改公开payload/序号/历史，成功也不伪造一次材料Commit或推进采样Tick。
        /// </summary>
        public bool TryRetireInactiveChunk(int index, out int movedFromIndex)
        {
            movedFromIndex = -1;
            if ((uint)index >= (uint)_count || _headers[index].IsResident)
                return Reject(WorldCommitOwnerFailure.InvalidInput);
            if (!_journal.TryRetireInactiveChunk(_keys[index])) return Reject(WorldCommitOwnerFailure.Journal);

            _indices.Remove(_keys[index]);
            int last = _count - 1;
            if (index != last)
            {
                _keys[index] = _keys[last]; _headers[index] = _headers[last];
                _indices[_keys[index]]=index;
                _candidateKeys[index] = _candidateKeys[last];
                _counts[index] = _counts[last]; _active[index] = _active[last];
                // uint[]归池而不清零：Header/有效前缀决定可读范围，下一次Capture会覆盖候选。
                // 交换两个数组引用能保留末槽payload，不分配/复制最大Chunk缓冲。
                uint[] published = _published[index]; _published[index] = _published[last]; _published[last] = published;
                uint[] candidate = _candidate[index]; _candidate[index] = _candidate[last]; _candidate[last] = candidate;
                movedFromIndex = last;
            }
            _keys[last] = _candidateKeys[last] = default; _headers[last] = default;
            PayloadCapacityWords-=(long)_published[last].Length+_candidate[last].Length;
            _published[last]=_candidate[last]=Array.Empty<uint>();
            _counts[last] = 0; _active[last] = false;
            _count = last; InactiveChunkCount--; LastFailure = WorldCommitOwnerFailure.None; return true;
        }

        /// <summary>Source调用已结束且库存事务栅栏释放后执行；失败保持全部公开数据与版本不变。</summary>
        public bool TryCaptureCommit(WorldReplicaBuffer source) => CaptureCommit(source, -1);

        /// <summary>每个列出的 Chunk 是完整替换，点名后没有样本表示清除。未点名的冻块与其 Revision 原样保留。</summary>
        public bool TryCaptureChangedChunks(WorldReplicaBuffer source,int changedChunkCount)
            => changedChunkCount>=0 && CaptureCommit(source,changedChunkCount);

        private bool CaptureCommit(WorldReplicaBuffer source,int changedChunkCount)
        {
            _budget=_inspectionBudget;
            if(source==null)return Reject(WorldCommitOwnerFailure.InvalidInput);
            var h=source.Header;
            bool partial=changedChunkCount>=0;
            if(partial && (_lastTick<0 || changedChunkCount>source.ChangedChunks.Length || changedChunkCount>_chunkLimit))
                return Reject(WorldCommitOwnerFailure.InvalidInput);
            _requestedKeys.Clear();_newIndices.Clear();_pendingIds.Clear();
            if(_batch==uint.MaxValue){Array.Clear(_scheduledBatch,0,_scheduledBatch.Length);_batch=0;}
            _batch++;
            _scheduledCount=0;
            for(int i=0;i<changedChunkCount;i++)
            {
                var key=source.ChangedChunks[i];
                if((byte)key.Domain>(byte)WorldMaterialDomain.Cellular || !_requestedKeys.Add(key))return Reject(WorldCommitOwnerFailure.InvalidInput);
                if(_indices.TryGetValue(key,out int existing))Schedule(existing);
            }
            if(h.WorldTick<0 || h.WorldTick<_lastTick || h.CellCount<0 || h.CellCount>source.Cells.Length || h.PendingCount<0 || h.PendingCount>source.Pending.Length || h.PendingCount>_pendingCapacity || h.GeometryCount<0 || h.GeometryCount>_geometries.Length || h.SpanCount<0 || h.SpanCount>source.Spans.Length)return Reject(WorldCommitOwnerFailure.InvalidInput);
            if(!Spend((partial?changedChunkCount:_count)+5L+h.CellCount+h.PendingCount+h.GeometryCount+h.SpanCount))return false;
            Array.Clear(_hasGeometry,0,_hasGeometry.Length);
            int nextCount=_count;
            if(!partial)for(int i=0;i<_count;i++)Schedule(i);
            EnsureRows(ref _nextCell,h.CellCount);EnsureRows(ref _nextPending,h.PendingCount);EnsureRows(ref _nextSpan,h.SpanCount);
            for(int i=0;i<h.GeometryCount;i++)
            {
                var g=source.Geometries[i];if(!WorldChunkPayloadCodec.ValidGeometry(g) || _hasGeometry[(int)g.Domain])return Reject(WorldCommitOwnerFailure.InvalidInput);
                _geometries[(int)g.Domain]=g;_hasGeometry[(int)g.Domain]=true;
                var anchor=new WorldChunkKey(g.Domain,0,0,0);
                if((!partial || _requestedKeys.Contains(anchor)) && !Activate(anchor,ref nextCount,out _))return false;
            }
            if(partial)
            {
                for(int i=0;i<_hasGeometry.Length;i++)
                    if(_hasGeometry[i]!=_previousDomains[i] || _hasGeometry[i] &&
                        !WorldChunkPayloadCodec.SameCellularGeometry(_geometries[i],_lastGeometry[i]))return Reject(WorldCommitOwnerFailure.InvalidInput);
                for(int i=0;i<changedChunkCount;i++)
                    if(!_hasGeometry[(int)source.ChangedChunks[i].Domain])return Reject(WorldCommitOwnerFailure.InvalidInput);
            }
            for(int i=0;i<h.CellCount;i++)
            {
                var c=source.Cells[i];if(c.Quantity==0)continue;
                if((byte)c.Domain>(byte)WorldMaterialDomain.Cellular || !_hasGeometry[(int)c.Domain])return Reject(WorldCommitOwnerFailure.InvalidInput);
                var key=WorldChunkPayloadCodec.CellKey(c.Domain,c.Cell,_geometries[(int)c.Domain].ChunkSize);
                if(partial && !_requestedKeys.Contains(key))return Reject(WorldCommitOwnerFailure.InvalidInput);
                if(!Activate(key,ref nextCount,out int slot))return false;
                AddRow(ref _cellRows[slot],i,_nextCell);
            }
            int cellularIndex=(int)WorldMaterialDomain.Cellular;
            if(_lastTick>=0 && (_hasGeometry[cellularIndex] != (_committedCellularGeometry.BaseVersion!=0) ||
                _hasGeometry[cellularIndex] && !WorldChunkPayloadCodec.SameCellularGeometry(_geometries[cellularIndex],_committedCellularGeometry)))
                return Reject(WorldCommitOwnerFailure.InvalidInput);
            for(int i=0;i<h.SpanCount;i++)
            {
                if(!_hasGeometry[cellularIndex])return Reject(WorldCommitOwnerFailure.InvalidInput);
                var key=WorldChunkPayloadCodec.CellKey(WorldMaterialDomain.Cellular,source.Spans[i].Cells.position,_geometries[cellularIndex].ChunkSize);
                if(partial && !_requestedKeys.Contains(key))return Reject(WorldCommitOwnerFailure.InvalidInput);
                if(!Activate(key,ref nextCount,out int slot))return false;
                AddRow(ref _spanRows[slot],i,_nextSpan);
            }
            for(int i=0;i<h.PendingCount;i++)
            {
                var p=source.Pending[i];if(!WorldChunkPayloadCodec.ValidPending(p) || !_hasGeometry[(int)p.Domain] || !WorldChunkPayloadCodec.TryPositionKey(_geometries[(int)p.Domain],p.WorldPosition,out var key))return Reject(WorldCommitOwnerFailure.InvalidInput);
                // FIFO身份在同一域全局唯一，不能因为重复落在不同Chunk就绕开验证。
                ulong id=PendingId(p.Domain,(uint)p.QueueIndex);
                if(!_pendingIds.Add(id) || partial && (!_requestedKeys.Contains(key) ||
                    _pendingOwners.TryGetValue(id,out var previousKey) && !_requestedKeys.Contains(previousKey)))
                    return Reject(WorldCommitOwnerFailure.InvalidInput);
                if(!Activate(key,ref nextCount,out int slot))return false;
                AddRow(ref _pendingRows[slot],i,_nextPending);
            }
            int changes=0;long pendingAfter=_pendingOwners.Count+h.PendingCount;
            for(int visit=0;visit<_scheduledCount;visit++)
            {
                int slot=_scheduled[visit];
                if(slot<_count && _headers[slot].IsResident)pendingAfter-=_published[slot][2];
                if(_active[slot] && !BuildChunk(source,slot))return false;
                bool old=slot<_count && _headers[slot].IsResident, now=_active[slot];
                if(!old && !now)continue;
                bool changed=old!=now;
                if(!changed)
                {
                    changed=_headers[slot].WordCount!=_counts[slot];
                    for(int w=0;!changed && w<_counts[slot];w++){if(!Spend(1))return false;changed=_published[slot][w]!=_candidate[slot][w];}
                }
                if(!changed)continue;
                _changes[changes]=new WorldChunkChange(_candidateKeys[slot],now?(old?WorldChunkChangeKind.Update:WorldChunkChangeKind.Create):WorldChunkChangeKind.Remove);
                _changedSlots[changes++]=slot;
            }
            if(pendingAfter>_pendingCapacity)return Reject(WorldCommitOwnerFailure.Capacity);
            // 为下面无失败发布预留全部工作量。Journal内部也独立限量；其成功之后没有外部回调。
            long publishWork=_scheduledCount;
            for(int c=0;c<changes;c++)publishWork+=(long)_counts[_changedSlots[c]]*3;
            if(!Spend(publishWork))return false;
            if(!_journal.TryCommit(_changes,changes))return Reject(WorldCommitOwnerFailure.Journal);
            // 先从所有替换块撤下旧 FIFO 身份，再登记新位置。排队投放跨块移动时顺序无关，
            // 同时保住未触达块的既有身份，避免每次火焰重试扫远处成千个洞。
            for(int c=0;c<changes;c++)
            {
                int slot=_changedSlots[c];
                if(slot>=_count || !_headers[slot].IsResident)continue;
                uint[] oldWords=_published[slot];
                int start=WorldChunkPayloadCodec.PendingOffset(oldWords),stride=WorldChunkPayloadCodec.PendingWordCount(oldWords);
                for(int p=0;p<oldWords[2];p++,start+=stride)_pendingOwners.Remove(PendingId(_keys[slot].Domain,oldWords[start+1]));
            }
            for(int c=0;c<changes;c++)
            {
                int slot=_changedSlots[c];_keys[slot]=_candidateKeys[slot];
                if(slot>=_count)_indices.Add(_keys[slot],slot);
                else if(_headers[slot].IsResident!=_active[slot])InactiveChunkCount+=_active[slot]?-1:1;
                for(int row=_pendingRows[slot].First;row!=0;row=_nextPending[row-1])
                {
                    var pending=source.Pending[row-1];
                    _pendingOwners.Add(PendingId(pending.Domain,(uint)pending.QueueIndex),_keys[slot]);
                }
                _journal.TryGetState(_keys[slot],out var state);
                ulong baseRevision=_changes[c].Kind==WorldChunkChangeKind.Create?0:state.Revision-1;
                uint[] previous=_published[slot];_published[slot]=_candidate[slot];_candidate[slot]=previous;
                int words=_active[slot]?_counts[slot]:0;
                var header=new WorldChunkPayloadHeader(WorldEpoch,CommitSequence,_keys[slot],state.Generation,baseRevision,state.Revision,h.WorldTick,words,WorldChunkPayloadCodec.ComputeChecksum(_published[slot],words),_active[slot]);
                _headers[slot]=header;AppendHistory(header);
            }
            _count=nextCount;_lastTick=h.WorldTick;LastFailure=WorldCommitOwnerFailure.None;
            Array.Copy(_changes,_committedChanges,changes);LastChangeCount=changes;
            if(_hasGeometry[cellularIndex])_committedCellularGeometry=_geometries[cellularIndex];
            if(!partial)
            {
                Array.Copy(_geometries,_lastGeometry,_lastGeometry.Length);
                Array.Copy(_hasGeometry,_previousDomains,_previousDomains.Length);
            }
            // 唯一Owner的真实发布边界：Journal、payload与历史均已成功，才观察本次变更。
            // 不按连接再捕获，不扫描/复制payload；无变化提交没有事件。Header.ServerTick是材料WorldTick，
            // Core没有Gameplay 30Hz上下文，保留事件GameplayTick=0，不能把两个时钟混成一个。
            var timing=WorldReplicationTimingTrace.Active;
            if(timing!=null)
                for(int c=0;c<changes;c++)
                    timing.Record(new WorldReplicationTimingEvent{Stage=WorldReplicationTimingStage.Committed,
                        Header=_headers[_changedSlots[c]],ConnectionId=-1,PageIndex=-1});
            return true;
        }
        private static ulong PendingId(WorldMaterialDomain domain,uint queueIndex) => ((ulong)domain<<32)|queueIndex;
        private bool Activate(WorldChunkKey key,ref int count,out int slot)
        {
            slot=-1;if(!Spend(1))return false;
            if(!_indices.TryGetValue(key,out slot) && !_newIndices.TryGetValue(key,out slot))
            {
                if(count==_chunkLimit)return Reject(WorldCommitOwnerFailure.Capacity);
                EnsureSlots(count+1);slot=count++;_candidateKeys[slot]=key;_newIndices.Add(key,slot);
            }
            Schedule(slot);_active[slot]=true;return true;
        }
        private bool BuildChunk(WorldReplicaBuffer source,int slot)
        {
            var key=_candidateKeys[slot];var geometry=_geometries[(int)key.Domain];int cells=0;
            if(!Spend((long)_cellRows[slot].Count+_pendingRows[slot].Count+11))return false;
            for(int row=_cellRows[slot].First;row!=0;row=_nextCell[row-1])
            {
                var c=source.Cells[row-1];
                if(cells==_cellScratch.Length)return Reject(WorldCommitOwnerFailure.Capacity);_cellScratch[cells++]=c;
            }
            // HeapSort+线性归并的保守上界与规范化器采用同一检查单位：比较/交换各一次。
            // 用long进行计量，不能再按旧插入排序n²扣预算，否则正常大Chunk会被人为拒绝。
            int levels=0;for(long n=1;n<Math.Max(2,cells);n*=2)levels++;
            if(!Spend(6L*cells*levels+4L*cells))return false;
            if(!_occupancy.TryCapture(_cellScratch,cells))return Reject(WorldCommitOwnerFailure.Occupancy);
            bool cellular=key.Domain==WorldMaterialDomain.Cellular;
            int prefix=cellular?WorldChunkPayloadCodec.CellularPrefixWords:11;
            long residentWords=prefix+(long)_occupancy.Count*7;
            int stride=cellular?WorldChunkPayloadCodec.CellularPendingWords:WorldChunkPayloadCodec.PendingWords;
            long required=residentWords+(long)stride*_pendingRows[slot].Count+(long)WorldChunkPayloadCodec.SpanWords*_spanRows[slot].Count;
            if(required>_wordLimit)return Reject(WorldCommitOwnerFailure.Capacity);
            uint[] words=_candidate[slot];
            if(required>words.Length)
            {
                int capacity=(int)Math.Min(_wordLimit,Math.Max(required,Math.Max(16L,words.Length*2L)));
                PayloadCapacityWords+=capacity-words.Length;words=_candidate[slot]=new uint[capacity];
            }
            words[0]=cellular?WorldChunkPayloadCodec.CellularMagic:WorldChunkPayloadCodec.Magic;words[1]=(uint)_occupancy.Count;words[2]=0;words[3]=1;
            WorldChunkPayloadCodec.WriteGeometry(words,4,geometry);int offset=prefix;
            if(cellular)words[14]=0;
            if(!Spend((long)_occupancy.Count*7))return false;
            for(int i=0;i<_occupancy.Count;i++,offset+=7)WorldChunkPayloadCodec.WriteCell(words,offset,_occupancy.GetCell(i));
            int pending=0;
            // Source的FIFO顺序应按QueueIndex递增；主动排序局部Pending让枚举实现细节不产生假差异。
            for(int row=_pendingRows[slot].First;row!=0;row=_nextPending[row-1])
            {
                var p=source.Pending[row-1];
                int position=offset;
                while(position>residentWords && words[position-stride+1]>(uint)p.QueueIndex)
                {if(!Spend(stride))return false;Array.Copy(words,position-stride,words,position,stride);position-=stride;}
                if(!Spend(stride))return false;WorldChunkPayloadCodec.WritePending(words,position,p);offset+=stride;pending++;
            }
            words[2]=(uint)pending;
            if(cellular)
            {
                int firstSpan=offset;
                if(!Spend(_spanRows[slot].Count))return false;
                for(int row=_spanRows[slot].First;row!=0;row=_nextSpan[row-1])
                {
                    var span=source.Spans[row-1];
                    const int spanWords=WorldChunkPayloadCodec.SpanWords;
                    if(words.Length-offset<spanWords)return Reject(WorldCommitOwnerFailure.Capacity);
                    int position=offset;
                    while(position>firstSpan && WorldChunkPayloadCodec.CompareSpans(WorldChunkPayloadCodec.ReadSpan(words,position-spanWords),span)>0)
                    {
                        if(!Spend(spanWords))return false;
                        Array.Copy(words,position-spanWords,words,position,spanWords);position-=spanWords;
                    }
                    WorldChunkPayloadCodec.WriteSpan(words,position,span);offset+=spanWords;words[14]++;
                }
                // 检查不同类型的盒不重复拥有空间，预算按枚举盒与点的比较上界预扣。
                long spans=words[14];if(!Spend(spans*(spans-1)/2+spans*_occupancy.Count))return false;
            }
            _counts[slot]=offset;
            if(!Spend(offset))return false;
            return WorldChunkPayloadCodec.Validate(words,offset,key) || Reject(WorldCommitOwnerFailure.InvalidInput);
        }
        private void AppendHistory(WorldChunkPayloadHeader h)
        {
            int index=(_historyHead+_historyCount)%_historyHeaders.Length;
            if(_historyCount==_historyHeaders.Length)
            {
                index=_historyHead;_historyHead=(_historyHead+1)%_historyHeaders.Length;
                var old=_historyHeaders[index];_historyLinks.Remove((old.Key,old.Generation,old.BaseRevision));
            }
            else _historyCount++;
            _historyLinks.Add((h.Key,h.Generation,h.BaseRevision));
            _historyHeaders[index]=h;HistoryRecordsWritten++;
        }
        /// <summary>
        /// 单次复制冻结整块；分页发送者应保存这份caller-owned数组，后续页不再回读Owner。
        /// 新Baseline的Tick和SnapshotId取当前世界提交边界，内容可自上次变化后一直保持相同。
        /// </summary>
        public bool TryCopyChunk(WorldChunkKey key,uint[] destination,out WorldChunkPayloadHeader header)
        {
            header=default;
            if(_indices.TryGetValue(key,out int i))
            {
                var h=_headers[i];
                // Current用于新Baseline，不带历史依赖；同一份payload在历史ring中仍保留真实BaseRevision。
                var baseline=new WorldChunkPayloadHeader(h.WorldEpoch,CommitSequence,h.Key,h.Generation,0,h.Revision,_lastTick,h.WordCount,h.Checksum,h.IsResident);
                return Copy(baseline,_published[i],destination,out header);
            }
            return Reject(WorldCommitOwnerFailure.BaselineRequired);
        }
        /// <summary>
        /// 从客户端已ACK的Base沿同Key/同Generation完整历史闭合到当前Revision，再交付当前完整替换正文。
        /// 每条记录保存after-image而非稀疏操作，因此连续中间版无需逐次发包/等ACK；Base仍严格为客户端
        /// 已安装版本，不能把“当前正文可读”误当作历史依赖已证明。缺链、旧窗口或换代仍要求Baseline。
        /// SnapshotId/ServerTick取该Key最后真实提交头，不重封装成后来无关Key推进的global采样序号。
        /// 验证已提交历史的起始依赖仍在有界窗口，再复制一块当前正文；不修改数量、发送预算或在途快照。
        /// </summary>
        public bool TryCopyDelta(WorldChunkKey key,ulong generation,ulong baseRevision,uint[] destination,out WorldChunkPayloadHeader header)
        {
            header=default;
            // Ring保留完整已提交历史，但退休后不再有当前地址；同Key重建也已经换代。
            // 先确认当前可寻址生命周期，避免旧ACK/调用者绕过Coordinator而冻结退休或旧代payload。
            if(!_indices.TryGetValue(key,out int slot) || baseRevision==0 || _headers[slot].Generation!=generation
                || baseRevision>=_headers[slot].Revision)return Reject(WorldCommitOwnerFailure.BaselineRequired);
            if(!_historyLinks.Contains((key,generation,baseRevision)))return Reject(WorldCommitOwnerFailure.BaselineRequired);
            // 先验检查对应已经被 FIFO 环保留的首条记录。上方校验当前同代且有更新，
            // 因此其后的全体连续 Revision 也仍在环内；正文仍绑定当前真实提交头。
            var current=_headers[slot];
            var merged=new WorldChunkPayloadHeader(current.WorldEpoch,current.SnapshotId,current.Key,
                current.Generation,baseRevision,current.Revision,current.ServerTick,current.WordCount,current.Checksum,current.IsResident);
            return Copy(merged,_published[slot],destination,out header);
        }
        private bool Copy(WorldChunkPayloadHeader h,uint[] source,uint[] destination,out WorldChunkPayloadHeader header)
        {
            header=default;if(destination==null || destination.Length<h.WordCount)return Reject(WorldCommitOwnerFailure.DestinationCapacity);
            Array.Copy(source,destination,h.WordCount);header=h;LastFailure=WorldCommitOwnerFailure.None;return true;
        }
        private bool Spend(long work){if(work>_budget)return Reject(WorldCommitOwnerFailure.InspectionBudget);_budget-=(int)work;return true;}
        private bool Reject(WorldCommitOwnerFailure f){LastFailure=f;return false;}
    }
}
