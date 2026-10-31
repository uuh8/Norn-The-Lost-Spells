namespace Game.Core
{
    /// <summary>
    /// 角色向共同 UI 暴露的只读表现能力。UI 不引用 Character 的具体 Wizard/Archer 类型，
    /// 因而新战斗区域只需提供角色，不需要重新识别职业、复制准心初始化逻辑。
    /// </summary>
    public interface ICombatActorPresentation
    {
        // 这里只表示进入区域时的默认值；蓄力、换手等运行中变化仍走现有 CrosshairVisibilityEvent。
        bool DefaultCrosshairVisible { get; }
    }
}
