using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class ElementCellSimulation
    {
        // 候选和活动队列都使用驻留槽的侵入式链接。一个格最多占一个候选，已释放的槽永远不会留下过期坐标。
        // Step 登记稳定主材；真正的跨 Owner 交接由世界在本次 Step 退出后进行。
        internal void QueueSleepingLiquid(Vector3Int cell)
        {
            if (!_indices.TryGetValue(cell, out int index)) return;
            ref Slot slot = ref _slots[index];
            var motion = ElementCellMaterialRules.Motion(slot.Value.Bulk);
            if (slot.Queued || slot.SleepingLiquid || slot.Value.Amount == 0 || slot.Value.Steam != 0 || slot.Value.Vapor != 0 ||
                _impulses.ContainsKey(cell) || motion != ElementCellMotion.Grain && motion != ElementCellMotion.Liquid &&
                slot.Value.Bulk != MaterialId.Ice && !(slot.Value.Bulk == MaterialId.DriedSlime && slot.Value.Amount == CellCapacity)) return;
            slot.SleepingLiquid = true;
            slot.SleepPrevious = _sleepLast; slot.SleepNext = -1;
            if (_sleepLast < 0) _sleepFirst = index; else _slots[_sleepLast].SleepNext = index;
            _sleepLast = index; _sleepCount++;
        }

        internal bool TryTakeSleepingLiquid(out Vector3Int cell)
        {
            if (_sleepFirst < 0) { cell = default; return false; }
            cell = _slots[_sleepFirst].Coordinate;
            RemoveSleepingLiquid(_sleepFirst);
            return true;
        }

        private void RemoveSleepingLiquid(int index)
        {
            ref Slot slot = ref _slots[index];
            if (!slot.SleepingLiquid) return;
            if (slot.SleepPrevious < 0) _sleepFirst = slot.SleepNext; else _slots[slot.SleepPrevious].SleepNext = slot.SleepNext;
            if (slot.SleepNext < 0) _sleepLast = slot.SleepPrevious; else _slots[slot.SleepNext].SleepPrevious = slot.SleepPrevious;
            slot.SleepingLiquid = false; _sleepCount--;
        }
    }
}
