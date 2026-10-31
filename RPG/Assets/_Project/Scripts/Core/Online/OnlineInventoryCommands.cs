using System;
using UnityEngine;

namespace Game.Core
{
    /// <summary>线上库存的有限命令集合；只描述意图，不包含 Session、Store 或 Unity 物品引用。</summary>
    public enum OnlineInventoryCommandKind : byte { SelectWand = 1, PlaceCard = 2 }

    /// <summary>显式编号是 Wire 合同。新失败原因只能追加，不能修改已有编号的业务含义。</summary>
    public enum OnlineInventoryCommandFailure : byte
    {
        None = 0, NotReady = 1, InvalidInput = 2, NotOwned = 3, StaleVersion = 4, StaleProgram = 5,
        StaleCommand = 6, CommandConflict = 7, Busy = 8, NoCapacity = 9, Rejected = 10, RateLimited = 11
    }

    /// <summary>
    /// UI/角色输入提出的值意图。ItemId 始终是既有物品实例；PlaceCard 的空 SourceWandId 表示来自
    /// 角色 SpellStorage，ToSpellStorage 指定目标根位置。版本取自开始操作时的权威读快照，不能提交前偷换成新版本。
    /// </summary>
    public struct OnlineInventoryCommandIntent
    {
        public ulong CommandId;
        public uint ConnectionEpoch, ExpectedInventoryVersion, SourceProgramRevision, TargetProgramRevision;
        public OnlineInventoryCommandKind Kind;
        public string ItemId, SourceWandId, TargetWandId;
        public int TargetSlot;
        public bool ToSpellStorage;
    }

    /// <summary>命令处理结果不携带可变资产；成功后 View 仍等待独立完整快照，不乐观地更改客户端物品归属。</summary>
    public struct OnlineInventoryCommandReceipt
    {
        public ulong CommandId;
        public uint ConnectionEpoch, InventoryVersion;
        public OnlineInventoryCommandFailure Failure;
        public bool Success => Failure == OnlineInventoryCommandFailure.None;
    }

    /// <summary>CallerId 仅关联本机 UI/输入对象，不能跨网络充当玩家身份；ActorId 必须由认证连接再次核实。</summary>
    public struct OnlineInventoryCommandRequestedEvent : IGameEvent
    {
        public ulong ActorId;
        public int CallerId;
        public OnlineInventoryCommandIntent Intent;
    }
    public struct OnlineInventoryCommandResultEvent : IGameEvent
    {
        public ulong ActorId;
        public int CallerId;
        public OnlineInventoryCommandReceipt Receipt;
    }

    /// <summary>
    /// 同一 Client 的多个视图与快捷键共享序号。Controller 重建不能从1重用，否则服务器会把新意图判为旧命令。
    /// 输入和 EventBus 在 Unity 主线程调用；这里不承诺后台线程安全，也不把本机序号当作认证或防重放凭证。
    /// </summary>
    public static class OnlineInventoryCommandIds
    {
        private static ulong _next;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset() => _next = 0;
        public static ulong Next()
        {
            if (_next == ulong.MaxValue) throw new InvalidOperationException("线上库存命令序号已耗尽，请重建连接。");
            return ++_next;
        }
    }
}
