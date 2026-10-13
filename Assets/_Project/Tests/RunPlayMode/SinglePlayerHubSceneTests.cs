#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using Game.Character;
using Game.Core;
using Game.ElementField;
using Game.Materials;
using Game.Skills;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Game.Run.Tests
{
    /// <summary>正式美术场景的实际调用链。内存档在Start前注入，绝不读写玩家正式库存。</summary>
    public sealed class SinglePlayerHubSceneTests
    {
        private const string Path = "Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity";
        private const string Evidence = "TestResults/SinglePlayer/M6-T1-Feedback";
        private Scene _scene;
        private SinglePlayerInventorySession _session;
        private SinglePlayerRunCoordinator _run;
        private WizardController _player;
        private ElementWorldRuntime _world;
        private HubInteractionController _hub;
        private SinglePlayerInventoryController _ui;
        private ShopController _shop;
        private string _bottle;
        private bool _background;
        private InputTestFixture _inputFixture;
        private int _cameraErrors, _grabErrors;
        private readonly List<string> _errors = new List<string>();
        [UnitySetUp] public IEnumerator InitializeHub()
        {
            _background = Application.runInBackground; Application.runInBackground = true; Time.timeScale = 1;
            // 本版TestRunner的UnitySetUp包在普通SetUp外层；直接继承会先加载场景，再重置输入。
            // 显式嵌入官方Fixture，保证所有角色/UI Action在隔离Runtime建立后才创建和Enable。
            _inputFixture=new InputTestFixture();_inputFixture.Setup();
            EditorWindow.GetWindow(Type.GetType("UnityEditor.GameView,UnityEditor")).Focus();
            Assert.That(System.IO.File.Exists(HubPath), Is.True); _errors.Clear(); _cameraErrors = _grabErrors = 0;
            Application.logMessageReceived += OnLog; SceneManager.sceneLoaded += Loaded;
            _scene = EditorSceneManager.LoadSceneInPlayMode(HubPath, new LoadSceneParameters(LoadSceneMode.Additive));
            yield return null; yield return null; yield return null; SceneManager.sceneLoaded -= Loaded;
            SceneManager.SetActiveScene(_scene); Assert.That(_session.ReadOnly, Is.Not.Null);
        }

        private void Loaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.path != HubPath) return; _scene = scene;
            _session = Find<SinglePlayerInventorySession>(); _run = Find<SinglePlayerRunCoordinator>(); _player = Find<WizardController>();
            _world = Find<ElementWorldRuntime>(); _hub = Find<HubInteractionController>(); _ui = Find<SinglePlayerInventoryController>(); _shop = Find<ShopController>();
            var data = new SerializedObject(_session);
            var catalog = (ItemCatalog)data.FindProperty("_catalog").objectReferenceValue;
            var seeds = (SinglePlayerStartingInventory)data.FindProperty("_startingInventory").objectReferenceValue;
            Assert.That(catalog.TryCreateIndex(out var index, out string error), Is.True, error);
            Assert.That(seeds.TryCreate(index, out var profile, out error), Is.True, error);
            foreach (var entry in profile.Items) if (entry.Item.Composition != null)
            { _bottle = entry.Item.InstanceId; entry.Item.Composition.AddReserved(new[] { new MaterialAmount(1, 36), new MaterialAmount(3, 24) }, 2); break; }
            Assert.That(_bottle, Is.Not.Null);
            foreach (var entry in profile.Items) if (entry.Location.Kind == ItemLocationKind.EquippedBackpack)
            { Assert.That(profile.Items.TryMove(_bottle, new GridPlacement(0, 0, 0).InBackpack(entry.Item.InstanceId)).Success, Is.True); break; }
            Assert.That(_session.Initialize(profile, new ProfileSaveRepository("Temp/SinglePlayer/FormalHubMemory/" + Guid.NewGuid().ToString("N"), index, new MemoryFiles())), Is.True);
        }

        private T Find<T>() where T : Component
        { foreach (var root in _scene.GetRootGameObjects()) { var component = root.GetComponentInChildren<T>(true); if (component != null) return component; } return null; }
        // 审阅副本验证期间只改SessionState入口；清除后继续覆盖正式Scene，不能永久绕过生产资产。
        private static string HubPath => SessionState.GetString("Norn.HubAlignment.ReviewPath", Path);

        [UnityTest] public IEnumerator WalkFromSpawnToUserShopAndWarehouseWithActualMoveInput()
        {
            yield return new WaitForSeconds(.5f);var keyboard=InputSystem.AddDevice<Keyboard>();
            try
            {
                // 通过主街折线步行，避免“Teleport到点能按E”冒充玩家实际能走到建筑门前。
                yield return Walk(keyboard,new Vector3(-4,0,11),.8f);
                yield return Walk(keyboard,Station(HubStationKind.Shop).Position,1.3f);
                Assert.That(_hub.TryGetStation(out var shop),Is.True);Assert.That(shop.Kind,Is.EqualTo(HubStationKind.Shop));
                InputSystem.QueueStateEvent(keyboard,new KeyboardState(Key.E));InputSystem.Update();yield return null;Assert.That(_shop.IsOpen,Is.True);_shop.Close();
                InputSystem.QueueStateEvent(keyboard,new KeyboardState());InputSystem.Update();yield return null;
                yield return Walk(keyboard,new Vector3(-4,0,11),.8f);
                // 实测南侧WoofFence_07横跨x=-4/-1；x=-8.6是现有约4.4m开口，无需移除围栏或放宽断言。
                yield return Walk(keyboard,new Vector3(-8.6f,0,4),.8f);
                yield return Walk(keyboard,new Vector3(-8.6f,0,-3),.8f);
                yield return Walk(keyboard,Station(HubStationKind.Warehouse).Position,1.3f);
                Assert.That(_hub.TryGetStation(out var warehouse),Is.True);Assert.That(warehouse.Kind,Is.EqualTo(HubStationKind.Warehouse));
                InputSystem.QueueStateEvent(keyboard,new KeyboardState(Key.E));InputSystem.Update();yield return null;Assert.That(_ui.IsOpen,Is.True);Assert.That(_ui.WarehouseMode,Is.True);
            }
            finally{InputSystem.RemoveDevice(keyboard);}
        }
        private IEnumerator Walk(Keyboard keyboard,Vector3 destination,float tolerance)
        {
            float end=Time.realtimeSinceStartup+12;
            while(Time.realtimeSinceStartup<end)
            {
                Vector3 delta=destination-_player.transform.position;delta.y=0;if(delta.magnitude<=tolerance)break;
                Vector3 forward=_player.MainCamera.transform.forward;forward.y=0;forward.Normalize();Vector3 right=Vector3.Cross(Vector3.up,forward);
                float x=Vector3.Dot(delta,right),z=Vector3.Dot(delta,forward);
                // dead zone的对角线长度须小于到达容差，避免紧凑路径停在mask=0却仍断言未到达。
                float threshold=Mathf.Min(.3f,tolerance*.5f);
                int mask=(z>threshold?1:z<-threshold?2:0)|(x>threshold?4:x<-threshold?8:0);
                // 自动测试的虚拟设备可能被Editor失焦重置；持续重申值类型状态，不改生产Input设置/移动算法。
                if(!keyboard.enabled)InputSystem.EnableDevice(keyboard);
                var state=new KeyboardState();state.Set(Key.W,(mask&1)!=0);state.Set(Key.S,(mask&2)!=0);state.Set(Key.D,(mask&4)!=0);state.Set(Key.A,(mask&8)!=0);InputSystem.QueueStateEvent(keyboard,state);InputSystem.Update();
                yield return null;
            }
            Vector2 lastInput=_player.MoveInput;Vector3 lastDirection=_player.MoveDirection;float lastScale=Time.timeScale;
            InputSystem.QueueStateEvent(keyboard,new KeyboardState());InputSystem.Update();yield return null;
            Vector3 remaining=destination-_player.transform.position;remaining.y=0;
            if(remaining.magnitude>tolerance)
            {
                // 失败后一次性保留真实PlayMode附近碰撞证据；Editor的Overlap未命中不能用于排除路径障碍。
                var report=new WalkFailure{Actor=_player.transform.position,Destination=destination,MoveInput=lastInput,MoveDirection=lastDirection,
                    TimeScale=lastScale,Focused=Application.isFocused,KeyboardEnabled=keyboard.enabled,Grounded=_player.GroundChecker.IsGrounded,
                    State=_player.StateMachine.CurrentState?.GetType().Name};
                foreach(var hit in Physics.OverlapSphere(_player.transform.position+Vector3.up*.6f,1.5f,~0,QueryTriggerInteraction.Ignore))
                    report.Nearby.Add(new WalkCollider{Path=AnimationUtility.CalculateTransformPath(hit.transform,null),Bounds=hit.bounds});
                System.IO.Directory.CreateDirectory(Evidence);System.IO.File.WriteAllText(Evidence+"/walk-blockers.json",JsonUtility.ToJson(report,true));
            }
            Assert.That(remaining.magnitude,Is.LessThanOrEqualTo(tolerance),"实际WASD路线被阻挡：目标="+destination+" 玩家="+_player.transform.position);
        }
        [UnityTest] public IEnumerator WalkFromSpawnToWesternExperimentAndThreePortalApproaches()
        {
            var audit=new WalkFailure{Actor=_player.transform.position};
            foreach(var root in _scene.GetRootGameObjects())foreach(var collider in root.GetComponentsInChildren<Collider>())
                if(collider.bounds.center.x>-42 && collider.bounds.center.x<42 && collider.bounds.center.z>-42 && collider.bounds.center.z<42)
                    audit.Nearby.Add(new WalkCollider{Path=AnimationUtility.CalculateTransformPath(collider.transform,null),Bounds=collider.bounds});
            System.IO.Directory.CreateDirectory(Evidence);System.IO.File.WriteAllText(Evidence+"/west-route-colliders.json",JsonUtility.ToJson(audit,true));
            var keyboard=InputSystem.AddDevice<Keyboard>();
            try
            {
                // 围栏口东侧还有袋子/堆箱；该简单输入驱动器没有Pathfinding，采用现有南侧街道路线。
                yield return Walk(keyboard,new Vector3(-8.6f,0,4),.8f);
                yield return Walk(keyboard,new Vector3(-8.6f,0,-3),.8f);
                yield return Walk(keyboard,new Vector3(-25,0,-3),.8f);
                // Bench_A在x=-26.55/z8.69，先从北侧绕过，再对齐平台东台阶。
                yield return Walk(keyboard,new Vector3(-25,0,10.5f),.8f);
                yield return Walk(keyboard,new Vector3(-27.8f,0,10.5f),.4f);
                yield return Walk(keyboard,new Vector3(-27.8f,0,9.5f),.4f);
                yield return Walk(keyboard,new Vector3(-31.5f,0,9.5f),.8f);
                Assert.That(Find<ExperimentAreaController>().IsInside,Is.True);yield return Capture("hub-west-experiment.png");
                yield return Walk(keyboard,new Vector3(-27.8f,0,9.5f),.4f);
                yield return Walk(keyboard,new Vector3(-27.8f,0,10.5f),.4f);
                yield return Walk(keyboard,new Vector3(-25,0,10.5f),.8f);
                yield return Walk(keyboard,new Vector3(-25,0,13),.8f);
                yield return Walk(keyboard,new Vector3(-25,0,30),.8f);
                yield return Walk(keyboard,new Vector3(-30,0,34),.8f);
                yield return Walk(keyboard,new Vector3(-30,0,38),.8f);yield return Capture("hub-northwest-portal.png");
                // MediumTree_1中心(-24.31,33.71)、碰撞半径1.02，北侧36.5的空地可绕过真实树干。
                yield return Walk(keyboard,new Vector3(-30,0,36.5f),.4f);
                yield return Walk(keyboard,new Vector3(8,0,36.5f),.8f);
                yield return Walk(keyboard,new Vector3(8,0,38),.8f);yield return Capture("hub-northeast-portal.png");
                yield return Walk(keyboard,new Vector3(8,0,36.5f),.4f);
                yield return Walk(keyboard,new Vector3(-30,0,36.5f),.8f);
                yield return Walk(keyboard,new Vector3(-25,0,30),.8f);
                yield return Walk(keyboard,new Vector3(-25,0,-5),.8f);
                // 西南木栅栏跨z=-7.24~-3.60；从其北端绕行，再沿地图西侧空地向南。
                yield return Walk(keyboard,new Vector3(-30,0,-5),.8f);
                yield return Walk(keyboard,new Vector3(-30,0,-2.5f),.4f);
                yield return Walk(keyboard,new Vector3(-35,0,-2.5f),.4f);
                yield return Walk(keyboard,new Vector3(-35,0,-30),.8f);
                // 门前2.5m停住，留出按键方向0.3m dead zone，避免路线测试因触发传送而换Scene。
                yield return Walk(keyboard,new Vector3(-35,0,-37.5f),.5f);yield return Capture("hub-southwest-portal.png");
            }
            finally{InputSystem.RemoveDevice(keyboard);}
        }
        [Serializable]private sealed class WalkFailure{public Vector3 Actor,Destination,MoveDirection;public Vector2 MoveInput;public float TimeScale;public bool Focused,KeyboardEnabled,Grounded;public string State;public List<WalkCollider> Nearby=new List<WalkCollider>();}
        [Serializable]private sealed class WalkCollider{public string Path;public Bounds Bounds;}

        [UnityTest] public IEnumerator TabBackpackIsScreenOverlayAndWorksAwayFromStations()
        {
            Move(new Vector3(-9,.2f,4)); yield return null;
            var keyboard=InputSystem.AddDevice<Keyboard>();
            try
            {
                InputSystem.QueueStateEvent(keyboard,new KeyboardState(Key.Tab));InputSystem.Update();yield return null;
                Assert.That(_ui.IsOpen,Is.True);Assert.That(_ui.WarehouseMode,Is.False);Assert.That(Time.timeScale,Is.Zero);
                Assert.That(_ui.GetComponentInParent<Canvas>().renderMode,Is.EqualTo(RenderMode.ScreenSpaceOverlay));
                var warehouse=(RectTransform)new SerializedObject(Find<InventoryWorkspaceView>()).FindProperty("_warehouse").objectReferenceValue;
                Assert.That(warehouse.gameObject.activeSelf,Is.False);
                yield return Capture("hub-tab-backpack.png");
                InputSystem.QueueStateEvent(keyboard,new KeyboardState());InputSystem.Update();yield return null;
                InputSystem.QueueStateEvent(keyboard,new KeyboardState(Key.Tab));InputSystem.Update();yield return null;
                Assert.That(_ui.IsOpen,Is.False);Assert.That(Time.timeScale,Is.EqualTo(1));
                InputSystem.QueueStateEvent(keyboard,new KeyboardState());InputSystem.Update();yield return null;
                InputSystem.QueueStateEvent(keyboard,new KeyboardState(Key.I));InputSystem.Update();yield return null;
                Assert.That(_ui.IsOpen,Is.False,"原测试I入口已取消。");
            }
            finally{InputSystem.RemoveDevice(keyboard);}
        }

        [UnityTest] public IEnumerator WarehouseAndShopOpenOnlyAtEStationsAndTabSwitchesToBackpack()
        {
            Move(new Vector3(-9,.2f,4));yield return null;_ui.OpenWarehouse();Assert.That(_ui.IsOpen,Is.False);
            _shop.Open();Assert.That(_shop.IsOpen,Is.False);
            var keyboard=InputSystem.AddDevice<Keyboard>();
            try
            {
                Move(Station(HubStationKind.Warehouse).Position);yield return null;
                InputSystem.QueueStateEvent(keyboard,new KeyboardState(Key.E));InputSystem.Update();yield return null;
                Assert.That(_ui.IsOpen,Is.True);Assert.That(_ui.WarehouseMode,Is.True);yield return Capture("hub-e-warehouse.png");
                InputSystem.QueueStateEvent(keyboard,new KeyboardState());InputSystem.Update();yield return null;
                InputSystem.QueueStateEvent(keyboard,new KeyboardState(Key.Tab));InputSystem.Update();yield return null;
                Assert.That(_ui.IsOpen,Is.True);Assert.That(_ui.WarehouseMode,Is.False,"仓库中Tab切到随身编程。");
                _ui.Close();InputSystem.QueueStateEvent(keyboard,new KeyboardState());InputSystem.Update();yield return null;
                Move(Station(HubStationKind.Shop).Position);yield return null;
                InputSystem.QueueStateEvent(keyboard,new KeyboardState(Key.B));InputSystem.Update();yield return null;
                Assert.That(_shop.IsOpen,Is.False,"商店只有站点E入口。");
                InputSystem.QueueStateEvent(keyboard,new KeyboardState());InputSystem.Update();yield return null;
                InputSystem.QueueStateEvent(keyboard,new KeyboardState(Key.E));InputSystem.Update();yield return null;
                Assert.That(_shop.IsOpen,Is.True);yield return Capture("hub-e-shop.png");
                InputSystem.QueueStateEvent(keyboard,new KeyboardState());InputSystem.Update();yield return null;
                InputSystem.QueueStateEvent(keyboard,new KeyboardState(Key.Tab));InputSystem.Update();yield return null;yield return null;
                Assert.That(_ui.IsOpen,Is.True);Assert.That(_ui.WarehouseMode,Is.False);Assert.That(_shop.IsOpen,Is.False);
                Assert.That(Time.timeScale,Is.Zero,"交接面板后不能误解除Inventory暂停。");
            }
            finally{InputSystem.RemoveDevice(keyboard);}
        }
        private void Move(Vector3 point)
        { bool enabled = _player.CharacterController.enabled; _player.CharacterController.enabled = false; _player.transform.position = point; _player.CharacterController.enabled = enabled; }
        private ulong Bottle()
        { Assert.That(_session.TryCopyContainer(_bottle, new MaterialAmount[8], out _, out ulong total, out _), Is.True); return total; }
        private ExperimentWorldQuery Query()
        { var query = new ExperimentWorldQuery { WorldId = "container-world" }; EventBus<ExperimentWorldQueryEvent>.Publish(new ExperimentWorldQueryEvent { Query = query }); Assert.That(query.Ready, Is.True); return query; }
        private HubDefinition.Station Station(HubStationKind kind)
        { foreach (var station in _hub.Definition.Stations) if (station.Kind == kind) return station; throw new InvalidOperationException("交互点缺失。"); }
        private void OnLog(string message, string stack, LogType kind)
        {
            if (message.Contains("UniversalCameraData has already")) _cameraErrors++;
            if (message.Contains("ApplyGrabPassMainThread")) _grabErrors++;
            if (kind == LogType.Error || kind == LogType.Exception || kind == LogType.Assert) _errors.Add(message + "\n" + stack);
        }

        [UnityTest] public IEnumerator SpawnSettlesOnRealMapAndWKeyboardMovesCharacter()
        {
            yield return new WaitForSeconds(.5f); Vector3 start = _player.transform.position;
            Assert.That(_player.GetComponent<GroundChecker>().IsGrounded, Is.True, "必须实际接地，不能仅凭射线有碰撞。");
            // 独立测试设备避免真实键盘/Editor焦点事件覆盖合成状态；仍经过生产InputAction和状态机。
            var keyboard=InputSystem.AddDevice<Keyboard>();
            try
            {
                InputSystem.QueueStateEvent(keyboard,new KeyboardState(Key.W));InputSystem.Update();yield return null;
                Assert.That(_player.MoveInput.y,Is.GreaterThan(.5f),"生产Move Action必须收到W输入。");
                yield return new WaitForSeconds(.45f);InputSystem.QueueStateEvent(keyboard,new KeyboardState());InputSystem.Update();yield return null;
                Vector3 delta=_player.transform.position-start;delta.y=0;
                Assert.That(delta.magnitude,Is.GreaterThan(.25f),"start="+start+" end="+_player.transform.position);Assert.That(_player.transform.position.y,Is.GreaterThan(-2));
            }
            finally{InputSystem.QueueStateEvent(keyboard,new KeyboardState());InputSystem.Update();InputSystem.RemoveDevice(keyboard);}
        }


        [UnityTest] public IEnumerator WorldLabelsTrackFinalCameraRotationFromFourDirections()
        {
            var camera=Find<Camera>();int count=0;
            foreach(var root in _scene.GetRootGameObjects())foreach(var label in root.GetComponentsInChildren<WorldSpaceLabelBillboard>(true))
            {
                count++;
                for(int direction=0;direction<4;direction++)
                {
                    _player.transform.Find("CameraRoot").rotation=Quaternion.Euler(12,direction*90,0);
                    yield return null;yield return new WaitForEndOfFrame();
                    Assert.That(Quaternion.Angle(label.transform.rotation,camera.transform.rotation),Is.LessThan(.01f));
                    Assert.That(Vector3.Dot(label.transform.right,camera.transform.right),Is.GreaterThan(.999f),"左右轴不能镜像");
                }
            }
            Assert.That(count,Is.GreaterThanOrEqualTo(4));
        }

        [UnityTest] public IEnumerator HubIgnoresDamageButRaidUsesActualDamageFunnel()
        {
            var health=_player.GetComponent<Game.Combat.HealthComponent>();float hp=health.CurrentHp;
            Assert.That(health.IsInvulnerable,Is.True);
            var hit=new Game.Combat.DamageRequest(777,2,10,Game.Combat.DamageType.True,_player.transform.position,Vector3.forward);
            health.ReceiveHit(hit);Assert.That(health.CurrentHp,Is.EqualTo(hp));
            Move(Station(HubStationKind.Expedition).Position);Assert.That(_run.Depart(),Is.True);
            for(int i=0;i<240 && _run.State.Phase!=RaidPhase.Exploring;i++)yield return null;
            Assert.That(_run.State.Phase,Is.EqualTo(RaidPhase.Exploring));Assert.That(health.IsInvulnerable,Is.False);
            health.ReceiveHit(hit);Assert.That(health.CurrentHp,Is.EqualTo(hp-10));
        }

        [UnityTest] public IEnumerator ContactPortalStartsRaidAndDoesNotImmediatelyReenterOnReturn()
        {
            var portal=Find<HubExpeditionPortal>();Assert.That(portal,Is.Not.Null);
            Move(portal.transform.position);yield return new WaitForFixedUpdate();yield return new WaitForFixedUpdate();
            for(int i=0;i<240 && _run.State.Phase!=RaidPhase.Exploring;i++)yield return null;
            Assert.That(_run.State.Phase,Is.EqualTo(RaidPhase.Exploring),"应由真实Collider触发，不调用Depart或TryEnter冒充接触");
            EventBus<RaidDepartureRequestedEvent>.Publish(new RaidDepartureRequestedEvent{InventoryId=_session.ReadOnly.InventoryId,Command=RaidCommand.Abandon});
            for(int i=0;i<240 && _run.State.Phase!=RaidPhase.HubReady;i++)yield return null;
            Assert.That(_run.State.Phase,Is.EqualTo(RaidPhase.HubReady));yield return new WaitForSeconds(.25f);
            Assert.That(_run.State.Phase,Is.EqualTo(RaidPhase.HubReady));Assert.That(portal.TryEnter(_player.CharacterController),Is.False,"原位置返回需走出再进入");
            Assert.That(_player.GetComponent<Game.Combat.HealthComponent>().IsInvulnerable,Is.True);
        }

        [UnityTest] public IEnumerator ThreeStationInteractionsAndWorkspaceRenderOnRealMap()
        {
            Move(Station(HubStationKind.Warehouse).Position); _hub.Request(HubInteractionKind.Warehouse); yield return null;
            Assert.That(_ui.IsOpen, Is.True); yield return Capture("warehouse.png"); _ui.Close();
            _ui.OpenBackpack(); yield return null; Assert.That(_ui.IsOpen, Is.True); yield return Capture("backpack.png"); _ui.Close();
            Move(Station(HubStationKind.Shop).Position); _hub.Request(HubInteractionKind.Shop); yield return null;
            Assert.That(_shop.IsOpen, Is.True); yield return Capture("shop.png"); _shop.Close();
            Move(Station(HubStationKind.Expedition).Position); Assert.That(_hub.TryGetStation(out var station), Is.True); Assert.That(station.Kind, Is.EqualTo(HubStationKind.Expedition));
        }

        [UnityTest] public IEnumerator ExperimentPlatformTransfersMixedStockWithoutFreeWorldSamples()
        {
            Assert.That(Query().Total, Is.Zero); Vector3 center = _hub.Definition.ExperimentCenter;
            Vector3 point = new Vector3(center.x, center.y - _hub.Definition.ExperimentSize.y * .5f + .3f, center.z);
            Move(point); yield return new WaitForSeconds(.2f); _world.SetSimulationPausedForDebug(true);
            Assert.That(Find<ExperimentAreaController>().IsInside, Is.True);
            _player.GetComponent<ContainerUseController>().SubmitUse(ContainerUseKind.Pour, 30, point);
            Assert.That(_player.GetComponent<ContainerUseController>().LastResult.Success, Is.True,
                "point="+point+" actor="+_player.transform.position+" failure="+_player.GetComponent<ContainerUseController>().LastResult.Transfer.Failure);
            Assert.That(Bottle(), Is.EqualTo(30)); Assert.That(Query().Total, Is.EqualTo(30));
            _player.GetComponent<MaterialCollectionController>().SubmitSample(point, false, 30);
            Assert.That(Bottle(), Is.EqualTo(60)); Assert.That(Query().Total, Is.Zero); yield return null;
        }

        [UnityTest] public IEnumerator AttachedCastConsumesActualMixedStockOnRealPlatform()
        {
            Vector3 center=_hub.Definition.ExperimentCenter;Vector3 foot=new Vector3(center.x,.6f,center.z);
            Move(foot);yield return new WaitForSeconds(.2f);_world.SetSimulationPausedForDebug(true);
            _session.WandsReadOnly.TryGetSlot(0,out var wand);_session.WandsReadOnly.TryGetProgram(wand.InstanceId,out var program);
            Find<SpellAttachmentView>().Submit(program.Program.GetInstanceId(0),_bottle,10,true);
            Assert.That(_player.GetComponent<OwnedWandCastAdapter>().TryCapture(out var projection,out var context),Is.True);
            Vector3 target=foot+Vector3.right*1.25f;
            Assert.That(_player.GetComponent<SpellCaster>().CastOwnedProgram(projection,context,target+Vector3.up*2,target-Vector3.up*2,1,777,_player.CharacterController),Is.EqualTo(1));
            Assert.That(Bottle(),Is.EqualTo(50));yield return new WaitForSeconds(.6f);
            Assert.That(_world.ImportedMaterialQuantity(MaterialId.Water)+_world.ImportedMaterialQuantity(MaterialId.Poison),Is.EqualTo(10));
        }

        [UnityTest] public IEnumerator BackpackLayoutRendersNearbyToolsAndSelectedDetailsOnRealMap()
        {
            string bag=null;foreach(var entry in _session.Profile.Items)if(entry.Location.Kind==ItemLocationKind.EquippedBackpack)bag=entry.Item.InstanceId;
            _session.ReadOnly.TryGetView(bag,out var backpack);
            var snapshots=new InventoryItemSnapshot[_session.ReadOnly.Count];int count=_session.ReadOnly.CopyItemsTo(snapshots);string ground=null;
            _session.WandsReadOnly.TryGetSlot(0,out var exampleWand);_session.WandsReadOnly.TryGetProgram(exampleWand.InstanceId,out var exampleProgram);
            _session.ReadOnly.TryGetView(exampleProgram.Program.GetInstanceId(0),out var exampleCard);
            Assert.That(_session.Profile.Items.TryAdd(new RuntimeItemState("layout.ground",exampleCard.ContentId,4,false,"layout.ground"),ItemLocation.Warehouse),Is.True);
            Assert.That(_session.Profile.Items.TryAdd(new RuntimeItemState("layout.carried",exampleCard.ContentId,6,false,"layout.carried"),new GridPlacement(1,0,0).InBackpack(bag)),Is.True);
            EventBus<InventoryCommandEvent>.Publish(new InventoryCommandEvent{InventoryId=_session.ReadOnly.InventoryId,Kind=InventoryCommandKind.Drop,ItemId="layout.ground",ExpectedVersion=_session.ReadOnly.Version});
            // 内存Fixture只搬已有实例；不把测试礼物或完整截图写进生产Profile。
            for(int i=0;i<count;i++)if(snapshots[i].Location.Kind==ItemLocationKind.Warehouse && snapshots[i].Kind==ItemKind.Spell)
            {
                if(ground==null){ground=snapshots[i].InstanceId;EventBus<InventoryCommandEvent>.Publish(new InventoryCommandEvent{InventoryId=_session.ReadOnly.InventoryId,Kind=InventoryCommandKind.Drop,ItemId=ground,ExpectedVersion=_session.ReadOnly.Version});continue;}
                bool placed=false;for(int y=0;y<backpack.Rules.BackpackHeight && !placed;y++)for(int x=0;x<backpack.Rules.BackpackWidth && !placed;x++)placed=_session.Profile.Items.TryMove(snapshots[i].InstanceId,new GridPlacement(x,y,0).InBackpack(bag)).Success;
            }
            _ui.OpenBackpack();_ui.SelectItem(_bottle);yield return null;Canvas.ForceUpdateCanvases();
            var workspace=Find<InventoryWorkspaceView>();var data=new SerializedObject(workspace);var nearby=(RectTransform)data.FindProperty("_nearbyRegion").objectReferenceValue;
            var tools=(RectTransform)data.FindProperty("_toolsRoot").objectReferenceValue;Assert.That(nearby.gameObject.activeInHierarchy,Is.True);
            bool bottle=false;foreach(var tile in tools.GetComponentsInChildren<InventoryItemView>())if(tile.Snapshot.InstanceId==_bottle)bottle=true;Assert.That(bottle,Is.True);
            Assert.That(_player.GetComponent<MaterialCollectionController>().SelectedContainerId,Is.EqualTo(_bottle));yield return Capture("backpack-container-details.png");
            _session.WandsReadOnly.TryGetSlot(1,out var wand);_ui.SelectItem(wand.InstanceId);yield return null;yield return Capture("backpack-wand-details.png");
            _session.WandsReadOnly.TryGetSlot(0,out wand);_session.WandsReadOnly.TryGetProgram(wand.InstanceId,out var program);_ui.SelectItem(program.Program.GetInstanceId(0));yield return null;yield return Capture("backpack-spell-details.png");
        }
        [UnityTest] public IEnumerator StableFormalHubUpdateScopesHaveNoGcAlloc(){yield return RecordStableScopes(false);}
        [UnityTest] public IEnumerator StableOpenInventoryDetailsHaveNoGcAlloc(){yield return RecordStableScopes(true);}
        private IEnumerator RecordStableScopes(bool inventory)
        {
            bool recording=UnityEditorInternal.ProfilerDriver.enabled;bool cpu=UnityEditorInternal.ProfilerDriver.IsAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU);
            try
            {
                if(inventory){_ui.OpenBackpack();_session.WandsReadOnly.TryGetSlot(0,out var wand);_session.WandsReadOnly.TryGetProgram(wand.InstanceId,out var program);_ui.SelectItem(program.Program.GetInstanceId(0));}
                UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,true);UnityEditorInternal.ProfilerDriver.enabled=true;
                for(int i=0;i<8;i++)yield return null;int first=UnityEditorInternal.ProfilerDriver.lastFrameIndex+1;for(int i=0;i<60;i++)yield return null;
                var report=new HubProfile{Utc=DateTime.UtcNow.ToString("O"),Scope="Editor TestRunner, "+_scene.path+", Map_1 empty materials, "+(inventory?"inventory open and spell selected":"UI closed")+"; named Update scopes only",FirstFrame=first,LastFrame=UnityEditorInternal.ProfilerDriver.lastFrameIndex,
                    Markers=new[]{new HubMarker{Name="Norn.Hub.NavigationUpdate"},new HubMarker{Name="Norn.Hub.ExperimentUpdate"},new HubMarker{Name="Norn.Shop.UiUpdate"},new HubMarker{Name="Norn.Shop.SessionUpdate"},new HubMarker{Name=inventory?"Norn.Inventory.ContextActions":"Norn.Hub.LabelBillboard"},new HubMarker{Name=inventory?"Norn.Inventory.AttachmentDetails":"Norn.ContainerHud.Update"}}};
                for(int frame=Math.Max(first,UnityEditorInternal.ProfilerDriver.firstFrameIndex);frame<=report.LastFrame;frame++)
                {
                    using var data=UnityEditorInternal.ProfilerDriver.GetRawFrameDataView(frame,0);if(!data.valid || data.sampleCount==0)continue;report.ValidFrames++;report.AverageMainThreadMs+=data.GetSampleTimeMs(0);
                    for(int sample=0;sample<data.sampleCount;sample++)
                    {
                        string name=data.GetSampleName(sample);if(name=="GC.Alloc" && data.GetSampleMetadataCount(sample)>0)report.MainThreadGcBytes+=data.GetSampleMetadataAsLong(sample,0);
                        foreach(var marker in report.Markers)if(name==marker.Name)
                        {double start=data.GetSampleStartTimeMs(sample),duration=data.GetSampleTimeMs(sample);marker.Samples++;marker.MaximumMs=Math.Max(marker.MaximumMs,duration);
                            for(int child=sample+1;child<data.sampleCount;child++){double time=data.GetSampleStartTimeMs(child);if(time>start+duration)break;if(time>=start && data.GetSampleName(child)=="GC.Alloc" && data.GetSampleMetadataCount(child)>0)marker.GcBytes+=data.GetSampleMetadataAsLong(child,0);}}
                    }
                }
                if(report.ValidFrames>0)report.AverageMainThreadMs/=report.ValidFrames;
                string artifactName=inventory?"hub-inventory-profiler":"hub-profiler";System.IO.Directory.CreateDirectory(Evidence);System.IO.File.WriteAllText(Evidence+"/"+artifactName+".json",JsonUtility.ToJson(report,true));UnityEditorInternal.ProfilerDriver.SaveProfile(Evidence+"/"+artifactName+".data");
                Assert.That(report.ValidFrames,Is.GreaterThanOrEqualTo(30));foreach(var marker in report.Markers){Assert.That(marker.Samples,Is.GreaterThan(0),marker.Name);Assert.That(marker.GcBytes,Is.Zero,marker.Name);}
            }
            finally{UnityEditorInternal.ProfilerDriver.enabled=recording;UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,cpu);}
        }
        [Serializable]private sealed class HubMarker{public string Name;public int Samples;public double MaximumMs;public long GcBytes;}
        [Serializable]private sealed class HubProfile{public string Utc,Scope;public int FirstFrame,LastFrame,ValidFrames;public double AverageMainThreadMs;public long MainThreadGcBytes;public HubMarker[] Markers;}

        [UnityTest] public IEnumerator ValidatedRaidRoundTripRestoresMapStationsAndExperiment()
        {
            Assert.That(_run.Definition.WorldScenePath, Is.EqualTo("Assets/_Project/Scenes/Tests/RaidWorldSandbox.unity"), "本轮仍是明确记录的临时验证路线。");
            Move(Station(HubStationKind.Expedition).Position); Assert.That(_run.Depart(), Is.True);
            float end = Time.realtimeSinceStartup + 15;
            while (_run.State.Phase == RaidPhase.Departing && Time.realtimeSinceStartup < end) yield return null;
            Assert.That(_run.State.Phase, Is.EqualTo(RaidPhase.Exploring));
            var keyboard=InputSystem.AddDevice<Keyboard>();
            try
            {
                // 据点环境停用后同一随身UI还在；真实按键经过生产InputAction。
                InputSystem.QueueStateEvent(keyboard,new KeyboardState(Key.Tab));InputSystem.Update();yield return null;
                Assert.That(_ui.IsOpen,Is.True);Assert.That(_ui.WarehouseMode,Is.False);Assert.That(_ui.gameObject.activeInHierarchy,Is.True);
                _ui.Close();InputSystem.QueueStateEvent(keyboard,new KeyboardState());InputSystem.Update();yield return null;
                _ui.OpenWarehouse();Assert.That(_ui.IsOpen,Is.False,"箱庭不能打开据点仓库。");
            }
            finally{InputSystem.RemoveDevice(keyboard);}
            GameObject map = null; foreach (var root in _scene.GetRootGameObjects())foreach(var node in root.GetComponentsInChildren<Transform>(true)) if (node.name == "Map_1") map = node.gameObject;
            Assert.That(map,Is.Not.Null,"已分组据点必须仍能找到环境。");
            Assert.That(map.activeSelf, Is.False);
            EventBus<RaidDepartureRequestedEvent>.Publish(new RaidDepartureRequestedEvent { InventoryId = _session.ReadOnly.InventoryId, Command = RaidCommand.Abandon });
            end = Time.realtimeSinceStartup + 15;
            while (_run.State.Phase != RaidPhase.HubReady && Time.realtimeSinceStartup < end) yield return null;
            Assert.That(_run.State.Phase, Is.EqualTo(RaidPhase.HubReady)); Assert.That(map.activeSelf, Is.True);
            Assert.That(_player.gameObject.scene, Is.EqualTo(_scene)); Assert.That(_hub.TryGetStation(out var station), Is.True); Assert.That(station.Kind, Is.EqualTo(HubStationKind.Expedition));
            keyboard=InputSystem.AddDevice<Keyboard>();
            try{InputSystem.QueueStateEvent(keyboard,new KeyboardState(Key.Tab));InputSystem.Update();yield return null;Assert.That(_ui.IsOpen,Is.True);Assert.That(_ui.WarehouseMode,Is.False);}
            finally{InputSystem.RemoveDevice(keyboard);}
        }

        [UnityTest] public IEnumerator SustainedGameplayRenderingHasNoLegacyCameraOrGrabPassErrors()
        {
            yield return new WaitForSecondsRealtime(3); yield return Capture("hub-gameplay.png");
            Assert.That(_cameraErrors, Is.Zero); Assert.That(_grabErrors, Is.Zero); Assert.That(_errors, Is.Empty, string.Join("\n", _errors));
        }

        private static IEnumerator Capture(string name)
        {
            System.IO.Directory.CreateDirectory(Evidence);
            yield return new WaitForEndOfFrame(); ScreenCapture.CaptureScreenshot(Evidence+"/" + name);
            yield return null; yield return new WaitForSecondsRealtime(.2f);
        }

        [UnityTearDown] public IEnumerator Cleanup()
        {
            try
            {
                SceneManager.sceneLoaded -= Loaded; Application.logMessageReceived -= OnLog;
                if (Keyboard.current != null) InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState());
                if (_ui != null) _ui.Close(); if (_shop != null) _shop.Close(); if (_run != null) _run.CloseSynchronously();
                var raid = SceneManager.GetSceneByPath("Assets/_Project/Scenes/Tests/RaidWorldSandbox.unity"); if (raid.IsValid() && raid.isLoaded) yield return SceneManager.UnloadSceneAsync(raid);
                if (_scene.IsValid() && _scene.isLoaded) yield return SceneManager.UnloadSceneAsync(_scene);
            }
            finally
            {
                // 先卸载场景并Disable其Action，再恢复原Runtime；异常也必须释放测试设备。
                try{_inputFixture?.TearDown();}finally{_inputFixture=null;Time.timeScale=1;Application.runInBackground=_background;}
            }
            yield return null;
        }
        private sealed class MemoryFiles : IProfileFileSystem
        {
            private readonly Dictionary<string, string> _files = new Dictionary<string, string>();
            public bool Exists(string path) => _files.ContainsKey(path);
            public string Read(string path) => _files[path];
            public void WriteFlushed(string path, string text) => _files[path] = text;
            public void Move(string source, string target) { _files[target] = _files[source]; _files.Remove(source); }
            public void Replace(string source, string target, string backup) { _files[backup] = _files[target]; Move(source, target); }
        }
    }
}
#endif
