using System;
using Game.ElementField;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Game.Materials;

namespace Game.Rendering.Tests
{
    /// <summary>
    /// 真实 Compute + R32_SFloat Tex3D 数值 fixture。WaitForCompletion 只允许出现在 Test-only；
    /// Runtime Density Renderer 不得同步回读，否则会让 CPU 等待 GPU 并破坏流水线并行。
    /// </summary>
    public sealed class GpuFluidDensityTests
    {
        private const string DensityShaderPath =
            "Assets/_Project/Art/Elemental/Compute/FluidDensity.compute";
        private const string SpatialHashShaderPath =
            "Assets/_Project/Art/Elemental/Compute/PbfSpatialHash.compute";
        private const int ParticleCapacity = 32;
        private const int HashCapacity = 64;
        private const float SmoothingRadius = 1f;
        private const float ParticleMass = 0.25f;

        [Test]
        public void SurfaceResourcesUseRequiredGpuLayoutsAndReuseTranslationOnlyGrid()
        {
            FluidSurfaceGridSettings first = CreateGrid(new Vector3(-2f, -2f, -2f));
            IgnoreWithoutSurfaceSupport(in first);

            var resources = new FluidSurfaceGpuResources(in first, ParticleCapacity);
            try
            {
                Assert.That(resources.DensityTexture.dimension, Is.EqualTo(TextureDimension.Tex3D));
                Assert.That(resources.DensityTexture.graphicsFormat, Is.EqualTo(GraphicsFormat.R32_SFloat));
                Assert.That(resources.DensityTexture.enableRandomWrite, Is.True);
                Assert.That(resources.DensityTexture.volumeDepth, Is.EqualTo(first.Resolution.z));
                Assert.That(resources.TriangleBuffer.stride, Is.EqualTo(96));
                Assert.That(resources.TriangleCounter.stride, Is.EqualTo(sizeof(uint)));
                Assert.That(resources.OverflowCounter.stride, Is.EqualTo(sizeof(uint)));
                Assert.That(resources.IndirectArguments.stride,
                    Is.EqualTo(GraphicsBuffer.IndirectDrawArgs.size));
                Assert.That(
                    resources.IndirectArguments.target,
                    Is.EqualTo(GraphicsBuffer.Target.IndirectArguments),
                    "Unity 6.3 的 IndirectArguments 已可绑定 RWByteAddressBuffer；Raw 组合会被 D3D11 拒绝。");
                Assert.That(resources.AnisotropyBuffer.stride, Is.EqualTo(64));
                Assert.That(resources.AnisotropyBuffer.count, Is.EqualTo(ParticleCapacity));

                RenderTexture originalTexture = resources.DensityTexture;
                GraphicsBuffer originalTriangles = resources.TriangleBuffer;
                FluidSurfaceGridSettings translated = CreateGrid(new Vector3(4f, -7f, 3f));
                Assert.That(resources.TryUpdateGrid(in translated), Is.True);
                Assert.That(resources.DensityTexture, Is.SameAs(originalTexture));
                Assert.That(resources.TriangleBuffer, Is.SameAs(originalTriangles));
                Assert.That(resources.GridSettings.WorldOrigin, Is.EqualTo(translated.WorldOrigin));
            }
            finally
            {
                resources.Dispose();
                resources.Dispose();
            }

            Assert.That(resources.DensityTexture, Is.Null);
            Assert.That(resources.TriangleBuffer, Is.Null);
            Assert.That(resources.IndirectArguments, Is.Null);
        }

        [Test]
        public void SingleParticleMatchesIndependentPoly6AtCenterAndIsZeroAtAndOutsideSupport()
        {
            FluidSurfaceGridSettings grid = CreateGrid(new Vector3(-2f, -2f, -2f));
            IgnoreWithoutSurfaceSupport(in grid);
            ComputeShader density = LoadShader(DensityShaderPath);
            ComputeShader spatialHash = LoadShader(SpatialHashShaderPath);

            using (var particles = new FluidGpuResourceSet(ParticleCapacity, HashCapacity, 1))
            using (var surface = new FluidSurfaceGpuResources(in grid, ParticleCapacity))
            {
                UploadParticles(particles, new[] { new Vector3(0f, 0f, 0f) });
                BuildSpatialHash(spatialHash, particles);
                DispatchDensity(density, particles, surface, in grid);
                float[] values = ReadDensityTestOnly(surface.DensityTexture);

                float expectedCenter = ParticleMass * IndependentPoly6(0f, SmoothingRadius);
                Assert.That(Read(values, grid.Resolution, 4, 4, 4),
                    Is.EqualTo(expectedCenter).Within(expectedCenter * 1e-4f));
                Assert.That(Read(values, grid.Resolution, 5, 4, 4),
                    Is.LessThan(expectedCenter));
                Assert.That(Read(values, grid.Resolution, 6, 4, 4), Is.EqualTo(0f).Within(1e-7f));
                Assert.That(Read(values, grid.Resolution, 7, 4, 4), Is.EqualTo(0f).Within(1e-7f));
                AssertAllFinite(values);
            }
        }

        [Test]
        public void TwoParticlesAddAndNegativeTranslatedWorldCoordinatesPreserveLocalDensity()
        {
            FluidSurfaceGridSettings negativeGrid = CreateGrid(new Vector3(-4f, -4f, -4f));
            IgnoreWithoutSurfaceSupport(in negativeGrid);
            ComputeShader density = LoadShader(DensityShaderPath);
            ComputeShader spatialHash = LoadShader(SpatialHashShaderPath);

            using (var particles = new FluidGpuResourceSet(ParticleCapacity, HashCapacity, 1))
            using (var surface = new FluidSurfaceGpuResources(in negativeGrid, ParticleCapacity))
            {
                var negativeParticle = new Vector3(-2f, -2f, -2f);
                UploadParticles(particles, new[] { negativeParticle, negativeParticle });
                BuildSpatialHash(spatialHash, particles);
                DispatchDensity(density, particles, surface, in negativeGrid);
                float[] twoParticleValues = ReadDensityTestOnly(surface.DensityTexture);

                float singleExpected = ParticleMass * IndependentPoly6(0f, SmoothingRadius);
                Assert.That(Read(twoParticleValues, negativeGrid.Resolution, 4, 4, 4),
                    Is.EqualTo(singleExpected * 2f).Within(singleExpected * 2e-4f));

                FluidSurfaceGridSettings translatedGrid = CreateGrid(new Vector3(6f, 1f, -8f));
                Assert.That(surface.TryUpdateGrid(in translatedGrid), Is.True);
                var translatedParticle = translatedGrid.WorldOrigin + Vector3.one * 2f;
                UploadParticles(particles, new[] { translatedParticle });
                BuildSpatialHash(spatialHash, particles);
                DispatchDensity(density, particles, surface, in translatedGrid);
                float[] translatedValues = ReadDensityTestOnly(surface.DensityTexture);

                Assert.That(Read(translatedValues, translatedGrid.Resolution, 4, 4, 4),
                    Is.EqualTo(singleExpected).Within(singleExpected * 1e-4f));
                Assert.That(Read(translatedValues, translatedGrid.Resolution, 5, 4, 4),
                    Is.EqualTo(IndependentPoly6(0.5f, SmoothingRadius) * ParticleMass)
                        .Within(singleExpected * 1e-4f));
                AssertAllFinite(twoParticleValues);
                AssertAllFinite(translatedValues);
            }
        }

        [Test]
        public void StylizedCrownRaisesSupportedCenterAboveOnlyAndZeroHeightMatchesLegacy()
        {
            FluidSurfaceGridSettings grid = CreateGrid(new Vector3(-2f, -2f, -2f));
            IgnoreWithoutSurfaceSupport(in grid);
            ComputeShader density = LoadShader(DensityShaderPath);
            ComputeShader spatialHash = LoadShader(SpatialHashShaderPath);

            using (var particles = new FluidGpuResourceSet(ParticleCapacity, HashCapacity, 1))
            using (var surface = new FluidSurfaceGpuResources(in grid, ParticleCapacity))
            {
                UploadParticles(particles, CreatePlanarPatch());
                BuildSpatialHash(spatialHash, particles);
                var supports = new float[ParticleCapacity];
                supports[12] = 1f;
                surface.SurfaceSupportBuffer.SetData(supports);

                DispatchDensity(density, particles, surface, in grid, false, 0.8f, 1.5f);
                float[] legacy = ReadDensityTestOnly(surface.DensityTexture);
                DispatchDensity(density, particles, surface, in grid, true, 0f, 1.5f);
                float[] zeroHeight = ReadDensityTestOnly(surface.DensityTexture);
                Assert.That(zeroHeight, Is.EqualTo(legacy).Within(1e-6f));

                DispatchDensity(density, particles, surface, in grid, true, 0.8f, 1.5f);
                float[] crowned = ReadDensityTestOnly(surface.DensityTexture);

                float legacyCenterAbove = Read(legacy, grid.Resolution, 4, 5, 4);
                float crownedCenterAbove = Read(crowned, grid.Resolution, 4, 5, 4);
                float legacyEdgeAbove = Read(legacy, grid.Resolution, 2, 5, 2);
                float crownedEdgeAbove = Read(crowned, grid.Resolution, 2, 5, 2);
                float legacyCenterBelow = Read(legacy, grid.Resolution, 4, 3, 4);
                float crownedCenterBelow = Read(crowned, grid.Resolution, 4, 3, 4);

                Assert.That(crownedCenterAbove, Is.GreaterThan(legacyCenterAbove * 1.1f));
                Assert.That(crownedEdgeAbove, Is.EqualTo(legacyEdgeAbove).Within(1e-5f));
                Assert.That(crownedCenterBelow, Is.EqualTo(legacyCenterBelow).Within(1e-5f));
                AssertAllFinite(crowned);
            }
        }

        [Test]
        public void TargetMaterialFilterSeparatesWaterAndPoisonInOneParticlePool()
        {
            FluidSurfaceGridSettings grid = CreateGrid(new Vector3(-2f, -2f, -2f));
            IgnoreWithoutSurfaceSupport(in grid);
            var rows = new FluidGpuLiquidMaterialParameters[256];
            LiquidMaterialSettings water = CreateLiquid(MaterialId.Water, 0.25f);
            LiquidMaterialSettings poison = CreateLiquid(MaterialId.Poison, 0.5f);
            rows[(byte)MaterialId.Water] = new FluidGpuLiquidMaterialParameters(in water);
            rows[(byte)MaterialId.Poison] = new FluidGpuLiquidMaterialParameters(in poison);
            using (var particles = new FluidGpuResourceSet(ParticleCapacity, HashCapacity, 1,
                       liquidMaterialParameters: rows))
            using (var surface = new FluidSurfaceGpuResources(in grid, ParticleCapacity))
            {
                UploadParticles(particles,
                    new[] { Vector3.zero, new Vector3(1.5f, 0f, 0f) },
                    new[] { MaterialId.Water, MaterialId.Poison });
                BuildSpatialHash(LoadShader(SpatialHashShaderPath), particles);
                ComputeShader shader = LoadShader(DensityShaderPath);
                DispatchDensity(shader, particles, surface, in grid, targetMaterial: MaterialId.Water);
                float[] waterDensity = ReadDensityTestOnly(surface.DensityTexture);
                DispatchDensity(shader, particles, surface, in grid, targetMaterial: MaterialId.Poison);
                float[] poisonDensity = ReadDensityTestOnly(surface.DensityTexture);
                Assert.That(Read(waterDensity, grid.Resolution, 4, 4, 4), Is.GreaterThan(0f));
                Assert.That(Read(waterDensity, grid.Resolution, 7, 4, 4), Is.Zero.Within(1e-6f));
                Assert.That(Read(poisonDensity, grid.Resolution, 4, 4, 4), Is.Zero.Within(1e-6f));
                Assert.That(Read(poisonDensity, grid.Resolution, 7, 4, 4), Is.GreaterThan(0f));
            }
        }

        private static FluidSurfaceGridSettings CreateGrid(Vector3 origin)
        {
            return new FluidSurfaceGridSettings(
                new Vector3Int(9, 9, 9),
                origin,
                Vector3.one * 0.5f,
                maximumTriangleCount: 32);
        }

        private static void UploadParticles(
            FluidGpuResourceSet particles,
            Vector3[] activePositions,
            MaterialId[] materials = null)
        {
            var positions = new Vector4[ParticleCapacity];
            var metadata = new FluidGpuUInt2[ParticleCapacity];
            for (int i = 0; i < activePositions.Length; i++)
            {
                positions[i] = new Vector4(
                    activePositions[i].x,
                    activePositions[i].y,
                    activePositions[i].z,
                    1f);
                metadata[i] = new FluidGpuUInt2(
                    materials == null ? 0u : (uint)materials[i],
                    FluidGpuLayout.ActiveFlag);
            }

            particles.PredictedPositions.SetData(positions);
            particles.Metadata.SetData(metadata);
        }

        private static Vector3[] CreatePlanarPatch()
        {
            var positions = new Vector3[25];
            int index = 0;
            for (int z = -2; z <= 2; z++)
            for (int x = -2; x <= 2; x++)
                positions[index++] = new Vector3(x * 0.5f, 0f, z * 0.5f);
            return positions;
        }

        private static void BuildSpatialHash(ComputeShader shader, FluidGpuResourceSet resources)
        {
            int build = shader.FindKernel("BuildSpatialEntries");
            int sort = shader.FindKernel("BitonicSort");
            int clear = shader.FindKernel("ClearCellRanges");
            int ranges = shader.FindKernel("BuildCellRanges");
            int particleGroups = DivideRoundUp(resources.ParticleCapacity, 64);
            int hashGroups = DivideRoundUp(resources.HashTableCapacity, 64);

            shader.SetInt("_ParticleCapacity", resources.ParticleCapacity);
            shader.SetInt("_HashTableCapacity", resources.HashTableCapacity);
            shader.SetFloat("_SmoothingRadius", SmoothingRadius);
            shader.SetBuffer(build, "_PredictedPositions", resources.PredictedPositions);
            shader.SetBuffer(build, "_ParticleMetadata", resources.Metadata);
            shader.SetBuffer(build, "_SpatialCells", resources.SpatialCells);
            shader.SetBuffer(build, "_SpatialEntries", resources.SpatialEntries);
            shader.Dispatch(build, particleGroups, 1, 1);

            shader.SetBuffer(sort, "_SpatialEntries", resources.SpatialEntries);
            for (int stage = 2; stage <= resources.ParticleCapacity; stage <<= 1)
            {
                for (int pass = stage >> 1; pass > 0; pass >>= 1)
                {
                    shader.SetInt("_BitonicStage", stage);
                    shader.SetInt("_BitonicPass", pass);
                    shader.Dispatch(sort, particleGroups, 1, 1);
                }
            }

            shader.SetBuffer(clear, "_CellRanges", resources.CellRanges);
            shader.Dispatch(clear, hashGroups, 1, 1);
            shader.SetBuffer(ranges, "_SpatialEntries", resources.SpatialEntries);
            shader.SetBuffer(ranges, "_CellRanges", resources.CellRanges);
            shader.Dispatch(ranges, particleGroups, 1, 1);
        }

        private static void DispatchDensity(
            ComputeShader shader,
            FluidGpuResourceSet particles,
            FluidSurfaceGpuResources surface,
            in FluidSurfaceGridSettings grid,
            bool useStylizedCrown = false,
            float crownHeightRatio = 0f,
            float crownFalloff = 1.5f,
            MaterialId targetMaterial = MaterialId.Empty)
        {
            int kernel = shader.FindKernel("GatherIsotropicDensity");
            shader.SetInt("_ParticleCapacity", particles.ParticleCapacity);
            shader.SetInt("_HashTableCapacity", particles.HashTableCapacity);
            shader.SetFloat("_SmoothingRadius", SmoothingRadius);
            shader.SetFloat("_ParticleMass", ParticleMass);
            shader.SetInt("_TargetMaterialId", (byte)targetMaterial);
            shader.SetInt("_DensityCellRadius", useStylizedCrown ? 2 : 1);
            shader.SetInt("_UseStylizedCrown", useStylizedCrown ? 1 : 0);
            shader.SetFloat("_CrownHeightRatio", crownHeightRatio);
            shader.SetFloat("_CrownFalloff", crownFalloff);
            shader.SetInt("_GridResolutionX", grid.Resolution.x);
            shader.SetInt("_GridResolutionY", grid.Resolution.y);
            shader.SetInt("_GridResolutionZ", grid.Resolution.z);
            shader.SetVector("_WorldOrigin", grid.WorldOrigin);
            shader.SetVector("_VoxelSize", grid.VoxelSize);
            shader.SetBuffer(kernel, "_PredictedPositions", particles.PredictedPositions);
            shader.SetBuffer(kernel, "_ParticleMetadata", particles.Metadata);
            shader.SetBuffer(kernel, "_SpatialEntries", particles.SpatialEntries);
            shader.SetBuffer(kernel, "_CellRanges", particles.CellRanges);
            shader.SetBuffer(kernel, "_SpatialCells", particles.SpatialCells);
            shader.SetBuffer(kernel, "_LiquidMaterialParameters", particles.LiquidMaterialParameters);
            shader.SetBuffer(kernel, "_SurfaceSupports", surface.SurfaceSupportBuffer);
            shader.SetTexture(kernel, "_DensityTexture", surface.DensityTexture);
            shader.Dispatch(
                kernel,
                DivideRoundUp(grid.Resolution.x, 4),
                DivideRoundUp(grid.Resolution.y, 4),
                DivideRoundUp(grid.Resolution.z, 4));
        }

        private static LiquidMaterialSettings CreateLiquid(MaterialId material, float mass)
        {
            return new LiquidMaterialSettings(material, 8u, mass, 1000f, 0.1f, 0.001f,
                12f, 1f, 0.2f, 0.02f);
        }

        private static float[] ReadDensityTestOnly(RenderTexture texture)
        {
            // Unity 6.3 DX12 的 Texture3D Readback 即使请求 depth>1 也只返回一个 z slice。
            // 测试边界逐 slice 同步读取并按 Unity 的线性布局拼回完整 Volume；Runtime 不走此同步路径。
            int sliceLength = checked(texture.width * texture.height);
            var result = new float[checked(sliceLength * texture.volumeDepth)];
            for (int z = 0; z < texture.volumeDepth; z++)
            {
                AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(
                    texture, 0, 0, texture.width, 0, texture.height, z, 1, null);
                request.WaitForCompletion();
                Assert.That(request.hasError, Is.False, "Test-only 3D density slice readback failed.");
                NativeArray<float> data = request.GetData<float>();
                Assert.That(data.Length, Is.EqualTo(sliceLength));
                for (int index = 0; index < sliceLength; index++)
                    result[z * sliceLength + index] = data[index];
            }

            return result;
        }

        private static float Read(float[] values, Vector3Int resolution, int x, int y, int z)
        {
            return values[x + resolution.x * (y + resolution.y * z)];
        }

        private static float IndependentPoly6(float distance, float smoothingRadius)
        {
            if (distance < 0f || distance >= smoothingRadius)
                return 0f;
            double h2 = smoothingRadius * smoothingRadius;
            double difference = h2 - distance * distance;
            double h9 = Math.Pow(smoothingRadius, 9d);
            return (float)(315d / (64d * Math.PI * h9) * difference * difference * difference);
        }

        private static void AssertAllFinite(float[] values)
        {
            for (int i = 0; i < values.Length; i++)
                Assert.That(float.IsNaN(values[i]) || float.IsInfinity(values[i]), Is.False);
        }

        private static ComputeShader LoadShader(string path)
        {
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
            Assert.That(shader, Is.Not.Null, $"Missing ComputeShader at {path}.");
            return shader;
        }

        private static int DivideRoundUp(int value, int groupSize)
        {
            return (value + groupSize - 1) / groupSize;
        }

        private static void IgnoreWithoutSurfaceSupport(in FluidSurfaceGridSettings grid)
        {
            string reason = null;
            if (!SystemInfo.supportsComputeShaders
                || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null
                || !FluidSurfaceGpuResources.IsSupported(in grid, out reason))
            {
                Assert.Ignore(reason ?? "ComputeShader is unavailable; GPU density test is skipped, not passed.");
            }
        }
    }
}
