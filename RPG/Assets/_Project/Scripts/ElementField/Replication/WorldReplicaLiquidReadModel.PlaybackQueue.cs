using System;
using System.Collections.Generic;
using Game.Core;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class WorldReplicaLiquidReadModel
    {
        // 这是只读表现缓冲，真实数量及ACK仍在Store/Coordinator。只有TryPublish完整验证过
        // 的帧才能入队，Assembler随后可Commit并复用自己的候选数组，不会覆盖这份副本。
        private const int BufferedFutureCapacity=2;
        private sealed class BufferedWindow
        {
            internal readonly WorldLiquidMotionFrame Frame=new WorldLiquidMotionFrame();
            internal readonly Dictionary<MotionKey,int> Rows=new Dictionary<MotionKey,int>(WorldLiquidMotionFrame.MaxRows);
            internal readonly KnownPrevious[] Previous=new KnownPrevious[WorldLiquidMotionFrame.MaxRows];
            internal double Start;
            internal void CopyFrom(WorldLiquidMotionFrame frame,double start,KnownPrevious[] known)
            {Frame.CopyFrom(frame);CopyKnownPrevious(known,Previous);Start=start;Rows.Clear();for(int i=0;i<frame.RowCount;i++)Rows.Add(new MotionKey(frame.Rows[i].Cell,frame.Rows[i].MaterialId),i);}
            internal void Clear(){Rows.Clear();Array.Clear(Previous,0,Previous.Length);Frame.RowCount=Frame.AnchorCount=0;Start=0;}
        }
        private readonly BufferedWindow[] _bufferedFuture={new BufferedWindow(),new BufferedWindow()};
        private readonly HashSet<Vector3Int> _bufferedChanged=new HashSet<Vector3Int>(WorldLiquidMotionFrame.MaxRows*(BufferedFutureCapacity+2));
        private int _bufferedFutureCount;
        private double _motionPlaybackStart;
        public int BufferedPresentationWindows=>_bufferedFutureCount;
        public long BufferedPresentationOverflows{get;private set;}
        public long BufferedPresentationDiscontinuities{get;private set;}
        public double PresentationPlaybackStartTime=>_hasMotion?_motionPlaybackStart:0;
        private WorldLiquidMotionFrame LatestAcceptedPresentationWindow=>_bufferedFutureCount>0?_bufferedFuture[_bufferedFutureCount-1].Frame:_motion;

        private void AcceptValidatedPresentationWindow(WorldLiquidMotionFrame frame,double serverTime,bool continuous)
        {
            _serverPresentationTime=Math.Max(_serverPresentationTime,serverTime);
            var prior=LatestAcceptedPresentationWindow;
            var known=KnownPreviousFor(frame);
            // accepted水位与显示游标分离。true只说明此帧完整、订阅有效、真实Current全部匹配，
            // 不表示它已经占用了active显示槽；下一条可靠Sequence可继续接收，不延迟数量ACK。
            _lastMotionSequence=frame.Sequence;_lastMotionTick=frame.MaterialTick;_lastMotionSampleTime=frame.SampleTime;
            _hasMotionTimeline=true;
            bool oldUnfinished=_hasMotion && !_motionSnap && _serverPresentationTime<_motionPlaybackStart+_motion.Interval;
            bool requiresBridge=_bufferedFutureCount>0 || oldUnfinished || _serverPresentationTime<frame.SampleTime;
            if(!_hasMotion || !continuous || requiresBridge && !PresentationFramesConnect(prior,frame))
            {
                if(_hasMotion && continuous)BufferedPresentationDiscontinuities++;
                ClearBufferedPresentationWindows();ActivatePresentationWindow(frame,frame.SampleTime,true,known);return;
            }
            double priorStart=_bufferedFutureCount>0?_bufferedFuture[_bufferedFutureCount-1].Start:_motionPlaybackStart;
            // 捕获timestamp有87..105ms抖动，不能截掉旧100ms曲线再从新窗中段开始。
            // S(n)=max(rawSample(n),S(n-1)+Interval(n-1))，保持old=1/new=0；
            // 固定间隔时偏移是已观测timestamp抖动的running maximum，不是每窗另加一次100ms。
            double start=_motionSnap && _bufferedFutureCount==0?frame.SampleTime:Math.Max(frame.SampleTime,priorStart+prior.Interval);
            if(_bufferedFutureCount==0 && !oldUnfinished && _serverPresentationTime>=start)
            {ActivatePresentationWindow(frame,start,false,known);return;}
            if(_bufferedFutureCount==BufferedFutureCapacity)
            {
                // 不能覆盖一个尚未播放的中间帧后谎称连续。有限容量满时明确降级最新已验证
                // Current端点，清旧曲线；没有资源写入、无限排队或过去时间的追赶模拟。
                BufferedPresentationOverflows++;ClearBufferedPresentationWindows();
                ActivatePresentationWindow(frame,frame.SampleTime,true,known);return;
            }
            _bufferedFuture[_bufferedFutureCount++].CopyFrom(frame,start,known);
            _hasPresentationCandidate=false;_candidatePartialValid=false;_presentationHolding=true;
            StartActiveRetentionLease();RebuildBufferedChangedCells();
            _observedLiquidVersion=_store.DomainVersion(WorldMaterialDomain.Liquid);_observedSolidVersion=MotionSolidVersion();
            AdvanceMotionPresentationRevision();
        }
        private void ActivatePresentationWindow(WorldLiquidMotionFrame frame,double start,bool snap,KnownPrevious[] known)
        {
            _candidatePartialValid=false;
            CopyKnownPrevious(known,_activeKnownPrevious);
            _motion.CopyFrom(frame);_motionPlaybackStart=start;_motionSnap=snap;_hasMotion=true;
            _motionRows.Clear();for(int i=0;i<_motion.RowCount;i++)_motionRows.Add(new MotionKey(_motion.Rows[i].Cell,_motion.Rows[i].MaterialId),i);
            if(_hasPresentationCandidate && _presentationCandidate.Sequence<=frame.Sequence)_hasPresentationCandidate=false;
            _presentationHolding=_bufferedFutureCount>0;
            _candidateLeaseWindow=0;if(_presentationHolding || _hasPresentationCandidate)StartActiveRetentionLease();
            RebuildBufferedChangedCells();
            _observedLiquidVersion=uint.MaxValue;_observedSolidVersion=MotionSolidVersion();
            AdvanceMotionPresentationRevision();
        }
        private void ActivateDuePresentationWindows()
        {
            if(!_hasMotion || _presentationFrozen)return;
            // 最多2次，处理单次Render跨过时间槽的情况；这里只切只读快照，不驱动Solver补Tick。
            for(int step=0;step<BufferedFutureCapacity && _bufferedFutureCount>0;step++)
            {
                // float Interval累加到double起点可产生纳秒级偏差；100ns只覆盖表示误差。
                var first=_bufferedFuture[0];if(_serverPresentationTime+.0000001<first.Start)break;
                _bufferedFutureCount--;
                if(_bufferedFutureCount>0)_bufferedFuture[0]=_bufferedFuture[1];
                _bufferedFuture[_bufferedFutureCount]=first;
                ActivatePresentationWindow(first.Frame,first.Start,false,first.Previous);first.Clear();
            }
        }
        private void StartActiveRetentionLease()
        {
            if(_candidateLeaseWindow==_motion.Sequence)return;
            _candidateLeaseWindow=_motion.Sequence;_candidateStagedAt=_serverPresentationTime;_candidateStagedAtRealtime=Time.realtimeSinceStartupAsDouble;
        }
        private void ClearBufferedPresentationWindows()
        {for(int i=0;i<_bufferedFuture.Length;i++)_bufferedFuture[i].Clear();_bufferedFutureCount=0;_candidateLeaseWindow=0;}

        private static int FindFrameRow(WorldLiquidMotionFrame frame,Vector3Int cell,byte material)
        {for(int i=0;i<frame.RowCount;i++)if(frame.Rows[i].Cell==cell && frame.Rows[i].MaterialId==material)return i;return -1;}
        private static void CopyKnownPrevious(KnownPrevious[] source,KnownPrevious[] destination)
        {
            if(ReferenceEquals(source,destination))return;
            if(source==null)Array.Clear(destination,0,destination.Length);
            else Array.Copy(source,destination,destination.Length);
        }
        private KnownPrevious[] KnownPreviousFor(WorldLiquidMotionFrame frame)
        {
            // TryPublish接收的是Assembler借用帧，Stage保存的是独立副本；身份/Sequence仍须相同，
            // 每条证明还会逐行核对cell/material/pose/anchor，不能借同序号为另一条轨迹放行。
            if(_hasPresentationCandidate && frame.WorldEpoch==_presentationCandidate.WorldEpoch
                && frame.ConnectionEpoch==_presentationCandidate.ConnectionEpoch && frame.Sequence==_presentationCandidate.Sequence
                && frame.MaterialTick==_presentationCandidate.MaterialTick)return _candidateKnownPrevious;
            if(ReferenceEquals(frame,_motion))return _activeKnownPrevious;
            for(int i=0;i<_bufferedFutureCount;i++)if(ReferenceEquals(frame,_bufferedFuture[i].Frame))return _bufferedFuture[i].Previous;
            return null;
        }
        private void CaptureKnownPrevious(WorldLiquidMotionFrame frame)
        {
            Array.Clear(_candidateKnownPrevious,0,_candidateKnownPrevious.Length);
            ValidateDormantEndpoints();
            if(!_hasMotion)return;
            var prior=LatestAcceptedPresentationWindow;
            for(int i=0;i<frame.RowCount;i++)
            {
                var row=frame.Rows[i];
                if(row.PreviousQuantity==0 || FindFrameRow(prior,row.Cell,row.MaterialId)>=0
                    || row.AnchorIndex<0 || row.AnchorIndex>=frame.AnchorCount)continue;
                var anchor=frame.Anchors[row.AnchorIndex];
                if(!_store.TryGetPublishedChunkFact(anchor.Key,out var fact,out bool interested)
                    || fact.WorldEpoch!=frame.WorldEpoch || fact.Generation!=anchor.Generation
                    || fact.Revision>anchor.Revision || !fact.IsResident || !interested
                    || !TryGetQuantity(row.Cell,(MaterialId)row.MaterialId,out uint quantity) || quantity!=row.PreviousQuantity)continue;
                // 没有传输过的静水，Replica的已知pose就是数量Store的静止表现。若更早已验证的
                // retained窗仍知道该格endpoint，优先使用那份pose；不读取新candidate的自报Previous。
                var sample=new LiquidCellPresentationSample(quantity,Vector3.zero,Vector3.zero);
                bool retainedPose=false;
                for(int window=_bufferedFutureCount;window>=0;window--)
                {
                    var retained=RetainedFrame(window);int index=FindFrameRow(retained,row.Cell,row.MaterialId);
                    if(index<0)continue;
                    var old=retained.Rows[index];
                    sample=new LiquidCellPresentationSample(old.CurrentQuantity,old.CurrentOffset,old.CurrentVelocity);retainedPose=true;break;
                }
                // active/future更近的明确事实优先；仅真正缺行时补取两Tick内完整witness过的独立值。
                // 不允许拿更旧cache覆盖一个现存但pose冲突的retained endpoint。
                if(!retainedPose && TryReadDormantEndpoint(frame,row,anchor,fact,out var dormant))sample=dormant;
                if(!PreviousPoseMatches(row,sample))continue;
                _candidateKnownPrevious[i]=new KnownPrevious(frame,row,anchor,fact.Revision,sample);
            }
        }
        private static bool PreviousPoseMatches(WorldLiquidMotionSample row,LiquidCellPresentationSample sample)
            =>row.PreviousQuantity==sample.Quantity && (row.PreviousOffset-sample.OffsetCells).sqrMagnitude<=.000001f
                && (row.PreviousVelocity-sample.VelocityWorld).sqrMagnitude<=.000001f;
        private bool MatchesKnownPrevious(WorldLiquidMotionFrame frame,int index,KnownPrevious[] known)
        {
            if(known==null || index<0 || index>=frame.RowCount)return false;
            var proof=known[index];var row=frame.Rows[index];
            if(!proof.Valid || proof.WorldEpoch!=frame.WorldEpoch || proof.ConnectionEpoch!=frame.ConnectionEpoch
                || proof.Sequence!=frame.Sequence || proof.MaterialTick!=frame.MaterialTick
                || proof.Cell!=row.Cell || proof.Material!=row.MaterialId || !PreviousPoseMatches(row,proof.Sample)
                || row.AnchorIndex<0 || row.AnchorIndex>=frame.AnchorCount)return false;
            var anchor=frame.Anchors[row.AnchorIndex];
            if(anchor.Key!=proof.Anchor.Key || anchor.Generation!=proof.Anchor.Generation
                || anchor.Revision!=proof.Anchor.Revision || anchor.SubscriptionEpoch!=proof.Anchor.SubscriptionEpoch
                || anchor.IsResident!=proof.Anchor.IsResident)return false;
            // 证明只延续Stage时已存在的旧量。后来的F/反应超过本窗Revision，或Generation/
            // Interest变更，立即失效；真正发布仍另走全部Current witness，不由这个缓存授予。
            return _store.TryGetPublishedChunkFact(anchor.Key,out var fact,out bool interested)
                && fact.WorldEpoch==proof.WorldEpoch && fact.Generation==proof.Anchor.Generation
                && fact.Revision>=proof.ObservedRevision && fact.Revision<=anchor.Revision
                && (!fact.IsResident || interested) && (fact.IsResident || !anchor.IsResident);
        }
        private bool PresentationFramesConnect(WorldLiquidMotionFrame prior,WorldLiquidMotionFrame next)
        {
            if(next.MaterialTick!=prior.MaterialTick+1)return false;
            for(int i=0;i<prior.AnchorCount;i++)for(int j=0;j<next.AnchorCount;j++)
                if(prior.Anchors[i].Key==next.Anchors[j].Key && (prior.Anchors[i].Generation!=next.Anchors[j].Generation || prior.Anchors[i].SubscriptionEpoch!=next.Anchors[j].SubscriptionEpoch))return false;
            for(int i=0;i<prior.RowCount;i++)
            {
                var old=prior.Rows[i];if(old.CurrentQuantity==0)continue;
                int index=FindFrameRow(next,old.Cell,old.MaterialId);
                if(index<0){if(CanRetainUnchangedMissingRow(prior,old))continue;return false;}
                var row=next.Rows[index];
                if(row.PreviousQuantity!=old.CurrentQuantity || (row.PreviousOffset-old.CurrentOffset).sqrMagnitude>.000001f || (row.PreviousVelocity-old.CurrentVelocity).sqrMagnitude>.000001f)return false;
            }
            for(int i=0;i<next.RowCount;i++)
            {
                var row=next.Rows[i];int index=FindFrameRow(prior,row.Cell,row.MaterialId);
                uint expected=index>=0?prior.Rows[index].CurrentQuantity:0;
                if(row.PreviousQuantity!=expected && (index>=0 || !MatchesKnownPrevious(next,i,KnownPreviousFor(next))))return false;
            }
            return true;
        }
        private uint ReadCurrentQuantity(WorldLiquidMotionSample row)
        {TryGetQuantity(row.Cell,(MaterialId)row.MaterialId,out uint quantity);return quantity;}
        private bool CanRetainUnchangedMissingRow(WorldLiquidMotionFrame prior,WorldLiquidMotionSample row)
        {
            if(row.CurrentQuantity==0 || row.AnchorIndex<0 || row.AnchorIndex>=prior.AnchorCount)return false;
            var anchor=prior.Anchors[row.AnchorIndex];
            return _store.TryGetPublishedChunkFact(anchor.Key,out var fact,out bool interested)
                && fact.WorldEpoch==prior.WorldEpoch && fact.Generation==anchor.Generation
                && fact.Revision>=anchor.Revision && fact.IsResident && interested
                && ReadCurrentQuantity(row)==row.CurrentQuantity;
        }
        private WorldLiquidMotionFrame RetainedFrame(int index)=>index==0?_motion:_bufferedFuture[index-1].Frame;
        private uint LatestRetainedQuantity(Vector3Int cell,byte material)
        {
            var key=new MotionKey(cell,material);
            for(int i=_bufferedFutureCount-1;i>=0;i--)if(_bufferedFuture[i].Rows.TryGetValue(key,out int row))return _bufferedFuture[i].Frame.Rows[row].CurrentQuantity;
            return _motionRows.TryGetValue(key,out int active)?_motion.Rows[active].CurrentQuantity:0;
        }
        private WorldLiquidMotionAnchor LatestAnchor(WorldChunkKey key,WorldLiquidMotionAnchor fallback,bool partial)
        {
            if(partial)for(int i=0;i<_presentationCandidate.AnchorCount;i++)if(_presentationCandidate.Anchors[i].Key==key)return _presentationCandidate.Anchors[i];
            for(int f=_bufferedFutureCount-1;f>=0;f--)for(int i=0;i<_bufferedFuture[f].Frame.AnchorCount;i++)if(_bufferedFuture[f].Frame.Anchors[i].Key==key)return _bufferedFuture[f].Frame.Anchors[i];
            return fallback;
        }
        private bool ValidateBufferedQuantityFacts(bool partial)
        {
            // 过去已验证的曲线可短暂保留，但只在后继帧严格衔接、最新真实数量仍与这条链相符时。
            // 非候选Row继续读Store；F移除最新Current、Generation/Interest/固体变化都立即撤旧。
            for(int f=0;f<=_bufferedFutureCount;f++)
            {
                var frame=RetainedFrame(f);
                for(int i=0;i<frame.AnchorCount;i++)
                {
                    var anchor=LatestAnchor(frame.Anchors[i].Key,frame.Anchors[i],partial);
                    if(!_store.TryGetPublishedChunkFact(anchor.Key,out var fact,out bool interested)
                        || fact.WorldEpoch!=_store.WorldEpoch || fact.Generation!=frame.Anchors[i].Generation
                        || fact.IsResident && !interested || partial && fact.Revision>anchor.Revision)return false;
                }
                for(int i=0;i<frame.RowCount;i++)
                {
                    var row=frame.Rows[i];TryGetQuantity(row.Cell,(MaterialId)row.MaterialId,out uint actual);
                    int candidate=partial?CandidateRow(row.Cell,row.MaterialId):-1;
                    if(candidate>=0)
                    {var next=_presentationCandidate.Rows[candidate];if(actual!=next.PreviousQuantity && actual!=next.CurrentQuantity)return false;}
                    else if(actual!=LatestRetainedQuantity(row.Cell,row.MaterialId))return false;
                }
            }
            return true;
        }
        private bool TryReadBufferedPresentation(Vector3Int cell,MaterialId material,bool previous,out LiquidCellPresentationSample sample)
        {
            var key=new MotionKey(cell,(byte)material);
            if(_motionRows.TryGetValue(key,out int index))
            {
                var row=_motion.Rows[index];sample=previous?new LiquidCellPresentationSample(row.PreviousQuantity,row.PreviousOffset,row.PreviousVelocity):new LiquidCellPresentationSample(row.CurrentQuantity,row.CurrentOffset,row.CurrentVelocity);return true;
            }
            // future-only新格已经在真实Store出现，表现仍取最早future的Previous，避免提前显示落点。
            for(int i=0;i<_bufferedFutureCount;i++)if(_bufferedFuture[i].Rows.TryGetValue(key,out index))
            {var row=_bufferedFuture[i].Frame.Rows[index];sample=new LiquidCellPresentationSample(row.PreviousQuantity,row.PreviousOffset,row.PreviousVelocity);return true;}
            // 合法future可继续播，但不能把它的Holding旗标借给被F/反应等撤销的candidate。
            // TryReadMotion入口已检查版本与租约；这里仅消费本candidate的已验证资格缓存。
            if(_presentationHolding && _hasPresentationCandidate && _candidatePartialValid && (index=CandidateRow(cell,(byte)material))>=0)
            {var row=_presentationCandidate.Rows[index];sample=new LiquidCellPresentationSample(row.PreviousQuantity,row.PreviousOffset,row.PreviousVelocity);return true;}
            sample=default;return false;
        }
        private void RebuildBufferedChangedCells(bool includeHeldCandidate=false)
        {
            _bufferedChanged.Clear();_motionChangedCount=0;
            for(int f=0;f<=_bufferedFutureCount;f++)
            {var frame=RetainedFrame(f);for(int i=0;i<frame.RowCount;i++)if(_bufferedChanged.Add(frame.Rows[i].Cell))_motionChangedCells[_motionChangedCount++]=frame.Rows[i].Cell;}
            if(!includeHeldCandidate || !_presentationHolding || !_hasPresentationCandidate || !_candidatePartialValid)return;
            for(int i=0;i<_presentationCandidate.RowCount;i++)
            {
                var row=_presentationCandidate.Rows[i];
                // Previous为0的目标不用画旧壳；Current仍等待全部真实数量见证，不能借coverage提前显示。
                if(row.PreviousQuantity>0 && _bufferedChanged.Add(row.Cell))_motionChangedCells[_motionChangedCount++]=row.Cell;
            }
        }

        // 资格依赖外层真实Subscription，尤其旧源Chunk已是tombstone时，Store不能区分新Leave。
        // 只借出immutable Anchor值，不让Multiplayer拿到内部可变Frame数组。
        public int RetainedMotionWindowCount=>_hasMotion?_bufferedFutureCount+1:0;
        public int GetRetainedMotionAnchorCount(int window)=>(uint)window<(uint)RetainedMotionWindowCount?RetainedFrame(window).AnchorCount:0;
        public bool TryGetRetainedMotionAnchor(int window,int index,out WorldLiquidMotionAnchor anchor)
        {anchor=default;if((uint)window>=(uint)RetainedMotionWindowCount)return false;var frame=RetainedFrame(window);if((uint)index>=(uint)frame.AnchorCount)return false;anchor=frame.Anchors[index];return true;}
    }
}
