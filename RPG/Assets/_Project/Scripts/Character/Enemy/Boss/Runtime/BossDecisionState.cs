namespace Game.Character
{
    /// <summary>
    /// Utility 只按 DecisionInterval 采样；每帧只递减 float Timer。
    /// 这样 Context 组装和 Program 循环不会成为无意义的逐帧工作。
    /// </summary>
    internal sealed class BossDecisionState : BossStateBase
    {
        private float _decisionTimer;

        public override BossRuntimeStateKind Kind =>
            BossRuntimeStateKind.Decision;

        public BossDecisionState(WizardBossController boss) : base(boss)
        {
        }

        public override void Enter()
        {
            _decisionTimer = 0f;
        }

        public override void Tick(float deltaTime)
        {
            if (!Boss.HasTarget)
            {
                Boss.EnterInactive();
                return;
            }

            if (!Boss.IsCombatEngaged)
            {
                Boss.EnterApproach();
                return;
            }

            Boss.MoveInCombat(deltaTime, isCasting: false);
            Boss.FaceTarget(deltaTime);

            if (Boss.GlobalCastIntervalRemaining > 0f)
            {
                return;
            }

            _decisionTimer -= deltaTime;
            if (_decisionTimer > 0f)
            {
                return;
            }

            _decisionTimer = Boss.DecisionInterval;
            int selectedIndex = Boss.SelectProgram(out float selectedScore);
            if (Boss.ShouldTeleport(selectedScore))
            {
                Boss.EnterTeleport(selectedIndex);
                return;
            }

            if (selectedIndex >= 0)
            {
                Boss.EnterCast(selectedIndex);
            }
        }
    }
}
