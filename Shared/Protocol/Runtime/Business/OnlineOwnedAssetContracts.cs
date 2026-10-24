using System;

namespace Game.NetworkProtocol
{
    /// <summary>业务服冻结的最小战斗装备闭包；普通整备库存仍由 Profile.Items 表示，不在 DS 伪造背包。</summary>
    [Serializable] public sealed class OnlineOwnedAssetSnapshot
    {
        public uint SchemaVersion; // 必须显式为 1；旧 JSON 的 0 无法获得运行资格。
        public string RunId = "", PlayerId = "", InventoryId = "", GameplayConfigHash = "", SelectedWandInstanceId = "";
        public ulong ActorId;
        public uint InventoryVersion;
        public OnlineOwnedItemSnapshot[] Items = Array.Empty<OnlineOwnedItemSnapshot>();
    }
    [Serializable] public sealed class OnlineOwnedItemSnapshot
    {
        public string InstanceId = "", ContentId = "", ParentInstanceId = "";
        public uint Revision;
        public int LocationKind, Slot, X, Y, QuarterTurns, RemainingCharges;
        public bool HasUnlimitedCharges;
        public bool UseTemplateWandStats; // true+null 明确使用受控模板；false 的 Wand 必须附带合法实例数值。
        public OnlineOwnedWandStatsSnapshot WandStats;
    }
    [Serializable] public sealed class OnlineOwnedWandStatsSnapshot
    {
        public float MaxMana, ManaRegenPerSecond, BaseCastDelay, BaseReloadTime, BaseSpreadDegrees;
        public int ProgramCapacity, BaseDraws;
        public bool Shuffle;
        public string[] AlwaysCastContentIds = Array.Empty<string>();
    }
}
