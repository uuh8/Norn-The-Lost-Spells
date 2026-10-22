using System;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Game.Rendering.Tests
{
    /// <summary>
    /// 真实 Compute fixture：以 CPU 生成的球形 Scalar Field 驱动 GPU Marching Cubes，
    /// 再用 Test-only 同步 Readback 检查拓扑容量、有限 Bounds 与 outward unit normal。
    /// </summary>
    public sealed class GpuMarchingCubesTests
    {
        private const string ShaderPath =
            "Assets/_Project/Art/Elemental/Compute/FluidMarchingCubes.compute";
        private const int Resolution = 9;
        private const float IsoLevel = 0.5f;

        [Test]
        public void SphereFieldProducesFiniteBoundedVerticesAndOutwardUnitNormals()
        {
            IgnoreWithoutGpuSupport();
            ComputeShader shader = LoadShader();
            Texture3D density = CreateSphereDensity();
            using (var triangles = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 512, 96))
            using (var triangleCounter = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 4))
            using (var overflowCounter = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 4))
            using (var args = new GraphicsBuffer(
                       GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Raw,
                       1,
                       GraphicsBuffer.IndirectDrawArgs.size))
            {
                try
                {
                    Dispatch(
                        shader,
                        density,
                        triangles,
                        triangleCounter,
                        overflowCounter,
                        args,
                        Resolution,
                        Vector3.one * 0.5f,
                        IsoLevel,
                        512);
                    uint triangleCount = ReadUIntTestOnly(triangleCounter, 0);
                    uint overflowCount = ReadUIntTestOnly(overflowCounter, 0);
                    uint[] drawArgs = ReadUInt4TestOnly(args);
                    Vector4[] packedVertices = ReadFloat4TestOnly(triangles, checked((int)triangleCount * 6));

                    Assert.That(triangleCount, Is.GreaterThan(0u));
                    Assert.That(triangleCount, Is.LessThanOrEqualTo(512u));
                    Assert.That(overflowCount, Is.Zero);
                    Assert.That(drawArgs, Is.EqualTo(new[] { triangleCount * 3u, 1u, 0u, 0u }));

                    Vector3 center = Vector3.one * 2f;
                    for (int i = 0; i < packedVertices.Length; i += 2)
                    {
                        Vector3 position = packedVertices[i];
                        Vector3 normal = packedVertices[i + 1];
                        AssertFinite(position);
                        AssertFinite(normal);
                        Assert.That(position.x, Is.InRange(0f, 4f));
                        Assert.That(position.y, Is.InRange(0f, 4f));
                        Assert.That(position.z, Is.InRange(0f, 4f));
                        Assert.That(normal.magnitude, Is.EqualTo(1f).Within(0.03f));
                        Assert.That(Vector3.Dot(normal, position - center), Is.GreaterThan(0.02f));
                    }
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(density);
                }
            }
        }

        [Test]
        public void SingleCornerCaseUsesExactEdgesZeroEightAndThree()
        {
            IgnoreWithoutGpuSupport();
            ComputeShader shader = LoadShader();
            Texture3D density = CreateSingleCornerDensity();
            using (var triangles = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 5, 96))
            using (var triangleCounter = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 4))
            using (var overflowCounter = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 4))
            using (var args = new GraphicsBuffer(
                       GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Raw,
                       1,
                       GraphicsBuffer.IndirectDrawArgs.size))
            {
                try
                {
                    Dispatch(
                        shader,
                        density,
                        triangles,
                        triangleCounter,
                        overflowCounter,
                        args,
                        resolution: 2,
                        voxelSize: Vector3.one,
                        isoLevel: 0.5f,
                        maximumTriangles: 5);
                    uint triangleCount = ReadUIntTestOnly(triangleCounter, 0);
                    uint overflowCount = ReadUIntTestOnly(overflowCounter, 0);
                    uint[] drawArgs = ReadUInt4TestOnly(args);
                    Vector4[] packedVertices = ReadFloat4TestOnly(triangles, 6);

                    Assert.That(triangleCount, Is.EqualTo(1u),
                        "Classic Case 1 must produce exactly one triangle.");
                    Assert.That(overflowCount, Is.Zero);
                    Assert.That(drawArgs, Is.EqualTo(new[] { 3u, 1u, 0u, 0u }));

                    // Winding correction may reorder the three vertices, so compare an unordered position set.
                    var actualPositions = new[]
                    {
                        (Vector3)packedVertices[0],
                        (Vector3)packedVertices[2],
                        (Vector3)packedVertices[4]
                    };
                    AssertContainsPosition(actualPositions, new Vector3(0.5f, 0f, 0f)); // edge 0: 0-1
                    AssertContainsPosition(actualPositions, new Vector3(0f, 0f, 0.5f)); // edge 8: 0-4
                    AssertContainsPosition(actualPositions, new Vector3(0f, 0.5f, 0f)); // edge 3: 3-0
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(density);
                }
            }
        }

        [Test]
        public void TriangleCapacityBoundsWritesAndBuildsBoundedIndirectArgs()
        {
            IgnoreWithoutGpuSupport();
            ComputeShader shader = LoadShader();
            Texture3D density = CreateSphereDensity();
            using (var triangles = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 96))
            using (var triangleCounter = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 4))
            using (var overflowCounter = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 4))
            using (var args = new GraphicsBuffer(
                       GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Raw,
                       1,
                       GraphicsBuffer.IndirectDrawArgs.size))
            {
                try
                {
                    Dispatch(
                        shader,
                        density,
                        triangles,
                        triangleCounter,
                        overflowCounter,
                        args,
                        Resolution,
                        Vector3.one * 0.5f,
                        IsoLevel,
                        1);
                    uint requestedCount = ReadUIntTestOnly(triangleCounter, 0);
                    uint overflowCount = ReadUIntTestOnly(overflowCounter, 0);
                    uint[] drawArgs = ReadUInt4TestOnly(args);

                    Assert.That(requestedCount, Is.GreaterThan(1u));
                    Assert.That(overflowCount, Is.EqualTo(requestedCount - 1u));
                    Assert.That(drawArgs, Is.EqualTo(new[] { 3u, 1u, 0u, 0u }));
                    Assert.That(ReadFloat4TestOnly(triangles, 6).Length, Is.EqualTo(6));
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(density);
                }
            }
        }

        private static Texture3D CreateSphereDensity()
        {
            var values = new float[Resolution * Resolution * Resolution];
            Vector3 center = Vector3.one * 4f;
            const float radius = 2.35f;
            for (int z = 0; z < Resolution; z++)
            for (int y = 0; y < Resolution; y++)
            for (int x = 0; x < Resolution; x++)
            {
                float distance = Vector3.Distance(new Vector3(x, y, z), center);
                values[x + Resolution * (y + Resolution * z)] = radius - distance + IsoLevel;
            }

            var texture = new Texture3D(
                Resolution,
                Resolution,
                Resolution,
                GraphicsFormat.R32_SFloat,
                TextureCreationFlags.None)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            texture.SetPixelData(values, 0);
            texture.Apply(false, true);
            return texture;
        }

        private static Texture3D CreateSingleCornerDensity()
        {
            // Texture3D 的线性 index 是 x + width*(y + height*z)。只有 (0,0,0) 高于 Iso，
            // 因而 Case bit 必须是 1；若 Corner 或 Edge mapping 漂移，三个几何交点会立刻改变。
            var values = new float[8];
            values[0] = 1f;
            var texture = new Texture3D(
                2,
                2,
                2,
                GraphicsFormat.R32_SFloat,
                TextureCreationFlags.None)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            texture.SetPixelData(values, 0);
            texture.Apply(false, true);
            return texture;
        }

        private static void Dispatch(
            ComputeShader shader,
            Texture density,
            GraphicsBuffer triangles,
            GraphicsBuffer triangleCounter,
            GraphicsBuffer overflowCounter,
            GraphicsBuffer args,
            int resolution,
            Vector3 voxelSize,
            float isoLevel,
            int maximumTriangles)
        {
            int clear = shader.FindKernel("ClearSurfaceCounters");
            int extract = shader.FindKernel("ExtractSurface");
            int buildArgs = shader.FindKernel("BuildIndirectArgs");
            shader.SetInt("_GridResolutionX", resolution);
            shader.SetInt("_GridResolutionY", resolution);
            shader.SetInt("_GridResolutionZ", resolution);
            shader.SetVector("_WorldOrigin", Vector3.zero);
            shader.SetVector("_VoxelSize", voxelSize);
            shader.SetFloat("_IsoLevel", isoLevel);
            shader.SetInt("_MaximumTriangleCount", maximumTriangles);

            BindWriteBuffers(shader, clear, triangles, triangleCounter, overflowCounter, args);
            shader.Dispatch(clear, 1, 1, 1);
            shader.SetTexture(extract, "_DensityTexture", density);
            BindWriteBuffers(shader, extract, triangles, triangleCounter, overflowCounter, args);
            shader.Dispatch(extract, DivideRoundUp(resolution - 1, 4),
                DivideRoundUp(resolution - 1, 4), DivideRoundUp(resolution - 1, 4));
            BindWriteBuffers(shader, buildArgs, triangles, triangleCounter, overflowCounter, args);
            shader.Dispatch(buildArgs, 1, 1, 1);
        }

        private static void BindWriteBuffers(
            ComputeShader shader,
            int kernel,
            GraphicsBuffer triangles,
            GraphicsBuffer triangleCounter,
            GraphicsBuffer overflowCounter,
            GraphicsBuffer args)
        {
            shader.SetBuffer(kernel, "_FluidSurfaceTriangles", triangles);
            shader.SetBuffer(kernel, "_TriangleCounter", triangleCounter);
            shader.SetBuffer(kernel, "_OverflowCounter", overflowCounter);
            shader.SetBuffer(kernel, "_IndirectArguments", args);
        }

        private static uint ReadUIntTestOnly(GraphicsBuffer buffer, int index)
        {
            return ReadUInt4TestOnly(buffer)[index];
        }

        private static uint[] ReadUInt4TestOnly(GraphicsBuffer buffer)
        {
            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(buffer);
            request.WaitForCompletion();
            Assert.That(request.hasError, Is.False, "Test-only uint buffer readback failed.");
            NativeArray<uint> data = request.GetData<uint>();
            return data.ToArray();
        }

        private static Vector4[] ReadFloat4TestOnly(GraphicsBuffer buffer, int float4Count)
        {
            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(
                buffer,
                checked(float4Count * 16),
                0);
            request.WaitForCompletion();
            Assert.That(request.hasError, Is.False, "Test-only triangle readback failed.");
            return request.GetData<Vector4>().ToArray();
        }

        private static ComputeShader LoadShader()
        {
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderPath);
            Assert.That(shader, Is.Not.Null, $"Missing ComputeShader at {ShaderPath}.");
            return shader;
        }

        private static int DivideRoundUp(int value, int divisor)
        {
            return (value + divisor - 1) / divisor;
        }

        private static void AssertFinite(Vector3 value)
        {
            Assert.That(float.IsNaN(value.x) || float.IsInfinity(value.x), Is.False);
            Assert.That(float.IsNaN(value.y) || float.IsInfinity(value.y), Is.False);
            Assert.That(float.IsNaN(value.z) || float.IsInfinity(value.z), Is.False);
        }

        private static void AssertContainsPosition(Vector3[] positions, Vector3 expected)
        {
            for (int i = 0; i < positions.Length; i++)
            {
                if (Vector3.Distance(positions[i], expected) <= 1e-5f)
                    return;
            }

            Assert.Fail($"Missing expected Marching Cubes edge intersection {expected}.");
        }

        private static void IgnoreWithoutGpuSupport()
        {
            bool d3d = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D11
                || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12;
            if (!SystemInfo.supportsComputeShaders
                || !SystemInfo.supportsIndirectArgumentsBuffer
                || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null
                || !d3d
                || !SystemInfo.IsFormatSupported(GraphicsFormat.R32_SFloat, GraphicsFormatUsage.Sample))
            {
                Assert.Ignore("D3D11/12 Compute + Indirect Args + R32 sample is unavailable; skipped is not passed.");
            }
        }
    }
}
