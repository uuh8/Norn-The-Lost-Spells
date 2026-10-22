namespace Game.Character
{
    /// <summary>
    /// 一次 Cast 在 Enter 时锁定 Program Index 与 Aim Snapshot。
    /// Telegraph 期间 Target 可以移动，但本发法术不会偷偷改瞄准点；Recovery 完成前 Action Lock 不释放。
    /// </summary>
    internal sealed class BossCastState : BossStateBase
    {
        private float _elapsed;
        private int _programIndex;
        private bool _released;

        public override BossRuntimeStateKind Kind =>
            BossRuntimeStateKind.Cast;

        public BossCastState(WizardBossController boss) : base(boss)
        {
        }

        public override void Enter()
        {
            if (!Boss.TryAcquireActionLock())
            {
                Boss.EnterDecision();
                return;
            }

            _elapsed = 0f;
            _released = false;
            _programIndex = Boss.SelectedProgramIndex;
            Boss.TriggerCastAnimation();
        }

        public override void Tick(float deltaTime)
        {
            _elapsed += deltaTime;
            Boss.MoveInCombat(deltaTime, isCasting: true);
            Boss.FacePoint(Boss.LockedAimPoint, deltaTime);

            if (!_released &&
                _elapsed >= Boss.CastTelegraphDuration)
            {
                _released = true;
                Boss.ReleaseSelectedProgram();
            }

            if (_elapsed >=
                Boss.CastTelegraphDuration +
                Boss.CastRecoveryDuration)
            {
                Boss.CompleteCast(_programIndex);
            }
        }

        public override void Exit()
        {
            Boss.ReleaseActionLock();
        }
    }
}
