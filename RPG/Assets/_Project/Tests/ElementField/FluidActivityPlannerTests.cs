using NUnit.Framework;

namespace Game.ElementField.Tests
{
    public sealed class FluidActivityPlannerTests
    {
        [Test]
        public void ArchiveLocked_RemainsAliveButNeverRequiresSolver()
        {
            uint flags = FluidActivityFlags.Alive | FluidActivityFlags.ArchiveLocked;
            FluidActivityTransition result = FluidActivityPlanner.Evaluate(flags, 7u, true, false, true, 2u);
            Assert.That(result.Flags, Is.EqualTo(flags));
            Assert.That(result.StableTicks, Is.EqualTo(7u));
            Assert.That(FluidActivityFlags.RequiresSolver(flags), Is.False);
            Assert.That(FluidActivityFlags.ContributesToSurface(flags), Is.True);
        }

        [Test]
        public void RestoreLoading_DoesNotContributeToSurfaceOrSolver()
        {
            uint flags = FluidActivityFlags.RestoreLoading;
            Assert.That(FluidActivityFlags.RequiresSolver(flags), Is.False);
            Assert.That(FluidActivityFlags.ContributesToSurface(flags), Is.False);
        }
        [Test]
        public void StableParticleSleepsOnlyAfterRequiredConsecutiveTicks()
        {
            uint flags = FluidActivityFlags.Alive
                | FluidActivityFlags.InterestActive
                | FluidActivityFlags.RequiresSimulation;
            uint stableTicks = 0u;

            for (int tick = 0; tick < 2; tick++)
            {
                FluidActivityTransition result = FluidActivityPlanner.Evaluate(
                    flags, stableTicks, insideInterest: true, isStable: true,
                    wakeRequested: false, requiredStableTicks: 3u);
                flags = result.Flags;
                stableTicks = result.StableTicks;
                Assert.That(FluidActivityFlags.RequiresSolver(flags), Is.True);
            }

            FluidActivityTransition asleep = FluidActivityPlanner.Evaluate(
                flags, stableTicks, true, true, false, 3u);
            Assert.That(FluidActivityFlags.IsSleeping(asleep.Flags), Is.True);
            Assert.That(FluidActivityFlags.RequiresSolver(asleep.Flags), Is.False);
            Assert.That(FluidActivityFlags.ContributesToSurface(asleep.Flags), Is.True);
        }

        [Test]
        public void InstabilityResetsHysteresisAndWakeRestoresSolverState()
        {
            uint awake = FluidActivityFlags.Alive
                | FluidActivityFlags.InterestActive
                | FluidActivityFlags.RequiresSimulation;
            FluidActivityTransition unstable = FluidActivityPlanner.Evaluate(
                awake, 2u, true, false, false, 3u);
            Assert.That(unstable.StableTicks, Is.Zero);

            uint sleeping = FluidActivityFlags.Alive
                | FluidActivityFlags.InterestActive
                | FluidActivityFlags.Sleeping;
            FluidActivityTransition woken = FluidActivityPlanner.Evaluate(
                sleeping, 99u, true, true, true, 3u);
            Assert.That(woken.Flags, Is.EqualTo(awake));
            Assert.That(woken.StableTicks, Is.Zero);
        }

        [Test]
        public void LeavingInterestKeepsResidentAndReentryWakes()
        {
            uint sleeping = FluidActivityFlags.Alive
                | FluidActivityFlags.InterestActive
                | FluidActivityFlags.Sleeping;
            FluidActivityTransition outside = FluidActivityPlanner.Evaluate(
                sleeping, 3u, false, true, false, 3u);
            Assert.That(outside.Flags, Is.EqualTo(FluidActivityFlags.Alive));
            Assert.That(FluidActivityFlags.IsAlive(outside.Flags), Is.True);

            FluidActivityTransition reentered = FluidActivityPlanner.Evaluate(
                outside.Flags, 0u, true, true, false, 3u);
            Assert.That(FluidActivityFlags.RequiresSolver(reentered.Flags), Is.True);
            Assert.That(FluidActivityFlags.ContributesToSurface(reentered.Flags), Is.True);
        }

        [Test]
        public void RemoteSpawnOutsideInterestKeepsSolverLeaseUntilStable()
        {
            uint remoteSpawn = FluidActivityFlags.Alive
                | FluidActivityFlags.RemoteSpawnActive
                | FluidActivityFlags.RequiresSimulation;

            FluidActivityTransition moving = FluidActivityPlanner.Evaluate(
                remoteSpawn, 0u, insideInterest: false, isStable: false,
                wakeRequested: false, requiredStableTicks: 2u);

            Assert.That(FluidActivityFlags.RequiresSolver(moving.Flags), Is.True,
                "远处新生粒子在落稳前必须继续接受 Gravity/PBF/Collision。"
            );
            Assert.That(FluidActivityFlags.ContributesToSurface(moving.Flags), Is.True,
                "Remote Lease 期间仍属于可渲染的 Alive 数据。"
            );

            FluidActivityTransition stableOnce = FluidActivityPlanner.Evaluate(
                moving.Flags, moving.StableTicks, false, true, false, 2u);
            Assert.That(FluidActivityFlags.RequiresSolver(stableOnce.Flags), Is.True);

            FluidActivityTransition settled = FluidActivityPlanner.Evaluate(
                stableOnce.Flags, stableOnce.StableTicks, false, true, false, 2u);
            Assert.That(settled.Flags, Is.EqualTo(FluidActivityFlags.Alive),
                "远处粒子落稳后应释放临时 Lease，而不是永久占用 Solver。"
            );
            Assert.That(FluidActivityFlags.RequiresSolver(settled.Flags), Is.False);
        }

        [Test]
        public void OrdinaryRemoteResidentNeverReentersSolverWithoutLease()
        {
            FluidActivityTransition result = FluidActivityPlanner.Evaluate(
                FluidActivityFlags.Alive,
                0u,
                insideInterest: false,
                isStable: false,
                wakeRequested: false,
                requiredStableTicks: 3u);

            Assert.That(result.Flags, Is.EqualTo(FluidActivityFlags.Alive));
            Assert.That(FluidActivityFlags.RequiresSolver(result.Flags), Is.False,
                "修复不能把全部历史 Resident 粒子重新唤醒。"
            );
        }

        [Test]
        public void FreeSlotBelongsToNoActivityAndCannotBeWoken()
        {
            FluidActivityTransition result = FluidActivityPlanner.Evaluate(
                0u, 7u, true, true, true, 3u);
            Assert.That(result.Flags, Is.Zero);
            Assert.That(result.StableTicks, Is.Zero);
            Assert.That(FluidActivityFlags.ContributesToSurface(result.Flags), Is.False);
            Assert.That(FluidActivityFlags.RequiresSolver(result.Flags), Is.False);
        }

        [Test]
        public void SolverDispatchUsesFullCapacityWhenAnySparseHighSlotIsAwake()
        {
            Assert.That(FluidActivityPlanner.CalculateSolverThreadGroups(0u, 8192, 64), Is.Zero);
            Assert.That(FluidActivityPlanner.CalculateSolverThreadGroups(1u, 8192, 64), Is.EqualTo(128));
            Assert.That(FluidActivityPlanner.CalculateSolverThreadGroups(1u, 65, 64), Is.EqualTo(2),
                "AwakeCount 不能直接决定 group 数，否则只活跃 slot64 时会被漏掉。");
        }
    }
}
