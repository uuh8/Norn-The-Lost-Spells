using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Game.Core;
using Game.ElementField;
using Game.Materials;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace Game.Rendering.Tests
{
    /// <summary>读取真实 GPU 场与三角形，验证连通性和外壳，避免只检查 Shader 字符串。</summary>
    public sealed class GpuFireParcelSurfaceTests
    {
        [TestCase(.081f, 1)]
        [TestCase(1f / 15f, 2)]
        public void FireSpawnScheduleDoesNotSlowDownWithClampedIntegration(float elapsed, int frames)
        {
            using (var fixture = new CpuSeedFixture(4, 1f))
            {
                int samples = 0;
                for (int i = 0; i < frames; i++) if (fixture.AdvanceSchedule(elapsed)) samples++;
                Assert.That(samples, Is.EqualTo(1),
                    "80ms采样间隔应由已经经过的scaled时间决定，不能把33.3ms的运动稳定性上限叠加成额外等待。");
            }
        }

        [Test]
        public void FireSpawnScheduleHasNoCatchUpDebtAndFitsTheProductionPool()
        {
            var production = AssetDatabase.LoadAssetAtPath<GpuFireParcelRenderProfile>(
                "Assets/_Project/ScriptableObjects/ElementField/Fire/GpuFireParcelRenderProfile_Default.asset");
            foreach (float frameTime in new[] { 1f / 240f, 1f / 60f, 1f / 30f, 1f / 15f, 1f / 5f, 1f })
            using (var fixture = new CpuSeedFixture(4, 1f))
            {
                var liveBatches = new Queue<float>();
                float now = 0f;
                for (int frame = 0; frame < Mathf.CeilToInt(12f / frameTime); frame++)
                {
                    now += frameTime;
                    while (liveBatches.Count > 0 && now - liveBatches.Peek() >= production.Lifetime) liveBatches.Dequeue();
                    if (fixture.AdvanceSchedule(frameTime)) liveBatches.Enqueue(now);
                    Assert.That(liveBatches.Count * production.MaximumSeedsPerTick, Is.LessThanOrEqualTo(production.Capacity),
                        "GPU真实秒龄与发种必须共用时钟，8192池不能为降低首次延迟而提前覆盖仍活跃的Surface/Gas。");
                }
            }
            using (var fixture = new CpuSeedFixture(4, 1f))
            {
                Assert.That(fixture.AdvanceSchedule(3f), Is.True, "长卡顿最多执行本帧的一次扫描。");
                Assert.That(fixture.AdvanceSchedule(0f), Is.False, "不得把长卡顿债务留给下一帧连续补跑。");
                Assert.That(fixture.AdvanceSchedule(.01f), Is.False);
            }
        }

        [Test]
        public void FireLifecycleAgesByElapsedTimeWhileMotionStaysClamped()
        {
            using (var fixture = new CpuSeedFixture(4, 1f))
            {
                fixture.EnableLifecycleGpu(new FireParcelStateGpu
                {
                    Position = Vector3.zero, SourcePosition = Vector3.zero, Velocity = Vector3.right,
                    Age = 0f, Lifetime = .15f, Heat = 1f, Active = 1f
                });
                fixture.Step(.1f);
                var state = fixture.ReadFirstParcel();
                Assert.That(state.Age, Is.EqualTo(.1f).Within(.00001f),
                    "低FPS不能把真实100ms写成33.3ms年龄，否则更快发种会累积过量长寿粒子。");
                Assert.That(state.Position.x, Is.EqualTo(.0333f).Within(.00001f),
                    "年龄使用真实elapsed不应放大位移积分；运动稳定性仍受原上限保护。");
                fixture.Step(0f);
                Assert.That(fixture.ReadFirstParcel().Age, Is.EqualTo(state.Age).Within(.00001f));
                fixture.Step(.1f);
                Assert.That(fixture.ReadFirstParcel().Active, Is.Zero, "超过真实寿命必须释放槽位，不能在卡顿后积压旧火苗。");
            }
        }

        [Test]
        public void PausedFireRendererDoesNotAdvanceTheScheduleOrGpuAge()
        {
            float previousScale = Time.timeScale;
            try
            {
                using (var fixture = new CpuSeedFixture(4, 1f))
                {
                    fixture.EnableLifecycleGpu(new FireParcelStateGpu
                    { Lifetime = 1f, Age = .25f, Active = 1f, Heat = 1f, Velocity = Vector3.right });
                    Time.timeScale = 0f;
                    fixture.UpdatePausedRenderer();
                    var parcel = fixture.ReadFirstParcel();
                    Assert.That(parcel.Age, Is.EqualTo(.25f));
                    Assert.That(parcel.Position, Is.EqualTo(Vector3.zero));
                    Assert.That(fixture.VisualSequence, Is.Zero);
                }
            }
            finally { Time.timeScale = previousScale; }
        }

        [Test]
        public void RendererTimingRecordsInstalledVersionAndSeedWithoutChangingVisualOutput()
        {
            var key = new WorldChunkKey(WorldMaterialDomain.LegacyCell, 0, 0, 0);
            FireParcelSpawnGpu expected;
            using (var baseline = new CpuSeedFixture(1, 1f))
            {
                baseline.Install(key, 1);
                baseline.Install(new WorldChunkKey(WorldMaterialDomain.LegacyCell, 1, 0, 0), 1);
                Assert.That(baseline.Collect(), Is.EqualTo(1));
                expected = baseline.Seeds[0];
                Assert.That(baseline.ScanCursor, Is.EqualTo(1));
            }
            using (var trace = new TimingTraceFixture(key))
            using (var observed = new CpuSeedFixture(1, 1f))
            {
                var first = observed.Install(key, 1);
                observed.Install(new WorldChunkKey(WorldMaterialDomain.LegacyCell, 1, 0, 0), 1);
                Assert.That(observed.Collect(), Is.EqualTo(1));
                Assert.That(observed.Seeds[0].Position, Is.EqualTo(expected.Position));
                Assert.That(observed.Seeds[0].Lifetime, Is.EqualTo(expected.Lifetime));
                Assert.That(observed.Seeds[0].InitialVelocity, Is.EqualTo(expected.InitialVelocity));
                Assert.That(observed.Seeds[0].Heat, Is.EqualTo(expected.Heat));
                Assert.That(observed.VisualSequence, Is.EqualTo(1u));
                Assert.That(observed.ScanCursor, Is.EqualTo(1));
                AssertTimingHeader(trace, "RendererRead", first, 1);
                AssertTimingHeader(trace, "SeedChosen", first, 1);

                // 相同安装版本持续燃烧时不应逐Tick淹没诊断ring；再次安装较新版本后须重新归因。
                observed.Collect();
                Assert.That(observed.VisualSequence, Is.EqualTo(2u));
                Assert.That(observed.ScanCursor, Is.Zero, "预算耗尽后仍须按原顺序轮转，不能为诊断额外扫描目标Key。");
                Assert.That(trace.Events("RendererRead").Count, Is.EqualTo(1));
                Assert.That(trace.Events("SeedChosen").Count, Is.EqualTo(1));
                var second = observed.Install(key, 2);
                observed.Collect();
                Assert.That(observed.VisualSequence, Is.EqualTo(3u));
                Assert.That(observed.ScanCursor, Is.EqualTo(1));
                AssertTimingHeader(trace, "RendererRead", second, 2);
                AssertTimingHeader(trace, "SeedChosen", second, 2);
                Assert.That(observed.Store.TryGetQuantity(WorldMaterialDomain.LegacyCell, Vector3Int.zero,
                    (byte)MaterialId.Fire, out uint quantity), Is.True);
                Assert.That(quantity, Is.EqualTo(255), "只读诊断不得改材料强度。");
            }
        }

        [TestCase(1)] // 视锥/距离没有通过，目标Chunk根本不读取Cell。
        [TestCase(2)] // 真实Fire读取成功，但原概率门拒绝本次Seed。
        [TestCase(3)] // 之前的Key占满既有seed预算，目标Key本轮未被读取。
        public void RendererTimingRecordsActualViewProbabilityAndBudgetRejection(int reason)
        {
            var key = reason == 1 ? new WorldChunkKey(WorldMaterialDomain.LegacyCell, -64, 0, -64)
                : new WorldChunkKey(WorldMaterialDomain.LegacyCell, reason == 3 ? 1 : 0, 0, 0);
            using (var trace = new TimingTraceFixture(key))
            using (var fixture = new CpuSeedFixture(reason == 3 ? 1 : 4, reason == 2 ? 0f : 1f))
            {
                if (reason == 3) fixture.Install(new WorldChunkKey(WorldMaterialDomain.LegacyCell, 0, 0, 0), 1);
                var header = fixture.Install(key, 1);
                Assert.That(fixture.Collect(), Is.EqualTo(reason == 3 ? 1 : 0));
                var rejected = trace.Events("RendererRejected");
                Assert.That(rejected.Count, Is.EqualTo(1), "必须记录真实被原流程门控的目标Key，不能把零Seed误写为已Draw。");
                Assert.That(ReadTimingField<int>(rejected[0], "Reason"), Is.EqualTo(reason));
                Assert.That(ReadTimingField<WorldChunkPayloadHeader>(rejected[0], "Header").Equals(header), Is.True);
                Assert.That(trace.Events("SeedChosen").Count, Is.Zero);
                Assert.That(trace.Events("SeedUploaded").Count, Is.Zero);
                Assert.That(trace.Events("DrawSubmitted").Count, Is.Zero,
                    "CPU CollectSeeds测试没有调用GPU上传或Draw，诊断不得替它们伪造完成事件。");
                Assert.That(trace.Events("RendererRead").Count, Is.EqualTo(reason == 2 ? 1 : 0));
                if (reason != 3)
                {
                    fixture.Collect();
                    Assert.That(trace.Events("RendererRejected").Count, Is.EqualTo(1), "同版本同原因应有界去重。");
                }
            }
        }

        private static void AssertTimingHeader(TimingTraceFixture trace, string stage,
            WorldChunkPayloadHeader expected, int expectedCount)
        {
            var events = trace.Events(stage);
            Assert.That(events.Count, Is.EqualTo(expectedCount), stage + "须来自真实CollectSeeds边界。");
            object value = events[events.Count - 1];
            Assert.That(ReadTimingField<WorldChunkPayloadHeader>(value, "Header").Equals(expected), Is.True);
            Assert.That(ReadTimingField<uint>(value, "PresentationVersion"), Is.EqualTo((uint)expectedCount));
            Assert.That(ReadTimingField<int>(value, "CellX"), Is.Zero);
            Assert.That(ReadTimingField<int>(value, "CellY"), Is.Zero);
            Assert.That(ReadTimingField<int>(value, "CellZ"), Is.Zero);
        }

        private static T ReadTimingField<T>(object value, string name) => (T)value.GetType().GetField(name).GetValue(value);

        /// <summary>
        /// 仅装配Renderer会读取的真实ReplicaStore和Geometry；不激活World，不创建三相Owner或任何GPU资源。
        /// 反射绕过生命周期只用于既有私有依赖注入，实际Payload校验/Store安装/读桥/CollectSeeds都走生产实现。
        /// </summary>
        private sealed class CpuSeedFixture : System.IDisposable
        {
            private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            private readonly GameObject _host, _cameraHost;
            private readonly ElementWorldProfile _worldProfile;
            private readonly GpuFireParcelRenderProfile _fireProfile;
            private readonly GpuFireParcelRenderer _renderer;
            private ComputeShader _lifecycle;
            private GraphicsBuffer _states;
            public readonly WorldReplicaStore Store = new WorldReplicaStore(7, 4, 32, 0, 256);
            public readonly FireParcelSpawnGpu[] Seeds;
            public uint VisualSequence => (uint)typeof(GpuFireParcelRenderer).GetField("_visualSequence", Hidden).GetValue(_renderer);
            public int ScanCursor => (int)typeof(GpuFireParcelRenderer).GetField("_seedScanChunkCursor", Hidden).GetValue(_renderer);

            public CpuSeedFixture(int seedCapacity, float probability)
            {
                _host = new GameObject("Inactive real Replica Fire seed fixture"); _host.SetActive(false);
                _worldProfile = ScriptableObject.CreateInstance<ElementWorldProfile>();
                JsonUtility.FromJsonOverwrite("{\"_cellSize\":1,\"_chunkSize\":4}", _worldProfile);
                _fireProfile = ScriptableObject.CreateInstance<GpuFireParcelRenderProfile>();
                typeof(GpuFireParcelRenderProfile).GetField("_spawnProbabilityAtFullAmount", Hidden).SetValue(_fireProfile, probability);
                var world = _host.AddComponent<ElementWorldRuntime>();
                typeof(ElementWorldRuntime).GetField("_profile", Hidden).SetValue(world, _worldProfile);
                world.ConfigureExecutionMode(WorldExecutionMode.Replica);
                typeof(ElementWorldRuntime).GetField("_replicaStore", Hidden).SetValue(world, Store);
                typeof(ElementWorldRuntime).GetField("<IsInitialized>k__BackingField", Hidden).SetValue(world, true);
                _cameraHost = new GameObject("CPU seed selection camera");
                var camera = _cameraHost.AddComponent<Camera>();
                camera.transform.position = new Vector3(0, 0, -8); camera.farClipPlane = 100; camera.aspect = 1;
                _renderer = _host.AddComponent<GpuFireParcelRenderer>();
                Set("_worldRuntime", world); Set("_world", world); Set("_chunkReader", world);
                Set("_profile", _fireProfile); Set("_viewCamera", camera);
                Set("_visibleKeys", new ElementChunkKey[4]); Set("_chunkCpu", new ElementCell[64]);
                Seeds = new FireParcelSpawnGpu[seedCapacity]; Set("_seedCpu", Seeds);
                Set("_phase", _fireProfile.CreatePhaseSettings());
            }
            private void Set(string name, object value) => typeof(GpuFireParcelRenderer).GetField(name, Hidden).SetValue(_renderer, value);
            public int Collect() => (int)typeof(GpuFireParcelRenderer).GetMethod("CollectSeeds", Hidden).Invoke(_renderer, null);
            public bool AdvanceSchedule(float elapsed) => (bool)typeof(GpuFireParcelRenderer)
                .GetMethod("AdvanceSpawnSchedule", Hidden).Invoke(_renderer, new object[] { elapsed });
            public void Step(float elapsed) => typeof(GpuFireParcelRenderer)
                .GetMethod("StepVisualFrame", Hidden).Invoke(_renderer, new object[] { elapsed });
            public void UpdatePausedRenderer()
            {
                Set("_initialized", true);
                typeof(GpuFireParcelRenderer).GetMethod("Update", Hidden).Invoke(_renderer, null);
            }
            public void EnableLifecycleGpu(FireParcelStateGpu initial)
            {
                if (!SystemInfo.supportsComputeShaders) Assert.Ignore("真实Lifecycle回归需要GPU Compute。");
                // 只克隆运行时配置：关闭外力后，X位移恰好等于clamped dt，方便直接验证积分没有被改大。
                JsonUtility.FromJsonOverwrite("{\"_capacity\":64,\"_buoyancy\":0,\"_drag\":0,\"_sourceTether\":0,\"_noiseStrength\":0}", _fireProfile);
                _lifecycle = Object.Instantiate(AssetDatabase.LoadAssetAtPath<ComputeShader>(
                    "Assets/_Project/Art/Elemental/Compute/FireParcelLifecycle.compute"));
                _states = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 64, FireParcelGpuLayout.StateStride);
                var values = new FireParcelStateGpu[64]; values[0] = initial; _states.SetData(values);
                Set("_stateBuffer", _states); Set("_lifecycleCompute", _lifecycle);
                Set("_simulateKernel", _lifecycle.FindKernel("SimulateParcels"));
            }
            public FireParcelStateGpu ReadFirstParcel()
            {
                var values = new FireParcelStateGpu[1]; _states.GetData(values, 0, 0, 1); return values[0];
            }
            public WorldChunkPayloadHeader Install(WorldChunkKey key, ulong revision)
            {
                var words = new uint[18]; words[0] = WorldChunkPayloadCodec.Magic; words[1] = 1; words[3] = 1;
                WorldChunkPayloadCodec.WriteGeometry(words, 4, new WorldGridGeometry(WorldMaterialDomain.LegacyCell, Vector3.zero, 1, 4));
                WorldChunkPayloadCodec.WriteCell(words, 11, new WorldMaterialCellSample(WorldMaterialDomain.LegacyCell,
                    WorldMaterialStorage.Resident, new Vector3Int(key.X * 4, key.Y * 4, key.Z * 4), (byte)MaterialId.Fire, 255));
                var header = new WorldChunkPayloadHeader(7, revision, key, 1, 0, revision, (long)revision * 10,
                    words.Length, WorldChunkPayloadCodec.ComputeChecksum(words, words.Length), true);
                Assert.That(Store.TryInstall(header, words, words.Length), Is.True);
                return header;
            }
            public void Dispose()
            {
                // 此夹具始终inactive，EditMode不保证派发OnDestroy；显式释放自有GPU Buffer，
                // 再清掉Renderer引用，避免平台恰好派发回调时对同一Buffer重复Dispose。
                if (_states != null) { Set("_stateBuffer", null); _states.Dispose(); _states = null; }
                Object.DestroyImmediate(_host); Object.DestroyImmediate(_cameraHost);
                Object.DestroyImmediate(_worldProfile); Object.DestroyImmediate(_fireProfile);
                if (_lifecycle != null) Object.DestroyImmediate(_lifecycle);
            }
        }

        // Core观测API由另一文件Owner提供；测试用反射读取值副本，RED不会因尚未落盘的类型导致整项目编译失败。
        private sealed class TimingTraceFixture : System.IDisposable
        {
            private readonly System.Type _type;
            private readonly object _trace;
            public TimingTraceFixture(WorldChunkKey key)
            {
                _type = typeof(WorldChunkPayloadHeader).Assembly.GetType("Game.Core.WorldReplicationTimingTrace");
                Assert.That(_type, Is.Not.Null, "先装配Core预分配观测器，再观察Renderer缺失hook的真实RED。");
                _trace = System.Activator.CreateInstance(_type, new object[] { 128, key, 7UL });
                Assert.That((bool)_type.GetMethod("TryAttach").Invoke(null, new[] { _trace }), Is.True);
            }
            public List<object> Events(string stage)
            {
                var result = new List<object>();
                int count = (int)_type.GetProperty("Count").GetValue(_trace);
                var read = _type.GetMethod("TryRead");
                for (int i = 0; i < count; i++)
                {
                    var args = new object[] { i, null };
                    Assert.That((bool)read.Invoke(_trace, args), Is.True);
                    if (args[1].GetType().GetField("Stage").GetValue(args[1]).ToString() == stage) result.Add(args[1]);
                }
                return result;
            }
            public void Dispose() => _type.GetMethod("Detach").Invoke(null, new[] { _trace });
        }

        [Test]
        public void ProductionPoolCanHoldWorstCaseEmissionForFullLifecycle()
        {
            var profile = AssetDatabase.LoadAssetAtPath<GpuFireParcelRenderProfile>(
                "Assets/_Project/ScriptableObjects/ElementField/Fire/GpuFireParcelRenderProfile_Default.asset");
            int required = Mathf.CeilToInt(profile.MaximumSeedsPerTick * profile.Lifetime / profile.SpawnInterval);
            Assert.That(profile.Capacity, Is.GreaterThanOrEqualTo(required),
                "最大发射负载也应保留 Surface→Gas 生命周期，而不能在 Hold 中途覆盖所有 Parcel。");
        }
        [UnityTest]
        public IEnumerator ActiveParcelOutsideRecentWindowStillHasSurface()
        {
            var states = new FireParcelStateGpu[1024];
            states[700] = Parcel(Vector3.zero);
            using (var scene = new Surface(states))
            {
                yield return scene.Read();
                Assert.That(scene.Count, Is.GreaterThan(0), "仍活跃的早期 Parcel 不能因为不是最新512个就丢失 Surface。");
            }
        }

        [UnityTest]
        public IEnumerator FireShellIsClosedAndConsistentlyOutward()
        {
            using (var scene = new Surface(new[] { Parcel(Vector3.zero) }))
            {
                yield return scene.Read();
                Assert.That(scene.Count, Is.GreaterThan(0));
                var vertices = new Dictionary<Vector3Int, int>();
                var edges = new Dictionary<long, int>();
                int inward = 0;
                for (int i = 0; i < scene.Count; i++)
                {
                    Triangle t = scene.Triangles[i];
                    int a = Weld(t.A.Position, vertices), b = Weld(t.B.Position, vertices), c = Weld(t.C.Position, vertices);
                    Edge(a, b, edges); Edge(b, c, edges); Edge(c, a, edges);
                    if (Vector3.Dot(Vector3.Cross(t.B.Position - t.A.Position, t.C.Position - t.A.Position),
                            t.A.Normal + t.B.Normal + t.C.Normal) < -1e-7f) inward++;
                }
                foreach (var edge in edges) Assert.That(edge.Value, Is.EqualTo(2), "Tetra 截面的四个交点必须按多边形顺序连接。");
                Assert.That(inward, Is.Zero, "统一 Outward Winding 后才能可靠地 Cull Back，消除前后壳透明乱序。");
            }
        }

        [UnityTest]
        public IEnumerator AdjacentSourcesShareBodyButDistantSourcesStaySeparate()
        {
            using (var scene = new Surface(new[] { Parcel(new Vector3(-.35f, 0, 0)), Parcel(new Vector3(.35f, 0, 0)) }))
            {
                yield return scene.Read();
                Assert.That(scene.Components(), Is.EqualTo(1), "0.7m间距的年轻火源需要共享连续主体。");
            }
            using (var scene = new Surface(new[] { Parcel(new Vector3(-1.3f, 0, 0)), Parcel(new Vector3(1.3f, 0, 0)) }))
            {
                yield return scene.Read();
                Assert.That(scene.Components(), Is.EqualTo(2), "视觉 Cohesion 不应把远离的火源跨空地连接。");
            }
        }

        private static FireParcelStateGpu Parcel(Vector3 p) => new FireParcelStateGpu
        { Position = p, SourcePosition = p, Radius = .24f, SurfaceWeight = 1, Heat = 1, Active = 1, Lifetime = 2.3f };

        private static int Weld(Vector3 p, Dictionary<Vector3Int, int> vertices)
        {
            var key = new Vector3Int(Mathf.RoundToInt(p.x * 10000), Mathf.RoundToInt(p.y * 10000), Mathf.RoundToInt(p.z * 10000));
            if (!vertices.TryGetValue(key, out int id)) { id = vertices.Count; vertices.Add(key, id); }
            return id;
        }
        private static void Edge(int a, int b, Dictionary<long, int> edges)
        {
            long key = ((long)Mathf.Min(a, b) << 32) | (uint)Mathf.Max(a, b);
            edges.TryGetValue(key, out int count); edges[key] = count + 1;
        }
        private struct Vertex { public Vector3 Position; public float Pad; public Vector3 Normal; public float Pad2; }
        private struct Triangle { public Vertex A, B, C; }

        private sealed class Surface : System.IDisposable
        {
            private readonly FireParcelSurfaceResources _resources;
            private readonly GraphicsBuffer _states;
            private readonly FireParcelSurfaceGridSettings _grid;
            private readonly ComputeShader _density, _mesh;
            public Triangle[] Triangles;
            public int Count;
            public Surface(FireParcelStateGpu[] states)
            {
                if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback) Assert.Ignore("需要实际GPU。");
                _grid = FireParcelSurfaceGridPlanner.Plan(Vector3.zero, 2.5f, .14f);
                _resources = new FireParcelSurfaceResources(in _grid, 131072);
                _states = new GraphicsBuffer(GraphicsBuffer.Target.Structured, states.Length, FireParcelGpuLayout.StateStride);
                _states.SetData(states);
                _density = Object.Instantiate(AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/_Project/Art/Elemental/Compute/FireParcelDensity.compute"));
                _mesh = Object.Instantiate(AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/_Project/Art/Elemental/Compute/FireParcelMarchingCubes.compute"));
                Grid(_density); Grid(_mesh);
                _density.SetInt("_Capacity", states.Length); _density.SetInt("_SurfaceParcelBudget", 512); _density.SetInt("_SurfaceStartSlot", 0);
                _density.SetFloat("_SurfaceKernelScale", 1.5f); _density.SetFloat("_LiquidLikeRadiusMultiplier", 1.35f);
                _density.SetFloat("_CohesionRadius", .6f); _density.SetFloat("_SurfaceVerticalStretch", 1.9f);
                _density.SetFloat("_VisualTime", 0);
                if (_density.HasKernel("ScatterFireDensity"))
                {
                    // 新旧实现共用相同输入与断言；RED 阶段运行旧 Gather，不因缺少新接口而失败。
                    var field = typeof(FireParcelSurfaceResources).GetProperty("DensityAccumulation");
                    var accumulation = (GraphicsBuffer)field.GetValue(_resources);
                    int clear = _density.FindKernel("ClearFireDensity"), scatter = _density.FindKernel("ScatterFireDensity"), resolve = _density.FindKernel("ResolveFireDensity");
                    _density.SetInt("_VoxelCount", _grid.Resolution.x * _grid.Resolution.y * _grid.Resolution.z);
                    _density.SetBuffer(clear, "_DensityAccumulation", accumulation); _density.Dispatch(clear, (_grid.Resolution.x * _grid.Resolution.y * _grid.Resolution.z + 63) / 64, 1, 1);
                    _density.SetBuffer(scatter, "_DensityAccumulation", accumulation); _density.SetBuffer(scatter, "_FireParcelStates", _states); _density.Dispatch(scatter, (states.Length + 63) / 64, 1, 1);
                    _density.SetBuffer(resolve, "_DensityAccumulation", accumulation); _density.SetTexture(resolve, "_FireDensity", _resources.Density);
                    _density.Dispatch(resolve, Groups, Groups, Groups);
                }
                else
                {
                    int gather = _density.FindKernel("GatherFireDensity");
                    _density.SetBuffer(gather, "_FireParcelStates", _states); _density.SetTexture(gather, "_FireDensity", _resources.Density); _density.Dispatch(gather, Groups, Groups, Groups);
                }
                _mesh.SetFloat("_IsoLevel", .42f); _mesh.SetInt("_MaximumTriangleCount", 131072);
                foreach (string name in new[] { "ClearFireSurface", "ExtractFireSurface", "BuildFireDrawArgs" })
                {
                    int k = _mesh.FindKernel(name);
                    _mesh.SetBuffer(k, "_FireSurfaceTriangles", _resources.Triangles); _mesh.SetBuffer(k, "_TriangleCounter", _resources.Counter);
                    _mesh.SetBuffer(k, "_OverflowCounter", _resources.Overflow); _mesh.SetBuffer(k, "_IndirectArguments", _resources.DrawArgs);
                    if (name == "ExtractFireSurface") { _mesh.SetTexture(k, "_FireDensity", _resources.Density); _mesh.Dispatch(k, Groups, Groups, Groups); }
                    else _mesh.Dispatch(k, 1, 1, 1);
                }
            }
            private int Groups => (_grid.Resolution.x + 3) / 4;
            private void Grid(ComputeShader shader)
            {
                shader.SetInt("_GridResolutionX", _grid.Resolution.x); shader.SetInt("_GridResolutionY", _grid.Resolution.y); shader.SetInt("_GridResolutionZ", _grid.Resolution.z);
                shader.SetVector("_WorldOrigin", _grid.Origin); shader.SetVector("_VoxelSize", _grid.VoxelSize);
            }
            public IEnumerator Read()
            {
                var count = AsyncGPUReadback.Request(_resources.Counter); var triangles = AsyncGPUReadback.Request(_resources.Triangles); var overflow = AsyncGPUReadback.Request(_resources.Overflow);
                while (!count.done || !triangles.done || !overflow.done) yield return null;
                Assert.That(count.hasError || triangles.hasError || overflow.hasError, Is.False);
                Assert.That(overflow.GetData<uint>()[0], Is.Zero);
                Count = (int)count.GetData<uint>()[0]; Triangles = triangles.GetData<Triangle>().ToArray();
            }
            public int Components()
            {
                var ids = new Dictionary<Vector3Int, int>(); var parents = new List<int>();
                for (int i = 0; i < Count; i++)
                {
                    Triangle t = Triangles[i]; int a = Weld(t.A.Position, ids), b = Weld(t.B.Position, ids), c = Weld(t.C.Position, ids);
                    while (parents.Count < ids.Count) parents.Add(parents.Count);
                    parents[Root(b, parents)] = Root(a, parents); parents[Root(c, parents)] = Root(a, parents);
                }
                int count = 0; for (int i = 0; i < parents.Count; i++) if (Root(i, parents) == i) count++;
                return count;
            }
            private static int Root(int i, List<int> parents) { while (i != parents[i]) i = parents[i]; return i; }
            public void Dispose() { _resources.Dispose(); _states.Dispose(); Object.DestroyImmediate(_density); Object.DestroyImmediate(_mesh); }
        }
    }
}
