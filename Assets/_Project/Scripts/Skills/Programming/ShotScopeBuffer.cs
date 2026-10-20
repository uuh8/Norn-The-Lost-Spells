using System.Collections.Generic;

namespace Game.Skills
{
    /// <summary>只在一次候选构建中可变；关闭Scope后才为本组全部输出生成最终值。</summary>
    internal sealed class ShotScopeBuffer
    {
        internal int Id, Parent, OwnerOutput, Depth, ClosedAtStep;
        internal float CastDelayDelta;
        internal CastModifierState Modifiers;
        internal readonly List<int> Outputs = new List<int>();
        internal readonly List<SpellParameterContribution> Contributions = new List<SpellParameterContribution>();
        internal void Reset(int id, int parent, int owner, int depth, float baseSpread)
        {
            Id = id; Parent = parent; OwnerOutput = owner; Depth = depth; ClosedAtStep = -1; CastDelayDelta = 0;
            Modifiers = new CastModifierState(0, 1, 1, baseSpread, 0, false, 0, 0, 0, 0, 0, 0, 0, ProjectileMotionMode.None);
            Outputs.Clear(); Contributions.Clear();
        }
    }
}
