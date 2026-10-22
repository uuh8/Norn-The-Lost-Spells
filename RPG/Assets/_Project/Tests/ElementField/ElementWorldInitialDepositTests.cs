using Game.Materials;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// 固定 Initial Deposit 的 Authoring→Request 转换、One-shot 语义和 Registry 边界。
    /// Chunk 中的实际扩散/反应由既有 ElementWorldWriteProcessor Tests 与 Play Mode Gate 覆盖。
    /// </summary>
    public sealed class ElementWorldInitialDepositTests
    {
        private GameObject _gameObject;
        private ElementWorldInitialDeposit _deposit;
        private RecordingWriteSink _sink;

        [SetUp]
        public void SetUp()
        {
            Assert.That(ElementRuntimeRegistry.ActiveSink, Is.Null,
                "上一个测试或场景 Runtime 没有释放 ElementRuntimeRegistry。");
            _gameObject = new GameObject("ElementWorldInitialDepositTests");
            _deposit = _gameObject.AddComponent<ElementWorldInitialDeposit>();
            _sink = new RecordingWriteSink();
        }

        [TearDown]
        public void TearDown()
        {
            ElementRuntimeRegistry.Unregister(_sink);
            Object.DestroyImmediate(_gameObject);
        }

        [Test]
        public void WaterConfigurationBuildsRequestAtProvidedWorldPosition()
        {
            Configure(MaterialId.Water, 320, 1.5f, true);
            var position = new Vector3(2f, 0.5f, -4f);

            bool built = _deposit.TryBuildRequest(position, out ElementWriteRequest request);

            Assert.That(built, Is.True);
            Assert.That(request.WorldPosition, Is.EqualTo(position));
            Assert.That(request.MaterialKind, Is.EqualTo(MaterialId.Water));
            Assert.That(request.TotalAmount, Is.EqualTo(320));
            Assert.That(request.Radius, Is.EqualTo(1.5f).Within(0.0001f));
            Assert.That(request.UseLinearFalloff, Is.True);
        }

        [Test]
        public void PoisonConfigurationBuildsRequestAndPreservesPoisonMaterial()
        {
            Configure(MaterialId.Poison, 8000, 1.2f, true);
            var position = new Vector3(3.34f, 0f, 2.96f);

            bool built = _deposit.TryBuildRequest(position, out ElementWriteRequest request);

            Assert.That(built, Is.True,
                "Poison 是已有 GPU Liquid Material，关卡 Initial Deposit 不应在进入 Runtime 前拒绝它。");
            Assert.That(request.WorldPosition, Is.EqualTo(position));
            Assert.That(request.MaterialKind, Is.EqualTo(MaterialId.Poison));
            Assert.That(request.TotalAmount, Is.EqualTo(8000));
            Assert.That(request.Radius, Is.EqualTo(1.2f).Within(0.0001f));
            Assert.That(request.UseLinearFalloff, Is.True);
        }

        [Test]
        public void StickyConfigurationBuildsRequestAndPreservesStickyMaterial()
        {
            Configure(MaterialId.Sticky, 1600, 0.75f, true);

            bool built = _deposit.TryBuildRequest(Vector3.one, out ElementWriteRequest request);

            Assert.That(built, Is.True);
            Assert.That(request.MaterialKind, Is.EqualTo(MaterialId.Sticky));
            Assert.That(request.TotalAmount, Is.EqualTo(1600));
        }

        [TestCase(MaterialId.Empty)]
        public void UnsupportedMaterialDoesNotBuildRequest(MaterialId materialKind)
        {
            Configure(materialKind, 220, 0.75f, true);

            Assert.That(_deposit.TryBuildRequest(Vector3.zero, out _), Is.False);
        }

        [TestCase(0)]
        [TestCase(-1)]
        public void NonPositiveAmountDoesNotBuildRequest(int amount)
        {
            Configure(MaterialId.Fire, amount, 0.5f, false);

            Assert.That(_deposit.TryBuildRequest(Vector3.zero, out _), Is.False);
        }

        [Test]
        public void SuccessfulDepositUsesTransformPositionAndSubmitsOnlyOnce()
        {
            Configure(MaterialId.Fire, 180, 0.5f, false);
            _gameObject.transform.position = new Vector3(3f, 1f, 7f);
            Assert.That(ElementRuntimeRegistry.TryRegister(_sink), Is.True);

            Assert.That(_deposit.TryQueueDeposit(), Is.True);
            Assert.That(_deposit.TryQueueDeposit(), Is.False);

            Assert.That(_deposit.HasSubmitted, Is.True);
            Assert.That(_sink.WriteCount, Is.EqualTo(1));
            Assert.That(_sink.LastRequest.WorldPosition, Is.EqualTo(_gameObject.transform.position));
            Assert.That(_sink.LastRequest.MaterialKind, Is.EqualTo(MaterialId.Fire));
        }

        [Test]
        public void MissingSinkDoesNotConsumeOneShotAndCanRetry()
        {
            Configure(MaterialId.Water, 220, 0.75f, true);
            LogAssert.Expect(
                LogType.Warning,
                "[ElementField] Initial Deposit 未提交：场景中没有可用的 Element Write Sink。请检查 ElementWorldRuntime 是否启用并完成初始化。");

            Assert.That(_deposit.TryQueueDeposit(), Is.False);
            Assert.That(_deposit.HasSubmitted, Is.False);

            Assert.That(ElementRuntimeRegistry.TryRegister(_sink), Is.True);
            Assert.That(_deposit.TryQueueDeposit(), Is.True);
            Assert.That(_sink.WriteCount, Is.EqualTo(1));
        }

        [Test]
        public void RejectedQueueDoesNotConsumeOneShot()
        {
            Configure(MaterialId.Fire, 180, 0.5f, true);
            _sink.AcceptWrites = false;
            Assert.That(ElementRuntimeRegistry.TryRegister(_sink), Is.True);
            LogAssert.Expect(
                LogType.Warning,
                "[ElementField] Initial Deposit 被 Element Runtime 拒绝。请检查 Runtime 初始化状态、Max Pending Writes 与 LastStats.RejectedWrites。");

            Assert.That(_deposit.TryQueueDeposit(), Is.False);
            Assert.That(_deposit.HasSubmitted, Is.False);
        }

        [Test]
        public void ExplicitResetQueuesExactlyOneAdditionalRequest()
        {
            Configure(MaterialId.Water, 220, 0.75f, true);
            Assert.That(ElementRuntimeRegistry.TryRegister(_sink), Is.True);
            Assert.That(_deposit.TryQueueDeposit(), Is.True);

            _deposit.ResetAndQueueDeposit();

            Assert.That(_deposit.HasSubmitted, Is.True);
            Assert.That(_sink.WriteCount, Is.EqualTo(2));
            Assert.That(_deposit.TryQueueDeposit(), Is.False);
        }

        private void Configure(
            MaterialId materialKind,
            int amount,
            float radius,
            bool useLinearFalloff)
        {
            SetPrivateField("_materialKind", materialKind);
            SetPrivateField("_totalAmount", amount);
            SetPrivateField("_radius", radius);
            SetPrivateField("_useLinearFalloff", useLinearFalloff);
        }

        private void SetPrivateField<T>(string fieldName, T value)
        {
            FieldInfo field = typeof(ElementWorldInitialDeposit).GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"缺少预期的序列化字段 {fieldName}。");
            field.SetValue(_deposit, value);
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
