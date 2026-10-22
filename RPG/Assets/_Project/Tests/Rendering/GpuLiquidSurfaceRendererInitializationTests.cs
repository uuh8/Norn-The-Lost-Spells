using NUnit.Framework;
using UnityEngine;

namespace Game.Rendering.Tests
{
    public sealed class GpuLiquidSurfaceRendererInitializationTests
    {
        [TestCase(false, false, 0u, 0u, LiquidSurfaceDiagnosticStage.WaitingForSnapshot)]
        [TestCase(true, false, 0u, 0u, LiquidSurfaceDiagnosticStage.WaitingForResources)]
        [TestCase(true, true, 0u, 0u, LiquidSurfaceDiagnosticStage.NoSurfaceTriangles)]
        [TestCase(true, true, 10u, 0u, LiquidSurfaceDiagnosticStage.IndirectArgsMissing)]
        [TestCase(true, true, 10u, 30u, LiquidSurfaceDiagnosticStage.DrawReady)]
        public void DiagnosticStageIdentifiesTheBrokenGpuPipelineBoundary(
            bool hasSnapshot,
            bool resourcesReady,
            uint triangleCount,
            uint indirectVertexCount,
            LiquidSurfaceDiagnosticStage expected)
        {
            Assert.That(
                LiquidSurfaceDiagnosticClassifier.Classify(
                    hasSnapshot,
                    resourcesReady,
                    triangleCount,
                    indirectVertexCount),
                Is.EqualTo(expected));
        }

        [Test]
        public void GeometryDiagnosticRejectsInvalidOutsideAndReversedTriangles()
        {
            var bounds = new Bounds(Vector3.zero, Vector3.one * 4f);
            Vector3 p0 = new Vector3(-0.5f, 0f, -0.5f);
            Vector3 p1 = new Vector3(0f, 0f, 0.5f);
            Vector3 p2 = new Vector3(0.5f, 0f, -0.5f);
            Vector3 up = Vector3.up;

            Assert.That(
                LiquidSurfaceGeometryDiagnosticClassifier.Classify(
                    false, in bounds, p0, p1, p2, up, up, up),
                Is.EqualTo(LiquidSurfaceGeometryDiagnosticStage.WaitingForTriangleSample));
            Assert.That(
                LiquidSurfaceGeometryDiagnosticClassifier.Classify(
                    true, in bounds, new Vector3(float.NaN, 0f, 0f), p1, p2, up, up, up),
                Is.EqualTo(LiquidSurfaceGeometryDiagnosticStage.InvalidTriangle));
            Assert.That(
                LiquidSurfaceGeometryDiagnosticClassifier.Classify(
                    true, in bounds, p0 + Vector3.right * 10f, p1, p2, up, up, up),
                Is.EqualTo(LiquidSurfaceGeometryDiagnosticStage.OutsideDrawBounds));
            Assert.That(
                LiquidSurfaceGeometryDiagnosticClassifier.Classify(
                    true, in bounds, p0, p2, p1, up, up, up),
                Is.EqualTo(LiquidSurfaceGeometryDiagnosticStage.WindingOpposesNormals));
            Assert.That(
                LiquidSurfaceGeometryDiagnosticClassifier.Classify(
                    true, in bounds, p0, p1, p2, up, up, up),
                Is.EqualTo(LiquidSurfaceGeometryDiagnosticStage.GeometryReady));
        }

        [Test]
        public void DefaultProfileSnapshotsStylizedCrownSettings()
        {
            LiquidRenderProfile profile = ScriptableObject.CreateInstance<LiquidRenderProfile>();
            try
            {
                LiquidRenderSettings settings = profile.CreateSettings();

                Assert.That(settings.UseStylizedCrown, Is.True);
                Assert.That(settings.CrownHeightRatio, Is.EqualTo(0.8f).Within(1e-6f));
                Assert.That(settings.CrownFalloff, Is.EqualTo(1.5f).Within(1e-6f));
                Assert.That(settings.CrownEdgeNeighborCount, Is.EqualTo(4));
                Assert.That(settings.CrownInteriorNeighborCount, Is.EqualTo(12));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void CrownSettingsRejectInvalidValuesButAllowZeroHeight()
        {
            Assert.DoesNotThrow(() => CreateSettings(crownHeightRatio: 0f));
            Assert.Throws<System.ArgumentOutOfRangeException>(() =>
                CreateSettings(crownHeightRatio: float.NaN));
            Assert.Throws<System.ArgumentOutOfRangeException>(() =>
                CreateSettings(crownHeightRatio: float.PositiveInfinity));
            Assert.Throws<System.ArgumentOutOfRangeException>(() =>
                CreateSettings(crownHeightRatio: -0.01f));
            Assert.Throws<System.ArgumentOutOfRangeException>(() =>
                CreateSettings(crownFalloff: 0f));
            Assert.Throws<System.ArgumentOutOfRangeException>(() =>
                CreateSettings(crownEdgeNeighborCount: 4, crownInteriorNeighborCount: 4));
        }

        [Test]
        public void InvalidProfileIsConvertedToFailureWithoutThrowing()
        {
            LiquidRenderProfile profile = ScriptableObject.CreateInstance<LiquidRenderProfile>();
            try
            {
                var serialized = new UnityEditor.SerializedObject(profile);
                // 0 会被 Profile.OnValidate 合法地 clamp 到 0.001；NaN 才能模拟损坏的序列化数据，
                // 并验证 Factory 确实把 Settings constructor 的异常转换成显式失败。
                serialized.FindProperty("_targetVoxelSize").floatValue = float.NaN;
                serialized.ApplyModifiedPropertiesWithoutUndo();

                Assert.DoesNotThrow(() => LiquidRenderSettingsFactory.TryCreate(
                    profile, out _, out _));
                Assert.That(LiquidRenderSettingsFactory.TryCreate(
                    profile, out _, out string error), Is.False);
                Assert.That(error, Is.Not.Empty);
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void NewRendererDefaultsToProductionFragmentPath()
        {
            GameObject gameObject = new GameObject("liquid-renderer-defaults");
            try
            {
                GpuLiquidSurfaceRenderer renderer = gameObject.AddComponent<GpuLiquidSurfaceRenderer>();
                var serialized = new UnityEditor.SerializedObject(renderer);

                Assert.That(
                    serialized.FindProperty("_debugForceOpaqueFragment").boolValue,
                    Is.False,
                    "新 Renderer 不得默认停留在只输出纯色的 Opaque Fragment 诊断模式。");
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        private static LiquidRenderSettings CreateSettings(
            float crownHeightRatio = 0.8f,
            float crownFalloff = 1.5f,
            int crownEdgeNeighborCount = 4,
            int crownInteriorNeighborCount = 12)
        {
            return new LiquidRenderSettings(
                0.1f,
                96,
                131072,
                0.3f,
                500f,
                true,
                2,
                5,
                0.65f,
                1.8f,
                2.5f,
                true,
                crownHeightRatio,
                crownFalloff,
                crownEdgeNeighborCount,
                crownInteriorNeighborCount);
        }
    }
}
