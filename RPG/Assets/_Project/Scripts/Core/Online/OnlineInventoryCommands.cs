using System;
using UnityEngine;

namespace Game.Core
{
    /// <summary>线上库存的有限命令集合；只描述意图，不包含 Session、Store 或 Unity 物品引用。</summary>
    public enum OnlineInventoryCommandKind : byte
    {
        SelectWand = 1, PlaceCard = 2, SelectTool = 3, CollectMaterial = 4, PourContainer = 5, BindTool = 6,
        DropContainer = 7, PickupContainer = 8, ThrowContainer = 9, ReleaseSolid = 10, ConfigureSpellAttachment = 11,
        // 通用移动复用原库存位置与事务；保留旧命令编号/语义，不能将客户端拖放等同于资产已提交。
        MoveOwned = 12, EquipWand = 13, DropItem = 14, PickupItem = 15,
        // 研究开关是会话意图，没有虚构ItemId。DS核验原Home区域，原账本仍负责幂等与Epoch。
        BeginWandResearch = 16, EndWandResearch = 17
    }

    /// <summary>显式编号是 Wire 合同。新失败原因只能追加，不能修改已有编号的业务含义。</summary>
    public enum OnlineInventoryCommandFailure : byte
    {
        None = 0, NotReady = 1, InvalidInput = 2, NotOwned = 3, StaleVersion = 4, StaleProgram = 5,
        StaleCommand = 6, CommandConflict = 7, Busy = 8, NoCapacity = 9, Rejected = 10, RateLimited = 11
    }

    /// <summary>
    /// UI/角色输入提出的值意图。物品操作ItemId必须是既有实例；研究会话开关必须为空。PlaceCard 的空 SourceWandId 表示来自
    /// 角色 SpellStorage，ToSpellStorage 指定目标根位置。版本取自开始操作时的权威读快照，不能提交前偷换成新版本。
    /// </summary>
    public struct OnlineInventoryCommandIntent
    {
        public ulong CommandId;
        /// <summary>请求的GMU上限，DS仍按可信定义和权威Tick预算限流，不能把客户端数值当作已转移数量。</summary>
        public ulong TransferBudget;
        /// <summary>镜头中心射线的值意图；DS使用真实Actor位置并重算采样/轨迹，不接受客户端落点或世界写结果。</summary>
        public Vector3 AimOrigin, AimDirection;
        public uint ConnectionEpoch, ExpectedInventoryVersion, SourceProgramRevision, TargetProgramRevision;
        public OnlineInventoryCommandKind Kind;
        public string ItemId, SourceWandId, TargetWandId;
        public int TargetSlot;
        public bool ToSpellStorage;
        // 同一实例可多次落地；WorldOwnerId区分这一次真实地面归属，旧拾取意图不能作用于下一次落地。
        // AutoPlace仅用于原附近自动拾取；手动拖入包时使用真实包ID与格位，不接收客户端世界落点。
        public string WorldOwnerId, TargetBackpackId;
        public int GridX, GridY, QuarterTurns;
        public bool AutoPlace;
        // 仅Kind11使用；配置不搬材料。清除必须空来源/零剂量，非附件命令不得夹带这些值。
        public string AttachmentContainerId;
        public ulong AttachmentDose;
        public bool AttachmentEnabled;
        // 仅通用移动/装备/拾取指定既有ItemLocationKind值。根Owner由DS身份决定，不能由Client指定其他玩家。
        // 自动拾取为0且不带目标格；手动包/杖父实例仍用上面的TargetBackpackId/TargetWandId。
        public int TargetLocationKind;
        public bool WholeStack; // 原同格整堆移动/丢弃语义，不是客户端直接提交的数量或实例列表。
    }

    /// <summary>命令处理结果不携带可变资产；成功后 View 仍等待独立完整快照，不乐观地更改客户端物品归属。</summary>
    public struct OnlineInventoryCommandReceipt
    {
        public ulong CommandId;
        public uint ConnectionEpoch, InventoryVersion;
        public OnlineInventoryCommandFailure Failure;
        /// <summary>数量事务的原失败与已提交GMU；重放同一意图返回原值，Owner快照才是资产安装来源。</summary>
        public MaterialTransferFailure TransferFailure;
        public ulong Transferred;
        public bool Success => Failure == OnlineInventoryCommandFailure.None;
    }

    /// <summary>
    /// 仅由DS装配层生成的调用上下文，不是Wire消息。Actor位置、当前世界与落点来自权威角色及
    /// 原采样/抛物线规则；Run只承接已验证的几何值，并再次检查当前选择、库存版本及数量事务。
    /// CanAdvance显式携带在线Frozen/租约门，因为底层实验World写口不能替代业务运行状态。
    /// </summary>
    public struct OnlineContainerAuthorityContext
    {
        public bool CanAdvance;
        public string WorldId;
        public Vector3 ActorPosition, TargetPosition;
        /// <summary>仅DS按原投瓶规则重算的起点与初速度；Wire不接受客户端提交这两个权威结果。</summary>
        public Vector3 ThrowOrigin, ThrowVelocity;
        /// <summary>仅DS已预检地面模型使用的实际旋转；不是Wire字段，不授予客户端摆放世界模型的权限。</summary>
        public Quaternion TargetRotation;
        /// <summary>DS瞄准采样未命中/触及有界采样上限时显式失败，禁止把默认落点误当有效材料目标。</summary>
        public MaterialTransferFailure PreparationFailure;
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
