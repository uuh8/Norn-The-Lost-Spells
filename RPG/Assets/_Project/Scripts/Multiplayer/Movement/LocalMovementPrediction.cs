using Game.Core;

namespace Game.Multiplayer
{
    /// <summary>固定容量的本机输入历史；服务器恢复完整运动状态后，只重放尚未消费确认的输入。</summary>
    public sealed class LocalMovementPrediction
    {
        private readonly PlayerSimulationFrame[] _frames = new PlayerSimulationFrame[128];
        private readonly PlayerMovementSnapshot[] _poses = new PlayerMovementSnapshot[128];
        private int _head, _count;
        private uint _epoch, _lastRecorded, _lastAck, _missingThrough;
        public int Count => _count;
        public int LastReplayCount { get; private set; }
        public long OverflowCount { get; private set; }
        public float LastCorrectionDistance { get; private set; }
        public void Reset(uint epoch) { _epoch = epoch; _lastRecorded = _lastAck = _missingThrough = 0; ClearHistory(); }
        public void ClearHistory() { _head = _count = LastReplayCount = 0; _missingThrough = _lastRecorded; }
        public bool Record(in PlayerSimulationFrame frame, in PlayerMovementSnapshot pose)
        {
            if (frame.ConnectionEpoch != _epoch || frame.Sequence == 0 || frame.Sequence <= _lastRecorded) return false;
            if (_count == _frames.Length)
            { _missingThrough = _frames[_head].Sequence; _head = (_head + 1) % _frames.Length; _count--; OverflowCount++; }
            int slot = (_head + _count) % _frames.Length;
            _frames[slot] = frame; _poses[slot] = pose; _count++; _lastRecorded = frame.Sequence; return true;
        }
        public bool Reconcile(IPlayerMovementSimulation motor, in PlayerMovementSnapshot snapshot, uint ack, uint epoch, float dt)
        {
            if (motor == null || epoch != _epoch || ack > _lastRecorded || ack < _lastAck) return false;
            LastReplayCount = 0; LastCorrectionDistance = 0; _lastAck = ack;
            // 丢失历史时只能硬校正；不能只重放还在环里的后半段并称其是完整恢复。
            if (ack < _missingThrough) { motor.RestoreMovementSnapshot(snapshot); ClearHistory(); return true; }
            while (_count > 0 && _frames[_head].Sequence <= ack)
            {
                if (_frames[_head].Sequence == ack) LastCorrectionDistance = UnityEngine.Vector3.Distance(_poses[_head].Position, snapshot.Position);
                _head = (_head + 1) % _frames.Length; _count--;
            }
            motor.RestoreMovementSnapshot(snapshot);
            for (int i = 0; i < _count; i++)
            {
                int slot = (_head + i) % _frames.Length;
                motor.Simulate(_frames[slot], dt, true); _poses[slot] = motor.CaptureMovementSnapshot(); LastReplayCount++;
            }
            return true;
        }
    }
}
