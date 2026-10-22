using System.Reflection;
using System.Runtime.InteropServices;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// Collider Proxy 是 Unity Physics 与 GPU Collision Constraint 之间的边界；这些测试用手算的
    /// World/Shape Space 数值锁住缩放、旋转、方向和确定性收集语义，而不是复用生产转换公式。
    /// </summary>
    public sealed class FluidColliderProxyTests
    {
        private GameObject _root;

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
                Object.DestroyImmediate(_root);
        }

        [Test]
        public void ProxyLayoutIsExactlySixtyFourBytes()
        {
            Assert.That(Marshal.SizeOf<FluidColliderProxy>(), Is.EqualTo(64));
            Assert.That(FluidGpuLayout.ColliderProxyStride, Is.EqualTo(64));
        }

        [Test]
        public void NonUniformScaledRotatedBoxKeepsWorldSizedHalfExtentsAndCenteredRigidBasis()
        {
            _root = new GameObject("BoxProxy");
            _root.transform.SetPositionAndRotation(
                new Vector3(3f, 4f, 5f),
                Quaternion.Euler(0f, 90f, 0f));
            _root.transform.localScale = new Vector3(2f, 3f, 4f);
            BoxCollider collider = _root.AddComponent<BoxCollider>();
            collider.center = new Vector3(1f, 0f, 0f);
            collider.size = new Vector3(2f, 4f, 6f);

            Assert.That(FluidColliderProxy.TryCreate(collider, out FluidColliderProxy proxy), Is.True);

            Assert.That(proxy.Shape, Is.EqualTo(FluidColliderShape.OrientedBox));
            AssertVector(proxy.Parameters, new Vector3(2f, 6f, 12f));
            Vector3 worldCenter = _root.transform.TransformPoint(collider.center);
            AssertVector(proxy.TransformWorldPoint(worldCenter), Vector3.zero);
            AssertVector(
                proxy.TransformWorldPoint(worldCenter + _root.transform.right * 2f),
                new Vector3(2f, 0f, 0f));
        }

        [Test]
        public void SphereUsesLargestAbsoluteLossyScaleAxis()
        {
            _root = new GameObject("SphereProxy");
            _root.transform.position = new Vector3(-2f, 1f, 3f);
            _root.transform.localScale = new Vector3(2f, 4f, 3f);
            SphereCollider collider = _root.AddComponent<SphereCollider>();
            collider.center = new Vector3(0.5f, -0.25f, 0.75f);
            collider.radius = 0.5f;

            Assert.That(FluidColliderProxy.TryCreate(collider, out FluidColliderProxy proxy), Is.True);

            Assert.That(proxy.Shape, Is.EqualTo(FluidColliderShape.Sphere));
            Assert.That(proxy.Parameters.x, Is.EqualTo(2f).Within(1e-5f));
            AssertVector(proxy.TransformWorldPoint(_root.transform.TransformPoint(collider.center)), Vector3.zero);
        }

        [TestCase(0, 2f, 2f)]
        [TestCase(2, 1.5f, 6.5f)]
        public void CapsuleDirectionBecomesShapeLocalYAndKeepsWorldRadiusAndHalfSegment(
            int direction,
            float expectedRadius,
            float expectedHalfSegment)
        {
            _root = new GameObject("CapsuleProxy");
            _root.transform.SetPositionAndRotation(
                new Vector3(1f, 2f, -3f),
                Quaternion.Euler(15f, 25f, 35f));
            _root.transform.localScale = new Vector3(2f, 3f, 4f);
            CapsuleCollider collider = _root.AddComponent<CapsuleCollider>();
            collider.center = new Vector3(0.25f, 0.5f, -0.25f);
            collider.direction = direction;
            collider.radius = 0.5f;
            collider.height = 4f;

            Assert.That(FluidColliderProxy.TryCreate(collider, out FluidColliderProxy proxy), Is.True);

            Assert.That(proxy.Shape, Is.EqualTo(FluidColliderShape.Capsule));
            Assert.That(proxy.Parameters.x, Is.EqualTo(expectedRadius).Within(1e-5f));
            Assert.That(proxy.Parameters.y, Is.EqualTo(expectedHalfSegment).Within(1e-5f));
            Vector3 axis = direction == 0 ? _root.transform.right : _root.transform.forward;
            Vector3 center = _root.transform.TransformPoint(collider.center);
            AssertVector(proxy.TransformWorldPoint(center + axis), Vector3.up);
        }

        [Test]
        public void CollectorKeepsStableSupportedSlotsAndCountsOverflowBeforeEligibilityFiltering()
        {
            _root = new GameObject("ColliderRoot");
            CreateAuthoredCollider<BoxCollider>("00-Box");

            FluidColliderAuthoring disabledAuthoring =
                CreateAuthoredCollider<BoxCollider>("01-Disabled");
            disabledAuthoring.enabled = false;

            FluidColliderAuthoring inactiveAuthoring =
                CreateAuthoredCollider<SphereCollider>("02-Inactive");
            inactiveAuthoring.gameObject.SetActive(false);

            FluidColliderAuthoring triggerAuthoring =
                CreateAuthoredCollider<CapsuleCollider>("03-Trigger");
            triggerAuthoring.GetComponent<CapsuleCollider>().isTrigger = true;

            FluidColliderAuthoring colliderDisabledAuthoring =
                CreateAuthoredCollider<SphereCollider>("04-ColliderDisabled");
            colliderDisabledAuthoring.GetComponent<SphereCollider>().enabled = false;

            CreateAuthoredCollider<MeshCollider>("05-Unsupported");
            CreateAuthoredCollider<SphereCollider>("06-Overflow");

            var collector = new FluidColliderProxyCollector(_root.transform, capacity: 5);

            Assert.That(collector.ProxyCount, Is.EqualTo(5));
            Assert.That(collector.OverflowCount, Is.EqualTo(1));
            Assert.That(collector.Proxies[0].Shape, Is.EqualTo(FluidColliderShape.OrientedBox));
            for (int index = 1; index < 5; index++)
                Assert.That((uint)collector.Proxies[index].Shape, Is.EqualTo(uint.MaxValue));
        }

        [Test]
        public void InitiallyDisabledAndInactiveSupportedSlotsBecomeValidAtTheirOriginalIndices()
        {
            _root = new GameObject("ColliderRoot");
            FluidColliderAuthoring disabled = CreateAuthoredCollider<BoxCollider>("00-Disabled");
            disabled.enabled = false;
            FluidColliderAuthoring inactive = CreateAuthoredCollider<SphereCollider>("01-Inactive");
            inactive.gameObject.SetActive(false);
            var collector = new FluidColliderProxyCollector(_root.transform, capacity: 2);

            Assert.That(collector.ProxyCount, Is.EqualTo(2));
            Assert.That((uint)collector.Proxies[0].Shape, Is.EqualTo(uint.MaxValue));
            Assert.That((uint)collector.Proxies[1].Shape, Is.EqualTo(uint.MaxValue));

            disabled.enabled = true;
            Assert.That(collector.RefreshChangedProxies(), Is.True);
            Assert.That(collector.Proxies[0].Shape, Is.EqualTo(FluidColliderShape.OrientedBox));

            inactive.gameObject.SetActive(true);
            Assert.That(collector.RefreshChangedProxies(), Is.True);
            Assert.That(collector.Proxies[1].Shape, Is.EqualTo(FluidColliderShape.Sphere));
        }

        [Test]
        public void TriggerAndDisabledColliderBecomeValidInStableSlotsAfterExplicitDirtySignal()
        {
            _root = new GameObject("ColliderRoot");
            FluidColliderAuthoring trigger = CreateAuthoredCollider<CapsuleCollider>("00-Trigger");
            trigger.GetComponent<CapsuleCollider>().isTrigger = true;
            FluidColliderAuthoring colliderDisabled = CreateAuthoredCollider<SphereCollider>("01-DisabledCollider");
            colliderDisabled.GetComponent<SphereCollider>().enabled = false;
            var collector = new FluidColliderProxyCollector(_root.transform, capacity: 2);

            trigger.GetComponent<CapsuleCollider>().isTrigger = false;
            trigger.MarkDirty();
            colliderDisabled.GetComponent<SphereCollider>().enabled = true;
            colliderDisabled.MarkDirty();

            Assert.That(collector.RefreshChangedProxies(), Is.True);
            Assert.That(collector.Proxies[0].Shape, Is.EqualTo(FluidColliderShape.Capsule));
            Assert.That(collector.Proxies[1].Shape, Is.EqualTo(FluidColliderShape.Sphere));
        }

        [Test]
        public void DynamicTransformChangeAndExplicitMarkDirtyRebuildWithoutSteadyStateChanges()
        {
            _root = new GameObject("ColliderRoot");
            FluidColliderAuthoring dynamicAuthoring =
                CreateAuthoredCollider<BoxCollider>("00-Dynamic");
            SetPrivateField(dynamicAuthoring, "_isDynamic", true);
            FluidColliderAuthoring staticAuthoring =
                CreateAuthoredCollider<SphereCollider>("01-Static");
            var collector = new FluidColliderProxyCollector(_root.transform, capacity: 2);

            Assert.That(collector.RefreshChangedProxies(), Is.False);

            dynamicAuthoring.transform.position = new Vector3(2f, 0f, 0f);
            Assert.That(collector.RefreshChangedProxies(), Is.True);
            AssertVector(
                collector.Proxies[0].TransformWorldPoint(dynamicAuthoring.transform.position),
                Vector3.zero);
            Assert.That(collector.RefreshChangedProxies(), Is.False);

            staticAuthoring.transform.position = new Vector3(0f, 3f, 0f);
            Assert.That(collector.RefreshChangedProxies(), Is.False,
                "Static Proxy 的 Transform 变化不会每 Tick 自动重建。");
            staticAuthoring.MarkDirty();
            Assert.That(collector.RefreshChangedProxies(), Is.True);
            AssertVector(
                collector.Proxies[1].TransformWorldPoint(staticAuthoring.transform.position),
                Vector3.zero);
        }

        private FluidColliderAuthoring CreateAuthoredCollider<TCollider>(string name)
            where TCollider : Collider
        {
            var child = new GameObject(name);
            child.transform.SetParent(_root.transform, false);
            child.AddComponent<TCollider>();
            return child.AddComponent<FluidColliderAuthoring>();
        }

        private static void SetPrivateField<TValue>(
            FluidColliderAuthoring authoring,
            string fieldName,
            TValue value)
        {
            FieldInfo field = typeof(FluidColliderAuthoring).GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"缺少 Authoring 字段 {fieldName}。");
            field.SetValue(authoring, value);
        }

        private static void AssertVector(Vector3 actual, Vector3 expected)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(1e-4f));
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(1e-4f));
            Assert.That(actual.z, Is.EqualTo(expected.z).Within(1e-4f));
        }
    }
}
