using System;
using System.Collections;
using Game.Core;
using Game.Networking;
using Game.NetworkProtocol;
using Mirror;
using UnityEngine;
using UnityEngine.Networking;

namespace Game.Multiplayer
{
    public sealed partial class RoomInstanceBootstrap
    {
        private readonly RunCheckpointPending _checkpointPending = new RunCheckpointPending();
        private UnityWebRequest _checkpointRequest;
        private Action<CapturedRunCheckpoint, string> _checkpointCaptured;
        private bool _checkpointPublishing, _checkpointAwaitingCapture, _checkpointSending, _checkpointDeliveryStopped;
        private bool _checkpointProviderSupported;
        private double _checkpointNextCapture, _checkpointNextDelivery;
        private ulong _checkpointNextSequence;
        private int _checkpointAttempts, _checkpointRejectedCandidates;
        private string _checkpointError;
        public ulong LastConfirmedCheckpointSequence => _checkpointPending.LastConfirmedSequence;
        public string PendingCheckpointId => _checkpointPending.CheckpointId;
        public string CheckpointFailure => _checkpointError;

        private void StartCheckpointPublisher()
        {
            if (_checkpointPublishing || !Ready || _launch == null) return;
            // 旧业务服仍可按原合同启动房间；仅不向它投递不支持的完整Schema4（余额与世界Coin事实）。
            // 匹配部署之前显式记录检查点未就绪，不假称已保存，也不实施被拒绝的Ready阻断门。
            if (!_checkpointProviderSupported)
            { CheckpointError("CheckpointCapabilityUnavailable", true); return; }
            _checkpointPublishing = true; _checkpointCaptured = OnCheckpointCaptured;
            // 沿已有Heartbeat频率安排离散捕获，不增加Gameplay Tick或每帧序列化。
            _checkpointNextCapture = Time.realtimeSinceStartupAsDouble + _launch.HeartbeatSeconds;
        }
        private void PumpCheckpointPublisher()
        {
            if (!_checkpointPublishing || _checkpointDeliveryStopped || !NetworkServer.active || !Ready || _checkpointSending) return;
            double now = Time.realtimeSinceStartupAsDouble;
            if (_checkpointPending.HasPending)
            {
                if (now >= _checkpointNextDelivery)
                { _checkpointSending = true; StartCoroutine(DeliverCheckpointOnce()); }
                return;
            }
            if (_completionStarted) return; // 全员终态后只由专属入口请求最后一份Head，不再造普通空Members候选。
            if (_checkpointAwaitingCapture || now < _checkpointNextCapture) return;
            _checkpointNextCapture = now + _launch.HeartbeatSeconds;
            var world = OnlineWorldBootstrap.Active;
            if (world == null || !world.CanAdvance) return;
            if (_checkpointNextSequence == ulong.MaxValue)
            { CheckpointError("CheckpointSequenceExhausted", true); return; }
            string id = Guid.NewGuid().ToString("N"); ulong sequence = ++_checkpointNextSequence;
            _checkpointAwaitingCapture = world.RequestRunCheckpoint(_launch, id, sequence,
                _checkpointPending.LastAcknowledgedRetirementSequence, _checkpointCaptured);
        }
        private void OnCheckpointCaptured(CapturedRunCheckpoint captured, string error)
        {
            _checkpointAwaitingCapture = false;
            bool terminal = _completionCapturing; _completionCapturing = false;
            if (!_checkpointPublishing) return;
            if (captured == null)
            {
                CheckpointError(error ?? "CheckpointCaptureUnavailable", false);
                if (terminal) CompletionBlocked(error ?? "InstanceCompletionCaptureUnavailable");
                return;
            }
            if (!_checkpointPending.TryPrepare(captured, out var failure))
            { CheckpointError(failure ?? "CheckpointEncodingRejected", true); return; }
            _checkpointAttempts = 0; _checkpointNextDelivery = Time.realtimeSinceStartupAsDouble;
            // 最后完整事实一旦固定就停新Gameplay写入；Heartbeat不能重新打开authorityReady。
            // 保存失败也不重新模拟篡改原候选。此处不删除任何世界对象或资产。
            if (captured.TerminalCapture && _manager != null) _manager.SetAuthorityReady(false);
        }
        private IEnumerator DeliverCheckpointOnce()
        {
            long postStatus = 0;
            string postDiagnostic = null;
            try
            {
                if (!_checkpointPublishing || !_checkpointPending.HasPending) yield break;
                _checkpointAttempts++;
                if (!CreateCheckpointRequest(0)) yield break;
                yield return _checkpointRequest.SendWebRequest();
                if (!_checkpointPublishing || _checkpointRequest == null) yield break;
                postStatus = _checkpointRequest.responseCode;
                bool recorded = _checkpointRequest.result == UnityWebRequest.Result.Success
                    && _checkpointPending.ObserveReceipt(ParseCheckpointResponse<OnlineRunAssetCheckpointReceipt>());
                // 诊断必须在Dispose前提取，但绝不把HTTP正文或未知Code带入日志/state。
                // 下载量先限界，避免为了一个错误码再把任意大响应解码成string；失败判定仍只由原pending规则决定。
                if (!recorded && postStatus >= 400 && postStatus < 500)
                {
                    string body = _checkpointRequest.downloadHandler != null && _checkpointRequest.downloadedBytes <= 1024
                        ? _checkpointRequest.downloadHandler.text : null;
                    postDiagnostic = CheckpointRejectionDiagnostic(postStatus, body);
                }
                ReleaseCheckpointRequest();
                if (!recorded)
                {
                    // 即使POST超时，也不更换正文或ID；先查询已提交事实，避免丢响应造成重复保存/误清水位。
                    if (!CreateCheckpointRequest(1)) yield break;
                    yield return _checkpointRequest.SendWebRequest();
                    if (!_checkpointPublishing || _checkpointRequest == null) yield break;
                    long queryStatus = _checkpointRequest.responseCode;
                    recorded = _checkpointRequest.result == UnityWebRequest.Result.Success
                        && _checkpointPending.ObserveReceipt(ParseCheckpointResponse<OnlineRunAssetCheckpointReceipt>());
                    ReleaseCheckpointRequest();
                    if (!recorded)
                    {
                        if (_checkpointPending.ResolveRejectedPost(postStatus, queryStatus))
                        {
                            _checkpointAttempts = 0; _checkpointRejectedCandidates++;
                            CheckpointError(postDiagnostic ?? "CheckpointCandidateRejected", _checkpointRejectedCandidates >= 3);
                        }
                        else CheckpointError("CheckpointReceiptUnconfirmed", false);
                        yield break;
                    }
                }
                // Receipt只证明写入存在。再读完整Head按全部规范值核对，才能确认捕获分区没有在存取中漏失。
                if (!CreateCheckpointRequest(2)) yield break;
                yield return _checkpointRequest.SendWebRequest();
                if (!_checkpointPublishing || _checkpointRequest == null) yield break;
                bool matched = _checkpointRequest.result == UnityWebRequest.Result.Success
                    && _checkpointPending.ConfirmReadback(ParseCheckpointResponse<OnlineRunAssetCheckpointHeadReply>());
                ReleaseCheckpointRequest();
                if (matched)
                {
                    _checkpointAttempts = _checkpointRejectedCandidates = 0; _checkpointError = null;
                    WriteState();
                }
                else CheckpointError("CheckpointFullReadbackUnconfirmed", false);
            }
            finally
            {
                ReleaseCheckpointRequest(); _checkpointSending = false;
                double next = Time.realtimeSinceStartupAsDouble + (_launch != null ? _launch.HeartbeatSeconds : 15);
                _checkpointNextDelivery = _checkpointNextCapture = next;
                // 每候选最多三次离散网络尝试，耗尽后保留原正文与Journal现场；不无限重试、退款或结束Run。
                if (_checkpointPending.HasPending && _checkpointAttempts >= 3)
                    CheckpointError("CheckpointDeliveryAttemptsExhausted", true);
            }
        }
        private bool CreateCheckpointRequest(int kind)
        {
            try
            {
                _checkpointRequest = kind == 0 ? (_checkpointPending.IsTerminal
                    ? InstanceControlHttp.CreateTerminalCheckpoint(_launch, _secret, _checkpointPending.Body)
                    : InstanceControlHttp.CreateCheckpoint(_launch, _secret, _checkpointPending.Body))
                    : kind == 1 ? InstanceControlHttp.QueryCheckpoint(_launch, _secret, _checkpointPending.CheckpointId)
                    : InstanceControlHttp.ReadCheckpointHead(_launch, _secret);
                return true;
            }
            catch (ArgumentException) { CheckpointError("CheckpointControlConfigurationRejected", true); return false; }
        }
        private T ParseCheckpointResponse<T>() where T : class
        {
            try { return JsonUtility.FromJson<T>(_checkpointRequest.downloadHandler.text); }
            catch (ArgumentException) { return null; }
        }
        private static string CheckpointRejectionDiagnostic(long postStatus, string body)
        {
            // 这只是控制面观测，不参与重试、Frozen或资产事务。复用中立DTO，不让Shared/Core引用后端实现。
            // 白名单逐项对应当前业务校验码；不能按RunCheckpoint前缀放行任意服务端文本，防止日志注入或泄露正文。
            string code = null;
            if (!string.IsNullOrEmpty(body) && body.Length <= 1024)
            {
                try { code = JsonUtility.FromJson<OnlineErrorReply>(body)?.Code; }
                catch (ArgumentException) { /* 非JSON/未知形状只保留HTTP状态，不拼接解析异常或原文。 */ }
            }
            switch (code)
            {
                case "InvalidRunCheckpointJson":
                case "RunCheckpointIdentityMismatch":
                case "RunCheckpointCompleteInvalid":
                case "RunCheckpointIdempotencyConflict":
                case "RunCheckpointReplay":
                case "RunCheckpointIdentityBudgetExceeded":
                case "RunCheckpointNotFound":
                case "RunCheckpointHeadInvalid":
                case "RunCheckpointPayloadTooLarge":
                case "RunCheckpointStoragePacketLimit":
                case "RunCheckpointMemberMismatch":
                case "RunCheckpointSchemaDowngrade":
                case "RunCheckpointEntryUnavailable":
                case "RunCheckpointOwnershipConflict":
                case "RunCheckpointProfileVersionConflict":
                case "RunCheckpointBoundsInvalid":
                case "RunCheckpointGroundInvalid":
                case "RunCheckpointRetirementInvalid":
                case "RunCheckpointItemMissing":
                case "RunCheckpointItemInvalid":
                case "RunCheckpointStateRollback":
                case "RunCheckpointWorldInvalid":
                case "RunCheckpointCoinInvalid":
                case "InstanceCompletionMembersPending":
                case "InstanceCompletionCheckpointPending":
                case "InstanceCompletionWorldAssetsPending":
                case "InvalidInstanceCredential":
                case "InstanceEpochExpired":
                case "InstanceNotFound":
                case "InstanceStateConflict":
                case "LocalControlOnly":
                case "ContentVersionMismatch":
                case "UnknownContent":
                case "CombatAssetsUnavailable":
                case "InvalidCombatAsset":
                case "SelectedWandNotOwned":
                case "CombatParentNotOwned":
                case "InvalidCombatCard":
                case "InvalidCombatWand":
                case "InvalidCombatContainer":
                case "InvalidCombatMaterial":
                case "InvalidCombatAttachment":
                case "InvalidCombatLocation":
                    break;
                default:
                    code = "UnrecognizedCode";
                    break;
            }
            return "CheckpointCandidateRejected/HTTP" + postStatus.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + code;
        }
        private void CheckpointError(string code, bool stopDelivery)
        {
            bool changed = _checkpointError != code || stopDelivery && !_checkpointDeliveryStopped;
            _checkpointError = code; _checkpointDeliveryStopped |= stopDelivery;
            if (changed) GameLog.Warn("检查点未确认，保留原资产与候选，不执行结算：" + code, "OnlineCheckpoint");
        }
        private void ReleaseCheckpointRequest()
        { var request = _checkpointRequest; _checkpointRequest = null; request?.Dispose(); }
        private void StopCheckpointPublisher()
        {
            _checkpointPublishing = false; _checkpointAwaitingCapture = false;
            _checkpointRequest?.Abort(); ReleaseCheckpointRequest(); _checkpointSending = false;
            // 不清pending正文/确认水位来伪造完成；世界/进程最终生命周期由原Scope负责。
        }
    }
}
