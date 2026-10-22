using Game.ElementField;
using Game.Materials;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.Rendering.Tests
{
    public sealed class GpuMultiMaterialLiquidSurfaceTests
    {
        [TestCase(MaterialSimulationBackendKind.GpuPbfLiquid, true)]
        [TestCase(MaterialSimulationBackendKind.ElementCell, false)]
        [TestCase(MaterialSimulationBackendKind.Unsupported, false)]
        public void AvailabilityUsesFinalMaterialRoute(
            MaterialSimulationBackendKind backend,
            bool expected)
        {
            Assert.That(
                LiquidBackendAvailabilityPolicy.ShouldRender(
                    MaterialId.Poison,
                    new FakeRoutes(backend)),
                Is.EqualTo(expected));
        }

        [Test]
        public void EmptyMaterialNeverRenders()
        {
            Assert.That(
                LiquidBackendAvailabilityPolicy.ShouldRender(
                    MaterialId.Empty,
                    new FakeRoutes(MaterialSimulationBackendKind.GpuPbfLiquid)),
                Is.False);
        }

        [Test]
        public void P7HasAllLiquidRenderersSharingOneGpuSource()
        {
            const string scenePath = "Assets/_Project/Scenes/P7_DemoRun.unity";
            SceneSetup[] previousSetup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                GpuLiquidSurfaceRenderer[] renderers = Object.FindObjectsByType<GpuLiquidSurfaceRenderer>(
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None);
                Assert.That(renderers, Has.Length.EqualTo(3),
                    "P7 应为 Water/Poison/Sticky 各保留一个表面 Renderer，而不是复制 PBF Runtime。");

                Object sharedSource = null;
                bool hasWater = false;
                bool hasPoison = false;
                bool hasSticky = false;
                for (int i = 0; i < renderers.Length; i++)
                {
                    var serialized = new SerializedObject(renderers[i]);
                    MaterialId target = (MaterialId)serialized.FindProperty("_targetMaterial").intValue;
                    Object source = serialized.FindProperty("_fluidSourceComponent").objectReferenceValue;
                    sharedSource ??= source;
                    Assert.That(source, Is.SameAs(sharedSource));
                    hasWater |= target == MaterialId.Water;
                    hasPoison |= target == MaterialId.Poison;
                    hasSticky |= target == MaterialId.Sticky;
                }

                Assert.That(hasWater, Is.True);
                Assert.That(hasPoison, Is.True);
                Assert.That(hasSticky, Is.True);
            }
            finally
            {
                if (previousSetup.Length > 0)
                    EditorSceneManager.RestoreSceneManagerSetup(previousSetup);
                else
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        [Test]
        public void SurfaceRefreshBuildsImmediatelyThenOnlyForANewSimulationVersion()
        {
            uint waterOnly = 1u << (int)MaterialId.Water;
            Assert.That(LiquidSurfaceRefreshPlanner.ShouldRebuild(false, 0u, 0u,
                MaterialId.Water, waterOnly, 10), Is.True);
            Assert.That(LiquidSurfaceRefreshPlanner.ShouldRebuild(true, 7u, 7u,
                MaterialId.Water, waterOnly, 11), Is.False);
            Assert.That(LiquidSurfaceRefreshPlanner.ShouldRebuild(true, 7u, 8u,
                MaterialId.Water, waterOnly, 11), Is.True);
        }

        [Test]
        public void ThreePresentMaterialsReceiveDifferentStableFrameRanks()
        {
            uint all = (1u << (int)MaterialId.Water)
                | (1u << (int)MaterialId.Poison)
                | (1u << (int)MaterialId.Sticky);
            Assert.That(LiquidSurfaceRefreshPlanner.ShouldRebuild(true, 1u, 2u,
                MaterialId.Water, all, 30), Is.True);
            Assert.That(LiquidSurfaceRefreshPlanner.ShouldRebuild(true, 1u, 2u,
                MaterialId.Poison, all, 30), Is.False);
            Assert.That(LiquidSurfaceRefreshPlanner.ShouldRebuild(true, 1u, 2u,
                MaterialId.Poison, all, 31), Is.True);
            Assert.That(LiquidSurfaceRefreshPlanner.ShouldRebuild(true, 1u, 2u,
                MaterialId.Sticky, all, 32), Is.True);
        }

        [Test]
        public void AbsentMaterialNeverConsumesARefreshSlot()
        {
            uint waterOnly = 1u << (int)MaterialId.Water;
            Assert.That(LiquidSurfaceRefreshPlanner.ShouldRebuild(false, 0u, 1u,
                MaterialId.Poison, waterOnly, 0), Is.False);
        }

        private sealed class FakeRoutes : IMaterialSimulationRouteReadOnly
        {
            private readonly MaterialSimulationBackendKind _backend;
            public FakeRoutes(MaterialSimulationBackendKind backend) => _backend = backend;
            public bool TryResolve(MaterialId material, out MaterialSimulationBackendKind backend)
            {
                backend = _backend;
                return material != MaterialId.Empty;
            }
        }
    }
}
