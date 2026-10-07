using Game.Materials;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class FluidGameplayOccupancyBridgeTests
    {
        [Test]
        public void RequestState_AllowsOnlyOneFlightAndOneLeaseRelease()
        {
            var state = new FluidReadbackRequestState();

            Assert.That(state.TryBegin(), Is.True);
            Assert.That(state.TryBegin(), Is.False);
            state.RecordCompletion(hasError: false);
            Assert.That(state.TryConsumeCompletion(out bool hasError), Is.True);
            Assert.That(hasError, Is.False);
            Assert.That(state.TryConsumeCompletion(out _), Is.False);
            Assert.That(state.TryReleaseLease(), Is.True);
            Assert.That(state.TryReleaseLease(), Is.False);
        }

        [Test]
        public void RequestState_DestroyWaitAndReleaseAreBothIdempotent()
        {
            var state = new FluidReadbackRequestState();
            state.TryBegin();

            Assert.That(state.TryBeginDestroyWait(), Is.True);
            Assert.That(state.TryBeginDestroyWait(), Is.False);
            state.RecordCompletion(hasError: true);
            Assert.That(state.TryReleaseLease(), Is.True);
            Assert.That(state.TryReleaseLease(), Is.False);
        }

        [Test]
        public void RequestState_CancelBeforeSubmissionClearsFlightAndStillReleasesLeaseOnce()
        {
            var state = new FluidReadbackRequestState();
            Assert.That(state.TryBegin(), Is.True);

            Assert.That(state.TryCancelBeforeSubmission(), Is.True);
            Assert.That(state.IsInFlight, Is.False);
            Assert.That(state.TryBeginDestroyWait(), Is.False);
            Assert.That(state.TryReleaseLease(), Is.True);
            Assert.That(state.TryReleaseLease(), Is.False);
            Assert.That(state.TryBegin(), Is.True);
        }

        [Test]
        public void SourceLeaseTracker_DefersTeardownUntilOutstandingLeaseIsReleased()
        {
            var tracker = new FluidReadbackLeaseTracker();
            Assert.That(tracker.TryAcquire(), Is.True);
            Assert.That(tracker.TryAcquire(), Is.False);
            Assert.That(tracker.RequestRelease(), Is.False);
            Assert.That(tracker.ReleaseLease(), Is.True);
            Assert.That(tracker.ReleaseLease(), Is.False);
        }

        [Test]
        public void FailedReadbackDecisionPreservesPreviousPublishedSnapshot()
        {
            Assert.That(FluidSnapshotPublishPolicy.ShouldPublish(
                requestHasError: true,
                stagingRebuildSucceeded: true), Is.False);
            Assert.That(FluidSnapshotPublishPolicy.ShouldPublish(
                requestHasError: false,
                stagingRebuildSucceeded: false), Is.False);
            Assert.That(FluidSnapshotPublishPolicy.ShouldPublish(
                requestHasError: false,
                stagingRebuildSucceeded: true), Is.True);
        }

        [Test]
        public void BroadphasePlan_KeepsLegacyAndDelayedLiquidBoundsAsTwoQueries()
        {
            var legacyBounds = new Bounds(Vector3.zero, Vector3.one * 4f);
            var delayedLiquidBounds = new Bounds(new Vector3(100f, 0f, 0f), Vector3.one * 4f);

            ElementExposureBroadphasePlan plan = ElementExposureBroadphasePlanner.Plan(
                WaterSimulationMode.GpuPbf,
                legacyBounds,
                hasLiquidSnapshot: true,
                delayedLiquidBounds);

            Assert.That(plan.QueryCount, Is.EqualTo(2),
                "不能把相距很远的 Bounds 合成一个巨大 OverlapBox；中间无关 Collider 会先填满 NonAlloc Buffer。");
            Assert.That(plan.GetQueryBounds(0), Is.EqualTo(legacyBounds),
                "Legacy/Fire Bounds 必须优先查询，避免 Water Snapshot 延迟时吞掉 Burning。");
            Assert.That(plan.GetQueryBounds(1), Is.EqualTo(delayedLiquidBounds));
        }

        [TestCase(WaterSimulationMode.LegacyCell, false)]
        [TestCase(WaterSimulationMode.GpuPbf, false)]
        public void BroadphasePlan_WithoutUsablePbfSnapshotUsesOnlyLegacyBounds(
            WaterSimulationMode mode,
            bool hasLiquidSnapshot)
        {
            var legacyBounds = new Bounds(Vector3.one, Vector3.one * 2f);

            ElementExposureBroadphasePlan plan = ElementExposureBroadphasePlanner.Plan(
                mode,
                legacyBounds,
                hasLiquidSnapshot,
                new Bounds(Vector3.one * 50f, Vector3.one));

            Assert.That(plan.QueryCount, Is.EqualTo(1));
            Assert.That(plan.GetQueryBounds(0), Is.EqualTo(legacyBounds));
        }

        [TestCase(float.NaN, 0.25f)]
        [TestCase(float.PositiveInfinity, 0.25f)]
        [TestCase(float.NegativeInfinity, 0.25f)]
        [TestCase(0f, 0.05f)]
        [TestCase(-1f, 0.05f)]
        [TestCase(0.4f, 0.4f)]
        public void ReadbackIntervalPolicy_PreventsInvalidValuesFromRequestingEveryFrame(
            float configured,
            float expected)
        {
            Assert.That(
                FluidReadbackIntervalPolicy.Sanitize(configured),
                Is.EqualTo(expected));
        }

        [Test]
        public void TemporaryLeaseContentionKeepsElapsedReadbackDebtForNextFrame()
        {
            const float interval = 0.25f;
            var root = new GameObject("FluidGameplayOccupancyBridgeTests.Retry");
            root.SetActive(false);
            try
            {
                ElementWorldRuntime world = root.AddComponent<ElementWorldRuntime>();
                FluidGameplayOccupancyBridge bridge = root.AddComponent<FluidGameplayOccupancyBridge>();
                SetPrivateField(world, "<IsInitialized>k__BackingField", true);
                SetPrivateField(bridge, "_elementWorldRuntime", world);
                SetPrivateField(bridge, "_source", new AlwaysBusyReadbackSource());
                SetPrivateField(bridge, "_supported", true);
                SetPrivateField(bridge, "_readbackInterval", interval);
                SetPrivateField(bridge, "_elapsed", interval);

                InvokePrivateVoid(bridge, "LateUpdate");

                Assert.That(GetPrivateField<float>(bridge, "_elapsed"), Is.GreaterThanOrEqualTo(interval),
                    "临时拿不到 Source Lease 时必须保留已到期状态，下一帧立即重试，不能再等待完整周期。");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static void SetPrivateField<TTarget, TValue>(
            TTarget target,
            string fieldName,
            TValue value)
        {
            FieldInfo field = typeof(TTarget).GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"缺少预期字段 {typeof(TTarget).Name}.{fieldName}。");
            field.SetValue(target, value);
        }

        private static TValue GetPrivateField<TValue>(object target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"缺少预期字段 {target.GetType().Name}.{fieldName}。");
            return (TValue)field.GetValue(target);
        }

        private static void InvokePrivateVoid(object target, string methodName)
        {
            MethodInfo method = target.GetType().GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, $"缺少预期方法 {target.GetType().Name}.{methodName}。");
            method.Invoke(target, null);
        }

        private sealed class AlwaysBusyReadbackSource : IFluidGameplayReadbackSource
        {
            public bool TryGetGameplayParticleCapacity(out int particleCapacity)
            {
                particleCapacity = 1;
                return true;
            }

            public bool TryAcquireGameplayReadbackLease(
                Vector3 worldOrigin,
                float cellSize,
                out FluidGameplayReadbackLease lease)
            {
                lease = default;
                return false;
            }

            public void ReleaseGameplayReadbackLease() { }
        }
    }
}
