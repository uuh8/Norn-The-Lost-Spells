using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace Game.Skills.Tests
{
    public sealed class SpellProgramMachineTests : SpellProgramTestSupport
    {
        [Test] public void DrawMachineContract_IsAvailableAsAnIndependentInterpreter()
        {
            Assert.That(typeof(CastEvaluator).Assembly.GetType("Game.Skills.SpellProgramMachine"), Is.Not.Null,
                "V2有状态Draw解释器尚未实现。");
        }

        private SpellBuildBuffer Build(SpellProgram program, WandDrawSnapshot before,
            SpellExecutionInputs inputs, out SpellBuildSummary summary, bool trace = true, int capacity = SpellProgramLimits.MaxTraceEvents)
        {
            var b = new SpellBuildBuffer(trace, capacity);
            Assert.That(new SpellProgramMachine().TryBuild(program, before, inputs, b, out summary), Is.True, summary.Diagnostic.Message);
            return b;
        }
        private SpellBuildBuffer First(SpellProgram p, out SpellBuildSummary s)
        { return Build(p, WandDrawSnapshot.CreateInitial(p), new SpellExecutionInputs(100), out s); }

        [Test] public void TwoClicks_ContinueDeckInsteadOfRestartingFirstCard()
        {
            var p = Compile(Emit(10), Emit(20)); var initial = WandDrawSnapshot.CreateInitial(p);
            var a = Build(p, initial, new SpellExecutionInputs(100), out var first);
            Assert.That(a.Graph.Outputs[0].Command.Damage, Is.EqualTo(10));
            Assert.That(a.NextState.Deck, Is.EqualTo(new[] { 1 })); Assert.That(a.NextState.Discard, Is.EqualTo(new[] { 0 }));
            Assert.That(initial.Deck, Is.EqualTo(new[] { 0, 1 })); Assert.That(first.NeedsReload, Is.False);
            var b = Build(p, a.NextState, new SpellExecutionInputs(first.ManaAfter), out var second);
            Assert.That(b.Graph.Outputs[0].Command.Damage, Is.EqualTo(20));
            Assert.That(second.NeedsReload, Is.True); Assert.That(b.NextState.Deck, Is.EqualTo(new[] { 0, 1 }));
            Assert.That(b.NextState.CycleIndex, Is.EqualTo(2));
        }

        [Test] public void EndingRootDraw_DoesNotScanLaterMulticast()
        {
            var b = First(Compile(Emit(), Multi(), Emit(), Emit()), out _);
            Assert.That(b.Graph.Outputs.Count, Is.EqualTo(1)); Assert.That(b.NextState.Deck.Count, Is.EqualTo(3));
        }

        [Test] public void NestedMulticast_DrawCallsProduceThreeOutputs()
        { Assert.That(First(Compile(Multi(), Multi(), Emit(), Emit(), Emit()), out _).Graph.Outputs.Count, Is.EqualTo(3)); }

        [Test] public void CurrentHand_IsNotRecycledToSatisfyMulticast()
        { Assert.That(First(Compile(Multi(), Emit()), out _).Graph.Outputs.Count, Is.EqualTo(1)); }

        [Test] public void TrailingModifier_WrapsPreviousDiscardAndTriggersReload()
        {
            var p = Compile(Emit(), Modify()); var a = First(p, out _);
            var b = Build(p, a.NextState, new SpellExecutionInputs(100), out var s);
            Assert.That(b.Graph.Outputs[0].Command.Damage, Is.EqualTo(15)); Assert.That(s.NeedsReload, Is.True);
            Assert.That(b.Trace.Steps, Has.Some.Matches<SpellExecutionTraceStep>(e => e.Kind == SpellTraceKind.WrapRecovered));
            Assert.That(b.NextState.Deck, Is.EqualTo(new[] { 0, 1 }));
        }

        [Test] public void LateDamageModifier_AffectsEarlierOutputInSameScope()
        {
            var b = First(Compile(Multi(), Emit(), Modify(), Emit()), out _);
            Assert.That(b.Graph.Outputs.Count, Is.EqualTo(2));
            foreach (var output in b.Graph.Outputs)
            {
                Assert.That(output.Command.Damage, Is.EqualTo(15));
                Assert.That(output.Parameters.Contributions[0].CardIndex, Is.EqualTo(2));
                Assert.That(output.Parameters.FinalDamage, Is.EqualTo(output.Command.Damage));
            }
        }

        [TestCase(true)] [TestCase(false)]
        public void TriggerScopes_HaveIndependentModifiers(bool modifierBeforeTrigger)
        {
            var t = Emit(trigger: PayloadTriggerMode.OnImpact); var d = Modify(); var f = Emit();
            var b = First(modifierBeforeTrigger ? Compile(d, t, f) : Compile(t, d, f), out _);
            Assert.That(b.Graph.Scopes.Count, Is.EqualTo(2)); Assert.That(b.Graph.RootOutputCount, Is.EqualTo(1));
            Assert.That(b.Graph.Outputs[0].Command.Damage, Is.EqualTo(modifierBeforeTrigger ? 15 : 10));
            Assert.That(b.Graph.Outputs[1].Command.Damage, Is.EqualTo(modifierBeforeTrigger ? 10 : 15));
            Assert.That(b.Graph.Outputs[0].PayloadScopeId, Is.EqualTo(1));
        }

        [Test] public void SevenCardExample_ExplainsTwoRootAndTwoChildOutputs()
        {
            var p = Compile(Multi(), Emit(10,10,PayloadTriggerMode.OnImpact), Multi(), Emit(10,5), Modify(6,5), Emit(10,5), Emit(10,5));
            var b = Build(p, WandDrawSnapshot.CreateInitial(p), new SpellExecutionInputs(80,80), out var s);
            Assert.That(b.Graph.RootOutputCount, Is.EqualTo(2)); Assert.That(b.Graph.Outputs.Count, Is.EqualTo(4));
            Assert.That(b.Graph.Scopes[1].OutputIds.Count, Is.EqualTo(2)); Assert.That(s.ManaAfter, Is.EqualTo(50));
            Assert.That(b.Graph.Outputs[0].Command.Damage, Is.EqualTo(10));
            Assert.That(b.Graph.Outputs[1].Command.Damage, Is.EqualTo(16));
            Assert.That(b.Graph.Outputs[2].Command.Damage, Is.EqualTo(16));
            Assert.That(b.Graph.Outputs[3].Command.Damage, Is.EqualTo(10));
        }

        [Test] public void UnaffordableCard_IsSkippedAndLaterAffordableCardRuns()
        {
            var p = Compile(Emit(10,10,PayloadTriggerMode.OnImpact), Emit(20,5));
            var b = Build(p, WandDrawSnapshot.CreateInitial(p), new SpellExecutionInputs(5), out var s);
            Assert.That(s.ManaAfter, Is.Zero); Assert.That(b.Graph.Outputs.Count, Is.EqualTo(1));
            Assert.That(b.Graph.Outputs[0].SourceCardIndex, Is.EqualTo(1));
        }

        [Test] public void FailedMulticastDraw_DoesNotRescanTheSameUnaffordableDiscard()
        {
            var p=Compile(Multi(10),Emit(10,20),Emit(10,20));
            var b=Build(p,WandDrawSnapshot.CreateInitial(p),new SpellExecutionInputs(5),out var summary);
            int skips=0;foreach(var step in b.Trace.Steps)if(step.Kind==SpellTraceKind.ManaSkipped)skips++;
            // 扫描完整牌堆后没有新收入或新卡，再读相同失败牌不能增加输出。
            Assert.That(skips,Is.EqualTo(2));
            Assert.That(b.Graph.Outputs.Count,Is.Zero);Assert.That(summary.ManaAfter,Is.EqualTo(5));
            Assert.That(b.NextState.Deck,Is.EqualTo(new[]{0,1,2}));
        }

        [TestCase(0,25)] [TestCase(90,95)]
        public void NegativeMana_IsAppliedInOrderAndClamped(float initial, float expected)
        {
            var p = Compile(Modify(0,-30), Emit(10,5));
            Build(p, WandDrawSnapshot.CreateInitial(p), new SpellExecutionInputs(initial), out var s);
            Assert.That(s.ManaAfter, Is.EqualTo(expected));
        }

        [Test] public void TriggerPayloadMana_IsPaidDuringRootBuild()
        {
            var p = Compile(Emit(10,10,PayloadTriggerMode.OnImpact), Emit(20,5));
            Build(p, WandDrawSnapshot.CreateInitial(p), new SpellExecutionInputs(20), out var s);
            Assert.That(s.ManaAfter, Is.EqualTo(5));
        }

        [Test] public void ChildCastDelay_DoesNotChangeRootInterval_ButReloadIsShared()
        {
            var d = Modify(0); d.CastDelayDelta = -100; d.ReloadDelta = -.2f;
            var b = First(Compile(Emit(trigger:PayloadTriggerMode.OnImpact), d, Emit()), out var s);
            Assert.That(s.CastInterval, Is.EqualTo(.15f).Within(.0001));
            Assert.That(s.ReloadInterval, Is.EqualTo(.5f).Within(.0001));
            Assert.That(b.NextState.CycleReloadDelta, Is.Zero);
        }

        [Test] public void ReloadDelta_AccumulatesAcrossRootsAndResetsAtReload()
        {
            var a = Emit(); var z = Emit(); a.ReloadDelta = -.2f; z.ReloadDelta = -.2f;
            var p = Compile(a,z); var first = First(p,out _);
            Assert.That(first.NextState.CycleReloadDelta, Is.EqualTo(-.2f));
            var second = Build(p, first.NextState, new SpellExecutionInputs(100), out var s);
            Assert.That(s.ReloadInterval, Is.EqualTo(.3f).Within(.0001)); Assert.That(second.NextState.CycleReloadDelta, Is.Zero);
        }

        [Test] public void LastUse_IsRemovedFromNextDeck_WhileFrozenOutputRemains()
        {
            var p = Compile(Emit()); var uses = new[] { 1 }; var eligible = new[] { true };
            var inputs = new SpellExecutionInputs(100, remainingUses:uses, eligibility:eligible); uses[0]=0; eligible[0]=false;
            var b = Build(p, WandDrawSnapshot.CreateInitial(p), inputs, out _);
            Assert.That(b.RemainingUsesAfter[0], Is.Zero); Assert.That(b.NextState.Deck.Count, Is.Zero);
            Assert.That(b.Graph.Outputs.Count, Is.EqualTo(1));
        }

        [Test] public void IneligibleCard_DoesNotPayManaOrUses()
        {
            var p = Compile(Emit(10,5));
            var b = Build(p, WandDrawSnapshot.CreateInitial(p), new SpellExecutionInputs(100,remainingUses:new[]{2},eligibility:new[]{false}),out var s);
            Assert.That(s.Outcome, Is.EqualTo(SpellBuildOutcome.AdvancedWithoutEmission));
            Assert.That(s.ManaAfter, Is.EqualTo(100)); Assert.That(b.RemainingUsesAfter[0], Is.EqualTo(2));
        }

        [Test] public void ModifierOnly_IsALegalZeroOutputAdvance()
        { var b=First(Compile(Modify(5,1)),out var s); Assert.That(s.Outcome,Is.EqualTo(SpellBuildOutcome.AdvancedWithoutEmission)); Assert.That(s.ManaAfter,Is.EqualTo(99)); Assert.That(b.NextState,Is.Not.Null); }

        [Test] public void InvalidSnapshot_DoesNotPublishCandidate()
        {
            var p=Compile(Emit()); var state=new WandDrawSnapshot(new[]{0},new[]{0},p.Revision); var buffer=new SpellBuildBuffer();
            Assert.That(new SpellProgramMachine().TryBuild(p,state,new SpellExecutionInputs(100),buffer,out var s),Is.False);
            Assert.That(s.Diagnostic.Error,Is.EqualTo(SpellProgramError.InvalidState)); Assert.That(buffer.Graph,Is.Null); Assert.That(buffer.NextState,Is.Null);
        }

        [Test] public void TechnicalStepLimit_RejectsEntireCandidateWithoutChangingBefore()
        {
            var cards=new SpellDefinition[1024]; cards[0]=Multi(); for(int i=1;i<cards.Length;i++)cards[i]=Emit(10,10);
            // 最后一张恢复Mana并继续Draw，产生有意义的第二次扫描；不能依赖已删除的空重试撞预算。
            cards[cards.Length-1]=Modify(0,-1);
            var p=Compile(cards); var state=WandDrawSnapshot.CreateInitial(p); var b=new SpellBuildBuffer();
            Assert.That(new SpellProgramMachine().TryBuild(p,state,new SpellExecutionInputs(0),b,out var s),Is.False);
            Assert.That(s.Diagnostic.Error,Is.EqualTo(SpellProgramError.TooComplex)); Assert.That(state.Deck.Count,Is.EqualTo(1024));
            Assert.That(state.ExecutionRevision,Is.Zero); Assert.That(b.NextState,Is.Null);
        }

        [TestCase(128,true)] [TestCase(129,false)]
        public void OutputLimit_IsAtomic(int count,bool valid)
        {
            var cards=new SpellDefinition[count+1]; cards[0]=Multi(count); for(int i=1;i<cards.Length;i++)cards[i]=Emit();
            var p=Compile(cards); var b=new SpellBuildBuffer();
            Assert.That(new SpellProgramMachine().TryBuild(p,WandDrawSnapshot.CreateInitial(p),new SpellExecutionInputs(100),b,out var s),Is.EqualTo(valid));
            if(valid)Assert.That(b.Graph.Outputs.Count,Is.EqualTo(count));
            else { Assert.That(s.Diagnostic.Error,Is.EqualTo(SpellProgramError.TooComplex)); Assert.That(b.Graph,Is.Null); }
        }

        [Test] public void NonFiniteComputedResult_IsRejected()
        {
            var d=Modify(0);d.ModSpeedMul=float.MaxValue;var p=Compile(d,Emit());var b=new SpellBuildBuffer();
            Assert.That(new SpellProgramMachine().TryBuild(p,WandDrawSnapshot.CreateInitial(p),new SpellExecutionInputs(100),b,out var s),Is.False);
            Assert.That(s.Diagnostic.Error,Is.EqualTo(SpellProgramError.NumericOverflow)); Assert.That(b.Graph,Is.Null);
        }

        [Test] public void TraceOnOffAndTruncation_DoNotChangeCandidateResults()
        {
            var p=Compile(Multi(),Emit(),Modify(),Emit());var state=WandDrawSnapshot.CreateInitial(p);
            var full=Build(p,state,new SpellExecutionInputs(100),out var a);
            var none=Build(p,state,new SpellExecutionInputs(100),out var z,false);
            var small=Build(p,state,new SpellExecutionInputs(100),out var t,true,1);
            Assert.That(none.Trace.WasCaptured,Is.False);Assert.That(small.Trace.Truncated,Is.True);
            Assert.That(full.NextState.Deck,Is.EqualTo(none.NextState.Deck));Assert.That(full.NextState.RandomState,Is.EqualTo(small.NextState.RandomState));
            Assert.That(a.ManaAfter,Is.EqualTo(z.ManaAfter));Assert.That(a.ManaAfter,Is.EqualTo(t.ManaAfter));
            Assert.That(full.Graph.Outputs[0].Command.Damage,Is.EqualTo(small.Graph.Outputs[0].Command.Damage));
            Assert.That(full.Graph.Outputs[0].RegisteredAtStep,Is.EqualTo(none.Graph.Outputs[0].RegisteredAtStep));
            var replay=new SpellTracePileReplay();Assert.That(replay.Seek(state,full.Trace,int.MaxValue),Is.True);
            Assert.That(replay.Deck,Is.EqualTo(full.NextState.Deck));Assert.That(replay.Discard,Is.EqualTo(full.NextState.Discard));Assert.That(replay.Hand.Count,Is.Zero);
        }

        [Test] public void MotionOverrideAndDamageFormula_MatchLegacyMath()
        {
            var h=Modify(0);h.ModHomingRadius=4;h.ModHomingDuration=1;h.ModHomingTurnRateDegrees=90;h.ModDamageMul=1.5f;
            var o=Modify(0);o.ModOrbitRadius=2;o.ModOrbitAngularSpeedDegrees=180;o.ModDamageMul=1.5f;
            var p=Compile(h,o,Emit()); var b=First(p,out _);
            Assert.That(b.Graph.Outputs[0].Command.MotionMode,Is.EqualTo(ProjectileMotionMode.Orbit));
            Assert.That(b.Graph.Outputs[0].Command.Damage,Is.EqualTo(20));
            Assert.That(b.Graph.Outputs[0].Parameters.Contributions.Count,Is.EqualTo(2));
        }

        [TestCase(16,true)] [TestCase(17,false)]
        public void TriggerDepth_IsBounded(int depth,bool valid)
        {
            var cards=new SpellDefinition[depth+1];for(int i=0;i<depth;i++)cards[i]=Emit(trigger:PayloadTriggerMode.OnImpact);cards[depth]=Emit();
            var p=Compile(cards);var b=new SpellBuildBuffer();
            Assert.That(new SpellProgramMachine().TryBuild(p,WandDrawSnapshot.CreateInitial(p),new SpellExecutionInputs(100),b,out var s),Is.EqualTo(valid));
            if(!valid)Assert.That(s.Diagnostic.Error,Is.EqualTo(SpellProgramError.TooComplex));
        }
    }
}
