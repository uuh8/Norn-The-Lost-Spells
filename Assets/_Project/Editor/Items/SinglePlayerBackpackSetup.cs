using System;
using System.IO;
using Game.Character;
using Game.Core;
using Game.Run;
using Game.UI;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object=UnityEngine.Object;

namespace Game.EditorTools
{
    /// <summary>只接用户已拆出的背包Canvas。正式Scene单Undo不保存；Review只复制干净磁盘Scene。</summary>
    [InitializeOnLoad]
    internal static class SinglePlayerBackpackSetup
    {
        internal const string Review="Assets/_Project/Scenes/Tests/Hub/SinglePlayer_Hub_BackpackReview.unity";
        private const string Request="Temp/SinglePlayer/backpack-setup.request";
        private const string Evidence="TestResults/SinglePlayer/M6-T1-Backpack";
        private static bool _undo;
        static SinglePlayerBackpackSetup() { EditorApplication.update+=Poll; }
        private static void Poll()
        {
            if (!File.Exists(Request) || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
            string command=File.ReadAllText(Request).Trim(); File.Delete(Request);
            try
            {
                if (EditorUtility.scriptCompilationFailed) throw new InvalidOperationException("编译失败，不执行旧作者接线。");
                if (command=="prepare") Prepare();
                else if (command=="repair-review") RepairReview();
                else if (command=="apply-current") ApplyCurrent();
                else throw new InvalidOperationException("未知背包命令："+command);
            }
            catch (Exception e) { Write("setup-error.txt",e.ToString()); GameLog.Error(e.ToString(),"BackpackSetup"); }
        }
        private static void Prepare()
        {
            for (int i=0;i<SceneManager.sceneCount;i++) if (SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("当前Scene未保存；不代用户保存或丢弃。");
            if (File.Exists(Review)) throw new IOException("Review已存在，拒绝覆盖。");
            if (!AssetDatabase.CopyAsset(SinglePlayerHubSceneSetup.Path,Review)) throw new IOException("复制Review失败。");
            var original=SceneManager.GetActiveScene(); var scene=EditorSceneManager.OpenScene(Review,OpenSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene); _undo=false; string report=Apply(scene);
                if (!EditorSceneManager.SaveScene(scene,Review)) throw new IOException("Review保存失败。");
                SessionState.SetString("Norn.HubAlignment.ReviewPath",Review); Write("prepared.txt",report);
            }
            finally { EditorSceneManager.CloseScene(scene,true); SceneManager.SetActiveScene(original); }
        }
        private static void RepairReview()
        {
            // 仅维护本轮创建且未加载的Review；不复制覆盖用户当前Scene。
            for (int i=0;i<SceneManager.sceneCount;i++) if (SceneManager.GetSceneAt(i).path==Review) throw new InvalidOperationException("Review已打开，交给用户配置，不自动保存。");
            var original=SceneManager.GetActiveScene();var scene=EditorSceneManager.OpenScene(Review,OpenSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene);_undo=false;
                foreach (var node in SinglePlayerHubHierarchySetup.All(scene)) if (node.TryGetComponent<SinglePlayerInventoryController>(out var ui) && Get<BackpackWorkspaceView>(ui,"_backpackWorkspace")!=null)
                {
                    ConfigureGrid(ui);ConfigureStatus(ui);
                    var hud=Find<HeldItemHud>(scene);var hudCanvas=hud.GetComponentInParent<Canvas>(true);
                    if(hudCanvas.gameObject==Find<WandResourceHud>(scene).gameObject)
                    {
                        var canvas=NewHudCanvas(scene);hud.transform.SetParent(canvas.transform,false);Top((RectTransform)hud.transform,new Vector2(16,-16),new Vector2(900,94));
                        hud.ConfigureVisibility(ui,Get<SinglePlayerInventoryController>(ui,"_otherWorkspace"),Find<ShopController>(scene),canvas);
                    }
                    foreach(var image in hud.GetComponentsInChildren<Image>(true))if(image.name=="Mana" || image.name=="Cooldown")
                    {var r=image.rectTransform;r.offsetMin=new Vector2(0,r.offsetMin.y);r.offsetMax=new Vector2(0,r.offsetMax.y);}
                    ConfigureWandIcons(ui);
                }
                EditorSceneManager.SaveScene(scene,Review);Write("review-repaired.txt","Review格子视口采用Stretch锚点，内容左上锚点；状态提示位于背包区下缘。当前Scene未保存/覆盖。");
            }
            finally { EditorSceneManager.CloseScene(scene,true);SceneManager.SetActiveScene(original); }
        }
        private static void ConfigureGrid(SinglePlayerInventoryController ui)
        {
            var grid=Get<InventoryGridView>(ui,"_gridView");Record(grid);grid.ConfigureViewportFit(true);
            var viewport=(RectTransform)grid.transform.parent;Record(viewport);
            // offset仅表示到锚点的距离；必须先Stretch，否则相同offset会形成负尺寸视口。
            Stretch(viewport,new Vector2(8,28),new Vector2(-8,-60));
            var content=(RectTransform)grid.transform;Record(content);Top(content,Vector2.zero,content.sizeDelta);
            var surface=Add<Image>(viewport.gameObject);Record(surface);surface.color=new Color(0,0,0,.01f);
            var scroll=Add<ScrollRect>(viewport.gameObject);Record(scroll);scroll.viewport=viewport;scroll.content=content;
            scroll.horizontal=scroll.vertical=true;scroll.movementType=ScrollRect.MovementType.Clamped;scroll.scrollSensitivity=24;
        }
        private static void ConfigureStatus(SinglePlayerInventoryController ui)
        {
            if (Get<Text>(ui,"_status")!=null) return;
            var panel=Get<GameObject>(ui,"_panelRoot").transform;var bag=panel.Find("LeftPart/BackpackRegion");
            var status=TextChild(bag,"BackpackRuntime_Status",13);var r=status.rectTransform;
            r.anchorMin=new Vector2(0,0);r.anchorMax=new Vector2(1,0);r.pivot=new Vector2(0,0);r.offsetMin=new Vector2(8,2);r.offsetMax=new Vector2(-8,26);
            Set(ui,"_status",status);
        }
        [MenuItem("Tools/Norn/据点界面/接线背包左中与九格HUD（Undo，不保存）")]
        private static void ApplyCurrent()
        {
            var scene=SceneManager.GetActiveScene();
            if (scene.path!=SinglePlayerHubSceneSetup.Path) throw new InvalidOperationException("只允许正式据点当前Scene。");
            Undo.IncrementCurrentGroup(); int group=Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("接线背包专项"); _undo=true;
            try { string report=Apply(scene); Undo.CollapseUndoOperations(group); EditorSceneManager.MarkSceneDirty(scene); Write("current-applied.txt",report+"\n当前Scene未保存，请用户自行保存。\n"); }
            catch { Undo.RevertAllDownToGroup(group); throw; }
            finally { _undo=false; }
        }
        private static string Apply(Scene scene)
        {
            SinglePlayerInventoryController backpack=null,warehouse=null;
            foreach (var node in SinglePlayerHubHierarchySetup.All(scene)) if (node.TryGetComponent<SinglePlayerInventoryController>(out var ui))
            {
                if (Get<RectTransform>(ui,"_warehouseRoot")==null) backpack=ui; else warehouse=ui;
            }
            if (backpack==null || warehouse==null) throw new InvalidOperationException("需要用户已拆出的独立背包与仓库Canvas，拒绝猜测/生成整套界面。");
            if (Get<BackpackWorkspaceView>(backpack,"_backpackWorkspace")!=null) return "已接线，未创建重复节点。";
            var old=backpack.GetComponent<InventoryWorkspaceView>(); var panel=Get<GameObject>(backpack,"_panelRoot").transform;
            var bag=Get<RectTransform>(old,"_bag"); var wands=Get<RectTransform>(old,"_wands");
            var nearby=Get<RectTransform>(old,"_nearbyRegion"); var right=Get<Text>(backpack,"_details").transform.parent;
            string rightBefore=Snapshot(right),warehouseBefore=Snapshot(Get<GameObject>(warehouse,"_panelRoot").transform);
            var bagPose=new Pose(bag);var wandPose=new Pose(wands);var nearbyPose=new Pose(nearby);
            var session=Get<SinglePlayerInventorySession>(backpack,"_session");
            Set(warehouse,"_toggleAction",null); Set(backpack,"_otherWorkspace",warehouse); Set(warehouse,"_otherWorkspace",backpack);
            Record(old); old.enabled=false;
            var presenter=Add<BackpackWorkspaceView>(backpack.gameObject);Set(backpack,"_backpackWorkspace",presenter);
            var capacity=TextChild(bag,"BackpackRuntime_Capacity",16); Top(capacity.rectTransform,new Vector2(8,-32),new Vector2(320,24));
            var walletRoot=panel.Find("LeftPart/GoldRegion/Gold_Number");
            if (walletRoot==null || !walletRoot.TryGetComponent<Text>(out var wallet)) throw new InvalidOperationException("找不到用户金币文本，停止而不猜测覆盖区域。");
            ConfigureGrid(backpack); ConfigureStatus(backpack);
            foreach (var name in new[]{"WandRegion","WandDetails"}) { var legacy=wands.Find(name); if (legacy!=null) { Record(legacy.gameObject); legacy.gameObject.SetActive(false); } }
            var content=Scroll(wands,"BackpackRuntime_Wands",new Vector2(8,8),new Vector2(-8,-32),false);
            var layout=Add<VerticalLayoutGroup>(content.gameObject); layout.spacing=8; layout.childControlHeight=true;layout.childControlWidth=true;layout.childForceExpandHeight=false;
            var fit=Add<ContentSizeFitter>(content.gameObject);fit.verticalFit=ContentSizeFitter.FitMode.PreferredSize;
            var views=new WandProgramPanelView[3];
            for (int i=0;i<3;i++)
            {
                var row=Rect(content,"Wand"+(i+1));var bg=Add<Image>(row.gameObject);bg.color=new Color(.06f,.09f,.13f,.94f);
                var size=Add<LayoutElement>(row.gameObject);size.preferredHeight=206;size.minHeight=82;
                var header=TextChild(row,"Attributes",16);Top(header.rectTransform,new Vector2(8,-6),new Vector2(720,72));
                StretchWidth(header.rectTransform,8,-40);var target=Add<InventoryDropTarget>(header.gameObject);header.raycastTarget=true;
                var expanded=Rect(row,"Expanded");Stretch(expanded,new Vector2(8,8),new Vector2(-8,-86));
                var program=Scroll(expanded,"Program",Vector2.zero,Vector2.zero,true);
                views[i]=Add<WandProgramPanelView>(row.gameObject);views[i].Configure(i,header,program,expanded.gameObject,target);
            }
            var wandView=Add<WandInventoryView>(wands.gameObject);wandView.ConfigurePanels(views);Set(backpack,"_wandsView",wandView);
            var bar=panel.Find("MiddlePart/InventoryBar") as RectTransform;
            if (bar==null) throw new InvalidOperationException("找不到用户道具栏区域。");
            var toolsRoot=Rect(bar,"BackpackRuntime_Tools");Stretch(toolsRoot,new Vector2(6,4),new Vector2(-6,-30));
            var toolsLayout=Add<HorizontalLayoutGroup>(toolsRoot.gameObject);toolsLayout.spacing=4;toolsLayout.childControlWidth=toolsLayout.childControlHeight=true;toolsLayout.childForceExpandWidth=true;
            var tools=Add<ToolbeltView>(toolsRoot.gameObject);var slots=new ToolbeltSlotView[6];
            for (int i=0;i<6;i++)
            {
                var slot=Rect(toolsRoot,"Slot"+(i+4));Add<Image>(slot.gameObject).color=new Color(.08f,.14f,.20f,.95f);Add<LayoutElement>(slot.gameObject).flexibleWidth=1;
                var icon=Add<Image>(Rect(slot,"Icon").gameObject);Stretch(icon.rectTransform,new Vector2(4,4),new Vector2(-4,-4));icon.raycastTarget=false;icon.preserveAspect=true;
                var caption=TextChild(slot,"Caption",14);Stretch(caption.rectTransform,new Vector2(2,2),new Vector2(-2,-2));caption.alignment=TextAnchor.MiddleCenter;
                slots[i]=Add<ToolbeltSlotView>(slot.gameObject);slots[i].Configure(i,icon,caption);
            }
            tools.Configure(slots);presenter.Configure(capacity,wallet,tools);ConfigureWandIcons(backpack);
            var player=Find<WizardController>(scene);var held=Add<HeldItemInputController>(player.gameObject);Set(held,"_session",session);
            BuildHud(scene,session,backpack,warehouse);
            bagPose.Verify();wandPose.Verify();nearbyPose.Verify();
            if (rightBefore!=Snapshot(right) || warehouseBefore!=Snapshot(Get<GameObject>(warehouse,"_panelRoot").transform))
                throw new InvalidOperationException("接线越界修改了右侧/仓库内容。");
            return "背包外框/附近外框/法杖外框、右侧全部组件与仓库全部组件保持；新六槽/三杖/九格HUD独立接线。";
        }
        private static void BuildHud(Scene scene,SinglePlayerInventorySession session,SinglePlayerInventoryController backpack,SinglePlayerInventoryController warehouse)
        {
            var old=Find<WandResourceHud>(scene);Record(old);old.enabled=false;
            for (int i=0;i<old.transform.childCount;i++) { var child=old.transform.GetChild(i).gameObject;Record(child);child.SetActive(false); }
            var canvas=NewHudCanvas(scene);
            var root=Rect(canvas.transform,"HeldItemRow_1至9");Top(root,new Vector2(16,-16),new Vector2(900,94));
            var layout=Add<HorizontalLayoutGroup>(root.gameObject);layout.spacing=4;layout.childControlWidth=layout.childControlHeight=true;layout.childForceExpandWidth=true;
            var cells=new HeldItemHud.Cell[9];
            for (int i=0;i<9;i++)
            {
                var slot=Rect(root,"Slot"+(i+1));var selection=Add<Image>(slot.gameObject);Add<LayoutElement>(slot.gameObject).flexibleWidth=1;
                var icon=Add<Image>(Rect(slot,"Icon").gameObject);Stretch(icon.rectTransform,new Vector2(8,16),new Vector2(-8,-22));icon.raycastTarget=false;icon.preserveAspect=true;
                var title=TextChild(slot,"Title",12);Top(title.rectTransform,new Vector2(4,-2),new Vector2(90,24));StretchWidth(title.rectTransform,4,-4);
                var value=TextChild(slot,"Value",12);value.alignment=TextAnchor.LowerCenter;Stretch(value.rectTransform,new Vector2(2,8),new Vector2(-2,-24));
                var mana=Bar(slot,"Mana",new Color(.1f,.6f,.95f,1),0);var cooldown=Bar(slot,"Cooldown",new Color(1,.45f,.1f,1),4);
                cells[i]=new HeldItemHud.Cell{Icon=icon,Selection=selection,Title=title,Value=value,Mana=mana,Cooldown=cooldown};
            }
            var hud=Add<HeldItemHud>(root.gameObject);hud.Configure(session,cells);
            hud.ConfigureVisibility(backpack,warehouse,Find<ShopController>(scene),canvas);
        }
        private static Canvas NewHudCanvas(Scene scene)
        {
            var old=Find<WandResourceHud>(scene);var root=Rect(old.transform.parent,"PlayerHUDCanvas_统一手持栏");
            var canvas=Add<Canvas>(root.gameObject);canvas.renderMode=RenderMode.ScreenSpaceOverlay;canvas.sortingOrder=10;
            var scaler=Add<CanvasScaler>(root.gameObject);scaler.uiScaleMode=CanvasScaler.ScaleMode.ScaleWithScreenSize;scaler.referenceResolution=new Vector2(1920,1080);scaler.matchWidthOrHeight=.5f;
            return canvas;
        }
        private static void ConfigureWandIcons(SinglePlayerInventoryController ui)
        {
            foreach(var view in ui.GetComponentsInChildren<WandProgramPanelView>(true))
            {
                var fields=new SerializedObject(view);
                if(fields.FindProperty("_icon").objectReferenceValue!=null)continue;
                var authored=view.transform.Find("Frame/WandIcon")??view.transform.Find("WandIcon");
                if(authored!=null){Set(view,"_icon",authored.GetComponent<Image>());continue;}
                var icon=Add<Image>(Rect(view.transform,"WandIcon").gameObject);Top(icon.rectTransform,new Vector2(12,-8),new Vector2(200,64));icon.raycastTarget=false;icon.preserveAspect=true;
                Set(view,"_icon",icon);
            }
        }
        private static Image Bar(RectTransform parent,string name,Color color,float y)
        { var image=Add<Image>(Rect(parent,name).gameObject);image.color=color;image.raycastTarget=false;image.type=Image.Type.Filled;image.fillMethod=Image.FillMethod.Horizontal;image.fillAmount=0;
            var r=image.rectTransform;r.anchorMin=new Vector2(0,0);r.anchorMax=new Vector2(1,0);r.offsetMin=new Vector2(0,y);r.offsetMax=new Vector2(0,y+3);return image; }
        private static RectTransform Scroll(Transform parent,string name,Vector2 min,Vector2 max,bool horizontal)
        {
            var root=Rect(parent,name);Stretch(root,min,max);var viewport=Rect(root,"Viewport");Stretch(viewport,Vector2.zero,Vector2.zero);Add<RectMask2D>(viewport.gameObject);
            var content=Rect(viewport,"Content");content.anchorMin=new Vector2(0,1);content.anchorMax=new Vector2(horizontal?0:1,1);content.pivot=new Vector2(0,1);content.sizeDelta=Vector2.zero;
            var scroll=Add<ScrollRect>(root.gameObject);scroll.viewport=viewport;scroll.content=content;scroll.horizontal=horizontal;scroll.vertical=!horizontal;scroll.movementType=ScrollRect.MovementType.Clamped;scroll.scrollSensitivity=26;
            // 空白区域也必须能接收滚轮，不能只有法术图标上才响应ScrollRect。
            Add<Image>(viewport.gameObject).color=new Color(0,0,0,.01f);return content;
        }
        private static RectTransform Rect(Transform parent,string name)
        { var go=new GameObject(name,typeof(RectTransform));if(_undo)Undo.RegisterCreatedObjectUndo(go,"背包接线");go.transform.SetParent(parent,false);return (RectTransform)go.transform; }
        private static Text TextChild(Transform parent,string name,int size)
        { var t=Add<Text>(Rect(parent,name).gameObject);t.font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");t.fontSize=size;t.color=Color.white;t.raycastTarget=false;return t; }
        private static void Top(RectTransform r,Vector2 pos,Vector2 size) { r.anchorMin=r.anchorMax=r.pivot=new Vector2(0,1);r.anchoredPosition=pos;r.sizeDelta=size; }
        private static void Stretch(RectTransform r,Vector2 min,Vector2 max) { r.anchorMin=Vector2.zero;r.anchorMax=Vector2.one;r.offsetMin=min;r.offsetMax=max; }
        private static void StretchWidth(RectTransform r,float left,float right) { r.anchorMin=new Vector2(0,1);r.anchorMax=new Vector2(1,1);r.offsetMin=new Vector2(left,r.offsetMin.y);r.offsetMax=new Vector2(right,r.offsetMax.y); }
        private static T Add<T>(GameObject go) where T:Component { var value=go.GetComponent<T>();return value!=null?value:(_undo?Undo.AddComponent<T>(go):go.AddComponent<T>()); }
        private static void Record(Object o) { if (_undo) Undo.RecordObject(o,"背包接线"); }
        private static T Get<T>(Object target,string name) where T:Object=>new SerializedObject(target).FindProperty(name)?.objectReferenceValue as T;
        private static void Set(Object target,string name,Object value) { Record(target);var data=new SerializedObject(target);data.FindProperty(name).objectReferenceValue=value;data.ApplyModifiedPropertiesWithoutUndo(); }
        private static T Find<T>(Scene scene) where T:Component { foreach(var root in scene.GetRootGameObjects()){var value=root.GetComponentInChildren<T>(true);if(value!=null)return value;}throw new InvalidOperationException("缺少"+typeof(T).Name); }
        private static string Snapshot(Transform root) { var text=new System.Text.StringBuilder();foreach(var c in root.GetComponentsInChildren<Component>(true))if(c!=null)text.Append(c.GetInstanceID()).Append(EditorJsonUtility.ToJson(c));return text.ToString(); }
        private sealed class Pose
        {
            private readonly RectTransform _r;private readonly string _json;
            internal Pose(RectTransform r) { _r=r;_json=EditorJsonUtility.ToJson(r); }
            internal void Verify() { if(_json!=EditorJsonUtility.ToJson(_r))throw new InvalidOperationException("改变了用户外框"+_r.name); }
        }
        private static void Write(string name,string text) { Directory.CreateDirectory(Evidence);File.WriteAllText(Evidence+"/"+name,text); }
    }
}
