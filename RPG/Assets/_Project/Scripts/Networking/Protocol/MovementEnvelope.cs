using System;
using System.IO;
using Mirror;

namespace Game.Networking
{
    /// <summary>正式N3输入消息；不复用存在历史ID碰撞的旧N0外壳。</summary>
    public struct MovementInputEnvelope : NetworkMessage { public ArraySegment<byte> Payload; }
    /// <summary>服务器逐Actor状态；Payload仅在当前网络回调有效。</summary>
    public struct MovementStateEnvelope : NetworkMessage { public ArraySegment<byte> Payload; }

    public static class MovementEnvelopeSerialization
    {
        public static void WriteMovementInputEnvelope(this NetworkWriter writer, MovementInputEnvelope message) => Write(writer, message.Payload);
        public static MovementInputEnvelope ReadMovementInputEnvelope(this NetworkReader reader) => new MovementInputEnvelope { Payload = Read(reader) };
        public static void WriteMovementStateEnvelope(this NetworkWriter writer, MovementStateEnvelope message) => Write(writer, message.Payload);
        public static MovementStateEnvelope ReadMovementStateEnvelope(this NetworkReader reader) => new MovementStateEnvelope { Payload = Read(reader) };

        private static void Write(NetworkWriter writer, ArraySegment<byte> payload)
        {
            if (payload.Array == null || payload.Count < 1 || payload.Count > MovementCodec.MaxPayloadBytes)
                throw new InvalidDataException("运动消息体越界。");
            // Mirror的messageId由消息类型决定，此处只写长度与原缓冲区，不new另一份Payload。
            writer.WriteUInt((uint)payload.Count);
            writer.WriteBytes(payload.Array, payload.Offset, payload.Count);
        }

        private static ArraySegment<byte> Read(NetworkReader reader)
        {
            if (reader.Remaining < 4) throw new InvalidDataException("运动消息头截断。");
            uint count = reader.ReadUInt();
            if (count < 1 || count > MovementCodec.MaxPayloadBytes || count > (uint)reader.Remaining)
                throw new InvalidDataException("运动消息体越界或截断。");
            // 借用Reader底层接收段；调用方须在回调内解码/复制数值，不能保存到下一帧。
            return reader.ReadBytesSegment((int)count);
        }
    }
}
