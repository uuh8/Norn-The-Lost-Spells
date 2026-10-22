using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.ElementField.Tests
{
    public sealed class GpuFluidChunkArchiveTests
    {
        private const string ShaderPath = "Assets/_Project/Art/Elemental/Compute/PbfParticleLifecycle.compute";

        [Test]
        public void Archive_OutsideRetainedBounds_PacksAndReleasesExactlyThoseSlots()
        {
            if (!SystemInfo.supportsComputeShaders || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("Requires a real graphics device with ComputeShader support.");
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderPath);
            Assert.That(shader, Is.Not.Null);
            var resources = new FluidGpuResourceSet(4, 8, 1);
            try
            {
                Initialize(shader, resources);
                resources.Positions.SetData(new[]
                {
                    new Vector4(0f, 0f, 0f, 1f), new Vector4(5f, 0f, 0f, 1f),
                    new Vector4(-5f, 0f, 0f, 1f), Vector4.zero,
                });
                resources.Velocities.SetData(new[]
                {
                    Vector4.zero, new Vector4(1f, 2f, 3f, 0f), new Vector4(-1f, 0f, 1f, 0f), Vector4.zero,
                });
                resources.Metadata.SetData(new[]
                {
                    new FluidGpuUInt2(1u, FluidGpuLayout.ActiveFlag),
                    new FluidGpuUInt2(1u, FluidGpuLayout.AliveFlag),
                    new FluidGpuUInt2(3u, FluidGpuLayout.AliveFlag),
                    new FluidGpuUInt2(0u, 0u),
                });
                resources.Counters.SetData(new[] { 1u, 3u, 0u, 0u });
                resources.FreeIndices.SetData(new[] { 3u, 0u, 0u, 0u });

                int lockKernel = shader.FindKernel("LockArchiveCandidates");
                shader.SetInt("_ParticleCapacity", 4);
                shader.SetVector("_ArchiveRetainedBoundsMin", new Vector4(-1f, -1f, -1f, 0f));
                shader.SetVector("_ArchiveRetainedBoundsMax", new Vector4(1f, 1f, 1f, 0f));
                shader.SetInt("_ArchiveIncludeSleepingRetained", 0);
                shader.SetBuffer(lockKernel, "_Positions", resources.Positions);
                shader.SetBuffer(lockKernel, "_ParticleMetadata", resources.Metadata);
                shader.Dispatch(lockKernel, 1, 1, 1);

                int pack = shader.FindKernel("PackArchiveSamples");
                shader.SetBuffer(pack, "_Positions", resources.Positions);
                shader.SetBuffer(pack, "_Velocities", resources.Velocities);
                shader.SetBuffer(pack, "_ParticleMetadata", resources.Metadata);
                shader.SetBuffer(pack, "_ArchiveSamples", resources.ArchiveSamples);
                shader.Dispatch(pack, 1, 1, 1);
                var samples = new FluidGpuArchiveSample[4];
                resources.ArchiveSamples.GetData(samples);
                Assert.That(samples[0].Flags, Is.EqualTo(FluidGpuLayout.ActiveFlag),
                    "未锁定 Alive 样本必须可见，供 CPU 验证 Retained Chunk 的唯一权威。");
                Assert.That(samples[1].MaterialId, Is.EqualTo(1u));
                Assert.That(samples[1].Velocity, Is.EqualTo(new Vector3(1f, 2f, 3f)));
                Assert.That(samples[2].MaterialId, Is.EqualTo(3u));

                int release = shader.FindKernel("ReleaseArchiveLockedParticles");
                shader.SetBuffer(release, "_Positions", resources.Positions);
                shader.SetBuffer(release, "_PredictedPositions", resources.PredictedPositions);
                shader.SetBuffer(release, "_Velocities", resources.Velocities);
                shader.SetBuffer(release, "_ParticleMetadata", resources.Metadata);
                shader.SetBuffer(release, "_FreeIndices", resources.FreeIndices);
                shader.SetBuffer(release, "_Counters", resources.Counters);
                shader.Dispatch(release, 1, 1, 1);
                var counters = new uint[FluidGpuLayout.CounterCount];
                resources.Counters.GetData(counters);
                Assert.That(counters[FluidGpuLayout.ActiveCountCounterIndex], Is.EqualTo(1u));
                Assert.That(counters[FluidGpuLayout.FreeCountCounterIndex], Is.EqualTo(3u));
                var metadata = new FluidGpuUInt2[4];
                resources.Metadata.GetData(metadata);
                Assert.That(metadata[0].Y, Is.EqualTo(FluidGpuLayout.ActiveFlag));
                Assert.That(metadata[1].Y, Is.Zero);
                Assert.That(metadata[2].Y, Is.Zero);
            }
            finally { resources.Dispose(); }
        }

        [Test]
        public void Archive_PressureModeLocksRetainedSleepingButPreservesAwakeAndCancelState()
        {
            if (!SystemInfo.supportsComputeShaders || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("Requires a real graphics device with ComputeShader support.");
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderPath);
            var resources = new FluidGpuResourceSet(3, 8, 1);
            try
            {
                Initialize(shader, resources);
                resources.Positions.SetData(new[]
                {
                    new Vector4(0f, 0f, 0f, 1f),
                    new Vector4(.5f, 0f, 0f, 1f),
                    new Vector4(5f, 0f, 0f, 1f),
                });
                uint sleeping = FluidGpuLayout.AliveFlag | FluidActivityFlags.InterestActive
                    | FluidActivityFlags.Sleeping;
                resources.Metadata.SetData(new[]
                {
                    new FluidGpuUInt2(1u, sleeping),
                    new FluidGpuUInt2(1u, FluidGpuLayout.ActiveFlag),
                    new FluidGpuUInt2(1u, FluidGpuLayout.AliveFlag),
                });

                int kernel = shader.FindKernel("LockArchiveCandidates");
                shader.SetInt("_ParticleCapacity", 3);
                shader.SetInt("_ArchiveIncludeSleepingRetained", 1);
                shader.SetVector("_ArchiveRetainedBoundsMin", new Vector4(-1f, -1f, -1f, 0f));
                shader.SetVector("_ArchiveRetainedBoundsMax", new Vector4(1f, 1f, 1f, 0f));
                shader.SetBuffer(kernel, "_Positions", resources.Positions);
                shader.SetBuffer(kernel, "_ParticleMetadata", resources.Metadata);
                shader.Dispatch(kernel, 1, 1, 1);

                var metadata = new FluidGpuUInt2[3];
                resources.Metadata.GetData(metadata);
                Assert.That(metadata[0].Y, Is.EqualTo(sleeping | FluidActivityFlags.ArchiveLocked));
                Assert.That(metadata[1].Y, Is.EqualTo(FluidGpuLayout.ActiveFlag));
                Assert.That(metadata[2].Y, Is.EqualTo(FluidGpuLayout.AliveFlag | FluidActivityFlags.ArchiveLocked));

                int cancel = shader.FindKernel("CancelArchiveLock");
                shader.SetBuffer(cancel, "_ParticleMetadata", resources.Metadata);
                shader.Dispatch(cancel, 1, 1, 1);
                resources.Metadata.GetData(metadata);
                Assert.That(metadata[0].Y, Is.EqualTo(sleeping));
                Assert.That(metadata[1].Y, Is.EqualTo(FluidGpuLayout.ActiveFlag));
                Assert.That(metadata[2].Y, Is.EqualTo(FluidGpuLayout.AliveFlag));
            }
            finally { resources.Dispose(); }
        }

        [Test]
        public void Archive_ExcludeListUnlocksOnlyMixedChunkParticleBeforeRelease()
        {
            if (!SystemInfo.supportsComputeShaders || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("Requires a real graphics device with ComputeShader support.");
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderPath);
            var resources = new FluidGpuResourceSet(2, 4, 1);
            try
            {
                Initialize(shader, resources);
                uint lockedSleeping = FluidGpuLayout.AliveFlag | FluidActivityFlags.InterestActive
                    | FluidActivityFlags.Sleeping | FluidActivityFlags.ArchiveLocked;
                resources.Metadata.SetData(new[]
                {
                    new FluidGpuUInt2(1u, lockedSleeping),
                    new FluidGpuUInt2(1u, lockedSleeping)
                });
                resources.Counters.SetData(new[] { 0u, 2u, 0u, 0u });
                resources.ArchiveUnlockIndices.SetData(new[] { 0u, 0u });

                int exclude = shader.FindKernel("ExcludeArchiveParticleIndices");
                shader.SetInt("_ParticleCapacity", 2);
                shader.SetInt("_ArchiveUnlockIndexCount", 1);
                shader.SetBuffer(exclude, "_ParticleMetadata", resources.Metadata);
                shader.SetBuffer(exclude, "_ArchiveUnlockIndices", resources.ArchiveUnlockIndices);
                shader.Dispatch(exclude, 1, 1, 1);

                int release = shader.FindKernel("ReleaseArchiveLockedParticles");
                shader.SetBuffer(release, "_Positions", resources.Positions);
                shader.SetBuffer(release, "_PredictedPositions", resources.PredictedPositions);
                shader.SetBuffer(release, "_Velocities", resources.Velocities);
                shader.SetBuffer(release, "_ParticleMetadata", resources.Metadata);
                shader.SetBuffer(release, "_FreeIndices", resources.FreeIndices);
                shader.SetBuffer(release, "_Counters", resources.Counters);
                shader.Dispatch(release, 1, 1, 1);

                var metadata = new FluidGpuUInt2[2];
                resources.Metadata.GetData(metadata);
                Assert.That(metadata[0].Y, Is.EqualTo(
                    FluidGpuLayout.AliveFlag | FluidActivityFlags.InterestActive
                    | FluidActivityFlags.Sleeping));
                Assert.That(metadata[1].Y, Is.Zero);
                var counters = new uint[FluidGpuLayout.CounterCount];
                resources.Counters.GetData(counters);
                Assert.That(counters[FluidGpuLayout.ActiveCountCounterIndex], Is.EqualTo(1u));
                Assert.That(counters[FluidGpuLayout.FreeCountCounterIndex], Is.EqualTo(1u));
            }
            finally { resources.Dispose(); }
        }

        private static void Initialize(ComputeShader shader, FluidGpuResourceSet resources)
        {
            int core = shader.FindKernel("InitializePool");
            shader.SetInt("_ParticleCapacity", resources.ParticleCapacity);
            shader.SetBuffer(core, "_Positions", resources.Positions);
            shader.SetBuffer(core, "_PredictedPositions", resources.PredictedPositions);
            shader.SetBuffer(core, "_Velocities", resources.Velocities);
            shader.SetBuffer(core, "_DensityLambda", resources.DensityLambda);
            shader.SetBuffer(core, "_ParticleMetadata", resources.Metadata);
            shader.SetBuffer(core, "_DeltaPositions", resources.DeltaPositions);
            shader.SetBuffer(core, "_SpatialEntries", resources.SpatialEntries);
            shader.SetBuffer(core, "_FreeIndices", resources.FreeIndices);
            shader.Dispatch(core, 1, 1, 1);
            int auxiliary = shader.FindKernel("InitializePoolAuxiliary");
            shader.SetInt("_HashTableCapacity", resources.HashTableCapacity);
            shader.SetBuffer(auxiliary, "_CellRanges", resources.CellRanges);
            shader.SetBuffer(auxiliary, "_Counters", resources.Counters);
            shader.Dispatch(auxiliary, 1, 1, 1);
        }
    }
}
