using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Game.Character.Tests
{
    public sealed class EnemyNavigationMathTests
    {
        [Test]
        public void ResolveCombatEngagement_OutsideCombat_OnlyEntersAtAttackDistance()
        {
            Assert.That(
                BossCombatMovementMath.ResolveCombatEngagement(
                    isEngaged: false,
                    horizontalDistance: 16f,
                    attackEnterDistance: 15f,
                    attackExitDistance: 22f),
                Is.False);
            Assert.That(
                BossCombatMovementMath.ResolveCombatEngagement(
                    isEngaged: false,
                    horizontalDistance: 15f,
                    attackEnterDistance: 15f,
                    attackExitDistance: 22f),
                Is.True);
        }

        [Test]
        public void ResolveCombatEngagement_AlreadyEngaged_StaysUntilChaseBoundaryIsExceeded()
        {
            Assert.That(
                BossCombatMovementMath.ResolveCombatEngagement(
                    isEngaged: true,
                    horizontalDistance: 21.9f,
                    attackEnterDistance: 15f,
                    attackExitDistance: 22f),
                Is.True);
            Assert.That(
                BossCombatMovementMath.ResolveCombatEngagement(
                    isEngaged: true,
                    horizontalDistance: 22.1f,
                    attackEnterDistance: 15f,
                    attackExitDistance: 22f),
                Is.False);
        }

        [TestCase(8f, BossCombatMoveMode.Retreat)]
        [TestCase(12f, BossCombatMoveMode.Orbit)]
        [TestCase(18f, BossCombatMoveMode.Approach)]
        public void ResolveMoveMode_UsesPreferredCombatBand(
            float distance,
            BossCombatMoveMode expected)
        {
            Assert.That(
                BossCombatMovementMath.ResolveMoveMode(
                    distance,
                    preferredMinDistance: 10f,
                    preferredMaxDistance: 16f),
                Is.EqualTo(expected));
        }

        [Test]
        public void ResolveCombatDirection_InsideBand_OrbitsUsingRequestedSide()
        {
            Vector3 clockwise = BossCombatMovementMath.ResolveCombatDirection(
                ownerPosition: Vector3.zero,
                targetPosition: Vector3.forward * 12f,
                preferredMinDistance: 10f,
                preferredMaxDistance: 16f,
                orbitSign: 1);
            Vector3 counterClockwise = BossCombatMovementMath.ResolveCombatDirection(
                ownerPosition: Vector3.zero,
                targetPosition: Vector3.forward * 12f,
                preferredMinDistance: 10f,
                preferredMaxDistance: 16f,
                orbitSign: -1);

            AssertVector(clockwise, 1f, 0f, 0f);
            AssertVector(counterClockwise, -1f, 0f, 0f);
        }

        [Test]
        public void ResolveCombatDirection_OutsideBand_AddsRadialDistanceCorrection()
        {
            Vector3 tooFar = BossCombatMovementMath.ResolveCombatDirection(
                Vector3.zero,
                Vector3.forward * 18f,
                10f,
                16f,
                1);
            Vector3 tooClose = BossCombatMovementMath.ResolveCombatDirection(
                Vector3.zero,
                Vector3.forward * 8f,
                10f,
                16f,
                1);

            Assert.That(tooFar.z, Is.GreaterThan(0f));
            Assert.That(tooClose.z, Is.LessThan(0f));
            Assert.That(tooFar.x, Is.GreaterThan(0f));
            Assert.That(tooClose.x, Is.GreaterThan(0f));
        }

#if UNITY_EDITOR
        [Test]
        public void P8BossAssets_UseNavMeshAndValidCombatDistanceOrdering()
        {
            const string definitionPath =
                "Assets/_Project/ScriptableObjects/P8/Boss/P8_Boss_Velmora_Definition.asset";
            const string prefabPath =
                "Assets/_Project/Art/Prefabs/Boss/Boss.prefab";
            BossDefinition definition =
                AssetDatabase.LoadAssetAtPath<BossDefinition>(definitionPath);
            GameObject prefab =
                AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);

            Assert.That(definition, Is.Not.Null);
            Assert.That(prefab, Is.Not.Null);
            Assert.That(
                definition.AttackExitDistance,
                Is.GreaterThan(definition.AttackEnterDistance));
            Assert.That(
                definition.PreferredCombatMinDistance,
                Is.LessThan(definition.PreferredCombatMaxDistance));
            Assert.That(prefab.GetComponent<NavMeshAgent>(), Is.Not.Null);
            Assert.That(prefab.GetComponent<Animator>().applyRootMotion, Is.False);
        }
#endif

        [Test]
        public void ShouldRefreshPath_WhenIntervalAndMovementAreBelowThreshold_ReturnsFalse()
        {
            bool result = EnemyNavigationMath.ShouldRefreshPath(
                0.1f,
                Vector3.zero,
                new Vector3(0.49f, 8f, 0f),
                0.2f,
                0.5f);

            Assert.That(result, Is.False);
        }

        [Test]
        public void ShouldRefreshPath_WhenIntervalExpires_ReturnsTrue()
        {
            bool result = EnemyNavigationMath.ShouldRefreshPath(
                0.2f,
                Vector3.zero,
                new Vector3(0.1f, 0f, 0f),
                0.2f,
                0.5f);

            Assert.That(result, Is.True);
        }

        [Test]
        public void ShouldRefreshPath_WhenHorizontalTargetMovementReachesThreshold_ReturnsTrue()
        {
            bool result = EnemyNavigationMath.ShouldRefreshPath(
                0.05f,
                Vector3.zero,
                new Vector3(0.3f, 9f, 0.4f),
                0.2f,
                0.5f);

            Assert.That(result, Is.True);
        }

        [Test]
        public void ProjectToNavigationPlane_PreservesHorizontalTargetAndUsesAgentHeight()
        {
            Vector3 result = EnemyNavigationMath.ProjectToNavigationPlane(
                new Vector3(4f, 2.17f, -3f),
                0.08f);

            AssertVector(result, 4f, 0.08f, -3f);
        }

        [Test]
        public void FillTargetSampleQueries_PrioritizesNavigationPlaneBeforeRawTransformHeight()
        {
            var positions = new Vector3[3];
            var radii = new float[3];

            int count = EnemyNavigationMath.FillTargetSampleQueries(
                new Vector3(4f, 2.17f, -3f),
                navigationY: 0.08f,
                queryRadius: 2f,
                expandedRadius: 4f,
                positions,
                radii);

            Assert.That(count, Is.EqualTo(3));
            AssertVector(positions[0], 4f, 0.08f, -3f);
            Assert.That(radii[0], Is.EqualTo(2f));
            AssertVector(positions[1], 4f, 2.17f, -3f);
            Assert.That(radii[1], Is.EqualTo(2f));
            AssertVector(positions[2], 4f, 0.08f, -3f);
            Assert.That(radii[2], Is.EqualTo(4f));
        }

        [Test]
        public void ShouldPreferTargetCandidate_PrefersCompletePathOverCloserPartialPath()
        {
            bool result = EnemyNavigationMath.ShouldPreferTargetCandidate(
                hasCurrentCandidate: true,
                currentPathComplete: false,
                currentTargetErrorSqr: 0.01f,
                candidatePathComplete: true,
                candidateTargetErrorSqr: 1f);

            Assert.That(result, Is.True);
        }

        [Test]
        public void ShouldPreferTargetCandidate_WithSamePathQuality_PrefersCloserEndpoint()
        {
            bool result = EnemyNavigationMath.ShouldPreferTargetCandidate(
                hasCurrentCandidate: true,
                currentPathComplete: true,
                currentTargetErrorSqr: 4f,
                candidatePathComplete: true,
                candidateTargetErrorSqr: 1f);

            Assert.That(result, Is.True);
        }

        [Test]
        public void FillRetreatDirections_WritesBackAndTwoSixtyDegreeFlanks()
        {
            var output = new Vector3[3];

            int count = EnemyNavigationMath.FillRetreatDirections(Vector3.back, output);

            Assert.That(count, Is.EqualTo(3));
            AssertVector(output[0], 0f, 0f, -1f);
            AssertVector(output[1], -0.8660254f, 0f, -0.5f);
            AssertVector(output[2], 0.8660254f, 0f, -0.5f);
        }

        [Test]
        public void FillRetreatDirections_WhenAwayDirectionIsZero_FallsBackToWorldBack()
        {
            var output = new Vector3[3];

            int count = EnemyNavigationMath.FillRetreatDirections(Vector3.zero, output);

            Assert.That(count, Is.EqualTo(3));
            AssertVector(output[0], 0f, 0f, -1f);
        }

        [Test]
        public void HasProgress_WhenHorizontalDistanceReachesMinimum_ReturnsTrue()
        {
            bool result = EnemyNavigationMath.HasProgress(
                new Vector3(2f, 0f, 3f),
                new Vector3(2.03f, 10f, 3.04f),
                0.05f);

            Assert.That(result, Is.True);
        }

        [Test]
        public void SelectFacingDirection_WhenNavigationIsMoving_UsesActualTravelDirection()
        {
            Vector3 result = EnemyNavigationMath.SelectFacingDirection(
                new Vector3(3f, 4f, 0f),
                Vector3.forward);

            AssertVector(result, 1f, 0f, 0f);
        }

        [Test]
        public void SelectFacingDirection_WhenNavigationIsStationary_UsesTargetDirection()
        {
            Vector3 result = EnemyNavigationMath.SelectFacingDirection(
                Vector3.zero,
                new Vector3(0f, 9f, -2f));

            AssertVector(result, 0f, 0f, -1f);
        }

        [Test]
        public void ResolveSteeringVelocity_WhenPathRefreshIsPending_PreservesPreviousSteering()
        {
            Vector3 result = EnemyNavigationMath.ResolveSteeringVelocity(
                Vector3.zero,
                new Vector3(2f, 0f, 1f),
                true);

            AssertVector(result, 2f, 0f, 1f);
        }

        [Test]
        public void ResolveSteeringVelocity_WhenAgentHasFreshSteering_UsesFreshValue()
        {
            Vector3 result = EnemyNavigationMath.ResolveSteeringVelocity(
                new Vector3(-1f, 7f, 3f),
                new Vector3(2f, 0f, 1f),
                true);

            AssertVector(result, -1f, 0f, 3f);
        }

        [Test]
        public void ShouldMoveAlongPath_WhenDistanceIsUnknownButAgentStillSteers_ReturnsTrue()
        {
            bool result = EnemyNavigationMath.ShouldMoveAlongPath(
                float.PositiveInfinity,
                stoppingDistance: 0.05f,
                reachedTolerance: 0.05f,
                steeringVelocity: new Vector3(4.8f, 0f, 1.2f));

            Assert.That(result, Is.True);
        }

        [Test]
        public void ShouldMoveAlongPath_WhenDistanceIsUnknownAndAvoidanceTemporarilyStops_ReturnsTrue()
        {
            bool result = EnemyNavigationMath.ShouldMoveAlongPath(
                float.PositiveInfinity,
                stoppingDistance: 0.05f,
                reachedTolerance: 0.05f,
                steeringVelocity: Vector3.zero);

            Assert.That(result, Is.True);
        }

        [Test]
        public void ResolveStuckRecoveryVelocity_WhenAvoidanceStops_AdvancesTowardCurrentPathCorner()
        {
            Vector3 result = EnemyNavigationMath.ResolveStuckRecoveryVelocity(
                new Vector3(1f, 0f, 1f),
                new Vector3(4f, 8f, 5f),
                5f);

            AssertVector(result, 3f, 0f, 4f);
        }

        [Test]
        public void ResolveAvoidancePriority_ForAdjacentEnemies_BreaksEqualPrioritySymmetry()
        {
            int first = EnemyNavigationMath.ResolveAvoidancePriority(101);
            int second = EnemyNavigationMath.ResolveAvoidancePriority(102);

            Assert.That(first, Is.InRange(20, 79));
            Assert.That(second, Is.InRange(20, 79));
            Assert.That(second, Is.Not.EqualTo(first));
        }

        [Test]
        public void IsAttackPathReady_WhenPathStillDetoursAroundObstacle_ReturnsFalse()
        {
            bool result = EnemyNavigationMath.IsAttackPathReady(
                remainingDistance: 8f,
                directDistance: 4f,
                attackRange: 12f,
                detourTolerance: 0.5f);

            Assert.That(result, Is.False);
        }

        [Test]
        public void IsAttackPathReady_WhenFinalSegmentIsWithinRange_ReturnsTrue()
        {
            bool result = EnemyNavigationMath.IsAttackPathReady(
                remainingDistance: 3.2f,
                directDistance: 3f,
                attackRange: 5f,
                detourTolerance: 0.5f);

            Assert.That(result, Is.True);
        }

        [Test]
        public void IsWithinVerticalCylinder_WhenTargetIsOnLowerGroundInsideBothLimits_ReturnsTrue()
        {
            bool result = EnemyPerceptionMath.IsWithinVerticalCylinder(
                observerPosition: new Vector3(0f, 4f, 0f),
                targetPosition: new Vector3(3f, 0f, 4f),
                horizontalRadius: 5f,
                verticalHalfHeight: 4f);

            Assert.That(result, Is.True);
        }

        [Test]
        public void IsWithinVerticalCylinder_WhenTargetIsManyFloorsBelow_ReturnsFalse()
        {
            bool result = EnemyPerceptionMath.IsWithinVerticalCylinder(
                observerPosition: new Vector3(0f, 12f, 0f),
                targetPosition: Vector3.zero,
                horizontalRadius: 5f,
                verticalHalfHeight: 6f);

            Assert.That(result, Is.False);
        }

        [Test]
        public void ShouldRetreat_WhenPlayerIsCloseOnSameLevel_ReturnsTrue()
        {
            bool result = EnemyPerceptionMath.ShouldRetreat(
                horizontalDistance: 2f,
                verticalDistance: 0.5f,
                retreatDistance: 4f,
                retreatHeight: 1.5f);

            Assert.That(result, Is.True);
        }

        [Test]
        public void ShouldRetreat_WhenPlayerIsBelowHighGround_ReturnsFalse()
        {
            bool result = EnemyPerceptionMath.ShouldRetreat(
                horizontalDistance: 2f,
                verticalDistance: 3f,
                retreatDistance: 4f,
                retreatHeight: 1.5f);

            Assert.That(result, Is.False);
        }

        [TestCase(EnemyNavigationResult.Moving, true)]
        [TestCase(EnemyNavigationResult.Pending, true)]
        [TestCase(EnemyNavigationResult.Unavailable, false)]
        [TestCase(EnemyNavigationResult.Invalid, false)]
        [TestCase(EnemyNavigationResult.Partial, false)]
        [TestCase(EnemyNavigationResult.Reached, false)]
        public void ShouldContinueRetreat_OnlyWhileRetreatCanStillProgress(
            EnemyNavigationResult navigationResult,
            bool expected)
        {
            Assert.That(
                EnemyNavigationMath.ShouldContinueRetreat(navigationResult),
                Is.EqualTo(expected));
        }

        [Test]
        public void ShouldRefreshRetreatSelection_AfterFailedAttemptBeforeInterval_ReturnsFalse()
        {
            bool result = EnemyNavigationMath.ShouldRefreshRetreatSelection(
                hasSelectionAttempt: true,
                elapsed: 0.05f,
                previousThreat: Vector3.zero,
                currentThreat: Vector3.zero,
                interval: 0.2f,
                moveThreshold: 0.5f);

            Assert.That(result, Is.False);
        }

        [Test]
        public void ShouldRefreshRetreatSelection_OnFirstAttemptOrElapsedInterval_ReturnsTrue()
        {
            bool firstAttempt = EnemyNavigationMath.ShouldRefreshRetreatSelection(
                hasSelectionAttempt: false,
                elapsed: 0f,
                previousThreat: Vector3.zero,
                currentThreat: Vector3.zero,
                interval: 0.2f,
                moveThreshold: 0.5f);
            bool elapsedInterval = EnemyNavigationMath.ShouldRefreshRetreatSelection(
                hasSelectionAttempt: true,
                elapsed: 0.2f,
                previousThreat: Vector3.zero,
                currentThreat: Vector3.zero,
                interval: 0.2f,
                moveThreshold: 0.5f);

            Assert.That(firstAttempt, Is.True);
            Assert.That(elapsedInterval, Is.True);
        }

        private static void AssertVector(Vector3 actual, float x, float y, float z)
        {
            Assert.That(actual.x, Is.EqualTo(x).Within(0.0001f));
            Assert.That(actual.y, Is.EqualTo(y).Within(0.0001f));
            Assert.That(actual.z, Is.EqualTo(z).Within(0.0001f));
        }
    }
}
