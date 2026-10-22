using System;
using Unity.Profiling;

namespace Game.Networking
{
    /// <summary>有界 Codec 调用的分配计数；必须先通过真实已知分配的校准，不能信任常数零。</summary>
    public sealed class CodecAllocationMeter : IDisposable
    {
        private ProfilerRecorder _recorder;
        private bool _overflowed;
        public long CalibrationEvents { get; }
        public long DotNetControlBytes { get; }
        public bool IsValid => _recorder.Valid && CalibrationEvents > 0 && !_overflowed;

        public CodecAllocationMeter(int capacity = 128)
        {
            if (capacity < 1 || capacity > 16384) throw new ArgumentOutOfRangeException(nameof(capacity));
            // 不使用 SumAllSamplesInFrame：逐事件收集才能在同一帧内隔离某次 Codec 调用。
            // 固定 native 容量；只在主线程建立一次，Begin/End 不创建 recorder 或托管缓冲区。
            _recorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", capacity,
                ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
            if (!_recorder.Valid) return;
            _recorder.Stop();
            Begin();
            long before = GC.GetAllocatedBytesForCurrentThread();
            byte[] control = new byte[1024];
            GC.KeepAlive(control);
            DotNetControlBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            CalibrationEvents = End();
        }

        public void Begin()
        {
            if (!_recorder.Valid) return;
            _recorder.Reset();
            _recorder.Start();
        }

        /// <summary>返回分配事件数而非 bytes；容量不足或记录器不可用时返回 -1，禁止误报零。</summary>
        public long End()
        {
            if (!_recorder.Valid) return -1;
            _recorder.Stop();
            int count = _recorder.Count;
            if (count >= _recorder.Capacity) _overflowed = true;
            return _overflowed ? -1 : count;
        }

        public void Dispose() { if (_recorder.Valid) _recorder.Dispose(); }
    }
}
