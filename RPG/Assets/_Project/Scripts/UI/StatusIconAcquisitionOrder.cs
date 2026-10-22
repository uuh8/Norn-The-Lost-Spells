using System;
using Game.Combat;

namespace Game.UI
{
    /// <summary>
    /// 只记录“当前激活状态的获得顺序”，不持有任何 UGUI 对象。
    /// 状态强度刷新不会重排；状态真正结束后再次获得，才会成为最新的最右项。
    /// 固定数组避免 StatusChangedEvent 高频刷新时产生 GC Alloc。
    /// </summary>
    public sealed class StatusIconAcquisitionOrder
    {
        private const int StatusCapacity = 4;

        private readonly bool[] _active = new bool[StatusCapacity];
        private readonly StatusKind[] _orderedKinds = new StatusKind[StatusCapacity];
        private int _activeCount;

        public int ActiveCount => _activeCount;

        /// <summary>
        /// 返回 true 表示这是一次“从无到有”的获得，调用方此时才应移动 Icon。
        /// </summary>
        public bool Activate(StatusKind kind)
        {
            int index = ToIndex(kind);
            if (_active[index])
                return false;

            _active[index] = true;
            _orderedKinds[_activeCount] = kind;
            _activeCount++;
            return true;
        }

        public bool Deactivate(StatusKind kind)
        {
            int index = ToIndex(kind);
            if (!_active[index])
                return false;

            _active[index] = false;
            int orderedIndex = FindOrderedIndex(kind);
            for (int i = orderedIndex; i < _activeCount - 1; i++)
                _orderedKinds[i] = _orderedKinds[i + 1];

            _activeCount--;
            _orderedKinds[_activeCount] = default;
            return true;
        }

        public StatusKind GetActiveKindAt(int index)
        {
            if (index < 0 || index >= _activeCount)
                throw new ArgumentOutOfRangeException(nameof(index));

            return _orderedKinds[index];
        }

        public void Clear()
        {
            Array.Clear(_active, 0, _active.Length);
            Array.Clear(_orderedKinds, 0, _orderedKinds.Length);
            _activeCount = 0;
        }

        private int FindOrderedIndex(StatusKind kind)
        {
            for (int i = 0; i < _activeCount; i++)
            {
                if (_orderedKinds[i] == kind)
                    return i;
            }

            throw new InvalidOperationException("Active status is missing from its acquisition order.");
        }

        private static int ToIndex(StatusKind kind)
        {
            int index = (int)kind;
            if (index < 0 || index >= StatusCapacity)
                throw new ArgumentOutOfRangeException(nameof(kind));

            return index;
        }
    }
}
