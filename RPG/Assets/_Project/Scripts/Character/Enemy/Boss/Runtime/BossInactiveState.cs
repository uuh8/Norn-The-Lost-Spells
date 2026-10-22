namespace Game.Character
{
    /// <summary>
    /// Safe Zone 阶段的完全惰性状态：不移动、不转向、不查询 Utility，也不施法。
    /// </summary>
    internal sealed class BossInactiveState : BossStateBase
    {
        public override BossRuntimeStateKind Kind =>
            BossRuntimeStateKind.Inactive;

        public BossInactiveState(WizardBossController boss) : base(boss)
        {
        }

        public override void Enter()
        {
            Boss.StopNavigation();
            // Safe Zone 阶段的“Boss 尚未参战”必须落实到 Combat 权威，而不能只停掉 AI Update。
            // Health 的统一 Gate 同时被 StatusController 读取，因此直击、DoT 与预埋状态都被拒绝。
            Boss.Health.SetInvulnerable(true);
        }

        public override void Exit()
        {
            // Encounter Start 路由离开 Inactive 时恢复正常受击；放在 Exit 可覆盖所有状态切换出口。
            Boss.Health.SetInvulnerable(false);
        }
    }
}
