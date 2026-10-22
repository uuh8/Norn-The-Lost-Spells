using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// 真实 GPU Buffer/Compute fixture。GetData 只允许出现在 Test-only 数值验证中，绝不能复制到 Runtime 热路径。
    /// 每个案例连续运行 120 tick，覆盖 Projection 后 Velocity Response 不被 UpdateVelocities 覆盖的完整顺序。
    /// </summary>
    public sealed class GpuPbfCollisionTests
    {
        private const string LifecycleShaderPath =
            "Assets/_Project/Art/Elemental/Compute/PbfParticleLifecycle.compute";
        private const string SolverShaderPath =
            "Assets/_Project/Art/Elemental/Compute/PbfSolver.compute";
        private const string CollisionShaderPath =
            "Assets/_Project/Art/Elemental/Compute/PbfCollision.compute";
        private const float ParticleRadius = 0.1f;

        [Test]
        public void FinalCollisionResponseRemovesInwardVelocityIntroducedByLaterFilter()
        {
            IgnoreWithoutComputeSupport();
            ComputeShader collision = LoadRequiredShader(CollisionShaderPath);
            var resources = new FluidGpuResourceSet(1, 2, 1, 1);
            try
            {
                resources.Velocities.SetData(new[] { new Vector4(2f, -3f, 0f, 0f) });
                resources.Metadata.SetData(new[] { new FluidGpuUInt2(1u, FluidGpuLayout.ActiveFlag) });
                resources.CollisionContacts.SetData(new[]
                {
                    new FluidCollisionContactManifold(
                        new Vector4(0f, 1f, 0f, 1f),
                        Vector4.zero,
                        Vector4.zero,
                        Vector4.zero),
                });
                resources.Counters.SetData(new uint[] { 0u, 1u, 0u, 0u });

                int kernel = collision.FindKernel("ApplyCollisionVelocities");
                collision.SetBuffer(kernel, "_Velocities", resources.Velocities);
                collision.SetBuffer(kernel, "_ParticleMetadata", resources.Metadata);
                collision.SetBuffer(kernel, "_CollisionContactsReadOnly", resources.CollisionContacts);
                collision.SetBuffer(kernel, "_Counters", resources.Counters);
                collision.SetInt("_ParticleCapacity", 1);
                collision.SetFloat("_CollisionFriction", 0.25f);
                collision.SetFloat("_CollisionRestitution", 0f);
                collision.Dispatch(kernel, 1, 1, 1);

                // 把输入视为 XSPH/Vorticity 最后写回的速度；Response 必须是最终法向 Gate。
                var velocities = new Vector4[1];
                var counters = new uint[FluidGpuLayout.CounterCount];
                resources.Velocities.GetData(velocities);
                resources.Counters.GetData(counters);
                Assert.That(velocities[0].x, Is.EqualTo(1.5f).Within(1e-4f));
                Assert.That(velocities[0].y, Is.EqualTo(0f).Within(1e-4f));
                Assert.That(velocities[0].z, Is.EqualTo(0f).Within(1e-4f));
                Assert.That(counters[FluidGpuLayout.NumericalErrorCounterIndex], Is.Zero);
            }
            finally
            {
                resources.Dispose();
            }
        }

        [Test]
        public void MultiContactManifoldConstrainsBothWallNormalsWithoutSummingThem()
        {
            IgnoreWithoutComputeSupport();
            FluidCollisionContactManifold manifold = new FluidCollisionContactManifold(
                new Vector4(1f, 0f, 0f, 1f),
                new Vector4(0f, 1f, 0f, 1f),
                Vector4.zero,
                Vector4.zero);

            Vector4 result = ApplyVelocityResponse(manifold, new Vector4(-2f, -3f, 4f, 0f));

            Assert.That(result.x, Is.GreaterThanOrEqualTo(-1e-5f));
            Assert.That(result.y, Is.GreaterThanOrEqualTo(-1e-5f));
            Assert.That(result.z, Is.EqualTo(4f).Within(1e-4f));
        }

        [Test]
        public void OppositeContactNormalsAreValidAndConvergeWithoutNumericalError()
        {
            IgnoreWithoutComputeSupport();
            FluidCollisionContactManifold manifold = new FluidCollisionContactManifold(
                new Vector4(1f, 0f, 0f, 1f),
                new Vector4(-1f, 0f, 0f, 1f),
                Vector4.zero,
                Vector4.zero);

            Vector4 result = ApplyVelocityResponse(manifold, new Vector4(5f, 1f, 0f, 0f));

            Assert.That(Mathf.Abs(result.x), Is.LessThanOrEqualTo(1e-5f));
            Assert.That(result.y, Is.EqualTo(1f).Within(1e-4f));
        }

        [Test]
        public void SweptProjectionStopsSingleTickHighSpeedCrossingOfThinBox()
        {
            IgnoreWithoutComputeSupport();
            var colliderObject = new GameObject("ThinBox");
            try
            {
                BoxCollider collider = colliderObject.AddComponent<BoxCollider>();
                collider.size = new Vector3(0.02f, 4f, 4f);
                Assert.That(FluidColliderProxy.TryCreate(collider, out FluidColliderProxy proxy), Is.True);

                SimulationResult result = RunSingleProjection(
                    proxy,
                    oldPosition: new Vector3(-1f, 0f, 0f),
                    predictedPosition: new Vector3(1f, 0f, 0f));

                AssertStable(result);
                Assert.That(result.Position.x, Is.EqualTo(-0.11f).Within(0.005f),
                    "Endpoint-only collision 会让两端都在外部的高速粒子直接穿过薄 Box。");
            }
            finally
            {
                Object.DestroyImmediate(colliderObject);
            }
        }

        [Test]
        public void SweptProjectionBlocksInwardMotionStartingExactlyOnExpandedThinBoxSurface()
        {
            IgnoreWithoutComputeSupport();
            var colliderObject = new GameObject("ThinBoxSurfaceStart");
            try
            {
                BoxCollider collider = colliderObject.AddComponent<BoxCollider>();
                collider.size = new Vector3(0.02f, 4f, 4f);
                Assert.That(FluidColliderProxy.TryCreate(collider, out FluidColliderProxy proxy), Is.True);

                // Thin Box half extent 0.01 + particle radius 0.1 = expanded surface x=-0.11。
                // 旧 Sweep 把 start-on-surface 当作普通 inside 直接 return false，endpoint=+1 又在外部，必然整步穿越。
                SimulationResult result = RunSingleProjection(
                    proxy,
                    oldPosition: new Vector3(-0.11f, 0f, 0f),
                    predictedPosition: new Vector3(1f, 0f, 0f));

                AssertStable(result);
                Assert.That(result.Position.x, Is.EqualTo(-0.1101f).Within(0.002f));
            }
            finally
            {
                Object.DestroyImmediate(colliderObject);
            }
        }

        [Test]
        public void HorizontalBoxFloorPreventsGravityPenetrationForOneHundredTwentyTicks()
        {
            IgnoreWithoutComputeSupport();
            var colliderObject = new GameObject("HorizontalFloor");
            try
            {
                colliderObject.transform.position = new Vector3(0f, -0.5f, 0f);
                BoxCollider collider = colliderObject.AddComponent<BoxCollider>();
                collider.size = new Vector3(10f, 1f, 10f);
                Assert.That(FluidColliderProxy.TryCreate(collider, out FluidColliderProxy proxy), Is.True);

                SimulationResult result = RunTicks(
                    proxy,
                    initialPosition: new Vector3(0f, 0.3f, 0f),
                    initialVelocity: Vector3.zero,
                    gravity: new Vector3(0f, -9.81f, 0f));

                AssertStable(result);
                Assert.That(result.Position.y, Is.GreaterThanOrEqualTo(ParticleRadius - 1e-3f));
            }
            finally
            {
                Object.DestroyImmediate(colliderObject);
            }
        }

        [Test]
        public void RotatedObbProjectsParticleBeyondExpandedFaceForOneHundredTwentyTicks()
        {
            IgnoreWithoutComputeSupport();
            var colliderObject = new GameObject("RotatedObb");
            try
            {
                colliderObject.transform.rotation = Quaternion.Euler(0f, 45f, 0f);
                BoxCollider collider = colliderObject.AddComponent<BoxCollider>();
                collider.size = new Vector3(2f, 2f, 0.2f);
                Assert.That(FluidColliderProxy.TryCreate(collider, out FluidColliderProxy proxy), Is.True);

                SimulationResult result = RunTicks(
                    proxy,
                    initialPosition: colliderObject.transform.position,
                    initialVelocity: Vector3.zero,
                    gravity: Vector3.zero);

                AssertStable(result);
                Vector3 local = proxy.TransformWorldPoint(result.Position);
                Assert.That(Mathf.Abs(local.z), Is.GreaterThanOrEqualTo(0.2f - 1e-3f));
            }
            finally
            {
                Object.DestroyImmediate(colliderObject);
            }
        }

        [Test]
        public void SphereProjectsParticleOutsideExpandedRadiusForOneHundredTwentyTicks()
        {
            IgnoreWithoutComputeSupport();
            var colliderObject = new GameObject("SphereObstacle");
            try
            {
                SphereCollider collider = colliderObject.AddComponent<SphereCollider>();
                collider.radius = 1f;
                Assert.That(FluidColliderProxy.TryCreate(collider, out FluidColliderProxy proxy), Is.True);

                SimulationResult result = RunTicks(
                    proxy,
                    initialPosition: Vector3.zero,
                    initialVelocity: Vector3.zero,
                    gravity: Vector3.zero);

                AssertStable(result);
                Vector3 local = proxy.TransformWorldPoint(result.Position);
                Assert.That(local.magnitude, Is.GreaterThanOrEqualTo(1.1f - 1e-3f));
            }
            finally
            {
                Object.DestroyImmediate(colliderObject);
            }
        }

        [Test]
        public void CapsuleProjectsParticleOutsideExpandedRadiusForOneHundredTwentyTicks()
        {
            IgnoreWithoutComputeSupport();
            var colliderObject = new GameObject("CapsuleObstacle");
            try
            {
                CapsuleCollider collider = colliderObject.AddComponent<CapsuleCollider>();
                collider.direction = 1;
                collider.radius = 0.5f;
                collider.height = 2f;
                Assert.That(FluidColliderProxy.TryCreate(collider, out FluidColliderProxy proxy), Is.True);

                SimulationResult result = RunTicks(
                    proxy,
                    initialPosition: Vector3.zero,
                    initialVelocity: Vector3.zero,
                    gravity: Vector3.zero);

                AssertStable(result);
                Vector3 local = proxy.TransformWorldPoint(result.Position);
                float axisY = Mathf.Clamp(local.y, -0.5f, 0.5f);
                float distanceToSegment = (local - new Vector3(0f, axisY, 0f)).magnitude;
                Assert.That(distanceToSegment, Is.GreaterThanOrEqualTo(0.6f - 1e-3f));
            }
            finally
            {
                Object.DestroyImmediate(colliderObject);
            }
        }

        private static SimulationResult RunTicks(
            FluidColliderProxy proxy,
            Vector3 initialPosition,
            Vector3 initialVelocity,
            Vector3 gravity)
        {
            ComputeShader lifecycle = LoadRequiredShader(LifecycleShaderPath);
            ComputeShader solver = LoadRequiredShader(SolverShaderPath);
            ComputeShader collision = LoadRequiredShader(CollisionShaderPath);
            var resources = new FluidGpuResourceSet(1, 2, 1, 1);
            try
            {
                resources.Positions.SetData(new[] { new Vector4(initialPosition.x, initialPosition.y, initialPosition.z, 1f) });
                resources.PredictedPositions.SetData(new[] { new Vector4(initialPosition.x, initialPosition.y, initialPosition.z, 1f) });
                resources.Velocities.SetData(new[] { new Vector4(initialVelocity.x, initialVelocity.y, initialVelocity.z, 0f) });
                resources.Metadata.SetData(new[] { new FluidGpuUInt2(1u, FluidGpuLayout.ActiveFlag) });
                resources.Counters.SetData(new uint[] { 0u, 1u, 0u, 0u });
                resources.ColliderProxies.SetData(new[] { proxy });
                resources.CollisionContacts.SetData(new[] { default(FluidCollisionContactManifold) });

                int applyGravity = lifecycle.FindKernel("ApplyGravity");
                int predictPositions = lifecycle.FindKernel("PredictPositions");
                int commitPositions = lifecycle.FindKernel("CommitPositions");
                int updateVelocities = solver.FindKernel("UpdateVelocities");
                int clearContacts = collision.FindKernel("ClearCollisionContacts");
                int projectCollisions = collision.FindKernel("ProjectCollisions");
                int applyCollisionVelocities = collision.FindKernel("ApplyCollisionVelocities");

                BindLifecycle(lifecycle, applyGravity, predictPositions, commitPositions, resources);
                solver.SetBuffer(updateVelocities, "_Positions", resources.Positions);
                solver.SetBuffer(updateVelocities, "_PredictedPositions", resources.PredictedPositions);
                solver.SetBuffer(updateVelocities, "_ParticleMetadata", resources.Metadata);
                solver.SetBuffer(updateVelocities, "_Velocities", resources.Velocities);
                solver.SetBuffer(updateVelocities, "_Counters", resources.Counters);
                collision.SetBuffer(clearContacts, "_CollisionContacts", resources.CollisionContacts);
                collision.SetBuffer(projectCollisions, "_PredictedPositions", resources.PredictedPositions);
                collision.SetBuffer(projectCollisions, "_Positions", resources.Positions);
                collision.SetBuffer(projectCollisions, "_ParticleMetadata", resources.Metadata);
                collision.SetBuffer(projectCollisions, "_ColliderProxies", resources.ColliderProxies);
                collision.SetBuffer(projectCollisions, "_CollisionContacts", resources.CollisionContacts);
                collision.SetBuffer(projectCollisions, "_Counters", resources.Counters);
                collision.SetBuffer(applyCollisionVelocities, "_Velocities", resources.Velocities);
                collision.SetBuffer(applyCollisionVelocities, "_ParticleMetadata", resources.Metadata);
                collision.SetBuffer(applyCollisionVelocities, "_CollisionContactsReadOnly", resources.CollisionContacts);
                collision.SetBuffer(applyCollisionVelocities, "_Counters", resources.Counters);

                const float deltaTime = 1f / 60f;
                lifecycle.SetInt("_ParticleCapacity", 1);
                lifecycle.SetFloat("_DeltaTime", deltaTime);
                lifecycle.SetFloat("_MaxSpeed", 25f);
                lifecycle.SetVector("_Gravity", gravity);
                solver.SetInt("_ParticleCapacity", 1);
                solver.SetFloat("_DeltaTime", deltaTime);
                collision.SetInt("_ParticleCapacity", 1);
                collision.SetInt("_ColliderCount", 1);
                collision.SetFloat("_ParticleRadius", ParticleRadius);
                collision.SetFloat("_CollisionFriction", 0.1f);
                collision.SetFloat("_CollisionRestitution", 0f);

                for (int tick = 0; tick < 120; tick++)
                {
                    collision.Dispatch(clearContacts, 1, 1, 1);
                    lifecycle.Dispatch(applyGravity, 1, 1, 1);
                    lifecycle.Dispatch(predictPositions, 1, 1, 1);
                    collision.Dispatch(projectCollisions, 1, 1, 1);
                    solver.Dispatch(updateVelocities, 1, 1, 1);
                    // 本 fixture 没开 XSPH/Vorticity；真实 Runtime 同一 Kernel 位于全部 Velocity Filter 之后。
                    collision.Dispatch(applyCollisionVelocities, 1, 1, 1);
                    lifecycle.Dispatch(commitPositions, 1, 1, 1);
                }

                // Test-only 同步 Readback：验证数值稳定性，禁止搬进正常 Frame/Tick Runtime。
                var positions = new Vector4[1];
                var velocities = new Vector4[1];
                var metadata = new FluidGpuUInt2[1];
                var counters = new uint[FluidGpuLayout.CounterCount];
                resources.Positions.GetData(positions);
                resources.Velocities.GetData(velocities);
                resources.Metadata.GetData(metadata);
                resources.Counters.GetData(counters);
                return new SimulationResult(
                    positions[0],
                    velocities[0],
                    metadata[0],
                    counters[FluidGpuLayout.ActiveCountCounterIndex],
                    counters[FluidGpuLayout.NumericalErrorCounterIndex]);
            }
            finally
            {
                resources.Dispose();
            }
        }

        private static Vector4 ApplyVelocityResponse(
            FluidCollisionContactManifold manifold,
            Vector4 velocity)
        {
            ComputeShader collision = LoadRequiredShader(CollisionShaderPath);
            var resources = new FluidGpuResourceSet(1, 2, 1, 1);
            try
            {
                resources.Velocities.SetData(new[] { velocity });
                resources.Metadata.SetData(new[] { new FluidGpuUInt2(1u, FluidGpuLayout.ActiveFlag) });
                resources.CollisionContacts.SetData(new[] { manifold });
                resources.Counters.SetData(new uint[] { 0u, 1u, 0u, 0u });
                int kernel = collision.FindKernel("ApplyCollisionVelocities");
                collision.SetBuffer(kernel, "_Velocities", resources.Velocities);
                collision.SetBuffer(kernel, "_ParticleMetadata", resources.Metadata);
                collision.SetBuffer(kernel, "_CollisionContactsReadOnly", resources.CollisionContacts);
                collision.SetBuffer(kernel, "_Counters", resources.Counters);
                collision.SetInt("_ParticleCapacity", 1);
                collision.SetFloat("_CollisionFriction", 0f);
                collision.SetFloat("_CollisionRestitution", 0f);
                collision.Dispatch(kernel, 1, 1, 1);

                var velocities = new Vector4[1];
                var counters = new uint[FluidGpuLayout.CounterCount];
                resources.Velocities.GetData(velocities);
                resources.Counters.GetData(counters);
                Assert.That(counters[FluidGpuLayout.NumericalErrorCounterIndex], Is.Zero);
                return velocities[0];
            }
            finally
            {
                resources.Dispose();
            }
        }

        private static SimulationResult RunSingleProjection(
            FluidColliderProxy proxy,
            Vector3 oldPosition,
            Vector3 predictedPosition)
        {
            ComputeShader collision = LoadRequiredShader(CollisionShaderPath);
            var resources = new FluidGpuResourceSet(1, 2, 1, 1);
            try
            {
                resources.Positions.SetData(new[] { new Vector4(oldPosition.x, oldPosition.y, oldPosition.z, 1f) });
                resources.PredictedPositions.SetData(new[] { new Vector4(predictedPosition.x, predictedPosition.y, predictedPosition.z, 1f) });
                resources.Velocities.SetData(new[] { Vector4.zero });
                resources.Metadata.SetData(new[] { new FluidGpuUInt2(1u, FluidGpuLayout.ActiveFlag) });
                resources.Counters.SetData(new uint[] { 0u, 1u, 0u, 0u });
                resources.ColliderProxies.SetData(new[] { proxy });
                resources.CollisionContacts.SetData(new[] { default(FluidCollisionContactManifold) });
                int clear = collision.FindKernel("ClearCollisionContacts");
                int project = collision.FindKernel("ProjectCollisions");
                collision.SetBuffer(clear, "_CollisionContacts", resources.CollisionContacts);
                collision.SetBuffer(project, "_Positions", resources.Positions);
                collision.SetBuffer(project, "_PredictedPositions", resources.PredictedPositions);
                collision.SetBuffer(project, "_ParticleMetadata", resources.Metadata);
                collision.SetBuffer(project, "_ColliderProxies", resources.ColliderProxies);
                collision.SetBuffer(project, "_CollisionContacts", resources.CollisionContacts);
                collision.SetBuffer(project, "_Counters", resources.Counters);
                collision.SetInt("_ParticleCapacity", 1);
                collision.SetInt("_ColliderCount", 1);
                collision.SetFloat("_ParticleRadius", ParticleRadius);
                collision.Dispatch(clear, 1, 1, 1);
                collision.Dispatch(project, 1, 1, 1);

                var positions = new Vector4[1];
                var velocities = new Vector4[1];
                var metadata = new FluidGpuUInt2[1];
                var counters = new uint[FluidGpuLayout.CounterCount];
                resources.PredictedPositions.GetData(positions);
                resources.Velocities.GetData(velocities);
                resources.Metadata.GetData(metadata);
                resources.Counters.GetData(counters);
                return new SimulationResult(
                    positions[0],
                    velocities[0],
                    metadata[0],
                    counters[FluidGpuLayout.ActiveCountCounterIndex],
                    counters[FluidGpuLayout.NumericalErrorCounterIndex]);
            }
            finally
            {
                resources.Dispose();
            }
        }

        private static void BindLifecycle(
            ComputeShader lifecycle,
            int applyGravity,
            int predictPositions,
            int commitPositions,
            FluidGpuResourceSet resources)
        {
            lifecycle.SetBuffer(applyGravity, "_Velocities", resources.Velocities);
            lifecycle.SetBuffer(applyGravity, "_ParticleMetadata", resources.Metadata);
            lifecycle.SetBuffer(predictPositions, "_Positions", resources.Positions);
            lifecycle.SetBuffer(predictPositions, "_PredictedPositions", resources.PredictedPositions);
            lifecycle.SetBuffer(predictPositions, "_Velocities", resources.Velocities);
            lifecycle.SetBuffer(predictPositions, "_ParticleMetadata", resources.Metadata);
            lifecycle.SetBuffer(commitPositions, "_Positions", resources.Positions);
            lifecycle.SetBuffer(commitPositions, "_PredictedPositions", resources.PredictedPositions);
            lifecycle.SetBuffer(commitPositions, "_ParticleMetadata", resources.Metadata);
        }

        private static void AssertStable(SimulationResult result)
        {
            Assert.That(IsFinite(result.Position), Is.True);
            Assert.That(IsFinite(result.Velocity), Is.True);
            Assert.That(result.Metadata.Y & FluidGpuLayout.ActiveFlag, Is.EqualTo(FluidGpuLayout.ActiveFlag));
            Assert.That(result.ActiveCount, Is.EqualTo(1u));
            Assert.That(result.NumericalErrorCount, Is.Zero);
        }

        private static ComputeShader LoadRequiredShader(string path)
        {
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
            Assert.That(shader, Is.Not.Null, $"Missing ComputeShader at {path}.");
            return shader;
        }

        private static bool IsFinite(Vector4 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static void IgnoreWithoutComputeSupport()
        {
            if (!SystemInfo.supportsComputeShaders
                || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Assert.Ignore("当前 Graphics Device 不支持 ComputeShader；GPU collision test 被跳过，不能记为通过。");
            }
        }

        private readonly struct SimulationResult
        {
            public readonly Vector4 Position;
            public readonly Vector4 Velocity;
            public readonly FluidGpuUInt2 Metadata;
            public readonly uint ActiveCount;
            public readonly uint NumericalErrorCount;

            public SimulationResult(
                Vector4 position,
                Vector4 velocity,
                FluidGpuUInt2 metadata,
                uint activeCount,
                uint numericalErrorCount)
            {
                Position = position;
                Velocity = velocity;
                Metadata = metadata;
                ActiveCount = activeCount;
                NumericalErrorCount = numericalErrorCount;
            }
        }
    }
}
