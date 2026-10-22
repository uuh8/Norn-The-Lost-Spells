namespace Game.Character
{
    /// <summary>
    /// Pending Phase 只在 Action Lock 空闲后进入本状态。
    /// 整段 Telegraph 无敌、不回血；Exit 无条件撤销无敌，防止 Disable/Scene Unload 留下脏状态。
    /// </summary>
    internal sealed class BossPhaseTransitionState : BossStateBase
    {
        private float _elapsed;

        public override BossRuntimeStateKind Kind =>
            BossRuntimeStateKind.PhaseTransition;

        public BossPhaseTransitionState(WizardBossController boss)
            : base(boss)
        {
        }

        public override void Enter()
        {
            Boss.StopNavigation();
            if (!Boss.TryAcquireActionLock())
            {
                Boss.EnterDecision();
                return;
            }

            _elapsed = 0f;
            Boss.Health.SetInvulnerable(true);
            Boss.TriggerPhaseTransitionAnimation();
        }

        public override void Tick(float deltaTime)
        {
            _elapsed += deltaTime;
            Boss.FaceTarget(deltaTime);
            if (_elapsed >= Boss.PhaseTransitionDuration)
            {
                Boss.CompletePhaseTransition();
            }
        }

        public override void Exit()
        {
            Boss.Health.SetInvulnerable(false);
            Boss.ReleaseActionLock();
        }
    }
}
