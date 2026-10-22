using System;
using NUnit.Framework;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// Active/Sleep/Reclaimable 必须由纯整数规则决定，而不是依赖 Scene View 是否看得到 Chunk。
    /// 这些测试锁定兴趣半径边界、负坐标、非空数据保留与空 Chunk Grace Period。
    /// </summary>
    public sealed class ElementChunkActivityPlannerTests
    {
        [TestCase(6, 0, 6, true)]
        [TestCase(-6, 2, -6, true)]
        [TestCase(7, 0, 0, false)]
        [TestCase(0, 3, 0, false)]
        public void IsInsideActiveRegion_UsesInclusiveChunkBox(
            int offsetX,
            int offsetY,
            int offsetZ,
            bool expected)
        {
            var interest = new ElementChunkKey(-4, 1, 9);
            var candidate = new ElementChunkKey(
                interest.X + offsetX,
                interest.Y + offsetY,
                interest.Z + offsetZ);

            bool actual = ElementChunkActivityPlanner.IsInsideActiveRegion(
                candidate,
                interest,
                activeRadiusXZ: 6,
                activeRadiusY: 2);

            Assert.That(actual, Is.EqualTo(expected));
        }

        [Test]
        public void Evaluate_ResidentChunkInsideRegionSleepsWhenEmpty()
        {
            ElementChunkActivityState state = ElementChunkActivityPlanner.Evaluate(
                candidate: new ElementChunkKey(2, 0, -1),
                interest: new ElementChunkKey(2, 0, -1),
                hasAnyElement: false,
                requiresSimulation: false,
                lastRelevantTick: 0,
                currentTick: 100,
                sleepGraceTicks: 20,
                activeRadiusXZ: 6,
                activeRadiusY: 2);

            Assert.That(state, Is.EqualTo(ElementChunkActivityState.Sleeping),
                "Interest Region 决定数据可驻留，不能强迫没有元素的 Chunk 执行四个 Solver Stage。");
        }

        [Test]
        public void Evaluate_NonEmptyChunkOutsideRegionSleepsAndPersists()
        {
            ElementChunkActivityState state = ElementChunkActivityPlanner.Evaluate(
                candidate: new ElementChunkKey(20, 0, 0),
                interest: new ElementChunkKey(0, 0, 0),
                hasAnyElement: true,
                requiresSimulation: false,
                lastRelevantTick: 1,
                currentTick: 1000,
                sleepGraceTicks: 20,
                activeRadiusXZ: 6,
                activeRadiusY: 2);

            Assert.That(state, Is.EqualTo(ElementChunkActivityState.Sleeping));
        }

        [Test]
        public void Evaluate_SettledNonEmptyChunkInsideRegionSleepsButKeepsData()
        {
            ElementChunkActivityState state = ElementChunkActivityPlanner.Evaluate(
                candidate: new ElementChunkKey(0, 0, 0),
                interest: new ElementChunkKey(0, 0, 0),
                hasAnyElement: true,
                requiresSimulation: false,
                lastRelevantTick: 100,
                currentTick: 100,
                sleepGraceTicks: 20,
                activeRadiusXZ: 6,
                activeRadiusY: 2);

            Assert.That(state, Is.EqualTo(ElementChunkActivityState.Sleeping),
                "稳定水仍保存在 Resident Chunk 中，但不应继续参加每个 Solver Tick。");
        }

        [Test]
        public void Evaluate_RecentlyTouchedChunkOutsidePlayerRegionKeepsTemporaryActiveLease()
        {
            ElementChunkActivityState state = ElementChunkActivityPlanner.Evaluate(
                candidate: new ElementChunkKey(30, 0, 0),
                interest: new ElementChunkKey(0, 0, 0),
                hasAnyElement: true,
                requiresSimulation: true,
                lastRelevantTick: 95,
                currentTick: 100,
                sleepGraceTicks: 20,
                activeRadiusXZ: 10,
                activeRadiusY: 3);

            Assert.That(state, Is.EqualTo(ElementChunkActivityState.Active));
        }

        [TestCase(119, ElementChunkActivityState.Sleeping)]
        [TestCase(120, ElementChunkActivityState.Reclaimable)]
        [TestCase(121, ElementChunkActivityState.Reclaimable)]
        public void Evaluate_EmptyOutsideChunkKeepsLeaseThenReclaimsAtGraceBoundary(
            long currentTick,
            ElementChunkActivityState expected)
        {
            ElementChunkActivityState state = ElementChunkActivityPlanner.Evaluate(
                candidate: new ElementChunkKey(20, 0, 0),
                interest: new ElementChunkKey(0, 0, 0),
                hasAnyElement: false,
                requiresSimulation: false,
                lastRelevantTick: 100,
                currentTick: currentTick,
                sleepGraceTicks: 20,
                activeRadiusXZ: 6,
                activeRadiusY: 2);

            Assert.That(state, Is.EqualTo(expected));
        }

        [Test]
        public void Evaluate_RejectsInvalidRadiiOrTickOrder()
        {
            var key = new ElementChunkKey(0, 0, 0);

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                ElementChunkActivityPlanner.Evaluate(
                    key, key, false, false, 0, 0, 0, -1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                ElementChunkActivityPlanner.Evaluate(
                    key, key, false, false, 0, 0, 0, 0, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                ElementChunkActivityPlanner.Evaluate(
                    key, key, false, false, 10, 9, 0, 0, 0));
        }
    }
}
