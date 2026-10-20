#if UNITY_EDITOR
using System.Collections;
using Game.Core;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using Game.Character;
using Game.Skills;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor.SceneManagement;
using UnityEngine.InputSystem.LowLevel;
using Object=UnityEngine.Object;

namespace Game.Run.Tests
{
    /// <summary>复用真实背包的内存档与InputSystem设备，不触碰正式Profile。</summary>
    public sealed class HubSpellResearchPlayModeTests
    {
        // 全部用例验证正式Hub接线；旧Review副本退役后不能继续充当隐藏的第二套运行入口。
        private const string ReviewPath="Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity";
        private const string EvidencePath="Temp/Spells/HubEvidence";
        private const string Data="Assets/_Project/ScriptableObjects/Spell/";
        private Scene Scene;private SinglePlayerInventorySession Session;private SinglePlayerInventoryController Backpack;private WizardController Player;
        private InputTestFixture _input;private Keyboard _keyboard;private Mouse _mouse;private bool _background;
        [UnitySetUp]public IEnumerator Setup()
        {
            _background=Application.runInBackground;Application.runInBackground=true;Time.timeScale=1;_input=new InputTestFixture();_input.Setup();_keyboard=InputSystem.AddDevice<Keyboard>();_mouse=InputSystem.AddDevice<Mouse>();
            EditorWindow.GetWindow(Type.GetType("UnityEditor.GameView,UnityEditor")).Focus();SceneManager.sceneLoaded+=Loaded;
            Scene=EditorSceneManager.LoadSceneInPlayMode(ReviewPath,new LoadSceneParameters(LoadSceneMode.Single));yield return null;yield return null;yield return null;SceneManager.sceneLoaded-=Loaded;
        }
        private void Loaded(Scene scene,LoadSceneMode mode)
        {
            if(scene.path!=ReviewPath)return;Scene=scene;Session=Find<SinglePlayerInventorySession>();Player=Find<WizardController>();
            foreach(var root in scene.GetRootGameObjects())foreach(var ui in root.GetComponentsInChildren<SinglePlayerInventoryController>(true))if(new SerializedObject(ui).FindProperty("_backpackWorkspace").objectReferenceValue!=null)Backpack=ui;
            var catalog=(ItemCatalog)new SerializedObject(Session).FindProperty("_catalog").objectReferenceValue;Assert.That(catalog.TryCreateIndex(out var index,out var error),Is.True,error);
            var store=new ItemOwnershipStore(index);
            void Add(string id,string content,ItemLocation location,int uses=0,bool unlimited=false,WandStats? stats=null)
            {Assert.That(store.TryAdd(new RuntimeItemState(id,content,uses,unlimited,rolledWandStats:stats),location),Is.True,id);}
            var bag=AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/Items/Backpacks/Item_Backpack_Bag.asset");Add("bag",bag.ContentId,new ItemLocation(ItemLocationKind.EquippedBackpack,"player",0,0,0,0));
            bool complex=TestContext.CurrentContext.Test.Name.Contains("ResearchComplex");
            for(int i=0;i<3;i++){var wand=AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/Spell/Wands/Items/Item_Wand_Starter"+(char)('A'+i)+".asset");Assert.That(wand,Is.Not.Null);Add("w"+i,wand.ContentId,new ItemLocation(ItemLocationKind.EquippedWand,"player",0,0,0,i),stats:new WandStats(complex?1000:100,10,.15f,32,1,.7f,0,complex&&TestContext.CurrentContext.Test.Name.Contains("Shuffle")&&i==0,complex&&i==2?new[]{"norn.v2.reload_faster"}:null));}
            Add("fire",AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/Spell/Items/Item_Spell_Emit_Fireball.asset").ContentId,new ItemLocation(ItemLocationKind.Backpack,"bag",0,0,0,-1),8);
            Add("ice",AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/Spell/Items/Item_Spell_Emit_Iceball.asset").ContentId,new ItemLocation(ItemLocationKind.Backpack,"bag",1,0,0,-1),8);
            Assert.That(Session.Initialize(new SinglePlayerProfile("player",store,currency:248),new ProfileSaveRepository("V2ResearchMemory",index,new MemoryFiles())),Is.True);
        }
        private T Find<T>()where T:Component
        {foreach(var root in Scene.GetRootGameObjects()){var found=root.GetComponentInChildren<T>(true);if(found!=null)return found;}return null;}
        private void Keys(params Key[] keys){InputSystem.QueueStateEvent(_keyboard,new KeyboardState(keys));InputSystem.Update();}
        private void Click(bool down){InputSystem.QueueStateEvent(_mouse,new MouseState{buttons=down?(ushort)1:(ushort)0});InputSystem.Update();}
        private void Capture(string name)
        {var texture=new Texture2D(Screen.width,Screen.height,TextureFormat.RGB24,false);try{Directory.CreateDirectory(EvidencePath);texture.ReadPixels(new Rect(0,0,Screen.width,Screen.height),0,0);texture.Apply();File.WriteAllBytes(EvidencePath+"/"+name,texture.EncodeToPNG());}finally{Object.Destroy(texture);}}
        [UnityTearDown]public IEnumerator Cleanup()
        {
            SceneManager.sceneLoaded-=Loaded;Time.timeScale=1;if(Scene.IsValid()&&Scene.isLoaded){if(SceneManager.sceneCount==1)SceneManager.CreateScene("V2ResearchCleanup");yield return SceneManager.UnloadSceneAsync(Scene);}
            if(HubPracticeSession.Current!=null)Object.Destroy(HubPracticeSession.Current.gameObject);_input?.TearDown();_input=null;Application.runInBackground=_background;Time.timeScale=1;yield return null;
            if(HomeSceneTransfer.Current!=null)Object.Destroy(HomeSceneTransfer.Current.gameObject);
            // P7的Run Root使用DontDestroyOnLoad；只卸载Scene不会释放它，否则会污染后面的Portal用例。
            yield return null;
        }
        private sealed class MemoryFiles:IProfileFileSystem
        {
            private readonly Dictionary<string,string> _files=new Dictionary<string,string>();public bool Exists(string p)=>_files.ContainsKey(p);public string Read(string p)=>_files[p];
            public void WriteFlushed(string p,string text){_files[p]=text;}public void Move(string a,string b){_files[b]=_files[a];_files.Remove(a);}public void Replace(string a,string b,string backup){_files[backup]=_files[b];Move(a,b);}
        }
        private HubAreaVolume Area => (HubAreaVolume)new SerializedObject(Find<HubInteractionController>()).FindProperty("_researchArea").objectReferenceValue;
        [UnityTest]public IEnumerator FormalHubResearchFAndBackpackRemainIndependent()
        {
            Move(Area.Center);yield return new WaitForSecondsRealtime(1);yield return new WaitForEndOfFrame();Capture("formal-hub-region-player.png");
            Keys(Key.F);yield return null;Keys();var research=Find<WandResearchController>();Assert.That(research.IsOpen,Is.True);Assert.That(Backpack.IsOpen,Is.False);Assert.That(Session.UseSpellProgrammingV2,Is.True);
            Keys(Key.Escape);yield return null;Keys();Assert.That(research.IsOpen,Is.False);Assert.That(Time.timeScale,Is.EqualTo(1));
            Keys(Key.Tab);yield return null;Keys();Assert.That(Backpack.IsOpen,Is.True);Assert.That(research.IsOpen,Is.False);Backpack.Close();
        }
        private void Move(Vector3 point)
        {Player.enabled=false;Player.CharacterController.enabled=false;Player.transform.position=point;Player.CharacterController.enabled=true;Physics.SyncTransforms();}
        private void AssertPortalInputReady(IPortalInteractionTarget gate)
        {
            var input=Player.GetComponent<PortalInteractInput>();
            bool focused=(bool)typeof(PortalInteractInput).GetField("_focused",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(input);
            bool wait=(bool)typeof(PortalInteractInput).GetField("_waitRelease",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(input);
            var stationInput=Player.GetComponent<HubInteractInput>();
            bool station=stationInput!=null && (bool)typeof(HubInteractInput).GetProperty("HasStation",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(stationInput);
            Assert.That(gate.CanInteract(Player.CharacterController)&&focused&&!wait&&!station,Is.True,
                "F入口状态：门资格="+gate.CanInteract(Player.CharacterController)+" focused="+focused+" waitRelease="+wait+" station="+station+" position="+Player.transform.position+" gate="+gate.PortalTransform.position);
        }
        [UnityTest] public IEnumerator ResearchOwnsPauseAndEditsActualItemsWithoutChangingForecastResources()
        {
            var hub=Find<HubInteractionController>();var research=Find<WandResearchController>();
            Move(Area.Center);Assert.That(hub.CanResearch,Is.True);Keys(Key.F);yield return null;Keys();
            Assert.That(research.IsOpen,Is.True);Assert.That(Time.timeScale,Is.Zero);
            Session.TryGetWandExecution("w0",out var before);uint version=Session.ReadOnly.Version;
            research.Presenter.Seek(3);research.Presenter.Seek(0);
            Session.TryGetWandExecution("w0",out var after);Assert.That(after.Draw,Is.SameAs(before.Draw));Assert.That(Session.ReadOnly.Version,Is.EqualTo(version));
            WandSpellCellView source=null,target=null;
            foreach(var cell in research.GetComponentsInChildren<WandSpellCellView>(true))
            {if(cell.Role==WandSpellCellRole.Library && cell.InstanceId=="fire")source=cell;if(cell.Role==WandSpellCellRole.Sequence && cell.SourceSlot==0)target=cell;}
            Assert.That(source,Is.Not.Null);Assert.That(target,Is.Not.Null);
            var pointer=new PointerEventData(EventSystem.current){position=RectTransformUtility.WorldToScreenPoint(null,source.Rect.position),button=PointerEventData.InputButton.Left};
            ExecuteEvents.Execute(source.gameObject,pointer,ExecuteEvents.beginDragHandler);pointer.position=RectTransformUtility.WorldToScreenPoint(null,target.Rect.position);
            ExecuteEvents.Execute(source.gameObject,pointer,ExecuteEvents.dragHandler);ExecuteEvents.Execute(target.gameObject,pointer,ExecuteEvents.dropHandler);ExecuteEvents.Execute(source.gameObject,pointer,ExecuteEvents.endDragHandler);yield return null;
            Assert.That(Session.ReadOnly.TryGetView("fire",out var fire),Is.True);Assert.That(fire.Location.Kind,Is.EqualTo(ItemLocationKind.WandSlot));Assert.That(fire.Location.OwnerId,Is.EqualTo("w0"));
            Assert.That(research.Presenter.Program.Count,Is.EqualTo(1));
            Assert.That(research.Presenter.LibraryProgram.Instructions,Has.None.Matches<SpellInstruction>(s=>s.InstanceId=="fire"),"已安装实例必须从右侧库消失。");
            yield return new WaitForEndOfFrame();System.IO.Directory.CreateDirectory(EvidencePath);Capture("research-owned.png");
            research.Remove("fire");yield return null;Session.ReadOnly.TryGetView("fire",out fire);Assert.That(fire.Location.Kind,Is.EqualTo(ItemLocationKind.SpellStorage));
            Assert.That(research.Presenter.LibraryProgram.Instructions,Has.Some.Matches<SpellInstruction>(s=>s.InstanceId=="fire"),"拆卸后同一个实例返回法术库。");
            Keys(Key.Tab);yield return null;Keys();Assert.That(Backpack.IsOpen,Is.True);Assert.That(research.IsOpen,Is.False);Assert.That(Time.timeScale,Is.Zero);
            Backpack.Close();Assert.That(Time.timeScale,Is.EqualTo(1));
        }
        [UnityTest] public IEnumerator ResearchRejectsForgeryDisabledAreaAndAwayPhaseAndClosesOutside()
        {
            var hub=Find<HubInteractionController>();var research=Find<WandResearchController>();Move(Area.Center);
            Assert.That(research.Open(new HubInteractionResultEvent{InventoryId=Session.ReadOnly.InventoryId,Kind=HubInteractionKind.SpellResearch,Success=true,CallerId="forged",RequestId=1}),Is.False);
            Area.enabled=false;hub.Request(HubInteractionKind.SpellResearch);Assert.That(research.IsOpen,Is.False);Area.enabled=true;
            hub.Request(HubInteractionKind.SpellResearch);Assert.That(research.IsOpen,Is.True);
            Move(Area.Center+Vector3.right*20);yield return null;Assert.That(research.IsOpen,Is.False);Assert.That(Time.timeScale,Is.EqualTo(1));
            Move(Area.Center);Area.transform.rotation=Quaternion.Euler(0,25,0);Area.transform.localScale=new Vector3(1.2f,1,1.3f);
            Assert.That(hub.CanResearch,Is.True);Area.Volume.enabled=false;Assert.That(hub.CanResearch,Is.False);
        }
        [UnityTest]public IEnumerator ResearchRejectsMissingAreaFrozenTransitionAndReplayedGrant()
        {
            var hub=Find<HubInteractionController>();var research=Find<WandResearchController>();var area=Area;Move(area.Center);
            Session.Profile.RaidActionsFrozen=true;Assert.That(hub.CanResearch,Is.False);Session.Profile.RaidActionsFrozen=false;
            var pause=Find<RunPauseCoordinator>();pause.RequestPause(RunPauseReason.SceneTransition);Assert.That(hub.CanResearch,Is.False);pause.ReleasePause(RunPauseReason.SceneTransition);
            var data=new SerializedObject(hub);data.FindProperty("_researchArea").objectReferenceValue=null;data.ApplyModifiedPropertiesWithoutUndo();Assert.That(hub.CanResearch,Is.False);
            data.FindProperty("_researchArea").objectReferenceValue=area;data.ApplyModifiedPropertiesWithoutUndo();
            HubInteractionResultEvent grant=default;void Granted(HubInteractionResultEvent e){if(e.Kind==HubInteractionKind.SpellResearch)grant=e;}
            EventBus<HubInteractionResultEvent>.Subscribe(Granted);hub.Request(HubInteractionKind.SpellResearch);EventBus<HubInteractionResultEvent>.Unsubscribe(Granted);
            Assert.That(research.IsOpen,Is.True);research.Close();Assert.That(research.Open(grant),Is.False);
            Move(area.transform.TransformPoint(area.Volume.center+Vector3.right*area.Volume.size.x*.5f));Assert.That(hub.CanResearch,Is.False);
            yield return null;
        }
        [UnityTest]public IEnumerator UnconsumedOldGrantExpiresAfterANewerFailedRequest()
        {
            var hub=Find<HubInteractionController>();var research=Find<WandResearchController>();Move(Area.Center);research.enabled=false;
            HubInteractionResultEvent grant=default;void CaptureGrant(HubInteractionResultEvent e){if(e.Kind==HubInteractionKind.SpellResearch && e.Success)grant=e;}
            EventBus<HubInteractionResultEvent>.Subscribe(CaptureGrant);hub.Request(HubInteractionKind.SpellResearch);EventBus<HubInteractionResultEvent>.Unsubscribe(CaptureGrant);
            Assert.That(grant.Success,Is.True);Move(Area.Center+Vector3.right*30);hub.Request(HubInteractionKind.SpellResearch);
            Move(Area.Center);research.enabled=true;Assert.That(research.Open(grant),Is.False,"旧成功事件未消费，也不能跨越更新的失败请求重新授权。");
            yield return null;
        }
        [UnityTest]public IEnumerator ResearchToP7CastAndReturnKeepsV2ResourcesAndActualReplay()
        {
            var hub=Find<HubInteractionController>();var research=Find<WandResearchController>();Move(Area.Center);hub.Request(HubInteractionKind.SpellResearch);
            research.Install("fire",0);research.Close();
            HubPracticePortal gate=null;foreach(var root in Scene.GetRootGameObjects())foreach(var candidate in root.GetComponentsInChildren<HubPracticePortal>(true))if(candidate.ScenePath!=null && candidate.ScenePath.Contains("P7"))gate=candidate;
            Assert.That(gate,Is.Not.Null);string destination=gate.ScenePath;Move(gate.transform.position);Keys(Key.F);yield return null;Keys();
            yield return WaitFor(destination);Find<GameplayGuideController>()?.Close();yield return null;
            Assert.That(Session.UseSpellProgrammingV2,Is.True);Assert.That(Find<WandResearchController>(),Is.Null,"副本不提供详细研究台");
            var adapter=Find<OwnedWandCastAdapter>();adapter.Select(0);
            Assert.That(adapter.TryCapture(out _,out _),Is.True,"副本绑定必须使用同一Owned来源");Assert.That(Session.WorldActionsAllowed,Is.True);
            // 刚在研究台改卡的重编程等待会随实例过图，不能因转场就要求马上能发。
            Session.TryGetWandExecution("w0",out var arrival);yield return new WaitForSeconds(Mathf.Max(arrival.CastRemaining,arrival.ReloadRemaining)+.1f);
            Click(true);yield return new WaitForSeconds(.4f);Click(false);
            Assert.That(Session.ExecutionReplays.TryGetLatest("w0",out var replay),Is.True);Assert.That(replay.WorldStatus,Is.EqualTo(WandWorldExecutionStatus.Released));
            string ticket=replay.TicketId;Session.TryGetWandExecution("w0",out var before);gate=Find<HubPracticePortal>();Move(gate.transform.position);
            // 测试瞬移不经过普通移动帧；让真实交互目标缓存/松手保护在F之前完成一次更新。
            yield return null;yield return new WaitForEndOfFrame();AssertPortalInputReady(gate);Keys(Key.F);yield return null;Keys();
            yield return WaitFor(ReviewPath);Assert.That(Session.ExecutionReplays.TryGetLatest("w0",out replay),Is.True);Assert.That(replay.TicketId,Is.EqualTo(ticket));
            Session.TryGetWandExecution("w0",out var after);Assert.That(after.Draw.ExecutionRevision,Is.EqualTo(before.Draw.ExecutionRevision));
            research=Find<WandResearchController>();Move(Area.Center);Find<HubInteractionController>().Request(HubInteractionKind.SpellResearch);Assert.That(research.IsOpen,Is.True);research.Presenter.ShowReplay();
            Assert.That(research.Presenter.IsHistory,Is.True);Assert.That(research.Presenter.RecordedReleased[0],Is.True);
            yield return new WaitForSecondsRealtime(1);yield return new WaitForEndOfFrame();Capture("research-return-replay.png");research.Close();
        }
        [UnityTest]public IEnumerator ResearchComplexScopeCopyWrapAndFixedCardsUseSameGrid()
        {
            var multi=AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/Spell/Items/Item_Spell_Multicast_DoubleShotAlias.asset");
            var modifier=AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/Spell/Items/Item_Spell_Modify_DamageUpAlias.asset");
            var trigger=AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/Spell/Items/Item_Spell_Emit_TriggerFireball.asset");
            Assert.That(multi,Is.Not.Null);Assert.That(trigger,Is.Not.Null);Assert.That(modifier,Is.Not.Null);
            var fire=AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/Spell/Items/Item_Spell_Emit_Fireball.asset");var ice=AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/Spell/Items/Item_Spell_Emit_Iceball.asset");
            var sequence=new[]{multi,trigger,multi,fire,modifier,ice,fire};
            void Install(string id,string content,string wand,int slot)
            {Assert.That(Session.Profile.Items.TryAdd(new RuntimeItemState(id,content,0,true),new ItemLocation(ItemLocationKind.WandSlot,wand,0,0,0,slot)),Is.True);}
            for(int i=0;i<sequence.Length;i++)Install("scope"+i,sequence[i].ContentId,"w0",i);
            Install("copy","norn.v2.gamma","w1",0);Install("borrowed",fire.ContentId,"w1",1);
            Install("wrap-fire",fire.ContentId,"w2",0);Install("wrap-mod",modifier.ContentId,"w2",1);
            Move(Area.Center);var research=Find<WandResearchController>();Find<HubInteractionController>().Request(HubInteractionKind.SpellResearch);var p=research.Presenter;
            Assert.That(p.CurrentRoot.Graph.Scopes.Count,Is.EqualTo(2));Assert.That(p.CurrentRoot.Graph.Outputs.Count,Is.EqualTo(4));p.SelectCard(4);Assert.That(p.HighlightedOutputs.Count,Is.EqualTo(2));
            var viewport=research.GetComponentInChildren<WandProgramCanvasViewport>();viewport.FitAll();yield return new WaitForEndOfFrame();Capture("research-complex-scope.png");
            research.SelectWand(1);p.SelectCard(0);Assert.That(p.HighlightedOutputs.Count,Is.EqualTo(1));Assert.That(p.CurrentRoot.After.Deck,Is.EqualTo(new[]{1}));
            yield return new WaitForEndOfFrame();Capture("research-complex-copy.png");
            research.SelectWand(2);Assert.That(p.Program.IntrinsicCount,Is.EqualTo(1));p.SetCycleMode(true);Assert.That(p.RootCount,Is.GreaterThanOrEqualTo(2));p.NextRoot();
            Assert.That(p.CurrentRoot.Summary.NeedsReload,Is.True);Assert.That(p.CurrentRoot.Trace.Steps,Has.Some.Matches<SpellExecutionTraceStep>(s=>s.Kind==SpellTraceKind.WrapRecovered));
            yield return new WaitForEndOfFrame();Capture("research-complex-wrap-fixed.png");research.Close();
        }
        [UnityTest]public IEnumerator ResearchComplexLimitShowsBriefVisibleReason()
        {
            for(int i=0;i<17;i++)Assert.That(Session.Profile.Items.TryAdd(new RuntimeItemState("deep"+i,"norn.v2.termination",0,true),new ItemLocation(ItemLocationKind.WandSlot,"w0",0,0,0,i)),Is.True);
            Move(Area.Center);Find<HubInteractionController>().Request(HubInteractionKind.SpellResearch);var research=Find<WandResearchController>();Assert.That(research.Presenter.CurrentRoot,Is.Null);
            UnityEngine.UI.Text identity=null;foreach(var text in research.GetComponentsInChildren<UnityEngine.UI.Text>())if(text.name=="PreviewIdentity")identity=text;
            Assert.That(identity,Is.Not.Null);Assert.That(identity.text,Does.Contain("复杂"),"技术预算拒绝要有简短可见原因，不能只留空白预览。");
            yield return null;research.Close();
        }
        [UnityTest]public IEnumerator ResearchComplexTwoTriggersPreviewMatchesActualP7Root()
        {
            string[] contents={"norn.v2.double","wandtest.trigger","practice.spell.b3936f478407732439005a10efdecac7","wandtest.trigger","wandtest.ice"};
            for(int i=0;i<contents.Length;i++)Assert.That(Session.Profile.Items.TryAdd(new RuntimeItemState("dual"+i,contents[i],8),new ItemLocation(ItemLocationKind.WandSlot,"w0",0,0,0,i)),Is.True);
            Move(Area.Center);Find<HubInteractionController>().Request(HubInteractionKind.SpellResearch);
            var research=Find<WandResearchController>();Assert.That(research.Presenter.CurrentRoot.Graph.RootOutputCount,Is.EqualTo(2));research.Close();
            HubPracticePortal gate=null;foreach(var root in Scene.GetRootGameObjects())foreach(var candidate in root.GetComponentsInChildren<HubPracticePortal>(true))if(candidate.ScenePath!=null&&candidate.ScenePath.Contains("P7"))gate=candidate;
            string destination=gate.ScenePath;Move(gate.transform.position);Keys(Key.F);yield return null;Keys();yield return WaitFor(destination);Find<GameplayGuideController>()?.Close();yield return null;
            var adapter=Find<OwnedWandCastAdapter>();adapter.Select(0);Assert.That(adapter.IsV2,Is.True,"所有实际玩家入口必须选择V2，而非只在预览使用V2。");
            Session.TryGetWandExecution("w0",out var state);yield return new WaitForSeconds(Mathf.Max(state.CastRemaining,state.ReloadRemaining)+.1f);
            Click(true);yield return null;Click(false);yield return new WaitForSeconds(1);
            Assert.That(Session.ExecutionReplays.TryGetLatest("w0",out var replay),Is.True);
            Assert.That(replay.Graph.RootOutputCount,Is.EqualTo(2),"根层必须在同一Ticket生成两颗不同载荷的触发弹。");
            foreach(int id in replay.Graph.Scopes[0].OutputIds)Assert.That(replay.ReleasedOutputs[id],Is.True);
            Assert.That(replay.Graph.Scopes.Count,Is.EqualTo(3));
        }
        private IEnumerator TravelHome()
        {
            var gate=Find<HomePortal>();Assert.That(gate,Is.Not.Null);Move(gate.transform.position);
            yield return null;yield return new WaitForEndOfFrame();AssertPortalInputReady(gate);Keys(Key.F);yield return null;Keys();
            yield return WaitFor("Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Home.unity");
        }
        [UnityTest]public IEnumerator ResearchComplexHomeFiniteMulticastMatchesActualGraphAndImpacts()
        {
            string[] contents={"norn.v2.double","wandtest.trigger","practice.spell.b3936f478407732439005a10efdecac7","wandtest.trigger","wandtest.ice"};
            for(int i=0;i<contents.Length;i++)Assert.That(Session.Profile.Items.TryAdd(new RuntimeItemState("dual"+i,contents[i],8),new ItemLocation(ItemLocationKind.WandSlot,"w0",0,0,0,i)),Is.True);
            yield return TravelHome();Assert.That(Session.UseSpellProgrammingV2,Is.True);Assert.That(Session.WorldActionsAllowed,Is.True);
            Session.TryGetWandExecution("w0",out var state);yield return new WaitForSeconds(Mathf.Max(state.CastRemaining,state.ReloadRemaining)+.1f);
            Assert.That(Session.TryCaptureWandResearch("w0",out var preview),Is.True);var buffer=new SpellBuildBuffer();
            Assert.That(new SpellProgramMachine().TryBuild(preview.Execution.Program,preview.Execution.Draw,preview.Inputs,buffer,out _),Is.True);Assert.That(buffer.Graph.RootOutputCount,Is.EqualTo(2));
            var adapter=Find<OwnedWandCastAdapter>();adapter.Select(0);Assert.That(adapter.TryCapture(out var program,out var context),Is.True);
            var wall=new GameObject("双触发真实碰撞验证墙");SceneManager.MoveGameObjectToScene(wall,Scene);wall.transform.position=new Vector3(200,4,205);wall.AddComponent<BoxCollider>().size=new Vector3(10,10,.2f);Physics.SyncTransforms();
            Assert.That(Find<SpellCaster>().CastOwnedProgram(program,context,new Vector3(200,4,200),new Vector3(200,4,230),1,Player.Health.Id,Player.CharacterController),Is.EqualTo(2),"和平试射不能跳过有限资格双重牌而退化成轮流单发。");
            Assert.That(Session.ExecutionReplays.TryGetLatest("w0",out var replay),Is.True);Assert.That(replay.Graph.RootOutputCount,Is.EqualTo(2));
            yield return new WaitForSeconds(1);
            Assert.That(replay.GetLayerStatus(1),Is.EqualTo(WandLayerResourceStatus.Committed));Assert.That(replay.GetLayerStatus(2),Is.EqualTo(WandLayerResourceStatus.Committed));
            foreach(bool released in replay.ReleasedOutputs)Assert.That(released,Is.True,"同一Root两颗父弹及不同子层都必须实际生成。");
            Assert.That(Session.Profile.Items.TryGet("dual0",out var multi),Is.True);Assert.That(multi.RemainingCharges,Is.EqualTo(8),"和平实验检查资格库存，不消耗下一次远征资格。");
        }
        [UnityTest]public IEnumerator HomeResearchAreaOpensWithFAndKeepsDungeonEntryClosed()
        {
            yield return TravelHome();var hub=Find<HubInteractionController>();var research=Find<WandResearchController>();Assert.That(hub,Is.Not.Null);Assert.That(research,Is.Not.Null);
            Move(Area.Center);Keys(Key.F);yield return null;Keys();Assert.That(research.IsOpen,Is.True);Assert.That(Time.timeScale,Is.Zero);Assert.That(research.Presenter.PreviewIdentity,Is.EqualTo("当前推演"));
            yield return new WaitForEndOfFrame();Capture("home-research.png");
            Keys(Key.Escape);yield return null;Keys();Assert.That(research.IsOpen,Is.False);Assert.That(Time.timeScale,Is.EqualTo(1));
            Move(Area.Center+Vector3.right*20);hub.Request(HubInteractionKind.SpellResearch);Assert.That(research.IsOpen,Is.False);
        }
        [UnityTest]public IEnumerator LibraryTracksDistinctInstancesAcrossBothWandsAndSwap()
        {
            Assert.That(Session.Profile.Items.TryAdd(new RuntimeItemState("spare-fire","wandtest.fire",8),new ItemLocation(ItemLocationKind.SpellStorage,"player",0,0,0,0)),Is.True);
            Move(Area.Center);Find<HubInteractionController>().Request(HubInteractionKind.SpellResearch);var research=Find<WandResearchController>();
            Assert.That(research.Presenter.LibraryProgram.Count,Is.EqualTo(3),"相同ContentId的两个实例都必须保留。");
            research.Install("fire",0);research.SelectWand(1);research.Install("spare-fire",0);research.SelectWand(0);
            Assert.That(research.Presenter.LibraryProgram.Count,Is.EqualTo(1),"其它法杖已安装卡也不能列回库。");
            research.Install("ice",0);Assert.That(research.Presenter.Program.Instructions[0].InstanceId,Is.EqualTo("ice"));
            Assert.That(research.Presenter.LibraryProgram.Instructions,Has.Some.Matches<SpellInstruction>(s=>s.InstanceId=="fire"),"替换下来的原卡回到来源库存后必须重新可见。");
            Assert.That(research.Presenter.LibraryProgram.Instructions,Has.None.Matches<SpellInstruction>(s=>s.InstanceId=="spare-fire"));
            yield return null;
        }
        [UnityTest]public IEnumerator PracticeEntryRejectsExhaustedInstalledQualificationWithoutSavingOrLoading()
        {
            Assert.That(Session.Profile.Items.TryAdd(new RuntimeItemState("exhausted-multi","norn.v2.double",0),new ItemLocation(ItemLocationKind.WandSlot,"w0",0,0,0,0)),Is.True);
            HubPracticePortal gate=null;foreach(var root in Scene.GetRootGameObjects())foreach(var candidate in root.GetComponentsInChildren<HubPracticePortal>(true))if(candidate.ScenePath!=null&&candidate.ScenePath.Contains("P7"))gate=candidate;
            Move(gate.transform.position);long generation=Session.SavedGeneration;uint version=Session.ReadOnly.Version;
            Assert.That(gate.TryEnter(Player.CharacterController),Is.False,"不能带着无法取得资格的卡进入再静默跳过；必须在保存/加载前拒绝。");
            Assert.That(HubPracticeSession.Current,Is.Null);Assert.That(Session.SavedGeneration,Is.EqualTo(generation));Assert.That(Session.ReadOnly.Version,Is.EqualTo(version));
            yield return null;
        }
        [UnityTest]public IEnumerator DirectP7PracticeDefaultsToV2WithoutHubTransfer()
        {
            const string path="Assets/_Project/Scenes/P7_DemoRun.unity";
            Scene=EditorSceneManager.LoadSceneInPlayMode(path,new LoadSceneParameters(LoadSceneMode.Single));
            for(int i=0;i<60;i++){yield return null;if(HubPracticeSession.Current!=null&&Find<SinglePlayerInventorySession>()!=null)break;}
            Session=Find<SinglePlayerInventorySession>();Player=Find<WizardController>();Assert.That(Session,Is.Not.Null);
            Assert.That(Session.UseSpellProgrammingV2,Is.True);Assert.That(Find<OwnedWandCastAdapter>().IsV2,Is.True);
            Assert.That(Find<WandResearchController>(),Is.Null,"直接Play也不能在副本获得研究权限。");
            Find<GameplayGuideController>()?.Close();
        }
        [UnityTest]public IEnumerator LegacySessionCannotOfferV2ResearchPreview()
        {
            Move(Area.Center);var data=new SerializedObject(Session);data.FindProperty("_useSpellProgrammingV2").boolValue=false;data.ApplyModifiedPropertiesWithoutUndo();
            var hub=Find<HubInteractionController>();Assert.That(hub.CanResearch,Is.False);hub.Request(HubInteractionKind.SpellResearch);Assert.That(Find<WandResearchController>().IsOpen,Is.False);
            yield return null;
        }
        [UnityTest]public IEnumerator ResearchComplexShuffleHasBriefIdentityAndKeepsInstallationOrder()
        {
            Assert.That(Session.Profile.Items.TryMove("fire",new ItemLocation(ItemLocationKind.WandSlot,"w0",0,0,0,0)).Success,Is.True);
            Assert.That(Session.Profile.Items.TryMove("ice",new ItemLocation(ItemLocationKind.WandSlot,"w0",0,0,0,1)).Success,Is.True);
            Move(Area.Center);Find<HubInteractionController>().Request(HubInteractionKind.SpellResearch);var p=Find<WandResearchController>().Presenter;
            Assert.That(p.PreviewIdentity,Does.Contain("乱序"));Assert.That(p.Program.Instructions[0].InstanceId,Is.EqualTo("fire"));Assert.That(p.Program.Instructions[1].InstanceId,Is.EqualTo("ice"));yield return null;
        }
        private IEnumerator WaitFor(string path)
        {
            double deadline=Time.realtimeSinceStartupAsDouble+45;
            while(Time.realtimeSinceStartupAsDouble<deadline)
            {
                var scene=SceneManager.GetSceneByPath(path);
                if(scene.IsValid()&&scene.isLoaded&&(HubPracticeSession.Current==null||!HubPracticeSession.Current.Loading)&&(HomeSceneTransfer.Current==null||!HomeSceneTransfer.Current.Loading))
                {Scene=scene;Session=Find<SinglePlayerInventorySession>();Player=Find<WizardController>();yield return null;yield return null;yield break;}
                yield return null;
            }
            Assert.Fail("转场未完成："+path);
        }
    }
}
#endif
