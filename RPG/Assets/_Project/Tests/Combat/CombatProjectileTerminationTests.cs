using System.Reflection;
using System.Text.RegularExpressions;
using Game.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Game.Combat.Tests
{
    /// <summary>
    /// 验证终止事实发布当刻的真实 Physics 状态。EditMode 中正常 Destroy 会被 Unity 拒绝，
    /// 因而可以在对象尚存时精确断言；明确 Expect 该引擎日志，不把它误认成本次 Gameplay 错误。
    /// </summary>
    public sealed class CombatProjectileTerminationTests
    {
        [TestCase(ProjectileTerminationReason.LifetimeExpired)]
        [TestCase(ProjectileTerminationReason.TimedTrigger)]
        [TestCase(ProjectileTerminationReason.Impact)]
        public void Authority_NormalTerminationDisablesColliderAndStopsBodyBeforePublishingFact(ProjectileTerminationReason reason)
        {
            GameObject root = new GameObject("Authority Terminal Physics");
            try
            {
                Collider collider = root.AddComponent<SphereCollider>();
                GameObject shieldTrigger = new GameObject("Child Shield Trigger");
                shieldTrigger.transform.SetParent(root.transform, false);
                Collider childCollider = shieldTrigger.AddComponent<SphereCollider>(); childCollider.isTrigger = true;
                CombatExecutionTestProjectile projectile = root.AddComponent<CombatExecutionTestProjectile>();
                projectile.Init(1, 10, 10f, DamageType.True, Vector3.forward * 10f, null, false);
                projectile.ConfigureExecutionMode(CombatExecutionMode.Authority);
                Rigidbody body = root.GetComponent<Rigidbody>();
                body.angularVelocity = Vector3.one;
                // 模型 forward 故意不同于速度；清零后若重新 ResolveDirection，会错误地把 Payload 朝向转到右侧。
                root.transform.rotation = Quaternion.LookRotation(Vector3.right);
                Vector3 factDirection = Vector3.zero, payloadDirection = Vector3.zero;
                bool factObserved = false, colliderStoppedAtFact = false, bodyStoppedAtFact = false;
                bool timedPayloadObserved = false, stoppedBeforeTimedPayload = false;
                projectile.Terminated += (actual, point, direction) =>
                {
                    if (actual != reason) return;
                    factObserved = true;
                    factDirection = direction;
                    colliderStoppedAtFact = !collider.enabled && !childCollider.enabled;
                    bodyStoppedAtFact = body.linearVelocity.sqrMagnitude < 0.000001f
                        && body.angularVelocity.sqrMagnitude < 0.000001f;
                };
                projectile.TimedTriggerElapsed += (point, direction) =>
                {
                    timedPayloadObserved = true;
                    payloadDirection = direction;
                    stoppedBeforeTimedPayload = !collider.enabled && !childCollider.enabled && body.linearVelocity.sqrMagnitude < 0.000001f;
                };

                // 本 Fixture 保留正常运行时 Destroy 路径；不要改用 DestroyImmediate 绕过帧内物理状态验证。
                LogAssert.Expect(LogType.Error, new Regex("Destroy may not be called from edit mode"));
                if (reason == ProjectileTerminationReason.LifetimeExpired)
                {
                    projectile.ConfigureLifetime(0.01f);
                    projectile.StepAuthority(0.02f, 1);
                }
                else if (reason == ProjectileTerminationReason.TimedTrigger)
                {
                    projectile.ArmTimedTrigger(0.01f);
                    projectile.StepAuthority(0.02f, 1);
                }
                else
                {
                    MethodInfo resolve = typeof(ProjectileBase).GetMethod("ResolveImpact", BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.That(resolve, Is.Not.Null);
                    resolve.Invoke(projectile, new object[] { null, null, Vector3.zero, Vector3.forward, Vector3.up, false });
                }

                Assert.That(factObserved, Is.True);
                Assert.That(colliderStoppedAtFact, Is.True, "终止对象不能再被本 Rendering frame 后续 Simulate 检出。");
                Assert.That(bodyStoppedAtFact, Is.True, "终止事实出现前必须清掉线速度与角速度。");
                Assert.That(Vector3.Dot(factDirection, Vector3.forward), Is.GreaterThan(0.999f));
                if (reason == ProjectileTerminationReason.TimedTrigger)
                {
                    Assert.That(timedPayloadObserved, Is.True);
                    Assert.That(stoppedBeforeTimedPayload, Is.True, "下一层Payload创建前，父弹体应已退出Physics。");
                    Assert.That(Vector3.Dot(payloadDirection, Vector3.forward), Is.GreaterThan(0.999f));
                }
            }
            finally { if (root != null) Object.DestroyImmediate(root); }
        }
    }
}
