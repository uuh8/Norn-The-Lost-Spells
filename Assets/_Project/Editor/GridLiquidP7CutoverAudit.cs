using System;
using System.IO;
using System.Text;
using Game.Character;
using Game.Combat;
using Game.Core;
using Game.ElementField;
using Game.Materials;
using Game.Rendering;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.EditorTools
{
    /// <summary>
    /// 正式 P7 切换前的只读接线审计。检查数据身份、写入路由和唯一表面，
    /// 不修改 Scene、Build Settings 或运行 Play；性能与画面仍须另做集中 Gate。
    /// </summary>
    internal static class GridLiquidP7CutoverAudit
    {
        private const string ProductionScene = "Assets/_Project/Scenes/P7_DemoRun.unity";
        private const string ComparisonScene =
            "Assets/_Project/Scenes/Prototypes/P7_DemoRun_GridLiquid.unity";
        private const string WorldAsset =
            "Assets/_Project/ScriptableObjects/ElementField/Liquids/ElementWorldProfile_P7Comparison.asset";
        private const string LiquidAsset =
            "Assets/_Project/ScriptableObjects/ElementField/Liquids/LiquidWorldProfile_P7Comparison.asset";
        private const string Report = "Temp/ElementPerformance/p7-grid-cutover-audit.txt";
        private static readonly MaterialId[] Liquids =
            { MaterialId.Water, MaterialId.Poison, MaterialId.Sticky };

        [MenuItem("Tools/Element World/Audit P7 Grid Cutover (Read Only)")]
        internal static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
            { GameLog.Warn("请在 EditMode 且导入完成后审计当前场景。", "Editor"); return; }
            RunScene(SceneManager.GetActiveScene(), Report);
        }

        // 跨场景同步仍使用同一份生产接线规则；显式传 Scene 避免 Additive 加载时
        // FindFirstObjectByType/ActiveScene 误读仍打开着的 P7，也无需重载用户关卡。
        internal static int RunScene(Scene scene, string destination)
        {
            var report = new StringBuilder(4096);
            int failures = 0;
            report.AppendLine("Scope: current loaded scene; includes unsaved changes.");
            report.AppendLine("Scene: " + scene.path + " | Dirty=" + scene.isDirty);
            try
            {
                Check(scene.IsValid() && scene.isLoaded, "当前场景有效且已加载", report, ref failures);
                ElementWorldRuntime world = FindWorld(scene, report, ref failures);
                if (world != null)
                {
                    ElementWorldProfile profile = Reference<ElementWorldProfile>(world, "_profile");
                    LiquidWorldRuntime runtime = Reference<LiquidWorldRuntime>(world, "_gridLiquidRuntime");
                    LiquidWorldProfile liquid = runtime != null ? Reference<LiquidWorldProfile>(runtime, "_profile") : null;
                    Check(profile != null && liquid != null, "当前世界连接完整 World / Liquid Profile", report, ref failures);
                    if (profile != null && liquid != null)
                    {
                        AuditProfiles(profile, liquid, report, ref failures);
                        AuditGridScene(scene, world, profile, liquid, report, ref failures);
                    }
                }
            }
            catch (Exception exception) { failures++; report.AppendLine("FAIL: " + exception); }
            // 只读当前内存场景：无需Additive打开旧对照资产，也不要求用户先保存未保存关卡。
            report.Insert(0, "Current Scene Grid Cutover Audit | " + DateTime.UtcNow.ToString("O") + " | Failures=" + failures + "\n");
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.WriteAllText(destination, report.ToString(), Encoding.UTF8);
            if (failures == 0) GameLog.Info("当前场景静态接线审计通过；" + destination + "。视觉与性能另由Play验证。", "Editor");
            else GameLog.Error("当前场景接线审计失败=" + failures + "；" + destination, "Editor");
            return failures;
        }

        private static ElementWorldRuntime FindWorld(Scene scene, StringBuilder report,
            ref int failures)
        {
            ElementWorldRuntime found = null;
            int count = 0;
            foreach (GameObject root in scene.GetRootGameObjects())
            foreach (ElementWorldRuntime world in root.GetComponentsInChildren<ElementWorldRuntime>(true))
            {
                found = world;
                count++;
            }
            Check(count == 1, scene.path + " 恰有一个 ElementWorldRuntime",
                report, ref failures);
            return count == 1 ? found : null;
        }

        private static void AuditProfiles(ElementWorldProfile world,
            LiquidWorldProfile liquid, StringBuilder report, ref int failures)
        {
            Check(world.WaterSimulationMode == WaterSimulationMode.GridLiquid
                    && !world.EnableFluidChunkStreaming,
                "Grid World 由 GridLiquid 独占 Water，旧 Fluid Streaming 关闭",
                report, ref failures);
            Check(world.MaterialCatalog != null,
                "Grid World 有 Material Catalog", report, ref failures);
            if (world.MaterialCatalog != null)
            {
                // 曾出现 Sticky 缺失导致整个 Runtime 初始化失败；在进入 Play 前
                // 用与 Runtime 相同的 Catalog Snapshot 校验三种液体身份。
                MaterialCatalogSnapshot catalog = world.MaterialCatalog.CreateSnapshot();
                for (int i = 0; i < Liquids.Length; i++)
                    Check(catalog.TryGet(Liquids[i], out MaterialDefinitionSnapshot definition)
                            && definition.Behavior == MaterialBehaviorKind.Liquid,
                        Liquids[i] + " 在 Catalog 中且 Behavior=Liquid",
                        report, ref failures);
            }
            CheckStatusProjection(world, report, ref failures);
            Check(world.MaterialSimulationRouting != null,
                "Grid World 有独立材料路由", report, ref failures);
            if (world.MaterialSimulationRouting != null)
            {
                string routePath = AssetDatabase.GetAssetPath(world.MaterialSimulationRouting);
                Check(routePath.Contains("P7Comparison") || routePath.Contains("_Grid"),
                    "Grid Route 是 P7 独立资产", report, ref failures);
                var routes = new SerializedObject(world.MaterialSimulationRouting)
                    .FindProperty("_routes");
                Check(routes != null && routes.arraySize == 3,
                    "P7 对照 Route 只配置 Fire、Poison、Sticky",
                    report, ref failures);
                CheckRoute(routes, MaterialId.Fire,
                    MaterialSimulationBackendKind.ElementCell, report, ref failures);
                CheckRoute(routes, MaterialId.Poison,
                    MaterialSimulationBackendKind.GridLiquid, report, ref failures);
                CheckRoute(routes, MaterialId.Sticky,
                    MaterialSimulationBackendKind.GridLiquid, report, ref failures);
            }
            try { liquid.Validate(); }
            catch (Exception exception)
            {
                failures++;
                report.AppendLine("FAIL: Grid Liquid Profile 无效：" + exception.Message);
            }
            Check(liquid.MaterialCount == Liquids.Length,
                "P7 Grid Profile 只包含三种目标液体", report, ref failures);
            for (int m = 0; m < Liquids.Length; m++)
            {
                MaterialId material = Liquids[m];
                int count = 0;
                for (int i = 0; i < liquid.MaterialCount; i++)
                {
                    LiquidBehaviorProfile behavior = liquid.GetMaterial(i);
                    if (behavior == null || behavior.Material != material) continue;
                    count++;
                    string behaviorPath = AssetDatabase.GetAssetPath(behavior);
                    Check(behaviorPath.Contains("P7Comparison") || behaviorPath.EndsWith("_Grid.asset", StringComparison.Ordinal),
                        material + " Behavior 是 P7 独立资产", report, ref failures);
                    Check(behavior.AllowsStableRegion,
                        material + " 允许稳定区域", report, ref failures);
                }
                Check(count == 1, material + " 在 Grid Profile 中恰有一条行为",
                    report, ref failures);
            }
        }

        private static void CheckRoute(SerializedProperty routes, MaterialId material,
            MaterialSimulationBackendKind expected, StringBuilder report, ref int failures)
        {
            int count = 0;
            if (routes != null)
                for (int i = 0; i < routes.arraySize; i++)
                {
                    SerializedProperty route = routes.GetArrayElementAtIndex(i);
                    if (route.FindPropertyRelative("_material").intValue != (int)material) continue;
                    if (route.FindPropertyRelative("_backend").intValue == (int)expected) count++;
                    else count += 2; // 错误 Backend 与重复 Route 都应失败。
                }
            Check(count == 1, material + " 恰有一条预期 Backend 路由",
                report, ref failures);
        }

        private static void CheckStatusProjection(ElementWorldProfile world,
            StringBuilder report, ref int failures)
        {
            Check(world.MaterialStatusProjection != null,
                "P7 Grid 有材料到状态的投影规则", report, ref failures);
            if (world.MaterialStatusProjection == null) return;
            // Asset 审计只检查配置：角色站到材料中能否实际拿到状态仍由 Play Gate 决定。
            SerializedProperty bindings = new SerializedObject(world.MaterialStatusProjection)
                .FindProperty("_bindings");
            CheckStatusBinding(bindings, MaterialId.Water, StatusKind.Wet, report, ref failures);
            CheckStatusBinding(bindings, MaterialId.Poison, StatusKind.Poisoned, report, ref failures);
            CheckStatusBinding(bindings, MaterialId.Sticky, StatusKind.Sticky, report, ref failures);
        }

        private static void CheckStatusBinding(SerializedProperty bindings,
            MaterialId material, StatusKind status, StringBuilder report, ref int failures)
        {
            int count = 0;
            if (bindings != null)
                for (int i = 0; i < bindings.arraySize; i++)
                {
                    SerializedProperty binding = bindings.GetArrayElementAtIndex(i);
                    if (binding.FindPropertyRelative("Material").intValue != (int)material)
                        continue;
                    if (binding.FindPropertyRelative("Status").intValue == (int)status
                        && binding.FindPropertyRelative("MaximumApplyPerExposureTick").floatValue > 0f)
                        count++;
                    else count += 2;
                }
            Check(count == 1, material + " 以正剂量投影为 " + status,
                report, ref failures);
        }

        private static void AuditSharedGameplayData(ElementWorldProfile oldProfile,
            ElementWorldProfile gridProfile, StringBuilder report, ref int failures)
        {
            // 对照应只更换液体后端；格长、Tick 与反应／状态数据漂移会使 A/B 失去意义。
            Check(oldProfile.CellSize == gridProfile.CellSize
                    && oldProfile.ChunkSize == gridProfile.ChunkSize
                    && oldProfile.TickRate == gridProfile.TickRate,
                "P7 两配置的 Cell／Chunk／Tick 粒度一致", report, ref failures);
            Check(oldProfile.MaterialCatalog == gridProfile.MaterialCatalog
                    && oldProfile.MaterialStatusProjection == gridProfile.MaterialStatusProjection
                    && oldProfile.ReactionProfile == gridProfile.ReactionProfile
                    && oldProfile.MaterialReactionBindings == gridProfile.MaterialReactionBindings,
                "P7 两配置共用材料、状态和反应规则", report, ref failures);
        }

        private static void AuditGridScene(Scene scene, ElementWorldRuntime world,
            ElementWorldProfile profile, LiquidWorldProfile liquid,
            StringBuilder report, ref int failures)
        {
            Check(world.enabled && world.gameObject.activeInHierarchy,
                "P7 Grid Scene 的 ElementWorldRuntime 启用", report, ref failures);
            ElementWorldExposureSystem exposure = world.GetComponent<ElementWorldExposureSystem>();
            int exposureCount = 0;
            foreach (GameObject root in scene.GetRootGameObjects())
                exposureCount += root.GetComponentsInChildren<ElementWorldExposureSystem>(true).Length;
            Check(exposureCount == 1 && exposure != null && exposure.enabled
                    && exposure.gameObject.activeInHierarchy,
                "P7 Grid 有且只有一个启用的角色接触投影组件，位于 World Runtime 同物体",
                report, ref failures);
            if (exposure != null)
            {
                var exposureData = new SerializedObject(exposure);
                Check(exposureData.FindProperty("_targetLayers").intValue != 0
                        && exposureData.FindProperty("_exposureInterval").floatValue > 0f,
                    "P7 Grid 的角色接触 Layer 和采样间隔有效", report, ref failures);
                AuditCharacterContact(scene, exposureData.FindProperty("_targetLayers").intValue,
                    report, ref failures);
            }
            Check(Reference<ElementWorldProfile>(world, "_profile") == profile,
                "P7 Grid Scene 引用独立 World Profile", report, ref failures);
            LiquidWorldRuntime runtime = Reference<LiquidWorldRuntime>(world,
                "_gridLiquidRuntime");
            Check(runtime != null && runtime.gameObject.scene == scene && runtime.enabled,
                "P7 Grid Scene 连接且启用同场景 LiquidWorldRuntime",
                report, ref failures);
            if (runtime != null)
                Check(Reference<LiquidWorldProfile>(runtime, "_profile") == liquid,
                    "LiquidWorldRuntime 引用独立 P7 Liquid Profile", report,
                    ref failures);

            int dynamicCount = 0, stableCount = 0, oldEnabled = 0;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                oldEnabled += EnabledCount<GpuPbfFluidRuntime>(root);
                oldEnabled += EnabledCount<FluidChunkStreamingRuntime>(root);
                oldEnabled += EnabledCount<FluidGameplayOccupancyBridge>(root);
                oldEnabled += EnabledCount<GpuLiquidSurfaceRenderer>(root);
                oldEnabled += EnabledCount<DormantLiquidCellRenderer>(root);
                oldEnabled += EnabledCount<ElementWorldWaterRenderer>(root);
                foreach (LiquidSurfaceRenderer surface in
                    root.GetComponentsInChildren<LiquidSurfaceRenderer>(true))
                {
                    dynamicCount++;
                    CheckSurface(surface, world, report, ref failures);
                }
                foreach (StableLiquidSurfaceRenderer surface in
                    root.GetComponentsInChildren<StableLiquidSurfaceRenderer>(true))
                {
                    stableCount++;
                    CheckSurface(surface, world, report, ref failures);
                }
            }
            Check(oldEnabled == 0, "P7 Grid 中旧 Runtime／Renderer 全部停用",
                report, ref failures);
            Check(dynamicCount == 3 && stableCount == 3,
                "P7 Grid 中三材料各保留动态回退与稳定表面", report, ref failures);
            int continuousCount=0;
            foreach(var root in scene.GetRootGameObjects())foreach(var field in root.GetComponentsInChildren<GridLiquidFieldSurfaceRenderer>(true))
            {
                if(!field.isActiveAndEnabled)continue;continuousCount++;
                var data=new SerializedObject(field);
                Check(data.FindProperty("_world").objectReferenceValue==world && data.FindProperty("_profile").objectReferenceValue!=null,
                    "统一连续液体表面连到当前世界与渲染Profile",report,ref failures);
                var renderProfile=data.FindProperty("_profile").objectReferenceValue as GridLiquidFieldRenderProfile;
                Check(renderProfile!=null && renderProfile.Compute!=null && renderProfile.Shader!=null
                    && renderProfile.Shader.name=="Game/Elemental/Liquid Continuous Field",
                    "统一外壳Compute与URP Shader引用完整",report,ref failures);
                if(renderProfile!=null)for(int slot=0;slot<liquid.MaterialCount;slot++)
                    Check(renderProfile.GetMaterial(slot)!=null,"统一Palette材料槽 "+slot+" 完整",report,ref failures);
            }
            Check(continuousCount==1,"P7有唯一统一连续液体外壳",report,ref failures);
            for (int i = 0; i < Liquids.Length; i++)
            {
                Check(CountMaterial<LiquidSurfaceRenderer>(scene, Liquids[i]) == 1,
                    Liquids[i] + " 动态表面唯一", report, ref failures);
                Check(CountMaterial<StableLiquidSurfaceRenderer>(scene, Liquids[i]) == 1,
                    Liquids[i] + " 稳定表面唯一", report, ref failures);
            }
        }

        private static void CheckSurface(Behaviour surface, ElementWorldRuntime world,
            StringBuilder report, ref int failures)
        {
            var data = new SerializedObject(surface);
            Check(surface.enabled && surface.gameObject.activeInHierarchy
                    && data.FindProperty("_world").objectReferenceValue == world
                    && data.FindProperty("_sourceMaterial").objectReferenceValue != null
                    && data.FindProperty("_liquidShader").objectReferenceValue != null,
                surface.name + " 启用并连到同一世界及材质／Shader",
                report, ref failures);
            var shader = data.FindProperty("_liquidShader").objectReferenceValue as Shader;
            var material = data.FindProperty("_sourceMaterial").objectReferenceValue as Material;
            // 有非空 Material 并不证明恢复了旧表面。检查真正使用的 Mesh Pass 和动态波纹输入，
            // 防止新后端只接了纯色 Shader，却在接线审计中被误判为表现已完成。
            Check(shader != null && shader.name == "Game/Elemental/Liquid Dormant Cell"
                    && material != null && material.shader == shader
                    && material.HasProperty("_WaveNormalStrength")
                    && material.GetFloat("_WaveNormalStrength") > 0f
                    && material.HasProperty("_WaveSpeedA")
                    && material.GetVector("_WaveSpeedA").sqrMagnitude > 0f
                    && material.GetFloat("_DebugForceOpaqueFragment") < .5f,
                surface.name + " 复用原液体表面光照，波纹启用且纯色诊断关闭",
                report, ref failures);
        }

        private static void AuditCharacterContact(Scene scene, int targetLayers,
            StringBuilder report, ref int failures)
        {
            int wizardCount = 0;
            bool hasStatus = false, sampledCollider = false;
            foreach (GameObject root in scene.GetRootGameObjects())
            foreach (WizardController wizard in root.GetComponentsInChildren<WizardController>(true))
            {
                wizardCount++;
                hasStatus |= wizard.GetComponent<StatusController>() != null;
                foreach (Collider collider in wizard.GetComponentsInChildren<Collider>(true))
                    if (collider.enabled && (targetLayers & (1 << collider.gameObject.layer)) != 0)
                        sampledCollider = true;
            }
            Check(wizardCount == 1 && hasStatus && sampledCollider,
                "P7 Wizard 有状态所有者，且至少一个启用 Collider 落在 Exposure Target Layers",
                report, ref failures);
        }

        private static int CountMaterial<T>(Scene scene, MaterialId material) where T : Behaviour
        {
            int count = 0;
            foreach (GameObject root in scene.GetRootGameObjects())
            foreach (T surface in root.GetComponentsInChildren<T>(true))
                if (new SerializedObject(surface).FindProperty("_material").intValue
                    == (int)material) count++;
            return count;
        }

        private static int EnabledCount<T>(GameObject root) where T : Behaviour
        {
            int count = 0;
            foreach (T behaviour in root.GetComponentsInChildren<T>(true))
                if (behaviour.enabled && behaviour.gameObject.activeInHierarchy) count++;
            return count;
        }

        private static T Reference<T>(UnityEngine.Object owner, string property)
            where T : UnityEngine.Object =>
            new SerializedObject(owner).FindProperty(property).objectReferenceValue as T;

        private static void Check(bool okay, string label, StringBuilder report,
            ref int failures)
        {
            if (!okay) failures++;
            report.AppendLine((okay ? "OK: " : "FAIL: ") + label);
        }
    }
}
