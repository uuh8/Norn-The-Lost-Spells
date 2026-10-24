namespace Game.Core
{
    /// <summary>网络组合层发布的只读 HUD 事实；UI 无需引用 Multiplayer/Character，也不持有 Gameplay 写入口。</summary>
    public struct OnlineCombatHudStateEvent : IGameEvent
    {
        public ulong ActorId;
        public bool IsLocal, IsBoss;
        public float CurrentHp, MaxHp, CurrentMana, MaxMana, Cooldown;
        public int Charges;
        public bool Unlimited;
        public uint BossPhase;
    }
}
