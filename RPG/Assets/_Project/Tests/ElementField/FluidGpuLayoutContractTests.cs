using System.Runtime.InteropServices;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// 这些测试锁定 CPU/HLSL 共享布局与真实 GraphicsBuffer 生命周期。
    /// 若只检查源码字符串，Stride 写错时仍可能通过，因此这里直接检查 Unity 创建出的 Buffer。
    /// </summary>
    public sealed class FluidGpuLayoutContractTests
    {
        [Test]
        public void ResidentBoundsTrackerDoesNotShrinkWhenInterestMoves()
        {
            var tracker = new FluidResidentBoundsTracker();
            tracker.Include(new Bounds(Vector3.zero, new Vector3(10f, 6f, 10f)));
            tracker.Include(new Bounds(new Vector3(20f, 0f, 0f), new Vector3(10f, 6f, 10f)));

            Assert.That(tracker.Bounds.min.x, Is.EqualTo(-5f).Within(0.0001f));
            Assert.That(tracker.Bounds.max.x, Is.EqualTo(25f).Within(0.0001f));
        }
        private const string LifecycleShaderPath =
            "Assets/_Project/Art/Elemental/Compute/PbfParticleLifecycle.compute";

        [Test]
        public void SharedGpuLayoutsKeepFixedCrossApiStrides()
        {
            Assert.That(Marshal.SizeOf<Vector4>(), Is.EqualTo(16));
            Assert.That(Marshal.SizeOf<FluidGpuUInt2>(), Is.EqualTo(8));
            Assert.That(Marshal.SizeOf<FluidGpuInt4>(), Is.EqualTo(16));
            Assert.That(Marshal.SizeOf<FluidGpuSpawnRequest>(), Is.EqualTo(64));
            Assert.That(Marshal.SizeOf<FluidGpuLiquidMaterialParameters>(), Is.EqualTo(32));
            Assert.That(FluidGpuLayout.SpawnRequestStride, Is.EqualTo(64));
            Assert.That(FluidGpuLayout.LiquidMaterialParameterStride, Is.EqualTo(32));
            Assert.That(FluidGpuLayout.LayoutVersion, Is.EqualTo(9u));
            Assert.That(FluidGpuLayout.ArchiveLockedFlag, Is.EqualTo(1u << 5));
            Assert.That(FluidGpuLayout.RestoreLoadingFlag, Is.EqualTo(1u << 6));
            Assert.That(FluidGpuLayout.ArchiveSampleStride, Is.EqualTo(32));
            Assert.That(
                FluidSpawnFlags.UseLinearFalloff & FluidSpawnFlags.DensityPacked,
                Is.Zero,
                "Packed 与 Legacy Falloff 必须是可独立开关的两个 bit。");
            Assert.That(Marshal.SizeOf<FluidConsumeCommand>(), Is.EqualTo(48));
            Assert.That(FluidGpuLayout.ConsumeRequestStride, Is.EqualTo(48));
            Assert.That(Marshal.SizeOf<FluidConvertCommand>(), Is.EqualTo(48));
            Assert.That(FluidGpuLayout.ConvertRequestStride, Is.EqualTo(48));
            Assert.That(Marshal.SizeOf<FluidColliderProxy>(), Is.EqualTo(64));
            Assert.That(Marshal.SizeOf<FluidCollisionContactManifold>(), Is.EqualTo(64));
            Assert.That(FluidGpuLayout.ColliderProxyStride, Is.EqualTo(64));
            Assert.That(FluidGpuLayout.CollisionContactManifoldStride, Is.EqualTo(64));
        }

        [Test]
        public void ResourceSetAllocatesEveryFixedCapacityBufferWithExpectedStride()
        {
            IgnoreWithoutComputeSupport();

            var resources = new FluidGpuResourceSet(
                particleCapacity: 8,
                hashTableCapacity: 16,
                maxSpawnRequests: 4,
                maxFluidColliders: 3);
            try
            {
                AssertBuffer(resources.Positions, 8, 16);
                AssertBuffer(resources.PredictedPositions, 8, 16);
                AssertBuffer(resources.Velocities, 8, 16);
                AssertBuffer(resources.DensityLambda, 8, 8);
                AssertBuffer(resources.Metadata, 8, 8);
                AssertBuffer(resources.DeltaPositions, 8, 16);
                AssertBuffer(resources.DeltaVelocities, 8, 16);
                AssertBuffer(resources.Vorticities, 8, 16);
                AssertBuffer(resources.SpatialCells, 8, FluidGpuLayout.Int4Stride);
                AssertBuffer(resources.SpatialEntries, 8, 8);
                AssertBuffer(resources.CellRanges, 16, 8);
                AssertBuffer(resources.FreeIndices, 8, 4);
                AssertBuffer(resources.Counters, FluidGpuLayout.CounterCount, 4);
                AssertBuffer(resources.SpawnRequests, 4, 64);
                AssertBuffer(resources.LiquidMaterialParameters, 256, 32);
                AssertBuffer(resources.ColliderProxies, 3, FluidGpuLayout.ColliderProxyStride);
                AssertBuffer(
                    resources.CollisionContacts,
                    8,
                    FluidGpuLayout.CollisionContactManifoldStride);
                AssertBuffer(resources.GameplaySamples, 8, FluidGameplaySampleCodec.Stride);
                AssertBuffer(resources.ConsumeRequests, 4, FluidGpuLayout.ConsumeRequestStride);
                AssertBuffer(resources.ConvertRequests, 4, FluidGpuLayout.ConvertRequestStride);
                AssertBuffer(resources.StableTickCounters, 8, 4);
                AssertBuffer(resources.WakeRequests, 8, 4);
                AssertBuffer(resources.ActivityCounters, FluidGpuLayout.ActivityCounterCount, 4);
                AssertBuffer(resources.SolverDispatchArgs, 3, 4);
                AssertBuffer(resources.HashDispatchArgs, 3, 4);
            }
            finally
            {
                resources.Dispose();
            }
        }

        [Test]
        public void DisposeIsIdempotentAndClearsEveryBufferReference()
        {
            IgnoreWithoutComputeSupport();

            var resources = new FluidGpuResourceSet(8, 16, 4, 3);

            resources.Dispose();
            Assert.DoesNotThrow(resources.Dispose);

            Assert.That(resources.Positions, Is.Null);
            Assert.That(resources.PredictedPositions, Is.Null);
            Assert.That(resources.Velocities, Is.Null);
            Assert.That(resources.DensityLambda, Is.Null);
            Assert.That(resources.Metadata, Is.Null);
            Assert.That(resources.DeltaPositions, Is.Null);
            Assert.That(resources.DeltaVelocities, Is.Null);
            Assert.That(resources.Vorticities, Is.Null);
            Assert.That(resources.SpatialCells, Is.Null);
            Assert.That(resources.SpatialEntries, Is.Null);
            Assert.That(resources.CellRanges, Is.Null);
            Assert.That(resources.FreeIndices, Is.Null);
            Assert.That(resources.Counters, Is.Null);
            Assert.That(resources.SpawnRequests, Is.Null);
            Assert.That(resources.ColliderProxies, Is.Null);
            Assert.That(resources.CollisionContacts, Is.Null);
            Assert.That(resources.GameplaySamples, Is.Null);
            Assert.That(resources.ConsumeRequests, Is.Null);
            Assert.That(resources.ConvertRequests, Is.Null);
            Assert.That(resources.StableTickCounters, Is.Null);
            Assert.That(resources.WakeRequests, Is.Null);
            Assert.That(resources.ActivityCounters, Is.Null);
            Assert.That(resources.SolverDispatchArgs, Is.Null);
            Assert.That(resources.HashDispatchArgs, Is.Null);
            Assert.That(resources.LiquidMaterialParameters, Is.Null);
        }

        [Test]
        public void FourthCounterIsReservedForNumericalErrors()
        {
            Assert.That(FluidGpuLayout.CounterCount, Is.EqualTo(4));
            Assert.That(FluidGpuLayout.NumericalErrorCounterIndex, Is.EqualTo(3));
        }

        [Test]
        public void ActivityFlagsUseIndependentBitsAndLegacyActiveMeansDefaultAwake()
        {
            Assert.That(FluidGpuLayout.AliveFlag, Is.EqualTo(1u));
            Assert.That(FluidGpuLayout.InterestActiveFlag, Is.EqualTo(2u));
            Assert.That(FluidGpuLayout.RequiresSimulationFlag, Is.EqualTo(4u));
            Assert.That(FluidGpuLayout.SleepingFlag, Is.EqualTo(8u));
            Assert.That(
                FluidGpuLayout.ActiveFlag,
                Is.EqualTo(FluidGpuLayout.AliveFlag
                    | FluidGpuLayout.InterestActiveFlag
                    | FluidGpuLayout.RequiresSimulationFlag));
        }

        [Test]
        public void RenderingSnapshotCarriesSimulationParticleMassInsteadOfDuplicatingItInRenderProfile()
        {
            var snapshot = new FluidGpuSnapshot(
                positions: null,
                predictedPositions: null,
                velocities: null,
                metadata: null,
                spatialEntries: null,
                cellRanges: null,
                spatialCells: null,
                particleCapacity: 8,
                hashTableCapacity: 16,
                smoothingRadius: 0.25f,
                particleMass: 0.0125f,
                activeBounds: new Bounds(Vector3.zero, Vector3.one),
                layoutVersion: FluidGpuLayout.LayoutVersion,
                topologyVersion: 19u);

            Assert.That(snapshot.ParticleMass, Is.EqualTo(0.0125f));
            Assert.That(snapshot.TopologyVersion, Is.EqualTo(19u));
        }

        [Test]
        public void MaterialPresenceMaskStartsEmptyAndBecomesMonotonic()
        {
            uint mask = 0u;
            Assert.That(FluidMaterialPresenceMask.MayContain(mask, 1u), Is.False);

            mask = FluidMaterialPresenceMask.Include(mask, 1u);
            Assert.That(FluidMaterialPresenceMask.MayContain(mask, 1u), Is.True);
            Assert.That(FluidMaterialPresenceMask.MayContain(mask, 3u), Is.False);

            mask = FluidMaterialPresenceMask.Include(mask, 3u);
            Assert.That(FluidMaterialPresenceMask.MayContain(mask, 1u), Is.True,
                "Adding Poison must never forget that Water may still be resident.");
            Assert.That(FluidMaterialPresenceMask.MayContain(mask, 3u), Is.True);
        }

        [Test]
        public void UnknownMaterialIdUsesConservativePresenceInsteadOfSkippingRendering()
        {
            uint mask = FluidMaterialPresenceMask.Include(0u, 32u);
            Assert.That(mask, Is.EqualTo(uint.MaxValue));
            Assert.That(FluidMaterialPresenceMask.MayContain(0u, 32u), Is.True);
        }

        [Test]
        public void TopologyVersionPublishesOnlyAfterNonEmptySpawnDispatchSubmission()
        {
            var queue = new FluidSpawnQueue(1);
            var tracker = new FluidTopologyVersionTracker();
            FluidSpawnRequest request = CreateSpawnRequest(radius: 0.5f, particleCount: 1u);
            var upload = new FluidSpawnRequest[1];

            Assert.That(queue.TryEnqueue(in request), Is.True);
            Assert.That(tracker.PublishedVersion, Is.EqualTo(0u),
                "CPU enqueue only promises future work; Rendering must still observe the old GPU topology.");
            Assert.That(queue.TryEnqueue(in request), Is.False,
                "A failed full-queue enqueue must not publish a topology change.");
            Assert.That(tracker.PublishedVersion, Is.EqualTo(0u));

            int requestCount = queue.CopyAndClear(upload);
            tracker.PublishAfterSpawnDispatch(requestCount);
            Assert.That(tracker.PublishedVersion, Is.EqualTo(1u),
                "The version becomes visible only at the non-empty Spawn dispatch submission boundary.");

            tracker.PublishAfterSpawnDispatch(0);
            Assert.That(tracker.PublishedVersion, Is.EqualTo(1u),
                "A tick without Spawn dispatch must preserve the published topology version.");
        }

        [Test]
        public void TopologyVersionDispatchPublicationUsesUncheckedWrap()
        {
            var tracker = new FluidTopologyVersionTracker(uint.MaxValue);

            tracker.PublishAfterSpawnDispatch(1);

            Assert.That(tracker.PublishedVersion, Is.EqualTo(0u));
        }

        [Test]
        public void LifecycleComputeAssetExposesTheLockedKernelContract()
        {
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(LifecycleShaderPath);

            Assert.That(shader, Is.Not.Null, $"Missing ComputeShader at {LifecycleShaderPath}.");
            Assert.That(shader.HasKernel("InitializePool"), Is.True);
            Assert.That(shader.HasKernel("InitializePoolAuxiliary"), Is.True);
            Assert.That(shader.HasKernel("SpawnParticles"), Is.True);
            Assert.That(shader.HasKernel("ConvertParticles"), Is.True);
            Assert.That(shader.HasKernel("ApplyGravity"), Is.True);
            Assert.That(shader.HasKernel("PredictPositions"), Is.True);
            Assert.That(shader.HasKernel("CommitPositions"), Is.True);
            Assert.That(shader.HasKernel("PackGameplaySamples"), Is.True);
        }

        [Test]
        public void SpawnRequestValidatorAcceptsOnlyFiniteNonzeroRequestsWithinParticlePoolCapacity()
        {
            const int particleCapacity = 8;
            FluidSpawnRequest validRequest = CreateSpawnRequest(radius: 0.5f, particleCount: 8u);

            Assert.That(
                FluidSpawnRequestValidator.IsValidForParticleCapacity(in validRequest, particleCapacity),
                Is.True);

            FluidSpawnRequest zeroCount = CreateSpawnRequest(radius: 0.5f, particleCount: 0u);
            Assert.That(
                FluidSpawnRequestValidator.IsValidForParticleCapacity(in zeroCount, particleCapacity),
                Is.False);

            FluidSpawnRequest oversized = CreateSpawnRequest(radius: 0.5f, particleCount: 9u);
            Assert.That(
                FluidSpawnRequestValidator.IsValidForParticleCapacity(in oversized, particleCapacity),
                Is.False);

            FluidSpawnRequest pointSpawn = CreateSpawnRequest(radius: 0f, particleCount: 1u);
            Assert.That(
                FluidSpawnRequestValidator.IsValidForParticleCapacity(in pointSpawn, particleCapacity),
                Is.True);

            FluidSpawnRequest negativeRadius = CreateSpawnRequest(radius: -0.01f, particleCount: 1u);
            Assert.That(
                FluidSpawnRequestValidator.IsValidForParticleCapacity(in negativeRadius, particleCapacity),
                Is.False);

            FluidSpawnRequest positiveInfinityRadius = CreateSpawnRequest(
                radius: float.PositiveInfinity,
                particleCount: 1u);
            Assert.That(
                FluidSpawnRequestValidator.IsValidForParticleCapacity(in positiveInfinityRadius, particleCapacity),
                Is.False);

            FluidSpawnRequest negativeInfinityRadius = CreateSpawnRequest(
                radius: float.NegativeInfinity,
                particleCount: 1u);
            Assert.That(
                FluidSpawnRequestValidator.IsValidForParticleCapacity(in negativeInfinityRadius, particleCapacity),
                Is.False);

            FluidSpawnRequest nanRadius = CreateSpawnRequest(radius: float.NaN, particleCount: 1u);
            Assert.That(
                FluidSpawnRequestValidator.IsValidForParticleCapacity(in nanRadius, particleCapacity),
                Is.False);

            FluidSpawnRequest invalidPosition = CreateSpawnRequest(
                radius: 0.5f,
                particleCount: 1u,
                worldPosition: new Vector3(float.PositiveInfinity, 0f, 0f));
            Assert.That(
                FluidSpawnRequestValidator.IsValidForParticleCapacity(in invalidPosition, particleCapacity),
                Is.False);

            FluidSpawnRequest invalidVelocity = CreateSpawnRequest(
                radius: 0.5f,
                particleCount: 1u,
                initialVelocity: new Vector3(0f, float.NaN, 0f));
            Assert.That(
                FluidSpawnRequestValidator.IsValidForParticleCapacity(in invalidVelocity, particleCapacity),
                Is.False);
        }

        private static FluidSpawnRequest CreateSpawnRequest(
            float radius,
            uint particleCount,
            Vector3? worldPosition = null,
            Vector3? initialVelocity = null)
        {
            return new FluidSpawnRequest(
                worldPosition: worldPosition ?? Vector3.zero,
                initialVelocity: initialVelocity ?? Vector3.zero,
                radius: radius,
                particleCount: particleCount,
                materialId: 1u,
                seed: 1u,
                flags: 0u);
        }

        private static void AssertBuffer(GraphicsBuffer buffer, int count, int stride)
        {
            Assert.That(buffer, Is.Not.Null);
            Assert.That(buffer.count, Is.EqualTo(count));
            Assert.That(buffer.stride, Is.EqualTo(stride));
        }

        private static void IgnoreWithoutComputeSupport()
        {
            if (!SystemInfo.supportsComputeShaders
                || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Assert.Ignore("当前 Graphics Device 不支持 ComputeShader；GPU Buffer 测试被跳过，不能记为通过。");
            }
        }
    }
}
