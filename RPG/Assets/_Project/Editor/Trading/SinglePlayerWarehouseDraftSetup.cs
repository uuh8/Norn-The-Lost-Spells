using System;
using System.IO;
using System.Security.Cryptography;
using Game.Core;
using Game.UI;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Game.EditorTools
{
    /// <summary>基于用户复制的Canvas做可撤销草稿。只改UI接线与RectTransform，不生成库存或保存当前Scene。</summary>
    [InitializeOnLoad]
    internal static class SinglePlayerWarehouseDraftSetup
    {
        private const string Formal = "Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity";
        private const string Review = "Assets/_Project/Scenes/Tests/Hub/SinglePlayer_Hub_WarehouseDraftReview.unity";
        private const string Request = "Temp/SinglePlayer/warehouse-draft.request";
        private const string Evidence = "TestResults/SinglePlayer/M6-T1-WarehouseDraft";
        private const string UndoName = "仓库区域草稿与Tab接线";
        private static bool _undo;
        static SinglePlayerWarehouseDraftSetup() => EditorApplication.update += Poll;
        private static void Poll()
        {
            if (!File.Exists(Request) || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
            string command = File.ReadAllText(Request).Trim(); File.Delete(Request);
            try
            {
                if (EditorUtility.scriptCompilationFailed) throw new InvalidOperationException("编译失败，拒绝旧Assembly。");
                if (command == "prepare") Prepare();
                else if (command == "apply-current") ApplyCurrent();
                else throw new ArgumentException(command);
            }
            catch (Exception e) { Write("setup-error.txt", e.ToString()); GameLog.Error(e.ToString(), "WarehouseDraft"); }
        }
        private static void Prepare()
        {
            for (int i=0;i<SceneManager.sceneCount;i++)
                if (SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("用户Scene未保存；不代为保存或丢弃。");
            if (SceneManager.GetSceneByPath(Review).isLoaded) throw new InvalidOperationException("Review已打开，拒绝覆盖。");
            if (!File.Exists(Review) && !AssetDatabase.CopyAsset(Formal,Review)) throw new IOException("复制Review失败。");
            var original = SceneManager.GetActiveScene(); var scene = EditorSceneManager.OpenScene(Review, OpenSceneMode.Additive);
            try { _undo=false; Apply(scene); EditorSceneManager.SaveScene(scene,Review); Write("prepared.txt",Review); }
            finally { EditorSceneManager.CloseScene(scene,true); SceneManager.SetActiveScene(original); }
        }
        [MenuItem("Tools/Norn/仓库/应用仓库颜色草稿与Tab修复（Undo，不保存）")]
        private static void ApplyCurrent()
        {
            var scene=SceneManager.GetActiveScene();
            if (scene.path!=Formal) throw new InvalidOperationException("只处理正式据点当前Scene。");
            string before=Hash(); Undo.IncrementCurrentGroup(); int group=Undo.GetCurrentGroup(); Undo.SetCurrentGroupName(UndoName);
            try
            {
                _undo=true; Apply(scene);
                // Edit Mode交付时直接展示草稿方便精修；Play Mode的Start仍会隐藏内部Panel。
                foreach(var root in scene.GetRootGameObjects())foreach(var ui in root.GetComponentsInChildren<SinglePlayerInventoryController>(true))
                    if(ui.name=="InventoryCanvas_仓库"){var panel=Get<GameObject>(ui,"_panelRoot");Record(panel);panel.SetActive(true);}
                if (Hash()!=before) throw new InvalidOperationException("正式Scene磁盘意外变化。");
                Undo.FlushUndoRecordObjects(); Undo.CollapseUndoOperations(group); EditorSceneManager.MarkSceneDirty(scene);
                Write("current-applied.txt","Tab仅背包；E独立仓库；五色草稿与两套Ghost保留；一组Undo。\nScene未保存，磁盘SHA256保持："+before);
            }
            catch { Undo.RevertAllDownToGroup(group); throw; }
            finally { _undo=false; }
        }
        private static void Apply(Scene scene)
        {
            SinglePlayerInventoryController bag=null,store=null;
            foreach (var root in scene.GetRootGameObjects()) foreach (var ui in root.GetComponentsInChildren<SinglePlayerInventoryController>(true))
            { if(ui.name=="InventoryCanvas_背包")bag=ui; if(ui.name=="InventoryCanvas_仓库")store=ui; }
            if (bag==null || store==null) throw new InvalidOperationException("需要用户命名的背包和仓库Canvas，不能猜测别的窗口。");
            var panel=Get<GameObject>(store,"_panelRoot").transform;
            if (panel.Find("LeftPart/WarehouseRegion_仓库存储")!=null) { RepairWindows(bag,store); RefineDraft(store); return; }
            var left=(RectTransform)panel.Find("LeftPart"); var middle=(RectTransform)panel.Find("MiddlePart"); var right=(RectTransform)panel.Find("RightPart");
            var carry=(RectTransform)left.Find("BackpackRegion"); var stock=(RectTransform)left.Find("NearbyItemsRegion");
            var programming=(RectTransform)middle.Find("WandProgrammingRegion");
            var ground=Get<RectTransform>(store,"_groundRoot"); var grid=Get<InventoryGridView>(store,"_gridView");
            if(carry==null || stock==null || programming==null || ground==null || grid==null) throw new InvalidOperationException("复制的区域不完整，拒绝重新生成全套UI。");
            // 先创建所有新节点，再记录旧组件；RegisterCreatedObjectUndo会Flush RecordObject队列。
            Label(panel,"WarehouseDraft_Title","仓库与魔法整备",new Vector2(.09f,.90f),new Vector2(.66f,.985f),36);
            Label(left,"WarehouseDraft_Header","仓库 · 存储物品",new Vector2(.04f,.93f),new Vector2(.96f,.99f),26);
            Label(left,"WarehouseDraft_Search","搜索物品…（功能预留）",new Vector2(.04f,.85f),new Vector2(.96f,.92f),20);
            Label(left,"WarehouseDraft_Filters","全部  |  法术  |  容器  |  材料  |  法杖",new Vector2(.04f,.77f),new Vector2(.96f,.84f),18);
            Label(middle,"WarehouseDraft_CarryHeader","携带背包 · 当前装备与格子",new Vector2(.03f,.94f),new Vector2(.97f,.995f),24);
            Label(middle,"WarehouseDraft_WandHeader","法杖编程 · 三根法杖",new Vector2(.03f,.53f),new Vector2(.97f,.59f),24);
            Label(right,"WarehouseDraft_DetailsHeader","选中物品详情",new Vector2(.04f,.93f),new Vector2(.96f,.995f),25);
            Label(right,"WarehouseDraft_FutureActions","保护 / 材料附着：区域预留",new Vector2(.04f,.22f),new Vector2(.96f,.32f),18);
            var preview=Region(middle,"BackpackEquipmentPreview_装备预览",new Color(.12f,.40f,.25f,.85f),new Vector2(.72f,.61f),new Vector2(.97f,.93f));
            Label(preview,"Caption","当前背包\n\n装备预览\n（美术预留）",new Vector2(.03f,.05f),new Vector2(.97f,.95f),20);
            RepairWindows(bag,store);
            Anchors(left,new Vector2(.01f,.03f),new Vector2(.275f,.89f));
            Anchors(middle,new Vector2(.285f,.03f),new Vector2(.735f,.89f));
            Anchors(right,new Vector2(.745f,.03f),new Vector2(.99f,.89f));
            foreach(var name in new[]{"Title_Backpack","Title_NearbyItems"}) Hide(left.Find(name));
            Hide(middle.Find("Title")); Hide(right.Find("Title")); Hide(middle.Find("InventoryBar")); Hide(panel.Find("Logo"));
            foreach(var part in new[]{left,middle,right}) Tint(part.Find("bg"),new Color(.07f,.12f,.18f,1));
            // 搬动整个作者区域保留内部ScrollRect、Scrollbar和Grid引用；不复制真实物品。
            if(_undo)Undo.SetTransformParent(carry,middle,UndoName); else carry.SetParent(middle,false);
            Record(carry.gameObject); carry.name="CarriedBackpackRegion_携带背包";
            Anchors(carry,new Vector2(.03f,.61f),new Vector2(.70f,.93f)); Tint(carry,new Color(.10f,.33f,.20f,.92f));
            Record(stock.gameObject); stock.name="WarehouseRegion_仓库存储";
            Anchors(stock,new Vector2(.03f,.10f),new Vector2(.97f,.76f)); Tint(stock,new Color(.08f,.20f,.44f,.92f));
            Anchors(programming,new Vector2(.02f,.02f),new Vector2(.98f,.53f)); Tint(programming,new Color(.48f,.22f,.07f,.92f));
            var palette=Get<SpellStorageView>(store,"_spellStorage");
            if(palette!=null)Tint(palette.transform,new Color(.63f,.52f,.12f,.92f));
            // 下半区继续使用现有三程序面板，不接回旧Arrange，用户调整不会在打开时被覆盖。
            var oldView=Get<BackpackWorkspaceView>(store,"_backpackWorkspace");if(oldView!=null){Record(oldView);oldView.enabled=false;}
            var nearby=Get<NearbyItemGridView>(store,"_nearbyGrid");if(nearby!=null){Record(nearby);nearby.enabled=false;}
            Set(store,"_backpackWorkspace",null);Set(store,"_workspace",null);Set(store,"_nearbyGrid",null);
            Set(store,"_groundRoot",null);Set(store,"_groundTarget",null);Set(store,"_warehouseRoot",ground);
            var target=stock.GetComponent<InventoryDropTarget>();if(target==null)target=Add<InventoryDropTarget>(stock.gameObject);
            Set(store,"_warehouseTarget",target);
            Record(store);var data=new SerializedObject(store);data.FindProperty("_warehouseTiles").boolValue=true;data.ApplyModifiedPropertiesWithoutUndo();
            var viewport=ground.parent as RectTransform; if(viewport!=null)Anchors(viewport,new Vector2(.02f,.02f),new Vector2(.96f,.98f));
            var details=Get<Text>(store,"_details"); if(details!=null){Anchors(details.rectTransform,new Vector2(.04f,.36f),new Vector2(.96f,.91f));}
            var gridData=new SerializedObject(grid);Record(grid);gridData.FindProperty("_fitToViewport").boolValue=false;gridData.ApplyModifiedPropertiesWithoutUndo();
            // 旧按钮监听器可能指向已停用的复制Workspace；换成实际窗口控制器，空的占位功能不伪装可用。
            foreach(var button in right.GetComponentsInChildren<Button>(true))
            {
                if(button.name=="Pickup")Hide(button.transform);
                else if(button.name=="Drop")Wire(button,store.DropSelected);
                else {Record(button);button.interactable=false;}
            }
            var close=panel.Find("Close")?.GetComponent<Button>();if(close!=null)Wire(close,store.Close);
            foreach(var button in panel.GetComponentsInChildren<Button>(true))
                if(button.name=="Protect" || button.name=="Unprotect" || button.name=="ApplyAttachment" || button.name=="ClearAttachment") {Record(button);button.interactable=false;}
            Tint(right.Find("ItemDetailsRegion_物品详情"),new Color(.40f,.16f,.48f,.92f));
            foreach(var label in left.GetComponentsInChildren<Text>(true))if(label.name=="BackpackRuntime_Capacity" || label.name=="BackpackRuntime_Status")Hide(label.transform);
            RefineDraft(store);
        }
        private static void RefineDraft(SinglePlayerInventoryController store)
        {
            var panel=Get<GameObject>(store,"_panelRoot").transform;var middle=(RectTransform)panel.Find("MiddlePart");var right=(RectTransform)panel.Find("RightPart");
            Anchors((RectTransform)middle.Find("CarriedBackpackRegion_携带背包"),new Vector2(.03f,.66f),new Vector2(.70f,.93f));
            Anchors((RectTransform)middle.Find("BackpackEquipmentPreview_装备预览"),new Vector2(.72f,.66f),new Vector2(.97f,.93f));
            Anchors((RectTransform)middle.Find("WarehouseDraft_WandHeader"),new Vector2(.03f,.59f),new Vector2(.97f,.65f));
            var programming=(RectTransform)middle.Find("WandProgrammingRegion");Anchors(programming,new Vector2(.02f,.02f),new Vector2(.98f,.59f));
            var wands=Get<WandInventoryView>(store,"_wandsView");
            var panels=new SerializedObject(wands).FindProperty("_panels");
            var first=(WandProgramPanelView)panels.GetArrayElementAtIndex(0).objectReferenceValue;
            // WandInventoryView挂在区域根，程序行的父对象才是ScrollRect.content；不能把区域根当滚动内容。
            var content=(RectTransform)first.transform.parent;var viewport=(RectTransform)content.parent;var host=(RectTransform)viewport.parent;
            var scroll=host.GetComponent<ScrollRect>();if(scroll==null)scroll=Add<ScrollRect>(host.gameObject);
            if(viewport.GetComponent<RectMask2D>()==null)Add<RectMask2D>(viewport.gameObject);
            {
                // 复制的ScrollRect保留旧滚动位置和绝对高度。重设视口与内容顶部，避免第一根法杖藏在背包后面。
                Anchors(host,new Vector2(.01f,.17f),new Vector2(.99f,.99f));Anchors(viewport,Vector2.zero,Vector2.one);
                Record(content);content.anchorMin=new Vector2(0,1);content.anchorMax=new Vector2(1,1);content.pivot=new Vector2(0,1);content.anchoredPosition=Vector2.zero;content.sizeDelta=new Vector2(0,460);
                Record(scroll);scroll.viewport=viewport;scroll.content=content;scroll.vertical=true;scroll.horizontal=false;scroll.movementType=ScrollRect.MovementType.Clamped;scroll.verticalNormalizedPosition=1;
            }
            var layout=content.GetComponent<VerticalLayoutGroup>();
            if(layout!=null){Record(layout);layout.childControlHeight=true;layout.childForceExpandHeight=false;}
            foreach(var row in wands.GetComponentsInChildren<WandProgramPanelView>(true))
            {
                var element=row.GetComponent<LayoutElement>();if(element!=null){Record(element);element.minHeight=element.preferredHeight=148;}
                var header=Get<Text>(row,"_header");Anchors(header.rectTransform,new Vector2(.08f,.55f),new Vector2(.98f,.99f));
                Record(header);header.fontSize=15;header.resizeTextForBestFit=false;
                var expanded=Get<GameObject>(row,"_expandedRoot");Anchors((RectTransform)expanded.transform,new Vector2(.01f,.01f),new Vector2(.99f,.54f));
            }
            var details=right.Find("ItemDetailsRegion_物品详情");if(details!=null)
            {
                Anchors((RectTransform)details,new Vector2(.03f,.03f),new Vector2(.97f,.92f));
                foreach(var name in new[]{"Composition","RaidControls","SpellAttachmentControls","AttachmentFeedback","PickupSelected"})Hide(details.Find(name));
                var drop=details.Find("DropSelected")?.GetComponent<Button>();if(drop!=null){Wire(drop,store.DropSelected);Anchors(drop.GetComponent<RectTransform>(),new Vector2(.04f,.04f),new Vector2(.96f,.11f));}
            }
            // 这些旧表现组件会在Update重新开启自己的控件；草稿使用预留区域，应连同表现驱动一起停用。
            foreach(var view in store.GetComponents<RaidPreparationView>()){Record(view);view.enabled=false;}
            foreach(var view in store.GetComponents<SpellAttachmentView>()){Record(view);view.enabled=false;}
            foreach(var label in panel.Find("LeftPart/GoldRegion").GetComponentsInChildren<Text>(true))
                if(label.name=="Gold_Number"){Record(label);label.text="钱包显示预留";label.fontSize=20;}
        }
        private static void RepairWindows(SinglePlayerInventoryController bag,SinglePlayerInventoryController store)
        {
            // 输入控制器必须位于启用Canvas上；隐藏的是内部Panel，而非把监听Tab的对象也停用。
            foreach(var ui in new[]{bag,store})
            {
                Record(ui.gameObject);ui.gameObject.SetActive(true);Record(ui);ui.enabled=true;
                var root=Get<GameObject>(ui,"_panelRoot");Record(root);root.SetActive(false);
                var ghost=Get<InventoryItemView>(ui,"_ghost");if(ghost!=null){Record(ghost.gameObject);ghost.name="DragGhost_拖拽物品预览（勿删）";ghost.gameObject.SetActive(false);}
                foreach(var view in ui.GetComponents<RaidResultView>())
                    if(new SerializedObject(view).FindProperty("_result").objectReferenceValue==null){Record(view);view.enabled=false;}
                foreach(var view in ui.GetComponents<ExtractionHud>())
                    if(new SerializedObject(view).FindProperty("_condition").objectReferenceValue==null || new SerializedObject(view).FindProperty("_timer").objectReferenceValue==null){Record(view);view.enabled=false;}
            }
            Set(store,"_toggleAction",null);Set(bag,"_otherWorkspace",store);Set(store,"_otherWorkspace",bag);
            // 复制的HUD仍可能引用仓库Canvas；随身HUD固定读背包控制器的字体和开关状态。
            var scene=bag.gameObject.scene;
            foreach(var root in scene.GetRootGameObjects())foreach(var hud in root.GetComponentsInChildren<NearbyPickupListView>(true))Set(hud,"_inventory",bag);
            foreach(var root in scene.GetRootGameObjects())foreach(var hud in root.GetComponentsInChildren<HeldItemHud>(true)){Set(hud,"_backpack",bag);Set(hud,"_warehouse",store);}
        }
        private static T Get<T>(Object target,string field) where T:Object => (T)new SerializedObject(target).FindProperty(field).objectReferenceValue;
        private static void Record(Object target){if(_undo && target!=null)Undo.RecordObject(target,UndoName);}
        private static void Set(Object target,string field,Object value){Record(target);var data=new SerializedObject(target);data.FindProperty(field).objectReferenceValue=value;data.ApplyModifiedPropertiesWithoutUndo();}
        private static T Add<T>(GameObject target) where T:Component => _undo?Undo.AddComponent<T>(target):target.AddComponent<T>();
        private static void Hide(Transform target){if(target==null)return;Record(target.gameObject);target.gameObject.SetActive(false);}
        private static void Anchors(RectTransform rect,Vector2 min,Vector2 max){Record(rect);rect.anchorMin=min;rect.anchorMax=max;rect.pivot=new Vector2(.5f,.5f);rect.offsetMin=rect.offsetMax=Vector2.zero;}
        private static void Tint(Transform target,Color color){if(target==null)return;var image=target.GetComponent<Image>();if(image!=null){Record(image);image.color=color;}}
        private static RectTransform Region(Transform parent,string name,Color color,Vector2 min,Vector2 max)
        {
            var go=new GameObject(name,typeof(RectTransform),typeof(Image));go.transform.SetParent(parent,false);
            var rect=(RectTransform)go.transform;rect.anchorMin=min;rect.anchorMax=max;rect.offsetMin=rect.offsetMax=Vector2.zero;go.GetComponent<Image>().color=color;
            if(_undo)Undo.RegisterCreatedObjectUndo(go,UndoName);return rect;
        }
        private static Text Label(Transform parent,string name,string text,Vector2 min,Vector2 max,int size)
        {
            var go=new GameObject(name,typeof(RectTransform),typeof(Text));go.transform.SetParent(parent,false);
            var rect=(RectTransform)go.transform;rect.anchorMin=min;rect.anchorMax=max;rect.offsetMin=rect.offsetMax=Vector2.zero;
            var label=go.GetComponent<Text>();label.text=text;label.fontSize=size;label.color=new Color(1,.86f,.52f,1);label.raycastTarget=false;label.alignment=TextAnchor.MiddleLeft;
            label.font=AssetDatabase.GetBuiltinExtraResource<Font>("LegacyRuntime.ttf");
            if(_undo)Undo.RegisterCreatedObjectUndo(go,UndoName);return label;
        }
        private static void Wire(Button button,UnityEngine.Events.UnityAction action)
        {
            Record(button);for(int i=button.onClick.GetPersistentEventCount()-1;i>=0;i--)UnityEventTools.RemovePersistentListener(button.onClick,i);
            UnityEventTools.AddPersistentListener(button.onClick,action);
        }
        private static string Hash(){using var sha=SHA256.Create();return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(Formal))).Replace("-","");}
        private static void Write(string name,string text){Directory.CreateDirectory(Evidence);File.WriteAllText(Evidence+"/"+name,text);}
    }
}
