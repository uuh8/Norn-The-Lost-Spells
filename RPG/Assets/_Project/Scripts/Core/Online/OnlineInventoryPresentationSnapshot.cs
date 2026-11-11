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
        // 0保留原选杖语义；1..7是已绑定工具槽0..6，必须由Run再次核验真实归属/容器。
        public uint SelectedToolSlotPlusOne;
        public OnlineOwnedAssetSnapshot Assets;
        // 正式世界按同批完整快照安装公开地面容器；null只表示此来源未提供该能力，不能清空已知地面。
        public OnlineGroundInventorySnapshot Ground;
        public OnlineInventoryWandPresentationSnapshot[] Wands = Array.Empty<OnlineInventoryWandPresentationSnapshot>();
        public OnlineInventorySpellUsePresentationSnapshot[] SpellUses = Array.Empty<OnlineInventorySpellUsePresentationSnapshot>();
        // 原研究台只在可信和平会话打开时获得完整输入/最近实发，不让每帧Mana事实携带大Graph。
        // Open同时是明确presence，避开Unity inline Serializable把null恢复成默认空对象的歧义。
        public bool ResearchOpen;
        public uint ResearchSessionVersion;
        public OnlineWandResearchStateSnapshot Research;
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

    /// <summary>
    /// 同一副本共享地面容器的完整只读投影。既有4×4096传输上界保持不变。
    /// 原P7新增来源按可信Catalog的真实容器携带限制加最多12瓶证明仍在此界内，超界配置显式拒绝；
    /// 这不是无限地图/无限物品承诺，也不能用副本读数代替N6可靠结算。
    /// </summary>
    [Serializable]
    public sealed class OnlineGroundInventorySnapshot
    {
        public const int MaximumItems = 4 * OnlineOwnedAssetSnapshot.MaximumItems;
        // 当前正式副本最多12个NPC，活动Coin使用原NPC稳定ID；不是无限金币/探索容量承诺。
        public const int MaximumCoins = 12;
        public uint Version;
        public OnlineGroundItemSnapshot[] Items = Array.Empty<OnlineGroundItemSnapshot>();
        public OnlineCoinSnapshot[] Coins = Array.Empty<OnlineCoinSnapshot>();
    }

    /// <summary>
    /// DS当前尚未拾取的唯一金币事实。值类型让表现层复制时不泄漏源引用，也不需要每帧创建对象。
    /// 这里只表达位置与原抽样金额；客户端读取不会产生余额写入，也不代表已永久保存。
    /// </summary>
    [Serializable]
    public struct OnlineCoinSnapshot
    {
        public ulong Id;
        public long Amount;
        public UnityEngine.Vector3 Position;
    }

    /// <summary>地面只有同一容器身份、当前落地身份和受控目录状态；不携带任意Prefab路径或客户端写口。</summary>
    [Serializable]
    public sealed class OnlineGroundItemSnapshot
    {
        public string InstanceId = "", ContentId = "", WorldOwnerId = "";
        public uint Revision;
        public UnityEngine.Vector3 Position;
        public UnityEngine.Quaternion Rotation;
        public OnlineOwnedContainerStateSnapshot ContainerState;
        public OnlineOwnedMaterialStateSnapshot MaterialState;
        // 新协议地面根与子卡保留完整实例值；旧根字段/材料字段仅作同值别名，接收时必须一致。
        // Item使用WorldItem=5、空Parent、Slot=-1；Children仅包含此根法杖安装卡，不能形成嵌套容器或任意父图。
        public OnlineOwnedItemSnapshot Item;
        public OnlineOwnedItemSnapshot[] Children = Array.Empty<OnlineOwnedItemSnapshot>();
    }

}
