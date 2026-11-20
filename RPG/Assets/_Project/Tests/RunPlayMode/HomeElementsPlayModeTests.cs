#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using Game.Character;
using Game.Combat;
using Game.Core;
using Game.ElementField;
using Game.Materials;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
namespace Game.Run.Tests
{
    /// <summary>隔离Review、内存Repository；通过实际Deposit/输入/转场验证，不碰正式资产存档。</summary>
    public sealed class HomeElementsPlayModeTests : BackpackWorkspacePlayModeTests
    {
        protected override string ReviewPath=>"Assets/_Project/Scenes/Tests/Hub/SinglePlayer_Home_ElementsReview.unity";
        // 派生fixture独立保留截图/Profiler，基类行为与历史M6证据均不改动。
        private readonly string _evidenceDirectory=Path.Combine("TestResults/FullAuditPlayMode/HomeElements",Guid.NewGuid().ToString("N"));
        protected override string EvidencePath=>_evidenceDirectory;
        private sealed class Source:IMaterialTransferSource
        {public bool TryReserve(MaterialAmount[] amounts,int count,out MaterialTransferFailure failure){failure=MaterialTransferFailure.None;return true;}public void Commit(){}public void Cancel(){}}
        private void Deposit(byte key,ulong quantity,Vector3 point,float radius=.3f)
        {
            var exchange=new MaterialDepositExchange{WorldId="container-world",Position=point,Radius=radius,Source=new Source(),Amounts=new[]{new MaterialAmount(key,quantity)},Count=1};
            EventBus<MaterialWorldDepositRequestedEvent>.Publish(new MaterialWorldDepositRequestedEvent{Exchange=exchange});
            Assert.That(exchange.Handled,Is.True);Assert.That(exchange.Failure,Is.EqualTo(MaterialTransferFailure.None),key.ToString());Assert.That(exchange.Transferred,Is.EqualTo(quantity));
        }
        protected override SinglePlayerProfile CustomizeProfile(SinglePlayerProfile profile,ItemCatalogIndex index)
        {
            for(byte key=5;key<=6;key++)
            {
                Assert.That(index.TryFindMaterialContent(key,out var content),Is.True);
                Assert.That(profile.Items.TryAdd(new RuntimeItemState("solid"+key,content,0,material:new MaterialItemState(key,400)),new ItemLocation(ItemLocationKind.Backpack,"bag",key,5,0,-1)),Is.True);
            }
            return profile;
        }
        private static object Property(Component component,string name)=>component.GetType().GetProperty(name).GetValue(component);
        [UnityTest]public IEnumerator ProductionWorldShowsLiquidAndGasAndKeepsPlayerInvulnerable()
        {
            var world=Find<ElementWorldRuntime>();Assert.That(world.IsInitialized,Is.True);Assert.That(Session.WorldActionsAllowed,Is.True);
            // 生产Coarse Liquid格有有限容量；视觉烟测按适中剂量/完整半径投放，不能把容量拒收误判为渲染失败。
            Deposit(1,2048,new Vector3(0,1.2f,3),.75f);Deposit(3,1024,new Vector3(-2,1.2f,3),.75f);Deposit(4,1024,new Vector3(2,1.2f,3),.75f);
            Deposit(7,2000,new Vector3(-2,1,3),.75f);Deposit(8,2000,new Vector3(2,1,3),.75f);
            for(int i=0;i<20;i++)yield return null;
            var liquid=world.GetComponent("GridLiquidFieldSurfaceRenderer");var gas=world.GetComponent("GasFieldVolumeRenderer");
            Assert.That(liquid,Is.Not.Null);Assert.That((int)Property(liquid,"SurfacePages"),Is.GreaterThan(0));
            Assert.That(gas,Is.Not.Null);Assert.That((int)Property(gas,"BoundTextureCount"),Is.GreaterThan(0));
            var health=Player.GetComponent<HealthComponent>();float before=health.CurrentHp;
            health.ReceiveHit(new DamageRequest(999,2,1000,DamageType.True,Player.transform.position,Vector3.forward));
            Assert.That(health.CurrentHp,Is.EqualTo(before));
            foreach(var root in Scene.GetRootGameObjects())foreach(var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                if(behaviour!=null&&behaviour.GetType().Name=="PlayerHealthBar")Assert.That(behaviour.isActiveAndEnabled,Is.False);
            yield return Shot("home-production-phases");
        }
        [UnityTest]public IEnumerator RealGThrowsSolidModelAndReturnsExactGmuToMaterialOwner()
        {
            Player.enabled=false;var world=Find<ElementWorldRuntime>();Bind(0,"solid6");Keys(Key.Digit4);yield return null;Keys();yield return null;
            Keys(Key.G);yield return null;yield return null;
            Keys();yield return null;
            Assert.That(Session.Profile.Items.TryGetLocation("solid6",out var transit),Is.True);Assert.That(transit.Kind,Is.EqualTo(ItemLocationKind.ThrownContainer));
            var projectile=Find<ThrownContainerProjectile>();Assert.That(projectile,Is.Not.Null);
            Assert.That(projectile.GetComponentInChildren<MeshRenderer>(),Is.Not.Null,"飞行的是真实固体模型");
            float until=Time.realtimeSinceStartup+12;
            while(Time.realtimeSinceStartup<until&&Session.Profile.Items.TryGet("solid6",out _))yield return null;
            Assert.That(Session.Profile.Items.TryGet("solid6",out _),Is.False,"命中成功后固体资产应交回世界");
            Assert.That(world.FixedSolids.TotalQuantity,Is.EqualTo(400));
            Assert.That(world.ImportedMaterialQuantity(MaterialId.DriedSlime),Is.EqualTo(400));
        }
        [UnityTest]public IEnumerator HeldFCollectsColliderlessGasIntoSelectedBottle()
        {
            Player.enabled=false;var world=Find<ElementWorldRuntime>();world.SetSimulationPausedForDebug(true);
            // 测试相机只供瞄准，不参与渲染/Cinemachine；防止第三人称墙体误命中遮蔽气团。
            var cameraObject=new GameObject("Gas collection aim");SceneManager.MoveGameObjectToScene(cameraObject,Scene);
            var camera=cameraObject.AddComponent<Camera>();camera.enabled=false;camera.transform.position=new Vector3(0,1,-1);camera.transform.rotation=Quaternion.identity;
            var collector=Find<MaterialCollectionController>();
            var data=new SerializedObject(collector);data.FindProperty("_aimCamera").objectReferenceValue=camera;data.ApplyModifiedPropertiesWithoutUndo();
            Bind(0,"b0");Keys(Key.Digit4);yield return null;Keys();yield return null;
            Deposit(7,128,new Vector3(0,1,1),.3f);ulong before=world.Gases.TotalQuantity;
            Keys(Key.F);for(int i=0;i<25;i++)yield return null;Keys();yield return null;
            Assert.That(Session.Profile.Items.TryGet("b0",out var bottle),Is.True);
            Assert.That(bottle.Composition.GetQuantity(7),Is.GreaterThan(0));
            Assert.That(world.Gases.TotalQuantity+bottle.Composition.GetQuantity(7),Is.EqualTo(before));
            Deposit(7,128,new Vector3(0,1,1),.3f);ulong remaining=world.Gases.TotalQuantity;
            Bind(1,"solid6");Keys(Key.Digit5);yield return null;Keys();yield return null;
            Keys(Key.F);for(int i=0;i<10;i++)yield return null;Keys();
            Assert.That(world.Gases.TotalQuantity,Is.EqualTo(remaining),"固体道具不能借用旧选瓶采气");
        }
        [UnityTest]public IEnumerator HomeReentryKeepsGroundItemsButResetsEveryMaterialPhase()
        {
            var repository=Session.Repository;
            EventBus<InventoryCommandEvent>.Publish(new InventoryCommandEvent{InventoryId=Session.ReadOnly.InventoryId,Kind=InventoryCommandKind.Drop,ItemId="fire",ExpectedVersion=Session.ReadOnly.Version});
            Deposit(1,500,new Vector3(0,1,3));Deposit(5,200,new Vector3(1,1,3));Deposit(7,500,new Vector3(2,1,3));
            var exit=Find<HomePortal>();Warp(exit);yield return null;Keys(Key.F);yield return null;Keys();
            yield return WaitScene("Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity");
            var entrance=Find<HomePortal>();var data=new SerializedObject(entrance);data.FindProperty("_targetScene").stringValue=ReviewPath;data.ApplyModifiedPropertiesWithoutUndo();
            Warp(entrance);yield return null;Keys(Key.F);yield return null;Keys();yield return WaitScene(ReviewPath);
            Assert.That(Session.Repository,Is.SameAs(repository));var world=Find<ElementWorldRuntime>();
            Assert.That(world.GridLiquids.TotalQuantity,Is.Zero);Assert.That(world.FixedSolids.TotalQuantity,Is.Zero);Assert.That(world.Gases.TotalQuantity,Is.Zero);
            bool found=false;foreach(var root in Scene.GetRootGameObjects())foreach(var pickup in root.GetComponentsInChildren<WorldItemPickup>())if(pickup.ItemId=="fire")found=true;
            Assert.That(found,Is.True,"家中的具体掉落物必须恢复");
            Warp(Find<HomePortal>());yield return null;Keys(Key.F);yield return null;Keys();yield return WaitScene("Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity");
        }
        [UnityTest]public IEnumerator HomeRunsCanonicalColdWaterToIceReaction()
        {
            var world=Find<ElementWorldRuntime>();world.SetSimulationPausedForDebug(true);Player.enabled=false;
            Assert.That(world.ExecutionMode,Is.EqualTo(WorldExecutionMode.SinglePlayer));
            var worldData=new SerializedObject(world);
            var conversion=(MaterialConversionProfile)worldData.FindProperty("_conversionProfile").objectReferenceValue;
            var worldProfile=(ElementWorldProfile)worldData.FindProperty("_profile").objectReferenceValue;
            Assert.That(AssetDatabase.GetAssetPath(conversion),Is.EqualTo("Assets/_Project/ScriptableObjects/ElementField/Phases/MaterialConversions_Default.asset"),"必须检验Home实际生产配方，不能换成快速测试Profile。");
            MaterialConversionRule freezing=default;bool foundFreezing=false;
            for(int i=0;i<conversion.RuleCount;i++)
            {
                var rule=conversion.GetRule(i);
                if(rule.Source==MaterialId.Water && rule.FirstProduct==MaterialId.Ice && rule.Condition==MaterialConversionCondition.Freezing && !rule.Disabled)
                {freezing=rule;foundFreezing=true;break;}
            }
            Assert.That(foundFreezing,Is.True);Assert.That(world.ConversionLedger,Is.Not.Null);
            var zone=new GameObject("Canonical cold verification zone");SceneManager.MoveGameObjectToScene(zone,Scene);zone.SetActive(false);zone.transform.position=new Vector3(0,1,3);
            var environment=zone.AddComponent<MaterialEnvironmentAuthoring>();var data=new SerializedObject(environment);
            data.FindProperty("_world").objectReferenceValue=world;data.FindProperty("_temperature").floatValue=-20;data.FindProperty("_size").vector3Value=new Vector3(8,6,8);data.ApplyModifiedPropertiesWithoutUndo();zone.SetActive(true);
            var cold=environment.Sample;Assert.That(MaterialConversionEvaluator.Matches(in freezing,in cold),Is.True);
            const ulong waterInput=1000;
            Assert.That(world.GridLiquids.TotalQuantity+world.FixedSolids.TotalQuantity+world.Gases.TotalQuantity,Is.Zero,"隔离Review应从空材料世界开始。");
            Deposit(1,waterInput,new Vector3(0,.3f,3),.3f);
            Assert.That(world.GridLiquids.TotalQuantity,Is.EqualTo(waterInput));
            ulong consumedBefore=world.ConversionLedger.Consumed(MaterialId.Water),producedBefore=world.ConversionLedger.Produced(MaterialId.Ice);
            float step=1f/worldProfile.TickRate;long startTick=world.WorldTick;
            // Pause同时约束自动Update与显式测试入口。先用负控制锁住这个生产合同，不能为测试绕过冻结门。
            Assert.That(world.TickForTests(step),Is.Zero);yield return null;
            Assert.That(world.WorldTick,Is.EqualTo(startTick));
            Assert.That(world.GridLiquids.TotalQuantity,Is.EqualTo(waterInput));
            Assert.That(world.FixedSolids.TotalQuantity,Is.Zero);Assert.That(world.Gases.TotalQuantity,Is.Zero);
            Assert.That(world.ConversionLedger.Consumed(MaterialId.Water),Is.EqualTo(consumedBefore));
            Assert.That(world.ConversionLedger.Produced(MaterialId.Ice),Is.EqualTo(producedBefore));

            // 生产扫描每Tick只访32粗格；8x6x8m冷区在0.5m格上有3072格，完整轮转约96 Tick。
            // 冻结还要求整格纯水积足Credit。按全部1000 GMU/生产64 GMU/s留足信用时间，
            // 再留最多每GMU一个候选的队列轮转与提交边界；这是功能推进预算，不是扩大墙钟timeout。
            Vector3Int minimum=Vector3Int.FloorToInt((environment.Bounds.min-world.GridLiquids.Origin)/world.GridLiquids.CellSize);
            Vector3Int maximum=Vector3Int.CeilToInt((environment.Bounds.max-world.GridLiquids.Origin)/world.GridLiquids.CellSize);
            Vector3Int size=maximum-minimum;int cells=size.x*size.y*size.z;
            int scanTicks=(cells+31)/32+1;
            int creditTicks=Mathf.CeilToInt((float)waterInput/(freezing.InputGmuPerSecond*step));
            int queueTicks=Mathf.CeilToInt(Mathf.Min((float)waterInput,conversion.MaximumCandidates)/conversion.CandidatesPerTick);
            int maximumTicks=scanTicks+creditTicks+queueTicks+2,acceptedTicks=0;
            for(int i=0;i<maximumTicks && world.FixedSolids.TotalQuantity==0;i++)
            {
                // 只同步推进一个明确的模拟步；yield之前恢复Pause，防自动Update偷偷贡献成功条件。
                int advanced;
                world.SetSimulationPausedForDebug(false);
                try{advanced=world.TickForTests(step);}
                finally{world.SetSimulationPausedForDebug(true);}
                Assert.That(advanced,Is.EqualTo(1),"每次显式推进必须真正接受一个生产Tick。");acceptedTicks+=advanced;
                Assert.That(world.WorldTick-startTick,Is.EqualTo((long)acceptedTicks));
                yield return null;
                Assert.That(world.WorldTick-startTick,Is.EqualTo((long)acceptedTicks),"等待渲染帧时Pause必须继续阻止自动模拟。");
            }
            ulong water=world.GridLiquids.TotalQuantity,ice=world.FixedSolids.TotalQuantity,gas=world.Gases.TotalQuantity;
            ulong consumed=world.ConversionLedger.Consumed(MaterialId.Water)-consumedBefore;
            ulong produced=world.ConversionLedger.Produced(MaterialId.Ice)-producedBefore;
            string report="acceptedTicks="+acceptedTicks+"/"+maximumTicks+",scanTicks="+scanTicks+",creditTicks="+creditTicks+",queueTicks="+queueTicks
                +",water="+water+",ice="+ice+",gas="+gas+",consumedWater="+consumed+",producedIce="+produced
                +",pendingCandidates="+world.ConversionPendingCandidates+",processedLastTick="+world.ConversionProcessedLastTick
                +",deferred="+world.ConversionDeferredTransactions+",overflows="+world.ConversionCandidateOverflows;
            Directory.CreateDirectory(EvidencePath);File.WriteAllText(Path.Combine(EvidencePath,"home-cold-reaction.txt"),report);TestContext.WriteLine(report);
            Assert.That(acceptedTicks,Is.GreaterThan(0));
            Assert.That(ice,Is.GreaterThan(0),"使用生产Conversion Profile的冷水结冰链路；"+report);
            Assert.That(consumed,Is.GreaterThan(0));Assert.That(produced,Is.EqualTo(ice));
            Assert.That(consumed,Is.EqualTo(produced),"Water到Ice必须按生产1:1配方提交同一批GMU。");
            Assert.That(water,Is.EqualTo(waterInput-consumed));Assert.That(gas,Is.Zero);
            Assert.That(water+ice+gas,Is.EqualTo(waterInput),"真实三相Owner中的总量必须守恒。");
            Assert.That(world.TryCaptureMaterialQuantityFamily(MaterialQuantityFamily.WaterBased,out var family),Is.True);
            Assert.That(family.IsBalanced,Is.True);Assert.That(family.Pending+family.Vented+family.RuleConsumed+family.Exported,Is.Zero);
        }
        [UnityTest]public IEnumerator ProductionSolidGravityAndVolumeAimHaveNoScopedGcAfterWarmup()
        {
            Player.enabled=false;var world=Find<ElementWorldRuntime>();world.SetSimulationPausedForDebug(true);
            Deposit(7,128,new Vector3(0,1,1),.3f);Deposit(5,512,new Vector3(2,4,2),.5f);
            var aim=new MaterialCollectionAimExchange{WorldId="container-world",ActorPosition=Player.transform.position,RayOrigin=new Vector3(0,1,-1),RayDirection=Vector3.forward,Reach=3,MaximumRayDistance=4,AllowedMaterials=1<<7};
            for(int i=0;i<8;i++){world.FixedSolids.StepGravity(.025f);aim.Handled=false;EventBus<MaterialCollectionAimRequestedEvent>.Publish(new MaterialCollectionAimRequestedEvent{Exchange=aim});yield return null;}
            bool recording=UnityEditorInternal.ProfilerDriver.enabled;bool cpu=UnityEditorInternal.ProfilerDriver.IsAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU);
            try
            {
                UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,true);UnityEditorInternal.ProfilerDriver.enabled=true;
                yield return null;int first=UnityEditorInternal.ProfilerDriver.lastFrameIndex+1;
                uint moves=world.FixedSolids.GravityMoves;
                for(int i=0;i<60;i++){world.FixedSolids.StepGravity(.025f);aim.Handled=false;EventBus<MaterialCollectionAimRequestedEvent>.Publish(new MaterialCollectionAimRequestedEvent{Exchange=aim});yield return null;}
                Assert.That(world.FixedSolids.GravityMoves,Is.GreaterThan(moves),"必须实际发生下落，而非空场景零分配");
                DumpGravityAllocationContext(first,UnityEditorInternal.ProfilerDriver.lastFrameIndex);
                WriteProfile("home-solid-gas-profiler",first,UnityEditorInternal.ProfilerDriver.lastFrameIndex,new[]{"FixedSolid.IslandGravity","FixedSolid.PublishGeometry","Norn.MaterialCollection.Aim"},"Production Home Review; actual falling solid and colliderless material query, 8 warmup frames excluded; whole-frame Editor allocation separate");
            }
            finally{UnityEditorInternal.ProfilerDriver.enabled=recording;UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,cpu);}
        }
        private void DumpGravityAllocationContext(int first,int last)
        {
            var text=new System.Text.StringBuilder();
            for(int frame=first;frame<=last;frame++)
            {
                using var data=UnityEditorInternal.ProfilerDriver.GetRawFrameDataView(frame,0);if(!data.valid)continue;
                for(int i=0;i<data.sampleCount;i++)if(data.GetSampleName(i)=="FixedSolid.IslandGravity")
                {
                    int end=i+data.GetSampleChildrenCountRecursive(i);
                    for(int n=i;n<=end;n++)if(data.GetSampleName(n)=="GC.Alloc")
                    {
                        text.AppendLine("Frame "+frame);
                        for(int k=Math.Max(i,n-12);k<=Math.Min(end,n+2);k++)text.AppendLine(k+" "+data.GetSampleName(k)+" children="+data.GetSampleChildrenCountRecursive(k));
                    }
                }
            }
            Directory.CreateDirectory(EvidencePath);File.WriteAllText(EvidencePath+"/gravity-gc-context.txt",text.ToString());
        }
        private void Warp(HomePortal gate)
        {Player.enabled=false;Player.CharacterController.enabled=false;Player.transform.position=gate.transform.position+Vector3.up*.1f;Player.CharacterController.enabled=true;Physics.SyncTransforms();}
        private IEnumerator WaitScene(string path)
        {
            float until=Time.realtimeSinceStartup+45;
            while(Time.realtimeSinceStartup<until)
            {var scene=SceneManager.GetSceneByPath(path);if(scene.isLoaded&&(HomeSceneTransfer.Current==null||!HomeSceneTransfer.Current.Loading)){Scene=scene;Session=Find<SinglePlayerInventorySession>();Player=Find<WizardController>();yield return null;yield return null;yield break;}yield return null;}
            Assert.Fail("转场超时 "+path);
        }
        private IEnumerator Shot(string name)
        {yield return new WaitForSecondsRealtime(.5f);yield return new WaitForEndOfFrame();Directory.CreateDirectory(EvidencePath);var texture=ScreenCapture.CaptureScreenshotAsTexture();File.WriteAllBytes(EvidencePath+"/"+name+".png",texture.EncodeToPNG());UnityEngine.Object.Destroy(texture);}
    }
}
#endif
