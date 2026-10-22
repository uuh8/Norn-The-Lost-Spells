using System;
using System.Collections.Generic;
using Game.Core;
using UnityEngine;

namespace Game.Run
{
    /// <summary>归属表的空间投影：占格是实际格点，包围框只用于边界检查，空洞不占用。</summary>
    public static class InventoryGrid
    {
        internal static InventoryFailureReason Validate(ItemCatalogIndex catalog, Dictionary<string, InventoryEntry> items)
        {
            foreach (InventoryEntry entry in items.Values)
            {
                ItemLocation location = entry.Location;
                if (location.Kind != ItemLocationKind.Backpack) continue;
                catalog.TryGetRules(entry.Item.ContentId, out InventoryItemRules rules);
                catalog.TryGetRules(items[location.OwnerId].Item.ContentId, out InventoryItemRules backpack);
                // 提升到 long 后相加，损坏坐标不能通过 int 溢出绕过检查。
                if ((long)location.X + rules.Shape.GetWidth(location.QuarterTurns) > backpack.BackpackWidth
                    || (long)location.Y + rules.Shape.GetHeight(location.QuarterTurns) > backpack.BackpackHeight)
                    return InventoryFailureReason.OutOfBounds;
                int stackCount = 1;
                foreach (InventoryEntry other in items.Values)
                {
                    if (string.Equals(entry.Item.InstanceId, other.Item.InstanceId, StringComparison.Ordinal)
                        || other.Location.Kind != ItemLocationKind.Backpack
                        || !string.Equals(location.OwnerId, other.Location.OwnerId, StringComparison.Ordinal)) continue;
                    catalog.TryGetRules(other.Item.ContentId, out InventoryItemRules otherRules);
                    if (!Overlaps(rules.Shape, location, otherRules.Shape, other.Location)) continue;
                    if (location != other.Location || !ItemStackOps.AreCompatible(entry.Item, other.Item)) return InventoryFailureReason.Occupied;
                    if (++stackCount > rules.MaxStack) return InventoryFailureReason.Capacity;
                }
            }
            return InventoryFailureReason.None;
        }
        public static bool Overlaps(InventoryShape a, in ItemLocation aLocation, InventoryShape b, in ItemLocation bLocation)
        {
            for (int i = 0; i < a.CellCount; i++)
            {
                Vector2Int ac = a.GetCell(i, aLocation.QuarterTurns);
                long ax = (long)aLocation.X + ac.x, ay = (long)aLocation.Y + ac.y;
                for (int j = 0; j < b.CellCount; j++)
                {
                    Vector2Int bc = b.GetCell(j, bLocation.QuarterTurns);
                    if (ax == (long)bLocation.X + bc.x && ay == (long)bLocation.Y + bc.y) return true;
                }
            }
            return false;
        }
    }
}
