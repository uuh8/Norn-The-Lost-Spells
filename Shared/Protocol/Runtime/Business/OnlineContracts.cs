using System;

namespace Game.NetworkProtocol
{
    // 业务 HTTP 使用 JSON，实时 Gameplay 才使用 Protobuf。公共字段同时适配 Unity JsonUtility 和服务端 IncludeFields。
    // 这些对象只有消息含义，没有数据库、Unity 或资产写权限；密码/Token 不得持久化到客户端。
    [Serializable] public sealed class RegisterAccountRequest
    {
        public string Username = ""; // 3–24 个 ASCII 字母/数字/下划线，账号唯一键不使用显示名。
        public string Password = ""; // 10–128 字符，仅提交给业务服的 PasswordHasher。
    }
    [Serializable] public sealed class RegisterAccountReply { public string Username = ""; }
    [Serializable] public sealed class LoginAccountRequest
    {
        public string Username = "";
        public string Password = "";
        public uint ProtocolVersion; // 应用版本，不是 Google.Protobuf 库版本。
        public string ContentManifestHash = ""; // Client/Server/业务端的物品身份清单必须一致。
    }
    [Serializable] public sealed class LoginAccountReply
    {
        public string Token = ""; // Opaque Bearer credential；UI/日志不能显示或保存它。
        public long ExpiresUnixMilliseconds;
    }
    [Serializable] public sealed class OnlineProfileReply
    {
        public string PlayerId = ""; // 业务服生成的角色 ID；不能拿 Client 自报值选择别人的资产。
        public string Username = "";
        public uint Version;
        public long Gold; // 整数最小单位；N1 没有 Client 增减货币接口。
        // 只描述业务租约事实；旧JSON缺省false不能作为解除资产锁的证明。
        public bool AssetsLocked;
        public string ActiveRunId = ""; // 持有租约的Run标识；不授予进入或解除资格。
        public string SafeHub = "";
        public int BackpackSlots;
        public int WarehouseSlots;
        public string ContentManifestHash = "";
        public OnlineItemReply[] Items = Array.Empty<OnlineItemReply>();
        // 私人据点地面姿态与物品真状态分离；Items 保留同 ID/子卡/组分，CombatAssets 不包含地面闭包。
        public OnlineHubWorldItemReply[] WorldItems = Array.Empty<OnlineHubWorldItemReply>();
        public OnlineOwnedAssetSnapshot CombatAssets; // 旧 JSON 缺失时保持 null；不能凭默认值授予可执行装备。
        // 业务从当前Bearer派生的opaque和平会话作用域；不是Token/TokenHash，不授予HTTP写权限。
        public string PracticeSessionScope = "";
        // 仅同一作用域返回的有界gzip-base64壳；DS严格恢复Mana/CD/Deck/历史，不包含物品写入合同。
        public string PracticeContinuityJson = "";
    }
    [Serializable] public sealed class OnlineItemReply
    {
        public string InstanceId = "";
        public string ContentId = ""; // 配置 ID 和 InstanceId 不同：同配置可以生成多个独立实例。
        public string DisplayName = "";
        public string Area = "";
        public int Slot;
        public uint Revision;
        public int LocationKind; // 与 Game.Run.ItemLocationKind 的稳定编号一致；Shared 不引用 Game.Run。
        public int X, Y, QuarterTurns; // 原生背包占格，不能由旧平面 Slot 猜造。
        public string ParentInstanceId = ""; // WandSlot 指向同一角色真实持有的 Wand；根位置为空。
        public int RemainingCharges;
        public bool HasUnlimitedCharges; // 必须由业务授权显式保存，不能从 ContentId 或负数次数推导。
        // Profile.Items还覆盖仓库：模板模式与实例roll必须随原ID显式传递，不能只在携带CombatAssets里保存。
        public bool UseTemplateWandStats; // 仅法杖的持久模板模式为true；false不能被客户端解释成允许自动补模板。
        public OnlineOwnedWandStatsSnapshot WandStats; // 实例roll复用同一中立合同；模板杖/非杖为null，保留float与trait原顺序。
        public bool EntryBoundBackpack; // 可信入场保底来源；UI仅据此展示/拦截，真正权限仍由服务器判定。
        public OnlineOwnedContainerStateSnapshot ContainerState;
        public OnlineOwnedMaterialStateSnapshot MaterialState;
        public OnlineOwnedSpellAttachmentStateSnapshot AttachmentState;
    }
    [Serializable] public sealed class InventoryMoveRequest
    {
        public string CommandId = ""; // 一次业务操作的幂等键，重试沿用原键和原请求内容。
        public uint ExpectedVersion;
        public string InstanceId = "";
        public string Destination = "";
        public int Slot;
        // 0保留原平面移动正文与幂等摘要；1表示账号结构化Move，不借用副本Run/Actor/Epoch。
        // 2 为选中本人已经装备的真实法杖，只更新选择，不隐式移动、换杖或修改物品 Revision。
        public int OperationKind;
        // 稳定位置值0/1/2/3/4/7/8：仓库、实包格、杖卡、杖装备、包装备、快捷法术、工具。
        // 根Owner只由Session推导；父ID只能引用同账号已有的真实包/杖，禁止客户端提交新资产状态。
        public int TargetLocationKind;
        public string TargetParentInstanceId = "";
        public int GridX, GridY, QuarterTurns;
        // 整栈成员由服务器按源实包同格推导；false只移动原实例，绝不凭此生成拆分资产。
        public bool WholeStack;
        // 3整理、4丢地、5拾取均沿原账号命令；姿态只描述已准备落点，绝不包含可改写的物品状态。
        public string WorldOwnerId = "";
        public OnlineHubWorldItemPose WorldPose;
    }
    [Serializable] public sealed class InventoryMoveReply
    {
        public string CommandId = "";
        public uint CommittedVersion; // 重试仍返回第一次提交的结果，不伪装成最新 Profile 版本。
        public string InstanceId = "";
        public string Destination = "";
        public int Slot;
        // 仅为已提交事务的目标回执，客户端还需确认同账号的完整Profile才能安装结果。
        public int OperationKind, TargetLocationKind;
        public ulong Transferred; // Op5材料实际拾取GMU；部分并堆时原地面ID/Spawn与余量仍存在，其余操作为0。
        public string TargetParentInstanceId = "";
        public int GridX, GridY, QuarterTurns;
        public bool WholeStack;
        public string WorldOwnerId = "";
        public OnlineHubWorldItemPose WorldPose;
    }
    [Serializable] public sealed class OnlineHubWorldItemReply
    {
        public string InstanceId = "";
        public string WorldOwnerId = ""; // 每次丢弃生成的新 Spawn 身份，旧拾取请求不能命中新一轮落地。
        public OnlineHubWorldItemPose Pose;
    }
    [Serializable] public sealed class OnlineHubWorldItemPose
    {
        public string SceneKey = "";
        public string WorldSceneKey = "";
        public float PositionX, PositionY, PositionZ;
        public float RotationX, RotationY, RotationZ, RotationW = 1;
        public int PoseKind; // 原 WorldItemPoseKind：0 Legacy / 1 Authored / 2 Grounded。
        public bool HasVisualScaleOverride;
        public float VisualScaleX = 1, VisualScaleY = 1, VisualScaleZ = 1;
    }
    [Serializable] public sealed class OnlineErrorReply { public string Code = ""; }

    public static class OnlineInventoryAreas
    {
        public const string Backpack = "Backpack";
        public const string Warehouse = "Warehouse";
        public const int BackpackCapacity = 8;
        public const int WarehouseCapacity = 16;
    }
}
