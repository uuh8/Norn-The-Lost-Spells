using System;

namespace Game.Run
{
    public enum ItemLocationKind : byte
    {
        Warehouse = 0,
        Backpack = 1,
        WandSlot = 2,
        EquippedWand = 3,
        EquippedBackpack = 4,
        WorldItem = 5,
        ThrownContainer = 6,
        // 追加枚举值，旧存档的位置编号保持不变。专区属于玩家，不依赖普通背包外壳。
        SpellStorage = 7,
        // 真正的独立道具装备区；追加编号保持旧存档位置不变。
        EquippedTool = 8
    }

    /// <summary>
    /// 唯一位置的值快照。嵌套位置的 OwnerId 是父实例，根位置则是仓库/装备/世界上下文。
    /// 只在归属表存一份，UI 得到值副本不能直接搬动资产；90° 旋转用整数避免浮点占格误差。
    /// </summary>
    public readonly struct ItemLocation : IEquatable<ItemLocation>
    {
        public readonly ItemLocationKind Kind;
        public readonly string OwnerId;
        public readonly int X, Y, QuarterTurns, SlotIndex;

        public ItemLocation(ItemLocationKind kind, string ownerId, int x, int y, int turns, int slot)
        {
            Kind = kind;
            OwnerId = ownerId;
            X = x;
            Y = y;
            QuarterTurns = turns;
            SlotIndex = slot;
        }

        public static ItemLocation Warehouse =>
            new ItemLocation(ItemLocationKind.Warehouse, "warehouse", 0, 0, 0, -1);

        public bool HasParentInstance => Kind == ItemLocationKind.Backpack || Kind == ItemLocationKind.WandSlot;

        public bool Equals(ItemLocation other) => Kind == other.Kind
            && string.Equals(OwnerId, other.OwnerId, StringComparison.Ordinal)
            && X == other.X && Y == other.Y && QuarterTurns == other.QuarterTurns && SlotIndex == other.SlotIndex;

        public override bool Equals(object obj) => obj is ItemLocation other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)Kind;
                hash = hash * 397 ^ (OwnerId != null ? StringComparer.Ordinal.GetHashCode(OwnerId) : 0);
                hash = hash * 397 ^ X;
                hash = hash * 397 ^ Y;
                hash = hash * 397 ^ QuarterTurns;
                return hash * 397 ^ SlotIndex;
            }
        }

        public static bool operator ==(ItemLocation left, ItemLocation right) => left.Equals(right);
        public static bool operator !=(ItemLocation left, ItemLocation right) => !left.Equals(right);
    }
}
