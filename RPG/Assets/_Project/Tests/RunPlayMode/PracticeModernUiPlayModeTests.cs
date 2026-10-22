#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using Game.Character;
using Game.Combat;
using Game.Core;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Game.Run.Tests
{
    /// <summary>真实隔离地图/PlayerLoop验证，不修改正式Scene，不替换实际施法来源。</summary>
    public sealed class PracticeModernUiPlayModeTests
    {
        private const string Evidence="TestResults/SinglePlayer/M6-T5-PracticeModernUi";
        private InputTestFixture _input;private Keyboard _keys;private Mouse _mouse;private Scene _scene;
        private bool _background;private SinglePlayerInventorySession _session;private WizardController _player;
        private SinglePlayerInventoryController _bag;
        [SetUp]public void Setup()
        {_background=Application.runInBackground;Application.runInBackground=true;_input=new InputTestFixture();_input.Setup();_keys=InputSystem.AddDevice<Keyboard>();_mouse=InputSystem.AddDevice<Mouse>();Time.timeScale=1;}
        private IEnumerator Open(string map)
        {
            EditorWindow.GetWindow(Type.GetType("UnityEditor.GameView,UnityEditor")).Focus();
            _scene=EditorSceneManager.LoadSceneInPlayMode("Assets/_Project/Scenes/"+map+".unity",new LoadSceneParameters(LoadSceneMode.Additive));
            for(int i=0;i<5;i++)yield return null;
            Find<GameplayGuideController>()?.Close();Time.timeScale=1;yield return null;
            _session=Find<SinglePlayerInventorySession>();_player=Find<WizardController>();_bag=Find<SinglePlayerInventoryController>();
            Assert.That(_session?.ReadOnly,Is.Not.Null);Assert.That(HubPracticeSession.Current,Is.Not.Null);
        }
        private T Find<T>()where T:Component
        {foreach(var root in _scene.GetRootGameObjects()){var c=root.GetComponentInChildren<T>(true);if(c!=null)return c;}return null;}
        private IEnumerator Layout(string map)
        {
            yield return Open(map);
            Assert.That(Find<PlayerManaBar>(),Is.Null);
            foreach(var root in _scene.GetRootGameObjects())foreach(var node in root.GetComponentsInChildren<Transform>(true))
                Assert.That(node.name.Trim(),Is.Not.EqualTo("AimPoint"),"旧红十字必须移除，仅检查新增白点会漏掉重影");
            Assert.That(UnityEngine.Object.FindObjectsByType<PracticeInventoryWorkspace>(FindObjectsSortMode.None).Length,Is.EqualTo(1));
            Assert.That(Find<HeldItemHud>(),Is.Not.Null);Assert.That(Find<NearbyPickupListView>(),Is.Not.Null);
            Assert.That(Find<ContainerCompositionView>(),Is.Not.Null);
            Assert.That(_player.GetComponent<OwnedWandCastAdapter>().ReadOnly.InventoryId,Is.EqualTo(_session.ReadOnly.InventoryId));
            var dot=Find<HeldItemHud>().GetComponentInParent<Canvas>().transform.Find("ScreenCenterDot");
            Assert.That(dot.gameObject.activeInHierarchy,Is.True);Assert.That(dot.GetComponent<Image>().color,Is.EqualTo(Color.white));
            Assert.That(((RectTransform)dot).sizeDelta,Is.EqualTo(new Vector2(5,5)));
            _input.Press(_keys.tabKey);yield return null;_input.Release(_keys.tabKey);yield return null;
            Assert.That(_bag.IsOpen,Is.True);Assert.That(Time.timeScale,Is.Zero);
            var convenience=new SerializedObject(_bag.GetComponent<BackpackWorkspaceView>());
            Assert.That(convenience.FindProperty("_organize").objectReferenceValue,Is.Not.Null,"副本共用工作区也需要整理按钮");
            Assert.That(convenience.FindProperty("_pickupAll").objectReferenceValue,Is.Not.Null,"副本共用工作区也需要全拾取按钮");
            Assert.That(Find<HeldItemHud>().GetComponentInParent<Canvas>().enabled,Is.False);
            Assert.That(Find<ContainerCompositionView>().GetComponentInParent<Canvas>().enabled,Is.False);
            yield return new WaitForEndOfFrame();Directory.CreateDirectory(Evidence);ScreenCapture.CaptureScreenshot(Evidence+"/"+map+"-backpack.png");
            _input.Press(_keys.tabKey);yield return null;_input.Release(_keys.tabKey);yield return null;
            Assert.That(_bag.IsOpen,Is.False);Assert.That(Time.timeScale,Is.EqualTo(1));
            Assert.That(Find<HeldItemHud>().GetComponentInParent<Canvas>().enabled,Is.True);
            yield return new WaitForEndOfFrame();ScreenCapture.CaptureScreenshot(Evidence+"/"+map+"-hud.png");
        }
        [UnityTest]public IEnumerator DirectP7UsesOneModernWorkspaceAndTab(){yield return Layout("P7_DemoRun");}
        [UnityTest]public IEnumerator DirectP8UsesOneModernWorkspaceAndTab(){yield return Layout("P8_BossField");}
        [UnityTest]public IEnumerator ModernP8LeavingSafeZoneStartsActualBossEncounter()
        {
            yield return Open("P8_BossField");
            var entry=Find<BossEntryTrigger>();var encounter=Find<BossEncounterController>();
            Assert.That(entry,Is.Not.Null);Assert.That(encounter.State,Is.EqualTo(BossEncounterState.Inactive));
            Assert.That(entry.TryArm(_player.CharacterController),Is.True);
            Assert.That(entry.TryActivate(_player.CharacterController),Is.True,"新版库存不能继续要求旧RuntimeWand才能开战。");
            Assert.That(encounter.State,Is.EqualTo(BossEncounterState.Running));
            Assert.That(entry.TryActivate(_player.CharacterController),Is.False,"只能启动一次。");
        }
        [UnityTest]public IEnumerator ModernP8ActualBoundaryExitAndRetryRestoreOwnedCheckpoint()
        {
            yield return Open("P8_BossField");
            var entry=Find<BossEntryTrigger>();var encounter=Find<BossEncounterController>();
            Assert.That(entry.TryArm(_player.CharacterController),Is.True);
            long money=_session.Currency;int count=_session.ReadOnly.Count;
            var bounds=entry.GetComponent<Collider>().bounds;
            // 关闭Collider再传到圈外不会产生Exit；先建立真实重叠，再由Move离开，验证实际Physics通路。
            _player.enabled=false;
            var inside=bounds.center;inside.y=_player.transform.position.y;
            _player.CharacterController.Move(inside-_player.transform.position);Physics.SyncTransforms();
            yield return new WaitForFixedUpdate();yield return new WaitForFixedUpdate();
            _player.CharacterController.Move(Vector3.right*(bounds.extents.x+3));Physics.SyncTransforms();
            yield return new WaitForSecondsRealtime(.3f);
            Assert.That(encounter.State,Is.EqualTo(BossEncounterState.Running),"真实Physics离开安全区也必须开战。");
            Assert.That(HubPracticeSession.Current.HasBossCheckpoint,Is.True);
            _session.Profile.Currency=money+17;
            string path=_scene.path;int oldHandle=_scene.handle;
            Find<DemoPauseMenuController>().RetryBoss();
            float end=Time.realtimeSinceStartup+35;
            while(Time.realtimeSinceStartup<end)
            {
                var loaded=SceneManager.GetSceneByPath(path);
                if(loaded.isLoaded && loaded.handle!=oldHandle && !HubPracticeSession.Current.Loading){_scene=loaded;break;}
                yield return null;
            }
            Assert.That(_scene.handle,Is.Not.EqualTo(oldHandle));
            Find<GameplayGuideController>()?.Close();yield return null;
            _session=Find<SinglePlayerInventorySession>();_player=Find<WizardController>();_bag=Find<SinglePlayerInventoryController>();
            Assert.That(_session.Currency,Is.EqualTo(money),"Retry必须回入场金币，旧工作区OnDestroy不能覆盖快照。");
            Assert.That(_session.ReadOnly.Count,Is.EqualTo(count));Assert.That(Find<BossEncounterController>().State,Is.EqualTo(BossEncounterState.Inactive));
            Assert.That(Find<BossEntryTrigger>().TryArm(_player.CharacterController),Is.True);
            Assert.That(Find<BossEntryTrigger>().TryActivate(_player.CharacterController),Is.True,"重新出圈仍可启动第二次战斗。");
            // 加载完成不等于渐亮完成；截图等待真实Fade清零，不能把黑帧当作Boss画面证据。
            yield return new WaitForSecondsRealtime(.8f);
            Assert.That(UnityEngine.Object.FindFirstObjectByType<PortalTransitionFade>().GetComponent<CanvasGroup>().alpha,Is.Zero);
            yield return new WaitForEndOfFrame();Directory.CreateDirectory("TestResults/SinglePlayer/M6-T11-PortalBossWands");
            ScreenCapture.CaptureScreenshot("TestResults/SinglePlayer/M6-T11-PortalBossWands/p8-boss-running-after-retry.png");
        }
        [UnityTest]public IEnumerator ActualKeyboardSelectionAndMouseCastConsumeOwnedMana()
        {
            yield return Open("P7_DemoRun");
            // Fixture额外开启的缓存诊断在Editor合成输入时报告Package内部状态错误。
            // 此用例验证游戏输入/实际Mana链路，采用直接读取；TearDown恢复原settings，不改项目配置。
            InputSystem.settings.SetInternalFeatureFlag("USE_READ_VALUE_CACHING",false);
            _input.Press(_keys.digit2Key);yield return null;_input.Release(_keys.digit2Key);yield return null;
            Assert.That(_session.ToolbeltReadOnly.SelectedSlot,Is.EqualTo(1));
            _input.Press(_keys.digit1Key);yield return null;_input.Release(_keys.digit1Key);yield return null;
            Assert.That(_session.WandsReadOnly.TryGetSlot(0,out var before),Is.True);
            Assert.That(_player.GetComponent<OwnedWandCastAdapter>().TryCapture(out var program,out _),Is.True);
            Assert.That(program.Program.Count,Is.GreaterThan(0));float legacy=_player.GetComponent<ManaComponent>().CurrentMana;
            // 使用Fixture按键入口保留其余设备状态，避免整体MouseState覆写触发缓存校验。
            _input.Press(_mouse.leftButton);yield return null;_input.Release(_mouse.leftButton);
            float end=Time.realtimeSinceStartup+4;bool spent=false;
            while(Time.realtimeSinceStartup<end){if(_session.WandsReadOnly.TryGetSlot(0,out var now)&&now.CurrentMana<before.CurrentMana){spent=true;break;}yield return null;}
            Assert.That(spent,Is.True,"点击必须消耗背包中实际法杖的Mana，不能继续走旧SpellCaster.CastWand路径");
            Assert.That(_player.GetComponent<ManaComponent>().CurrentMana,Is.EqualTo(legacy));
        }
        [UnityTest]public IEnumerator EscapeClosesBackpackBeforeOpeningPauseMenu()
        {
            yield return Open("P7_DemoRun");_bag.OpenBackpack();yield return null;
            _input.Press(_keys.escapeKey);yield return null;_input.Release(_keys.escapeKey);yield return null;
            Assert.That(_bag.IsOpen,Is.False);Assert.That(Time.timeScale,Is.EqualTo(1),"关闭背包的Esc不能同时又打开Pause Menu");
            _input.Press(_keys.escapeKey);yield return null;_input.Release(_keys.escapeKey);yield return null;
            Assert.That(Find<RunPauseCoordinator>().HasReason(RunPauseReason.PauseMenu),Is.True);
            _input.Press(_keys.escapeKey);yield return null;_input.Release(_keys.escapeKey);yield return null;
            Assert.That(Time.timeScale,Is.EqualTo(1));
        }
        [UnityTest]public IEnumerator RewardIsOwnedAndIdempotentAndFullInventoryCanRetry()
        {
            yield return Open("P7_DemoRun");_player.enabled=false;
            var rewards=Find<PracticeSpellRewardAdapter>();var spell=AssetDatabase.LoadAssetAtPath<Game.Skills.SpellLibrary>("Assets/_Project/ScriptableObjects/Spell/Libraries/SpellLibrary_Player.asset").Available[0];
            Assert.That(_session.Profile.Items.Catalog.TryFindSpell(spell,out var definition),Is.True);
            int count=_session.ReadOnly.Count;Assert.That(rewards.TryGrant("test",spell),Is.True);Assert.That(_session.ReadOnly.Count,Is.EqualTo(count+1));
            Assert.That(rewards.TryGrant("test",spell),Is.True);Assert.That(_session.ReadOnly.Count,Is.EqualTo(count+1));
            var store=_session.Profile.Items;string bagId=null;
            foreach(var entry in store)if(entry.Location.Kind==ItemLocationKind.EquippedBackpack)bagId=entry.Item.InstanceId;
            for(int i=0;i<12;i++)store.TryAdd(RuntimeItemState.Create(definition.ContentId,0,true),new ItemLocation(ItemLocationKind.SpellStorage,_session.ReadOnly.ActorId,0,0,0,i));
            for(int y=0;y<8;y++)for(int x=0;x<8;x++)store.TryAdd(RuntimeItemState.Create(definition.ContentId,0,true),new GridPlacement(x,y,0).InBackpack(bagId));
            count=store.Count;Assert.That(rewards.TryGrant("full",spell),Is.False);Assert.That(store.Count,Is.EqualTo(count));
            string remove=null;foreach(var entry in store)if(entry.Location.Kind==ItemLocationKind.SpellStorage){remove=entry.Item.InstanceId;break;}
            Assert.That(store.TryMove(remove,ItemLocation.Warehouse).Success,Is.True);
            Assert.That(rewards.TryGrant("full",spell),Is.True);Assert.That(rewards.TryGrant("full",spell),Is.True);Assert.That(store.Count,Is.EqualTo(count+1));
        }
        [UnityTest]public IEnumerator EditedFiniteProgramAndCurrencySurviveP7AndReturnToHub()
        {
            yield return Open("P7_DemoRun");_player.enabled=false;
            var adapter=_player.GetComponent<OwnedWandCastAdapter>();Assert.That(adapter.TryCapture(out var initial,out _),Is.True);
            var spell=initial.Program[0];Assert.That(_session.Profile.Items.Catalog.TryFindSpell(spell,out var definition),Is.True);
            EventBus<InventoryCommandEvent>.Publish(new InventoryCommandEvent{InventoryId=_session.ReadOnly.InventoryId,Kind=InventoryCommandKind.Place,
                ItemId=initial.Program.GetInstanceId(0),Destination=InventoryDestination.SpellStorage,SlotIndex=0,ExpectedVersion=_session.ReadOnly.Version});
            var finite=RuntimeItemState.Create(definition.ContentId,3,false);
            Assert.That(_session.Profile.Items.TryAdd(finite,new ItemLocation(ItemLocationKind.SpellStorage,_session.ReadOnly.ActorId,0,0,0,1)),Is.True);
            EventBus<InventoryCommandEvent>.Publish(new InventoryCommandEvent{InventoryId=_session.ReadOnly.InventoryId,Kind=InventoryCommandKind.Place,
                ItemId=finite.InstanceId,OwnerId=initial.SourceWandId,Destination=InventoryDestination.WandSlot,SlotIndex=0,ExpectedVersion=_session.ReadOnly.Version});
            Assert.That(adapter.TryCapture(out var edited,out var source),Is.True);Assert.That(edited.Program.GetInstanceId(0),Is.EqualTo(finite.InstanceId));
            Assert.That(_player.SpellCaster.CastOwnedProgram(edited,source,new Vector3(30,20,20),new Vector3(30,20,40),1,777,_player.CharacterController),Is.EqualTo(1));
            Assert.That(_session.ReadOnly.TryGetView(finite.InstanceId,out var card),Is.True);Assert.That(card.RemainingCharges,Is.EqualTo(2));
            var catalog=AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/_Project/ScriptableObjects/World/Hub/Inventory/Catalog.asset");
            var definitions=new SerializedObject(catalog).FindProperty("_definitions");ItemDefinition bottleDefinition=null;
            for(int i=0;i<definitions.arraySize;i++){var item=(ItemDefinition)definitions.GetArrayElementAtIndex(i).objectReferenceValue;if(item.Kind==ItemKind.Container){bottleDefinition=item;break;}}
            var bottle=RuntimeItemState.Create(bottleDefinition.ContentId);string bagId=null;
            foreach(var entry in _session.Profile.Items)if(entry.Location.Kind==ItemLocationKind.EquippedBackpack)bagId=entry.Item.InstanceId;
            Assert.That(_session.Profile.Items.TryAdd(bottle,new GridPlacement(0,0,0).InBackpack(bagId)),Is.True);
            EventBus<ToolbeltBindingRequestedEvent>.Publish(new ToolbeltBindingRequestedEvent{InventoryId=_session.ReadOnly.InventoryId,ItemId=bottle.InstanceId,ToolSlot=0,ExpectedVersion=_session.ToolbeltReadOnly.ConfigurationVersion});
            _player.GetComponent<HeldItemInputController>().Select(3);
            Assert.That(_player.GetComponent<MaterialCollectionController>().SelectedContainerId,Is.EqualTo(bottle.InstanceId));
            _session.Profile.Currency=4321;
            var gate=Find<StageExitPortal>();int encounter=new SerializedObject(gate).FindProperty("_encounterIndex").intValue;
            EventBus<EncounterCompletedEvent>.Publish(new EncounterCompletedEvent{EncounterIndex=encounter});
            Assert.That(gate.TryEnter(_player.CharacterController),Is.True);
            float end=Time.realtimeSinceStartup+40;
            while(HubPracticeSession.Current!=null&&Time.realtimeSinceStartup<end)yield return null;
            _scene=SceneManager.GetSceneByPath("Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity");
            yield return null;yield return null;Assert.That(_scene.isLoaded,Is.True);
            Assert.That(SceneManager.GetSceneByPath("Assets/_Project/Scenes/P7_DemoRun.unity").isLoaded,Is.False);
            _session=Find<SinglePlayerInventorySession>();_player=Find<WizardController>();_bag=Find<SinglePlayerInventoryController>();
            AssertCarriedProgram(initial.SourceWandId,finite.InstanceId);
            Assert.That(_session.ToolbeltReadOnly.SelectedSlot,Is.EqualTo(3));
            Assert.That(_player.GetComponent<MaterialCollectionController>().SelectedContainerId,Is.EqualTo(bottle.InstanceId),"新Scene需要恢复已选容器，不能依赖另一次SelectionChanged事件");
        }
        private void AssertCarriedProgram(string wand,string card)
        {
            Assert.That(_session.ReadOnly.TryGetView(card,out var view),Is.True);Assert.That(view.RemainingCharges,Is.EqualTo(2));
            Assert.That(view.Location.Kind,Is.EqualTo(ItemLocationKind.WandSlot));Assert.That(view.Location.OwnerId,Is.EqualTo(wand));
            Assert.That(_session.Currency,Is.EqualTo(4321));Assert.That(_session.WandsReadOnly.TryGetProgram(wand,out var program),Is.True);
            Assert.That(program.Program.GetInstanceId(0),Is.EqualTo(card));
        }
        [UnityTest]public IEnumerator StablePracticeHudAndPickupHaveNoScopedGc()
        {
            yield return Open("P8_BossField");_player.enabled=false;CreateSelectedBottle();
            bool record=UnityEditorInternal.ProfilerDriver.enabled;bool cpu=UnityEditorInternal.ProfilerDriver.IsAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU);
            try
            {
                UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,true);UnityEditorInternal.ProfilerDriver.enabled=true;
                for(int i=0;i<30;i++)yield return null;int first=UnityEditorInternal.ProfilerDriver.lastFrameIndex+1;
                for(int i=0;i<60;i++)yield return null;int last=UnityEditorInternal.ProfilerDriver.lastFrameIndex;
                var names=new[]{"Norn.HeldItemHud.Update","Norn.Items.NearbyQuery","Norn.Items.PickupList","Norn.ContainerHud.Update"};var samples=new int[4];var bytes=new long[4];int frames=0;
                for(int frame=Math.Max(first,UnityEditorInternal.ProfilerDriver.firstFrameIndex);frame<=last;frame++)
                {
                    using var data=UnityEditorInternal.ProfilerDriver.GetRawFrameDataView(frame,0);if(!data.valid)continue;frames++;
                    for(int sample=0;sample<data.sampleCount;sample++)for(int m=0;m<names.Length;m++)if(data.GetSampleName(sample)==names[m])
                    {samples[m]++;int stop=sample+data.GetSampleChildrenCountRecursive(sample);for(int child=sample+1;child<=stop;child++)if(data.GetSampleName(child)=="GC.Alloc"&&data.GetSampleMetadataCount(child)>0)bytes[m]+=data.GetSampleMetadataAsLong(child,0);}
                }
                Directory.CreateDirectory(Evidence);var text="稳定实际P8，预热后60帧；离散生成/装备变化排除\nFrames="+frames;
                for(int m=0;m<names.Length;m++)text+="\n"+names[m]+" Samples="+samples[m]+" GCBytes="+bytes[m];File.WriteAllText(Evidence+"/practice-modern-profiler.txt",text);
                UnityEditorInternal.ProfilerDriver.SaveProfile(Evidence+"/practice-modern-profiler.data");
                Assert.That(frames,Is.GreaterThanOrEqualTo(30));for(int m=0;m<names.Length;m++){Assert.That(samples[m],Is.GreaterThan(0),names[m]);Assert.That(bytes[m],Is.Zero,names[m]);}
            }
            finally{UnityEditorInternal.ProfilerDriver.enabled=record;UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,cpu);}
        }
        private void CreateSelectedBottle()
        {
            var catalog=AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/_Project/ScriptableObjects/World/Hub/Inventory/Catalog.asset");
            var definitions=new SerializedObject(catalog).FindProperty("_definitions");ItemDefinition definition=null;
            for(int i=0;i<definitions.arraySize;i++){var item=(ItemDefinition)definitions.GetArrayElementAtIndex(i).objectReferenceValue;if(item.Kind==ItemKind.Container){definition=item;break;}}
            string bag=null;foreach(var entry in _session.Profile.Items)if(entry.Location.Kind==ItemLocationKind.EquippedBackpack)bag=entry.Item.InstanceId;
            var bottle=RuntimeItemState.Create(definition.ContentId);Assert.That(_session.Profile.Items.TryAdd(bottle,new GridPlacement(0,0,0).InBackpack(bag)),Is.True);
            EventBus<ToolbeltBindingRequestedEvent>.Publish(new ToolbeltBindingRequestedEvent{InventoryId=_session.ReadOnly.InventoryId,ToolSlot=0,ItemId=bottle.InstanceId,ExpectedVersion=_session.ToolbeltReadOnly.ConfigurationVersion});
            _player.GetComponent<HeldItemInputController>().Select(3);
        }
        [UnityTearDown]public IEnumerator Cleanup()
        {
            try
            {
                _bag?.Close();Time.timeScale=1;
                if(HubPracticeSession.Current!=null)UnityEngine.Object.Destroy(HubPracticeSession.Current.gameObject);
                foreach(var projectile in UnityEngine.Object.FindObjectsByType<ProjectileBase>(FindObjectsSortMode.None))UnityEngine.Object.Destroy(projectile.gameObject);
                yield return null;
                var empty=SceneManager.GetSceneByName("Modern practice test cleanup");if(!empty.IsValid()||!empty.isLoaded)empty=SceneManager.CreateScene("Modern practice test cleanup");SceneManager.SetActiveScene(empty);
                for(int i=SceneManager.sceneCount-1;i>=0;i--)
                {
                    var scene=SceneManager.GetSceneAt(i);
                    if(scene!=empty&&(scene==_scene||scene.path=="Assets/_Project/Scenes/P7_DemoRun.unity"||scene.path=="Assets/_Project/Scenes/P8_BossField.unity"||scene.path=="Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity"))yield return SceneManager.UnloadSceneAsync(scene);
                }
            }
            finally{_input?.TearDown();Application.runInBackground=_background;Time.timeScale=1;}
            yield return Resources.UnloadUnusedAssets();GC.Collect();
        }
    }
}
#endif
