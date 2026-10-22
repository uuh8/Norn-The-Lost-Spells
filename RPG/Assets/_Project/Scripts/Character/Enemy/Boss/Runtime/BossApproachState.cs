namespace Game.Character
{
    /// <summary>
    /// 使用 NavMesh 逼近；进入较小的攻击阈值后锁入战斗，避免边界附近反复切换。
    /// </summary>
    internal sealed class BossApproachState : BossStateBase
    {
        public override BossRuntimeStateKind Kind =>
            BossRuntimeStateKind.Approach;

        public BossApproachState(WizardBossController boss) : base(boss)
        {
        }

        public override void Tick(float deltaTime)
        {
            if (!Boss.HasTarget)
            {
                Boss.EnterInactive();
                return;
            }

            if (Boss.IsCombatEngaged)
            {
                Boss.EnterDecision();
                return;
            }

            Boss.MoveTowardTarget(deltaTime);
        }
    }
}
