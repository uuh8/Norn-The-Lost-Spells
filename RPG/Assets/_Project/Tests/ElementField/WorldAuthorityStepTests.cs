using System;
using System.Reflection;
using Game.Combat;
using Game.Core;
using Game.Materials;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// 只验证 World 时钟的唯一入口；空 CPU Store 是有意的隔离，不把这些断言当作完整材料复制验收。
    /// 使用真实累加器与真实 SimulateOneTick，因此能发现把外部 30Hz 误当材料 30Hz 的接线错误。
    /// </summary>
    public sealed class WorldAuthorityStepTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameObject _host;
        private ElementWorldRuntime _world;
        private ElementWorldProfile _profile;

        [SetUp]
        public void SetUp()
        {
            _host = new GameObject("World authority clock test");
            _host.SetActive(false);
            _world = _host.AddComponent<ElementWorldRuntime>();
            _profile = ScriptableObject.CreateInstance<ElementWorldProfile>();
            JsonUtility.FromJsonOverwrite("{\"_chunkSize\":2,\"_maximumResidentChunks\":8,\"_tickRate\":10,\"_enableWorldActivityExpansion\":false}", _profile);
            var reaction = AssetDatabase.LoadAssetAtPath<ElementReactionProfile>("Assets/_Project/ScriptableObjects/Combat/Status/ElementReactionProfile_Default.asset");
            Assert.That(reaction, Is.Not.Null);
            typeof(ElementWorldProfile).GetField("_reactionProfile", Private).SetValue(_profile, reaction);
            Set("_profile", _profile);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_host);
            Object.DestroyImmediate(_profile);
        }

        [Test]
        public void ThirtyServerStepsFeedExistingTenHertzAccumulatorOnce()
        {
            InitializeClock("Authority");
            Type contract = typeof(GameLog).Assembly.GetType("Game.Core.IWorldAuthoritySimulation");
            Assert.That(contract, Is.Not.Null, "Core 应公开中立材料模拟合同。");
            Assert.That(contract.IsInstanceOfType(_world), Is.True);
            for (uint tick = 1; tick <= 30; tick++) Step(1f / 30f, tick);
            Assert.That(_world.WorldTick, Is.EqualTo(10));
        }

        [Test]
        public void AuthorityIgnoresAutomaticAndTestTickEntryPoints()
        {
            InitializeClock("Authority");
            Invoke("Update");
            Assert.That(_world.TickForTests(1f), Is.Zero);
            Assert.That(_world.WorldTick, Is.Zero);
            Step(.1f, 1);
            Assert.That(_world.WorldTick, Is.EqualTo(1));
        }

        [Test]
        public void AutomaticSinglePlayerEntriesDoNotClearAuthorityStepDiagnostics()
        {
            InitializeClock("Authority");
            Step(.1f, 1);
            Assert.That(_world.LastFrameTickCount, Is.EqualTo(1));
            Invoke("Update");
            Assert.That(_world.LastFrameTickCount, Is.EqualTo(1));
            Assert.That(_world.TickForTests(.1f), Is.Zero);
            Assert.That(_world.LastFrameTickCount, Is.EqualTo(1));
            Step(.1f, 1);
            Assert.That(_world.LastFrameTickCount, Is.Zero, "显式 Authority 调用自身仍可汇报该调用未推进。");
        }

        [Test]
        public void DuplicateBackwardAndZeroTicksCannotAdvanceWorld()
        {
            InitializeClock("Authority");
            Step(.1f, 4);
            Step(.1f, 4);
            Step(.1f, 3);
            Step(.1f, 0);
            Assert.That(_world.WorldTick, Is.EqualTo(1));
            Step(.1f, 5);
            Assert.That(_world.WorldTick, Is.EqualTo(2));
        }

        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(-.1f)]
        [TestCase(0f)]
        public void InvalidDeltaDoesNotPoisonSameServerTick(float delta)
        {
            InitializeClock("Authority");
            Assert.DoesNotThrow(() => Step(delta, 1));
            Step(.1f, 1);
            Assert.That(_world.WorldTick, Is.EqualTo(1));
        }

        [Test]
        public void FrozenStepsAreConsumedWithoutTimeDebtOrReplay()
        {
            InitializeClock("Authority");
            Step(.05f, 1);
            _world.SetSimulationSuspended(true);
            Step(30f, 2);
            _world.SetSimulationSuspended(false);
            Step(.1f, 2);
            Assert.That(_world.WorldTick, Is.Zero, "冻结期间的 tick 不可解冻后重放。");
            Step(.05f, 3);
            Assert.That(_world.WorldTick, Is.EqualTo(1), "冻结前合法不足一帧的余数可以保留，冻结时长不可入账。");
        }

        [Test]
        public void SynchronousReactionFreezeStopsRemainingCatchUpTicks()
        {
            InitializeClock("Authority");
            int reactions = 0;
            Action<ElementReactionEvent> freeze = reaction => { reactions++; _world.SetSimulationSuspended(true); };
            EventBus<ElementReactionEvent>.Subscribe(freeze);
            try
            {
                // Store 初始化回调是现有同步接缝：在首个 Tick 的真实写入内投递反应伤害命令，
                // 经过正式 ApplyWorldReactionDamage→EventBus 路径冻结，不为 Runtime 新增测试钩子。
                // 本例验证跨 Tick 的停止规则；毒液/Fire 如何生成命令由反应系统测试独立覆盖。
                MethodInfo apply = typeof(ElementWorldRuntime).GetMethod("ApplyWorldReactionDamage", Private);
                var damage = new WorldReactionDamageCommand(ElementReactionId.ToxicCombustion, Vector3.zero, 1, .1f);
                Set("_store", new ElementWorldStore(2, 8, chunk => apply.Invoke(_world, new object[] { damage })));
                var writes = new ElementWriteQueue(8);
                var request = new ElementWriteRequest(Vector3.zero, MaterialId.Fire, 1, 0, false);
                Assert.That(writes.TryEnqueue(in request), Is.True);
                Set("_writeQueue", writes);
                Step(.31f, 1);
                Assert.That(reactions, Is.EqualTo(1), "确认同步事件实际执行，避免空世界假通过。");
                Assert.That(_world.WorldTick, Is.EqualTo(1), "首个 Tick 内冻结后不得执行已经预算的其他补步。");
                Assert.That(_world.LastFrameTickCount, Is.EqualTo(1), "诊断必须汇报真正执行数。");
                _world.SetSimulationSuspended(false);
                Step(.09f, 2);
                Assert.That(_world.WorldTick, Is.EqualTo(2), "未执行的整 Tick 预算不得变成解冻债务。");
            }
            finally { EventBus<ElementReactionEvent>.Unsubscribe(freeze); }
        }

        [TestCase("SinglePlayer")]
        [TestCase("Authority")]
        public void SimulationProgressFlagCoversSynchronousTickAndReleasesAfterCommit(string mode)
        {
            InitializeClock(mode);
            bool observed = false;
            PrepareWriteCallback(() => observed = ReadStepInProgress());
            Assert.That(ReadStepInProgress(), Is.False);
            if (mode == "Authority") Step(.1f, 1); else _world.TickForTests(.1f);
            Assert.That(observed, Is.True, "同步写入回调必须观察到尚未提交，Source 不得导出半份材料状态。");
            Assert.That(ReadStepInProgress(), Is.False);
            Assert.That(ReadFaulted(), Is.False, "正常提交不能误置失败锁存。");
        }

        [TestCase("SinglePlayer")]
        [TestCase("Authority")]
        public void SimulationProgressFlagIsReleasedWhenSynchronousTickThrows(string mode)
        {
            InitializeClock(mode);
            PrepareWriteCallback(() => throw new InvalidOperationException("Intentional tick callback failure"));
            if (mode == "Authority") Assert.Throws<InvalidOperationException>(() => Step(.1f, 1));
            else Assert.Throws<InvalidOperationException>(() => _world.TickForTests(.1f));
            Assert.That(ReadStepInProgress(), Is.False, "异常路径也必须 finally 释放，不能永久锁住 Source。");
        }

        [TestCase("SinglePlayer")]
        [TestCase("Authority")]
        public void PartiallyExecutedTickFaultsWorldAndRejectsEveryFutureClockEntry(string mode)
        {
            InitializeClock(mode);
            int callbacks = 0;
            PrepareWriteCallback(() => { callbacks++; throw new InvalidOperationException("Intentional partial tick failure"); });
            if (mode == "Authority") Assert.Throws<InvalidOperationException>(() => Step(.1f, 1));
            else Assert.Throws<InvalidOperationException>(() => _world.TickForTests(.1f));
            Assert.That(_world.WorldTick, Is.EqualTo(1), "异常之前真实 Tick 已推进，finally 释放不能伪称回滚。");
            Assert.That(ReadFaulted(), Is.True, "必须锁存不完整提交，阻止 Source 将半份世界发布为新快照。");
            Type contract = typeof(GameLog).Assembly.GetType("Game.Core.IWorldAuthoritySimulation");
            Assert.That(contract, Is.Not.Null);
            Assert.That(contract.GetProperty("IsSimulationFaulted"), Is.Not.Null, "Core 装配层也必须能观察失败状态。");
            Assert.That(ReadStepInProgress(), Is.False);
            Assert.DoesNotThrow(() => Step(.1f, 2));
            Assert.That(_world.TickForTests(.1f), Is.Zero);
            Invoke("Update");
            Assert.That(_world.WorldTick, Is.EqualTo(1));
            Assert.That(callbacks, Is.EqualTo(1), "失败后不能重试同一个已经部分执行的世界。");
        }

        [Test]
        public void SinglePlayerRetainsTestClockButDoesNotAcceptAuthoritySteps()
        {
            InitializeClock("SinglePlayer");
            Step(.1f, 1);
            Assert.That(_world.WorldTick, Is.Zero);
            Assert.That(_world.TickForTests(.1f), Is.EqualTo(1));
        }

        [Test]
        public void InitializedWorldCannotChangeAuthorityOwner()
        {
            InitializeClock("Authority");
            Assert.DoesNotThrow(() => Configure("Authority"));
            Assert.Throws<InvalidOperationException>(() => Configure("Replica"));
            Assert.Throws<ArgumentOutOfRangeException>(() => Configure("99"));
        }

        [Test]
        public void ReplicaLifecycleDoesNotCreateAuthorityOrRegisterWriteSink()
        {
            Configure("Replica");
            Invoke("Awake"); Invoke("OnEnable");
            Step(.1f, 1);
            Assert.That(_world.TickForTests(.1f), Is.Zero);
            Assert.That(_world.IsInitialized, Is.False);
            Assert.That(ElementRuntimeRegistry.ActiveSink, Is.Not.SameAs(_world));
            Assert.That(typeof(ElementWorldRuntime).GetField("_store", Private).GetValue(_world), Is.Null);
        }

        [TestCase(WaterSimulationMode.GpuPbf)]
        [TestCase(WaterSimulationMode.LegacyCell)]
        public void AuthorityRejectsNonGridConfigurationBeforeHardwareFallback(WaterSimulationMode waterMode)
        {
            JsonUtility.FromJsonOverwrite("{\"_waterSimulationMode\":" + (int)waterMode + "}", _profile);
            Configure("Authority");
            LogAssert.Expect(LogType.Error, "[ElementField] Authority requires an explicit CPU GridLiquid profile; hardware fallback is not authority configuration.");
            Assert.That(Invoke("TryInitialize"), Is.False);
            Assert.That(_world.IsInitialized, Is.False);
        }

        private void InitializeClock(string mode)
        {
            Configure(mode);
            Set("_store", new ElementWorldStore(2, 8));
            Set("_writeQueue", new ElementWriteQueue(8));
            var catalog = AssetDatabase.LoadAssetAtPath<MaterialCatalog>("Assets/_Project/ScriptableObjects/Materials/MaterialCatalog_Default.asset");
            var bindings = AssetDatabase.LoadAssetAtPath<MaterialReactionBindingProfile>("Assets/_Project/ScriptableObjects/Combat/Reactions/MaterialReactionBindings_Default.asset");
            Set("_simulator", new ElementWorldSimulator(8, bindings.CreateSnapshot(catalog.CreateSnapshot())));
            Set("_activeChunks", new ElementWorldChunk[8]);
            Set("_reclaimKeys", new ElementChunkKey[8]);
            Set("_settings", _profile.CreateSimulationSettings());
            Set("_tickInterval", .1f);
            Type accumulator = typeof(ElementWorldRuntime).Assembly.GetType("Game.ElementField.ElementFieldTickAccumulator");
            Set("_tickAccumulator", Activator.CreateInstance(accumulator, 10f, 4));
            typeof(ElementWorldRuntime).GetProperty("IsInitialized").SetValue(_world, true);
        }

        private void Set(string field, object value) => typeof(ElementWorldRuntime).GetField(field, Private).SetValue(_world, value);
        private object Invoke(string method) => typeof(ElementWorldRuntime).GetMethod(method, Private).Invoke(_world, null);

        private bool ReadStepInProgress()
        {
            PropertyInfo property = typeof(ElementWorldRuntime).GetProperty("IsWorldStepInProgress", Private);
            Assert.That(property, Is.Not.Null, "缺少跨所有时钟入口的材料提交边界。");
            return (bool)property.GetValue(_world);
        }

        private bool ReadFaulted()
        {
            PropertyInfo property = typeof(ElementWorldRuntime).GetProperty("IsSimulationFaulted", BindingFlags.Public | BindingFlags.Instance);
            Assert.That(property, Is.Not.Null, "缺少部分执行失败锁存；需要销毁并重建 World 才能恢复。");
            return (bool)property.GetValue(_world);
        }

        private void PrepareWriteCallback(Action callback)
        {
            Set("_store", new ElementWorldStore(2, 8, chunk => callback()));
            var writes = new ElementWriteQueue(8);
            var request = new ElementWriteRequest(Vector3.zero, MaterialId.Fire, 1, 0, false);
            Assert.That(writes.TryEnqueue(in request), Is.True);
            Set("_writeQueue", writes);
        }

        // 反射仅隔离 RED 阶段缺少API造成的整仓编译失败；行为仍调用真实 Runtime，不提供测试替身。
        private void Configure(string mode)
        {
            Type modeType = typeof(GameLog).Assembly.GetType("Game.Core.WorldExecutionMode");
            Assert.That(modeType, Is.Not.Null, "缺少 WorldExecutionMode 合同。");
            CallPublic("ConfigureExecutionMode", Enum.Parse(modeType, mode));
        }

        private void Step(float delta, uint tick) => CallPublic("StepAuthority", delta, tick);

        private void CallPublic(string name, params object[] arguments)
        {
            MethodInfo method = typeof(ElementWorldRuntime).GetMethod(name, BindingFlags.Public | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null, "缺少 Runtime " + name + " 入口。");
            try { method.Invoke(_world, arguments); }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw(); }
        }
    }
}
