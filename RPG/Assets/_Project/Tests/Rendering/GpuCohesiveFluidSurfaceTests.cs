using System;
using System.IO;
using Game.ElementField;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Rendering.Tests
{
    /// <summary>
    /// Connected Component 只存在于 Test Assembly：Runtime 仍直接在 GPU 上重建并绘制表面，
    /// 不会为了测试指标同步回读 3D Density Texture，也不会在 Player 分配 Flood-fill Queue。
    /// </summary>
    public sealed class GpuCohesiveFluidSurfaceTests
    {
        private const string RenderProfilePath =
            "Assets/_Project/ScriptableObjects/ElementField/Fluid/LiquidRenderProfile_Water.asset";
        private const string LifecycleShaderPath =
            "Assets/_Project/Art/Elemental/Compute/PbfParticleLifecycle.compute";
        private const string SolverShaderPath =
            "Assets/_Project/Art/Elemental/Compute/PbfSolver.compute";
        private const string DensityShaderPath =
            "Assets/_Project/Art/Elemental/Compute/FluidDensity.compute";
        private const string SpatialHashShaderPath =
            "Assets/_Project/Art/Elemental/Compute/PbfSpatialHash.compute";

        [Test]
        public void TwoHundredPackedParticles_ProduceOneMajorGpuDensityComponent()
        {
            const int particleCapacity = 256;
            var grid = new FluidSurfaceGridSettings(
                new Vector3Int(17, 17, 17),
                Vector3.one * -0.8f,
                Vector3.one * 0.1f,
                1024);
            string reason = null;
            if (!SystemInfo.supportsComputeShaders
                || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null
                || !FluidSurfaceGpuResources.IsSupported(in grid, out reason))
            {
                Assert.Ignore(reason ?? "GPU surface resources are unavailable; skipped is not passed.");
            }

            ComputeShader densityShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(DensityShaderPath);
            ComputeShader spatialHash = AssetDatabase.LoadAssetAtPath<ComputeShader>(SpatialHashShaderPath);
            Assert.That(densityShader, Is.Not.Null);
            Assert.That(spatialHash, Is.Not.Null);

            using (var particles = new FluidGpuResourceSet(particleCapacity, 512, 1))
            using (var surface = new FluidSurfaceGpuResources(in grid, particleCapacity))
            {
                UploadPackedParticles(particles, 200, 0.1f, 2468u);
                BuildSpatialHash(spatialHash, particles, 0.25f);
                DispatchDensity(densityShader, particles, surface, in grid, 0.25f, 1f);
                float[] values = ReadDensityTestOnly(surface.DensityTexture);
                for (int i = 0; i < values.Length; i++)
                    Assert.That(float.IsNaN(values[i]) || float.IsInfinity(values[i]), Is.False);
                Assert.That(CountMajorComponents(values, grid.Resolution, 500f, 8), Is.EqualTo(1));
            }
        }

        [Test]
        public void StylizedCrown_RaisesPackedSurfaceWithoutSplittingItsMajorComponent()
        {
            const int particleCapacity = 256;
            var grid = new FluidSurfaceGridSettings(
                new Vector3Int(17, 17, 17),
                Vector3.one * -0.8f,
                Vector3.one * 0.1f,
                1024);
            string reason = null;
            if (!SystemInfo.supportsComputeShaders
                || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null
                || !FluidSurfaceGpuResources.IsSupported(in grid, out reason))
            {
                Assert.Ignore(reason ?? "GPU surface resources are unavailable; skipped is not passed.");
            }

            ComputeShader densityShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(DensityShaderPath);
            ComputeShader spatialHash = AssetDatabase.LoadAssetAtPath<ComputeShader>(SpatialHashShaderPath);
            Assert.That(densityShader, Is.Not.Null);
            Assert.That(spatialHash, Is.Not.Null);

            using (var particles = new FluidGpuResourceSet(particleCapacity, 512, 1))
            using (var surface = new FluidSurfaceGpuResources(in grid, particleCapacity))
            {
                UploadPackedParticles(particles, 200, 0.1f, 2468u);
                BuildSpatialHash(spatialHash, particles, 0.25f);

                // 这个 Test 只验证 Density Crown 的表面拓扑，因此直接注入“内部粒子”支持度；
                // 邻域计数到 Support 的真实性由 GpuFluidAnisotropyTests 独立锁定。
                var supports = new float[particleCapacity];
                for (int i = 0; i < 200; i++)
                    supports[i] = 1f;
                surface.SurfaceSupportBuffer.SetData(supports);

                DispatchDensity(densityShader, particles, surface, in grid, 0.25f, 1f);
                float[] legacy = ReadDensityTestOnly(surface.DensityTexture);
                float legacyCenterY = CalculateDensityWeightedY(legacy, grid.Resolution);

                DispatchDensity(
                    densityShader,
                    particles,
                    surface,
                    in grid,
                    0.25f,
                    1f,
                    useStylizedCrown: true,
                    crownHeightRatio: 0.8f);
                float[] crowned = ReadDensityTestOnly(surface.DensityTexture);

                for (int i = 0; i < crowned.Length; i++)
                    Assert.That(float.IsNaN(crowned[i]) || float.IsInfinity(crowned[i]), Is.False);
                // Voxel 是离散网格，冠高不足一个 Voxel 时最高占用层可能仍相同；Density 重心则能
                // 连续地度量“上半部支持域增加”，不会把 Grid Quantization 误判为功能失效。
                Assert.That(
                    CalculateDensityWeightedY(crowned, grid.Resolution),
                    Is.GreaterThan(legacyCenterY + 0.01f));
                Assert.That(CountMajorComponents(crowned, grid.Resolution, 500f, 8), Is.EqualTo(1));
            }
        }

        [Test]
        public void SixConnectedAnalysis_IgnoresNoiseAndDistinguishesSeparatedFromMergedWater()
        {
            var resolution = new Vector3Int(12, 6, 6);
            float[] density = new float[resolution.x * resolution.y * resolution.z];
            FillBlock(density, resolution, new Vector3Int(1, 1, 1), new Vector3Int(3, 3, 3), 600f);
            Set(density, resolution, 10, 5, 5, 600f); // 少于 8 voxel 的噪声液滴。
            Assert.That(CountMajorComponents(density, resolution, 500f, 8), Is.EqualTo(1));

            FillBlock(density, resolution, new Vector3Int(7, 1, 1), new Vector3Int(9, 3, 3), 600f);
            Assert.That(CountMajorComponents(density, resolution, 500f, 8), Is.EqualTo(2));

            // 6-connected 要求共享面；沿 X 补一条截面为 3x3 的桥，模拟两团进入支持域后融合。
            FillBlock(density, resolution, new Vector3Int(4, 1, 1), new Vector3Int(6, 3, 3), 600f);
            Assert.That(CountMajorComponents(density, resolution, 500f, 8), Is.EqualTo(1));
        }

        [Test]
        public void PhaseFProfilesAndComputeContractsAreImportable()
        {
            LiquidRenderProfile profile = AssetDatabase.LoadAssetAtPath<LiquidRenderProfile>(RenderProfilePath);
            Assert.That(profile, Is.Not.Null);
            LiquidRenderSettings settings = profile.CreateSettings();
            Assert.That(settings.TargetVoxelSize, Is.EqualTo(0.1f).Within(1e-6f));
            Assert.That(settings.MaximumResolutionPerAxis, Is.EqualTo(96));
            Assert.That(settings.IsoLevel, Is.EqualTo(500f));
            Assert.That(settings.UseAnisotropy, Is.True);
            Assert.That(settings.AnisotropyUpdateIntervalFrames, Is.EqualTo(2));
            Assert.That(settings.AnisotropyNeighborThreshold, Is.EqualTo(5));
            Assert.That(settings.MinimumAnisotropyScale, Is.GreaterThan(0f));
            Assert.That(settings.MinimumAnisotropyScale, Is.LessThanOrEqualTo(settings.MaximumAnisotropyScale),
                "Render Profile 是可调美术数据；测试只锁定合法关系，不能覆盖用户已经验证过的调参。");
            Assert.That(settings.MaximumAnisotropyScale, Is.EqualTo(1.8f));
            Assert.That(settings.MaximumAnisotropyRatio, Is.EqualTo(2.5f));
            Assert.That(settings.UseStylizedCrown, Is.True);
            Assert.That(settings.CrownHeightRatio, Is.GreaterThan(0f));
            Assert.That(settings.CrownFalloff, Is.EqualTo(1.5f).Within(1e-6f));
            Assert.That(settings.CrownEdgeNeighborCount, Is.EqualTo(4));
            Assert.That(settings.CrownInteriorNeighborCount, Is.EqualTo(12));

            ComputeShader lifecycle = AssetDatabase.LoadAssetAtPath<ComputeShader>(LifecycleShaderPath);
            ComputeShader solver = AssetDatabase.LoadAssetAtPath<ComputeShader>(SolverShaderPath);
            Assert.That(lifecycle, Is.Not.Null);
            Assert.That(solver, Is.Not.Null);
            Assert.That(lifecycle.HasKernel("SpawnParticles"), Is.True);
            Assert.That(solver.HasKernel("ComputeCohesionDeltaVelocities"), Is.True);
        }

        [Test]
        public void LiquidRendererUsesOneMarchingSurfaceAndContainsNoDistanceLodPath()
        {
            string shader = File.ReadAllText(
                "Assets/_Project/Art/Elemental/Shaders/ElementLiquidProcedural.shader");
            string renderer = File.ReadAllText(
                "Assets/_Project/Scripts/Rendering/ElementField/GpuLiquidSurfaceRenderer.cs");
            string profile = File.ReadAllText(
                "Assets/_Project/Scripts/Rendering/ElementField/LiquidRenderProfile.cs");

            StringAssert.DoesNotContain("_midResources", renderer);
            StringAssert.DoesNotContain("DrawFarParticleSplats", renderer);
            StringAssert.DoesNotContain("FluidSurfaceClipmapPlanner", renderer);
            StringAssert.DoesNotContain("_SurfaceLodMode", shader);
            StringAssert.DoesNotContain("_FarSplatRadius", shader);
            StringAssert.Contains("DispatchSurfaceNeighborhood", renderer);
            StringAssert.Contains("DispatchDensity", renderer);
            StringAssert.Contains("DispatchMarchingCubes", renderer);
            StringAssert.Contains("UseStylizedCrown", profile);
        }

        [Test]
        public void FluidComputeSourcesUsePortableFiniteChecksAndExplicitMathDomains()
        {
            string[] numericalShaderPaths =
            {
                SolverShaderPath,
                DensityShaderPath,
                "Assets/_Project/Art/Elemental/Compute/FluidAnisotropy.compute",
                "Assets/_Project/Art/Elemental/Compute/FluidMarchingCubes.compute",
                "Assets/_Project/Art/Elemental/Compute/PbfCollision.compute",
            };

            for (int i = 0; i < numericalShaderPaths.Length; i++)
            {
                string source = File.ReadAllText(numericalShaderPaths[i]);
                StringAssert.DoesNotContain("isnan(", source,
                    $"{numericalShaderPaths[i]} 不得依赖可能被 D3D fast-math 优化掉的 isnan。 ");
                StringAssert.DoesNotContain("isinf(", source,
                    $"{numericalShaderPaths[i]} 不得依赖可能被 D3D fast-math 优化掉的 isinf。 ");
                StringAssert.Contains("asuint(value)", source,
                    $"{numericalShaderPaths[i]} 必须使用 IEEE 754 bit pattern 检测 NaN/Infinity。 ");
            }

            string solver = File.ReadAllText(SolverShaderPath);
            string anisotropy = File.ReadAllText(
                "Assets/_Project/Art/Elemental/Compute/FluidAnisotropy.compute");
            string lifecycle = File.ReadAllText(LifecycleShaderPath);
            string fireSurface = File.ReadAllText(
                "Assets/_Project/Art/Elemental/Compute/FireParcelMarchingCubes.compute");
            StringAssert.DoesNotContain("pow(", solver,
                "PBF 固定整数次幂应展开为乘法；分数次幂必须走显式正数域 helper。 ");
            StringAssert.DoesNotContain("pow(", anisotropy,
                "Anisotropy 比例幂必须先限定正数域并使用不触发负底数 warning 的实现。 ");
            StringAssert.DoesNotContain("pow(", lifecycle,
                "Spawn 的立方根必须显式限定非负域，避免 Shader compiler 保守 warning。 ");
            StringAssert.Contains("uint3 maximumCell", fireSurface,
                "Fire Marching Cubes 的 Dispatch ID 与 Grid 上界必须显式统一为 uint。 ");
        }

        private static int CountMajorComponents(
            float[] density,
            Vector3Int resolution,
            float isoLevel,
            int minimumVoxelCount)
        {
            if (density == null)
                throw new ArgumentNullException(nameof(density));
            int length = checked(resolution.x * resolution.y * resolution.z);
            if (resolution.x <= 0 || resolution.y <= 0 || resolution.z <= 0 || density.Length != length)
                throw new ArgumentException("Density array and resolution do not match.");

            var visited = new bool[length];
            var queue = new int[length];
            int componentCount = 0;
            for (int start = 0; start < length; start++)
            {
                if (visited[start] || density[start] < isoLevel)
                    continue;

                int head = 0;
                int tail = 0;
                int voxelCount = 0;
                queue[tail++] = start;
                visited[start] = true;
                while (head < tail)
                {
                    int index = queue[head++];
                    voxelCount++;
                    int z = index / (resolution.x * resolution.y);
                    int remainder = index - z * resolution.x * resolution.y;
                    int y = remainder / resolution.x;
                    int x = remainder - y * resolution.x;
                    TryEnqueue(x - 1, y, z);
                    TryEnqueue(x + 1, y, z);
                    TryEnqueue(x, y - 1, z);
                    TryEnqueue(x, y + 1, z);
                    TryEnqueue(x, y, z - 1);
                    TryEnqueue(x, y, z + 1);
                }

                if (voxelCount >= minimumVoxelCount)
                    componentCount++;

                void TryEnqueue(int x, int y, int z)
                {
                    if ((uint)x >= (uint)resolution.x
                        || (uint)y >= (uint)resolution.y
                        || (uint)z >= (uint)resolution.z)
                    {
                        return;
                    }

                    int neighbor = x + resolution.x * (y + resolution.y * z);
                    if (visited[neighbor] || density[neighbor] < isoLevel)
                        return;
                    visited[neighbor] = true;
                    queue[tail++] = neighbor;
                }
            }

            return componentCount;
        }

        private static float CalculateDensityWeightedY(float[] density, Vector3Int resolution)
        {
            double weightedY = 0d;
            double totalDensity = 0d;
            for (int y = 0; y < resolution.y; y++)
            for (int z = 0; z < resolution.z; z++)
            for (int x = 0; x < resolution.x; x++)
            {
                int index = x + resolution.x * (y + resolution.y * z);
                float value = Mathf.Max(0f, density[index]);
                weightedY += value * y;
                totalDensity += value;
            }

            return totalDensity > 0d ? (float)(weightedY / totalDensity) : -1f;
        }

        private static void FillBlock(
            float[] values,
            Vector3Int resolution,
            Vector3Int minimum,
            Vector3Int maximum,
            float value)
        {
            for (int z = minimum.z; z <= maximum.z; z++)
            for (int y = minimum.y; y <= maximum.y; y++)
            for (int x = minimum.x; x <= maximum.x; x++)
                Set(values, resolution, x, y, z, value);
        }

        private static void Set(
            float[] values,
            Vector3Int resolution,
            int x,
            int y,
            int z,
            float value)
        {
            values[x + resolution.x * (y + resolution.y * z)] = value;
        }

        private static void UploadPackedParticles(
            FluidGpuResourceSet particles,
            int count,
            float restSpacing,
            uint seed)
        {
            var positions = new Vector4[particles.ParticleCapacity];
            var metadata = new FluidGpuUInt2[particles.ParticleCapacity];
            int side = Mathf.CeilToInt(Mathf.Pow(count, 1f / 3f));
            int sideSquared = side * side;
            int totalSlots = sideSquared * side;
            int step = sideSquared + side + 1;
            float center = 0.5f * (side - 1);
            for (int localIndex = 0; localIndex < count; localIndex++)
            {
                int slot = (int)(((uint)localIndex * (uint)step + seed % (uint)totalSlots) % (uint)totalSlots);
                int x = slot % side;
                int y = (slot / side) % side;
                int z = slot / sideSquared;
                Vector3 position = (new Vector3(x, y, z) - Vector3.one * center) * restSpacing;
                positions[localIndex] = new Vector4(position.x, position.y, position.z, 1f);
                metadata[localIndex] = new FluidGpuUInt2(1u, FluidGpuLayout.ActiveFlag);
            }

            particles.PredictedPositions.SetData(positions);
            particles.Metadata.SetData(metadata);
        }

        private static void BuildSpatialHash(
            ComputeShader shader,
            FluidGpuResourceSet resources,
            float smoothingRadius)
        {
            int build = shader.FindKernel("BuildSpatialEntries");
            int sort = shader.FindKernel("BitonicSort");
            int clear = shader.FindKernel("ClearCellRanges");
            int ranges = shader.FindKernel("BuildCellRanges");
            int particleGroups = DivideRoundUp(resources.ParticleCapacity, 64);
            shader.SetInt("_ParticleCapacity", resources.ParticleCapacity);
            shader.SetInt("_HashTableCapacity", resources.HashTableCapacity);
            shader.SetFloat("_SmoothingRadius", smoothingRadius);
            shader.SetBuffer(build, "_PredictedPositions", resources.PredictedPositions);
            shader.SetBuffer(build, "_ParticleMetadata", resources.Metadata);
            shader.SetBuffer(build, "_SpatialCells", resources.SpatialCells);
            shader.SetBuffer(build, "_SpatialEntries", resources.SpatialEntries);
            shader.Dispatch(build, particleGroups, 1, 1);
            shader.SetBuffer(sort, "_SpatialEntries", resources.SpatialEntries);
            for (int stage = 2; stage <= resources.ParticleCapacity; stage <<= 1)
            for (int pass = stage >> 1; pass > 0; pass >>= 1)
            {
                shader.SetInt("_BitonicStage", stage);
                shader.SetInt("_BitonicPass", pass);
                shader.Dispatch(sort, particleGroups, 1, 1);
            }
            shader.SetBuffer(clear, "_CellRanges", resources.CellRanges);
            shader.Dispatch(clear, DivideRoundUp(resources.HashTableCapacity, 64), 1, 1);
            shader.SetBuffer(ranges, "_SpatialEntries", resources.SpatialEntries);
            shader.SetBuffer(ranges, "_CellRanges", resources.CellRanges);
            shader.Dispatch(ranges, particleGroups, 1, 1);
        }

        private static void DispatchDensity(
            ComputeShader shader,
            FluidGpuResourceSet particles,
            FluidSurfaceGpuResources surface,
            in FluidSurfaceGridSettings grid,
            float smoothingRadius,
            float particleMass,
            bool useStylizedCrown = false,
            float crownHeightRatio = 0f)
        {
            int kernel = shader.FindKernel("GatherIsotropicDensity");
            shader.SetInt("_ParticleCapacity", particles.ParticleCapacity);
            shader.SetInt("_HashTableCapacity", particles.HashTableCapacity);
            shader.SetFloat("_SmoothingRadius", smoothingRadius);
            shader.SetFloat("_ParticleMass", particleMass);
            // 0 是 Legacy/Test wildcard；Production Renderer 会传具体 MaterialId 做独立 Surface。
            shader.SetInt("_TargetMaterialId", 0);
            shader.SetInt("_DensityCellRadius", useStylizedCrown ? 2 : 1);
            shader.SetInt("_UseStylizedCrown", useStylizedCrown ? 1 : 0);
            shader.SetFloat("_CrownHeightRatio", crownHeightRatio);
            shader.SetFloat("_CrownFalloff", 1.5f);
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

        private static float[] ReadDensityTestOnly(RenderTexture texture)
        {
            int sliceLength = texture.width * texture.height;
            var result = new float[sliceLength * texture.volumeDepth];
            for (int z = 0; z < texture.volumeDepth; z++)
            {
                AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(
                    texture, 0, 0, texture.width, 0, texture.height, z, 1, null);
                request.WaitForCompletion();
                Assert.That(request.hasError, Is.False);
                NativeArray<float> slice = request.GetData<float>();
                for (int i = 0; i < sliceLength; i++)
                    result[z * sliceLength + i] = slice[i];
            }
            return result;
        }

        private static int DivideRoundUp(int value, int divisor)
        {
            return (value + divisor - 1) / divisor;
        }
    }
}
