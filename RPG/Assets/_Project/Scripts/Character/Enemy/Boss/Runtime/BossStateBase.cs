namespace Game.Character
{
    /// <summary>
    /// Boss State 是预创建的普通 C# 对象，不接受 Unity Lifecycle 回调。
    /// 所有 Scene 组件访问统一经过 Controller，避免每个 State 重复查找组件。
    /// </summary>
    internal abstract class BossStateBase
    {
        protected readonly WizardBossController Boss;

        public abstract BossRuntimeStateKind Kind { get; }

        protected BossStateBase(WizardBossController boss)
        {
            Boss = boss;
        }

        public virtual void Enter()
        {
        }

        public virtual void Tick(float deltaTime)
        {
        }

        public virtual void Exit()
        {
        }
    }
}
