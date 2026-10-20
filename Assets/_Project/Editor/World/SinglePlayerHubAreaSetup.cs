using System;
using System.IO;
using System.Text;
using Game.Core;
using Game.Rendering;
using Game.Run;
using Game.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.EditorTools
{
    /// <summary>只在显式请求执行；先诊断/复制Review，正式Scene接线必须单Undo且不保存。</summary>
    [InitializeOnLoad]
    internal static class SinglePlayerHubAreaSetup
    {
        internal const string Review = "Assets/_Project/Scenes/Tests/Hub/SinglePlayer_Hub_AreaReview.unity";
        internal const string Evidence = "TestResults/SinglePlayer/M6-T1-Areas";
        private const string Request = "Temp/SinglePlayer/hub-area-setup.request";
        private static bool _undo;
        static SinglePlayerHubAreaSetup() { EditorApplication.update += Poll; }
        private static void Poll()
        {
            if (!File.Exists(Request) || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
            string command = File.ReadAllText(Request).Trim(); File.Delete(Request);
            try
            {
                if (EditorUtility.scriptCompilationFailed) throw new InvalidOperationException("编译失败，不使用旧Assembly接线。");
                if (command == "inspect") Write("before.txt", Inspect(SceneManager.GetActiveScene()));
                else if (command == "copy-review") CopyReview();
                else if (command == "prepare") Prepare();
                else if (command == "apply-current") ApplyCurrent();
                else throw new InvalidOperationException("未知区域命令：" + command);
            }
            catch (Exception error) { Write("setup-error-" + DateTime.UtcNow.Ticks + ".txt", error.ToString()); GameLog.Error(error.ToString(), "HubAreas"); }
        }
        private static void CopyReview()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++) if (SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("存在未保存Scene；不替用户保存或丢弃。");
            if (File.Exists(Review)) throw new IOException("新Review已存在，拒绝覆盖。");
            if (!AssetDatabase.CopyAsset(SinglePlayerHubSceneSetup.Path, Review)) throw new IOException("复制Review失败。");
            Write("copied.txt", DateTime.UtcNow.ToString("O") + "\n从用户已保存的正式Scene复制未修复Review，用于RED。");
        }
        private static void Prepare()
        {
            for (int i=0;i<SceneManager.sceneCount;i++) if(SceneManager.GetSceneAt(i).isDirty)throw new InvalidOperationException("Scene未保存，不代用户处理。");
            if(SceneManager.GetSceneByPath(Review).isLoaded)throw new InvalidOperationException("Review已由用户打开，不自动保存。");
            var original=SceneManager.GetActiveScene();var scene=EditorSceneManager.OpenScene(Review,OpenSceneMode.Additive);
            try
            {
                // 所有临时控件先进入Review，避免新GameObject创建到用户当前Scene。
                SceneManager.SetActiveScene(scene);_undo=false;string report=Apply(scene);
                if(!EditorSceneManager.SaveScene(scene,Review))throw new IOException("保存Review失败。");
                SessionState.SetString("Norn.HubAlignment.ReviewPath",Review);Write("prepared.txt",report);
            }
            finally {EditorSceneManager.CloseScene(scene,true);SceneManager.SetActiveScene(original);}
        }
        [MenuItem("Tools/Norn/据点界面/修复交互区域、准星与大背包格（Undo，不保存）")]
        private static void ApplyCurrent()
        {
            var scene=SceneManager.GetActiveScene();if(scene.path!=SinglePlayerHubSceneSetup.Path || EditorApplication.isPlaying)throw new InvalidOperationException("只允许正式据点Edit Mode。");
            Undo.IncrementCurrentGroup();int group=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("据点区域与背包可读性");_undo=true;
            try
            {
                string report=Apply(scene);Undo.CollapseUndoOperations(group);EditorSceneManager.MarkSceneDirty(scene);
                // Review路径只属于本次验证；交付后后续正式场景检查应读取用户当前配置，避免使用旧副本。
                SessionState.EraseString("Norn.HubAlignment.ReviewPath");
                Write("current-applied.txt",report+"\n正式Scene未保存，请用户自行Ctrl+S。");
            }
            catch {Undo.RevertAllDownToGroup(group);throw;}
            finally {_undo=false;}
        }
        private static string Apply(Scene scene)
        {
            var hub=Find<HubInteractionController>(scene);var experiment=Find<ExperimentAreaController>(scene);
            SinglePlayerInventoryController backpack=null,warehouse=null;
            foreach(var root in scene.GetRootGameObjects())foreach(var ui in root.GetComponentsInChildren<SinglePlayerInventoryController>(true))
                if(Get<BackpackWorkspaceView>(ui,"_backpackWorkspace")!=null)backpack=ui;else warehouse=ui;
            if(backpack==null || warehouse==null)throw new InvalidOperationException("需要用户已拆出的背包/仓库。");
            var panel=Get<GameObject>(backpack,"_panelRoot").transform;
            string outer=Pose(panel.Find("LeftPart"))+Pose(panel.Find("MiddlePart"))+Pose(panel.Find("RightPart"));
            string right=Snapshot(panel.Find("RightPart")),warehouseBefore=Snapshot(Get<GameObject>(warehouse,"_panelRoot").transform);
            // 用户为预览背包关闭了两个整个Canvas，连入口控制器也停用。保持Canvas根常驻，只隐藏视觉面板。
            // 被隐藏的交互提示Canvas继续隐藏，不把旧提示文字重新带回画面。
            var shop=Find<ShopController>(scene);
            Record(warehouse.gameObject);warehouse.gameObject.SetActive(true);
            var warehousePanel=Get<GameObject>(warehouse,"_panelRoot");Record(warehousePanel);warehousePanel.SetActive(false);
            Record(shop.gameObject);shop.gameObject.SetActive(true);
            var shopPanel=Get<GameObject>(shop,"_panel");Record(shopPanel);shopPanel.SetActive(false);
            foreach(var station in hub.Definition.Stations)
            {
                if(station.Kind!=HubStationKind.Shop && station.Kind!=HubStationKind.Warehouse)continue;
                var marker=Named(scene,station.DisplayName+" · E");string name=station.Kind==HubStationKind.Shop?"ShopArea_商店区域":"WarehouseArea_仓库区域";
                var area=Area(marker,name,new Vector3(12,6,station.Kind==HubStationKind.Shop?14:12),Vector3.up*2.5f);
                Set(hub,station.Kind==HubStationKind.Shop?"_shopArea":"_warehouseArea",area);
            }
            var platform=Named(scene,"实验平台 · 真实库存");float scaleY=Mathf.Abs(platform.lossyScale.y);
            if(scaleY<.00001f)throw new InvalidOperationException("实验平台Y缩放为0。");
            // X/Z沿原平台单位盒子覆盖；Y补偿平台的0.3缩放，使作者许可始终有6m垂直高度。
            var experimentArea=Area(platform,"ExperimentArea_实验区",new Vector3(1,6/scaleY,1),new Vector3(0,.5f+3/scaleY,0));
            Set(experiment,"_area",experimentArea);
            ConfigureGrid(backpack);
            var hud=Find<HeldItemHud>(scene);var canvas=hud.GetComponentInParent<Canvas>(true);var dot=Named(scene,"Crosshair");
            Record(dot);if(_undo)Undo.SetTransformParent(dot,canvas.transform,"恢复准星");else dot.SetParent(canvas.transform,false);
            var rect=(RectTransform)dot;Record(rect);rect.anchorMin=rect.anchorMax=rect.pivot=new Vector2(.5f,.5f);
            rect.anchoredPosition=Vector2.zero;rect.sizeDelta=new Vector2(5,5);rect.localScale=Vector3.one;rect.localRotation=Quaternion.identity;
            Record(dot.gameObject);dot.gameObject.SetActive(true);var image=dot.GetComponent<Image>();Record(image);image.color=Color.white;image.raycastTarget=false;
            var consumer=Add<CrosshairUI>(canvas.gameObject);Set(consumer,"_crosshair",dot.gameObject);
            if(outer!=Pose(panel.Find("LeftPart"))+Pose(panel.Find("MiddlePart"))+Pose(panel.Find("RightPart")))throw new InvalidOperationException("用户外框发生改变。");
            if(right!=Snapshot(panel.Find("RightPart")) || warehouseBefore!=Snapshot(Get<GameObject>(warehouse,"_panelRoot").transform))throw new InvalidOperationException("右侧/仓库发生改变。");
            return DateTime.UtcNow.ToString("O")+"\n三处Scene BoxCollider区域接线；实验随实际平台，保留平台碰撞。Grid72与双Scrollbar；准星独立HUD。外框/右栏/仓库审计一致。\n"+Inspect(scene);
        }
        private static HubAreaVolume Area(Transform parent,string name,Vector3 size,Vector3 center)
        {
            var child=parent.Find(name);bool created=child==null;
            if(created){var go=new GameObject(name);if(_undo)Undo.RegisterCreatedObjectUndo(go,"创建区域");child=go.transform;child.SetParent(parent,false);}
            var box=Add<BoxCollider>(child.gameObject);Record(box);box.isTrigger=true;
            // 再次接线只检查引用，不覆盖用户后来用Edit Collider调整的区域尺寸。
            if(created){box.size=size;box.center=center;}
            var area=Add<HubAreaVolume>(child.gameObject);Set(area,"_volume",box);return area;
        }
        private static void ConfigureGrid(SinglePlayerInventoryController ui)
        {
            var grid=Get<InventoryGridView>(ui,"_gridView");Record(grid);var data=new SerializedObject(grid);
            data.FindProperty("_fitToViewport").boolValue=false;data.FindProperty("_cellSize").floatValue=72;data.ApplyModifiedPropertiesWithoutUndo();
            var viewport=(RectTransform)grid.transform.parent;var scroll=viewport.GetComponent<ScrollRect>();
            var workspace=Get<BackpackWorkspaceView>(ui,"_backpackWorkspace");
            if(Get<Text>(workspace,"_capacity")==null)
            {
                // 用户删掉旧容量文字后引用为空；只补这一项业务显示，不重建左列或挪动网格外框。
                var label=viewport.parent.Find("BackpackRuntime_Capacity") as RectTransform;
                if(label==null)label=Rect(viewport.parent,"BackpackRuntime_Capacity");
                var text=Add<Text>(label.gameObject);Record(text);text.font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");text.fontSize=18;text.color=Color.white;text.raycastTarget=false;
                Set(workspace,"_capacity",text);
            }
            var capacity=Get<Text>(workspace,"_capacity");
            if(capacity.name=="BackpackRuntime_Capacity")
            {
                // 容量文字占独立顶行；缩小内部Viewport的可见高度，不能把文字叠在第一格物品上。
                var label=capacity.rectTransform;Record(label);label.anchorMin=new Vector2(0,1);label.anchorMax=new Vector2(1,1);label.pivot=new Vector2(0,1);
                label.offsetMin=new Vector2(8,-26);label.offsetMax=new Vector2(-8,-2);
                Record(viewport);var inset=viewport.offsetMax;inset.y=Mathf.Min(inset.y,-32);viewport.offsetMax=inset;
            }
            if(scroll==null)throw new InvalidOperationException("缺少已有Grid ScrollRect。");
            Record(scroll);scroll.horizontal=scroll.vertical=true;scroll.movementType=ScrollRect.MovementType.Clamped;scroll.scrollSensitivity=36;
            scroll.horizontalScrollbar=Bar(viewport,"HorizontalScrollbar",false);scroll.verticalScrollbar=Bar(viewport,"VerticalScrollbar",true);
            // ScrollRect与Viewport是同一对象，避免AutoHideAndExpandViewport反向驱动父容器；仅自动隐藏不改外框。
            scroll.horizontalScrollbarVisibility=scroll.verticalScrollbarVisibility=ScrollRect.ScrollbarVisibility.AutoHide;
            if(viewport.GetComponent<RectMask2D>()==null)Add<RectMask2D>(viewport.gameObject);
        }
        private static Scrollbar Bar(RectTransform parent,string name,bool vertical)
        {
            var child=parent.Find(name) as RectTransform;if(child==null)child=Rect(parent,name);
            Record(child);child.anchorMin=vertical?new Vector2(1,0):new Vector2(0,0);child.anchorMax=vertical?new Vector2(1,1):new Vector2(1,0);
            child.pivot=vertical?new Vector2(1,1):Vector2.zero;
            child.offsetMin=vertical?new Vector2(-14,16):new Vector2(0,0);child.offsetMax=vertical?Vector2.zero:new Vector2(-16,14);
            var background=Add<Image>(child.gameObject);Record(background);background.color=new Color(.08f,.13f,.18f,1);
            var handle=child.Find("Handle") as RectTransform;if(handle==null)handle=Rect(child,"Handle");
            Record(handle);handle.anchorMin=Vector2.zero;handle.anchorMax=Vector2.one;handle.offsetMin=Vector2.one*2;handle.offsetMax=Vector2.one*-2;
            var image=Add<Image>(handle.gameObject);Record(image);image.color=new Color(.75f,.65f,.35f,1);
            var bar=Add<Scrollbar>(child.gameObject);Record(bar);bar.handleRect=handle;bar.targetGraphic=image;
            bar.direction=vertical?Scrollbar.Direction.BottomToTop:Scrollbar.Direction.LeftToRight;bar.numberOfSteps=0;
            var navigation=bar.navigation;navigation.mode=Navigation.Mode.None;bar.navigation=navigation;return bar;
        }
        private static RectTransform Rect(Transform parent,string name)
        {var go=new GameObject(name,typeof(RectTransform));if(_undo)Undo.RegisterCreatedObjectUndo(go,"创建滚动条");go.transform.SetParent(parent,false);return (RectTransform)go.transform;}
        private static T Add<T>(GameObject go)where T:Component {var value=go.GetComponent<T>();return value!=null?value:_undo?Undo.AddComponent<T>(go):go.AddComponent<T>();}
        private static void Record(UnityEngine.Object value){if(_undo)Undo.RecordObject(value,"据点区域与背包可读性");}
        private static T Get<T>(UnityEngine.Object target,string name)where T:UnityEngine.Object=>new SerializedObject(target).FindProperty(name)?.objectReferenceValue as T;
        private static void Set(UnityEngine.Object target,string name,UnityEngine.Object value){Record(target);var data=new SerializedObject(target);data.FindProperty(name).objectReferenceValue=value;data.ApplyModifiedPropertiesWithoutUndo();}
        private static Transform Named(Scene scene,string name)
        {foreach(var root in scene.GetRootGameObjects())foreach(var node in root.GetComponentsInChildren<Transform>(true))if(node.name==name)return node;throw new InvalidOperationException("缺少节点"+name);}
        private static string Pose(Transform node)=>EditorJsonUtility.ToJson(node);
        private static string Snapshot(Transform root)
        {var text=new StringBuilder();foreach(var component in root.GetComponentsInChildren<Component>(true))if(component!=null)text.Append(component.GetInstanceID()).Append(EditorJsonUtility.ToJson(component));return text.ToString();}
        private static string Inspect(Scene scene)
        {
            var text = new StringBuilder(); text.AppendLine(DateTime.UtcNow.ToString("O")).AppendLine(scene.path + " Dirty=" + scene.isDirty);
            var hub = Find<HubInteractionController>(scene); text.AppendLine("Hub=" + JsonUtility.ToJson(hub));
            text.AppendLine("Definition=" + JsonUtility.ToJson(hub.Definition));
            foreach (var root in scene.GetRootGameObjects()) foreach (var node in root.GetComponentsInChildren<Transform>(true))
            {
                if (node.name.Contains(" · E") || node.name == "实验平台 · 真实库存" || node.name == "Crosshair")
                    text.AppendLine(Hierarchy(node) + " position=" + node.position + " scale=" + node.lossyScale + " active=" + node.gameObject.activeInHierarchy);
                if (node.TryGetComponent<InventoryGridView>(out var grid)) text.AppendLine(Hierarchy(node) + " " + JsonUtility.ToJson(grid));
                if (node.TryGetComponent<MonoBehaviour>(out _) && node.GetComponent("CrosshairUI") != null) text.AppendLine("Crosshair consumer: " + Hierarchy(node));
            }
            return text.ToString();
        }
        private static string Hierarchy(Transform node) { string path = node.name; while (node.parent != null) { node = node.parent; path = node.name + "/" + path; } return path; }
        private static T Find<T>(Scene scene) where T : Component
        { foreach (var root in scene.GetRootGameObjects()) { var value = root.GetComponentInChildren<T>(true); if (value != null) return value; } throw new InvalidOperationException("缺少" + typeof(T).Name); }
        private static void Write(string name, string value) { Directory.CreateDirectory(Evidence); File.WriteAllText(Evidence + "/" + name, value); }
    }
}
