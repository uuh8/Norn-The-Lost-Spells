#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using Game.Character;
using Game.Combat;
using Game.Core;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Game.Run.Tests
{
    /// <summary>直接接触两旧图练习门及回程门；sceneLoaded在Start前注入内存档，防止测试触及正式资产。</summary>
    public sealed class HubPracticePortalTests
    {
        private static string Hub=>SessionState.GetString("Norn.HubAlignment.ReviewPath","Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity");
        private SinglePlayerInventorySession _session;
        private WizardController _player;
        private ProfileSaveRepository _repository;
        private MemoryFiles _files;
        private Scene _scene;
        private string _before;
        private bool _background;
        private InputTestFixture _inputFixture;
        private Keyboard _keyboard;
        [UnitySetUp]public IEnumerator InitializePractice()
        {
            _background=Application.runInBackground;Application.runInBackground=true;Time.timeScale=1;
            // UnitySetUp先于普通SetUp；嵌入Fixture以保证Scene里的Action属于隔离Runtime。
            _inputFixture=new InputTestFixture();_inputFixture.Setup();
            _keyboard=InputSystem.AddDevice<Keyboard>();InputSystem.AddDevice<Mouse>();
            EditorWindow.GetWindow(Type.GetType("UnityEditor.GameView,UnityEditor")).Focus();
            // 官方InputTestFixture隔离整套测试输入Runtime，真实设备不参与测试也不会被禁用。
            _files=new MemoryFiles();_repository=null;SceneManager.sceneLoaded+=Loaded;
            _scene=EditorSceneManager.LoadSceneInPlayMode(Hub,new LoadSceneParameters(LoadSceneMode.Additive));
            yield return null;yield return null;yield return null;Assert.That(_session.ReadOnly,Is.Not.Null);
        }
        private void Loaded(Scene scene,LoadSceneMode mode)
        {
            if(scene.path!=Hub)return;_scene=scene;_session=Find<SinglePlayerInventorySession>(scene);_player=Find<WizardController>(scene);
            var serialized=new SerializedObject(_session);var catalog=(ItemCatalog)serialized.FindProperty("_catalog").objectReferenceValue;
            Assert.That(catalog.TryCreateIndex(out var index,out _),Is.True);SinglePlayerProfile profile;
            if(_repository==null)
            {
                var seeds=(SinglePlayerStartingInventory)serialized.FindProperty("_startingInventory").objectReferenceValue;
                Assert.That(seeds.TryCreate(index,out profile,out _),Is.True);
                _repository=new ProfileSaveRepository("Temp/SinglePlayer/PracticeMemory/"+Guid.NewGuid().ToString("N"),index,_files);
            }
            else Assert.That(_repository.TryLoad(out profile).Success,Is.True);
            Assert.That(_session.Initialize(profile,_repository),Is.True);
        }
        private static T Find<T>(Scene scene)where T:Component
        {foreach(var root in scene.GetRootGameObjects()){var c=root.GetComponentInChildren<T>(true);if(c!=null)return c;}return null;}
        private static void Move(WizardController player,Vector3 position)
        {player.CharacterController.enabled=false;player.transform.position=position;player.CharacterController.enabled=true;Physics.SyncTransforms();}
        private HubPracticePortal Portal(string suffix)
        {foreach(var root in _scene.GetRootGameObjects())foreach(var portal in root.GetComponentsInChildren<HubPracticePortal>(true))if(portal.name=="远征点 · "+suffix)return portal;throw new InvalidOperationException("缺少练习门");}
        [UnityTest]public IEnumerator ContactP7AndReturnPreservesHubAssets(){yield return Practice("P7");}
        [UnityTest]public IEnumerator ContactP8AndReturnPreservesHubAssets(){yield return Practice("P8");}
        [UnityTest]public IEnumerator P7DeathKeepsBackpackWarehouseAndCurrencyButLosesOtherCarriedGear(){yield return DeathReturns("P7",false);}
        [UnityTest]public IEnumerator P8DeathRetriesFailedCommitThenReturnsWithoutLegacyPanel(){yield return DeathReturns("P8",true);}
        [UnityTest]public IEnumerator P8BossBattleDeathKeepsBackpackAndReturnsWithOtherCarriedLost(){yield return DeathReturns("P8",false,true);}
        [UnityTest]public IEnumerator P8DeathAfterVictoryKeepsBackpackAndReturnsWithOtherCarriedLost(){yield return DeathReturns("P8",false,true,true);}
        [UnityTest]public IEnumerator P8VictoryCreatesOneReachableFPortalWithoutResultPanel()
        {
            string target=Portal("P8").ScenePath;Assert.That(Portal("P8").TryEnter(_player.CharacterController),Is.True);
            float end=Time.realtimeSinceStartup+30;Scene practice=default;
            while(Time.realtimeSinceStartup<end){practice=SceneManager.GetSceneByPath(target);if(practice.IsValid() && practice.isLoaded && !HubPracticeSession.Current.Loading)break;yield return null;}
            yield return null;Find<GameplayGuideController>(practice)?.Close();yield return null;
            Assert.That(Find<HubPracticePortal>(practice),Is.Null,"P8回程门由胜利生成");
            yield return CompleteBoss(practice);yield return null;yield return new WaitForSecondsRealtime(.2f);
            var gate=Find<HubPracticePortal>(practice);var player=Find<WizardController>(practice);
            Assert.That(gate,Is.Not.Null);Assert.That(Vector3.Distance(gate.transform.position,player.transform.position),Is.LessThan(8));
            EventBus<RunStateChangedEvent>.Publish(new RunStateChangedEvent{PreviousState=RunState.Running,CurrentState=RunState.Completed});
            int count=0;foreach(var root in practice.GetRootGameObjects())count+=root.GetComponentsInChildren<HubPracticePortal>().Length;
            Assert.That(count,Is.EqualTo(1));Assert.That(HubPracticeSession.Current.Loading,Is.False,"胜利只开门，等待F");
            var menu=new SerializedObject(Find<DemoPauseMenuController>(practice));
            Assert.That(((GameObject)menu.FindProperty("_victoryPanel").objectReferenceValue).activeSelf,Is.False);
            yield return new WaitForEndOfFrame();ScreenCapture.CaptureScreenshot("TestResults/SinglePlayer/M6-T12-WandTerminalMenu/p8-victory-return-portal.png");
            Move(player,gate.transform.position);yield return null;_inputFixture.Press(_keyboard.fKey);yield return null;_inputFixture.Release(_keyboard.fKey);
            end=Time.realtimeSinceStartup+30;while(HubPracticeSession.Current!=null && Time.realtimeSinceStartup<end)yield return null;
            Assert.That(HubPracticeSession.Current,Is.Null);Assert.That(_scene.path,Is.EqualTo(Hub));
        }
        private static IEnumerator CompleteBoss(Scene scene)
        {
            // PlayerControllerBase.Current在OnDisable注销；真实Encounter Start需要当前玩家，不能为固定测试位置先禁用控制器。
            var player=Find<WizardController>(scene);player.enabled=true;var entry=Find<BossEntryTrigger>(scene);
            Assert.That(entry.TryArm(player.CharacterController),Is.True);Assert.That(entry.TryActivate(player.CharacterController),Is.True);
            var encounter=Find<BossEncounterController>(scene);
            var boss=(HealthComponent)new SerializedObject(encounter).FindProperty("_bossHealth").objectReferenceValue;
            // Encounter事件先发布，Boss FSM在自身Update退出Inactive/入场无敌；按真实时序等待可受伤。
            float end=Time.realtimeSinceStartup+6;
            while(boss.IsInvulnerable && Time.realtimeSinceStartup<end)yield return null;
            Assert.That(boss.IsInvulnerable,Is.False);
            boss.ReceiveHit(new DamageRequest(12345,1,999999,DamageType.True,boss.transform.position,Vector3.forward));
            Assert.That(encounter.State,Is.EqualTo(BossEncounterState.Completed));
        }
        private IEnumerator DeathReturns(string map,bool failSave,bool fighting=false,bool afterVictory=false)
        {
            string retainedBag=_session.EquippedBackpackId;
            long money=_session.Currency;var carried=new List<string>();var warehouse=new List<string>();
            foreach(var entry in _session.Profile.Items)
                if(RaidProfileOps.IsCarried(_session.Profile,entry.Item.InstanceId))carried.Add(entry.Item.InstanceId);else warehouse.Add(entry.Item.InstanceId);
            Assert.That(carried.Count,Is.GreaterThan(0));
            string target=Portal(map).ScenePath;Assert.That(Portal(map).TryEnter(_player.CharacterController),Is.True);
            float end=Time.realtimeSinceStartup+30;Scene practice=default;
            while(Time.realtimeSinceStartup<end){practice=SceneManager.GetSceneByPath(target);if(practice.IsValid() && practice.isLoaded && !HubPracticeSession.Current.Loading)break;yield return null;}
            yield return null;Find<GameplayGuideController>(practice)?.Close();yield return null;
            var inventory=Find<SinglePlayerInventorySession>(practice);var health=Find<WizardController>(practice).GetComponent<HealthComponent>();
            if(afterVictory)yield return CompleteBoss(practice);
            else if(fighting)
            {
                var entry=Find<BossEntryTrigger>(practice);Assert.That(entry.TryArm(health.GetComponent<CharacterController>()),Is.True);
                Assert.That(entry.TryActivate(health.GetComponent<CharacterController>()),Is.True);
                Assert.That(Find<BossEncounterController>(practice).State,Is.EqualTo(BossEncounterState.Running));
            }
            money=inventory.Currency;
            string before=JsonUtility.ToJson(ProfileSaveMapper.ToDto(inventory.Profile,0));_files.Fail=failSave;
            health.ReceiveHit(new DamageRequest(12345,2,999999,DamageType.True,health.transform.position,Vector3.forward));
            if(failSave)
            {
                yield return new WaitForSecondsRealtime(2);
                Assert.That(practice.isLoaded,Is.True);Assert.That(HubPracticeSession.Current.Loading,Is.False);
                Assert.That(JsonUtility.ToJson(ProfileSaveMapper.ToDto(inventory.Profile,0)),Is.EqualTo(before));
                var menu=Find<DemoPauseMenuController>(practice);var serialized=new SerializedObject(menu);
                Assert.That(serialized.FindProperty("_defeatPanel").objectReferenceValue is GameObject panel && panel.activeSelf,Is.False);
                _files.Fail=false;
            }
            end=Time.realtimeSinceStartup+30;while(HubPracticeSession.Current!=null && Time.realtimeSinceStartup<end)yield return null;
            Assert.That(HubPracticeSession.Current,Is.Null,"死亡应自动回据点");
            Assert.That(_scene.path,Is.EqualTo(Hub));Assert.That(_session.Currency,Is.EqualTo(money));
            foreach(string id in carried)Assert.That(_session.Profile.Items.TryGet(id,out _),Is.EqualTo(id==retainedBag),"仅背包本体保留 "+id);
            foreach(string id in warehouse)Assert.That(_session.Profile.Items.TryGet(id,out _),Is.True,"仓库保留 "+id);
            foreach(var entry in _session.Profile.Items)
                if(entry.Item.InstanceId!=retainedBag)Assert.That(RaidProfileOps.IsCarried(_session.Profile,entry.Item.InstanceId),Is.False);
            Assert.That(_session.EquippedBackpackId,Is.EqualTo(retainedBag));
            yield return new WaitForSecondsRealtime(.6f);yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot("TestResults/SinglePlayer/M6-T13-BackpackPortalGrid/"+map+"-death-return-hub.png");
        }
        [UnityTest]public IEnumerator MainMenuPlayLoadsHubInsteadOfP7()
        {
            const string formalHub="Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity";
            var menuScene=EditorSceneManager.LoadSceneInPlayMode("Assets/_Project/Scenes/P7_MainMenu.unity",new LoadSceneParameters(LoadSceneMode.Single));
            yield return null;yield return null;yield return null;
            Find<DemoMainMenuController>(menuScene).StartGame();
            float end=Time.realtimeSinceStartup+30;
            while(Time.realtimeSinceStartup<end && SceneManager.GetActiveScene().path!=formalHub)yield return null;
            Assert.That(SceneManager.GetActiveScene().path,Is.EqualTo(formalHub));
            Assert.That(SceneManager.GetSceneByPath("Assets/_Project/Scenes/P7_DemoRun.unity").isLoaded,Is.False);
            // Active Scene早于目标Start/一次性场景投放；等待初始化结束后再让Fixture卸载地图。
            yield return null;yield return null;yield return null;yield return new WaitForSecondsRealtime(.25f);
            yield return new WaitForEndOfFrame();ScreenCapture.CaptureScreenshot("TestResults/SinglePlayer/M6-T12-WandTerminalMenu/main-menu-play-hub.png");
        }
        [UnityTest]public IEnumerator ContactWaitsForFBeforeLoadingPractice()
        {
            var portal=Portal("P7");_player.enabled=false;Move(_player,portal.transform.position);
            yield return new WaitForSecondsRealtime(.3f);
            Assert.That(HubPracticeSession.Current,Is.Null,"走入范围不能自动传送");
            InputSystem.QueueStateEvent(_keyboard,new UnityEngine.InputSystem.LowLevel.KeyboardState(Key.F));InputSystem.Update();yield return null;
            InputSystem.QueueStateEvent(_keyboard,new UnityEngine.InputSystem.LowLevel.KeyboardState());InputSystem.Update();
            Assert.That(HubPracticeSession.Current,Is.Not.Null,"范围内F应申请进入");
        }
        [UnityTest]public IEnumerator P7FCollectsFourKindsAndReturnsOriginalIdentities(){yield return Practice("P7",true);}
        [UnityTest]public IEnumerator P8FCollectsFourKindsAndReturnsOriginalIdentities(){yield return Practice("P8",true);}
        [UnityTest]public IEnumerator FullPracticeBackpackOpensArrangementAndKeepsGroundSource(){yield return Practice("P7",true,true);}
        [UnityTest]public IEnumerator P8BossCheckpointAndRetrySaveFailureKeepActualInventory()
        {
            string target=Portal("P8").ScenePath;Assert.That(Portal("P8").TryEnter(_player.CharacterController),Is.True);
            float end=Time.realtimeSinceStartup+30;Scene practice=default;
            while(Time.realtimeSinceStartup<end){practice=SceneManager.GetSceneByPath(target);if(practice.IsValid() && practice.isLoaded && !HubPracticeSession.Current.Loading)break;yield return null;}
            yield return null;Find<GameplayGuideController>(practice)?.Close();yield return null;
            var player=Find<WizardController>(practice);player.enabled=false;
            var inventory=Find<SinglePlayerInventorySession>(practice);var entry=Find<BossEntryTrigger>(practice);var encounter=Find<BossEncounterController>(practice);
            Assert.That(entry.TryArm(player.CharacterController),Is.True);
            string before=JsonUtility.ToJson(ProfileSaveMapper.ToDto(inventory.Profile,0));
            _files.Fail=true;Assert.That(entry.TryActivate(player.CharacterController),Is.False);
            Assert.That(encounter.State,Is.EqualTo(BossEncounterState.Inactive));Assert.That(HubPracticeSession.Current.HasBossCheckpoint,Is.False);
            Assert.That(JsonUtility.ToJson(ProfileSaveMapper.ToDto(inventory.Profile,0)),Is.EqualTo(before));
            _files.Fail=false;Assert.That(entry.TryActivate(player.CharacterController),Is.True);
            inventory.Profile.Currency+=19;before=JsonUtility.ToJson(ProfileSaveMapper.ToDto(inventory.Profile,0));
            _files.Fail=true;Assert.That(HubPracticeSession.Current.RetryBoss(),Is.False);
            Assert.That(HubPracticeSession.Current.Loading,Is.False);Assert.That(practice.isLoaded,Is.True);
            Assert.That(JsonUtility.ToJson(ProfileSaveMapper.ToDto(inventory.Profile,0)),Is.EqualTo(before));_files.Fail=false;
        }
        [UnityTest]public IEnumerator FailedReturnSaveKeepsOriginalGroundStateAndCanRetry()
        {
            string target=Portal("P7").ScenePath;Assert.That(Portal("P7").TryEnter(_player.CharacterController),Is.True);
            float end=Time.realtimeSinceStartup+30;Scene practice=default;
            while(Time.realtimeSinceStartup<end){practice=SceneManager.GetSceneByPath(target);if(practice.IsValid() && practice.isLoaded)break;yield return null;}
            yield return null;yield return null;Find<GameplayGuideController>(practice)?.Close();yield return null;
            var player=Find<WizardController>(practice);player.enabled=false;var inventory=Find<SinglePlayerInventorySession>(practice);
            var point=Spawn(practice,inventory,player.transform,"Food/Apple");string id=point.RuntimeInstanceId;
            string before=JsonUtility.ToJson(ProfileSaveMapper.ToDto(inventory.Profile,0));
            _files.Fail=true;Assert.That(HubPracticeSession.Current.ReturnToHub(),Is.False);
            Assert.That(JsonUtility.ToJson(ProfileSaveMapper.ToDto(inventory.Profile,0)),Is.EqualTo(before),"回程写入失败不能先删除地面资产");
            Assert.That(inventory.FindWorldProxy(id).gameObject.activeSelf,Is.True);_files.Fail=false;
            Assert.That(HubPracticeSession.Current.ReturnToHub(),Is.True);
            end=Time.realtimeSinceStartup+30;while(HubPracticeSession.Current!=null && Time.realtimeSinceStartup<end)yield return null;
            Assert.That(_session.Profile.Items.TryGet(id,out _),Is.False);
        }
        [UnityTest] public IEnumerator PendingThrowSaveFailureKeepsBottleAndRetryCanEnterPractice()
        {
            _player.enabled=false;
            var hub=Find<HubInteractionController>(_scene);Move(_player,hub.Definition.ExperimentCenter);yield return null;
            string bottle=null;
            foreach(var entry in _session.Profile.Items)
                if(entry.Item.Composition!=null && entry.Item.Composition.Total>0){bottle=entry.Item.InstanceId;break;}
            Assert.That(bottle,Is.Not.Null);
            Assert.That(_session.Profile.Items.TryMove(bottle,new GridPlacement(0,0,0).InBackpack(_session.EquippedBackpackId)).Success,Is.True);
            EventBus<ToolbeltBindingRequestedEvent>.Publish(new ToolbeltBindingRequestedEvent{InventoryId=_session.ReadOnly.InventoryId,ToolSlot=0,ItemId=bottle,ExpectedVersion=_session.ToolbeltReadOnly.ConfigurationVersion});
            EventBus<HeldItemSelectionRequestedEvent>.Publish(new HeldItemSelectionRequestedEvent{InventoryId=_session.ReadOnly.InventoryId,SlotIndex=3});yield return null;yield return null;
            var keyboard=_keyboard;
            InputSystem.QueueStateEvent(keyboard,new UnityEngine.InputSystem.LowLevel.KeyboardState(Key.G));InputSystem.Update();yield return null;yield return null;
            InputSystem.QueueStateEvent(keyboard,new UnityEngine.InputSystem.LowLevel.KeyboardState());InputSystem.Update();yield return null;
            Assert.That(_session.PendingContainerFlights,Is.EqualTo(1));Assert.That(_session.Save().Success,Is.False,"普通保存仍保护孤儿在途量");
            _session.ReadOnly.TryGetView(bottle,out var before);long currency=_session.Profile.Currency;
            _files.Fail=true;Assert.That(Portal("P7").TryEnter(_player.CharacterController),Is.False);
            Assert.That(_session.PendingContainerFlights,Is.EqualTo(1));Assert.That(_session.ReadOnly.TryGetView(bottle,out var after),Is.True);
            Assert.That(after.Location.Kind,Is.EqualTo(ItemLocationKind.ThrownContainer));Assert.That(after.ContainerTotal,Is.EqualTo(before.ContainerTotal));
            Assert.That(_session.Profile.Currency,Is.EqualTo(currency));Assert.That(HubPracticeSession.Current,Is.Null);
            _files.Fail=false;Find<HubNavigationView>(_scene)?.Cancel();
            Assert.That(Portal("P7").TryEnter(_player.CharacterController),Is.True,"待投放材料不能永久堵门");
            Assert.That(_session.PendingContainerFlights,Is.Zero);Assert.That(_session.ReadOnly.TryGetView(bottle,out _),Is.False,"投出的瓶不能重新发进背包");
            float end=Time.realtimeSinceStartup+30;while(HubPracticeSession.Current!=null && HubPracticeSession.Current.Loading && Time.realtimeSinceStartup<end)yield return null;
        }
        [UnityTest]public IEnumerator P7DepartureShrinksAndClosesBeforeOpaqueLoadThenReveals()
        {
            var portal=Portal("P7");string path=portal.ScenePath;
            var traveler=_player.GetComponent<PlayerPortalTraveler>();Assert.That(traveler,Is.Not.Null);
            var visual=(Transform)new SerializedObject(traveler).FindProperty("_visualRoot").objectReferenceValue;
            var original=visual.localScale;bool closed=false,black=false,loaded=false,shrunk=false,opaque=false;
            int closeFrame=-1,blackFrame=-1,loadFrame=-1;
            Action<PortalStateChangedEvent> closing=e=>{if(e.PortalId==portal.PortalId&&e.State==PortalVfxState.Closing){closed=true;closeFrame=Time.frameCount;shrunk=visual.localScale.magnitude<original.magnitude*.2f;}};
            Action<PortalFadeRequestedEvent> fading=e=>{if(e.Black){black=true;blackFrame=Time.frameCount;}};
            UnityEngine.Events.UnityAction<Scene,LoadSceneMode> loading=(scene,mode)=>{if(scene.path==path){loaded=true;loadFrame=Time.frameCount;var fade=UnityEngine.Object.FindFirstObjectByType<PortalTransitionFade>();opaque=fade!=null&&fade.GetComponent<CanvasGroup>().alpha>=.999f;}};
            EventBus<PortalStateChangedEvent>.Subscribe(closing);EventBus<PortalFadeRequestedEvent>.Subscribe(fading);SceneManager.sceneLoaded+=loading;
            try
            {
                Assert.That(portal.TryEnter(_player.CharacterController),Is.True);
                float end=Time.realtimeSinceStartup+30;while(!loaded&&Time.realtimeSinceStartup<end)yield return null;
                Assert.That(loaded&&closed&&black&&shrunk&&opaque,Is.True);
                Assert.That(closeFrame,Is.LessThan(blackFrame));Assert.That(blackFrame+1,Is.LessThan(loadFrame));
                var scene=SceneManager.GetSceneByPath(path);AssertSingleMap(scene);Find<GameplayGuideController>(scene)?.Close();
                yield return new WaitForSecondsRealtime(.8f);
                Assert.That(UnityEngine.Object.FindFirstObjectByType<PortalTransitionFade>().GetComponent<CanvasGroup>().alpha,Is.Zero);
                string evidence="TestResults/SinglePlayer/M6-T9-UnifiedItems";System.IO.Directory.CreateDirectory(evidence);
                System.IO.File.WriteAllText(evidence+"/portal-order.txt","ShrinkBeforeClose="+shrunk+"\nCloseFrame="+closeFrame+"\nBlackFrame="+blackFrame+"\nLoadFrame="+loadFrame+"\nOpaqueAtLoad="+opaque);
            }
            finally{EventBus<PortalStateChangedEvent>.Unsubscribe(closing);EventBus<PortalFadeRequestedEvent>.Unsubscribe(fading);SceneManager.sceneLoaded-=loading;}
        }
        [UnityTest]public IEnumerator ActualStagePortalReturnsP7LootToHubOutsideOriginalEntrance()
        {yield return FinalStagePortal(false);}
        [UnityTest]public IEnumerator FullInventoryStillReturnsThroughP7FinalPortalWithFWithoutOpeningBackpack()
        {yield return FinalStagePortal(true);}
        private IEnumerator FinalStagePortal(bool full)
        {
            AssertSingleMap(_scene);
            string p7=Portal("P7").ScenePath;Assert.That(Portal("P7").TryEnter(_player.CharacterController),Is.True);
            float end=Time.realtimeSinceStartup+30;Scene scene=default;
            while(Time.realtimeSinceStartup<end){scene=SceneManager.GetSceneByPath(p7);if(scene.IsValid() && scene.isLoaded)break;yield return null;}
            yield return null;yield return null;Find<GameplayGuideController>(scene)?.Close();yield return null;
            AssertSingleMap(scene);
            var player=Find<WizardController>(scene);player.enabled=false;var inventory=Find<SinglePlayerInventorySession>(scene);
            string id=Spawn(scene,inventory,player.transform,"Food/Pumpkin").RuntimeInstanceId;
            Find<NearbyPickupController>(scene).RefreshCandidates();_inputFixture.Press(_keyboard.fKey);yield return null;_inputFixture.Release(_keyboard.fKey);yield return null;
            if(full)
            {
                // 将法术位及真实背包填满，复现出口“强制领奖→RequiresArrangement→打开背包”的阻塞。
                string content=null;
                foreach(var entry in inventory.Profile.Items)
                    if(inventory.Profile.Items.Catalog.TryGetRules(entry.Item.ContentId,out var rules) && rules.Kind==ItemKind.Spell)
                    {content=entry.Item.ContentId;break;}
                Assert.That(content,Is.Not.Null);
                for(int slot=0;slot<inventory.SpellStorageCapacity;slot++)
                    inventory.Profile.Items.TryAdd(RuntimeItemState.Create(content,8,true),new ItemLocation(ItemLocationKind.SpellStorage,inventory.Profile.ActorId,0,0,0,slot));
                inventory.Profile.Items.TryGet(inventory.EquippedBackpackId,out var bag);
                inventory.Profile.Items.Catalog.TryGetRules(bag.ContentId,out var bagRules);
                for(int y=0;y<bagRules.BackpackHeight;y++)for(int x=0;x<bagRules.BackpackWidth;x++)
                    inventory.Profile.Items.TryAdd(RuntimeItemState.Create(content,8,true),new GridPlacement(x,y,0).InBackpack(bag.InstanceId));
                var library=(Game.Skills.SpellLibrary)new SerializedObject(Find<StageExitPortal>(scene)).FindProperty("_fullLibrary").objectReferenceValue;
                Assert.That(PracticeSpellRewardOps.TryGrant(inventory.Profile,library.Available[0],out _),Is.False,"实际满容量，不能把空背包测试冒充复现");
            }
            var gate=Find<StageExitPortal>(scene);Assert.That(gate,Is.Not.Null);gate.CloseDuration=0;gate.TravelerTransitDuration=0;
            int index=new SerializedObject(gate).FindProperty("_encounterIndex").intValue;
            EventBus<EncounterCompletedEvent>.Publish(new EncounterCompletedEvent{EncounterIndex=index});
            Move(player,gate.transform.position);yield return null;
            Assert.That(gate.IsTransitionRequested,Is.False,"通关门仍需显式F");
            _inputFixture.Press(_keyboard.fKey);yield return null;_inputFixture.Release(_keyboard.fKey);
            Assert.That(gate.IsTransitionRequested,Is.True,"使用原关卡实际F出口");
            Assert.That(Find<SinglePlayerInventoryController>(scene).IsOpen,Is.False,"F回程不被领奖容量打断");
            end=Time.realtimeSinceStartup+30;while(HubPracticeSession.Current!=null && Time.realtimeSinceStartup<end)yield return null;
            AssertSingleMap(SceneManager.GetSceneByPath(Hub));
            Assert.That(SceneManager.GetSceneByPath("Assets/_Project/Scenes/P8_BossField.unity").isLoaded,Is.False);
            Assert.That(_session.ReadOnly.TryGetView(id,out var item),Is.True);Assert.That(item.Location.Kind,Is.EqualTo(ItemLocationKind.Backpack));
            Assert.That(Vector3.Distance(_player.transform.position, Portal("P7").transform.position),Is.GreaterThan(2));
        }
        private static void AssertSingleMap(Scene expected)
        {
            // 跨图保留的是资产数据。地图、角色和 EventSystem 必须被 Single 加载替换，不能三图共存。
            Assert.That(expected.IsValid() && expected.isLoaded,Is.True);
            int maps=0;
            for(int i=0;i<SceneManager.sceneCount;i++)
            {
                var scene=SceneManager.GetSceneAt(i);
                if(scene.isLoaded && (scene.path==Hub || scene.path=="Assets/_Project/Scenes/P7_DemoRun.unity" || scene.path=="Assets/_Project/Scenes/P8_BossField.unity"))maps++;
            }
            Assert.That(maps,Is.EqualTo(1),"每一段传送只能运行一张游戏地图");
            Assert.That(UnityEngine.Object.FindObjectsByType<WizardController>(FindObjectsSortMode.None).Length,Is.EqualTo(1));
            Assert.That(UnityEngine.Object.FindObjectsByType<UnityEngine.EventSystems.EventSystem>(FindObjectsSortMode.None).Length,Is.EqualTo(1));
        }
        [UnityTest]public IEnumerator LoadingPracticeRejectsHubAssetChangesAfterReliableSave()
        {
            var gate=Portal("P7");string target=gate.ScenePath;uint version=_session.ReadOnly.Version;Assert.That(gate.TryEnter(_player.CharacterController),Is.True);
            Assert.That(_session.RunCoordinator.ActionsAllowed,Is.False);Assert.That(Time.timeScale,Is.Zero);
            string item=null;foreach(var e in _session.Profile.Items)if(e.Location.Kind==ItemLocationKind.EquippedWand){item=e.Item.InstanceId;break;}
            EventBus<InventoryCommandEvent>.Publish(new InventoryCommandEvent{InventoryId=_session.ReadOnly.InventoryId,Kind=InventoryCommandKind.Drop,ItemId=item,ExpectedVersion=version});
            Assert.That(_session.ReadOnly.Version,Is.EqualTo(version));Assert.That(_session.Profile.PendingRaid,Is.Null);
            float until=Time.realtimeSinceStartup+25;while(!SceneManager.GetSceneByPath(target).isLoaded && Time.realtimeSinceStartup<until)yield return null;
            Assert.That(SceneManager.GetSceneByPath(target).isLoaded,Is.True);
            Assert.That(HubPracticeSession.Current,Is.Not.Null);
        }
        private IEnumerator Practice(string suffix,bool collect=false,bool full=false)
        {
            _before=JsonUtility.ToJson(ProfileSaveMapper.ToDto(_session.Profile,0));var gate=Portal(suffix);string target=gate.ScenePath;
            // 接触只显示提示；走真实Input System的F输入，不能用TryEnter冒充玩家操作。
            Move(_player,gate.transform.position+Vector3.up*.2f);
            yield return null;_inputFixture.Press(_keyboard.fKey);yield return null;_inputFixture.Release(_keyboard.fKey);
            float end=Time.realtimeSinceStartup+25;Scene practice=default;
            while(Time.realtimeSinceStartup<end){practice=SceneManager.GetSceneByPath(target);if(practice.IsValid() && practice.isLoaded)break;yield return null;}
            Assert.That(practice.IsValid() && practice.isLoaded,Is.True,"接触后未加载 "+target);
            yield return null;yield return null;Assert.That(HubPracticeSession.Current,Is.Not.Null);Assert.That(Find<SinglePlayerInventorySession>(practice),Is.Not.Null,"练习Scene必须有真实库存工作区");
            var player=Find<WizardController>(practice);Assert.That(player,Is.Not.Null);
            // 原P7首进主动打开教学说明并暂停；按正常玩家流程关闭，不强改TimeScale绕过暂停所有者。
            var guide=Find<GameplayGuideController>(practice);if(guide!=null)guide.Close();yield return null;Assert.That(Time.timeScale,Is.EqualTo(1));
            if(suffix=="P8"){yield return CompleteBoss(practice);yield return null;}
            var back=Find<HubPracticePortal>(practice);Assert.That(back,Is.Not.Null);
            var ids=new List<string>();string unpicked=null;
            if(collect)
            {
                player.enabled=false;
                var inventory=Find<SinglePlayerInventorySession>(practice);var nearby=Find<NearbyPickupController>(practice);
                Assert.That(inventory.CanAccessWarehouse,Is.False);Assert.That(nearby.Player,Is.EqualTo(player.transform));
                var content=new[]{"Food/Pumpkin","Herb/Herb_Flower_01","Container/Container_02","HubSpell"};
                string bag=null;foreach(var entry in inventory.Profile.Items)if(entry.Location.Kind==ItemLocationKind.EquippedBackpack)bag=entry.Item.InstanceId;
                Assert.That(bag,Is.Not.Null);
                if(full)
                {
                    var ui=Find<SinglePlayerInventoryController>(practice);
                    _inputFixture.Press(_keyboard.tabKey);yield return null;_inputFixture.Release(_keyboard.tabKey);yield return null;
                    Assert.That(ui.IsOpen,Is.True,"练习图Tab应打开携带背包");
                    Assert.That(Find<WandEditorController>(practice),Is.Null,"练习图只保留新版背包，不再实例化旧编辑器");
                    ui.Close();yield return null;Assert.That(Time.timeScale,Is.EqualTo(1));
                    inventory.Profile.Items.TryGet(bag,out var bagItem);inventory.Profile.Items.Catalog.TryGetRules(bagItem.ContentId,out var rules);
                    inventory.Profile.Items.Catalog.TryGet("item.food.apple",out var apple);
                    for(int y=0;y<rules.BackpackHeight;y++)for(int x=0;x<rules.BackpackWidth;x++)
                        inventory.Profile.Items.TryAdd(new RuntimeItemState("fill."+x+"."+y,apple.ContentId,0),new GridPlacement(x,y,0).InBackpack(bag));
                }
                foreach(string key in content)
                {
                    var point=Spawn(practice,inventory,player.transform,key);string id=point.RuntimeInstanceId;ids.Add(id);
                    Assert.That(id,Is.Not.Null);nearby.RefreshCandidates();Assert.That(nearby.FocusedPickup.ItemId,Is.EqualTo(id));
                    _inputFixture.Press(_keyboard.fKey);yield return null;_inputFixture.Release(_keyboard.fKey);yield return null;
                    if(full && key==content[0])
                    {
                        var ui=Find<SinglePlayerInventoryController>(practice);Assert.That(ui.IsOpen,Is.True);
                        inventory.ReadOnly.TryGetView(id,out var ground);Assert.That(ground.Location.Kind,Is.EqualTo(ItemLocationKind.WorldItem));
                        Assert.That(inventory.FindWorldProxy(id).gameObject.activeSelf,Is.True);
                        var changes=new List<InventoryEntry>();foreach(var entry in inventory.Profile.Items)
                            if(entry.Item.InstanceId.StartsWith("fill."))changes.Add(new InventoryEntry(entry.Item,ItemLocation.Warehouse));
                        Assert.That(inventory.Profile.Items.TryApply(changes,inventory.Profile.Items.Version).Success,Is.True);
                        ui.Close();yield return null;nearby.RefreshCandidates();_inputFixture.Press(_keyboard.fKey);yield return null;_inputFixture.Release(_keyboard.fKey);yield return null;
                    }
                    inventory.ReadOnly.TryGetView(id,out var picked);Assert.That(picked.InstanceId,Is.EqualTo(id));
                    Assert.That(picked.Location.Kind,Is.EqualTo(key=="HubSpell"?ItemLocationKind.SpellStorage:ItemLocationKind.Backpack));
                    if(key=="HubSpell")Assert.That(picked.RemainingCharges,Is.EqualTo(7));
                    if(key.StartsWith("Container"))Assert.That(picked.ContainerCapacity,Is.GreaterThan(0));
                }
                unpicked=Spawn(practice,inventory,player.transform,"Food/Apple").RuntimeInstanceId;
                if(suffix=="P7" && !full)yield return ProbePickupProfiler();
                yield return new WaitForEndOfFrame();
                System.IO.Directory.CreateDirectory("TestResults/SinglePlayer/M6-T3-ItemCarry");
                ScreenCapture.CaptureScreenshot("TestResults/SinglePlayer/M6-T3-ItemCarry/"+suffix+"-pickup.png");
            }
            // 原关卡的玩家/法术与世界有独立初始化，实际施法也不能写进据点库存。
            player.GetComponent<SpellCaster>().CastWand(player.transform.position+Vector3.up*2,player.transform.position+Vector3.forward*10,1,777,player.CharacterController);
            Move(player,back.transform.position+Vector3.up*.2f);
            yield return null;_inputFixture.Press(_keyboard.fKey);yield return null;_inputFixture.Release(_keyboard.fKey);
            end=Time.realtimeSinceStartup+25;
            while(Time.realtimeSinceStartup<end){var hub=SceneManager.GetSceneByPath(Hub);if(hub.IsValid() && hub.isLoaded && _session!=null && _session.gameObject.scene==hub)break;yield return null;}
            yield return null;yield return null;Assert.That(_scene.path,Is.EqualTo(Hub));Assert.That(_scene.isLoaded,Is.True);Assert.That(_session,Is.Not.Null);
            Assert.That(HubPracticeSession.Current,Is.Null);Assert.That(RunSessionCoordinator.Current,Is.Null);
            if(!collect)Assert.That(JsonUtility.ToJson(ProfileSaveMapper.ToDto(_session.Profile,0)),Is.EqualTo(_before));
            else
            {
                foreach(string id in ids){Assert.That(_session.ReadOnly.TryGetView(id,out var item),Is.True,"回Hub丢失"+id);
                    Assert.That(item.Location.Kind==ItemLocationKind.Backpack || item.Location.Kind==ItemLocationKind.SpellStorage,Is.True);}
                Assert.That(_session.Profile.Items.TryGet(unpicked,out _),Is.False,"留在练习图的地面物品不能寄回据点");
            }
            Assert.That(_session.Profile.PendingRaid,Is.Null);
        }
        private static WorldItemSpawnPoint Spawn(Scene scene,SinglePlayerInventorySession session,Transform player,string key)
        {
            var definition=AssetDatabase.LoadAssetAtPath<ItemDefinition>(key=="HubSpell"
                ?"Assets/_Project/ScriptableObjects/SinglePlayer/Hub/SinglePlayerWands/fire.asset"
                :"Assets/_Project/ScriptableObjects/SinglePlayer/Items/"+key+".asset");
            var node=definition.PickupPrefab!=null?UnityEngine.Object.Instantiate(definition.PickupPrefab):new GameObject("Test authored item");
            SceneManager.MoveGameObjectToScene(node,scene);node.transform.position=player.position+Vector3.up*.4f;
            var point=node.GetComponent<WorldItemSpawnPoint>();if(point==null)point=node.AddComponent<WorldItemSpawnPoint>();
            var data=new SerializedObject(point);data.FindProperty("_definition").objectReferenceValue=definition;
            data.FindProperty("_spawnId").stringValue=Guid.NewGuid().ToString("N");data.FindProperty("_initialCharges").intValue=7;data.ApplyModifiedPropertiesWithoutUndo();
            point.BindRuntimeSession(session,scene.path);Assert.That(point.TryActivate(),Is.True);return point;
        }
        private static IEnumerator ProbePickupProfiler()
        {
            bool recording=UnityEditorInternal.ProfilerDriver.enabled;
            bool cpu=UnityEditorInternal.ProfilerDriver.IsAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU);
            try
            {
                UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,true);UnityEditorInternal.ProfilerDriver.enabled=true;
                for(int i=0;i<30;i++)yield return null;int first=UnityEditorInternal.ProfilerDriver.lastFrameIndex+1;
                for(int i=0;i<45;i++)yield return null;int last=UnityEditorInternal.ProfilerDriver.lastFrameIndex;
                var names=new[]{"Norn.Items.NearbyQuery","Norn.Items.PickupList"};var samples=new int[2];var bytes=new long[2];int frames=0;
                for(int frame=Math.Max(first,UnityEditorInternal.ProfilerDriver.firstFrameIndex);frame<=last;frame++)
                {
                    using var data=UnityEditorInternal.ProfilerDriver.GetRawFrameDataView(frame,0);if(!data.valid)continue;frames++;
                    for(int sample=0;sample<data.sampleCount;sample++)for(int m=0;m<2;m++)if(data.GetSampleName(sample)==names[m])
                    {
                        samples[m]++;int stop=sample+data.GetSampleChildrenCountRecursive(sample);
                        for(int child=sample+1;child<=stop;child++)if(data.GetSampleName(child)=="GC.Alloc" && data.GetSampleMetadataCount(child)>0)bytes[m]+=data.GetSampleMetadataAsLong(child,0);
                    }
                }
                const string folder="TestResults/SinglePlayer/M6-T3-ItemCarry";System.IO.Directory.CreateDirectory(folder);
                System.IO.File.WriteAllText(folder+"/practice-pickup-profiler.txt","实际P7工作区，稳定附近列表，初始化/拾取等离散变化排除\nFrames="+frames
                    +"\n"+names[0]+" Samples="+samples[0]+" GCBytes="+bytes[0]+"\n"+names[1]+" Samples="+samples[1]+" GCBytes="+bytes[1]);
                UnityEditorInternal.ProfilerDriver.SaveProfile(folder+"/practice-pickup-profiler.data");
                Assert.That(frames,Is.GreaterThanOrEqualTo(30));for(int m=0;m<2;m++){Assert.That(samples[m],Is.GreaterThan(0),names[m]);Assert.That(bytes[m],Is.Zero,names[m]);}
            }
            finally{UnityEditorInternal.ProfilerDriver.enabled=recording;UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,cpu);}
        }
        [UnityTest]public IEnumerator FailedSaveRejectsPracticeContactAndLeavesReadableFailure()
        {
            var navigation=Find<HubNavigationView>(_scene);
            // 用户当前布局停用了旧导航Canvas。失败提示用例只在隔离Scene显式启用消费者，不改正式Scene配置。
            TestContext.WriteLine("Saved navigation activeSelf="+navigation.gameObject.activeSelf);
            navigation.gameObject.SetActive(true);yield return null;
            Assert.That(navigation.isActiveAndEnabled,Is.True,"失败提示消费者需要活动层级。");
            _files.Fail=true;var gate=Portal("P7");_before=JsonUtility.ToJson(ProfileSaveMapper.ToDto(_session.Profile,0));
            Assert.That(gate.TryEnter(_player.CharacterController),Is.False);Assert.That(HubPracticeSession.Current,Is.Null);
            Assert.That(_scene.isLoaded,Is.True);Assert.That(JsonUtility.ToJson(ProfileSaveMapper.ToDto(_session.Profile,0)),Is.EqualTo(_before));
            Assert.That(Time.timeScale,Is.Zero);Find<HubNavigationView>(_scene).Cancel();Assert.That(Time.timeScale,Is.EqualTo(1));yield return null;
        }
        [UnityTearDown]public IEnumerator Cleanup()
        {
            try
            {
                SceneManager.sceneLoaded-=Loaded;if(_files!=null)_files.Fail=false;Time.timeScale=1;
                if(HubPracticeSession.Current!=null)UnityEngine.Object.Destroy(HubPracticeSession.Current.gameObject);
                RunSessionCoordinator.Current?.EndRun();
                // Single加载可能已卸载Runner初始Scene；先创建空承载Scene，再卸载本例的真实场景。
                var empty=SceneManager.GetSceneByName("Practice test cleanup");if(!empty.IsValid() || !empty.isLoaded)empty=SceneManager.CreateScene("Practice test cleanup");SceneManager.SetActiveScene(empty);
                for(int i=SceneManager.sceneCount-1;i>=0;i--){var scene=SceneManager.GetSceneAt(i);if(scene!=empty && (scene.path==Hub || scene.path=="Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity" || scene.path.EndsWith("P7_MainMenu.unity") || scene.path.EndsWith("P7_DemoRun.unity") || scene.path.EndsWith("P8_BossField.unity")))yield return SceneManager.UnloadSceneAsync(scene);}
            }
            finally
            {
                // 清理或断言异常不能把独立测试Runtime留给真实玩家输入。
                try{_inputFixture?.TearDown();}finally{_inputFixture=null;Application.runInBackground=_background;Time.timeScale=1;}
            }
            yield return null;
            // 多个真实地图用例串行后释放测试资源，避免D3D12保留上一地图的高分辨率Texture。
            yield return Resources.UnloadUnusedAssets();GC.Collect();
        }
        private sealed class MemoryFiles:IProfileFileSystem
        {
            private readonly Dictionary<string,string> _files=new Dictionary<string,string>();public bool Fail;
            public bool Exists(string p)=>_files.ContainsKey(p);public string Read(string p)=>_files[p];
            public void WriteFlushed(string p,string text){if(Fail)throw new System.IO.IOException("Injected practice save failure");_files[p]=text;}
            public void Move(string a,string b){_files[b]=_files[a];_files.Remove(a);}public void Replace(string a,string b,string backup){_files[backup]=_files[b];Move(a,b);}
        }
    }
}
#endif
