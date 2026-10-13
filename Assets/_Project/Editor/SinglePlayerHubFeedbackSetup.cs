using System;
using System.IO;
using Game.Character;
using Game.Core;
using Game.Run;
using Game.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object=UnityEngine.Object;

namespace Game.EditorTools
{
    /// <summary>在独立副本完成反馈修正；正式当前Scene仅Undo应用，用户自行保存。</summary>
    [InitializeOnLoad]
    internal static class SinglePlayerHubFeedbackSetup
    {
        internal const string Review="Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub_FeedbackReview.unity";
        private const string Sandbox="Assets/_Project/Scenes/Tests/HubFeedbackSandbox.unity";
        private const string Request="Temp/SinglePlayer/hub-feedback.request";
        private const string Evidence="TestResults/SinglePlayer/M6-T1-Feedback";
        private const string ReturnPrefab="Assets/_Project/Prefabs/SinglePlayer/HubPracticeReturn.prefab";
        static SinglePlayerHubFeedbackSetup(){EditorApplication.update+=Poll;}
        private static void Poll()
        {
            if(!File.Exists(Request) || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)return;
            var command=File.ReadAllText(Request).Trim();File.Delete(Request);
            try
            {
                if(EditorUtility.scriptCompilationFailed)throw new InvalidOperationException("编译失败，拒绝使用旧工具。");
                if(command=="prepare" || command=="repair")Prepare(command=="repair");
                else if(command=="apply-current")
                {
                    var scene=SceneManager.GetActiveScene();if(scene.path!=SinglePlayerHubSceneSetup.Path || scene.isDirty)throw new InvalidOperationException("仅对干净的正式当前Scene应用。");
                    Undo.IncrementCurrentGroup();int group=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("据点集中反馈修正");Apply(scene,true);Undo.CollapseUndoOperations(group);
                    EditorSceneManager.MarkSceneDirty(scene);SessionState.EraseString("Norn.HubAlignment.ReviewPath");
                    Write("current-applied.json",JsonUtility.ToJson(new Report{Scene=scene.path,Dirty=scene.isDirty},true));
                }
                else if(command=="clear-review")SessionState.EraseString("Norn.HubAlignment.ReviewPath");
                else throw new InvalidOperationException("未知反馈装配命令。");
            }
            catch(Exception e){Write("setup-error.txt",e.ToString());GameLog.Error(e.ToString(),"HubAuthoring");}
        }
        private static void Prepare(bool repair)
        {
            for(int i=0;i<SceneManager.sceneCount;i++)if(SceneManager.GetSceneAt(i).isDirty)throw new InvalidOperationException("用户Scene未保存，不自动保存/丢弃。");
            if(SceneManager.GetSceneByPath(Review).isLoaded)throw new InvalidOperationException("用户打开审阅Scene，拒绝覆盖。");
            if(!repair && File.Exists(Review))throw new InvalidOperationException("审阅副本已存在，拒绝覆盖。");
            if(!repair && !AssetDatabase.CopyAsset(SinglePlayerHubSceneSetup.Path,Review))throw new IOException("复制审阅Scene失败。");
            var original=SceneManager.GetActiveScene();var scene=EditorSceneManager.OpenScene(Review,OpenSceneMode.Additive);
            try
            {SceneManager.SetActiveScene(scene);Apply(scene,false);if(!EditorSceneManager.SaveScene(scene,Review))throw new IOException("保存自建审阅副本失败。");
                SessionState.SetString("Norn.HubAlignment.ReviewPath",Review);Write("prepared.json",JsonUtility.ToJson(new Report{Scene=Review,Dirty=scene.isDirty},true));}
            finally{EditorSceneManager.CloseScene(scene,true);if(original.IsValid() && original.isLoaded)SceneManager.SetActiveScene(original);}
            // 灰盒回归也使用同一套作者接线；保留旧证据场景，避免把旧行为测试偷偷留在旧配置中。
            if(SceneManager.GetSceneByPath(Sandbox).isLoaded)throw new InvalidOperationException("用户打开灰盒反馈Scene，拒绝覆盖。");
            if(!File.Exists(Sandbox) && !AssetDatabase.CopyAsset("Assets/_Project/Scenes/Tests/HubSandbox.unity",Sandbox))throw new IOException("复制灰盒失败。");
            var sandbox=EditorSceneManager.OpenScene(Sandbox,OpenSceneMode.Additive);
            try{SceneManager.SetActiveScene(sandbox);Apply(sandbox,false);if(!EditorSceneManager.SaveScene(sandbox,Sandbox))throw new IOException("保存自建灰盒失败。");}
            finally{EditorSceneManager.CloseScene(sandbox,true);if(original.IsValid() && original.isLoaded)SceneManager.SetActiveScene(original);}
        }
        internal static void Apply(Scene scene,bool undo)
        {
            var session=Find<SinglePlayerInventorySession>(scene);var run=Find<SinglePlayerRunCoordinator>(scene);var ui=Find<SinglePlayerInventoryController>(scene);
            var player=Find<WizardController>(scene);var pickup=Add<NearbyPickupController>(session.gameObject,undo);Set(pickup,"_session",session,undo);Set(pickup,"_player",player.transform,undo);
            Set(ui,"_nearby",pickup,undo);Set(player.GetComponent<HubInteractInput>(),"_pickups",pickup,undo);
            var lifetime=Add<HubExperimentWorldLifetime>(session.gameObject,undo);Set(lifetime,"_session",session,undo);Set(lifetime,"_run",run,undo);Set(lifetime,"_world",Get<GameObject>(run,"_hubWorld"),undo);
            var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(ReturnPrefab);if(prefab==null)prefab=CreateReturnPrefab();
            foreach(var node in SinglePlayerHubHierarchySetup.All(scene))if(node.name=="远征点 · P7" || node.name=="远征点 · P8")
            {
                var root=node.gameObject;
                var collider=Add<SphereCollider>(root,undo);Record(collider,undo);collider.isTrigger=true;collider.radius=1.3f;collider.center=Vector3.up*.8f;
                var body=Add<Rigidbody>(root,undo);Record(body,undo);body.isKinematic=true;body.useGravity=false;
                var portal=Add<HubPracticePortal>(root,undo);Set(portal,"_session",session,undo);Set(portal,"_player",player.transform,undo);Set(portal,"_returnPortalPrefab",prefab,undo);
                String(portal,"_scenePath",root.name.EndsWith("P7")?"Assets/_Project/Scenes/P7_DemoRun.unity":"Assets/_Project/Scenes/P8_BossField.unity",undo);
                SinglePlayerUnifiedItemsSetup.InstallPortalVisual(root,undo);
            }
            BuildInventory(scene,ui,undo);CompactHud(scene,undo);
            // Build中也允许Single切旧图；已有列表保留，不重排/移除其他Scene。
            var scenes=new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            foreach(var path in new[]{"Assets/_Project/Scenes/P7_DemoRun.unity","Assets/_Project/Scenes/P8_BossField.unity",SinglePlayerHubSceneSetup.Path})
            {bool found=false;foreach(var item in scenes)if(item.path==path){item.enabled=true;found=true;}if(!found)scenes.Add(new EditorBuildSettingsScene(path,true));}
            EditorBuildSettings.scenes=scenes.ToArray();
        }
        private static void BuildInventory(Scene scene,SinglePlayerInventoryController ui,bool undo)
        {
            var workspace=ui.GetComponent<InventoryWorkspaceView>();var panel=Get<GameObject>(ui,"_panelRoot").transform;
            // 区域用真实序列化引用定位，作者可读命名不再成为重复创建界面的原因。
            var wands=Get<RectTransform>(workspace,"_wands");var details=Get<Text>(ui,"_details").transform.parent;
            var existingNearby=Get<RectTransform>(workspace,"_nearbyRegion");
            var nearby=Child(panel,existingNearby!=null?existingNearby.name:"Nearby",new Vector2(20,-610),new Vector2(580,350),undo,true);
            Label(nearby,"Title",new Vector2(12,-8),new Vector2(550,34),"附近物品",24,undo);
            var ground=Scroll(nearby,"Ground",new Vector2(12,-54),new Vector2(550,282),false,undo);
            Set(ui,"_groundRoot",ground,undo);Set(workspace,"_nearbyRegion",nearby,undo);
            // 附近空白区也可接收真实丢弃拖拽；满包玩家必须有可见方式腾格，不能仅测试直接调用函数。
            var dropTarget=Add<InventoryDropTarget>(nearby.gameObject,undo);Set(ui,"_groundTarget",dropTarget,undo);
            var stats=Label(wands,"WandStats",new Vector2(12,-176),new Vector2(716,122),"选择法杖查看属性",22,undo);
            Set(ui.GetComponentInChildren<WandInventoryView>(true),"_stats",stats,undo);Set(workspace,"_wandStats",stats,undo);
            Label(wands,"ToolsTitle",new Vector2(12,-630),new Vector2(716,34),"可用道具",23,undo);
            var tools=Scroll(wands,"Tools",new Vector2(12,-678),new Vector2(716,104),true,undo);Set(workspace,"_toolsRoot",tools,undo);
            var take=Button(details,"PickupSelected",new Vector2(12,-354),new Vector2(220,46),"拾取",ui.PickupSelected,undo);Set(workspace,"_pickup",take,undo);
            var discard=Button(details,"DropSelected",new Vector2(246,-354),new Vector2(220,46),"丢弃到地面",ui.DropSelected,undo);Set(workspace,"_drop",discard,undo);
            var preparation=Find<RaidPreparationView>(scene);Bool(preparation,"_contextualDetails",true,undo);
            var controls=Get<GameObject>(preparation,"_controls");Parent(controls.transform,details,undo);Place((RectTransform)controls.transform,new Vector2(12,-408),new Vector2(450,100),undo);
            Place(Get<Text>(preparation,"_selection").rectTransform,new Vector2(0,0),new Vector2(440,34),undo);
            Place(Get<Button>(preparation,"_protect").GetComponent<RectTransform>(),new Vector2(0,-44),new Vector2(200,44),undo);
            Place(Get<Button>(preparation,"_unprotect").GetComponent<RectTransform>(),new Vector2(218,-44),new Vector2(200,44),undo);
            Get<Button>(preparation,"_depart").gameObject.SetActive(false);
            int index=0;foreach(var field in new[]{"_starter","_abandon","_retry","_cancelExtraction","_startExtraction"})
            {var button=Get<Button>(preparation,field);if(button==null)continue;Parent(button.transform,panel,undo);Place(button.GetComponent<RectTransform>(),new Vector2(230+index*170,-938),new Vector2(158,42),undo);index++;}
            var attachment=Find<SpellAttachmentView>(scene);Bool(attachment,"_contextualDetails",true,undo);
            var attachmentControls=Get<GameObject>(attachment,"_controls");Place((RectTransform)attachmentControls.transform,new Vector2(12,-520),new Vector2(450,230),undo);
            var feedback=Get<Text>(attachment,"_feedback");Place(feedback.rectTransform,new Vector2(12,-774),new Vector2(450,60),undo);Record(feedback,undo);feedback.text="";
            foreach(var name in new[]{"Save","Next"})if(details.Find(name)!=null)
            {var item=details.Find(name);Parent(item,panel,undo);Place((RectTransform)item,new Vector2(name=="Save"?1380:1610,-938),new Vector2(210,42),undo);}
            Place(Get<Text>(ui,"_status").rectTransform,new Vector2(20,-54),new Vector2(1740,30),undo);
            // 右侧描述只介绍所选项，空选择的长教程文字不再占位；原业务组件仍在。
            Record(Get<Text>(workspace,"_containerDetails"),undo);Get<Text>(workspace,"_containerDetails").text="";
            Record(Get<Text>(ui,"_details"),undo);Get<Text>(ui,"_details").text="选择物品查看详情";
        }
        private static void CompactHud(Scene scene,bool undo)
        {
            var hud=Find<ContainerCompositionView>(scene);var root=(RectTransform)hud.transform;
            foreach(Transform child in root){Record(child.gameObject,undo);child.gameObject.SetActive(false);}
            Record(root,undo);root.anchorMin=root.anchorMax=root.pivot=new Vector2(1,0);root.anchoredPosition=new Vector2(-24,24);root.sizeDelta=new Vector2(400,110);
            var image=root.GetComponent<Image>();if(image!=null){Record(image,undo);image.enabled=false;}
            var compact=Child(root,"CompactContainer",Vector2.zero,new Vector2(400,110),undo,true);compact.gameObject.SetActive(true);
            var title=Label(compact,"Title",new Vector2(12,-6),new Vector2(370,26),"当前容器 · F 切换",18,undo);
            var loaded=Label(compact,"Loaded",new Vector2(12,-34),new Vector2(80,24),"已装",18,undo);
            var capacity=Label(compact,"Capacity",new Vector2(12,-60),new Vector2(80,24),"容量",18,undo);
            Record(hud,undo);var data=new SerializedObject(hud);var labels=data.FindProperty("_labels");labels.arraySize=3;
            labels.GetArrayElementAtIndex(0).objectReferenceValue=title;labels.GetArrayElementAtIndex(1).objectReferenceValue=loaded;
            labels.GetArrayElementAtIndex(2).objectReferenceValue=capacity;data.ApplyModifiedPropertiesWithoutUndo();
            var digits=Get<Text[]>(hud,"_digits");for(int row=0;row<2;row++)for(int column=0;column<20;column++)
            {var digit=digits[row*20+column];Parent(digit.transform,compact,undo);digit.gameObject.SetActive(true);Place(digit.rectTransform,new Vector2(96+column*10,-34-row*26),new Vector2(12,24),undo);}
            Set(hud,"_compactPanel",compact.gameObject,undo);
            var status=Get<Text>(hud,"_status");Parent(status.transform,compact,undo);Place(status.rectTransform,new Vector2(12,-86),new Vector2(375,24),undo);status.gameObject.SetActive(false);
            var extraction=Find<ExtractionHud>(scene);foreach(var field in new[]{"_condition","_timer"}){var text=Get<Text>(extraction,field);Record(text.gameObject,undo);text.gameObject.SetActive(false);Record(text,undo);text.text="";}
            var runResult=Find<RaidResultView>(scene);var result=Get<Text>(runResult,"_result");Record(result,undo);result.text="";
        }
        private static GameObject CreateReturnPrefab()
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ReturnPrefab));var root=new GameObject("HubPracticeReturn");
            try
            {
                var collider=root.AddComponent<SphereCollider>();collider.isTrigger=true;collider.radius=1.3f;collider.center=Vector3.up*.8f;
                var body=root.AddComponent<Rigidbody>();body.isKinematic=true;body.useGravity=false;var portal=root.AddComponent<HubPracticePortal>();Bool(portal,"_returnToHub",true,false);
                SinglePlayerUnifiedItemsSetup.InstallPortalVisual(root,false);
                return PrefabUtility.SaveAsPrefabAsset(root,ReturnPrefab);
            }
            finally{Object.DestroyImmediate(root);}
        }
        private static RectTransform Child(Transform parent,string name,Vector2 position,Vector2 size,bool undo,bool box=false)
        {
            var child=parent.Find(name);RectTransform rect;
            if(child!=null)rect=(RectTransform)child;else{var go=new GameObject(name,typeof(RectTransform));if(undo)Undo.RegisterCreatedObjectUndo(go,"据点反馈UI");go.transform.SetParent(parent,false);rect=(RectTransform)go.transform;}
            Place(rect,position,size,undo);if(box){var image=Add<Image>(rect.gameObject,undo);Record(image,undo);image.color=new Color(.025f,.045f,.065f,.97f);}return rect;
        }
        private static Text Label(Transform parent,string name,Vector2 position,Vector2 size,string text,int font,bool undo)
        {var r=Child(parent,name,position,size,undo);var t=Add<Text>(r.gameObject,undo);Record(t,undo);t.text=text;t.font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");t.fontSize=font;t.color=Color.white;t.raycastTarget=false;return t;}
        private static RectTransform Scroll(Transform parent,string name,Vector2 position,Vector2 size,bool horizontal,bool undo)
        {var root=Child(parent,name,position,size,undo);var viewport=Child(root,"Viewport",Vector2.zero,size,undo);Add<RectMask2D>(viewport.gameObject,undo);
            var content=Child(viewport,"Content",Vector2.zero,size,undo);var scroll=Add<ScrollRect>(root.gameObject,undo);Record(scroll,undo);scroll.content=content;scroll.viewport=viewport;scroll.horizontal=horizontal;scroll.vertical=!horizontal;scroll.movementType=ScrollRect.MovementType.Clamped;return content;}
        private static Button Button(Transform parent,string name,Vector2 position,Vector2 size,string text,UnityEngine.Events.UnityAction action,bool undo)
        {var rect=Child(parent,name,position,size,undo,true);var button=Add<Button>(rect.gameObject,undo);if(button.onClick.GetPersistentEventCount()==0)UnityEditor.Events.UnityEventTools.AddPersistentListener(button.onClick,action);
            var label=Label(rect,"Label",new Vector2(6,-4),size-new Vector2(12,8),text,21,undo);label.alignment=TextAnchor.MiddleCenter;return button;}
        private static void Place(RectTransform r,Vector2 pos,Vector2 size,bool undo){Record(r,undo);r.anchorMin=r.anchorMax=r.pivot=new Vector2(0,1);r.anchoredPosition=pos;r.sizeDelta=size;}
        private static void Parent(Transform t,Transform parent,bool undo){if(undo)Undo.SetTransformParent(t,parent,"据点反馈UI");else t.SetParent(parent,false);}
        private static T Add<T>(GameObject go,bool undo)where T:Component{var c=go.GetComponent<T>();return c!=null?c:undo?Undo.AddComponent<T>(go):go.AddComponent<T>();}
        private static void Record(Object obj,bool undo){if(undo)Undo.RecordObject(obj,"据点集中反馈修正");}
        private static T Get<T>(Object target,string field)where T:class
        {var p=new SerializedObject(target).FindProperty(field);if(typeof(T)==typeof(Text[])){var list=new Text[p.arraySize];for(int i=0;i<list.Length;i++)list[i]=(Text)p.GetArrayElementAtIndex(i).objectReferenceValue;return list as T;}return p.objectReferenceValue as T;}
        private static void Set(Object target,string field,Object value,bool undo){Record(target,undo);var s=new SerializedObject(target);s.FindProperty(field).objectReferenceValue=value;s.ApplyModifiedPropertiesWithoutUndo();}
        private static void Bool(Object target,string field,bool value,bool undo){Record(target,undo);var s=new SerializedObject(target);s.FindProperty(field).boolValue=value;s.ApplyModifiedPropertiesWithoutUndo();}
        private static void String(Object target,string field,string value,bool undo){Record(target,undo);var s=new SerializedObject(target);s.FindProperty(field).stringValue=value;s.ApplyModifiedPropertiesWithoutUndo();}
        private static T Find<T>(Scene scene)where T:Component{foreach(var root in scene.GetRootGameObjects()){var c=root.GetComponentInChildren<T>(true);if(c!=null)return c;}throw new InvalidOperationException(typeof(T).Name+"缺失");}
        private static void Write(string name,string value){Directory.CreateDirectory(Evidence);File.WriteAllText(Evidence+"/"+name,value);}
        [Serializable]private sealed class Report{public string Scene;public bool Dirty;}
    }
}
