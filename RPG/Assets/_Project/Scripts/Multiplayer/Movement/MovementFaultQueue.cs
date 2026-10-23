using System;

namespace Game.Multiplayer
{
    /// <summary>Development验收使用的有界应用消息延迟/丢弃队列。作用于运动消息，不冒充底层UDP丢包。</summary>
    public sealed class MovementFaultQueue<T> where T : struct
    {
        private readonly T[] _values = new T[128];
        private readonly double[] _due = new double[128];
        private readonly bool[] _active = new bool[128];
        private int _pending, _latency, _jitter, _loss;
        private uint _random = 1;
        public bool Enabled => _latency != 0 || _jitter != 0 || _loss != 0;
        public int Pending => _pending;
        public long Dropped { get; private set; }
        public void Configure(int roundTripMilliseconds, int jitterMilliseconds, int lossPercent, uint seed)
        {
            Clear(); _latency = Math.Clamp(roundTripMilliseconds, 0, 1000);
            _jitter = Math.Clamp(jitterMilliseconds, 0, 200); _loss = Math.Clamp(lossPercent, 0, 50);
            _random = seed == 0 ? 1u : seed; Dropped = 0;
        }
        public void Clear() { Array.Clear(_active, 0, _active.Length); _pending = 0; }
        private uint Next() { _random ^= _random << 13; _random ^= _random >> 17; _random ^= _random << 5; return _random; }
        public bool Schedule(in T value, double now)
        {
            if (Next() % 100 < _loss || _pending == _values.Length) { Dropped++; return false; }
            for (int i = 0; i < _values.Length; i++) if (!_active[i])
            {
                // 每向加入RTT/2，Jitter表示每向的±毫秒；固定Seed让同一档位可以复查。
                int jitter = _jitter == 0 ? 0 : (int)(Next() % (uint)(_jitter * 2 + 1)) - _jitter;
                _values[i] = value; _due[i] = now + Math.Max(0, _latency * .5 + jitter) / 1000d;
                _active[i] = true; _pending++; return true;
            }
            return false;
        }
        public bool TryPop(double now, out T value)
        {
            int earliest = -1; double time = double.MaxValue;
            for (int i = 0; i < _values.Length; i++) if (_active[i] && _due[i] <= now && _due[i] < time) { earliest = i; time = _due[i]; }
            if (earliest < 0) { value = default; return false; }
            value = _values[earliest]; _active[earliest] = false; _pending--; return true;
        }
    }
}
