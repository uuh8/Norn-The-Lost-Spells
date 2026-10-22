using Game.Materials;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// 验证 Persistent Fire Source 的 Fire-only Request、重复 Pulse 与 Registry 失败边界。
    /// Coroutine 的真实时间推进留给 Play Mode Gate；EditMode Test 通过 TryPulse 固定每次离散写入。
    /// </summary>
    public sealed class ElementWorldPersistentFireSourceTests
    {
        private GameObject _gameObject;
        private ElementWorldPersistentFireSource _source;
        private RecordingWriteSink _sink;

        [SetUp]
        public void SetUp()
        {
            Assert.That(ElementRuntimeRegistry.ActiveSink, Is.Null,
                "上一个测试或场景 Runtime 没有释放 ElementRuntimeRegistry。");
            _gameObject = new GameObject("ElementWorldPersistentFireSourceTests");
            _source = _gameObject.AddComponent<ElementWorldPersistentFireSource>();
            _sink = new RecordingWriteSink();
        }

        [TearDown]
        public void TearDown()
        {
            ElementRuntimeRegistry.Unregister(_sink);
            Object.DestroyImmediate(_gameObject);
        }

        [Test]
        public void ConfigurationAlwaysBuildsFireRequestAtProvidedPosition()
        {
            Configure(pulseAmount: 512, radius: 0.35f, linearFalloff: true);
            var position = new Vector3(2f, 0.5f, -4f);

            bool built = _source.TryBuildPulseRequest(
                position,
                out ElementWriteRequest request);

            Assert.That(built, Is.True);
            Assert.That(request.WorldPosition, Is.EqualTo(position));
            Assert.That(request.MaterialKind, Is.EqualTo(MaterialId.Fire));
            Assert.That(request.TotalAmount, Is.EqualTo(512));
            Assert.That(request.Radius, Is.EqualTo(0.35f).Within(0.0001f));
            Assert.That(request.UseLinearFalloff, Is.True);
        }

        [TestCase(0)]
        [TestCase(-1)]
        public void NonPositivePulseAmountDoesNotBuildRequest(int amount)
        {
            Configure(amount, radius: 0.35f, linearFalloff: true);

            Assert.That(_source.TryBuildPulseRequest(Vector3.zero, out _), Is.False);
        }

        [Test]
        public void NegativeRadiusIsClampedToZeroAtRuntimeBoundary()
        {
            Configure(pulseAmount: 512, radius: -1f, linearFalloff: false);

            Assert.That(
                _source.TryBuildPulseRequest(Vector3.zero, out ElementWriteRequest request),
                Is.True);
            Assert.That(request.Radius, Is.Zero);
        }

        [Test]
        public void RepeatedPulseSubmitsRepeatedFireRequests()
        {
            Configure(pulseAmount: 512, radius: 0.35f, linearFalloff: true);
            _gameObject.transform.position = new Vector3(3f, 1f, 7f);
            Assert.That(ElementRuntimeRegistry.TryRegister(_sink), Is.True);

            Assert.That(_source.TryPulse(), Is.True);
            Assert.That(_source.TryPulse(), Is.True);

            Assert.That(_sink.WriteCount, Is.EqualTo(2));
            Assert.That(_source.SuccessfulPulseCount, Is.EqualTo(2));
            Assert.That(_source.RejectedPulseCount, Is.Zero);
            Assert.That(_sink.LastRequest.WorldPosition, Is.EqualTo(_gameObject.transform.position));
            Assert.That(_sink.LastRequest.MaterialKind, Is.EqualTo(MaterialId.Fire));
        }

        [Test]
        public void MissingSinkDoesNotPreventLaterPulseRetry()
        {
            Configure(pulseAmount: 512, radius: 0.35f, linearFalloff: true);
            LogAssert.Expect(
                LogType.Warning,
                "[ElementField] Persistent Fire Pulse 未提交：场景中没有可用的 Element Write Sink。");

            Assert.That(_source.TryPulse(), Is.False);
            Assert.That(_source.RejectedPulseCount, Is.EqualTo(1));

            Assert.That(ElementRuntimeRegistry.TryRegister(_sink), Is.True);
            Assert.That(_source.TryPulse(), Is.True);
            Assert.That(_sink.WriteCount, Is.EqualTo(1));
            Assert.That(_source.SuccessfulPulseCount, Is.EqualTo(1));
        }

        [Test]
        public void RejectedQueueDoesNotPreventLaterPulseRetry()
        {
            Configure(pulseAmount: 512, radius: 0.35f, linearFalloff: true);
            _sink.AcceptWrites = false;
            Assert.That(ElementRuntimeRegistry.TryRegister(_sink), Is.True);
            LogAssert.Expect(
                LogType.Warning,
                "[ElementField] Persistent Fire Pulse 被 Element Runtime 拒绝。请检查 Runtime 初始化、Max Pending Writes 与 LastStats.RejectedWrites。");

            Assert.That(_source.TryPulse(), Is.False);
            Assert.That(_source.RejectedPulseCount, Is.EqualTo(1));

            _sink.AcceptWrites = true;
            Assert.That(_source.TryPulse(), Is.True);
            Assert.That(_sink.WriteCount, Is.EqualTo(1));
        }

        [Test]
        [TestCase(0f, 0.25f)]
        [TestCase(0.01f, 0.25f)]
        [TestCase(1.5f, 1.5f)]
        public void PulseIntervalHasDefensiveMinimum(float configured, float expected)
        {
            SetPrivateField("_pulseInterval", configured);

            Assert.That(_source.PulseIntervalSeconds, Is.EqualTo(expected).Within(0.0001f));
        }

        private void Configure(int pulseAmount, float radius, bool linearFalloff)
        {
            SetPrivateField("_pulseAmount", pulseAmount);
            SetPrivateField("_radius", radius);
            SetPrivateField("_useLinearFalloff", linearFalloff);
        }

        private void SetPrivateField<T>(string fieldName, T value)
        {
            FieldInfo field = typeof(ElementWorldPersistentFireSource).GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"缺少预期的序列化字段 {fieldName}。");
            field.SetValue(_source, value);
        }

        private sealed class RecordingWriteSink : IElementWriteSink
        {
            public bool AcceptWrites { get; set; } = true;
            public int WriteCount { get; private set; }
            public ElementWriteRequest LastRequest { get; private set; }

            public bool TryEnqueueWrite(in ElementWriteRequest request)
            {
                if (!AcceptWrites)
                    return false;

                LastRequest = request;
                WriteCount++;
                return true;
            }
        }
    }
}
