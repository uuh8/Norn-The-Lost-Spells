using Game.Core;

namespace Game.Multiplayer
{
    /// <summary>延迟播放有限权威历史；冻结期间重新对齐两端时钟，不对未知未来做无限预测。</summary>
    public sealed class RemoteMovementInterpolation
    {
        private readonly MovementStateFrame[] _states = new MovementStateFrame[16];
        private int _head, _count;
        private uint _lastSequence, _lastTick;
        private double _offset;
        public void Reset() { _head = _count = 0; _lastSequence = _lastTick = 0; _offset = 0; }
        public bool TryAdd(in MovementStateFrame state, double receivedAt)
        {
            if (state.SnapshotSequence == 0 || state.SnapshotSequence <= _lastSequence || state.ServerTick < _lastTick) return false;
            if (_count == 0 || state.Frozen)
            {
                // Frozen只停模拟Tick、不停真实时间。清旧段避免解冻后跨越一分钟的无效时间轴。
                _offset = receivedAt - state.ServerTick / 30.0;
                _head = _count = 0;
            }
            _lastSequence = state.SnapshotSequence; _lastTick = state.ServerTick;
            if (_count == _states.Length) { _head = (_head + 1) % _states.Length; _count--; }
            _states[(_head + _count) % _states.Length] = state; _count++; return true;
        }
        public bool TrySample(double now, float delay, out PlayerMovementSnapshot pose)
        {
            pose = default; if (_count == 0) return false;
            double target = now - _offset - delay;
            var first = _states[_head]; pose = first.Movement;
            if (target <= first.ServerTick / 30.0) return true;
            for (int i = 1; i < _count; i++)
            {
                var next = _states[(_head + i) % _states.Length]; double end = next.ServerTick / 30.0;
                if (target <= end && end > first.ServerTick / 30.0)
                {
                    float t = (float)((target - first.ServerTick / 30.0) / (end - first.ServerTick / 30.0));
                    pose = next.Movement;
                    pose.Position = UnityEngine.Vector3.Lerp(first.Movement.Position, next.Movement.Position, t);
                    pose.Rotation = UnityEngine.Quaternion.Slerp(first.Movement.Rotation, next.Movement.Rotation, t);
                    return true;
                }
                first = next;
            }
            // 缺包时停在最新已知位置；不把最后一份速度无限外推到墙外。
            pose = first.Movement; return true;
        }
    }
}
