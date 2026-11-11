using System;
using System.IO;
using Mirror;

namespace Game.Networking
{
    /// <summary>独立可靠小包身份，不替换正式Room Ping或其它Gameplay Handler。</summary>
    public struct RunExitIntentEnvelope : NetworkMessage { public ArraySegment<byte> Payload; }
    public struct RunExitStatusEnvelope : NetworkMessage { public ArraySegment<byte> Payload; }

    public static class RunExitEnvelopeSerialization
    {
        public static void WriteRunExitIntentEnvelope(this NetworkWriter writer, RunExitIntentEnvelope value) => Write(writer, value.Payload);
        public static RunExitIntentEnvelope ReadRunExitIntentEnvelope(this NetworkReader reader) => new RunExitIntentEnvelope { Payload = Read(reader) };
        public static void WriteRunExitStatusEnvelope(this NetworkWriter writer, RunExitStatusEnvelope value) => Write(writer, value.Payload);
        public static RunExitStatusEnvelope ReadRunExitStatusEnvelope(this NetworkReader reader) => new RunExitStatusEnvelope { Payload = Read(reader) };
        private static void Write(NetworkWriter writer, ArraySegment<byte> payload)
        {
            if (payload.Array == null || payload.Count < 1 || payload.Count > RunExitCodec.MaxPayloadBytes)
                throw new InvalidDataException("个人退出消息体越界。");
            writer.WriteUInt((uint)payload.Count); writer.WriteBytes(payload.Array, payload.Offset, payload.Count);
        }
        private static ArraySegment<byte> Read(NetworkReader reader)
        {
            if (reader.Remaining < 4) throw new InvalidDataException("个人退出消息头截断。");
            uint length = reader.ReadUInt();
            if (length < 1 || length > RunExitCodec.MaxPayloadBytes || length > (uint)reader.Remaining)
                throw new InvalidDataException("个人退出消息体越界或截断。");
            // 借用Mirror回调缓冲，只在当前回调解码为值，不把此段存到下一帧。
            return reader.ReadBytesSegment((int)length);
        }
    }
}
