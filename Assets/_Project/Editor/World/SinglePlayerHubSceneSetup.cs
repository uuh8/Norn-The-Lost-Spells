using System;
using System.Collections.Generic;
using System.IO;
using Game.Character;
using Game.Core;
using Game.ElementField;
using Game.Run;
using Unity.Cinemachine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Game.EditorTools
{
    /// <summary>在新Scene内复用M5接线并装入用户选定环境；不保存当前Scene，也不更改旧测试资产。</summary>
    [InitializeOnLoad]
    internal static class SinglePlayerHubSceneSetup
    {
        internal const string Path = "Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity";
        private const string Fixture = "Assets/_Project/Scenes/Tests/HubSandbox.unity";
        private const string Root = "Assets/_Project/ScriptableObjects/World/Hub";
        private const string Evidence = "TestResults/SinglePlayer/M6-T1-Hub";
        private const string Request = "Temp/SinglePlayer/create-formal-hub.request";
        private static double _nextPoll, _monitorEnd;
        private static int _monitorFrames, _cameraErrors, _grabErrors;
        private static bool _monitor;
        private static readonly List<string> NewErrors = new List<string>();

        static SinglePlayerHubSceneSetup() { EditorApplication.update += Poll; }
        private static void Poll()
        {
            if (_monitor) { Monitor(); return; }
            if (EditorApplication.timeSinceStartup < _nextPoll || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            _nextPoll = EditorApplication.timeSinceStartup + .25;
            if (!File.Exists(Request)) return; string command = File.ReadAllText(Request).Trim(); File.Delete(Request);
            try
            {
                if (EditorUtility.scriptCompilationFailed || EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("编译失败或Play期间不装配。");
                if (command == "create") Create();
                else if (command == "audit") Write("current-audit.json", SinglePlayerSceneAudit.Inspect(SceneManager.GetActiveScene()));
                else if (command == "monitor") BeginMonitor();
                else if (command == "inspect-layout") InspectLayout();
                else if (command == "repair-layout") RepairLayout();
                else throw new InvalidOperationException("未知正式据点命令。");
            }
            catch (Exception exception) { Write("setup-error.json", new Failure { Error = exception.ToString() }); GameLog.Error(exception.ToString(), "HubAuthoring"); }
        }

        internal static void Create()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++) if (SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("用户Scene未保存，未保存或丢弃。");
            if (File.Exists(Path) || AssetDatabase.IsValidFolder(Root)) throw new InvalidOperationException("正式据点或部分配置已存在，拒绝覆盖。");
            Folder(Root); var copies = CloneFixtureAssets();
            if (!AssetDatabase.CopyAsset(BeffioHubCompatibility.Candidate, Path)) throw new IOException("据点环境复制失败。");
            var original = SceneManager.GetActiveScene(); var scene = EditorSceneManager.OpenScene(Path, OpenSceneMode.Additive); Scene fixture = default;
            try
            {
                SceneManager.SetActiveScene(scene);
                foreach (var root in scene.GetRootGameObjects()) if (root.name == "Environment Review Camera") Object.DestroyImmediate(root);
                fixture = EditorSceneManager.OpenScene(Fixture, OpenSceneMode.Additive);
                var run = Find<SinglePlayerRunCoordinator>(fixture); var world = Find<ElementWorldRuntime>(fixture);
                var obsolete = new HashSet<GameObject>(); var source = new SerializedObject(run).FindProperty("_hubEnvironment");
                for (int i = 0; i < source.arraySize; i++)
                { var go = source.GetArrayElementAtIndex(i).objectReferenceValue as GameObject; if (go != null && go != world.transform.root.gameObject) obsolete.Add(go.transform.root.gameObject); }
                foreach (var root in fixture.GetRootGameObjects()) if (!obsolete.Contains(root)) SceneManager.MoveGameObjectToScene(root, scene);
                SceneManager.SetActiveScene(scene);
                foreach (var root in scene.GetRootGameObjects()) RemapTree(root, copies);
                var session = Find<SinglePlayerInventorySession>(scene); SetBool(session, "_sandboxProfile", false); SetBool(session, "_startSandboxRaid", false);
                ConfigureLayout(scene);
                // 原调用链负责世界唯一写入口、出发停用和返回恢复；这里只把真实美术根对象接进原数组。
                if (!EditorSceneManager.SaveScene(scene,Path)) throw new IOException("正式据点保存失败。");
                Write("created-audit.json",SinglePlayerSceneAudit.Inspect(scene));
                Write("asset-copies.json",new AssetCopies{Paths=CopyPaths(copies)});
            }
            finally
            {
                if (fixture.IsValid() && fixture.isLoaded) EditorSceneManager.CloseScene(fixture,true);
                EditorSceneManager.CloseScene(scene,true); if(original.IsValid() && original.isLoaded)SceneManager.SetActiveScene(original);
            }
        }

        private static void RepairLayout()
        {
            for(int i=0;i<SceneManager.sceneCount;i++)if(SceneManager.GetSceneAt(i).isDirty)throw new InvalidOperationException("当前Scene未保存，不自动保存或丢弃。");
            if(SceneManager.GetSceneByPath(Path).isLoaded)throw new InvalidOperationException("正式据点正在用户Editor中打开，拒绝自动覆盖。");
            var original=SceneManager.GetActiveScene();var scene=EditorSceneManager.OpenScene(Path,OpenSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene);ConfigureLayout(scene);
                if(!EditorSceneManager.SaveScene(scene,Path))throw new IOException("正式据点布局保存失败。");
                Write("layout-audit.json",SinglePlayerSceneAudit.Inspect(scene));
            }
            finally{EditorSceneManager.CloseScene(scene,true);if(original.IsValid() && original.isLoaded)SceneManager.SetActiveScene(original);}
        }

        private static void ConfigureLayout(Scene scene)
        {
            var player=Find<WizardController>(scene);var definition=Find<HubInteractionController>(scene).Definition;
            var map=FindRoot(scene,"Map_1");var ground=map.transform.Find("Ground_Map_1").GetComponent<MeshCollider>();
            player.transform.position=GroundPoint(ground,new Vector3(-4,0,4))+Vector3.up*.15f;player.transform.rotation=Quaternion.identity;
            definition.Stations=new[]
            {
                new HubDefinition.Station{Kind=HubStationKind.Warehouse,Position=GroundPoint(ground,new Vector3(-4,0,1))+Vector3.up*.15f,DisplayName="仓库与魔法整备"},
                new HubDefinition.Station{Kind=HubStationKind.Shop,Position=GroundPoint(ground,new Vector3(-4,0,8))+Vector3.up*.15f,DisplayName="据点商店"},
                new HubDefinition.Station{Kind=HubStationKind.Expedition,Position=GroundPoint(ground,new Vector3(-4,0,15))+Vector3.up*.15f,DisplayName="远征入口"}
            };
            // 快照后删除，避免销毁迭代器当前所在父物件导致漏查；支持已整理的据点层级。
            var obsoleteMarkers=new List<GameObject>();
            foreach(var node in SinglePlayerHubHierarchySetup.All(scene))if(node.name=="实验平台 · 真实库存" || node.name=="实验区提示" || node.name.EndsWith(" · E",StringComparison.Ordinal))obsoleteMarkers.Add(node.gameObject);
            foreach(var marker in obsoleteMarkers)Object.DestroyImmediate(marker);
            Vector3 center=GroundPoint(ground,new Vector3(-20,0,-25));float surface=.5f;
            const string materialPath="Assets/_Project/Art/SinglePlayer/Hub/ExperimentPlatform.mat";
            var material=AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if(material==null){material=new Material(Shader.Find("Universal Render Pipeline/Lit"));material.SetColor("_BaseColor",new Color(.1f,.24f,.25f));Folder("Assets/_Project/Art/SinglePlayer/Hub");AssetDatabase.CreateAsset(material,materialPath);}
            var platform=GameObject.CreatePrimitive(PrimitiveType.Cube);platform.name="实验平台 · 真实库存";
            platform.transform.position=new Vector3(center.x,surface-.15f,center.z);platform.transform.localScale=new Vector3(8,.3f,8);platform.GetComponent<Renderer>().sharedMaterial=material;
            // 表面与0.5m材料格边界对齐，静态固体不占满上方液体格；可见台阶允许原CharacterController步行进入。
            for(int i=0;i<2;i++)
            {var step=GameObject.CreatePrimitive(PrimitiveType.Cube);step.name="实验平台台阶";step.transform.SetParent(platform.transform,true);float height=i==0?.4f:.2f;
                step.transform.position=new Vector3(center.x,height*.5f,center.z+4.25f+i*.5f);step.transform.localScale=new Vector3(.375f,height/.3f,.0625f);step.GetComponent<Renderer>().sharedMaterial=material;}
            definition.ExperimentCenter=new Vector3(center.x,surface+2.9f,center.z);definition.ExperimentSize=new Vector3(8,6,8);EditorUtility.SetDirty(definition);AssetDatabase.SaveAssetIfDirty(definition);
            var environment=new List<GameObject>{map,FindRoot(scene,"Lights"),FindRoot(scene,"ReflectionProbes"),Find<ElementWorldRuntime>(scene).transform.root.gameObject,platform};
            foreach(var station in definition.Stations)
            {
                var marker=new GameObject(station.DisplayName+" · E");marker.transform.position=station.Position;environment.Add(marker);
                var label=new GameObject("交互提示",typeof(TextMesh));label.transform.SetParent(marker.transform,false);label.transform.localPosition=Vector3.up*2.2f;
                var text=label.GetComponent<TextMesh>();text.text=station.DisplayName+" [E]";text.characterSize=.1f;text.anchor=TextAnchor.MiddleCenter;text.color=new Color(.95f,.85f,.55f);
                label.AddComponent<Game.UI.WorldSpaceLabelBillboard>();
            }
            var experimentLabel=new GameObject("实验区提示",typeof(TextMesh));experimentLabel.transform.position=center+Vector3.up*1.2f;
            var description=experimentLabel.GetComponent<TextMesh>();description.text="实验区 · 使用真实库存";description.characterSize=.12f;description.anchor=TextAnchor.MiddleCenter;environment.Add(experimentLabel);
            experimentLabel.AddComponent<Game.UI.WorldSpaceLabelBillboard>();
            var data=new SerializedObject(Find<SinglePlayerRunCoordinator>(scene));var array=data.FindProperty("_hubEnvironment");array.arraySize=environment.Count;
            for(int i=0;i<environment.Count;i++)array.GetArrayElementAtIndex(i).objectReferenceValue=environment[i];data.ApplyModifiedPropertiesWithoutUndo();
            // 地图只决定空间位置；最后统一P7的3C与用户确认站点，防止旧灰盒坐标覆盖正式布局。
            SinglePlayerHubP7Alignment.Apply(scene);
            SinglePlayerHubSpatialSetup.Apply(scene,false);
            SinglePlayerHubFeedbackSetup.Apply(scene,false);
            SinglePlayerHubHierarchySetup.Apply(scene,false);
        }

        private static Dictionary<Object,Object> CloneFixtureAssets()
        {
            var copies = new Dictionary<Object,Object>(); var destinations = new List<string>();
            foreach (string path in AssetDatabase.GetDependencies(Fixture,true))
            {
                string destination;
                const string assets="Assets/_Project/Tests/Fixtures/Run/", prefabs="Assets/_Project/Prefabs/Tests/";
                if(path.StartsWith(assets,StringComparison.Ordinal))destination="Assets/_Project/ScriptableObjects/World/Hub/"+path.Substring(assets.Length);
                else if(path.StartsWith(prefabs,StringComparison.Ordinal))destination="Assets/_Project/Prefabs/SinglePlayer/Hub/"+path.Substring(prefabs.Length);
                else continue;
                Folder(destination.Substring(0,destination.LastIndexOf('/')));
                if(File.Exists(destination) || !AssetDatabase.CopyAsset(path,destination))throw new IOException("拒绝覆盖或无法复制："+destination);
                destinations.Add(destination); var targets=new Dictionary<long,Object>();
                foreach(var asset in AssetDatabase.LoadAllAssetsAtPath(destination))if(asset!=null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset,out string _,out long id))targets[id]=asset;
                foreach(var asset in AssetDatabase.LoadAllAssetsAtPath(path))if(asset!=null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset,out string _,out long id) && targets.TryGetValue(id,out var target))copies[asset]=target;
            }
            foreach(string destination in destinations)
            {
                if(destination.EndsWith(".prefab",StringComparison.Ordinal))
                {
                    var prefab=PrefabUtility.LoadPrefabContents(destination);
                    try{RemapTree(prefab,copies);PrefabUtility.SaveAsPrefabAsset(prefab,destination);}finally{PrefabUtility.UnloadPrefabContents(prefab);}
                }
                else foreach(var asset in AssetDatabase.LoadAllAssetsAtPath(destination))if(asset!=null){Remap(asset,copies);AssetDatabase.SaveAssetIfDirty(asset);}
            }
            return copies;
        }

        private static void InspectLayout()
        {
            var original=SceneManager.GetActiveScene();var scene=EditorSceneManager.OpenScene(Path,OpenSceneMode.Additive);
            try
            {
                Physics.SyncTransforms();var ground=FindRoot(scene,"Map_1").transform.Find("Ground_Map_1").GetComponent<MeshCollider>();
                var platform=FindRoot(scene,"实验平台 · 真实库存").transform;
                var report=new LayoutReport();
                // 只读空间探测必须过滤Scene，防止同时打开的候选地图产生假阳性。
                for(int z=-30;z<=10;z+=5)for(int x=-25;x<=10;x+=5)
                {
                    var point=GroundPoint(ground,new Vector3(x,0,z));var blockers=new List<string>();
                    foreach(var collider in Physics.OverlapBox(point+Vector3.up*1.5f,new Vector3(3.8f,1.1f,3.8f)))
                        if(collider.gameObject.scene==scene && collider!=ground && !(collider is CharacterController) && !collider.transform.IsChildOf(platform))
                            blockers.Add(collider.name+" "+collider.bounds);
                    report.Points.Add(point+" => "+string.Join("; ",blockers));
                }
                Write("layout-probes.json",report);
            }
            finally{EditorSceneManager.CloseScene(scene,true);if(original.IsValid() && original.isLoaded)SceneManager.SetActiveScene(original);}
        }
        private static void RemapTree(GameObject root,Dictionary<Object,Object> copies)
        {foreach(var transform in root.GetComponentsInChildren<Transform>(true)){Remap(transform.gameObject,copies);foreach(var component in transform.GetComponents<Component>())if(component!=null)Remap(component,copies);}}
        private static void Remap(Object target,Dictionary<Object,Object> copies)
        {
            var data=new SerializedObject(target);var property=data.GetIterator();bool changed=false;
            // Next而非NextVisible：InputActionReference等隐藏序列化引用也需要指向正式副本。
            while(property.Next(true))if(property.propertyType==SerializedPropertyType.ObjectReference && property.objectReferenceValue!=null && copies.TryGetValue(property.objectReferenceValue,out var copy))
            {property.objectReferenceValue=copy;changed=true;}if(changed)data.ApplyModifiedPropertiesWithoutUndo();
        }
        private static List<string> CopyPaths(Dictionary<Object,Object> copies)
        {var result=new List<string>();foreach(var pair in copies)if(AssetDatabase.IsMainAsset(pair.Key))result.Add(AssetDatabase.GetAssetPath(pair.Key)+" -> "+AssetDatabase.GetAssetPath(pair.Value));return result;}
        private static Vector3 GroundPoint(MeshCollider ground,Vector3 position)
        {if(!ground.Raycast(new Ray(new Vector3(position.x,30,position.z),Vector3.down),out var hit,60))throw new InvalidOperationException("指定点没有真实地面："+position);return hit.point;}
        private static void Folder(string path){if(AssetDatabase.IsValidFolder(path))return;int split=path.LastIndexOf('/');Folder(path.Substring(0,split));AssetDatabase.CreateFolder(path.Substring(0,split),path.Substring(split+1));}
        private static T Find<T>(Scene scene)where T:Component{foreach(var root in scene.GetRootGameObjects()){var component=root.GetComponentInChildren<T>(true);if(component!=null)return component;}throw new InvalidOperationException(typeof(T).Name+"缺失。");}
        private static GameObject FindRoot(Scene scene,string name)=>SinglePlayerHubHierarchySetup.FindNamed(scene,name)??throw new InvalidOperationException(name+"对象缺失。");
        private static void SetBool(Object target,string field,bool value){var data=new SerializedObject(target);data.FindProperty(field).boolValue=value;data.ApplyModifiedPropertiesWithoutUndo();}
        private static void Write(string name,object value){Directory.CreateDirectory(Evidence);File.WriteAllText(Evidence+"/"+name,JsonUtility.ToJson(value,true));}

        private static void BeginMonitor()
        {
            _monitor=true;_monitorEnd=EditorApplication.timeSinceStartup+10;_monitorFrames=_cameraErrors=_grabErrors=0;NewErrors.Clear();
            Application.logMessageReceived+=OnLog;
        }
        private static void OnLog(string message,string stack,LogType kind)
        {
            if(message.Contains("UniversalCameraData has already"))_cameraErrors++;
            if(message.Contains("ApplyGrabPassMainThread"))_grabErrors++;
            if(kind==LogType.Error || kind==LogType.Exception || kind==LogType.Assert)NewErrors.Add(message+"\n"+stack);
        }
        private static void Monitor()
        {
            _monitorFrames++;SceneView.RepaintAll();EditorApplication.QueuePlayerLoopUpdate();
            if(EditorApplication.timeSinceStartup<_monitorEnd)return;
            Application.logMessageReceived-=OnLog;_monitor=false;
            Write("editor-render-monitor.json",new MonitorReport{Utc=DateTime.UtcNow.ToString("O"),Scene=SceneManager.GetActiveScene().path,Frames=_monitorFrames,Seconds=10,CameraErrors=_cameraErrors,GrabPassErrors=_grabErrors,Errors=new List<string>(NewErrors),Audit=SinglePlayerSceneAudit.Inspect(SceneManager.GetActiveScene(),false)});
        }
        [Serializable]private sealed class Failure{public string Error;}
        [Serializable]private sealed class LayoutReport{public List<string> Points=new List<string>();}
        [Serializable]private sealed class AssetCopies{public List<string> Paths;}
        [Serializable]private sealed class MonitorReport{public string Utc,Scene;public int Frames,Seconds,CameraErrors,GrabPassErrors;public List<string> Errors;public SinglePlayerSceneAudit.Report Audit;}
    }
}
