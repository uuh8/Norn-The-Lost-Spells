using System;
using System.Reflection;
using Game.Materials;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.ElementField.Tests
{
    /// <summary>真实CPU液体与落水运动样本；只替换输入时钟，不复制Solver算法或伪造返回值。</summary>
    public sealed class LiquidAuthoritySampleTimeTests
    {
        private GameObject _host;
        private LiquidWorldRuntime _runtime;
        private LiquidWorldProfile _profile;
        private LiquidBehaviorProfile _water;
        private MethodInfo _step;
        private readonly Vector3Int _cell = new Vector3Int(0, 3, 0);
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        [SetUp] public void Prepare()
        {
            _step = typeof(LiquidWorldRuntime).GetMethod("Step", new[] { typeof(float), typeof(float) });
            Assert.That(_step, Is.Not.Null, "Authority需要显式材料样本时钟入口，不能隐式取Time.time。");
            _host = new GameObject("Explicit liquid sample clock"); _host.SetActive(false);
            _runtime = _host.AddComponent<LiquidWorldRuntime>();
            _profile = ScriptableObject.CreateInstance<LiquidWorldProfile>();
            _water = ScriptableObject.CreateInstance<LiquidBehaviorProfile>();
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":8,\"_maximumActiveCells\":64,\"_maximumCellsPerTick\":64,\"_resolutionScale\":1,\"_unitVolumeMultiplier\":1,\"_enableAutoStableDetection\":false}", _profile);
            typeof(LiquidWorldProfile).GetField("_materials", Private).SetValue(_profile, new[] { _water });
            typeof(LiquidWorldRuntime).GetField("_profile", Private).SetValue(_runtime, _profile);
            Assert.True(_runtime.TryInitialize(new Vector3(30000, 2000, 30000), .25f, 4, _host.AddComponent<ElementFieldSolidBaker>()));
            Assert.True(_runtime.TryAddInitial(_cell, MaterialId.Water, 64, true));
        }
        [TearDown] public void Cleanup()
        { if (_host != null) Object.DestroyImmediate(_host); if (_profile != null) Object.DestroyImmediate(_profile); if (_water != null) Object.DestroyImmediate(_water); }

        [Test] public void ExplicitStepPublishesAuthorityTimeInRealFallingMotion()
        {
            _step.Invoke(_runtime, new object[] { .1f, 42f });
            Assert.True(_runtime.TryGetFallingMotion(_cell, MaterialId.Water, out var motion));
            Assert.That(motion.SampleTime, Is.EqualTo(42f));
            Assert.That(_runtime.TotalQuantity, Is.EqualTo(64UL));
        }
        [Test] public void ExistingSinglePlayerStepPreservesUnityPresentationClock()
        {
            float expected = Time.time; _runtime.Step(.1f);
            Assert.True(_runtime.TryGetFallingMotion(_cell, MaterialId.Water, out var motion));
            Assert.That(motion.SampleTime, Is.EqualTo(expected));
        }
        [TestCase(float.NaN, .1f)]
        [TestCase(float.PositiveInfinity, .1f)]
        [TestCase(-.1f, .1f)]
        [TestCase(.1f, float.NaN)]
        [TestCase(.1f, float.PositiveInfinity)]
        [TestCase(.1f, -1f)]
        public void InvalidExplicitTimeFailsBeforeAnyQuantityOrMotionMutation(float delta, float sampleTime)
        {
            var error = Assert.Throws<TargetInvocationException>(() => _step.Invoke(_runtime, new object[] { delta, sampleTime }));
            Assert.That(error.InnerException, Is.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(_runtime.TotalQuantity, Is.EqualTo(64UL));
            Assert.False(_runtime.TryGetFallingMotion(_cell, MaterialId.Water, out _));
            _step.Invoke(_runtime, new object[] { .1f, .1f });
            Assert.True(_runtime.TryGetFallingMotion(_cell, MaterialId.Water, out var accepted));
            Assert.That(accepted.SampleTime, Is.EqualTo(.1f));
        }
    }
}
