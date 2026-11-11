using System;
using Game.Core;
using Shared = Game.NetworkProtocol;

namespace Game.Multiplayer
{
    public sealed partial class OnlineWorldBootstrap
    {
        private IOnlineItemRetirementJournal _runCheckpointJournal;
        private Action<CapturedRunCheckpoint, string> _checkpointCaptureCallback;
        private Shared.InstanceLaunchDescriptor _checkpointCaptureLaunch;
        private string _checkpointCaptureId;
        private ulong _checkpointCaptureSequence, _checkpointRetirementAck;
        private bool _checkpointCaptureTerminal;

        // 生命周期属于整个World/Run，不属于会因连接装配而重建的Combat适配器。
        internal IOnlineItemRetirementJournal RunCheckpointJournal
            => _runCheckpointJournal ?? (_runCheckpointJournal = new OnlineRunItemRetirementJournal());

        public bool RequestRunCheckpoint(Shared.InstanceLaunchDescriptor launch, string id, ulong sequence,
            ulong retirementAck, Action<CapturedRunCheckpoint, string> completed)
            => RequestRunCheckpointCore(launch, id, sequence, retirementAck, completed, false);

        /// <summary>只允许Instance全员最终收据已确认后的离散捕获；仍在原材料提交Tick取完整事实。</summary>
        internal bool RequestTerminalRunCheckpoint(Shared.InstanceLaunchDescriptor launch, string id, ulong sequence,
            ulong retirementAck, Action<CapturedRunCheckpoint, string> completed)
            => RequestRunCheckpointCore(launch, id, sequence, retirementAck, completed, true);

        private bool RequestRunCheckpointCore(Shared.InstanceLaunchDescriptor launch, string id, ulong sequence,
            ulong retirementAck, Action<CapturedRunCheckpoint, string> completed, bool terminal)
        {
            if (!IsAuthority || !CanAdvance || _checkpointCaptureCallback != null || launch == null
                || launch.RunId != _runId || launch.GameplayConfigHash != _combatDefinition.GameplayConfigHash
                || terminal && (_instance == null || !_instance.HasConfirmedAllMemberExits)
                || sequence == 0 || completed == null || !Guid.TryParseExact(id, "N", out _)) return false;
            _checkpointCaptureLaunch = launch; _checkpointCaptureId = id;
            _checkpointCaptureSequence = sequence; _checkpointRetirementAck = retirementAck;
            _checkpointCaptureTerminal = terminal;
            _checkpointCaptureCallback = completed; return true;
        }
        private void CompleteRequestedRunCheckpoint(uint tick)
        {
            var completed = _checkpointCaptureCallback;
            if (completed == null || !CanAdvance) return;
            var launch = _checkpointCaptureLaunch; string id = _checkpointCaptureId;
            ulong sequence = _checkpointCaptureSequence, ack = _checkpointRetirementAck;
            bool terminal = _checkpointCaptureTerminal;
            ClearCheckpointRequest();
            // 当前方法只在原10Hz正式Capture成功后同步执行。任何源失败不发布半份候选；
            // 不在HTTP/yield之后再补Owner，也不把上一材料Tick与之后库存变化拼接。
            if (_combat == null || _materialReplication == null
                || !_materialReplication.TryCaptureCheckpoint(MaterialTransferWorldId, out var material)
                || !RunCheckpointJournal.TryCapture(out var retired))
            { completed(null, "CheckpointCommittedFactsUnavailable"); return; }
            if (!_combat.TryCaptureRunCheckpoint(launch, id, sequence, ack, tick, material, retired,
                RunCheckpointJournal.Sequence, out var captured, out var error, terminal))
            { completed(null, error ?? "CheckpointOwnedFactsBusyOrUnavailable"); return; }
            completed(captured, null);
        }
        private void ClearCheckpointRequest()
        {
            _checkpointCaptureCallback = null; _checkpointCaptureLaunch = null; _checkpointCaptureId = null;
            _checkpointCaptureSequence = _checkpointRetirementAck = 0;
            _checkpointCaptureTerminal = false;
        }
        private void CancelRequestedRunCheckpoint()
        {
            var completed = _checkpointCaptureCallback; ClearCheckpointRequest();
            completed?.Invoke(null, "CheckpointWorldStopped");
        }
    }
}
