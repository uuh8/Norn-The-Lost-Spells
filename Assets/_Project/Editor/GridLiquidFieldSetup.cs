using System;
using System.IO;
using Game.ElementField;
using Game.Rendering;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>显式升级入口，保留旧组件和用户地形；所有场景改动可Undo，不自动Save。</summary>
    [InitializeOnLoad]
    internal static class GridLiquidFieldSetup
    {
        private const string Request="Temp/ElementPerformance/apply-field-surface.request";
        private const string Result="Temp/ElementPerformance/field-surface-setup.json";
        private const string ProfilePath="Assets/_Project/ScriptableObjects/ElementField/Liquids/GridLiquidFieldRenderProfile_Default.asset";
        private static double _next;
        static GridLiquidFieldSetup()=>EditorApplication.update+=Poll;
        private static void Poll()
        {
            if(EditorApplication.timeSinceStartup<_next)return;_next=EditorApplication.timeSinceStartup+1;
            if(EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode || !File.Exists(Request))return;
            File.Delete(Request);Apply();
        }
        [MenuItem("Tools/Grid Liquid/Upgrade Current Scene To Continuous Field (Undo)")]
        private static void Apply()
        {
            try
            {
                var world=UnityEngine.Object.FindFirstObjectByType<ElementWorldRuntime>();
                if(world==null || world.GridLiquids==null)throw new InvalidOperationException("当前场景没有Grid Liquid World。");
                var compute=AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/_Project/Art/Elemental/Compute/GridLiquidField.compute");
                var shader=AssetDatabase.LoadAssetAtPath<Shader>("Assets/_Project/Art/Elemental/Shaders/ElementLiquidContinuous.shader");
                if(compute==null || shader==null || ShaderUtil.ShaderHasError(shader))throw new InvalidOperationException("连续液体Shader尚未成功导入。");
                ConfigureWorld(world,compute,shader);
                EditorSceneManager.MarkSceneDirty(world.gameObject.scene);
                File.WriteAllText(Result,JsonUtility.ToJson(new SetupResult{Utc=DateTime.UtcNow.ToString("O"),Scene=world.gameObject.scene.path,Applied=true,SceneSaved=false,Profile=ProfilePath},true));
            }
            catch(Exception e){File.WriteAllText(Result,JsonUtility.ToJson(new SetupResult{Utc=DateTime.UtcNow.ToString("O"),Error=e.Message},true));Game.Core.GameLog.Error(e.Message,"GridLiquidFieldSetup");}
        }
        internal static void EnsureWorld(ElementWorldRuntime world)
        {
            var compute=AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/_Project/Art/Elemental/Compute/GridLiquidField.compute");
            var shader=AssetDatabase.LoadAssetAtPath<Shader>("Assets/_Project/Art/Elemental/Shaders/ElementLiquidContinuous.shader");
            if(compute!=null && shader!=null)ConfigureWorld(world,compute,shader);
        }
        private static void ConfigureWorld(ElementWorldRuntime world,ComputeShader compute,Shader shader)
        {
                var old=world.GetComponentsInChildren<LiquidSurfaceRenderer>(true);var palette=new Material[3];
                foreach(var r in old)
                {
                    var s=new SerializedObject(r);int slot=(int)s.FindProperty("_material").intValue;
                    // 正式Profile材料顺序Water/Poison/Sticky；读取旧组件上的实际材质引用。
                    if(slot==(int)Game.Materials.MaterialId.Water)palette[0]=(Material)s.FindProperty("_sourceMaterial").objectReferenceValue;
                    else if(slot==(int)Game.Materials.MaterialId.Poison)palette[1]=(Material)s.FindProperty("_sourceMaterial").objectReferenceValue;
                    else if(slot==(int)Game.Materials.MaterialId.Sticky)palette[2]=(Material)s.FindProperty("_sourceMaterial").objectReferenceValue;
                }
                for(int i=0;i<palette.Length;i++)if(palette[i]==null)throw new InvalidOperationException("缺少旧液体材质引用，不能用纯色默认材质替代。");
                var profile=AssetDatabase.LoadAssetAtPath<GridLiquidFieldRenderProfile>(ProfilePath);
                if(profile==null){profile=ScriptableObject.CreateInstance<GridLiquidFieldRenderProfile>();AssetDatabase.CreateAsset(profile,ProfilePath);}
                Undo.RecordObject(profile,"Configure Continuous Liquid Field");var p=new SerializedObject(profile);
                p.FindProperty("_compute").objectReferenceValue=compute;p.FindProperty("_shader").objectReferenceValue=shader;
                var ms=p.FindProperty("_palette");ms.arraySize=palette.Length;for(int i=0;i<palette.Length;i++)ms.GetArrayElementAtIndex(i).objectReferenceValue=palette[i];p.ApplyModifiedProperties();
                EditorUtility.SetDirty(profile);AssetDatabase.SaveAssetIfDirty(profile);
                var renderer=world.GetComponent<GridLiquidFieldSurfaceRenderer>();if(renderer==null)renderer=Undo.AddComponent<GridLiquidFieldSurfaceRenderer>(world.gameObject);
                Undo.RecordObject(renderer,"Connect Continuous Liquid Field");var so=new SerializedObject(renderer);
                so.FindProperty("_world").objectReferenceValue=world;so.FindProperty("_profile").objectReferenceValue=profile;so.ApplyModifiedProperties();renderer.enabled=true;
        }
        [Serializable]private sealed class SetupResult{public string Utc,Scene,Profile,Error;public bool Applied,SceneSaved;}
    }
}
