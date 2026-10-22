using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Combat.Tests
{
    /// <summary>
    /// 使用真实 Unity Physics 验证范围伤害 Adapter：阵营过滤、多 Collider 去重和 DamageRequest 快照。
    /// </summary>
    public class AreaReactionDamageResolverTests
    {
        private sealed class TestDamageable : MonoBehaviour, IDamageable
        {
            public byte TeamId { get; private set; }
            public bool IsAlive => true;
            public int HitCount { get; private set; }
            public DamageRequest LastRequest { get; private set; }

            public void Initialize(byte teamId)
            {
                TeamId = teamId;
            }

            public void ReceiveHit(in DamageRequest request)
            {
                HitCount++;
                LastRequest = request;
            }
        }

        [UnityTest]
        public IEnumerator Resolve_DamagesHostilesOnceAndSkipsSourceTeam()
        {
            // Physics 查询只在 PlayMode 中具有完整场景语义；SyncTransforms 确保刚设置的位置已同步到物理世界。
            yield return new EnterPlayMode();

            TestDamageable center = CreateTarget("center", Vector3.zero, 1, twoColliders: false);
            TestDamageable hostile = CreateTarget("hostile", Vector3.right, 1, twoColliders: true);
            TestDamageable sourceAlly = CreateTarget("source-ally", Vector3.left, 2, twoColliders: false);
            var resolver = new AreaReactionDamageResolver();
            var command = new ReactionDamageCommand(new StatusSource(77, 2), 24f, 2.5f);
            Physics.SyncTransforms();

            resolver.Resolve(in command, Vector3.zero);

            Assert.AreEqual(1, center.HitCount, "毒爆中心载体也应承受一次范围伤害");
            Assert.AreEqual(1, hostile.HitCount, "同一目标的多个 Collider 必须去重");
            Assert.AreEqual(0, sourceAlly.HitCount, "与伤害来源同队的目标必须跳过");
            Assert.AreEqual(77, hostile.LastRequest.AttackerId);
            Assert.AreEqual(2, hostile.LastRequest.AttackerTeam);
            Assert.AreEqual(24f, hostile.LastRequest.BaseAmount, 1e-4f);
            Assert.AreEqual(DamageType.Magical, hostile.LastRequest.Type);
            Assert.IsFalse(hostile.LastRequest.TriggerHitReaction, "范围元素反应不得持续触发受击硬直");

            Object.Destroy(center.gameObject);
            Object.Destroy(hostile.gameObject);
            Object.Destroy(sourceAlly.gameObject);
            yield return null;
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator Resolve_DefaultOrCancelledCommandDoesNotDamage()
        {
            // default command 的 Amount/Radius 均为 0，Resolver 应在执行 Physics 查询前快速返回。
            yield return new EnterPlayMode();

            TestDamageable target = CreateTarget("target", Vector3.zero, 1, twoColliders: false);
            var resolver = new AreaReactionDamageResolver();
            ReactionDamageCommand cancelledCommand = default;
            Physics.SyncTransforms();

            resolver.Resolve(in cancelledCommand, Vector3.zero);

            Assert.AreEqual(0, target.HitCount);
            Object.Destroy(target.gameObject);
            yield return null;
            yield return new ExitPlayMode();
        }

        private static TestDamageable CreateTarget(
            string name,
            Vector3 position,
            byte teamId,
            bool twoColliders)
        {
            var gameObject = new GameObject(name);
            gameObject.transform.position = position;
            TestDamageable damageable = gameObject.AddComponent<TestDamageable>();
            damageable.Initialize(teamId);
            gameObject.AddComponent<SphereCollider>().radius = 0.4f;

            if (twoColliders)
            {
                // 子 Collider 与根 Collider 都会命中，但 GetComponentInParent 找到同一个 IDamageable。
                var child = new GameObject("extra-collider");
                child.transform.SetParent(gameObject.transform, false);
                child.transform.localPosition = Vector3.right * 0.2f;
                child.AddComponent<SphereCollider>().radius = 0.4f;
            }

            return damageable;
        }
    }
}
