using System;
using Game.NetworkProtocol;
using Mirror;

namespace Game.Networking
{
    /// <summary>正式房间的外层消息身份；旧ProtobufEnvelope的Mirror ID与ObjectDestroyMessage碰撞，不能沿用。</summary>
    public struct RoomProtocolEnvelope : NetworkMessage
    {
        public RealtimeMessageKind Kind;
        public ArraySegment<byte> Payload;
    }
    public static class RoomProtocolEnvelopeSerialization
    {
        // 只改变Mirror外层类型ID；Kind/限长Payload复用已验证的序列化规则，无额外数组或对象分配。
        public static void WriteRoomProtocolEnvelope(this NetworkWriter writer, RoomProtocolEnvelope message) =>
            writer.WriteProtobufEnvelope(new ProtobufEnvelope { Kind = message.Kind, Payload = message.Payload });
        public static RoomProtocolEnvelope ReadRoomProtocolEnvelope(this NetworkReader reader)
        {
            var message = reader.ReadProtobufEnvelope(); return new RoomProtocolEnvelope { Kind = message.Kind, Payload = message.Payload };
        }
    }
}
