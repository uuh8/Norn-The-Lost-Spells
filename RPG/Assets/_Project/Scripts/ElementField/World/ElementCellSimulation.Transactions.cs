using System;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class ElementCellSimulation
    {
        // 一笔命令最多涉及中心和六个面邻居。所有探测共用预分配草稿，只有完整成功才改已发布的 Slot。
        private struct Edit
        {
            public Vector3Int Cell;
            public int Slot;
            public Contents Before, After;
            public bool Changed => Before.Bulk != After.Bulk || Before.Amount != After.Amount ||
                                   Before.Steam != After.Steam || Before.Vapor != After.Vapor;
        }

        private readonly Edit[] _edits = new Edit[7];
        private int _editCount;

        public bool TryDeposit(Vector3Int cell, MaterialId material, uint amount)
        {
            return !_stepping && amount != 0 && amount <= CellCapacity && ElementCellMaterialRules.Supports(material)
                   && !Blocked(cell) && PlanDeposit(cell, material, amount) && Commit();
        }

        public bool TryRemove(Vector3Int cell, MaterialId material, uint amount)
        {
            if (_stepping || amount == 0 || Read(cell).Quantity(material) < amount) return false;
            _editCount = 0;
            _edits[Stage(cell)].After.Remove(material, amount);
            return Commit();
        }

        /// <summary>同一逻辑格的等量转换。反应条件由调用方确定；不能把 Fire 强度当作等量的 GMU 消费。</summary>
        public bool TryConvert(Vector3Int cell, MaterialId source, MaterialId product, uint amount)
        {
            return !_stepping && Convert(cell, source, product, amount);
        }

        private bool Convert(Vector3Int cell, MaterialId source, MaterialId product, uint amount)
        {
            if (amount == 0 || source == product || !ElementCellMaterialRules.Supports(product) ||
                Movable(cell, source) < amount || Blocked(cell)) return false;
            _editCount = 0;
            int center = Stage(cell);
            _edits[center].After.Remove(source, amount);
            return StageDeposit(center, product, amount) && Commit();
        }

        /// <summary>观察与投放共用一套预检。气体必须能从邻面退让，世界槽和未读取的变化记录也要有位置。</summary>
        public uint GetFreeCapacity(Vector3Int cell, MaterialId material)
        {
            if (_stepping || !ElementCellMaterialRules.Supports(material) || Blocked(cell)) return 0;
            Contents value = Read(cell);
            uint high;
            if (ElementCellMaterialRules.Motion(material) == ElementCellMotion.Gas) high = value.Free(CellCapacity);
            else if (value.Amount == 0 || value.Bulk == material) high = CellCapacity - value.Amount;
            else return 0;
            if (high == 0 || PlanDeposit(cell, material, high) && CanCommit()) return high;
            // 常见情况在上面一次即返回。只有接近占满／需要部分挤气时才二分确切的整数上限。
            uint low = 0;
            while (low < high)
            {
                uint middle = low + (high - low + 1) / 2;
                if (PlanDeposit(cell, material, middle) && CanCommit()) low = middle; else high = middle - 1;
            }
            return low;
        }

        /// <summary>
        /// 容器跨格事务使用此上限。此范围内 TryDeposit 只动目标格，反向 TryRemove 即可还原；
        /// 普通的 GetFreeCapacity 还会考虑邻面挤气，那种移动不能由一次反向扣量恢复。
        /// </summary>
        public uint GetLocalFreeCapacity(Vector3Int cell, MaterialId material)
        {
            if (_stepping || !ElementCellMaterialRules.Supports(material) || Blocked(cell)) return 0;
            var value = Read(cell);
            bool gas = ElementCellMaterialRules.Motion(material) == ElementCellMotion.Gas;
            if (!gas && value.Amount != 0 && value.Bulk != material) return 0;
            uint room = value.Free(CellCapacity);
            if (ElementCellMaterialRules.OccupiesWholeCell(material))
                room = value.Steam == 0 && value.Vapor == 0 ? CellCapacity - value.Amount : 0;
            if (room == 0) return 0;
            _editCount = 0;
            _edits[Stage(cell)].After.Add(material, room);
            return CanCommit() ? room : 0;
        }

        private bool PlanDeposit(Vector3Int cell, MaterialId material, uint amount)
        {
            _editCount = 0;
            return StageDeposit(Stage(cell), material, amount);
        }

        private bool StageDeposit(int center, MaterialId material, uint amount)
        {
            ref Contents value = ref _edits[center].After;
            if (ElementCellMaterialRules.Motion(material) == ElementCellMotion.Gas)
            {
                if (amount > value.Free(CellCapacity)) return false;
                value.Add(material, amount);
                return true;
            }
            if (value.Amount != 0 && value.Bulk != material || amount > CellCapacity - value.Amount) return false;
            value.Add(material, amount);
            if (Excess(in value) == 0) return true;

            // 已有气体先退入已有邻格，再使用新槽。任一份气体没有去处，连同中心写入一起丢弃草稿。
            Vector3Int cell = _edits[center].Cell;
            for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < Faces.Length; i++)
            {
                if (!Offset(cell, Faces[i], out Vector3Int neighbor) || _indices.ContainsKey(neighbor) != (pass == 0) || Blocked(neighbor))
                    continue;
                MoveExcess(center, Stage(neighbor));
                if (Excess(in value) == 0) return true;
            }
            return false;
        }

        private uint Excess(in Contents value)
        {
            uint total = value.Steam + value.Vapor, capacity = value.GasSpace(CellCapacity);
            return total > capacity ? total - capacity : 0;
        }

        private void MoveExcess(int from, int to)
        {
            ref Edit source = ref _edits[from];
            ref Contents destination = ref _edits[to].After;
            uint excess = Excess(in source.After);
            if (excess == 0 || Excess(in destination) != 0) return;
            uint move = Math.Min(excess, destination.Free(CellCapacity));
            uint steam = Math.Min(move, AvailableInDraft(in source, MaterialId.Steam));
            uint vapor = Math.Min(move - steam, AvailableInDraft(in source, MaterialId.ToxicVapor));
            source.After.Steam -= steam; destination.Steam += steam;
            source.After.Vapor -= vapor; destination.Vapor += vapor;
        }

        private uint AvailableInDraft(in Edit edit, MaterialId material)
        {
            uint current = edit.After.Quantity(material);
            if (!_stepping) return current;
            uint starting = edit.Before.Quantity(material);
            uint spent = starting > current ? starting - current : 0;
            uint available = Movable(edit.Cell, material);
            return available > spent ? Math.Min(current, available - spent) : 0;
        }

        private int Stage(Vector3Int cell)
        {
            for (int i = 0; i < _editCount; i++) if (_edits[i].Cell == cell) return i;
            bool found = _indices.TryGetValue(cell, out int slot);
            Contents initial = found ? _slots[slot].Value : default;
            int index = _editCount++;
            _edits[index] = new Edit { Cell = cell, Slot = found ? slot : -1, Before = initial, After = initial };
            return index;
        }

        private bool CanCommit()
        {
            int cells = _residentCount, changes = _changed.Count;
            for (int i = 0; i < _editCount; i++)
            {
                ref Edit edit = ref _edits[i];
                if (!edit.Changed) continue;
                if (edit.Before.Empty) cells++;
                if (edit.After.Empty) cells--;
                if (!_changedSet.Contains(edit.Cell)) changes++;
            }
            return cells <= _slots.Length && changes <= _changeCapacity;
        }

        private bool Commit()
        {
            if (!CanCommit())
            {
                // 当前候选在下一预算轮继续尝试；容量不足时不扣源量，也不能丢掉已清空坐标的通知。
                if (_stepping) _retry = true;
                return false;
            }
            for (int i = 0; i < _editCount; i++)
            {
                ref Edit edit = ref _edits[i];
                if (!edit.Changed) continue;
                _totals[(byte)edit.Before.Bulk] -= edit.Before.Amount;
                _totals[(byte)MaterialId.Steam] -= edit.Before.Steam;
                _totals[(byte)MaterialId.ToxicVapor] -= edit.Before.Vapor;
                if (edit.After.Empty) Release(edit.Slot);
            }
            for (int i = 0; i < _editCount; i++)
            {
                ref Edit edit = ref _edits[i];
                if (!edit.Changed) continue;
                if (!edit.After.Empty)
                {
                    // 上一轮先回收全部空源格，即使驻留量正好到上限，整份跨格移动仍能复用原槽。
                    int index = edit.Before.Empty ? Allocate(edit.Cell) : edit.Slot;
                    ref Slot slot = ref _slots[index];
                    if (_stepping)
                    {
                        if (slot.MovableStep != _step) { slot.Movable = edit.Before; slot.MovableStep = _step; }
                        Spend(ref slot.Movable, in edit, edit.Before.Bulk);
                        Spend(ref slot.Movable, in edit, MaterialId.Steam);
                        Spend(ref slot.Movable, in edit, MaterialId.ToxicVapor);
                    }
                    slot.Value = edit.After;
                    _totals[(byte)edit.After.Bulk] += edit.After.Amount;
                    _totals[(byte)MaterialId.Steam] += edit.After.Steam;
                    _totals[(byte)MaterialId.ToxicVapor] += edit.After.Vapor;
                }
                if (_changedSet.Add(edit.Cell)) _changed.Enqueue(edit.Cell);
            }
            for (int i = 0; i < _editCount; i++) if (_edits[i].Changed)
            {
                ref Edit edit = ref _edits[i];
                if (edit.Before.Steam != edit.After.Steam || edit.Before.Vapor != edit.After.Vapor)
                    _gasEnvironment?.Observe(edit.Cell, in edit.Before, in edit.After);
                WakeNeighbors(edit.Cell);
            }
            _gasEnvironment?.FinishEdits();
            return true;
        }

        private static void Spend(ref Contents available, in Edit edit, MaterialId material)
        {
            uint before = edit.Before.Quantity(material), after = edit.After.Quantity(material);
            if (before > after) available.Remove(material, Math.Min(available.Quantity(material), before - after));
        }

        private int Allocate(Vector3Int cell)
        {
            int index = _freeSlots[--_freeCount];
            _slots[index] = new Slot { Coordinate = cell, ResidentIndex = _residentCount };
            _residents[_residentCount++] = index;
            _indices.Add(cell, index);
            return index;
        }

        private void Release(int index)
        {
            Unlink(index);
            RemoveSleepingLiquid(index);
            ref Slot slot = ref _slots[index];
            _indices.Remove(slot.Coordinate);
            _impulses.Remove(slot.Coordinate);
            int last = _residents[--_residentCount];
            _residents[slot.ResidentIndex] = last;
            _slots[last].ResidentIndex = slot.ResidentIndex;
            _freeSlots[_freeCount++] = index;
        }
    }
}
