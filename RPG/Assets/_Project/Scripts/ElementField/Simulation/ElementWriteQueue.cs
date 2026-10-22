using System;

namespace Game.ElementField
{
    /// <summary>
    /// 固定容量 Ring Buffer。容量在初始化时确定，运行时只移动 Head/Tail，
    /// 避免 List 扩容和 Projectile 命中高峰造成不可预测的 GC Alloc。
    /// </summary>
    public sealed class ElementWriteQueue : IElementWriteSink
    {
        private readonly ElementWriteRequest[] _buffer;
        private int _head;
        private int _tail;
        private int _count;
        private int _rejectedEnqueues;

        public ElementWriteQueue(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));

            _buffer = new ElementWriteRequest[capacity];
        }

        public int Capacity => _buffer.Length;
        public int Count => _count;

        public bool TryEnqueue(in ElementWriteRequest request)
        {
            if (_count >= _buffer.Length)
            {
                _rejectedEnqueues++;
                return false;
            }

            _buffer[_tail] = request;
            _tail = (_tail + 1) % _buffer.Length;
            _count++;
            return true;
        }

        bool IElementWriteSink.TryEnqueueWrite(in ElementWriteRequest request) =>
            TryEnqueue(in request);

        public bool TryDequeue(out ElementWriteRequest request)
        {
            if (_count == 0)
            {
                request = default;
                return false;
            }

            request = _buffer[_head];
            _buffer[_head] = default;
            _head = (_head + 1) % _buffer.Length;
            _count--;
            return true;
        }

        internal int TakeRejectedEnqueueCount()
        {
            int count = _rejectedEnqueues;
            _rejectedEnqueues = 0;
            return count;
        }
    }
}
