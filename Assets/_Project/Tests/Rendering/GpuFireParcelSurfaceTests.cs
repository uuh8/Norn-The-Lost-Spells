using System.Collections;
using System.Collections.Generic;
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
