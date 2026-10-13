using System.Collections.Generic;

namespace Game.Skills
{
    // 只记录成功执行的指令，容量可复用；预算阻止、空槽和无法支付都不计入。
    public sealed class CastExecutionRecorder
    {
        private readonly List<CastExecutionUse> _uses = new List<CastExecutionUse>(16);
        public int Count => _uses.Count;
        public CastExecutionUse this[int index] => _uses[index];
        internal void Clear() => _uses.Clear();
        internal void Record(in CastExecutionUse use) => _uses.Add(use);
    }
}
