using System;
using System.IO;
using Game.Networking;
using Mirror;

namespace Game.Multiplayer
{
    /// <summary>独立可靠消息身份；进度不依附某个玩家Actor的重连代次，也不复用历史冲突的ProtobufEnvelope。</summary>
    public struct DungeonProgressEnvelope : NetworkMessage { public ArraySegment<byte> Payload; }
    public static class DungeonProgressEnvelopeSerialization
    {
        public static void WriteDungeonProgressEnvelope(this NetworkWriter writer, DungeonProgressEnvelope message)
        {
            if (message.Payload.Array == null || message.Payload.Count < 1 || message.Payload.Count > DungeonProgressCodec.MaxPayloadBytes)
                throw new InvalidDataException("副本进度长度越界。");
            writer.WriteUInt((uint)message.Payload.Count); writer.WriteBytes(message.Payload.Array, message.Payload.Offset, message.Payload.Count);
        }
        public static DungeonProgressEnvelope ReadDungeonProgressEnvelope(this NetworkReader reader)
        {
            // 借用接收缓冲仅在当前回调有效；先验证长度，回调外只保存已解码的Core值。
            if (reader.Remaining < 4) throw new InvalidDataException("副本进度头截断。");
            uint size = reader.ReadUInt();
            if (size < 1 || size > DungeonProgressCodec.MaxPayloadBytes || size > reader.Remaining) throw new InvalidDataException("副本进度截断或超限。");
            return new DungeonProgressEnvelope { Payload = reader.ReadBytesSegment((int)size) };
        }
    }
}
