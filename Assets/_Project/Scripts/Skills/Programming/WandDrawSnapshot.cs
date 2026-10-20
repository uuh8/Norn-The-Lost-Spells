using System;
using System.Collections.Generic;

namespace Game.Skills
{
    /// <summary>一根法杖在两个Root之间的冻结牌堆；构建候选从副本出发，不能写此对象。</summary>
    public sealed class WandDrawSnapshot
    {
        public IReadOnlyList<int> Deck { get; }
        public IReadOnlyList<int> Discard { get; }
        public long ProgramRevision { get; }
        public long ExecutionRevision { get; }
        public int CycleIndex { get; }
        public uint RandomState { get; }
        public float CycleReloadDelta { get; }

        public WandDrawSnapshot(IReadOnlyList<int> deck, IReadOnlyList<int> discard, long programRevision,
            long executionRevision = 0, int cycleIndex = 1, uint randomState = 1, float cycleReloadDelta = 0)
        {
            Deck = Freeze(deck); Discard = Freeze(discard); ProgramRevision = programRevision;
            ExecutionRevision = executionRevision; CycleIndex = cycleIndex;
            RandomState = randomState; CycleReloadDelta = cycleReloadDelta;
        }
        public static WandDrawSnapshot CreateInitial(SpellProgram program, uint seed = 1)
        {
            if (program == null) throw new ArgumentNullException(nameof(program));
            var deck = new List<int>(program.Count);
            for (int i = 0; i < program.Count; i++) if(!program.Instructions[i].IsIntrinsic)deck.Add(i);
            uint random = seed == 0 ? 1 : seed;
            SpellDeckOrdering.Order(deck, program.Shuffle, ref random);
            return new WandDrawSnapshot(deck, null, program.Revision, randomState: random);
        }
        internal static IReadOnlyList<int> Freeze(IReadOnlyList<int> source)
        {
            var copy = new int[source == null ? 0 : source.Count];
            for (int i = 0; i < copy.Length; i++) copy[i] = source[i];
            return Array.AsReadOnly(copy);
        }
    }

    internal static class SpellDeckOrdering
    {
        internal static void Order(List<int> deck, bool shuffle, ref uint state)
        {
            if (!shuffle) { deck.Sort(); return; }
            // 固定xorshift32与Fisher-Yates，查询/预览使用副本；不依赖全局Unity.Random。
            for (int i = deck.Count - 1; i > 0; i--)
            {
                state ^= state << 13; state ^= state >> 17; state ^= state << 5;
                int j = (int)(state % (uint)(i + 1));
                int swap = deck[i]; deck[i] = deck[j]; deck[j] = swap;
            }
        }
    }

    /// <summary>场景和库存的数值输入；Run负责真实归属/资格，Skills只读取已捕获的值。</summary>
    public readonly struct SpellExecutionInputs
    {
        public readonly float AvailableMana, MaxMana, HpRatio;
        public readonly bool SelfWet, SelfBurning;
        public readonly long InputVersion;
        public readonly IReadOnlyList<int> RemainingUses;
        public readonly IReadOnlyList<bool> Eligibility;
        public SpellExecutionInputs(float availableMana, float maxMana = 100,
            IReadOnlyList<int> remainingUses = null, IReadOnlyList<bool> eligibility = null,
            float hpRatio = 1, bool selfWet = false, bool selfBurning = false, long inputVersion = 0)
        {
            AvailableMana = availableMana; MaxMana = maxMana; HpRatio = hpRatio;
            SelfWet = selfWet; SelfBurning = selfBurning; InputVersion = inputVersion;
            RemainingUses = remainingUses == null ? null : WandDrawSnapshot.Freeze(remainingUses);
            if (eligibility == null) Eligibility = null;
            else
            {
                var copy = new bool[eligibility.Count];
                for (int i = 0; i < copy.Length; i++) copy[i] = eligibility[i];
                Eligibility = Array.AsReadOnly(copy);
            }
        }
    }
}
