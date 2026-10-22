#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using Game.Character;
using Game.Core;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Unity.Profiling;
using System.Reflection;

namespace Game.Run.Tests
{
    /// <summary>真实F和Single加载往返；同一内存Repository，不接触正式存档。</summary>
    public sealed class HomeScenePlayModeTests:BackpackWorkspacePlayModeTests
    {
        private const string Home="Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Home.unity";
        protected override string ReviewPath=>"Assets/_Project/Scenes/Tests/Hub/SinglePlayer_Hub_HomeReview.unity";
        protected override string EvidencePath=>"TestResults/SinglePlayer/M6-T22-HomeGround";
        private void Warp(HomePortal gate)
        {
            Player.enabled=false;Player.CharacterController.enabled=false;Player.transform.position=gate.transform.position+Vector3.up*.1f;
            Player.CharacterController.enabled=true;Physics.SyncTransforms();
        }
        protected override IEnumerator PrepareExperimentForProfiling()
        {
            // Hub已不再承载旧实验组件；继承的性能用例也必须先按真实F入口到Home，而非恢复被删组件。
            var gate=Find<HomePortal>();Warp(gate);Keys(Key.F);yield return null;Keys();yield return WaitForScene(Home);
            Backpack=null;foreach(var root in Scene.GetRootGameObjects())foreach(var ui in root.GetComponentsInChildren<Game.UI.SinglePlayerInventoryController>(true))
                if(new SerializedObject(ui).FindProperty("_backpackWorkspace").objectReferenceValue!=null)Backpack=ui;
            Assert.That(Backpack,Is.Not.Null);Assert.That(Session.WorldActionsAllowed,Is.True);Player.enabled=false;
        }
        [UnityTearDown]public IEnumerator ReleaseHomeTestTransfer()
        {
            // 性能用例结束在Home，不走回程；跨Scene纯数据Root也须随测试释放，不能留给后续fixture。
            if(HomeSceneTransfer.Current!=null)UnityEngine.Object.Destroy(HomeSceneTransfer.Current.gameObject);
            yield return null;
        }
        private IEnumerator WaitForScene(string path)
        {
            float until=Time.realtimeSinceStartup+45;
            while(Time.realtimeSinceStartup<until)
            {
                var loaded=SceneManager.GetSceneByPath(path);
                if(loaded.IsValid()&&loaded.isLoaded&&(HomeSceneTransfer.Current==null||!HomeSceneTransfer.Current.Loading))
                {Scene=loaded;Session=Find<SinglePlayerInventorySession>();Player=Find<WizardController>();yield return null;yield return null;yield break;}
                yield return null;
            }
            Assert.Fail("转场未完成："+path);
        }
        [UnityTest] public IEnumerator FHomeRoundTripsKeepProfileAndIsolateGroundItems()
        {
            var gate=Find<HomePortal>();Assert.That(gate,Is.Not.Null);
            Assert.That(Find<ExperimentAreaController>(),Is.Null);Assert.That(Session.WorldActionsAllowed,Is.False);
            long currency=Session.Currency;int count=Session.Profile.Items.Count;
            var bagId=Session.EquippedBackpackId;var repo=Session.Repository;
            Warp(gate);yield return null;
            Assert.That(gate.CanInteract(Player.CharacterController),Is.True);
            yield return new WaitForSecondsRealtime(.2f);Assert.That(Scene.path,Is.EqualTo(ReviewPath),"接触门不能自动进入");
            yield return Shot("hub-home-door");
            Keys(Key.F);yield return null;Keys();yield return WaitForScene(Home);
            Assert.That(Session.Repository,Is.SameAs(repo));Assert.That(Session.Currency,Is.EqualTo(currency));
            Assert.That(Session.Profile.Items.Count,Is.EqualTo(count));Assert.That(Session.EquippedBackpackId,Is.EqualTo(bagId));
            Assert.That(Session.Profile.PendingRaid,Is.Null);Assert.That(Session.WorldActionsAllowed,Is.True);Assert.That(Session.CanAccessWarehouse,Is.False);
            Assert.That(Session.ReadOnly.TryGetView("w0",out _),Is.True);
            // 转向回程门拍摄完整金圈；只控制测试视角，不改变Scene作者的相机配置。
            Player.enabled=false;var pivot=Player.transform.Find("CameraRoot");if(pivot!=null)pivot.rotation=Quaternion.Euler(0,180,0);
            yield return Shot("home-gold-return-door");if(pivot!=null)pivot.rotation=Quaternion.identity;
            // 留地资产按稳定SceneKey恢复；和平返城不能把家里的地面物品删成副本遗失。
            EventBus<InventoryCommandEvent>.Publish(new InventoryCommandEvent{InventoryId=Session.ReadOnly.InventoryId,Kind=InventoryCommandKind.Drop,ItemId="fire",ExpectedVersion=Session.ReadOnly.Version});
            yield return null;Assert.That(Session.ReadOnly.TryGetView("fire",out var dropped),Is.True);Assert.That(dropped.Location.Kind,Is.EqualTo(ItemLocationKind.WorldItem));
            yield return Shot("home-flat-experiment");
            var exit=Find<HomePortal>();Warp(exit);yield return null;Keys(Key.F);yield return null;Keys();yield return WaitForScene(ReviewPath);
            Assert.That(Session.Repository,Is.SameAs(repo));Assert.That(Session.Currency,Is.EqualTo(currency));Assert.That(Session.EquippedBackpackId,Is.EqualTo(bagId));
            Assert.That(Session.Profile.PendingRaid,Is.Null);Assert.That(HomeSceneTransfer.Current,Is.Null);
            Assert.That(Vector3.Distance(Player.transform.position,Find<HomePortal>().transform.Find("OutsideArrival").position),Is.LessThan(.7f));
            foreach(var pickup in UnityEngine.Object.FindObjectsByType<WorldItemPickup>(FindObjectsSortMode.None))Assert.That(pickup.ItemId,Is.Not.EqualTo("fire"),"家的落地物不能在据点生成");
            gate=Find<HomePortal>();Warp(gate);yield return null;Keys(Key.F);yield return null;Keys();yield return WaitForScene(Home);
            WorldItemPickup found=null;foreach(var pickup in UnityEngine.Object.FindObjectsByType<WorldItemPickup>(FindObjectsSortMode.None))if(pickup.ItemId=="fire")found=pickup;
            Assert.That(found,Is.Not.Null,"再次回家应恢复同一份落地资产");
            Assert.That(Session.Currency,Is.EqualTo(currency));Assert.That(Session.EquippedBackpackId,Is.EqualTo(bagId));
            exit=Find<HomePortal>();Warp(exit);yield return null;Keys(Key.F);yield return null;Keys();yield return WaitForScene(ReviewPath);
            yield return Shot("hub-return-outside-home");
        }
        [UnityTest] public IEnumerator GroundProbeIsStableAndHasNoScopedAllocation()
        {
            var checker=Player.GroundChecker;Player.enabled=false;
            bool recording=UnityEditorInternal.ProfilerDriver.enabled;bool cpu=UnityEditorInternal.ProfilerDriver.IsAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU);
            try
            {
                UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,true);UnityEditorInternal.ProfilerDriver.enabled=true;
                for(int i=0;i<10;i++){checker.Refresh();yield return null;}int first=UnityEditorInternal.ProfilerDriver.lastFrameIndex+1;
                for(int i=0;i<60;i++){checker.Refresh();yield return null;}
                WriteProfile("ground-probe-profiler",first,UnityEditorInternal.ProfilerDriver.lastFrameIndex,new[]{"Norn.Character.GroundProbe"},"60 steady real Physics refresh frames; whole-frame GC is reported separately");
            }
            finally{UnityEditorInternal.ProfilerDriver.enabled=recording;UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,cpu);}
            Assert.That(checker.IsGrounded,Is.True);
        }
        [UnityTest] public IEnumerator RealWizardStaysOnThinPostThenJumpsAndLeavesSupport()
        {
            var post=GameObject.CreatePrimitive(PrimitiveType.Cube);SceneManager.MoveGameObjectToScene(post,Scene);
            post.transform.localScale=new Vector3(.1f,1.8f,.1f);post.transform.position=new Vector3(100,1.1f,100);
            Player.CharacterController.enabled=false;Player.transform.position=new Vector3(100,2.1f,100);Player.CharacterController.enabled=true;
            Player.VerticalVelocity=-2;Player.StateMachine.ChangeState(Player.GroundedState);Physics.SyncTransforms();
            for(int i=0;i<30;i++)yield return null;
            Assert.That(Player.GroundChecker.IsGrounded,Is.True);Assert.That(Player.StateMachine.CurrentState,Is.SameAs(Player.GroundedState));
            Keys(Key.Space);for(int i=0;i<5;i++)yield return null;Keys();
            Assert.That(Player.VerticalVelocity,Is.GreaterThan(0));Assert.That(Player.StateMachine.CurrentState,Is.SameAs(Player.AirborneState));
            Player.CharacterController.enabled=false;Player.transform.position+=Vector3.right*3;Player.CharacterController.enabled=true;
            Player.VerticalVelocity=-1;Physics.SyncTransforms();yield return null;
            Assert.That(Player.GroundChecker.IsGrounded,Is.False,"离开细柱不能继续粘住Grounded");UnityEngine.Object.Destroy(post);
        }
        [UnityTest] public IEnumerator SaveFailureAndMissingSceneKeepActorAndAssets()
        {
            var gate=Find<HomePortal>();Warp(gate);yield return null;
            var repository=Session.Repository;
            var inner=(IProfileFileSystem)typeof(ProfileSaveRepository).GetField("_files",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(repository);
            var failed=new ProfileSaveRepository(Path.GetDirectoryName(repository.MainPath),Session.Profile.Items.Catalog,new RefuseWriteFiles(inner));
            Assert.That(failed.TryLoad(out _).Success,Is.True);
            var field=typeof(SinglePlayerInventorySession).GetField("_repository",BindingFlags.Instance|BindingFlags.NonPublic);
            uint version=Session.ReadOnly.Version;long currency=Session.Currency;int count=Session.Profile.Items.Count;
            field.SetValue(Session,failed);
            try
            {
                LogAssert.Expect(LogType.Warning,new System.Text.RegularExpressions.Regex("\\[Home\\].*保存失败"));
                Assert.That(gate.Interact(Player.CharacterController),Is.False);
                Assert.That(HomeSceneTransfer.Current,Is.Null);Assert.That(Session.ReadOnly.Version,Is.EqualTo(version));
                Assert.That(Session.Profile.Items.Count,Is.EqualTo(count));Assert.That(Session.Currency,Is.EqualTo(currency));
                Assert.That(Session.Profile.RaidActionsFrozen,Is.False);Assert.That(Time.timeScale,Is.EqualTo(1));
            }
            finally{field.SetValue(Session,repository);}
            var data=new SerializedObject(gate);data.FindProperty("_targetScene").stringValue="Assets/MissingHome.unity";data.ApplyModifiedPropertiesWithoutUndo();
            Assert.That(gate.Interact(Player.CharacterController),Is.False);Assert.That(HomeSceneTransfer.Current,Is.Null);
            Assert.That(Session.Profile.Items.Count,Is.EqualTo(count));Assert.That(Scene.path,Is.EqualTo(ReviewPath));
        }
        private sealed class RefuseWriteFiles:IProfileFileSystem
        {
            private readonly IProfileFileSystem _inner;
            public RefuseWriteFiles(IProfileFileSystem inner)=>_inner=inner;
            public bool Exists(string path)=>_inner.Exists(path);
            public string Read(string path)=>_inner.Read(path);
            public void WriteFlushed(string path,string value)=>throw new IOException("injected home save failure");
            public void Move(string source,string destination)=>_inner.Move(source,destination);
            public void Replace(string source,string destination,string backup)=>_inner.Replace(source,destination,backup);
        }
        private IEnumerator Shot(string name)
        {
            // sceneLoaded早于渐亮结束，截图必须等unscaled淡入和相机LateUpdate完成。
            yield return new WaitForSecondsRealtime(.8f);yield return new WaitForEndOfFrame();Directory.CreateDirectory(EvidencePath);
            string debug="";foreach(var camera in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))debug+=camera.name+" enabled="+camera.enabled+" pos="+camera.transform.position+" forward="+camera.transform.forward+" mask="+camera.cullingMask+" target="+camera.targetTexture+"\n";
            foreach(var group in UnityEngine.Object.FindObjectsByType<CanvasGroup>(FindObjectsSortMode.None))if(group.alpha>.9f)debug+=group.name+" opaque\n";
            File.WriteAllText(EvidencePath+"/"+name+"-render.txt",debug);
            var texture=ScreenCapture.CaptureScreenshotAsTexture();File.WriteAllBytes(EvidencePath+"/"+name+".png",texture.EncodeToPNG());UnityEngine.Object.Destroy(texture);
        }
    }
}
#endif
