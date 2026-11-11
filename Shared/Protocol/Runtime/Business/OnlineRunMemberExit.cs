using System;

namespace Game.NetworkProtocol
{
    /// <summary>可信DS声明的个人离局原因；客户端不能借Session自报最终死亡来操作资产。</summary>
    public enum OnlineRunMemberExitReason
    {
        None = 0,
        VoluntaryExit = 1,
        FinalDeath = 2
    }
    /// <summary>DS稳定捕获的个人ESC候选；固定正文按同SettlementId重试，不含凭据，不决定死亡/通关规则。</summary>
    [Serializable] public sealed class OnlineRunMemberExitRequest
    {
        public const uint LegacySchemaVersion = 1;
        public const uint LegacyFinalDeathSchemaVersion = 2;
        public const uint CurrentSchemaVersion = 3;
        // 保留合法4096物品+完整trait/组分布局的空间；只复用既有控制面上限，不扩大物品槽。
        public const int MaximumPayloadBytes = OnlineRunAssetCheckpoint.MaximumPayloadBytes;
        public uint SchemaVersion;
        public string SettlementId = "", InstanceId = "", RunId = "", PlayerId = "";
        public uint RoomEpoch, ConnectionEpoch, ExpectedPersistentVersion;
        public ulong ActorId;
        public OnlineOwnedAssetSnapshot Assets;
        // V1没有原因字段，只承载原ESC。V2必须显式选原因，不能把缺失0默认为死亡或主动退出。
        public OnlineRunMemberExitReason Reason;
        // Assets始终保留清理前真实Owned3；最终死亡的精确Destroyed集合不靠“Items缺项”推断。
        // 主动退出显式[]；旧V1保持null并使用旧Canonical字段形状，不能改变已保存请求Hash。
        public OnlineRunAssetCheckpointRetired[] FinalRetiredItems;
        // 最终死亡专属真实销毁量；按上述原ID清前状态派生，不混入在途/供料旧损耗来源。
        public OnlineOwnedMaterialAmountSnapshot[] FinalDeathLostMaterials;
        // V3只承认可信DS明确成功出局事实；普通ESC/最终死亡false/0，不能借退出自动补货。
        public bool SuccessfulExtraction;
        public ulong CollectedGmu;
        public uint DefeatedEnemies;
        // 仅Home沿原Entry作用域提交连续性；SHA覆盖整个压缩壳UTF8，不回写客户端资产草稿。
        public string PracticeSessionScope = "", PracticeContinuityJson = "", PracticeContinuityHash = "";
        // V3必须显式给数组；普通副本/死亡为[]，Home真实地面根/安装卡由独立事务保留。
        // V1/2必须null并从旧Canonical省略，不能把追加字段默认值写进历史请求Hash。
        public OnlineRunAssetCheckpointGround[] PracticeGroundItems;
    }

    /// <summary>
    /// 资产/金币/本人资格同事务提交后才有最终收据。PayloadHash是业务规范正文摘要，
    /// 不是Unity JSON原文字节Hash；未知回包先查询，不以断开网络判断资产已经保存。
    /// </summary>
    [Serializable] public sealed class OnlineRunMemberExitReceipt
    {
        public uint SchemaVersion;
        public string SettlementId = "", InstanceId = "", RunId = "", PlayerId = "", PayloadHash = "";
        public uint RoomEpoch, ConnectionEpoch, CommittedVersion, CurrencyVersion;
        public ulong ActorId;
        public long Gold;
        public bool Exited;
        public OnlineRunMemberExitReason Reason;
        public OnlineRunAssetCheckpointRetired[] FinalRetiredItems;
        public OnlineOwnedMaterialAmountSnapshot[] FinalDeathLostMaterials;
        public bool SuccessfulExtraction;
        public ulong CollectedGmu;
        public uint DefeatedEnemies;
        // 原PayloadHash证明整个候选；不在收据重复回显可能32MiB的正文/地面，防止SQL及HTTP预算翻倍。
        public string PracticeSessionScope = "", PracticeContinuityHash = "";
    }
}
