namespace Game.Character
{
    /// <summary>只调度一次可用发射；不解释程序、不跟随动画长度补发历史帧。</summary>
    public sealed class OwnedWandFireScheduler
    {
        private int _lastFrame = -1;
        private double _retryAfter;
        public bool CanAttempt(int frame, double time, bool requested, bool allowed, float ready, bool hasProgram)
            => requested && allowed && hasProgram && float.IsFinite(ready) && ready <= 0 && frame != _lastFrame && time >= _retryAfter;
        public void RecordAttempt(int frame, double time, bool committed)
        { _lastFrame = frame; _retryAfter = committed ? time : time + .15; }
        public void Reset() { _lastFrame = -1; _retryAfter = 0; }
    }
}
