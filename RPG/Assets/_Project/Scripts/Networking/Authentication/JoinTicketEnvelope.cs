using System;
using System.IO;
using Mirror;

namespace Game.Networking
{
    public struct JoinTicketRequestEnvelope : NetworkMessage { public ArraySegment<byte> Payload; }
    public struct JoinTicketReplyEnvelope : NetworkMessage { public ArraySegment<byte> Payload; }
    public static class JoinTicketEnvelopeSerialization
    {
        public static void WriteJoinTicketRequestEnvelope(this NetworkWriter w, JoinTicketRequestEnvelope m) => Write(w, m.Payload);
        public static JoinTicketRequestEnvelope ReadJoinTicketRequestEnvelope(this NetworkReader r) => new JoinTicketRequestEnvelope { Payload = Read(r) };
        public static void WriteJoinTicketReplyEnvelope(this NetworkWriter w, JoinTicketReplyEnvelope m) => Write(w, m.Payload);
        public static JoinTicketReplyEnvelope ReadJoinTicketReplyEnvelope(this NetworkReader r) => new JoinTicketReplyEnvelope { Payload = Read(r) };
        private static void Write(NetworkWriter writer, ArraySegment<byte> payload)
        {
            if (payload.Array == null || payload.Count < 1 || payload.Count > 512) throw new InvalidDataException("入场消息越界。");
            writer.WriteUInt((uint)payload.Count); writer.WriteBytes(payload.Array, payload.Offset, payload.Count);
        }
        private static ArraySegment<byte> Read(NetworkReader reader)
        {
            // 先校验声明长度，再取得接收缓冲区的借用段；不能先按恶意长度new byte[]。
            if (reader.Remaining < 4) throw new InvalidDataException("入场消息头截断。");
            uint size = reader.ReadUInt(); if (size < 1 || size > 512 || size > reader.Remaining) throw new InvalidDataException("入场消息体越界。");
            return reader.ReadBytesSegment((int)size);
        }
    }
    public sealed class JoinConnectionEpochs
    {
        private readonly uint[] _epochs = new uint[4];
        public bool TryAdvance(ulong actorId, uint epoch)
        {
            if (actorId < 1 || actorId > 4 || epoch == 0 || epoch <= _epochs[(int)actorId - 1]) return false;
            _epochs[(int)actorId - 1] = epoch; return true;
        }
        public void Clear() => Array.Clear(_epochs, 0, _epochs.Length);
    }
}
