using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Skills
{
    /// <summary>纯规则候选构建；Run后续决定何时提交，Machine不触及Scene与真实库存。</summary>
    public sealed class SpellProgramMachine
    {
        public bool TryBuild(SpellProgram program, WandDrawSnapshot before, in SpellExecutionInputs inputs,
            SpellBuildBuffer buffer, out SpellBuildSummary summary)
        {
            if (buffer == null || buffer.IsBuilding)
            {
                summary = new SpellBuildSummary(SpellBuildOutcome.Rejected,
                    new SpellProgramDiagnostic(SpellProgramError.InvalidInput, -1, "工作区不可用或正在构建。"), inputs.AvailableMana, 0, 0, false, 0);
                return false;
            }
            buffer.Reset(); buffer.Mana = inputs.AvailableMana; buffer.IsBuilding = true;
            try
            {
                if (!Validate(program, before, inputs, buffer, out var error))
                    return Reject(buffer, inputs, error, -1, out summary);
                if (program.Count == 0 || before.Deck.Count + before.Discard.Count == 0 && program.IntrinsicCount==0)
                    return Reject(buffer, inputs, SpellProgramError.EmptyProgram, -1, out summary);

                buffer.Mana = inputs.AvailableMana; buffer.ReloadDelta = before.CycleReloadDelta; buffer.Random = before.RandomState;
                for (int i = 0; i < before.Deck.Count; i++) buffer.Deck.Add(before.Deck[i]);
                for (int i = 0; i < before.Discard.Count; i++) buffer.Discard.Add(before.Discard[i]);
                AddScope(buffer, -1, -1, 0, program.BaseSpreadDegrees);
                Push(buffer, 0, program.BaseDraws, false, true, -1);
                // 固定action先执行，再进入普通根Draw；不加入deck/hand，也不占卡槽与物品次数。
                for(int i=program.Count-1;i>=0;i--)if(program.Instructions[i].IsIntrinsic)
                    buffer.Stack[buffer.StackCount++]=new SpellDrawFrame{Scope=0,Remaining=1,Request=buffer.NextRequest++,ParentRequest=-1,
                        Borrowed=true,Intrinsic=true,PermanentContext=true,BorrowedCard=i,CauseCard=-1};

                // 显式栈代替递归C#调用：Modifier链也可很长，不把平台线程栈当玩法预算。
                while (buffer.StackCount > 0)
                {
                    int top = buffer.StackCount - 1;
                    var frame = buffer.Stack[top];
                    if (frame.Remaining <= 0)
                    {
                        buffer.StackCount--;
                        if (frame.ClosesScope)
                            buffer.ScopePool[frame.Scope].ClosedAtStep = buffer.Record(SpellTraceKind.ScopeClosed, frame.Scope, request: frame.Request);
                        buffer.Record(SpellTraceKind.DrawReturned, frame.Scope, request: frame.Request, parentRequest: frame.ParentRequest);
                        continue;
                    }
                    buffer.Stack[top].Remaining--;
                    if (!(frame.Borrowed ? TakeBorrowed(program, inputs, buffer, frame, out int card, out error)
                        : Take(program, inputs, buffer, frame, out card, out error)))
                    {
                        if (error != SpellProgramError.None) return Reject(buffer, inputs, error, card, out summary, program);
                        // Take已扫描本次可用deck（含允许的一次回收）。失败后本frame没有新收入，
                        // 重试只会再次读取相同失败牌。结束当前请求，父请求仍照常返回/继续。
                        buffer.Stack[top].Remaining = 0;
                        continue;
                    }
                    var instruction = program.Instructions[card];
                    var scope = buffer.ScopePool[frame.Scope];
                    scope.CastDelayDelta += instruction.CastDelayDelta; buffer.ReloadDelta += instruction.ReloadDelta;
                    if (!float.IsFinite(scope.CastDelayDelta) || !float.IsFinite(buffer.ReloadDelta))
                        return Reject(buffer, inputs, SpellProgramError.NumericOverflow, card, out summary, program);
                    if (instruction.Kind == SpellKind.Modify)
                    {
                        scope.Modifiers = scope.Modifiers.Apply(instruction.Modifier);
                        if (!Finite(scope.Modifiers)) return Reject(buffer, inputs, SpellProgramError.NumericOverflow, card, out summary, program);
                        int step = buffer.Record(SpellTraceKind.ModifierApplied, scope.Id, card, frame.Request);
                        scope.Contributions.Add(new SpellParameterContribution(card, step, instruction));
                        // 原Lua永久卡规则：draw_actions(1)不额外抽一张；Multicast的多Draw仍照常执行。
                        if (!frame.PermanentContext && !Push(buffer, scope.Id, 1, true, false, frame.Request, card))
                            return Reject(buffer, inputs, SpellProgramError.TooComplex, card, out summary, program);
                    }
                    else
                    {
                        if (instruction.Kind == SpellKind.Copy)
                        {
                            if(!Borrow(program,buffer,frame,instruction.CopyMode,card))
                                return Reject(buffer,inputs,SpellProgramError.TooComplex,card,out summary,program);
                            continue;
                        }
                        if (instruction.CastDelayDelta != 0 || instruction.ReloadDelta != 0)
                            scope.Contributions.Add(new SpellParameterContribution(card, buffer.EventCount, instruction));
                        if (instruction.Kind == SpellKind.Multicast)
                        {
                            if ((!frame.PermanentContext || instruction.DrawCount!=1) && !Push(buffer, scope.Id, instruction.DrawCount, true, false, frame.Request, card,frame.PermanentContext))
                                return Reject(buffer, inputs, SpellProgramError.TooComplex, card, out summary, program);
                            continue;
                        }
                        if (buffer.Drafts.Count >= SpellProgramLimits.MaxOutputs)
                            return Reject(buffer, inputs, SpellProgramError.TooComplex, card, out summary, program);
                        int output = buffer.Drafts.Count;
                        int registered = buffer.Record(SpellTraceKind.OutputRegistered, scope.Id, card, frame.Request, output: output);
                        int child = -1;
                        if (instruction.Kind == SpellKind.Emit && instruction.Emission.PayloadTrigger != PayloadTriggerMode.None)
                        {
                            if (scope.Depth >= SpellProgramLimits.MaxScopeDepth)
                                return Reject(buffer, inputs, SpellProgramError.TooComplex, card, out summary, program);
                            // 新子Scope从默认修正开始，共享牌堆/候选Mana；父修正不会隐式传入。
                            child = AddScope(buffer, scope.Id, output, scope.Depth + 1, 0);
                        }
                        buffer.Drafts.Add(new ShotOutputDraft { Scope = scope.Id, Card = card, Child = child, Step = registered });
                        scope.Outputs.Add(output);
                        if(child>=0 && frame.PermanentContext && instruction.TriggerDrawCount==1)
                            buffer.ScopePool[child].ClosedAtStep=buffer.Record(SpellTraceKind.ScopeClosed,child);
                        else if (child >= 0 && !Push(buffer, child, instruction.TriggerDrawCount, true, true, frame.Request, card,frame.PermanentContext))
                            return Reject(buffer, inputs, SpellProgramError.TooComplex, card, out summary, program);
                    }
                }

                FilterExhausted(buffer, buffer.Deck, SpellCardCollection.Deck);
                FilterExhausted(buffer, buffer.Discard, SpellCardCollection.Discard);
                FilterExhausted(buffer, buffer.Hand, SpellCardCollection.Hand);
                MoveAll(buffer, buffer.Hand, buffer.Discard, SpellCardCollection.Hand, SpellCardCollection.Discard);
                bool reload = buffer.Wrapped || buffer.Deck.Count == 0;
                if (reload)
                {
                    if (before.CycleIndex == int.MaxValue) return Reject(buffer, inputs, SpellProgramError.InvalidState, -1, out summary);
                    MoveAll(buffer, buffer.Discard, buffer.Deck, SpellCardCollection.Discard, SpellCardCollection.Deck);
                    Reorder(buffer, program.Shuffle);
                    buffer.Record(SpellTraceKind.ReloadPrepared);
                }
                float castTime = program.BaseCastDelay + buffer.ScopePool[0].CastDelayDelta;
                float reloadTime = program.BaseReloadTime + buffer.ReloadDelta;
                if (!float.IsFinite(castTime) || !float.IsFinite(reloadTime))
                    return Reject(buffer, inputs, SpellProgramError.NumericOverflow, -1, out summary);
                if (!FreezeGraph(program, buffer, out var graph))
                    return Reject(buffer, inputs, SpellProgramError.NumericOverflow, -1, out summary);

                buffer.Graph = graph;
                buffer.NextState = new WandDrawSnapshot(buffer.Deck, buffer.Discard, program.Revision,
                    before.ExecutionRevision + 1, before.CycleIndex + (reload ? 1 : 0), buffer.Random, reload ? 0 : buffer.ReloadDelta);
                var uses = new int[program.Count]; Array.Copy(buffer.Uses, uses, uses.Length);
                buffer.RemainingUsesAfter = Array.AsReadOnly(uses);
                buffer.Record(SpellTraceKind.BuildCompleted); buffer.FreezeTrace();
                summary = new SpellBuildSummary(graph.RootOutputCount > 0 ? SpellBuildOutcome.Emitted : SpellBuildOutcome.AdvancedWithoutEmission,
                    default, buffer.Mana, Mathf.Max(SpellProgramLimits.MinimumInterval, castTime),
                    Mathf.Max(SpellProgramLimits.MinimumInterval, reloadTime), reload, buffer.Steps);
                return true;
            }
            finally { buffer.IsBuilding = false; }
        }

        private static bool Validate(SpellProgram p, WandDrawSnapshot state, in SpellExecutionInputs input,
            SpellBuildBuffer b, out SpellProgramError error)
        {
            error = SpellProgramError.InvalidInput;
            if (p == null || state == null || !float.IsFinite(input.AvailableMana) || !float.IsFinite(input.MaxMana)
                || input.AvailableMana < 0 || input.MaxMana < input.AvailableMana || !float.IsFinite(input.HpRatio)
                || input.HpRatio < 0 || input.HpRatio > 1 || input.RemainingUses != null && input.RemainingUses.Count != p.Count
                || input.Eligibility != null && input.Eligibility.Count != p.Count) return false;
            error = SpellProgramError.InvalidState;
            if (state.ProgramRevision != p.Revision || state.ExecutionRevision < 0 || state.ExecutionRevision == long.MaxValue
                || state.CycleIndex < 1 || state.RandomState == 0 || !float.IsFinite(state.CycleReloadDelta)) return false;
            for (int i = 0; i < p.Count; i++)
            {
                b.Uses[i] = input.RemainingUses == null ? -1 : input.RemainingUses[i];
                if (b.Uses[i] < -1) return false;
            }
            if (!ValidatePile(state.Deck, p.Count, b.Seen) || !ValidatePile(state.Discard, p.Count, b.Seen)) return false;
            for (int i = 0; i < p.Count; i++)
                if (p.Instructions[i].IsIntrinsic ? b.Seen[i] : !b.Seen[i] && b.Uses[i] != 0) return false;
            error = SpellProgramError.None; return true;
        }
        private static bool ValidatePile(IReadOnlyList<int> pile, int count, bool[] seen)
        {
            for (int i = 0; i < pile.Count; i++)
            { int index = pile[i]; if (index < 0 || index >= count || seen[index]) return false; seen[index] = true; }
            return true;
        }
        private static int AddScope(SpellBuildBuffer b, int parent, int owner, int depth, float spread)
        {
            int id = b.ScopeCount++;
            if (id == b.ScopePool.Count) b.ScopePool.Add(new ShotScopeBuffer());
            b.ScopePool[id].Reset(id, parent, owner, depth, spread); b.Record(SpellTraceKind.ScopeOpened, id, output: owner);
            return id;
        }
        private static bool Push(SpellBuildBuffer b, int scope, int draws, bool wrap, bool closes, int parent, int causeCard = -1,bool permanent=false)
        {
            if (b.StackCount >= b.Stack.Length) return false;
            int request = b.NextRequest++;
            b.Stack[b.StackCount++] = new SpellDrawFrame { Scope = scope, Remaining = draws, CanWrap = wrap,
                ClosesScope = closes, Request = request, ParentRequest = parent,PermanentContext=permanent };
            b.Record(SpellTraceKind.DrawRequested, scope, card: causeCard, request: request, parentRequest: parent); return true;
        }
        private static bool Take(SpellProgram p, in SpellExecutionInputs input, SpellBuildBuffer b,
            SpellDrawFrame frame, out int card, out SpellProgramError error)
        {
            card = -1; error = SpellProgramError.None;
            if (b.Deck.Count == 0 && frame.CanWrap && b.Discard.Count > 0)
            {
                MoveAll(b, b.Discard, b.Deck, SpellCardCollection.Discard, SpellCardCollection.Deck);
                Reorder(b, p.Shuffle); b.Wrapped = true; b.Record(SpellTraceKind.WrapRecovered, frame.Scope, request: frame.Request);
            }
            // 失败后只重试当前deck剩余项；不在一次失败Draw内无限回收昂贵卡。
            while (b.Deck.Count > 0)
            {
                card = b.Deck[0];
                if (b.Steps >= SpellProgramLimits.MaxSteps) { error = SpellProgramError.TooComplex; return false; }
                b.Steps++; b.Deck.RemoveAt(0); var instruction = p.Instructions[card];
                b.Record(SpellTraceKind.CardDrawn, frame.Scope, card, frame.Request, frame.ParentRequest);
                var skipped = b.Uses[card] == 0 ? SpellTraceKind.UsesSkipped
                    : input.Eligibility != null && !input.Eligibility[card] ? SpellTraceKind.EligibilitySkipped
                    : instruction.ManaCost > b.Mana ? SpellTraceKind.ManaSkipped : SpellTraceKind.CardDrawn;
                if (skipped != SpellTraceKind.CardDrawn)
                {
                    b.Discard.Add(card); b.Record(skipped, frame.Scope, card, frame.Request);
                    b.Record(SpellTraceKind.CardMoved, frame.Scope, card, frame.Request, from: SpellCardCollection.Deck, to: SpellCardCollection.Discard);
                    continue;
                }
                float previousMana = b.Mana; int previousUses = b.Uses[card];
                // 双精度中间量避免合法负Mana在钳制前发生float溢出。
                b.Mana = (float)Math.Max(0d, Math.Min(input.MaxMana, (double)b.Mana - instruction.ManaCost));
                if (b.Uses[card] > 0) b.Uses[card]--;
                b.Hand.Add(card);
                // 回放的资源变化必须落在支付事件上，不能在前一个集合移动步骤提前显示扣费。
                b.Record(SpellTraceKind.ManaPaid, frame.Scope, card, frame.Request, before: previousMana,
                    usesBefore: previousUses, usesAfter: b.Uses[card]);
                b.Record(SpellTraceKind.CardMoved, frame.Scope, card, frame.Request, from: SpellCardCollection.Deck, to: SpellCardCollection.Hand);
                return true;
            }
            card = -1; return false;
        }
        private static bool TakeBorrowed(SpellProgram p,in SpellExecutionInputs input,SpellBuildBuffer b,SpellDrawFrame frame,out int card,out SpellProgramError error)
        {
            card=frame.BorrowedCard;error=SpellProgramError.None;
            if(b.Steps>=SpellProgramLimits.MaxSteps){error=SpellProgramError.TooComplex;return false;}
            b.Steps++;
            if(frame.Intrinsic)
            {
                float previous=b.Mana;
                // 固定卡免正Mana；负成本的AddMana仍只在本次固定调用获得一次。
                if(p.Instructions[card].ManaCost<0)b.Mana=(float)Math.Min(input.MaxMana,(double)b.Mana-p.Instructions[card].ManaCost);
                b.Record(SpellTraceKind.IntrinsicActionInvoked,frame.Scope,card,frame.Request,before:previous,invocation:SpellInvocationMode.Intrinsic);
                return true;
            }
            // 手中本次刚支付最后一次的卡仍存在；上次Root已耗尽并过滤的卡不会出现在可寻址集合。
            bool eligible=input.Eligibility==null||input.Eligibility[card];
            bool alive=input.RemainingUses==null||input.RemainingUses[card]!=0;
            b.Record(!alive?SpellTraceKind.UsesSkipped:!eligible?SpellTraceKind.EligibilitySkipped:SpellTraceKind.BorrowedActionInvoked,
                frame.Scope,card,frame.Request,frame.ParentRequest,from:frame.TargetCollection,
                invocation:SpellInvocationMode.BorrowedCopy,causeCard:frame.CauseCard,targetPosition:frame.TargetPosition);
            return alive&&eligible;
        }
        private static bool Borrow(SpellProgram p,SpellBuildBuffer b,SpellDrawFrame frame,SpellCopyMode mode,int cause)
        {
            List<int> pile;SpellCardCollection collection;int position;
            if(mode==SpellCopyMode.Alpha)
            {pile=b.Discard.Count>0?b.Discard:b.Hand.Count>0?b.Hand:b.Deck;collection=pile==b.Discard?SpellCardCollection.Discard:pile==b.Hand?SpellCardCollection.Hand:SpellCardCollection.Deck;position=0;}
            else{pile=b.Deck.Count>0?b.Deck:b.Hand;collection=pile==b.Deck?SpellCardCollection.Deck:SpellCardCollection.Hand;position=pile.Count-1;}
            if(pile.Count==0)return true;
            int card=pile[position],depth=frame.RecursionDepth;
            // 对照原gun.lua的check_recursion：只在目标是recursive的Copy时加深；达到2是合法空操作。
            if(p.Instructions[card].Kind==SpellKind.Copy)
            {
                if(depth>=2){b.Record(SpellTraceKind.CopyRecursionRejected,frame.Scope,card,frame.Request,from:collection,
                    invocation:SpellInvocationMode.BorrowedCopy,causeCard:cause,targetPosition:position);return true;}
                depth++;
            }
            if(b.StackCount>=b.Stack.Length)return false;
            int request=b.NextRequest++;
            b.Stack[b.StackCount++]=new SpellDrawFrame{Scope=frame.Scope,Remaining=1,Request=request,ParentRequest=frame.Request,
                Borrowed=true,PermanentContext=frame.PermanentContext,BorrowedCard=card,RecursionDepth=depth,CauseCard=cause,TargetPosition=position,TargetCollection=collection};
            return true;
        }
        private static void MoveAll(SpellBuildBuffer b, List<int> source, List<int> destination,
            SpellCardCollection from, SpellCardCollection to)
        {
            for (int i = 0; i < source.Count; i++)
            { int card = source[i]; destination.Add(card); b.Record(SpellTraceKind.CardMoved, card: card, from: from, to: to); }
            source.Clear();
        }
        private static void Reorder(SpellBuildBuffer b, bool shuffle)
        {
            SpellDeckOrdering.Order(b.Deck, shuffle, ref b.Random);
            // 把最终排列表示成依次移到deck尾部，回放器无需实现Shuffle/排序规则。
            for (int i = 0; i < b.Deck.Count; i++)
                b.Record(SpellTraceKind.CardMoved, card: b.Deck[i], from: SpellCardCollection.Deck, to: SpellCardCollection.Deck);
        }
        private static void FilterExhausted(SpellBuildBuffer b, List<int> pile, SpellCardCollection from)
        {
            for (int i = pile.Count - 1; i >= 0; i--)
                if (b.Uses[pile[i]] == 0)
                { int card = pile[i]; pile.RemoveAt(i); b.Record(SpellTraceKind.CardMoved, card: card, from: from); }
        }
        private static bool FreezeGraph(SpellProgram p, SpellBuildBuffer b, out ShotExecutionGraph graph)
        {
            graph = null; var scopes = new ShotScope[b.ScopeCount];
            var contributions = new IReadOnlyList<SpellParameterContribution>[b.ScopeCount];
            for (int i = 0; i < scopes.Length; i++)
            {
                var s = b.ScopePool[i];
                scopes[i] = new ShotScope(i, s.Parent, s.OwnerOutput, s.ClosedAtStep, s.Modifiers, s.CastDelayDelta, s.Outputs.ToArray());
                contributions[i] = Array.AsReadOnly(s.Contributions.ToArray());
            }
            var outputs = new ShotOutput[b.Drafts.Count];
            for (int i = 0; i < outputs.Length; i++)
            {
                var draft = b.Drafts[i]; var instruction = p.Instructions[draft.Card];
                var modifiers = b.ScopePool[draft.Scope].Modifiers;
                int child = draft.Child >= 0 && b.ScopePool[draft.Child].Outputs.Count > 0 ? draft.Child : -1;
                var command = SpellCommandBaker.Bake(instruction.Emission, modifiers, graphPayload: child >= 0);
                if (!float.IsFinite(command.Damage) || !float.IsFinite(command.Speed) || !float.IsFinite(command.ExplosionDamage)
                    || !float.IsFinite(command.FireFieldDamagePerTick)) return false;
                var provenance = new SpellParameterProvenance(draft.Scope, instruction.Emission, modifiers, command, contributions[draft.Scope]);
                outputs[i] = new ShotOutput(i, draft.Scope, draft.Card, instruction.InstanceId, child, draft.Step, command, provenance);
            }
            graph = new ShotExecutionGraph(p, scopes, outputs); return true;
        }
        private static bool Finite(in CastModifierState m)
        {
            return float.IsFinite(m.LifetimeMul) && float.IsFinite(m.LifetimeAddSeconds) && float.IsFinite(m.DamageAddFlat) && float.IsFinite(m.DamageMul) && float.IsFinite(m.SpeedMul)
                && float.IsFinite(m.SpreadDegrees) && float.IsFinite(m.HomingRadius) && float.IsFinite(m.HomingDuration)
                && float.IsFinite(m.HomingTurnRateDegrees) && float.IsFinite(m.OrbitRadius) && float.IsFinite(m.OrbitAngularSpeedDegrees)
                && float.IsFinite(m.OrbitPhaseOffsetDegrees) && float.IsFinite(m.OrbitPlaneTiltDegrees);
        }
        private static bool Reject(SpellBuildBuffer b, in SpellExecutionInputs input, SpellProgramError error,
            int card, out SpellBuildSummary summary, SpellProgram program = null)
        {
            b.Record(SpellTraceKind.BuildRejected, card: card, error: error); b.FreezeTrace();
            int slot = program != null && card >= 0 ? program.Instructions[card].SourceSlot : -1;
            summary = new SpellBuildSummary(SpellBuildOutcome.Rejected, new SpellProgramDiagnostic(error, slot, error.ToString()),
                input.AvailableMana, 0, 0, false, b.Steps);
            return false;
        }
    }
}
