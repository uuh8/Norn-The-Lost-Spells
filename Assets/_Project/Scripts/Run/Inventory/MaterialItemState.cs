using System;
using Game.Core;

namespace Game.Run
{
    /// <summary>稳定固体进入背包后只保存身份/GMU，不继续运行世界 Solver 或创建每单位 GameObject。</summary>
    public sealed class MaterialItemState
    {
        public byte MaterialKey { get; }
        public ulong Quantity { get; private set; }
        // 和容器组分一样仅由持锁预约提交补量；不为每个持续采集Tick创建实例/候选Dictionary。
        internal void AddReserved(ulong amount) { Quantity=checked(Quantity+amount); }
        public MaterialItemState(byte key, ulong quantity)
        {
            if (!MaterialAmount.IsKnown(key) || quantity == 0) throw new ArgumentException("材料物品需要已知键和正数量。");
            MaterialKey = key; Quantity = quantity;
        }
    }
}
