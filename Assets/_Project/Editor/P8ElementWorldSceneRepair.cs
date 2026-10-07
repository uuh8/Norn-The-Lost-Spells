#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using Game.Core;
using Game.ElementField;
using Game.Rendering;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Game.EditorTools
{
    /// <summary>
    /// 显式把 P8 接入当前 P7 的 Grid 生产配置。规则共享，Runtime/Buffer 各场景独立。
    /// Domain Reload 只注册请求入口，绝不自动按旧 PBF 模板迁移或保存用户场景。
    /// </summary>
    [InitializeOnLoad]
    internal static class P8ElementWorldSceneRepair
    {
        private const string P7 = "Assets/_Project/Scenes/P7_DemoRun.unity";
        private const string P8 = "Assets/_Project/Scenes/P8_BossField.unity";
        private const string P8Profile = "Assets/_Project/ScriptableObjects/P8/ElementField/ElementWorldProfile_P8BossField.asset";
        private const string Request = "Temp/ElementPerformance/p8-grid-sync.request";
        private const string Result = "Temp/ElementPerformance/p8-grid-sync.json";
        private const string Audit = "Temp/ElementPerformance/p8-grid-cutover-audit.txt";
        private static double _nextPoll;
        static P8ElementWorldSceneRepair() => EditorApplication.update += Poll;

        private static void Poll()
        {
            if(EditorApplication.timeSinceStartup<_nextPoll)return;
            _nextPoll=EditorApplication.timeSinceStartup+0.5;
            if(EditorApplication.isCompiling||EditorApplication.isUpdating||EditorApplication.isPlayingOrWillChangePlaymode||!File.Exists(Request))return;
            string command=File.ReadAllText(Request).Trim();File.Delete(Request);
            if(command=="sync"||command=="audit")Run(command=="sync");
        }
        [MenuItem("Tools/Game/Element/Repair P8 ElementWorld Scene")]
        private static void RepairFromMenu()=>Run(true);
        [MenuItem("Tools/Game/Element/Validate P8 ElementWorld Scene")]
        private static void ValidateFromMenu()=>Run(false);

        private static void Run(bool synchronize)
        {
            Scene active=SceneManager.GetActiveScene(),p7=SceneManager.GetSceneByPath(P7),p8=SceneManager.GetSceneByPath(P8);
            bool openedP7=false,openedP8=false;ElementWorldProfile rollback=null,targetProfile=null;
            bool targetDirty=false,committed=false;
            var result=new SyncResult{Utc=DateTime.UtcNow.ToString("O"),Scene=P8};
            try
            {
                if(EditorApplication.isPlayingOrWillChangePlaymode)throw new InvalidOperationException("请先退出 Play Mode。");
                if(!p7.IsValid()||!p7.isLoaded){p7=EditorSceneManager.OpenScene(P7,OpenSceneMode.Additive);openedP7=true;}
                if(!p8.IsValid()||!p8.isLoaded){p8=EditorSceneManager.OpenScene(P8,OpenSceneMode.Additive);openedP8=true;}
                // 已打开且 Dirty 的 P8 只在内存同步，保存权仍属于用户；P7 永远只读。
                bool saveScene=!p8.isDirty;
                var source=Single<ElementWorldRuntime>(p7);var target=Single<ElementWorldRuntime>(p8);
                if(synchronize)
                {
                    var canonical=Reference<ElementWorldProfile>(source,"_profile");
                    if(canonical==null||canonical.WaterSimulationMode!=WaterSimulationMode.GridLiquid||canonical.EnableFluidChunkStreaming)
                        throw new InvalidOperationException("实际 P7 尚未配置当前 Grid 后端，拒绝复制旧模板。");
                    var sourceLiquid=Single<LiquidWorldRuntime>(p7);
                    var sourceField=Single<GridLiquidFieldSurfaceRenderer>(p7);
                    var liquidProfile=Reference<LiquidWorldProfile>(sourceLiquid,"_profile");
                    var renderProfile=Reference<GridLiquidFieldRenderProfile>(sourceField,"_profile");
                    if(liquidProfile==null||renderProfile==null||renderProfile.Compute==null||renderProfile.Shader==null)
                        throw new InvalidOperationException("P7 缺少完整 Liquid/Render Profile。");
                    targetProfile=AssetDatabase.LoadAssetAtPath<ElementWorldProfile>(P8Profile)
                        ??throw new InvalidOperationException("缺少 P8 World Profile。");
                    targetDirty=EditorUtility.IsDirty(targetProfile);rollback=Object.Instantiate(targetProfile);
                    string originalName=targetProfile.name;
                    // CopySerialized 只同步配置字段，目标 Asset 的 GUID 保留；所有规则引用随生产源更新。
                    EditorUtility.CopySerialized(canonical,targetProfile);targetProfile.name=originalName;
                    var interest=Reference<Transform>(target,"_interestPoint")
                        ??throw new InvalidOperationException("P8 缺少自己的玩家 Interest Point。");
                    if(interest.gameObject.scene!=p8)throw new InvalidOperationException("P8 Interest 引用了别的场景。");
                    var liquid=Ensure<LiquidWorldRuntime>(target.gameObject);Bind(liquid,"_profile",liquidProfile);liquid.enabled=true;
                    Bind(target,"_profile",targetProfile);Bind(target,"_gridLiquidRuntime",liquid);
                    var field=Ensure<GridLiquidFieldSurfaceRenderer>(target.gameObject);
                    Bind(field,"_world",target);Bind(field,"_profile",renderProfile);field.enabled=true;
                    SynchronizeSurfaces<LiquidSurfaceRenderer>(p7,p8,target);
                    SynchronizeSurfaces<StableLiquidSurfaceRenderer>(p7,p8,target);
                    Disable<GpuPbfFluidRuntime>(p8);Disable<FluidChunkStreamingRuntime>(p8);
                    Disable<FluidGameplayOccupancyBridge>(p8);Disable<GpuLiquidSurfaceRenderer>(p8);
                    Disable<DormantLiquidCellRenderer>(p8);Disable<ElementWorldWaterRenderer>(p8);
                    // 旧 PBF 用 11×11 粗 Box 代理 Terrain；Grid 的支撑/固体缓存读取真实
                    // TerrainCollider。随旧后端停用代理，避免它们参与新的地形查询。
                    foreach(var child in target.GetComponentsInChildren<Transform>(true))
                        if(child.name=="FluidTerrainCollision")child.gameObject.SetActive(false);
                    var exposure=Single<ElementWorldExposureSystem>(p8);
                    // Grid 的真实量由 World.MaterialAmounts 查询；旧粒子快照不再参与 Broadphase。
                    Bind(exposure,"_liquidOccupancyComponent",null);exposure.enabled=true;
                    var fire=Single<GpuFireParcelRenderer>(p8);
                    EditorUtility.CopySerialized(Single<GpuFireParcelRenderer>(p7),fire);
                    Bind(fire,"_worldRuntime",target);Bind(fire,"_interestCenter",interest);fire.enabled=true;
                    EditorUtility.CopySerialized(Single<ReactionBurstVisualController>(p7),Single<ReactionBurstVisualController>(p8));
                    EditorSceneManager.MarkSceneDirty(p8);
                }
                result.AuditFailures=GridLiquidP7CutoverAudit.RunScene(p8,Audit);
                if(result.AuditFailures!=0)throw new InvalidOperationException("P8 接线审计失败；未保存场景，查看 "+Audit);
                if(synchronize)
                {
                    // 精确保存本次明确修改的 Profile，避免 SaveAssets 顺带保存其他正在编辑的资产。
                    EditorUtility.SetDirty(targetProfile);AssetDatabase.SaveAssetIfDirty(targetProfile);
                    if(saveScene)
                    {
                        if(!EditorSceneManager.SaveScene(p8))throw new InvalidOperationException("P8 保存失败。");
                        result.SceneSaved=true;
                    }
                    result.Applied=true;committed=true;
                }
                result.Success=true;
            }
            catch(Exception exception)
            {
                if(rollback!=null&&targetProfile!=null&&!committed)
                {EditorUtility.CopySerialized(rollback,targetProfile);if(!targetDirty)EditorUtility.ClearDirty(targetProfile);}
                result.Error=exception.ToString();GameLog.Error(result.Error,"P8 Grid Sync");
            }
            finally
            {
                if(rollback!=null)Object.DestroyImmediate(rollback);
                if(active.IsValid()&&active.isLoaded)SceneManager.SetActiveScene(active);
                if(openedP8&&p8.IsValid()&&p8.isLoaded)EditorSceneManager.CloseScene(p8,true);
                if(openedP7&&p7.IsValid()&&p7.isLoaded)EditorSceneManager.CloseScene(p7,true);
                result.ActiveScene=SceneManager.GetActiveScene().path;
                Directory.CreateDirectory(Path.GetDirectoryName(Result));File.WriteAllText(Result,JsonUtility.ToJson(result,true));
            }
        }
        private static void SynchronizeSurfaces<T>(Scene source,Scene target,ElementWorldRuntime world)where T:Behaviour
        {
            T[] originals=Components<T>(source);if(originals.Length!=3)throw new InvalidOperationException("P7 三材料表面不完整："+typeof(T).Name);
            foreach(var original in originals)
            {
                int material=new SerializedObject(original).FindProperty("_material").intValue;T found=null;
                foreach(var candidate in Components<T>(target))
                    if(new SerializedObject(candidate).FindProperty("_material").intValue==material)
                    {if(found!=null)throw new InvalidOperationException("P8 材料表面重复。");found=candidate;}
                if(found==null)
                {
                    var go=new GameObject(original.gameObject.name);SceneManager.MoveGameObjectToScene(go,target);
                    go.transform.SetParent(world.transform,false);found=go.AddComponent<T>();
                }
                // 只复制组件配置与共享材质；目标世界重绑，不复制其他场景的活跃数量或 GameObject。
                EditorUtility.CopySerialized(original,found);Bind(found,"_world",world);found.enabled=true;
            }
        }
        private static T Ensure<T>(GameObject go)where T:Component=>go.GetComponent<T>()??go.AddComponent<T>();
        private static void Bind(Object owner,string property,Object value)
        {var data=new SerializedObject(owner);data.FindProperty(property).objectReferenceValue=value;data.ApplyModifiedPropertiesWithoutUndo();}
        private static T Reference<T>(Object owner,string property)where T:Object=>new SerializedObject(owner).FindProperty(property).objectReferenceValue as T;
        private static T[] Components<T>(Scene scene)where T:Component
        {var list=new List<T>();foreach(var root in scene.GetRootGameObjects())list.AddRange(root.GetComponentsInChildren<T>(true));return list.ToArray();}
        private static T Single<T>(Scene scene)where T:Component
        {var values=Components<T>(scene);if(values.Length!=1)throw new InvalidOperationException(scene.path+" "+typeof(T).Name+" count="+values.Length);return values[0];}
        private static void Disable<T>(Scene scene)where T:Behaviour
        {foreach(var component in Components<T>(scene))component.enabled=false;}
        [Serializable]private sealed class SyncResult
        {public string Utc,Scene,ActiveScene,Error;public bool Success,Applied,SceneSaved;public int AuditFailures;}
    }
}
#endif
