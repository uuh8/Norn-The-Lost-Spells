using System;

namespace Game.Core
{
    /// <summary>
    /// 组合层从已认证连接取得的预期身份，不能直接由待验证库存快照反填。
    /// Game.Run 不依赖 Mirror/Protobuf；网络模块先校验连接，再把这些基础值交给读模型。
    /// WorldSceneKey 是当前 Client 的区域键，不是 Dedicated Server 进程中的 Scene handle。
    /// </summary>
    public readonly struct OnlineInventoryPresentationIdentity
    {
        public readonly string RunId, PlayerId, InventoryId, GameplayConfigHash, WorldSceneKey;
        public readonly ulong ActorId;
        public readonly uint ConnectionEpoch;

        public OnlineInventoryPresentationIdentity(string runId, string playerId, ulong actorId,
            string inventoryId, string gameplayConfigHash, uint connectionEpoch, string worldSceneKey)
        {
            RunId = runId; PlayerId = playerId; ActorId = actorId; InventoryId = inventoryId;
            GameplayConfigHash = gameplayConfigHash; ConnectionEpoch = connectionEpoch; WorldSceneKey = worldSceneKey;
        }
    }

    /// <summary>
    /// 一次完整的线上库存表现值快照。Assets 复用 Core 已有的物品值合同，不能让 Run 反向引用 NetworkProtocol。
    /// 它与“首次入局发给 DS 的执行投影”不同：Wands 必须给出当前 Mana/CD/ProgramRevision，
    /// SpellUses 必须给出服务器已经批准的资格，Client 不从模板或剩余次数猜测当前执行状态。
    /// </summary>
    [Serializable]
    public sealed class OnlineInventoryPresentationSnapshot
    {
        public const uint CurrentSchemaVersion = 1;
        public uint SchemaVersion; // 默认0表示未提供当前合同，不能悄悄兼容成有权威数据。
        public uint ConnectionEpoch; // 与预期连接代次完全一致，拒绝重连前的迟到快照。
        public ulong SnapshotSequence; // 当前连接内单调递增；整份数据一起安装，不混拼不同批次。
        public uint ServerTick; // 导出当前资源的权威Tick基线，仅用于同连接时序；不能当成本机Rendering frame。
        public uint ResourceVersion, SelectionVersion, SpellUseVersion;
        public OnlineOwnedAssetSnapshot Assets;
        public OnlineInventoryWandPresentationSnapshot[] Wands = Array.Empty<OnlineInventoryWandPresentationSnapshot>();
        public OnlineInventorySpellUsePresentationSnapshot[] SpellUses = Array.Empty<OnlineInventorySpellUsePresentationSnapshot>();
    }

    /// <summary>一根真实装备法杖的当前读数；固定属性仍来自 Assets 中经配置 Hash 校验的模板/实例属性。</summary>
    [Serializable]
    public sealed class OnlineInventoryWandPresentationSnapshot
    {
        public string InstanceId = "";
        public uint ProgramRevision; // 服务器实际程序版本，不在 Client 按导入次数重新编号。
        public float CurrentMana, CooldownRemaining; // Mana为当前值，CooldownRemaining单位为秒；不在 Client 自动推进。
    }

    /// <summary>资格已由服务器确定；RemainingCharges/Unlimited仍使用同批 Assets 里的实例值，避免两份数量冲突。</summary>
    [Serializable]
    public sealed class OnlineInventorySpellUsePresentationSnapshot
    {
        public string InstanceId = "";
        public bool Eligible; // 可为“未来次数已到0，但本局已取得资格”；不能仅凭当前次数重新推断。
    }
}
