#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Game.Character;
using Game.Combat;
using Game.Core;
using Game.ElementField;
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
using Object=UnityEngine.Object;
namespace Game.Run.Tests
{
    public sealed class SinglePlayerRaidLifecycleTests
    {
        private const string HubPath="Assets/_Project/Scenes/Tests/DepartureSandbox.unity",Root="Assets/_Project/Tests/Fixtures/Run/SinglePlayerRaids/";
        private Scene _hub;
        private SinglePlayerInventorySession _session;
        private SinglePlayerRunCoordinator _run;
        private WizardController _wizard;
        private SinglePlayerInventoryController _ui;
        private RaidPreparationView _view;
        private MaterialCollectionController _collection;
        private ItemCatalog _catalog;
        private RaidDefinition _definition;
        private HealthComponent _health;
        private RaidFaultFiles _files;
        private string _directory,_card,_hubCard;
        private float _time;private bool _cursor;private CursorLockMode _lock;private EditorWindow _window;
        private bool _reload;
        private InputTestFixture _isolatedInput;
        private Keyboard _testKeyboard;
        private readonly List<Object> _temporary=new List<Object>();
        private static FieldInfo Field(Type t,string n)=>t.GetField(n,BindingFlags.Instance|BindingFlags.NonPublic);
        [UnitySetUp] public IEnumerator Setup()
        {
            _time=Time.timeScale;_cursor=Cursor.visible;_lock=Cursor.lockState;_window=EditorWindow.focusedWindow;Time.timeScale=1;
            if(TestContext.CurrentContext.Test.Name=="AuthoredWorldItemCanBeCollectedAndReturnedWithSameIdentity"
                || TestContext.CurrentContext.Test.Name=="ActualCollectionAndExitReturnOnlyCarriedRemainingAssets"
                || TestContext.CurrentContext.Test.Name=="ActualExitDamageDoesNotCancelAndPlayerCanCancelAndReenter")
            {_isolatedInput=new InputTestFixture();_isolatedInput.Setup();_testKeyboard=InputSystem.AddDevice<Keyboard>();InputSystem.AddDevice<Mouse>();}
            EditorWindow.GetWindow(Type.GetType("UnityEditor.GameView,UnityEditor")).Focus();
            _directory=Path.GetFullPath(Path.Combine("Temp/SinglePlayer/RaidTests",Guid.NewGuid().ToString("N")));_reload=false;
            _catalog=Object.Instantiate(AssetDatabase.LoadAssetAtPath<ItemCatalog>(Root+"Catalog.asset"));_temporary.Add(_catalog);
            var original=(ItemDefinition[])Field(typeof(ItemCatalog),"_definitions").GetValue(_catalog);var copies=new ItemDefinition[original.Length];
            for(int i=0;i<copies.Length;i++)
            {copies[i]=Object.Instantiate(original[i]);_temporary.Add(copies[i]);if(copies[i].SpellDefinition==null)continue;
                var spell=Object.Instantiate(copies[i].SpellDefinition);spell.CastSfx=null;_temporary.Add(spell);Field(typeof(ItemDefinition),"_spellDefinition").SetValue(copies[i],spell);}
            Field(typeof(ItemCatalog),"_definitions").SetValue(_catalog,copies);
            if(TestContext.CurrentContext.Test.Name=="AuthoredWorldItemCanBeCollectedAndReturnedWithSameIdentity")
            {
                var expanded=new List<ItemDefinition>(copies);
                foreach(var copy in copies)if(copy.Kind==ItemKind.Backpack)
                {var data=new SerializedObject(copy);data.FindProperty("_backpackWidth").intValue=8;data.FindProperty("_backpackHeight").intValue=8;data.ApplyModifiedPropertiesWithoutUndo();}
                foreach(string key in new[]{"Food/Pumpkin","Herb/Herb_Flower_01","Container/Container_02"})
                    expanded.Add(AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/Items/"+key+".asset"));
                Field(typeof(ItemCatalog),"_definitions").SetValue(_catalog,expanded.ToArray());
            }
            _definition=Object.Instantiate(AssetDatabase.LoadAssetAtPath<RaidDefinition>(Root+"Raid.asset"));_temporary.Add(_definition);_definition.ExtractionSeconds=.25f;
            SceneManager.sceneLoaded+=Loaded;_hub=EditorSceneManager.LoadSceneInPlayMode(HubPath,new LoadSceneParameters(LoadSceneMode.Additive));
            yield return null;yield return null;yield return null;SceneManager.sceneLoaded-=Loaded;
            Assert.That(_session.ReadOnly,Is.Not.Null);Assert.That(_run.State.Phase,Is.EqualTo(RaidPhase.HubReady));
        }
        private T Find<T>(Scene scene)where T:Component
        {foreach(var root in scene.GetRootGameObjects()){var c=root.GetComponentInChildren<T>(true);if(c!=null)return c;}return null;}
        private void Loaded(Scene scene,LoadSceneMode mode)
        {
            if(scene.path!=HubPath)return;_hub=scene;_session=Find<SinglePlayerInventorySession>(scene);_run=Find<SinglePlayerRunCoordinator>(scene);
            _wizard=Find<WizardController>(scene);_health=_wizard.GetComponent<HealthComponent>();_ui=Find<SinglePlayerInventoryController>(scene);_view=Find<RaidPreparationView>(scene);_collection=Find<MaterialCollectionController>(scene);
            Field(typeof(SinglePlayerRunCoordinator),"_definition").SetValue(_run,_definition);
            var seeds=AssetDatabase.LoadAssetAtPath<SinglePlayerStartingInventory>(Root+"StartingInventory.asset");
            if(_reload){Assert.That(_session.ConfigureAndInitialize(_catalog,seeds,_directory),Is.True);return;}
            Assert.That(_catalog.TryCreateIndex(out var index,out _),Is.True);Assert.That(seeds.TryCreate(index,out var profile,out _),Is.True);
            foreach(var e in profile.Items)if(e.Location.Kind==ItemLocationKind.EquippedWand&&e.Location.SlotIndex==0)
            {foreach(var card in profile.Items)if(card.Location.Kind==ItemLocationKind.WandSlot&&card.Location.OwnerId==e.Item.InstanceId&&card.Location.SlotIndex==0){_card=card.Item.InstanceId;break;}break;}
            profile.Items.TryGet(_card,out var original);profile.Items.TryGetLocation(_card,out var location);
            Assert.That(ItemStackOps.TryChangeState(profile.Items,new RuntimeItemState(_card,original.ContentId,3,false),location,profile.Items.Version).Success,Is.True);
            string bag=null;var bottles=new List<string>();foreach(var e in profile.Items)
            {if(e.Location.Kind==ItemLocationKind.EquippedBackpack)bag=e.Item.InstanceId;if(e.Item.ContentId=="containertest.bottle")bottles.Add(e.Item.InstanceId);}
            for(int i=0;i<bottles.Count;i++)Assert.That(profile.Items.TryMove(bottles[i],new GridPlacement(i,0,0).InBackpack(bag)).Success,Is.True);
            var spare=RuntimeItemState.Create("wandtest.fire",8);_hubCard=spare.InstanceId;Assert.That(profile.Items.TryAdd(spare,ItemLocation.Warehouse),Is.True);
            _files=new RaidFaultFiles();var repository=new ProfileSaveRepository(_directory,index,_files);Assert.That(repository.TryLoad(out _).Status,Is.EqualTo(ProfilePersistenceStatus.Missing));
            Assert.That(_session.Initialize(profile,repository),Is.True);
        }
        private IEnumerator Begin(bool protect=true)
        {
            if(protect){_ui.Open();_ui.SelectItem(_card);yield return null;_view.Protect();Assert.That(_run.Protection.Contains(_card),Is.True);_ui.Close();}
            Assert.That(_run.Depart(),Is.True);float until=Time.realtimeSinceStartup+5;
            while(_run.State.Phase==RaidPhase.Departing&&Time.realtimeSinceStartup<until)yield return null;
            Assert.That(_run.State.Phase,Is.EqualTo(RaidPhase.Exploring));Assert.That(_run.HasWorld,Is.True);
        }
        private IEnumerator WaitHub()
        {float until=Time.realtimeSinceStartup+5;while(_run.State.Phase!=RaidPhase.HubReady&&Time.realtimeSinceStartup<until)yield return null;Assert.That(_run.State.Phase,Is.EqualTo(RaidPhase.HubReady));Assert.That(_run.HasWorld,Is.False);}
        private static void Hit(HealthComponent target,int attacker=777)=>target.ReceiveHit(new DamageRequest(attacker,1,10000,DamageType.True,target.transform.position,Vector3.forward));
        private void MovePlayer(Vector3 point)
        {bool enabled=_wizard.CharacterController.enabled;_wizard.CharacterController.enabled=false;_wizard.transform.position=point;_wizard.CharacterController.enabled=enabled;}
        private IEnumerator Collect(ulong quantity=10)
        {
            var world=SceneManager.GetSceneByPath(_definition.WorldScenePath);var supply=Find<ContainerSandboxMaterialAuthoring>(world);
            Assert.That(supply,Is.Not.Null);yield return null;MovePlayer(supply.MixedSamplePosition+new Vector3(0,.5f,-.5f));
            _collection.SubmitSample(supply.MixedSamplePosition,false,quantity);Assert.That(_collection.LastFailure,Is.EqualTo(MaterialTransferFailure.None));yield return null;
        }
        private sealed class DepartureProxy : IContainerThrowProxy
        { public void Launch() { } public void Cancel() { } }
        [UnityTest]public IEnumerator PendingThrowDepartureSaveFailurePreservesBottleThenCommitsWithoutRefund()
        {
            string bottle=null;foreach(var entry in _session.Profile.Items)if(entry.Item.Composition!=null){bottle=entry.Item.InstanceId;break;}
            Assert.That(bottle,Is.Not.Null);
            _session.Profile.Items.TryGet(bottle,out var initial);initial.Composition.AddReserved(new[]{new MaterialAmount(1,10)},1);
            void Prepare(ContainerThrownEvent e){if(e.Exchange.Request.ItemId==bottle)e.Exchange.Proxy=new DepartureProxy();}
            EventBus<ContainerThrownEvent>.Subscribe(Prepare);
            try
            {
                EventBus<ContainerUseRequestedEvent>.Publish(new ContainerUseRequestedEvent{InventoryId=_session.ReadOnly.InventoryId,WorldId="raid.entry",SourceId="departure.test",ItemId=bottle,
                    Kind=ContainerUseKind.Throw,RequestId=1,Radius=.2f,ExpectedVersion=_session.ReadOnly.Version});
                Assert.That(_session.PendingContainerFlights,Is.EqualTo(1));
                _session.ReadOnly.TryGetView(bottle,out var before);ulong quantity=before.ContainerTotal;long coins=_session.Profile.Currency;
                _files.FailWrite=true;Assert.That(_run.Depart(),Is.False);
                Assert.That(_session.PendingContainerFlights,Is.EqualTo(1));Assert.That(_session.ReadOnly.TryGetView(bottle,out var kept),Is.True);
                Assert.That(kept.ContainerTotal,Is.EqualTo(quantity));Assert.That(_session.Profile.Currency,Is.EqualTo(coins));
                _files.FailWrite=false;Assert.That(_run.Depart(),Is.True);
                Assert.That(_session.PendingContainerFlights,Is.Zero);Assert.That(_session.ReadOnly.TryGetView(bottle,out _),Is.False,"已经投出的瓶不能退款");
                float until=Time.realtimeSinceStartup+10;while(_run.State.Phase==RaidPhase.Departing&&Time.realtimeSinceStartup<until)yield return null;
                Assert.That(_run.State.Phase,Is.EqualTo(RaidPhase.Exploring));_view.Abandon();yield return WaitHub();
            }
            finally{EventBus<ContainerThrownEvent>.Unsubscribe(Prepare);}
        }
        [UnityTest]public IEnumerator ActualCollectionAndExitReturnOnlyCarriedRemainingAssets()
        {
            yield return Begin();Assert.That(_session.ReadOnly.TryGetView(_hubCard,out _),Is.False,"局内仓库资产不能访问");
            Assert.That(_wizard.gameObject.scene.path,Is.EqualTo(_definition.WorldScenePath));
            Assert.That(_session.ReadOnly.TryGetView(_collection.SelectedContainerId,out var initialBottle),Is.True);ulong initialAmount=initialBottle.ContainerTotal;
            yield return Collect();string bottle=_collection.SelectedContainerId;
            MovePlayer(_definition.ExitPosition);yield return null;yield return null;Assert.That(_run.Extraction.Running,Is.False,"出口接触不能自动启动");
            _isolatedInput.Press(_testKeyboard.fKey);yield return null;_isolatedInput.Release(_testKeyboard.fKey);yield return null;Assert.That(_run.Extraction.Running,Is.True);
            yield return WaitHub();Assert.That(_run.LastResult.Outcome,Is.EqualTo(RaidOutcome.Success));
            Assert.That(_session.ReadOnly.TryGetView(_hubCard,out var hub),Is.True);Assert.That(hub.RemainingCharges,Is.EqualTo(8));
            Assert.That(_session.ReadOnly.TryGetView(bottle,out var sample),Is.True);Assert.That(sample.ContainerTotal,Is.EqualTo(initialAmount+10));Assert.That(_health.IsAlive,Is.True);
            Assert.That(_wizard.gameObject.scene.path,Is.EqualTo(HubPath));
        }
        [UnityTest]public IEnumerator RealCastThenDeathKeepsActualProtectedCountAndUncarriedHubCard()
        {
            yield return Begin();var adapter=_wizard.GetComponent<OwnedWandCastAdapter>();Assert.That(adapter.TryCapture(out var program,out var context),Is.True);
            Assert.That(_wizard.GetComponent<SpellCaster>().CastOwnedProgram(program,context,new Vector3(0,5,0),new Vector3(0,5,30),1,_health.Id,_wizard.CharacterController),Is.EqualTo(1));
            Hit(_health);yield return WaitHub();Assert.That(_run.LastResult.Outcome,Is.EqualTo(RaidOutcome.Death));Assert.That(_session.ReadOnly.Count,Is.EqualTo(3),"仓库原卡、保护卡和装备背包");
            _session.ReadOnly.TryGetView(_card,out var card);Assert.That(card.RemainingCharges,Is.EqualTo(2));Assert.That(card.Location,Is.EqualTo(ItemLocation.Warehouse));
            Assert.That(Object.FindObjectsByType<ProjectileBase>(FindObjectsSortMode.None),Is.Empty,"已提交结算后释放本局攻击资源");
        }
        [UnityTest]public IEnumerator AuthoredWorldItemCanBeCollectedAndReturnedWithSameIdentity()
        {
            var points=new List<WorldItemSpawnPoint>();
            UnityEngine.Events.UnityAction<Scene,LoadSceneMode> author=(scene,mode)=>
            {
                if(scene.path!=_definition.WorldScenePath)return;
                foreach(string content in new[]{"wandtest.fire","item.food.pumpkin","item.herb.herb_flower_01","item.container.container_02"})
                {
                    _session.Profile.Items.Catalog.TryGet(content,out var definition);
                    var node=definition.PickupPrefab!=null?Object.Instantiate(definition.PickupPrefab):new GameObject("Test authored spell");
                    SceneManager.MoveGameObjectToScene(node,scene);node.transform.position=_definition.SpawnPosition+Vector3.up;
                    var point=node.GetComponent<WorldItemSpawnPoint>();if(point==null)point=node.AddComponent<WorldItemSpawnPoint>();
                    var data=new SerializedObject(point);data.FindProperty("_definition").objectReferenceValue=definition;
                    data.FindProperty("_spawnId").stringValue="test.raid."+content;data.FindProperty("_initialCharges").intValue=7;
                    data.ApplyModifiedPropertiesWithoutUndo();points.Add(point);
                }
            };
            SceneManager.sceneLoaded+=author;
            try{yield return Begin(false);}finally{SceneManager.sceneLoaded-=author;}
            _wizard.enabled=false;var nearby=_wizard.gameObject.AddComponent<NearbyPickupController>();nearby.Configure(_session,_wizard.transform);
            var input=_wizard.gameObject.AddComponent<NearbyPickupInput>();var inputData=new SerializedObject(input);inputData.FindProperty("_pickups").objectReferenceValue=nearby;inputData.ApplyModifiedPropertiesWithoutUndo();
            var ids=new List<string>();
            foreach(var point in points)
            {
                string id=point.RuntimeInstanceId;Assert.That(id,Is.Not.Null,"Coordinator未自动连接投放点");ids.Add(id);
                int count=_session.ReadOnly.Count;Assert.That(point.TryActivate(),Is.True);Assert.That(_session.ReadOnly.Count,Is.EqualTo(count));
                Assert.That(_session.Profile.HubWorldItems.Exists(r=>r.InstanceId==id),Is.False);
                nearby.RefreshCandidates();for(int i=0;i<nearby.Count && nearby.FocusedPickup.ItemId!=id;i++)nearby.SelectRelative(1);
                Assert.That(nearby.FocusedPickup.ItemId,Is.EqualTo(id));_isolatedInput.Press(_testKeyboard.fKey);yield return null;_isolatedInput.Release(_testKeyboard.fKey);yield return null;
                _session.ReadOnly.TryGetView(id,out var item);Assert.That(item.Location.Kind,Is.EqualTo(point.Definition.Kind==ItemKind.Spell?ItemLocationKind.SpellStorage:ItemLocationKind.Backpack));
            }
            yield return Collect();MovePlayer(_definition.ExitPosition);yield return null;yield return null;
            _isolatedInput.Press(_testKeyboard.fKey);yield return null;_isolatedInput.Release(_testKeyboard.fKey);yield return WaitHub();
            Assert.That(_run.LastResult.Outcome,Is.EqualTo(RaidOutcome.Success));
            foreach(string id in ids)Assert.That(_session.ReadOnly.TryGetView(id,out _),Is.True,"实际出口回程丢失"+id);
            _session.ReadOnly.TryGetView(ids[0],out var spell);Assert.That(spell.RemainingCharges,Is.EqualTo(7));
        }
        [UnityTest]public IEnumerator MissingMapRollsBackDepartureWithoutLoss()
        {
            int count=_session.ReadOnly.Count;_definition.WorldScenePath="Assets/m4-missing-map.unity";Assert.That(_run.Depart(),Is.True);yield return null;yield return WaitHub();
            Assert.That(_session.ReadOnly.Count,Is.EqualTo(count));Assert.That(_session.ReadOnly.ActorId,Is.EqualTo("player"));Assert.That(_run.LastResult,Is.Null);
        }
        [UnityTest]public IEnumerator SettlementWriteFailureKeepsWorldAndRetryPublishesOnce()
        {
            yield return Begin();int published=0;Action<RaidResultEvent> listener=e=>{if(e.InventoryId==_session.ReadOnly.InventoryId)published++;};EventBus<RaidResultEvent>.Subscribe(listener);
            try
            {_files.FailReplace=true;Hit(_health);yield return null;yield return null;Assert.That(_run.State.Phase,Is.EqualTo(RaidPhase.SaveFailed));Assert.That(_run.HasWorld,Is.True);Assert.That(published,Is.Zero);
                _files.FailReplace=false;_view.Retry();yield return WaitHub();Assert.That(published,Is.EqualTo(1));}
            finally{EventBus<RaidResultEvent>.Unsubscribe(listener);}
        }
        [UnityTest]public IEnumerator ActualEnemyDeathsGiveSeparateSpellsAndDuplicateDeathDoesNotAwardAgain()
        {
            yield return Begin(false);var world=SceneManager.GetSceneByPath(_definition.WorldScenePath);var enemies=new List<SpellLootDropAdapter>();foreach(var root in world.GetRootGameObjects())enemies.AddRange(root.GetComponentsInChildren<SpellLootDropAdapter>(true));Assert.That(enemies.Count,Is.EqualTo(2));
            var table=Object.Instantiate(enemies[0].Table);_temporary.Add(table);table.DropChance=1;table.Entries=new[]{new SpellLootTable.Entry{ContentId="wandtest.fire",Weight=1,Charges=5}};
            for(int i=0;i<enemies.Count;i++){enemies[i].Table=table;Hit(enemies[i].GetComponent<HealthComponent>(),_health.Id);}
            var views=new InventoryItemSnapshot[_session.ReadOnly.Count];int n=_session.ReadOnly.CopyItemsTo(views);var ids=new List<string>();for(int i=0;i<n;i++)if(views[i].Location.Kind==ItemLocationKind.WorldItem)ids.Add(views[i].InstanceId);
            Assert.That(ids.Count,Is.EqualTo(2));Assert.That(ids[0],Is.Not.EqualTo(ids[1]));EventBus<DeathEvent>.Publish(new DeathEvent{TargetId=enemies[0].GetComponent<HealthComponent>().Id});Assert.That(_run.TryDropSpell(enemies[0].GetComponent<HealthComponent>().Id,table.Entries[0],Vector3.zero),Is.False);
            // Runtime表示跟随World Scene，Session留在Hub；按权威库存中的ID定位，避免依赖旧父子层级。
            var pickups=new[]{_session.FindWorldProxy(ids[0]),_session.FindWorldProxy(ids[1])};
            Assert.That(pickups[0],Is.Not.Null);Assert.That(pickups[1],Is.Not.Null);string bag=null;for(int i=0;i<n;i++)if(views[i].Location.Kind==ItemLocationKind.EquippedBackpack)bag=views[i].InstanceId;
            Assert.That(pickups[0].TryCollect(bag,new GridPlacement(0,0,0),_session.ReadOnly.Version).Success,Is.False);Assert.That(pickups[0].gameObject.activeSelf,Is.True);
            _view.Abandon();yield return WaitHub();Assert.That(_session.ReadOnly.Count,Is.EqualTo(2),"原仓库卡+保留的装备背包");
            Assert.That(_session.EquippedBackpackId,Is.EqualTo(bag));
            foreach(string id in ids)Assert.That(_session.ReadOnly.TryGetView(id,out _),Is.False,"未拾取世界掉落仍然遗失");
        }
        [UnityTest]public IEnumerator CloseWritesFailureAndReliablePendingRecoveryDoesNotReloadWorld()
        {
            yield return Begin();Assert.That(_run.CloseSynchronously(),Is.True);var dto=JsonUtility.FromJson<SinglePlayerSaveDto>(File.ReadAllText(Path.Combine(_directory,"profile.json")));
            Assert.That(dto.HasPendingRaid,Is.False);Assert.That(dto.LastRaidReceipt.Outcome,Is.EqualTo(RaidOutcome.Closed));Assert.That(dto.Items.Length,Is.EqualTo(3));
        }
        [UnityTest]public IEnumerator RestartRecoversLastCheckpointProtectedCountsWithoutAdventureResume()
        {
            yield return Begin();_files.FailWrite=true;var world=SceneManager.GetSceneByPath(_definition.WorldScenePath);
            yield return SceneManager.UnloadSceneAsync(world);yield return SceneManager.UnloadSceneAsync(_hub);
            _reload=true;SceneManager.sceneLoaded+=Loaded;_hub=EditorSceneManager.LoadSceneInPlayMode(HubPath,new LoadSceneParameters(LoadSceneMode.Additive));
            yield return null;yield return null;yield return null;SceneManager.sceneLoaded-=Loaded;
            Assert.That(_session.ReadOnly.Count,Is.EqualTo(3));Assert.That(_run.State.Phase,Is.EqualTo(RaidPhase.HubReady));Assert.That(_run.HasWorld,Is.False);
            Assert.That(_session.ReadOnly.TryGetView(_card,out var card),Is.True);Assert.That(card.RemainingCharges,Is.EqualTo(3));Assert.That(_session.InitializationMessage,Does.Contain("未恢复旧箱庭"));
        }
        [UnityTest]public IEnumerator ThreeFailuresAndStarterKitCanDepartAgainWithFreshWorld()
        {
            for(int round=0;round<3;round++)
            {
                yield return Begin(round==0);var world=SceneManager.GetSceneByPath(_definition.WorldScenePath);var material=Find<ElementWorldRuntime>(world);
                Assert.That(material,Is.Not.Null);yield return Collect();_view.Abandon();yield return WaitHub();
                _view.Starter();yield return null;Assert.That(_session.ReadOnly.Count,Is.GreaterThan(1));
                Assert.That(Object.FindObjectsByType<ElementWorldRuntime>(FindObjectsSortMode.None).Length,Is.EqualTo(1),"每轮只保留据点Owner，不积累旧箱庭。");
                Assert.That(Object.FindObjectsByType<MaterialPayloadRuntimeAdapter>(FindObjectsSortMode.None),Is.Empty,"终局材料产出代理不留到下一局。");
                Assert.That(_session.WandsReadOnly.TryGetSlot(0,out var wand),Is.True);Assert.That(_session.WandsReadOnly.TryGetProgram(wand.InstanceId,out var program),Is.True);Assert.That(program.Program.Count,Is.GreaterThan(0));
                bool unlimited=false;var cards=new InventoryItemSnapshot[_session.ReadOnly.Count];int count=_session.ReadOnly.CopyItemsTo(cards);
                for(int i=0;i<count;i++)if(cards[i].Location.Kind==ItemLocationKind.WandSlot&&cards[i].HasUnlimitedCharges)unlimited=true;
                Assert.That(unlimited,Is.True,"免费基础法术能直接无限施法，不能被有限库存误认为已补齐。");
            }
            Assert.That(Object.FindObjectsByType<ProjectileBase>(FindObjectsSortMode.None),Is.Empty);
        }
        [UnityTest]public IEnumerator ReturningHubClearsRaidStatusesAndRestoresActivitySource()
        {
            yield return Begin();var status=_wizard.GetComponent<StatusController>();Assert.That(status,Is.Not.Null);
            status.ApplyStatus(StatusKind.Burning,50,777,2);Assert.That(status.GetIntensity(StatusKind.Burning),Is.GreaterThan(0));
            _view.Abandon();yield return WaitHub();Assert.That(status.GetIntensity(StatusKind.Burning),Is.Zero,"旧局状态不应在据点继续扣血。");
        }
        [UnityTest]public IEnumerator ActualExitDamageDoesNotCancelAndPlayerCanCancelAndReenter()
        {
            _definition.ExtractionSeconds=2;yield return Begin();yield return Collect();MovePlayer(_definition.ExitPosition);yield return null;yield return null;
            Assert.That(_run.Extraction.Running,Is.False);_isolatedInput.Press(_testKeyboard.fKey);yield return null;_isolatedInput.Release(_testKeyboard.fKey);
            Assert.That(_run.Extraction.Running,Is.True);yield return null;
            Assert.That(_run.Extraction.Running,Is.True);float remaining=_run.Extraction.Remaining;
            _health.ReceiveHit(new DamageRequest(777,2,1,DamageType.True,_health.transform.position,Vector3.forward));yield return null;
            Assert.That(_run.Extraction.Running,Is.True);Assert.That(_run.Extraction.Remaining,Is.LessThan(remaining));
            _view.CancelExtraction();yield return null;Assert.That(_run.Extraction.Cancelled,Is.True);Assert.That(_run.Extraction.Remaining,Is.EqualTo(2));
            _isolatedInput.Press(_testKeyboard.fKey);yield return null;_isolatedInput.Release(_testKeyboard.fKey);yield return null;
            Assert.That(_run.Extraction.Running,Is.True);_view.Abandon();yield return WaitHub();
        }
        [UnityTest]public IEnumerator FailedCheckpointFreezesDirectPickupUntilReliableRetry()
        {
            // Spawn=(0,1,-3)，旧Vector3.zero距离已超过3m拾取范围；本用例验证写锁而非远程拾取。
            yield return Begin(false);Assert.That(_run.TryDropSpell(888,new SpellLootTable.Entry{ContentId="wandtest.fire",Weight=1,Charges=5},_wizard.transform.position+Vector3.forward),Is.True);
            WorldItemPickup pickup=null;foreach(var entry in _session.Profile.Items)
                if(entry.Location.Kind==ItemLocationKind.WorldItem){pickup=_session.FindWorldProxy(entry.Item.InstanceId);if(pickup!=null)break;}
            Assert.That(pickup,Is.Not.Null);
            var items=new InventoryItemSnapshot[_session.ReadOnly.Count];int n=_session.ReadOnly.CopyItemsTo(items);string bag=null;
            for(int i=0;i<n;i++)if(items[i].Location.Kind==ItemLocationKind.EquippedBackpack)bag=items[i].InstanceId;
            _files.FailWrite=true;Assert.That(_run.Checkpoint().Success,Is.False);uint version=_session.ReadOnly.Version;
            Assert.That(pickup.TryCollect(bag,new GridPlacement(3,3,0),version).Success,Is.False);Assert.That(_session.ReadOnly.Version,Is.EqualTo(version));
            _files.FailWrite=false;_view.Retry();var collected=pickup.TryCollect(bag,new GridPlacement(3,3,0),_session.ReadOnly.Version);
            Assert.That(collected.Success,Is.True,"恢复后拾取失败="+collected.Reason+"；距离="+Vector3.Distance(pickup.transform.position,_wizard.transform.position));
            _view.Abandon();yield return WaitHub();
        }
        [UnityTest]public IEnumerator ActualPreparationGameViewShowsProtectionAndDepartureControls()
        {
            _ui.Open();_ui.SelectItem(_card);yield return null;_view.Protect();Canvas.ForceUpdateCanvases();yield return null;
            ScreenCapture.CaptureScreenshot("Temp/SinglePlayer/raid-preparation.png");yield return new WaitForSecondsRealtime(.3f);
            Assert.That(File.Exists("Temp/SinglePlayer/raid-preparation.png"),Is.True);
            _ui.Close();yield return Begin(false);yield return Collect();
            ScreenCapture.CaptureScreenshot("Temp/SinglePlayer/raid-world.png");yield return new WaitForSecondsRealtime(.3f);
            Assert.That(File.Exists("Temp/SinglePlayer/raid-world.png"),Is.True);_view.Abandon();yield return WaitHub();
        }
        [UnityTest]public IEnumerator ProfilerRecordsRaidTickWithoutHotAllocAndRealCheckpointCost()
        {
            yield return Begin();bool recording=UnityEditorInternal.ProfilerDriver.enabled;
            bool cpu=UnityEditorInternal.ProfilerDriver.IsAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU);
            try
            {
                UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,true);UnityEditorInternal.ProfilerDriver.enabled=true;
                for(int i=0;i<5;i++)yield return null;int first=UnityEditorInternal.ProfilerDriver.lastFrameIndex+1;
                for(int i=0;i<60;i++)yield return null;int last=UnityEditorInternal.ProfilerDriver.lastFrameIndex;
                var idle=Measure(first,last,"Norn.Raid.Update");
                Assert.That(idle.Frames,Is.GreaterThanOrEqualTo(30));Assert.That(idle.Samples,Is.GreaterThanOrEqualTo(30));Assert.That(idle.MarkerGcBytes,Is.Zero);
                first=UnityEditorInternal.ProfilerDriver.lastFrameIndex+1;
                Assert.That(_wizard.GetComponent<OwnedWandCastAdapter>().TryCapture(out var program,out var context),Is.True);
                Assert.That(_wizard.GetComponent<SpellCaster>().CastOwnedProgram(program,context,new Vector3(0,5,0),new Vector3(0,5,30),1,_health.Id,_wizard.CharacterController),Is.EqualTo(1));
                yield return null;yield return null;
                var checkpoint=Measure(first,UnityEditorInternal.ProfilerDriver.lastFrameIndex,"Norn.Raid.Checkpoint");
                Assert.That(checkpoint.Samples,Is.GreaterThan(0),"记录真正扣次后的文件提交，不以空Marker充当I/O证据。");
                File.WriteAllText("Temp/SinglePlayer/raid-profiler.json",JsonUtility.ToJson(new RaidProfileReport{Utc=DateTime.UtcNow.ToString("O"),Scope="Editor/TestRunner; actual graybox, authored solver paused; 60 idle frames and real owned-cast checkpoint; no GPU/whole-world performance claim",Idle=idle,Checkpoint=checkpoint},true));
                UnityEditorInternal.ProfilerDriver.SaveProfile("Temp/SinglePlayer/raid-profiler.data");
            }
            finally{UnityEditorInternal.ProfilerDriver.enabled=recording;UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,cpu);}
            _view.Abandon();yield return WaitHub();
        }
        [Serializable]private sealed class RaidProfileReport{public string Utc,Scope;public RaidMarkerReport Idle,Checkpoint;}
        [Serializable]private sealed class RaidMarkerReport{public string Marker;public int First,Last,Frames,Samples;public long MarkerGcBytes,MainThreadGcBytes;public double PeakMarkerMs,AverageMainThreadMs;}
        private static RaidMarkerReport Measure(int first,int last,string marker)
        {
            var result=new RaidMarkerReport{Marker=marker,First=first,Last=last};
            for(int frame=Math.Max(first,UnityEditorInternal.ProfilerDriver.firstFrameIndex);frame<=last;frame++)
            {
                using var data=UnityEditorInternal.ProfilerDriver.GetRawFrameDataView(frame,0);if(!data.valid||data.sampleCount==0)continue;
                result.Frames++;result.AverageMainThreadMs+=data.GetSampleTimeMs(0);
                for(int sample=0;sample<data.sampleCount;sample++)
                {
                    string name=data.GetSampleName(sample);if(name=="GC.Alloc"&&data.GetSampleMetadataCount(sample)>0)result.MainThreadGcBytes+=data.GetSampleMetadataAsLong(sample,0);
                    if(name!=marker)continue;result.Samples++;double start=data.GetSampleStartTimeMs(sample),duration=data.GetSampleTimeMs(sample);result.PeakMarkerMs=Math.Max(result.PeakMarkerMs,duration);
                    // 只统计指定Marker时间区间的子GC事件；JSON/导出分配发生在测量窗口之后。
                    for(int child=sample+1;child<data.sampleCount;child++){double at=data.GetSampleStartTimeMs(child);if(at>start+duration)break;if(at>=start&&data.GetSampleName(child)=="GC.Alloc"&&data.GetSampleMetadataCount(child)>0)result.MarkerGcBytes+=data.GetSampleMetadataAsLong(child,0);}
                }
            }
            if(result.Frames>0)result.AverageMainThreadMs/=result.Frames;return result;
        }
        [UnityTearDown]public IEnumerator Cleanup()
        {
            SceneManager.sceneLoaded-=Loaded;if(_files!=null){_files.FailReplace=false;_files.FailWrite=false;}
            if(_ui!=null)_ui.Close();if(_run!=null)_run.CloseSynchronously();
            var world=SceneManager.GetSceneByPath("Assets/_Project/Scenes/Tests/RaidWorldSandbox.unity");if(world.IsValid()&&world.isLoaded)yield return SceneManager.UnloadSceneAsync(world);
            if(_hub.IsValid()&&_hub.isLoaded)yield return SceneManager.UnloadSceneAsync(_hub);
            for(int i=_temporary.Count-1;i>=0;i--)if(_temporary[i]!=null)Object.Destroy(_temporary[i]);_temporary.Clear();yield return null;
            _isolatedInput?.TearDown();_isolatedInput=null;
            Time.timeScale=_time;Cursor.visible=_cursor;Cursor.lockState=_lock;if(_window!=null)_window.Focus();
            string root=Path.GetFullPath("Temp/SinglePlayer/RaidTests")+Path.DirectorySeparatorChar;if(_directory.StartsWith(root,StringComparison.OrdinalIgnoreCase)&&Directory.Exists(_directory))Directory.Delete(_directory,true);
        }
    }
    public sealed class RaidFaultFiles:IProfileFileSystem
    {
        private readonly ProfileFileSystem _real=new ProfileFileSystem();public bool FailWrite,FailReplace;
        public bool Exists(string p)=>_real.Exists(p);public string Read(string p)=>_real.Read(p);
        public void WriteFlushed(string p,string t){if(FailWrite)throw new IOException("M4 injected write failure");_real.WriteFlushed(p,t);}
        public void Move(string a,string b){if(FailReplace)throw new IOException("M4 injected commit failure");_real.Move(a,b);}
        public void Replace(string a,string b,string backup){if(FailReplace)throw new IOException("M4 injected commit failure");_real.Replace(a,b,backup);}
    }
}
#endif
