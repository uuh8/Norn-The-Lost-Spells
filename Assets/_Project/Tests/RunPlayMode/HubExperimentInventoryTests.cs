#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Game.Character;
using Game.Combat;
using Game.Core;
using Game.ElementField;
using Game.Materials;
using Game.Skills;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace Game.Run.Tests
{
    /// <summary>直接HubScene；真实瓶/世界/施法/买卖/出发返回，独立内存Profile不接触玩家存档。</summary>
    public sealed class HubExperimentInventoryTests
    {
        private const string Path="Assets/_Project/Scenes/Tests/HubFeedbackSandbox.unity";
        private Scene _scene;
        private SinglePlayerInventorySession _session;
        private SinglePlayerRunCoordinator _run;
        private WizardController _player;
        private ElementWorldRuntime _world;
        private MaterialCollectionController _collect;
        private ContainerUseController _use;
        private SinglePlayerInventoryController _ui;
        private ShopController _shop;
        private HubInteractionController _hub;
        private ExperimentAreaController _experiment;
        private string _bottle;
        private bool _background;
        private MemoryFiles _files;
        [UnitySetUp]public IEnumerator Setup()
        {
            Assert.That(System.IO.File.Exists(Path),Is.True,"直接HubScene尚未生成。");
            _background=Application.runInBackground;Application.runInBackground=true;Time.timeScale=1;
            EditorWindow.GetWindow(Type.GetType("UnityEditor.GameView,UnityEditor")).Focus();
            SceneManager.sceneLoaded+=Loaded;_scene=EditorSceneManager.LoadSceneInPlayMode(Path,new LoadSceneParameters(LoadSceneMode.Additive));
            yield return null;yield return null;yield return null;SceneManager.sceneLoaded-=Loaded;
            Assert.That(_session.ReadOnly,Is.Not.Null);_world.SetSimulationPausedForDebug(true);Move(Vector3.zero+Vector3.up*.1f);
        }
        private static T Find<T>(Scene scene)where T:Component
        {foreach(var root in scene.GetRootGameObjects()){var c=root.GetComponentInChildren<T>(true);if(c!=null)return c;}return null;}
        private void Loaded(Scene scene,LoadSceneMode mode)
        {
            if(scene.path!=Path)return;_scene=scene;_session=Find<SinglePlayerInventorySession>(scene);_run=Find<SinglePlayerRunCoordinator>(scene);_player=Find<WizardController>(scene);_world=Find<ElementWorldRuntime>(scene);
            _collect=_player.GetComponent<MaterialCollectionController>();_use=_player.GetComponent<ContainerUseController>();_ui=Find<SinglePlayerInventoryController>(scene);_shop=Find<ShopController>(scene);_hub=Find<HubInteractionController>(scene);_experiment=Find<ExperimentAreaController>(scene);
            var data=new SerializedObject(_session);var catalog=(ItemCatalog)data.FindProperty("_catalog").objectReferenceValue;var seeds=(SinglePlayerStartingInventory)data.FindProperty("_startingInventory").objectReferenceValue;
            Assert.That(catalog.TryCreateIndex(out var index,out _),Is.True);Assert.That(seeds.TryCreate(index,out var initial,out _),Is.True);
            var profile=new SinglePlayerProfile(initial.ActorId,initial.Items,currency:100);
            foreach(var e in profile.Items)if(e.Item.Composition!=null){_bottle=e.Item.InstanceId;e.Item.Composition.AddReserved(new[]{new MaterialAmount(1,36),new MaterialAmount(3,24)},2);break;}
            foreach(var e in profile.Items)if(e.Location.Kind==ItemLocationKind.EquippedBackpack){Assert.That(profile.Items.TryMove(_bottle,new GridPlacement(0,0,0).InBackpack(e.Item.InstanceId)).Success,Is.True);break;}
            // 仅该内存Fixture提供可辨认的真实实例供布局/筛选验收；生产新档没有测试礼物。
            Assert.That(profile.Items.TryAdd(new RuntimeItemState("ui.fire","wandtest.fire",6),ItemLocation.Warehouse),Is.True);
            Assert.That(profile.Items.TryAdd(new RuntimeItemState("ui.ice","wandtest.ice",8),ItemLocation.Warehouse),Is.True);
            _files=new MemoryFiles();
            Assert.That(_session.Initialize(profile,new ProfileSaveRepository("Temp/SinglePlayer/HubMemory/"+Guid.NewGuid().ToString("N"),index,_files)),Is.True);
        }
        private void Move(Vector3 point){bool enabled=_player.CharacterController.enabled;_player.CharacterController.enabled=false;_player.transform.position=point;_player.CharacterController.enabled=enabled;}
        private ulong Bottle(){Assert.That(_session.TryCopyContainer(_bottle,new MaterialAmount[8],out _,out ulong total,out _),Is.True);return total;}
        private ExperimentWorldQuery Query(){var q=new ExperimentWorldQuery{WorldId="container-world"};EventBus<ExperimentWorldQueryEvent>.Publish(new ExperimentWorldQueryEvent{Query=q});Assert.That(q.Ready,Is.True);return q;}
        private void Pour(ulong quantity=30){_use.SubmitUse(ContainerUseKind.Pour,quantity,new Vector3(.25f,.25f,.25f));Assert.That(_use.LastResult.Success,Is.True,"投放失败="+_use.LastResult.Transfer.Failure+" 选瓶="+_collect.SelectedContainerId+" inside="+_experiment.IsInside+" scale="+Time.timeScale);}
        private string BagId(){foreach(var e in _session.Profile.Items)if(e.Location.Kind==ItemLocationKind.EquippedBackpack)return e.Item.InstanceId;return null;}
        private WorldItemPickup Drop(string id)
        {
            EventBus<InventoryCommandEvent>.Publish(new InventoryCommandEvent{InventoryId=_session.ReadOnly.InventoryId,Kind=InventoryCommandKind.Drop,ItemId=id,ExpectedVersion=_session.ReadOnly.Version});
            var proxy=WorldItemPickup.FindNearby(_session.ReadOnly.InventoryId,id,_player.transform,3);Assert.That(proxy,Is.Not.Null);return proxy;
        }
        [UnityTest]public IEnumerator ActualPickupKeyMovesNearbyInstanceWithoutCloning()
        {
            var proxy=Drop("ui.fire");int count=_session.ReadOnly.Count;var keyboard=InputSystem.AddDevice<Keyboard>();
            try{InputSystem.QueueStateEvent(keyboard,new KeyboardState(Key.E));InputSystem.Update();yield return null;
                Assert.That(_session.ReadOnly.TryGetView("ui.fire",out var item),Is.True);Assert.That(item.Location.Kind,Is.EqualTo(ItemLocationKind.Backpack));
                Assert.That(_session.ReadOnly.Count,Is.EqualTo(count));Assert.That(_ui.IsOpen,Is.False);Assert.That(proxy==null || !proxy.gameObject.activeSelf,Is.True);}
            finally{InputSystem.RemoveDevice(keyboard);}
        }
        [UnityTest]public IEnumerator FullBagOpensArrangementAndRealDragPickupCommitsOnlyAfterFreeingSpace()
        {
            string bagId=BagId();_session.ReadOnly.TryGetView(bagId,out var bag);string freed=null;
            for(int y=0;y<bag.Rules.BackpackHeight;y++)for(int x=0;x<bag.Rules.BackpackWidth;x++)
            {string id="filler."+x+"."+y;if(_session.Profile.Items.TryAdd(new RuntimeItemState(id,"wandtest.ice",1,false,id),new GridPlacement(x,y,0).InBackpack(bagId)))freed=id;}
            Assert.That(freed,Is.Not.Null);var proxy=Drop("ui.fire");uint version=_session.ReadOnly.Version;var keyboard=InputSystem.AddDevice<Keyboard>();
            try
            {
                InputSystem.QueueStateEvent(keyboard,new KeyboardState(Key.E));InputSystem.Update();yield return null;
                Assert.That(_ui.IsOpen,Is.True);Assert.That(_ui.WarehouseMode,Is.False);Assert.That(_session.ReadOnly.Version,Is.EqualTo(version));
                _session.ReadOnly.TryGetView("ui.fire",out var ground);Assert.That(ground.Location.Kind,Is.EqualTo(ItemLocationKind.WorldItem));Assert.That(proxy.gameObject.activeSelf,Is.True);
                _session.ReadOnly.TryGetView(freed,out var filler);_ui.SelectItem(freed);_ui.DropSelected();
                _ui.BeginDrag(ground,Vector2.zero,null,true);_ui.SubmitToBackpack(filler.Location.X,filler.Location.Y,bagId);
                _session.ReadOnly.TryGetView("ui.fire",out var picked);Assert.That(picked.Location.Kind,Is.EqualTo(ItemLocationKind.Backpack));Assert.That(picked.Location.X,Is.EqualTo(filler.Location.X));
            }
            finally{InputSystem.RemoveDevice(keyboard);}
        }
        [UnityTest]public IEnumerator FinalPickupGateRejectsRemoteAndDifferentSceneEvenWithCachedUiItem()
        {
            var proxy=Drop("ui.fire");uint version=_session.ReadOnly.Version;proxy.transform.position=_player.transform.position+Vector3.right*10;
            Assert.That(proxy.TryCollect(BagId(),new GridPlacement(1,0,0),version).Success,Is.False);Assert.That(_session.ReadOnly.Version,Is.EqualTo(version));
            proxy.transform.position=_player.transform.position;proxy.transform.SetParent(null);var foreign=SceneManager.CreateScene("Other pickup scene");SceneManager.MoveGameObjectToScene(proxy.gameObject,foreign);
            Assert.That(proxy.TryCollect(BagId(),new GridPlacement(1,0,0),version).Success,Is.False);Assert.That(Find<NearbyPickupController>(_scene).IsNearby("ui.fire"),Is.False);
            yield return SceneManager.UnloadSceneAsync(foreign);
        }
        [UnityTest]public IEnumerator ToolRowSelectsSameCarriedContainerAndWandStatsFollowSelection()
        {
            _ui.OpenBackpack();yield return null;var workspace=Find<InventoryWorkspaceView>(_scene);
            var root=(RectTransform)typeof(InventoryWorkspaceView).GetField("_toolsRoot",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(workspace);
            InventoryItemView tile=null;foreach(var view in root.GetComponentsInChildren<InventoryItemView>())if(view.Snapshot.InstanceId==_bottle)tile=view;
            Assert.That(tile,Is.Not.Null);int count=_session.ReadOnly.Count;tile.OnPointerClick(new UnityEngine.EventSystems.PointerEventData(Find<UnityEngine.EventSystems.EventSystem>(_scene)));Assert.That(_collect.SelectedContainerId,Is.EqualTo(_bottle));Assert.That(_session.ReadOnly.Count,Is.EqualTo(count));
            _session.WandsReadOnly.TryGetSlot(1,out var wand);_ui.SelectItem(wand.InstanceId);
            var stats=(Text)typeof(WandInventoryView).GetField("_stats",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(Find<WandInventoryView>(_scene));
            Assert.That(stats.text,Does.Contain(wand.MaxMana.ToString("0.#")));Assert.That(stats.text,Does.Contain("根层蓝耗"));
            var protect=(Button)typeof(RaidPreparationView).GetField("_protect",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(Find<RaidPreparationView>(_scene));
            Assert.That(protect.gameObject.activeInHierarchy,Is.False,"选中法杖不显示无关的保护法术按钮。");
        }
        [UnityTest]public IEnumerator ReturningToPortableModeDiscardsWarehouseBagPreview()
        {
            string carried=BagId();_session.ReadOnly.TryGetView(carried,out var bag);
            Assert.That(_session.Profile.Items.TryAdd(new RuntimeItemState("preview.bag",bag.ContentId,0),ItemLocation.Warehouse),Is.True);
            Move(new Vector3(-5,.1f,0));_hub.Request(HubInteractionKind.Warehouse);_ui.SelectItem("preview.bag");yield return null;
            Assert.That(Find<InventoryGridView>(_scene).BackpackId,Is.EqualTo("preview.bag"));
            _ui.OpenBackpack();yield return null;Assert.That(Find<InventoryGridView>(_scene).BackpackId,Is.EqualTo(carried));
        }
        [UnityTest]public IEnumerator NewHubHasNoFreeWorldSamples()
        {Assert.That(Find<ContainerSandboxMaterialAuthoring>(_scene),Is.Null);Assert.That(Query().Total,Is.Zero);Assert.That(Bottle(),Is.EqualTo(60));yield return null;}
        [UnityTest]public IEnumerator OutsideExperimentCannotSpendOrCreateMaterials()
        {Move(new Vector3(6,.1f,-1));_use.SubmitUse(ContainerUseKind.Pour,30,new Vector3(.25f,.25f,.25f));Assert.That(_use.LastResult.Success,Is.False);Assert.That(Bottle(),Is.EqualTo(60));Assert.That(Query().Total,Is.Zero);yield return null;}
        [UnityTest]public IEnumerator PourAndRecollectTransferExactMixedQuantity()
        {Pour();Assert.That(Bottle(),Is.EqualTo(30));Assert.That(Query().Total,Is.EqualTo(30));_collect.SubmitSample(new Vector3(.25f,.25f,.25f),false,30);Assert.That(_collect.LastFailure,Is.EqualTo(MaterialTransferFailure.None));Assert.That(Bottle(),Is.EqualTo(60));Assert.That(Query().Total,Is.Zero);yield return null;}
        [UnityTest]public IEnumerator LeavingExperimentSuspendsWorldWithoutRefundOrDeletion()
        {Pour();Move(new Vector3(6,.1f,-1));yield return null;Assert.That(_world.SimulationSuspended,Is.True);Assert.That(Query().Total,Is.EqualTo(30));Assert.That(Bottle(),Is.EqualTo(30));Move(new Vector3(0,.1f,0));yield return null;Assert.That(_world.SimulationSuspended,Is.False);Assert.That(Query().Total,Is.EqualTo(30));}
        [UnityTest]public IEnumerator SavedInventoryContainsOnlyRecollectedMaterial()
        {Pour();Assert.That(_session.Save().Success,Is.True);Assert.That(_session.Repository.TryLoad(out var saved).Success,Is.True);Assert.That(saved.Items.TryGet(_bottle,out var bottle),Is.True);Assert.That(bottle.Composition.Total,Is.EqualTo(30));Assert.That(Query().Total,Is.EqualTo(30));yield return null;}
        [UnityTest]public IEnumerator AttachedCastConsumesActualStockInHub()
        {
            _session.WandsReadOnly.TryGetSlot(0,out var wand);_session.WandsReadOnly.TryGetProgram(wand.InstanceId,out var program);string card=program.Program.GetInstanceId(0);
            Find<SpellAttachmentView>(_scene).Submit(card,_bottle,10,true);var adapter=_player.GetComponent<OwnedWandCastAdapter>();Assert.That(adapter.TryCapture(out var projection,out var context),Is.True);
            int count=_player.GetComponent<SpellCaster>().CastOwnedProgram(projection,context,new Vector3(1.25f,2,.25f),new Vector3(1.25f,-2,.25f),1,777,_player.CharacterController);
            Assert.That(count,Is.EqualTo(1));Assert.That(Bottle(),Is.EqualTo(50));yield return new WaitForSeconds(.6f);Assert.That(_world.ImportedMaterialQuantity(MaterialId.Water)+_world.ImportedMaterialQuantity(MaterialId.Poison),Is.EqualTo(10));
        }
        [UnityTest]public IEnumerator RootCastOutsideExperimentKeepsManaAndStock()
        {
            Move(new Vector3(6,.1f,-1));_session.WandsReadOnly.TryGetSlot(0,out var before);var adapter=_player.GetComponent<OwnedWandCastAdapter>();adapter.TryCapture(out var projection,out var context);
            int count=_player.GetComponent<SpellCaster>().CastOwnedProgram(projection,context,new Vector3(0,2,0),new Vector3(0,-2,0),1,777,_player.CharacterController);
            Assert.That(count,Is.Zero);_session.WandsReadOnly.TryGetSlot(0,out var after);Assert.That(after.CurrentMana,Is.EqualTo(before.CurrentMana));Assert.That(Bottle(),Is.EqualTo(60));yield return null;
        }
        [UnityTest]public IEnumerator WarehouseInteractionRequiresNearStationAndOpensCorrectLayout()
        {
            var keyboard=InputSystem.AddDevice<Keyboard>();try
            {Move(new Vector3(-5,.1f,0));InputSystem.QueueStateEvent(keyboard,new KeyboardState(Key.E));InputSystem.Update();yield return null;
                Assert.That(_ui.IsOpen,Is.True);Assert.That(_ui.WarehouseMode,Is.True);_ui.Close();InputSystem.QueueStateEvent(keyboard,new KeyboardState());InputSystem.Update();
                Move(new Vector3(0,.1f,0));_hub.Request(HubInteractionKind.Warehouse);yield return null;Assert.That(_ui.IsOpen,Is.False);}
            finally{InputSystem.RemoveDevice(keyboard);}
        }
        [UnityTest]public IEnumerator ShopCannotTradeRemotelyAndHasCorrectColumnOrder()
        {
            _shop.Open();Assert.That(_shop.IsOpen,Is.False);Move(new Vector3(5,.1f,0));_hub.Request(HubInteractionKind.Shop);yield return null;Assert.That(_shop.IsOpen,Is.True);
            var offers=ShopField<RectTransform>("_offersRoot");var owned=ShopField<RectTransform>("_ownedRoot");var detail=ShopField<Text>("_details");Assert.That(offers.position.x,Is.LessThan(detail.transform.position.x));Assert.That(detail.transform.position.x,Is.LessThan(owned.position.x));
            _shop.SelectOffer("material.1");ShopField<InputField>("_quantity").text="10";ShopField<Button>("_buyButton").onClick.Invoke();Assert.That(Bottle(),Is.EqualTo(70));Assert.That(Find<SinglePlayerShopSession>(_scene).ReadOnly.Currency,Is.EqualTo(80));
        }
        private T ShopField<T>(string name)where T:class=>(T)typeof(ShopController).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(_shop);
        private int Cast()
        {
            var adapter=_player.GetComponent<OwnedWandCastAdapter>();Assert.That(adapter.TryCapture(out var projection,out var context),Is.True);
            return _player.GetComponent<SpellCaster>().CastOwnedProgram(projection,context,new Vector3(.25f,2,.25f),new Vector3(.25f,-2,.25f),1,777,_player.CharacterController);
        }
        [UnityTest]public IEnumerator FiniteExperimentCastSpendsUsesAndEmptyBottleStillCastsBaseSpell()
        {
            _session.WandsReadOnly.TryGetSlot(0,out var wand);_session.WandsReadOnly.TryGetProgram(wand.InstanceId,out var program);string id=program.Program.GetInstanceId(0);
            _session.Profile.Items.TryGet(id,out var card);_session.Profile.Items.TryGetLocation(id,out var location);
            Assert.That(_session.Profile.Items.TryApply(new[]{new InventoryEntry(new RuntimeItemState(id,card.ContentId,3,false,card.StackStateKey),location)},_session.ReadOnly.Version).Success,Is.True);
            Find<SpellAttachmentView>(_scene).Submit(id,_bottle,100,true);Assert.That(Cast(),Is.EqualTo(1));Assert.That(Bottle(),Is.Zero);
            yield return new WaitForSeconds(.8f);Assert.That(Query().Total,Is.EqualTo(60));
            Assert.That(Cast(),Is.EqualTo(1));Assert.That(Bottle(),Is.Zero);Assert.That(_session.ReadOnly.TryGetView(id,out var after),Is.True);Assert.That(after.RemainingCharges,Is.EqualTo(1));yield return null;
        }
        [UnityTest]public IEnumerator RealColdConversionChangesMaterialIdentityWithoutInventoryRefund()
        {
            // 混样不能在残留毒液中直接插入冰墙；买入纯水到另一只实际空瓶，再验证冻结。
            Move(new Vector3(5,.1f,0));_shop.Open();var ids=ShopField<List<string>>("_containerIds");int target=-1;
            for(int i=0;i<ids.Count;i++){_session.TryCopyContainer(ids[i],new MaterialAmount[8],out _,out ulong total,out _);if(total==0){target=i;break;}}
            Assert.That(target,Is.GreaterThanOrEqualTo(0));string pureBottle=ids[target];ShopField<Dropdown>("_containers").value=target;ShopField<InputField>("_quantity").text="30";_shop.SelectOffer("material.1");_shop.Buy();_shop.Close();Move(new Vector3(0,.1f,0));
            Assert.That(_session.Profile.Currency,Is.EqualTo(40));
            var host=new GameObject("Test Cold Condition");SceneManager.MoveGameObjectToScene(host,_scene);host.SetActive(false);
            // 环境扫描有意限定32粗格/Tick；8m立方体需要超过5秒才扫到中心。
            // 聚焦测试使用覆盖投放点的2m条件区，不提高生产扫描预算来缩短测试。
            host.transform.position=new Vector3(.25f,.25f,.25f);var zone=host.AddComponent<MaterialEnvironmentAuthoring>();var data=new SerializedObject(zone);data.FindProperty("_world").objectReferenceValue=_world;data.FindProperty("_size").vector3Value=Vector3.one*2;data.FindProperty("_temperature").floatValue=-10;data.ApplyModifiedPropertiesWithoutUndo();host.SetActive(true);
            EventBus<ContainerUseRequestedEvent>.Publish(new ContainerUseRequestedEvent{InventoryId=_session.ReadOnly.InventoryId,WorldId="container-world",SourceId="cold-test",RequestId=1,ItemId=pureBottle,Kind=ContainerUseKind.Pour,Budget=30,ExpectedVersion=_session.ReadOnly.Version,ActorPosition=_player.transform.position,TargetPosition=new Vector3(.25f,.25f,.25f),Radius=.3f});
            _session.TryCopyContainer(pureBottle,new MaterialAmount[8],out _,out ulong remaining,out _);Assert.That(remaining,Is.Zero);Assert.That(Query().Total,Is.EqualTo(30));
            Move(new Vector3(-2.5f,.1f,1));Physics.SyncTransforms();_world.SetSimulationPausedForDebug(false);
            float until=Time.realtimeSinceStartup+5;ulong ice=0;
            while(ice==0 && Time.realtimeSinceStartup<until){var query=Query();for(int i=0;i<query.Materials.Length;i++)if(query.Materials[i].MaterialKey==5)ice+=query.Materials[i].Quantity;yield return null;}
            if(ice==0)Assert.Fail(ColdDiagnostics());Assert.That(Bottle(),Is.EqualTo(60));
            _session.TryCopyContainer(pureBottle,new MaterialAmount[8],out _,out remaining,out _);Assert.That(remaining,Is.Zero);
            Assert.That(_world.TryCaptureMaterialQuantityFamily(MaterialQuantityFamily.WaterBased,out var audit),Is.True);Assert.That(audit.IsBalanced,Is.True);
        }
        private string ColdDiagnostics()
        {
            var scheduler=typeof(ElementWorldRuntime).GetField("_conversionScheduler",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(_world);var type=scheduler.GetType();var flags=BindingFlags.NonPublic|BindingFlags.Instance;
            string message="Cold conversion Deferred="+_world.ConversionDeferredTransactions+" Candidates="+_world.ConversionPendingCandidates+" Total="+Query().Total+" Ticks="+_world.LastFrameTickCount+" Clock="+type.GetField("_clock",flags).GetValue(scheduler)+" Environments="+type.GetField("_environmentCount",flags).GetValue(scheduler);
            var candidates=(Array)type.GetField("_candidates",flags).GetValue(scheduler);var position=type.GetMethod("Position",flags);var env=type.GetMethod("EnvironmentAt",flags);var active=typeof(ElementWorldRuntime).GetMethod("IsConversionAddressActive",flags);
            foreach(var candidate in candidates){var t=candidate.GetType();var address=t.GetField("Address",flags).GetValue(candidate);var a=address.GetType();var material=a.GetField("Material",BindingFlags.Public|flags).GetValue(address);if(material.ToString()=="Empty")continue;var point=position.Invoke(scheduler,new[]{address});var sample=(MaterialEnvironmentSample)env.Invoke(scheduler,new[]{point});message+="\n"+material+" position="+point+" active="+active.Invoke(_world,new[]{address})+" temperature="+sample.Temperature+" rule="+t.GetField("Rule",flags).GetValue(candidate)+" credit="+t.GetField("Credit",flags).GetValue(candidate);}
            return message;
        }
        [UnityTest]public IEnumerator FireSpellConsumesRealPoisonThroughExistingReactionAndCannotRefundIt()
        {
            Pour(60);Assert.That(Cast(),Is.EqualTo(1));_world.SetSimulationPausedForDebug(false);
            float until=Time.realtimeSinceStartup+5;while(_world.ConversionLedger.ExplicitlyConsumed(MaterialId.Poison)==0 && Time.realtimeSinceStartup<until)yield return null;
            ulong consumed=_world.ConversionLedger.ExplicitlyConsumed(MaterialId.Poison);Assert.That(consumed,Is.GreaterThan(0),"真实Fire命中→Grid Contact→ToxicCombustion消费，没有直接删除库存冒充反应。");
            Assert.That(Bottle(),Is.Zero);Assert.That(_world.TryCaptureMaterialQuantityFamily(MaterialQuantityFamily.Toxic,out var audit),Is.True);Assert.That(audit.IsBalanced,Is.True);Assert.That(audit.Resident+audit.Pending+audit.Vented+consumed,Is.EqualTo(24));
            _collect.SubmitSample(new Vector3(.25f,.25f,.25f),false,100);Assert.That(Bottle(),Is.LessThan(60),"采集只能取剩余真实样本，消耗的毒液不会免费返还。");
        }
        [UnityTest]public IEnumerator StableHubAndOpenShopUpdateScopesHaveNoGcAlloc()
        {
            bool recording=UnityEditorInternal.ProfilerDriver.enabled;bool cpu=UnityEditorInternal.ProfilerDriver.IsAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU);
            try
            {
                Move(new Vector3(5,.1f,0));_shop.Open();
                UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,true);UnityEditorInternal.ProfilerDriver.enabled=true;
                for(int i=0;i<8;i++)yield return null;int first=UnityEditorInternal.ProfilerDriver.lastFrameIndex+1;for(int i=0;i<60;i++)yield return null;
                var report=new HubProfile{Utc=DateTime.UtcNow.ToString("O"),Scope="Editor TestRunner, stable Hub shop open and paused, no transactions; named Update scopes only",FirstFrame=first,LastFrame=UnityEditorInternal.ProfilerDriver.lastFrameIndex,
                    Markers=new[]{new HubMarker{Name="Norn.Hub.NavigationUpdate"},new HubMarker{Name="Norn.Hub.ExperimentUpdate"},new HubMarker{Name="Norn.Shop.UiUpdate"},new HubMarker{Name="Norn.Shop.SessionUpdate"}}};
                for(int frame=Math.Max(first,UnityEditorInternal.ProfilerDriver.firstFrameIndex);frame<=report.LastFrame;frame++)
                {
                    using var data=UnityEditorInternal.ProfilerDriver.GetRawFrameDataView(frame,0);if(!data.valid || data.sampleCount==0)continue;report.ValidFrames++;report.AverageMainThreadMs+=data.GetSampleTimeMs(0);
                    for(int sample=0;sample<data.sampleCount;sample++)
                    {
                        string name=data.GetSampleName(sample);if(name=="GC.Alloc" && data.GetSampleMetadataCount(sample)>0)report.MainThreadGcBytes+=data.GetSampleMetadataAsLong(sample,0);
                        for(int m=0;m<report.Markers.Length;m++)if(name==report.Markers[m].Name)
                        {var marker=report.Markers[m];double start=data.GetSampleStartTimeMs(sample),duration=data.GetSampleTimeMs(sample);marker.Samples++;marker.MaximumMs=Math.Max(marker.MaximumMs,duration);
                            for(int child=sample+1;child<data.sampleCount;child++){double time=data.GetSampleStartTimeMs(child);if(time>start+duration)break;if(time>=start && data.GetSampleName(child)=="GC.Alloc" && data.GetSampleMetadataCount(child)>0)marker.GcBytes+=data.GetSampleMetadataAsLong(child,0);}}
                    }
                }
                if(report.ValidFrames>0)report.AverageMainThreadMs/=report.ValidFrames;
                System.IO.Directory.CreateDirectory("TestResults/SinglePlayer/M6-T1-Feedback");System.IO.File.WriteAllText("TestResults/SinglePlayer/M6-T1-Feedback/shop-profiler.json",JsonUtility.ToJson(report,true));UnityEditorInternal.ProfilerDriver.SaveProfile("TestResults/SinglePlayer/M6-T1-Feedback/shop-profiler.data");
                Assert.That(report.ValidFrames,Is.GreaterThanOrEqualTo(30));foreach(var marker in report.Markers){Assert.That(marker.Samples,Is.GreaterThan(0),marker.Name);Assert.That(marker.GcBytes,Is.Zero,marker.Name);}
            }
            finally{UnityEditorInternal.ProfilerDriver.enabled=recording;UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,cpu);}
        }
        [Serializable]private sealed class HubMarker{public string Name;public int Samples;public double MaximumMs;public long GcBytes;}
        [Serializable]private sealed class HubProfile{public string Utc,Scope;public int FirstFrame,LastFrame,ValidFrames;public double AverageMainThreadMs;public long MainThreadGcBytes;public HubMarker[] Markers;}
        [UnityTest]public IEnumerator BoughtMaterialExperimentCollectionSuccessfulExitAndShopReopenShareOneProfile()
        {
            Move(new Vector3(5,.1f,0));_shop.Open();_shop.SelectOffer("material.1");ShopField<InputField>("_quantity").text="10";_shop.Buy();_shop.Close();Assert.That(Bottle(),Is.EqualTo(70));
            Move(new Vector3(0,.1f,0));Pour(20);_collect.SubmitSample(new Vector3(.25f,.25f,.25f),false,10);Assert.That(_collect.LastFailure,Is.EqualTo(MaterialTransferFailure.None));Assert.That(Bottle(),Is.EqualTo(60));
            Move(new Vector3(0,.1f,-5));_experiment.AcknowledgeDeparture();Assert.That(_run.Depart(),Is.True);
            float until=Time.realtimeSinceStartup+8;while(_run.State.Phase!=RaidPhase.Exploring && Time.realtimeSinceStartup<until)yield return null;
            Assert.That(_run.State.Phase,Is.EqualTo(RaidPhase.Exploring));var raid=SceneManager.GetSceneByPath(_run.Definition.WorldScenePath);var supply=Find<ContainerSandboxMaterialAuthoring>(raid);yield return null;
            Move(supply.MixedSamplePosition+new Vector3(0,.5f,-.5f));_collect.SubmitSample(supply.MixedSamplePosition,false,10);Assert.That(_collect.LastFailure,Is.EqualTo(MaterialTransferFailure.None));
            Move(_run.Definition.ExitPosition);yield return null;yield return null;EventBus<ExtractionRequestedEvent>.Publish(new ExtractionRequestedEvent{RaidId=_run.State.RaidId,Start=true});
            until=Time.realtimeSinceStartup+10;while(_run.State.Phase!=RaidPhase.HubReady && Time.realtimeSinceStartup<until)yield return null;
            Assert.That(_run.LastResult.Outcome,Is.EqualTo(RaidOutcome.Success));Assert.That(_session.Profile.AdventureProgress,Is.EqualTo(1));Assert.That(Bottle(),Is.EqualTo(70));Assert.That(Query().Total,Is.Zero);
            Move(new Vector3(5,.1f,0));_shop.Open();Assert.That(Find<SinglePlayerShopSession>(_scene).ReadOnly.Currency,Is.EqualTo(80));Assert.That(_session.Profile.Shop.AppliedProgress,Is.EqualTo(1));
            long saved=_session.SavedGeneration;_shop.Close();_shop.Open();Assert.That(_session.SavedGeneration,Is.EqualTo(saved));Assert.That(_session.Repository.TryLoad(out var profile).Success,Is.True);Assert.That(profile.Items.TryGet(_bottle,out var bottle),Is.True);Assert.That(bottle.Composition.Total,Is.EqualTo(70));
        }
        [UnityTest]public IEnumerator ReviewDoesNotCreateResidueConfirmationAndEscapeClosesInventory()
        {
            var keyboard=InputSystem.AddDevice<Keyboard>();try
            {Move(new Vector3(0,.1f,-5));_hub.Request(HubInteractionKind.Expedition);yield return null;var pause=Find<RunPauseCoordinator>(_scene);Assert.That(pause.HasReason(RunPauseReason.HubConfirmation),Is.False);Assert.That(_ui.IsOpen,Is.True);
                InputSystem.QueueStateEvent(keyboard,new KeyboardState(Key.Escape));InputSystem.Update();yield return null;Assert.That(pause.HasReason(RunPauseReason.HubConfirmation),Is.False);Assert.That(Time.timeScale,Is.EqualTo(1));Assert.That(_run.State.Phase,Is.EqualTo(RaidPhase.HubReady));}
            finally{InputSystem.RemoveDevice(keyboard);}
        }
        [UnityTest]public IEnumerator SearchAndCategoryFilterOnlyVisibleOffersAndBackpackEquipmentStaysInItsColumn()
        {
            _ui.OpenBackpack();yield return null;var workspace=Find<InventoryWorkspaceView>(_scene);var bag=(RectTransform)typeof(InventoryWorkspaceView).GetField("_bag",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(workspace);var equipment=((Text)typeof(InventoryWorkspaceView).GetField("_bagEquipment",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(workspace)).rectTransform;
            Assert.That(equipment.anchoredPosition.x+equipment.rect.width,Is.LessThanOrEqualTo(bag.rect.width));_ui.Close();Move(new Vector3(5,.1f,0));_shop.Open();ShopField<InputField>("_search").text="水";ShopField<Dropdown>("_category").value=3;yield return null;
            var rows=ShopField<RectTransform>("_offersRoot").GetComponentsInChildren<ShopEntryView>();Assert.That(rows.Length,Is.EqualTo(1));Assert.That(rows[0].GetComponentInChildren<Text>().text,Does.Contain("/ GMU"));Assert.That(_session.Profile.Currency,Is.EqualTo(100));
        }
        [UnityTest]public IEnumerator LeavingHubClearsActualWorldWithoutAcknowledgement()
        {
            Pour();Assert.That(Query().Total,Is.EqualTo(30));var oldWorld=_world;Move(new Vector3(0,.1f,-5));Assert.That(_run.Depart(),Is.True);
            float until=Time.realtimeSinceStartup+8;while(_run.State.Phase!=RaidPhase.Exploring && Time.realtimeSinceStartup<until)yield return null;
            yield return null;Assert.That(oldWorld==null,Is.True,"离开据点须Destroy实际世界，释放资源。");
            Assert.That(_run.State.Phase,Is.EqualTo(RaidPhase.Exploring));Assert.That(Time.timeScale,Is.EqualTo(1));Assert.That(Find<RunPauseCoordinator>(_scene).HasReason(RunPauseReason.HubConfirmation),Is.False);
            // 此测试聚焦场景资源边界；实际条件撤离另由上一项覆盖，不冒充完整撤离玩法。
            _run.State.RequestOutcome(RaidOutcome.Success);
            until=Time.realtimeSinceStartup+8;while(_run.State.Phase!=RaidPhase.HubReady && Time.realtimeSinceStartup<until)yield return null;
            Assert.That(_run.State.Phase,Is.EqualTo(RaidPhase.HubReady));Assert.That(Query().Total,Is.Zero);Assert.That(Bottle(),Is.EqualTo(30));
            _world=Find<ElementWorldRuntime>(_scene);Assert.That(_world,Is.Not.Null);_world.SetSimulationPausedForDebug(true);Move(new Vector3(0,.1f,0));yield return null;
            Pour(10);Assert.That(Query().Total,Is.EqualTo(10));_collect.SubmitSample(new Vector3(.25f,.25f,.25f),false,10);Assert.That(Bottle(),Is.EqualTo(30));Assert.That(Query().Total,Is.Zero);
        }
        [UnityTest]public IEnumerator FailedDepartureSaveRestoresVisibleFeedbackWithoutMovingAssets()
        {
            Move(new Vector3(0,.1f,-5));_hub.Request(HubInteractionKind.Expedition);yield return null;
            _files.FailWrites=true;var navigation=Find<HubNavigationView>(_scene);navigation.Continue();
            Assert.That(_run.State.Phase,Is.EqualTo(RaidPhase.HubReady));Assert.That(_session.Profile.PendingRaid,Is.Null);Assert.That(Bottle(),Is.EqualTo(60));
            var panel=(GameObject)typeof(HubNavigationView).GetField("_departurePanel",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(navigation);
            var message=(Text)typeof(HubNavigationView).GetField("_residue",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(navigation);
            Assert.That(panel.activeSelf,Is.True);Assert.That(message.text,Does.Contain(_run.Message));Assert.That(Time.timeScale,Is.Zero);
            _files.FailWrites=false;navigation.Cancel();Assert.That(Time.timeScale,Is.EqualTo(1));
        }
        [UnityTest]public IEnumerator MissingResidueQueryDoesNotBlockReliableDeparture()
        {
            Find<ExperimentWorldAdapter>(_scene).enabled=false;Move(new Vector3(0,.1f,-5));
            Assert.That(_experiment.CheckDeparture(),Is.True);Assert.That(_experiment.DepartureAcknowledged,Is.False);
            Assert.That(_run.Depart(),Is.True);Assert.That(Bottle(),Is.EqualTo(60));
            float until=Time.realtimeSinceStartup+8;while(_run.State.Phase!=RaidPhase.Exploring && Time.realtimeSinceStartup<until)yield return null;
            Assert.That(_run.State.Phase,Is.EqualTo(RaidPhase.Exploring));
        }
        [UnityTest]public IEnumerator ThreeWorkspaceLayoutsProduceActualGameViewEvidence()
        {
            Move(new Vector3(-5,.1f,0));_hub.Request(HubInteractionKind.Warehouse);_ui.SelectItem("ui.ice");yield return null;Canvas.ForceUpdateCanvases();Capture("warehouse-layout.png");yield return null;yield return new WaitForSecondsRealtime(.3f);
            _ui.Close();string bag=null;foreach(var entry in _session.Profile.Items)if(entry.Location.Kind==ItemLocationKind.EquippedBackpack)bag=entry.Item.InstanceId;
            Assert.That(_session.Profile.Items.TryMove("ui.fire",new GridPlacement(1,0,0).InBackpack(bag)).Success,Is.True);
            _ui.OpenBackpack();_ui.SelectItem(_bottle);yield return null;Canvas.ForceUpdateCanvases();Capture("backpack-layout.png");yield return null;yield return new WaitForSecondsRealtime(.3f);
            _ui.Close();Move(new Vector3(5,.1f,0));_hub.Request(HubInteractionKind.Shop);_shop.SelectOffer("material.1");yield return null;Canvas.ForceUpdateCanvases();Capture("shop-layout.png");yield return null;yield return new WaitForSecondsRealtime(.3f);
        }
        private static void Capture(string name){System.IO.Directory.CreateDirectory("TestResults/SinglePlayer/M6-T1-Feedback");ScreenCapture.CaptureScreenshot("TestResults/SinglePlayer/M6-T1-Feedback/"+name);}
        [UnityTearDown]public IEnumerator Cleanup()
        {
            SceneManager.sceneLoaded-=Loaded;if(_ui!=null)_ui.Close();if(_shop!=null)_shop.Close();
            if(_files!=null)_files.FailWrites=false;
            // 断言中途失败也须释放附加Raid世界，否则下个Fixture会撞上唯一世界写入口。
            if(_run!=null)_run.CloseSynchronously();
            var world=SceneManager.GetSceneByPath("Assets/_Project/Scenes/Tests/RaidWorldSandbox.unity");if(world.IsValid() && world.isLoaded)yield return SceneManager.UnloadSceneAsync(world);
            if(_scene.IsValid() && _scene.isLoaded)yield return SceneManager.UnloadSceneAsync(_scene);Time.timeScale=1;Application.runInBackground=_background;yield return null;
        }
        private sealed class MemoryFiles:IProfileFileSystem
        {private readonly Dictionary<string,string> _files=new Dictionary<string,string>();public bool FailWrites;public bool Exists(string p)=>_files.ContainsKey(p);public string Read(string p)=>_files[p];public void WriteFlushed(string p,string t){if(FailWrites)throw new System.IO.IOException("Injected departure write failure");_files[p]=t;}public void Move(string a,string b){_files[b]=_files[a];_files.Remove(a);}public void Replace(string a,string b,string backup){_files[backup]=_files[b];Move(a,b);}}
    }
}
#endif
