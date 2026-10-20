using System;
using System.Collections.Generic;

namespace Game.Skills
{
    public enum SpellInvocationMode : byte { Draw, BorrowedCopy, Intrinsic }
    public enum SpellCardCollection : byte { None, Deck, Hand, Discard }
    public enum SpellTraceKind : byte
    {
        ScopeOpened, DrawRequested, CardDrawn, CardMoved, ManaPaid, ManaSkipped, UsesSkipped,
        EligibilitySkipped, ModifierApplied, OutputRegistered, ScopeClosed, DrawReturned,
        WrapRecovered, ReloadPrepared, BuildCompleted, BuildRejected,
        BorrowedActionInvoked, CopyRecursionRejected, IntrinsicActionInvoked
    }

    /// <summary>值记录保留“因为什么”；候选Mana不表示已经提交到玩家库存。</summary>
    public readonly struct SpellExecutionTraceStep
    {
        public readonly int StepId, ScopeId, CardIndex, RequestId, ParentRequestId, OutputId;
        public readonly int CauseCardIndex, TargetPosition;
        public readonly SpellTraceKind Kind;
        public readonly SpellInvocationMode InvocationMode;
        public readonly SpellCardCollection From, To;
        public readonly float ManaBefore, ManaAfter;
        public readonly int UsesBefore, UsesAfter;
        public readonly SpellProgramError Error;
        public SpellExecutionTraceStep(int stepId, SpellTraceKind kind, int scopeId, int cardIndex,
            int requestId, int parentRequestId, int outputId, float manaBefore, float manaAfter,
            SpellCardCollection from = SpellCardCollection.None, SpellCardCollection to = SpellCardCollection.None,
            SpellProgramError error = SpellProgramError.None, SpellInvocationMode invocationMode = SpellInvocationMode.Draw,
            int usesBefore = -1, int usesAfter = -1, int causeCardIndex = -1, int targetPosition = -1)
        {
            StepId = stepId; Kind = kind; ScopeId = scopeId; CardIndex = cardIndex;
            RequestId = requestId; ParentRequestId = parentRequestId; OutputId = outputId;
            ManaBefore = manaBefore; ManaAfter = manaAfter; From = from; To = to; Error = error;
            InvocationMode = invocationMode;
            UsesBefore = usesBefore; UsesAfter = usesAfter;
            CauseCardIndex=causeCardIndex;TargetPosition=targetPosition;
        }
    }

    public sealed class SpellExecutionTrace
    {
        public IReadOnlyList<SpellExecutionTraceStep> Steps { get; }
        public int TotalEvents { get; }
        public bool Truncated { get; }
        public bool WasCaptured { get; }
        internal SpellExecutionTrace(SpellExecutionTraceStep[] steps, int total, bool truncated, bool captured)
        { Steps = Array.AsReadOnly(steps); TotalEvents = total; Truncated = truncated; WasCaptured = captured; }
    }

    /// <summary>UI只回放集合移动，不重新实现Draw；游标移动复用三个List。</summary>
    public sealed class SpellTracePileReplay
    {
        private readonly List<int> _deck = new List<int>(), _hand = new List<int>(), _discard = new List<int>();
        private readonly IReadOnlyList<int> _deckView, _handView, _discardView;
        public IReadOnlyList<int> Deck => _deckView;
        public IReadOnlyList<int> Hand => _handView;
        public IReadOnlyList<int> Discard => _discardView;
        public SpellTracePileReplay()
        { _deckView = _deck.AsReadOnly(); _handView = _hand.AsReadOnly(); _discardView = _discard.AsReadOnly(); }
        public bool Seek(WandDrawSnapshot before, SpellExecutionTrace trace, int throughStep)
        {
            _deck.Clear(); _hand.Clear(); _discard.Clear();
            for (int i = 0; i < before.Deck.Count; i++) _deck.Add(before.Deck[i]);
            for (int i = 0; i < before.Discard.Count; i++) _discard.Add(before.Discard[i]);
            if (!trace.WasCaptured || trace.Truncated) return false;
            for (int i = 0; i < trace.Steps.Count && trace.Steps[i].StepId <= throughStep; i++)
            {
                var step = trace.Steps[i];
                if (step.Kind != SpellTraceKind.CardMoved) continue;
                Pile(step.From)?.Remove(step.CardIndex); Pile(step.To)?.Add(step.CardIndex);
            }
            // 排序也是Deck内部顺序移动；回放器不复制解释器的随机算法。
            return true;
        }
        private List<int> Pile(SpellCardCollection kind)
        { return kind == SpellCardCollection.Deck ? _deck : kind == SpellCardCollection.Hand ? _hand : kind == SpellCardCollection.Discard ? _discard : null; }
    }
}
