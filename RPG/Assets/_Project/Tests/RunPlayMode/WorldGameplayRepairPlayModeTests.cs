#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Game.Character;
using Game.Combat;
using Game.Core;
using Game.ElementField;
using Game.Materials;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
namespace Game.Run.Tests
{
    public sealed class HomeGameplayRepairPlayModeTests:BackpackWorkspacePlayModeTests
    {
        protected override string ReviewPath=>"Assets/_Project/Scenes/Tests/Hub/SinglePlayer_Home_GameplayRepairReview.unity";
        protected override string EvidencePath=>"TestResults/SinglePlayer/M6-T25-WorldGameplayRepair";
        protected override bool UseSingleScene=>true;
        protected override SinglePlayerProfile CustomizeProfile(SinglePlayerProfile p,ItemCatalogIndex index)
        {
            for(byte key=5;key<=6;key++)
            {Assert.That(index.TryFindMaterialContent(key,out var content),Is.True);Assert.That(p.Items.TryAdd(new RuntimeItemState("solid"+key,content,0,material:new MaterialItemState(key,key==5?5000UL:15000UL)),new ItemLocation(ItemLocationKind.Backpack,"bag",key,5,0,-1)),Is.True);}
            p.Items.TryGet("b0",out var bottle);index.TryGetRules(bottle.ContentId,out var rules);
            var gas=new ContainerComposition(rules.ContainerCapacity,rules.ContainerMaterials);gas.AddReserved(new[]{new MaterialAmount(7,15000)},1);
            Assert.That(p.Items.TryAdd(new RuntimeItemState("gasfull",bottle.ContentId,0,composition:gas),new ItemLocation(ItemLocationKind.Backpack,"bag",5,0,0,-1)),Is.True);
            var mixed=new ContainerComposition(rules.ContainerCapacity,rules.ContainerMaterials);mixed.AddReserved(new[]{new MaterialAmount(7,7500),new MaterialAmount(8,7500)},2);
            Assert.That(p.Items.TryAdd(new RuntimeItemState("mixedfull",bottle.ContentId,0,composition:mixed),new ItemLocation(ItemLocationKind.Backpack,"bag",6,0,0,-1)),Is.True);
            return p;
        }
        private ElementWorldRuntime World()
        {
            Player.enabled=false;Player.CharacterController.enabled=false;Player.transform.position=new Vector3(0,.1f,0);Player.CharacterController.enabled=true;Physics.SyncTransforms();
            var camera=Camera.main;Assert.That(camera,Is.Not.Null);var brain=camera.GetComponent("CinemachineBrain") as Behaviour;if(brain!=null)brain.enabled=false;
            camera.transform.position=new Vector3(0,3,-5);camera.transform.LookAt(new Vector3(0,1,3));
            return Find<ElementWorldRuntime>();
        }
        [UnityTest]public IEnumerator HomeFireProducesVisibleGpuSeedsFromActualFireCells()
        {
            var world=World();world.SetSimulationPausedForDebug(true);
            Assert.That(world.TryEnqueueWrite(new ElementWriteRequest(new Vector3(0,1,3),MaterialId.Fire,255,.5f,false)),Is.True);world.TickForTests(.1f);
            Component renderer=null;foreach(var root in Scene.GetRootGameObjects())foreach(var c in root.GetComponentsInChildren<MonoBehaviour>(true))if(c!=null&&c.GetType().Name=="GpuFireParcelRenderer")renderer=c;
            Assert.That(renderer,Is.Not.Null);var type=renderer.GetType();uint before=(uint)type.GetProperty("TotalSeeds").GetValue(renderer);
            for(int i=0;i<30;i++)yield return null;
            bool ready=(bool)type.GetProperty("IsReady").GetValue(renderer);uint after=(uint)type.GetProperty("TotalSeeds").GetValue(renderer);
            var keys=new ElementChunkKey[world.MaximumResidentChunkCount];int count=world.CopyVisibleChunkKeys(keys),fireCells=0,fireAmount=0;
            var cells=new ElementCell[world.ChunkSize*world.ChunkSize*world.ChunkSize];for(int k=0;k<count;k++){int used=world.CopyChunkCells(keys[k],cells);for(int n=0;n<used;n++)if(cells[n].MaterialKind==MaterialId.Fire){fireCells++;fireAmount+=cells[n].Amount;}}
            var interest=type.GetField("_interestCenter",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).GetValue(renderer) as Transform;
            Directory.CreateDirectory(EvidencePath);File.WriteAllText(EvidencePath+"/home-fire-diagnostic.txt","Ready="+ready+" Seeds="+after+" Before="+before+" WorldInitialized="+world.IsInitialized+" Visible="+count+" FireCells="+fireCells+" FireAmount="+fireAmount+" InterestUnityNull="+(interest==null)+" InterestClrNull="+ReferenceEquals(interest,null)+" TimeScale="+Time.timeScale);
            Assert.That(ready,Is.True);Assert.That(after,Is.GreaterThan(before),"真实Fire Cell必须生成可见火焰种子");
            yield return new WaitForEndOfFrame();var image=ScreenCapture.CaptureScreenshotAsTexture();File.WriteAllBytes(EvidencePath+"/home-fire.png",image.EncodeToPNG());Object.Destroy(image);
        }
        [UnityTest]public IEnumerator Ice5000RealGDepositsWholeQuantity(){yield return Throw(5,5000);}
        [UnityTest]public IEnumerator Dried15000RealGDepositsWholeQuantity(){yield return Throw(6,15000);}
        [UnityTest]public IEnumerator BackpackSolidCaptionIsBareQuantity()
        {
            Backpack.OpenBackpack();yield return null;bool found=false;
            foreach(var root in Scene.GetRootGameObjects())foreach(var card in root.GetComponentsInChildren<InventoryItemView>(true))
                if(card.gameObject.activeInHierarchy&&card.Snapshot.InstanceId=="solid5")
                {
                    found=true;bool number=false;foreach(var label in card.GetComponentsInChildren<UnityEngine.UI.Text>(true))
                    {Assert.That(label.text,Does.Not.Contain("GMU"));if(label.text.Contains("5000"))number=true;}
                    Assert.That(number,Is.True,"材料应保留真实数量，只移除术语");
                }
            Assert.That(found,Is.True,"必须检查实际渲染卡片，而非只搜索源码");
        }
        [UnityTest]public IEnumerator FullSteamBottleRealGDepositsWholeQuantity(){yield return GasThrow("gasfull");}
        [UnityTest]public IEnumerator FullMixedGasBottleRealGDepositsWholeQuantity(){yield return GasThrow("mixedfull");}
        private IEnumerator GasThrow(string id)
        {
            var world=World();world.SetSimulationPausedForDebug(true);Bind(0,id);Keys(Key.Digit4);yield return null;Keys();yield return null;
            Keys(Key.G);yield return null;yield return null;Keys();yield return null;
            float until=Time.realtimeSinceStartup+12;while(Time.realtimeSinceStartup<until&&Session.Profile.Items.TryGet(id,out _))yield return null;
            ulong remaining=Session.Profile.Items.TryGet(id,out var item)?item.Composition.Total:0;
            Directory.CreateDirectory(EvidencePath);File.WriteAllText(EvidencePath+"/"+id+"-15000-diagnostic.txt","World="+world.Gases.TotalQuantity+" Transit="+remaining+" Failure="+Find<ContainerUseController>().LastResult.Transfer.Failure);
            Assert.That(world.Gases.TotalQuantity+remaining,Is.EqualTo(15000UL));Assert.That(remaining,Is.Zero,"满气体容器不能因固定落地半径永久滞留在途");
        }
        [UnityTest]public IEnumerator ActiveIceThrowConservesQuantityAcrossPhaseChanges()
        {
            var world=World();Bind(0,"solid5");Keys(Key.Digit4);yield return null;Keys();yield return null;
            Keys(Key.G);yield return null;yield return null;Keys();yield return null;
            float until=Time.realtimeSinceStartup+12;while(Time.realtimeSinceStartup<until&&Session.Profile.Items.TryGet("solid5",out _))yield return null;
            Assert.That(Session.Profile.Items.TryGet("solid5",out _),Is.False);
            yield return new WaitForSecondsRealtime(2);
            Assert.That(world.TryCaptureMaterialQuantityFamily(MaterialQuantityFamily.WaterBased,out var balance),Is.True);
            // 相变是合法 Owner 转移；使用同族质量账核对，不能只看剩余冰块的外观体积。
            File.WriteAllText(EvidencePath+"/active-ice-quantity.txt","Ice="+world.FixedSolids.TotalQuantity+" Liquid="+LiquidTotal(world)+" Gas="+world.Gases.TotalQuantity+" Imported="+balance.Imported+" Resident="+balance.Resident+" Pending="+balance.Pending+" Vented="+balance.Vented+" RuleConsumed="+balance.RuleConsumed+" Balanced="+balance.IsBalanced);
            Assert.That(balance.IsBalanced,Is.True);
            Assert.That(world.ImportedMaterialQuantity(MaterialId.Ice),Is.EqualTo(5000UL));
            Assert.That(world.FixedSolids.TotalQuantity+world.Gases.TotalQuantity+LiquidTotal(world),Is.EqualTo(5000UL));
        }
        private static ulong LiquidTotal(ElementWorldRuntime world)
        {
            return world.GridLiquids.TotalQuantity;
        }
        [UnityTest]public IEnumerator ProductionHomeFireFallsAndWarmedMarkersHaveNoScopedGc()
        {
            var world=World();yield return GameplayRepairChecks.CheckFire(world,Player.transform.position);
            world.SetSimulationPausedForDebug(true);
            // 重复离散写入用于保持真实火源；写入和TestRunner的分配不纳入待测Marker。
            var request=new ElementWriteRequest(new Vector3(0,3,3),MaterialId.Fire,255,.5f,false);
            for(int i=0;i<12;i++){world.TryEnqueueWrite(request);world.TickForTests(.1f);yield return null;}
            bool recording=UnityEditorInternal.ProfilerDriver.enabled;bool cpu=UnityEditorInternal.ProfilerDriver.IsAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU);
            try
            {
                UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,true);UnityEditorInternal.ProfilerDriver.enabled=true;
                yield return null;int first=UnityEditorInternal.ProfilerDriver.lastFrameIndex+1;
                world.SetSimulationPausedForDebug(false);
                for(int i=0;i<65;i++){world.TryEnqueueWrite(request);world.TickForTests(.1f);yield return null;}
                WriteProfile("actual-fire-world-and-render",first,UnityEditorInternal.ProfilerDriver.lastFrameIndex,new[]{"ElementWorld.Simulate.FireFall","GpuFire.ParcelLifecycle","GpuFire.Draw"},"Home production world; actual Fire cells fall and renderer simulates/draws after 12 warmup frames; test injections and whole-frame allocations reported separately");
            }
            finally{UnityEditorInternal.ProfilerDriver.enabled=recording;UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,cpu);}
        }
        [UnityTest]public IEnumerator HomePortalFacesCameraWithoutMovingLogicRoot()
        {
            World();var portal=Find<HomePortalPresentation>();Assert.That(portal,Is.Not.Null);
            var visual=new SerializedObject(portal).FindProperty("_visual").objectReferenceValue as Transform;Assert.That(visual,Is.Not.Null);
            var position=portal.transform.position;var rotation=portal.transform.rotation;var center=visual.position;
            foreach(var offset in new[]{new Vector3(5,2,0),new Vector3(0,3,-5),new Vector3(-5,1,0)})
            {
                Camera.main.transform.position=center+offset;Camera.main.transform.LookAt(center);yield return null;yield return null;
                Assert.That(Vector3.Dot(visual.forward,offset.normalized),Is.GreaterThan(.98f),"视觉面应始终对着镜头");
                Assert.That(portal.transform.position,Is.EqualTo(position));Assert.That(portal.transform.rotation,Is.EqualTo(rotation));Assert.That((visual.position-center).sqrMagnitude,Is.LessThan(.00001f));
            }
        }
        private IEnumerator Throw(byte key,ulong quantity)
        {
            var world=World();world.SetSimulationPausedForDebug(true);Bind(0,"solid"+key);Keys(Key.Digit4);yield return null;Keys();yield return null;
            Keys(Key.G);yield return null;yield return null;Keys();yield return null;
            Assert.That(Session.Profile.Items.TryGetLocation("solid"+key,out var state)&&state.Kind==ItemLocationKind.ThrownContainer,Is.True,"真实G释放必须进入在途");
            float until=Time.realtimeSinceStartup+12;while(Time.realtimeSinceStartup<until&&Session.Profile.Items.TryGet("solid"+key,out _))yield return null;
            ulong remaining=Session.Profile.Items.TryGet("solid"+key,out var item)?item.Material.Quantity:0;
            Directory.CreateDirectory(EvidencePath);File.WriteAllText(EvidencePath+"/solid-"+quantity+"-diagnostic.txt","Expected="+quantity+" World="+world.FixedSolids.TotalQuantity+" Imported="+world.ImportedMaterialQuantity((MaterialId)key)+" Transit="+remaining+" Result="+Find<ContainerUseController>().LastResult.Transfer.Failure);
            Assert.That(world.FixedSolids.TotalQuantity+remaining,Is.EqualTo(quantity),"落地+在途必须精确守恒");
            Assert.That(remaining,Is.Zero,"大剂量不能永久卡在等容量的代理中");Assert.That(world.FixedSolids.TotalQuantity,Is.EqualTo(quantity));
        }
    }
    /// <summary>从保存布局的Hub进入真实P7/P8再返回，不强开世界权限，不以动画或只扣Mana替代发射证明。</summary>
    public sealed class DungeonInputRepairPlayModeTests:BackpackWorkspacePlayModeTests
    {
        protected override string ReviewPath=>"Assets/_Project/Scenes/Tests/Hub/SinglePlayer_Hub_GameplayRepairReview.unity";
        protected override string EvidencePath=>"TestResults/SinglePlayer/M6-T25-WorldGameplayRepair";
        protected override bool UseSingleScene=>true;
        protected override SinglePlayerProfile CustomizeProfile(SinglePlayerProfile p,ItemCatalogIndex index)
        {
            Assert.That(p.Items.TryMove("fire",new ItemLocation(ItemLocationKind.WandSlot,"w0",0,0,0,0),p.Items.Version).Success,Is.True);
            var pumpkin=AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/Items/Food/Item_Food_Pumpkin.asset");
            Assert.That(p.Items.TryAdd(new RuntimeItemState("pumpkin",pumpkin.ContentId,0),new ItemLocation(ItemLocationKind.Backpack,"bag",4,4,0,-1)),Is.True);return p;
        }
        [UnityTest]public IEnumerator HubToP7ActualMouseFiresAndGThrowsThenReturns(){yield return Flow("P7_DemoRun");}
        [UnityTest]public IEnumerator HubToP8ActualMouseFiresAndGThrowsThenReturns(){yield return Flow("P8_BossField");}
        private IEnumerator Flow(string name)
        {
            HubPracticePortal portal=null;foreach(var root in Scene.GetRootGameObjects())foreach(var candidate in root.GetComponentsInChildren<HubPracticePortal>())if(candidate.ScenePath!=null&&candidate.ScenePath.Contains(name))portal=candidate;
            Assert.That(portal,Is.Not.Null);Warp(portal.transform);yield return null;Keys(Key.F);yield return null;Keys();
            // Single加载会销毁旧UI，主动清引用；Unity的?.不会识别已销毁Object。
            Backpack=null;Warehouse=null;
            yield return WaitFor("Assets/_Project/Scenes/"+name+".unity");
            Find<GameplayGuideController>()?.Close();Player.GetComponent<HealthComponent>().SetInvulnerable(true);Time.timeScale=1;
            Assert.That(Session.WorldActionsAllowed,Is.True);Assert.That(Player.MainCamera,Is.Not.Null);
            yield return GameplayRepairChecks.CheckFire(Find<ElementWorldRuntime>(),Player.transform.position);
            Keys(Key.Digit1);yield return null;Keys();Click(false);yield return null;
            var owner=Player.GetComponent<HealthComponent>();var attacker=typeof(ProjectileBase).GetField("_attackerId",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
            bool spawned=false;Click(true);yield return null;Click(false);float until=Time.realtimeSinceStartup+5;
            while(Time.realtimeSinceStartup<until&&!spawned)
            {foreach(var shot in Object.FindObjectsByType<ProjectileBase>(FindObjectsSortMode.None))if(shot.gameObject.scene==Scene&&(int)attacker.GetValue(shot)==owner.Id)spawned=true;yield return null;}
            Assert.That(spawned,Is.True,"真实左键必须产生玩家Projectile，不能只播放施法动作");
            bool approved=false,proxy=false;
            void Result(ContainerUseResultEvent e){if(e.Kind==ContainerUseKind.Throw&&e.Transfer.ItemId=="b0"&&e.Success)approved=true;}
            void Proxy(ContainerThrownEvent e){if(e.Exchange.Request.ItemId=="b0"&&e.Exchange.Proxy is Component c&&c.gameObject.scene==Scene)proxy=true;}
            EventBus<ContainerUseResultEvent>.Subscribe(Result);EventBus<ContainerThrownEvent>.Subscribe(Proxy);
            try
            {
                Bind(0,"b0");Keys(Key.Digit4);yield return null;Keys();yield return null;Keys(Key.G);yield return null;yield return null;Keys();yield return null;
                Assert.That(approved,Is.True,"副本G必须被实际接受");Assert.That(proxy,Is.True,"必须准备真实容器模型物理代理");
            }
            finally{EventBus<ContainerUseResultEvent>.Unsubscribe(Result);EventBus<ContainerThrownEvent>.Unsubscribe(Proxy);}
            until=Time.realtimeSinceStartup+12;while(Session.PendingContainerFlights>0&&Time.realtimeSinceStartup<until)yield return null;
            Assert.That(Session.PendingContainerFlights,Is.Zero);
            Bind(1,"pumpkin");Keys(Key.Digit5);yield return null;Keys();yield return null;Keys(Key.G);yield return null;yield return null;Keys();yield return null;yield return new WaitForEndOfFrame();
            Assert.That(Session.Profile.Items.TryGetLocation("pumpkin",out var ordinary)&&ordinary.Kind==ItemLocationKind.ThrownContainer,Is.True,"副本普通Items也必须经真实G进入物理飞行");
            until=Time.realtimeSinceStartup+12;
            while(Time.realtimeSinceStartup<until&&Session.Profile.Items.TryGetLocation("pumpkin",out ordinary)&&ordinary.Kind==ItemLocationKind.ThrownContainer)yield return null;
            Assert.That(Session.Profile.Items.TryGetLocation("pumpkin",out ordinary)&&ordinary.Kind==ItemLocationKind.WorldItem,Is.True,"普通物体必须落为同一可拾取实例");
            if(name=="P8_BossField")
            {
                var entry=Find<BossEntryTrigger>();Assert.That(entry.TryArm(Player.CharacterController),Is.True);Assert.That(entry.TryActivate(Player.CharacterController),Is.True);
                var encounter=Find<BossEncounterController>();var boss=new SerializedObject(encounter).FindProperty("_bossHealth").objectReferenceValue as HealthComponent;
                Assert.That(boss,Is.Not.Null);until=Time.realtimeSinceStartup+6;while(boss.IsInvulnerable&&Time.realtimeSinceStartup<until)yield return null;
                Assert.That(boss.IsInvulnerable,Is.False);boss.ReceiveHit(new DamageRequest(12345,1,999999,DamageType.True,boss.transform.position,Vector3.forward));yield return null;yield return null;
            }
            var back=Find<HubPracticePortal>();Assert.That(back,Is.Not.Null);Warp(back.transform);yield return null;Keys(Key.F);yield return null;Keys();Backpack=null;
            yield return WaitFor(ReviewPath);
            Assert.That(HubPracticeSession.Current,Is.Null);Assert.That(Session.ReadOnly.TryGetView("w0",out _),Is.True);
            Directory.CreateDirectory(EvidencePath);File.WriteAllText(EvidencePath+"/"+name+"-roundtrip.txt","MouseProjectile=true\nGApproved=true\nRealModelProxy=true\nOrdinaryItemGFlightAndPickupIdentity=true\nPendingFlightsAtReturn=0\nReturnedToHub=true\nWorldRulesSource="+name);
        }
        private void Warp(Transform target)
        {Player.CharacterController.enabled=false;Player.transform.position=target.position+Vector3.up*.1f;Player.CharacterController.enabled=true;Physics.SyncTransforms();}
        private IEnumerator WaitFor(string path)
        {
            float until=Time.realtimeSinceStartup+45;
            while(Time.realtimeSinceStartup<until)
            {var loaded=UnityEngine.SceneManagement.SceneManager.GetSceneByPath(path);if(loaded.isLoaded&&(HubPracticeSession.Current==null||!HubPracticeSession.Current.Loading))
                {Scene=loaded;Session=Find<SinglePlayerInventorySession>();Player=Find<WizardController>();Backpack=Find<SinglePlayerInventoryController>();Warehouse=null;yield return null;yield return null;yield break;}yield return null;}
            Assert.Fail("真实转场超时："+path);
        }
        [UnityTearDown]public IEnumerator EnsureFailedSingleSceneCanUnload()
        {
            // Single转场可能卸载TestRunner初始空Scene。最后一张Scene不能Unload，先准备空接收Scene，防止失败污染后续世界单写者。
            if(UnityEngine.SceneManagement.SceneManager.sceneCount==1)
                UnityEngine.SceneManagement.SceneManager.CreateScene("GameplayRepairTestCleanup");
            if(HubPracticeSession.Current!=null)Object.Destroy(HubPracticeSession.Current.gameObject);
            yield return null;
        }
    }
    /// <summary>
    /// 只使用原门资源和临时Camera检查纯视觉接线。不进入账号/库存或真实转场，
    /// 也不以转动Trigger来代替Billboard；返回许可仍调用原PortalInteractionRegistry。
    /// </summary>
    public sealed class PortalVisualBillboardPlayModeTests
    {
        private readonly List<Camera> _previousMainCameras = new List<Camera>();
        private Scene _scene;
        private Camera _camera;

        [SetUp]
        public void BeginIsolatedPortalView()
        {
            // Camera.main按激活和Tag选择。只在测试开始隔离已有主镜头，结束逐一恢复，
            // 防止测试自己的Camera碰巧未被选中却误报门的朝向问题。
            foreach (var camera in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
                if (camera.isActiveAndEnabled && camera.CompareTag("MainCamera"))
                { _previousMainCameras.Add(camera); camera.enabled = false; }
            _scene = SceneManager.CreateScene("PortalVisualBillboardFixture");
            _camera = NewCamera("Portal test main camera");
        }

        [UnityTest]
        public IEnumerator UnifiedDynamicPortalFacesCameraWithoutRotatingTriggerOrMovingItsVisualCenter()
        {
            var portal = NewDynamicPortal(out var visual);
            var billboard = visual.GetComponent<WorldSpaceLabelBillboard>();
            Assert.That(billboard, Is.Not.Null, "原统一门的动态实例必须自行接入共用纯视觉Billboard。");
            Assert.That(portal.GetComponent<WorldSpaceLabelBillboard>(), Is.Null, "返回门逻辑根不能随Camera旋转。");
            yield return CheckCameraOrbit(portal, visual);
        }

        [UnityTest]
        public IEnumerator ExportedHomeReturnVisualFacesCameraWithoutChangingOriginalBoxPermission()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/World/Online/HomeWorld.prefab");
            Assert.That(prefab, Is.Not.Null);
            var source = prefab.GetComponent<OnlineHomeWorldBindings>();
            Assert.That(source, Is.Not.Null);
            var portal = Object.Instantiate(source.ReturnPortalTransform.gameObject);
            SceneManager.MoveGameObjectToScene(portal, _scene);
            var billboard = portal.GetComponentInChildren<WorldSpaceLabelBillboard>(true);
            Assert.That(billboard, Is.Not.Null,
                "实际交付的HomeWorld必须保留原视觉子根Billboard，不能只让Editor Preview通过。");
            Assert.That(billboard.transform, Is.Not.SameAs(portal.transform));
            Assert.That(portal.GetComponentInChildren<HomePortalPresentation>(true), Is.Null);
            Assert.That(portal.GetComponentInChildren<HomePortal>(true), Is.Null);
            yield return CheckCameraOrbit(portal, billboard.transform);
        }

        [UnityTest]
        public IEnumerator PortalBillboardRebindsWhenTheOldMainCameraIsDisabled()
        {
            var portal = NewDynamicPortal(out var visual);
            Assert.That(visual.GetComponent<WorldSpaceLabelBillboard>(), Is.Not.Null);
            _camera.transform.position = visual.position + new Vector3(5, 2, 0);
            _camera.transform.rotation = Quaternion.Euler(12, -35, 0);
            yield return null;
            AssertFacing(visual, _camera);
            var old = _camera; old.enabled = false;
            _camera = NewCamera("Replacement portal main camera");
            _camera.transform.position = visual.position + new Vector3(-4, 3, 2);
            _camera.transform.rotation = Quaternion.Euler(-10, 90, 0);
            yield return null; yield return null;
            AssertFacing(visual, _camera);
            Assert.That(Quaternion.Angle(portal.transform.rotation, Quaternion.Euler(0, 37, 0)), Is.LessThan(.001f));
        }

        [UnityTest]
        public IEnumerator ExistingWorldLabelsKeepScreenAlignmentByDefault()
        {
            var label = new GameObject("Existing world label"); SceneManager.MoveGameObjectToScene(label, _scene);
            label.transform.position = new Vector3(1700, 10, 1700);
            label.AddComponent<WorldSpaceLabelBillboard>();
            _camera.transform.position = label.transform.position + new Vector3(5, 2, 1);
            _camera.transform.rotation = Quaternion.Euler(16, 28, 7);
            yield return null;
            Assert.That(Quaternion.Angle(label.transform.rotation, _camera.transform.rotation), Is.LessThan(.001f),
                "门使用朝相机位置的选项，原文字仍按Camera的屏幕平面对齐。");
        }

        [UnityTest]
        public IEnumerator DynamicPortalDoesNotBillboardAConfiguredLogicRootOrColliderSubtree()
        {
            foreach (bool sameRoot in new[] { true, false })
            {
                var logic = new GameObject("Unsafe portal visual fixture"); logic.SetActive(false);
                SceneManager.MoveGameObjectToScene(logic, _scene); logic.AddComponent<BoxCollider>();
                Transform target = logic.transform;
                if (!sameRoot)
                {
                    var visual = new GameObject("Visual subtree containing interaction geometry");
                    visual.transform.SetParent(logic.transform, false); target = visual.transform;
                    var nested = new GameObject("Nested interaction Collider"); nested.transform.SetParent(target, false);
                    nested.AddComponent<BoxCollider>();
                }
                var controller = logic.AddComponent<PortalVfxController>();
                var data = new SerializedObject(controller);
                data.FindProperty("_visualRoot").objectReferenceValue = target; data.ApplyModifiedPropertiesWithoutUndo();
                logic.SetActive(true); yield return null;
                Assert.That(target.GetComponent<WorldSpaceLabelBillboard>(), Is.Null,
                    "配置必须指向无Collider的独立视觉子树，不能为达到朝镜头效果转动交互几何。");
            }
        }

        [UnityTest]
        public IEnumerator PortalBillboardKeepsItsPreviousRotationWhenCameraPassesThroughVisualCenter()
        {
            NewDynamicPortal(out var visual);
            _camera.transform.position = visual.position + new Vector3(5, 2, 0);
            yield return null;
            AssertFacing(visual, _camera);
            var before = visual.rotation;
            _camera.transform.position = visual.position;
            yield return null;
            Assert.That(Quaternion.Angle(visual.rotation, before), Is.LessThan(.001f),
                "零方向没有唯一朝向，必须保留上一帧，避免LookRotation错误或跳到identity。");
        }

        private GameObject NewDynamicPortal(out Transform visual)
        {
            var logic = new GameObject("Original return trigger fixture"); logic.SetActive(false);
            SceneManager.MoveGameObjectToScene(logic, _scene);
            logic.transform.SetPositionAndRotation(new Vector3(1700, 10, 1700), Quaternion.Euler(0, 37, 0));
            var box = logic.AddComponent<BoxCollider>(); box.isTrigger = true;
            box.center = Vector3.up * 1.25f; box.size = new Vector3(2, 3, 1);
            var prefab = Resources.Load<GameObject>("SinglePlayer/UnifiedPortalVisual");
            Assert.That(prefab, Is.Not.Null);
            var instance = Object.Instantiate(prefab, logic.transform, false);
            var controller = instance.GetComponent<PortalVfxController>();
            Assert.That(controller, Is.Not.Null);
            visual = new SerializedObject(controller).FindProperty("_visualRoot").objectReferenceValue as Transform;
            Assert.That(visual, Is.Not.Null);
            logic.SetActive(true);
            return logic;
        }

        private IEnumerator CheckCameraOrbit(GameObject logic, Transform visual)
        {
            var box = logic.GetComponent<BoxCollider>(); Assert.That(box, Is.Not.Null);
            Vector3 position = logic.transform.position, scale = logic.transform.localScale;
            Quaternion rotation = logic.transform.rotation;
            Vector3 center = visual.position, boxCenter = box.center, boxSize = box.size;
            var actor = new GameObject("Portal permission observer"); SceneManager.MoveGameObjectToScene(actor, _scene);
            var actorBox = actor.AddComponent<BoxCollider>(); actorBox.size = new Vector3(.4f, 1, .4f);
            actor.transform.position = box.transform.TransformPoint(box.center);
            Physics.SyncTransforms();
            Assert.That(PortalInteractionRegistry.Contains(box, actorBox), Is.True);
            foreach (var offset in new[] { new Vector3(5, 2, 0), new Vector3(0, 3, -5), new Vector3(-5, 1, 0) })
            {
                _camera.transform.position = center + offset;
                // 故意不让Camera自身LookAt门，才能区分朝Camera位置与原文字screen-aligned模式。
                _camera.transform.rotation = Quaternion.Euler(15, 23, 4);
                yield return null; yield return null;
                AssertFacing(visual, _camera);
                Assert.That(logic.transform.position, Is.EqualTo(position));
                Assert.That(logic.transform.localScale, Is.EqualTo(scale));
                Assert.That(Quaternion.Angle(logic.transform.rotation, rotation), Is.LessThan(.001f));
                Assert.That(Vector3.Distance(visual.position, center), Is.LessThan(.0001f));
                Assert.That(box.center, Is.EqualTo(boxCenter)); Assert.That(box.size, Is.EqualTo(boxSize));
                Physics.SyncTransforms();
                Assert.That(PortalInteractionRegistry.Contains(box, actorBox), Is.True,
                    "Billboard仅改视觉朝向，不能改变原Collider最近点交互许可。");
            }
        }

        private static void AssertFacing(Transform visual, Camera camera)
            => Assert.That(Vector3.Dot(visual.forward, (camera.transform.position - visual.position).normalized), Is.GreaterThan(.999f));

        private Camera NewCamera(string name)
        {
            var node = new GameObject(name); SceneManager.MoveGameObjectToScene(node, _scene);
            node.tag = "MainCamera";
            return node.AddComponent<Camera>();
        }

        [UnityTearDown]
        public IEnumerator EndIsolatedPortalView()
        {
            if (_scene.IsValid() && _scene.isLoaded) yield return SceneManager.UnloadSceneAsync(_scene);
            foreach (var camera in _previousMainCameras) if (camera != null) camera.enabled = true;
            _previousMainCameras.Clear();
        }
    }

    /// <summary>使用正式单机场景的出生位置、场景碰撞、投射物 Prefab 和自动世界 Tick。
    /// Editor 直接进入这些场景使用原有 ScenePreview；不加载账号或替换材料配置。</summary>
    public sealed class FormalMaterialImpactPlayModeTests
    {
        private Scene _loaded;
        private float _initialScale;

        [UnityTest] public IEnumerator HomeWaterProjectileReachesTheWorld() =>
            Impact("Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Home.unity", "home");
        [UnityTest] public IEnumerator P7WaterProjectileReachesTheWorld() =>
            Impact("Assets/_Project/Scenes/P7_DemoRun.unity", "p7");
        [UnityTest] public IEnumerator P8WaterProjectileReachesTheWorld() =>
            Impact("Assets/_Project/Scenes/P8_BossField.unity", "p8");

        private IEnumerator Impact(string path, string mapId)
        {
            _initialScale = Time.timeScale;
            _loaded = UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(path, new LoadSceneParameters(LoadSceneMode.Additive));
            yield return null;
            yield return null;
            var world = InMap<ElementWorldRuntime>();
            Assert.That(world, Is.Not.Null);
            Assert.That(world.UsesCellularWorld && world.CellularMapContentId == mapId, Is.True, path);
            Assert.That(ElementRuntimeRegistry.ActiveSink, Is.SameAs(world));
            var player = InMap<WizardController>();
            Assert.That(player, Is.Not.Null);
            InMap<GameplayGuideController>()?.Close();
            Time.timeScale = 1;
            Physics.SyncTransforms();
            Vector3 start = player.transform.position + player.transform.forward * 2.5f + Vector3.up * 2f;
            Assert.That(_loaded.GetPhysicsScene().Raycast(start, Vector3.down, out var ground, 20f,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore), Is.True, "出生点前应有可命中的地面。");

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Elemental/Prefabs/PF_ElementWaterProjectile.prefab");
            var shot = Object.Instantiate(prefab, ground.point + Vector3.up * 1.5f, Quaternion.identity);
            SceneManager.MoveGameObjectToScene(shot, _loaded);
            var projectile = shot.GetComponent<ProjectileBase>();
            Assert.That(shot.GetComponent<ElementDepositOnImpact>().TryBuildRequest(ground.point, out var expected), Is.True);
            ulong initial = world.ConversionLedger.ExternallyInjected(MaterialId.Water);
            ulong writes = world.ProcessedWriteCount;
            ProjectileImpactContext? hit = null;
            projectile.SurfaceImpacted += contact => hit = contact;
            var owner = player.GetComponent<HealthComponent>();
            projectile.Init(owner.TeamId, owner.Id, 0f, DamageType.Magical, Vector3.down * 8f, player.CharacterController, false);
            float until = Time.realtimeSinceStartup + 10f;
            while (Time.realtimeSinceStartup < until)
            {
                if (hit.HasValue && world.ProcessedWriteCount > writes && HasWaterNearImpact(world, hit.Value.Point)) break;
                yield return null;
            }
            Assert.That(hit.HasValue, Is.True, "必须发生真实 Physics 命中。");
            ulong injected = world.ConversionLedger.ExternallyInjected(MaterialId.Water) - initial;
            if (injected != expected.TotalAmount)
                Assert.Fail($"期望 {expected.TotalAmount} GMU，实际入账 {injected}。地面 {ground.collider.name}，" +
                    $"碰撞 {hit.Value.Point:F3}，法线 {hit.Value.SurfaceNormal:F3}，方向 {hit.Value.IncomingDirection:F3}，" +
                    $"原点 {world.Origin:F3}，格距 {world.CellSize}，Tick {world.WorldTick}，队列 {world.PendingWriteCount}，" +
                    $"活跃写入端 {ReferenceEquals(ElementRuntimeRegistry.ActiveSink, world)}，故障 {world.IsSimulationFaulted}。" +
                    DescribeSurface(world, hit.Value));
            Assert.That(HasWaterNearImpact(world, hit.Value.Point), Is.True, "角色及采集瞄准使用的只读接口须读到命中处的真实水量。");
        }

        private static string DescribeSurface(ElementWorldRuntime world, ProjectileImpactContext hit)
        {
            var facts = new System.Text.StringBuilder();
            foreach (float distance in new[] { -.51f, 0f, .51f, 1.51f, 2.51f })
            {
                var cell = Vector3Int.FloorToInt((hit.Point + hit.SurfaceNormal * (distance * world.CellSize) - world.Origin) / world.CellSize);
                var center = world.Origin + ((Vector3)cell + Vector3.one * .5f) * world.CellSize;
                var overlaps = Physics.OverlapBox(center, Vector3.one * (.49f * world.CellSize), Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
                facts.Append($"\n{distance} 格 {cell}，地图 {world.GetTerrainMaterial(cell)}，阻挡 {world.IsMaterialCellBlocked(cell)}，碰撞体 ");
                foreach (var collider in overlaps) facts.Append(collider.name).Append(' ');
            }
            return facts.ToString();
        }

        private static bool HasWaterNearImpact(ElementWorldRuntime world, Vector3 point)
        {
            // 一次自动 Tick 可以在下落后侧移。只看命中附近，同时排除旧 GPU 投影和只入队的数量。
            var center = Vector3Int.FloorToInt((point - world.Origin) / world.CellSize);
            int extent = Mathf.CeilToInt(1.25f / world.CellSize);
            for (int y = -2; y <= extent; y++)
            for (int z = -extent; z <= extent; z++)
            for (int x = -extent; x <= extent; x++)
                if (world.TrySampleMaterial(center + new Vector3Int(x,y,z), MaterialId.Water, out var sample)
                    && sample.QuantityGmu > 0 && (sample.Source == ElementWorldMaterialSource.Cellular
                    || sample.Source == ElementWorldMaterialSource.RestingLiquid)) return true;
            return false;
        }

        private T InMap<T>() where T : Component
        {
            foreach (var root in _loaded.GetRootGameObjects())
                if (root.GetComponentInChildren<T>() is T found) return found;
            return null;
        }

        [UnityTearDown] public IEnumerator Unload()
        {
            if (_loaded.IsValid() && _loaded.isLoaded) yield return SceneManager.UnloadSceneAsync(_loaded);
            Time.timeScale = _initialScale;
        }
    }

    internal static class GameplayRepairChecks
    {
        internal static IEnumerator CheckFire(ElementWorldRuntime world,Vector3 actor)
        {
            var profile=new SerializedObject(world).FindProperty("_profile").objectReferenceValue as ElementWorldProfile;
            Assert.That(profile.MaxFireDownFlowPerTick,Is.EqualTo(255),"Home/P7/P8必须使用同一火焰下落规则");
            var cell=Vector3Int.FloorToInt((actor+Vector3.up*3-world.Origin)/world.CellSize);
            var center=world.Origin+((Vector3)cell+Vector3.one*.5f)*world.CellSize;
            world.SetSimulationPausedForDebug(true);Assert.That(world.TryEnqueueWrite(new ElementWriteRequest(center,MaterialId.Fire,200,.01f,false)),Is.True);world.TickForTests(.1f);
            // TickForTests会先处理写入再模拟，暂停只停止自动Update，不能假设写入后还停在原格。
            Assert.That(world.TryGetCell(cell+Vector3Int.down,out var before)&&before.MaterialKind==MaterialId.Fire,Is.True,"首Tick写入后应真实下落一格");
            world.SetSimulationPausedForDebug(false);world.TickForTests(.1f);
            Assert.That(world.TryGetCell(cell+Vector3Int.down*2,out var below)&&below.MaterialKind==MaterialId.Fire,Is.True,"生产世界真实火焰必须继续下落，不能只动GPU特效");
            yield return null;
        }
    }
}
#endif
