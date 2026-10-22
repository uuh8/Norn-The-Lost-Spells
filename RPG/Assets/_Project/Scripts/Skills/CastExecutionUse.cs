namespace Game.Skills
{
    // 正式执行凭据；具体实例由 Run 映射，诊断 Trace 不参与资产结算。
    public readonly struct CastExecutionUse
    {
        public readonly int SlotIndex, EmitIndex, PayloadStart, PayloadEnd;
        public readonly SpellKind Kind;
        public CastExecutionUse(int slot, SpellKind kind, int emit = -1, int start = 0, int end = 0)
        { SlotIndex = slot; Kind = kind; EmitIndex = emit; PayloadStart = start; PayloadEnd = end; }
    }
}
