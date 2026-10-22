namespace Game.ElementField
{
    internal enum FluidChunkTransferPhase : byte
    {
        Idle = 0,
        WaitingForReadback = 1,
        ReadbackCompleted = 2,
    }

    internal enum FluidChunkTransferKind : byte
    {
        None = 0,
        Archive = 1,
        Restore = 2,
    }

    /// <summary>
    /// AsyncGPUReadback callback 只登记结果，真正的 Builder、Store 和 GPU Commit 在下一次 Update 执行。
    /// 这样 callback 不修改世界权威，也让成功、失败和重复回调遵守同一套 exactly-once 规则。
    /// </summary>
    internal sealed class FluidChunkTransferState
    {
        private uint _transactionId;
        private bool _readbackHasError;

        public FluidChunkTransferPhase Phase { get; private set; }
        public FluidChunkTransferKind Kind { get; private set; }
        public bool IsInFlight => Phase != FluidChunkTransferPhase.Idle;
        public uint TransactionId => _transactionId;

        public bool TryBegin(uint transactionId)
        {
            return TryBegin(transactionId, FluidChunkTransferKind.Archive);
        }

        public bool TryBegin(uint transactionId, FluidChunkTransferKind kind)
        {
            if (Phase != FluidChunkTransferPhase.Idle || transactionId == 0u
                || kind == FluidChunkTransferKind.None)
                return false;
            _transactionId = transactionId;
            Kind = kind;
            _readbackHasError = false;
            Phase = FluidChunkTransferPhase.WaitingForReadback;
            return true;
        }

        public bool RecordCompletion(uint transactionId, bool hasError)
        {
            if (Phase != FluidChunkTransferPhase.WaitingForReadback
                || transactionId != _transactionId)
                return false;
            _readbackHasError = hasError;
            Phase = FluidChunkTransferPhase.ReadbackCompleted;
            return true;
        }

        public bool TryConsumeCompletion(out uint transactionId, out bool hasError)
        {
            if (Phase != FluidChunkTransferPhase.ReadbackCompleted)
            {
                transactionId = 0u;
                hasError = false;
                return false;
            }
            transactionId = _transactionId;
            hasError = _readbackHasError;
            return true;
        }

        public bool Finish(uint transactionId)
        {
            if (Phase != FluidChunkTransferPhase.ReadbackCompleted
                || transactionId != _transactionId)
                return false;
            _transactionId = 0u;
            Kind = FluidChunkTransferKind.None;
            _readbackHasError = false;
            Phase = FluidChunkTransferPhase.Idle;
            return true;
        }
    }
}
