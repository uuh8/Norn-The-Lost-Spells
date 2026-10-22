using System;
using System.IO;
using System.Collections.Generic;
using Game.Run;
using Game.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Game.EditorTools
{
    /// <summary>本轮修正的独立审阅Scene；当前用户Scene只通过Undo修改，不自动保存。</summary>
    [InitializeOnLoad]
    internal static class SinglePlayerHubSpatialSetup
    {
        internal const string Review = "Assets/_Project/Scenes/Tests/Hub/SinglePlayer_Hub_LayoutReview.unity";
        private const string DefinitionPath = "Assets/_Project/ScriptableObjects/World/Hub/HubSpatialLayout.asset";
        private const string Request = "Temp/SinglePlayer/hub-spatial.request";
        private const string Evidence = "TestResults/SinglePlayer/M6-T1-Spatial";
        static SinglePlayerHubSpatialSetup() { EditorApplication.update += Poll; }
        private static void Poll()
        {
            if (!File.Exists(Request) || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
            string command = File.ReadAllText(Request).Trim(); File.Delete(Request);
            try
            {
                if (EditorUtility.scriptCompilationFailed) throw new InvalidOperationException("编译失败，不能使用旧工具。");
                if (command == "prepare") Prepare(false);
                else if (command == "repair") Prepare(true);
                else if (command == "apply-current") ApplyCurrent();
                else if (command == "clear-review") SessionState.EraseString("Norn.HubAlignment.ReviewPath");
                else throw new InvalidOperationException("未知布局命令。");
            }
            catch (Exception e) { Directory.CreateDirectory(Evidence); File.WriteAllText(Evidence+"/setup-error.txt",e.ToString()); Game.Core.GameLog.Error(e.ToString(),"HubAuthoring"); }
        }
        private static void Prepare(bool repair)
        {
            for (int i=0;i<SceneManager.sceneCount;i++) if(SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("未保存用户Scene，拒绝自动处理。");
            if(SceneManager.GetSceneByPath(Review).isLoaded)throw new InvalidOperationException("审阅Scene由用户打开，拒绝保存。");
            if(!repair && File.Exists(Review))throw new InvalidOperationException("已有审阅Scene，拒绝覆盖。");
            Directory.CreateDirectory(Evidence);
            if(!repair && !AssetDatabase.CopyAsset(SinglePlayerHubSceneSetup.Path,Review))throw new IOException("创建审阅副本失败。");
            Scene original=SceneManager.GetActiveScene(), scene=EditorSceneManager.OpenScene(Review,OpenSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene);Apply(scene,false);
                if(!EditorSceneManager.SaveScene(scene,Review))throw new IOException("审阅副本保存失败。");
                SessionState.SetString("Norn.HubAlignment.ReviewPath",Review);
                File.WriteAllText(Evidence+"/prepared.json",JsonUtility.ToJson(new Report{Scene=Review,Dirty=scene.isDirty},true));
            }
            finally{EditorSceneManager.CloseScene(scene,true);if(original.IsValid() && original.isLoaded)SceneManager.SetActiveScene(original);}
        }
        internal static void Apply(Scene scene,bool undo)
        {
            var hub=Find<HubInteractionController>(scene);var definition=AssetDatabase.LoadAssetAtPath<HubDefinition>(DefinitionPath);
            if(definition==null){definition=Object.Instantiate(hub.Definition);definition.name="HubSpatialLayout";AssetDatabase.CreateAsset(definition,DefinitionPath);}
            var map=Root(scene,"Map_1");var ground=map.transform.Find("Ground_Map_1").GetComponent<MeshCollider>();
            // 仅本轮布局SO改变，不修改历史审阅副本共同引用的Hub.asset。
            for(int i=0;i<definition.Stations.Length;i++)
            {
                var station=definition.Stations[i];if(station.Kind!=HubStationKind.Expedition)continue;
                station.Position=Ground(ground,new Vector3(-35,0,-40))+Vector3.up*.15f;definition.Stations[i]=station;
            }
            Vector3 center=Ground(ground,new Vector3(-31.5f,0,9.5f));
            // 0.5m格边界继续沿用，平台移动保持真实材料查询与静态阻挡关系。
            float surface=Mathf.Ceil(center.y*2)*.5f+.5f;
            definition.ExperimentCenter=new Vector3(center.x,surface+2.9f,center.z);definition.ExperimentSize=new Vector3(4,6,4);
            EditorUtility.SetDirty(definition);AssetDatabase.SaveAssetIfDirty(definition);
            Set(hub,"_definition",definition,undo);Set(Find<ExperimentAreaController>(scene),"_definition",definition,undo);
            var platform=Root(scene,"实验平台 · 真实库存");Record(platform.transform,undo);platform.transform.position=new Vector3(center.x,surface-.15f,center.z);
            // 实际Tent_A覆盖北侧、Stone_10覆盖西侧；用现有空地4m平台和东向台阶，保留全部地图碰撞。
            platform.transform.localScale=new Vector3(4,.3f,4);platform.transform.rotation=Quaternion.Euler(0,90,0);
            for(int i=0;i<platform.transform.childCount;i++)
            {var step=platform.transform.GetChild(i);if(step.name!="实验平台台阶")continue;Record(step,undo);
                float height=i==0?.4f:.2f;step.localPosition=new Vector3(0,(height*.5f-(surface-.15f))/.3f,i==0?.5625f:.6875f);step.localScale=new Vector3(.375f,height/.3f,.125f);}
            var experiment=Root(scene,"实验区提示");Record(experiment.transform,undo);experiment.transform.position=center+Vector3.up*3;
            foreach(var node in SinglePlayerHubHierarchySetup.All(scene))
            {
                var root=node.gameObject;
                if(root.name.EndsWith(" · E") || root.name=="实验区提示")
                {
                    foreach(var station in definition.Stations)if(root.name==station.DisplayName+" · E"){Record(root.transform,undo);root.transform.position=station.Position;}
                    foreach(var label in root.GetComponentsInChildren<TextMesh>(true))Add<WorldSpaceLabelBillboard>(label.gameObject,undo);
                }
            }
            foreach(var root in scene.GetRootGameObjects())foreach(var label in root.GetComponentsInChildren<UnityEngine.UI.Text>(true))
            {
                float y; float height;
                if(label.name=="Prompt"){y=-480;height=84;}
                else if(label.name=="ExtractionCondition"){y=-580;height=60;}
                else if(label.name=="ExtractionTimer"){y=-646;height=64;}
                else continue;
                Record(label,undo);Record(label.rectTransform,undo);
                var rect=label.rectTransform;rect.anchorMin=rect.anchorMax=rect.pivot=new Vector2(0,1);rect.anchoredPosition=new Vector2(24,y);rect.sizeDelta=new Vector2(390,height);
                label.alignment=TextAnchor.UpperLeft;label.horizontalOverflow=HorizontalWrapMode.Wrap;
            }
            var run=Find<SinglePlayerRunCoordinator>(scene);var session=Find<SinglePlayerInventorySession>(scene);
            var actor=(Game.Combat.HealthComponent)new SerializedObject(run).FindProperty("_player").objectReferenceValue;
            var extra=new List<GameObject>();
            foreach(var station in definition.Stations)if(station.Kind==HubStationKind.Expedition)
            {
                var marker=Root(scene,station.DisplayName+" · E");
                BuildTrigger(marker,undo);BuildPortalVisual(marker,undo);var portal=Add<HubExpeditionPortal>(marker,undo);Set(portal,"_session",session,undo);Set(portal,"_player",actor.transform,undo);
            }
            foreach(var point in new[]{new Vector3(-30,0,40),new Vector3(8,0,40)})
            {
                string name=point.x<0?"远征点 · P7":"远征点 · P8";var marker=OptionalRoot(scene,name);
                if(marker==null){marker=new GameObject(name);if(undo)Undo.RegisterCreatedObjectUndo(marker,"布置据点分区");SceneManager.MoveGameObjectToScene(marker,scene);}
                Record(marker.transform,undo);marker.transform.position=Ground(ground,point)+Vector3.up*.15f;
                BuildPortalVisual(marker,undo);extra.Add(marker);
            }
            Record(run,undo);var data=new SerializedObject(run);var array=data.FindProperty("_hubEnvironment");
            foreach(var marker in extra){bool exists=false;for(int i=0;i<array.arraySize;i++)if(array.GetArrayElementAtIndex(i).objectReferenceValue==marker)exists=true;
                if(!exists){int index=array.arraySize++;array.GetArrayElementAtIndex(index).objectReferenceValue=marker;}}
            data.ApplyModifiedPropertiesWithoutUndo();
        }
        private static void BuildTrigger(GameObject go,bool undo)
        {var collider=Add<SphereCollider>(go,undo);Record(collider,undo);collider.isTrigger=true;collider.radius=1.3f;collider.center=Vector3.up*.8f;
            var body=Add<Rigidbody>(go,undo);Record(body,undo);body.isKinematic=true;body.useGravity=false;}
        private static void BuildPortalVisual(GameObject root,bool undo)
        {
            SinglePlayerUnifiedItemsSetup.InstallPortalVisual(root,undo);
        }
        private static void ApplyCurrent()
        {
            var scene=SceneManager.GetActiveScene();if(scene.path!=SinglePlayerHubSceneSetup.Path || scene.isDirty)throw new InvalidOperationException("只对干净的正式当前Scene应用。");
            Undo.IncrementCurrentGroup();int group=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("据点分区与文字修正");Apply(scene,true);Undo.CollapseUndoOperations(group);EditorSceneManager.MarkSceneDirty(scene);
            SessionState.EraseString("Norn.HubAlignment.ReviewPath");File.WriteAllText(Evidence+"/current-applied.json",JsonUtility.ToJson(new Report{Scene=scene.path,Dirty=scene.isDirty},true));
        }
        private static Vector3 Ground(MeshCollider ground,Vector3 point)
        {if(!ground.Raycast(new Ray(point+Vector3.up*30,Vector3.down),out var hit,60))throw new InvalidOperationException("新区域缺少实际地面："+point);return hit.point;}
        private static void Record(Object target,bool undo){if(undo)Undo.RecordObject(target,"据点分区与文字修正");}
        private static T Add<T>(GameObject go,bool undo)where T:Component{var value=go.GetComponent<T>();return value!=null?value:undo?Undo.AddComponent<T>(go):go.AddComponent<T>();}
        private static void Set(Object target,string field,Object value,bool undo){Record(target,undo);var data=new SerializedObject(target);data.FindProperty(field).objectReferenceValue=value;data.ApplyModifiedPropertiesWithoutUndo();}
        private static T Find<T>(Scene scene)where T:Component{foreach(var root in scene.GetRootGameObjects()){var value=root.GetComponentInChildren<T>(true);if(value!=null)return value;}throw new InvalidOperationException(typeof(T).Name+"缺失");}
        private static GameObject OptionalRoot(Scene scene,string name)=>SinglePlayerHubHierarchySetup.FindNamed(scene,name);
        private static GameObject Root(Scene scene,string name)=>OptionalRoot(scene,name)??throw new InvalidOperationException(name+"缺失");
        [Serializable] private sealed class Report{public string Scene;public bool Dirty;}
    }
}
