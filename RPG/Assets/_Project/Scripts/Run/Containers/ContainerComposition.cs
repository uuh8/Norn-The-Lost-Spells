using System;
using Game.Core;

namespace Game.Run
{
    /// <summary>容器实例的真实资产；只公开读取，256 槽启动分配，连续采集只改整数。</summary>
    public sealed class ContainerComposition
    {
        private readonly ulong[] _quantities = new ulong[256];
        public ulong Capacity { get; }
        public ulong Total { get; private set; }
        public uint Version { get; private set; }
        public ushort AllowedMaterials { get; }
        public ulong FreeCapacity => Capacity - Total;
        public ulong GetQuantity(byte key) => _quantities[key];
        public ContainerComposition(ulong capacity, ushort allowedMaterials)
        {
            if (capacity == 0 || (allowedMaterials & 0x1fe) == 0 || (allowedMaterials & ~0x1fe) != 0)
                throw new ArgumentException("容器需要正容量与已知兼容材料。");
            Capacity = capacity; AllowedMaterials = allowedMaterials;
        }
        public int CopyTo(MaterialAmount[] buffer)
        {
            int count = 0; for (int i = 1; i <= 8; i++) if (_quantities[i] > 0) count++;
            if (buffer == null || buffer.Length < count) throw new ArgumentException("组分快照不可静默截断。");
            count = 0; for (byte i = 1; i <= 8; i++) if (_quantities[i] > 0) buffer[count++] = new MaterialAmount(i, _quantities[i]);
            return count;
        }
        internal bool CanAdd(MaterialAmount[] amounts, int count)
        {
            if (amounts == null || count <= 0 || count > amounts.Length || count > 8) return false;
            ulong total = 0; ushort seen = 0;
            for (int i = 0; i < count; i++)
            {
                byte key = amounts[i].MaterialKey;
                if (!MaterialAmount.IsKnown(key) || (AllowedMaterials & 1 << key) == 0 || (seen & 1 << key) != 0
                    || amounts[i].Quantity > FreeCapacity - total) return false;
                seen |= (ushort)(1 << key); total += amounts[i].Quantity;
            }
            return total > 0;
        }
        internal void AddReserved(MaterialAmount[] amounts, int count)
        { for (int i = 0; i < count; i++) { _quantities[amounts[i].MaterialKey] += amounts[i].Quantity; Total += amounts[i].Quantity; } Version++; }
        internal bool CanRemove(MaterialAmount[] amounts, int count)
        {
            if (amounts == null || count <= 0 || count > 8 || count > amounts.Length) return false;
            ushort seen = 0; ulong total = 0;
            for (int i = 0; i < count; i++)
            {
                byte key = amounts[i].MaterialKey;
                if (!MaterialAmount.IsKnown(key) || (seen & 1 << key) != 0 || amounts[i].Quantity > _quantities[key]) return false;
                seen |= (ushort)(1 << key); total += amounts[i].Quantity;
            }
            return total > 0;
        }
        // 来源已持有库存锁；只有世界写入全部验证通过才调用，不在此发送外部事件。
        internal void RemoveReserved(MaterialAmount[] amounts, int count)
        { for (int i = 0; i < count; i++) { _quantities[amounts[i].MaterialKey] -= amounts[i].Quantity; Total -= amounts[i].Quantity; } Version++; }
        internal bool SameAs(ContainerComposition other)
        {
            if (other == null) return Total == 0;
            if (Total != other.Total || Capacity != other.Capacity || AllowedMaterials != other.AllowedMaterials) return false;
            for (int i = 1; i <= 8; i++) if (_quantities[i] != other._quantities[i]) return false;
            return true;
        }
    }
}
