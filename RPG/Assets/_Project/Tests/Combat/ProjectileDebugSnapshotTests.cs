using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Combat.Tests
{
    public sealed class ProjectileDebugTestProjectile : ProjectileBase
    {
        public void PrepareForEditModeTest(Vector3 velocity, bool useGravity)
        {
            _rb = GetComponent<Rigidbody>();
            _collider = GetComponent<Collider>();
            _rb.useGravity = useGravity;
            _rb.linearVelocity = velocity;
        }

        public Vector3 ResolveHomingTargetPointForTest(Collider target)
        {
            return ResolveHomingTargetPoint(target);
        }
    }

    public sealed class ProjectileDebugSnapshotTests
    {
        private readonly List<GameObject> _createdObjects = new List<GameObject>();

        [UnityTest]
        public IEnumerator Init_RegistersActiveProjectile_AndDestroyUnregisters()
        {
            yield return new EnterPlayMode();

            int before = ProjectileBase.ActiveCount;
            GameObject go = CreateProjectile(out ProjectileDebugTestProjectile projectile);

            projectile.Init(
                1, 10, 5f, DamageType.Magical,
                Vector3.forward * 20f, null, false);

            int activeAfterInit = ProjectileBase.ActiveCount;
            DestroyTrackedObjects();
            yield return null;
            int activeAfterDestroy = ProjectileBase.ActiveCount;

            yield return new ExitPlayMode();

            Assert.AreEqual(before + 1, activeAfterInit);
            Assert.AreEqual(before, activeAfterDestroy);
        }

        [UnityTest]
        public IEnumerator DisableThenEnable_ReregistersProjectile_AndRestoresSameTeamIgnoreCollision()
        {
            yield return new EnterPlayMode();

            int before = ProjectileBase.ActiveCount;
            GameObject firstObject = CreateProjectile(out ProjectileDebugTestProjectile first);
            first.Init(1, 10, 5f, DamageType.Magical, Vector3.forward, null, false);

            firstObject.SetActive(false);
            int activeAfterDisable = ProjectileBase.ActiveCount;

            CreateProjectile(out ProjectileDebugTestProjectile second);
            second.Init(1, 11, 5f, DamageType.Magical, Vector3.forward, null, false);

            firstObject.SetActive(true);
            int activeAfterEnable = ProjectileBase.ActiveCount;
            bool ignoresCollision = Physics.GetIgnoreCollision(
                first.GetComponent<Collider>(), second.GetComponent<Collider>());

            DestroyTrackedObjects();
            yield return null;
            int activeAfterDestroy = ProjectileBase.ActiveCount;

            yield return new ExitPlayMode();

            Assert.AreEqual(before, activeAfterDisable);
            Assert.AreEqual(before + 2, activeAfterEnable);
            Assert.IsTrue(ignoresCollision);
            Assert.AreEqual(before, activeAfterDestroy);
        }

        [Test]
        public void Snapshot_Configuration_ReportsPositionVelocityGravityHomingAndOrbit()
        {
            GameObject go = CreateProjectile(out ProjectileDebugTestProjectile projectile);
            go.transform.position = new Vector3(3f, 4f, 5f);
            projectile.ConfigureHoming(6f, 0.5f, 120f);
            projectile.ConfigureOrbit(2f, 90f, 0f, 0f);
            projectile.PrepareForEditModeTest(Vector3.forward * 20f, true);

            ProjectileDebugSnapshot snapshot = projectile.GetDebugSnapshot();

            Assert.AreEqual(new Vector3(3f, 4f, 5f), snapshot.Position);
            Assert.AreEqual(new Vector3(0f, 0f, 20f), snapshot.Velocity);
            Assert.IsTrue(snapshot.UsesGravity);
            Assert.IsTrue(snapshot.HomingEnabled);
            Assert.AreEqual(6f, snapshot.HomingRadius, 1e-4f);
            Assert.IsTrue(snapshot.OrbitEnabled);
            Assert.AreEqual(2f, snapshot.OrbitRadius, 1e-4f);
            Object.DestroyImmediate(go);
        }

        [Test]
        public void ResolveHomingTargetPoint_UsesColliderBoundsCenterInsteadOfRoot()
        {
            GameObject projectileObject =
                CreateProjectile(out ProjectileDebugTestProjectile projectile);
            GameObject targetRoot = new GameObject("Homing Target Root");
            targetRoot.transform.position = new Vector3(5f, 0f, -2f);
            GameObject hitVolume = new GameObject("Homing Hit Volume");
            hitVolume.transform.SetParent(targetRoot.transform, false);
            hitVolume.transform.localPosition = new Vector3(0f, 1.5f, 0f);
            BoxCollider targetCollider = hitVolume.AddComponent<BoxCollider>();
            targetCollider.size = new Vector3(1f, 3f, 1f);
            _createdObjects.Add(targetRoot);
            Physics.SyncTransforms();

            Vector3 targetPoint =
                projectile.ResolveHomingTargetPointForTest(targetCollider);

            Assert.That(
                targetPoint,
                Is.EqualTo(targetCollider.bounds.center));
            Assert.That(
                targetPoint,
                Is.Not.EqualTo(targetRoot.transform.position));
            Object.DestroyImmediate(projectileObject);
            Object.DestroyImmediate(targetRoot);
            _createdObjects.Clear();
        }

        [UnityTest]
        public IEnumerator Homing_AcquiredTarget_ReportsMovingColliderCenter()
        {
            yield return new EnterPlayMode();

            CreateProjectile(out ProjectileDebugTestProjectile projectile);
            GameObject targetRoot = CreateHomingTarget(
                new Vector3(5f, 0f, 0f),
                out BoxCollider targetCollider);
            projectile.ConfigureHoming(12f, 2f, 180f);
            projectile.Init(
                1,
                10,
                5f,
                DamageType.Magical,
                Vector3.forward * 10f,
                null,
                false);
            Physics.SyncTransforms();

            // EditMode Test Runner 的 Run All 不保证 WaitForFixedUpdate 会真正调度
            // MonoBehaviour.FixedUpdate。显式发送生产 FixedUpdate，既保留真实 Homing 路径，
            // 又让测试结果只由当前夹具决定，不依赖 Editor 窗口的 PlayerLoop 时序。
            projectile.SendMessage(
                "FixedUpdate",
                SendMessageOptions.RequireReceiver);
            ProjectileDebugSnapshot acquiredSnapshot =
                projectile.GetDebugSnapshot();

            targetRoot.transform.position += Vector3.up;
            Physics.SyncTransforms();
            projectile.SendMessage(
                "FixedUpdate",
                SendMessageOptions.RequireReceiver);
            ProjectileDebugSnapshot movedSnapshot =
                projectile.GetDebugSnapshot();
            Vector3 expectedMovedCenter = targetCollider.bounds.center;

            DestroyTrackedObjects();
            yield return null;
            yield return new ExitPlayMode();

            Assert.That(acquiredSnapshot.HasHomingTarget, Is.True);
            Assert.That(
                acquiredSnapshot.HomingTargetPosition.y,
                Is.GreaterThan(0f));
            Assert.That(
                movedSnapshot.HomingTargetPosition,
                Is.EqualTo(expectedMovedCenter));
        }

        [UnityTest]
        public IEnumerator ShieldReflection_Reinit_ClearsRecentReflection()
        {
            yield return new EnterPlayMode();

            CreateProjectile(out ProjectileDebugTestProjectile projectile);
            projectile.Init(1, 10, 5f, DamageType.Magical, Vector3.forward * 20f, null, false);

            bool reflected = projectile.ReflectByShield(Vector3.back, 2, 99);
            ProjectileDebugSnapshot reflectedSnapshot = projectile.GetDebugSnapshot();
            projectile.Init(2, 99, 5f, DamageType.Magical, Vector3.back * 20f, null, false);
            ProjectileDebugSnapshot reinitializedSnapshot = projectile.GetDebugSnapshot();

            DestroyTrackedObjects();
            yield return null;
            yield return new ExitPlayMode();

            Assert.IsTrue(reflected);
            Assert.IsTrue(reflectedSnapshot.HasRecentReflection);
            Assert.AreEqual(Vector3.back, reflectedSnapshot.LastCollisionNormal);
            Assert.AreEqual(Vector3.back, reflectedSnapshot.LastReflectedDirection);
            Assert.IsFalse(reinitializedSnapshot.HasRecentReflection);
        }

        [UnityTest]
        public IEnumerator Bounce_RecordsReflectionPointNormalDirectionAndTime()
        {
            yield return new EnterPlayMode();

            CreateProjectile(out ProjectileDebugTestProjectile projectile);
            CreateBounceWall();
            projectile.ConfigureBounce(1);
            projectile.Init(1, 10, 5f, DamageType.Magical, Vector3.forward * 20f, null, false);

            ProjectileDebugSnapshot snapshot = default;
            SimulationMode originalSimulationMode = Physics.simulationMode;
            try
            {
                // Run All 时，EditMode 测试协程的 WaitForFixedUpdate 不一定会让 Physics 真正走一步。
                // 切换到 Script 模式并显式 Simulate，测试结果便只由输入场景决定，而不依赖 Editor PlayerLoop。
                Physics.simulationMode = SimulationMode.Script;
                Physics.SyncTransforms();

                for (int i = 0; i < 20 && !snapshot.HasRecentReflection; i++)
                {
                    Physics.Simulate(Time.fixedDeltaTime);
                    snapshot = projectile.GetDebugSnapshot();
                }
            }
            finally
            {
                // Physics.simulationMode 是全局状态；不恢复会污染后续测试和当前打开的场景。
                Physics.simulationMode = originalSimulationMode;
            }

            // 失败时保留销毁前的 Physics 事实。测试原先只报告 Expected True，无法判断是
            // FixedUpdate 未推进、Rigidbody 未移动，还是高速物体穿过了碰撞器。
            Rigidbody projectileBody = projectile.GetComponent<Rigidbody>();
            Vector3 finalPosition = projectile.transform.position;
            Vector3 finalVelocity = projectileBody.linearVelocity;
            bool wasSleeping = projectileBody.IsSleeping();
            bool wasKinematic = projectileBody.isKinematic;
            bool detectedCollisions = projectileBody.detectCollisions;
            SimulationMode simulationMode = Physics.simulationMode;

            DestroyTrackedObjects();
            yield return null;
            yield return new ExitPlayMode();

            Assert.IsTrue(
                snapshot.HasRecentReflection,
                "投射物在 20 个 FixedUpdate 内没有记录反射。销毁前诊断："
                + $"position={finalPosition}, velocity={finalVelocity}, sleeping={wasSleeping}, "
                + $"isKinematic={wasKinematic}, detectCollisions={detectedCollisions}, "
                + $"simulationMode={simulationMode}, fixedDeltaTime={Time.fixedDeltaTime}");
            Assert.Greater(snapshot.LastCollisionPoint.z, 0f);
            Assert.Greater(Vector3.Dot(snapshot.LastCollisionNormal, Vector3.back), 0.99f);
            Assert.Greater(Vector3.Dot(snapshot.LastReflectedDirection, Vector3.back), 0.99f);
            Assert.GreaterOrEqual(snapshot.LastCollisionTime, 0f);
        }

        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            DestroyTrackedObjects();

            if (Application.isPlaying)
            {
                yield return null;
                yield return new ExitPlayMode();
            }
        }

        private GameObject CreateProjectile(out ProjectileDebugTestProjectile projectile)
        {
            var go = new GameObject("DebugSnapshotProjectile");
            go.AddComponent<Rigidbody>();
            go.AddComponent<SphereCollider>();
            projectile = go.AddComponent<ProjectileDebugTestProjectile>();
            _createdObjects.Add(go);
            return go;
        }

        private void CreateBounceWall()
        {
            var go = new GameObject("DebugSnapshotBounceWall");
            go.transform.position = new Vector3(0f, 0f, 2f);
            go.transform.localScale = new Vector3(10f, 10f, 0.1f);
            go.AddComponent<BoxCollider>();
            _createdObjects.Add(go);
        }

        private GameObject CreateHomingTarget(
            Vector3 position,
            out BoxCollider targetCollider)
        {
            var root = new GameObject("Homing Target Root");
            root.transform.position = position;
            root.AddComponent<HealthComponent>();

            var hitVolume = new GameObject("Homing Hit Volume");
            hitVolume.transform.SetParent(root.transform, false);
            hitVolume.transform.localPosition = new Vector3(0f, 1.5f, 0f);
            targetCollider = hitVolume.AddComponent<BoxCollider>();
            targetCollider.size = new Vector3(1f, 3f, 1f);

            _createdObjects.Add(root);
            return root;
        }

        private void DestroyTrackedObjects()
        {
            for (int i = 0; i < _createdObjects.Count; i++)
            {
                GameObject go = _createdObjects[i];
                if (go == null)
                    continue;

                if (Application.isPlaying)
                    Object.Destroy(go);
                else
                    Object.DestroyImmediate(go);
            }

            _createdObjects.Clear();
        }
    }
}
