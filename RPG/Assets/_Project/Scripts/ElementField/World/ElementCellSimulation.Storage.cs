using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class ElementCellSimulation
    {
        // Slot 被运动、气体和休眠队列以整数索引引用。扩容只增长页面目录，已发布的页和 index 均保持稳定。
        // 小型构造器的最后一页严格截在指定上限；清空后的槽归还本世界并复用，避免反复搬运时不停申请页。
        private sealed class ResidentPages<T>
        {
            private const int Shift = 9, Size = 1 << Shift;
            private T[][] _pages = Array.Empty<T[]>();
            internal int Capacity { get; private set; }

            internal ref T this[int index]
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => ref _pages[index >> Shift][index & (Size - 1)];
            }

            internal void Ensure(int required, int limit)
            {
                while (Capacity < required)
                {
                    int page = Capacity >> Shift;
                    if (page == _pages.Length) Array.Resize(ref _pages, Math.Max(1, page * 2));
                    int count = Math.Min(Size, limit - Capacity);
                    _pages[page] = new T[count];
                    Capacity += count;
                }
            }
        }

        // 空世界只有小型分表目录。新地址只扩大所属分表，跨过旧上限时不用一次重排全世界的地址。
        // 分表不负责遍历顺序；晚绑定快照仍按稠密的 _residents 枚举，同格移动仍以一次事务发布。
        private sealed class ResidentAddressIndex
        {
            private const int Shards = 64;
            private readonly Dictionary<Vector3Int, int>[] _shards = new Dictionary<Vector3Int, int>[Shards];

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal bool TryGetValue(Vector3Int cell, out int slot)
            {
                var shard = _shards[AddressHash(cell) & (Shards - 1)];
                if (shard != null) return shard.TryGetValue(cell, out slot);
                slot = default;
                return false;
            }

            internal bool ContainsKey(Vector3Int cell) => TryGetValue(cell, out _);

            internal void Add(Vector3Int cell, int slot)
            {
                int part = AddressHash(cell) & (Shards - 1);
                var shard = _shards[part] ?? (_shards[part] = new Dictionary<Vector3Int, int>());
                shard.Add(cell, slot);
            }

            internal void Remove(Vector3Int cell) => _shards[AddressHash(cell) & (Shards - 1)].Remove(cell);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int AddressHash(Vector3Int cell)
        {
            uint hash = unchecked((uint)cell.x * 73856093u ^ (uint)cell.y * 19349663u ^ (uint)cell.z * 83492791u);
            return unchecked((int)(hash ^ (hash >> 16)));
        }

        // 正式上限独立于当前物理页数。逻辑预检（包括完全只读的 GetFreeCapacity）成功后，
        // 只在真提交且净新增的驻留超过物理页时申请；先释放空源，再发放新目标，可满载原位换槽。
        private void PrepareResidentPages()
        {
            if (_residentCount <= _slots.Capacity - _editCount) return;
            int required = _residentCount;
            for (int i = 0; i < _editCount; i++)
            {
                ref Edit edit = ref _edits[i];
                if (edit.Before.Empty != edit.After.Empty) required += edit.Before.Empty ? 1 : -1;
            }
            if (required <= _slots.Capacity) return;
            _residents.Ensure(required, _residentLimit);
            _slots.Ensure(required, _residentLimit);
        }

        private void EnsureStepLookups()
        {
            int desired = Math.Min(_residentLimit, Math.Max(32, Math.Min(_residentCount, 4096) * 2));
            if (_lookups.Length >= desired) return;
            int count = 1;
            while (count < desired) count <<= 1;
            _lookups = new StepLookup[count];
        }
    }
}
