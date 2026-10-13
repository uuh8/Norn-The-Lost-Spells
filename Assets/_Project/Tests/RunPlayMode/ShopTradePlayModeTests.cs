#if UNITY_EDITOR
using System;
using System.Collections;
using System.Reflection;
using Game.Core;
using Game.Character;
using Game.Combat;
using Game.ElementField;
using Game.Run;
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
    /// <summary>直接加载商店Scene，以真实按钮/Core事件/可靠保存回归；每项使用内存文件，不接触玩家档。</summary>
    public sealed class ShopTradePlayModeTests
    {
        private InputTestFixture _input;
        private const string ScenePath="Assets/_Project/Scenes/Tests/TradeSandbox.unity";
        private Scene _scene;
        private SinglePlayerInventorySession _inventory;
        private SinglePlayerShopSession _shop;
        private SinglePlayerRunCoordinator _run;
        private ShopController _ui;
        private RunPauseCoordinator _pause;
        private ProfileSaveRepository _repository;
        private string _bottle,_card;
        private bool _background;
        [UnitySetUp]public IEnumerator Setup()
        {
            // 只隔离测试输入，不禁用用户真实键鼠；finally在卸载Scene后恢复。
            _input=new InputTestFixture();_input.Setup();
            _background=Application.runInBackground;Application.runInBackground=true;Time.timeScale=1;
            EditorWindow.GetWindow(Type.GetType("UnityEditor.GameView,UnityEditor")).Focus();
            SceneManager.sceneLoaded+=Loaded;_scene=EditorSceneManager.LoadSceneInPlayMode(ScenePath,new LoadSceneParameters(LoadSceneMode.Additive));
            yield return null;yield return null;yield return null;SceneManager.sceneLoaded-=Loaded;
            Assert.That(_shop.ReadOnly,Is.Not.Null,_shop.Message);Assert.That(_ui.IsOpen,Is.True);
        }
        private void Loaded(Scene scene,LoadSceneMode mode)
        {
            if(scene.path!=ScenePath)return;_scene=scene;
            _inventory=Find<SinglePlayerInventorySession>(scene);_shop=Find<SinglePlayerShopSession>(scene);_ui=Find<ShopController>(scene);
            _pause=Find<RunPauseCoordinator>(scene);_run=Find<SinglePlayerRunCoordinator>(scene);
            var serialized=new SerializedObject(_inventory);var catalog=(ItemCatalog)serialized.FindProperty("_catalog").objectReferenceValue;
            var seeds=(SinglePlayerStartingInventory)serialized.FindProperty("_startingInventory").objectReferenceValue;
            Assert.That(catalog.TryCreateIndex(out var index,out _),Is.True);Assert.That(seeds.TryCreate(index,out var initial,out _),Is.True);
            var profile=new SinglePlayerProfile(initial.ActorId,initial.Items,currency:100);
            foreach(var entry in profile.Items)if(entry.Item.Composition!=null){_bottle=entry.Item.InstanceId;var amounts=new[]{new MaterialAmount(1,36),new MaterialAmount(3,24)};entry.Item.Composition.AddReserved(amounts,2);break;}
            foreach(var entry in profile.Items)if(entry.Location.Kind==ItemLocationKind.EquippedBackpack)
            {Assert.That(profile.Items.TryMove(_bottle,new GridPlacement(0,0,0).InBackpack(entry.Item.InstanceId)).Success,Is.True);break;}
            var card=RuntimeItemState.Create("wandtest.fire",12);_card=card.InstanceId;Assert.That(profile.Items.TryAdd(card,ItemLocation.Warehouse),Is.True);
            _repository=new ProfileSaveRepository("Temp/SinglePlayer/M5-PlayMemory/"+Guid.NewGuid().ToString("N"),index,new PlayMemoryFiles());
            Assert.That(_inventory.Initialize(profile,_repository),Is.True);
        }
        private static T Find<T>(Scene scene)where T:Component
        {foreach(var root in scene.GetRootGameObjects()){var c=root.GetComponentInChildren<T>(true);if(c!=null)return c;}return null;}
        private T Field<T>(string name)=> (T)typeof(ShopController).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(_ui);
        private void Click(string name)=>Field<Button>(name).onClick.Invoke();
        [UnityTest]public IEnumerator BuyButtonCommitsActualWalletStockAndDisk()
        {
            _ui.SelectOffer("wandtest.fire");Field<InputField>("_quantity").text="1";int before=_inventory.Profile.Items.Count;
            Click("_buyButton");yield return null;
            Assert.That(_shop.ReadOnly.Currency,Is.EqualTo(80));Assert.That(_inventory.Profile.Items.Count,Is.EqualTo(before+1));
            Assert.That(_repository.TryLoad(out var persisted).Success,Is.True);Assert.That(persisted.Currency,Is.EqualTo(80));Assert.That(_ui.Status,Does.Contain("已保存"));
        }
        [UnityTest]public IEnumerator SellMaterialsButtonPreservesShellAndMixture()
        {
            _ui.SelectOwned(_bottle);Field<InputField>("_quantity").text="10";Click("_sellMaterialButton");yield return null;
            Assert.That(_inventory.Profile.Items.TryGet(_bottle,out var bottle),Is.True);Assert.That(bottle.Composition.GetQuantity(1),Is.EqualTo(30));Assert.That(bottle.Composition.GetQuantity(3),Is.EqualTo(20));Assert.That(_shop.ReadOnly.Currency,Is.EqualTo(114));
        }
        [UnityTest]public IEnumerator InsufficientMoneyShowsFailureAndLeavesInventory()
        {
            _ui.SelectOffer("material.1");Field<InputField>("_quantity").text="100";int count=_inventory.Profile.Items.Count;
            Click("_buyButton");yield return null;Assert.That(_ui.Status,Does.Contain("余额不足"));Assert.That(_shop.ReadOnly.Currency,Is.EqualTo(100));Assert.That(_inventory.Profile.Items.Count,Is.EqualTo(count));
        }
        [UnityTest]public IEnumerator ClosingShopDoesNotReleaseAnotherPauseReason()
        {
            _pause.RequestPause(RunPauseReason.PauseMenu);_ui.Close();yield return null;
            Assert.That(_pause.HasReason(RunPauseReason.Shop),Is.False);Assert.That(_pause.HasReason(RunPauseReason.PauseMenu),Is.True);Assert.That(Time.timeScale,Is.Zero);
            _pause.ReleasePause(RunPauseReason.PauseMenu);Assert.That(Time.timeScale,Is.EqualTo(1));
        }
        [UnityTest]public IEnumerator UiSaleAndReopenDoNotRefreshBudget()
        {
            _ui.SelectOwned(_card);Click("_sellItemButton");yield return null;long budget=_shop.ReadOnly.RemainingBudget;
            _ui.Close();_ui.Open();yield return null;Assert.That(_shop.ReadOnly.RemainingBudget,Is.EqualTo(budget));Assert.That(budget,Is.EqualTo(94));
        }
        [UnityTest]public IEnumerator TransactionDoesNotResetExistingWandManaOrCooldown()
        {
            Assert.That(_inventory.WandsReadOnly.TryGetSlot(0,out var initial),Is.True);
            Assert.That(_inventory.WandsReadOnly.TryGetProgram(initial.InstanceId,out var program),Is.True);
            // 根施法需要当前程序版本；旧版本请求被拒绝不能用来证明发生过Mana消费。
            EventBus<WandResourceRequestedEvent>.Publish(new WandResourceRequestedEvent{InventoryId=_inventory.ReadOnly.InventoryId,CallerId="shop-regression",RequestId=1,SourceWandId=initial.InstanceId,ProgramRevision=program.ProgramRevision,ManaCost=10,IsRootCast=true});
            Assert.That(_inventory.WandsReadOnly.TryGetSlot(0,out var before),Is.True);Assert.That(before.CurrentMana,Is.LessThan(initial.CurrentMana));
            _ui.SelectOffer("wandtest.fire");Click("_buyButton");yield return null;
            Assert.That(_inventory.WandsReadOnly.TryGetSlot(0,out var after),Is.True);Assert.That(after.CurrentMana,Is.EqualTo(before.CurrentMana));Assert.That(after.CooldownRemaining,Is.EqualTo(before.CooldownRemaining));
        }
        [UnityTest]public IEnumerator StockAndWalletSurviveDepartureAndFailure()
        {
            _ui.SelectOffer("wandtest.fire");Click("_buyButton");yield return null;long budget=_shop.ReadOnly.RemainingBudget;
            _ui.Close();Assert.That(_run.Depart(),Is.True);for(int i=0;i<120 && _run.State.Phase!=RaidPhase.Exploring;i++)yield return null;
            Assert.That(_run.State.Phase,Is.EqualTo(RaidPhase.Exploring));
            EventBus<RaidDepartureRequestedEvent>.Publish(new RaidDepartureRequestedEvent{InventoryId=_inventory.ReadOnly.InventoryId,Command=RaidCommand.Abandon});
            for(int i=0;i<120 && _run.State.Phase!=RaidPhase.HubReady;i++)yield return null;
            Assert.That(_run.State.Phase,Is.EqualTo(RaidPhase.HubReady));Assert.That(_shop.ReadOnly.Currency,Is.EqualTo(80));Assert.That(_shop.ReadOnly.RemainingBudget,Is.EqualTo(budget));Assert.That(_inventory.Profile.AdventureProgress,Is.Zero);
        }
        [UnityTest]public IEnumerator ShopLayoutContainsActionsWithinScreenAndCapturesActualUi()
        {
            Canvas.ForceUpdateCanvases();var corners=new Vector3[4];string[] buttons={"_buyButton","_sellItemButton","_sellMaterialButton","_closeButton"};
            for(int i=0;i<buttons.Length;i++){Field<Button>(buttons[i]).GetComponent<RectTransform>().GetWorldCorners(corners);
                for(int j=0;j<4;j++){Assert.That(corners[j].x,Is.InRange(0f,(float)Screen.width));Assert.That(corners[j].y,Is.InRange(0f,(float)Screen.height));}}
            System.IO.Directory.CreateDirectory("TestResults/SinglePlayer/M5-T1");ScreenCapture.CaptureScreenshot("TestResults/SinglePlayer/M5-T1/shop-layout.png");
            yield return null;yield return null;
        }
        [UnityTest]public IEnumerator RealCollectionAndZeroDropEnemyThenSuccessfulExitRefreshShopOnce()
        {
            _ui.SelectOffer("wandtest.fire");Click("_buyButton");yield return null;
            var player=Find<WizardController>(_scene);_ui.Close();Assert.That(_run.Depart(),Is.True);
            float until=Time.realtimeSinceStartup+8;while(_run.State.Phase!=RaidPhase.Exploring && Time.realtimeSinceStartup<until)yield return null;
            Assert.That(_run.State.Phase,Is.EqualTo(RaidPhase.Exploring));var world=SceneManager.GetSceneByPath(_run.Definition.WorldScenePath);
            var supply=Find<ContainerSandboxMaterialAuthoring>(world);Assert.That(supply,Is.Not.Null);yield return null;
            MovePlayer(player,supply.MixedSamplePosition+new Vector3(0,.5f,-.5f));
            var collection=player.GetComponent<MaterialCollectionController>();collection.SubmitSample(supply.MixedSamplePosition,false,10);
            Assert.That(collection.LastFailure,Is.EqualTo(MaterialTransferFailure.None));Assert.That(_run.Extraction.Conditions.Collected,Is.EqualTo(10));
            var enemy=Find<SpellLootDropAdapter>(world);var table=ScriptableObject.CreateInstance<SpellLootTable>();table.DropChance=0;enemy.Table=table;
            enemy.GetComponent<HealthComponent>().ReceiveHit(new DamageRequest(777,1,10000,DamageType.True,enemy.transform.position,Vector3.forward));
            Assert.That(_run.DefeatedEnemyCount,Is.EqualTo(1),"零掉落的真实死亡仍记录进展。");UnityEngine.Object.Destroy(table);
            MovePlayer(player,_run.Definition.ExitPosition);yield return null;yield return null;
            EventBus<ExtractionRequestedEvent>.Publish(new ExtractionRequestedEvent{RaidId=_run.State.RaidId,Start=true});
            until=Time.realtimeSinceStartup+10;while(_run.State.Phase!=RaidPhase.HubReady && Time.realtimeSinceStartup<until)yield return null;
            Assert.That(_run.State.Phase,Is.EqualTo(RaidPhase.HubReady));Assert.That(_run.LastResult.Outcome,Is.EqualTo(RaidOutcome.Success));
            Assert.That(_inventory.Profile.AdventureProgress,Is.EqualTo(1));Assert.That(_inventory.Profile.Shop.AppliedProgress,Is.EqualTo(1));Assert.That(_inventory.Profile.Shop.Find("wandtest.fire").Remaining,Is.EqualTo(5));
            Assert.That(_shop.ReadOnly.Currency,Is.EqualTo(80));Assert.That(_repository.TryLoad(out var saved).Success,Is.True);Assert.That(saved.AdventureProgress,Is.EqualTo(1));
            long generation=_inventory.SavedGeneration;_ui.Open();_ui.Close();_ui.Open();Assert.That(_inventory.SavedGeneration,Is.EqualTo(generation));
        }
        private static void MovePlayer(WizardController player,Vector3 position)
        {bool enabled=player.CharacterController.enabled;player.CharacterController.enabled=false;player.transform.position=position;player.CharacterController.enabled=enabled;}
        [UnityTest]public IEnumerator StableOpenShopHasNoAllocationsInsideNewUpdateMarkers()
        {
            bool recording=UnityEditorInternal.ProfilerDriver.enabled;
            bool cpu=UnityEditorInternal.ProfilerDriver.IsAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU);
            try
            {
                // 只测已初始化且无交易的稳定帧；同步序列化/保存属于离散输入，不能据此声称没有成本。
                UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,true);
                UnityEditorInternal.ProfilerDriver.enabled=true;
                for(int i=0;i<5;i++)yield return null;
                int first=UnityEditorInternal.ProfilerDriver.lastFrameIndex+1;
                for(int i=0;i<60;i++)yield return null;
                var report=new ShopProfile{Utc=DateTime.UtcNow.ToString("O"),Scope="Editor/TestRunner, initialized Shop open and paused, no transactions; new Update CPU scopes only",FirstFrame=first,LastFrame=UnityEditorInternal.ProfilerDriver.lastFrameIndex,
                    Markers=new[]{new ShopMarker{Name="Norn.Shop.SessionUpdate"},new ShopMarker{Name="Norn.Shop.UiUpdate"}}};
                for(int frame=Math.Max(first,UnityEditorInternal.ProfilerDriver.firstFrameIndex);frame<=report.LastFrame;frame++)
                {
                    using var data=UnityEditorInternal.ProfilerDriver.GetRawFrameDataView(frame,0);
                    if(!data.valid || data.sampleCount==0)continue;
                    report.ValidFrames++;report.AverageMainThreadMs+=data.GetSampleTimeMs(0);
                    for(int sample=0;sample<data.sampleCount;sample++)
                    {
                        string name=data.GetSampleName(sample);
                        if(name=="GC.Alloc" && data.GetSampleMetadataCount(sample)>0)report.MainThreadGcBytes+=data.GetSampleMetadataAsLong(sample,0);
                        for(int m=0;m<report.Markers.Length;m++)if(name==report.Markers[m].Name)
                        {
                            var marker=report.Markers[m];double start=data.GetSampleStartTimeMs(sample),duration=data.GetSampleTimeMs(sample);
                            marker.Samples++;marker.MaximumMs=Math.Max(marker.MaximumMs,duration);
                            for(int child=sample+1;child<data.sampleCount;child++)
                            {
                                double time=data.GetSampleStartTimeMs(child);if(time>start+duration)break;
                                if(time>=start && data.GetSampleName(child)=="GC.Alloc" && data.GetSampleMetadataCount(child)>0)marker.GcBytes+=data.GetSampleMetadataAsLong(child,0);
                            }
                        }
                    }
                }
                if(report.ValidFrames>0)report.AverageMainThreadMs/=report.ValidFrames;
                System.IO.File.WriteAllText("TestResults/SinglePlayer/M5-T1/shop-profiler.json",JsonUtility.ToJson(report,true));
                UnityEditorInternal.ProfilerDriver.SaveProfile("TestResults/SinglePlayer/M5-T1/shop-profiler.data");
                Assert.That(report.ValidFrames,Is.GreaterThanOrEqualTo(30));
                foreach(var marker in report.Markers){Assert.That(marker.Samples,Is.GreaterThan(0),marker.Name);Assert.That(marker.GcBytes,Is.Zero,marker.Name);}
            }
            finally{UnityEditorInternal.ProfilerDriver.enabled=recording;UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,cpu);}
        }
        [Serializable]private sealed class ShopMarker{public string Name;public int Samples;public double MaximumMs;public long GcBytes;}
        [Serializable]private sealed class ShopProfile
        {public string Utc,Scope;public int FirstFrame,LastFrame,ValidFrames;public double AverageMainThreadMs;public long MainThreadGcBytes;public ShopMarker[] Markers;}
        [UnityTest]public IEnumerator KeyboardBUsesInputSystemToCloseAndReopenShop()
        {
            var keyboard=InputSystem.AddDevice<Keyboard>();
            try
            {InputSystem.QueueStateEvent(keyboard,new KeyboardState(Key.B));InputSystem.Update();yield return null;Assert.That(_ui.IsOpen,Is.False);
                InputSystem.QueueStateEvent(keyboard,new KeyboardState());InputSystem.Update();yield return null;
                InputSystem.QueueStateEvent(keyboard,new KeyboardState(Key.B));InputSystem.Update();yield return null;Assert.That(_ui.IsOpen,Is.True);Assert.That(_pause.HasReason(RunPauseReason.Shop),Is.True);}
            finally{InputSystem.RemoveDevice(keyboard);}
        }
        [UnityTearDown]public IEnumerator Teardown()
        {
            try
            {
            SceneManager.sceneLoaded-=Loaded;if(_ui!=null)_ui.Close();
            if(_scene.IsValid() && _scene.isLoaded)yield return SceneManager.UnloadSceneAsync(_scene);
            Time.timeScale=1;Application.runInBackground=_background;yield return null;
            EditorWindow.GetWindow(Type.GetType("UnityEditor.GameView,UnityEditor")).Focus();
            }
            finally {_input?.TearDown();_input=null;}
        }
        private sealed class PlayMemoryFiles:IProfileFileSystem
        {
            private readonly System.Collections.Generic.Dictionary<string,string> _data=new System.Collections.Generic.Dictionary<string,string>();
            public bool Exists(string p)=>_data.ContainsKey(p);public string Read(string p)=>_data[p];public void WriteFlushed(string p,string t)=>_data[p]=t;
            public void Move(string a,string b){_data[b]=_data[a];_data.Remove(a);}public void Replace(string a,string b,string backup){_data[backup]=_data[b];Move(a,b);}
        }
    }
}
#endif
