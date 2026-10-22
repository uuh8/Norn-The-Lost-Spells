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
using Object=UnityEngine.Object;

namespace Game.EditorTools
{
    /// <summary>复用商店业务和用户复制Canvas；布局用Undo修改，正式Scene绝不自动保存。</summary>
    [InitializeOnLoad]
    internal static class SinglePlayerShopDraftSetup
    {
        private const string Formal="Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity";
        private const string Review="Assets/_Project/Scenes/Tests/Hub/SinglePlayer_Hub_ShopDraftReview.unity";
        private const string Request="Temp/SinglePlayer/shop-draft.request";
        private const string Evidence="TestResults/SinglePlayer/M6-T1-ShopDraft";
        private const string UndoName="商店三列颜色草稿";
        private static bool _undo;
        static SinglePlayerShopDraftSetup()=>EditorApplication.update+=Poll;
        private static void Poll()
        {
            if(!File.Exists(Request)||EditorApplication.isCompiling||EditorApplication.isUpdating||EditorApplication.isPlayingOrWillChangePlaymode)return;
            string command=File.ReadAllText(Request).Trim();File.Delete(Request);
            try
            {
                if(EditorUtility.scriptCompilationFailed)throw new InvalidOperationException("编译失败，拒绝使用旧Assembly。");
                if(command=="inspect")Write("inspection.txt",SceneManager.GetActiveScene().path+"\nDirty="+SceneManager.GetActiveScene().isDirty);
                else if(command=="copy-baseline")Prepare(false);
                else if(command=="prepare")Prepare(true);
                else if(command=="rebuild-review")Prepare(true,true);
                else if(command=="apply-current")ApplyCurrent();
                else throw new ArgumentException(command);
            }
            catch(Exception e){Write("setup-error.txt",e.ToString());GameLog.Error(e.ToString(),"ShopDraft");}
        }
        private static void Prepare(bool layout,bool rebuild=false)
        {
            for(int i=0;i<SceneManager.sceneCount;i++)if(SceneManager.GetSceneAt(i).isDirty)throw new InvalidOperationException("用户Scene未保存，不代为保存或丢弃。");
            if(SceneManager.GetSceneByPath(Review).isLoaded)throw new InvalidOperationException("Review已打开，拒绝覆盖。");
            if(!File.Exists(Review)&&!AssetDatabase.CopyAsset(Formal,Review))throw new IOException("复制Review失败。");
            // 仅重建自动测试专用副本，保留其.meta GUID；当前正式Scene和任何用户已打开的Review均不覆盖。
            if(rebuild){File.Copy(Formal,Review,true);AssetDatabase.ImportAsset(Review,ImportAssetOptions.ForceUpdate);}
            var original=SceneManager.GetActiveScene();var scene=EditorSceneManager.OpenScene(Review,OpenSceneMode.Additive);
            try{_undo=false;if(layout){Apply(scene);EditorSceneManager.SaveScene(scene,Review);}Write("prepared.txt","Assets/_Project/Scenes/Tests/Hub/SinglePlayer_Hub_ShopDraftReview.unity\\nLayout="+layout);}
            finally{EditorSceneManager.CloseScene(scene,true);SceneManager.SetActiveScene(original);}
        }
        [MenuItem("Tools/Norn/商店/应用三列颜色草稿（Undo，不保存）")]
        private static void ApplyCurrent()
        {
            var scene=SceneManager.GetActiveScene();if(scene.path!=Formal)throw new InvalidOperationException("只处理当前正式据点。");
            string before=Hash();Undo.IncrementCurrentGroup();int group=Undo.GetCurrentGroup();Undo.SetCurrentGroupName(UndoName);
            try
            {
                _undo=true;Apply(scene);var shop=Find<ShopController>(scene);var panel=Get<GameObject>(shop,"_panel");Record(panel);panel.SetActive(true);
                if(Hash()!=before)throw new InvalidOperationException("正式Scene磁盘发生意外变化。");
                Undo.FlushUndoRecordObjects();Undo.CollapseUndoOperations(group);EditorSceneManager.MarkSceneDirty(scene);
                Write("current-applied.txt","商店草稿已展示；一组Undo；未保存正式Scene。\nSHA256="+before);
            }
            catch{Undo.RevertAllDownToGroup(group);throw;}
            finally{_undo=false;}
        }
        private static void Apply(Scene scene)
        {
            SinglePlayerInventoryController copy=null,bag=null,warehouse=null;
            foreach(var root in scene.GetRootGameObjects())foreach(var ui in root.GetComponentsInChildren<SinglePlayerInventoryController>(true))
            {if(ui.name=="InventoryCanvas_商店")copy=ui;if(ui.name=="InventoryCanvas_背包")bag=ui;if(ui.name=="InventoryCanvas_仓库")warehouse=ui;}
            var shop=Find<ShopController>(scene);
            if(copy==null||bag==null||warehouse==null||shop==null)throw new InvalidOperationException("需要用户复制并命名的商店Canvas和原有商店控制器。");
            var panel=Get<GameObject>(copy,"_panelRoot").GetComponent<RectTransform>();
            if(panel.Find("ShopDraft_Left_商品与购买预览")!=null)throw new InvalidOperationException("草稿已应用；为保护精修位置，请Undo后再执行。");
            // 创建节点先于RecordObject，避免RegisterCreatedObjectUndo Flush旧对象记录。比例采用父Rect的anchor空间。
            var left=Region(panel,"ShopDraft_Left_商品与购买预览",new Color(.04f,.08f,.13f),.015f,.03f,.35f,.89f);
            var middle=Region(panel,"ShopDraft_Middle_背包与仓库",new Color(.04f,.08f,.13f),.36f,.03f,.72f,.89f);
            var right=Region(panel,"ShopDraft_Right_详情与交易",new Color(.04f,.08f,.13f),.73f,.03f,.985f,.89f);
            var goods=Region(left,"GoodsRegion_商店货品",new Color(.08f,.22f,.40f),0,.40f,1,1);
            var cart=Region(left,"CartRegion_购买预览",new Color(.38f,.25f,.08f),0,0,1,.385f);
            var carry=Region(middle,"CarryRegion_携带背包",new Color(.10f,.33f,.20f),0,.50f,1,1);
            var stock=Region(middle,"WarehouseRegion_仓库",new Color(.08f,.31f,.34f),0,0,1,.485f);
            var details=Region(right,"DetailsRegion_物品详情",new Color(.30f,.15f,.38f),0,.65f,1,1);
            var preview=Region(right,"SpellPreviewRegion_法术效果预览",new Color(.36f,.19f,.07f),0,.43f,1,.635f);
            var trade=Region(right,"TradeInfoRegion_购买信息",new Color(.14f,.23f,.36f),0,.23f,1,.415f);
            var merchant=Region(right,"MerchantRegion_商店信息",new Color(.29f,.13f,.20f),0,.10f,1,.215f);
            var actions=Region(right,"ActionsRegion_操作",new Color(.12f,.15f,.20f),0,0,1,.085f);
            Header(panel,"ShopDraft_Title","诺恩商店 · 魔法补给",.09f,.90f,.75f,.985f,36);
            Header(goods,"Header","商店货品",.03f,.90f,.97f,.995f,26);
            Header(cart,"Header","购买预览 · 清单功能预留",.03f,.84f,.97f,.995f,24);
            Header(cart,"CartPlaceholder","商品名称    单价    数量    小计\n\n清单尚未接入；右侧购买直接购买所选商品。",.04f,.49f,.96f,.82f,19);
            Header(preview,"Header","法术效果预览",.03f,.76f,.97f,.99f,23);
            Header(preview,"Placeholder","法术图标 / 效果说明 / Mana / 冷却\n（展示预留，后续配置）",.04f,.10f,.96f,.73f,19);
            Header(trade,"Header","购买信息 · 实际交易设置",.03f,.79f,.97f,.99f,21);
            Header(trade,"QuantityCaption","数量 / GMU",.04f,.53f,.46f,.77f,17);
            Header(merchant,"Header","商店信息",.03f,.68f,.97f,.99f,21);
            Header(merchant,"Placeholder","店主头像 / 名称 / 简介（预留）",.04f,.04f,.96f,.65f,17);
            Header(stock,"Header","仓库 · 点击资产查看 / 出售",.03f,.87f,.97f,.995f,23);
            Header(details,"Header","物品详情",.03f,.87f,.97f,.995f,25);
            var add=PlaceholderButton(actions,"AddToList_加入清单（预留）","加入清单",.02f,.10f,.34f,.90f);
            var close=PlaceholderButton(actions,"Close_关闭商店","关闭",.69f,.10f,.98f,.90f);
            var equipment=Region(carry,"BackpackPreview_装备预览",new Color(.13f,.25f,.16f),.73f,.03f,.98f,.79f);
            Header(equipment,"Placeholder","当前背包\n\n装备图预留",.05f,.05f,.95f,.95f,20);
            // 原背包布局留作可撤销的作者参考；其控制器与输入全部停用，避免复制出的Tab监听打开第三套背包。
            foreach(var childName in new[]{"LeftPart","MiddlePart","RightPart","Logo"})Hide(panel.Find(childName));
            var ghost=Get<InventoryItemView>(copy,"_ghost");if(ghost!=null){Hide(ghost.transform);Record(ghost.gameObject);ghost.name="DragGhost_商店未使用（停用）";}
            Record(copy);copy.enabled=false;Set(copy,"_toggleAction",null);Set(copy,"_rotateAction",null);Set(copy,"_cancelAction",null);Set(copy,"_splitAction",null);Set(copy,"_backpackWorkspace",null);
            foreach(var component in copy.GetComponents<MonoBehaviour>())
                if(component is RaidPreparationView||component is SpellAttachmentView||component is RaidResultView||component is ExtractionHud){Record(component);component.enabled=false;}
            Record(copy.gameObject);copy.gameObject.SetActive(true);Record(panel.gameObject);panel.name="ShopWorkspaceRoot_商店";
            // 保持原ShopController实例身份，Hub/HUD仍引用它；只替换展示Panel。旧Canvas必须激活才能消费E事件。
            var oldPanel=Get<GameObject>(shop,"_panel").transform;Record(shop.gameObject);shop.gameObject.SetActive(true);Record(shop);shop.enabled=true;
            Record(shop.GetComponent<Canvas>());shop.GetComponent<Canvas>().enabled=false;
            Set(shop,"_inventoryUi",bag);Set(shop,"_panel",panel.gameObject);Set(shop,"_toggleAction",null);SetBool(shop,"_openOnStart",false);SetBool(shop,"_warehouseOnly",true);
            var capacity=Get<Text>(Get<ShopBackpackView>(shop,"_backpackView"),"_capacity");Move(capacity.transform,carry,.03f,.80f,.97f,.98f);TextStyle(capacity,23);
            var bagView=Get<ShopBackpackView>(shop,"_backpackView");var bagViewport=(RectTransform)bagView.transform.parent;
            Record(bagView);var bagData=new SerializedObject(bagView);bagData.FindProperty("_cellSize").floatValue=70;bagData.ApplyModifiedPropertiesWithoutUndo();
            Move(bagViewport,carry,.025f,.035f,.70f,.78f);BagScroll(bagViewport,(RectTransform)bagView.transform);
            var offerRoot=Get<RectTransform>(shop,"_offersRoot");MoveScroll(offerRoot,goods,.025f,.03f,.975f,.66f,3,112);
            Move(Get<InputField>(shop,"_search").transform,goods,.03f,.78f,.97f,.89f);
            Move(Get<Dropdown>(shop,"_category").transform,goods,.03f,.67f,.97f,.77f);
            foreach(var text in Get<InputField>(shop,"_search").GetComponentsInChildren<Text>(true)){TextStyle(text,20);text.color=new Color(.20f,.24f,.29f);}
            var category=Get<Dropdown>(shop,"_category");TextStyle(category.captionText,20);TextStyle(category.itemText,20);
            category.captionText.color=category.itemText.color=new Color(.20f,.24f,.29f);
            var owned=Get<RectTransform>(shop,"_ownedRoot");MoveScroll(owned,stock,.025f,.025f,.975f,.85f,3,100);
            Move(Get<Text>(shop,"_details").transform,details,.04f,.04f,.96f,.85f);TextStyle(Get<Text>(shop,"_details"),18);
            Move(Get<Text>(shop,"_summary").transform,cart,.04f,.24f,.96f,.47f);TextStyle(Get<Text>(shop,"_summary"),17);
            Move(Get<Text>(shop,"_status").transform,cart,.04f,.03f,.96f,.22f);TextStyle(Get<Text>(shop,"_status"),18);
            Move(Get<InputField>(shop,"_quantity").transform,trade,.49f,.53f,.96f,.76f);
            Move(Get<Dropdown>(shop,"_containers").transform,trade,.04f,.27f,.96f,.50f);
            Move(Get<Toggle>(shop,"_toBackpack").transform,trade,.04f,.015f,.96f,.25f);
            var buy=Get<Button>(shop,"_buyButton");Move(buy.transform,actions,.36f,.10f,.67f,.90f);Caption(buy,"购买所选",19);Tint(buy.transform,new Color(.80f,.50f,.10f));
            // 出售仍是真实功能，放在仓库区域底部的一条工具栏，避免与详情区的购买混淆。
            Move(Get<Button>(shop,"_sellItemButton").transform,stock,.03f,.025f,.48f,.115f);Caption(Get<Button>(shop,"_sellItemButton"),"出售物品",17);
            Move(Get<Button>(shop,"_sellMaterialButton").transform,stock,.52f,.025f,.97f,.115f);Caption(Get<Button>(shop,"_sellMaterialButton"),"出售材料",17);
            var stockScroll=owned.GetComponentInParent<ScrollRect>();Anchors((RectTransform)stockScroll.transform,.025f,.13f,.975f,.85f);
            Wire(close,shop.Close);close.interactable=true;
            var copiedClose=panel.Find("Close")?.GetComponent<Button>();if(copiedClose!=null)Wire(copiedClose,shop.Close);
            // _closeButton由ShopController在OnEnable注册，无需重复persistent listener。
            Set(shop,"_closeButton",close);Clear(close);
            Hide(oldPanel);Hide(Get<Button>(shop,"_openButton")?.transform);
            foreach(var ui in new[]{bag,warehouse}){var otherPanel=Get<GameObject>(ui,"_panelRoot");Record(otherPanel);otherPanel.SetActive(false);}
            Record(panel.gameObject);panel.gameObject.SetActive(false);
            Canvas.ForceUpdateCanvases();Write("layout.txt","蓝：商品；金棕：购买预览；绿：背包；青：仓库；紫：详情；橙：效果预览；蓝灰：购买设置；酒红：商店信息。\n保留交易；清单与效果预览占位；复制背包输入停用。");
        }
        private static void BagScroll(RectTransform viewport,RectTransform content)
        {
            var scroll=viewport.GetComponent<ScrollRect>();if(scroll==null)scroll=Add<ScrollRect>(viewport.gameObject);
            Record(scroll);scroll.viewport=viewport;scroll.content=content;scroll.horizontal=scroll.vertical=true;scroll.movementType=ScrollRect.MovementType.Clamped;scroll.scrollSensitivity=32;
            Record(content);content.anchorMin=content.anchorMax=content.pivot=new Vector2(0,1);content.anchoredPosition=Vector2.zero;
        }
        private static void MoveScroll(RectTransform content,Transform parent,float x0,float y0,float x1,float y1,int columns,float height)
        {
            var scroll=content.GetComponentInParent<ScrollRect>();if(scroll==null)throw new InvalidOperationException("原有商品/仓库列表缺少ScrollRect。");
            Move(scroll.transform,parent,x0,y0,x1,y1);Record(scroll);scroll.horizontal=false;scroll.vertical=true;scroll.scrollSensitivity=35;
            Anchors(scroll.viewport,0,0,1,1);
            // GridLayoutGroup只管子项，ContentSizeFitter只管内容高度；不会跟viewport形成双向layout循环。
            // LayoutGroup带DisallowMultipleComponent；仅enabled=false仍不能添加另一种LayoutGroup，需用Undo替换组件。
            var vertical=content.GetComponent<VerticalLayoutGroup>();if(vertical!=null){if(_undo)Undo.DestroyObjectImmediate(vertical);else Object.DestroyImmediate(vertical);}
            var grid=content.GetComponent<GridLayoutGroup>();if(grid==null)grid=Add<GridLayoutGroup>(content.gameObject);Record(grid);grid.enabled=true;grid.constraint=GridLayoutGroup.Constraint.FixedColumnCount;grid.constraintCount=columns;grid.spacing=new Vector2(8,8);grid.padding=new RectOffset(6,6,6,6);
            Canvas.ForceUpdateCanvases();float width=((RectTransform)parent).rect.width*(x1-x0);grid.cellSize=new Vector2(Mathf.Max(70,(width-12-8*(columns-1))/columns),height);
            var fit=content.GetComponent<ContentSizeFitter>();if(fit==null)fit=Add<ContentSizeFitter>(content.gameObject);Record(fit);fit.horizontalFit=ContentSizeFitter.FitMode.Unconstrained;fit.verticalFit=ContentSizeFitter.FitMode.PreferredSize;
            Record(content);content.anchorMin=new Vector2(0,1);content.anchorMax=Vector2.one;content.pivot=new Vector2(0,1);content.offsetMin=new Vector2(0,-height);content.offsetMax=Vector2.zero;
        }
        private static T Find<T>(Scene scene)where T:Component{foreach(var root in scene.GetRootGameObjects()){var value=root.GetComponentInChildren<T>(true);if(value!=null)return value;}return null;}
        private static T Get<T>(Object target,string field)where T:Object=>(T)new SerializedObject(target).FindProperty(field).objectReferenceValue;
        private static void Record(Object target){if(_undo&&target!=null)Undo.RecordObject(target,UndoName);}
        private static void Set(Object target,string field,Object value){Record(target);var data=new SerializedObject(target);data.FindProperty(field).objectReferenceValue=value;data.ApplyModifiedPropertiesWithoutUndo();}
        private static void SetBool(Object target,string field,bool value){Record(target);var data=new SerializedObject(target);data.FindProperty(field).boolValue=value;data.ApplyModifiedPropertiesWithoutUndo();}
        private static T Add<T>(GameObject target)where T:Component=>_undo?Undo.AddComponent<T>(target):target.AddComponent<T>();
        private static void Hide(Transform target){if(target==null)return;Record(target.gameObject);target.gameObject.SetActive(false);}
        private static void Move(Transform target,Transform parent,float x0,float y0,float x1,float y1){if(_undo)Undo.SetTransformParent(target,parent,UndoName);else target.SetParent(parent,false);Anchors((RectTransform)target,x0,y0,x1,y1);Record(target.gameObject);target.gameObject.SetActive(true);}
        private static void Anchors(RectTransform rect,float x0,float y0,float x1,float y1){Record(rect);rect.anchorMin=new Vector2(x0,y0);rect.anchorMax=new Vector2(x1,y1);rect.pivot=new Vector2(.5f,.5f);rect.offsetMin=rect.offsetMax=Vector2.zero;}
        private static RectTransform Region(Transform parent,string name,Color color,float x0,float y0,float x1,float y1)
        {var go=new GameObject(name,typeof(RectTransform),typeof(Image));go.transform.SetParent(parent,false);var rect=(RectTransform)go.transform;Anchors(rect,x0,y0,x1,y1);go.GetComponent<Image>().color=color;if(_undo)Undo.RegisterCreatedObjectUndo(go,UndoName);return rect;}
        private static Text Header(Transform parent,string name,string caption,float x0,float y0,float x1,float y1,int size)
        {var go=new GameObject(name,typeof(RectTransform),typeof(Text));go.transform.SetParent(parent,false);Anchors((RectTransform)go.transform,x0,y0,x1,y1);var label=go.GetComponent<Text>();label.text=caption;label.font=AssetDatabase.GetBuiltinExtraResource<Font>("LegacyRuntime.ttf");label.fontSize=size;label.color=new Color(1,.86f,.58f);label.alignment=TextAnchor.MiddleLeft;label.raycastTarget=false;if(_undo)Undo.RegisterCreatedObjectUndo(go,UndoName);return label;}
        private static Button PlaceholderButton(Transform parent,string name,string caption,float x0,float y0,float x1,float y1)
        {var rect=Region(parent,name,new Color(.16f,.29f,.39f),x0,y0,x1,y1);var label=Header(rect,"Label",caption,.04f,.05f,.96f,.95f,19);label.alignment=TextAnchor.MiddleCenter;var button=Add<Button>(rect.gameObject);button.interactable=false;return button;}
        private static void TextStyle(Text label,int size){Record(label);label.fontSize=size;label.horizontalOverflow=HorizontalWrapMode.Wrap;label.verticalOverflow=VerticalWrapMode.Truncate;label.color=new Color(.93f,.96f,1);}
        private static void Caption(Button button,string caption,int size)
        {
            var label=button.GetComponentInChildren<Text>(true);Record(label);label.text=caption;label.fontSize=size;label.alignment=TextAnchor.MiddleCenter;
            // 旧按钮的Label曾采用固定608px位置；移动父按钮不会清除旧offset，因此必须同时改为内部stretch。
            Anchors(label.rectTransform,.03f,.03f,.97f,.97f);
        }
        private static void Tint(Transform target,Color color){var image=target.GetComponent<Image>();if(image!=null){Record(image);image.color=color;}}
        private static void Clear(Button button){Record(button);for(int i=button.onClick.GetPersistentEventCount()-1;i>=0;i--)UnityEventTools.RemovePersistentListener(button.onClick,i);}
        private static void Wire(Button button,UnityEngine.Events.UnityAction action){Clear(button);UnityEventTools.AddPersistentListener(button.onClick,action);}
        private static string Hash(){using var sha=SHA256.Create();return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(Formal))).Replace("-","");}
        private static void Write(string name,string value){Directory.CreateDirectory(Evidence);File.WriteAllText(Evidence+"/"+name,value);}
    }
}
