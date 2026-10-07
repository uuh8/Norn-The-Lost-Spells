using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// GPU Spatial Hash 数值契约。GetData 会同步等待 GPU，仅限 Test-only Readback；
    /// Runtime 的固定 Tick 绝不能复制这些读取，以免 CPU/GPU 流水线被阻塞。
    /// </summary>
    public sealed class GpuFluidSpatialHashTests
    {
        private const string SpatialHashShaderPath =
            "Assets/_Project/Art/Elemental/Compute/PbfSpatialHash.compute";
        private const int ThreadGroupSize = 64;

        [Test]
        public void BuildSortAndRangeMapPreserveCandidateRangesAndRejectCollidingRealCells()
        {
            IgnoreWithoutComputeSupport();
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(SpatialHashShaderPath);
            Assert.That(shader, Is.Not.Null, $"Missing ComputeShader at {SpatialHashShaderPath}.");

            const int particleCapacity = 8;
            const int hashTableCapacity = 2;
            const float smoothingRadius = 1f;
            var resources = new FluidGpuResourceSet(particleCapacity, hashTableCapacity, maxSpawnRequests: 1);
            try
            {
                var predictedPositions = new[]
                {
                    new Vector4(0.20f, 0.20f, 0.20f, 1f),  // target cell (0, 0, 0)
                    new Vector4(0.80f, 0.40f, 0.20f, 1f),  // same real cell
                    new Vector4(2.20f, 0.20f, 0.20f, 1f),  // same bucket, different real cell
                    new Vector4(-0.20f, -0.20f, -0.20f, 1f),
                    new Vector4(0f, 0f, 0f, 0f),
                    new Vector4(0f, 0f, 0f, 0f),
                    new Vector4(0f, 0f, 0f, 0f),
                    new Vector4(0f, 0f, 0f, 0f)
                };
                resources.PredictedPositions.SetData(predictedPositions);
                var metadata = new FluidGpuUInt2[particleCapacity];
                for (int index = 0; index < 4; index++)
                    metadata[index] = new FluidGpuUInt2(0u, FluidGpuLayout.ActiveFlag);
                resources.Metadata.SetData(metadata);

                DispatchSpatialHash(shader, resources, smoothingRadius);

                var entries = new FluidGpuUInt2[particleCapacity];
                var ranges = new FluidGpuUInt2[hashTableCapacity];
                var spatialCells = new FluidGpuInt4[particleCapacity];
                resources.SpatialEntries.GetData(entries);
                resources.CellRanges.GetData(ranges);
                resources.SpatialCells.GetData(spatialCells);

                Assert.That(spatialCells[0], Is.EqualTo(new FluidGpuInt4(0, 0, 0, 1)));
                Assert.That(spatialCells[1], Is.EqualTo(new FluidGpuInt4(0, 0, 0, 1)));
                Assert.That(spatialCells[2], Is.EqualTo(new FluidGpuInt4(2, 0, 0, 1)));
                Assert.That(spatialCells[3], Is.EqualTo(new FluidGpuInt4(-1, -1, -1, 1)));
                Assert.That(spatialCells[4], Is.EqualTo(new FluidGpuInt4(0, 0, 0, 0)));

                AssertEntriesAreLexicographicallySorted(entries);
                AssertInactiveEntriesAreAtTheEnd(entries);
                AssertRangesAreHalfOpenAndMatchTheirBucket(entries, ranges);

                Vector3Int targetCell = Vector3Int.zero;
                uint targetBucket = FluidSpatialHash.HashCell(targetCell, hashTableCapacity);
                FluidGpuUInt2 targetRange = ranges[targetBucket];
                Assert.That(targetRange.X, Is.Not.EqualTo(FluidSpatialHash.InactiveBucket));
                Assert.That(targetRange.Y, Is.GreaterThan(targetRange.X));

                bool foundSameCell = false;
                bool foundCollisionCandidate = false;
                bool collisionPassedRealCellFilter = false;
                for (uint sortedIndex = targetRange.X; sortedIndex < targetRange.Y; sortedIndex++)
                {
                    uint particleIndex = entries[(int)sortedIndex].Y;
                    Vector3 candidate = (Vector3)predictedPositions[(int)particleIndex];
                    bool isTargetRealCell = FluidSpatialHash.IsCandidateInCell(
                        candidate,
                        targetCell,
                        smoothingRadius);
                    if (particleIndex == 0u || particleIndex == 1u)
                        foundSameCell |= isTargetRealCell;
                    if (particleIndex == 2u)
                    {
                        foundCollisionCandidate = true;
                        collisionPassedRealCellFilter = isTargetRealCell;
                    }
                }

                Assert.That(foundSameCell, Is.True);
                Assert.That(foundCollisionCandidate, Is.True);
                Assert.That(collisionPassedRealCellFilter, Is.False);
            }
            finally
            {
                resources.Dispose();
            }
        }

        [Test]
        public void BuildSpatialEntriesKeepsAliveParticleOutsideInterestForRendering()
        {
            IgnoreWithoutComputeSupport();
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(SpatialHashShaderPath);
            Assert.That(shader, Is.Not.Null);

            var resources = new FluidGpuResourceSet(2, 2, maxSpawnRequests: 1);
            try
            {
                resources.PredictedPositions.SetData(new[]
                {
                    new Vector4(2f, 0f, 0f, 1f),
                    Vector4.zero
                });
                resources.Metadata.SetData(new[]
                {
                    new FluidGpuUInt2(1u, FluidGpuLayout.AliveFlag),
                    new FluidGpuUInt2(0u, 0u)
                });

                DispatchSpatialHash(shader, resources, 1f);
                var cells = new FluidGpuInt4[2];
                resources.SpatialCells.GetData(cells);

                Assert.That(cells[0], Is.EqualTo(new FluidGpuInt4(2, 0, 0, 1)),
                    "Alive 但离开 Interest 的粒子必须保留在 Rendering 可读 Hash 中。");
                Assert.That(cells[1].W, Is.Zero);
            }
            finally
            {
                resources.Dispose();
            }
        }

        [Test]
        public void NeighborWake_PropagatesVisibleMotionButIgnoresSettlingJitter()
        {
            IgnoreWithoutComputeSupport();
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(SpatialHashShaderPath);
            Assert.That(shader, Is.Not.Null);
            var resources = new FluidGpuResourceSet(2, 4, maxSpawnRequests: 1);
            try
            {
                resources.PredictedPositions.SetData(new[]
                {
                    new Vector4(.1f, .1f, .1f, 1f),
                    new Vector4(.2f, .1f, .1f, 1f)
                });
                uint sleeping = FluidGpuLayout.AliveFlag | FluidActivityFlags.InterestActive
                    | FluidActivityFlags.Sleeping;
                resources.Metadata.SetData(new[]
                {
                    new FluidGpuUInt2(1u, sleeping),
                    new FluidGpuUInt2(1u, FluidGpuLayout.ActiveFlag)
                });
                resources.Velocities.SetData(new[]
                {
                    Vector4.zero,
                    new Vector4(.05f, 0f, 0f, 0f)
                });
                // GraphicsBuffer 创建后内容未定义；Runtime 由 InitializeActivity 清零，独立 Kernel 测试需显式复现。
                resources.WakeRequests.SetData(new uint[2]);
                DispatchSpatialHash(shader, resources, 1f);

                int wakeKernel = shader.FindKernel("MarkNeighborWakeRequests");
                shader.SetInt("_ParticleCapacity", 2);
                shader.SetInt("_HashTableCapacity", 4);
                shader.SetFloat("_SmoothingRadius", 1f);
                shader.SetFloat("_WakeVelocityThreshold", .1f);
                shader.SetBuffer(wakeKernel, "_PredictedPositions", resources.PredictedPositions);
                shader.SetBuffer(wakeKernel, "_Velocities", resources.Velocities);
                shader.SetBuffer(wakeKernel, "_ParticleMetadata", resources.Metadata);
                shader.SetBuffer(wakeKernel, "_SpatialCells", resources.SpatialCells);
                shader.SetBuffer(wakeKernel, "_SpatialEntries", resources.SpatialEntries);
                shader.SetBuffer(wakeKernel, "_CellRanges", resources.CellRanges);
                shader.SetBuffer(wakeKernel, "_WakeRequests", resources.WakeRequests);
                shader.Dispatch(wakeKernel, 1, 1, 1);
                var wakes = new uint[2];
                resources.WakeRequests.GetData(wakes);
                Assert.That(wakes[0], Is.Zero,
                    "低于静止阈值的 Solver 抖动不能阻止相邻粒子完成休眠。");

                resources.Velocities.SetData(new[]
                {
                    Vector4.zero,
                    new Vector4(.2f, 0f, 0f, 0f)
                });
                shader.Dispatch(wakeKernel, 1, 1, 1);
                resources.WakeRequests.GetData(wakes);
                Assert.That(wakes[0], Is.EqualTo(1u),
                    "可见运动必须沿 PBF 邻域唤醒已休眠粒子。");
            }
            finally
            {
                resources.Dispose();
            }
        }

        private static void DispatchSpatialHash(
            ComputeShader shader,
            FluidGpuResourceSet resources,
            float smoothingRadius)
        {
            int buildEntriesKernel = shader.FindKernel("BuildSpatialEntries");
            int sortKernel = shader.FindKernel("BitonicSort");
            int clearRangesKernel = shader.FindKernel("ClearCellRanges");
            int buildRangesKernel = shader.FindKernel("BuildCellRanges");
            int particleGroups = DivideRoundUp(resources.ParticleCapacity);
            int hashGroups = DivideRoundUp(resources.HashTableCapacity);

            shader.SetInt("_ParticleCapacity", resources.ParticleCapacity);
            shader.SetInt("_HashTableCapacity", resources.HashTableCapacity);
            shader.SetFloat("_SmoothingRadius", smoothingRadius);
            shader.SetBuffer(buildEntriesKernel, "_PredictedPositions", resources.PredictedPositions);
            shader.SetBuffer(buildEntriesKernel, "_ParticleMetadata", resources.Metadata);
            shader.SetBuffer(buildEntriesKernel, "_SpatialCells", resources.SpatialCells);
            shader.SetBuffer(buildEntriesKernel, "_SpatialEntries", resources.SpatialEntries);
            shader.Dispatch(buildEntriesKernel, particleGroups, 1, 1);

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

            shader.SetBuffer(clearRangesKernel, "_CellRanges", resources.CellRanges);
            shader.Dispatch(clearRangesKernel, hashGroups, 1, 1);

            shader.SetBuffer(buildRangesKernel, "_SpatialEntries", resources.SpatialEntries);
            shader.SetBuffer(buildRangesKernel, "_CellRanges", resources.CellRanges);
            shader.Dispatch(buildRangesKernel, particleGroups, 1, 1);
        }

        private static void AssertEntriesAreLexicographicallySorted(FluidGpuUInt2[] entries)
        {
            for (int i = 1; i < entries.Length; i++)
            {
                Assert.That(
                    FluidSpatialHash.CompareEntries(entries[i - 1], entries[i]),
                    Is.LessThanOrEqualTo(0),
                    $"Entries {i - 1} and {i} are not sorted by (bucket, particle index).");
            }
        }

        private static void AssertInactiveEntriesAreAtTheEnd(FluidGpuUInt2[] entries)
        {
            bool encounteredInactive = false;
            int inactiveCount = 0;
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].X == FluidSpatialHash.InactiveBucket)
                {
                    encounteredInactive = true;
                    Assert.That(entries[i].Y, Is.EqualTo((uint)(4 + inactiveCount)));
                    inactiveCount++;
                }
                else
                    Assert.That(encounteredInactive, Is.False, "Active entry appeared after inactive sentinel.");
            }

            // 本 fixture 固定上传四个 inactive Slot；仅检查“末尾都是 sentinel”会漏掉漏写或错误复用 index。
            Assert.That(inactiveCount, Is.EqualTo(4));
        }

        private static void AssertRangesAreHalfOpenAndMatchTheirBucket(
            FluidGpuUInt2[] entries,
            FluidGpuUInt2[] ranges)
        {
            for (uint bucket = 0; bucket < ranges.Length; bucket++)
            {
                FluidGpuUInt2 range = ranges[bucket];
                uint expectedStart = uint.MaxValue;
                uint expectedEnd = 0u;
                for (uint sortedIndex = 0u; sortedIndex < entries.Length; sortedIndex++)
                {
                    if (entries[(int)sortedIndex].X != bucket)
                        continue;

                    if (sortedIndex < expectedStart)
                        expectedStart = sortedIndex;
                    if (sortedIndex + 1u > expectedEnd)
                        expectedEnd = sortedIndex + 1u;
                }

                if (range.X == FluidSpatialHash.InactiveBucket)
                {
                    Assert.That(range.Y, Is.Zero);
                    Assert.That(expectedStart, Is.EqualTo(uint.MaxValue));
                    continue;
                }

                Assert.That(range.X, Is.LessThanOrEqualTo(range.Y));
                Assert.That(range.Y, Is.LessThanOrEqualTo((uint)entries.Length));
                Assert.That(range.X, Is.EqualTo(expectedStart));
                Assert.That(range.Y, Is.EqualTo(expectedEnd));
                for (uint sortedIndex = range.X; sortedIndex < range.Y; sortedIndex++)
                    Assert.That(entries[(int)sortedIndex].X, Is.EqualTo(bucket));
            }
        }

        private static int DivideRoundUp(int count)
        {
            return (count + ThreadGroupSize - 1) / ThreadGroupSize;
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
