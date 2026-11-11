using System;

namespace Game.NetworkProtocol
{
    /// <summary>
    /// DS 在稳定资产边界捕获的 Run 级归属证据。它不是客户端存档，也不决定退出、死亡或撤离规则；
    /// 业务端必须先验证实例凭据、租约及前次提交，再检查这里的完整携带/地面/消耗分区。
    /// </summary>
    [Serializable] public sealed class OnlineRunAssetCheckpoint
    {
        public const uint CurrentSchemaVersion = 1;
        public const uint LegacyCompleteSchemaVersion = 2;
        public const uint LegacyCurrencyCompleteSchemaVersion = 3;
        public const uint CompleteSchemaVersion = 4;
        // V3已有个人金币，V4再补世界Coin墓碑；不能用新字段默认值推定历史能力。
        public static bool HasWorldFacts(uint schemaVersion) => schemaVersion == LegacyCompleteSchemaVersion
            || schemaVersion == LegacyCurrencyCompleteSchemaVersion || schemaVersion == CompleteSchemaVersion;
        public static bool HasCurrencyFacts(uint schemaVersion) => schemaVersion == LegacyCurrencyCompleteSchemaVersion
            || schemaVersion == CompleteSchemaVersion;
        public const int MaximumCoins = 12;
        public const int MaximumPayloadBytes = 64 * 1024 * 1024;
        // 仅由既有wire预算推导的保守数量门；不把Item容量或已落地输出槽误用为供料准备阶段玩法上限。
        public const int MaximumMaterialOutputs = MaximumPayloadBytes / 64;
        public const int MaximumMembers = 4;
        public const int MaximumLostMaterials = MaximumMembers * 3 * 8;
        // 对齐原地面快照的 4×4096 预算，不能把分页/单条命令的128收据误当作地面容量。
        public const int MaximumGroundItems = MaximumMembers * OnlineOwnedAssetSnapshot.MaximumItems;
        public const int MaximumActiveItems = MaximumMembers * OnlineOwnedAssetSnapshot.MaximumItems + MaximumGroundItems;
        public const int MaximumRetiredItems = MaximumActiveItems;
        public const int MaximumWorldKeys = 128;
        public const int MaximumWordsPerChunk = 32768;
        public const int MaximumFlightsPerMember = 8;
        public uint SchemaVersion;
        public string CheckpointId = "", InstanceId = "", RunId = "", GameplayConfigHash = "";
        public uint RoomEpoch, ServerTick;
        public ulong Sequence;
        public OnlineRunAssetCheckpointMember[] Members = Array.Empty<OnlineRunAssetCheckpointMember>();
        public OnlineRunAssetCheckpointGround[] GroundItems = Array.Empty<OnlineRunAssetCheckpointGround>();
        public OnlineRunAssetCheckpointRetired[] RetiredItems = Array.Empty<OnlineRunAssetCheckpointRetired>();
        // Schema1缺失这些事实，只能称资产检查点；V2/V3/V4必须显式提供，空集合不能由null猜成[]。
        // V3/V4同时要求Members.Assets的Gold/CurrencyVersion完整；历史V2不补余额0。
        public OnlineRunAssetCheckpointWorld World;
        public OnlineRunAssetCheckpointFlight[] Flights;
        public OnlineRunAssetCheckpointMaterialOutput[] MaterialOutputs;
        public OnlineRunAssetCheckpointLostMaterial[] LostMaterials;
        // V4必须显式提供全表（含单赢家墓碑）；null是缺事实，不能默补空覆盖世界。
        // 历史1/2/3保持null并在Canonical中省略，保护已有正文Hash与请求幂等。
        public OnlineRunAssetCheckpointCoin[] Coins;

        /// <summary>多端共用的有界纯值校验；不引用Unity/数据库，也不执行拾取、加款或结算。</summary>
        public static bool HasValidCoinFacts(OnlineRunAssetCheckpointCoin[] coins)
        {
            if (coins == null || coins.Length > MaximumCoins) return false;
            ushort seen = 0;
            for (int i = 0; i < coins.Length; i++)
            {
                var coin = coins[i];
                if (coin == null || coin.Id < 5 || coin.Id > 16 || coin.Amount <= 0 || coin.Position == null
                    || !Finite(coin.Position.X) || !Finite(coin.Position.Y) || !Finite(coin.Position.Z)
                    || (coin.Collected ? coin.CollectorActorId < 1 || coin.CollectorActorId > 4 : coin.CollectorActorId != 0)) return false;
                int bit = 1 << (int)(coin.Id - 5); if ((seen & bit) != 0) return false; seen |= (ushort)bit;
            }
            return true;
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    /// <summary>原NPC一次掉落的恒定身份/金额/死亡位置与单赢家状态；已拾记录继续占原12个槽。</summary>
    [Serializable] public sealed class OnlineRunAssetCheckpointCoin
    {
        public ulong Id;
        public long Amount;
        public OnlineRunAssetCheckpointVector3 Position;
        public bool Collected;
        public ulong CollectorActorId;
    }

    [Serializable] public sealed class OnlineRunAssetCheckpointMember
    {
        public string PlayerId = "";
        public ulong ActorId;
        public uint ConnectionEpoch;
        public OnlineOwnedAssetSnapshot Assets;
        public bool NeverAdmitted;
        public uint ResourceVersion, SelectionVersion, SpellUseVersion, SelectedToolSlotPlusOne;
        public OnlineRunAssetCheckpointWandResource[] Wands;
        public OnlineRunAssetCheckpointSpellUse[] SpellUses;
    }

    /// <summary>地面根与其安装卡仍是原实例；Children不允许夹带另一个根或跨Owner父链。</summary>
    [Serializable] public sealed class OnlineRunAssetCheckpointGround
    {
        public string WorldOwnerId = "";
        public OnlineOwnedItemSnapshot Item;
        public OnlineOwnedItemSnapshot[] Children = Array.Empty<OnlineOwnedItemSnapshot>();
        public OnlineRunAssetCheckpointVector3 Position;
        public OnlineRunAssetCheckpointQuaternion Rotation;
    }

    public enum OnlineRunAssetRetiredReason
    {
        None = 0,
        Consumed = 1,
        Merged = 2,
        Destroyed = 3
    }

    /// <summary>仅本次相对已知存活集合的终止证据，不携带无限增长的历史墓碑数组。</summary>
    [Serializable] public sealed class OnlineRunAssetCheckpointRetired
    {
        public string InstanceId = "", ContentId = "";
        public uint Revision;
        public OnlineRunAssetRetiredReason Reason;
    }

    /// <summary>只证明检查点已可靠保存；不表示Items已回写、租约已解除或成员已退出。</summary>
    [Serializable] public sealed class OnlineRunAssetCheckpointReceipt
    {
        public string CheckpointId = "", InstanceId = "", RunId = "", PayloadHash = "";
        public uint RoomEpoch;
        public ulong Sequence;
    }

    [Serializable] public sealed class OnlineRunAssetCheckpointVector3 { public float X, Y, Z; }
    [Serializable] public sealed class OnlineRunAssetCheckpointQuaternion { public float X, Y, Z, W; }
    [Serializable] public sealed class OnlineRunAssetCheckpointWandResource
    {
        public string InstanceId = "";
        public uint ProgramRevision;
        public float CurrentMana, CooldownRemaining;
    }
    [Serializable] public sealed class OnlineRunAssetCheckpointSpellUse
    {
        public string InstanceId = "";
        public bool Eligible;
    }
    /// <summary>已正式发布的所有历史Key（含tombstone），只保存数量/版本，不恢复Solver规则执行。</summary>
    [Serializable] public sealed class OnlineRunAssetCheckpointWorld
    {
        public string WorldId = "";
        public ulong WorldEpoch, CommitSequence;
        public long WorldTick;
        public bool CanRestoreAuthorityRules;
        public OnlineRunAssetCheckpointChunk[] Chunks;
    }
    [Serializable] public sealed class OnlineRunAssetCheckpointChunk
    {
        public uint Domain;
        public int X, Y, Z;
        public ulong WorldEpoch, SnapshotId, Generation, BaseRevision, Revision;
        public long ServerTick;
        public int WordCount;
        public uint Checksum;
        public bool IsResident;
        public uint[] Words;
    }
    /// <summary>原Location6仍是活跃物品，不能因Owner UI不展示而误记为消耗；姿态来自实际物理代理。</summary>
    [Serializable] public sealed class OnlineRunAssetCheckpointFlight
    {
        public string PlayerId = "", FlightId = "", WorldId = "", SourceId = "";
        public ulong ActorId;
        public OnlineOwnedItemSnapshot Item;
        public bool Impacted, HasPhysicalProxy;
        public float Radius;
        public ulong Deposited;
        public OnlineRunAssetCheckpointVector3 Position, Velocity;
        public OnlineRunAssetCheckpointQuaternion Rotation;
    }
    /// <summary>施法提交时已经扣瓶的独立剩余预算，不得并回瓶子或重新扣费。</summary>
    [Serializable] public sealed class OnlineRunAssetCheckpointMaterialOutput
    {
        public string PlayerId = "", OutputId = "", WorldId = "";
        public ulong ActorId;
        public OnlineOwnedMaterialAmountSnapshot[] Remaining;
    }
    /// <summary>原服务聚合损耗计数，不是新退款/销毁策略；按Owner、服务来源和原材料Key保留。</summary>
    [Serializable] public sealed class OnlineRunAssetCheckpointLostMaterial
    {
        public const string ContainerTransitSource = "ContainerTransit", SpellOutputSource = "SpellOutput", FinalDeathSource = "FinalDeath";
        public string PlayerId = "", Source = "";
        public ulong ActorId;
        public byte MaterialKey;
        public ulong Quantity;
    }
    [Serializable] public sealed class OnlineRunAssetCheckpointHeadReply
    {
        public OnlineRunAssetCheckpointReceipt Receipt;
        public OnlineRunAssetCheckpoint Checkpoint;
        public bool IsComplete;
    }
}
