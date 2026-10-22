using System.Collections.Generic;

namespace Game.Skills
{
    /// <summary>
    /// 一次 CastEvaluator 诊断过程的可复用记录容器。
    /// sealed 表示该类型不允许继承，避免子类改写收集语义；它由 SpellCaster 长期持有，
    /// 每次详细 Trace 前 Clear 并复用 List 的内部容量，而不是为每次施法创建新集合。
    /// </summary>
    public sealed class CastTraceCollector
    {
        // List<T> 提供连续索引访问；Trace 要保持解释器发生顺序，因此不使用无序集合。
        private readonly List<CastTraceStep> _steps;

        /// <summary>当前已记录的有效步骤数量，不等同于 List 的 Capacity。</summary>
        public int Count => _steps.Count;
        /// <summary>索引器：允许 collector[i] 形式读取；越界行为与 List 索引器一致，会抛异常。</summary>
        public CastTraceStep this[int index] => _steps[index];
        /// <summary>
        /// 以 IReadOnlyList 视图暴露结果，调用方可以遍历和索引，但不能通过该属性 Add/Remove。
        /// 这不是深拷贝；底层仍是同一个 _steps，下一次 Clear 后该视图看到的 Count 也会变化。
        /// </summary>
        public IReadOnlyList<CastTraceStep> Steps => _steps;
        /// <summary>本轮 Record 是否曾把 Count 推到旧 Capacity 边界，用于发现诊断集合扩容。</summary>
        public bool ExpandedDuringLastCollection { get; private set; }

        /// <summary>
        /// 预分配 initialCapacity 个槽位，降低常见 Trace 长度下的 List 扩容；负数按 0 处理，避免构造异常。
        /// Capacity 只预留内存，不会创建对应数量的 CastTraceStep，初始 Count 仍为 0。
        /// </summary>
        public CastTraceCollector(int initialCapacity = 64)
        {
            _steps = new List<CastTraceStep>(initialCapacity > 0 ? initialCapacity : 0);
        }

        public void Clear()
        {
            // List.Clear 把 Count 归零，但通常保留内部数组和 Capacity，便于下一次施法复用。
            _steps.Clear();
            ExpandedDuringLastCollection = false;
        }

        /// <summary>按解释器发生顺序追加一条不可变快照，并记录本次是否触发 List 扩容。</summary>
        public void Record(CastTraceStep step)
        {
            // Add 前 Count == Capacity，说明没有空槽；随后的 Add 会要求 List 扩大内部数组。
            if (_steps.Count == _steps.Capacity)
                ExpandedDuringLastCollection = true;

            _steps.Add(step);
        }
    }
}
