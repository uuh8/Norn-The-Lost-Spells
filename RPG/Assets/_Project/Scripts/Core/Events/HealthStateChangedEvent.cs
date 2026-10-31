namespace Game.Core
{
    /// <summary>只读生命值状态变化；完整网络快照可校正 HUD，但不表示一次伤害，不能据此重播命中反馈。</summary>
    public struct HealthStateChangedEvent : IGameEvent
    {
        public int TargetId;
        public float CurrentHp, MaxHp;
    }
}
