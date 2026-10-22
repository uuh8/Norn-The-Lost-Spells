using System;
using System.Collections.Generic;
using Game.Combat;
using Unity.Profiling;
using UnityEngine;

namespace Game.Skills
{
    /// <summary>
    /// 一层法术解释结束后的轻量摘要。
    /// 具体攻击内容不会塞进这个 struct，而是写入调用方提供并复用的 List&lt;EmitCommand&gt;；
    /// 这里仅返回“纯逻辑一共消费了多少 Mana”和“是否因 Mana 不足提前中止”。
    /// readonly struct 是值类型快照，适合用作一次调用的无可变共享状态返回值。
    /// </summary>
    public readonly struct CastSummary
    {
        // 已成功执行的指令成本之和；发生 Fizzle 的那条指令没有支付，也不会计入。
        public readonly float ManaSpent;
        // true 表示遇到无法完整支付的指令后停止读取本层剩余序列。
        public readonly bool Fizzled;

        public CastSummary(float manaSpent, bool fizzled)
        {
            ManaSpent = manaSpent;
            Fizzled = fizzled;
        }
    }

    /// <summary>
    /// 法杖配置在“当前施法层”上的只读预览。
    /// 它不访问 ManaComponent，也不创建 EmitCommand；因此 UI 可以安全地读取它，
    /// 并与 SpellCaster 施法前的 Mana 预检使用同一套解释器语义。
    /// </summary>
    public readonly struct CastPreview
    {
        // 本层真正会执行的指令总成本；Trigger 捕获的 payload 属于未来层，不在这里预付费。
        public readonly float ImmediateManaCost;
        // 顺序读完本层后仍未被产出类法术消费的 Draw Budget，不等同于本次已生成的投射物数量。
        public readonly int RemainingDrawBudget;

        public CastPreview(float immediateManaCost, int remainingDrawBudget)
        {
            ImmediateManaCost = immediateManaCost;
            RemainingDrawBudget = remainingDrawBudget;
        }
    }

    /// <summary>
    /// 法术编程系统的解释器内核。
    ///
    /// 正式入口（Evaluate）
    /// 核心解释流程（EvaluateCore）
    /// 四类指令的分支处理
    /// 修正快照的合并（CastModifierState.Apply）
    /// 攻击订单的生成（BakeEmit）
    /// Mana 预检（EstimateManaCost）
    /// 攻击订单列表（List<EmitCommand>）
    /// </summary>
    public static class CastEvaluator
    {
        // Payload 边界解析的最大递归层数。它是错误数据的最后护栏，不是正常法术的设计目标深度。
        public const int MaxActionDepth = 16;
        // 单次 Evaluate 最多写入多少条命令，避免异常 Multicast 配置让一次求值无限扩大输出。
        public const int MaxEmitCommands = 64;

        // ProfilerMarker.Auto() 用 using 作用域记录该段 CPU 时间；Dispose 时自动结束采样，异常/提前 return 也不会漏关 Sample。
        private static readonly ProfilerMarker s_evaluateMarker = new ProfilerMarker("Spell.Evaluate");

        /// <summary>
        /// 正式求值入口。IReadOnlyList 只限制本方法不能增删/替换元素，不代表底层集合被深度冻结；
        /// output 由 SpellCaster 长期持有并复用，本方法进入时先 Clear，调用者不能依赖旧结果。
        /// </summary>
        /// <param name="spells">按玩家配置顺序排列的 SpellDefinition 引用集合；索引顺序就是解释顺序。</param>
        /// <param name="baseDraws">本层初始 Draw Budget，即最多允许多少条有效产出指令进入 output。</param>
        /// <param name="availableMana">纯数值资源上限；本方法不会直接访问或修改场景中的 ManaComponent。</param>
        /// <param name="incomingMods">进入本层前已经形成的 Modifier 值快照。</param>
        /// <param name="output">由调用方持有并跨施法复用的输出 Buffer；进入核心逻辑后首先清空。</param>
        /// <returns>成功支付的 Mana 总量，以及本层是否因 Mana 不足提前中止。</returns>
        public static CastSummary Evaluate(
            IReadOnlyList<SpellDefinition> spells,
            int baseDraws,
            float availableMana,
            CastModifierState incomingMods,
            List<EmitCommand> output)
        {
            // 正式模式不需要观察每一步，所以把 trace 传为 null；Gameplay 规则仍只存在于 EvaluateCore 一份。
            return EvaluateCore(spells, baseDraws, availableMana, incomingMods, output, null);
        }

        /// <summary>
        /// 开发诊断入口：规则与 Evaluate 完全共用，只额外记录每条指令前后的预算、法力和修正快照。
        /// 这样 Trace 是旁路观察者，不会因为复制一套 switch 而与正式 Gameplay 语义逐渐分叉。
        /// </summary>
        public static CastSummary EvaluateWithTrace(
            IReadOnlyList<SpellDefinition> spells,
            int baseDraws,
            float availableMana,
            CastModifierState incomingMods,
            List<EmitCommand> output,
            CastTraceCollector trace)
        {
            // Detailed Trace 的调用方必须显式提供 Collector；静默忽略会让“开启诊断但没有记录”更难排查。
            if (trace == null)
                throw new ArgumentNullException(nameof(trace));
            // Collector 与 output 一样复用内部 List 容量；每次求值只重置逻辑 Count，不重新创建容器。
            trace.Clear();

            return EvaluateCore(spells, baseDraws, availableMana, incomingMods, output, trace);
        }

        public static CastSummary EvaluateRecorded(IReadOnlyList<SpellDefinition> spells, int baseDraws,
            float availableMana, CastModifierState incomingMods, List<EmitCommand> output, CastExecutionRecorder recorder)
        {
            if (recorder == null) throw new ArgumentNullException(nameof(recorder));
            recorder.Clear();
            return EvaluateCore(spells, baseDraws, availableMana, incomingMods, output, null, recorder);
        }

        private static CastSummary EvaluateCore(
            IReadOnlyList<SpellDefinition> spells,
            int baseDraws,
            float availableMana,
            CastModifierState incomingMods,
            List<EmitCommand> output,
            CastTraceCollector trace, CastExecutionRecorder recorder = null)
        {
            // 正式资源记录与诊断收集共用解释器，避免两套语义分叉。
            using (s_evaluateMarker.Auto())
            {
                // Clear 只把 Count 归零，List 的内部数组容量会保留，下一次施法可复用已分配内存。
                output.Clear();

                // 下面五个局部变量就是一次解释过程的 Runtime State；方法返回后全部结束，不会写回共享资产。
                int drawBudget = baseDraws;             // 还允许产生多少条有效攻击订单的整数预算。
                float manaLeft = availableMana;         // 纯数值剩余资源，不直接写 ManaComponent。
                float manaSpent = 0f;                   // 只累计已成功支付的指令成本。
                bool fizzled = false;                   // 是否因为 Mana 不足而中断。
                CastModifierState mods = incomingMods;  // 当前指令可见的不可变 Modifier Snapshot。

                // ?. 是 null-conditional operator：正式 Evaluate 传入 null 时整次 Record 调用被跳过。
                trace?.Record(new CastTraceStep(
                    CastTraceStepKind.CastStarted, -1, null,
                    drawBudget, drawBudget, manaLeft, manaLeft, mods, mods));
                if (spells == null || spells.Count == 0)
                {
                    // 空输入仍记录 Started/Completed，便于 Trace 调用方看到一次完整但没有指令的求值。
                    trace?.Record(new CastTraceStep(
                        CastTraceStepKind.CastCompleted, -1, null,
                        drawBudget, drawBudget, manaLeft, manaLeft, mods, mods));
                    return new CastSummary(0f, false);
                }

                // i 是 Instruction Pointer：它指向下一条要解释的 Slot。从左到右读取
                int i = 0;
                while (i < spells.Count && output.Count < MaxEmitCommands)
                {
                    // 循环有两道边界：i 防止读出序列末尾，output 上限防止异常 Multicast 制造过多命令。
                    SpellDefinition spell = spells[i];

                    // 在执行当前指令前保存 Before Snapshot。Trace 用它和执行后的状态对比，而不是重新推演规则。
                    int budgetBefore = drawBudget;
                    float manaBefore = manaLeft;
                    CastModifierState modsBefore = mods;

                    if (spell == null)
                    {
                        // 空 Slot 没有规则和 Mana 成本，但仍算“已经扫描过”，所以必须推进 Instruction Pointer。
                        trace?.Record(new CastTraceStep(
                            CastTraceStepKind.NullSpellSkipped, i, null,
                            budgetBefore, drawBudget, manaBefore, manaLeft,
                            modsBefore, mods));

                        i++;
                        continue;
                    }

                    switch (spell.Kind)
                    {
                        case SpellKind.Modify:                  // 修正类指令更新当前修正快照
                            // Modify 不直接生成攻击，但它本身仍是一条需要支付 Mana 的指令。
                            // Apply 返回新的 readonly struct；旧 mods 不会被原地修改，接下来的 Emit 只读取新快照。
                            if (!TrySpend(spell, ref manaLeft, ref manaSpent))
                            {
                                fizzled = true;
                                trace?.Record(new CastTraceStep(
                                    CastTraceStepKind.ManaFizzle, i, spell,
                                    budgetBefore, drawBudget, manaBefore, manaLeft,
                                    modsBefore, mods));
                                break;
                            }

                            recorder?.Record(new CastExecutionUse(i, spell.Kind));
                            mods = mods.Apply(spell);           // 更新当前修正快照
                            trace?.Record(new CastTraceStep(
                                CastTraceStepKind.ModifyApplied, i, spell,
                                budgetBefore, drawBudget, manaBefore, manaLeft,
                                modsBefore, mods));
                            i++;                                //  读取位置加1
                            break;

                        case SpellKind.Multicast:               // 多重射击类增加剩余产出预算
                            if (!TrySpend(spell, ref manaLeft, ref manaSpent))
                            {
                                fizzled = true;
                                trace?.Record(new CastTraceStep(
                                    CastTraceStepKind.ManaFizzle, i, spell,
                                    budgetBefore, drawBudget, manaBefore, manaLeft,
                                    modsBefore, mods));
                                break;
                            }

                            recorder?.Record(new CastExecutionUse(i, spell.Kind));
                            drawBudget += spell.ExtraDraws;     // 增加产出预算
                            trace?.Record(new CastTraceStep(
                                CastTraceStepKind.MulticastApplied, i, spell,
                                budgetBefore, drawBudget, manaBefore, manaLeft,
                                modsBefore, mods));
                            i++;                                // 读取位置加1
                            break;

                        case SpellKind.Emit:                    // 尝试生成一张攻击订单 EmitCommand
                        case SpellKind.StaticProjectile:        // 尝试生成一张攻击订单 EmitCommand
                            // 两种 Kind 在 Pure Logic 中都是“消耗一次预算并生成 EmitCommand”。
                            // 它们最终是飞行、坠落还是定点对象，由命令里的 SpawnMode 交给 SpellCaster 决定。
                            if (drawBudget <= 0)
                            {
                                trace?.Record(new CastTraceStep(
                                    CastTraceStepKind.DrawBudgetBlocked, i, spell,
                                    budgetBefore, drawBudget, manaBefore, manaLeft,
                                    modsBefore, mods));
                                // 被预算阻止的 Emit 已经“读过”，必须前进；否则移除 while
                                // 的 drawBudget Gate 后会永远停在同一个 Slot。
                                // 该指令没有真正执行，因此不扣 Mana，也不进入 output。
                                i++;                            // 读取位置加1
                                break;
                            }

                            if (!TrySpend(spell, ref manaLeft, ref manaSpent))
                            {
                                fizzled = true;                 // 付不起该指令成本，标记失败
                                trace?.Record(new CastTraceStep(
                                    CastTraceStepKind.ManaFizzle, i, spell,
                                    budgetBefore, drawBudget, manaBefore, manaLeft,
                                    modsBefore, mods));
                                break;
                            }

                            // Trigger Emit 不会把后续全部指令都吞掉，而是从下一 Slot 开始解析“一个完整 Action”的边界。
                            // Action 可以只是一个 Emit，也可以由 Multicast 扩张为多个 Emit；其中若再遇到 Trigger，
                            // FindActionEnd 会递归越过内层 Payload，保证外层切片的括号关系正确。
                            int payloadStart = i + 1;
                            int payloadEnd = payloadStart;
                            IReadOnlyList<SpellDefinition> payload = null;
                            if (spell.PayloadTrigger != PayloadTriggerMode.None)
                            {
                                payloadEnd = FindActionEnd(
                                    spells,
                                    payloadStart,
                                    0);
                                payload = CaptureRange(
                                    spells,
                                    payloadStart,
                                    payloadEnd);
                            }

                            // Bake 表示把 Authoring Data + 当前 Runtime 修正合成为独立输出快照。
                            EmitCommand command = BakeEmit(spell, mods, payload);
                            int emitIndex = output.Count;
                            recorder?.Record(new CastExecutionUse(i, spell.Kind, emitIndex, payloadStart, payloadEnd));
                            output.Add(command);
                            drawBudget--;

                            // 普通 Emit 只前进一格；Trigger 已把 [payloadStart, payloadEnd) 捕获给未来执行，
                            // 当前层必须直接跳到 payloadEnd，避免同一组指令现在执行一次、命中后又执行一次。
                            i = spell.PayloadTrigger != PayloadTriggerMode.None
                                ? payloadEnd
                                : i + 1;

                            trace?.Record(new CastTraceStep(
                                CastTraceStepKind.EmitProduced, i, spell,
                                budgetBefore, drawBudget, manaBefore, manaLeft,
                                modsBefore, mods, emitIndex, command));
                            if (spell.PayloadTrigger != PayloadTriggerMode.None)
                            {
                                trace?.Record(new CastTraceStep(
                                    CastTraceStepKind.PayloadCaptured, i, spell,
                                    budgetBefore, drawBudget, manaBefore, manaLeft,
                                    modsBefore, mods, emitIndex, command,
                                    payloadStart,
                                    command.HasPayload ? command.Payload.Count : 0,
                                    command.PayloadTrigger));
                            }
                            break;
                    }

                    // Legacy不执行V2 Copy；未知Opcode必须推进，否则旧预览会卡在同一槽。
                    if (spell.Kind > SpellKind.StaticProjectile) { i++; fizzled = true; }
                    // switch 中的 break 只离开 switch，不会自动离开 while；用统一检查结束整层解释。
                    // 因此 Fizzle 之后既不会继续读取剩余指令，也不会部分支付失败指令的 Mana。
                    if (fizzled)
                        break;
                }

                // 正常读完、达到命令上限或 Mana Fizzle，都会落到同一个收尾点，返回最终统计快照。
                trace?.Record(new CastTraceStep(
                    CastTraceStepKind.CastCompleted, -1, null,
                    drawBudget, drawBudget, manaLeft, manaLeft, mods, mods));
                return new CastSummary(manaSpent, fizzled);
            }
        }

        /// <summary>
        /// 估算当前求值层会实际执行到的指令 Mana 成本。
        /// 它不实例化、不扣真实资源，只镜像 Evaluate 的 Instruction Pointer、Draw Budget 和 Payload 边界。
        /// Trigger 捕获的 Payload Action 不在父层预付费；等命中或计时真正触发时，SpellCaster 会把它作为新层再次估算。
        /// 采用单独的预检方法，是为了让 Runtime 可以实施“本层整笔支付”：余额不够时一条命令都不生成。
        /// </summary>
        public static float EstimateManaCost(
            IReadOnlyList<SpellDefinition> spells,
            int baseDraws,
            CastModifierState incomingMods)
        {
            return Preview(spells, baseDraws, incomingMods).ImmediateManaCost;
        }

        /// <summary>
        /// 预览当前法杖这一层施法会立即扣除的 Mana，以及解释结束后剩余的产出预算。
        /// EstimateManaCost 与法杖编辑器都调用本方法，避免 UI 另写一套规则后和真实扣费分叉。
        /// </summary>
        public static CastPreview Preview(
            IReadOnlyList<SpellDefinition> spells,
            int baseDraws,
            CastModifierState incomingMods)
        {
            if (spells == null || spells.Count == 0)
                return new CastPreview(0f, baseDraws);

            int drawBudget = baseDraws;
            float manaCost = 0f;
            // 当前 Mana 公式尚不读取 Modifier，但仍镜像 Apply，确保未来出现“Modifier 影响成本”时两条路径有明确同步点。
            CastModifierState mods = incomingMods;

            // 这里故意不调用 Evaluate 再读取 CastSummary：Estimate 不能污染正式 output，也不应先构造临时 EmitCommand。
            int i = 0;
            int emittedCount = 0;
            // 必须镜像 Evaluate：零预算 Emit 不计费但会被跳过，后续 Multicast
            // 仍可能重新开放预算，因此不能在 drawBudget==0 时提前结束估算。
            // emittedCount 同步正式 Evaluate 的 MaxEmitCommands 护栏，避免异常配置让预览成本高于实际运行时。
            while (i < spells.Count && emittedCount < MaxEmitCommands)
            {
                SpellDefinition spell = spells[i];
                // 新Opcode的费用由V2推演给出；Legacy估算安全停止，不能原地死循环。
                if (spell != null && spell.Kind > SpellKind.StaticProjectile) break;
                if (spell == null)
                {
                    i++;
                    continue;
                }

                switch (spell.Kind)
                {
                    case SpellKind.Modify:
                        // Modify 会被正式解释器执行，因此它的成本计入本层，并同步推进 Modifier Snapshot。
                        manaCost += SanitizedManaCost(spell);
                        mods = mods.Apply(spell);
                        i++;
                        break;

                    case SpellKind.Multicast:
                        // Multicast 自己收费，并改变之后有多少条 Emit 能够真正执行。
                        manaCost += SanitizedManaCost(spell);
                        drawBudget += spell.ExtraDraws;
                        i++;
                        break;

                    case SpellKind.Emit:
                    case SpellKind.StaticProjectile:
                        if (drawBudget <= 0)
                        {
                            // 被 Draw Budget 阻止的产出不会进入 output，正式求值也不会向它收费。
                            i++;
                            break;
                        }

                        manaCost += SanitizedManaCost(spell);
                        drawBudget--;
                        emittedCount++;

                        if (spell.PayloadTrigger != PayloadTriggerMode.None)
                        {
                            // 父层只支付 Trigger Emit 自身；被捕获 Action 的成本留到未来触发层。
                            i = FindActionEnd(spells, i + 1, 0);
                        }
                        else
                        {
                            i++;
                        }
                        break;
                }
            }

            return new CastPreview(manaCost, drawBudget);
        }

        /// <summary>
        /// 对解释器局部变量执行原子式纯数值扣费。
        /// ref 允许方法在支付成功时直接更新调用方的 manaLeft 和 manaSpent；这里不会访问场景中的 ManaComponent。
        /// 如果余额不足，两个数值都保持原样，调用方据此进入 Fizzle，而不会产生“扣了一部分但指令没执行”的状态。
        /// </summary>
        private static bool TrySpend(SpellDefinition spell, ref float manaLeft, ref float manaSpent)
        {
            float cost = SanitizedManaCost(spell);
            if (cost > manaLeft)
                return false;

            manaLeft -= cost;
            manaSpent += cost;
            return true;
        }

        private static float SanitizedManaCost(SpellDefinition spell)
        {
            // 负成本不会被解释成回复 Mana；null 和非正数统一按 0 处理，防止错误 Authoring Data 破坏资源约束。
            return spell != null && spell.ManaCost > 0f ? spell.ManaCost : 0f;
        }

        /// <summary>
        /// 把一条产出指令的 Authoring Data 与当前 Modifier Snapshot 烘焙成最终 EmitCommand。
        /// “烘焙”意味着在这一刻算出伤害、速度、运动参数、Prefab 引用和 Payload，Runtime 不再回查可变化的解释器局部状态。
        /// 该方法只构造纯数据边界对象，不创建 GameObject，也不操作 Rigidbody。
        /// </summary>
        private static EmitCommand BakeEmit(SpellDefinition spell, CastModifierState mods, IReadOnlyList<SpellDefinition> payload)
        {
            // 只共享数学；Legacy仍在读到Emit时立即Bake，并保留旧Payload继承语义。
            return SpellCommandBaker.Bake(new SpellEmissionData(spell), mods, payload);
        }

        /// <summary>
        /// 从 start 开始计算“一个完整 Action”的右开边界，并返回第一个不属于该 Action 的索引。
        ///
        /// Action 不是固定一条 Slot：初始 Draw Budget 为 1；Multicast 会扩大它需要包含的有效产出数量；
        /// Modify 只改变后续产出，不消耗 Draw；Emit/StaticProjectile 各消耗一次 Draw。
        /// 若产出本身又是 Trigger，它后面的 Payload Action 属于该 Trigger 的内部结构，必须递归越过，
        /// 外层才能继续寻找自己的下一条并行产出。这个过程类似在没有显式括号的线性指令流中恢复嵌套边界。
        ///
        /// 本方法只移动索引，不支付 Mana、不生成 EmitCommand。深度上限是错误资产的防御性护栏；
        /// 正常输入因每次递归都从更靠后的索引开始，所以会朝序列末尾收敛。
        /// </summary>
        private static int FindActionEnd(
            IReadOnlyList<SpellDefinition> spells,
            int start,
            int depth)
        {
            // depth 是语义解析深度，不是 Unity 帧数；硬上限防止恶意/错误资产导致无界递归。
            if (spells == null || start >= spells.Count || depth >= MaxActionDepth)
                return start;

            int cursor = start;
            // “捕获一个 Action”默认需要找到一条有效产出；Multicast 可以把这个需求扩大为多条。
            int drawBudget = 1;
            while (cursor < spells.Count && drawBudget > 0)
            {
                // 先保存引用再推进 cursor，使 cursor 始终表示“下一条尚未检查的 Slot”。
                SpellDefinition spell = spells[cursor];
                cursor++;
                if (spell == null)
                    continue;

                switch (spell.Kind)
                {
                    case SpellKind.Multicast:
                        // 边界解析不允许负 ExtraDraws 反向关闭 Action，错误负值按 0 处理。
                        drawBudget += Math.Max(0, spell.ExtraDraws);
                        break;

                    case SpellKind.Emit:
                    case SpellKind.StaticProjectile:
                        drawBudget--;
                        if (spell.PayloadTrigger != PayloadTriggerMode.None)
                        {
                            // cursor 此时已经指向 Trigger 后第一条 Slot；递归返回其内部 Payload Action 的末尾。
                            cursor = FindActionEnd(
                                spells,
                                cursor,
                                depth + 1);
                        }
                        break;
                }
            }

            return cursor;
        }

        /// <summary>
        /// 将 [start, end) 这段 Action 复制为可在未来独立读取的 Payload List。
        /// 复制的是 SpellDefinition 引用，不是克隆 ScriptableObject 资产；因此成本与切片中的 Slot 数量线性相关。
        /// 该分配发生在离散施法求值，不位于 Update/FixedUpdate 热路径，是用一次小型分配换取未来触发时不依赖原序列索引。
        /// </summary>
        private static List<SpellDefinition> CaptureRange(
            IReadOnlyList<SpellDefinition> spells,
            int start,
            int end)
        {
            // Payload 需要跨越当前调用，独立存活到投射物未来命中/计时帧，因此不能只保存 start/end 局部变量。
            var payload = new List<SpellDefinition>();
            // end 来自边界解析，但仍钳制到当前 Count，防止错误输入造成越界访问。
            int clampedEnd = Math.Min(end, spells.Count);
            for (int j = start; j < clampedEnd; j++)
                // null Slot 对未来解释没有语义，捕获时直接省略；非空元素仍是对共享 Authoring Asset 的引用。
                if (spells[j] != null) payload.Add(spells[j]);
            return payload;
        }
    }
}
