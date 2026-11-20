using System;

namespace Game.Core
{
    public enum WorldLiquidMotionAssemblyFailure : byte
    { None, InvalidHeader, WrongIdentity, StaleSequence, PendingBusy, NoPending, InvalidPage, ConflictingPage, Checksum, Payload, Timeout, NotReady }

    public readonly struct WorldLiquidMotionPageHeader : IEquatable<WorldLiquidMotionPageHeader>
    {
        public readonly ulong WorldEpoch, Sequence;
        public readonly uint ConnectionEpoch, MaterialTick, Checksum;
        public readonly int WordCount, PageCount;
        public WorldLiquidMotionPageHeader(ulong worldEpoch, uint connectionEpoch, ulong sequence,
            uint materialTick, int wordCount, int pageCount, uint checksum)
        { WorldEpoch = worldEpoch; ConnectionEpoch = connectionEpoch; Sequence = sequence; MaterialTick = materialTick; WordCount = wordCount; PageCount = pageCount; Checksum = checksum; }
        public bool Equals(WorldLiquidMotionPageHeader other) => WorldEpoch == other.WorldEpoch
            && ConnectionEpoch == other.ConnectionEpoch && Sequence == other.Sequence
            && MaterialTick == other.MaterialTick && WordCount == other.WordCount
            && PageCount == other.PageCount && Checksum == other.Checksum;
        public override bool Equals(object other) => other is WorldLiquidMotionPageHeader header && Equals(header);
        public override int GetHashCode() => WorldEpoch.GetHashCode() ^ Sequence.GetHashCode();
    }

    /// <summary>
    /// 一份已发布帧加一份候选；完整结构验证与实际数量验证分成 Ready / Commit 两个边界。
    /// 页头来自已认证Channel，body完整后仍不授权显示：调用方先让Sink校验全部数量/订阅依赖，
    /// 成功才CommitCandidate。拒绝高Sequence候选不会污染AcceptedSequence，下一合法低序号仍可到达。
    /// 只在所属Unity主线程串行使用；不调用外部回调，也不替数量通道发送ACK或Resync。
    /// </summary>
    public sealed class WorldLiquidMotionAssembler
    {
        public const int PageWords = 160;
        private const int MaximumPages = (WorldLiquidMotionCodec.MaxWords + PageWords - 1) / PageWords;
        private readonly uint[] _words = new uint[WorldLiquidMotionCodec.MaxWords];
        private readonly bool[] _received = new bool[MaximumPages];
        private WorldLiquidMotionFrame _published = new WorldLiquidMotionFrame();
        private WorldLiquidMotionFrame _candidate = new WorldLiquidMotionFrame();
        private readonly ulong _worldEpoch;
        private readonly double _timeoutSeconds;
        private WorldLiquidMotionPageHeader _pending;
        private int _receivedCount;
        private double _startedAt;
        public bool HasPending { get; private set; }
        public bool HasCompleteCandidate { get; private set; }
        public bool HasPublished { get; private set; }
        public ulong AcceptedSequence { get; private set; }
        public uint AcceptedMaterialTick { get; private set; }
        public uint ConnectionEpoch { get; private set; }
        public WorldLiquidMotionAssemblyFailure LastFailure { get; private set; }
        public WorldLiquidMotionAssembler(ulong worldEpoch, uint connectionEpoch = 0, double timeoutSeconds = .5)
        {
            if (worldEpoch == 0 || !double.IsFinite(timeoutSeconds) || timeoutSeconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(worldEpoch));
            _worldEpoch = worldEpoch; ConnectionEpoch = connectionEpoch; _timeoutSeconds = timeoutSeconds;
        }

        public bool TryBegin(in WorldLiquidMotionPageHeader header, double now)
        {
            // 身份、shape先检查；非法高Sequence不能取消另一份正在接收的合法候选。
            if (header.WorldEpoch != _worldEpoch || ConnectionEpoch == 0 || header.ConnectionEpoch != ConnectionEpoch)
                return Reject(WorldLiquidMotionAssemblyFailure.WrongIdentity);
            if (!double.IsFinite(now) || now < 0 || header.Sequence == 0 || header.MaterialTick == 0
                || header.WordCount < WorldLiquidMotionCodec.PrefixWords || header.WordCount > WorldLiquidMotionCodec.MaxWords
                || header.PageCount != 1 + (header.WordCount - 1) / PageWords)
                return Reject(WorldLiquidMotionAssemblyFailure.InvalidHeader);
            if (header.Sequence <= AcceptedSequence || HasPublished && header.MaterialTick < AcceptedMaterialTick)
                return Reject(WorldLiquidMotionAssemblyFailure.StaleSequence);
            if (HasPending)
            {
                if (ExpirePending(now)) return false;
                // 同一候选重发幂等，但不刷新开始时间。更大序号也不能隐式抢占未验证的完整候选；
                // 上层决定合并最新表现时必须显式Cancel，不借此遗忘已发布的版本水位。
                return _pending.Equals(header) ? Accept() : Reject(WorldLiquidMotionAssemblyFailure.PendingBusy);
            }
            Array.Clear(_received, 0, header.PageCount);
            _pending = header; _startedAt = now; _receivedCount = 0;
            HasPending = true; HasCompleteCandidate = false; return Accept();
        }

        public bool TryReceivePage(in WorldLiquidMotionPageHeader header, int pageIndex, uint[] words, int count, double now)
        {
            if (!HasPending) return Reject(WorldLiquidMotionAssemblyFailure.NoPending);
            if (!_pending.Equals(header)) return Reject(WorldLiquidMotionAssemblyFailure.WrongIdentity);
            if (!double.IsFinite(now) || now < 0) return Reject(WorldLiquidMotionAssemblyFailure.InvalidPage);
            if (ExpirePending(now)) return false;
            if (pageIndex < 0 || pageIndex >= _pending.PageCount || words == null || count < 0 || count > words.Length)
                return Reject(WorldLiquidMotionAssemblyFailure.InvalidPage);
            int offset = pageIndex * PageWords, expected = Math.Min(PageWords, _pending.WordCount - offset);
            if (count != expected) return Reject(WorldLiquidMotionAssemblyFailure.InvalidPage);
            if (_received[pageIndex])
            {
                for (int i = 0; i < count; i++)
                    if (_words[offset + i] != words[i])
                    { CancelPending(); return Reject(WorldLiquidMotionAssemblyFailure.ConflictingPage); }
                return Accept();
            }
            Array.Copy(words, 0, _words, offset, count);
            _received[pageIndex] = true; _receivedCount++;
            if (_receivedCount != _pending.PageCount) return Accept();
            if (WorldLiquidMotionCodec.ComputeChecksum(_words, _pending.WordCount) != _pending.Checksum)
            { CancelPending(); return Reject(WorldLiquidMotionAssemblyFailure.Checksum); }
            if (!WorldLiquidMotionCodec.TryDecode(_words, _pending.WordCount, _candidate))
            { CancelPending(); return Reject(WorldLiquidMotionAssemblyFailure.Payload); }
            _candidate.WorldEpoch = _pending.WorldEpoch; _candidate.ConnectionEpoch = _pending.ConnectionEpoch;
            _candidate.Sequence = _pending.Sequence; _candidate.MaterialTick = _pending.MaterialTick;
            HasCompleteCandidate = true; return Accept();
        }

        /// <summary>
        /// 只借用到下一次Commit/Reject/Reset；调用方只能读，Sink若要跨帧保留必须CopyFrom。
        /// 这里不把结构合法误称为数量依赖合法，也不提前更新AcceptedSequence。
        /// </summary>
        public bool TryGetCompletedCandidate(out WorldLiquidMotionFrame frame)
        {
            frame = HasCompleteCandidate ? _candidate : null;
            return HasCompleteCandidate;
        }

        /// <summary>仅供刚刚同步完成Sink.TryPublish成功的可信Coordinator调用；中间不得yield或推进数量。</summary>
        public bool CommitCandidate()
        {
            if (!HasPending || !HasCompleteCandidate) return Reject(WorldLiquidMotionAssemblyFailure.NotReady);
            var previous = _published; _published = _candidate; _candidate = previous;
            AcceptedSequence = _pending.Sequence; AcceptedMaterialTick = _pending.MaterialTick;
            HasPublished = true; CancelPending(); return Accept();
        }

        public void RejectCandidate() => CancelPending();
        public void CancelPending()
        {
            HasPending = HasCompleteCandidate = false; _pending = default; _receivedCount = 0;
            // 数组保留供下一候选复用；所有读取入口都以状态和有效前缀为界，不需要逐帧清全数组。
        }

        /// <summary>从第一份合法Begin计时，缺页、重复包和依赖等待都不能无限延长TTL。</summary>
        public bool ExpirePending(double now)
        {
            if (!HasPending || !double.IsFinite(now)) return false;
            if (now >= _startedAt && now - _startedAt < _timeoutSeconds) return false;
            CancelPending(); LastFailure = WorldLiquidMotionAssemblyFailure.Timeout; return true;
        }

        public bool ResetConnection(uint epoch)
        {
            if (epoch == 0 || epoch < ConnectionEpoch) return Reject(WorldLiquidMotionAssemblyFailure.WrongIdentity);
            if (epoch == ConnectionEpoch) return Accept();
            // 运动不是持久数量。重连须从新订阅/真实Baseline重新关联，不能接续旧连接的运动轨迹。
            CancelPending(); ConnectionEpoch = epoch; HasPublished = false;
            AcceptedSequence = 0; AcceptedMaterialTick = 0; return Accept();
        }

        public bool TryCopyPublished(WorldLiquidMotionFrame destination)
        {
            if (!HasPublished || destination == null) return false;
            destination.CopyFrom(_published); return true;
        }

        private bool Accept() { LastFailure = WorldLiquidMotionAssemblyFailure.None; return true; }
        private bool Reject(WorldLiquidMotionAssemblyFailure failure) { LastFailure = failure; return false; }
    }
}
