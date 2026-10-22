using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// 验证 P7 把 GPU Readback 与 RAM Archive 的组合 Occupancy 显式交给 Exposure System；
    /// 否则归档恢复交接期间角色脚下的 Water Amount 会短暂消失。
    /// </summary>
    public sealed class P7GpuWaterGameplayIntegrationTests
    {
        private const string P7ScenePath = "Assets/_Project/Scenes/P7_DemoRun.unity";

        [Test]
        public void P7ExposureReferencesFluidStreamingCompositeOccupancy()
        {
            SceneSetup[] previousSetup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                EditorSceneManager.OpenScene(P7ScenePath, OpenSceneMode.Single);
                ElementWorldExposureSystem exposure = Object.FindFirstObjectByType<ElementWorldExposureSystem>(
                    FindObjectsInactive.Include);
                FluidGameplayOccupancyBridge bridge =
                    Object.FindFirstObjectByType<FluidGameplayOccupancyBridge>(FindObjectsInactive.Include);
                FluidChunkStreamingRuntime streaming =
                    Object.FindFirstObjectByType<FluidChunkStreamingRuntime>(FindObjectsInactive.Include);

                Assert.That(exposure, Is.Not.Null, "P7 缺少 ElementWorldExposureSystem。");
                Assert.That(bridge, Is.Not.Null, "P7 缺少 FluidGameplayOccupancyBridge。");
                Assert.That(streaming, Is.Not.Null, "P7 缺少 FluidChunkStreamingRuntime。");

                var serializedExposure = new SerializedObject(exposure);
                SerializedProperty occupancyProperty =
                    serializedExposure.FindProperty("_liquidOccupancyComponent");
                Assert.That(occupancyProperty, Is.Not.Null,
                    "Exposure System 缺少流体 Gameplay Occupancy 序列化入口。");
                Assert.That(occupancyProperty.objectReferenceValue, Is.SameAs(streaming),
                    "P7 Exposure 必须读取 Streaming 的 GPU+Archive 组合权威。");
            }
            finally
            {
                // Test Runner 可能在“没有任何已加载场景”的隔离环境启动；空 Setup 不能传回 Unity，
                // 否则既会遮住真正断言，也会把 P7 的 Runtime Component 留给后续测试造成污染。
                if (previousSetup.Length > 0)
                    EditorSceneManager.RestoreSceneManagerSetup(previousSetup);
                else
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }
    }
}
