using System;

namespace Game.Core
{
    /// <summary>
    /// 业务服冻结的入局执行投影。只包含已装备法杖及其安装卡闭包，不把业务 Backpack 槽伪造成 Run 的背包容器。
    /// Core 保存原始值；Run 验证完整父链、次数和固定属性后才建立权威内存工作区。
    /// </summary>
    [Serializable]
    public sealed class OnlineOwnedAssetSnapshot
    {
        public const uint CurrentSchemaVersion = 1;
        // 默认 0 是有意的：旧 JSON 没有 Owned 字段时必须拒绝，不能自动授予新版本权限。
        public uint SchemaVersion;
        public string RunId = "", PlayerId = "", InventoryId = "", GameplayConfigHash = "", SelectedWandInstanceId = "";
        public ulong ActorId;
        public uint InventoryVersion;
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
        // 法杖必须二选一：显式使用经 Hash 校验的模板，或者携带完整实例固定属性。
        public bool UseTemplateWandStats;
        public OnlineOwnedWandStatsSnapshot WandStats;
    }

    [Serializable]
    public sealed class OnlineOwnedWandStatsSnapshot
    {
        public float MaxMana, ManaRegenPerSecond, BaseCastDelay, BaseReloadTime, BaseSpreadDegrees;
        public int ProgramCapacity, BaseDraws;
        public bool Shuffle;
        public string[] AlwaysCastContentIds = Array.Empty<string>();
    }
}
