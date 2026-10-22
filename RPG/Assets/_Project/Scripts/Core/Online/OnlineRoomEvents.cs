namespace Game.Core
{
    public enum OnlineRoomAction : byte { Ready, Start, Cancel, Disconnect, Reconnect, Observe }
    public enum OnlineRoomPhase : byte { None, Matching, Lobby, Starting, Connecting, Connected, Disconnected, Error }

    /// <summary>UI只传意图，不传Token/玩家ID或任意Scene；Multiplayer从真实Session取得身份。</summary>
    public struct OnlineRoomRequestEvent : IGameEvent { public int PortalId; public OnlineRoomAction Action; }
    public readonly struct OnlineRoomMemberView
    {
        public readonly string Name;
        public readonly bool Self, Leader, Ready;
        public OnlineRoomMemberView(string name, bool self, bool leader, bool ready)
        { Name = name; Self = self; Leader = leader; Ready = ready; }
    }
    public struct OnlineRoomChangedEvent : IGameEvent
    {
        public int PortalId;
        public OnlineRoomPhase Phase;
        public bool Busy, SelfReady, CanStart;
        public string RoomId, Status;
        public OnlineRoomMemberView[] Members;
        public ulong ActorId;
        public uint ConnectionEpoch;
        public long Replies;
        public bool NativeSocket;
    }
    /// <summary>匹配不存在/取消确认后解开Run的选择Gate；失败时可保留选择框，不默默进入单机。</summary>
    public struct DungeonMatchReleasedEvent : IGameEvent { public int PortalId; public bool CloseChoice; }
    /// <summary>真实Ticket认证成功后释放本地门暂停；不赋予任何服务器Gameplay权限。</summary>
    public struct DungeonOnlineEntryConfirmedEvent : IGameEvent { public int PortalId; }
}
