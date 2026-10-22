namespace Game.Core
{
    public enum OnlineLobbyAction : byte { Register, Login, Refresh, Move, Logout, Observe, EnterHub }
    public enum AccountMenuCommand : byte { EnterHub, Settings, Quit }
    public struct AccountMenuCommandEvent : IGameEvent { public AccountMenuCommand Command; }
    /// <summary>业务Profile认证查询成功后才通知主菜单进入；客户端通知不能替代副本服的JoinTicket验证。</summary>
    public struct AccountHubEntryReadyEvent : IGameEvent { }

    /// <summary>UI 只表达用户意图；密码只在本次同步通知/HTTP 请求期间使用，不进入日志或存档。</summary>
    public struct OnlineLobbyRequestEvent : IGameEvent
    {
        public OnlineLobbyAction Action;
        public string Username, Password, InstanceId, Destination;
        public uint ExpectedVersion;
        public int Slot;
    }
    public readonly struct OnlineItemView
    {
        public readonly string InstanceId, DisplayName, Area;
        public readonly int Slot;
        public OnlineItemView(string instanceId, string displayName, string area, int slot)
        { InstanceId = instanceId; DisplayName = displayName; Area = area; Slot = slot; }
    }
    /// <summary>一次响应产生的只读展示快照；不包含 Session Token，不赋予 UI 资产写权限。</summary>
    public struct OnlineLobbyChangedEvent : IGameEvent
    {
        public bool Busy, Authenticated;
        public string Status, Username, PlayerId;
        public uint Version;
        public long Gold;
        public OnlineItemView[] Items;
    }
    public struct OnlineLobbyClosedEvent : IGameEvent { }
}
