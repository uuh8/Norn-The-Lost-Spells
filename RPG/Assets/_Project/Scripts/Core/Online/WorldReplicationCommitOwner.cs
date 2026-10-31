using System;

namespace Game.Core
{
    public enum WorldCommitOwnerFailure : byte { None, InvalidInput, Capacity, InspectionBudget, Occupancy, Journal, BaselineRequired, DestinationCapacity }

    /// <summary>
    /// 世界唯一复制提交Owner。调用方在一次材料提交边界捕获Source后调用一次，不按连接扫描世界。
    /// 先找原生Chunk，再在有界小块内规范化Stable/Dynamic；Geometry和Pending同占据一起参与差异。
    /// published/candidate双缓冲及历史均在构造时分配。Journal成功之前不发布任何payload；失败需装配层
    /// 暂停复制并重试/重建，不允许越过失败提交继续假装客户端拥有完整历史。所有访问限所属主线程。
    /// 历史使用完整Chunk replacement，消耗比稀疏patch更多内存，换取明确且容易审查的原子安装语义。
    /// </summary>
    public sealed class WorldReplicationCommitOwner
    {
        private readonly WorldChunkCommitJournal _journal;
        private readonly WorldChunkKey[] _keys, _candidateKeys;
        private readonly WorldChunkPayloadHeader[] _headers, _historyHeaders;
        private readonly uint[][] _published, _candidate, _history;
        private readonly int[] _counts;
        private readonly bool[] _active;
        private readonly WorldChunkChange[] _changes;
        private readonly int[] _changedSlots;
        private readonly WorldMaterialCellSample[] _cellScratch;
        private readonly WorldMaterialOccupancySnapshot _occupancy;
        private readonly WorldGridGeometry[] _geometries=new WorldGridGeometry[4];
        private readonly bool[] _hasGeometry=new bool[4];
        private readonly int _inspectionBudget, _pendingCapacity;
        private int _count, _historyHead, _historyCount, _budget;
        private long _lastTick=-1;
        public ulong WorldEpoch => _journal.WorldEpoch;
        public ulong CommitSequence => _journal.CommitSequence;
        public int ChunkCount => _count;
        public WorldCommitOwnerFailure LastFailure {get;private set;}

        public WorldReplicationCommitOwner(ulong epoch,int chunkCapacity,int cellCapacity,int pendingCapacity,int historyCapacity,int maxWordsPerChunk,int inspectionBudget)
        {
            if(chunkCapacity<=0 || cellCapacity<=0 || pendingCapacity<0 || historyCapacity<=0 || maxWordsPerChunk<11 || inspectionBudget<=0)throw new ArgumentOutOfRangeException(nameof(chunkCapacity));
            _journal=new WorldChunkCommitJournal(epoch,chunkCapacity,Math.Max(historyCapacity,chunkCapacity),inspectionBudget);
            _keys=new WorldChunkKey[chunkCapacity];_candidateKeys=new WorldChunkKey[chunkCapacity];_headers=new WorldChunkPayloadHeader[chunkCapacity];
            _historyHeaders=new WorldChunkPayloadHeader[historyCapacity];_published=Allocate(chunkCapacity,maxWordsPerChunk);_candidate=Allocate(chunkCapacity,maxWordsPerChunk);_history=Allocate(historyCapacity,maxWordsPerChunk);
            _counts=new int[chunkCapacity];_active=new bool[chunkCapacity];_changes=new WorldChunkChange[chunkCapacity];_changedSlots=new int[chunkCapacity];
            _cellScratch=new WorldMaterialCellSample[cellCapacity];_occupancy=new WorldMaterialOccupancySnapshot(cellCapacity,inspectionBudget);
            _inspectionBudget=inspectionBudget;_pendingCapacity=pendingCapacity;
        }
        private static uint[][] Allocate(int slots,int words)
        { var a=new uint[slots][];for(int i=0;i<slots;i++)a[i]=new uint[words];return a; }
        public WorldChunkPayloadHeader GetChunkHeader(int index)
        {if(index<0 || index>=_count)throw new ArgumentOutOfRangeException(nameof(index));return _headers[index];}

        /// <summary>Source调用已结束且库存事务栅栏释放后执行；失败保持全部公开数据与版本不变。</summary>
        public bool TryCaptureCommit(WorldReplicaBuffer source)
        {
            _budget=_inspectionBudget;
            if(source==null)return Reject(WorldCommitOwnerFailure.InvalidInput);
            var h=source.Header;
            if(h.WorldTick<0 || h.WorldTick<_lastTick || h.CellCount<0 || h.CellCount>source.Cells.Length || h.PendingCount<0 || h.PendingCount>source.Pending.Length || h.PendingCount>_pendingCapacity || h.GeometryCount<0 || h.GeometryCount>4)return Reject(WorldCommitOwnerFailure.InvalidInput);
            if(!Spend(_count+4L+h.CellCount+h.PendingCount+h.GeometryCount))return false;
            for(int i=0;i<4;i++)_hasGeometry[i]=false;
            int nextCount=_count;
            for(int i=0;i<_count;i++){_candidateKeys[i]=_keys[i];_active[i]=false;_counts[i]=0;}
            for(int i=0;i<h.GeometryCount;i++)
            {
                var g=source.Geometries[i];if(!WorldChunkPayloadCodec.ValidGeometry(g) || _hasGeometry[(int)g.Domain])return Reject(WorldCommitOwnerFailure.InvalidInput);
                _geometries[(int)g.Domain]=g;_hasGeometry[(int)g.Domain]=true;
                if(!Activate(new WorldChunkKey(g.Domain,0,0,0),ref nextCount))return false;
            }
            for(int i=0;i<h.CellCount;i++)
            {
                var c=source.Cells[i];if(c.Quantity==0)continue;
                if((byte)c.Domain>3 || !_hasGeometry[(int)c.Domain])return Reject(WorldCommitOwnerFailure.InvalidInput);
                if(!Activate(WorldChunkPayloadCodec.CellKey(c.Domain,c.Cell,_geometries[(int)c.Domain].ChunkSize),ref nextCount))return false;
            }
            for(int i=0;i<h.PendingCount;i++)
            {
                var p=source.Pending[i];if(!WorldChunkPayloadCodec.ValidPending(p) || !_hasGeometry[(int)p.Domain] || !WorldChunkPayloadCodec.TryPositionKey(_geometries[(int)p.Domain],p.WorldPosition,out var key))return Reject(WorldCommitOwnerFailure.InvalidInput);
                // FIFO身份在同一域全局唯一，不能因为重复落在不同Chunk就绕开验证。
                for(int j=0;j<i;j++){if(!Spend(1))return false;if(source.Pending[j].Domain==p.Domain && source.Pending[j].QueueIndex==p.QueueIndex)return Reject(WorldCommitOwnerFailure.InvalidInput);}
                if(!Activate(key,ref nextCount))return false;
            }
            int changes=0;
            for(int slot=0;slot<nextCount;slot++)
            {
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
            // 为下面无失败发布预留全部工作量。Journal内部也独立限量；其成功之后没有外部回调。
            long publishWork=nextCount;
            for(int c=0;c<changes;c++)publishWork+=(long)_counts[_changedSlots[c]]*3;
            if(!Spend(publishWork))return false;
            if(!_journal.TryCommit(_changes,changes))return Reject(WorldCommitOwnerFailure.Journal);
            for(int c=0;c<changes;c++)
            {
                int slot=_changedSlots[c];_keys[slot]=_candidateKeys[slot];
                _journal.TryGetState(_keys[slot],out var state);
                ulong baseRevision=_changes[c].Kind==WorldChunkChangeKind.Create?0:state.Revision-1;
                uint[] previous=_published[slot];_published[slot]=_candidate[slot];_candidate[slot]=previous;
                int words=_active[slot]?_counts[slot]:0;
                var header=new WorldChunkPayloadHeader(WorldEpoch,CommitSequence,_keys[slot],state.Generation,baseRevision,state.Revision,h.WorldTick,words,WorldChunkPayloadCodec.ComputeChecksum(_published[slot],words),_active[slot]);
                _headers[slot]=header;AppendHistory(header,_published[slot]);
            }
            _count=nextCount;_lastTick=h.WorldTick;LastFailure=WorldCommitOwnerFailure.None;return true;
        }
        private bool Activate(WorldChunkKey key,ref int count)
        {
            for(int i=0;i<count;i++){if(!Spend(1))return false;if(_candidateKeys[i]==key){_active[i]=true;return true;}}
            if(count==_keys.Length)return Reject(WorldCommitOwnerFailure.Capacity);
            _candidateKeys[count]=key;_active[count]=true;_counts[count]=0;count++;return true;
        }
        private bool BuildChunk(WorldReplicaBuffer source,int slot)
        {
            var key=_candidateKeys[slot];var geometry=_geometries[(int)key.Domain];int cells=0;
            if(!Spend((long)source.Header.CellCount+source.Header.PendingCount+11))return false;
            for(int i=0;i<source.Header.CellCount;i++)
            {
                var c=source.Cells[i];if(c.Quantity==0 || c.Domain!=key.Domain || WorldChunkPayloadCodec.CellKey(c.Domain,c.Cell,geometry.ChunkSize)!=key)continue;
                if(cells==_cellScratch.Length)return Reject(WorldCommitOwnerFailure.Capacity);_cellScratch[cells++]=c;
            }
            // HeapSort+线性归并的保守上界与规范化器采用同一检查单位：比较/交换各一次。
            // 用long进行计量，不能再按旧插入排序n²扣预算，否则正常大Chunk会被人为拒绝。
            int levels=0;for(long n=1;n<Math.Max(2,cells);n*=2)levels++;
            if(!Spend(6L*cells*levels+4L*cells))return false;
            if(!_occupancy.TryCapture(_cellScratch,cells))return Reject(WorldCommitOwnerFailure.Occupancy);
            uint[] words=_candidate[slot];long residentWords=11L+(long)_occupancy.Count*7;
            if(residentWords>words.Length)return Reject(WorldCommitOwnerFailure.Capacity);
            words[0]=WorldChunkPayloadCodec.Magic;words[1]=(uint)_occupancy.Count;words[2]=0;words[3]=1;
            WorldChunkPayloadCodec.WriteGeometry(words,4,geometry);int offset=11;
            if(!Spend((long)_occupancy.Count*7))return false;
            for(int i=0;i<_occupancy.Count;i++,offset+=7)WorldChunkPayloadCodec.WriteCell(words,offset,_occupancy.GetCell(i));
            int pending=0;
            // Source的FIFO顺序应按QueueIndex递增；主动排序局部Pending让枚举实现细节不产生假差异。
            for(int i=0;i<source.Header.PendingCount;i++)
            {
                var p=source.Pending[i];if(p.Domain!=key.Domain)continue;
                WorldChunkPayloadCodec.TryPositionKey(geometry,p.WorldPosition,out var k);if(k!=key)continue;
                if(words.Length-offset<18)return Reject(WorldCommitOwnerFailure.Capacity);
                int position=offset;
                while(position>residentWords && words[position-17]>(uint)p.QueueIndex)
                {if(!Spend(18))return false;Array.Copy(words,position-18,words,position,18);position-=18;}
                if(!Spend(18))return false;WorldChunkPayloadCodec.WritePending(words,position,p);offset+=18;pending++;
            }
            words[2]=(uint)pending;_counts[slot]=offset;
            if(!Spend(offset))return false;
            return WorldChunkPayloadCodec.Validate(words,offset,key) || Reject(WorldCommitOwnerFailure.InvalidInput);
        }
        private void AppendHistory(WorldChunkPayloadHeader h,uint[] words)
        {
            int index=(_historyHead+_historyCount)%_history.Length;
            if(_historyCount==_history.Length){index=_historyHead;_historyHead=(_historyHead+1)%_history.Length;}else _historyCount++;
            _historyHeaders[index]=h;Array.Copy(words,_history[index],h.WordCount);
        }
        /// <summary>
        /// 单次复制冻结整块；分页发送者应保存这份caller-owned数组，后续页不再回读Owner。
        /// 新Baseline的Tick和SnapshotId取当前世界提交边界，内容可自上次变化后一直保持相同。
        /// </summary>
        public bool TryCopyChunk(WorldChunkKey key,uint[] destination,out WorldChunkPayloadHeader header)
        {
            header=default;
            for(int i=0;i<_count;i++)if(_keys[i]==key)
            {
                var h=_headers[i];
                // Current用于新Baseline，不带历史依赖；同一份payload在历史ring中仍保留真实BaseRevision。
                var baseline=new WorldChunkPayloadHeader(h.WorldEpoch,CommitSequence,h.Key,h.Generation,0,h.Revision,_lastTick,h.WordCount,h.Checksum,h.IsResident);
                return Copy(baseline,_published[i],destination,out header);
            }
            return Reject(WorldCommitOwnerFailure.BaselineRequired);
        }
        /// <summary>只找恰好依赖客户端当前版本的一条完整替换记录；被覆盖/换代时明确重新Baseline。</summary>
        public bool TryCopyDelta(WorldChunkKey key,ulong generation,ulong baseRevision,uint[] destination,out WorldChunkPayloadHeader header)
        {
            header=default;
            for(int i=0;i<_historyCount;i++)
            {int at=(_historyHead+i)%_history.Length;var h=_historyHeaders[at];if(h.Key==key && h.Generation==generation && h.BaseRevision==baseRevision && baseRevision!=0)return Copy(h,_history[at],destination,out header);}
            return Reject(WorldCommitOwnerFailure.BaselineRequired);
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
