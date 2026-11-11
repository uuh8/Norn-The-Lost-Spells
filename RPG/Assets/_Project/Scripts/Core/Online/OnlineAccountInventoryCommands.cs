namespace Game.Core
{
    /// <summary>账号Hub复用原移动、手持选杖和背包整理；不借此授予副本、世界投放或本地存档写权限。</summary>
    public enum OnlineAccountInventoryCommandKind : byte { Move = 1, SelectWand = 2, OrganizeBackpack = 3, Drop = 4, Pickup = 5 }
    public enum OnlineAccountInventoryCommandState : byte { Pending = 0, Unknown = 1, Committed = 2, Rejected = 3 }

    /// <summary>
    /// UI给账号HTTP组合层的中立值意图。PlayerId/SceneHandle用于本机上下文栅栏，业务端仍只从Session取身份。
    /// 没有Run/Actor/ConnectionEpoch；不能为了复用Gameplay网关填假Epoch或改发本地库存事件。
    /// 版本取自抓起或按下原按钮时已安装的完整Profile；位置只描述原实例目标，数量/同格整堆由服务器真实Store裁决。
    /// </summary>
    public struct OnlineAccountInventoryCommandIntent
    {
        public string CommandId, PlayerId, ItemId, TargetParentInstanceId;
        public int SceneHandle, TargetLocationKind, TargetSlot, GridX, GridY, QuarterTurns;
        public uint ExpectedVersion;
        public OnlineAccountInventoryCommandKind Kind;
        public bool WholeStack;
        // 私人Hub掉落使用服务器生成的落地身份；拾取须回传已安装的同一次WorldOwnerId，旧代理不能拾新落点。
        public string WorldOwnerId;
        public OnlineAccountWorldItemPose WorldPose;
    }

    public struct OnlineAccountInventoryCommandRequestedEvent : IGameEvent
    {
        public int CallerId;
        public OnlineAccountInventoryCommandIntent Intent;
    }

    /// <summary>
    /// 原角色数字键的账号Hub薄入口。角色只报告原槽位，不复制Profile、不本地改选杖；
    /// 账号组合层核对同Scene/Inventory/版本并从完整Profile解析实际装备实例，复用固定HTTP请求与收据流程。
    /// 这不是DS意图，不携带Run/Actor/Epoch；查看法杖程序也不会发布它。
    /// </summary>
    public struct OnlineAccountWandSelectionRequestedEvent : IGameEvent
    {
        public int CallerId, SceneHandle, SlotIndex;
        public string InventoryId;
        public uint ExpectedVersion;
    }

    /// <summary>
    /// Pending/Unknown保留原CommandId；HTTP 200不等于Committed。组合层必须确认相同账号/Scene、
    /// 完整Profile已安装且版本达到CommittedVersion、无租约锁，才发布Committed；UI不据收据乐观改物品。
    /// </summary>
    public struct OnlineAccountInventoryCommandResultEvent : IGameEvent
    {
        public int CallerId, SceneHandle;
        public string PlayerId, CommandId, Message;
        public OnlineAccountInventoryCommandState State;
        public uint CommittedVersion;
        public OnlineInventoryCommandFailure Failure;
    }
}
