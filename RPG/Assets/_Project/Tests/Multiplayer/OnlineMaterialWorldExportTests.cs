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
        private GameObject _bootstrapRoot;
        private OnlineWorldBootstrap _bootstrap;
        private GameObject Preview(string id)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            { _tools = assembly.GetType("Game.Project.Editor.OnlineDungeonTools"); if (_tools != null) break; }
            Assert.That(_tools, Is.Not.Null);
            _root = (GameObject)_tools.GetMethod("BuildPreview").Invoke(null, new object[] { id }); return _root;
        }
        [TearDown] public void Cleanup()
        {
            if (_bootstrap != null) _bootstrap.StopWorld();
            if (_bootstrapRoot != null) UnityEngine.Object.DestroyImmediate(_bootstrapRoot);
            if (_root != null) _tools.GetMethod("ReleasePreview").Invoke(null, new object[] { _root });
        }
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
            Assert.That(Find(root, "Game.Rendering.ReactionBurstVisualController"), Is.Not.Null,
                "原地图的毒爆Burst消费者必须随正式世界导出。");
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

        // 只压缩Preview已克隆的CPU Profile，保留正式Catalog/Route/几何；不保存任何Asset。
        // 真实TryInitialize而不是反射改IsInitialized，让装配遗漏在初始化边界直接失败。
        private static Component InitializeAuthority(GameObject root, IOnlineMaterialWorldBinding binding)
        {
            JsonUtility.FromJsonOverwrite("{\"_captureCells\":128,\"_capturePending\":32,\"_inspectionBudget\":1000000}", binding);
            Assert.That(binding.TryConfigure(WorldExecutionMode.Authority, 19), Is.True);
            var world = Find(root, "Game.ElementField.ElementWorldRuntime");
            var profile = new SerializedObject(world).FindProperty("_profile").objectReferenceValue;
            Assert.That(profile.name, Does.Contain("OnlineCPU"));
            JsonUtility.FromJsonOverwrite("{\"_maximumResidentChunks\":16,\"_maxPendingWrites\":16}", profile);
            foreach (var owner in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (owner == null) continue;
                string type = owner.GetType().FullName;
                if (type != "Game.ElementField.LiquidWorldRuntime" && type != "Game.ElementField.FixedSolidWorldRuntime"
                    && type != "Game.ElementField.GasWorldRuntime") continue;
                var ownerProfile = new SerializedObject(owner).FindProperty("_profile").objectReferenceValue;
                Assert.That(ownerProfile.name, Does.Contain("OnlineCPU"));
                if (type == "Game.ElementField.LiquidWorldRuntime")
                    JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":8,\"_maximumActiveCells\":128,\"_maximumPendingDeposits\":16,\"_maximumCellsPerTick\":64,\"_maximumStableRegions\":4,\"_maximumStableOutlets\":4,\"_maximumStableRegionCells\":256,\"_maximumStableSurfacePatches\":16,\"_maximumImmediateExpansionCells\":64,\"_maximumImmediateExpansionSurfaceChunks\":2,\"_maximumAutoCandidateCells\":64,\"_autoValidationCellsPerTick\":32}", ownerProfile);
                else if (type == "Game.ElementField.FixedSolidWorldRuntime")
                    JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":8,\"_maximumPendingDeposits\":16,\"_maximumNavigationBoxesPerChunk\":4}", ownerProfile);
                else JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":8,\"_maximumActiveCells\":128,\"_maximumCellsPerTick\":32,\"_maximumPendingDeposits\":16,\"_maximumTerrainSamplesPerTick\":16,\"_maximumBirthScansPerTick\":128}", ownerProfile);
            }
            var baker = Find(root, "Game.ElementField.ElementFieldSolidBaker");
            var bakerData = new SerializedObject(baker);
            bakerData.FindProperty("_solidLayerMask").intValue = 0;
            bakerData.FindProperty("_includeDefaultEnvironment").boolValue = false;
            bakerData.ApplyModifiedPropertiesWithoutUndo();
            Assert.That((bool)world.GetType().GetMethod("TryInitialize", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(world, null), Is.True);
            return world;
        }

        private static long Tick(Component world) => (long)world.GetType().GetProperty("WorldTick").GetValue(world);

        [Test] public void RealAuthorityBindingInitializesWithoutAClientCameraAndOwnsTheOnlyMaterialClock()
        {
            var root = Preview("p7");
            var binding = (IOnlineMaterialWorldBinding)Find(root, "Game.ElementField.OnlineMaterialWorldBinding");
            var world = InitializeAuthority(root, binding);
            Assert.That(root.activeSelf, Is.False, "配置和初始化检查不能为测试激活整张作者地图。");
            Assert.That(binding.IsReady, Is.True); Assert.That(binding.Faulted, Is.False);
            Assert.That(binding.ReplicaInstaller, Is.Null);
            Assert.That(binding.AuthoritySimulation.ExecutionMode, Is.EqualTo(WorldExecutionMode.Authority));
            foreach (var component in root.GetComponentsInChildren<MonoBehaviour>(true))
                if (component != null) Assert.That(component.GetType().Namespace, Is.Not.EqualTo("Game.Rendering"));
            long before = Tick(world);
            world.GetType().GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(world, null);
            Assert.That(Tick(world), Is.EqualTo(before), "Authority不能沿单机自动Update再推进一次。");
            for (uint i = 1; i <= 3; i++) binding.AuthoritySimulation.StepAuthority(1f / 30f, i);
            Assert.That(Tick(world), Is.EqualTo(before + 1));
            binding.AuthoritySimulation.StepAuthority(1f / 30f, 3);
            Assert.That(Tick(world), Is.EqualTo(before + 1), "同一服务器Tick不能重复喂时。");
            Assert.That(binding.ReplicationSource.TryCapture(binding.CaptureBuffer, out var failure), Is.True, failure.ToString());
        }

        [Test] public void RealAuthorityBindingSuspensionDropsDebtAndDetachStopsItsCachedClock()
        {
            var root = Preview("p7");
            var binding = (IOnlineMaterialWorldBinding)Find(root, "Game.ElementField.OnlineMaterialWorldBinding");
            var world = InitializeAuthority(root, binding); var simulation = binding.AuthoritySimulation;
            simulation.StepAuthority(1f / 30f, 1); simulation.StepAuthority(1f / 30f, 2);
            binding.SetSuspended(true);
            for (uint i = 3; i <= 32; i++) simulation.StepAuthority(1f / 30f, i);
            Assert.That(Tick(world), Is.Zero);
            binding.SetSuspended(false); simulation.StepAuthority(1f / 30f, 33);
            Assert.That(Tick(world), Is.EqualTo(1), "解冻仅消费已有合法余数，不追赶一秒冻结债务。");
            binding.Detach(); binding.Detach(); simulation.StepAuthority(1f, 34);
            Assert.That(Tick(world), Is.EqualTo(1)); Assert.That(binding.IsReady, Is.False);
            Assert.That(binding.AuthoritySimulation, Is.Null); Assert.That(binding.ReplicationSource, Is.Null);
            Assert.That(binding.CaptureBuffer, Is.Null);
        }

        [Test] public void RealReplicaBindingCannotAdvanceAuthorityClockAndDetachReleasesItsInstaller()
        {
            var root = Preview("p8");
            var binding = (IOnlineMaterialWorldBinding)Find(root, "Game.ElementField.OnlineMaterialWorldBinding");
            JsonUtility.FromJsonOverwrite("{\"_captureCells\":128,\"_capturePending\":32,\"_replicaChunks\":8,\"_maximumChunkWords\":2048}", binding);
            Assert.That(binding.TryConfigure(WorldExecutionMode.Replica, 19), Is.True);
            var world = Find(root, "Game.ElementField.ElementWorldRuntime");
            var simulation = (IWorldAuthoritySimulation)world;
            simulation.StepAuthority(1f, 1);
            Assert.That(Tick(world), Is.Zero); Assert.That(binding.IsReady, Is.True);
            Assert.That(binding.AuthoritySimulation, Is.Null); Assert.That(binding.ReplicationSource, Is.Null);
            Assert.That(binding.CaptureBuffer, Is.Null); Assert.That(binding.ReplicaInstaller, Is.Not.Null);
            binding.Detach(); binding.Detach(); simulation.StepAuthority(1f, 2);
            Assert.That(Tick(world), Is.Zero); Assert.That(binding.IsReady, Is.False);
            Assert.That(binding.ReplicaInstaller, Is.Null);
        }

        [Test] public void BindingWhichAwokeWithoutConfigurationRejectsLateOwnershipChange()
        {
            var root = Preview("p7");
            var component = Find(root, "Game.ElementField.OnlineMaterialWorldBinding");
            component.GetType().GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(component, null);
            var binding = (IOnlineMaterialWorldBinding)component;
            Assert.That(binding.Faulted, Is.True);
            Assert.That(binding.TryConfigure(WorldExecutionMode.Authority, 19), Is.False);
            Assert.That(binding.TryConfigure(WorldExecutionMode.Replica, 19), Is.False);
            Assert.That(binding.AuthoritySimulation, Is.Null); Assert.That(binding.ReplicaInstaller, Is.Null);
        }

        private void PrepareBootstrap(GameObject content, bool authority)
        {
            _bootstrapRoot = new GameObject("Inactive material Bootstrap lifecycle fixture");
            _bootstrapRoot.SetActive(false);
            _bootstrap = _bootstrapRoot.AddComponent<OnlineWorldBootstrap>();
            typeof(OnlineWorldBootstrap).GetField("<IsAuthority>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(_bootstrap, authority);
            var prepare = typeof(OnlineWorldBootstrap).GetMethod("ConfigureMaterialWorldBeforeActivation", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(prepare, Is.Not.Null, "正式Bootstrap需要在激活前通过Core合同配置真实材料Binding。");
            prepare.Invoke(_bootstrap, new object[] { content, 19UL });
        }

        [TestCase(true)][TestCase(false)]
        public void BootstrapConfiguresRealBindingBeforeActivationAndDetachesItOnShutdown(bool authority)
        {
            var root = Preview("p7");
            var binding = (IOnlineMaterialWorldBinding)Find(root, "Game.ElementField.OnlineMaterialWorldBinding");
            PrepareBootstrap(root, authority);
            Assert.That(root.activeSelf, Is.False);
            if (authority)
            {
                InitializeAuthority(root, binding);
                Assert.That(binding.AuthoritySimulation.ExecutionMode, Is.EqualTo(WorldExecutionMode.Authority));
            }
            else
            {
                Assert.That(binding.IsReady, Is.True); Assert.That(binding.AuthoritySimulation, Is.Null);
                Assert.That(binding.ReplicaInstaller, Is.Not.Null);
            }
            _bootstrap.StopWorld(); _bootstrap.StopWorld();
            Assert.That(binding.IsReady, Is.False); Assert.That(binding.AuthoritySimulation, Is.Null);
            Assert.That(binding.ReplicaInstaller, Is.Null); Assert.That(binding.ReplicationSource, Is.Null);
        }

        [Test] public void BootstrapTickDriverPropagatesFrozenEvenWhenNoGameplayTickIsAllowed()
        {
            var root = Preview("p7");
            var binding = (IOnlineMaterialWorldBinding)Find(root, "Game.ElementField.OnlineMaterialWorldBinding");
            PrepareBootstrap(root, true);
            var world = InitializeAuthority(root, binding); var simulation = binding.AuthoritySimulation;
            // 配置入口初始暂停；先恢复一个独立窗口产生不足一材料Tick的合法余数。
            binding.SetSuspended(false);
            simulation.StepAuthority(1f / 30f, 1); simulation.StepAuthority(1f / 30f, 2);
            var driver = _bootstrapRoot.AddComponent<AuthorityTickDriver>();
            driver.Initialize(_bootstrap, false); // 不取得全局Physics，只观察生产Driver的0步分支。
            typeof(OnlineWorldBootstrap).GetField("_driver", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_bootstrap, driver);
            typeof(AuthorityTickDriver).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(driver, null);
            Assert.That(driver.Tick, Is.Zero, "未Ready/没有业务模拟许可时不能产生Gameplay Tick。");
            simulation.StepAuthority(1f / 30f, 3);
            Assert.That(Tick(world), Is.Zero, "即使本帧0逻辑步，Bootstrap仍须把Frozen传给材料Owner。");
        }

        [Test] public void BootstrapRejectsAlreadyActiveMaterialRootBeforeOwnershipConfiguration()
        {
            var root = Preview("p7"); root.SetActive(true);
            var error = Assert.Throws<TargetInvocationException>(() => PrepareBootstrap(root, true));
            Assert.That(error.InnerException, Is.TypeOf<InvalidOperationException>());
            var binding = (IOnlineMaterialWorldBinding)Find(root, "Game.ElementField.OnlineMaterialWorldBinding");
            Assert.That(binding.AuthoritySimulation, Is.Null); Assert.That(binding.ReplicaInstaller, Is.Null);
            root.SetActive(false);
        }
    }
}
