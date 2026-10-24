using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Game.Character.Tests
{
    public sealed class OnlineCastIntentRulesTests
    {
        private static bool Validate(Vector3 authority, Vector3 claimed, Vector3 aim)
        {
            var type = typeof(WizardController).Assembly.GetType("Game.Character.OnlineCastIntentRules");
            Assert.That(type, Is.Not.Null, "缺少 Server 施法瞄准校验，Client 坐标不能直接用作生成位置。");
            return (bool)type.GetMethod("ValidateAim", BindingFlags.Public | BindingFlags.Static).Invoke(null,
                new object[] { authority, claimed, aim, 3f, 120f });
        }
        [Test] public void NearbyClickOriginAndFiniteAim_AreAccepted() =>
            Assert.That(Validate(Vector3.zero, Vector3.right, Vector3.forward * 30), Is.True);
        [Test] public void ClaimedDistantOrigin_IsRejected() =>
            Assert.That(Validate(Vector3.zero, Vector3.right * 4, Vector3.forward * 30), Is.False);
        [Test] public void NonFiniteAim_IsRejected() =>
            Assert.That(Validate(Vector3.zero, Vector3.zero, new Vector3(float.NaN, 0, 10)), Is.False);
        [Test] public void NonFiniteClaimedOrigin_IsRejected() =>
            Assert.That(Validate(Vector3.zero, new Vector3(0, float.PositiveInfinity, 0), Vector3.forward), Is.False);
        [Test] public void AimBeyondGameplayRange_IsRejected() =>
            Assert.That(Validate(Vector3.zero, Vector3.zero, Vector3.forward * 121), Is.False);
        [Test] public void ZeroDirection_IsRejected() =>
            Assert.That(Validate(Vector3.zero, Vector3.zero, Vector3.zero), Is.False);
    }
}
