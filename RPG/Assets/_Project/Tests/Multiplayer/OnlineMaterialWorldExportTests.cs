using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Core;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Multiplayer.Tests
{
    /// <summary>真实原图Preview验证材料导出边界；不保存资产、不启动网络，也不把只读装配当可见双Client验收。</summary>
    public sealed class OnlineMaterialWorldExportTests
    {
        private GameObject _root;
        private Type _tools;
        private GameObject Preview(string id)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            { _tools = assembly.GetType("Game.Project.Editor.OnlineDungeonTools"); if (_tools != null) break; }
            Assert.That(_tools, Is.Not.Null);
            _root = (GameObject)_tools.GetMethod("BuildPreview").Invoke(null, new object[] { id }); return _root;
        }
        [TearDown] public void Cleanup()
        { if (_root != null) _tools.GetMethod("ReleasePreview").Invoke(null, new object[] { _root }); }
        private static Component Find(GameObject root, string type)
        {
            foreach (var component in root.GetComponentsInChildren<Component>(true))
                if (component != null && component.GetType().FullName == type) return component;
            return null;
        }
        [TestCase("p7")][TestCase("p8")]
        public void ExportKeepsMaterialRootOriginalRenderingAndSeparateCpuProfiles(string id)
        {
            var root = Preview(id);
            var world = Find(root, "Game.ElementField.ElementWorldRuntime");
            Assert.That(world, Is.Not.Null, "不能继续删掉正式场景的材料世界。");
            Assert.That(Find(root, "Game.ElementField.OnlineMaterialWorldBinding"), Is.Not.Null);
            Assert.That(Find(root, "Game.Rendering.GridLiquidFieldSurfaceRenderer"), Is.Not.Null);
            var data = new SerializedObject(world);
            var profile = data.FindProperty("_profile").objectReferenceValue;
            Assert.That(profile, Is.Not.Null);
            var config = new SerializedObject(profile);
            Assert.That(config.FindProperty("_waterSimulationMode").enumValueIndex, Is.EqualTo(2));
            Assert.That(config.FindProperty("_enableFluidChunkStreaming").boolValue, Is.False);
            var route = config.FindProperty("_materialSimulationRouting").objectReferenceValue;
            Assert.That(route, Is.Not.Null);
            var routes = new SerializedObject(route).FindProperty("_routes");
            for (int i = 0; i < routes.arraySize; i++)
                Assert.That(routes.GetArrayElementAtIndex(i).FindPropertyRelative("_backend").intValue, Is.Not.EqualTo(2),
                    "任何非Water GPU Route也必须覆盖为CPU；不能只改水。");
            Assert.That(profile.name, Does.Contain("OnlineCPU"));
        }
        [TestCase("p7")][TestCase("p8")]
        public void ReplicaConfigurationRemovesGameplayOwnersBeforeAnyAwake(string id)
        {
            var root = Preview(id); var binding = Find(root, "Game.ElementField.OnlineMaterialWorldBinding");
            Assert.That(binding, Is.Not.Null);
            Assert.That(binding.GetType().GetMethod("TryConfigure").Invoke(binding,
                new object[] { WorldExecutionMode.Replica, 19UL }), Is.True);
            foreach (string forbidden in new[] { "ElementWorldExposureSystem", "MaterialInventoryTransferAdapter",
                "ContainerDepositAdapter", "GpuPbfFluidRuntime", "FluidGameplayOccupancyBridge", "FluidChunkStreamingRuntime",
                "LiquidWorldRuntime", "FixedSolidWorldRuntime", "GasWorldRuntime" })
                Assert.That(Find(root, "Game.ElementField." + forbidden), Is.Null, "禁用Behaviour仍会Awake，必须剥除 " + forbidden);
            Assert.That(root.activeSelf, Is.False);
            Assert.That(Find(root, "Game.Rendering.GridLiquidFieldSurfaceRenderer"), Is.Not.Null);
            Assert.That(binding.GetType().GetProperty("ReplicaInstaller").GetValue(binding), Is.Not.Null);
            binding.GetType().GetMethod("Detach").Invoke(binding, null);
            Assert.That(binding.GetType().GetProperty("IsReady").GetValue(binding), Is.False);
        }
    }
}
