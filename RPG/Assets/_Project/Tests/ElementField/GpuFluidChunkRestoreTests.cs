using Game.Materials;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.ElementField.Tests
{
    public sealed class GpuFluidChunkRestoreTests
    {
        private const string ShaderPath = "Assets/_Project/Art/Elemental/Compute/PbfParticleLifecycle.compute";

        [Test]
        public void Restore_ReserveStageAndActivate_IsAllOrNothing()
        {
            RequireCompute();
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderPath);
            var resources = new FluidGpuResourceSet(4, 8, 1);
            try
            {
                Initialize(shader, resources);
                resources.RestoreParticles.SetData(new[]
                {
                    new FluidGpuRestoreParticle(Vector3.zero, MaterialId.Water, Vector3.right, 7u),
                    new FluidGpuRestoreParticle(new Vector3(5f, 0f, 0f), MaterialId.Poison, Vector3.up, 7u),
                }, 0, 0, 2);

                Reserve(shader, resources, 2, 1, 7);
                FluidGpuTransferStatus status = ReadStatus(resources);
                Assert.That(status.Succeeded, Is.EqualTo(1u));
                Assert.That(status.ReservedBase, Is.EqualTo(2u));
                Assert.That(ReadCounters(resources)[FluidGpuLayout.FreeCountCounterIndex], Is.EqualTo(2u));

                Stage(shader, resources, 2, 7);
                var staged = new FluidGpuUInt2[4];
                resources.Metadata.GetData(staged);
                Assert.That(staged[2].Y, Is.EqualTo(FluidGpuLayout.RestoreLoadingFlag));
                Assert.That(staged[3].Y, Is.EqualTo(FluidGpuLayout.RestoreLoadingFlag));
                Assert.That((staged[2].Y & FluidGpuLayout.AliveFlag), Is.Zero);

                Activate(shader, resources, 2, 7);
                resources.Metadata.GetData(staged);
                Assert.That(staged[2].Y, Is.EqualTo(FluidGpuLayout.ActiveFlag));
                Assert.That(staged[3].Y, Is.EqualTo(
                    FluidGpuLayout.AliveFlag | FluidGpuLayout.RemoteSpawnActiveFlag
                    | FluidGpuLayout.RequiresSimulationFlag));
                Assert.That(ReadCounters(resources)[FluidGpuLayout.ActiveCountCounterIndex], Is.EqualTo(2u));
            }
            finally { resources.Dispose(); }
        }

        [Test]
        public void Restore_InsufficientReserve_DoesNotChangePoolOrStageSlots()
        {
            RequireCompute();
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderPath);
            var resources = new FluidGpuResourceSet(4, 8, 1);
            try
            {
                Initialize(shader, resources);
                Reserve(shader, resources, 3, 2, 8);
                Assert.That(ReadStatus(resources).Succeeded, Is.Zero);
                Assert.That(ReadCounters(resources)[FluidGpuLayout.FreeCountCounterIndex], Is.EqualTo(4u));
                Stage(shader, resources, 3, 8);
                var metadata = new FluidGpuUInt2[4];
                resources.Metadata.GetData(metadata);
                Assert.That(metadata[0].Y | metadata[1].Y | metadata[2].Y | metadata[3].Y, Is.Zero);
            }
            finally { resources.Dispose(); }
        }

        [Test]
        public void Restore_RollbackReturnsEveryReservedSlotOnce_WrongTransactionDoesNothing()
        {
            RequireCompute();
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderPath);
            var resources = new FluidGpuResourceSet(4, 8, 1);
            try
            {
                Initialize(shader, resources);
                resources.RestoreParticles.SetData(new[]
                {
                    new FluidGpuRestoreParticle(Vector3.one, MaterialId.Sticky, Vector3.zero, 9u),
                    new FluidGpuRestoreParticle(Vector3.one * 2f, MaterialId.Sticky, Vector3.zero, 9u),
                }, 0, 0, 2);
                Reserve(shader, resources, 2, 0, 9);
                Stage(shader, resources, 2, 9);

                Rollback(shader, resources, 2, 10);
                Assert.That(ReadCounters(resources)[FluidGpuLayout.FreeCountCounterIndex], Is.EqualTo(2u));
                Rollback(shader, resources, 2, 9);
                Assert.That(ReadCounters(resources)[FluidGpuLayout.FreeCountCounterIndex], Is.EqualTo(4u));
                Rollback(shader, resources, 2, 9);
                Assert.That(ReadCounters(resources)[FluidGpuLayout.FreeCountCounterIndex], Is.EqualTo(4u));
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

        private static void Reserve(ComputeShader shader, FluidGpuResourceSet r, int count, int reserve, int transaction)
        {
            int k = shader.FindKernel("ReserveRestoreSlots");
            shader.SetInt("_RestoreParticleCount", count);
            shader.SetInt("_GameplayReserveParticles", reserve);
            shader.SetInt("_TransferTransactionId", transaction);
            shader.SetBuffer(k, "_Counters", r.Counters);
            shader.SetBuffer(k, "_TransferStatus", r.TransferStatus);
            shader.Dispatch(k, 1, 1, 1);
        }

        private static void Stage(ComputeShader shader, FluidGpuResourceSet r, int count, int transaction)
        {
            int core = shader.FindKernel("StageRestoreParticlesCore");
            shader.SetInt("_ParticleCapacity", r.ParticleCapacity);
            shader.SetInt("_TransferTransactionId", transaction);
            shader.SetBuffer(core, "_RestoreParticles", r.RestoreParticles);
            shader.SetBuffer(core, "_RestoreReservedIndices", r.RestoreReservedIndices);
            shader.SetBuffer(core, "_TransferStatus", r.TransferStatus);
            shader.SetBuffer(core, "_FreeIndices", r.FreeIndices);
            shader.SetBuffer(core, "_Positions", r.Positions);
            shader.SetBuffer(core, "_PredictedPositions", r.PredictedPositions);
            shader.SetBuffer(core, "_Velocities", r.Velocities);
            shader.SetBuffer(core, "_ParticleMetadata", r.Metadata);
            shader.Dispatch(core, 1, 1, 1);

            int auxiliary = shader.FindKernel("StageRestoreParticlesAuxiliary");
            shader.SetBuffer(auxiliary, "_RestoreReservedIndices", r.RestoreReservedIndices);
            shader.SetBuffer(auxiliary, "_TransferStatus", r.TransferStatus);
            shader.SetBuffer(auxiliary, "_DensityLambda", r.DensityLambda);
            shader.SetBuffer(auxiliary, "_DeltaPositions", r.DeltaPositions);
            shader.SetBuffer(auxiliary, "_StableTickCounters", r.StableTickCounters);
            shader.SetBuffer(auxiliary, "_WakeRequests", r.WakeRequests);
            shader.Dispatch(auxiliary, 1, 1, 1);
        }

        private static void Activate(ComputeShader shader, FluidGpuResourceSet r, int count, int transaction)
        {
            int k = shader.FindKernel("ActivateRestoredParticles");
            shader.SetInt("_ParticleCapacity", r.ParticleCapacity);
            shader.SetInt("_TransferTransactionId", transaction);
            shader.SetVector("_ActiveBoundsMin", Vector3.one * -1f);
            shader.SetVector("_ActiveBoundsMax", Vector3.one);
            shader.SetBuffer(k, "_RestoreReservedIndices", r.RestoreReservedIndices);
            shader.SetBuffer(k, "_TransferStatus", r.TransferStatus);
            shader.SetBuffer(k, "_Positions", r.Positions);
            shader.SetBuffer(k, "_PredictedPositions", r.PredictedPositions);
            shader.SetBuffer(k, "_ParticleMetadata", r.Metadata);
            shader.SetBuffer(k, "_Counters", r.Counters);
            shader.Dispatch(k, 1, 1, 1);
        }

        private static void Rollback(ComputeShader shader, FluidGpuResourceSet r, int count, int transaction)
        {
            int k = shader.FindKernel("RollbackRestoredParticles");
            shader.SetInt("_ParticleCapacity", r.ParticleCapacity);
            shader.SetInt("_TransferTransactionId", transaction);
            shader.SetBuffer(k, "_RestoreReservedIndices", r.RestoreReservedIndices);
            shader.SetBuffer(k, "_TransferStatus", r.TransferStatus);
            shader.SetBuffer(k, "_ParticleMetadata", r.Metadata);
            shader.SetBuffer(k, "_Positions", r.Positions);
            shader.SetBuffer(k, "_PredictedPositions", r.PredictedPositions);
            shader.SetBuffer(k, "_Velocities", r.Velocities);
            shader.SetBuffer(k, "_FreeIndices", r.FreeIndices);
            shader.SetBuffer(k, "_Counters", r.Counters);
            shader.Dispatch(k, 1, 1, 1);
        }

        private static FluidGpuTransferStatus ReadStatus(FluidGpuResourceSet r)
        {
            var values = new FluidGpuTransferStatus[1];
            r.TransferStatus.GetData(values);
            return values[0];
        }

        private static uint[] ReadCounters(FluidGpuResourceSet r)
        {
            var values = new uint[FluidGpuLayout.CounterCount];
            r.Counters.GetData(values);
            return values;
        }

        private static void RequireCompute()
        {
            if (!SystemInfo.supportsComputeShaders || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("Requires a real graphics device with ComputeShader support.");
        }
    }
}
