using System.Collections.Generic;
using Game.ElementField;
using Game.Materials;
using Game.Rendering;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.Rendering.Tests
{
    /// <summary>
    /// 锁定 P8 与当前生产版 ElementWorld 的场景接线。
    /// 物质规则可以跨场景共享，但 GPU Runtime、Readback Bridge 和 Renderer 必须由每个场景重新创建，
    /// 否则切场景后会留下“法术命中但没有流体、反应与暴露”的半初始化状态。
    /// </summary>
    public sealed class P8ElementWorldSceneContractTests
    {
        private const string ScenePath = "Assets/_Project/Scenes/P8_BossField.unity";
        private const string P7ScenePath = "Assets/_Project/Scenes/P7_DemoRun.unity";
        private const string P8ProfilePath =
            "Assets/_Project/ScriptableObjects/P8/ElementField/ElementWorldProfile_P8BossField.asset";
        private const string CanonicalProfilePath =
            "Assets/_Project/ScriptableObjects/ElementField/Fluid/ElementWorldProfile_P7_PBF.asset";

        [TestCase(P7ScenePath, CanonicalProfilePath)]
        [TestCase(ScenePath, P8ProfilePath)]
        public void ProductionSceneOwnsStreamingRuntimeAndUsesValidatedSettings(
            string scenePath, string profilePath)
        {
            SceneSetup[] previousSetup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                ElementWorldRuntime world = Object.FindFirstObjectByType<ElementWorldRuntime>(
                    FindObjectsInactive.Include);
                ElementWorldExposureSystem exposure =
                    Object.FindFirstObjectByType<ElementWorldExposureSystem>(FindObjectsInactive.Include);
                GpuPbfFluidRuntime fluid = Object.FindFirstObjectByType<GpuPbfFluidRuntime>(
                    FindObjectsInactive.Include);
                FluidGameplayOccupancyBridge bridge =
                    Object.FindFirstObjectByType<FluidGameplayOccupancyBridge>(FindObjectsInactive.Include);
                FluidChunkStreamingRuntime streaming =
                    Object.FindFirstObjectByType<FluidChunkStreamingRuntime>(FindObjectsInactive.Include);

                Assert.That(world, Is.Not.Null, $"{scenePath} 缺少 ElementWorldRuntime。");
                Assert.That(exposure, Is.Not.Null, $"{scenePath} 缺少 ElementWorldExposureSystem。");
                Assert.That(fluid, Is.Not.Null, $"{scenePath} 缺少 GpuPbfFluidRuntime。");
                Assert.That(bridge, Is.Not.Null, $"{scenePath} 缺少 FluidGameplayOccupancyBridge。");
                Assert.That(streaming, Is.Not.Null, $"{scenePath} 缺少 FluidChunkStreamingRuntime。");
                Assert.That(streaming.gameObject, Is.SameAs(world.gameObject),
                    "Streaming Runtime 必须与 ElementWorldRuntime 同 Root，生命周期才能同步销毁。");

                var worldData = new SerializedObject(world);
                Assert.That(worldData.FindProperty("_fluidChunkStreamingRuntime").objectReferenceValue,
                    Is.SameAs(streaming));
                var exposureData = new SerializedObject(exposure);
                Assert.That(exposureData.FindProperty("_liquidOccupancyComponent").objectReferenceValue,
                    Is.SameAs(streaming), "Gameplay Exposure 必须读取 GPU+Archive 的统一权威视图。");

                ElementWorldProfile profile = AssetDatabase.LoadAssetAtPath<ElementWorldProfile>(profilePath);
                Assert.That(profile, Is.Not.Null);
                AssertStreamingSettings(profile);
                Assert.That(worldData.FindProperty("_profile").objectReferenceValue, Is.SameAs(profile));
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
        public void P8UsesCanonicalMaterialRulesAndOwnsACompleteGpuRuntimeChain()
        {
            SceneSetup[] previousSetup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

                ElementWorldRuntime world = Object.FindFirstObjectByType<ElementWorldRuntime>(
                    FindObjectsInactive.Include);
                ElementWorldExposureSystem exposure =
                    Object.FindFirstObjectByType<ElementWorldExposureSystem>(FindObjectsInactive.Include);
                GpuPbfFluidRuntime fluid = Object.FindFirstObjectByType<GpuPbfFluidRuntime>(
                    FindObjectsInactive.Include);
                FluidGameplayOccupancyBridge bridge =
                    Object.FindFirstObjectByType<FluidGameplayOccupancyBridge>(FindObjectsInactive.Include);

                Assert.That(world, Is.Not.Null, "P8 缺少 ElementWorldRuntime。");
                Assert.That(exposure, Is.Not.Null, "P8 缺少 ElementWorldExposureSystem。");
                Assert.That(fluid, Is.Not.Null, "P8 缺少 GpuPbfFluidRuntime。");
                Assert.That(bridge, Is.Not.Null, "P8 缺少 FluidGameplayOccupancyBridge。");

                AssertCanonicalRules(world);
                AssertRuntimeReferences(world, exposure, fluid, bridge);
                AssertLiquidRenderers(world, fluid);
                Assert.That(
                    Object.FindFirstObjectByType<GpuFireParcelRenderer>(FindObjectsInactive.Include),
                    Is.Not.Null,
                    "P8 缺少当前生产版 Fire Parcel Renderer。");
                Assert.That(
                    Object.FindFirstObjectByType<ReactionBurstVisualController>(FindObjectsInactive.Include),
                    Is.Not.Null,
                    "P8 缺少元素反应的事件型表现消费者。");
                AssertTerrainProxyGrid(fluid);
            }
            finally
            {
                if (previousSetup.Length > 0)
                    EditorSceneManager.RestoreSceneManagerSetup(previousSetup);
                else
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        private static void AssertCanonicalRules(ElementWorldRuntime world)
        {
            ElementWorldProfile p8 = AssetDatabase.LoadAssetAtPath<ElementWorldProfile>(P8ProfilePath);
            ElementWorldProfile canonical =
                AssetDatabase.LoadAssetAtPath<ElementWorldProfile>(CanonicalProfilePath);
            Assert.That(p8, Is.Not.Null);
            Assert.That(canonical, Is.Not.Null);

            var p8Data = new SerializedObject(p8);
            var canonicalData = new SerializedObject(canonical);
            Assert.That(p8Data.FindProperty("_waterSimulationMode").intValue, Is.EqualTo(1),
                "P8 必须把 Water/Poison/Sticky 路由到 GPU PBF，而不是旧 Cell Water。");

            string[] sharedRuleFields =
            {
                "_materialCatalog",
                "_materialSimulationRouting",
                "_materialStatusProjection",
                "_reactionProfile",
                "_materialReactionBindings",
            };
            for (int i = 0; i < sharedRuleFields.Length; i++)
            {
                string field = sharedRuleFields[i];
                Assert.That(
                    p8Data.FindProperty(field).objectReferenceValue,
                    Is.SameAs(canonicalData.FindProperty(field).objectReferenceValue),
                    $"P8 的 {field} 必须引用当前生产版共享规则，不能维护场景私有副本。");
            }

            var worldData = new SerializedObject(world);
            Assert.That(worldData.FindProperty("_profile").objectReferenceValue, Is.SameAs(p8));
        }

        private static void AssertStreamingSettings(ElementWorldProfile profile)
        {
            var data = new SerializedObject(profile);
            Assert.That(data.FindProperty("_fluidWarmPaddingChunks").intValue, Is.EqualTo(1));
            Assert.That(data.FindProperty("_fluidArchiveGraceSeconds").floatValue, Is.EqualTo(2f));
            Assert.That(data.FindProperty("_maximumArchivedFluidChunks").intValue, Is.EqualTo(512));
            Assert.That(data.FindProperty("_maximumArchivedFluidCellRecords").intValue,
                Is.EqualTo(65536));
            Assert.That(data.FindProperty("_maximumPendingFluidWrites").intValue, Is.EqualTo(256));
            Assert.That(data.FindProperty("_gameplaySpawnReserveParticles").intValue, Is.EqualTo(512));
            Assert.That(data.FindProperty("_enableLocalFluidDormancy").boolValue, Is.True);
            Assert.That(data.FindProperty("_maximumRestoreParticlesPerFrame").intValue,
                Is.EqualTo(128));
        }

        private static void AssertRuntimeReferences(
            ElementWorldRuntime world,
            ElementWorldExposureSystem exposure,
            GpuPbfFluidRuntime fluid,
            FluidGameplayOccupancyBridge bridge)
        {
            var worldData = new SerializedObject(world);
            Transform interestPoint = worldData.FindProperty("_interestPoint").objectReferenceValue as Transform;
            Assert.That(interestPoint, Is.Not.Null, "P8 ElementWorld 缺少玩家 Interest Point。");
            Assert.That(worldData.FindProperty("_gpuPbfFluidRuntime").objectReferenceValue, Is.SameAs(fluid));
            Assert.That(worldData.FindProperty("_fluidGameplayOccupancy").objectReferenceValue, Is.SameAs(bridge));

            var bridgeData = new SerializedObject(bridge);
            Assert.That(bridgeData.FindProperty("_fluidSourceComponent").objectReferenceValue,
                Is.SameAs(fluid));
            Assert.That(bridgeData.FindProperty("_elementWorldRuntime").objectReferenceValue,
                Is.SameAs(world));

            var exposureData = new SerializedObject(exposure);
            FluidChunkStreamingRuntime streaming = world.GetComponent<FluidChunkStreamingRuntime>();
            Assert.That(streaming, Is.Not.Null);
            Assert.That(worldData.FindProperty("_fluidChunkStreamingRuntime").objectReferenceValue,
                Is.SameAs(streaming));
            Assert.That(exposureData.FindProperty("_liquidOccupancyComponent").objectReferenceValue,
                Is.SameAs(streaming));

            var fluidData = new SerializedObject(fluid);
            Assert.That(fluidData.FindProperty("_simulationBoundsCenter").objectReferenceValue,
                Is.SameAs(interestPoint));
        }

        private static void AssertLiquidRenderers(ElementWorldRuntime world, GpuPbfFluidRuntime fluid)
        {
            GpuLiquidSurfaceRenderer[] renderers = Object.FindObjectsByType<GpuLiquidSurfaceRenderer>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            Assert.That(renderers, Has.Length.EqualTo(3),
                "P8 应由同一个 GPU Source 分别重建 Water、Poison、Sticky 表面。");

            var targets = new HashSet<MaterialId>();
            for (int i = 0; i < renderers.Length; i++)
            {
                var data = new SerializedObject(renderers[i]);
                Assert.That(data.FindProperty("_fluidSourceComponent").objectReferenceValue,
                    Is.SameAs(fluid));
                Assert.That(data.FindProperty("_worldRuntime").objectReferenceValue,
                    Is.SameAs(world));
                targets.Add((MaterialId)data.FindProperty("_targetMaterial").intValue);
            }

            CollectionAssert.AreEquivalent(
                new[] { MaterialId.Water, MaterialId.Poison, MaterialId.Sticky },
                targets);

            DormantLiquidCellRenderer[] dormant =
                Object.FindObjectsByType<DormantLiquidCellRenderer>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
            Assert.That(dormant, Has.Length.EqualTo(3),
                "Water、Poison、Sticky 都需要 Dormant Cell 低成本表现。");
            targets.Clear();
            for (int i = 0; i < dormant.Length; i++)
            {
                var data = new SerializedObject(dormant[i]);
                Assert.That(dormant[i].gameObject, Is.SameAs(fluid.gameObject));
                Assert.That(data.FindProperty("_worldRuntime").objectReferenceValue,
                    Is.SameAs(world));
                Assert.That(data.FindProperty("_sourceLiquidMaterial").objectReferenceValue,
                    Is.Not.Null);
                Assert.That(data.FindProperty("_dormantShader").objectReferenceValue,
                    Is.Not.Null, "序列化 Shader 引用用于防止 Player Build stripping。");
                targets.Add((MaterialId)data.FindProperty("_targetMaterial").intValue);
            }
            CollectionAssert.AreEquivalent(
                new[] { MaterialId.Water, MaterialId.Poison, MaterialId.Sticky }, targets);
        }

        private static void AssertTerrainProxyGrid(GpuPbfFluidRuntime fluid)
        {
            var fluidData = new SerializedObject(fluid);
            Transform proxyRoot = fluidData.FindProperty("_fluidColliderRoot").objectReferenceValue as Transform;
            Assert.That(proxyRoot, Is.Not.Null, "P8 GPU 流体缺少显式 Collider Root。");
            Assert.That(proxyRoot.name, Is.EqualTo("FluidTerrainCollision"));

            FluidColliderAuthoring[] authorings =
                proxyRoot.GetComponentsInChildren<FluidColliderAuthoring>(true);
            Assert.That(authorings, Has.Length.EqualTo(121),
                "11×11 地形代理必须完整，且不能超过 LiquidSimulationProfile 的 128 Collider 上限。");
            for (int i = 0; i < authorings.Length; i++)
            {
                BoxCollider box = authorings[i].GetComponent<BoxCollider>();
                Assert.That(box, Is.Not.Null);
                Assert.That(box.isTrigger, Is.False);
                Assert.That(box.excludeLayers.value, Is.EqualTo(~0),
                    "地形代理只供 GPU 流体读取，不能进入角色/投射物的 CPU Physics。");
            }
        }
    }
}
