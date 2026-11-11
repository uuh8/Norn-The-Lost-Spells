using System;
using System.Collections;
using Game.Core;
using Game.Networking;
using Game.NetworkProtocol;
using UnityEngine;
using UnityEngine.Networking;

namespace Game.Multiplayer
{
    public sealed partial class RoomInstanceBootstrap
    {
        private bool _completionStarted, _completionCapturing, _completionSending, _completionStopped, _completionCompleted;
        private string _completionBody, _completionError;
        private int _completionAttempts;
        private double _completionNextAttempt;
        private UnityWebRequest _completionRequest;

        /// <summary>
        /// 只读已确认的个人最终收据，不看连接数或血量。原Entry每一席必须逐一对应，不能用数量相等替代身份。
        /// 热路径只读固定4槽；不会拷贝背包、构建字典或查询业务服。
        /// </summary>
        internal bool HasConfirmedAllMemberExits
        {
            get
            {
                if (!Ready || _launch == null || Inventory == null || Inventory.MemberCount < 1 || Inventory.MemberCount > 4
                    || Inventory.ActiveMemberCount != 0) return false;
                for (int i = 0; i < Inventory.MemberCount; i++)
                {
                    if (!Inventory.TryGetFrozenMember(i, out var entry) || entry.ActorId < 1 || entry.ActorId > 4
                        || entry.Profile == null || !Inventory.IsMemberExited(entry.ActorId)) return false;
                    var pending = _memberExits[(int)entry.ActorId - 1];
                    var receipt = pending != null ? pending.Receipt : null;
                    if (pending == null || pending.Phase != OnlineRunExitPhase.Committed || pending.Sending
                        || receipt == null || !receipt.Exited || receipt.PlayerId != entry.Profile.PlayerId
                        || receipt.ActorId != entry.ActorId || receipt.InstanceId != _launch.InstanceId
                        || receipt.RunId != _launch.RunId || receipt.RoomEpoch != _launch.RoomEpoch
                        || receipt.SettlementId != pending.Command || receipt.ConnectionEpoch == 0
                        || pending.Losses == null) return false;
                }
                return true;
            }
        }

        /// <summary>
        /// 先排完原固定候选，再在原材料提交边界捕获唯一终态Head。完整值读回后才发Complete。
        /// 未决地面/托管不自动返还、退休或销毁；旧IdleShutdown仍存在，失败必须在state明确留档。
        /// </summary>
        private void PumpInstanceCompletion()
        {
            if (_completionCompleted || _completionStopped || !HasConfirmedAllMemberExits) return;
            _completionStarted = true;
            if (!_checkpointProviderSupported || !_checkpointPublishing || _checkpointDeliveryStopped)
            { CompletionBlocked("InstanceCompletionCheckpointUnavailable"); return; }
            if (_checkpointPending.HasPending || _checkpointSending || _checkpointAwaitingCapture || _completionCapturing) return;
            if (!_checkpointPending.LastConfirmedTerminal)
            {
                if (_checkpointNextSequence == ulong.MaxValue)
                { CompletionBlocked("CheckpointSequenceExhausted"); return; }
                var world = OnlineWorldBootstrap.Active;
                _completionCapturing = world != null && world.RequestTerminalRunCheckpoint(_launch,
                    Guid.NewGuid().ToString("N"), ++_checkpointNextSequence,
                    _checkpointPending.LastAcknowledgedRetirementSequence, _checkpointCaptured);
                _checkpointAwaitingCapture = _completionCapturing;
                if (!_completionCapturing) CompletionBlocked("InstanceCompletionCaptureUnavailable");
                return;
            }
            if (_completionSending || Time.realtimeSinceStartupAsDouble < _completionNextAttempt) return;
            if (_completionAttempts >= 3) { CompletionBlocked("InstanceCompletionUnconfirmed"); return; }
            if (_completionBody == null)
                _completionBody = JsonUtility.ToJson(new InstanceCompleteRequest { RunId = _launch.RunId,
                    RoomEpoch = _launch.RoomEpoch, CheckpointId = _checkpointPending.LastConfirmedCheckpointId,
                    Sequence = _checkpointPending.LastConfirmedSequence, PayloadHash = _checkpointPending.LastConfirmedPayloadHash });
            _completionSending = true; StartCoroutine(DeliverInstanceCompletion());
        }

        private IEnumerator DeliverInstanceCompletion()
        {
            try
            {
                _completionAttempts++;
                _completionRequest = InstanceControlHttp.Complete(_launch, _secret, _completionBody);
                yield return _completionRequest.SendWebRequest();
                InstanceCompleteReply reply = null;
                if (_completionRequest.result == UnityWebRequest.Result.Success)
                    try { reply = JsonUtility.FromJson<InstanceCompleteReply>(_completionRequest.downloadHandler.text); }
                    catch (ArgumentException) { reply = null; }
                if (reply != null && reply.Completed && reply.InstanceId == _launch.InstanceId && reply.RunId == _launch.RunId
                    && reply.RoomEpoch == _launch.RoomEpoch && reply.CheckpointId == _checkpointPending.LastConfirmedCheckpointId
                    && reply.Sequence == _checkpointPending.LastConfirmedSequence && reply.PayloadHash == _checkpointPending.LastConfirmedPayloadHash)
                {
                    _completionCompleted = true; _completionError = null;
                    WriteState(); Application.Quit(); yield break;
                }
                if (_completionRequest.responseCode == 400 || _completionRequest.responseCode == 401
                    || _completionRequest.responseCode == 403 || _completionRequest.responseCode == 409 || _completionRequest.responseCode == 503)
                {
                    string code = null;
                    try { code = JsonUtility.FromJson<OnlineErrorReply>(_completionRequest.downloadHandler.text)?.Code; }
                    catch (ArgumentException) { }
                    CompletionBlocked(string.IsNullOrEmpty(code) ? "InstanceCompletionRejected" : code);
                }
                // 暂时网络失败最多重试原正文三次。成功响应丢失后，同一Head请求可幂等查回Completed。
            }
            finally
            {
                _completionRequest?.Dispose(); _completionRequest = null; _completionSending = false;
                _completionNextAttempt = Time.realtimeSinceStartupAsDouble + (_launch != null ? _launch.HeartbeatSeconds : 15);
            }
        }
        private void CompletionBlocked(string error)
        {
            _completionStopped = true; _completionError = error;
            GameLog.Warn("实例正常结束仍未确认；不更改地面或托管资产去向：" + error, "OnlineRooms");
            WriteState();
        }
        private void StopInstanceCompletion()
        { _completionRequest?.Abort(); _completionRequest?.Dispose(); _completionRequest = null; _completionSending = false; }
    }
}
