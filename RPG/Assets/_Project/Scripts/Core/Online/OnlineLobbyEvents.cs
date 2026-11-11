using System;
using System.Collections.Generic;

namespace Game.Core
{
    public enum OnlineLobbyAction : byte { Register, Login, Refresh, Move, Logout, Observe, EnterHub }
    public enum AccountMenuCommand : byte { EnterHub, Settings, Quit }
    public struct AccountMenuCommandEvent : IGameEvent { public AccountMenuCommand Command; }
    /// <summary>业务Profile认证查询成功后才通知主菜单进入；客户端通知不能替代副本服的JoinTicket验证。</summary>
    public struct AccountHubEntryReadyEvent : IGameEvent
    {
        // 只关联原菜单的本次请求，不携带Token或授予资产写权；加载目标必须与预arm目标相同。
        public int RequestOwnerId;
        public string TargetScene;
    }

    /// <summary>UI 只表达用户意图；密码只在本次同步通知/HTTP 请求期间使用，不进入日志或存档。</summary>
    public struct OnlineLobbyRequestEvent : IGameEvent
    {
        public OnlineLobbyAction Action;
        public string Username, Password, InstanceId, Destination;
        public uint ExpectedVersion;
        public int Slot;
        public int RequestOwnerId;
    }
    public readonly struct OnlineItemView
    {
        public readonly string InstanceId, DisplayName, Area;
        public readonly int Slot;
        // 模板必须由业务显式声明；旧服务缺字段时既不是模板，也不能展示一组猜造的零属性。
        public readonly bool UseTemplateWandStats;
        public readonly OnlineWandStatsView? WandStats;
        public OnlineItemView(string instanceId, string displayName, string area, int slot)
            : this(instanceId, displayName, area, slot, false, null) { }
        public OnlineItemView(string instanceId, string displayName, string area, int slot,
            bool useTemplateWandStats, OnlineOwnedWandStatsSnapshot wandStats)
        {
            InstanceId = instanceId; DisplayName = displayName; Area = area; Slot = slot;
            UseTemplateWandStats = useTemplateWandStats;
            WandStats = wandStats == null ? (OnlineWandStatsView?)null : new OnlineWandStatsView(wandStats);
        }
    }
    /// <summary>
    /// 账户事件的纯只读属性值；复制HTTP可变DTO和有序trait，不向UI暴露另一份资产写入口。
    /// 仅在Profile响应时创建，不进入Gameplay每帧路径，也不从ContentId重新Roll或补模板。
    /// </summary>
    public readonly struct OnlineWandStatsView
    {
        public readonly float MaxMana, ManaRegenPerSecond, BaseCastDelay, BaseReloadTime, BaseSpreadDegrees;
        public readonly int ProgramCapacity, BaseDraws;
        public readonly bool Shuffle;
        private readonly IReadOnlyList<string> _alwaysCastContentIds;
        public IReadOnlyList<string> AlwaysCastContentIds => _alwaysCastContentIds ?? Array.Empty<string>();
        public OnlineWandStatsView(OnlineOwnedWandStatsSnapshot source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            MaxMana = source.MaxMana; ManaRegenPerSecond = source.ManaRegenPerSecond;
            BaseCastDelay = source.BaseCastDelay; BaseReloadTime = source.BaseReloadTime;
            BaseSpreadDegrees = source.BaseSpreadDegrees; ProgramCapacity = source.ProgramCapacity;
            BaseDraws = source.BaseDraws; Shuffle = source.Shuffle;
            var ids = source.AlwaysCastContentIds == null ? Array.Empty<string>() : (string[])source.AlwaysCastContentIds.Clone();
            _alwaysCastContentIds = Array.AsReadOnly(ids);
        }
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
