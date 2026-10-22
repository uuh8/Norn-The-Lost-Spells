using System;
using System.IO;
using Game.NetworkProtocol;
using Mirror;

namespace Game.Networking
{
    /// <summary>Mirror 消息外壳；Payload 只在当前收包回调有效，不能跨帧持有。</summary>
    public struct ProtobufEnvelope : NetworkMessage
    {
        public RealtimeMessageKind Kind;
        public ArraySegment<byte> Payload;
    }

    /// <summary>显式 Serializer，先检查声明长度再读字节段，避免按恶意长度分配数组。</summary>
    public static class ProtobufEnvelopeSerialization
    {
        public static void WriteProtobufEnvelope(this NetworkWriter writer, ProtobufEnvelope envelope)
        {
            if (!ProtoMessageCodec.IsKnownKind(envelope.Kind) || envelope.Payload.Array == null ||
                envelope.Payload.Count < 1 || envelope.Payload.Count > ProtocolCompatibility.MaxPingPayloadBytes)
                throw new InvalidDataException("无效实时消息种类或字节预算。");
            writer.WriteUShort((ushort)envelope.Kind);
            writer.WriteUInt((uint)envelope.Payload.Count);
            writer.WriteBytes(envelope.Payload.Array, envelope.Payload.Offset, envelope.Payload.Count);
        }

        public static ProtobufEnvelope ReadProtobufEnvelope(this NetworkReader reader)
        {
            // Header 为 ushort kind + uint length；Mirror 自己的 messageId 不在此重复编码。
            if (reader.Remaining < 6) throw new InvalidDataException("实时消息头截断。");
            RealtimeMessageKind kind = (RealtimeMessageKind)reader.ReadUShort();
            uint length = reader.ReadUInt();
            if (!ProtoMessageCodec.IsKnownKind(kind) || length == 0 ||
                length > ProtocolCompatibility.MaxPingPayloadBytes || length > (uint)reader.Remaining)
                throw new InvalidDataException("无效实时消息头或长度。");
            return new ProtobufEnvelope { Kind = kind, Payload = reader.ReadBytesSegment((int)length) };
        }
    }
}
