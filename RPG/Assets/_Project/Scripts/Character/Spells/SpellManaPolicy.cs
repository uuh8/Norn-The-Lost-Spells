namespace Game.Character
{
    /// <summary>
    /// Runtime 进入 SpellCaster.RunCast 时采用的法力策略。
    /// 它只决定施法前是否读取并扣除 ManaComponent，不改变 CastEvaluator 的指令顺序、预算或 Modifier 语义。
    /// 使用 enum 而不是 bool，可让调用处直接表达意图，避免 true 到底表示“扣费”还是“免费”的歧义。
    /// </summary>
    public enum SpellManaPolicy : byte
    {
        SpendCasterMana = 0, // 玩家施法：先估算当前层成本，资源足够才整笔扣除。
        IgnoreMana = 1,      // 不使用玩家资源的调用方仍复用同一解释器和投射物链路。
    }
}
