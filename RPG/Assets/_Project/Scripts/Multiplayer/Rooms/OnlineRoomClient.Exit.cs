using System;
using System.Collections;
using Game.Core;
using Game.Networking;
using Game.NetworkProtocol;
using Mirror;
using UnityEngine;

namespace Game.Multiplayer
{
    /// <summary>
    /// 主动退出与普通传输断开分开保存。CommandId在一次退出生命周期内固定，
    /// 收到可靠最终结果之前不把Cancel/超时解释成可以回到可写据点。
    /// </summary>
    public sealed partial class OnlineRoomClient
    {
        private string _exitCommandId, _exitPayloadHash, _exitFailure, _exitPlayerId;
        private uint _exitEpoch, _exitCommittedVersion;
        private bool _exitCommitted, _exitHubReady;
        private bool _exitThroughReturnPortal;
        private OnlineRunExitPhase _exitPhase;
        private OnlineRunExitReason _exitReason;
        private bool _terminalExitProbeAllowsReconnect;
        private bool _exitRequested, _exitResolveRequested, _exitHubRestored, _exitReceiptAbsent, _exitServerObserved;
        private ulong _exitActor;
        private int _exitQueries, _exitProfileAttempts, _exitIntentAttempts;
        private double _exitNextQuery, _exitNextProfile, _exitNextIntent;
        private const string ExitAccountMismatchMessage = "退出属于原账号，请恢复该账号后重试确认；不会切换物资或退出请求。";

        private bool TryBindMemberExitPlayer()
        {
            if (_exitPlayerId == null)
            {
                var identity = _auth != null && _auth.ClientAuthenticated ? _auth.ClientIdentity : null;
                // 首次记录后与Command/未知正文同生命周期；新Token或新Profile不能改写退出所属玩家。
                _exitPlayerId = identity != null ? identity.PlayerId : _account != null ? _account.PlayerId : null;
            }
            return MatchesMemberExitPlayer();
        }
        private bool MatchesMemberExitPlayer()
        {
            if (string.IsNullOrEmpty(_exitPlayerId) || _account == null || _account.PlayerId != _exitPlayerId) return false;
            var identity = _auth != null && _auth.ClientAuthenticated ? _auth.ClientIdentity : null;
            return identity == null || identity.PlayerId == _exitPlayerId;
        }
        private void ShowMemberExitAccountMismatch()
        { if (_status != ExitAccountMismatchMessage) Status(ExitAccountMismatchMessage); }

        private void OnReturnPortal(OnlineReturnPortalRequestedEvent request)
        {
            var world = OnlineWorldBootstrap.Active;
            if (_exitRequested || world == null || world.IsAuthority || world.gameObject.scene.handle != request.WorldSceneHandle
                || request.ActorId != ActorId || request.ConnectionEpoch != ConnectionEpoch || !world.CanUseLocalReturnPortal()) return;
            BeginMemberExit(true);
        }
        private void BeginMemberExit(bool throughReturnPortal = false)
        {
            if (_room == null || string.IsNullOrEmpty(_room.RunId)) return;
            if (!TryBindMemberExitPlayer()) { ShowMemberExitAccountMismatch(); return; }
            if (_exitCommandId == null || _exitPhase == OnlineRunExitPhase.Rejected && _exitFailure == "RunMemberExitConnectionSuperseded")
            {
                // 未认证时只记录退出/查询意图，不伪造epoch；真正首包发送时才生成有界freshness ID。
                _exitCommandId = null; _exitActor = ActorId;
                _exitPhase = OnlineRunExitPhase.Draining; _exitFailure = null;
                _exitReason = OnlineRunExitReason.VoluntaryExit;
                _exitThroughReturnPortal = throughReturnPortal || _exitThroughReturnPortal;
            }
            _exitRequested = _exitResolveRequested = true; _cancelRequested = false;
            _exitServerObserved = false; _exitIntentAttempts = 0; _exitNextIntent = 0;
            _exitQueries = _exitProfileAttempts = 0; _exitNextQuery = _exitNextProfile = Time.realtimeSinceStartupAsDouble;
            if (_phase == OnlineRoomPhase.Disconnected && !_exitCommitted) _command = OnlineRoomAction.Reconnect;
            SendStableMemberExitIntent();
            Status(_exitCommitted ? "退出已保存，正在确认同账号据点资产…" : "正在保存并退出本局；确认前不会返回据点或生成第二份资产。");
        }
        private void SendStableMemberExitIntent()
        {
            if (!_exitRequested || _exitCommitted || !MatchesMemberExitPlayer() || _auth == null || !_auth.ClientAuthenticated || _auth.ClientIdentity == null) return;
            // 死亡只能重试DS已给出的固定命令；客户端永不为死亡Mint第二份正文或提交Reason。
            if (_exitReason == OnlineRunExitReason.FinalDeath && !ExitId(_exitCommandId)) return;
            var channel = _manager != null ? _manager.GetComponent<RunExitNetworkChannel>() : null;
            if (channel == null || channel.GameplayHash == 0 || !NetworkClient.isConnected) return;
            _exitEpoch = _auth.ClientIdentity.ConnectionEpoch;
            if (_exitCommandId == null)
                _exitCommandId = CreateMemberExitCommand(_exitEpoch);
            _exitIntentAttempts++; _exitNextIntent = Time.realtimeSinceStartupAsDouble + 2;
            if (_exitActor == 0) _exitActor = _auth.ClientIdentity.ActorId;
            // 本地仅发送稳定命令ID，身份与资产分别由认证连接和DS真实Owner决定。
            EventBus<OnlineRunExitRequestedEvent>.Publish(new OnlineRunExitRequestedEvent
                { CommandId = _exitCommandId, ThroughReturnPortal = _exitThroughReturnPortal });
        }
        private void OnMemberExitStatus(OnlineRunExitStatusChangedEvent change)
        {
            if (_auth == null || !_auth.ClientAuthenticated || _auth.ClientIdentity == null
                || change.Status.ConnectionEpoch != _auth.ClientIdentity.ConnectionEpoch) return;
            if (_exitThroughReturnPortal && change.Status.CommandId == _exitCommandId && !_exitServerObserved && !_exitCommitted
                && change.Status.Phase == OnlineRunExitPhase.Rejected && change.Status.Failure == "ReturnPortalUnavailable")
            {
                // 仅DS在建立Fence之前的明确空间拒绝可恢复游戏；未知保存结果或已开始drain仍不能解锁。
                ResetMemberExit(); Status("尚未进入可用返回门，请靠近后再按F。"); return;
            }
            // 最终死亡由当前可信DS主动发起，不依赖玩家已经按过ESC，也不伪造一条客户端死亡意图。
            // 同命令ESC在固定正文前可以升级；DS已先建死亡候选时，也可用其稳定ID取代未确认本地按键ID。
            if (change.Status.Reason == OnlineRunExitReason.FinalDeath && !_exitCommitted)
            {
                if (!ExitId(change.Status.CommandId) || !TryBindMemberExitPlayer()) return;
                _exitCommandId = change.Status.CommandId; _exitActor = _auth.ClientIdentity.ActorId;
                _exitRequested = _exitResolveRequested = true; _cancelRequested = false;
                _exitReason = OnlineRunExitReason.FinalDeath;
                _exitQueries = _exitProfileAttempts = 0; _exitNextQuery = _exitNextProfile = 0;
            }
            if (!_exitRequested) return;
            if (!MatchesMemberExitPlayer()) { ShowMemberExitAccountMismatch(); return; }
            _exitEpoch = _auth.ClientIdentity.ConnectionEpoch;
            if (!TryApplyMemberExitStatus(change.Status)) return;
            _exitServerObserved = true;
            if (_exitCommitted) Status("退出已保存，正在读取同账号的最新据点资产…");
            else if (_exitPhase == OnlineRunExitPhase.Rejected) Status("退出保存被拒绝；物资现场仍保留。请重试确认，不会直接返回旧据点资产。");
            else if (_exitPhase == OnlineRunExitPhase.Uncertain) Status("退出保存结果尚未确认，正在查询同一请求；不会重复提交新物资。");
            else Status("正在完成本人的已提交动作并保存物资；队友仍可继续。");
        }
        private IEnumerator ResolveMemberExitReceipt()
        {
            if (_room == null || !_exitRequested) yield break;
            _exitReceiptAbsent = false;
            if (!MatchesMemberExitPlayer()) { ShowMemberExitAccountMismatch(); yield break; }
            _exitResolveRequested = false; _exitQueries++; _exitNextQuery = Time.realtimeSinceStartupAsDouble + 2;
            yield return Send("GET", "/api/online/runs/" + _room.RunId + "/member-exit", null);
            if (!MatchesMemberExitPlayer()) { ShowMemberExitAccountMismatch(); yield break; }
            if (!_success)
            {
                // 只有同Session认证请求的明确404才允许后续Mint；超时/500/坏收据保持Unknown。
                _exitReceiptAbsent = _httpStatus == 404 && _account != null && _account.Authenticated;
                yield break;
            }
            OnlineRunMemberExitReceipt receipt;
            try { receipt = JsonUtility.FromJson<OnlineRunMemberExitReceipt>(_body); }
            catch (ArgumentException) { yield break; }
            if (!TryAcceptSessionMemberExitReceipt(receipt)) yield break;
            Status("已确认退出收据，正在刷新账号资产…");
        }

        /// <summary>
        /// Status若在传输断开前完全丢失，客户端尚无退出命令。先以本人的Session查询终态，
        /// 避免最终死亡已结束资格时反复Mint必然失败的票；只有明确404允许继续原重连链。
        /// </summary>
        private IEnumerator ProbeTerminalMemberExit()
        {
            _terminalExitProbeAllowsReconnect = false;
            if (_exitRequested)
            {
                yield return ResolveMemberExitReceipt();
                _terminalExitProbeAllowsReconnect = _exitReceiptAbsent; yield break;
            }
            if (_room == null || !TryBindMemberExitPlayer()) yield break;
            yield return Send("GET", "/api/online/runs/" + _room.RunId + "/member-exit", null);
            if (!MatchesMemberExitPlayer()) { ShowMemberExitAccountMismatch(); yield break; }
            if (!_success)
            { _terminalExitProbeAllowsReconnect = _httpStatus == 404 && _account != null && _account.Authenticated; yield break; }
            OnlineRunMemberExitReceipt receipt;
            try { receipt = JsonUtility.FromJson<OnlineRunMemberExitReceipt>(_body); }
            catch (ArgumentException) { yield break; }
            if (TryAcceptSessionMemberExitReceipt(receipt)) Status("已确认个人离局收据，正在恢复据点资产…");
        }
        private bool TryAcceptSessionMemberExitReceipt(OnlineRunMemberExitReceipt receipt)
        {
            if (receipt == null || !TryReadMemberExitReason(receipt, out var reason) || !receipt.Exited || !ExitId(receipt.SettlementId)
                || _exitCommandId != null && receipt.SettlementId != _exitCommandId && reason != OnlineRunExitReason.FinalDeath
                || receipt.RunId != _room.RunId || receipt.RoomEpoch != _room.RoomEpoch || receipt.PlayerId != _exitPlayerId
                || !ExitId(receipt.InstanceId) || receipt.ActorId < 1 || receipt.ActorId > 4
                || _exitActor != 0 && receipt.ActorId != _exitActor || receipt.ConnectionEpoch == 0
                || receipt.Gold < 0 || receipt.CurrencyVersion == 0 || receipt.CommittedVersion == 0 || !ExitHash(receipt.PayloadHash)) return false;
            if (_exitCommitted && (_exitCommittedVersion != receipt.CommittedVersion || _exitPayloadHash != receipt.PayloadHash
                || _exitCommandId != receipt.SettlementId || _exitReason != reason)) return false;
            // Session查询能在原Transport已断开、资格已结束而不能Mint新票时恢复最终事实。
            // 这里的origin epoch属于已提交候选，不强行改成新连接的transport epoch。
            _exitCommandId = receipt.SettlementId; _exitActor = receipt.ActorId; _exitCommitted = true; _exitPhase = OnlineRunExitPhase.Committed;
            _exitReason = reason; _exitRequested = true; _cancelRequested = false;
            _exitCommittedVersion = receipt.CommittedVersion; _exitPayloadHash = receipt.PayloadHash;
            return true;
        }
        private static bool TryReadMemberExitReason(OnlineRunMemberExitReceipt receipt, out OnlineRunExitReason reason)
        {
            reason = OnlineRunExitReason.None;
            if (receipt.SchemaVersion == OnlineRunMemberExitRequest.LegacySchemaVersion && receipt.Reason == OnlineRunMemberExitReason.None)
            { reason = OnlineRunExitReason.VoluntaryExit; return true; }
            if (receipt.SchemaVersion != OnlineRunMemberExitRequest.CurrentSchemaVersion
                && receipt.SchemaVersion != OnlineRunMemberExitRequest.LegacyFinalDeathSchemaVersion
                || receipt.Reason < OnlineRunMemberExitReason.VoluntaryExit || receipt.Reason > OnlineRunMemberExitReason.FinalDeath
                || receipt.FinalRetiredItems == null || receipt.FinalRetiredItems.Length > Game.NetworkProtocol.OnlineOwnedAssetSnapshot.MaximumItems
                || receipt.FinalDeathLostMaterials == null || receipt.FinalDeathLostMaterials.Length > 8) return false;
            if (receipt.Reason == OnlineRunMemberExitReason.VoluntaryExit
                && (receipt.FinalDeathLostMaterials.Length != 0 || receipt.FinalRetiredItems.Length != 0
                    && !(receipt.SchemaVersion == OnlineRunMemberExitRequest.CurrentSchemaVersion
                        && ExitHash(receipt.PracticeSessionScope) && ExitHash(receipt.PracticeContinuityHash)))) return false;
            reason = (OnlineRunExitReason)(byte)receipt.Reason; return true;
        }
        private IEnumerator PrepareMemberExitHub()
        {
            if (!MatchesMemberExitPlayer()) { ShowMemberExitAccountMismatch(); yield break; }
            _exitProfileAttempts++; _exitNextProfile = Time.realtimeSinceStartupAsDouble + 2;
            yield return Send("GET", "/api/online/profile", null);
            if (!MatchesMemberExitPlayer()) { ShowMemberExitAccountMismatch(); yield break; }
            if (!_success) { Status("物资已保存，但账号资产尚未读回；保留当前安全状态，请重试确认。"); yield break; }
            OnlineProfileReply profile;
            try { profile = JsonUtility.FromJson<OnlineProfileReply>(_body); }
            catch (ArgumentException) { yield break; }
            if (profile == null || profile.PlayerId != _exitPlayerId) { ShowMemberExitAccountMismatch(); yield break; }
            if (!_suspendedHub.IsValid() || !_suspendedHub.isLoaded)
            {
                if (_inConnectionScene) { Status("物资已保存，但原据点场景已不可用；不会激活其他场景或旧资产。"); yield break; }
                _suspendedHub = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            }
            if (!_account.TryAdoptVerifiedHubProfile(profile, _exitCommittedVersion, out var error))
            { Status(error ?? "据点账号资产尚未就绪，暂不激活旧本地档。"); yield break; }
            if (!MatchesMemberExitPlayer()) { ShowMemberExitAccountMismatch(); yield break; }
            if (!_account.TryPrepareVerifiedHubScene(_suspendedHub, out error))
            { Status(error ?? "据点账号资产尚未就绪，暂不激活旧本地档。"); yield break; }
            if (!_exitHubRestored)
            {
                // Prepare已先锁旧本地写入口并安装真实账号Source；成功后才断KCP和恢复原Hub。
                StopConnection(); yield return ReturnToHub(); _inConnectionScene = false;
                _exitHubRestored = _suspendedHub.IsValid() && _suspendedHub.isLoaded
                    && UnityEngine.SceneManagement.SceneManager.GetActiveScene() == _suspendedHub;
                yield return null;
            }
            _exitHubReady = _exitHubRestored && MatchesMemberExitPlayer() && _account.IsVerifiedHubSceneReady(_suspendedHub, _exitCommittedVersion);
            if (!_exitHubReady) Status("退出已保存，正在等待据点账号资产绑定完成；不会退回本地旧档。");
        }
        private void ResetMemberExit()
        {
            _exitCommandId = _exitPayloadHash = _exitFailure = _exitPlayerId = null; _exitEpoch = _exitCommittedVersion = 0;
            _exitCommitted = _exitHubReady = _exitRequested = _exitResolveRequested = _exitHubRestored = false;
            _exitReceiptAbsent = _exitServerObserved = false;
            _exitActor = 0; _exitQueries = _exitProfileAttempts = _exitIntentAttempts = 0; _exitNextQuery = _exitNextProfile = _exitNextIntent = 0;
            _exitPhase = OnlineRunExitPhase.None;
            _exitReason = OnlineRunExitReason.None; _terminalExitProbeAllowsReconnect = false;
            _exitThroughReturnPortal = false;
        }
        private static string CreateMemberExitCommand(uint epoch)
        {
            // 离散操作仅生成一次真实96位随机尾；重发原ID，不把随机生成放进每帧热路径。
            var nonce = new byte[12];
            using (var random = System.Security.Cryptography.RandomNumberGenerator.Create()) random.GetBytes(nonce);
            var chars = new char[32];
            epoch.ToString("x8", System.Globalization.CultureInfo.InvariantCulture).CopyTo(0, chars, 0, 8);
            const string hex = "0123456789abcdef";
            for (int i = 0; i < nonce.Length; i++) { chars[8 + i * 2] = hex[nonce[i] >> 4]; chars[9 + i * 2] = hex[nonce[i] & 15]; }
            return new string(chars);
        }
        private IEnumerator ResumePendingMemberExit()
        {
            // 临时停用后只恢复同ID查询；不自动Mint/替换候选，需用户明确重连或重试。
            _phase = OnlineRoomPhase.Disconnected; _command = OnlineRoomAction.Observe;
            _exitResolveRequested = true; _exitQueries = _exitProfileAttempts = 0;
            _exitNextQuery = _exitNextProfile = 0;
            yield return Connected();
            StopConnection(); _phase = OnlineRoomPhase.None; _busy = false;
            Application.runInBackground = _originalBackground; _routine = null;
        }

        /// <summary>
        /// 通道已验协议/配置，本地还必须关联当前命令和transport代次。
        /// 只复制结果，场景切换仍由原Connected Coroutine在安全生命周期完成。
        /// </summary>
        private bool TryApplyMemberExitStatus(in OnlineRunExitStatus status)
        {
            if (!ExitId(_exitCommandId) || status.CommandId != _exitCommandId || status.ConnectionEpoch == 0
                || status.ConnectionEpoch != _exitEpoch || status.Phase < OnlineRunExitPhase.Draining
                || status.Phase > OnlineRunExitPhase.Rejected || !ExitFailure(status.Failure)
                || status.Reason < OnlineRunExitReason.VoluntaryExit || status.Reason > OnlineRunExitReason.FinalDeath
                || _exitReason != OnlineRunExitReason.None && status.Reason != _exitReason) return false;
            bool completed = status.Phase == OnlineRunExitPhase.Committed;
            if (completed)
            {
                if (status.CommittedVersion == 0 || !ExitHash(status.PayloadHash) || !string.IsNullOrEmpty(status.Failure)) return false;
                if (_exitCommitted)
                    return _exitCommittedVersion == status.CommittedVersion && _exitPayloadHash == status.PayloadHash;
                _exitCommitted = true; _exitCommittedVersion = status.CommittedVersion; _exitPayloadHash = status.PayloadHash;
            }
            else
            {
                if (_exitCommitted || status.CommittedVersion != 0 || !string.IsNullOrEmpty(status.PayloadHash)) return false;
                if (status.Phase == OnlineRunExitPhase.Rejected && string.IsNullOrEmpty(status.Failure)) return false;
            }
            _exitPhase = status.Phase; _exitFailure = status.Failure ?? string.Empty; _exitReason = status.Reason;
            return true;
        }

        private static bool ExitId(string value) => ExitHex(value, 32);
        private static bool ExitHash(string value) => ExitHex(value, 64);
        private static bool ExitHex(string value, int length)
        {
            if (value == null || value.Length != length) return false;
            for (int i = 0; i < value.Length; i++)
                if (!(value[i] >= '0' && value[i] <= '9') && !(value[i] >= 'a' && value[i] <= 'f')) return false;
            return true;
        }
        private static bool ExitFailure(string value)
        {
            if (value == null) return true;
            if (value.Length > 96) return false;
            for (int i = 0; i < value.Length; i++) if (value[i] < 32 || value[i] > 126) return false;
            return true;
        }
    }
}
