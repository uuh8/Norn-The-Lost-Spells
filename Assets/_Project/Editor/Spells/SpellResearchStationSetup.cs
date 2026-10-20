using System;
using System.Collections.Generic;
using System.IO;
using Game.Core;
using Game.Run;
using Game.UI;
using Game.Character;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.EditorTools
{
    /// <summary>正式研究站的显式维护入口；早期覆盖整场景的Review/Apply脚手架已退役。</summary>
    [InitializeOnLoad]
    public static class SpellResearchStationSetup
    {
        public const string HubPath="Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity";
        private const string Request="Temp/Spells/integration.request";
        static SpellResearchStationSetup(){EditorApplication.update+=Poll;}
        private static void Poll()
        {
            if(!File.Exists(Request)||EditorApplication.isCompiling||EditorApplication.isUpdating||EditorApplication.isPlayingOrWillChangePlaymode)return;
            string command=File.ReadAllText(Request).Trim();File.Delete(Request);
            try
            {
                if(command=="inspect")InspectHub();
                else if(command=="migration")WandExecutionDiagnosticsWindow.ExportMigrationEvidence();
                else if(command=="content")SpellContentSetup.Build();
                else if(command=="home")ApplyHomeResearch();
                else throw new InvalidOperationException("未知接线指令："+command);
                File.WriteAllText("Temp/Spells/integration-result.txt","OK: "+command);
            }
            catch(Exception e){File.WriteAllText("Temp/Spells/integration-result.txt",e.ToString());GameLog.Error(e.Message,"SpellV2");}
        }
        [Serializable]private sealed class Entry{public string Name,Parent;public Vector3 Position;}
        [Serializable]private sealed class Inspection{public List<Entry> Entries=new List<Entry>();}
        private static void Set(UnityEngine.Object target,string field,UnityEngine.Object value)
        {var data=new SerializedObject(target);data.FindProperty(field).objectReferenceValue=value;data.ApplyModifiedPropertiesWithoutUndo();}
        private static T Find<T>(Scene scene) where T:Component
        {foreach(var root in scene.GetRootGameObjects()){var item=root.GetComponentInChildren<T>(true);if(item!=null)return item;}throw new InvalidOperationException("缺少 "+typeof(T).Name);}
        private static void ApplyHomeResearch()
        {
            const string path="Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Home.unity";
            if(EditorSceneManager.GetActiveScene().isDirty)throw new InvalidOperationException("当前Scene有未保存编辑，保留现场。");
            string previous=SceneManager.GetActiveScene().path;
            const string backup="Temp/Spells/FeedbackBackup";Directory.CreateDirectory(backup);
            foreach(var file in new[]{path,"Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Home.unity.meta"}){string target=backup+"/"+Path.GetFileName(file);if(!File.Exists(target))File.Copy(file,target);}
            var scene=EditorSceneManager.OpenScene(path,OpenSceneMode.Single);
            try
            {
                var session=Find<SinglePlayerInventorySession>(scene);var pause=Find<RunPauseCoordinator>(scene);var player=Find<WizardController>(scene);
                var data=new SerializedObject(session);data.FindProperty("_useSpellProgrammingV2").boolValue=true;data.ApplyModifiedPropertiesWithoutUndo();
                var definition=AssetDatabase.LoadAssetAtPath<HubDefinition>("Assets/_Project/ScriptableObjects/World/Home/HomeResearch.asset");
                if(definition==null){definition=ScriptableObject.CreateInstance<HubDefinition>();AssetDatabase.CreateAsset(definition,"Assets/_Project/ScriptableObjects/World/Home/HomeResearch.asset");}
                // 放在初始位置右侧，避开南侧返回金门；Box与四条视觉边界使用相同局部尺寸。
                Vector3 center=new Vector3(6,1.5f,0),size=new Vector3(4,4,4);
                definition.Stations=new[]{new HubDefinition.Station{Kind=HubStationKind.SpellResearch,Position=center,DisplayName="法术研究台"}};EditorUtility.SetDirty(definition);
                GameObject areaObject=null;foreach(var root in scene.GetRootGameObjects())if(root.name=="HomeSpellResearchArea_实验区研究台")areaObject=root;
                if(areaObject==null)areaObject=new GameObject("HomeSpellResearchArea_实验区研究台");areaObject.transform.position=center;
                var area=areaObject.GetComponent<HubAreaVolume>();if(area==null)area=areaObject.AddComponent<HubAreaVolume>();area.Volume.isTrigger=true;area.Volume.size=size;
                if(areaObject.transform.childCount==0)
                {
                    var mat=AssetDatabase.LoadAssetAtPath<Material>("Assets/_Project/Art/Materials/World/ResearchBorder.mat");
                    for(int i=0;i<4;i++){var edge=GameObject.CreatePrimitive(PrimitiveType.Cube);edge.name="研究区边缘";UnityEngine.Object.DestroyImmediate(edge.GetComponent<Collider>());edge.transform.SetParent(areaObject.transform,false);edge.transform.localPosition=new Vector3(i<2?(i==0?-2:2):0,-1.47f,i<2?0:(i==2?-2:2));edge.transform.localScale=i<2?new Vector3(.035f,.025f,4):new Vector3(4,.025f,.035f);edge.GetComponent<Renderer>().sharedMaterial=mat;}
                }
                var hub=session.GetComponent<HubInteractionController>();if(hub==null)hub=session.gameObject.AddComponent<HubInteractionController>();
                Set(hub,"_session",session);Set(hub,"_definition",definition);Set(hub,"_player",player.transform);Set(hub,"_researchArea",area);Set(hub,"_researchPause",pause);
                var input=player.GetComponent<HubInteractInput>();if(input==null)input=player.gameObject.AddComponent<HubInteractInput>();Set(input,"_hub",hub);
                SinglePlayerInventoryController inventory=null;foreach(var root in scene.GetRootGameObjects())foreach(var ui in root.GetComponentsInChildren<SinglePlayerInventoryController>(true))if(new SerializedObject(ui).FindProperty("_backpackWorkspace").objectReferenceValue!=null)inventory=ui;
                if(inventory==null)throw new InvalidOperationException("实验区缺少背包工作区。");
                var controller=session.GetComponent<WandResearchController>();if(controller==null)controller=session.gameObject.AddComponent<WandResearchController>();
                Set(controller,"_session",session);Set(controller,"_hub",hub);Set(controller,"_pause",pause);Set(controller,"_inventory",inventory);
                foreach(var root in scene.GetRootGameObjects())foreach(var list in root.GetComponentsInChildren<NearbyPickupListView>(true))Set(list,"_hub",hub);
                AssetDatabase.SaveAssets();EditorSceneManager.MarkSceneDirty(scene);if(!EditorSceneManager.SaveScene(scene))throw new IOException("实验区保存失败。");
                File.WriteAllText("Temp/Spells/home-research-manifest.txt","Scene="+"Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Home.unity\\nCenter="+center+"\nSize="+size+"\nF input and existing pickup prompt; no Warehouse/Shop; original UI retained.");
                const string workspacePath="Assets/_Project/Prefabs/SinglePlayer/PracticeInventoryWorkspace.prefab";
                string prefabBackup=backup+"/PracticeInventoryWorkspace.prefab";
                if(!File.Exists(prefabBackup)){File.Copy(workspacePath,prefabBackup);File.Copy("Assets/_Project/Prefabs/SinglePlayer/PracticeInventoryWorkspace.prefab.meta",prefabBackup+".meta");}
                // 直接从P7/P8 Editor Play没有Hub Transfer，默认也须使用与研究台一致的解释器。
                var workspace=PrefabUtility.LoadPrefabContents(workspacePath);
                try{var sessionData=new SerializedObject(workspace.GetComponentInChildren<SinglePlayerInventorySession>(true));sessionData.FindProperty("_useSpellProgrammingV2").boolValue=true;sessionData.ApplyModifiedPropertiesWithoutUndo();PrefabUtility.SaveAsPrefabAsset(workspace,workspacePath);}
                finally{PrefabUtility.UnloadPrefabContents(workspace);}
            }
            finally{if(!string.IsNullOrEmpty(previous))EditorSceneManager.OpenScene(previous,OpenSceneMode.Single);}
        }
        private static void InspectHub()
        {
            var scene=EditorSceneManager.OpenPreviewScene(HubPath);
            try
            {
                var report=new Inspection();
                foreach(var root in scene.GetRootGameObjects())foreach(var node in root.GetComponentsInChildren<Transform>(true))
                {
                    string name=node.name.ToLowerInvariant();
                    if(name.Contains("table")||name.Contains("cauldron")||name.Contains("alchemy")||name.Contains("bench")
                        ||name.Contains("house")||name.Contains("hut")||name.Contains("building")||name.Contains("wizard")||name.Contains("camera"))
                        report.Entries.Add(new Entry{Name=node.name,Parent=node.parent!=null?node.parent.name:"",Position=node.position});
                }
                Directory.CreateDirectory("Temp/Spells");File.WriteAllText("Temp/Spells/hub-inspection.json",JsonUtility.ToJson(report,true));
                var cameraObject=EditorUtility.CreateGameObjectWithHideFlags("V2 hub inspection camera",HideFlags.HideAndDontSave,typeof(Camera));
                SceneManager.MoveGameObjectToScene(cameraObject,scene);
                var camera=cameraObject.GetComponent<Camera>();camera.scene=scene;camera.orthographic=true;camera.orthographicSize=32;
                camera.transform.SetPositionAndRotation(new Vector3(-4,70,4),Quaternion.Euler(90,0,0));
                var target=RenderTexture.GetTemporary(1400,1000,24);var previous=RenderTexture.active;
                var texture=new Texture2D(1400,1000,TextureFormat.RGB24,false);
                try
                {
                    camera.targetTexture=target;camera.Render();RenderTexture.active=target;texture.ReadPixels(new Rect(0,0,1400,1000),0,0);texture.Apply();
                    File.WriteAllBytes("Temp/Spells/hub-top.png",texture.EncodeToPNG());
                }
                finally{camera.targetTexture=null;RenderTexture.active=previous;RenderTexture.ReleaseTemporary(target);UnityEngine.Object.DestroyImmediate(texture);UnityEngine.Object.DestroyImmediate(cameraObject);}
            }
            finally{EditorSceneManager.ClosePreviewScene(scene);}
        }
    }
}
