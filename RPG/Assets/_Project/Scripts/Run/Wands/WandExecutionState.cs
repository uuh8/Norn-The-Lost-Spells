using System;
using System.Collections.Generic;
using Game.Skills;

namespace Game.Run
{
    /// <summary>Run的只读执行起点；资源仍由WandLoadoutService唯一持有。</summary>
    public readonly struct WandExecutionSnapshot
    {
        public readonly SpellProgram Program;
        public readonly WandDrawSnapshot Draw;
        public readonly float Mana, CastRemaining, ReloadRemaining;
        public readonly uint ResourceVersion;
        internal WandExecutionSnapshot(SpellProgram program, WandDrawSnapshot draw, float mana,
            float cast, float reload, uint version)
        { Program = program; Draw = draw; Mana = mana; CastRemaining = cast; ReloadRemaining = reload; ResourceVersion = version; }
    }

    /// <summary>只由资源持有者写入；和预览的副本不是同一个状态。</summary>
    internal sealed class WandExecutionState
    {
        internal SpellProgram Program;
        internal WandDrawSnapshot Draw;
        internal float CastRemaining, ReloadRemaining;
        internal WandExecutionState(SpellProgram program, string instanceId)
        {
            Program = program;
            // FNV-1a按字符确定种子，不使用跨进程不稳定的string.GetHashCode。
            uint seed = 2166136261;
            for (int i = 0; i < instanceId.Length; i++) seed = (seed ^ instanceId[i]) * 16777619;
            Draw = WandDrawSnapshot.CreateInitial(program, seed);
        }
        internal void Reprogram(SpellProgram program)
        {
            var next = WandDrawSnapshot.CreateInitial(program, Draw.RandomState);
            Program = program;
            Draw = new WandDrawSnapshot(next.Deck, next.Discard, program.Revision, Draw.ExecutionRevision,
                Draw.CycleIndex, next.RandomState);
            ReloadRemaining = Math.Max(ReloadRemaining, program.BaseReloadTime);
        }
        internal void Commit(SpellProgram program, WandDrawSnapshot next, in SpellBuildSummary summary)
        {
            if (ReferenceEquals(program, Program)) Draw = next;
            else
            {
                // 耗尽只按稳定ID重映射索引；不重新抽种子、不把discard重建为满deck。
                var indices = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < program.Count; i++) indices.Add(program.Instructions[i].InstanceId, i);
                Draw = new WandDrawSnapshot(Remap(next.Deck, indices), Remap(next.Discard, indices),
                    program.Revision, next.ExecutionRevision, next.CycleIndex, next.RandomState, next.CycleReloadDelta);
            }
            Program = program; CastRemaining = summary.CastInterval;
            ReloadRemaining = summary.NeedsReload ? summary.ReloadInterval : 0;
        }
        private List<int> Remap(IReadOnlyList<int> source, Dictionary<string, int> indices)
        {
            var result = new List<int>(source.Count);
            for (int i = 0; i < source.Count; i++)
                if (indices.TryGetValue(Program.Instructions[source[i]].InstanceId, out int index)) result.Add(index);
            return result;
        }
        internal void Tick(float dt)
        { CastRemaining = Math.Max(0, CastRemaining - dt); ReloadRemaining = Math.Max(0, ReloadRemaining - dt); }
    }
}
