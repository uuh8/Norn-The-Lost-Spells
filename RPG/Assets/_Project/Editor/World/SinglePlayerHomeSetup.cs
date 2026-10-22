using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Game.Core;
using Game.Character;
using Game.Run;
using Game.UI;
using UnityEditor.SceneManagement;
using Object=UnityEngine.Object;

namespace Game.EditorTools
{
    /// <summary>显式请求驱动的场景审查；无定时场景写入，无自动保存正式据点。</summary>
    [InitializeOnLoad] internal static class SinglePlayerHomeSetup
    {
        internal const string Home="Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Home.unity";
        internal const string Review="Assets/_Project/Scenes/Tests/Hub/SinglePlayer_Hub_HomeReview.unity";
        internal const string Evidence="TestResults/SinglePlayer/M6-T22-HomeGround";
        private const string Request="Temp/SinglePlayer/home-setup.request";
        static SinglePlayerHomeSetup(){EditorApplication.update+=Poll;}
        private static void Poll()
        {
            if(!File.Exists(Request)||EditorApplication.isCompiling||EditorApplication.isUpdating||EditorApplication.isPlayingOrWillChangePlaymode)return;
            string command=File.ReadAllText(Request).Trim();File.Delete(Request);
            try
            {
                if(EditorUtility.scriptCompilationFailed)throw new InvalidOperationException("编译失败，不使用旧Assembly");
                if(command=="inspect")Inspect(SceneManager.GetActiveScene());
                else if(command=="prepare")Prepare();
                else if(command=="apply-current")ApplyCurrent();
                else throw new InvalidOperationException("未知命令："+command);
            }
            catch(Exception e){Write("setup-error.txt",e.ToString());GameLog.Error(e.ToString(),"HomeSetup");}
        }
        private static void Inspect(Scene scene)
        {
            var text=new StringBuilder();text.AppendLine(scene.path+" Dirty="+scene.isDirty);
            foreach(var root in scene.GetRootGameObjects())
            {
                foreach(var ground in root.GetComponentsInChildren<GroundChecker>(true))
                {
                    var data=new SerializedObject(ground);var cc=ground.GetComponent<CharacterController>();
                    text.AppendLine("GroundChecker "+Path(ground.transform)+" mask="+data.FindProperty("_groundLayer").intValue+" radius="+data.FindProperty("_sphereRadius").floatValue
                        +" cc center="+cc.center+" height="+cc.height+" radius="+cc.radius+" skin="+cc.skinWidth+" position="+cc.transform.position);
                }
                foreach(var collider in root.GetComponentsInChildren<Collider>(true))
                    if(collider.name.Contains("Fence",StringComparison.OrdinalIgnoreCase)||collider.name.Contains("Building_14",StringComparison.OrdinalIgnoreCase))
                        text.AppendLine("Collider "+Path(collider.transform)+" layer="+collider.gameObject.layer+" bounds="+collider.bounds);
            }
            foreach(var root in scene.GetRootGameObjects())foreach(var t in root.GetComponentsInChildren<Transform>(true))
            {
                if(t.parent!=null && !t.name.Contains("House",StringComparison.OrdinalIgnoreCase)&&!t.name.Contains("Building",StringComparison.OrdinalIgnoreCase)
                    &&!t.name.Contains("门")&&!t.name.Contains("实验")&&!t.name.Contains("Ground")&&!t.name.Contains("Portal"))continue;
                text.AppendLine(Path(t)+" pos="+t.position+" rot="+t.rotation.eulerAngles+" scale="+t.lossyScale);
            }
            Write("scene-inspect.txt",text.ToString());
        }
        private static string Path(Transform t)=>t.parent==null?t.name:Path(t.parent)+"/"+t.name;
        private const string Hub="Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity";
        private static T Find<T>(Scene scene) where T:Component
        {foreach(var root in scene.GetRootGameObjects()){var value=root.GetComponentInChildren<T>(true);if(value!=null)return value;}return null;}
        private static GameObject Named(Scene scene,string name)
        {foreach(var root in scene.GetRootGameObjects())foreach(var t in root.GetComponentsInChildren<Transform>(true))if(t.name==name)return t.gameObject;return null;}
        private static void CleanScenes()
        {for(int i=0;i<SceneManager.sceneCount;i++)if(SceneManager.GetSceneAt(i).isDirty)throw new InvalidOperationException("Scene未保存，不替用户保存或丢弃。");}
        private static void Set(Object target,string field,Object value,bool undo=false)
        {if(undo)Undo.RecordObject(target,"家场景装配");var data=new SerializedObject(target);data.FindProperty(field).objectReferenceValue=value;data.ApplyModifiedPropertiesWithoutUndo();}
        private static void Flag(Object target,string field,bool value,bool undo=false)
        {if(undo)Undo.RecordObject(target,"场景权限");var data=new SerializedObject(target);data.FindProperty(field).boolValue=value;data.ApplyModifiedPropertiesWithoutUndo();}
        private static void Remove(Object value,bool undo)
        {if(value==null)return;if(undo)Undo.DestroyObjectImmediate(value);else Object.DestroyImmediate(value);}
        private static void Prepare()
        {
            CleanScenes();var original=SceneManager.GetActiveScene();
            if(SceneManager.GetSceneByPath(Home).isLoaded||SceneManager.GetSceneByPath(Review).isLoaded)throw new InvalidOperationException("Home/Review已由用户打开，不覆盖作者现场。");
            if(!AssetDatabase.CopyAsset(Hub,Review)&&!System.IO.File.Exists(Review))throw new IOException("复制Hub Review失败");
            var review=EditorSceneManager.OpenScene(Review,OpenSceneMode.Additive);
            try{SceneManager.SetActiveScene(review);ApplyHub(review,false);EditorSceneManager.SaveScene(review);}
            finally{EditorSceneManager.CloseScene(review,true);SceneManager.SetActiveScene(original);}
            // 新作者Scene只创建一次；重复prepare不能覆盖用户后续设计的家布局。
            if(!System.IO.File.Exists(Home))
            {
                if(!AssetDatabase.CopyAsset(Hub,Home))throw new IOException("复制Home失败");
                var home=EditorSceneManager.OpenScene(Home,OpenSceneMode.Additive);
                try{SceneManager.SetActiveScene(home);ApplyHome(home);EditorSceneManager.SaveScene(home);}
                finally{EditorSceneManager.CloseScene(home,true);SceneManager.SetActiveScene(original);}
            }
            else
            {
                // 已生成Home不重建布局，仅修复从作者预览状态继承的禁用相机根。
                var home=EditorSceneManager.OpenScene(Home,OpenSceneMode.Additive);
                try{SceneManager.SetActiveScene(home);EnableCameras(home,false);OrientGold(Find<HomePortal>(home),false);EditorSceneManager.SaveScene(home);}
                finally{EditorSceneManager.CloseScene(home,true);SceneManager.SetActiveScene(original);}
            }
            var list=new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            if(!list.Exists(s=>s.path==Home))list.Add(new EditorBuildSettingsScene(Home,true));EditorBuildSettings.scenes=list.ToArray();
            Write("prepared.txt","Home="+"Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Home.unity\\nReview="+"Assets/_Project/Scenes/Tests/Hub/SinglePlayer_Hub_HomeReview.unity\\n正式据点未保存/未修改；家场景及Build入口已创建。");
        }
        private static void ApplyCurrent()
        {
            CleanScenes();var scene=SceneManager.GetActiveScene();
            if(scene.path!=Hub||SceneManager.sceneCount!=1)throw new InvalidOperationException("只在单一干净正式据点应用。");
            Undo.IncrementCurrentGroup();int group=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("独立家实验场景入口");
            try{ApplyHub(scene,true);Undo.FlushUndoRecordObjects();Undo.CollapseUndoOperations(group);EditorSceneManager.MarkSceneDirty(scene);Write("formal-applied.txt","Undo=true FormalSceneSaved=false Dirty="+scene.isDirty);}
            catch{Undo.RevertAllDownToGroup(group);throw;}
        }
        private static void ApplyHub(Scene scene,bool undo)
        {
            EnableCameras(scene,undo);
            var session=Find<SinglePlayerInventorySession>(scene);var run=Find<SinglePlayerRunCoordinator>(scene);
            // 删除精确功能对象，城镇地图和房屋完全保留。
            foreach(var root in scene.GetRootGameObjects())
            {
                foreach(var component in root.GetComponentsInChildren<HubExperimentWorldLifetime>(true))Remove(component,undo);
                foreach(var component in root.GetComponentsInChildren<ExperimentAreaController>(true))Remove(component,undo);
            }
            Remove(Named(scene,"实验平台 · 真实库存"),undo);Remove(Named(scene,"实验区提示"),undo);
            Remove(Named(scene,"__ElementWorld"),undo);Set(run,"_hubWorld",null,undo);Flag(session,"_allowWorldExperiments",false,undo);
            if(undo)Undo.RecordObject(run,"清理旧实验引用");var data=new SerializedObject(run);var array=data.FindProperty("_hubEnvironment");
            for(int i=array.arraySize-1;i>=0;i--)if(array.GetArrayElementAtIndex(i).objectReferenceValue==null)array.DeleteArrayElementAtIndex(i);
            data.ApplyModifiedPropertiesWithoutUndo();
            if(Find<HomePortal>(scene)!=null){OrientGold(Find<HomePortal>(scene),undo);return;}
            var houses=Named(scene,"Map_1").transform.Find("Houses");var house=houses.Find("Building_14");
            if(house==null)throw new InvalidOperationException("用户家建筑Building_14缺失");
            // Building_14南向门廊，使用建筑本地坐标而非城镇原点，后续作者可独立移动门根。
            Vector3 position=house.TransformPoint(new Vector3(6.8f,0,-3.8f));
            var ground=Named(scene,"Ground_Map_1").GetComponent<Collider>();
            if(ground.Raycast(new Ray(position+Vector3.up*30,Vector3.down),out var hit,60))position.y=hit.point.y;
            // 从门前4m而非屋顶上方探台阶，避免把屋顶误当入口落点；地形仍是最终fallback。
            foreach(var collider in house.GetComponentsInChildren<Collider>(true))
                if(collider.Raycast(new Ray(position+Vector3.up*4,Vector3.down),out var step,4)&&step.normal.y>.5f)position.y=Mathf.Max(position.y,step.point.y);
            var marker=CreatePortal(scene,Named(scene,"__HubInteractions").transform,"HomeEntrance_家门",position,Quaternion.identity,session,false,undo);
            var outside=marker.transform.Find("OutsideArrival");var arrival=outside.position;
            if(ground.Raycast(new Ray(arrival+Vector3.up*30,Vector3.down),out var support,60))arrival.y=support.point.y;
            foreach(var collider in house.GetComponentsInChildren<Collider>(true))
                if(collider.Raycast(new Ray(arrival+Vector3.up*4,Vector3.down),out var step,4)&&step.normal.y>.5f)arrival.y=Mathf.Max(arrival.y,step.point.y);
            outside.position=arrival+Vector3.up*.1f;
            Write("hub-entry.txt","Building="+Path(house)+"\nGate="+position+"\nOutside="+marker.transform.Find("OutsideArrival").position);
        }
        private static void ApplyHome(Scene scene)
        {
            EnableCameras(scene,false);
            var session=Find<SinglePlayerInventorySession>(scene);var run=Find<SinglePlayerRunCoordinator>(scene);var player=Find<WizardController>(scene);
            foreach(var root in scene.GetRootGameObjects())
            {
                foreach(var component in root.GetComponentsInChildren<HubExperimentWorldLifetime>(true))Remove(component,false);
                foreach(var component in root.GetComponentsInChildren<ExperimentAreaController>(true))Remove(component,false);
                foreach(var component in root.GetComponentsInChildren<HubInteractionController>(true))Remove(component,false);
            }
            Remove(Named(scene,"__Level"),false);Remove(Named(scene,"__HubInteractions"),false);
            foreach(var root in scene.GetRootGameObjects())foreach(var ui in root.GetComponentsInChildren<SinglePlayerInventoryController>(true))
            {
                var data=new SerializedObject(ui);
                if(data.FindProperty("_backpackWorkspace").objectReferenceValue==null)Remove(ui.gameObject,false);
            }
            var shop=Find<ShopController>(scene);if(shop!=null)Remove(shop.gameObject,false);
            Flag(session,"_allowWorldExperiments",true);Flag(session,"_allowWarehouse",false);
            var sd=new SerializedObject(session);sd.FindProperty("_worldSceneKey").stringValue="home.map1";sd.FindProperty("_sandboxName").stringValue="HomePreview";sd.ApplyModifiedPropertiesWithoutUndo();
            var platform=GameObject.CreatePrimitive(PrimitiveType.Cube);platform.name="HomeGround_家实验平地";
            platform.transform.position=new Vector3(0,-.25f,0);platform.transform.localScale=new Vector3(40,.5f,40);platform.layer=LayerMask.NameToLayer("Ground");
            const string material="Assets/_Project/Art/SinglePlayer/HomeGround.mat";var mat=AssetDatabase.LoadAssetAtPath<Material>(material);
            if(mat==null){mat=new Material(Shader.Find("Universal Render Pipeline/Lit"));mat.SetColor("_BaseColor",new Color(.28f,.32f,.34f));AssetDatabase.CreateAsset(mat,material);}
            platform.GetComponent<Renderer>().sharedMaterial=mat;
            var light=new GameObject("HomeDirectionalLight").AddComponent<Light>();light.type=LightType.Directional;light.intensity=1.1f;light.transform.rotation=Quaternion.Euler(50,-30,0);light.shadows=LightShadows.Soft;
            RenderSettings.ambientMode=UnityEngine.Rendering.AmbientMode.Flat;RenderSettings.ambientLight=new Color(.45f,.5f,.55f);
            RenderSettings.fog=false;
            bool enabled=player.GetComponent<CharacterController>().enabled;player.GetComponent<CharacterController>().enabled=false;player.transform.SetPositionAndRotation(new Vector3(0,.1f,0),Quaternion.identity);player.GetComponent<CharacterController>().enabled=enabled;
            var group=new GameObject("__HomeInteractions");CreatePortal(scene,group.transform,"HomeExit_返回据点",new Vector3(0,0,-5),Quaternion.Euler(0,180,0),session,true,false);
            var dataRun=new SerializedObject(run);dataRun.FindProperty("_hubEnvironment").arraySize=0;
            dataRun.FindProperty("_hubWorld").objectReferenceValue=Named(scene,"__ElementWorld");dataRun.ApplyModifiedPropertiesWithoutUndo();
            var test=Named(scene,"Test");if(test!=null)Remove(test,false);
        }
        private static void EnableCameras(Scene scene,bool undo)
        {
            var cameras=Named(scene,"__Cameras");if(cameras==null)return;
            if(undo)Undo.RecordObject(cameras,"启用游戏相机");cameras.SetActive(true);
        }
        private static HomePortal CreatePortal(Scene scene,Transform parent,string name,Vector3 position,Quaternion rotation,SinglePlayerInventorySession session,bool returning,bool undo)
        {
            var root=new GameObject(name);SceneManager.MoveGameObjectToScene(root,scene);root.transform.SetParent(parent,false);root.transform.SetPositionAndRotation(position,rotation);
            if(undo)Undo.RegisterCreatedObjectUndo(root,"创建家门");
            var box=root.AddComponent<BoxCollider>();box.isTrigger=true;box.center=new Vector3(0,1.2f,0);box.size=new Vector3(2.4f,2.8f,2.8f);
            var gate=root.AddComponent<HomePortal>();Set(gate,"_session",session);Set(gate,"_player",Find<WizardController>(scene).transform);
            var data=new SerializedObject(gate);data.FindProperty("_targetScene").stringValue=returning?Hub:Home;data.FindProperty("_returnToHub").boolValue=returning;
            data.FindProperty("_arrivalPosition").vector3Value=returning?new Vector3(11.63f,.1f,-7):new Vector3(0,.1f,0);data.FindProperty("_arrivalEuler").vector3Value=Vector3.zero;data.ApplyModifiedPropertiesWithoutUndo();
            var outside=new GameObject("OutsideArrival");outside.transform.SetParent(root.transform,false);outside.transform.localPosition=new Vector3(0,.1f,-3.2f);Set(gate,"_outsidePoint",outside.transform);
            var center=new GameObject("UnifiedPortalVisual");center.transform.SetParent(root.transform,false);center.transform.localPosition=Vector3.up*1.65f;
            var prefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Prefabs/Portal/PortalGold.prefab");
            if(prefab==null)throw new InvalidOperationException("缺少用户PortalGold.prefab");
            var visual=(GameObject)PrefabUtility.InstantiatePrefab(prefab,scene);visual.transform.SetParent(center.transform,false);visual.transform.localPosition=Vector3.zero;
            var presentation=root.AddComponent<HomePortalPresentation>();Set(presentation,"_visual",center.transform);OrientGold(gate,false);return gate;
        }
        private static void OrientGold(HomePortal gate,bool undo)
        {
            if(gate==null)return;var visual=gate.transform.Find("UnifiedPortalVisual/PortalGold");if(visual==null)return;
            // 原Prefab的圈面朝本地X轴，家门朝Z；保留原Z旋转使椭圆竖直，再绕Y转90度面向门前。
            if(undo)Undo.RecordObject(visual,"家金门朝向");visual.localRotation=Quaternion.Euler(0,90,90);
        }
        private static void Write(string name,string content)
        {Directory.CreateDirectory(Evidence);File.WriteAllText(Evidence+"/"+name,DateTime.UtcNow.ToString("O")+"\n"+content);}
    }
}
