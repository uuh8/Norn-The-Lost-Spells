using System;

namespace Game.ElementField
{
    /// <summary>
    /// 固定容量的 Spawn Command Ring Buffer。数组只在构造时分配一次；Projectile 高峰期只写入
    /// 已存在的 Slot，Runtime 再复制到长期 Upload Array，因此 Update/Simulation Tick 没有 List 扩容 GC。
    /// </summary>
    public sealed class FluidSpawnQueue : IFluidSpawnSink
    {
        private readonly FluidSpawnRequest[] _buffer;
        private int _head;
        private int _tail;
        private int _count;

        public FluidSpawnQueue(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));

            _buffer = new FluidSpawnRequest[capacity];
        }

        public int Capacity => _buffer.Length;
        public int Count => _count;
        public uint RejectedRequestCount { get; private set; }

        public bool TryEnqueue(in FluidSpawnRequest request)
        {
            if (_count >= _buffer.Length)
            {
                if (RejectedRequestCount < uint.MaxValue)
                    RejectedRequestCount++;
                return false;
            }

            _buffer[_tail] = request;
            _tail = (_tail + 1) % _buffer.Length;
            _count++;
            return true;
        }

        bool IFluidSpawnSink.TryEnqueueSpawn(in FluidSpawnRequest request) =>
            TryEnqueue(in request);

        /// <summary>
        /// 将当前批次以 FIFO 顺序复制到调用方的长期数组，并立即释放 Queue Slot。
        /// destination 太小时抛错且不消费任何命令，防止半批 Upload 破坏后续 Water 写入顺序。
        /// </summary>
        public int CopyAndClear(FluidSpawnRequest[] destination)
        {
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            if (destination.Length < _count)
                throw new ArgumentException("Destination is smaller than queued spawn requests.", nameof(destination));

            int copiedCount = _count;
            for (int i = 0; i < copiedCount; i++)
            {
                int sourceIndex = (_head + i) % _buffer.Length;
                destination[i] = _buffer[sourceIndex];
                _buffer[sourceIndex] = default;
            }

            _head = _tail;
            _count = 0;
            return copiedCount;
        }
    }
}
