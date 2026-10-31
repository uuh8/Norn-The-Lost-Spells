using System;
using System.IO;
using Mirror;

namespace Game.Networking
{
    /// <summary>独立正式消息身份；可靠页与控制使用同Schema，业务优先级在入Mirror前执行。</summary>
    public struct WorldEnvelope : NetworkMessage { public ArraySegment<byte> Payload; }
    public static class WorldEnvelopeSerialization
    {
        public static void WriteWorldEnvelope(this NetworkWriter writer,WorldEnvelope message)
        {
            if(message.Payload.Array==null || message.Payload.Count<1 || message.Payload.Count>WorldPageCodec.MaxPayloadBytes)
                throw new InvalidDataException("世界页长度越界。");
            writer.WriteUInt((uint)message.Payload.Count);writer.WriteBytes(message.Payload.Array,message.Payload.Offset,message.Payload.Count);
        }
        public static WorldEnvelope ReadWorldEnvelope(this NetworkReader reader)
        {
            if(reader.Remaining<4)throw new InvalidDataException("世界页头截断。");
            uint length=reader.ReadUInt();
            if(length<1 || length>WorldPageCodec.MaxPayloadBytes || length>reader.Remaining)
                throw new InvalidDataException("世界页截断或超限。");
            return new WorldEnvelope{Payload=reader.ReadBytesSegment((int)length)};
        }
    }
}
