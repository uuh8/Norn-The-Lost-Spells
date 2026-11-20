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
        private long[] _retryTicks;

        public ElementWriteQueue(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));

            _buffer = new ElementWriteRequest[capacity];
        }

        public int Capacity => _buffer.Length;
        public int Count => _count;
        /// <summary>复制待写请求时保留FIFO与原队列，禁止用Dequeue/Requeue改变真实提交顺序。</summary>
        internal bool TryPeek(int index, out ElementWriteRequest request)
        {
            request = default;
            if ((uint)index >= (uint)_count) return false;
            request = _buffer[(_head + index) % _buffer.Length]; return true;
        }

        public bool TryEnqueue(in ElementWriteRequest request)
            => TryEnqueue(in request, 0);

        // 已接收的元胞投放可能在处理前被另一笔写入抢占空间。保留整份数量并记录重试时间，
        // Fire 和原有后端仍使用普通 FIFO，未使用延迟重试的队列无需分配附加数组。
        internal bool TryEnqueue(in ElementWriteRequest request, long retryTick)
        {
            if (_count >= _buffer.Length)
            {
                _rejectedEnqueues++;
                return false;
            }

            _buffer[_tail] = request;
            if (retryTick != 0 && _retryTicks == null) _retryTicks = new long[_buffer.Length];
            if (_retryTicks != null) _retryTicks[_tail] = retryTick;
            _tail = (_tail + 1) % _buffer.Length;
            _count++;
            return true;
        }

        bool IElementWriteSink.TryEnqueueWrite(in ElementWriteRequest request) =>
            TryEnqueue(in request);

        public bool TryDequeue(out ElementWriteRequest request)
            => TryDequeue(out request, out _);

        internal bool TryDequeue(out ElementWriteRequest request, out long retryTick)
        {
            retryTick = 0;
            if (_count == 0)
            {
                request = default;
                return false;
            }

            request = _buffer[_head];
            if (_retryTicks != null) retryTick = _retryTicks[_head];
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
