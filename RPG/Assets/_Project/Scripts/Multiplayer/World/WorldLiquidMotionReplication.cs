using System;
using System.Collections.Generic;
using Game.Core;
using Game.Networking;
using Game.NetworkProtocol;
using UnityEngine;

namespace Game.Multiplayer
{
    /// <summary>
    /// 数量提交旁的只读运动通道。完整组先选择、完整帧再分页；任何表现失败只回退静态数量，
    /// 不写数量Store、不推进数量ACK、不触发checkpoint，也不能把表现拥塞变成Gameplay Frozen。
    /// 所有可增长容器在构造时预留固定上限；热路径只复用数组和Protobuf repeated容量。
    /// </summary>
    public sealed class WorldLiquidMotionReplication
    {
        // 页上限覆盖一份最大完整窗口，不增加96KiB/s或12KiB burst。
        // 正式Channel在窗口末页立刻结束本轮运动段，不能借剩余循环次数再拼下一份window。
        public const int BytesPerSecond=96*1024, BurstBytes=12*1024;
        public const int MaxPagesPerPump=(WorldLiquidMotionCodec.MaxWords+WorldPageCodec.PageWords-1)/WorldPageCodec.PageWords;
        private const double MaximumWindowAge=.35;
        private readonly WorldReplicationCoordinator _quantities;
        private readonly IWorldLiquidMotionSource _source;
        private readonly IWorldLiquidMotionSink _sink;
        private WorldLiquidMotionCaptureBuffer _capture=new WorldLiquidMotionCaptureBuffer();
        private WorldLiquidMotionCaptureBuffer _captureCandidate=new WorldLiquidMotionCaptureBuffer();
        private readonly WorldLiquidMotionAssembler _assembler;
        private readonly WorldLiquidMotionFrame _published=new WorldLiquidMotionFrame();
        private readonly uint[] _receiveWords=new uint[WorldPageCodec.PageWords];
        private readonly Dictionary<int,int> _groupLookup=new Dictionary<int,int>(WorldLiquidMotionCaptureBuffer.Capacity);
        private readonly int[] _heads=new int[WorldLiquidMotionCaptureBuffer.Capacity],
            _next=new int[WorldLiquidMotionCaptureBuffer.Capacity],_counts=new int[WorldLiquidMotionCaptureBuffer.Capacity];
        private readonly float[] _distances=new float[WorldLiquidMotionCaptureBuffer.Capacity];
        private readonly int[] _heap=new int[WorldLiquidMotionCaptureBuffer.Capacity];
        private int _groupCount;
        private uint _captureEpoch;
        private ulong _sequence, _receivingSequence;
        private double _sampleTime, _receivingAt;
        private readonly Sender[] _senders;
        private readonly WorldPageMessage _outbound=new WorldPageMessage();
        private sealed class Sender
        {
            internal readonly WorldLiquidMotionFrame Frame=new WorldLiquidMotionFrame();
            internal readonly uint[] Words=new uint[WorldLiquidMotionCodec.MaxWords];
            internal int WordCount,PageIndex,PageCount;
            internal uint Epoch,Checksum;
            internal ulong ConsideredSequence;
            internal double Tokens=BurstBytes,LastBudgetTime;
            internal bool HasBudgetTime;
        }
        public long Captures {get;private set;}
        public long CaptureRejected {get;private set;}
        public long GroupsRejected {get;private set;}
        public long PagesSent {get;private set;}
        public long EncodedBytesSent {get;private set;}
        public long ChargedBytesSent {get;private set;}
        public long Published {get;private set;}
        public long Rejected {get;private set;}
        public long TimedOut {get;private set;}
        public uint LastCapturedEpoch=>_captureEpoch;
        public ulong LastPublishedSequence=>_assembler!=null?_assembler.AcceptedSequence:0;

        public WorldLiquidMotionReplication(WorldReplicationCoordinator quantities,IOnlineLiquidMotionWorld binding)
        {
            _quantities=quantities??throw new ArgumentNullException(nameof(quantities));
            _source=binding?.Source;_sink=binding?.Sink;
            if(quantities.IsAuthority) { _senders=new Sender[4];for(int i=0;i<4;i++)_senders[i]=new Sender(); }
            else _assembler=new WorldLiquidMotionAssembler(quantities.WorldEpoch);
            for(int i=0;i<WorldPageCodec.PageWords;i++)_outbound.Words.Add(0);
            _outbound.Words.Clear();
        }

        /// <summary>仅在同一数量Capture成功后调用；同一个材料Tick不会重复造窗口。</summary>
        public void Capture(WorldReplicaBuffer committedQuantities,double serverTime)
        {
            if(_source==null || !_quantities.IsAuthority || !double.IsFinite(serverTime))return;
            try
            {
                if(!_source.TryCapture(_captureCandidate,committedQuantities)) { CaptureRejected++;_capture.Count=0;_groupCount=0;return; }
                if(_captureCandidate.Epoch==0 || _captureCandidate.Epoch==_captureEpoch)return;
                // Source会覆盖候选数组；同Epoch的外部变化不能把旧链表和新Rows拼在一起。
                var prior=_capture;_capture=_captureCandidate;_captureCandidate=prior;
                _captureEpoch=_capture.Epoch;_sampleTime=serverTime;_sequence++;Captures++;
                _groupLookup.Clear();_groupCount=0;
                for(int i=0;i<_capture.Count;i++)
                {
                    int id=_capture.GroupIds[i];
                    if(!_groupLookup.TryGetValue(id,out int group))
                    {group=_groupCount++;_groupLookup.Add(id,group);_heads[group]=-1;_counts[group]=0;}
                    _next[i]=_heads[group];_heads[group]=i;_counts[group]++;
                }
            }
            catch(Exception) { CaptureRejected++;_capture.Count=0;_groupCount=0; }
        }

        public void ResetPeer(int slot)
        { if(_senders!=null && (uint)slot<4) {var s=_senders[slot];s.Epoch=0;s.PageCount=0;s.ConsideredSequence=0;s.HasBudgetTime=false;s.Tokens=BurstBytes;} }

        /// <summary>实际Actor位置只用于表现优先级；选择失败的整个搬运组都跳过，绝不截断成半团。</summary>
        public bool TryPreparePage(int slot,uint connectionEpoch,Vector3 actorPosition,double now,double serverTime,out WorldPageMessage page)
        {
            page=null;
            if(_senders==null || (uint)slot>=4 || connectionEpoch==0 || !double.IsFinite(now)
                || !double.IsFinite(serverTime) || !_quantities.TryGetLiquidMotionGeometry(out var geometry))return false;
            var sender=_senders[slot];
            if(sender.Epoch!=connectionEpoch) {ResetPeer(slot);sender.Epoch=connectionEpoch;}
            Refill(sender,now);
            if(sender.Tokens<WorldPageCodec.MaxPayloadBytes+72)return false;
            if(sender.PageIndex<sender.PageCount && serverTime-sender.Frame.SampleTime>MaximumWindowAge)
                sender.PageCount=0;
            if(sender.PageIndex>=sender.PageCount)
            {
                if(sender.ConsideredSequence==_sequence || _sequence==0 || _capture.Count==0
                    || serverTime-_sampleTime>MaximumWindowAge)return false;
                sender.ConsideredSequence=_sequence;
                // 一份窗口每peer只筛选一次，订阅未就绪则等下一材料Tick，防止每个Pump重复全世界扫描。
                if(!BuildFrame(slot,sender,actorPosition,geometry))return false;
                if(!WorldLiquidMotionCodec.TryEncode(sender.Frame,sender.Words,out sender.WordCount))
                {Rejected++;return false;}
                _quantities.RetainLiquidMotionAnchors(slot,sender.Frame,now);
                sender.Checksum=WorldLiquidMotionCodec.ComputeChecksum(sender.Words,sender.WordCount);
                sender.PageCount=(sender.WordCount+WorldPageCodec.PageWords-1)/WorldPageCodec.PageWords;
                sender.PageIndex=0;
            }
            var f=sender.Frame;var m=_outbound;m.ResetForReuse();
            m.ProtocolVersion=ProtocolCompatibility.CurrentVersion;m.GameplayHash=_quantities.GameplayHash;
            m.WorldEpoch=f.WorldEpoch;m.ConnectionEpoch=f.ConnectionEpoch;m.Kind=8;m.Domain=1;
            m.SnapshotId=f.Sequence;m.ServerTick=f.MaterialTick;m.Checksum=sender.Checksum;
            m.TotalWords=(uint)sender.WordCount;m.PageIndex=(uint)sender.PageIndex;m.PageCount=(uint)sender.PageCount;
            int offset=sender.PageIndex*WorldPageCodec.PageWords,count=Math.Min(WorldPageCodec.PageWords,sender.WordCount-offset);
            m.WordOffset=(uint)offset;for(int i=0;i<count;i++)m.Words.Add(sender.Words[offset+i]);
            page=m;return true;
        }
        public bool MarkPageSent(int slot,int encodedBytes)
        {
            if(_senders==null || (uint)slot>=4 || encodedBytes<1 || encodedBytes>WorldPageCodec.MaxPayloadBytes)return false;
            var sender=_senders[slot];int charged=encodedBytes+72;
            if(sender.PageIndex>=sender.PageCount || sender.Tokens<charged)return false;
            sender.Tokens-=charged;sender.PageIndex++;PagesSent++;EncodedBytesSent+=encodedBytes;ChargedBytesSent+=charged;return true;
        }
        private static void Refill(Sender sender,double now)
        {
            if(sender.HasBudgetTime)sender.Tokens=Math.Min(BurstBytes,sender.Tokens+Math.Max(0,now-sender.LastBudgetTime)*BytesPerSecond);
            sender.HasBudgetTime=true;sender.LastBudgetTime=now;
        }
        private bool BuildFrame(int slot,Sender sender,Vector3 actor,WorldGridGeometry geometry)
        {
            var f=sender.Frame;f.AnchorCount=f.RowCount=0;
            f.WorldEpoch=_quantities.WorldEpoch;f.ConnectionEpoch=sender.Epoch;f.Sequence=_sequence;
            f.MaterialTick=_captureEpoch;f.SampleTime=_sampleTime;f.Interval=_capture.Interval;f.ChunkSize=_capture.ChunkSize;
            for(int g=0;g<_groupCount;g++)
            {
                _heap[g]=g;float distance=float.PositiveInfinity;
                for(int row=_heads[g];row>=0;row=_next[row])
                {var p=geometry.Origin+((Vector3)_capture.Rows[row].Cell+Vector3.one*.5f)*geometry.CellSize;
                    distance=Math.Min(distance,(p-actor).sqrMagnitude);}
                _distances[g]=distance;
            }
            // 原地min heap：即使8192组均不符合订阅，也只有O(N log N)，不能每次全扫找最近造成O(N²)。
            int heapCount=_groupCount;
            for(int i=heapCount/2-1;i>=0;i--)SiftDown(i,heapCount);
            while(heapCount>0 && f.RowCount<WorldLiquidMotionFrame.MaxRows)
            {
                int closest=_heap[0];_heap[0]=_heap[--heapCount];SiftDown(0,heapCount);
                if(_counts[closest]>WorldLiquidMotionFrame.MaxRows-f.RowCount){GroupsRejected++;continue;}
                int originalRows=f.RowCount,originalAnchors=f.AnchorCount;bool valid=true;
                for(int index=_heads[closest];index>=0;index=_next[index])
                {
                    var row=_capture.Rows[index];var key=Key(row.Cell,f.ChunkSize);int anchor=-1;
                    for(int a=0;a<f.AnchorCount;a++)if(f.Anchors[a].Key==key){anchor=a;break;}
                    if(anchor<0)
                    {
                        if(f.AnchorCount==WorldLiquidMotionFrame.MaxAnchors || !_quantities.TryGetLiquidMotionAnchor(slot,key,out var value))
                        {valid=false;break;}
                        anchor=f.AnchorCount++;f.Anchors[anchor]=value;
                    }
                    f.Rows[f.RowCount++]=new WorldLiquidMotionSample(row.Cell,row.MaterialId,row.PreviousQuantity,row.CurrentQuantity,
                        row.PreviousOffset,row.PreviousVelocity,row.CurrentOffset,row.CurrentVelocity,anchor);
                }
                if(!valid){f.RowCount=originalRows;f.AnchorCount=originalAnchors;GroupsRejected++;}
            }
            return f.RowCount>0;
        }
        private void SiftDown(int root,int count)
        {
            while(root*2+1<count)
            {
                int child=root*2+1;
                if(child+1<count && _distances[_heap[child+1]]<_distances[_heap[child]])child++;
                if(_distances[_heap[root]]<=_distances[_heap[child]])break;
                int swap=_heap[root];_heap[root]=_heap[child];_heap[child]=swap;root=child;
            }
        }
        private static WorldChunkKey Key(Vector3Int cell,int size)=>new WorldChunkKey(WorldMaterialDomain.Liquid,
            Floor(cell.x,size),Floor(cell.y,size),Floor(cell.z,size));
        private static int Floor(int value,int divisor)=>value/divisor-(value%divisor<0?1:0);

        public void ResetConnection(uint epoch)
        {
            if(_assembler==null || !_assembler.ResetConnection(epoch))return;
            _receivingSequence=0;_published.RowCount=_published.AnchorCount=0;_sink?.ResetConnection(epoch);
        }
        /// <summary>
        /// Mirror同一帧可连续交付数量和运动页。上一完整窗口可能刚刚满足数量证据，
        /// 若直接接下一Sequence，CancelPending就会丢掉它；因此在替换前和完成后各尝试发布。
        /// 仍走完整订阅/数量witness和Frozen校验，不延迟数量安装，也不缓存第二份玩法状态。
        /// </summary>
        public bool ReceivePage(WorldPageMessage page,double now,double serverTime,bool frozen)
        {
            Advance(now,serverTime,frozen);
            bool received=ReceivePage(page,now);
            if(received)Advance(now,serverTime,frozen);
            return received;
        }
        public bool ReceivePage(WorldPageMessage page,double now)
        {
            if(_assembler==null || _sink==null || page==null || page.Kind!=8 || page.GameplayHash!=_quantities.GameplayHash)return false;
            var header=new WorldLiquidMotionPageHeader(page.WorldEpoch,page.ConnectionEpoch,page.SnapshotId,page.ServerTick,
                (int)page.TotalWords,(int)page.PageCount,(uint)page.Checksum);
            // 可靠连接按发送序列到达；新完整窗口可替换未完成候选，但接受水位只在Sink验证后推进。
            if(page.SnapshotId!=_receivingSequence)
            {
                if(page.WorldEpoch!=_quantities.WorldEpoch || page.ConnectionEpoch!=_assembler.ConnectionEpoch
                    || page.SnapshotId<=_assembler.AcceptedSequence || page.ServerTick<_assembler.AcceptedMaterialTick)return false;
                _assembler.CancelPending();
                if(!_assembler.TryBegin(header,now)){Rejected++;return false;}
                _receivingSequence=page.SnapshotId;_receivingAt=now;
            }
            for(int i=0;i<page.Words.Count;i++)_receiveWords[i]=page.Words[i];
            bool accepted=_assembler.TryReceivePage(header,(int)page.PageIndex,_receiveWords,page.Words.Count,now);
            if(!accepted){Rejected++;_receivingSequence=_assembler.AcceptedSequence;}
            return accepted;
        }
        /// <summary>在本轮数量页安装之后发布。等量快照尚未到达时最多保留一个候选，不请求数量重传。</summary>
        public void Advance(double now,double serverTime,bool frozen)
        {
            if(_assembler==null || _sink==null)return;
            if(_sink is IWorldLiquidMotionRetainedAnchors retained)
            {
                // 只读固定上限3×32。尤其active旧源已tombstone、future只引用目标时，
                // Store不能独自察觉旧源Leave；仍沿真实Coordinator订阅裁决立即清理全部旧显示。
                int windows=retained.RetainedMotionWindowCount;bool valid=windows>=0 && windows<=3;
                for(int window=0;window<windows && valid;window++)
                {
                    int anchors=retained.GetRetainedMotionAnchorCount(window);
                    if(anchors<1 || anchors>WorldLiquidMotionFrame.MaxAnchors){valid=false;break;}
                    for(int anchor=0;anchor<anchors;anchor++)
                        if(!retained.TryGetRetainedMotionAnchor(window,anchor,out var value) || !_quantities.AcceptsLiquidMotionAnchor(value)){valid=false;break;}
                }
                if(!valid){_sink.ResetConnection(_assembler.ConnectionEpoch);_published.RowCount=0;}
            }
            // 先把完整且订阅有效的候选交给只读衔接层，才观察本轮多Chunk中间数量。
            // 它只能续播旧已验证曲线；TryPublish仍要求所有真实数量见证齐全，不提前ACK。
            if(_sink is IWorldLiquidMotionCandidateSink staging)
            {
                if(!frozen && _assembler.TryGetCompletedCandidate(out var candidate)
                    && _quantities.AcceptsLiquidMotionFrame(candidate)) staging.StagePresentationCandidate(candidate,serverTime);
                else staging.CancelPresentationCandidate();
            }
            _sink.AdvancePresentation(serverTime,frozen);
            if(_published.RowCount>0 && !_quantities.AcceptsLiquidMotionFrame(_published))
            { _sink.ResetConnection(_assembler.ConnectionEpoch);_published.RowCount=0; }
            if(_assembler.TryGetCompletedCandidate(out var frame))
            {
                if(!frozen && _quantities.AcceptsLiquidMotionFrame(frame) && _sink.TryPublish(frame,serverTime))
                { _published.CopyFrom(frame);_assembler.CommitCandidate();Published++; }
                else if(now-_receivingAt>MaximumWindowAge)
                { _assembler.RejectCandidate();_receivingSequence=_assembler.AcceptedSequence;TimedOut++; }
            }
            if(_assembler.ExpirePending(now)){TimedOut++;_receivingSequence=_assembler.AcceptedSequence;}
        }
        public void Clear()
        {
            _assembler?.CancelPending();_sink?.ResetConnection(0);_published.RowCount=0;
            _capture.Count=_captureCandidate.Count=_groupCount=0;_receivingSequence=0;_receivingAt=0;
            // 同World实例禁用再启用不能让Sequence回绕；真正换世界会构造新的通道。
            _captureEpoch=0;
            for(int slot=0;slot<4;slot++)ResetPeer(slot);
        }
    }
}
