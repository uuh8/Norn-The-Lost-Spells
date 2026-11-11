using Game.Core;
using Game.Networking;
using UnityEngine;

namespace Game.Multiplayer
{
    public sealed partial class OnlineWorldBootstrap
    {
        private RunExitNetworkChannel _memberExitChannel;
        private bool _localMemberExitPending;
        private string _localMemberExitCommand;
        private void OnLocalMemberExitRequested(OnlineRunExitRequestedEvent request)
        {
            if (IsAuthority || _stopped || string.IsNullOrEmpty(request.CommandId)) return;
            _localMemberExitPending = true; _localMemberExitCommand = request.CommandId; ApplyImmediateInputGate(); PublishView();
        }
        private void OnLocalMemberExitStatus(OnlineRunExitStatusChangedEvent change)
        {
            if (IsAuthority || _stopped || _auth == null || !_auth.ClientAuthenticated || _auth.ClientIdentity == null
                || change.Status.ConnectionEpoch != _auth.ClientIdentity.ConnectionEpoch) return;
            if (change.Status.Phase == OnlineRunExitPhase.Rejected && change.Status.Failure == "ReturnPortalUnavailable"
                && change.Status.CommandId == _localMemberExitCommand)
            { _localMemberExitPending = false; _localMemberExitCommand = null; ApplyImmediateInputGate(); PublishView(); return; }
            if (change.Status.Reason != OnlineRunExitReason.FinalDeath) return;
            // 可信DS的死亡终态也关闭本地输入；不发布Requested，避免把通知反射成新的结算意图。
            _localMemberExitPending = true; _localMemberExitCommand = change.Status.CommandId; ApplyImmediateInputGate(); PublishView();
        }

        private void BindMemberExitChannel()
        {
            ClearMemberExitChannel();
            if (_manager == null || _combatDefinition == null) return;
            _memberExitChannel = _manager.GetComponent<RunExitNetworkChannel>();
            if (_memberExitChannel == null) _memberExitChannel = _manager.gameObject.AddComponent<RunExitNetworkChannel>();
            _memberExitChannel.ConfigureWorld(this, _combatDefinition.WireHash);
            _memberExitChannel.IntentReceived += OnMemberExitIntent;
        }
        private void ClearMemberExitChannel()
        {
            if (_memberExitChannel != null)
            { _memberExitChannel.IntentReceived -= OnMemberExitIntent; _memberExitChannel.ClearWorld(this); }
            _memberExitChannel = null;
        }
        private void OnMemberExitIntent(JoinConnectionIdentity identity, OnlineRunExitIntent intent)
        {
            if (!IsAuthority || _instance == null || identity == null || Registry == null) return;
            _instance.RequestMemberExit(identity, intent);
        }

        /// <summary>退出仅撤销本人的新意图；排队施法、物理和已提交材料输出继续使用原权威时钟。</summary>
        internal bool TryBeginMemberExit(JoinConnectionIdentity identity, string command)
        {
            if (!IsAuthority || !CanAdvance || Registry == null || identity == null || identity.ActorId < 1 || identity.ActorId > 4) return false;
            var slot = Registry[(int)identity.ActorId - 1];
            if (slot.Epoch != identity.ConnectionEpoch || slot.Cast == null) return false;
            slot.Cast.CloseResearchSession();
            if (!slot.Cast.TryBeginMemberExit(command)) return false;
            slot.Input.Neutralize();
            if (slot.Player != null) slot.Player.ClearOnlineInput();
            return true;
        }
        private void CompleteMemberExitCaptures(uint tick)
        {
            // 在本Tick角色、Physics和材料提交之后同步取本人Owned；不占用全世界Checkpoint请求槽。
            if (IsAuthority && CanAdvance && _instance != null) _instance.CaptureMemberExits(this, tick);
        }
        internal bool TryReplaceRejectedMemberExit(JoinConnectionIdentity identity, string previous, string next)
        {
            if (!IsAuthority || !CanAdvance || Registry == null || identity == null || identity.ActorId < 1 || identity.ActorId > 4) return false;
            var slot = Registry[(int)identity.ActorId - 1];
            return slot.Epoch == identity.ConnectionEpoch && slot.Cast != null && slot.Cast.TryReplaceRejectedMemberExit(previous, next);
        }
        internal void SendMemberExitStatus(JoinConnectionIdentity identity, in OnlineRunExitStatus status)
        { if (_memberExitChannel != null) _memberExitChannel.SendStatus(identity, status); }

        internal void CompleteMemberExit(ulong actor, string player)
        {
            if (!IsAuthority || Registry == null || _instance == null || !_instance.Inventory.MarkMemberExited(actor, player)) return;
            var slot = Registry[(int)actor - 1];
            slot.Connected = slot.SimulationReady = false; slot.Input.Neutralize();
            if (slot.Player != null) slot.Player.ClearOnlineInput();
            // 原Movement Connected=false快照移除其他客户端的Avatar；不Dispose整个World或队友资源。
            // 本人连接暂留用于可靠最终收据，客户端确认Profile桥之后自行StopConnection。
        }
        private static bool HasMemberExitFence(NetworkActorRegistry.Slot slot) => slot.Cast != null && slot.Cast.HasMemberExitFence;
    }
}
