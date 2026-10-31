using System;
using Game.Core;
using UnityEngine;

namespace Game.Multiplayer
{
    /// <summary>
    /// Replica的有限历史播放器：只根据已收到的ServerTick生成表现Pose，不执行角色规则或预测未知未来。
    /// ServerTick可能因DS有界补步而永久少走，不能用首包的时钟offset把它等同于本机实时时钟。
    /// 因此独立推进playback timeline，并以剩余历史反馈调速；欠载停住并重建headroom，而不是永久逐包跳动。
    /// 所有热路径只读写预分配的16格ring与标量，Configure/Reset也不创建新的集合或委托。
    /// </summary>
    public sealed class RemoteMovementInterpolation
    {
        private readonly MovementStateFrame[] _states = new MovementStateFrame[16];
        private const double TickRate = 30.0;
        private const double Epsilon = .0000001;
        private int _head, _count;
        private uint _lastSequence, _lastTick;
        private double _playback, _lastWall, _lastReceived, _timescale = 1, _underflowAt;
        private double _deliveryMean, _deliveryVariance;
        private float _interval = 1f / 15f, _minimumDelay = .1f, _maximumDelay = .2f, _effectiveDelay = .1f;
        private float _lastSegmentSpeed;
        private Vector3 _lastSegmentVelocity;
        private bool _initialized, _frozen, _rebuffering, _hasDeliverySample;

        public int BufferedCount => _count;
        /// <summary>尚未播放的模拟时间，不是网络RTT或真实输入延迟；没有权威未来时为0。</summary>
        public float BufferedSeconds => _count == 0 ? 0 : (float)Math.Max(0, LatestTime - _playback);
        public double PlaybackTime => _playback;
        public float EffectiveDelay => _effectiveDelay;
        public int UnderflowCount { get; private set; }
        public int ResyncCount { get; private set; }
        public int ReceivedCount { get; private set; }
        /// <summary>当前权威段的水平米/秒；短欠载只保留一个发送周期，绝不据此继续移动角色。</summary>
        public float PlanarSpeed { get; private set; }
        /// <summary>
        /// 与PlanarSpeed同一段、同一生命周期的世界水平速度，单位米/秒，y恒为0。
        /// Boss的2D Blend Tree需要真实方向，不能把“正在移动”这个布尔量当成moveX/moveZ。
        /// 它只供表现读取，既不外推Position，也不授予Replica运行CharacterController.Move的权限。
        /// </summary>
        public Vector3 PlanarVelocity { get; private set; }
        private double LatestTime => _states[(_head + _count - 1) % _states.Length].ServerTick / TickRate;

        /// <summary>
        /// 由Registry在首包前提供当前Profile。minimumDelay是稳定链路的目标缓冲，maximumDelay限制jitter额外成本。
        /// 运行中更新配置只改变追踪目标，不能把已展示的playback往回拨。Reset会保留配置以供新Epoch复用。
        /// </summary>
        public void Configure(float interval, float minimumDelay, float maximumDelay = .2f)
        {
            _interval = IsFinite(interval) ? Mathf.Max(.01f, interval) : 1f / 15f;
            _minimumDelay = IsFinite(minimumDelay) ? Mathf.Max(0, minimumDelay) : .1f;
            _maximumDelay = IsFinite(maximumDelay) ? Mathf.Max(_minimumDelay, maximumDelay) : Mathf.Max(_minimumDelay, .2f);
            _effectiveDelay = Mathf.Clamp(_effectiveDelay, _minimumDelay, _maximumDelay);
            if (!_initialized) _effectiveDelay = _minimumDelay;
        }

        public void Reset()
        {
            _head = _count = 0; _lastSequence = _lastTick = 0;
            _playback = _lastWall = _lastReceived = _underflowAt = 0;
            _deliveryMean = _deliveryVariance = 0; _timescale = 1;
            _initialized = _frozen = _rebuffering = _hasDeliverySample = false;
            _effectiveDelay = _minimumDelay; _lastSegmentSpeed = PlanarSpeed = 0;
            _lastSegmentVelocity = PlanarVelocity = Vector3.zero;
            UnderflowCount = ResyncCount = ReceivedCount = 0;
        }

        public bool TryAdd(in MovementStateFrame state, double receivedAt)
        {
            if (!IsFinite(receivedAt) || state.SnapshotSequence == 0 || state.SnapshotSequence <= _lastSequence || state.ServerTick < _lastTick) return false;
            double remoteTime = state.ServerTick / TickRate;
            if (!_initialized || state.Frozen)
            {
                // Frozen快照持续提供新的wall锚点，清除旧段和旧jitter；首个解冻包保留这个锚点继续推进。
                // 若解冻时再次从新包初始化，会白白多等一次delay，也会丢失冻结末包到解冻首包之间的正确时间。
                _head = _count = 0;
                _playback = remoteTime - _minimumDelay; _lastWall = receivedAt; _timescale = 1;
                _effectiveDelay = _minimumDelay; _deliveryMean = _deliveryVariance = 0;
                _rebuffering = _hasDeliverySample = false; _initialized = true;
                _lastSegmentSpeed = PlanarSpeed = 0;
                _lastSegmentVelocity = PlanarVelocity = Vector3.zero;
            }
            else
            {
                // 收包和Render共用同一推进入口。先用旧timescale结算到receivedAt，再计算反馈，
                // 不能把尚未执行的Render时间误算成积压，也不能让新timescale追溯作用于旧时间。
                AdvanceTo(receivedAt);
                if (!_frozen) UpdateDelivery(receivedAt);
            }
            _lastReceived = Math.Max(_lastReceived, receivedAt);
            _lastSequence = state.SnapshotSequence; _lastTick = state.ServerTick; ReceivedCount++;
            _frozen = state.Frozen;
            // 同Tick的新序号可更新连接/冻结事实，但不是额外的一段运动历史，不能除以零求速度。
            if (_count > 0 && _states[(_head + _count - 1) % _states.Length].ServerTick == state.ServerTick)
                _states[(_head + _count - 1) % _states.Length] = state;
            else
            {
                if (_count == _states.Length) { _head = (_head + 1) % _states.Length; _count--; }
                _states[(_head + _count) % _states.Length] = state; _count++;
            }
            if (!_frozen) AdjustPlayback();
            return true;
        }

        public bool TrySample(double now, float delay, out PlayerMovementSnapshot pose)
        {
            pose = default; if (_count == 0 || !IsFinite(now)) return false;
            // 保留已有调用API：delay是调用方的最低缓冲，动态jitter仍受Configure的上限限制。
            // Profile应在首包前Configure；运行中改变delay只改变目标，不倒放已展示的角色。
            if (IsFinite(delay) && delay >= 0 && Math.Abs(delay - _minimumDelay) > Epsilon)
            {
                _minimumDelay = delay; _maximumDelay = Mathf.Max(_maximumDelay, delay);
                _effectiveDelay = Mathf.Clamp(_effectiveDelay, _minimumDelay, _maximumDelay);
            }
            AdvanceTo(now);
            if (_frozen) { pose = _states[(_head + _count - 1) % _states.Length].Movement; PlanarSpeed = 0; PlanarVelocity = Vector3.zero; return true; }
            var first = _states[_head]; pose = first.Movement;
            if (_playback < first.ServerTick / TickRate - Epsilon) { PlanarSpeed = 0; PlanarVelocity = Vector3.zero; return true; }
            for (int i = 1; i < _count; i++)
            {
                var next = _states[(_head + i) % _states.Length];
                double start = first.ServerTick / TickRate, end = next.ServerTick / TickRate;
                if (_playback <= end + Epsilon && end > start)
                {
                    float t = Mathf.Clamp01((float)((_playback - start) / (end - start)));
                    // Position/Rotation是连续量，可以插值；Grounded/Locomotion等离散事实必须在next的时间边界才生效。
                    pose = _playback >= end - Epsilon ? next.Movement : first.Movement;
                    pose.Position = Vector3.Lerp(first.Movement.Position, next.Movement.Position, t);
                    pose.Rotation = Quaternion.Slerp(first.Movement.Rotation, next.Movement.Rotation, t);
                    Vector3 delta = next.Movement.Position - first.Movement.Position;
                    // 除以ServerTick形成的段时长，与本Render帧deltaTime无关；
                    // 先去掉竖直分量，跳跃/落地不能误成为水平跑步或侧移。
                    _lastSegmentVelocity = new Vector3((float)(delta.x / (end - start)), 0f,
                        (float)(delta.z / (end - start)));
                    _lastSegmentSpeed = (float)(Math.Sqrt(delta.x * delta.x + delta.z * delta.z) / (end - start));
                    ApplySegmentSpeed(now);
                    // 保留当前两端；丢弃绝不会再播放的旧段，使16格容量真正留给未来headroom。
                    if (i > 1) { _head = (_head + i - 1) % _states.Length; _count -= i - 1; }
                    return true;
                }
                first = next;
            }
            pose = first.Movement; ApplySegmentSpeed(now); return true;
        }

        /// <summary>
        /// playback只单调前进，永不越过latest。欠载后保持最后位置，持续更新wall锚点以丢弃暂停债务；
        /// 否则重建好的buffer会在下一帧被整段暂停时间再次吃光，形成永远无法恢复的卡顿循环。
        /// </summary>
        private void AdvanceTo(double now)
        {
            if (!_initialized || now <= _lastWall) return;
            double elapsed = now - _lastWall;
            if (!_rebuffering)
            {
                double latest = LatestTime, candidate = _playback + elapsed * _timescale;
                if (candidate > latest + Epsilon && !_frozen)
                {
                    _underflowAt = _lastWall + Math.Max(0, latest - _playback) / _timescale;
                    _rebuffering = true; UnderflowCount++;
                }
                _playback = Math.Max(_playback, Math.Min(candidate, latest));
            }
            _lastWall = now;
        }

        private void AdjustPlayback()
        {
            double latest = LatestTime, headroom = latest - _playback;
            double oldest = _states[_head].ServerTick / TickRate;
            // 首包异常迟到/后台恢复若积压过大，5%追赶会持续数秒。只向前跳到保留delay的位置，
            // 并记录Resync；普通小误差仍调速，不能每包重设timeline把到包jitter直接传给模型。
            // 初次缓冲时playback本来就应早于首包，不能把这个合法等待当成ring已覆盖旧历史。
            if (_count == _states.Length && _playback < oldest - Epsilon || headroom > _effectiveDelay + Math.Max(.25, _effectiveDelay * 2))
            {
                double candidate = Math.Max(oldest, latest - _effectiveDelay);
                if (candidate > _playback + Epsilon)
                {
                    _playback = candidate; _rebuffering = false; _timescale = 1; ResyncCount++;
                }
            }
            headroom = latest - _playback;
            if (_rebuffering)
            {
                // 从已展示的位置等待未来历史，不能用latest-delay倒拨角色。
                if (headroom + Epsilon < Math.Max(_interval, _effectiveDelay)) return;
                _rebuffering = false;
            }
            double error = headroom - _effectiveDelay, threshold = Math.Max(.005, _interval * .5);
            _timescale = error > threshold ? 1.05 : error < -threshold ? .95 : 1;
        }

        private void UpdateDelivery(double receivedAt)
        {
            double gap = receivedAt - _lastReceived;
            // 一个长暂停不是稳态jitter样本：它交给rebuffer/resync处理，避免其方差把延迟长期顶在上限。
            if (gap <= 0 || gap > Math.Max(.5, _interval * 8)) return;
            if (!_hasDeliverySample) { _deliveryMean = _interval; _hasDeliverySample = true; }
            double alpha = 1 - Math.Exp(-gap / 1.0), difference = gap - _deliveryMean;
            _deliveryMean += alpha * difference;
            _deliveryVariance = (1 - alpha) * (_deliveryVariance + alpha * difference * difference);
            float target = Mathf.Clamp(_minimumDelay + (float)(2 * Math.Sqrt(Math.Max(0, _deliveryVariance))), _minimumDelay, _maximumDelay);
            // buffer可快速增加安全余量，下降按wall时间缓慢收敛，不因几份稳定包反复增减播放延迟。
            _effectiveDelay = target >= _effectiveDelay ? target : Mathf.Max(target, _effectiveDelay - (float)(gap * .02));
        }

        private void ApplySegmentSpeed(double now)
        {
            // 明确静止段立即为0；只有缺少未来段时才有限保持原速度，保持计时从真正underflow开始。
            PlanarSpeed = _lastSegmentSpeed > 0 && (!_rebuffering || now - _underflowAt <= _interval + Epsilon) ? _lastSegmentSpeed : 0;
            // 方向和模长必须一起失效，否则长期断流后Boss仍会在2D Blend Tree里保留侧移方向。
            PlanarVelocity = PlanarSpeed > 0 ? _lastSegmentVelocity : Vector3.zero;
        }

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
