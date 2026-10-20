using System;
using System.IO;
using Game.Core;
using Game.ElementField;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
namespace Game.EditorTools
{
    /// <summary>七项修复共用已保存生产Scene的隔离副本，不保存或丢弃任何正式布局。</summary>
    [InitializeOnLoad]internal static class WorldGameplayRepairSetup
    {
        private const string Request="Temp/SinglePlayer/world-gameplay-repair.request";
        private const string Evidence="TestResults/SinglePlayer/M6-T25-WorldGameplayRepair";
        static WorldGameplayRepairSetup(){EditorApplication.update+=Poll;}
        private static void Poll()
        {
            if(!File.Exists(Request)||EditorApplication.isPlayingOrWillChangePlaymode||EditorApplication.isCompiling||EditorApplication.isUpdating)return;
            string command=File.ReadAllText(Request).Trim();File.Delete(Request);
            try
            {
                if(EditorUtility.scriptCompilationFailed)throw new InvalidOperationException("脚本编译失败");
                for(int i=0;i<SceneManager.sceneCount;i++)if(SceneManager.GetSceneAt(i).isDirty)throw new InvalidOperationException("有未保存Scene，不代保存/丢弃");
                if(command=="configure-rules"){ConfigureRules();return;}
                if(command!="prepare")throw new ArgumentException(command);
                var original=SceneManager.GetActiveScene();var report=new System.Text.StringBuilder();
                foreach(var name in new[]{"SinglePlayer_Hub","SinglePlayer_Home","P7_DemoRun","P8_BossField"})
                {
                    string folder="Assets/_Project/Scenes/",source=folder+(name.StartsWith("SinglePlayer")?"SinglePlayer/":"")+name+".unity";
                    string target=folder+"Tests/Hub/"+name+"_GameplayRepairReview.unity";
                    if(SceneManager.GetSceneByPath(target).isLoaded)throw new InvalidOperationException("Review仍打开，拒绝覆盖");
                    if(File.Exists(target)){File.Copy(source,target,true);AssetDatabase.ImportAsset(target,ImportAssetOptions.ForceUpdate);}
                    else if(!AssetDatabase.CopyAsset(source,target))throw new IOException(source);
                    report.AppendLine(target);
                }
                SceneManager.SetActiveScene(original);Write("prepared.txt",report+"FormalScenesSaved=false");
            }
            catch(Exception e){Write("setup-error.txt",e.ToString());GameLog.Error(e.ToString(),"WorldGameplayRepair");}
        }
        private static void ConfigureRules()
        {
            var profiles=new HashSet<ElementWorldProfile>();var original=SceneManager.GetActiveScene();var report=new System.Text.StringBuilder();
            foreach(var path in new[]{"Assets/_Project/Scenes/P7_DemoRun.unity","Assets/_Project/Scenes/P8_BossField.unity","Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Home.unity"})
            {
                var scene=SceneManager.GetSceneByPath(path);bool opened=!scene.isLoaded;if(opened)scene=EditorSceneManager.OpenScene(path,OpenSceneMode.Additive);
                try
                {
                    ElementWorldRuntime world=null;foreach(var root in scene.GetRootGameObjects()){world=root.GetComponentInChildren<ElementWorldRuntime>(true);if(world!=null)break;}
                    if(world==null)throw new InvalidOperationException("缺少生产世界："+path);
                    var profile=new SerializedObject(world).FindProperty("_profile").objectReferenceValue as ElementWorldProfile;
                    if(profile==null)throw new InvalidOperationException("缺少生产Profile："+path);profiles.Add(profile);report.AppendLine(path+" => "+AssetDatabase.GetAssetPath(profile));
                }
                finally{if(opened)EditorSceneManager.CloseScene(scene,true);SceneManager.SetActiveScene(original);}
            }
            foreach(var profile in profiles){var data=new SerializedObject(profile);data.FindProperty("_maxFireDownFlowPerTick").intValue=255;data.ApplyModifiedPropertiesWithoutUndo();EditorUtility.SetDirty(profile);}
            AssetDatabase.SaveAssets();Write("shared-rules.txt",report+"MaxFireDownFlowPerTick=255\nProfiles="+profiles.Count+"\nFormalScenesSaved=false");
        }
        private static void Write(string name,string text){Directory.CreateDirectory(Evidence);File.WriteAllText(Evidence+"/"+name,DateTime.UtcNow.ToString("O")+"\n"+text);}
    }
}
