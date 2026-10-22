namespace Game.Combat
{
    /// <summary>
    /// 【预留缝】Game.Skills 未来对目标直接结算伤害的入口（绕过近战命中判定）。
    /// 本轮不接技能系统；保留签名以锁定调用形态：技能命中后构造 DamageRequest 调本方法。
    /// </summary>
    public static class CombatDamage
    {
        public static void Deal(in DamageRequest req, IDamageable target)
        {
            if (target == null || !target.IsAlive) return;
            target.ReceiveHit(in req);
        }
    }
}
