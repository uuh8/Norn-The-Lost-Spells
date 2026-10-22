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
        public string SafeHub = "";
        public int BackpackSlots;
        public int WarehouseSlots;
        public string ContentManifestHash = "";
        public OnlineItemReply[] Items = Array.Empty<OnlineItemReply>();
    }
    [Serializable] public sealed class OnlineItemReply
    {
        public string InstanceId = "";
        public string ContentId = ""; // 配置 ID 和 InstanceId 不同：同配置可以生成多个独立实例。
        public string DisplayName = "";
        public string Area = "";
        public int Slot;
        public uint Revision;
    }
    [Serializable] public sealed class InventoryMoveRequest
    {
        public string CommandId = ""; // 一次业务操作的幂等键，重试沿用原键和原请求内容。
        public uint ExpectedVersion;
        public string InstanceId = "";
        public string Destination = "";
        public int Slot;
    }
    [Serializable] public sealed class InventoryMoveReply
    {
        public string CommandId = "";
        public uint CommittedVersion; // 重试仍返回第一次提交的结果，不伪装成最新 Profile 版本。
        public string InstanceId = "";
        public string Destination = "";
        public int Slot;
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
