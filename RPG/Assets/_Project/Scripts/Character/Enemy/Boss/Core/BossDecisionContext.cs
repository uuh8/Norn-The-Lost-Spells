namespace Game.Character
{
    /// <summary>
    /// 一次 Utility Tick 的不可变值快照。
    /// Evaluator 不读取 Transform/StatusController，因而可以确定性测试并避免隐藏 Scene 查询。
    /// </summary>
    public readonly struct BossDecisionContext
    {
        public readonly BossPhase Phase;
        public readonly float DistanceToPlayer;
        public readonly float PlayerWetIntensity;
        public readonly float PlayerMoveSpeed;
        public readonly float BossHealthRatio;
        public readonly float PlayerHealthRatio;

        public BossDecisionContext(
            BossPhase phase,
            float distanceToPlayer,
            float playerWetIntensity,
            float playerMoveSpeed,
            float bossHealthRatio,
            float playerHealthRatio)
        {
            Phase = phase;
            DistanceToPlayer = distanceToPlayer;
            PlayerWetIntensity = playerWetIntensity;
            PlayerMoveSpeed = playerMoveSpeed;
            BossHealthRatio = bossHealthRatio;
            PlayerHealthRatio = playerHealthRatio;
        }
    }
}
