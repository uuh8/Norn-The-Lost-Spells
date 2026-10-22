using UnityEngine;

namespace Game.Core
{
    public enum DungeonEntryMode : byte { Cancel, SinglePlayer, Matchmaking }

    /// <summary>Run已核对门与角色后申请显示选项；目标键来自门配置，不赋予UI加载/携装权限。</summary>
    public struct DungeonEntryChoiceRequestedEvent : IGameEvent
    {
        public int PortalId;
        public Collider Actor;
        public string DestinationKey, Label;
    }
    public struct DungeonEntryChoiceSubmittedEvent : IGameEvent
    {
        public int PortalId;
        public Collider Actor;
        public DungeonEntryMode Mode;
    }
    /// <summary>取消、完成或门被卸载时关闭相同门的Presentation；不是副本进入成功通知。</summary>
    public struct DungeonEntryChoiceClosedEvent : IGameEvent { public int PortalId; }
    public struct DungeonMatchRequestedEvent : IGameEvent { public int PortalId; public string DestinationKey; }
    public struct DungeonMatchStatusEvent : IGameEvent { public int PortalId; public string Message; }
}
