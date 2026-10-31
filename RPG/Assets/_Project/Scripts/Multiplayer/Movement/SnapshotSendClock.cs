namespace Game.Multiplayer
{
    /// <summary>
    /// 决定本渲染帧是否允许发送一份最新状态快照，供移动与战斗分别持有自己的时钟；
    /// 不负责组包、网络身份或游戏模拟，也不让两类独立频率互相消耗发送资格。
    /// 时钟只保存一个 deadline（下一次可发送的真实时间），可由 Server 每帧复用，无热路径分配。
    /// </summary>
    public sealed class SnapshotSendClock
    {
        private double _nextSend;
        private bool _started;

        /// <summary>
        /// 使用不受 Time.timeScale 影响的非负单调时间；无效时间或周期不取得发送资格。
        /// 首次调用立即发送；此后沿已有 deadline 推进，并保留晚帧剩余时间。
        /// 即使一帧错过很多周期，也只返回一次 true：过期历史状态没有保存，补发只会制造 burst。
        /// </summary>
        public bool TrySend(double now, float interval)
        {
            if (double.IsNaN(now) || double.IsInfinity(now) || now < 0 ||
                float.IsNaN(interval) || float.IsInfinity(interval) || interval <= 0) return false;

            double period = interval;
            double nextFromNow = now + period;
            // 有限但极端的输入也要有界拒绝。例如 now 过大、period 小于该量级 double
            // 的有效精度时，now + period == now；允许它会在同一时刻反复取得发送资格。
            if (double.IsInfinity(nextFromNow) || nextFromNow <= now) return false;
            if (!_started)
            {
                _started = true;
                _nextSend = nextFromNow;
                return true;
            }

            // Profile 的 interval 是 float，而 Unity 的真实时间是 double。
            // 1f/30f 比精确 1/30 秒略大；不能因此把刚好到达的帧推迟整整一帧。
            // 容差至多占周期的万分之一，并封顶 1 微秒，仅吸收数值误差，不吞掉帧抖动。
            double tolerance = System.Math.Min(.000001, period * .0001);
            if (now < _nextSend)
            {
                if (_nextSend - now > tolerance) return false;
                // 在容差内略早发送时，重新贴齐本帧，防止 float 周期误差逐次积累。
                _nextSend = nextFromNow;
                return true;
            }

            // 一次算出错过的整周期数，跳到首个未来 deadline；不通过 while 补发。
            // 例如周期 125 ms、deadline=10.125、now=10.8：下一次仍是 10.875，
            // 而非 now+period=10.925，因此晚帧的余数没有被重新计入等待。
            double skipped = System.Math.Floor((now - _nextSend) / period);
            double next = _nextSend + (skipped + 1) * period;
            // 极端跨度的浮点乘加可能失去一个 period 的精度；退回从当前时刻开始，
            // 保证同一时刻只发一次并彻底清债，而不是让无效 deadline 进入下一帧。
            _nextSend = double.IsInfinity(next) || next <= now + tolerance ? nextFromNow : next;
            return true;
        }

        /// <summary>会话结束时撤销旧 deadline，避免下一世界沿用上次会话的等待时间。</summary>
        public void Reset()
        {
            _nextSend = 0;
            _started = false;
        }
    }
}
