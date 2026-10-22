using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Game.Materials;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// 这是 Test-only 的同步 GPU 数值验证。GraphicsBuffer.GetData 会等待 GPU，绝不能复制到
    /// Runtime；Runtime 的 Gameplay/Rendering 读取仍必须走后续低频 AsyncGPUReadback Snapshot。
    /// </summary>
    public sealed class GpuPbfSolverTests
    {
        private const string SolverShaderPath =
            "Assets/_Project/Art/Elemental/Compute/PbfSolver.compute";
        private const string SpatialHashShaderPath =
            "Assets/_Project/Art/Elemental/Compute/PbfSpatialHash.compute";
        private const int ThreadGroupSize = 64;
        private const float SmoothingRadius = 0.15f;
        private const float ParticleMass = 0.001f;
        private const float RestDensity = 1f;
        private const float DeltaTime = 1f / 120f;
        private const float MaximumPositionCorrection = 0.04f;
        private const float XsphViscosity = 0.25f;
        private const float VorticityStrength = 0.1f;
        private const float MaximumTensilePositionCorrection = 0.0001f;

        [Test]
        public void Density_IgnoresDifferentMaterialInsideSameHashCell()
        {
            IgnoreWithoutComputeSupport();
            ComputeShader solver = LoadRequiredShader(SolverShaderPath);
            ComputeShader spatialHash = LoadRequiredShader(SpatialHashShaderPath);
            var rows = new FluidGpuLiquidMaterialParameters[256];
            LiquidMaterialSettings water = CreateMaterialSettings(MaterialId.Water, ParticleMass, RestDensity);
            LiquidMaterialSettings poison = CreateMaterialSettings(MaterialId.Poison, ParticleMass * 5f, RestDensity * 2f);
            rows[(byte)MaterialId.Water] = new FluidGpuLiquidMaterialParameters(in water);
            rows[(byte)MaterialId.Poison] = new FluidGpuLiquidMaterialParameters(in poison);
            var resources = new FluidGpuResourceSet(2, 16, 1, liquidMaterialParameters: rows);
            try
            {
                UploadMixedMaterialParticles(resources);
                RunPbf(solver, spatialHash, resources, iterations: 0);

                var result = new Vector2[2];
                resources.DensityLambda.GetData(result);
                float selfKernel = PbfKernelMath.Poly6(0f, SmoothingRadius);
                Assert.That(result[0].x, Is.EqualTo(water.ParticleMass * selfKernel).Within(1e-3f));
                Assert.That(result[1].x, Is.EqualTo(poison.ParticleMass * selfKernel).Within(1e-3f));
            }
            finally
            {
                resources.Dispose();
            }
        }

        [Test]
        public void UnderdenseFreeSurfaceProducesZeroGpuLambda()
        {
            IgnoreWithoutComputeSupport();
            ComputeShader solver = LoadRequiredShader(SolverShaderPath);
            ComputeShader spatialHash = LoadRequiredShader(SpatialHashShaderPath);
            var resources = new FluidGpuResourceSet(1, 2, 1);
            try
            {
                UploadActiveParticles(resources, new[] { new Vector4(0f, 0f, 0f, 1f) });
                RunPbf(solver, spatialHash, resources, iterations: 0);

                var densityLambda = new Vector2[1];
                resources.DensityLambda.GetData(densityLambda);
                Assert.That(densityLambda[0].x, Is.LessThan(RestDensity));
                Assert.That(densityLambda[0].y, Is.Zero,
                    "真实 GPU Kernel 不得把自由表面欠密度转换成会反复拉扯边缘的正 Lambda。");
                AssertNoNumericalErrors(resources);
            }
            finally
            {
                resources.Dispose();
            }
        }

        [Test]
        public void TensileConstraint_AttractsUnderdensePairAndClampsEachIteration()
        {
            IgnoreWithoutComputeSupport();
            ComputeShader solver = LoadRequiredShader(SolverShaderPath);
            ComputeShader spatialHash = LoadRequiredShader(SpatialHashShaderPath);
            var resources = new FluidGpuResourceSet(2, 16, 1);
            try
            {
                var original = new[]
                {
                    new Vector4(-0.05f, 0f, 0f, 1f),
                    new Vector4(0.05f, 0f, 0f, 1f),
                };
                UploadActiveParticles(resources, original);

                RunTensileIteration(
                    solver,
                    spatialHash,
                    resources,
                    density: 0.5f * RestDensity,
                    strength: 1000f,
                    maximumCorrection: MaximumTensilePositionCorrection);

                var corrected = new Vector4[2];
                resources.PredictedPositions.GetData(corrected);
                Assert.That(corrected[0].x, Is.GreaterThan(original[0].x));
                Assert.That(corrected[1].x, Is.LessThan(original[1].x));
                Assert.That(Vector3.Distance(corrected[0], corrected[1]),
                    Is.LessThan(Vector3.Distance(original[0], original[1])));
                Assert.That(Vector3.Distance(corrected[0], original[0]),
                    Is.LessThanOrEqualTo(MaximumTensilePositionCorrection + 1e-6f));
                Assert.That(Vector3.Distance(corrected[1], original[1]),
                    Is.LessThanOrEqualTo(MaximumTensilePositionCorrection + 1e-6f));
                AssertNoNumericalErrors(resources);
            }
            finally
            {
                resources.Dispose();
            }
        }

        [Test]
        public void TensileConstraint_DoesNotMoveParticlesAtOrAboveRestDensity()
        {
            IgnoreWithoutComputeSupport();
            ComputeShader solver = LoadRequiredShader(SolverShaderPath);
            ComputeShader spatialHash = LoadRequiredShader(SpatialHashShaderPath);
            var resources = new FluidGpuResourceSet(2, 16, 1);
            try
            {
                var original = new[]
                {
                    new Vector4(-0.05f, 0f, 0f, 1f),
                    new Vector4(0.05f, 0f, 0f, 1f),
                };
                UploadActiveParticles(resources, original);

                RunTensileIteration(
                    solver,
                    spatialHash,
                    resources,
                    density: RestDensity,
                    strength: 1000f,
                    maximumCorrection: MaximumTensilePositionCorrection);

                var corrected = new Vector4[2];
                resources.PredictedPositions.GetData(corrected);
                Assert.That(corrected[0], Is.EqualTo(original[0]));
                Assert.That(corrected[1], Is.EqualTo(original[1]));
                AssertNoNumericalErrors(resources);
            }
            finally
            {
                resources.Dispose();
            }
        }

        [Test]
        public void Cohesion_AttractsFreeSurfaceCancelsSymmetricInteriorAndClampsDeltaSpeed()
        {
            IgnoreWithoutComputeSupport();
            ComputeShader solver = LoadRequiredShader(SolverShaderPath);
            ComputeShader spatialHash = LoadRequiredShader(SpatialHashShaderPath);
            // Spatial Hash 的 Bitonic Sort 需要 2 的幂容量；只激活 3 个 slot，第四个保持 inactive。
            var resources = new FluidGpuResourceSet(4, 16, 1);
            try
            {
                UploadActiveParticles(resources, new[]
                {
                    new Vector4(-0.125f, 0f, 0f, 1f),
                    new Vector4(0f, 0f, 0f, 1f),
                    new Vector4(0.125f, 0f, 0f, 1f),
                });

                RunCohesion(solver, spatialHash, resources, strength: 10000f, maximumDeltaSpeed: 0.02f);

                var velocities = new Vector4[4];
                resources.Velocities.GetData(velocities);
                Assert.That(velocities[0].x, Is.GreaterThan(0f));
                Assert.That(Mathf.Abs(velocities[1].x), Is.LessThan(1e-5f));
                Assert.That(velocities[2].x, Is.LessThan(0f));
                Assert.That(((Vector3)velocities[0]).magnitude, Is.LessThanOrEqualTo(0.02001f));
                Assert.That(((Vector3)velocities[2]).magnitude, Is.LessThanOrEqualTo(0.02001f));
                AssertNoNumericalErrors(resources);
            }
            finally
            {
                resources.Dispose();
            }
        }

        [Test]
        public void Cohesion_DoesNotAttractParticlesOutsideCompactSupport()
        {
            IgnoreWithoutComputeSupport();
            ComputeShader solver = LoadRequiredShader(SolverShaderPath);
            ComputeShader spatialHash = LoadRequiredShader(SpatialHashShaderPath);
            var resources = new FluidGpuResourceSet(2, 16, 1);
            try
            {
                UploadActiveParticles(resources, new[]
                {
                    new Vector4(0f, 0f, 0f, 1f),
                    new Vector4(SmoothingRadius + 0.01f, 0f, 0f, 1f),
                });

                RunCohesion(solver, spatialHash, resources, strength: 12f, maximumDeltaSpeed: 0.2f);

                var velocities = new Vector4[2];
                resources.Velocities.GetData(velocities);
                Assert.That(velocities[0], Is.EqualTo(Vector4.zero));
                Assert.That(velocities[1], Is.EqualTo(Vector4.zero));
                AssertNoNumericalErrors(resources);
            }
            finally
            {
                resources.Dispose();
            }
        }

        [Test]
        public void FourPbfIterationsReduceDenseTwoByTwoByTwoDensityErrorWithoutChangingActiveCount()
        {
            IgnoreWithoutComputeSupport();
            ComputeShader solver = LoadRequiredShader(SolverShaderPath);
            ComputeShader spatialHash = LoadRequiredShader(SpatialHashShaderPath);
            var resources = new FluidGpuResourceSet(8, 16, 1);
            try
            {
                Vector4[] originalPositions = CreateDenseBlock();
                UploadActiveParticles(resources, originalPositions);

                RunPbf(solver, spatialHash, resources, iterations: 0);
                float initialError = ReadBruteForceAverageDensityError(resources);
                AssertGpuDensityMatchesBruteForce(resources);
                int activeBefore = CountActive(resources);

                UploadActiveParticles(resources, originalPositions);
                RunPbf(solver, spatialHash, resources, iterations: 4);
                float correctedError = ReadBruteForceAverageDensityError(resources);
                AssertGpuDensityMatchesBruteForce(resources);
                int activeAfter = CountActive(resources);

                Assert.That(correctedError, Is.LessThan(initialError),
                    "4 次 Density/Lambda -> Delta -> Apply 必须比 0 次更接近 Rest Density。");
                Assert.That(activeAfter, Is.EqualTo(activeBefore));
                AssertFiniteActiveState(resources);
                AssertNoNumericalErrors(resources);
            }
            finally
            {
                resources.Dispose();
            }
        }

        [Test]
        public void SingleAndExactlyOverlappingParticlesStayFinite()
        {
            IgnoreWithoutComputeSupport();
            ComputeShader solver = LoadRequiredShader(SolverShaderPath);
            ComputeShader spatialHash = LoadRequiredShader(SpatialHashShaderPath);
            var resources = new FluidGpuResourceSet(2, 4, 1);
            try
            {
                UploadActiveParticles(resources, new[] { new Vector4(0f, 0f, 0f, 1f) });
                RunPbf(solver, spatialHash, resources, iterations: 4);
                AssertFiniteActiveState(resources);
                Assert.That(CountActive(resources), Is.EqualTo(1));
                AssertNoNumericalErrors(resources);

                UploadActiveParticles(resources, new[]
                {
                    new Vector4(0f, 0f, 0f, 1f),
                    new Vector4(0f, 0f, 0f, 1f),
                });
                RunPbf(solver, spatialHash, resources, iterations: 4);
                AssertFiniteActiveState(resources);
                Assert.That(CountActive(resources), Is.EqualTo(2));
                AssertNoNumericalErrors(resources);
            }
            finally
            {
                resources.Dispose();
            }
        }

        [Test]
        public void NonContiguousActiveSlotsUseMetadataForDensityOracleAndUniformDensity()
        {
            IgnoreWithoutComputeSupport();
            ComputeShader solver = LoadRequiredShader(SolverShaderPath);
            ComputeShader spatialHash = LoadRequiredShader(SpatialHashShaderPath);
            var resources = new FluidGpuResourceSet(4, 16, 1);
            try
            {
                // free-stack 回收后 active slot 可以有洞：不能把 activeCount 当作 [0, count) 的连续区间。
                UploadActiveParticlesAtSlots(
                    resources,
                    new[] { 1, 3 },
                    new[]
                    {
                        new Vector4(-0.02f, 0f, 0f, 1f),
                        new Vector4(0.02f, 0f, 0f, 1f),
                    });

                SetUniformActiveDensity(resources, RestDensity);
                var initializedDensities = new Vector2[resources.ParticleCapacity];
                resources.DensityLambda.GetData(initializedDensities);
                Assert.That(initializedDensities[0], Is.EqualTo(Vector2.zero));
                Assert.That(initializedDensities[1].x, Is.EqualTo(RestDensity));
                Assert.That(initializedDensities[2], Is.EqualTo(Vector2.zero));
                Assert.That(initializedDensities[3].x, Is.EqualTo(RestDensity));

                RunPbf(solver, spatialHash, resources, iterations: 0);
                float pairDensity = ParticleMass * (
                    PbfKernelMath.Poly6(0f, SmoothingRadius)
                    + PbfKernelMath.Poly6(0.04f, SmoothingRadius));
                float expectedAverageError = Mathf.Abs(pairDensity / RestDensity - 1f);
                Assert.That(
                    ReadBruteForceAverageDensityError(resources),
                    Is.EqualTo(expectedAverageError).Within(1e-4f));
                AssertGpuDensityMatchesBruteForce(resources);

                // 故意破坏高位 active slot；oracle 必须比较 slot 3，而不是只比较前两个数组元素。
                var corruptedDensityLambda = new Vector2[resources.ParticleCapacity];
                resources.DensityLambda.GetData(corruptedDensityLambda);
                corruptedDensityLambda[3].x += 0.25f;
                resources.DensityLambda.SetData(corruptedDensityLambda);
                Assert.That(
                    Assert.Throws<AssertionException>(() => AssertGpuDensityMatchesBruteForce(resources)),
                    Is.Not.Null);
                AssertNoNumericalErrors(resources);
            }
            finally
            {
                resources.Dispose();
            }
        }

        [Test]
        public void HashBuildCellSnapshotRetainsNeighborsAfterPredictedPositionCrossesCellBoundary()
        {
            IgnoreWithoutComputeSupport();
            ComputeShader solver = LoadRequiredShader(SolverShaderPath);
            ComputeShader spatialHash = LoadRequiredShader(SpatialHashShaderPath);
            var resources = new FluidGpuResourceSet(2, 16, 1);
            try
            {
                const float epsilon = 0.001f;
                Assert.That(
                    FluidSpatialHash.HashCell(Vector3Int.zero, resources.HashTableCapacity),
                    Is.Not.EqualTo(FluidSpatialHash.HashCell(new Vector3Int(-1, 0, 0), resources.HashTableCapacity)));

                UploadActiveParticles(resources, new[]
                {
                    new Vector4(epsilon, 0f, 0f, 1f),
                    new Vector4(epsilon * 3f, 0f, 0f, 1f),
                });
                BuildSpatialHash(spatialHash, resources);

                // 模拟同一 Substep 的 Apply：仅移动当前 Predicted Position，故意不重建/重排 Hash。
                SetPredictedPositions(resources, new[]
                {
                    new Vector4(-epsilon, 0f, 0f, 1f),
                    new Vector4(epsilon * 3f, 0f, 0f, 1f),
                });
                DispatchDensityFromExistingSpatialHash(solver, resources);

                var densityLambda = new Vector2[resources.ParticleCapacity];
                resources.DensityLambda.GetData(densityLambda);
                float selfDensity = ParticleMass * PbfKernelMath.Poly6(0f, SmoothingRadius);
                Assert.That(densityLambda[0].x, Is.GreaterThan(selfDensity * 1.5f),
                    "同一 Substep 内跨 cell 后仍必须从 build-time cell snapshot 找到 self 与旧邻居；不能改用当前 cell 查旧 Range。");
                AssertGpuDensityMatchesBruteForce(resources);
                AssertNoNumericalErrors(resources);
            }
            finally
            {
                resources.Dispose();
            }
        }

        [Test]
        public void PositiveXsphReducesOppositeVelocityMagnitudesAndPreservesPairMomentum()
        {
            IgnoreWithoutComputeSupport();
            ComputeShader solver = LoadRequiredShader(SolverShaderPath);
            ComputeShader spatialHash = LoadRequiredShader(SpatialHashShaderPath);
            var resources = new FluidGpuResourceSet(2, 16, 1);
            try
            {
                UploadActiveParticles(
                    resources,
                    new[]
                    {
                        new Vector4(-0.02f, 0f, 0f, 1f),
                        new Vector4(0.02f, 0f, 0f, 1f),
                    },
                    new[]
                    {
                        new Vector4(-1f, 0f, 0f, 0f),
                        new Vector4(1f, 0f, 0f, 0f),
                    });

                RunVelocityFilters(solver, spatialHash, resources, XsphViscosity, vorticity: 0f);

                var velocities = new Vector4[resources.ParticleCapacity];
                resources.Velocities.GetData(velocities);
                Assert.That(Mathf.Abs(velocities[0].x), Is.LessThan(1f));
                Assert.That(Mathf.Abs(velocities[1].x), Is.LessThan(1f));
                Assert.That(velocities[0].x + velocities[1].x, Is.EqualTo(0f).Within(1e-5f),
                    "同质量且 rho 相同的成对 XSPH 修正应近似守恒动量。");
                AssertFiniteActiveState(resources);
                Assert.That(CountActive(resources), Is.EqualTo(2));
                AssertNoNumericalErrors(resources);
            }
            finally
            {
                resources.Dispose();
            }
        }

        [Test]
        public void PositiveVorticityRunsTwoPhaseFilterWithoutNumericalErrorsOrActiveCountChange()
        {
            IgnoreWithoutComputeSupport();
            ComputeShader solver = LoadRequiredShader(SolverShaderPath);
            ComputeShader spatialHash = LoadRequiredShader(SpatialHashShaderPath);
            var resources = new FluidGpuResourceSet(2, 16, 1);
            try
            {
                UploadActiveParticles(
                    resources,
                    new[]
                    {
                        new Vector4(-0.02f, 0f, 0f, 1f),
                        new Vector4(0.02f, 0f, 0f, 1f),
                    },
                    new[]
                    {
                        new Vector4(0f, -1f, 0f, 0f),
                        new Vector4(0f, 1f, 0f, 0f),
                    });

                RunVelocityFilters(
                    solver,
                    spatialHash,
                    resources,
                    viscosity: 0f,
                    vorticity: VorticityStrength);

                AssertFiniteActiveState(resources);
                Assert.That(CountActive(resources), Is.EqualTo(2));
                AssertNoNumericalErrors(resources);
            }
            finally
            {
                resources.Dispose();
            }
        }

        private static void RunPbf(
            ComputeShader solver,
            ComputeShader spatialHash,
            FluidGpuResourceSet resources,
            int iterations)
        {
            BuildSpatialHash(spatialHash, resources);

            int densityLambdaKernel = solver.FindKernel("ComputeDensityLambda");
            int deltaPositionKernel = solver.FindKernel("ComputeDeltaPosition");
            int applyPositionKernel = solver.FindKernel("ApplyDeltaPosition");
            int updateVelocityKernel = solver.FindKernel("UpdateVelocities");
            int computeXsphDeltaVelocityKernel = solver.FindKernel("ComputeXsphDeltaVelocities");
            int applyDeltaVelocityKernel = solver.FindKernel("ApplyDeltaVelocities");
            int clampVelocityKernel = solver.FindKernel("ClampVelocities");
            int groups = DivideRoundUp(resources.ParticleCapacity);

            SetSolverParameters(solver, resources, viscosity: 0f, vorticity: 0f);

            BindNeighborReadBuffers(solver, densityLambdaKernel, resources);
            solver.SetBuffer(densityLambdaKernel, "_DensityLambda", resources.DensityLambda);
            solver.SetBuffer(densityLambdaKernel, "_Counters", resources.Counters);

            BindNeighborReadBuffers(solver, deltaPositionKernel, resources);
            solver.SetBuffer(deltaPositionKernel, "_DensityLambda", resources.DensityLambda);
            solver.SetBuffer(deltaPositionKernel, "_DeltaPositions", resources.DeltaPositions);
            solver.SetBuffer(deltaPositionKernel, "_DeltaVelocities", resources.DeltaVelocities);
            solver.SetBuffer(deltaPositionKernel, "_Counters", resources.Counters);

            solver.SetBuffer(applyPositionKernel, "_ParticleMetadata", resources.Metadata);
            solver.SetBuffer(applyPositionKernel, "_DeltaPositions", resources.DeltaPositions);
            solver.SetBuffer(applyPositionKernel, "_PredictedPositions", resources.PredictedPositions);
            solver.SetBuffer(applyPositionKernel, "_Counters", resources.Counters);

            for (int iteration = 0; iteration < iterations; iteration++)
            {
                solver.Dispatch(densityLambdaKernel, groups, 1, 1);
                solver.Dispatch(deltaPositionKernel, groups, 1, 1);
                solver.Dispatch(applyPositionKernel, groups, 1, 1);
            }

            // 0 次 Iteration 仍计算一次 density，保证两组 density error 都来自同一个 GPU Kernel。
            solver.Dispatch(densityLambdaKernel, groups, 1, 1);

            solver.SetBuffer(updateVelocityKernel, "_Positions", resources.Positions);
            solver.SetBuffer(updateVelocityKernel, "_PredictedPositions", resources.PredictedPositions);
            solver.SetBuffer(updateVelocityKernel, "_ParticleMetadata", resources.Metadata);
            solver.SetBuffer(updateVelocityKernel, "_Velocities", resources.Velocities);
            solver.SetBuffer(updateVelocityKernel, "_Counters", resources.Counters);
            solver.Dispatch(updateVelocityKernel, groups, 1, 1);

            // Apply/Clamp 均会从 IsActive 读取 PredictedPositions.w；即使这一例 Viscosity=0，
            // 也执行零增量的两阶段路径，防止测试自己漏绑而在真实 GPU Dispatch 时留下未绑定资源。
            BindNeighborReadBuffers(solver, computeXsphDeltaVelocityKernel, resources);
            solver.SetBuffer(computeXsphDeltaVelocityKernel, "_DensityLambda", resources.DensityLambda);
            solver.SetBuffer(computeXsphDeltaVelocityKernel, "_Velocities", resources.Velocities);
            solver.SetBuffer(computeXsphDeltaVelocityKernel, "_DeltaVelocities", resources.DeltaVelocities);
            solver.SetBuffer(computeXsphDeltaVelocityKernel, "_Counters", resources.Counters);
            solver.Dispatch(computeXsphDeltaVelocityKernel, groups, 1, 1);

            solver.SetBuffer(applyDeltaVelocityKernel, "_PredictedPositions", resources.PredictedPositions);
            solver.SetBuffer(applyDeltaVelocityKernel, "_ParticleMetadata", resources.Metadata);
            solver.SetBuffer(applyDeltaVelocityKernel, "_Velocities", resources.Velocities);
            solver.SetBuffer(applyDeltaVelocityKernel, "_DeltaVelocities", resources.DeltaVelocities);
            solver.SetBuffer(applyDeltaVelocityKernel, "_Counters", resources.Counters);
            solver.Dispatch(applyDeltaVelocityKernel, groups, 1, 1);

            solver.SetBuffer(clampVelocityKernel, "_PredictedPositions", resources.PredictedPositions);
            solver.SetBuffer(clampVelocityKernel, "_ParticleMetadata", resources.Metadata);
            solver.SetBuffer(clampVelocityKernel, "_Velocities", resources.Velocities);
            solver.SetBuffer(clampVelocityKernel, "_Counters", resources.Counters);
            solver.Dispatch(clampVelocityKernel, groups, 1, 1);
        }

        private static void DispatchDensityFromExistingSpatialHash(
            ComputeShader solver,
            FluidGpuResourceSet resources)
        {
            int densityLambdaKernel = solver.FindKernel("ComputeDensityLambda");
            SetSolverParameters(solver, resources, viscosity: 0f, vorticity: 0f);
            BindNeighborReadBuffers(solver, densityLambdaKernel, resources);
            solver.SetBuffer(densityLambdaKernel, "_DensityLambda", resources.DensityLambda);
            solver.SetBuffer(densityLambdaKernel, "_Counters", resources.Counters);
            solver.Dispatch(densityLambdaKernel, DivideRoundUp(resources.ParticleCapacity), 1, 1);
        }

        private static void RunVelocityFilters(
            ComputeShader solver,
            ComputeShader spatialHash,
            FluidGpuResourceSet resources,
            float viscosity,
            float vorticity)
        {
            BuildSpatialHash(spatialHash, resources);
            SetUniformActiveDensity(resources, RestDensity);
            SetSolverParameters(solver, resources, viscosity, vorticity);

            int groups = DivideRoundUp(resources.ParticleCapacity);
            int applyDeltaVelocityKernel = solver.FindKernel("ApplyDeltaVelocities");
            if (viscosity > 0f)
            {
                int xsphKernel = solver.FindKernel("ComputeXsphDeltaVelocities");
                BindNeighborReadBuffers(solver, xsphKernel, resources);
                solver.SetBuffer(xsphKernel, "_DensityLambda", resources.DensityLambda);
                solver.SetBuffer(xsphKernel, "_Velocities", resources.Velocities);
                solver.SetBuffer(xsphKernel, "_DeltaVelocities", resources.DeltaVelocities);
                solver.SetBuffer(xsphKernel, "_Counters", resources.Counters);
                solver.Dispatch(xsphKernel, groups, 1, 1);
                DispatchApplyDeltaVelocities(solver, applyDeltaVelocityKernel, resources, groups);
            }

            if (vorticity > 0f)
            {
                int vorticitiesKernel = solver.FindKernel("ComputeVorticities");
                int vorticityDeltaKernel = solver.FindKernel("ComputeVorticityDeltaVelocities");
                BindNeighborReadBuffers(solver, vorticitiesKernel, resources);
                solver.SetBuffer(vorticitiesKernel, "_DensityLambda", resources.DensityLambda);
                solver.SetBuffer(vorticitiesKernel, "_Velocities", resources.Velocities);
                solver.SetBuffer(vorticitiesKernel, "_Vorticities", resources.Vorticities);
                solver.SetBuffer(vorticitiesKernel, "_Counters", resources.Counters);
                solver.Dispatch(vorticitiesKernel, groups, 1, 1);

                BindNeighborReadBuffers(solver, vorticityDeltaKernel, resources);
                solver.SetBuffer(vorticityDeltaKernel, "_DensityLambda", resources.DensityLambda);
                solver.SetBuffer(vorticityDeltaKernel, "_Vorticities", resources.Vorticities);
                solver.SetBuffer(vorticityDeltaKernel, "_DeltaVelocities", resources.DeltaVelocities);
                solver.SetBuffer(vorticityDeltaKernel, "_Counters", resources.Counters);
                solver.Dispatch(vorticityDeltaKernel, groups, 1, 1);
                DispatchApplyDeltaVelocities(solver, applyDeltaVelocityKernel, resources, groups);
            }

            int clampKernel = solver.FindKernel("ClampVelocities");
            solver.SetBuffer(clampKernel, "_PredictedPositions", resources.PredictedPositions);
            solver.SetBuffer(clampKernel, "_ParticleMetadata", resources.Metadata);
            solver.SetBuffer(clampKernel, "_Velocities", resources.Velocities);
            solver.SetBuffer(clampKernel, "_Counters", resources.Counters);
            solver.Dispatch(clampKernel, groups, 1, 1);
        }

        private static void RunCohesion(
            ComputeShader solver,
            ComputeShader spatialHash,
            FluidGpuResourceSet resources,
            float strength,
            float maximumDeltaSpeed)
        {
            BuildSpatialHash(spatialHash, resources);
            SetUniformActiveDensity(resources, RestDensity);
            SetSolverParameters(solver, resources, viscosity: 0f, vorticity: 0f);
            solver.SetFloat("_CohesionStrength", strength);
            solver.SetFloat("_CohesionRestDistance", 0.1f);
            solver.SetFloat("_MaximumCohesionDeltaSpeed", maximumDeltaSpeed);

            int cohesionKernel = solver.FindKernel("ComputeCohesionDeltaVelocities");
            BindNeighborReadBuffers(solver, cohesionKernel, resources);
            solver.SetBuffer(cohesionKernel, "_DensityLambda", resources.DensityLambda);
            solver.SetBuffer(cohesionKernel, "_DeltaVelocities", resources.DeltaVelocities);
            solver.SetBuffer(cohesionKernel, "_Counters", resources.Counters);
            int groups = DivideRoundUp(resources.ParticleCapacity);
            solver.Dispatch(cohesionKernel, groups, 1, 1);
            DispatchApplyDeltaVelocities(
                solver,
                solver.FindKernel("ApplyDeltaVelocities"),
                resources,
                groups);
        }

        private static void RunTensileIteration(
            ComputeShader solver,
            ComputeShader spatialHash,
            FluidGpuResourceSet resources,
            float density,
            float strength,
            float maximumCorrection)
        {
            BuildSpatialHash(spatialHash, resources);
            SetUniformActiveDensity(resources, density);
            SetSolverParameters(solver, resources, viscosity: 0f, vorticity: 0f);
            solver.SetFloat("_TensileStrength", strength);
            solver.SetFloat("_MaximumTensilePositionCorrection", maximumCorrection);

            int computeKernel = solver.FindKernel("ComputeDeltaPosition");
            BindNeighborReadBuffers(solver, computeKernel, resources);
            solver.SetBuffer(computeKernel, "_DensityLambda", resources.DensityLambda);
            solver.SetBuffer(computeKernel, "_DeltaPositions", resources.DeltaPositions);
            solver.SetBuffer(computeKernel, "_DeltaVelocities", resources.DeltaVelocities);
            solver.SetBuffer(computeKernel, "_Counters", resources.Counters);

            int applyKernel = solver.FindKernel("ApplyTensileDeltaPosition");
            solver.SetBuffer(applyKernel, "_ParticleMetadata", resources.Metadata);
            solver.SetBuffer(applyKernel, "_PredictedPositions", resources.PredictedPositions);
            solver.SetBuffer(applyKernel, "_DeltaVelocities", resources.DeltaVelocities);
            solver.SetBuffer(applyKernel, "_Counters", resources.Counters);

            int groups = DivideRoundUp(resources.ParticleCapacity);
            solver.Dispatch(computeKernel, groups, 1, 1);
            solver.Dispatch(applyKernel, groups, 1, 1);
        }

        private static void DispatchApplyDeltaVelocities(
            ComputeShader solver,
            int applyDeltaVelocityKernel,
            FluidGpuResourceSet resources,
            int groups)
        {
            solver.SetBuffer(applyDeltaVelocityKernel, "_PredictedPositions", resources.PredictedPositions);
            solver.SetBuffer(applyDeltaVelocityKernel, "_ParticleMetadata", resources.Metadata);
            solver.SetBuffer(applyDeltaVelocityKernel, "_Velocities", resources.Velocities);
            solver.SetBuffer(applyDeltaVelocityKernel, "_DeltaVelocities", resources.DeltaVelocities);
            solver.SetBuffer(applyDeltaVelocityKernel, "_Counters", resources.Counters);
            solver.Dispatch(applyDeltaVelocityKernel, groups, 1, 1);
        }

        private static void SetSolverParameters(
            ComputeShader solver,
            FluidGpuResourceSet resources,
            float viscosity,
            float vorticity)
        {
            solver.SetInt("_ParticleCapacity", resources.ParticleCapacity);
            solver.SetInt("_HashTableCapacity", resources.HashTableCapacity);
            solver.SetFloat("_SmoothingRadius", SmoothingRadius);
            solver.SetFloat("_ParticleMass", ParticleMass);
            solver.SetFloat("_RestDensity", RestDensity);
            solver.SetFloat("_LambdaEpsilon", 0.0001f);
            solver.SetFloat("_ArtificialPressure", 0.001f);
            solver.SetFloat("_MaximumPositionCorrection", MaximumPositionCorrection);
            solver.SetFloat("_TensileStrength", 0f);
            solver.SetFloat("_MaximumTensilePositionCorrection", MaximumTensilePositionCorrection);
            solver.SetFloat("_DeltaTime", DeltaTime);
            solver.SetFloat("_Viscosity", viscosity);
            solver.SetFloat("_Vorticity", vorticity);
            solver.SetFloat("_MaxSpeed", 25f);
        }

        private static void BindNeighborReadBuffers(
            ComputeShader shader,
            int kernel,
            FluidGpuResourceSet resources)
        {
            shader.SetBuffer(kernel, "_PredictedPositions", resources.PredictedPositions);
            shader.SetBuffer(kernel, "_ParticleMetadata", resources.Metadata);
            shader.SetBuffer(kernel, "_SpatialEntries", resources.SpatialEntries);
            shader.SetBuffer(kernel, "_CellRanges", resources.CellRanges);
            shader.SetBuffer(kernel, "_SpatialCells", resources.SpatialCells);
            shader.SetBuffer(
                kernel,
                "_LiquidMaterialParameters",
                resources.LiquidMaterialParameters);
        }

        private static void BuildSpatialHash(ComputeShader shader, FluidGpuResourceSet resources)
        {
            int buildKernel = shader.FindKernel("BuildSpatialEntries");
            int sortKernel = shader.FindKernel("BitonicSort");
            int clearKernel = shader.FindKernel("ClearCellRanges");
            int rangesKernel = shader.FindKernel("BuildCellRanges");
            int particleGroups = DivideRoundUp(resources.ParticleCapacity);
            int hashGroups = DivideRoundUp(resources.HashTableCapacity);

            shader.SetInt("_ParticleCapacity", resources.ParticleCapacity);
            shader.SetInt("_HashTableCapacity", resources.HashTableCapacity);
            shader.SetFloat("_SmoothingRadius", SmoothingRadius);
            shader.SetBuffer(buildKernel, "_PredictedPositions", resources.PredictedPositions);
            shader.SetBuffer(buildKernel, "_ParticleMetadata", resources.Metadata);
            shader.SetBuffer(buildKernel, "_SpatialCells", resources.SpatialCells);
            shader.SetBuffer(buildKernel, "_SpatialEntries", resources.SpatialEntries);
            shader.Dispatch(buildKernel, particleGroups, 1, 1);

            shader.SetBuffer(sortKernel, "_SpatialEntries", resources.SpatialEntries);
            int passCount = FluidSpatialHash.GetBitonicPassCount(resources.ParticleCapacity);
            for (int passIndex = 0; passIndex < passCount; passIndex++)
            {
                Assert.That(
                    FluidSpatialHash.TryGetBitonicPass(resources.ParticleCapacity, passIndex, out FluidBitonicPass pass),
                    Is.True);
                shader.SetInt("_BitonicStage", pass.Stage);
                shader.SetInt("_BitonicPass", pass.Pass);
                shader.Dispatch(sortKernel, particleGroups, 1, 1);
            }

            shader.SetBuffer(clearKernel, "_CellRanges", resources.CellRanges);
            shader.Dispatch(clearKernel, hashGroups, 1, 1);
            shader.SetBuffer(rangesKernel, "_SpatialEntries", resources.SpatialEntries);
            shader.SetBuffer(rangesKernel, "_CellRanges", resources.CellRanges);
            shader.Dispatch(rangesKernel, particleGroups, 1, 1);
        }

        private static void UploadActiveParticles(
            FluidGpuResourceSet resources,
            Vector4[] activePositions,
            Vector4[] initialVelocities = null)
        {
            Assert.That(initialVelocities == null || initialVelocities.Length == activePositions.Length, Is.True);
            var positions = new Vector4[resources.ParticleCapacity];
            var predictedPositions = new Vector4[resources.ParticleCapacity];
            var velocities = new Vector4[resources.ParticleCapacity];
            var densityLambda = new Vector2[resources.ParticleCapacity];
            var deltaPositions = new Vector4[resources.ParticleCapacity];
            var deltaVelocities = new Vector4[resources.ParticleCapacity];
            var vorticities = new Vector4[resources.ParticleCapacity];
            var metadata = new FluidGpuUInt2[resources.ParticleCapacity];
            var counters = new uint[FluidGpuLayout.CounterCount];

            for (int index = 0; index < activePositions.Length; index++)
            {
                positions[index] = activePositions[index];
                predictedPositions[index] = activePositions[index];
                if (initialVelocities != null)
                    velocities[index] = initialVelocities[index];
                metadata[index] = new FluidGpuUInt2(1u, FluidGpuLayout.ActiveFlag);
            }

            resources.Positions.SetData(positions);
            resources.PredictedPositions.SetData(predictedPositions);
            resources.Velocities.SetData(velocities);
            resources.DensityLambda.SetData(densityLambda);
            resources.DeltaPositions.SetData(deltaPositions);
            resources.DeltaVelocities.SetData(deltaVelocities);
            resources.Vorticities.SetData(vorticities);
            resources.Metadata.SetData(metadata);
            resources.Counters.SetData(counters);
        }

        private static void UploadMixedMaterialParticles(FluidGpuResourceSet resources)
        {
            // Position.w 是 GPU Slot 的 active 标记；Vector4.zero 会让第一个粒子直接被 Kernel 跳过。
            var positions = new[] { new Vector4(0f, 0f, 0f, 1f), new Vector4(0.01f, 0f, 0f, 1f) };
            resources.Positions.SetData(positions);
            resources.PredictedPositions.SetData(positions);
            resources.Velocities.SetData(new Vector4[2]);
            resources.DensityLambda.SetData(new Vector2[2]);
            resources.Metadata.SetData(new[]
            {
                new FluidGpuUInt2((uint)MaterialId.Water, FluidGpuLayout.ActiveFlag),
                new FluidGpuUInt2((uint)MaterialId.Poison, FluidGpuLayout.ActiveFlag),
            });
            resources.Counters.SetData(new uint[FluidGpuLayout.CounterCount]);
        }

        private static LiquidMaterialSettings CreateMaterialSettings(
            MaterialId material,
            float mass,
            float restDensity)
        {
            return new LiquidMaterialSettings(
                material, 8u, mass, restDensity, 0.1f, 0.001f, 12f, 1f, 0.2f, 0.02f);
        }

        private static void UploadActiveParticlesAtSlots(
            FluidGpuResourceSet resources,
            int[] activeSlots,
            Vector4[] activePositions)
        {
            Assert.That(activeSlots.Length, Is.EqualTo(activePositions.Length));
            var positions = new Vector4[resources.ParticleCapacity];
            var predictedPositions = new Vector4[resources.ParticleCapacity];
            var velocities = new Vector4[resources.ParticleCapacity];
            var densityLambda = new Vector2[resources.ParticleCapacity];
            var deltaPositions = new Vector4[resources.ParticleCapacity];
            var deltaVelocities = new Vector4[resources.ParticleCapacity];
            var vorticities = new Vector4[resources.ParticleCapacity];
            var metadata = new FluidGpuUInt2[resources.ParticleCapacity];
            var counters = new uint[FluidGpuLayout.CounterCount];

            for (int activeIndex = 0; activeIndex < activeSlots.Length; activeIndex++)
            {
                int slot = activeSlots[activeIndex];
                Assert.That(slot, Is.InRange(0, resources.ParticleCapacity - 1));
                positions[slot] = activePositions[activeIndex];
                predictedPositions[slot] = activePositions[activeIndex];
                metadata[slot] = new FluidGpuUInt2(1u, FluidGpuLayout.ActiveFlag);
            }

            resources.Positions.SetData(positions);
            resources.PredictedPositions.SetData(predictedPositions);
            resources.Velocities.SetData(velocities);
            resources.DensityLambda.SetData(densityLambda);
            resources.DeltaPositions.SetData(deltaPositions);
            resources.DeltaVelocities.SetData(deltaVelocities);
            resources.Vorticities.SetData(vorticities);
            resources.Metadata.SetData(metadata);
            resources.Counters.SetData(counters);
        }

        private static void SetPredictedPositions(FluidGpuResourceSet resources, Vector4[] positions)
        {
            Assert.That(positions.Length, Is.EqualTo(resources.ParticleCapacity));
            resources.PredictedPositions.SetData(positions);
        }

        private static void SetUniformActiveDensity(FluidGpuResourceSet resources, float density)
        {
            FluidGpuUInt2[] metadata = ReadMetadata(resources);
            var densityLambda = new Vector2[resources.ParticleCapacity];
            for (int index = 0; index < metadata.Length; index++)
            {
                if ((metadata[index].Y & FluidGpuLayout.ActiveFlag) == 0u)
                    continue;

                densityLambda[index] = new Vector2(density, 0f);
            }

            resources.DensityLambda.SetData(densityLambda);
        }

        private static float ReadBruteForceAverageDensityError(FluidGpuResourceSet resources)
        {
            Vector2[] bruteForceDensities = CalculateBruteForceDensities(resources, out int activeCount);
            FluidGpuUInt2[] metadata = ReadMetadata(resources);

            float sum = 0f;
            for (int index = 0; index < metadata.Length; index++)
            {
                if ((metadata[index].Y & FluidGpuLayout.ActiveFlag) == 0u)
                    continue;

                sum += Mathf.Abs(bruteForceDensities[index].x / RestDensity - 1f);
            }

            Assert.That(activeCount, Is.GreaterThan(0));
            return sum / activeCount;
        }

        private static void AssertGpuDensityMatchesBruteForce(FluidGpuResourceSet resources)
        {
            Vector2[] bruteForceDensities = CalculateBruteForceDensities(resources, out _);
            FluidGpuUInt2[] metadata = ReadMetadata(resources);
            var gpuDensityLambda = new Vector2[resources.ParticleCapacity];
            resources.DensityLambda.GetData(gpuDensityLambda);

            for (int index = 0; index < metadata.Length; index++)
            {
                if ((metadata[index].Y & FluidGpuLayout.ActiveFlag) == 0u)
                    continue;

                Assert.That(
                    gpuDensityLambda[index].x,
                    Is.EqualTo(bruteForceDensities[index].x).Within(1e-3f),
                    "GPU Density 必须匹配不依赖 SpatialHash 的 CPU brute-force Poly6 oracle。");
            }
        }

        private static Vector2[] CalculateBruteForceDensities(
            FluidGpuResourceSet resources,
            out int activeCount)
        {
            var predictedPositions = new Vector4[resources.ParticleCapacity];
            var metadata = new FluidGpuUInt2[resources.ParticleCapacity];
            var densities = new Vector2[resources.ParticleCapacity];
            resources.PredictedPositions.GetData(predictedPositions);
            resources.Metadata.GetData(metadata);

            activeCount = 0;
            for (int index = 0; index < metadata.Length; index++)
            {
                if ((metadata[index].Y & FluidGpuLayout.ActiveFlag) == 0u)
                    continue;

                activeCount++;
                float density = 0f;
                Vector3 position = (Vector3)predictedPositions[index];
                for (int neighborIndex = 0; neighborIndex < metadata.Length; neighborIndex++)
                {
                    if ((metadata[neighborIndex].Y & FluidGpuLayout.ActiveFlag) == 0u)
                        continue;

                    float distance = Vector3.Distance(position, (Vector3)predictedPositions[neighborIndex]);
                    density += ParticleMass * PbfKernelMath.Poly6(distance, SmoothingRadius);
                }

                densities[index] = new Vector2(density, 0f);
            }

            return densities;
        }

        private static int CountActive(FluidGpuResourceSet resources)
        {
            FluidGpuUInt2[] metadata = ReadMetadata(resources);

            int activeCount = 0;
            for (int index = 0; index < metadata.Length; index++)
            {
                if ((metadata[index].Y & FluidGpuLayout.ActiveFlag) != 0u)
                    activeCount++;
            }

            return activeCount;
        }

        private static FluidGpuUInt2[] ReadMetadata(FluidGpuResourceSet resources)
        {
            var metadata = new FluidGpuUInt2[resources.ParticleCapacity];
            resources.Metadata.GetData(metadata);
            return metadata;
        }

        private static void AssertFiniteActiveState(FluidGpuResourceSet resources)
        {
            var positions = new Vector4[resources.ParticleCapacity];
            var predictedPositions = new Vector4[resources.ParticleCapacity];
            var velocities = new Vector4[resources.ParticleCapacity];
            var densityLambda = new Vector2[resources.ParticleCapacity];
            var metadata = new FluidGpuUInt2[resources.ParticleCapacity];
            resources.Positions.GetData(positions);
            resources.PredictedPositions.GetData(predictedPositions);
            resources.Velocities.GetData(velocities);
            resources.DensityLambda.GetData(densityLambda);
            resources.Metadata.GetData(metadata);

            for (int index = 0; index < metadata.Length; index++)
            {
                if ((metadata[index].Y & FluidGpuLayout.ActiveFlag) == 0u)
                    continue;

                Assert.That(IsFinite(positions[index]), Is.True, $"Position {index} is non-finite.");
                Assert.That(IsFinite(predictedPositions[index]), Is.True, $"PredictedPosition {index} is non-finite.");
                Assert.That(IsFinite(velocities[index]), Is.True, $"Velocity {index} is non-finite.");
                Assert.That(IsFinite(densityLambda[index]), Is.True, $"Density/Lambda {index} is non-finite.");
            }
        }

        private static void AssertNoNumericalErrors(FluidGpuResourceSet resources)
        {
            var counters = new uint[FluidGpuLayout.CounterCount];
            resources.Counters.GetData(counters);
            Assert.That(
                counters[FluidGpuLayout.NumericalErrorCounterIndex],
                Is.Zero,
                "正常 fixture 不应依赖 NaN/Infinity sanitize-to-zero 才通过。");
        }

        private static Vector4[] CreateDenseBlock()
        {
            var positions = new Vector4[8];
            int index = 0;
            for (int z = 0; z < 2; z++)
            {
                for (int y = 0; y < 2; y++)
                {
                    for (int x = 0; x < 2; x++)
                        // 故意偏离 cell 边界；0-vs-4 的密度回归不能靠 x/y/z=0 的边界假阳性成立。
                        positions[index++] = new Vector4(
                            0.04f + x * 0.03f,
                            0.04f + y * 0.03f,
                            0.04f + z * 0.03f,
                            1f);
                }
            }

            return positions;
        }

        private static ComputeShader LoadRequiredShader(string path)
        {
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
            Assert.That(shader, Is.Not.Null, $"Missing ComputeShader at {path}.");
            return shader;
        }

        private static int DivideRoundUp(int count)
        {
            return (count + ThreadGroupSize - 1) / ThreadGroupSize;
        }

        private static bool IsFinite(Vector4 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w);
        }

        private static bool IsFinite(Vector2 value)
        {
            return IsFinite(value.x) && IsFinite(value.y);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static void IgnoreWithoutComputeSupport()
        {
            if (!SystemInfo.supportsComputeShaders
                || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Assert.Ignore("Current Graphics Device does not support ComputeShader; GPU numeric test is skipped, not passed.");
            }
        }
    }
}
