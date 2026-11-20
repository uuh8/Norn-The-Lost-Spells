using System;
using System.Collections.Generic;
using Game.Core;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    // 诊断码只描述原有拒绝分支，不新增放行条件。固定数值便于Native JSON跨版本审计；
    // 不在热路径创建字符串/异常/集合，也不能用诊断码代替数量或订阅见证。
    public enum WorldLiquidMotionWitnessFailure : byte
    {
        None, AnchorFormat, AnchorMissing, AnchorWorldEpoch, AnchorGeneration, AnchorRevisionBehind,
        AnchorResidency, AnchorInterest, RowAnchor, RowCapacity, RowMaterial, RowCellKey,
        CurrentQuantity, DuplicateMaterial, PreviousCapacity
    }
    public enum WorldLiquidMotionHoldFailure : byte
    {
        None, NoCandidate, Frozen, TickGap, LeaseExpired, SampleTooOld, MissingContinuationRow,
        PreviousQuantity, PreviousOffset, PreviousVelocity, UnexpectedPreviousQuantity, RowAnchor,
        Generation, RevisionAhead, Interest, UnknownPreviousSource, NotPreviousOrCurrent
    }
    [Serializable]
    public struct WorldLiquidMotionValidationDiagnostic
    {
        // JsonUtility不能可靠序列化合同readonly字段，故只导出中立值的扁平快照。
        // Expected/Actual的含义由Reason决定；Pose误差另存平方距离，避免字符串格式化。
        public byte Reason,MaterialId;
        public ulong Sequence,ExpectedRevision,ActualRevision,ExpectedGeneration,ActualGeneration;
        public uint MaterialTick,ExpectedQuantity,ActualQuantity;
        public int ChunkX,ChunkY,ChunkZ,CellX,CellY,CellZ,AnchorCount,RowCount;
        public float PoseDeltaSquared;
    }
    public sealed partial class WorldReplicaLiquidReadModel : IWorldLiquidMotionSink, IWorldLiquidMotionCandidateSink, IWorldLiquidMotionRetainedAnchors, ILiquidPresentationPlaybackSource
    {
        private readonly struct MotionKey : IEquatable<MotionKey>
        {
            internal readonly Vector3Int Cell; internal readonly byte Material;
            internal MotionKey(Vector3Int cell, byte material) { Cell = cell; Material = material; }
            public bool Equals(MotionKey other) => Cell == other.Cell && Material == other.Material;
            public override bool Equals(object value) => value is MotionKey other && Equals(other);
            public override int GetHashCode() => unchecked(Cell.GetHashCode() * 397 ^ Material);
        }

        // 网络解码缓冲下一条消息会复用；本视图独立拥有一份固定前缀，绝不保存调用者数组引用。
        // History仅是只读表示，所有真实数量始终从Store取得。没有本地Solver或第二份GMU账本。
        private readonly WorldLiquidMotionFrame _motion = new WorldLiquidMotionFrame();
        private readonly Dictionary<MotionKey, int> _motionRows = new Dictionary<MotionKey, int>(WorldLiquidMotionFrame.MaxRows);
        // active + 最多2个完整future + 1个已核对可续播的candidate；这里只扩大固定只读枚举上限。
        private readonly Vector3Int[] _motionChangedCells = new Vector3Int[WorldLiquidMotionFrame.MaxRows*(BufferedFutureCapacity+2)];
        private int _motionChangedCount;
        private uint _motionConnectionEpoch, _motionPresentationRevision = 1, _motionInvalidationRevision = 1,
            _observedLiquidVersion, _observedSolidVersion;
        private ulong _lastMotionSequence;
        private uint _lastMotionTick;
        private double _serverPresentationTime, _lastMotionSampleTime;
        private bool _hasMotion, _hasMotionTimeline, _motionSnap, _presentationFrozen;
        private readonly WorldLiquidMotionFrame _presentationCandidate = new WorldLiquidMotionFrame();
        private readonly Dictionary<MotionKey, int> _candidateRows = new Dictionary<MotionKey, int>(WorldLiquidMotionFrame.MaxRows);
        // Motion只发送有运动的子集：上一帧缺行不等于真实世界没有该格。完整M先于Q到达时，
        // 只为这些新参与的静水捕获旧Store数量/已知pose及anchor身份。每窗最多128项，
        // candidate/active/future各自拥有固定副本；它不是数量账本，也不能授权Current或ACK。
        private readonly struct KnownPrevious
        {
            internal readonly bool Valid;
            internal readonly ulong WorldEpoch,Sequence,ObservedRevision;
            internal readonly uint ConnectionEpoch,MaterialTick;
            internal readonly Vector3Int Cell;
            internal readonly byte Material;
            internal readonly WorldLiquidMotionAnchor Anchor;
            internal readonly LiquidCellPresentationSample Sample;
            internal KnownPrevious(WorldLiquidMotionFrame frame,WorldLiquidMotionSample row,WorldLiquidMotionAnchor anchor,
                ulong observedRevision,LiquidCellPresentationSample sample)
            {Valid=true;WorldEpoch=frame.WorldEpoch;Sequence=frame.Sequence;ConnectionEpoch=frame.ConnectionEpoch;
                MaterialTick=frame.MaterialTick;Cell=row.Cell;Material=row.MaterialId;Anchor=anchor;ObservedRevision=observedRevision;Sample=sample;}
        }
        private readonly KnownPrevious[] _candidateKnownPrevious=new KnownPrevious[WorldLiquidMotionFrame.MaxRows];
        private readonly KnownPrevious[] _activeKnownPrevious=new KnownPrevious[WorldLiquidMotionFrame.MaxRows];
        private bool _hasPresentationCandidate, _presentationHolding;
        // Holding可能只来自无关的已验证future，不能据此读取另一份candidate的Previous。
        // 资格只由ValidatePublishedMotion在数量/solid版本或Stage变化时计算一次；逐行采样只读缓存，
        // 避免Renderer数千次查询各自重复O(128²)的CanHold核验。时钟/墙钟租约仍先于版本快路检查。
        private bool _candidatePartialValid;
        private double _candidateStagedAt, _candidateStagedAtRealtime;
        private ulong _candidateLeaseWindow;
        // 与数量流解耦后，sidecar丢失只让显示回到当前数量，不触发Resync或拖延采集。
        private const double MaximumMotionAgeSeconds = .35;

        public uint PresentationRevision { get { ValidatePublishedMotion(); return _motionPresentationRevision; } }
        public uint PresentationInvalidationRevision { get { ValidatePublishedMotion(); return _motionInvalidationRevision; } }
        public ulong PresentationWindowId { get { ValidatePublishedMotion(); return _hasMotion ? _motion.Sequence : 0; } }
        public bool PresentationHolding { get { ValidatePublishedMotion(); return _presentationHolding; } }
        // 值诊断不拥有任何写权；显式Native探针用它区分真实断档snap与Renderer自行跳终点。
        public bool PresentationSnapped => _hasMotion && _motionSnap;
        public uint PresentationMaterialTick => _hasMotion ? _motion.MaterialTick : 0;
        public double PresentationSampleTime => _hasMotion ? _motion.SampleTime : 0;
        public ulong PendingPresentationSequence => _hasPresentationCandidate ? _presentationCandidate.Sequence : 0;
        public double PresentationServerTime => _serverPresentationTime;
        public ulong LastAttemptedMotionSequence { get; private set; }
        public double LastAttemptedMotionSampleTime { get; private set; }
        public int LastAttemptedMotionAnchorCount { get; private set; }
        public int LastAttemptedMotionRowCount { get; private set; }
        // 0成功/无、1身份或格式、2数量见证、3超前时钟、4过期。仅值观测，不改变发布门槛。
        public byte LastMotionPublishFailure { get; private set; }
        public WorldLiquidMotionValidationDiagnostic LastMotionWitnessDiagnostic { get; private set; }
        public WorldLiquidMotionValidationDiagnostic LastMotionHoldDiagnostic { get; private set; }
        public long MotionHoldChecks { get; private set; }

        public bool TryGetPresentationAlpha(ulong capturedWindowId, out float alpha)
        {
            ValidatePublishedMotion(); alpha = 1;
            if (!_hasMotion || capturedWindowId == 0 || capturedWindowId != _motion.Sequence) return false;
            // Holding只描述数量见证暂时转由可信相邻帧支撑；它不能强迫仍在播放的旧曲线跳到1。
            alpha = _motionSnap ? 1 : Mathf.Clamp01((float)((_serverPresentationTime - _motionPlaybackStart) / _motion.Interval));
            return true;
        }

        /// <summary>
        /// 先完整验证anchor和每行数量证据，再一次发布两相位。较新的无关cell提交可以保留本帧，
        /// 所以Revision允许大于引用值，但Generation、resident事实与每行CurrentQuantity必须一致。
        /// 外层认证Channel负责SubscriptionEpoch；本地不能仅凭一个非零订阅号授予订阅资格。
        /// </summary>
        public bool TryPublish(WorldLiquidMotionFrame frame, double serverTime)
        {
            ValidatePublishedMotion();
            LastAttemptedMotionSequence = frame != null ? frame.Sequence : 0;
            LastAttemptedMotionSampleTime = frame != null ? frame.SampleTime : 0;
            LastAttemptedMotionAnchorCount = frame != null ? frame.AnchorCount : 0;
            LastAttemptedMotionRowCount = frame != null ? frame.RowCount : 0;
            LastMotionWitnessDiagnostic = default;
            LastMotionPublishFailure = 1;
            if (_presentationFrozen || frame == null || _motionConnectionEpoch == 0
                || frame.WorldEpoch != _store.WorldEpoch || frame.ConnectionEpoch != _motionConnectionEpoch
                || frame.Sequence == 0 || frame.Sequence <= _lastMotionSequence || frame.MaterialTick == 0
                || frame.ChunkSize != ChunkSize || !double.IsFinite(serverTime)
                || !double.IsFinite(frame.SampleTime) || frame.SampleTime < 0
                || !WorldLiquidMotionCodec.Validate(frame)) return false;
            if (serverTime - frame.SampleTime > MaximumMotionAgeSeconds) { LastMotionPublishFailure = 4; return false; }
            if (frame.SampleTime - serverTime > frame.Interval) { LastMotionPublishFailure = 3; return false; }
            if (!MatchesQuantityFacts(frame, true)) { LastMotionPublishFailure = 2; return false; }
            LastMotionPublishFailure = 0;
            RememberValidatedEndpoints(frame);

            // 首帧、断档或重新连接先采用Current端点。只有紧邻的权威样本才续播History；
            // 已验证accepted水位与显示active分离，最多两份future保住尚未播完的旧尾段，
            // 不制造客户端数量History、不延迟数量安装/ACK，也不使用predictedTime抢播。
            bool continuous = _hasMotionTimeline && frame.MaterialTick == _lastMotionTick + 1
                && frame.SampleTime > _lastMotionSampleTime
                && Math.Abs(frame.SampleTime - _lastMotionSampleTime - frame.Interval) <= Math.Max(.002, frame.Interval * .25);
            AcceptValidatedPresentationWindow(frame,serverTime,continuous);
            return true;
        }

        /// <summary>
        /// 完整候选复制到本Owner的固定缓冲。重复页/候选不得续租；100ms内仅续播原已验证曲线，
        /// 不延迟Store安装，也不把候选Current当作真实库存或可采集量。超过候选引用Revision的
        /// 收取/反应、Interest退出和固体变化都立即撤掉旧壳，避免为了平滑复活已经收走的水。
        /// </summary>
        public void StagePresentationCandidate(WorldLiquidMotionFrame frame, double serverTime)
        {
            if (_presentationFrozen || !_hasMotionTimeline || frame == null || !double.IsFinite(serverTime)
                || frame.WorldEpoch != _store.WorldEpoch || frame.ConnectionEpoch != _motionConnectionEpoch
                || frame.ChunkSize != ChunkSize || frame.SampleTime - serverTime > frame.Interval
                || serverTime - frame.SampleTime > MaximumMotionAgeSeconds
                || frame.Sequence <= _lastMotionSequence || !WorldLiquidMotionCodec.Validate(frame)) return;
            if (_hasPresentationCandidate && frame.Sequence == _presentationCandidate.Sequence) return;
            // 必须在覆盖candidate和安装本窗Q之前取证。不能在TryPublish看到Current后倒推旧量，
            // 也不因同Sequence重复Stage重采证据，让后来出生的水伪装成早已存在的静水。
            CaptureKnownPrevious(frame);
            _candidatePartialValid=false;
            _presentationCandidate.CopyFrom(frame); _hasPresentationCandidate = true;
            _candidateRows.Clear();
            for (int i = 0; i < frame.RowCount; i++) _candidateRows.Add(new MotionKey(frame.Rows[i].Cell, frame.Rows[i].MaterialId), i);
            // 租约属于旧显示窗口。新候选/重送都不能延长这份旧水的可见寿命；同时检查
            // 同步播放时钟和本地单调墙钟，避免NetworkTime缓慢调时让100ms上限被延长。
            if (_candidateLeaseWindow != _motion.Sequence)
            { _candidateLeaseWindow = _motion.Sequence; _candidateStagedAt = serverTime; _candidateStagedAtRealtime = Time.realtimeSinceStartupAsDouble; }
            _observedLiquidVersion = uint.MaxValue;
        }

        public void CancelPresentationCandidate()
        {
            // 外层掌握Subscription资格。目标尚未安装时，本地Store无法判断它已退出订阅，
            // 因此完整候选被撤销/替换/过期必须显式传入，不等待100ms超时才收回旧显示。
            bool hadCandidate=_hasPresentationCandidate;_hasPresentationCandidate=false;
            _candidatePartialValid=false;
            Array.Clear(_candidateKnownPrevious,0,_candidateKnownPrevious.Length);
            // Assembler Commit后候选自然消失；已复制并验证的future有独立生命周期，不能一并撤掉。
            if(_presentationHolding && _bufferedFutureCount==0)InvalidateMotion(false);
            else if(_bufferedFutureCount>0)
            {
                // Assembler正常Commit后，每个Pump都会先Cancel(no pending)，再Advance(newClock)。
                // 这里若用上一Pump的旧时钟立即校验，本地realtime恰过100ms就会在完整future
                // 获得到期切槽机会前撤掉它。只将缓存标脏；紧随Advance仍先重查数量/固体等
                // 原安全链再切槽，外部若先读取播放窗口/两相位样本，也会即时重查并撤掉非法旧壳。
                // 未验证candidate已清除，故其Previous/Current不能继续为后续改量提供资格。
                _observedLiquidVersion=uint.MaxValue;
                // candidate-only的旧静水页也属于快照输入。即使future继续有效，撤销candidate后
                // 也须让Renderer重抓coverage，不能保留上一份已失效的源页直到下一次Q改量。
                if(hadCandidate)AdvanceMotionPresentationRevision();
            }
        }

        public void AdvancePresentation(double serverTime, bool frozen)
        {
            if (!double.IsFinite(serverTime)) { InvalidateMotion(true); return; }
            // 无future的原Frozen分支会保留当前暂停Alpha，也必须立即撤掉独立旧pose证明；
            // 它不属于可在暂停期间续租/恢复追赶的运动状态。
            if(frozen && !_presentationFrozen)ClearDormantEndpoints();
            // 已验证future存在时清旧队列；无future仍保留原暂停Alpha、恢复后snap的合同。
            if(frozen && _bufferedFutureCount>0){_presentationFrozen=true;InvalidateMotion(true);return;}
            if (_presentationFrozen && !frozen)
            {
                // Frozen期间网络仍可安装权威数量；恢复时旧运动不能追赶经过的墙钟时间。
                _presentationFrozen = false; _serverPresentationTime = serverTime; InvalidateMotion(true); return;
            }
            if (!_presentationFrozen) _serverPresentationTime = Math.Max(_serverPresentationTime, serverTime);
            _presentationFrozen = frozen;
            // 一帧从99ms跨到109ms时，已到期且完整验证的future应先经过同样的数量/固体
            // 安全校验再切槽。不能先用旧candidate的100ms期限撤掉整个已验证队列。
            // 这个豁免只存在于本次有界切槽过程，不供普通getter借用，也不会给候选续租。
            ValidatePublishedMotion(true);
            ActivateDuePresentationWindows();
            if (!_presentationFrozen && _hasMotion && _serverPresentationTime - _motion.SampleTime > MaximumMotionAgeSeconds)
                InvalidateMotion(true);
        }

        public void ResetConnection(uint epoch)
        {
            _motionConnectionEpoch = epoch; _lastMotionSequence = 0; _lastMotionTick = 0;
            _lastMotionSampleTime = _serverPresentationTime = 0; _presentationFrozen = false;
            _candidateLeaseWindow = 0;
            InvalidateMotion(true);
        }

        private uint MotionSolidVersion() => unchecked(_store.DomainVersion(WorldMaterialDomain.Solid)
            + (_solids != null ? _solids.GeometryVersion : 0u));

        private void ValidatePublishedMotion(bool allowDueWindowTransition=false)
        {
            ValidateDormantEndpoints();
            if (!_hasMotion) return;
            uint liquid = _store.DomainVersion(WorldMaterialDomain.Liquid), solid = MotionSolidVersion();
            bool dueVerifiedWindow=allowDueWindowTransition && !_presentationFrozen && _bufferedFutureCount>0
                && _serverPresentationTime+.0000001>=_bufferedFuture[0].Start;
            if (_presentationHolding && (_presentationFrozen
                || _bufferedFutureCount==0 && !_hasPresentationCandidate || CandidateLeaseExpired() && !dueVerifiedWindow))
            { InvalidateMotion(false); return; }
            // 到期切槽始终重查原安全链，不能因cached版本相同而绕过真实Current/Generation/
            // Interest，也不能先Activate重置Solid版本再检查，掩盖本帧刚生成的墙。
            if (!dueVerifiedWindow && _observedLiquidVersion == liquid && _observedSolidVersion == solid) return;
            // 新的墙/残留支撑也会改变运动合法空间。禁止旧速度在新Collider形成后继续穿入它。
            if (_observedSolidVersion != solid) { InvalidateMotion(false); return; }
            if(_bufferedFutureCount>0)
            {
                _candidatePartialValid=_hasPresentationCandidate && CanHoldPresentationCandidate();
                if(!ValidateBufferedQuantityFacts(_candidatePartialValid)){InvalidateMotion(false);return;}
                _presentationHolding=true;_observedLiquidVersion=liquid;return;
            }
            if (!MatchesQuantityFacts(_motion))
            {
                _candidatePartialValid=CanHoldPresentationCandidate();
                if (_candidatePartialValid) { _presentationHolding = true; _observedLiquidVersion = liquid; return; }
                InvalidateMotion(false); return;
            }
            _candidatePartialValid=false;
            _observedLiquidVersion = liquid;
        }

        private int CandidateRow(Vector3Int cell, byte material)
            => _candidateRows.TryGetValue(new MotionKey(cell, material), out int index) ? index : -1;
        // 1微秒仅处理double相减的边界误差，不把续租或长网络停顿藏进时间容差。
        private bool CandidateLeaseExpired() => _serverPresentationTime - _candidateStagedAt > Math.Min(.1, _motion.Interval)+.000001
            || Time.realtimeSinceStartupAsDouble - _candidateStagedAtRealtime > Math.Min(.1, _motion.Interval)+.000001;
        private bool CanHoldPresentationCandidate()
        {
            MotionHoldChecks++;
            var next = _presentationCandidate;
            var prior=LatestAcceptedPresentationWindow;
            if (!_hasPresentationCandidate) return RejectHold(WorldLiquidMotionHoldFailure.NoCandidate);
            if (_presentationFrozen) return RejectHold(WorldLiquidMotionHoldFailure.Frozen);
            if (next.MaterialTick != prior.MaterialTick + 1) return RejectHold(WorldLiquidMotionHoldFailure.TickGap);
            if (CandidateLeaseExpired()) return RejectHold(WorldLiquidMotionHoldFailure.LeaseExpired);
            if (_serverPresentationTime - next.SampleTime > MaximumMotionAgeSeconds) return RejectHold(WorldLiquidMotionHoldFailure.SampleTooOld);
            // 必须完整衔接上一份Current，而不是因“总量碰巧相等”就借另一团水的运动。
            for (int i = 0; i < prior.RowCount; i++)
            {
                var old = prior.Rows[i]; if (old.CurrentQuantity == 0) continue;
                int index = CandidateRow(old.Cell, old.MaterialId);
                if(index<0)
                {
                    // 新Motion可以省略已不再参与运动的旧行；只有它的真实Current见证仍完整
                    // 成立才保留原曲线。Qty已变的缺行仍拒绝，不删除changed-row coverage门槛。
                    if(CanRetainUnchangedMissingRow(prior,old))continue;
                    return RejectHold(WorldLiquidMotionHoldFailure.MissingContinuationRow,old.Cell,old.MaterialId,old.CurrentQuantity,ReadCurrentQuantity(old));
                }
                var row = next.Rows[index];
                if (row.PreviousQuantity != old.CurrentQuantity) return RejectHold(WorldLiquidMotionHoldFailure.PreviousQuantity,old.Cell,old.MaterialId,old.CurrentQuantity,row.PreviousQuantity);
                float offsetError=(row.PreviousOffset-old.CurrentOffset).sqrMagnitude;
                if (offsetError>.000001f) return RejectHold(WorldLiquidMotionHoldFailure.PreviousOffset,old.Cell,old.MaterialId,poseError:offsetError);
                float velocityError=(row.PreviousVelocity-old.CurrentVelocity).sqrMagnitude;
                if (velocityError>.000001f) return RejectHold(WorldLiquidMotionHoldFailure.PreviousVelocity,old.Cell,old.MaterialId,poseError:velocityError);
            }
            for (int i = 0; i < next.RowCount; i++)
            {
                var row = next.Rows[i];int previousIndex=FindFrameRow(prior,row.Cell,row.MaterialId);
                uint previous=previousIndex>=0?prior.Rows[previousIndex].CurrentQuantity:0;
                if (row.PreviousQuantity != previous && (previousIndex>=0 || !MatchesKnownPrevious(next,i,_candidateKnownPrevious)))
                    return RejectHold(WorldLiquidMotionHoldFailure.UnexpectedPreviousQuantity,row.Cell,row.MaterialId,previous,row.PreviousQuantity);
                if (row.AnchorIndex >= next.AnchorCount) return RejectHold(WorldLiquidMotionHoldFailure.RowAnchor,row.Cell,row.MaterialId);
                var anchor = next.Anchors[row.AnchorIndex];
                if (WorldChunkPayloadCodec.CellKey(WorldMaterialDomain.Liquid, row.Cell, ChunkSize) != anchor.Key) return RejectHold(WorldLiquidMotionHoldFailure.RowAnchor,row.Cell,row.MaterialId);
                // 未知目标不能授予未来事实；此时仅允许保留旧壳。真正发布仍须MatchesQuantityFacts。
                if (_store.TryGetPublishedChunkFact(anchor.Key, out var fact, out bool interested))
                {
                    if (fact.Generation != anchor.Generation) return RejectHold(WorldLiquidMotionHoldFailure.Generation,row.Cell,row.MaterialId,anchor:anchor,fact:fact);
                    if (fact.Revision > anchor.Revision) return RejectHold(WorldLiquidMotionHoldFailure.RevisionAhead,row.Cell,row.MaterialId,anchor:anchor,fact:fact);
                    if (fact.IsResident && !interested) return RejectHold(WorldLiquidMotionHoldFailure.Interest,row.Cell,row.MaterialId,anchor:anchor,fact:fact);
                }
                else if (row.PreviousQuantity != 0) return RejectHold(WorldLiquidMotionHoldFailure.UnknownPreviousSource,row.Cell,row.MaterialId,row.PreviousQuantity,0);
                TryGetQuantity(row.Cell, (MaterialId)row.MaterialId, out uint current);
                if (current != row.PreviousQuantity && current != row.CurrentQuantity) return RejectHold(WorldLiquidMotionHoldFailure.NotPreviousOrCurrent,row.Cell,row.MaterialId,row.CurrentQuantity,current);
            }
            // 仅候选覆盖的row可续播旧曲线，其他row始终读最新Store。不能冻结整份可见世界，
            // 否则同Chunk里另一洼水被收走也会被旧Mesh遮住；也不需要复制全世界的数量账本。
            LastMotionHoldDiagnostic=MotionDiagnostic(next,0,default);
            return true;
        }

        private bool RejectHold(WorldLiquidMotionHoldFailure reason,Vector3Int cell=default,byte material=0,
            uint expected=0,uint actual=0,float poseError=0,WorldLiquidMotionAnchor anchor=default,WorldChunkPayloadHeader fact=default)
        {
            var key=WorldChunkPayloadCodec.CellKey(WorldMaterialDomain.Liquid,cell,ChunkSize);
            var diagnostic=MotionDiagnostic(_presentationCandidate,(byte)reason,key);
            diagnostic.CellX=cell.x;diagnostic.CellY=cell.y;diagnostic.CellZ=cell.z;diagnostic.MaterialId=material;
            diagnostic.ExpectedQuantity=expected;diagnostic.ActualQuantity=actual;diagnostic.PoseDeltaSquared=poseError;
            diagnostic.ExpectedRevision=anchor.Revision;diagnostic.ActualRevision=fact.Revision;
            diagnostic.ExpectedGeneration=anchor.Generation;diagnostic.ActualGeneration=fact.Generation;
            LastMotionHoldDiagnostic=diagnostic;return false;
        }

        private bool MatchesQuantityFacts(WorldLiquidMotionFrame frame,bool recordDiagnostic=false)
        {
            for (int i = 0; i < frame.AnchorCount; i++)
            {
                var anchor = frame.Anchors[i];
                if (anchor.Key.Domain != WorldMaterialDomain.Liquid || anchor.SubscriptionEpoch == 0)
                    return RejectWitness(recordDiagnostic,frame,WorldLiquidMotionWitnessFailure.AnchorFormat,anchor,default);
                if (!_store.TryGetPublishedChunkFact(anchor.Key,out var fact,out bool interested))
                    return RejectWitness(recordDiagnostic,frame,WorldLiquidMotionWitnessFailure.AnchorMissing,anchor,default);
                if (fact.WorldEpoch!=frame.WorldEpoch) return RejectWitness(recordDiagnostic,frame,WorldLiquidMotionWitnessFailure.AnchorWorldEpoch,anchor,fact);
                if (fact.Generation!=anchor.Generation) return RejectWitness(recordDiagnostic,frame,WorldLiquidMotionWitnessFailure.AnchorGeneration,anchor,fact);
                if (fact.Revision<anchor.Revision) return RejectWitness(recordDiagnostic,frame,WorldLiquidMotionWitnessFailure.AnchorRevisionBehind,anchor,fact);
                if (fact.IsResident!=anchor.IsResident) return RejectWitness(recordDiagnostic,frame,WorldLiquidMotionWitnessFailure.AnchorResidency,anchor,fact);
                if (fact.IsResident && !interested) return RejectWitness(recordDiagnostic,frame,WorldLiquidMotionWitnessFailure.AnchorInterest,anchor,fact);
            }
            for (int i = 0; i < frame.RowCount; i++)
            {
                var row = frame.Rows[i];
                if (row.AnchorIndex<0 || row.AnchorIndex>=frame.AnchorCount) return RejectRowWitness(recordDiagnostic,frame,WorldLiquidMotionWitnessFailure.RowAnchor,row);
                if (row.PreviousQuantity>CellCapacityGmu || row.CurrentQuantity>CellCapacityGmu) return RejectRowWitness(recordDiagnostic,frame,WorldLiquidMotionWitnessFailure.RowCapacity,row);
                if (GetDensityGroup((MaterialId)row.MaterialId)<0) return RejectRowWitness(recordDiagnostic,frame,WorldLiquidMotionWitnessFailure.RowMaterial,row);
                if (WorldChunkPayloadCodec.CellKey(WorldMaterialDomain.Liquid,row.Cell,ChunkSize)!=frame.Anchors[row.AnchorIndex].Key) return RejectRowWitness(recordDiagnostic,frame,WorldLiquidMotionWitnessFailure.RowCellKey,row);
                TryGetQuantity(row.Cell, (MaterialId)row.MaterialId, out uint current);
                if (current != row.CurrentQuantity) return RejectRowWitness(recordDiagnostic,frame,WorldLiquidMotionWitnessFailure.CurrentQuantity,row,current);
                ulong previousTotal = row.PreviousQuantity;
                for (int j = 0; j < i; j++)
                {
                    var prior = frame.Rows[j]; if (prior.Cell != row.Cell) continue;
                    if (prior.MaterialId == row.MaterialId) return RejectRowWitness(recordDiagnostic,frame,WorldLiquidMotionWitnessFailure.DuplicateMaterial,row,current);
                    previousTotal += prior.PreviousQuantity;
                }
                if (previousTotal > (ulong)CellCapacityGmu) return RejectRowWitness(recordDiagnostic,frame,WorldLiquidMotionWitnessFailure.PreviousCapacity,row,(uint)previousTotal);
            }
            return true;
        }

        private static WorldLiquidMotionValidationDiagnostic MotionDiagnostic(WorldLiquidMotionFrame frame,byte reason,WorldChunkKey key)
            =>new WorldLiquidMotionValidationDiagnostic{Reason=reason,Sequence=frame.Sequence,MaterialTick=frame.MaterialTick,
                ChunkX=key.X,ChunkY=key.Y,ChunkZ=key.Z,AnchorCount=frame.AnchorCount,RowCount=frame.RowCount};
        private bool RejectWitness(bool record,WorldLiquidMotionFrame frame,WorldLiquidMotionWitnessFailure reason,
            WorldLiquidMotionAnchor anchor,WorldChunkPayloadHeader fact)
        {
            // ValidatePublishedMotion也调用同一校验，但record=false，不能覆盖最近一次TryPublish的拒因。
            if(record)
            {
                var diagnostic=MotionDiagnostic(frame,(byte)reason,anchor.Key);
                diagnostic.ExpectedRevision=anchor.Revision;diagnostic.ActualRevision=fact.Revision;
                diagnostic.ExpectedGeneration=anchor.Generation;diagnostic.ActualGeneration=fact.Generation;
                LastMotionWitnessDiagnostic=diagnostic;
            }
            return false;
        }
        private bool RejectRowWitness(bool record,WorldLiquidMotionFrame frame,WorldLiquidMotionWitnessFailure reason,
            WorldLiquidMotionSample row,uint actual=0)
        {
            if(record)
            {
                var key=WorldChunkPayloadCodec.CellKey(WorldMaterialDomain.Liquid,row.Cell,ChunkSize);
                var diagnostic=MotionDiagnostic(frame,(byte)reason,key);
                diagnostic.CellX=row.Cell.x;diagnostic.CellY=row.Cell.y;diagnostic.CellZ=row.Cell.z;diagnostic.MaterialId=row.MaterialId;
                diagnostic.ExpectedQuantity=reason==WorldLiquidMotionWitnessFailure.PreviousCapacity?(uint)CellCapacityGmu:row.CurrentQuantity;
                diagnostic.ActualQuantity=actual;
                if(row.AnchorIndex>=0 && row.AnchorIndex<frame.AnchorCount)
                {
                    var anchor=frame.Anchors[row.AnchorIndex];diagnostic.ExpectedRevision=anchor.Revision;diagnostic.ExpectedGeneration=anchor.Generation;
                    if(_store.TryGetPublishedChunkFact(anchor.Key,out var fact,out _))
                    {diagnostic.ActualRevision=fact.Revision;diagnostic.ActualGeneration=fact.Generation;}
                }
                LastMotionWitnessDiagnostic=diagnostic;
            }
            return false;
        }

        private void InvalidateMotion(bool resetTimeline)
        {
            bool changed = _hasMotion;
            // 一条活动曲线因覆盖不全/局部代次变化而失效，并不表示其它格已完整witness的
            // Current pose也失效。按真实数量/身份只删坏键，避免恢复后每窗再丢一批独立证据。
            // 时钟/连接重置仍清全；新Solid由ValidateDormantEndpoints的独立版本检查清全，
            // Frozen入口也已显式清全。这里不保留active/future/candidate的任何旧显示权限。
            if(resetTimeline)ClearDormantEndpoints();else ValidateDormantEndpoints();
            _hasMotion = false; _motionRows.Clear(); _motionChangedCount = 0;
            ClearBufferedPresentationWindows();
            _presentationHolding = _hasPresentationCandidate = false;
            _candidatePartialValid=false;
            Array.Clear(_candidateKnownPrevious,0,_candidateKnownPrevious.Length);
            Array.Clear(_activeKnownPrevious,0,_activeKnownPrevious.Length);
            if (resetTimeline) _hasMotionTimeline = false;
            if (changed || resetTimeline)
            { AdvanceMotionPresentationRevision(); unchecked { _motionInvalidationRevision++; } }
        }
        private void AdvanceMotionPresentationRevision() { unchecked { _motionPresentationRevision++; } }

        private bool TryReadMotion(Vector3Int cell, MaterialId material, bool previous, out LiquidCellPresentationSample sample)
        {
            ValidatePublishedMotion(); sample = default;
            if (!_hasMotion) return false;
            return TryReadBufferedPresentation(cell,material,previous,out sample);
        }
        private int CopyMotionChangedCells(Vector3Int[] destination)
        {
            ValidatePublishedMotion();
            // 源Chunk已经装了tombstone时，合法Previous仍可暂时可见，却不再出现在resident枚举。
            // 每次只重建最多512行的固定集合；仅CanHold完整成立的candidate旧量可补充coverage，
            // 不将任意未发布Current或未知Previous当成数量Chunk，也不改变Store/ACK安装边界。
            if(_hasMotion)RebuildBufferedChangedCells(true);
            int count = Math.Min(_motionChangedCount, destination.Length);
            Array.Copy(_motionChangedCells, destination, count); return count;
        }
    }
}
