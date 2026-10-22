namespace Game.Skills
{
    /// <summary>
    /// Emit 产出的投射物携带 Payload 时，下一层法术 Action 的释放条件。
    /// Payload 的具体内容由 CastEvaluator 捕获，触发时机由 ProjectileBase 的实例事件提供，
    /// SpellCaster 负责把二者连接起来；该枚举本身不执行任何 Unity 行为。
    /// </summary>
    public enum PayloadTriggerMode : byte
    {
        None = 0,       // 普通产出，不保存也不运行后续 Action。
        OnImpact = 1,   // 命中敌方或环境后，以命中点和命中方向运行 Payload；同阵营穿过不触发。
        AfterDelay = 2, // 投射物存活到指定秒数后，以当前位置和当前方向运行；提前碰撞/销毁则不会到点触发。
        OnTermination = 3 // 只响应真实命中或正常寿命结束；Cancel/卸载不释放攻击。
    }
}
