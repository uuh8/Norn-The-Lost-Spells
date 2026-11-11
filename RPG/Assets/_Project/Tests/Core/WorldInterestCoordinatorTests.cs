using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Game.Core.Tests
{
    public sealed class WorldInterestCoordinatorTests
    {
        private object _coordinator;
        private Type _type;
        [SetUp] public void SetUp()
        {
            _type = typeof(GameLog).Assembly.GetType("Game.Core.WorldInterestCoordinator");
            Assert.That(_type, Is.Not.Null, "缺少独立于Gameplay模拟的每连接Interest。");
            _coordinator = Activator.CreateInstance(_type);
        }
        private object Call(string name, params object[] args)
        {
            MethodInfo method = _type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null, "缺少Interest实例入口：" + name);
            return method.Invoke(_coordinator, args);
        }

        // 先通过反射写期望合同：入口尚未实现时产生可运行的断言RED，不能用编译错误替代TDD。
        private bool TryBuildReferenceBounds(Vector3 position, Vector3 origin, float chunkWorldSize,
            int radiusXZ, int radiusY, out Bounds bounds)
        {
            MethodInfo method = _type.GetMethod("TryBuildReferenceBounds", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(Vector3), typeof(Vector3), typeof(float), typeof(int), typeof(int),
                    typeof(Bounds).MakeByRefType() }, null);
            Assert.That(method, Is.Not.Null, "缺少按权威位置计算独立网络参考窗口的静态入口。");
            object[] args = { position, origin, chunkWorldSize, radiusXZ, radiusY, default(Bounds) };
            bool success = (bool)method.Invoke(null, args);
            bounds = (Bounds)args[5];
            return success;
        }

        private static void AssertBounds(Bounds actual, Vector3 minimum, Vector3 maximum)
        {
            Assert.That(actual.min, Is.EqualTo(minimum), "窗口最小端点不匹配手工推导的参考块边界。");
            Assert.That(actual.max, Is.EqualTo(maximum), "窗口最大端点不匹配手工推导的参考块边界。");
        }

        private void AssertReferenceBoundsRejected(Vector3 position, Vector3 origin, float chunkWorldSize,
            int radiusXZ, int radiusY)
        {
            Assert.That(TryBuildReferenceBounds(position, origin, chunkWorldSize, radiusXZ, radiusY,
                out Bounds bounds), Is.False);
            Assert.That(bounds, Is.EqualTo(default(Bounds)), "失败时不得把部分候选或上一次范围交给发送者。");
        }

        [Test] public void FourConnectionsKeepSeparateBoundsAndDoNotFillTheGap()
        {
            for (int slot = 0; slot < 4; slot++)
                Assert.That(Call("TrySetConnection", slot, slot + 10, 1UL,
                    new Bounds(Vector3.right * (slot * 100 - 200), Vector3.one * 4)), Is.True);
            Assert.That(Call("Contains", 10, 1UL, Vector3.left * 200), Is.True);
            Assert.That(Call("Contains", 10, 1UL, Vector3.left * 100), Is.False);
            Assert.That(Call("Contains", 11, 1UL, Vector3.left * 150), Is.False);
        }
        [Test] public void StaleEpochCannotMoveOrRemoveReconnectedInterest()
        {
            var bounds = new Bounds(Vector3.zero, Vector3.one * 4);
            Assert.That(Call("TrySetConnection", 0, 10, 2UL, bounds), Is.True);
            Assert.That(Call("TrySetConnection", 0, 10, 1UL, new Bounds(Vector3.one * 100, Vector3.one)), Is.False);
            Assert.That(Call("RemoveConnection", 10, 1UL), Is.False);
            Assert.That(Call("Contains", 10, 2UL, Vector3.zero), Is.True);
            Assert.That(Call("RemoveConnection", 10, 2UL), Is.True);
            Assert.That(Call("Contains", 10, 2UL, Vector3.zero), Is.False);
        }
        [Test] public void CapacityInvalidBoundsAndDuplicateConnectionFailWithoutReplacingExisting()
        {
            var bounds = new Bounds(Vector3.zero, Vector3.one * 4);
            Assert.That(Call("TrySetConnection", 0, 10, 1UL, bounds), Is.True);
            Assert.That(Call("TrySetConnection", 4, 20, 1UL, bounds), Is.False);
            Assert.That(Call("TrySetConnection", 1, 10, 1UL, bounds), Is.False);
            Assert.That(Call("TrySetConnection", 0, 10, 2UL,
                new Bounds(new Vector3(float.NaN, 0, 0), Vector3.one)), Is.False);
            Assert.That(Call("Contains", 10, 1UL, Vector3.zero), Is.True);
        }
        [Test] public void MaximumBoundaryIsExcludedAndMinimumIncluded()
        {
            Call("TrySetConnection", 0, 10, 1UL, new Bounds(Vector3.zero, Vector3.one * 4));
            Assert.That(Call("Contains", 10, 1UL, Vector3.left * 2), Is.True);
            Assert.That(Call("Contains", 10, 1UL, Vector3.right * 2), Is.False);
        }

        [Test] public void DefaultReferenceWindowContainsTheActorChunkAndTenThreeNeighborChunks()
        {
            Assert.That(TryBuildReferenceBounds(Vector3.zero, Vector3.zero, 2, 10, 3,
                out Bounds bounds), Is.True);
            AssertBounds(bounds, new Vector3(-20, -6, -20), new Vector3(22, 8, 22));
            Assert.That(bounds.size, Is.EqualTo(new Vector3(42, 14, 42)));
            Assert.That(Call("TrySetConnection", 0, 10, 1UL, bounds), Is.True);
            Assert.That(Call("Contains", 10, 1UL, new Vector3(-20, -6, -20)), Is.True);
            Assert.That(Call("Contains", 10, 1UL, new Vector3(22, 0, 0)), Is.False);
        }

        [TestCase(-2.0001f, -4, -2)]
        [TestCase(-2f, -2, 0)]
        [TestCase(-1.9999f, -2, 0)]
        [TestCase(-0.0001f, -2, 0)]
        [TestCase(0f, 0, 2)]
        [TestCase(1.9999f, 0, 2)]
        [TestCase(2f, 2, 4)]
        [TestCase(2.0001f, 2, 4)]
        public void ReferenceChunkUsesFloorOnBothSidesOfZero(float x, float minX, float maxX)
        {
            Assert.That(TryBuildReferenceBounds(new Vector3(x, 0, 0), Vector3.zero, 2, 0, 0,
                out Bounds bounds), Is.True);
            AssertBounds(bounds, new Vector3(minX, 0, 0), new Vector3(maxX, 2, 2));
        }

        [Test] public void NegativeCoordinatesFloorIndependentlyOnEveryAxis()
        {
            Assert.That(TryBuildReferenceBounds(new Vector3(-0.01f, -2.01f, -4.01f), Vector3.zero,
                2, 0, 0, out Bounds bounds), Is.True);
            AssertBounds(bounds, new Vector3(-2, -4, -6), new Vector3(0, -2, -4));
        }

        [Test] public void NonzeroWorldOriginDefinesTheReferenceLattice()
        {
            Assert.That(TryBuildReferenceBounds(new Vector3(-0.01f, 4.99f, -5.01f),
                new Vector3(1, 3, -7), 2, 1, 1, out Bounds bounds), Is.True);
            AssertBounds(bounds, new Vector3(-3, 1, -9), new Vector3(3, 7, -3));
        }

        [Test] public void WindowMovesOneReferenceChunkOnlyAfterTheActorCrossesItsBoundary()
        {
            Assert.That(TryBuildReferenceBounds(new Vector3(1.999f, 1.999f, 1.999f), Vector3.zero,
                2, 10, 3, out Bounds before), Is.True);
            Assert.That(TryBuildReferenceBounds(new Vector3(2, 2, 2), Vector3.zero,
                2, 10, 3, out Bounds after), Is.True);
            AssertBounds(before, new Vector3(-20, -6, -20), new Vector3(22, 8, 22));
            AssertBounds(after, new Vector3(-18, -4, -18), new Vector3(24, 10, 24));
        }

        [TestCase(float.NaN, 0, 0)]
        [TestCase(0, float.NaN, 0)]
        [TestCase(0, 0, float.NaN)]
        [TestCase(float.PositiveInfinity, 0, 0)]
        [TestCase(0, float.NegativeInfinity, 0)]
        [TestCase(0, 0, float.PositiveInfinity)]
        public void NonfiniteActorPositionCannotProduceAnInterestWindow(float x, float y, float z)
        {
            AssertReferenceBoundsRejected(new Vector3(x, y, z), Vector3.zero, 2, 10, 3);
        }

        [TestCase(float.NaN, 0, 0)]
        [TestCase(0, float.NaN, 0)]
        [TestCase(0, 0, float.NaN)]
        [TestCase(float.NegativeInfinity, 0, 0)]
        [TestCase(0, float.PositiveInfinity, 0)]
        [TestCase(0, 0, float.NegativeInfinity)]
        public void NonfiniteGeometryOriginCannotProduceAnInterestWindow(float x, float y, float z)
        {
            AssertReferenceBoundsRejected(Vector3.zero, new Vector3(x, y, z), 2, 10, 3);
        }

        [TestCase(0f)]
        [TestCase(-2f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        public void InvalidReferenceChunkSizeIsRejected(float chunkWorldSize)
        {
            AssertReferenceBoundsRejected(Vector3.zero, Vector3.zero, chunkWorldSize, 10, 3);
        }

        [TestCase(-1, 3)]
        [TestCase(10, -1)]
        [TestCase(int.MaxValue, 3)]
        [TestCase(10, int.MaxValue)]
        public void NegativeOrOverflowingReferenceRadiusIsRejected(int radiusXZ, int radiusY)
        {
            AssertReferenceBoundsRejected(Vector3.zero, Vector3.zero, 2, radiusXZ, radiusY);
        }

        [Test] public void WorldExtentOverflowCannotPublishInfiniteBounds()
        {
            AssertReferenceBoundsRejected(Vector3.zero, Vector3.zero, float.MaxValue, 10, 3);
        }

        [Test] public void UnrepresentableFloatEndpointsCannotPublishCollapsedBounds()
        {
            AssertReferenceBoundsRejected(new Vector3(float.MaxValue, 0, 0), Vector3.zero, 2, 10, 3);
            AssertReferenceBoundsRejected(new Vector3(1e20f, 0, 0), new Vector3(1e20f, 0, 0), 2, 10, 3);
        }

        [Test] public void NativeCoarseChunkIsIncludedWhenOnlyPartOfItIntersectsTheWindow()
        {
            Assert.That(TryBuildReferenceBounds(Vector3.zero, Vector3.zero, 2, 10, 3,
                out Bounds interest), Is.True);
            Assert.That(Call("TrySetConnection", 0, 10, 1UL, interest), Is.True);
            var coarseChunk = new Bounds(new Vector3(24, 0, 0), Vector3.one * 8);
            Assert.That(Call("Contains", 10, 1UL, coarseChunk.center), Is.False);
            Assert.That(Call("Intersects", 10, 1UL, coarseChunk), Is.True,
                "原生块中心可以在窗口外；只要仍有正体积交集就必须整块同步。");
            Assert.That(Call("Intersects", 10, 1UL, new Bounds(Vector3.zero, Vector3.one * 128)), Is.True);
        }

        [TestCase(3, 0, 0)]
        [TestCase(-3, 0, 0)]
        [TestCase(0, 3, 0)]
        [TestCase(0, -3, 0)]
        [TestCase(0, 0, 3)]
        [TestCase(0, 0, -3)]
        [TestCase(3, 3, 0)]
        [TestCase(3, 3, 3)]
        public void FaceEdgeOrCornerContactWithoutPositiveVolumeIsExcluded(float x, float y, float z)
        {
            Assert.That(Call("TrySetConnection", 0, 10, 1UL, new Bounds(Vector3.zero, Vector3.one * 4)), Is.True);
            Assert.That(Call("Intersects", 10, 1UL,
                new Bounds(new Vector3(x, y, z), Vector3.one * 2)), Is.False);
        }

        [Test] public void SmallPositiveVolumeAcrossEveryAxisIsIncluded()
        {
            Assert.That(Call("TrySetConnection", 0, 10, 1UL, new Bounds(Vector3.zero, Vector3.one * 4)), Is.True);
            Assert.That(Call("Intersects", 10, 1UL,
                new Bounds(new Vector3(2.999f, 2.999f, 2.999f), Vector3.one * 2)), Is.True);
        }

        [Test] public void InvalidChunkBoundsCannotTurnIntoAnIntersection()
        {
            Assert.That(Call("TrySetConnection", 0, 10, 1UL, new Bounds(Vector3.zero, Vector3.one * 4)), Is.True);
            Assert.That(Call("Intersects", 10, 1UL, new Bounds(Vector3.zero, new Vector3(0, 2, 2))), Is.False);
            Assert.That(Call("Intersects", 10, 1UL, new Bounds(Vector3.zero, new Vector3(2, -2, 2))), Is.False);
            Assert.That(Call("Intersects", 10, 1UL, new Bounds(new Vector3(float.NaN, 0, 0), Vector3.one)), Is.False);
            Assert.That(Call("Intersects", 10, 1UL, new Bounds(Vector3.zero,
                new Vector3(2, float.PositiveInfinity, 2))), Is.False);
            Assert.That(Call("Intersects", 10, 1UL, new Bounds(new Vector3(1e20f, 0, 0), Vector3.one)), Is.False);
        }

        [Test] public void FourConnectionsIntersectTheirOwnWindowsWithoutFillingTheGap()
        {
            for (int slot = 0; slot < 4; slot++)
                Assert.That(Call("TrySetConnection", slot, slot + 10, 2UL,
                    new Bounds(Vector3.right * (slot * 100 - 200), Vector3.one * 4)), Is.True);
            for (int slot = 0; slot < 4; slot++)
            {
                var ownChunk = new Bounds(Vector3.right * (slot * 100 - 200), Vector3.one);
                Assert.That(Call("Intersects", slot + 10, 2UL, ownChunk), Is.True);
                Assert.That(Call("Intersects", slot + 10, 1UL, ownChunk), Is.False);
                Assert.That(Call("Intersects", slot + 10, 0UL, ownChunk), Is.False);
                Assert.That(Call("Intersects", slot + 10, 2UL,
                    new Bounds(Vector3.left * 150, Vector3.one)), Is.False);
                for (int other = 0; other < 4; other++)
                    if (other != slot)
                        Assert.That(Call("Intersects", other + 10, 2UL, ownChunk), Is.False);
            }
            Assert.That(Call("RemoveConnection", 10, 2UL), Is.True);
            Assert.That(Call("Intersects", 10, 2UL, new Bounds(Vector3.left * 200, Vector3.one)), Is.False);
            Assert.That(Call("Intersects", 11, 2UL, new Bounds(Vector3.left * 100, Vector3.one)), Is.True);
            Assert.That(Call("Intersects", 99, 2UL, new Bounds(Vector3.zero, Vector3.one)), Is.False);
        }
    }
}
