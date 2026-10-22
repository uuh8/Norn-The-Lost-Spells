namespace Game.Character
{
    /// <summary>
    /// 单次施法的诊断输出级别。它只控制 Editor/Development Build 中记录多少 Trace，
    /// 不参与法术求值，也不会改变伤害、法力或产出数量。
    /// 显式使用 byte 声明 C# 底层数值类型和可用值域；Unity 资产依赖这里的整数值，因此已使用的数值不要随意改序。
    /// </summary>
    public enum CastTraceLevel : byte
    {
        Off = 0,       // 不收集/打印施法诊断，正常游戏使用。
        Summary = 1,   // 每层只打印产出数量、所需法力、是否失败等汇总事实。
        Detailed = 2   // 额外记录解释器读过的每条指令及其前后预算、法力和 Modifier 快照。
    }
}
