using System;

namespace Game.NetworkProtocol
{
    /// <summary>业务服冻结的真实携带闭包；装备、包内实例及其组分一并冻结，仓库不投影到副本。</summary>
    [Serializable] public sealed class OnlineOwnedAssetSnapshot
    {
        public const uint PreCurrencySchemaVersion = 2;
        public const uint CurrentSchemaVersion = 3;
        // 包内备用杖也能安装卡，不能再按“三把装备杖”计算闭包上限。有限预算不承诺无限背包。
        public const int MaximumItems = 4096;
        public uint SchemaVersion; // 必须显式为当前版本；不把旧数据默认为空容器。
        public string RunId = "", PlayerId = "", InventoryId = "", GameplayConfigHash = "", SelectedWandInstanceId = "";
        public ulong ActorId;
        public uint InventoryVersion;
        // 统一账号的当前金币读数。真实入局来自业务Wallet，局内由DS维护；不是另发一份副本货币。
        // CurrencyVersion是DS该读数的修订，独立于物品库存Version；非零才有可信币值，零不能补默认余额。
        public long Gold;
        public uint CurrencyVersion;
        public OnlineOwnedItemSnapshot[] Items = Array.Empty<OnlineOwnedItemSnapshot>();
    }
    [Serializable] public sealed class OnlineOwnedItemSnapshot
    {
        public string InstanceId = "", ContentId = "", ParentInstanceId = "";
        public uint Revision;
        public int LocationKind, Slot, X, Y, QuarterTurns, RemainingCharges;
        public bool HasUnlimitedCharges;
        public bool EntryBoundBackpack; // 服务端来源与Owner绑定，不由客户端设置；普通同款包仍可按原规则使用。
        public bool UseTemplateWandStats; // true+null 明确使用受控模板；false 的 Wand 必须附带合法实例数值。
        public OnlineOwnedWandStatsSnapshot WandStats;
        public OnlineOwnedContainerStateSnapshot ContainerState; // null 是缺失；Schema=1 且零组分才是明确空瓶。
        public OnlineOwnedMaterialStateSnapshot MaterialState;
        public OnlineOwnedSpellAttachmentStateSnapshot AttachmentState; // 仅具体法术卡的可选设置；失效来源仍保留，不授予数量。 // 仅Material实物，缺状态不从容量/模板补量。
    }
    /// <summary>真实实物材料数量；同一值跨Profile/入局冻结/DS保持，不以容器组分或模板替代。</summary>
    [Serializable] public sealed class OnlineOwnedMaterialStateSnapshot
    {
        public uint SchemaVersion; // 显式1；旧/未知0拒绝，不制造空壳或满量实物。
        public byte MaterialKey;
        public ulong Quantity; // 精确GMU；正量且不得超过可信目录的实物容量。
    }
    /// <summary>容器实例的真实组分；只运送状态，容量与材料许可来自受控内容清单。</summary>
    [Serializable] public sealed class OnlineOwnedContainerStateSnapshot
    {
        public uint SchemaVersion;
        public OnlineOwnedMaterialAmountSnapshot[] Components; // 缺字段保持未知；明确空瓶须由权威方提供 []。
    }
    [Serializable] public sealed class OnlineOwnedMaterialAmountSnapshot
    {
        public byte MaterialKey;
        public ulong Quantity; // GMU 整数；不得经过 float/double 或有符号 SQL BIGINT。
    }
    [Serializable] public sealed class OnlineOwnedWandStatsSnapshot
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
