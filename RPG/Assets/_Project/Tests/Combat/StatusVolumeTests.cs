using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Combat.Tests
{
    /// <summary>
    /// 验证环境 Volume 的固定间隔、离开范围、来源归因、Collider 去重与固定缓冲区边界。
    /// </summary>
    public class StatusVolumeTests
    {
        private sealed class TestDamageable : MonoBehaviour, IDamageable
        {
            public byte TeamId => 1;
            public bool IsAlive => true;
            public DamageRequest LastRequest { get; private set; }
            public int HitCount { get; private set; }

            public void ReceiveHit(in DamageRequest request)
            {
                LastRequest = request;
                HitCount++;
            }
        }

        [UnityTest]
        public IEnumerator Tick_AppliesOncePerIntervalAndDeduplicatesMultipleColliders()
        {
            // 0.24 + 0.01 恰好跨过 0.25 秒边界；两 Collider 仍只能累计一次 5 点 Wet。
            yield return new EnterPlayMode();

            StatusVolume volume = CreateVolume(StatusKind.Wet, 0.25f, 5f, 255);
            StatusController target = CreateTarget(Vector3.zero, twoColliders: true);
            Physics.SyncTransforms();

            volume.TickForTests(0.24f);
            Assert.AreEqual(0f, target.GetIntensity(StatusKind.Wet), 1e-4f);

            volume.TickForTests(0.01f);
            Assert.AreEqual(5f, target.GetIntensity(StatusKind.Wet), 1e-4f,
                "同一角色即使有多个 Collider，一个环境 tick 也只能施加一次状态");

            Cleanup(volume, target);
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator Tick_TargetOutsideBoxStopsReceivingStatus()
        {
            // Overlap 查询是离散采样；目标移出并同步 Physics Transform 后，下一 tick 不再命中。
            yield return new EnterPlayMode();

            StatusVolume volume = CreateVolume(StatusKind.Sticky, 0.25f, 5f, 255);
            StatusController target = CreateTarget(Vector3.zero, twoColliders: false);
            Physics.SyncTransforms();
            volume.TickForTests(0.25f);

            target.transform.position = Vector3.right * 10f;
            Physics.SyncTransforms();
            volume.TickForTests(0.25f);

            Assert.AreEqual(5f, target.GetIntensity(StatusKind.Sticky), 1e-4f);

            Cleanup(volume, target);
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator Tick_SustainedContactHoldsDecayAndLeavingResumesItAfterGrace()
        {
            // StatusVolume 与 ElementField Exposure 共用同一持续来源契约：
            // 低频 Physics Query 之间不衰减，离开 Box 后则只保留一个短 Grace。
            yield return new EnterPlayMode();

            StatusVolume volume = CreateVolume(StatusKind.Wet, 0.25f, 5f, 255);
            StatusController target = CreateTarget(Vector3.zero, twoColliders: false);
            StatusDefinition wet = ScriptableObject.CreateInstance<StatusDefinition>();
            wet.Kind = StatusKind.Wet;
            wet.NaturalDecayPerSecond = 8f;
            target.SetDefinitionsForTests(wet);
            Physics.SyncTransforms();

            volume.TickForTests(0.25f);
            target.TickForTests(0.25f);
            volume.TickForTests(0.25f);
            target.TickForTests(0.25f);
            Assert.AreEqual(10f, target.GetIntensity(StatusKind.Wet), 1e-4f,
                "持续位于 Volume 中时，两次补充之间不应发生 NaturalDecay。");

            target.transform.position = Vector3.right * 10f;
            Physics.SyncTransforms();
            volume.TickForTests(0.25f);
            target.TickForTests(0.2f);

            // 最近一次 Apply 的 Hold=0.35 秒，前一 Tick 已消耗 0.25 秒；
            // 离场后 0.20 秒只有后 0.10 秒参与 8/s 衰减，因此 10 - 0.8 = 9.2。
            Assert.AreEqual(9.2f, target.GetIntensity(StatusKind.Wet), 1e-4f);

            Object.Destroy(wet);
            Cleanup(volume, target);
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator Tick_UsesVolumeInstanceIdAndConfiguredEnvironmentTeamAsSource()
        {
            // 状态来源会继续进入 DoT 的 DamageRequest，因此环境也必须提供稳定 Id 与 Team。
            yield return new EnterPlayMode();

            StatusVolume volume = CreateVolume(StatusKind.Poisoned, 0.25f, 100f, 255);
            var targetObject = new GameObject("status-target-with-damageable");
            targetObject.AddComponent<SphereCollider>().radius = 0.1f;
            TestDamageable damageable = targetObject.AddComponent<TestDamageable>();
            // StatusController 在 Awake 缓存 IDamageable，所以测试也必须遵循真实 Prefab 的组件建立顺序。
            StatusController target = targetObject.AddComponent<StatusController>();
            StatusDefinition poison = ScriptableObject.CreateInstance<StatusDefinition>();
            poison.Kind = StatusKind.Poisoned;
            poison.DealsDamage = true;
            poison.DamageInterval = 0.05f;
            poison.BaseDamagePerTick = 1f;
            target.SetDefinitionsForTests(poison);
            Physics.SyncTransforms();

            volume.TickForTests(0.25f);
            target.TickForTests(0.05f);

            Assert.AreEqual(1, damageable.HitCount);
            Assert.AreEqual(volume.gameObject.GetInstanceID(), damageable.LastRequest.AttackerId);
            Assert.AreEqual(255, damageable.LastRequest.AttackerTeam);

            Object.Destroy(poison);
            Cleanup(volume, target);
            yield return new ExitPlayMode();
        }

        [Test]
        public void Tick_WhenColliderBufferIsFull_DoesNotThrow()
        {
            // 固定数组容量是 32；超过容量会截断而不是扩容或抛异常，这是明确的性能上限。
            StatusVolume volume = CreateVolume(StatusKind.Burning, 0.25f, 5f, 255);
            var colliders = new GameObject[40];
            for (int i = 0; i < colliders.Length; i++)
            {
                colliders[i] = new GameObject("buffer-collider-" + i);
                colliders[i].transform.position = Vector3.zero;
                colliders[i].AddComponent<SphereCollider>().radius = 0.05f;
            }
            Physics.SyncTransforms();

            Assert.DoesNotThrow(() => volume.TickForTests(0.25f));

            for (int i = 0; i < colliders.Length; i++)
                Object.DestroyImmediate(colliders[i]);
            Object.DestroyImmediate(volume.gameObject);
        }

        private static StatusVolume CreateVolume(StatusKind kind, float tickInterval, float amount, byte sourceTeam)
        {
            var gameObject = new GameObject("status-volume");
            StatusVolume volume = gameObject.AddComponent<StatusVolume>();
            volume.ConfigureForTests(kind, tickInterval, amount, sourceTeam);
            return volume;
        }

        private static StatusController CreateTarget(Vector3 position, bool twoColliders)
        {
            var gameObject = new GameObject("status-target");
            gameObject.transform.position = position;
            gameObject.AddComponent<SphereCollider>().radius = 0.1f;
            StatusController controller = gameObject.AddComponent<StatusController>();

            if (twoColliders)
            {
                var child = new GameObject("extra-collider");
                child.transform.SetParent(gameObject.transform, false);
                child.AddComponent<SphereCollider>().radius = 0.1f;
            }

            return controller;
        }

        private static void Cleanup(StatusVolume volume, StatusController target)
        {
            Object.Destroy(volume.gameObject);
            Object.Destroy(target.gameObject);
        }
    }
}
