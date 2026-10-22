using System;
using System.IO;
using Game.Core;
using Game.Run;
using Game.Rendering;
using Game.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
namespace Game.EditorTools
{
    /// <summary>增量表现接线：Review可保存；当前Scene只Undo应用，不覆盖用户外框/右栏。</summary>
    [InitializeOnLoad]
    internal static class SinglePlayerItemPresentationSetup
    {
        private const string Formal="Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity";
        internal const string Review="Assets/_Project/Scenes/Tests/Hub/SinglePlayer_Hub_ItemPresentationReview.unity";
        internal const string FeedbackReview="Assets/_Project/Scenes/Tests/Hub/SinglePlayer_Hub_ItemFeedbackReview.unity";
        internal const string ArrangementReview="Assets/_Project/Scenes/Tests/Hub/SinglePlayer_Hub_ArrangementGlowReview.unity";
        private const string Request="Temp/SinglePlayer/item-presentation.request";
        private const string Evidence="TestResults/SinglePlayer/M6-T1-ItemPresentation";
        private static bool _undo;
        static SinglePlayerItemPresentationSetup()=>EditorApplication.update+=Poll;
        private static void Poll()
        {
            if(!File.Exists(Request) || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)return;
            string command=File.ReadAllText(Request).Trim();File.Delete(Request);
            try
            {if(EditorUtility.scriptCompilationFailed)throw new InvalidOperationException("编译失败，拒绝旧Assembly。");
                if(command=="bake" || command=="rebake")Write("bake.txt",ItemIconBaker.BakeAll(command=="rebake").ToString());else if(command=="prepare")Prepare();else if(command=="prepare-feedback")Prepare(true);else if(command=="prepare-arrangement")Prepare(false,true);else if(command=="apply-current")ApplyCurrent();else if(command=="apply-prompts")ApplyPromptCurrent();else throw new ArgumentException(command);}
            catch(Exception e){Write("setup-error.txt",e.ToString());GameLog.Error(e.ToString(),"ItemPresentation");}
        }
        private static void Prepare(bool feedback=false,bool arrangement=false)
        {
            for(int i=0;i<SceneManager.sceneCount;i++)if(SceneManager.GetSceneAt(i).isDirty)throw new InvalidOperationException("用户Scene未保存，不能复制审阅图。");
            string path=arrangement?ArrangementReview:feedback?FeedbackReview:Review;
            if(SceneManager.GetSceneByPath(path).isLoaded)throw new InvalidOperationException("审阅图由用户打开，不覆盖。");
            if(!File.Exists(path) && !AssetDatabase.CopyAsset(Formal,path))throw new IOException("复制Review失败。");
            var original=SceneManager.GetActiveScene();var scene=EditorSceneManager.OpenScene(path,OpenSceneMode.Additive);
            try{SceneManager.SetActiveScene(scene);_undo=false;Apply(scene);EditorSceneManager.SaveScene(scene,path);}
            finally{EditorSceneManager.CloseScene(scene,true);SceneManager.SetActiveScene(original);}
            Write(feedback?"feedback-prepared.txt":"prepared.txt",path);
        }
        [MenuItem("Tools/Norn/物品/应用图标、发光与固定拾取列表（Undo，不保存）")]
        private static void ApplyCurrent()
        {
            var scene=SceneManager.GetActiveScene();if(scene.path!=Formal || EditorApplication.isPlaying)throw new InvalidOperationException("仅正式据点EditMode。");
            Undo.IncrementCurrentGroup();int group=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("物品图标、发光与附近网格");_undo=true;
            string before=PreservedUi(scene);
            try{Apply(scene);if(before!=PreservedUi(scene))throw new InvalidOperationException("外围布局或右栏改变。");
                Undo.FlushUndoRecordObjects();Undo.CollapseUndoOperations(group);EditorSceneManager.MarkSceneDirty(scene);Write("current-applied.txt","单Undo应用；未保存当前Scene；外框与右栏数据一致。");}
            catch{Undo.RevertAllDownToGroup(group);throw;}finally{_undo=false;}
        }
        [MenuItem("Tools/Norn/交互/应用角色旁区域与拾取提示（Undo，不保存）")]
        private static void ApplyPromptCurrent()
        {
            var scene=SceneManager.GetActiveScene();
            if(scene.path!=Formal || EditorApplication.isPlayingOrWillChangePlaymode)throw new InvalidOperationException("仅正式据点EditMode。");
            Undo.IncrementCurrentGroup();int group=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("角色旁区域与拾取提示");
            try
            {
                // 只处理这个任务的HUD和两种旧文字；不调用全套Apply，不触碰背包、右栏、发光或区域边界。
                string preserved=PreservedUi(scene);
                var view=Find<NearbyPickupListView>(scene);var hub=Find<HubInteractionController>(scene);
                var data=new SerializedObject(view);
                data.FindProperty("_hub").objectReferenceValue=hub;
                var panel=Get<GameObject>(view,"_visual");
                // 站点和拾取复用现有行，仅删除旧分支，不覆盖作者锚点/子对象布局。
                for(int i=panel.transform.childCount-1;i>=0;i--)
                    if(panel.transform.GetChild(i).name.StartsWith("Station entry"))Undo.DestroyObjectImmediate(panel.transform.GetChild(i).gameObject);
                Undo.RecordObject(view,"角色旁区域与拾取提示");
                data.ApplyModifiedPropertiesWithoutUndo();int removed=0;
                foreach(var root in scene.GetRootGameObjects())foreach(var label in root.GetComponentsInChildren<TextMesh>(true))
                    if(NearbyPickupListView.IsLegacyStationLabel(label,hub.Definition)){Undo.DestroyObjectImmediate(label.gameObject);removed++;}
                if(preserved!=PreservedUi(scene))throw new InvalidOperationException("背包或右栏数据改变，撤销提示接线。");
                Undo.FlushUndoRecordObjects();Undo.CollapseUndoOperations(group);EditorSceneManager.MarkSceneDirty(scene);
                Directory.CreateDirectory("TestResults/SinglePlayer/M6-T1-InteractionPrompt");
                File.WriteAllText("TestResults/SinglePlayer/M6-T1-InteractionPrompt/current-applied.txt",
                    "单Undo增量应用；未保存Scene。作者anchor保留；removedLegacyLabels="+removed+"；区域/背包/右栏/Prefab保持。");
            }
            catch{Undo.RevertAllDownToGroup(group);throw;}
        }
        private static void Apply(Scene scene)
        {
            // 最早的采集瓶仍是旧数据，补展示引用即可；稳定ContentId、容量、占格与存档规则不变。
            var starter=AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/Items/Containers/Definitions/Item_Container_Bottle.asset");
            if(starter!=null && starter.Icon==null)
            {
                var asset=new SerializedObject(starter);
                asset.FindProperty("_icon").objectReferenceValue=AssetDatabase.LoadAssetAtPath<Sprite>("Assets/_Project/Art/UI/ItemIcons/Container/Container_01.png");
                if(starter.WorldModel==null)asset.FindProperty("_worldModel").objectReferenceValue=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Prefabs/Item/Container/Container_01.prefab");
                asset.ApplyModifiedPropertiesWithoutUndo();AssetDatabase.SaveAssetIfDirty(starter);
            }
            var session=Find<SinglePlayerInventorySession>(scene);var nearby=Find<NearbyPickupController>(scene);
            SinglePlayerInventoryController backpack=null;
            foreach(var root in scene.GetRootGameObjects())foreach(var ui in root.GetComponentsInChildren<SinglePlayerInventoryController>(true))
                if(Get<BackpackWorkspaceView>(ui,"_backpackWorkspace")!=null)backpack=ui;
            if(backpack==null)throw new InvalidOperationException("没有独立背包界面。");
            var ground=Get<RectTransform>(backpack,"_groundRoot");var grid=Add<NearbyItemGridView>(ground.gameObject);Set(backpack,"_nearbyGrid",grid);
            var scroll=ground.GetComponentInParent<ScrollRect>();if(scroll!=null)
            {
                Record(scroll);scroll.horizontal=true;scroll.vertical=true;scroll.scrollSensitivity=32;EnsureScrollbar(scroll);
                var auto=Add<InventoryDragAutoScroll>(scroll.gameObject);Set(auto,"_scroll",scroll);
                // 空白Viewport必须参与Raycast，滚轮才能沿父层级到达ScrollRect。
                var background=Add<Image>(scroll.viewport.gameObject);Record(background);background.color=Color.clear;background.raycastTarget=true;
                Add<InventoryDropTarget>(scroll.viewport.gameObject);
            }
            foreach(var root in scene.GetRootGameObjects())foreach(var old in root.GetComponentsInChildren<WorldPickupPromptView>(true)){Record(old);old.enabled=false;var visual=Get<GameObject>(old,"_visual");if(visual!=null){Record(visual);visual.SetActive(false);}}
            var uiRoot=Root(scene,"__UI");var node=uiRoot.Find("PickupListCanvas_固定附近拾取");
            var hud=node!=null?node.gameObject:New("PickupListCanvas_固定附近拾取",uiRoot,true);
            var canvas=Add<Canvas>(hud);Record(canvas);canvas.renderMode=RenderMode.ScreenSpaceOverlay;canvas.sortingOrder=70;
            var scaler=Add<CanvasScaler>(hud);Record(scaler);scaler.uiScaleMode=CanvasScaler.ScaleMode.ScaleWithScreenSize;scaler.referenceResolution=new Vector2(1920,1080);scaler.matchWidthOrHeight=.5f;
            var view=Add<NearbyPickupListView>(hud);Set(view,"_session",session);Set(view,"_nearby",nearby);Set(view,"_inventory",backpack);
            if(hud.transform.Find("Nearby pickup list")==null)
            {
                var panel=(RectTransform)New("Nearby pickup list",hud.transform,true).transform;panel.anchorMin=panel.anchorMax=new Vector2(.56f,.45f);panel.pivot=new Vector2(0,1);panel.anchoredPosition=new Vector2(0,0);panel.sizeDelta=new Vector2(360,360);
                var rows=new GameObject[5];var labels=new Text[5];var icons=new Image[5];var backgrounds=new Image[5];
                for(int i=0;i<5;i++)
                {
                    rows[i]=New("Entry "+(i+1),panel,true);var rect=(RectTransform)rows[i].transform;rect.anchorMin=rect.anchorMax=rect.pivot=new Vector2(0,1);rect.anchoredPosition=new Vector2(0,-i*64);rect.sizeDelta=new Vector2(360,112);
                    backgrounds[i]=Add<Image>(rows[i]);backgrounds[i].sprite=AssetDatabase.LoadAssetAtPath<Sprite>("Assets/_Project/Art/UI/Funtion_UI2/PickupItemEntry.png");backgrounds[i].preserveAspect=true;backgrounds[i].raycastTarget=false;
                    icons[i]=Add<Image>(New("Item icon",rect,true));icons[i].preserveAspect=true;icons[i].raycastTarget=false;icons[i].rectTransform.anchorMin=icons[i].rectTransform.anchorMax=new Vector2(0,.5f);icons[i].rectTransform.anchoredPosition=new Vector2(40,0);icons[i].rectTransform.sizeDelta=Vector2.one*44;
                    labels[i]=Label("F and item name",rect);labels[i].alignment=TextAnchor.MiddleLeft;labels[i].alignByGeometry=true;labels[i].rectTransform.offsetMin=new Vector2(80,0);labels[i].rectTransform.offsetMax=new Vector2(-10,0);
                }
                var footer=Label("Scroll selection hint",panel);footer.rectTransform.anchorMin=footer.rectTransform.anchorMax=new Vector2(0,1);footer.rectTransform.pivot=new Vector2(0,1);footer.rectTransform.anchoredPosition=new Vector2(10,-350);footer.rectTransform.sizeDelta=new Vector2(350,30);footer.fontSize=16;
                Set(view,"_visual",panel.gameObject);SetArray(view,"_rows",rows);SetArray(view,"_labels",labels);SetArray(view,"_icons",icons);SetArray(view,"_backgrounds",backgrounds);Set(view,"_footer",footer);panel.gameObject.SetActive(false);
            }
            const string profilePath="Assets/_Project/ScriptableObjects/Rendering/Items/PickupHighlight.asset";
            ItemIconBaker.EnsureFolder("Assets/_Project/ScriptableObjects/Rendering/Items");var profile=AssetDatabase.LoadAssetAtPath<WorldItemHighlightProfile>(profilePath);
            if(profile==null){profile=ScriptableObject.CreateInstance<WorldItemHighlightProfile>();profile.FallbackShader=Shader.Find("Norn/ItemPickupRim");AssetDatabase.CreateAsset(profile,profilePath);}
            WorldItemOutlineBaker.Bake(profile);
            EnsureGlow(profile);
            var renderingRoot=Root(scene,"__Systems");var binding=renderingRoot.Find("WorldItemHighlight_可拾取发光");
            var binder=Add<WorldItemHighlightBinder>(binding!=null?binding.gameObject:New("WorldItemHighlight_可拾取发光",renderingRoot));Set(binder,"_profile",profile);
        }
        private static void EnsureGlow(WorldItemHighlightProfile profile)
        {
            const string backup="TestResults/SinglePlayer/M6-T1-ArrangementGlow/asset-backups";
            Directory.CreateDirectory(backup);
            foreach(var name in new[]{"PC_Renderer","Mobile_Renderer"})
            {
                string path="Assets/_Project/Settings/"+name+".asset";
                var renderer=AssetDatabase.LoadAssetAtPath<UnityEngine.Rendering.Universal.UniversalRendererData>(path);
                if(renderer==null)throw new InvalidOperationException("缺少Renderer："+path);
                WorldItemGlowRendererFeature feature=null;
                foreach(var current in renderer.rendererFeatures)if(current is WorldItemGlowRendererFeature glow)feature=glow;
                if(feature!=null)continue;
                string copy=backup+"/"+name+".asset";if(!File.Exists(copy))File.Copy(path,copy);
                string profileCopy=backup+"/PickupHighlight.asset";if(!File.Exists(profileCopy))File.Copy(AssetDatabase.GetAssetPath(profile),profileCopy);
                // 仅首次升级设置参考色，之后用户Inspector调整不会被重复Apply重置。
                profile.Color=new Color(.65f,.95f,1,1);profile.Strength=1.15f;profile.OutlinePixels=2.5f;profile.HaloStrength=.8f;profile.HaloRadiusPixels=12;
                EditorUtility.SetDirty(profile);AssetDatabase.SaveAssetIfDirty(profile);
                feature=ScriptableObject.CreateInstance<WorldItemGlowRendererFeature>();feature.name="Pickup edge halo";
                feature.Profile=profile;feature.GlowShader=Shader.Find("Hidden/Norn/ItemPickupGlow");feature.Create();
                if(_undo){Undo.RegisterCreatedObjectUndo(feature,"Pickup glow feature");Undo.RecordObject(renderer,"Pickup glow feature");}
                AssetDatabase.AddObjectToAsset(feature,renderer);renderer.rendererFeatures.Add(feature);renderer.SetDirty();
                EditorUtility.SetDirty(feature);EditorUtility.SetDirty(renderer);AssetDatabase.SaveAssetIfDirty(renderer);
            }
        }
        private static void EnsureScrollbar(ScrollRect scroll)
        {
            // 超宽物品扩展Content，横向也必须有可拖动的Scrollbar，不能靠缩小格子迁就。
            if(scroll.horizontalScrollbar==null)
            {
                var horizontal=(RectTransform)New("Nearby horizontal scrollbar",scroll.transform,true).transform;
                horizontal.anchorMin=Vector2.zero;horizontal.anchorMax=new Vector2(1,0);horizontal.pivot=Vector2.zero;
                horizontal.offsetMin=Vector2.zero;horizontal.offsetMax=new Vector2(-12,12);
                var bg=Add<Image>(horizontal.gameObject);bg.color=new Color(.15f,.18f,.18f,1);
                var thumb=(RectTransform)New("Handle",horizontal,true).transform;Stretch(thumb);
                var color=Add<Image>(thumb.gameObject);color.color=new Color(.8f,.67f,.25f,1);
                var horizontalBar=Add<Scrollbar>(horizontal.gameObject);horizontalBar.handleRect=thumb;horizontalBar.targetGraphic=color;
                horizontalBar.direction=Scrollbar.Direction.LeftToRight;scroll.horizontalScrollbar=horizontalBar;
            }
            Record(scroll);scroll.horizontalScrollbarVisibility=ScrollRect.ScrollbarVisibility.AutoHide;
            scroll.verticalScrollbarVisibility=ScrollRect.ScrollbarVisibility.AutoHide;
            if(scroll.verticalScrollbar!=null)return;var rect=(RectTransform)New("Nearby vertical scrollbar",scroll.transform,true).transform;
            rect.anchorMin=new Vector2(1,0);rect.anchorMax=Vector2.one;rect.pivot=Vector2.one;rect.offsetMin=new Vector2(-12,0);rect.offsetMax=Vector2.zero;
            var background=Add<Image>(rect.gameObject);background.color=new Color(.15f,.18f,.18f,1);
            var handle=(RectTransform)New("Handle",rect,true).transform;Stretch(handle);var image=Add<Image>(handle.gameObject);image.color=new Color(.8f,.67f,.25f,1);
            var bar=Add<Scrollbar>(rect.gameObject);bar.handleRect=handle;bar.targetGraphic=image;bar.direction=Scrollbar.Direction.BottomToTop;scroll.verticalScrollbar=bar;
        }
        private static string PreservedUi(Scene scene)
        {var b=new System.Text.StringBuilder();foreach(var root in scene.GetRootGameObjects())foreach(var ui in root.GetComponentsInChildren<SinglePlayerInventoryController>(true))
            {var panel=Get<GameObject>(ui,"_panelRoot");if(panel==null)continue;b.Append(EditorJsonUtility.ToJson(panel.transform));foreach(Transform child in panel.transform)b.Append(EditorJsonUtility.ToJson(child));
                var right=panel.transform.Find("RightPart");if(right!=null)foreach(var component in right.GetComponentsInChildren<Component>(true))if(component!=null)b.Append(EditorJsonUtility.ToJson(component));}return b.ToString();}
        private static Text Label(string name,Transform parent){var text=Add<Text>(New(name,parent,true));text.font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");text.fontSize=22;text.color=Color.white;text.raycastTarget=false;Stretch(text.rectTransform);return text;}
        private static void Stretch(RectTransform rect){rect.anchorMin=Vector2.zero;rect.anchorMax=Vector2.one;rect.offsetMin=rect.offsetMax=Vector2.zero;}
        private static Transform Root(Scene scene,string name){foreach(var root in scene.GetRootGameObjects())if(root.name==name)return root.transform;throw new InvalidOperationException(name);}
        private static T Find<T>(Scene scene)where T:Component{foreach(var root in scene.GetRootGameObjects()){var value=root.GetComponentInChildren<T>(true);if(value!=null)return value;}throw new InvalidOperationException(typeof(T).Name);}
        private static GameObject New(string name,Transform parent,bool rect=false){var go=rect?new GameObject(name,typeof(RectTransform)):new GameObject(name);if(_undo)Undo.RegisterCreatedObjectUndo(go,"物品表现接线");go.transform.SetParent(parent,false);return go;}
        private static T Add<T>(GameObject go)where T:Component{var value=go.GetComponent<T>();return value!=null?value:_undo?Undo.AddComponent<T>(go):go.AddComponent<T>();}
        private static void Record(UnityEngine.Object obj){if(_undo)Undo.RecordObject(obj,"物品表现接线");}
        private static T Get<T>(UnityEngine.Object obj,string name)where T:UnityEngine.Object=>new SerializedObject(obj).FindProperty(name).objectReferenceValue as T;
        private static void Set(UnityEngine.Object obj,string name,UnityEngine.Object value){Record(obj);var so=new SerializedObject(obj);so.FindProperty(name).objectReferenceValue=value;so.ApplyModifiedPropertiesWithoutUndo();}
        private static void SetArray(UnityEngine.Object obj,string name,UnityEngine.Object[] values){Record(obj);var so=new SerializedObject(obj);var array=so.FindProperty(name);array.arraySize=values.Length;for(int i=0;i<values.Length;i++)array.GetArrayElementAtIndex(i).objectReferenceValue=values[i];so.ApplyModifiedPropertiesWithoutUndo();}
        private static void Write(string name,string value){Directory.CreateDirectory(Evidence);File.WriteAllText(Evidence+"/"+name,DateTime.UtcNow.ToString("O")+"\n"+value);}
    }
}
