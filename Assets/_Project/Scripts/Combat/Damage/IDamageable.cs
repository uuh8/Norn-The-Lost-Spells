namespace Game.Combat
{
    /// <summary>
    /// “任何能受伤对象”的最小 Contract：公开阵营、存活状态和统一命中入口，当前由 HealthComponent 实现。
    /// 投射物只依赖这个接口而不依赖玩家、敌人等具体类型，因此同一套攻击链可作用于不同对象。
    /// 命中判定方通过 ReceiveHit 提交攻击意图，实现方再结合自身防御档案完成结算。
    /// </summary>
    public interface IDamageable
    {
        /// <summary>所属阵营。命中判定据此跳过同阵营（含攻击者自身）。</summary>
        byte TeamId { get; }

        /// <summary>是否存活。已死目标不再受理命中。</summary>
        bool IsAlive { get; }

        /// <summary>受理一次命中 </summary>
        void ReceiveHit(in DamageRequest req);
    }
}
