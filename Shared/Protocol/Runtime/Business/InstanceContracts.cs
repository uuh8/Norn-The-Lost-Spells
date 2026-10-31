using System;

namespace Game.NetworkProtocol
{
    // 实例控制只在本机受控链路使用；实例密钥经环境变量传入，不能序列化到此描述或Client应答。
    [Serializable] public sealed class InstanceLaunchDescriptor
    {
        public string InstanceId = "", RoomId = "", RunId = "";
        public string DungeonId = ""; // 业务 Party 授权的稳定地图键，绝不是可自由加载的 Scene 路径；旧描述缺省空值拒绝。
        public uint RoomEpoch;
        public int Port;
        public string BusinessBaseUrl = "";
        public uint ProtocolVersion;
        public string BuildVersion = "", ManifestHash = "";
        public string GameplayConfigHash = ""; // 玩法规则摘要，与物品身份清单分开；旧描述缺失时拒绝战斗入局。
        public int HeartbeatSeconds = 15;
        public int FreezeAfterSeconds = 60;
        public int IdleShutdownSeconds = 180;
    }
    [Serializable] public sealed class StartRoomRequest { public uint ExpectedRevision; }
    [Serializable] public sealed class RoomInstanceReply
    {
        public string InstanceId = "", RunId = "", State = "", Host = "127.0.0.1";
        public string DungeonId = ""; // 控制面只读的副本身份；与实际 DS 装配的配置键核对。
        public uint RoomEpoch;
        public int Port;
    }
    [Serializable] public sealed class InstanceReadyRequest
    {
        public uint RoomEpoch, ProtocolVersion;
        public string BuildVersion = "", ManifestHash = "";
        public string GameplayConfigHash = ""; // DS 必须证明加载了冻结资产对应的规则版本。
        public string DungeonId = ""; // DS 已完成装配的地图；业务服须在发布 Ready 前与 Party 比对。
        public int ProcessId;
        public long ProcessStartTicks; // UTC DateTime ticks，用来拒绝被复用的PID。
    }
    [Serializable] public sealed class InstanceHeartbeatRequest { public uint RoomEpoch; }
    [Serializable] public sealed class InstanceLeaseReply { public long LeaseExpiresUnixMilliseconds; }
    [Serializable] public sealed class InstanceReadyReply
    {
        public string DungeonId = ""; // 再确认业务授权地图；不允许根据返回值在运行中偷偷切换世界。
        public long LeaseExpiresUnixMilliseconds;
        public InstanceEntryMember[] Members = Array.Empty<InstanceEntryMember>();
    }
    [Serializable] public sealed class InstanceEntryMember
    {
        public ulong ActorId; // 本Run内稳定的Seat+1，不是Unity GetInstanceID或Client自报身份。
        public OnlineProfileReply Profile;
    }
    [Serializable] public sealed class JoinTicketReply
    {
        public string Ticket = "", PlayerId = "", InstanceId = "", RoomId = "", RunId = "";
        public string DungeonId = ""; // 业务签发的期望地图，握手确认必须保持同值；不能从本地门名替代。
        public uint RoomEpoch;
        public string Host = "127.0.0.1";
        public int Port;
        public long ExpiresUnixMilliseconds; // 60秒一次性凭据，仅内存持有，不打印或落Client存档。
    }
    [Serializable] public sealed class ConsumeJoinTicketRequest
    {
        public string Ticket = "", PlayerId = "";
        public uint RoomEpoch;
    }
    [Serializable] public sealed class ConsumeJoinTicketReply
    {
        public string PlayerId = "", RoomId = "", RunId = "";
        public string DungeonId = ""; // 原子兑换后从 Party 派生的地图，不接受 Client 自报。
        public uint RoomEpoch, ConnectionEpoch; // 每次兑换递增，旧连接与迟到认证回复必须失效。
        public ulong ActorId;
    }
}
