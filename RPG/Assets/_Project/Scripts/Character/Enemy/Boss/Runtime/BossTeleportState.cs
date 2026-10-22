namespace Game.Character
{
    /// <summary>
    /// Teleport 与 Cast/PhaseTransition 共用 Action Lock。
    /// 目标点在 Enter 时一次性确定，Telegraph 期间不再追踪 Player，保证表现可读且结果可复现。
    /// </summary>
    internal sealed class BossTeleportState : BossStateBase
    {
        private float _elapsed;
        private bool _moved;
        private int _fallbackProgramIndex;

        public override BossRuntimeStateKind Kind =>
            BossRuntimeStateKind.Teleport;

        public BossTeleportState(WizardBossController boss) : base(boss)
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
            _moved = false;
            _fallbackProgramIndex = Boss.TeleportFallbackProgramIndex;
            if (!Boss.TryPrepareTeleport())
            {
                Boss.CancelTeleport(_fallbackProgramIndex);
                return;
            }

            Boss.PublishTeleportTelegraph();
        }

        public override void Tick(float deltaTime)
        {
            _elapsed += deltaTime;
            Boss.FaceTarget(deltaTime);

            if (!_moved &&
                _elapsed >= Boss.TeleportTelegraphDuration)
            {
                _moved = true;
                Boss.ExecutePreparedTeleport();
            }

            if (_elapsed >=
                Boss.TeleportTelegraphDuration +
                Boss.TeleportRecoveryDuration)
            {
                Boss.CompleteTeleport();
            }
        }

        public override void Exit()
        {
            Boss.ReleaseActionLock();
        }
    }
}
