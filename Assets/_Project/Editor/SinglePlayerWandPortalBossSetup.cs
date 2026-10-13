using System;
using System.IO;
using System.Text;
using Game.Run;
using Game.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.EditorTools
{
    /// <summary>本轮只生成三杖资产/独立Review/共享工作区；正式Scene仅Undo接线，不保存。</summary>
    [InitializeOnLoad]
    internal static class SinglePlayerWandPortalBossSetup
    {
        private const string Request="Temp/SinglePlayer/wand-portal-boss.request";
        private const string Hub="Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity";
        private const string Review="Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub_UnifiedItemsReview.unity";
        private const string Evidence="TestResults/SinglePlayer/M6-T11-PortalBossWands";
        static SinglePlayerWandPortalBossSetup(){EditorApplication.update+=Poll;}
        private static void Poll()
        {
            if(!File.Exists(Request)||EditorApplication.isCompiling||EditorApplication.isUpdating||EditorApplication.isPlayingOrWillChangePlaymode)return;
            string command=File.ReadAllText(Request).Trim();File.Delete(Request);
            try
            {
                if(EditorUtility.scriptCompilationFailed)throw new InvalidOperationException("编译失败，不能运行旧Assembly。");
                if(command=="prepare")Prepare();else if(command=="apply-current")ApplyCurrent();else if(command=="inspect")Write("inspection.txt",Inspect(SceneManager.GetActiveScene()));
                else throw new ArgumentException(command);
            }
            catch(Exception e){Write("error.txt",e.ToString());Game.Core.GameLog.Error(e.ToString(),"WandPortalBoss");}
        }
        private static void Configure(Scene scene,bool undo)
        {
            foreach(var root in scene.GetRootGameObjects())
                foreach(var panel in root.GetComponentsInChildren<WandProgramPanelView>(true))
                {
                    var fields=new SerializedObject(panel);var icon=fields.FindProperty("_icon").objectReferenceValue as Image;
                    if(icon==null)continue;
                    // 安装工具也遵守作者子布局。保留材质/颜色/Rect，仅补可拖拽的Raycast资格。
                    if(undo)Undo.RecordObject(icon,"法杖拖拽接线");
                    icon.raycastTarget=true;
                }
        }
        private static void Prepare()
        {
            for(int i=0;i<SceneManager.sceneCount;i++)if(SceneManager.GetSceneAt(i).isDirty)throw new InvalidOperationException("当前Scene未保存，不代保存/丢弃。");
            foreach(string name in new[]{"wand.a","wand.b","wand.c"})
            {
                var item=AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/SinglePlayer/Hub/SinglePlayerWands/"+name+".asset");
                if(item==null)throw new InvalidOperationException(name);
                var fields=new SerializedObject(item);fields.FindProperty("_shapeWidth").intValue=3;fields.FindProperty("_shapeHeight").intValue=1;
                var cells=fields.FindProperty("_occupiedCells");cells.arraySize=3;
                for(int x=0;x<3;x++)cells.GetArrayElementAtIndex(x).vector2IntValue=new Vector2Int(x,0);
                fields.ApplyModifiedPropertiesWithoutUndo();AssetDatabase.SaveAssetIfDirty(item);
            }
            int baked=ItemIconBaker.BakeAll(true,wandsOnly:true);
            var original=SceneManager.GetActiveScene();
            if(SceneManager.GetSceneByPath(Review).isLoaded)throw new InvalidOperationException("Review已打开。");
            if(!File.Exists(Review))AssetDatabase.CopyAsset(Hub,Review);
            else {File.Copy(Hub,Review,true);AssetDatabase.ImportAsset(Review,ImportAssetOptions.ForceUpdate);}
            var scene=EditorSceneManager.OpenScene(Review,OpenSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene);Configure(scene,false);
                var prefab=SinglePlayerItemCarrySetup.Generate(scene);
                foreach(var root in scene.GetRootGameObjects())foreach(var session in root.GetComponentsInChildren<SinglePlayerInventorySession>(true))
                {var data=new SerializedObject(session);data.FindProperty("_practiceWorkspacePrefab").objectReferenceValue=prefab;data.ApplyModifiedPropertiesWithoutUndo();}
                EditorSceneManager.SaveScene(scene);
                SessionState.SetString("Norn.HubAlignment.ReviewPath",Review);
                Write("prepared.txt","WandIcons="+baked+"\nShape=3 columns x1 row\n"+Inspect(scene));
            }
            finally{SceneManager.SetActiveScene(original);EditorSceneManager.CloseScene(scene,true);}
        }
        private static void ApplyCurrent()
        {
            var scene=SceneManager.GetActiveScene();if(scene.path!=Hub || SceneManager.sceneCount!=1)throw new InvalidOperationException("请单独打开据点。");
            Undo.IncrementCurrentGroup();int group=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("M6-T11 法杖宽图标");
            Configure(scene,true);Undo.FlushUndoRecordObjects();Undo.CollapseUndoOperations(group);EditorSceneManager.MarkSceneDirty(scene);
            Write("applied.txt",Inspect(scene)+"\n正式Scene未保存，由作者检查并Ctrl+S；单Undo组。");
        }
        private static string Inspect(Scene scene)
        {
            var report=new StringBuilder();report.AppendLine("Scene="+scene.path+" Dirty="+scene.isDirty+" SceneCount="+SceneManager.sceneCount);
            report.AppendLine("Playing="+EditorApplication.isPlayingOrWillChangePlaymode+" CompilationFailed="+EditorUtility.scriptCompilationFailed);
            int missing=0;
            foreach(var root in scene.GetRootGameObjects())foreach(var node in root.GetComponentsInChildren<Transform>(true))
            {
                missing+=GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(node.gameObject);
                var panel=node.GetComponent<WandProgramPanelView>();if(panel==null)continue;
                var image=new SerializedObject(panel).FindProperty("_icon").objectReferenceValue as Image;
                report.AppendLine("WandPanel="+node.name+" IconRect="+(image!=null?image.rectTransform.sizeDelta.ToString():"null"));
            }
            report.AppendLine("MissingScripts="+missing);return report.ToString();
        }
        private static void Write(string name,string value){Directory.CreateDirectory(Evidence);File.WriteAllText(Evidence+"/"+name,DateTime.UtcNow.ToString("O")+"\n"+value);}
    }
}
