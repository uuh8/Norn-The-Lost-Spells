using Game.Core;
namespace Game.Run
{
    /// <summary>进程内状态，不保存世界；同帧成功/死亡先收集意图，再在LateUpdate统一裁决。</summary>
    public sealed class RaidState
    {
        public string RaidId { get; internal set; }
        public RaidPhase Phase { get; internal set; }
        private bool _success, _failure;
        private RaidOutcome _reason;
        public void RequestOutcome(RaidOutcome outcome)
        { if (outcome == RaidOutcome.Success) _success = true; else { _failure = true; _reason = outcome; } }
        public bool TryResolve(out RaidOutcome outcome)
        { outcome = _failure ? _reason : RaidOutcome.Success; bool any = _failure || _success; _failure = _success = false; return any; }
    }
}
