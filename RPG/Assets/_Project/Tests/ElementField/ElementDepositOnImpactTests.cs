using Game.Materials;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Utils;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// Projectile 的真实 Collision 与事件订阅留给 Play Mode Gate 验证；这里先固定
    /// “Inspector 配置如何转换成值类型 ElementWriteRequest”的规则与非法输入边界。
    /// </summary>
    public sealed class ElementDepositOnImpactTests
    {
        private GameObject _gameObject;
        private ElementDepositOnImpact _deposit;

        [SetUp]
        public void SetUp()
        {
            _gameObject = new GameObject("ElementDepositOnImpactTests");
            _deposit = _gameObject.AddComponent<ElementDepositOnImpact>();
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_gameObject);
        }

        [Test]
        public void WaterConfigBuildsWaterRequest()
        {
            Configure(MaterialId.Water, totalAmount: 220, radius: 0.75f, linearFalloff: true);
            var worldPosition = new Vector3(1.25f, 2.5f, -3.75f);

            bool built = _deposit.TryBuildRequest(worldPosition, out ElementWriteRequest request);

            Assert.That(built, Is.True);
            Assert.That(request.WorldPosition, Is.EqualTo(worldPosition));
            Assert.That(request.MaterialKind, Is.EqualTo(MaterialId.Water));
            Assert.That(request.TotalAmount, Is.EqualTo(220));
            Assert.That(request.Radius, Is.EqualTo(0.75f).Within(0.0001f));
            Assert.That(request.UseLinearFalloff, Is.True);
            Assert.That(request.InitialVelocity, Is.EqualTo(Vector3.zero),
                "旧构造/普通 Source 必须保持零初速度，避免改变 Fire 与 Persistent Source。");
        }

        [Test]
        public void ImpactDirectionBuildsNormalizedVelocityAtConfiguredSpeed()
        {
            Configure(MaterialId.Water, totalAmount: 220, radius: 0.75f, linearFalloff: true);
            SetPrivateField("_fluidInitialSpeed", 6f);

            bool built = _deposit.TryBuildImpactRequest(
                Vector3.one,
                new Vector3(3f, 0f, 4f),
                out ElementWriteRequest request);

            Assert.That(built, Is.True);
            Assert.That(request.InitialVelocity.x, Is.EqualTo(3.6f).Within(0.0001f));
            Assert.That(request.InitialVelocity.y, Is.Zero.Within(0.0001f));
            Assert.That(request.InitialVelocity.z, Is.EqualTo(4.8f).Within(0.0001f));
        }

        [Test]
        public void StickyImpactInheritsConfiguredProjectileVelocity()
        {
            Configure(MaterialId.Sticky, totalAmount: 1600, radius: 0.75f, linearFalloff: true);
            SetPrivateField("_fluidInitialSpeed", 4f);

            Assert.That(_deposit.TryBuildImpactRequest(
                Vector3.zero,
                Vector3.forward,
                Vector3.up,
                out ElementWriteRequest request), Is.True);
            Assert.That(request.MaterialKind, Is.EqualTo(MaterialId.Sticky));
            Assert.That(request.InitialVelocity, Is.EqualTo(Vector3.forward * 4f));
        }

        [Test]
        public void ExplicitSurfaceNormalIsPreservedAlongsideImpactVelocity()
        {
            Configure(MaterialId.Water, totalAmount: 220, radius: 0.75f, linearFalloff: true);
            SetPrivateField("_fluidInitialSpeed", 6f);

            Assert.That(_deposit.TryBuildImpactRequest(
                Vector3.zero,
                Vector3.right,
                Vector3.up * 2f,
                out ElementWriteRequest request), Is.True);
            Assert.That(request.InitialVelocity, Is.EqualTo(Vector3.right * 6f));
            Assert.That(request.SurfaceNormal, Is.EqualTo(Vector3.up * 2f));
        }

        [Test]
        public void LegacyImpactOverloadUsesOppositeIncomingDirectionAsSurfaceNormal()
        {
            Configure(MaterialId.Water, 220, 0.75f, true);

            Assert.That(_deposit.TryBuildImpactRequest(
                Vector3.zero,
                new Vector3(0f, -3f, 4f),
                out ElementWriteRequest request), Is.True);
            Assert.That(request.SurfaceNormal, Is.EqualTo(new Vector3(0f, 0.6f, -0.8f)).Using(Vector3ComparerWithEqualsOperator.Instance));
        }

        [Test]
        public void MissingSurfaceNormalAndIncomingDirectionRejectsImpactRequest()
        {
            Configure(MaterialId.Water, 220, 0.75f, true);

            Assert.That(_deposit.TryBuildImpactRequest(
                Vector3.zero,
                Vector3.zero,
                Vector3.zero,
                out ElementWriteRequest request), Is.False);
            Assert.That(request, Is.EqualTo(default(ElementWriteRequest)));
        }

        [Test]
        public void FireImpactKeepsZeroVelocityEvenWhenFluidSpeedIsConfigured()
        {
            Configure(MaterialId.Fire, totalAmount: 180, radius: 0.5f, linearFalloff: false);
            SetPrivateField("_fluidInitialSpeed", 6f);

            Assert.That(_deposit.TryBuildImpactRequest(
                Vector3.zero,
                Vector3.right,
                out ElementWriteRequest request), Is.True);
            Assert.That(request.InitialVelocity, Is.EqualTo(Vector3.zero));
        }

        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        public void NonFiniteImpactDirectionDoesNotBuildOrEnqueue(float invalidDirection)
        {
            Configure(MaterialId.Water, 220, 0.75f, true);
            SetPrivateField("_fluidInitialSpeed", 6f);
            var sink = new RecordingWriteSink();
            Assert.That(ElementRuntimeRegistry.TryRegister(sink), Is.True);
            try
            {
                Assert.That(_deposit.TryBuildImpactRequest(
                    Vector3.zero,
                    new Vector3(invalidDirection, 0f, 0f),
                    out _), Is.False);
                Assert.That(sink.WriteCount, Is.Zero);
            }
            finally
            {
                ElementRuntimeRegistry.Unregister(sink);
            }
        }

        [Test]
        public void NonFiniteSpeedOrPositionDoesNotBuildOrEnqueue()
        {
            Configure(MaterialId.Water, 220, 0.75f, true);
            SetPrivateField("_fluidInitialSpeed", float.PositiveInfinity);
            Assert.That(_deposit.TryBuildImpactRequest(Vector3.zero, Vector3.right, out _), Is.False);

            SetPrivateField("_fluidInitialSpeed", 6f);
            Assert.That(_deposit.TryBuildImpactRequest(
                new Vector3(float.PositiveInfinity, 0f, 0f), Vector3.right, out _), Is.False);
        }

        [TestCase(0)]
        [TestCase(-5)]
        public void NonPositiveAmountDoesNotBuildRequest(int invalidAmount)
        {
            Configure(MaterialId.Fire, invalidAmount, radius: 0.5f, linearFalloff: false);

            bool built = _deposit.TryBuildRequest(Vector3.one, out ElementWriteRequest request);

            Assert.That(built, Is.False);
            Assert.That(request.TotalAmount, Is.Zero);
        }

        [Test]
        public void NegativeRadiusIsClampedToZero()
        {
            Configure(MaterialId.Fire, totalAmount: 180, radius: -2f, linearFalloff: true);

            bool built = _deposit.TryBuildRequest(Vector3.zero, out ElementWriteRequest request);

            Assert.That(built, Is.True);
            Assert.That(request.Radius, Is.Zero);
        }

        [Test]
        public void DepositWithoutActiveFieldFailsSafely()
        {
            Configure(MaterialId.Water, totalAmount: 220, radius: 0.75f, linearFalloff: true);

            Assert.That(ElementRuntimeRegistry.ActiveSink, Is.Null);
            LogAssert.Expect(
                LogType.Warning,
                "[ElementField] 元素沉积已忽略：当前场景中不存在可用的 Element Write Sink。");
            Assert.That(_deposit.TryDepositAt(Vector3.zero), Is.False);
        }

        [Test]
        public void DepositUsesRegisteredWriteSinkWithoutKnowingRuntimeType()
        {
            Configure(MaterialId.Fire, totalAmount: 180, radius: 0.5f, linearFalloff: false);
            var sink = new RecordingWriteSink();
            Assert.That(ElementRuntimeRegistry.TryRegister(sink), Is.True);

            try
            {
                Assert.That(_deposit.TryDepositAt(Vector3.one), Is.True);
                Assert.That(sink.WriteCount, Is.EqualTo(1));
                Assert.That(sink.LastRequest.MaterialKind, Is.EqualTo(MaterialId.Fire));
                Assert.That(sink.LastRequest.WorldPosition, Is.EqualTo(Vector3.one));
            }
            finally
            {
                ElementRuntimeRegistry.Unregister(sink);
            }
        }

        private void Configure(
            MaterialId materialKind,
            int totalAmount,
            float radius,
            bool linearFalloff)
        {
            SetPrivateField("_materialKind", materialKind);
            SetPrivateField("_totalAmount", totalAmount);
            SetPrivateField("_radius", radius);
            SetPrivateField("_useLinearFalloff", linearFalloff);
        }

        private void SetPrivateField<T>(string fieldName, T value)
        {
            FieldInfo field = typeof(ElementDepositOnImpact).GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"缺少预期的序列化字段 {fieldName}。");
            field.SetValue(_deposit, value);
        }

        private sealed class RecordingWriteSink : IElementWriteSink
        {
            public int WriteCount { get; private set; }
            public ElementWriteRequest LastRequest { get; private set; }

            public bool TryEnqueueWrite(in ElementWriteRequest request)
            {
                LastRequest = request;
                WriteCount++;
                return true;
            }
        }
    }
}
