using Game.Core;

namespace Game.Run
{
    /// <summary>完整 Replica 状态驱动同一正式 Boss HUD；这是数值校正，不伪造伤害或重播阶段特效。</summary>
    public struct BossHudStateChangedEvent : IGameEvent
    {
        public int BossId;
        public float CurrentHp;
        public byte CurrentPhase;
        public bool Active;
    }
}
