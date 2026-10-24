using System;

namespace Game.Character
{
    /// <summary>
    /// 动作的逻辑时钟。Server 只使用导出的秒数，绝不依赖无渲染环境下的 Animator.normalizedTime；
    /// Client 用相同定义播放动作，出手权限仍属于 Server。
    /// </summary>
    public sealed class AuthorityActionTimeline
    {
        private float _duration, _release;
        private bool _released, _hasTick;
        private uint _lastTick;
        public ulong CommandId { get; private set; }
        public float ElapsedSeconds { get; private set; }
        public bool IsActive { get; private set; }

        public bool TryStart(ulong commandId, float durationSeconds, float releaseSeconds)
        {
            if (IsActive || commandId == 0 || !float.IsFinite(durationSeconds) || durationSeconds <= 0
                || !float.IsFinite(releaseSeconds) || releaseSeconds < 0 || releaseSeconds > durationSeconds) return false;
            CommandId = commandId; _duration = durationSeconds; _release = releaseSeconds;
            ElapsedSeconds = 0; _released = _hasTick = false; IsActive = true;
            return true;
        }

        /// <returns>仅跨过出手阈值的那一个 Tick 为 true；同帧 catch-up 也不会重复释放。</returns>
        public bool Step(float deltaTime, uint serverTick)
        {
            if (!IsActive || !float.IsFinite(deltaTime) || deltaTime <= 0
                || _hasTick && unchecked((int)(serverTick - _lastTick)) <= 0) return false;
            _lastTick = serverTick; _hasTick = true;
            ElapsedSeconds = Math.Min(_duration, ElapsedSeconds + deltaTime);
            bool releaseNow = !_released && ElapsedSeconds >= _release;
            if (releaseNow) _released = true;
            if (ElapsedSeconds >= _duration) IsActive = false;
            return releaseNow;
        }

        public void Cancel()
        {
            IsActive = false; _released = true;
        }
    }
}
