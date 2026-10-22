using Game.Core;

namespace Game.Run
{
    /// <summary>
    /// Boss 完成 Phase Transition 后发布的只读快照。
    /// Phase 使用 byte 保持 Game.Run 不反向依赖 Game.Character 的 enum。
    /// </summary>
    public struct BossPhaseChangedEvent : IGameEvent
    {
        public int BossId;
        public byte PreviousPhase;
        public byte CurrentPhase;
        public float HealthRatio;
    }
}
