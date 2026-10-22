using System;

namespace Game.NetworkProtocol
{
    // 实例控制只在本机受控链路使用；实例密钥经环境变量传入，不能序列化到此描述或Client应答。
    [Serializable] public sealed class InstanceLaunchDescriptor
    {
        public string InstanceId = "", RoomId = "", RunId = "";
        public uint RoomEpoch;
        public int Port;
        public string BusinessBaseUrl = "";
        public uint ProtocolVersion;
        public string BuildVersion = "", ManifestHash = "";
        public int HeartbeatSeconds = 15;
        public int FreezeAfterSeconds = 60;
        public int IdleShutdownSeconds = 180;
    }
    [Serializable] public sealed class StartRoomRequest { public uint ExpectedRevision; }
    [Serializable] public sealed class RoomInstanceReply
    {
        public string InstanceId = "", RunId = "", State = "", Host = "127.0.0.1";
        public uint RoomEpoch;
        public int Port;
    }
    [Serializable] public sealed class InstanceReadyRequest
    {
        public uint RoomEpoch, ProtocolVersion;
        public string BuildVersion = "", ManifestHash = "";
        public int ProcessId;
        public long ProcessStartTicks; // UTC DateTime ticks，用来拒绝被复用的PID。
    }
    [Serializable] public sealed class InstanceHeartbeatRequest { public uint RoomEpoch; }
    [Serializable] public sealed class InstanceLeaseReply { public long LeaseExpiresUnixMilliseconds; }
    [Serializable] public sealed class InstanceReadyReply
    {
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
        public uint RoomEpoch, ConnectionEpoch; // 每次兑换递增，旧连接与迟到认证回复必须失效。
        public ulong ActorId;
    }
}
