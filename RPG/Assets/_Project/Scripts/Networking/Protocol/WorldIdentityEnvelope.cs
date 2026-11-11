using System;
using System.IO;
using Mirror;

namespace Game.Networking
{
    /// <summary>
    /// 认证后由 DS 单独发送的世界身份消息；Mirror 以完整类型名分配消息 ID，
    /// 与材料页分开，使握手可以在创建 Replica/接收材料页之前建立可信 WorldEpoch。
    /// Payload 是同步收发回调中的借用段，跨回调只能保留 Codec 验证后的身份值。
    /// </summary>
    public struct WorldIdentityEnvelope : NetworkMessage
    {
        public ArraySegment<byte> Payload;
    }

    public static class WorldIdentityEnvelopeSerialization
    {
        public static void WriteWorldIdentityEnvelope(this NetworkWriter writer, WorldIdentityEnvelope message)
        {
            var payload = message.Payload;
            // 先验证再写四字节头，失败不会把半个消息留给后续 Mirror Batching。
            if (payload.Array == null || payload.Count < 1 || payload.Count > WorldIdentityCodec.MaxPayloadBytes)
                throw new InvalidDataException("世界身份消息长度越界。");
            writer.WriteUInt((uint)payload.Count);
            writer.WriteBytes(payload.Array, payload.Offset, payload.Count);
        }

        public static WorldIdentityEnvelope ReadWorldIdentityEnvelope(this NetworkReader reader)
        {
            // 声明长度是网络输入，不能先转 int 或按它分配数组；确认真实剩余量后才借用接收缓冲。
            if (reader.Remaining < 4) throw new InvalidDataException("世界身份消息头截断。");
            uint length = reader.ReadUInt();
            if (length < 1 || length > WorldIdentityCodec.MaxPayloadBytes || length > reader.Remaining)
                throw new InvalidDataException("世界身份消息截断或超限。");
            return new WorldIdentityEnvelope { Payload = reader.ReadBytesSegment((int)length) };
        }
    }
}
