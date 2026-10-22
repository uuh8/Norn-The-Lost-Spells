namespace Game.Skills
{
    /// <summary>
    /// 解释器 Trace 中一条记录所描述的事件类型。
    /// Trace 只观察 CastEvaluator 已经发生的事实，不反向驱动求值；显式 byte 声明该 enum 的 C# 底层数值类型。
    /// </summary>
    public enum CastTraceStepKind : byte
    {
        CastStarted = 0,       // 进入一层求值，记录初始 Draw Budget、法力和 Modifier。
        NullSpellSkipped = 1,  // 序列槽位为空，解释器推进指针但不改变状态。
        ModifyApplied = 2,     // 一条 Modify 已支付并合并到 Modifier 快照。
        MulticastApplied = 3,  // 一条 Multicast 已增加 Draw Budget。
        DrawBudgetBlocked = 4, // 产出指令被零预算阻止，但解释器仍继续向右读取。
        EmitProduced = 5,      // 已烘焙并加入一条 EmitCommand。
        PayloadCaptured = 6,   // Trigger 已捕获后面的一个完整 Action 切片。
        ManaFizzle = 7,        // 纯求值入口的可用法力不足，本层提前结束。
        CastCompleted = 8      // 本层正常结束或在 Fizzle 后收尾。
    }
}
