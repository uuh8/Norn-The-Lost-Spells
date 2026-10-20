using System;
using System.Collections.Generic;

namespace Game.Skills
{
    public enum SpellBuildOutcome : byte { Rejected, Emitted, AdvancedWithoutEmission }
    public readonly struct SpellBuildSummary
    {
        public readonly SpellBuildOutcome Outcome;
        public readonly SpellProgramDiagnostic Diagnostic;
        public readonly float ManaAfter, CastInterval, ReloadInterval;
        public readonly bool NeedsReload;
        public readonly int InstructionSteps;
        internal SpellBuildSummary(SpellBuildOutcome outcome, SpellProgramDiagnostic diagnostic,
            float manaAfter, float cast, float reload, bool needsReload, int steps)
        {
            Outcome = outcome; Diagnostic = diagnostic; ManaAfter = manaAfter; CastInterval = cast;
            ReloadInterval = reload; NeedsReload = needsReload; InstructionSteps = steps;
        }
    }

    internal struct SpellDrawFrame
    { internal int Scope, Remaining, Request, ParentRequest, BorrowedCard, RecursionDepth, CauseCard, TargetPosition; internal bool CanWrap, ClosesScope, Borrowed, Intrinsic, PermanentContext; internal SpellCardCollection TargetCollection; }
    internal struct ShotOutputDraft
    { internal int Scope, Card, Child, Step; }

    /// <summary>服务/面板持有并复用工作区；Graph/NextState只在成功结束后公开，拒绝不泄漏半成品。</summary>
    public sealed class SpellBuildBuffer
    {
        public ShotExecutionGraph Graph { get; internal set; }
        public WandDrawSnapshot NextState { get; internal set; }
        public SpellExecutionTrace Trace { get; internal set; }
        public IReadOnlyList<int> RemainingUsesAfter { get; internal set; }
        public bool CaptureTrace { get; set; }
        public int TraceCapacity { get; }
        internal readonly List<int> Deck = new List<int>(), Hand = new List<int>(), Discard = new List<int>();
        internal readonly List<ShotScopeBuffer> ScopePool = new List<ShotScopeBuffer>();
        internal readonly List<ShotOutputDraft> Drafts = new List<ShotOutputDraft>();
        internal readonly List<SpellExecutionTraceStep> Events = new List<SpellExecutionTraceStep>();
        internal readonly int[] Uses = new int[SpellProgramLimits.MaxInstructions];
        internal readonly bool[] Seen = new bool[SpellProgramLimits.MaxInstructions];
        internal readonly SpellDrawFrame[] Stack = new SpellDrawFrame[SpellProgramLimits.MaxSteps + SpellProgramLimits.MaxScopeDepth + 2];
        internal int ScopeCount, StackCount, Steps, EventCount, NextRequest;
        internal float Mana, ReloadDelta;
        internal uint Random;
        internal bool Wrapped, IsBuilding, TraceTruncated;
        public SpellBuildBuffer(bool captureTrace = true, int traceCapacity = SpellProgramLimits.MaxTraceEvents)
        { CaptureTrace = captureTrace; TraceCapacity = Math.Max(0, Math.Min(traceCapacity, SpellProgramLimits.MaxTraceEvents)); }
        internal void Reset()
        {
            Graph = null; NextState = null; Trace = null; RemainingUsesAfter = null;
            Deck.Clear(); Hand.Clear(); Discard.Clear(); Drafts.Clear(); Events.Clear();
            ScopeCount = 0; StackCount = 0; Steps = 0; EventCount = 0; NextRequest = 0;
            Wrapped = false; TraceTruncated = false; Array.Clear(Seen, 0, Seen.Length);
        }
        internal int Record(SpellTraceKind kind, int scope = -1, int card = -1, int request = -1,
            int parentRequest = -1, int output = -1, float before = -1, SpellCardCollection from = SpellCardCollection.None,
            SpellCardCollection to = SpellCardCollection.None, SpellProgramError error = SpellProgramError.None,
            int usesBefore = -1, int usesAfter = -1, SpellInvocationMode invocation=SpellInvocationMode.Draw,
            int causeCard=-1,int targetPosition=-1)
        {
            int id = EventCount++;
            // 即使不收集Trace也推进逻辑事件号，保持Graph来源定位与记录开关无关。
            if (CaptureTrace)
            {
                if (Events.Count < TraceCapacity) Events.Add(new SpellExecutionTraceStep(id, kind, scope, card,
                    request, parentRequest, output, before < 0 ? Mana : before, Mana, from, to, error,
                    usesBefore: usesBefore, usesAfter: usesAfter, invocationMode: invocation,
                    causeCardIndex:causeCard,targetPosition:targetPosition));
                else TraceTruncated = true;
            }
            return id;
        }
        internal void FreezeTrace() => Trace = new SpellExecutionTrace(Events.ToArray(), EventCount, TraceTruncated, CaptureTrace);
    }
}
