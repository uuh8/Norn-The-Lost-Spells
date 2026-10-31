using System;
using System.IO;
using Game.Networking;
using Mirror;

namespace Game.Multiplayer
{
    /// <summary>完整库存是独立的可靠消息；不挤入512B战斗事实，也不复用已有Mirror消息身份。</summary>
    public struct InventorySnapshotEnvelope : NetworkMessage
    {
        public ArraySegment<byte> Payload;
    }

    public static class InventorySnapshotEnvelopeSerialization
    {
        public static void WriteInventorySnapshotEnvelope(this NetworkWriter writer, InventorySnapshotEnvelope message)
        {
            var bytes = message.Payload;
            if (bytes.Array == null || bytes.Count < 1 || bytes.Count > InventorySnapshotCodec.MaxPayloadBytes)
                throw new InvalidDataException("库存消息长度越界。");
            writer.WriteUInt((uint)bytes.Count); writer.WriteBytes(bytes.Array, bytes.Offset, bytes.Count);
        }

        public static InventorySnapshotEnvelope ReadInventorySnapshotEnvelope(this NetworkReader reader)
        {
            // ReadBytesSegment借用Mirror接收缓冲，不new接收数组；必须先验证声明长度和实际剩余量。
            if (reader.Remaining < 4) throw new InvalidDataException("库存消息头截断。");
            uint size = reader.ReadUInt();
            if (size < 1 || size > InventorySnapshotCodec.MaxPayloadBytes || size > reader.Remaining)
                throw new InvalidDataException("库存消息体越界或截断。");
            return new InventorySnapshotEnvelope { Payload = reader.ReadBytesSegment((int)size) };
        }
    }
}
