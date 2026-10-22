using System;

namespace Game.ElementField
{
    /// <summary>
    /// Archive/Restore 期间保存原始流体写入的固定 Ring Buffer。
    /// 它保留完整 ElementWriteRequest，不在延迟期间重新计算速度、法线或 Falloff。
    /// </summary>
    public sealed class FluidPendingWriteQueue
    {
        private readonly ElementWriteRequest[] _items;
        private int _head;

        public int Count { get; private set; }
        public int Capacity => _items.Length;
        public uint RejectedCount { get; private set; }

        public FluidPendingWriteQueue(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _items = new ElementWriteRequest[capacity];
        }

        public bool TryEnqueue(in ElementWriteRequest request)
        {
            if (Count >= _items.Length)
            {
                if (RejectedCount < uint.MaxValue) RejectedCount++;
                return false;
            }
            int tail = (_head + Count) % _items.Length;
            _items[tail] = request;
            Count++;
            return true;
        }

        public bool TryPeek(out ElementWriteRequest request)
        {
            if (Count == 0) { request = default; return false; }
            request = _items[_head];
            return true;
        }

        public bool TryDequeue(out ElementWriteRequest request)
        {
            if (!TryPeek(out request)) return false;
            _items[_head] = default;
            _head = (_head + 1) % _items.Length;
            Count--;
            return true;
        }
    }
}
