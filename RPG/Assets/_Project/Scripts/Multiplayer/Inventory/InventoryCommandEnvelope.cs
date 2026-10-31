using System;
using System.IO;
using Mirror;

namespace Game.Multiplayer
{
    // 与完整快照分开命名，Mirror按类型全名计算消息ID；不能复用早期发生碰撞的ProtobufEnvelope。
    public struct InventoryCommandEnvelope : NetworkMessage { public ArraySegment<byte> Payload; }
    public struct InventoryReceiptEnvelope : NetworkMessage { public ArraySegment<byte> Payload; }

    public static class InventoryCommandEnvelopeSerialization
    {
        private const int Maximum = 512;
        public static void WriteInventoryCommandEnvelope(this NetworkWriter writer, InventoryCommandEnvelope value) => Write(writer, value.Payload);
        public static void WriteInventoryReceiptEnvelope(this NetworkWriter writer, InventoryReceiptEnvelope value) => Write(writer, value.Payload);
        public static InventoryCommandEnvelope ReadInventoryCommandEnvelope(this NetworkReader reader) => new InventoryCommandEnvelope { Payload = Read(reader) };
        public static InventoryReceiptEnvelope ReadInventoryReceiptEnvelope(this NetworkReader reader) => new InventoryReceiptEnvelope { Payload = Read(reader) };
        private static void Write(NetworkWriter writer, ArraySegment<byte> bytes)
        {
            if (bytes.Array == null || bytes.Count < 1 || bytes.Count > Maximum) throw new InvalidDataException("库存命令长度越界。");
            writer.WriteUInt((uint)bytes.Count); writer.WriteBytes(bytes.Array, bytes.Offset, bytes.Count);
        }
        private static ArraySegment<byte> Read(NetworkReader reader)
        {
            // 在借用Mirror缓冲区前检查真实剩余长度。借用只在当前同步回调有效，后续只保存解码后的值。
            if (reader.Remaining < 4) throw new InvalidDataException("库存命令头截断。");
            uint size = reader.ReadUInt();
            if (size < 1 || size > Maximum || size > reader.Remaining) throw new InvalidDataException("库存命令越界或截断。");
            return reader.ReadBytesSegment((int)size);
        }
    }
}
