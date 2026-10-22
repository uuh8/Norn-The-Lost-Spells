namespace Game.Skills
{
    /// <summary>
    /// 解释器处理某个时刻的一份只读诊断快照。
    /// 它同时保存指令身份、处理前后状态和可选产出，因而调试器无需重新执行规则就能解释“为什么得到该结果”。
    /// readonly struct 是值类型：加入 List 时复制当时的字段值，之后 Modifier 局部变量继续变化也不会改写旧记录。
    /// </summary>
    public readonly struct CastTraceStep
    {
        // ── 事件身份：发生了什么，以及对应序列中的哪条指令 ──
        public readonly CastTraceStepKind Kind;
        public readonly int SpellIndex;
        public readonly SpellDefinition Spell;

        // ── 状态前后快照：用于比较该步骤对预算、法力和 Modifier 的实际影响 ──
        public readonly int DrawBudgetBefore;
        public readonly int DrawBudgetAfter;
        public readonly float ManaLeftBefore;
        public readonly float ManaLeftAfter;
        public readonly CastModifierState ModifiersBefore;
        public readonly CastModifierState ModifiersAfter;

        // ── 产出/控制流附加数据：不是每种 Kind 都会使用，未使用时保持下方构造参数的默认哨兵值 ──
        public readonly int EmitIndex;
        public readonly EmitCommand Emit;
        public readonly int PayloadStartIndex;
        public readonly int PayloadCount;
        public readonly PayloadTriggerMode PayloadTrigger;

        /// <summary>
        /// 创建一条完整 Trace 快照。可选参数让非 Emit 步骤不必构造另一套类型：
        /// -1 表示“没有合法索引”，default(EmitCommand) 表示“本步骤没有产出命令”。
        /// default 是 C# 的默认值表达式，会把 struct 的所有数值/引用字段初始化为 0/null。
        /// </summary>
        public CastTraceStep(
            CastTraceStepKind kind,
            int spellIndex,
            SpellDefinition spell,
            int drawBudgetBefore,
            int drawBudgetAfter,
            float manaLeftBefore,
            float manaLeftAfter,
            CastModifierState modifiersBefore,
            CastModifierState modifiersAfter,
            int emitIndex = -1,
            EmitCommand emit = default,
            int payloadStartIndex = -1,
            int payloadCount = 0,
            PayloadTriggerMode payloadTrigger = PayloadTriggerMode.None)
        {
            Kind = kind;
            SpellIndex = spellIndex;
            Spell = spell;
            DrawBudgetBefore = drawBudgetBefore;
            DrawBudgetAfter = drawBudgetAfter;
            ManaLeftBefore = manaLeftBefore;
            ManaLeftAfter = manaLeftAfter;
            ModifiersBefore = modifiersBefore;
            ModifiersAfter = modifiersAfter;
            EmitIndex = emitIndex;
            Emit = emit;
            PayloadStartIndex = payloadStartIndex;
            PayloadCount = payloadCount;
            PayloadTrigger = payloadTrigger;
        }
    }
}
