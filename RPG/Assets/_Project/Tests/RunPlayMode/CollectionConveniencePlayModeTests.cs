#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using Game.Character;
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
using UnityEngine.UI;
namespace Game.Run.Tests
{
    /// <summary>生产Home的真实材料Owner与输入集成；隔离Review和内存档，不修改正式Scene或存档。</summary>
    public class CollectionConveniencePlayModeTests:BackpackWorkspacePlayModeTests
    {
        protected override string ReviewPath=>"Assets/_Project/Scenes/Tests/Hub/SinglePlayer_Home_CollectionReview.unity";
        protected override string EvidencePath=>"TestResults/SinglePlayer/M6-T24-CollectionConvenience";
        protected override SinglePlayerProfile CustomizeProfile(SinglePlayerProfile p,ItemCatalogIndex catalog)
        {
            Assert.That(catalog.TryFindMaterialContent(5,out var content),Is.True);
            Assert.That(p.Items.TryAdd(new RuntimeItemState("solid",content,0,material:new MaterialItemState(5,100)),new ItemLocation(ItemLocationKind.Backpack,"bag",6,6,0,-1)),Is.True);
            Assert.That(p.Items.TryAdd(new RuntimeItemState("fragment",content,0,material:new MaterialItemState(5,200)),new ItemLocation(ItemLocationKind.Backpack,"bag",7,7,0,-1)),Is.True);
            p.Items.TryGet("b0",out var bottle);catalog.TryGetRules(bottle.ContentId,out var rules);
            var gas=new ContainerComposition(rules.ContainerCapacity,rules.ContainerMaterials);gas.AddReserved(new[]{new MaterialAmount(7,10000)},1);
            Assert.That(p.Items.TryAdd(new RuntimeItemState("gas",bottle.ContentId,0,composition:gas),new ItemLocation(ItemLocationKind.Backpack,"bag",5,0,0,-1)),Is.True);
            return p;
        }
        private sealed class Source:IMaterialTransferSource
        {public bool TryReserve(MaterialAmount[] amounts,int count,out MaterialTransferFailure failure){failure=MaterialTransferFailure.None;return true;}public void Commit(){}public void Cancel(){}}
        private ElementWorldRuntime PrepareWorld()
        {
            Player.enabled=false;Player.CharacterController.enabled=false;Player.transform.position=new Vector3(0,.1f,0);Player.CharacterController.enabled=true;Physics.SyncTransforms();
            var world=Find<ElementWorldRuntime>();world.SetSimulationPausedForDebug(true);return world;
        }
        private void Deposit(byte key,ulong quantity,Vector3 point,float radius)
        {
            var x=new MaterialDepositExchange{WorldId="container-world",Position=point,Radius=radius,Amounts=new[]{new MaterialAmount(key,quantity)},Count=1,Source=new Source()};
            EventBus<MaterialWorldDepositRequestedEvent>.Publish(new MaterialWorldDepositRequestedEvent{Exchange=x});Assert.That(x.Failure,Is.EqualTo(MaterialTransferFailure.None));Assert.That(x.Transferred,Is.EqualTo(quantity));
        }
        private Camera CollectionCamera(Vector3 origin)
        {
            var go=new GameObject("Collection test aim");SceneManager.MoveGameObjectToScene(go,Scene);var camera=go.AddComponent<Camera>();camera.enabled=false;go.transform.position=origin;go.transform.rotation=Quaternion.identity;
            var data=new SerializedObject(Find<MaterialCollectionController>());data.FindProperty("_aimCamera").objectReferenceValue=camera;data.ApplyModifiedPropertiesWithoutUndo();return camera;
        }
        [UnityTest]public IEnumerator HoldFCollectsWideSolidContinuouslyIntoExistingPileAndStopsOnRelease()
        {
            var world=PrepareWorld();CollectionCamera(new Vector3(0,1,-1));Keys(Key.Digit0);yield return null;Keys();yield return null;
            Deposit(5,3000,new Vector3(.35f,1,1),.5f);ulong total=world.FixedSolids.TotalQuantity;
            Keys(Key.F);float end=Time.realtimeSinceStartup+.65f;while(Time.realtimeSinceStartup<end)yield return null;Keys();yield return null;
            Assert.That(Session.Profile.Items.TryGet("fragment",out var pile),Is.True); // 最高填量旧堆优先补齐
            Assert.That(pile.Material.Quantity,Is.GreaterThan(1200),"单次长按应持续采集，不再每按一次只给100");
            Assert.That(world.FixedSolids.TotalQuantity+pile.Material.Quantity+100,Is.EqualTo(total+300));
            int count=0;foreach(var item in Session.Profile.Items)if(item.Item.Material!=null)count++;Assert.That(count,Is.EqualTo(2),"不新造碎片实例");
            ulong after=world.FixedSolids.TotalQuantity;for(int i=0;i<5;i++)yield return null;Assert.That(world.FixedSolids.TotalQuantity,Is.EqualTo(after));
            Backpack.OpenBackpack();yield return Capture("solid-held-collection-backpack");
        }
        [UnityTest]public IEnumerator HoldFCollectsOffCenterGasButDoesNotCollectThroughWall()
        {
            var world=PrepareWorld();CollectionCamera(new Vector3(0,1,-1));Bind(0,"gas");Keys(Key.Digit4);yield return null;Keys();yield return null;
            Deposit(7,512,new Vector3(.38f,1,1),.3f);ulong before=Bottle("gas");
            Keys(Key.F);for(int i=0;i<16;i++)yield return null;Keys();yield return null;
            Assert.That(Bottle("gas"),Is.GreaterThan(before),"宽容瞄准应覆盖偏离中心射线的气团");Assert.That(world.Gases.TotalQuantity+Bottle("gas"),Is.EqualTo(before+512));
            var wall=GameObject.CreatePrimitive(PrimitiveType.Cube);SceneManager.MoveGameObjectToScene(wall,Scene);wall.transform.position=new Vector3(0,1,.5f);wall.transform.localScale=new Vector3(4,3,.1f);Physics.SyncTransforms();
            Deposit(7,128,new Vector3(.38f,1,1),.3f);before=Bottle("gas");Keys(Key.F);for(int i=0;i<12;i++)yield return null;Keys();yield return null;
            Assert.That(Bottle("gas"),Is.EqualTo(before),"放大采集不能穿墙");
        }
        [UnityTest]public IEnumerator HeldLeftPoursMuchMoreActualGasAndConservesIt()
        {
            var world=PrepareWorld();Bind(0,"gas");Keys(Key.Digit4);yield return null;Keys();yield return null;
            var camera=Camera.main;var brain=camera.GetComponent("CinemachineBrain") as Behaviour;if(brain!=null)brain.enabled=false;
            camera.transform.position=new Vector3(0,2,-1);camera.transform.rotation=Quaternion.Euler(55,0,0);yield return null;
            Click(false);yield return null;ulong before=Bottle("gas");Click(true);float end=Time.realtimeSinceStartup+.65f;while(Time.realtimeSinceStartup<end)yield return null;Click(false);yield return null;
            ulong exported=before-Bottle("gas");Assert.That(exported,Is.GreaterThan(1500),"实际气体撒出量应明显提高");Assert.That(world.Gases.TotalQuantity,Is.EqualTo(exported));Assert.That(world.ImportedMaterialQuantity(MaterialId.Steam),Is.EqualTo(exported));
            yield return Capture("gas-fast-pour");
        }
        [UnityTest]public IEnumerator WalletButtonsOrganizeFragmentsAndPickupAllIsIdempotent()
        {
            PrepareWorld();Backpack.OpenBackpack();yield return null;
            var view=Backpack.GetComponent<BackpackWorkspaceView>();var data=new SerializedObject(view);
            var organize=data.FindProperty("_organize").objectReferenceValue as Button;var pickup=data.FindProperty("_pickupAll").objectReferenceValue as Button;
            Assert.That(organize,Is.Not.Null);Assert.That(pickup,Is.Not.Null);organize.onClick.Invoke();yield return null;
            Assert.That(organize.GetComponentInChildren<Text>().text,Is.EqualTo("整理完成"));
            Assert.That(Session.Profile.Items.TryGet("fragment",out var pile),Is.True);Assert.That(pile.Material.Quantity,Is.EqualTo(300));Assert.That(Session.Profile.Items.TryGet("solid",out _),Is.False);
            Session.Profile.Items.TryGetLocation("fragment",out var location);Assert.That(location.Y,Is.Zero);Assert.That(location.X,Is.Zero);
            EventBus<InventoryCommandEvent>.Publish(new InventoryCommandEvent{InventoryId=Session.ReadOnly.InventoryId,Kind=InventoryCommandKind.Drop,ItemId="fire",ExpectedVersion=Session.ReadOnly.Version});yield return null;
            Assert.That(Session.Profile.Items.TryGetLocation("fire",out var ground),Is.True);Assert.That(ground.Kind,Is.EqualTo(ItemLocationKind.WorldItem));
            pickup.onClick.Invoke();yield return null;Session.Profile.Items.TryGetLocation("fire",out var collected);Assert.That(collected.Kind,Is.Not.EqualTo(ItemLocationKind.WorldItem));
            uint version=Session.ReadOnly.Version;pickup.onClick.Invoke();yield return null;Assert.That(Session.ReadOnly.Version,Is.EqualTo(version));yield return Capture("wallet-buttons");
            Assert.That(pickup.GetComponentInChildren<Text>().text,Does.Contain("剩余"));
        }
        [UnityTest]public IEnumerator WarmContinuousCollectionAndAimHaveNoScopedGc()
        {
            var world=PrepareWorld();Deposit(5,3000,new Vector3(1,1,1),.5f);
            var collector=Find<MaterialCollectionController>();for(int i=0;i<4;i++){collector.SubmitSample(new Vector3(1,1,1),true,1);yield return null;}
            var aim=new MaterialCollectionAimExchange{WorldId="container-world",ActorPosition=Player.transform.position,RayOrigin=new Vector3(.7f,1,-1),RayDirection=Vector3.forward,Reach=3,MaximumRayDistance=4,AllowedMaterials=1<<5,SampleRadius=.5f};
            bool recording=UnityEditorInternal.ProfilerDriver.enabled;bool cpu=UnityEditorInternal.ProfilerDriver.IsAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU);
            try
            {
                UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,true);UnityEditorInternal.ProfilerDriver.enabled=true;yield return null;int first=UnityEditorInternal.ProfilerDriver.lastFrameIndex+1;
                uint version=Session.ReadOnly.Version;
                for(int i=0;i<60;i++){aim.Handled=false;EventBus<MaterialCollectionAimRequestedEvent>.Publish(new MaterialCollectionAimRequestedEvent{Exchange=aim});collector.SubmitSample(new Vector3(1,1,1),true,1);yield return null;}
                Assert.That(Session.ReadOnly.Version,Is.GreaterThan(version+30));
                WriteProfile("continuous-solid-and-wide-aim",first,UnityEditorInternal.ProfilerDriver.lastFrameIndex,new[]{"Norn.MaterialTransfer.Execute","Norn.MaterialTransfer.World","Norn.MaterialTransfer.Sample","Norn.MaterialCollection.Aim"},"Production Home; existing material pile, actual GMU export and wide aim after warmup; discrete new identity and button work excluded");
            }
            finally{UnityEditorInternal.ProfilerDriver.enabled=recording;UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,cpu);}
        }
        [UnityTest]public IEnumerator WarmGasPourAndTrajectoryHaveNoScopedGc()
        {
            PrepareWorld();Bind(0,"gas");Keys(Key.Digit4);yield return null;Keys();Click(false);yield return null;
            var camera=Camera.main;var brain=camera.GetComponent("CinemachineBrain") as Behaviour;if(brain!=null)brain.enabled=false;
            camera.transform.position=new Vector3(0,2,-1);camera.transform.rotation=Quaternion.Euler(55,0,0);Click(true);
            for(int i=0;i<8;i++)yield return null;
            bool recording=UnityEditorInternal.ProfilerDriver.enabled;bool cpu=UnityEditorInternal.ProfilerDriver.IsAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU);
            try
            {
                UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,true);UnityEditorInternal.ProfilerDriver.enabled=true;yield return null;int first=UnityEditorInternal.ProfilerDriver.lastFrameIndex+1;
                for(int i=0;i<60;i++){camera.transform.position=new Vector3(i*.04f,2,-1);yield return null;}
                WriteProfile("gas-pour-and-trajectory",first,UnityEditorInternal.ProfilerDriver.lastFrameIndex,new[]{"Norn.ContainerUse.Execute","Norn.MaterialTransfer.Deposit","Norn.ContainerThrow.Trajectory"},"Real left-held gas pour with moving aim, bounded production batches after 8 warmup frames; Editor whole-frame allocation separate");
            }
            finally{Click(false);UnityEditorInternal.ProfilerDriver.enabled=recording;UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,cpu);}
        }
        protected IEnumerator Capture(string name)
        {yield return new WaitForEndOfFrame();Directory.CreateDirectory(EvidencePath);var image=ScreenCapture.CaptureScreenshotAsTexture();File.WriteAllBytes(EvidencePath+"/"+name+".png",image.EncodeToPNG());UnityEngine.Object.Destroy(image);}
    }
    public sealed class HubCollectionButtonsPlayModeTests:CollectionConveniencePlayModeTests
    {
        protected override string ReviewPath=>"Assets/_Project/Scenes/Tests/Hub/SinglePlayer_Hub_CollectionReview.unity";
        [UnityTest]public IEnumerator HubButtonsVisibleAndAuthorLayoutStable()
        {
            Backpack.OpenBackpack();yield return null;var view=Backpack.GetComponent<BackpackWorkspaceView>();var data=new SerializedObject(view);
            var organize=data.FindProperty("_organize").objectReferenceValue as Button;var pickup=data.FindProperty("_pickupAll").objectReferenceValue as Button;
            Assert.That(organize,Is.Not.Null);Assert.That(pickup,Is.Not.Null);var rect=(RectTransform)organize.transform;var position=rect.anchoredPosition;
            for(int i=0;i<5;i++)yield return null;Assert.That(rect.anchoredPosition,Is.EqualTo(position));yield return Capture("hub-wallet-buttons");
        }
    }
}
#endif
