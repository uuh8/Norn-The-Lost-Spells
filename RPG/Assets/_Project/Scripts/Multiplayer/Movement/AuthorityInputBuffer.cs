using Game.Core;

namespace Game.Multiplayer
{
    /// <summary>每个已绑定Actor拥有固定输入容量；身份、序号与消费Ack不共享同一个计数器。</summary>
    public sealed class AuthorityInputBuffer
    {
        public uint Epoch { get; private set; }
        public uint LastAcknowledged { get; private set; }
        private readonly PlayerSimulationFrame[] _frames = new PlayerSimulationFrame[32];
        private int _head, _count;
        private uint _lastAccepted;
        private double _lastReceived, _refill;
        private double _tokens;
        private PlayerSimulationFrame _held;
        public int PendingCount => _count;
        public void Reset(uint epoch)
        {
            Epoch = epoch; LastAcknowledged = _lastAccepted = 0;
            _head = _count = 0; _tokens = 12; _refill = _lastReceived = double.NaN; _held = default;
        }
        public bool TryEnqueue(in PlayerSimulationFrame frame, uint boundEpoch, double now)
        {
            if (Epoch == 0 || boundEpoch != Epoch || frame.ConnectionEpoch != Epoch ||
                frame.Sequence == 0 || frame.Sequence <= _lastAccepted || _count == _frames.Length ||
                !Finite(frame.MoveAxis.x) || !Finite(frame.MoveAxis.y) || !Finite(frame.ViewYaw) ||
                UnityEngine.Mathf.Abs(frame.MoveAxis.x) > 1 || UnityEngine.Mathf.Abs(frame.MoveAxis.y) > 1 ||
                double.IsNaN(now) || double.IsInfinity(now)) return false;
            if (double.IsNaN(_refill)) _refill = now;
            _tokens = System.Math.Min(12, _tokens + System.Math.Max(0, now - _refill) * 60); _refill = now;
            if (_tokens < 1) return false;
            _tokens--; _lastAccepted = frame.Sequence; _lastReceived = now;
            _frames[(_head + _count) % _frames.Length] = frame; _count++; return true;
        }
        public PlayerSimulationFrame Consume(double now)
        {
            // 长时间没有新包时连旧积压也丢弃，不能靠先前发送的大队列延长失联控制。
            if (!double.IsNaN(_lastReceived) && now - _lastReceived <= .2 && _count > 0)
            {
                var frame = _frames[_head]; _head = (_head + 1) % _frames.Length; _count--;
                LastAcknowledged = frame.Sequence; _held = frame; return frame;
            }
            if (double.IsNaN(_lastReceived) || now - _lastReceived > .2) Neutralize();
            var held = _held; held.Sequence = LastAcknowledged; held.ConnectionEpoch = Epoch;
            held.JumpPressed = held.DashPressed = false; return held;
        }
        public void Neutralize()
        {
            _head = _count = 0;
            _held = new PlayerSimulationFrame(LastAcknowledged, Epoch, UnityEngine.Vector2.zero, _held.ViewYaw, false, false, false, true);
            // 清按键不清_lastAccepted：Frozen/断线的迟到旧包仍不得重新取得资格。
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
