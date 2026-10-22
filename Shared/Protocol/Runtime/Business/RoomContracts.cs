using System;

namespace Game.NetworkProtocol
{
    // Room是开局前的成员聚合，Run是某一次入局；DTO没有Token、数据库连接或Scene加载能力。
    [Serializable] public sealed class CreateRoomRequest
    {
        public string DungeonId = ""; // 服务端白名单的配置键；不是Client任意指定的Scene路径。
    }
    [Serializable] public sealed class RoomReadyRequest
    {
        public uint ExpectedRevision; // 针对当前房间快照操作，成员变化后的旧按钮请求必须失败。
        public uint ExpectedInventoryVersion; // Ready绑定线上资产版本，不读取单机存档。
        public bool Ready;
    }
    [Serializable] public sealed class RoomReply
    {
        public string RoomId = "";
        public string DungeonId = "";
        public string LeaderPlayerId = "";
        public string State = ""; // Open / Starting / Running / Closed；Starting不等于副本已Ready或获得入场许可。
        public uint ReadyRevision;
        public uint RoomEpoch; // fencing token；失败后的旧实例不能释放下一次Run的资产。
        public string RunId = "";
        public RoomMemberReply[] Members = Array.Empty<RoomMemberReply>();
    }
    [Serializable] public sealed class RoomMemberReply
    {
        public string PlayerId = "";
        public string Username = "";
        public int Seat; // 0–3，数据库唯一约束保证不会出现第五席。
        public bool Ready;
        public uint InventoryVersion;
    }
}
