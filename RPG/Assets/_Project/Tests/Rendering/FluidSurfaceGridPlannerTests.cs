using System;
using System.Runtime.InteropServices;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Rendering.Tests
{
    /// <summary>
    /// Surface Grid 是 Density Field 与 Marching Cubes 共用的 CPU Contract。
    /// 这些测试先锁定 Bounds、Lattice Sample 与资源复用语义，不依赖 GPU 或 Scene。
    /// </summary>
    public sealed class FluidSurfaceGridPlannerTests
    {
        [Test]
        public void PlanPadsBoundsCeilsSampleResolutionAndDerivesActualVoxelSize()
        {
            var bounds = new Bounds(new Vector3(10f, 20f, 30f), new Vector3(4f, 2f, 6f));
            var profile = new LiquidRenderSettings(
                targetVoxelSize: 1.1f,
                maximumResolutionPerAxis: 64,
                maximumTriangleCount: 1024,
                boundsPadding: 0.5f,
                isoLevel: 1f);

            FluidSurfaceGridSettings grid = FluidSurfaceGridPlanner.Plan(bounds, in profile);

            Assert.That(grid.Resolution, Is.EqualTo(new Vector3Int(6, 4, 8)));
            Assert.That(grid.WorldOrigin, Is.EqualTo(new Vector3(8f, 19f, 27f)));
            Assert.That(grid.VoxelSize.x, Is.EqualTo(1f).Within(1e-6f));
            Assert.That(grid.VoxelSize.y, Is.EqualTo(1f).Within(1e-6f));
            Assert.That(grid.VoxelSize.z, Is.EqualTo(1f).Within(1e-6f));
            Assert.That(grid.WorldBounds.size, Is.EqualTo(new Vector3(5f, 3f, 7f)));
            Assert.That(grid.SampleCount, Is.EqualTo(192L));
            Assert.That(grid.CellCount, Is.EqualTo(105L));
        }

        [Test]
        public void PlanClampsEachAxisAndStillSpansTheWholePaddedBounds()
        {
            var bounds = new Bounds(Vector3.zero, new Vector3(10f, 4f, 2f));
            var profile = new LiquidRenderSettings(1f, 4, 128, 0f, 1f);

            FluidSurfaceGridSettings grid = FluidSurfaceGridPlanner.Plan(bounds, in profile);

            Assert.That(grid.Resolution, Is.EqualTo(new Vector3Int(4, 4, 3)));
            Assert.That(grid.VoxelSize.x, Is.EqualTo(10f / 3f).Within(1e-6f));
            Assert.That(grid.VoxelSize.y, Is.EqualTo(4f / 3f).Within(1e-6f));
            Assert.That(grid.VoxelSize.z, Is.EqualTo(1f).Within(1e-6f));
            Assert.That(grid.WorldOrigin + Vector3.Scale(grid.VoxelSize, grid.Resolution - Vector3Int.one),
                Is.EqualTo(bounds.max));
        }

        [Test]
        public void VeryCoarseTargetStillProducesAtLeastTwoSamplesPerAxis()
        {
            var profile = new LiquidRenderSettings(100f, 8, 32, 0f, 1f);

            FluidSurfaceGridSettings grid = FluidSurfaceGridPlanner.Plan(
                new Bounds(Vector3.zero, Vector3.one),
                in profile);

            Assert.That(grid.Resolution, Is.EqualTo(new Vector3Int(2, 2, 2)));
            Assert.That(grid.CellCount, Is.EqualTo(1L));
        }

        [Test]
        public void InvalidOrOverflowingGridInputsAreRejectedAtThePureBoundary()
        {
            var valid = new LiquidRenderSettings(0.25f, 128, 1024, 0.1f, 1f);

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                FluidSurfaceGridPlanner.Plan(new Bounds(Vector3.zero, Vector3.zero), in valid));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new LiquidRenderSettings(float.NaN, 128, 1024, 0.1f, 1f));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new LiquidRenderSettings(0.25f, 1, 1024, 0.1f, 1f));
            Assert.Throws<OverflowException>(() =>
                new FluidSurfaceGridSettings(
                    new Vector3Int(int.MaxValue, int.MaxValue, int.MaxValue),
                    Vector3.zero,
                    Vector3.one,
                    1));
        }

        [Test]
        public void TranslationOnlyChangesOriginAndKeepsGpuAllocationCompatible()
        {
            var profile = new LiquidRenderSettings(0.5f, 64, 2048, 0.25f, 1f);
            FluidSurfaceGridSettings first = FluidSurfaceGridPlanner.Plan(
                new Bounds(Vector3.zero, new Vector3(4f, 2f, 3f)),
                in profile);
            FluidSurfaceGridSettings translated = FluidSurfaceGridPlanner.Plan(
                new Bounds(new Vector3(17f, -3f, 9f), new Vector3(4f, 2f, 3f)),
                in profile);

            Assert.That(translated.WorldOrigin, Is.Not.EqualTo(first.WorldOrigin));
            Assert.That(first.IsResourceCompatibleWith(in translated), Is.True);

            var changedProfile = new LiquidRenderSettings(0.25f, 64, 2048, 0.25f, 1f);
            FluidSurfaceGridSettings changed = FluidSurfaceGridPlanner.Plan(
                new Bounds(Vector3.zero, new Vector3(4f, 2f, 3f)),
                in changedProfile);
            Assert.That(first.IsResourceCompatibleWith(in changed), Is.False);
        }

        [Test]
        public void SubVoxelBoundsMotionKeepsTheDensityLatticeWorldAnchored()
        {
            var profile = new LiquidRenderSettings(0.5f, 64, 2048, 0.5f, 1f);
            var stationaryWindow = new Bounds(Vector3.zero, new Vector3(4f, 2f, 3f));
            var slightlyMovedWindow = new Bounds(
                new Vector3(0.1f, 0f, 0.1f),
                stationaryWindow.size);

            FluidSurfaceGridSettings first = FluidSurfaceGridPlanner.Plan(stationaryWindow, in profile);
            FluidSurfaceGridSettings moved = FluidSurfaceGridPlanner.Plan(slightlyMovedWindow, in profile);

            Assert.That(moved.WorldOrigin, Is.EqualTo(first.WorldOrigin));
            Assert.That(ContainsBounds(moved.WorldBounds, slightlyMovedWindow), Is.True,
                "量化后的 Density Grid 仍必须完整包住实际模拟窗口，不能用稳定性换裁切。");
        }

        [Test]
        public void LargerBoundsMotionMovesOriginByWholeVoxelStepsAndStillCoversInterestBounds()
        {
            var profile = new LiquidRenderSettings(0.5f, 64, 2048, 0.5f, 1f);
            var firstWindow = new Bounds(Vector3.zero, new Vector3(4f, 2f, 3f));
            var movedWindow = new Bounds(new Vector3(0.4f, 0f, 0f), firstWindow.size);

            FluidSurfaceGridSettings first = FluidSurfaceGridPlanner.Plan(firstWindow, in profile);
            FluidSurfaceGridSettings moved = FluidSurfaceGridPlanner.Plan(movedWindow, in profile);

            float originDeltaInVoxels =
                (moved.WorldOrigin.x - first.WorldOrigin.x) / moved.VoxelSize.x;
            Assert.That(originDeltaInVoxels, Is.EqualTo(Mathf.Round(originDeltaInVoxels)).Within(1e-5f));
            Assert.That(ContainsBounds(moved.WorldBounds, movedWindow), Is.True);
        }

        [Test]
        public void SingleSurfaceGridCoversTheWholeRequestedBounds()
        {
            var requestedBounds = new Bounds(
                new Vector3(4f, 2f, -3f),
                new Vector3(18f, 6f, 22f));
            var settings = new LiquidRenderSettings(
                0.1f, 96, 131072, 0.3f, 500f,
                useStylizedCrown: true);

            FluidSurfaceGridSettings grid = FluidSurfaceGridPlanner.Plan(
                requestedBounds, in settings);

            Assert.That(ContainsBounds(grid.WorldBounds, requestedBounds), Is.True,
                "单 Surface 路径必须重建调用者请求的完整 Bounds，不能再裁成角色附近的 Near Box。");
        }

        [Test]
        public void ProfileCreatesAnImmutableInitializationSnapshot()
        {
            LiquidRenderProfile profile = ScriptableObject.CreateInstance<LiquidRenderProfile>();
            try
            {
                LiquidRenderSettings before = profile.CreateSettings();
                var serialized = new SerializedObject(profile);
                serialized.FindProperty("_targetVoxelSize").floatValue = before.TargetVoxelSize * 2f;
                serialized.ApplyModifiedPropertiesWithoutUndo();

                LiquidRenderSettings after = profile.CreateSettings();

                Assert.That(before.TargetVoxelSize, Is.Not.EqualTo(after.TargetVoxelSize));
                Assert.That(before.TargetVoxelSize, Is.EqualTo(0.1f).Within(1e-6f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void FireParcelLifecycleBlendsSurfaceIntoGasAndFadesAtExpiry()
        {
            var settings = new FireParcelPhaseSettings(0.8f, 0.2f, 1.2f, 1f, 2f);
            FireParcelPhaseSample young = FireParcelPhaseMath.Evaluate(0f, in settings);
            FireParcelPhaseSample middle = FireParcelPhaseMath.Evaluate(0.9f, in settings);
            FireParcelPhaseSample expired = FireParcelPhaseMath.Evaluate(
                settings.TotalLifetimeSeconds, in settings);

            Assert.That(young.SurfaceWeight, Is.EqualTo(1f));
            Assert.That(young.GasWeight, Is.EqualTo(0f));
            Assert.That(middle.SurfaceWeight, Is.GreaterThan(0f));
            Assert.That(middle.GasWeight, Is.GreaterThan(0f));
            Assert.That(expired.SurfaceWeight, Is.EqualTo(0f));
            Assert.That(expired.GasWeight, Is.EqualTo(0f));
        }

        [Test]
        public void FireParcelGpuRecordsAndRingCursorKeepTheirContracts()
        {
            Assert.That(Marshal.SizeOf<FireParcelSpawnGpu>(), Is.EqualTo(32));
            Assert.That(Marshal.SizeOf<FireParcelStateGpu>(), Is.EqualTo(64));
            Assert.That(FireParcelPoolMath.ResolveRingSlot(5u, 4), Is.EqualTo(1));
        }

        private static bool ContainsBounds(Bounds container, Bounds content)
        {
            const float Epsilon = 1e-5f;
            return container.min.x <= content.min.x + Epsilon
                && container.min.y <= content.min.y + Epsilon
                && container.min.z <= content.min.z + Epsilon
                && container.max.x + Epsilon >= content.max.x
                && container.max.y + Epsilon >= content.max.y
                && container.max.z + Epsilon >= content.max.z;
        }
    }
}
