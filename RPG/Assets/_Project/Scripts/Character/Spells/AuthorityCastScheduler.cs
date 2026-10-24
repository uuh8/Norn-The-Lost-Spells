using System;

namespace Game.Character
{
    /// <summary>
    /// 服务器延迟施法的有界逻辑时钟。槽位只在初始化分配；Tick 扫描不创建集合或闭包。
    /// callback 在离散施法时提供，冻结根 CommandId；没有 Authority Step 就没有时间流逝。
    /// </summary>
    public sealed class AuthorityCastScheduler
    {
        private struct Entry
        {
            internal uint Handle;
            internal ulong CommandId, EarliestStep;
            internal double Deadline;
            internal Action<ulong> Callback;
        }
        private readonly Entry[] _entries;
        private uint _nextHandle, _lastTick;
        private bool _hasTick, _stepping;
        private ulong _step;
        private double _time;
        public int PendingCount { get; private set; }
        public int Capacity => _entries.Length;
        public AuthorityCastScheduler(int capacity = 128)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _entries = new Entry[capacity];
        }
        public bool TrySchedule(float delaySeconds, ulong rootCommandId, Action<ulong> callback, out uint handle)
        {
            handle = 0;
            if (!float.IsFinite(delaySeconds) || delaySeconds < 0 || callback == null || rootCommandId == 0 || PendingCount >= Capacity)
                return false;
            // Handle 不复用，避免回滚持有的旧 handle 取消后来占据同一槽位的新施法。
            if (_nextHandle == uint.MaxValue) return false;
            for (int i = 0; i < _entries.Length; i++)
            {
                if (_entries[i].Handle != 0) continue;
                handle = ++_nextHandle;
                _entries[i] = new Entry { Handle = handle, CommandId = rootCommandId, Callback = callback,
                    Deadline = _time + delaySeconds, EarliestStep = _step + 1 };
                PendingCount++; return true;
            }
            return false;
        }
        public bool Cancel(uint handle)
        {
            if (handle == 0) return false;
            for (int i = 0; i < _entries.Length; i++)
                if (_entries[i].Handle == handle) { _entries[i] = default; PendingCount--; return true; }
            return false;
        }
        public void CancelAll()
        { Array.Clear(_entries, 0, _entries.Length); PendingCount = 0; }
        public void StepAuthority(float deltaTime, uint serverTick)
        {
            if (_stepping || !float.IsFinite(deltaTime) || deltaTime <= 0
                || _hasTick && unchecked((int)(serverTick - _lastTick)) <= 0) return;
            _hasTick = true; _lastTick = serverTick; _step++; _time += deltaTime;
            _stepping = true;
            try
            {
                for (int i = 0; i < _entries.Length; i++)
                {
                    Entry entry = _entries[i];
                    if (entry.Handle == 0 || entry.EarliestStep > _step || entry.Deadline > _time) continue;
                    // 先退槽再回调；回调中新加的零延迟项带下一 Step 门槛，不在同 Tick 递归释放。
                    _entries[i] = default; PendingCount--;
                    entry.Callback(entry.CommandId);
                }
            }
            finally { _stepping = false; }
        }
    }
}
