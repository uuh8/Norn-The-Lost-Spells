using Game.Materials;
using NUnit.Framework;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// Registry Tests 只验证“当前场景唯一写入端”的所有权协议，不依赖具体 MonoBehaviour。
    /// 这样固定实验场和稀疏世界可以共享同一入口，Projectile 也不必识别两种 Runtime 类型。
    /// </summary>
    public sealed class ElementRuntimeRegistryTests
    {
        private FakeWriteSink _first;
        private FakeWriteSink _second;

        [SetUp]
        public void SetUp()
        {
            _first = new FakeWriteSink();
            _second = new FakeWriteSink();
            Assert.That(ElementRuntimeRegistry.ActiveSink, Is.Null,
                "上一个测试或场景 Runtime 没有正确释放 Registry 所有权。");
        }

        [TearDown]
        public void TearDown()
        {
            ElementRuntimeRegistry.Unregister(_first);
            ElementRuntimeRegistry.Unregister(_second);
        }

        [Test]
        public void FirstSinkRegistersAndReceivesWrite()
        {
            var request = new ElementWriteRequest(
                UnityEngine.Vector3.one,
                MaterialId.Water,
                totalAmount: 12,
                radius: 0f,
                useLinearFalloff: false);

            Assert.That(ElementRuntimeRegistry.TryRegister(_first), Is.True);
            Assert.That(ElementRuntimeRegistry.ActiveSink, Is.SameAs(_first));
            Assert.That(ElementRuntimeRegistry.TryEnqueueWrite(in request), Is.True);
            Assert.That(_first.WriteCount, Is.EqualTo(1));
            Assert.That(_first.LastRequest.TotalAmount, Is.EqualTo(12));
        }

        [Test]
        public void SecondDifferentSinkIsRejected()
        {
            Assert.That(ElementRuntimeRegistry.TryRegister(_first), Is.True);

            Assert.That(ElementRuntimeRegistry.TryRegister(_second), Is.False);
            Assert.That(ElementRuntimeRegistry.ActiveSink, Is.SameAs(_first));
        }

        [Test]
        public void UnregisterActiveSinkReleasesSlotForNextRuntime()
        {
            Assert.That(ElementRuntimeRegistry.TryRegister(_first), Is.True);

            ElementRuntimeRegistry.Unregister(_first);

            Assert.That(ElementRuntimeRegistry.ActiveSink, Is.Null);
            Assert.That(ElementRuntimeRegistry.TryRegister(_second), Is.True);
            Assert.That(ElementRuntimeRegistry.ActiveSink, Is.SameAs(_second));
        }

        [Test]
        public void EnqueueWithoutRegisteredSinkFailsSafely()
        {
            var request = new ElementWriteRequest(
                UnityEngine.Vector3.zero,
                MaterialId.Fire,
                totalAmount: 8,
                radius: 0f,
                useLinearFalloff: false);

            Assert.That(ElementRuntimeRegistry.TryEnqueueWrite(in request), Is.False);
        }

        private sealed class FakeWriteSink : IElementWriteSink
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
