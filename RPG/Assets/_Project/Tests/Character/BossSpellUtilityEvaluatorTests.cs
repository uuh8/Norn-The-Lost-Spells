using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Game.Character.Tests
{
    public sealed class BossSpellUtilityEvaluatorTests
    {
        private readonly List<Object> _createdObjects = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            for (int i = _createdObjects.Count - 1; i >= 0; i--)
            {
                Object.DestroyImmediate(_createdObjects[i]);
            }

            _createdObjects.Clear();
        }

        [Test]
        public void SelectProgram_PhaseMaskAndCooldownExcludeCandidates()
        {
            BossSpellProgramDefinition phaseOneOnly = CreateProgram("Phase One", 20f);
            phaseOneOnly.PhaseMask = BossPhaseMask.Phase1;

            BossSpellProgramDefinition available = CreateProgram("Available", 1f);
            var runtime = new BossProgramRuntimeState(2, 2);
            BossDecisionContext context = Context(BossPhase.Phase2, 5f);

            Assert.That(
                BossSpellUtilityEvaluator.SelectProgram(
                    new[] { phaseOneOnly, available },
                    runtime,
                    in context,
                    out _),
                Is.EqualTo(1));

            runtime.MarkUsed(1, 2f);
            Assert.That(
                BossSpellUtilityEvaluator.SelectProgram(
                    new[] { phaseOneOnly, available },
                    runtime,
                    in context,
                    out _),
                Is.EqualTo(-1));
        }

        [TestCase(2f, 0)]
        [TestCase(12f, 1)]
        public void SelectProgram_PreferredDistanceChangesWinner(
            float distance,
            int expectedIndex)
        {
            BossSpellProgramDefinition close = CreateProgram("Close", 1f);
            close.MinPreferredDistance = 0f;
            close.MaxPreferredDistance = 4f;
            close.DistanceFalloff = 2f;
            close.DistanceScoreWeight = 10f;

            BossSpellProgramDefinition far = CreateProgram("Far", 1f);
            far.MinPreferredDistance = 8f;
            far.MaxPreferredDistance = 15f;
            far.DistanceFalloff = 2f;
            far.DistanceScoreWeight = 10f;

            var runtime = new BossProgramRuntimeState(2, 2);
            BossDecisionContext context = Context(BossPhase.Phase1, distance);

            int result = BossSpellUtilityEvaluator.SelectProgram(
                new[] { close, far },
                runtime,
                in context,
                out _);

            Assert.That(result, Is.EqualTo(expectedIndex));
        }

        [Test]
        public void SelectProgram_WetAndMovingContextApplyDataDrivenModifiers()
        {
            BossSpellProgramDefinition neutral = CreateProgram("Neutral", 4f);

            BossSpellProgramDefinition wetFinisher = CreateProgram("Wet Finisher", 1f);
            wetFinisher.PlayerWetScoreModifier = 5f;

            BossSpellProgramDefinition hunter = CreateProgram("Hunter", 1f);
            hunter.MovingTargetScoreModifier = 6f;
            hunter.MovingTargetSpeedThreshold = 4f;

            var runtime = new BossProgramRuntimeState(3, 3);
            BossDecisionContext wetContext = new BossDecisionContext(
                BossPhase.Phase2,
                8f,
                100f,
                0f,
                0.6f,
                1f);
            BossDecisionContext movingContext = new BossDecisionContext(
                BossPhase.Phase2,
                8f,
                0f,
                5f,
                0.6f,
                1f);

            Assert.That(
                BossSpellUtilityEvaluator.SelectProgram(
                    new[] { neutral, wetFinisher, hunter },
                    runtime,
                    in wetContext,
                    out _),
                Is.EqualTo(1));
            Assert.That(
                BossSpellUtilityEvaluator.SelectProgram(
                    new[] { neutral, wetFinisher, hunter },
                    runtime,
                    in movingContext,
                    out _),
                Is.EqualTo(2));
        }

        [Test]
        public void SelectProgram_RecentPenaltyAndStableTieBreakAreDeterministic()
        {
            BossSpellProgramDefinition first = CreateProgram("First", 5f);
            first.RecentUsePenalty = 2f;
            BossSpellProgramDefinition second = CreateProgram("Second", 5f);
            second.RecentUsePenalty = 2f;

            var runtime = new BossProgramRuntimeState(2, 2);
            BossDecisionContext context = Context(BossPhase.Phase1, 5f);

            Assert.That(
                BossSpellUtilityEvaluator.SelectProgram(
                    new[] { first, second },
                    runtime,
                    in context,
                    out _),
                Is.Zero,
                "完全同分时固定选择较小 Index，保证重放可复现。");

            runtime.MarkUsed(0, 0f);
            Assert.That(
                BossSpellUtilityEvaluator.SelectProgram(
                    new[] { first, second },
                    runtime,
                    in context,
                    out _),
                Is.EqualTo(1));
        }

        [Test]
        public void SelectProgram_WhenAllCoolingDownOnlyFallbackCanBypassCooldown()
        {
            BossSpellProgramDefinition fallback = CreateProgram("Fallback", 1f);
            fallback.IsFallback = true;
            BossSpellProgramDefinition normal = CreateProgram("Normal", 10f);

            var runtime = new BossProgramRuntimeState(2, 2);
            runtime.MarkUsed(0, 5f);
            runtime.MarkUsed(1, 5f);
            BossDecisionContext context = Context(BossPhase.Phase1, 5f);

            int result = BossSpellUtilityEvaluator.SelectProgram(
                new[] { fallback, normal },
                runtime,
                in context,
                out _);

            Assert.That(result, Is.Zero);
        }

        private BossSpellProgramDefinition CreateProgram(
            string programName,
            float baseWeight)
        {
            BossSpellProgramDefinition program =
                ScriptableObject.CreateInstance<BossSpellProgramDefinition>();
            program.name = programName;
            program.BaseWeight = baseWeight;
            program.PhaseMask = BossPhaseMask.All;
            program.Phase1Multiplier = 1f;
            program.Phase2Multiplier = 1f;
            program.Phase3Multiplier = 1f;
            _createdObjects.Add(program);
            return program;
        }

        private static BossDecisionContext Context(
            BossPhase phase,
            float distance)
        {
            return new BossDecisionContext(
                phase,
                distance,
                0f,
                0f,
                1f,
                1f);
        }
    }
}
