using System;

namespace Game.Character
{
    /// <summary>
    /// 数值按战斗推进顺序递增，使 Resolver 可以用“只取更高阶段”保证 Phase 不回退。
    /// </summary>
    public enum BossPhase : byte
    {
        Phase1 = 1,
        Phase2 = 2,
        Phase3 = 3,
    }

    [Flags]
    public enum BossPhaseMask : byte
    {
        None = 0,
        Phase1 = 1 << 0,
        Phase2 = 1 << 1,
        Phase3 = 1 << 2,
        All = Phase1 | Phase2 | Phase3,
    }
}
