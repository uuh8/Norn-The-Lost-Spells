using System;

namespace Game.ElementField
{
    /// <summary>不同来源的恢复请求 FIFO 去重，避免后一个单位覆盖前一个单位的唤醒。</summary>
    internal sealed class FluidChunkWakeQueue
    {
        private readonly ElementChunkKey[] _keys;
        private readonly FluidDormantWakeReason[] _reasons;
        private int _head;
        internal int Count { get; private set; }
        internal uint RejectedCount { get; private set; }
        internal FluidChunkWakeQueue(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _keys = new ElementChunkKey[capacity];
            _reasons = new FluidDormantWakeReason[capacity];
        }
        internal bool TryEnqueue(ElementChunkKey key, FluidDormantWakeReason reason)
        {
            for (int i = 0; i < Count; i++)
                if (_keys[(_head + i) % _keys.Length] == key) return true;
            if (Count == _keys.Length) { RejectedCount++; return false; }
            int tail = (_head + Count++) % _keys.Length;
            _keys[tail] = key; _reasons[tail] = reason;
            return true;
        }
        internal bool TryDequeue(out ElementChunkKey key, out FluidDormantWakeReason reason)
        {
            key = default; reason = default;
            if (Count == 0) return false;
            key = _keys[_head]; reason = _reasons[_head];
            _head = (_head + 1) % _keys.Length; Count--;
            return true;
        }
    }
}
