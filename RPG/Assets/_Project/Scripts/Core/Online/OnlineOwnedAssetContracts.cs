using System;

namespace Game.Core
{
    /// <summary>
    /// 业务服冻结的真实携带闭包：装备、包内实例与容器组分；旧平面背包不得伪造成Run实例。
    /// Core仅保存中立原始值，Run校验归属、占格、固定属性和数量后才发布权威工作区。
    /// </summary>
    [Serializable]
    public sealed class OnlineOwnedAssetSnapshot
    {
        public const uint CurrentSchemaVersion = 3;
        // 与 Shared 的纯合同保持同一有限预算；备用杖的安装卡也属于真实携带闭包。
        public const int MaximumItems = 4096;
        // 默认 0 是有意的：旧 JSON 没有 Owned 字段时必须拒绝，不能自动授予新版本权限。
        public uint SchemaVersion;
        public string RunId = "", PlayerId = "", InventoryId = "", GameplayConfigHash = "", SelectedWandInstanceId = "";
        public ulong ActorId;
        public uint InventoryVersion;
        // Schema3显式携带同一个玩家的真实金币总额；不因单机/联机拆成两份，也不从本地模板补数。
        // CurrencyVersion仅是存活DS工作区的独立修订，不是永久Wallet/Character版本或结算收据。
        // 零版本表示未知字段/旧包，必须拒绝；合法余额可以为0，故不能用Gold!=0判断字段存在。
        public long Gold;
        public uint CurrencyVersion;
        public OnlineOwnedItemSnapshot[] Items = Array.Empty<OnlineOwnedItemSnapshot>();
    }

    [Serializable]
    public sealed class OnlineOwnedItemSnapshot
    {
        public string InstanceId = "", ContentId = "", ParentInstanceId = "";
        public uint Revision;
        // 使用既有保存契约编号：WandSlot=2、EquippedWand=3。不得直接强转尚未校验的网络整数。
        public int LocationKind, Slot, X, Y, QuarterTurns, RemainingCharges;
        public bool HasUnlimitedCharges;
        public bool EntryBoundBackpack; // 业务入场来源的可信实例标记；客户端命令不得设置或解除，不按ContentId推断。
        // 法杖必须二选一：显式使用经 Hash 校验的模板，或者携带完整实例固定属性。
        public bool UseTemplateWandStats;
        public OnlineOwnedWandStatsSnapshot WandStats;
        public OnlineOwnedContainerStateSnapshot ContainerState;
        public OnlineOwnedMaterialStateSnapshot MaterialState;
        public OnlineOwnedSpellAttachmentStateSnapshot AttachmentState; // 仅具体法术卡的可选设置；失效来源仍保留，不授予数量。
    }

    /// <summary>固体实物的显式数量；容量不是初始量。Schema1且正Key/Quantity才是已知状态。</summary>
    [Serializable]
    public sealed class OnlineOwnedMaterialStateSnapshot
    {
        public uint SchemaVersion;
        public byte MaterialKey;
        public ulong Quantity;
    }

    /// <summary>中立容器状态。Schema=1 的空数组代表空瓶；null/旧版本不能授予可执行状态。</summary>
    [Serializable]
    public sealed class OnlineOwnedContainerStateSnapshot
    {
        public uint SchemaVersion;
        public OnlineOwnedMaterialAmountSnapshot[] Components; // null 不自动补成空瓶。
    }

    [Serializable]
    public sealed class OnlineOwnedMaterialAmountSnapshot
    {
        public byte MaterialKey;
        public ulong Quantity;
    }

    [Serializable]
    public sealed class OnlineOwnedWandStatsSnapshot
    {
        public float MaxMana, ManaRegenPerSecond, BaseCastDelay, BaseReloadTime, BaseSpreadDegrees;
        public int ProgramCapacity, BaseDraws;
        public bool Shuffle;
        public string[] AlwaysCastContentIds = Array.Empty<string>();
    }
    /// <summary>具体法术卡的供料设置；Schema1、真实来源ID与正剂量，实际扣料仍由施法事务裁决。</summary>
    [Serializable] public sealed class OnlineOwnedSpellAttachmentStateSnapshot
    {
        public uint SchemaVersion;
        public string ContainerId = "";
        public ulong Dose;
        public bool Enabled;
    }

}
