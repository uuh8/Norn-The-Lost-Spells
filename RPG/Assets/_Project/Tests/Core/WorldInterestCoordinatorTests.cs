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
        private object Call(string name, params object[] args) => _type.GetMethod(name).Invoke(_coordinator, args);

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
    }
}
