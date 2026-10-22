using System;
namespace Game.Run
{
    /// <summary>轻量条件和倒计时是纯状态；受伤没有取消入口，离开或显式取消清零。</summary>
    public sealed class ExtractionProgress
    {
        private readonly float _seconds;
        private float _elapsed;
        private bool _completed, _cancelledUntilExit, _started;
        public ExtractionConditionTracker Conditions { get; }
        public bool Inside { get; private set; }
        public bool CanStart => Inside && Conditions.Ready && !_completed && !Running;
        public bool Start()
        {
            // 进入出口只给启动资格；玩家明确开始才计时，取消后再次开始从零计时。
            if(!CanStart)return false;_elapsed=0;_cancelledUntilExit=false;_started=true;return true;
        }
        public bool Cancelled => _cancelledUntilExit;
        public bool Running => _started && Inside && Conditions.Ready && !_completed && !_cancelledUntilExit;
        public float Remaining => Math.Max(0, _seconds - _elapsed);
        public ExtractionProgress(ExtractionConditionTracker conditions, float seconds)
        { if (conditions == null || seconds <= 0 || float.IsNaN(seconds) || float.IsInfinity(seconds)) throw new ArgumentException("撤离配置非法。"); Conditions = conditions; _seconds = seconds; }
        public void SetInside(bool inside)
        { if (Inside == inside) return; Inside = inside; if (!inside) { _elapsed = 0; _cancelledUntilExit = false; _started=false; } }
        public void Cancel() { _elapsed = 0; _cancelledUntilExit = true; _started=false; }
        public bool Tick(float delta)
        {
            if (!Running || delta <= 0 || float.IsNaN(delta) || float.IsInfinity(delta)) return false;
            _elapsed = Math.Min(_seconds, _elapsed + delta); if (_elapsed < _seconds) return false;
            _completed = true; return true;
        }
    }
}
