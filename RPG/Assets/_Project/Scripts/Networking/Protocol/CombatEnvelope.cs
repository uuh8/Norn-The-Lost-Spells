using System;
using System.IO;
using Mirror;

namespace Game.Networking
{
    public struct CombatIntentEnvelope : NetworkMessage { public ArraySegment<byte> Payload; }
    public struct CombatFactEnvelope : NetworkMessage { public ArraySegment<byte> Payload; }
    public static class CombatEnvelopeSerialization
    {
        public static void WriteCombatIntent(this NetworkWriter writer, CombatIntentEnvelope message) => Write(writer, message.Payload);
        public static CombatIntentEnvelope ReadCombatIntent(this NetworkReader reader) => new CombatIntentEnvelope { Payload = Read(reader) };
        public static void WriteCombatFact(this NetworkWriter writer, CombatFactEnvelope message) => Write(writer, message.Payload);
        public static CombatFactEnvelope ReadCombatFact(this NetworkReader reader) => new CombatFactEnvelope { Payload = Read(reader) };
        private static void Write(NetworkWriter writer, ArraySegment<byte> bytes)
        {
            if (bytes.Array == null || bytes.Count < 1 || bytes.Count > CombatCodec.MaxPayloadBytes) throw new InvalidDataException("战斗消息越界。");
            writer.WriteUInt((uint)bytes.Count); writer.WriteBytes(bytes.Array, bytes.Offset, bytes.Count);
        }
        private static ArraySegment<byte> Read(NetworkReader reader)
        {
            if (reader.Remaining < 4) throw new InvalidDataException("战斗消息长度头截断。");
            uint size = reader.ReadUInt();
            if (size < 1 || size > CombatCodec.MaxPayloadBytes || size > reader.Remaining) throw new InvalidDataException("战斗消息越界或截断。");
            return reader.ReadBytesSegment((int)size);
        }
    }
}
