using System;
using System.Collections.Generic;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// CPU 权威的三维材料数量表与活动队列。使用全局整数格作为键，跨 Chunk 的移动与块内移动共用一次提交。
    /// 世界基底／未接管的碰撞由只读回调提供。所有命令、查询与 Step 由同一世界线程顺序调用。
    /// </summary>
    public sealed partial class ElementCellSimulation : IMaterialQuantityReadOnly
    {
        // 只有四种液体中的一种或一个固体结构可以占主槽。两种气体独立计量，共享主槽余下的体积。
        private struct Contents
        {
            public MaterialId Bulk;
            public uint Amount, Steam, Vapor;
            public bool Empty => Amount == 0 && Steam == 0 && Vapor == 0;
            public uint Quantity(MaterialId material)
            {
                if (material == MaterialId.Steam) return Steam;
                if (material == MaterialId.ToxicVapor) return Vapor;
                return material == Bulk ? Amount : 0;
            }
            public uint GasSpace(uint capacity) => ElementCellMaterialRules.OccupiesWholeCell(Bulk) ? 0 : capacity - Amount;
            public uint Free(uint capacity) => GasSpace(capacity) - Steam - Vapor;
            public void Add(MaterialId material, uint amount)
            {
                if (material == MaterialId.Steam) Steam += amount;
                else if (material == MaterialId.ToxicVapor) Vapor += amount;
                else { Bulk = material; Amount += amount; }
            }
            public void Remove(MaterialId material, uint amount)
            {
                if (material == MaterialId.Steam) Steam -= amount;
                else if (material == MaterialId.ToxicVapor) Vapor -= amount;
                else { Amount -= amount; if (Amount == 0) Bulk = MaterialId.Empty; }
            }
        }

        private struct Slot
        {
            public Vector3Int Coordinate;
            public Contents Value, Movable;
            public ulong MovableStep, QueuedStep, RevisitStep;
            public int ResidentIndex, Previous, Next;
            public int SleepPrevious, SleepNext;
            public bool Queued, SleepingLiquid;
        }

        private static readonly Vector3Int[] Sides =
            { Vector3Int.right, Vector3Int.forward, Vector3Int.left, Vector3Int.back };
        private static readonly Vector3Int[] Faces =
            { Vector3Int.up, Vector3Int.down, Vector3Int.right, Vector3Int.forward, Vector3Int.left, Vector3Int.back };

        private readonly Func<Vector3Int, bool> _isBlocked;
        private readonly ElementCellMaterialRules.Transport _transport;
        private readonly Dictionary<Vector3Int, int> _indices;
        private readonly Slot[] _slots;
        private readonly int[] _residents, _freeSlots;
        private readonly ulong[] _totals = new ulong[256];
        private readonly Queue<Vector3Int> _changed;
        private readonly HashSet<Vector3Int> _changedSet;
        private readonly int _changeCapacity;
        private int _freeCount, _residentCount, _activeCount, _first = -1, _last = -1;
        private int _sleepFirst = -1, _sleepLast = -1, _sleepCount;
        private ulong _step;
        private bool _stepping, _retry;

        public ElementCellSimulation(int capacityPerCell, int maxCells, Func<Vector3Int, bool> isBlocked)
            : this(capacityPerCell, maxCells, isBlocked, null) { }

        public ElementCellSimulation(int capacityPerCell, int maxCells, Func<Vector3Int, bool> isBlocked,
            ElementCellTransportTuning[] transport)
        {
            if (capacityPerCell < 1) throw new ArgumentOutOfRangeException(nameof(capacityPerCell));
            if (maxCells < 1 || maxCells > int.MaxValue / 2) throw new ArgumentOutOfRangeException(nameof(maxCells));
            CellCapacity = (uint)capacityPerCell;
            _isBlocked = isBlocked;
            _transport = ElementCellMaterialRules.Bake(transport);
            _slots = new Slot[maxCells];
            _indices = new Dictionary<Vector3Int, int>(maxCells);
            _residents = new int[maxCells];
            _freeSlots = new int[maxCells];
            for (int i = maxCells - 1; i >= 0; i--) _freeSlots[_freeCount++] = i;
            _changeCapacity = Math.Max(256, maxCells * 2);
            _changed = new Queue<Vector3Int>(_changeCapacity);
            _changedSet = new HashSet<Vector3Int>(_changeCapacity);
        }

        public uint CellCapacity { get; }
        public int OccupiedCellCount => _residentCount;
        public int ActiveCellCount => _activeCount;
        public int ChangedCellCount => _changed.Count;
        public int SleepingLiquidCandidateCount => _sleepCount;
        internal int FreeResidentSlots => _freeCount;
        public ulong GetTotalQuantity(MaterialId material) => _totals[(byte)material];

        public bool TryGetQuantity(Vector3Int cell, MaterialId material, out uint amount)
        {
            amount = Read(cell).Quantity(material);
            return amount != 0;
        }

        public bool TryGetOccupiedCell(Vector3Int cell, out MaterialId material, out uint quantity)
        {
            var value = Read(cell);
            material = value.Bulk;
            if (value.Amount == 0 && !value.Empty)
                material = value.Steam >= value.Vapor ? MaterialId.Steam : MaterialId.ToxicVapor;
            quantity = value.Quantity(material);
            return quantity != 0;
        }

        /// <summary>迟绑定时读完整快照。索引只在两次写入之间稳定，后续逐格读取变化队列。</summary>
        public bool TryGetOccupiedCoordinate(int index, out Vector3Int cell)
        {
            if ((uint)index < (uint)_residentCount) { cell = _slots[_residents[index]].Coordinate; return true; }
            cell = default;
            return false;
        }

        public bool HasFluidOrGasAt(Vector3Int cell)
        {
            var value = Read(cell);
            return value.Steam != 0 || value.Vapor != 0 || ElementCellMaterialRules.Motion(value.Bulk) == ElementCellMotion.Liquid;
        }

        public bool HasSolidAt(Vector3Int cell)
        {
            var motion = ElementCellMaterialRules.Motion(Read(cell).Bulk);
            return Blocked(cell) || motion == ElementCellMotion.Fixed || motion == ElementCellMotion.Grain;
        }

        /// <summary>包含已清空的源格。多个写入只报告一次地址，消费者读取该地址的最后提交值。</summary>
        public bool TryDequeueChanged(out Vector3Int cell)
        {
            if (_changed.Count == 0) { cell = default; return false; }
            cell = _changed.Dequeue();
            _changedSet.Remove(cell);
            return true;
        }

        public void WakeNeighbors(Vector3Int cell)
        {
            Wake(cell);
            for (int i = 0; i < Faces.Length; i++)
                if (Offset(cell, Faces[i], out Vector3Int face)) Wake(face);
            // 斜下目标刚被挖空时，斜上方的沙子也必须醒来，即使直邻格本身完全静止。
            for (int i = 0; i < Sides.Length; i++)
                if (Offset(cell, Vector3Int.up + Sides[i], out Vector3Int diagonal)) Wake(diagonal);
        }

        public int Step(int budget)
        {
            if (budget <= 0 || _stepping || _activeCount == 0) return 0;
            _step++;
            int visited = 0;
            _stepping = true;
            try
            {
                // 新登记的一代全部排在尾部。预算用完保留原队首，忙碌的源格不能插队饿死远端候选。
                while (visited < budget && _first >= 0 && _slots[_first].QueuedStep != _step)
                {
                    Vector3Int cell = _slots[_first].Coordinate;
                    bool revisit = _slots[_first].RevisitStep == _step;
                    Unlink(_first);
                    _retry = false;
                    if (!Blocked(cell)) AdvanceCell(cell);
                    if (revisit || _retry) Wake(cell);
                    else QueueSleepingLiquid(cell);
                    visited++;
                }
            }
            finally { _stepping = false; }
            return visited;
        }

        private Contents Read(Vector3Int cell) => _indices.TryGetValue(cell, out int index) ? _slots[index].Value : default;
        private bool Blocked(Vector3Int cell) => _isBlocked != null && _isBlocked(cell);

        private uint Movable(Vector3Int cell, MaterialId material)
        {
            if (!_indices.TryGetValue(cell, out int index)) return 0;
            ref Slot slot = ref _slots[index];
            uint current = slot.Value.Quantity(material);
            return _stepping && slot.MovableStep == _step ? Math.Min(current, slot.Movable.Quantity(material)) : current;
        }

        internal static bool Offset(Vector3Int cell, Vector3Int direction, out Vector3Int result)
        {
            // 不能把 int.MaxValue 外的邻居卷绕到地图另一端；地形回调可以另外收紧实际的世界边界。
            long x = (long)cell.x + direction.x, y = (long)cell.y + direction.y, z = (long)cell.z + direction.z;
            if (x < int.MinValue || x > int.MaxValue || y < int.MinValue || y > int.MaxValue || z < int.MinValue || z > int.MaxValue)
            { result = default; return false; }
            result = new Vector3Int((int)x, (int)y, (int)z);
            return true;
        }

        private void Wake(Vector3Int cell)
        {
            if (!_indices.TryGetValue(cell, out int index)) return;
            ref Slot slot = ref _slots[index];
            RemoveSleepingLiquid(index);
            if (slot.Queued)
            {
                // 若今天尚待执行的格收到了新量，今天只能搬走此前的额度。记住明天还需再看一遍，
                // 避免旧候选被消费时，把仍需运输的新量一起留在休眠格里。
                if (_stepping && slot.QueuedStep != _step) slot.RevisitStep = _step;
                return;
            }
            if (slot.Value.Steam == 0 && slot.Value.Vapor == 0 && ElementCellMaterialRules.Motion(slot.Value.Bulk) == ElementCellMotion.Fixed)
                return;
            slot.Queued = true;
            slot.QueuedStep = _step;
            slot.Previous = _last;
            slot.Next = -1;
            if (_last >= 0) _slots[_last].Next = index; else _first = index;
            _last = index;
            _activeCount++;
        }

        private void Unlink(int index)
        {
            ref Slot slot = ref _slots[index];
            if (!slot.Queued) return;
            if (slot.Previous >= 0) _slots[slot.Previous].Next = slot.Next; else _first = slot.Next;
            if (slot.Next >= 0) _slots[slot.Next].Previous = slot.Previous; else _last = slot.Previous;
            slot.Queued = false;
            _activeCount--;
        }
    }
}
