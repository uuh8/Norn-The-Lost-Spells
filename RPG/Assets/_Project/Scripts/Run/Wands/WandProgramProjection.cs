using System.Collections;
using System.Collections.Generic;
using Game.Skills;

namespace Game.Run
{
    /// <summary>私有数组只在离散库存变动时构造；旧快照不会被后来的编辑覆盖，null 保留程序空槽。</summary>
    public sealed class WandProgramProjection : IReadOnlyList<SpellDefinition>
    {
        private readonly SpellDefinition[] _spells;
        private readonly string[] _ids;
        internal WandProgramProjection(SpellDefinition[] spells, string[] ids) { _spells = spells; _ids = ids; }
        public int Count => _spells.Length;
        public SpellDefinition this[int index] => _spells[index];
        public string GetInstanceId(int index) => _ids[index];
        public IEnumerator<SpellDefinition> GetEnumerator() => ((IEnumerable<SpellDefinition>)_spells).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        internal bool Matches(WandProgramProjection other)
        {
            if (Count != other.Count) return false;
            for (int i = 0; i < Count; i++) if (_ids[i] != other._ids[i] || _spells[i] != other._spells[i]) return false;
            return true;
        }
    }
    public readonly struct WandProgramSnapshot
    {
        public readonly string SourceWandId;
        public readonly uint ProgramRevision;
        public readonly int BaseDraws;
        public readonly WandProgramProjection Program;
        public WandProgramSnapshot(string source, uint revision, int draws, WandProgramProjection program)
        { SourceWandId = source; ProgramRevision = revision; BaseDraws = draws; Program = program; }
    }
}
