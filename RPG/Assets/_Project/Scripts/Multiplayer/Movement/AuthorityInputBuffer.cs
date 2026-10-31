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
        public long SupersededContinuousFrames { get; private set; }
        public void Reset(uint epoch)
        {
            Epoch = epoch; LastAcknowledged = _lastAccepted = 0;
            _head = _count = 0; _tokens = 12; _refill = _lastReceived = double.NaN; _held = default;
            SupersededContinuousFrames = 0;
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
                // DS卡顿后，Client仍按30Hz发包，而DS也只按30Hz消费；盲目FIFO会永久背着旧队列，
                // 32格容量甚至能变成约1秒控制延迟。方向/视角属于可由新值替代的连续状态，
                // 因此压到最新一帧后消费；保留两帧再消费会留下1帧旧方向，同频收发时可能永久保留。
                // 压缩只替换输入值，每个权威Tick仍只Simulate一次，绝不补跑获得额外速度。
                //
                // Jump/Dash边沿不能OR合并：先跳后Dash若揉成同帧，会改变原FSM优先级。
                // JumpHeld的变化影响Jump Cut，Suppressed变化影响UI/失焦门，也必须依序执行。
                // 只有安全连续帧被替代；遇到这些语义边界先停下，由本Tick消费它，下一Tick再压缩后段。
                while (_count > 1)
                {
                    var oldest = _frames[_head];
                    if (oldest.JumpPressed || oldest.DashPressed || oldest.JumpHeld != _held.JumpHeld ||
                        oldest.InputSuppressed != _held.InputSuppressed) break;
                    _head = (_head + 1) % _frames.Length; _count--; SupersededContinuousFrames++;
                }
                var frame = _frames[_head]; _head = (_head + 1) % _frames.Length; _count--;
                // Ack在权威Consume提交时才推进；更早的连续帧已明确由本帧替代，不再等待执行。
                // Client据此清除相应预测历史，恢复权威Pose后仅重放Ack之后的输入，避免旧队列再生。
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
