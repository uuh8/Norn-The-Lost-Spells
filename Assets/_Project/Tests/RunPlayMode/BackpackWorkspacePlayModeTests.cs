#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using Game.Character;
using Game.Core;
using Game.Skills;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Game.Run.Tests
{
    /// <summary>只进入独立Review并注入内存档，验证用户新布局与真实资产/输入接线。</summary>
    public class BackpackWorkspacePlayModeTests
    {
        protected const string Review="Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub_UnifiedItemsReview.unity";
        protected virtual string ReviewPath => Review;
        protected virtual string EvidencePath => "TestResults/SinglePlayer/M6-T1-Backpack";
        protected Scene Scene;
        protected SinglePlayerInventorySession Session;
        protected SinglePlayerInventoryController Backpack,Warehouse;
        protected WizardController Player;
        protected Keyboard Keyboard;
        protected Mouse Mouse;
        private InputTestFixture _input;
        private bool _background;
        protected readonly List<UnityEngine.Object> TemporaryAssets=new List<UnityEngine.Object>();
        [UnitySetUp] public IEnumerator Setup()
        {
            _background=Application.runInBackground;Application.runInBackground=true;Time.timeScale=1;
            _input=new InputTestFixture();_input.Setup();Keyboard=InputSystem.AddDevice<Keyboard>();Mouse=InputSystem.AddDevice<Mouse>();
            EditorWindow.GetWindow(Type.GetType("UnityEditor.GameView,UnityEditor")).Focus();
            SceneManager.sceneLoaded+=Loaded;
            Scene=EditorSceneManager.LoadSceneInPlayMode(ReviewPath,new LoadSceneParameters(LoadSceneMode.Additive));
            yield return null;yield return null;yield return null;SceneManager.sceneLoaded-=Loaded;
            Assert.That(Session.ReadOnly,Is.Not.Null);Assert.That(Backpack,Is.Not.Null);
        }
        private void Loaded(Scene scene,LoadSceneMode mode)
        {
            if (scene.path!=ReviewPath) return;Scene=scene;Session=Find<SinglePlayerInventorySession>();Player=Find<WizardController>();
            foreach (var root in scene.GetRootGameObjects()) foreach(var ui in root.GetComponentsInChildren<SinglePlayerInventoryController>(true))
            {
                var data = new SerializedObject(ui);
                if(data.FindProperty("_backpackWorkspace").objectReferenceValue!=null)Backpack=ui;
                else if (!data.FindProperty("_embedded").boolValue) Warehouse=ui;
            }
            var catalog=(ItemCatalog)new SerializedObject(Session).FindProperty("_catalog").objectReferenceValue;
            if(TestContext.CurrentContext.Test.Name.StartsWith("PreviewMana"))
            {
                catalog=UnityEngine.Object.Instantiate(catalog);TemporaryAssets.Add(catalog);
                var data=new SerializedObject(catalog);var definitions=data.FindProperty("_definitions");
                foreach(var name in new[]{"fire","ice","trigger"})
                {
                    var original=AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/SinglePlayer/Hub/SinglePlayerWands/"+name+".asset");
                    for(int i=0;i<definitions.arraySize;i++)if(definitions.GetArrayElementAtIndex(i).objectReferenceValue==original)
                    {
                        var item=UnityEngine.Object.Instantiate(original);TemporaryAssets.Add(item);
                        var spell=UnityEngine.Object.Instantiate(original.SpellDefinition);TemporaryAssets.Add(spell);
                        spell.Kind=name=="ice"?SpellKind.Multicast:SpellKind.Emit;spell.ManaCost=name=="ice"?7:name=="trigger"?5:3;
                        spell.ExtraDraws=1;spell.PayloadTrigger=name=="trigger"?PayloadTriggerMode.OnImpact:PayloadTriggerMode.None;
                        var itemData=new SerializedObject(item);itemData.FindProperty("_spellDefinition").objectReferenceValue=spell;itemData.ApplyModifiedPropertiesWithoutUndo();
                        definitions.GetArrayElementAtIndex(i).objectReferenceValue=item;
                    }
                }
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            if(TestContext.CurrentContext.Test.Name.StartsWith("TwentySlot"))
            {
                // 仅克隆内存定义，验证超过12槽的UI，不改变正式SO或用户存档。
                catalog=UnityEngine.Object.Instantiate(catalog);TemporaryAssets.Add(catalog);
                var data=new SerializedObject(catalog);var definitions=data.FindProperty("_definitions");
                var original=AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/SinglePlayer/Hub/SinglePlayerWands/wand.a.asset");
                for(int i=0;i<definitions.arraySize;i++)if(definitions.GetArrayElementAtIndex(i).objectReferenceValue==original)
                {
                    var item=UnityEngine.Object.Instantiate(original);TemporaryAssets.Add(item);
                    var wand=UnityEngine.Object.Instantiate(original.WandDefinition);TemporaryAssets.Add(wand);
                    var stats=new SerializedObject(wand);stats.FindProperty("_programCapacity").intValue=20;stats.ApplyModifiedPropertiesWithoutUndo();
                    var itemData=new SerializedObject(item);itemData.FindProperty("_wandDefinition").objectReferenceValue=wand;itemData.ApplyModifiedPropertiesWithoutUndo();
                    definitions.GetArrayElementAtIndex(i).objectReferenceValue=item;
                }
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            catalog=CustomizeCatalog(catalog);
            Assert.That(catalog.TryCreateIndex(out var index,out var error),Is.True,error);
            var store=new ItemOwnershipStore(index);
            if(TestContext.CurrentContext.Test.Name.StartsWith("ZeroWallet"))
            {
                Assert.That(Session.Initialize(new SinglePlayerProfile("player",store),new ProfileSaveRepository("Temp/SinglePlayer/BackpackEmpty",index,new MemoryFiles())),Is.True);return;
            }
            Add(store,"bag","SinglePlayerWands/bag.asset",new ItemLocation(ItemLocationKind.EquippedBackpack,"player",0,0,0,0));
            for(int i=0;i<3;i++)Add(store,"w"+i,"SinglePlayerWands/wand."+(char)('a'+i)+".asset",new ItemLocation(ItemLocationKind.EquippedWand,"player",0,0,0,i));
            Add(store,"fire","SinglePlayerWands/fire.asset",new ItemLocation(ItemLocationKind.Backpack,"bag",0,4,0,-1),8);
            Add(store,"ice","SinglePlayerWands/ice.asset",new ItemLocation(ItemLocationKind.Backpack,"bag",1,4,0,-1),8);
            if(TestContext.CurrentContext.Test.Name.StartsWith("PreviewMana"))Add(store,"trigger","SinglePlayerWands/trigger.asset",new ItemLocation(ItemLocationKind.Backpack,"bag",2,4,0,-1),8);
            for(int i=0;i<2;i++)
            {
                var bottle=AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/SinglePlayer/Hub/SinglePlayerContainers/bottle.asset");
                index.TryGetRules(bottle.ContentId,out var rules);var mix=new ContainerComposition(rules.ContainerCapacity,rules.ContainerMaterials);
                mix.AddReserved(new[]{new MaterialAmount(1,60),new MaterialAmount(3,40)},2);
                Assert.That(store.TryAdd(new RuntimeItemState("b"+i,bottle.ContentId,0,composition:mix),new ItemLocation(ItemLocationKind.Backpack,"bag",i*3,0,0,-1)),Is.True);
            }
            var profile=CustomizeProfile(new SinglePlayerProfile("player",store,currency:248),index);
            Assert.That(Session.Initialize(profile,new ProfileSaveRepository("Temp/SinglePlayer/BackpackMemory/"+Guid.NewGuid().ToString("N"),index,new MemoryFiles())),Is.True);
        }
        // 派生用例在Scene loaded且Session尚未初始化时配置内存档，不破坏Runtime只初始化一次的契约。
        protected virtual ItemCatalog CustomizeCatalog(ItemCatalog catalog)=>catalog;
        protected virtual SinglePlayerProfile CustomizeProfile(SinglePlayerProfile profile,ItemCatalogIndex index)=>profile;
        private static void Add(ItemOwnershipStore store,string id,string asset,ItemLocation location,int charges=0)
        {
            var definition=AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/SinglePlayer/Hub/"+asset);
            Assert.That(store.TryAdd(new RuntimeItemState(id,definition.ContentId,charges),location),Is.True,id);
        }
        protected T Find<T>() where T:Component
        { foreach(var root in Scene.GetRootGameObjects()){var value=root.GetComponentInChildren<T>(true);if(value!=null)return value;}return null; }
        protected void Bind(int slot,string id)
        {
            EventBus<ToolbeltBindingRequestedEvent>.Publish(new ToolbeltBindingRequestedEvent{InventoryId=Session.ReadOnly.InventoryId,
                ToolSlot=slot,ItemId=id,ExpectedVersion=Session.ToolbeltReadOnly.ConfigurationVersion});
            Assert.That(Session.ToolbeltReadOnly.TryGetBinding(slot,out var actual),Is.True);Assert.That(actual,Is.EqualTo(id));
        }
        protected void Keys(params Key[] keys){InputSystem.QueueStateEvent(Keyboard,new KeyboardState(keys));InputSystem.Update();}
        protected void Click(bool down){InputSystem.QueueStateEvent(Mouse,new MouseState{buttons=down?(ushort)1:(ushort)0});InputSystem.Update();}
        protected void MoveToExperiment()
        {
            var area=Find<ExperimentAreaController>();var definition=(HubDefinition)new SerializedObject(area).FindProperty("_definition").objectReferenceValue;
            Player.CharacterController.enabled=false;Player.transform.position=definition.ExperimentCenter;Player.CharacterController.enabled=true;Physics.SyncTransforms();
            // 输入测试固定实验点，避免地形高度/重力让玩家离开实验许可范围；不改变正式场景3C。
            Player.enabled=false;
        }
        protected ulong Bottle(string id){Session.TryCopyContainer(id,new MaterialAmount[8],out _,out var total,out _);return total;}
        protected void AimIntoExperiment()
        {
            // 实验域有边界；真实液体断言必须把Camera朝向域内，域外拒绝应另测且保留材料。
            var camera=Camera.main;var brain=camera.GetComponent("CinemachineBrain") as Behaviour;
            if(brain!=null)brain.enabled=false;
            camera.transform.rotation=Quaternion.Euler(65,Player.transform.eulerAngles.y+30,0);
            camera.transform.position=Player.transform.position+Vector3.up-camera.transform.forward*6;
        }
        [UnityTest] public IEnumerator TabOpensOnlyBackpackAndPreservesAuthorLayoutAndCoins()
        {
            var panel=(GameObject)new SerializedObject(Backpack).FindProperty("_panelRoot").objectReferenceValue;
            var region=(RectTransform)panel.transform.Find("LeftPart/BackpackRegion");var position=region.anchoredPosition;var size=region.sizeDelta;
            Keys(Key.Tab);yield return null;Keys();yield return null;
            Assert.That(Backpack.IsOpen,Is.True);Assert.That(Warehouse.IsOpen,Is.False);Assert.That(Time.timeScale,Is.Zero);
            Assert.That(region.anchoredPosition,Is.EqualTo(position));Assert.That(region.sizeDelta,Is.EqualTo(size));
            Assert.That(panel.transform.Find("LeftPart/GoldRegion/Gold_Number").GetComponent<Text>().text,Is.EqualTo("248"));
            Assert.That(region.GetComponentsInChildren<Image>().Length,Is.GreaterThanOrEqualTo(64));
            var grid=region.GetComponentInChildren<InventoryGridView>();var viewport=(RectTransform)grid.transform.parent;
            Assert.That(viewport.rect.width,Is.GreaterThan(200));Assert.That(viewport.rect.height,Is.GreaterThan(200));
            // ScrollRect/Layout 的浮点坐标换算可能留下约 0.0001 像素误差；仍严格检查可见布局没有位移。
            var gridPosition=((RectTransform)grid.transform).anchoredPosition;
            Assert.That(gridPosition.magnitude,Is.LessThan(0.001f));
            Keys(Key.Tab);yield return null;Keys();yield return null;Assert.That(Backpack.IsOpen,Is.False);Assert.That(Time.timeScale,Is.EqualTo(1));
        }
        [UnityTest] public IEnumerator DragToEquipmentMovesAssetAndClickOnlyInspects()
        {
            Backpack.OpenBackpack();yield return null;Session.ReadOnly.TryGetView("b0",out var item);uint before=Session.ReadOnly.Version;
            Backpack.BeginDrag(item,Vector2.zero,null,true);Backpack.SubmitToShortcut(0);yield return null;
            Assert.That(Session.ToolbeltReadOnly.TryGetBinding(0,out var id),Is.True);Assert.That(id,Is.EqualTo("b0"));
            Assert.That(Session.ReadOnly.Version,Is.GreaterThan(before));Assert.That(Session.ToolbeltReadOnly.SelectedSlot,Is.EqualTo(0));
            Assert.That(Session.ReadOnly.TryGetView("b0",out var equipped),Is.True);Assert.That(equipped.Location.Kind,Is.EqualTo(ItemLocationKind.EquippedTool));
            Backpack.SelectItem("b0");Assert.That(Session.ToolbeltReadOnly.SelectedSlot,Is.EqualTo(0));
            Backpack.BeginShortcutDrag("b0",Vector2.zero,null);Backpack.SubmitToBackpack(6,6,"bag");
            Assert.That(Session.ReadOnly.TryGetView("b0",out var retained),Is.True);Assert.That(retained.Location.Kind,Is.EqualTo(ItemLocationKind.Backpack));
            Backpack.ClearShortcut(0);Assert.That(Session.ToolbeltReadOnly.TryGetBinding(0,out _),Is.False);
        }
        [UnityTest] public IEnumerator AllThreeProgramsRemainExpandedWithoutSelecting()
        {
            Backpack.OpenBackpack();yield return null;
            var panels=Backpack.GetComponentsInChildren<WandProgramPanelView>(true);Assert.That(panels.Length,Is.EqualTo(3));
            Session.ReadOnly.TryGetView("fire",out var fire);Backpack.BeginDrag(fire,Vector2.zero,null,true);
            Backpack.SubmitToTarget(InventoryDropKind.ProgramSlot,"w2",0);yield return null;
            Assert.That(Session.ReadOnly.TryGetView("fire",out var placed),Is.True);Assert.That(placed.Location.OwnerId,Is.EqualTo("w2"));
            var selected=Session.ToolbeltReadOnly.SelectedSlot;yield return null;
            foreach(var panel in panels)Assert.That(panel.transform.Find("Expanded").gameObject.activeSelf,Is.True);
            Assert.That(Session.ToolbeltReadOnly.SelectedSlot,Is.EqualTo(selected));
            Session.ReadOnly.TryGetView("fire",out placed);Backpack.BeginDrag(placed,Vector2.zero,null,true);
            Backpack.SubmitToTarget(InventoryDropKind.ProgramSlot,"w0",1);yield return null;
            Session.ReadOnly.TryGetView("fire",out placed);Assert.That(placed.Location.OwnerId,Is.EqualTo("w0"));Assert.That(placed.Location.SlotIndex,Is.EqualTo(1));
        }
        [UnityTest] public IEnumerator CaptureBackpackAndHudWithRealTestAssets()
        {
            Backpack.OpenBackpack();Session.ReadOnly.TryGetView("fire",out var fire);Backpack.BeginDrag(fire,Vector2.zero,null,true);Backpack.SubmitToTarget(InventoryDropKind.ProgramSlot,"w0",0);
            Session.ReadOnly.TryGetView("ice",out var ice);Backpack.BeginDrag(ice,Vector2.zero,null,true);Backpack.SubmitToTarget(InventoryDropKind.ProgramSlot,"w1",0);Backpack.Close();
            Bind(0,"b0");Bind(1,"b1");Backpack.OpenBackpack();yield return null;Canvas.ForceUpdateCanvases();
            System.IO.Directory.CreateDirectory(EvidencePath);
            yield return new WaitForEndOfFrame();Capture("backpack.png");yield return null;yield return null;
            Backpack.Close();yield return null;yield return null;
            var hud=Find<HeldItemHud>();Assert.That(hud.isActiveAndEnabled,Is.True);Assert.That(hud.GetComponentInParent<Canvas>().enabled,Is.True);
            yield return new WaitForEndOfFrame();Capture("hud.png");yield return null;yield return null;
        }
        [UnityTest] public IEnumerator TwentySlotProgramScrollsToLastCellAndAcceptsRealDrop()
        {
            Backpack.OpenBackpack();yield return null;Canvas.ForceUpdateCanvases();
            var panel=Backpack.GetComponentsInChildren<WandProgramPanelView>()[0];
            var scroll=panel.GetComponentInChildren<ScrollRect>();
            Assert.That(scroll.horizontal,Is.True);Assert.That(scroll.content.rect.width,Is.GreaterThan(scroll.viewport.rect.width));
            Assert.That(scroll.content.GetComponentsInChildren<InventoryDropTarget>().Length,Is.EqualTo(20));
            scroll.horizontalNormalizedPosition=1;yield return null;Canvas.ForceUpdateCanvases();
            var target=scroll.content.Find("SpellSlot20");Assert.That(target,Is.Not.Null);
            var point=RectTransformUtility.WorldToScreenPoint(null,target.TransformPoint(new Vector3(35,-38,0)));
            Assert.That(RectTransformUtility.RectangleContainsScreenPoint(scroll.viewport,point),Is.True);
            Session.ReadOnly.TryGetView("ice",out var ice);Backpack.BeginDrag(ice,point,null,true);
            var pointer=new PointerEventData(EventSystem.current){position=point,button=PointerEventData.InputButton.Left};
            ExecuteEvents.Execute(target.gameObject,pointer,ExecuteEvents.dropHandler);yield return null;
            Session.ReadOnly.TryGetView("ice",out var result);Assert.That(result.Location.OwnerId,Is.EqualTo("w0"));Assert.That(result.Location.SlotIndex,Is.EqualTo(19));
        }
        [UnityTest] public IEnumerator ZeroWalletAndNoEquippedBackpackDisplayActualEmptyState()
        {
            Backpack.OpenBackpack();yield return null;
            var panel=(GameObject)new SerializedObject(Backpack).FindProperty("_panelRoot").objectReferenceValue;
            Assert.That(panel.transform.Find("LeftPart/GoldRegion/Gold_Number").GetComponent<Text>().text,Is.EqualTo("0"));
            var workspace=Backpack.GetComponent<BackpackWorkspaceView>();
            var capacity=(Text)new SerializedObject(workspace).FindProperty("_capacity").objectReferenceValue;
            Assert.That(capacity.text,Is.EqualTo("未装备背包"));Assert.That(Session.ToolbeltReadOnly.SelectedSlot,Is.EqualTo(-1));
        }
        [UnityTest] public IEnumerator PreviewManaShowsEmitMulticastAndOnlyCurrentTriggerLayer()
        {
            Backpack.OpenBackpack();yield return null;var panels=Backpack.GetComponentsInChildren<WandProgramPanelView>();
            Session.ReadOnly.TryGetView("fire",out var fire);Backpack.BeginDrag(fire,Vector2.zero,null,true);Backpack.SubmitToTarget(InventoryDropKind.ProgramSlot,"w0",0);yield return null;
            Assert.That(((Text)new SerializedObject(panels[0]).FindProperty("_header").objectReferenceValue).text,Does.Contain("本次预估蓝耗 3"));
            Session.ReadOnly.TryGetView("ice",out var ice);Backpack.BeginDrag(ice,Vector2.zero,null,true);Backpack.SubmitToTarget(InventoryDropKind.ProgramSlot,"w1",0);
            Session.ReadOnly.TryGetView("fire",out fire);Backpack.BeginDrag(fire,Vector2.zero,null,true);Backpack.SubmitToTarget(InventoryDropKind.ProgramSlot,"w1",1);yield return null;
            Assert.That(((Text)new SerializedObject(panels[1]).FindProperty("_header").objectReferenceValue).text,Does.Contain("本次预估蓝耗 10"));
            Session.ReadOnly.TryGetView("trigger",out var trigger);Backpack.BeginDrag(trigger,Vector2.zero,null,true);Backpack.SubmitToTarget(InventoryDropKind.ProgramSlot,"w2",0);
            Session.ReadOnly.TryGetView("fire",out fire);Backpack.BeginDrag(fire,Vector2.zero,null,true);Backpack.SubmitToTarget(InventoryDropKind.ProgramSlot,"w2",1);yield return null;
            Assert.That(((Text)new SerializedObject(panels[2]).FindProperty("_header").objectReferenceValue).text,Does.Contain("本次预估蓝耗 5"));
        }
        [UnityTest] public IEnumerator FullBackpackOpensForArrangementAndNearbyDragCollectsOnlyAfterSpaceIsFreed()
        {
            Player.enabled=false;var store=Session.Profile.Items;store.TryGet("fire",out var fire);
            for(int y=0;y<8;y++)for(int x=0;x<8;x++)store.TryAdd(new RuntimeItemState("fill"+x+"_"+y,fire.ContentId,1),new ItemLocation(ItemLocationKind.Backpack,"bag",x,y,0,-1));
            // 新专区也必须真实填满，普通包满已不等于法术无处存放。
            for(int slot=0;slot<Session.SpellStorageCapacity;slot++)Assert.That(store.TryAdd(new RuntimeItemState("trayFill"+slot,fire.ContentId,1),
                new ItemLocation(ItemLocationKind.SpellStorage,Session.ReadOnly.ActorId,0,0,0,slot)),Is.True);
            var location=new ItemLocation(ItemLocationKind.WorldItem,"near.world",0,0,0,-1);
            Assert.That(store.TryAdd(new RuntimeItemState("near",fire.ContentId,2),location),Is.True);
            var go=GameObject.CreatePrimitive(PrimitiveType.Cube);SceneManager.MoveGameObjectToScene(go,Scene);go.transform.position=Player.transform.position+Vector3.right;
            var proxy=go.AddComponent<WorldItemPickup>();proxy.ConfigureReach(Player.transform,3);Assert.That(proxy.TryBind(store,"near","near.world",Session.ReadOnly.InventoryId),Is.True);
            uint before=store.Version;Assert.That(Find<NearbyPickupController>().TryCollect("near"),Is.False);yield return null;
            Assert.That(Backpack.IsOpen,Is.True);Assert.That(Warehouse.IsOpen,Is.False);Assert.That(store.Version,Is.EqualTo(before));
            Session.ReadOnly.TryGetView("near",out var item);Assert.That(item.Location.Kind,Is.EqualTo(ItemLocationKind.WorldItem));
            // 实际编程腾出一格，随后用UGUI drag/drop回调拾取；不直接写入玩家背包。
            Session.ReadOnly.TryGetView("fire",out var owned);Backpack.BeginDrag(owned,Vector2.zero,null,true);Backpack.SubmitToTarget(InventoryDropKind.ProgramSlot,"w0",0);yield return null;
            InventoryItemView nearby=null;foreach(var view in Backpack.GetComponentsInChildren<InventoryItemView>())if(view.Snapshot.InstanceId=="near")nearby=view;
            Assert.That(nearby,Is.Not.Null);
            // 新附近区是真实格子图，抓取首格内部，避免边界浮点误差被Floor到相邻的负格。
            var grid=Backpack.GetComponentInChildren<InventoryGridView>();var data=new PointerEventData(EventSystem.current){position=RectTransformUtility.WorldToScreenPoint(null,nearby.Rect.TransformPoint(new Vector3(16,-16,0))),button=PointerEventData.InputButton.Left};
            ExecuteEvents.Execute(nearby.gameObject,data,ExecuteEvents.beginDragHandler);
            Assert.That(Backpack.IsDragging,Is.True);
            data.position=RectTransformUtility.WorldToScreenPoint(null,grid.transform.TransformPoint(new Vector3(grid.CellSize*.5f,-grid.CellSize*4.5f,0)));
            ExecuteEvents.Execute(nearby.gameObject,data,ExecuteEvents.dragHandler);ExecuteEvents.Execute(grid.gameObject,data,ExecuteEvents.dropHandler);ExecuteEvents.Execute(nearby.gameObject,data,ExecuteEvents.endDragHandler);yield return null;
            Session.ReadOnly.TryGetView("near",out item);Assert.That(item.Location.Kind,Is.EqualTo(ItemLocationKind.Backpack));Assert.That(item.Location.X,Is.Zero);Assert.That(item.Location.Y,Is.EqualTo(4));
            Assert.That(proxy==null,Is.True);Assert.That(Session.WalletReadOnly.Currency,Is.EqualTo(248));
        }
        [UnityTest] public IEnumerator ActualPointerDragRotatesAndRejectedPlacementKeepsSource()
        {
            Backpack.OpenBackpack();yield return null;var grid=Backpack.GetComponentInChildren<InventoryGridView>();InventoryItemView source=null;
            foreach(var view in grid.GetComponentsInChildren<InventoryItemView>())if(view.Snapshot.InstanceId=="b0")source=view;
            Assert.That(source,Is.Not.Null);var data=new PointerEventData(EventSystem.current){position=RectTransformUtility.WorldToScreenPoint(null,source.Rect.TransformPoint(new Vector3(grid.CellSize*.5f,-grid.CellSize*.5f,0))),button=PointerEventData.InputButton.Left};
            ExecuteEvents.Execute(source.gameObject,data,ExecuteEvents.beginDragHandler);Backpack.RotateDrag();
            // 先滚到真实可见的目标行；旧测试直接向被Viewport裁掉的第七行提交鼠标坐标。
            var scroll=grid.GetComponentInParent<UnityEngine.UI.ScrollRect>();scroll.normalizedPosition=new Vector2(1,0);Canvas.ForceUpdateCanvases();
            data.position=RectTransformUtility.WorldToScreenPoint(null,grid.transform.TransformPoint(new Vector3(grid.CellSize*6.5f,-grid.CellSize*6.5f,0)));
            ExecuteEvents.Execute(source.gameObject,data,ExecuteEvents.dragHandler);ExecuteEvents.Execute(grid.gameObject,data,ExecuteEvents.dropHandler);yield return null;
            Session.ReadOnly.TryGetView("b0",out var item);Assert.That(item.Location.X,Is.EqualTo(6));Assert.That(item.Location.Y,Is.EqualTo(6));Assert.That(item.Location.QuarterTurns,Is.EqualTo(1));
            uint before=Session.ReadOnly.Version;Backpack.BeginDrag(item,Vector2.zero,null,true);Backpack.SubmitToBackpack(8,8,"bag");yield return null;
            Assert.That(Session.ReadOnly.Version,Is.EqualTo(before));Session.ReadOnly.TryGetView("b0",out var after);Assert.That(after.Location,Is.EqualTo(item.Location));
        }
        protected void Capture(string name)
        {
            // CaptureScreenshot异步读回可能延后到卸载Scene；EndOfFrame同步读回确保保存当前画面。
            var pixels=new Texture2D(Screen.width,Screen.height,TextureFormat.RGB24,false);
            try { System.IO.Directory.CreateDirectory(EvidencePath); pixels.ReadPixels(new Rect(0,0,Screen.width,Screen.height),0,0);pixels.Apply();System.IO.File.WriteAllBytes(EvidencePath+"/"+name,pixels.EncodeToPNG()); }
            finally { UnityEngine.Object.Destroy(pixels); }
        }
        [UnityTest] public IEnumerator StableBackpackHudDragAndPourScopesHaveNoGcAlloc()
        {
            Session.Profile.Items.TryGet("w0",out var template);
            Assert.That(Session.Profile.Items.TryAdd(new RuntimeItemState("profiler.spare",template.ContentId,0),new GridPlacement(4,0,0).InBackpack("bag")),Is.True);
            bool callstacks=UnityEngine.Profiling.Profiler.enableAllocationCallstacks;UnityEngine.Profiling.Profiler.enableAllocationCallstacks=true;
            bool recording=UnityEditorInternal.ProfilerDriver.enabled;bool cpu=UnityEditorInternal.ProfilerDriver.IsAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU);
            try
            {
                UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,true);UnityEditorInternal.ProfilerDriver.enabled=true;
                MoveToExperiment();Bind(0,"b0");Backpack.OpenBackpack();yield return null;Session.ReadOnly.TryGetView("ice",out var spell);Backpack.BeginDrag(spell,Vector2.zero,null,true);
                var grid=Backpack.GetComponentInChildren<InventoryGridView>();Vector2 pointer=RectTransformUtility.WorldToScreenPoint(null,grid.transform.TransformPoint(new Vector3(grid.CellSize*6.5f,-grid.CellSize*6.5f,0)));
                Backpack.MoveGhost(pointer,null);for(int i=0;i<10;i++)yield return null;int first=UnityEditorInternal.ProfilerDriver.lastFrameIndex+1;
                for(int i=0;i<60;i++){Backpack.MoveGhost(pointer,null);yield return null;}int last=UnityEditorInternal.ProfilerDriver.lastFrameIndex;
                WriteProfile("backpack-profiler",first,last,new[]{"Norn.Backpack.Update","Norn.Backpack.DragPreview","Norn.Backpack.DragFollow","Norn.HeldItemHud.Update"},"Editor TestRunner, paused backpack and fixed pointer drag; warmup excluded");
                Backpack.CancelDrag();Backpack.Close();Keys(Key.Digit4);yield return null;Keys();yield return null;Click(true);
                Session.WandsReadOnly.TryGetProgram("w0",out var program);
                EventBus<WandResourceRequestedEvent>.Publish(new WandResourceRequestedEvent{InventoryId=Session.ReadOnly.InventoryId,
                    SourceWandId="w0",CallerId="profiler",RequestId=1,ProgramRevision=program.ProgramRevision,ManaCost=50,IsRootCast=true});
                Session.WandsReadOnly.TryGetState("w0",out var beforeRecovery);Assert.That(beforeRecovery.CurrentMana,Is.LessThan(beforeRecovery.MaxMana));
                Assert.That(Session.WandsReadOnly.TryGetState("profiler.spare",out _),Is.True,"Profiler采样包含额外携带杖缓存");
                for(int i=0;i<10;i++)yield return null;first=UnityEditorInternal.ProfilerDriver.lastFrameIndex+1;
                for(int i=0;i<60;i++)yield return null;last=UnityEditorInternal.ProfilerDriver.lastFrameIndex;
                WriteProfile("held-profiler",first,last,new[]{"Norn.HeldItemHud.Update","Norn.ContainerUse.Execute","Norn.WandResources.Tick"},"Editor TestRunner, held LMB pour plus four carried wands Mana/cooldown recovery; configuration changes excluded");Click(false);
                Session.WandsReadOnly.TryGetState("w0",out var afterRecovery);Assert.That(afterRecovery.CurrentMana,Is.GreaterThan(beforeRecovery.CurrentMana));
                Assert.That(afterRecovery.CooldownRemaining,Is.LessThan(beforeRecovery.CooldownRemaining));
            }
            finally { UnityEngine.Profiling.Profiler.enableAllocationCallstacks=callstacks;UnityEditorInternal.ProfilerDriver.enabled=recording;UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,cpu); }
        }
        protected void WriteProfile(string name,int first,int last,string[] names,string scope)
        {
            var report=new LocalProfile{Utc=DateTime.UtcNow.ToString("O"),Scope=scope,First=first,Last=last,Markers=new LocalMarker[names.Length]};
            for(int i=0;i<names.Length;i++)report.Markers[i]=new LocalMarker{Name=names[i]};
            for(int frame=Math.Max(first,UnityEditorInternal.ProfilerDriver.firstFrameIndex);frame<=last;frame++)
            {
                using var data=UnityEditorInternal.ProfilerDriver.GetRawFrameDataView(frame,0);if(!data.valid || data.sampleCount==0)continue;report.Frames++;report.MainThreadMs+=data.GetSampleTimeMs(0);
                for(int sample=0;sample<data.sampleCount;sample++)
                {
                    string sampleName=data.GetSampleName(sample);
                    if(sampleName=="GC.Alloc" && data.GetSampleMetadataCount(sample)>0)report.MainThreadGcBytes+=data.GetSampleMetadataAsLong(sample,0);
                    for(int m=0;m<report.Markers.Length;m++)if(sampleName==report.Markers[m].Name)
                    {
                        var marker=report.Markers[m];double start=data.GetSampleStartTimeMs(sample),duration=data.GetSampleTimeMs(sample);marker.Samples++;marker.MaxMs=Math.Max(marker.MaxMs,duration);
                        // 按真实Profiler子树统计，时间边界会把同时间戳的后续Coroutine分配误算给前一个Marker。
                        int end=sample+data.GetSampleChildrenCountRecursive(sample);
                        for(int child=sample+1;child<=end;child++)if(data.GetSampleName(child)=="GC.Alloc" && data.GetSampleMetadataCount(child)>0)
                        {
                            marker.GcBytes+=data.GetSampleMetadataAsLong(child,0);report.Trace+="Frame "+frame+" marker "+marker.Name+" allocation "+data.GetSampleMetadataAsLong(child,0)+" B\n";
                            var addresses=new List<ulong>();data.GetSampleCallstack(child,addresses);
                            foreach(var address in addresses){var method=data.ResolveMethodInfo(address);report.Trace+=method.methodName+" "+method.sourceFileName+":"+method.sourceFileLine+"\n";}
                        }
                    }
                }
            }
            if(report.Frames>0)report.MainThreadMs/=report.Frames;
            System.IO.Directory.CreateDirectory(EvidencePath);
            System.IO.File.WriteAllText(EvidencePath+"/"+name+".json",JsonUtility.ToJson(report,true));UnityEditorInternal.ProfilerDriver.SaveProfile(EvidencePath+"/"+name+".data");
            foreach(var marker in report.Markers)if(marker.GcBytes>0)
            {
                string failurePath=EvidencePath+"/"+name+"-red-"+DateTime.UtcNow.ToString("HHmmss");
                System.IO.File.WriteAllText(failurePath+".json",JsonUtility.ToJson(report,true));UnityEditorInternal.ProfilerDriver.SaveProfile(failurePath+".data");break;
            }
            Assert.That(report.Frames,Is.GreaterThanOrEqualTo(30));foreach(var marker in report.Markers){Assert.That(marker.Samples,Is.GreaterThan(0),marker.Name);Assert.That(marker.GcBytes,Is.Zero,marker.Name);}
        }
        [Serializable]private sealed class LocalMarker{public string Name;public int Samples;public double MaxMs;public long GcBytes;}
        [Serializable]private sealed class LocalProfile{public string Utc,Scope,Trace;public int First,Last,Frames;public double MainThreadMs;public long MainThreadGcBytes;public LocalMarker[] Markers;}
        [UnityTearDown] public IEnumerator Cleanup()
        {
            try
            {
                SceneManager.sceneLoaded-=Loaded;Backpack?.Close();Warehouse?.Close();Time.timeScale=1;
                if(Scene.IsValid() && Scene.isLoaded)yield return SceneManager.UnloadSceneAsync(Scene);
            }
            finally { try{_input?.TearDown();}finally{_input=null;Application.runInBackground=_background;Time.timeScale=1;
                foreach(var asset in TemporaryAssets)UnityEngine.Object.Destroy(asset);TemporaryAssets.Clear();} }
            yield return null;
        }
        private sealed class MemoryFiles:IProfileFileSystem
        {
            private readonly Dictionary<string,string> _files=new Dictionary<string,string>();
            public bool Exists(string p)=>_files.ContainsKey(p);public string Read(string p)=>_files[p];
            public void WriteFlushed(string p,string text){_files[p]=text;}public void Move(string a,string b){_files[b]=_files[a];_files.Remove(a);}
            public void Replace(string a,string b,string backup){_files[backup]=_files[b];Move(a,b);}
        }
    }
    public sealed class HeldItemInputPlayModeTests:BackpackWorkspacePlayModeTests
    {
        [UnityTest] public IEnumerator FCollectionRequestsUseSelectedContainerAndSwitchRequiresRelease()
        {
            MoveToExperiment();Bind(0,"b0");Bind(1,"b1");Keys(Key.Digit4);yield return null;Keys();yield return null;
            string requested=null;int requests=0;
            void Observe(MaterialTransferRequestedEvent e){if(e.InventoryId==Session.ReadOnly.InventoryId){requested=e.ItemId;requests++;}}
            EventBus<MaterialTransferRequestedEvent>.Subscribe(Observe);
            try
            {
                // 验证真实F Action到Core命令的目标；空实验世界不伪造采集成功/数量。
                Keys(Key.F);yield return new WaitForSeconds(.25f);Assert.That(requested,Is.EqualTo("b0"));
                Keys(Key.Digit5,Key.F);yield return null;Keys(Key.F);yield return null;int stopped=requests;
                yield return new WaitForSeconds(.2f);Assert.That(requests,Is.EqualTo(stopped));
                Keys();yield return null;Keys(Key.F);yield return new WaitForSeconds(.25f);Assert.That(requested,Is.EqualTo("b1"));Keys();
            }
            finally {EventBus<MaterialTransferRequestedEvent>.Unsubscribe(Observe);}
        }
        [UnityTest] public IEnumerator HeldLeftMousePoursOnlySelectedContainerAndSwitchWaitsForRelease()
        {
            MoveToExperiment();AimIntoExperiment();Bind(0,"b0");Bind(1,"b1");Keys(Key.Digit4);yield return null;Keys();yield return null;
            Assert.That(Session.ToolbeltReadOnly.SelectedItemId,Is.EqualTo("b0"));Click(true);yield return new WaitForSeconds(.25f);
            Assert.That(Bottle("b0"),Is.LessThan(100));Assert.That(Bottle("b1"),Is.EqualTo(100));
            Keys(Key.Digit5);yield return null;Keys();yield return new WaitForSeconds(.2f);Assert.That(Bottle("b1"),Is.EqualTo(100));
            Click(false);yield return null;Click(true);
            // 实际世界转移可能等待GPU reservation；在持续按住期间等收据，不能把资源忙误判为输入丢失。
            for(int frame=0;frame<120 && Bottle("b1")==100;frame++) yield return null;
            Assert.That(Bottle("b1"),Is.LessThan(100),"selected="+Session.ToolbeltReadOnly.SelectedItemId+" collection="+Find<MaterialCollectionController>().SelectedContainerId+" canPour="+Find<HeldItemInputController>().CanPour+" last="+Find<ContainerUseController>().LastResult.Transfer.Failure+" worldAllowed="+Session.WorldActionsAllowed+" pos="+Player.transform.position);
            Click(false);yield return null;
        }
        [UnityTest] public IEnumerator FocusRestoreWaitsForReleaseAndFDoesNotCycleConfiguredTools()
        {
            MoveToExperiment();AimIntoExperiment();Bind(0,"b0");Bind(2,"b1");Keys(Key.Digit4);yield return null;Keys();yield return null;Click(true);yield return new WaitForSeconds(.2f);
            var input=Find<HeldItemInputController>();input.SendMessage("OnApplicationFocus",false);yield return null;ulong frozen=Bottle("b0");
            yield return new WaitForSecondsRealtime(.2f);Assert.That(Bottle("b0"),Is.EqualTo(frozen));input.SendMessage("OnApplicationFocus",true);yield return new WaitForSeconds(.2f);Assert.That(Bottle("b0"),Is.EqualTo(frozen));
            Click(false);yield return null;Keys(Key.F);yield return null;Keys();yield return null;Assert.That(Session.ToolbeltReadOnly.SelectedItemId,Is.EqualTo("b0"));Assert.That(Find<MaterialCollectionController>().SelectedContainerId,Is.EqualTo("b0"));
            Keys(Key.Digit6);yield return null;Keys();yield return null;Assert.That(Session.ToolbeltReadOnly.SelectedItemId,Is.EqualTo("b1"));
        }
        [UnityTest] public IEnumerator GThrowsChosenContainerAndClearsItsBinding()
        {
            MoveToExperiment();Bind(0,"b0");Keys(Key.Digit4);yield return null;Keys();yield return null;
            Keys(Key.G);yield return null;Keys();yield return new WaitForEndOfFrame();
            Assert.That(Session.ToolbeltReadOnly.TryGetBinding(0,out _),Is.False);
            Assert.That(Session.ReadOnly.TryGetView("b0",out var item),Is.True);Assert.That(item.Location.Kind,Is.EqualTo(ItemLocationKind.ThrownContainer));
            Assert.That(Bottle("b1"),Is.EqualTo(100));
        }
        [UnityTest] public IEnumerator ContainerModeDoesNotQueueCastAndOneReturnsToWand()
        {
            Bind(0,"b0");Keys(Key.Digit4);yield return null;Keys();Click(true);yield return null;
            Assert.That(Player.AttackBufferCounter,Is.Zero);Keys(Key.Digit1);yield return null;Keys();yield return null;
            Assert.That(Session.ToolbeltReadOnly.SelectedSlot,Is.EqualTo(0));Assert.That(Player.AttackBufferCounter,Is.Zero);
            Click(false);yield return null;
        }
        [UnityTest] public IEnumerator PauseStopsPourAndHeldMouseDoesNotResumeAfterClosing()
        {
            MoveToExperiment();AimIntoExperiment();Bind(0,"b0");Keys(Key.Digit4);yield return null;Keys();yield return null;Click(true);yield return new WaitForSeconds(.2f);
            Backpack.OpenBackpack();yield return null;ulong total=Bottle("b0");
            yield return new WaitForSecondsRealtime(.2f);Assert.That(Bottle("b0"),Is.EqualTo(total));Backpack.Close();yield return new WaitForSeconds(.2f);
            Assert.That(Bottle("b0"),Is.EqualTo(total));Click(false);yield return null;
        }
    }
}
#endif
