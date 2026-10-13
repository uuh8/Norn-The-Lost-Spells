using System;
using System.IO;
using System.Collections.Generic;
using Game.Character;
using Game.Core;
using Game.ElementField;
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
    /// <summary>复制独立Hub副本后整合；所有保存只针对新路径，绝不保存用户打开Scene。</summary>
    [InitializeOnLoad]
    internal static class SinglePlayerHubSandboxSetup
    {
        private const string Root="Assets/_Project/ScriptableObjects/Tests/SinglePlayerHub";
        private const string Path="Assets/_Project/Scenes/Tests/HubSandbox.unity";
        private const string Request="Temp/SinglePlayer/create-hub-sandbox.request";
        private const string RepairRequest="Temp/SinglePlayer/repair-hub-layout.request";
        static SinglePlayerHubSandboxSetup(){EditorApplication.update+=Poll;}
        private static void Poll()
        {if((!File.Exists(Request) && !File.Exists(RepairRequest)) || EditorApplication.isCompiling || EditorApplication.isUpdating || SessionState.GetBool("Norn.SinglePlayer.Tests.Running",false))return;
            bool repair=File.Exists(RepairRequest);File.Delete(repair?RepairRequest:Request);try{if(repair)RepairLayout();else Create();}catch(Exception e){Report("Error",e.ToString());GameLog.Error(e.ToString(),"HubSetup");}}
        private static void RepairLayout()
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode || EditorUtility.scriptCompilationFailed){Report("Blocked","Playing/编译失败");return;}
            for(int i=0;i<SceneManager.sceneCount;i++)if(SceneManager.GetSceneAt(i).isDirty){Report("Blocked","Scene未保存；未替用户保存或丢弃。");return;}
            // 仅修复这次新建的独立灰盒，资产/GUID保持原样；不会复制覆盖任何用户场景。
            var original=SceneManager.GetActiveScene();var scene=EditorSceneManager.OpenScene(Path,OpenSceneMode.Additive);
            // DefaultControls.Create*先创建根对象再SetParent，创建阶段也必须属于新副本。
            // 否则即使最终父级在Hub，用户active Scene仍会被Unity标记为dirty。
            try{SceneManager.SetActiveScene(scene);ConfigureWorkspace(scene);EditorSceneManager.SaveScene(scene,Path);}
            finally{EditorSceneManager.CloseScene(scene,true);if(original.IsValid() && original.isLoaded)SceneManager.SetActiveScene(original);}
            Report("LayoutUpdated",null);
        }
        [MenuItem("Tools/Norn/Single Player/Create Hub Sandbox")]
        private static void Create()
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode || EditorUtility.scriptCompilationFailed){Report("Blocked","Playing/编译失败");return;}
            for(int i=0;i<SceneManager.sceneCount;i++)if(SceneManager.GetSceneAt(i).isDirty){Report("Blocked","Scene未保存，未修改/保存/丢弃。");return;}
            if(File.Exists(Path) || AssetDatabase.IsValidFolder(Root)){Report("Existing","已有/部分资产，拒绝覆盖。");return;}
            AssetDatabase.CreateFolder("Assets/_Project/ScriptableObjects/Tests","SinglePlayerHub");
            var definition=ScriptableObject.CreateInstance<HubDefinition>();definition.Stations=new[]{new HubDefinition.Station{Kind=HubStationKind.Warehouse,Position=new Vector3(-6,0,0),DisplayName="仓库与魔法整备"},new HubDefinition.Station{Kind=HubStationKind.Shop,Position=new Vector3(6,0,0),DisplayName="据点商店"},new HubDefinition.Station{Kind=HubStationKind.Expedition,Position=new Vector3(0,0,-6),DisplayName="箱庭入口"}};
            definition.ExperimentCenter=new Vector3(0,2,1);definition.ExperimentSize=new Vector3(8,6,8);AssetDatabase.CreateAsset(definition,Root+"/Hub.asset");
            if(!AssetDatabase.CopyAsset("Assets/_Project/Scenes/Tests/TradeSandbox.unity",Path))throw new IOException("复制Hub副本失败。");
            var original=SceneManager.GetActiveScene();var scene=EditorSceneManager.OpenScene(Path,OpenSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene);var inventory=Find<SinglePlayerInventorySession>(scene);var ui=Find<SinglePlayerInventoryController>(scene);var player=Find<WizardController>(scene);
                SetString(inventory,"_sandboxName","HubSandbox");SetBool(ui,"_openOnStart",false);
                // 正式实验不注入免费测试供给；旧数量验证Scene继续保留各自的Authoring。
                foreach(var root in scene.GetRootGameObjects())foreach(var supply in root.GetComponentsInChildren<ContainerSandboxMaterialAuthoring>(true))Object.DestroyImmediate(supply);
                var world=Find<ElementWorldRuntime>(scene);var adapter=world.gameObject.AddComponent<ExperimentWorldAdapter>();Set(adapter,"_world",world);
                var experiment=inventory.gameObject.AddComponent<ExperimentAreaController>();Set(experiment,"_session",inventory);Set(experiment,"_definition",definition);Set(experiment,"_player",player.transform);
                var hub=inventory.gameObject.AddComponent<HubInteractionController>();Set(hub,"_session",inventory);Set(hub,"_definition",definition);Set(hub,"_player",player.transform);
                Set(ui,"_hubAccess",hub);
                var interact=player.gameObject.AddComponent<HubInteractInput>();Set(interact,"_hub",hub);
                var shop=Find<ShopController>(scene);Set(shop,"_hubAccess",hub);SetBool(shop,"_openOnStart",false);
                BuildInventory(scene,ui);BuildShop(shop,ui);BuildNavigation(scene,ui,shop,hub,experiment,Find<RunPauseCoordinator>(scene));
                BuildStations(scene,definition,Find<SinglePlayerRunCoordinator>(scene));
                ConfigureWorkspace(scene);
                // 数量实验旧HUD信息含免费样本位置，新的真实实验不保留这些误导提示。
                foreach(var root in scene.GetRootGameObjects())if(root.name=="Container Composition")
                {var title=root.transform.Find("Panel/Title").GetComponent<Text>();title.text="材料与容器\nE 采集 · F 换瓶 · J 取固体\nH 撒出 · G 投掷 · K 放固体";
                    var help=root.transform.Find("Panel/Help");if(help!=null)help.GetComponent<Text>().text="实验使用真实库存；没有免费初始样本。\n残留保留在进程内，未采回部分不随存档恢复。";}
                if(!EditorSceneManager.SaveScene(scene,Path))throw new IOException("新HubScene保存失败。");
            }
            finally{EditorSceneManager.CloseScene(scene,true);if(original.IsValid() && original.isLoaded)SceneManager.SetActiveScene(original);}
            Report("Created",null);
        }
        private static void BuildInventory(Scene scene,SinglePlayerInventoryController ui)
        {
            var old=Get<GameObject>(ui,"_panelRoot");old.SetActive(false);old.name="LegacyInventoryPanel";
            var canvas=ui.GetComponentInParent<Canvas>();canvas.sortingOrder=100;var scaler=canvas.GetComponent<CanvasScaler>();scaler.referenceResolution=new Vector2(1920,1080);
            var panel=Box(ui.transform,"WorkspacePanel",new Vector2(20,-60),new Vector2(1880,1000));
            var title=Label(panel,"Title",new Vector2(20,-10),new Vector2(1440,50),"仓库与魔法整备",30);
            var close=Button(panel,"Close","关闭 [Esc]",new Vector2(1670,-10),new Vector2(180,46));Persistent(close,ui.Close);
            var warehouse=Box(panel,"Warehouse",new Vector2(20,-90),new Vector2(470,890));Label(warehouse,"Title",new Vector2(12,-8),new Vector2(440,38),"仓库",25);
            var search=Input(warehouse,"Search",new Vector2(12,-54),new Vector2(440,38),"");
            var category=Dropdown(warehouse,"Category",new Vector2(12,-104),new Vector2(440,38),new[]{"全部","法术","容器","材料","法杖","背包"});
            var warehouseRoot=Scroll(warehouse,"Items",new Vector2(12,-158),new Vector2(448,708),false);
            var target=warehouse.gameObject.AddComponent<InventoryDropTarget>();Set(ui,"_warehouseTarget",target);Set(ui,"_warehouseRoot",warehouseRoot);
            var bag=Box(panel,"Carried",new Vector2(510,-90),new Vector2(850,380));var capacity=Label(bag,"Capacity",new Vector2(12,-8),new Vector2(690,34),"携带背包",24);
            var grid=Get<InventoryGridView>(ui,"_gridView");var gridViewport=Rect(bag,"GridViewport",new Vector2(14,-58),new Vector2(500,298));gridViewport.gameObject.AddComponent<RectMask2D>();
            grid.transform.SetParent(gridViewport,false);Position((RectTransform)grid.transform,Vector2.zero,Vector2.zero);SetFloat(grid,"_cellSize",64);
            var gridScroll=gridViewport.gameObject.AddComponent<ScrollRect>();gridScroll.viewport=gridViewport;gridScroll.content=(RectTransform)grid.transform;gridScroll.horizontal=true;gridScroll.vertical=true;gridScroll.movementType=ScrollRect.MovementType.Clamped;
            var bagEquipment=Label(bag,"BagEquipment",new Vector2(518,-58),new Vector2(300,90),"当前背包",20);var bagTarget=bagEquipment.gameObject.AddComponent<InventoryDropTarget>();Set(ui,"_backpackTarget",bagTarget);
            var wands=Box(panel,"Wands",new Vector2(510,-480),new Vector2(850,500));Label(wands,"Title",new Vector2(12,-8),new Vector2(700,40),"法杖装备与编程",25);
            var equipment=Rect(wands,"Equipment",new Vector2(12,-64),new Vector2(700,100));var programTitle=Label(wands,"ProgramTitle",new Vector2(12,-180),new Vector2(720,42),"法术程序",21);
            var program=Scroll(wands,"Program",new Vector2(12,-228),new Vector2(716,102),true);
            var wandView=Get<WandInventoryView>(ui,"_wandsView");wandView.transform.SetParent(wands,false);Set(wandView,"_equipmentRoot",equipment);Set(wandView,"_programRoot",program);Set(wandView,"_programTitle",programTitle);SetBool(wandView,"_wideLayout",true);
            Label(wands,"PaletteTitle",new Vector2(12,-344),new Vector2(720,30),"背包中的可用法术 · 拖入程序槽",21);var palette=Scroll(wands,"Available",new Vector2(12,-386),new Vector2(716,104),true);
            var detailsGroup=Box(panel,"DetailsGroup",new Vector2(1380,-90),new Vector2(480,890));Label(detailsGroup,"Title",new Vector2(12,-8),new Vector2(450,38),"所选物品详情",25);
            var details=Label(detailsGroup,"Details",new Vector2(12,-56),new Vector2(450,122),"点击一份物品",19);Set(ui,"_details",details);
            var container=Label(detailsGroup,"Composition",new Vector2(12,-186),new Vector2(450,160),"实际材料组分",18);
            var preparation=Find<RaidPreparationView>(scene);var controls=Get<GameObject>(preparation,"_controls");controls.transform.SetParent(detailsGroup,false);Position(controls.GetComponent<RectTransform>(),new Vector2(12,-354),new Vector2(450,210));
            var attachment=Find<SpellAttachmentView>(scene);var attachmentControls=Get<GameObject>(attachment,"_controls");attachmentControls.transform.SetParent(detailsGroup,false);Position(attachmentControls.GetComponent<RectTransform>(),new Vector2(12,-582),new Vector2(450,180));
            var feedback=Get<Text>(attachment,"_feedback");feedback.transform.SetParent(detailsGroup,false);Position(feedback.rectTransform,new Vector2(12,-768),new Vector2(450,66));
            var save=Button(detailsGroup,"Save","保存实际库存",new Vector2(12,-838),new Vector2(210,40));Persistent(save,ui.Save);
            var next=Button(detailsGroup,"Next","同格下一份",new Vector2(234,-838),new Vector2(210,40));Persistent(next,ui.SelectNextMember);
            var status=Label(panel,"Status",new Vector2(20,-54),new Vector2(1620,30),"拖拽移动 · R旋转 · Shift拆出指定一份；程序容量与背包分开。",18);Set(ui,"_status",status);Set(ui,"_panelRoot",panel.gameObject);
            var prefab=Get<InventoryItemView>(ui,"_itemPrefab");var workspace=ui.gameObject.AddComponent<InventoryWorkspaceView>();Set(ui,"_workspace",workspace);
            Set(workspace,"_inventory",ui);Set(workspace,"_warehouse",warehouse);Set(workspace,"_bag",bag);Set(workspace,"_wands",wands);Set(workspace,"_paletteRoot",palette);Set(workspace,"_title",title);Set(workspace,"_capacity",capacity);Set(workspace,"_bagEquipment",bagEquipment);Set(workspace,"_containerDetails",container);Set(workspace,"_search",search);Set(workspace,"_category",category);Set(workspace,"_prefab",prefab);
            // 地面拾取仍由已有代理完成；旧地面列表不混入仓库/编程排布。
            Set(ui,"_groundRoot",null);var ghost=Get<InventoryItemView>(ui,"_ghost");ghost.transform.SetParent(ui.transform,false);
        }
        private static void BuildShop(ShopController shop,SinglePlayerInventoryController ui)
        {
            var panel=Get<GameObject>(shop,"_panel").GetComponent<RectTransform>();Position(panel,new Vector2(20,-60),new Vector2(1880,1000));
            var offers=Get<RectTransform>(shop,"_offersRoot");var offersScroll=offers.GetComponentInParent<ScrollRect>();Position(offersScroll.GetComponent<RectTransform>(),new Vector2(20,-170),new Vector2(650,750));Position(offersScroll.viewport,Vector2.zero,new Vector2(650,750));
            offers.sizeDelta=new Vector2(650,0);
            Object.DestroyImmediate(offers.GetComponent<VerticalLayoutGroup>());var grid=offers.gameObject.AddComponent<GridLayoutGroup>();grid.cellSize=new Vector2(152,170);grid.spacing=new Vector2(10,10);grid.constraint=GridLayoutGroup.Constraint.FixedColumnCount;grid.constraintCount=4;
            var details=Get<Text>(shop,"_details");Position(details.rectTransform,new Vector2(700,-170),new Vector2(560,310));
            var owned=Get<RectTransform>(shop,"_ownedRoot");var ownedScroll=owned.GetComponentInParent<ScrollRect>();Position(ownedScroll.GetComponent<RectTransform>(),new Vector2(1300,-530),new Vector2(550,300));Position(ownedScroll.viewport,Vector2.zero,new Vector2(550,300));
            owned.sizeDelta=new Vector2(550,0);
            Position(panel.Find("OffersTitle").GetComponent<RectTransform>(),new Vector2(20,-126),new Vector2(630,36));panel.Find("OwnedTitle").GetComponent<Text>().text="可出售物品 · 包含实际仓库资产";Position(panel.Find("OwnedTitle").GetComponent<RectTransform>(),new Vector2(1300,-480),new Vector2(550,42));Position(panel.Find("DetailsTitle").GetComponent<RectTransform>(),new Vector2(700,-126),new Vector2(550,36));
            Position(panel.Find("QuantityLabel").GetComponent<RectTransform>(),new Vector2(700,-486),new Vector2(290,34));Position(Get<InputField>(shop,"_quantity").GetComponent<RectTransform>(),new Vector2(1000,-482),new Vector2(250,44));
            Position(panel.Find("ContainerLabel").GetComponent<RectTransform>(),new Vector2(700,-544),new Vector2(550,32));Position(Get<Dropdown>(shop,"_containers").GetComponent<RectTransform>(),new Vector2(700,-584),new Vector2(550,44));Position(Get<Toggle>(shop,"_toBackpack").GetComponent<RectTransform>(),new Vector2(700,-642),new Vector2(550,38));
            Position(Get<Button>(shop,"_buyButton").GetComponent<RectTransform>(),new Vector2(700,-708),new Vector2(550,56));Position(Get<Button>(shop,"_sellItemButton").GetComponent<RectTransform>(),new Vector2(1300,-846),new Vector2(550,48));Position(Get<Button>(shop,"_sellMaterialButton").GetComponent<RectTransform>(),new Vector2(1300,-906),new Vector2(550,48));
            Position(Get<Text>(shop,"_status").rectTransform,new Vector2(700,-794),new Vector2(550,150));
            var capacity=Label(panel,"CarriedCapacity",new Vector2(1300,-126),new Vector2(550,36),"我的携带背包",23);var viewport=Rect(panel,"ShopBagViewport",new Vector2(1300,-178),new Vector2(550,280));viewport.gameObject.AddComponent<RectMask2D>();var root=Rect(viewport,"Grid",Vector2.zero,Vector2.zero);var backpack=root.gameObject.AddComponent<ShopBackpackView>();Set(backpack,"_inventory",ui);Set(backpack,"_prefab",Get<InventoryItemView>(ui,"_itemPrefab"));Set(backpack,"_capacity",capacity);Set(shop,"_backpackView",backpack);
            var bagScroll=viewport.gameObject.AddComponent<ScrollRect>();bagScroll.viewport=viewport;bagScroll.content=root;bagScroll.horizontal=true;bagScroll.vertical=true;bagScroll.movementType=ScrollRect.MovementType.Clamped;
            Get<ShopEntryView>(shop,"_entryPrefab").GetComponent<LayoutElement>().preferredHeight=70;
        }
        private static void ConfigureWorkspace(Scene scene)
        {
            var ui=Find<SinglePlayerInventoryController>(scene);var shop=Find<ShopController>(scene);var nav=Find<HubNavigationView>(scene);
            Set(Find<SpellAttachmentView>(scene),"_shop",shop);Set(nav,"_shopOpenButton",Get<Button>(shop,"_openButton").gameObject);
            var hud=Find<WandResourceHud>(scene);var data=new SerializedObject(nav);var array=data.FindProperty("_worldHud");array.arraySize=1;array.GetArrayElementAtIndex(0).objectReferenceValue=hud.gameObject;data.ApplyModifiedPropertiesWithoutUndo();
            var panel=Get<GameObject>(shop,"_panel").transform;
            if(panel.Find("Search")==null)
            {
                var search=Input(panel,"Search",new Vector2(20,-166),new Vector2(420,40),"");search.placeholder.GetComponent<Text>().text="搜索商品…";Set(shop,"_search",search);
                var category=Dropdown(panel,"Category",new Vector2(454,-166),new Vector2(216,40),new[]{"全部","法术","容器","材料","法杖","背包"});Set(shop,"_category",category);
            }
            var offers=Get<RectTransform>(shop,"_offersRoot").GetComponentInParent<ScrollRect>();Position(offers.GetComponent<RectTransform>(),new Vector2(20,-220),new Vector2(650,700));Position(offers.viewport,Vector2.zero,new Vector2(650,700));
            var warehouse=Get<InventoryWorkspaceView>(ui,"_workspace");Get<InputField>(warehouse,"_search").placeholder.GetComponent<Text>().text="搜索物品…";
        }
        private static void BuildNavigation(Scene scene,SinglePlayerInventoryController ui,ShopController shop,HubInteractionController hub,ExperimentAreaController experiment,RunPauseCoordinator pause)
        {
            var canvas=new GameObject("HubNavigation",typeof(RectTransform),typeof(Canvas),typeof(CanvasScaler),typeof(GraphicRaycaster));SceneManager.MoveGameObjectToScene(canvas,scene);canvas.GetComponent<Canvas>().renderMode=RenderMode.ScreenSpaceOverlay;canvas.GetComponent<Canvas>().sortingOrder=120;
            var scale=canvas.GetComponent<CanvasScaler>();scale.uiScaleMode=CanvasScaler.ScaleMode.ScaleWithScreenSize;scale.referenceResolution=new Vector2(1920,1080);
            var prompt=Label(canvas.transform,"Prompt",new Vector2(460,-18),new Vector2(1000,36),"据点交互",21);var departure=Box(canvas.transform,"DepartureConfirmation",new Vector2(510,-360),new Vector2(900,330));var residue=Label(departure,"Residue",new Vector2(24,-22),new Vector2(852,180),"实验残留提示",24);
            var proceed=Button(departure,"Continue","保留据点残留，继续出发",new Vector2(24,-240),new Vector2(530,56));var cancel=Button(departure,"Cancel","返回整备",new Vector2(586,-240),new Vector2(290,56));
            var nav=canvas.AddComponent<HubNavigationView>();Set(nav,"_inventory",ui);Set(nav,"_shop",shop);Set(nav,"_hub",hub);Set(nav,"_experiment",experiment);Set(nav,"_prompt",prompt);Set(nav,"_residue",residue);Set(nav,"_departurePanel",departure.gameObject);Set(nav,"_continue",proceed);Set(nav,"_cancel",cancel);Set(nav,"_pause",pause);
            var prep=Find<RaidPreparationView>(scene);var depart=Get<Button>(prep,"_depart");Set(nav,"_depart",depart);
            // 移除旧View的出发监听（它在OnEnable会再添加），改由明确残留确认入口负责该按钮。
            SetBool(prep,"_externalDeparture",true);departure.gameObject.SetActive(false);
        }
        private static void BuildStations(Scene scene,HubDefinition definition,SinglePlayerRunCoordinator run)
        {
            var roots=new List<GameObject>();var old=new SerializedObject(run).FindProperty("_hubEnvironment");for(int i=0;i<old.arraySize;i++)roots.Add((GameObject)old.GetArrayElementAtIndex(i).objectReferenceValue);
            for(int i=0;i<definition.Stations.Length;i++)
            {var station=GameObject.CreatePrimitive(PrimitiveType.Cube);station.name=definition.Stations[i].DisplayName;station.transform.position=definition.Stations[i].Position+Vector3.up*.5f;station.transform.localScale=new Vector3(1,1,1);Color color=i==0?new Color(.5f,.3f,.1f):i==1?new Color(.2f,.6f,.3f):new Color(.2f,.4f,.8f);Paint(station,color,"Station"+i);roots.Add(station);
                var label=new GameObject("Station Label",typeof(TextMesh));label.transform.SetParent(station.transform,false);label.transform.localPosition=new Vector3(-.6f,1.2f,0);label.GetComponent<TextMesh>().text=i==0?"WAREHOUSE":i==1?"SHOP":"EXPEDITION";label.GetComponent<TextMesh>().characterSize=.15f;}
            var area=GameObject.CreatePrimitive(PrimitiveType.Cube);area.name="实验区域 · 消耗实际库存";area.transform.position=new Vector3(definition.ExperimentCenter.x,-.04f,definition.ExperimentCenter.z);area.transform.localScale=new Vector3(definition.ExperimentSize.x,.02f,definition.ExperimentSize.z);Object.DestroyImmediate(area.GetComponent<Collider>());Paint(area,new Color(.08f,.22f,.24f),"ExperimentArea");roots.Add(area);
            var data=new SerializedObject(run);var array=data.FindProperty("_hubEnvironment");array.arraySize=roots.Count;for(int i=0;i<roots.Count;i++)array.GetArrayElementAtIndex(i).objectReferenceValue=roots[i];data.ApplyModifiedPropertiesWithoutUndo();
        }
        private static void Paint(GameObject go,Color color,string name){var m=new Material(Shader.Find("Universal Render Pipeline/Lit"));m.SetColor("_BaseColor",color);AssetDatabase.CreateAsset(m,Root+"/"+name+".mat");go.GetComponent<Renderer>().sharedMaterial=m;}
        private static T Find<T>(Scene scene)where T:Component{foreach(var root in scene.GetRootGameObjects()){var c=root.GetComponentInChildren<T>(true);if(c!=null)return c;}throw new InvalidOperationException(typeof(T).Name+"缺失");}
        private static T Get<T>(Object target,string field)where T:Object=>(T)new SerializedObject(target).FindProperty(field).objectReferenceValue;
        private static void Set(Object target,string field,Object value){var s=new SerializedObject(target);var p=s.FindProperty(field);if(p==null)throw new InvalidOperationException(target.name+"字段缺失 "+field);p.objectReferenceValue=value;s.ApplyModifiedPropertiesWithoutUndo();}
        private static void SetString(Object target,string field,string value){var s=new SerializedObject(target);s.FindProperty(field).stringValue=value;s.ApplyModifiedPropertiesWithoutUndo();}
        private static void SetBool(Object target,string field,bool value){var s=new SerializedObject(target);s.FindProperty(field).boolValue=value;s.ApplyModifiedPropertiesWithoutUndo();}
        private static void SetFloat(Object target,string field,float value){var s=new SerializedObject(target);s.FindProperty(field).floatValue=value;s.ApplyModifiedPropertiesWithoutUndo();}
        private static void Position(RectTransform r,Vector2 position,Vector2 size){r.anchorMin=r.anchorMax=r.pivot=new Vector2(0,1);r.anchoredPosition=position;r.sizeDelta=size;}
        private static RectTransform Rect(Transform parent,string name,Vector2 position,Vector2 size){var go=new GameObject(name,typeof(RectTransform));go.transform.SetParent(parent,false);var r=go.GetComponent<RectTransform>();Position(r,position,size);return r;}
        private static RectTransform Box(Transform parent,string name,Vector2 position,Vector2 size){var r=Rect(parent,name,position,size);r.gameObject.AddComponent<Image>().color=new Color(.025f,.045f,.065f,.99f);return r;}
        private static Text Label(Transform parent,string name,Vector2 position,Vector2 size,string content,int font=20){var r=Rect(parent,name,position,size);var t=r.gameObject.AddComponent<Text>();t.font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");t.fontSize=font;t.text=content;t.color=new Color(.9f,.94f,.96f);t.raycastTarget=false;return t;}
        private static Button Button(Transform parent,string name,string text,Vector2 position,Vector2 size){var r=Box(parent,name,position,size);var b=r.gameObject.AddComponent<Button>();var label=Label(r,"Label",new Vector2(6,-4),size-new Vector2(12,8),text);label.alignment=TextAnchor.MiddleCenter;return b;}
        private static void Persistent(Button button,UnityEngine.Events.UnityAction callback)=>UnityEditor.Events.UnityEventTools.AddPersistentListener(button.onClick,callback);
        private static InputField Input(Transform parent,string name,Vector2 position,Vector2 size,string text){var go=DefaultControls.CreateInputField(new DefaultControls.Resources());go.name=name;go.transform.SetParent(parent,false);Position(go.GetComponent<RectTransform>(),position,size);go.GetComponent<InputField>().text=text;return go.GetComponent<InputField>();}
        private static Dropdown Dropdown(Transform parent,string name,Vector2 position,Vector2 size,string[] choices){var go=DefaultControls.CreateDropdown(new DefaultControls.Resources());go.name=name;go.transform.SetParent(parent,false);Position(go.GetComponent<RectTransform>(),position,size);var d=go.GetComponent<Dropdown>();d.ClearOptions();d.AddOptions(new List<string>(choices));return d;}
        private static RectTransform Scroll(Transform parent,string name,Vector2 position,Vector2 size,bool horizontal)
        {var root=Rect(parent,name,position,size);var viewport=Rect(root,"Viewport",Vector2.zero,size);viewport.gameObject.AddComponent<RectMask2D>();var content=Rect(viewport,"Content",Vector2.zero,size);var scroll=root.gameObject.AddComponent<ScrollRect>();scroll.viewport=viewport;scroll.content=content;scroll.horizontal=horizontal;scroll.vertical=!horizontal;scroll.movementType=ScrollRect.MovementType.Clamped;return content;}
        private static void Report(string state,string error){Directory.CreateDirectory("Temp/SinglePlayer");File.WriteAllText("Temp/SinglePlayer/hub-sandbox-report.json",JsonUtility.ToJson(new ReportData{State=state,Error=error,Scene=Path},true));}
        [Serializable]private sealed class ReportData{public string State,Error,Scene;}
    }
}
