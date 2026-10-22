using System;
using System.Runtime.InteropServices;
using Game.ElementField;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Rendering.Tests
{
    /// <summary>
    /// Anisotropy 的真实 GPU 数值 fixture。非紧凑 active slot、真实 Spatial Hash 和真实 active index
    /// 可防止测试误读 buffer[0] 仍得到默认零值的假阳性；同步 Readback 只允许存在于 Test-only。
    /// </summary>
    public sealed class GpuFluidAnisotropyTests
    {
        private const string AnisotropyShaderPath =
            "Assets/_Project/Art/Elemental/Compute/FluidAnisotropy.compute";
        private const string DensityShaderPath =
            "Assets/_Project/Art/Elemental/Compute/FluidDensity.compute";
        private const string SpatialHashShaderPath =
            "Assets/_Project/Art/Elemental/Compute/PbfSpatialHash.compute";
        private const int ParticleCapacity = 16;
        private const int HashCapacity = 32;
        private const float SmoothingRadius = 1f;
        private const float ParticleMass = 0.25f;
        private const int NeighborThreshold = 5;
        private const float MinimumScale = 0.65f;
        private const float MaximumScale = 1.8f;
        private const float MaximumRatio = 2.5f;

        private static readonly int[] SymmetricSlots = { 1, 3, 5, 7, 9, 11, 13 };

        [Test]
        public void PlanarPatchWritesHigherSupportAtCenterAndZeroForEdgeAndIsolatedParticle()
        {
            FluidSurfaceGridSettings grid = CreateGrid();
            IgnoreWithoutSurfaceSupport(in grid);
            ComputeShader anisotropy = LoadShader(AnisotropyShaderPath);
            ComputeShader spatialHash = LoadShader(SpatialHashShaderPath);
            int[] slots = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 15 };
            Vector3[] positions = CreatePlanarPatchWithIsolatedParticle();

            using (var particles = new FluidGpuResourceSet(ParticleCapacity, HashCapacity, 1))
            using (var anisotropicSurface = new FluidSurfaceGpuResources(in grid, ParticleCapacity))
            using (var supportOnlySurface = new FluidSurfaceGpuResources(in grid, ParticleCapacity))
            {
                UploadNonCompactParticles(particles, slots, positions);
                BuildSpatialHash(spatialHash, particles);
                DispatchAnisotropy(anisotropy, particles, anisotropicSurface);
                DispatchSurfaceSupport(anisotropy, particles, supportOnlySurface);

                float[] anisotropicSupports =
                    ReadFloatTestOnly(anisotropicSurface.SurfaceSupportBuffer);
                float[] supportOnly =
                    ReadFloatTestOnly(supportOnlySurface.SurfaceSupportBuffer);

                Assert.That(anisotropicSupports[4], Is.GreaterThan(anisotropicSupports[0]));
                Assert.That(anisotropicSupports[0], Is.EqualTo(0f).Within(1e-6f));
                Assert.That(anisotropicSupports[15], Is.EqualTo(0f).Within(1e-6f));
                for (int i = 0; i < slots.Length; i++)
                {
                    int slot = slots[i];
                    Assert.That(float.IsNaN(anisotropicSupports[slot])
                        || float.IsInfinity(anisotropicSupports[slot]), Is.False);
                    Assert.That(anisotropicSupports[slot], Is.InRange(0f, 1f));
                    Assert.That(supportOnly[slot],
                        Is.EqualTo(anisotropicSupports[slot]).Within(1e-5f));
                }
            }
        }

        [Test]
        public void SurfaceResourcesDisposeReleasesSupportBuffer()
        {
            FluidSurfaceGridSettings grid = CreateGrid();
            IgnoreWithoutSurfaceSupport(in grid);
            var resources = new FluidSurfaceGpuResources(in grid, ParticleCapacity);

            Assert.That(resources.SurfaceSupportBuffer, Is.Not.Null);
            resources.Dispose();

            Assert.That(resources.SurfaceSupportBuffer, Is.Null);
        }

        [Test]
        public void UpdateScheduleRunsImmediatelyAndAgainImmediatelyAfterResourceReset()
        {
            var schedule = new FluidAnisotropyUpdateSchedule(3);

            Assert.That(schedule.ShouldUpdateAndAdvance(7u), Is.True);
            Assert.That(schedule.ShouldUpdateAndAdvance(7u), Is.False);
            Assert.That(schedule.ShouldUpdateAndAdvance(7u), Is.False);
            Assert.That(schedule.ShouldUpdateAndAdvance(7u), Is.True);

            schedule.Reset();
            Assert.That(schedule.ShouldUpdateAndAdvance(7u), Is.True,
                "A newly allocated transform buffer must be initialized before Density reads it.");
        }

        [Test]
        public void UpdateScheduleForcesRefreshWhenParticleTopologyVersionChangesIncludingWrap()
        {
            var schedule = new FluidAnisotropyUpdateSchedule(4);

            Assert.That(schedule.ShouldUpdateAndAdvance(uint.MaxValue), Is.True);
            Assert.That(schedule.ShouldUpdateAndAdvance(uint.MaxValue), Is.False);
            Assert.That(schedule.ShouldUpdateAndAdvance(0u), Is.True,
                "A spawned/reused slot must not wait for the normal visual update interval.");
            Assert.That(schedule.ShouldUpdateAndAdvance(0u), Is.False,
                "After the forced refresh, the ordinary frame interval must remain in effect.");
        }

        [Test]
        public void TransformLayoutIsExactlyFourFloat4RowsAndProfileSnapshotCarriesAbControls()
        {
            Assert.That(Marshal.SizeOf<FluidAnisotropyTransform>(), Is.EqualTo(64));
            Assert.That(FluidSurfaceGpuResources.AnisotropyStride, Is.EqualTo(64));

            var settings = new LiquidRenderSettings(
                0.1f,
                64,
                2048,
                0.25f,
                1f,
                useAnisotropy: true,
                anisotropyUpdateIntervalFrames: 3,
                anisotropyNeighborThreshold: 6,
                minimumAnisotropyScale: 0.7f,
                maximumAnisotropyScale: 1.7f,
                maximumAnisotropyRatio: 2.2f);

            Assert.That(settings.UseAnisotropy, Is.True);
            Assert.That(settings.AnisotropyUpdateIntervalFrames, Is.EqualTo(3));
            Assert.That(settings.AnisotropyNeighborThreshold, Is.EqualTo(6));
            Assert.That(settings.MinimumAnisotropyScale, Is.EqualTo(0.7f));
            Assert.That(settings.MaximumAnisotropyScale, Is.EqualTo(1.7f));
            Assert.That(settings.MaximumAnisotropyRatio, Is.EqualTo(2.2f));
            Assert.That(settings.AnisotropyCellRadius, Is.EqualTo(2));

            Assert.Throws<ArgumentOutOfRangeException>(() => new LiquidRenderSettings(
                0.1f, 64, 2048, 0.25f, 1f, true, 0, 6, 0.7f, 1.7f, 2.2f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new LiquidRenderSettings(
                0.1f, 64, 2048, 0.25f, 1f, true, 3, 1, 0.7f, 1.7f, 2.2f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new LiquidRenderSettings(
                0.1f, 64, 2048, 0.25f, 1f, true, 3, 6, 1.1f, 1.7f, 2.2f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new LiquidRenderSettings(
                0.1f, 64, 2048, 0.25f, 1f, true, 3, 6, 0.7f, 0.9f, 2.2f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new LiquidRenderSettings(
                0.1f, 64, 2048, 0.25f, 1f, true, 3, 6, 0.7f, 4.1f, 2.2f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new LiquidRenderSettings(
                0.1f, 64, 2048, 0.25f, 1f, true, 3, 6, 0.7f, 1.7f, 0.99f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new LiquidRenderSettings(
                0.1f, 64, 2048, 0.25f, 1f, true, 3, 6, 0.7f, 2.01f, 2.2f));
        }

        [Test]
        public void SymmetricNeighborhoodProducesUnitScalesSymmetricMatrixAndWeightedCenter()
        {
            FluidSurfaceGridSettings grid = CreateGrid();
            IgnoreWithoutSurfaceSupport(in grid);
            ComputeShader anisotropy = LoadShader(AnisotropyShaderPath);
            ComputeShader spatialHash = LoadShader(SpatialHashShaderPath);
            Vector3 center = new Vector3(0.25f, -0.1f, 0.3f);
            Vector3[] positions =
            {
                center,
                center + Vector3.right * 0.3f,
                center - Vector3.right * 0.3f,
                center + Vector3.up * 0.3f,
                center - Vector3.up * 0.3f,
                center + Vector3.forward * 0.3f,
                center - Vector3.forward * 0.3f
            };

            using (var particles = new FluidGpuResourceSet(ParticleCapacity, HashCapacity, 1))
            using (var surface = new FluidSurfaceGpuResources(in grid, ParticleCapacity))
            {
                UploadNonCompactParticles(particles, SymmetricSlots, positions);
                BuildSpatialHash(spatialHash, particles);
                DispatchAnisotropy(anisotropy, particles, surface);

                FluidAnisotropyTransform[] transforms =
                    ReadTransformsTestOnly(surface.AnisotropyBuffer);
                uint[] counters = ReadUIntTestOnly(surface.AnisotropyCounters);
                FluidAnisotropyTransform value = transforms[SymmetricSlots[0]];

                Assert.That(counters, Is.EqualTo(new[] { 7u, 0u, 0u, 7u }));
                AssertFinite(in value);
                Assert.That(value.ScaleValidity.w, Is.EqualTo(1f));
                Assert.That(value.ScaleValidity.x, Is.EqualTo(1f).Within(0.03f));
                Assert.That(value.ScaleValidity.y, Is.EqualTo(1f).Within(0.03f));
                Assert.That(value.ScaleValidity.z, Is.EqualTo(1f).Within(0.03f));
                Assert.That(value.Row0.y, Is.EqualTo(value.Row1.x).Within(1e-4f));
                Assert.That(value.Row0.z, Is.EqualTo(value.Row2.x).Within(1e-4f));
                Assert.That(value.Row1.z, Is.EqualTo(value.Row2.y).Within(1e-4f));
                Assert.That(TransformPoint(in value, center).magnitude, Is.LessThan(1e-3f));
                Assert.That(LinearDeterminant(in value), Is.EqualTo(1f).Within(0.03f));
            }
        }

        [Test]
        public void LinearNeighborhoodElongatesAlongPrincipalAxisWithinRatioAndVolumeBounds()
        {
            FluidSurfaceGridSettings grid = CreateGrid();
            IgnoreWithoutSurfaceSupport(in grid);
            ComputeShader anisotropy = LoadShader(AnisotropyShaderPath);
            ComputeShader spatialHash = LoadShader(SpatialHashShaderPath);
            Vector3 center = new Vector3(0.25f, -0.1f, 0.3f);
            Vector3[] positions = CreateLinearPositions(center);

            using (var particles = new FluidGpuResourceSet(ParticleCapacity, HashCapacity, 1))
            using (var surface = new FluidSurfaceGpuResources(in grid, ParticleCapacity))
            {
                UploadNonCompactParticles(particles, SymmetricSlots, positions);
                BuildSpatialHash(spatialHash, particles);
                DispatchAnisotropy(anisotropy, particles, surface);

                FluidAnisotropyTransform[] transforms =
                    ReadTransformsTestOnly(surface.AnisotropyBuffer);
                uint[] counters = ReadUIntTestOnly(surface.AnisotropyCounters);
                FluidAnisotropyTransform value = transforms[SymmetricSlots[3]];

                Assert.That(counters, Is.EqualTo(new[] { 7u, 0u, 0u, 7u }));
                AssertFinite(in value);
                Assert.That(value.ScaleValidity.w, Is.EqualTo(1f));
                Assert.That(value.ScaleValidity.x, Is.GreaterThan(1.2f));
                Assert.That(value.ScaleValidity.y, Is.LessThan(0.95f));
                Assert.That(value.ScaleValidity.z, Is.EqualTo(value.ScaleValidity.y).Within(1e-4f));
                Assert.That(value.ScaleValidity.x, Is.InRange(MinimumScale, MaximumScale));
                Assert.That(value.ScaleValidity.y, Is.InRange(MinimumScale, MaximumScale));
                Assert.That(value.ScaleValidity.x / value.ScaleValidity.y,
                    Is.LessThanOrEqualTo(MaximumRatio + 1e-3f));
                Assert.That(value.ScaleValidity.x * value.ScaleValidity.y * value.ScaleValidity.z,
                    Is.EqualTo(1f).Within(0.02f));
                Assert.That(TransformVector(in value, Vector3.right).magnitude,
                    Is.LessThan(TransformVector(in value, Vector3.up).magnitude * 0.9f));
                Assert.That(TransformPoint(in value, center).magnitude, Is.LessThan(1e-3f));
                Assert.That(LinearDeterminant(in value), Is.EqualTo(1f).Within(0.03f));
            }
        }

        [Test]
        public void InsufficientNeighborsFallBackToIdentityAtOriginalParticleCenters()
        {
            FluidSurfaceGridSettings grid = CreateGrid();
            IgnoreWithoutSurfaceSupport(in grid);
            ComputeShader anisotropy = LoadShader(AnisotropyShaderPath);
            ComputeShader spatialHash = LoadShader(SpatialHashShaderPath);
            int[] slots = { 2, 14 };
            Vector3[] positions = { new Vector3(-1.2f, 0f, 0f), new Vector3(1.2f, 0f, 0f) };

            using (var particles = new FluidGpuResourceSet(ParticleCapacity, HashCapacity, 1))
            using (var surface = new FluidSurfaceGpuResources(in grid, ParticleCapacity))
            {
                UploadNonCompactParticles(particles, slots, positions);
                BuildSpatialHash(spatialHash, particles);
                DispatchAnisotropy(anisotropy, particles, surface);

                FluidAnisotropyTransform[] transforms =
                    ReadTransformsTestOnly(surface.AnisotropyBuffer);
                uint[] counters = ReadUIntTestOnly(surface.AnisotropyCounters);
                Assert.That(counters, Is.EqualTo(new[] { 2u, 2u, 0u, 0u }));
                for (int i = 0; i < slots.Length; i++)
                {
                    FluidAnisotropyTransform value = transforms[slots[i]];
                    AssertIdentityAtCenter(in value, positions[i]);
                }
            }
        }

        [Test]
        public void AnisotropicDensityUsesAffineKernelWhileIsotropicPathRemainsUnchanged()
        {
            FluidSurfaceGridSettings grid = CreateGrid();
            IgnoreWithoutSurfaceSupport(in grid);
            ComputeShader anisotropy = LoadShader(AnisotropyShaderPath);
            ComputeShader density = LoadShader(DensityShaderPath);
            ComputeShader spatialHash = LoadShader(SpatialHashShaderPath);
            // 所有粒子都位于 hash cell -1，sample 位于 cell +1。两者相差 2 cells：
            // 若 Anisotropic gather 仍错误复用 Isotropic 的固定 ±1 搜索，结果会假阴性为 0。
            Vector3[] positions = CreateLinearPositions(Vector3.left * 0.5f);

            using (var particles = new FluidGpuResourceSet(ParticleCapacity, HashCapacity, 1))
            using (var surface = new FluidSurfaceGpuResources(in grid, ParticleCapacity))
            {
                UploadNonCompactParticles(particles, SymmetricSlots, positions);
                BuildSpatialHash(spatialHash, particles);
                DispatchAnisotropy(anisotropy, particles, surface);

                DispatchDensity(density, "GatherIsotropicDensity", particles, surface, in grid, false);
                float[] isotropic = ReadDensityTestOnly(surface.DensityTexture);
                DispatchDensity(density, "GatherAnisotropicDensity", particles, surface, in grid, true);
                float[] anisotropic = ReadDensityTestOnly(surface.DensityTexture);
                int sample = LinearIndex(grid.Resolution, 12, 8, 8); // world (1.0, 0, 0)

                Assert.That(isotropic[sample], Is.EqualTo(0f).Within(1e-7f));
                Assert.That(anisotropic[sample], Is.GreaterThan(1e-5f));
                AssertAllFinite(isotropic);
                AssertAllFinite(anisotropic);
            }
        }

        [Test]
        public void AnisotropicDensityFindsAStretchedKernelAcrossTwoHashCells()
        {
            FluidSurfaceGridSettings grid = CreateGrid();
            IgnoreWithoutSurfaceSupport(in grid);
            ComputeShader density = LoadShader(DensityShaderPath);
            ComputeShader spatialHash = LoadShader(SpatialHashShaderPath);
            const int activeSlot = 14;
            var particlePosition = new Vector3(-0.05f, 0f, 0f); // floor(x/h)=-1

            using (var particles = new FluidGpuResourceSet(ParticleCapacity, HashCapacity, 1))
            using (var surface = new FluidSurfaceGpuResources(in grid, ParticleCapacity))
            {
                UploadNonCompactParticles(
                    particles,
                    new[] { activeSlot },
                    new[] { particlePosition });
                BuildSpatialHash(spatialHash, particles);

                var transforms = new FluidAnisotropyTransform[ParticleCapacity];
                // World->Kernel 的 X 系数是 1/1.8；Sample x=1.25 与粒子相隔 1.30h，
                // 变换后距离约 0.722h，理应仍在支持域内。两者 Hash Cell 分别为 -1 与 +1。
                transforms[activeSlot] = new FluidAnisotropyTransform(
                    new Vector4(1f / 1.8f, 0f, 0f, -particlePosition.x / 1.8f),
                    new Vector4(0f, Mathf.Sqrt(1.8f), 0f, 0f),
                    new Vector4(0f, 0f, Mathf.Sqrt(1.8f), 0f),
                    new Vector4(1.8f, 1f / Mathf.Sqrt(1.8f), 1f / Mathf.Sqrt(1.8f), 1f));
                surface.AnisotropyBuffer.SetData(transforms);

                DispatchDensity(density, "GatherIsotropicDensity", particles, surface, in grid, false);
                float[] isotropic = ReadDensityTestOnly(surface.DensityTexture);
                DispatchDensity(density, "GatherAnisotropicDensity", particles, surface, in grid, true);
                float[] anisotropic = ReadDensityTestOnly(surface.DensityTexture);
                int sample = LinearIndex(grid.Resolution, 13, 8, 8); // world (1.25,0,0)

                Assert.That(isotropic[sample], Is.EqualTo(0f).Within(1e-7f));
                Assert.That(anisotropic[sample], Is.GreaterThan(1e-5f));
                AssertAllFinite(anisotropic);
            }
        }

        [Test]
        public void InvalidTransformForNewNonCompactActiveSlotFallsBackToCurrentParticleCenter()
        {
            FluidSurfaceGridSettings grid = CreateGrid();
            IgnoreWithoutSurfaceSupport(in grid);
            ComputeShader density = LoadShader(DensityShaderPath);
            ComputeShader spatialHash = LoadShader(SpatialHashShaderPath);
            const int reusedSlot = 14;
            var particlePosition = new Vector3(0.5f, 0f, 0f);

            using (var particles = new FluidGpuResourceSet(ParticleCapacity, HashCapacity, 1))
            using (var surface = new FluidSurfaceGpuResources(in grid, ParticleCapacity))
            {
                // 模拟隔帧复用：slot 上一帧 inactive，所以 Transform 仍是全零；本帧它在非紧凑位置被 Spawn/reuse。
                // 即使版本层未来被错误绕过，Density 消费边界也必须用当前粒子中心做 Identity fallback。
                UploadNonCompactParticles(
                    particles,
                    new[] { reusedSlot },
                    new[] { particlePosition });
                BuildSpatialHash(spatialHash, particles);
                surface.AnisotropyBuffer.SetData(
                    new FluidAnisotropyTransform[ParticleCapacity]);

                DispatchDensity(density, "GatherIsotropicDensity", particles, surface, in grid, false);
                float[] isotropic = ReadDensityTestOnly(surface.DensityTexture);
                DispatchDensity(density, "GatherAnisotropicDensity", particles, surface, in grid, true);
                float[] anisotropic = ReadDensityTestOnly(surface.DensityTexture);
                int particleCenter = LinearIndex(grid.Resolution, 10, 8, 8); // world (0.5,0,0)
                int oldZeroAffinePeak = LinearIndex(grid.Resolution, 8, 8, 8); // world (0,0,0)

                Assert.That(anisotropic[particleCenter],
                    Is.EqualTo(isotropic[particleCenter]).Within(1e-5f));
                Assert.That(anisotropic[oldZeroAffinePeak],
                    Is.EqualTo(isotropic[oldZeroAffinePeak]).Within(1e-5f));
                Assert.That(anisotropic[particleCenter], Is.GreaterThan(anisotropic[oldZeroAffinePeak]));
                AssertAllFinite(anisotropic);
            }
        }

        private static FluidSurfaceGridSettings CreateGrid()
        {
            return new FluidSurfaceGridSettings(
                new Vector3Int(17, 17, 17),
                new Vector3(-2f, -2f, -2f),
                Vector3.one * 0.25f,
                maximumTriangleCount: 32);
        }

        private static Vector3[] CreateLinearPositions(Vector3 center)
        {
            return new[]
            {
                center + Vector3.right * -0.45f,
                center + Vector3.right * -0.3f,
                center + Vector3.right * -0.15f,
                center,
                center + Vector3.right * 0.15f,
                center + Vector3.right * 0.3f,
                center + Vector3.right * 0.45f
            };
        }

        private static Vector3[] CreatePlanarPatchWithIsolatedParticle()
        {
            const float spacing = 0.55f;
            return new[]
            {
                new Vector3(-spacing, 0f, -spacing),
                new Vector3(0f, 0f, -spacing),
                new Vector3(spacing, 0f, -spacing),
                new Vector3(-spacing, 0f, 0f),
                Vector3.zero,
                new Vector3(spacing, 0f, 0f),
                new Vector3(-spacing, 0f, spacing),
                new Vector3(0f, 0f, spacing),
                new Vector3(spacing, 0f, spacing),
                new Vector3(3f, 0f, 0f)
            };
        }

        private static void UploadNonCompactParticles(
            FluidGpuResourceSet particles,
            int[] activeSlots,
            Vector3[] activePositions)
        {
            Assert.That(activeSlots.Length, Is.EqualTo(activePositions.Length));
            var positions = new Vector4[ParticleCapacity];
            var metadata = new FluidGpuUInt2[ParticleCapacity];
            for (int i = 0; i < activeSlots.Length; i++)
            {
                int slot = activeSlots[i];
                positions[slot] = new Vector4(
                    activePositions[i].x,
                    activePositions[i].y,
                    activePositions[i].z,
                    1f);
                metadata[slot] = new FluidGpuUInt2(0u, FluidGpuLayout.ActiveFlag);
            }

            particles.PredictedPositions.SetData(positions);
            particles.Metadata.SetData(metadata);
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
            for (int pass = stage >> 1; pass > 0; pass >>= 1)
            {
                shader.SetInt("_BitonicStage", stage);
                shader.SetInt("_BitonicPass", pass);
                shader.Dispatch(sort, particleGroups, 1, 1);
            }
            shader.SetBuffer(clear, "_CellRanges", resources.CellRanges);
            shader.Dispatch(clear, hashGroups, 1, 1);
            shader.SetBuffer(ranges, "_SpatialEntries", resources.SpatialEntries);
            shader.SetBuffer(ranges, "_CellRanges", resources.CellRanges);
            shader.Dispatch(ranges, particleGroups, 1, 1);
        }

        private static void DispatchAnisotropy(
            ComputeShader shader,
            FluidGpuResourceSet particles,
            FluidSurfaceGpuResources surface)
        {
            int clear = shader.FindKernel("ClearAnisotropyCounters");
            int mean = shader.FindKernel("GatherAnisotropyMean");
            int transform = shader.FindKernel("BuildAnisotropyTransform");
            shader.SetInt("_ParticleCapacity", particles.ParticleCapacity);
            shader.SetInt("_HashTableCapacity", particles.HashTableCapacity);
            shader.SetInt("_NeighborThreshold", NeighborThreshold);
            shader.SetInt("_CrownEdgeNeighborCount", 4);
            shader.SetInt("_CrownInteriorNeighborCount", 12);
            shader.SetFloat("_SmoothingRadius", SmoothingRadius);
            shader.SetFloat("_MinimumAnisotropyScale", MinimumScale);
            shader.SetFloat("_MaximumAnisotropyScale", MaximumScale);
            shader.SetFloat("_MaximumAnisotropyRatio", MaximumRatio);
            shader.SetBuffer(clear, "_AnisotropyCounters", surface.AnisotropyCounters);
            shader.Dispatch(clear, 1, 1, 1);
            BindAnisotropyNeighborhood(shader, mean, particles);
            shader.SetBuffer(mean, "_AnisotropyTransforms", surface.AnisotropyBuffer);
            shader.SetBuffer(mean, "_SurfaceSupports", surface.SurfaceSupportBuffer);
            shader.Dispatch(mean, DivideRoundUp(ParticleCapacity, 64), 1, 1);
            BindAnisotropyNeighborhood(shader, transform, particles);
            shader.SetBuffer(transform, "_AnisotropyTransforms", surface.AnisotropyBuffer);
            shader.SetBuffer(transform, "_AnisotropyCounters", surface.AnisotropyCounters);
            shader.Dispatch(transform, DivideRoundUp(ParticleCapacity, 64), 1, 1);
        }

        private static void DispatchSurfaceSupport(
            ComputeShader shader,
            FluidGpuResourceSet particles,
            FluidSurfaceGpuResources surface)
        {
            int kernel = shader.FindKernel("GatherSurfaceSupport");
            shader.SetInt("_ParticleCapacity", particles.ParticleCapacity);
            shader.SetInt("_HashTableCapacity", particles.HashTableCapacity);
            shader.SetInt("_CrownEdgeNeighborCount", 4);
            shader.SetInt("_CrownInteriorNeighborCount", 12);
            shader.SetFloat("_SmoothingRadius", SmoothingRadius);
            BindAnisotropyNeighborhood(shader, kernel, particles);
            shader.SetBuffer(kernel, "_SurfaceSupports", surface.SurfaceSupportBuffer);
            shader.Dispatch(kernel, DivideRoundUp(ParticleCapacity, 64), 1, 1);
        }

        private static void BindAnisotropyNeighborhood(
            ComputeShader shader,
            int kernel,
            FluidGpuResourceSet particles)
        {
            shader.SetBuffer(kernel, "_PredictedPositions", particles.PredictedPositions);
            shader.SetBuffer(kernel, "_ParticleMetadata", particles.Metadata);
            shader.SetBuffer(kernel, "_SpatialEntries", particles.SpatialEntries);
            shader.SetBuffer(kernel, "_CellRanges", particles.CellRanges);
            shader.SetBuffer(kernel, "_SpatialCells", particles.SpatialCells);
        }

        private static void DispatchDensity(
            ComputeShader shader,
            string kernelName,
            FluidGpuResourceSet particles,
            FluidSurfaceGpuResources surface,
            in FluidSurfaceGridSettings grid,
            bool bindAnisotropy)
        {
            int kernel = shader.FindKernel(kernelName);
            shader.SetInt("_ParticleCapacity", particles.ParticleCapacity);
            shader.SetInt("_HashTableCapacity", particles.HashTableCapacity);
            shader.SetFloat("_SmoothingRadius", SmoothingRadius);
            shader.SetFloat("_ParticleMass", ParticleMass);
            shader.SetInt("_TargetMaterialId", 0);
            shader.SetInt("_DensityCellRadius", 2);
            shader.SetInt("_UseStylizedCrown", 0);
            shader.SetFloat("_CrownHeightRatio", 0f);
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
            if (bindAnisotropy)
                shader.SetBuffer(kernel, "_AnisotropyTransforms", surface.AnisotropyBuffer);
            shader.SetTexture(kernel, "_DensityTexture", surface.DensityTexture);
            shader.Dispatch(kernel,
                DivideRoundUp(grid.Resolution.x, 4),
                DivideRoundUp(grid.Resolution.y, 4),
                DivideRoundUp(grid.Resolution.z, 4));
        }

        private static FluidAnisotropyTransform[] ReadTransformsTestOnly(GraphicsBuffer buffer)
        {
            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(buffer);
            request.WaitForCompletion();
            Assert.That(request.hasError, Is.False, "Test-only anisotropy readback failed.");
            NativeArray<FluidAnisotropyTransform> values =
                request.GetData<FluidAnisotropyTransform>();
            return values.ToArray();
        }

        private static uint[] ReadUIntTestOnly(GraphicsBuffer buffer)
        {
            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(buffer);
            request.WaitForCompletion();
            Assert.That(request.hasError, Is.False, "Test-only counter readback failed.");
            return request.GetData<uint>().ToArray();
        }

        private static float[] ReadFloatTestOnly(GraphicsBuffer buffer)
        {
            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(buffer);
            request.WaitForCompletion();
            Assert.That(request.hasError, Is.False, "Test-only float buffer readback failed.");
            return request.GetData<float>().ToArray();
        }

        private static float[] ReadDensityTestOnly(RenderTexture texture)
        {
            int sliceLength = checked(texture.width * texture.height);
            var result = new float[checked(sliceLength * texture.volumeDepth)];
            for (int z = 0; z < texture.volumeDepth; z++)
            {
                AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(
                    texture, 0, 0, texture.width, 0, texture.height, z, 1, null);
                request.WaitForCompletion();
                Assert.That(request.hasError, Is.False, "Test-only density slice readback failed.");
                NativeArray<float> data = request.GetData<float>();
                Assert.That(data.Length, Is.EqualTo(sliceLength));
                for (int index = 0; index < sliceLength; index++)
                    result[z * sliceLength + index] = data[index];
            }

            return result;
        }

        private static Vector3 TransformPoint(in FluidAnisotropyTransform value, Vector3 point)
        {
            return new Vector3(
                Vector3.Dot(new Vector3(value.Row0.x, value.Row0.y, value.Row0.z), point) + value.Row0.w,
                Vector3.Dot(new Vector3(value.Row1.x, value.Row1.y, value.Row1.z), point) + value.Row1.w,
                Vector3.Dot(new Vector3(value.Row2.x, value.Row2.y, value.Row2.z), point) + value.Row2.w);
        }

        private static Vector3 TransformVector(in FluidAnisotropyTransform value, Vector3 vector)
        {
            return new Vector3(
                Vector3.Dot(new Vector3(value.Row0.x, value.Row0.y, value.Row0.z), vector),
                Vector3.Dot(new Vector3(value.Row1.x, value.Row1.y, value.Row1.z), vector),
                Vector3.Dot(new Vector3(value.Row2.x, value.Row2.y, value.Row2.z), vector));
        }

        private static float LinearDeterminant(in FluidAnisotropyTransform value)
        {
            var row0 = new Vector3(value.Row0.x, value.Row0.y, value.Row0.z);
            var row1 = new Vector3(value.Row1.x, value.Row1.y, value.Row1.z);
            var row2 = new Vector3(value.Row2.x, value.Row2.y, value.Row2.z);
            return Vector3.Dot(row0, Vector3.Cross(row1, row2));
        }

        private static void AssertIdentityAtCenter(
            in FluidAnisotropyTransform value,
            Vector3 center)
        {
            AssertFinite(in value);
            Assert.That(value.Row0, Is.EqualTo(new Vector4(1f, 0f, 0f, -center.x)));
            Assert.That(value.Row1, Is.EqualTo(new Vector4(0f, 1f, 0f, -center.y)));
            Assert.That(value.Row2, Is.EqualTo(new Vector4(0f, 0f, 1f, -center.z)));
            Assert.That(value.ScaleValidity, Is.EqualTo(new Vector4(1f, 1f, 1f, 0f)));
            Assert.That(TransformPoint(in value, center), Is.EqualTo(Vector3.zero));
        }

        private static void AssertFinite(in FluidAnisotropyTransform value)
        {
            AssertFinite(value.Row0);
            AssertFinite(value.Row1);
            AssertFinite(value.Row2);
            AssertFinite(value.ScaleValidity);
        }

        private static void AssertFinite(Vector4 value)
        {
            Assert.That(float.IsNaN(value.x) || float.IsInfinity(value.x), Is.False);
            Assert.That(float.IsNaN(value.y) || float.IsInfinity(value.y), Is.False);
            Assert.That(float.IsNaN(value.z) || float.IsInfinity(value.z), Is.False);
            Assert.That(float.IsNaN(value.w) || float.IsInfinity(value.w), Is.False);
        }

        private static void AssertAllFinite(float[] values)
        {
            for (int i = 0; i < values.Length; i++)
                Assert.That(float.IsNaN(values[i]) || float.IsInfinity(values[i]), Is.False);
        }

        private static int LinearIndex(Vector3Int resolution, int x, int y, int z)
        {
            return x + resolution.x * (y + resolution.y * z);
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
                Assert.Ignore(reason ?? "ComputeShader is unavailable; GPU anisotropy test is skipped, not passed.");
            }
        }
    }
}
