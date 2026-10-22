using System;

namespace Game.Combat
{
    /// <summary>
    /// 用一个 byte 的不同 bit 标记本帧哪些状态已由元素反应接管。
    /// 每个值都必须是 2 的幂，才能通过按位或（|）同时保存多个标记，
    /// 再通过按位与（&amp;）判断某一标记是否存在。
    /// </summary>
    [Flags]
    public enum StatusMask : byte
    {
        None = 0,
        Fire = 1 << 0,
        Water = 1 << 1,
        Poison = 1 << 2,
        Goo = 1 << 3,
    }

    /// <summary>
    /// 从纯数值层发往表现层的“反应阶段事实”。
    /// NormalizedStrength 被限制在 [0, 1]，方便直接映射到粒子数量、颜色或音量；
    /// ExpectedDuration 只是表现层的时间提示，不能被当成战斗结算依据。
    /// </summary>
    public readonly struct ReactionSignal
    {
        public readonly ElementReactionId Reaction;
        public readonly ElementReactionPhase Phase;
        public readonly float NormalizedStrength;
        public readonly float ExpectedDuration;

        public ReactionSignal(
            ElementReactionId reaction,
            ElementReactionPhase phase,
            float normalizedStrength,
            float expectedDuration)
        {
            Reaction = reaction;
            Phase = phase;
            NormalizedStrength = normalizedStrength;
            ExpectedDuration = expectedDuration;
        }
    }

    /// <summary>
    /// Toxic Combustion 结算后生成的范围伤害命令。
    /// Runtime 只输出值快照，不调用 Physics 或 IDamageable；MonoBehaviour Adapter
    /// 会在外层消费该命令，从而保持数值核心可用 NUnit 独立测试。
    /// </summary>
    public readonly struct ReactionDamageCommand
    {
        public readonly StatusSource Source;
        public readonly float Amount;
        public readonly float Radius;

        public ReactionDamageCommand(StatusSource source, float amount, float radius)
        {
            Source = source;
            Amount = amount;
            Radius = radius;
        }
    }

    /// <summary>
    /// 一个模拟步的纯输出：状态增量、自然衰减抑制、伤害命令和表现信号。
    /// Runtime 不直接修改 StatusController，而是返回 Delta，让 Adapter 在统一位置应用，
    /// 避免“计算到一半就修改真实状态”导致后续反应读取到不可预测的数据。
    /// 最多三个固定 Signal 字段，避免每帧创建数组或 List 造成 GC Alloc。
    /// </summary>
    public struct ElementReactionFrame
    {
        public float FireDelta;
        public float WaterDelta;
        public float PoisonDelta;
        public float GooDelta;
        public StatusMask SuppressNaturalDecay;
        public bool HasAreaDamage;
        public ReactionDamageCommand AreaDamage;
        public int SignalCount;
        public ReactionSignal Signal0;
        public ReactionSignal Signal1;
        public ReactionSignal Signal2;

        /// <summary>
        /// 按固定顺序写入预留槽位；超过三个的信号会被有意丢弃。
        /// 当前同时存在的 Process 上限正好是三种，因此固定容量既表达架构约束，
        /// 也避免为极小集合引入动态容器。
        /// </summary>
        public void AddSignal(in ReactionSignal signal)
        {
            if (SignalCount == 0)
                Signal0 = signal;
            else if (SignalCount == 1)
                Signal1 = signal;
            else if (SignalCount == 2)
                Signal2 = signal;
            else
                return;

            SignalCount++;
        }
    }
}
