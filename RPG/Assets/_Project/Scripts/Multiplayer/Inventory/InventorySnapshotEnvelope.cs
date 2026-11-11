using System;
using System.IO;
using Game.Networking;
using Mirror;

namespace Game.Multiplayer
{
    /// <summary>完整库存是独立的可靠消息；不挤入512B战斗事实，也不复用已有Mirror消息身份。</summary>
    public struct InventorySnapshotEnvelope : NetworkMessage
    {
        public uint ConnectionEpoch;
        public ulong SnapshotSequence;
        public int TotalBytes, PageIndex, PageCount;
        public uint Checksum;
        public ArraySegment<byte> Payload;
        public InventorySnapshotPage Page => new InventorySnapshotPage(ConnectionEpoch, SnapshotSequence,
            TotalBytes, PageIndex, PageCount, Checksum, Payload);
    }

    public static class InventorySnapshotEnvelopeSerialization
    {
        public static void WriteInventorySnapshotEnvelope(this NetworkWriter writer, InventorySnapshotEnvelope message)
        {
            var bytes = message.Payload;
            if (!InventorySnapshotPaging.Valid(message.Page))
                throw new InvalidDataException("库存消息长度越界。");
            // 完整快照不直接送KCP；Envelope只携16KiB页，独立页头固定32B。
            writer.WriteUInt(message.ConnectionEpoch); writer.WriteULong(message.SnapshotSequence);
            writer.WriteUInt((uint)message.TotalBytes); writer.WriteUInt((uint)message.PageIndex);
            writer.WriteUInt((uint)message.PageCount); writer.WriteUInt(message.Checksum);
            writer.WriteUInt((uint)bytes.Count); writer.WriteBytes(bytes.Array, bytes.Offset, bytes.Count);
        }

        public static InventorySnapshotEnvelope ReadInventorySnapshotEnvelope(this NetworkReader reader)
        {
            // ReadBytesSegment借用Mirror接收缓冲，不new接收数组；必须先验证声明长度和实际剩余量。
            if (reader.Remaining < 32) throw new InvalidDataException("库存消息头截断。");
            uint epoch = reader.ReadUInt(); ulong sequence = reader.ReadULong();
            uint total = reader.ReadUInt(), index = reader.ReadUInt(), count = reader.ReadUInt(), checksum = reader.ReadUInt();
            uint size = reader.ReadUInt();
            if (total < 1 || total > InventorySnapshotPaging.MaxSnapshotBytes || index >= InventorySnapshotPaging.MaxPages
                || count < 1 || count > InventorySnapshotPaging.MaxPages || size < 1
                || size > InventorySnapshotPaging.MaxPageBytes || size > reader.Remaining)
                throw new InvalidDataException("库存消息体越界或截断。");
            var message = new InventorySnapshotEnvelope { ConnectionEpoch = epoch, SnapshotSequence = sequence,
                TotalBytes = (int)total, PageIndex = (int)index, PageCount = (int)count, Checksum = checksum,
                Payload = reader.ReadBytesSegment((int)size) };
            if (!InventorySnapshotPaging.Valid(message.Page)) throw new InvalidDataException("库存页元数据不一致。");
            return message;
        }
    }
}
