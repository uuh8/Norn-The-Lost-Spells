using System;
using System.Reflection;
using Game.Materials;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    // 第一轮先检查生产接口缺失；后续用同一入口验证真实事务，不以视觉粒子数代替材料量。
    public sealed class MaterialConversionTests
    {
        private static Type ProductionType(string name) => typeof(ElementWorldRuntime).Assembly.GetType("Game.ElementField." + name);
        [Test]
        public void ConversionFoundationProvidesQuantityTransactionAndEnvironmentRules()
        {
            Assert.That(ProductionType("MaterialConversionTransaction"), Is.Not.Null, "缺少跨形态原子事务。");
            Assert.That(ProductionType("MaterialConversionProfile"), Is.Not.Null);
            Assert.That(ProductionType("MaterialEnvironmentAuthoring"), Is.Not.Null);
            Assert.That(ProductionType("MaterialWorldQuantityLedger"), Is.Not.Null);
        }

        [Test]
        public void DryingKeepsRecipeRemainderAndSeparateTemperatureThresholds()
        {
            var profile=ScriptableObject.CreateInstance<MaterialConversionProfile>();
            try
            {
                Assert.That(profile.IsValid,Is.True);
                var dry=profile.GetRule(4);
                Assert.That(MaterialConversionEvaluator.Batches(in dry,7,20),Is.EqualTo(1));
                Assert.That(MaterialConversionEvaluator.Batches(in dry,3,20),Is.Zero);
                var middle=new MaterialEnvironmentSample(50,.8f);
                var heat=profile.GetRule(0); var cold=profile.GetRule(1);
                Assert.That(MaterialConversionEvaluator.Matches(in heat,in middle),Is.False);
                Assert.That(MaterialConversionEvaluator.Matches(in cold,in middle),Is.False);
                var invalid=dry; invalid.SecondUnits=2;
                Assert.That(invalid.IsValid,Is.False);
                Assert.That(MaterialWorldQuantityLedger.Family(MaterialId.Fire),Is.EqualTo(MaterialQuantityFamily.None));
            }
            finally { UnityEngine.Object.DestroyImmediate(profile); }
        }

        private sealed class ReservationPort : IMaterialConversionPort
        {
            public uint Source=7, Version, StampVersion;
            public bool SecondTargetAvailable=true, Prepared;
            public uint First,Second;
            public bool TryPrepare(MaterialConversionAddress source,uint consume,MaterialConversionPlacement[] targets,int count,out MaterialConversionStamp stamp)
            {
                stamp=default;
                if(Prepared || !SecondTargetAvailable || Source<consume) return false;
                StampVersion=Version; Prepared=true; stamp=new MaterialConversionStamp(0,0,Version,Source); return true;
            }
            public bool TryCommit(MaterialConversionAddress source,uint consume,MaterialConversionPlacement[] targets,int count,in MaterialConversionStamp stamp)
            {
                if(!Prepared || stamp.Gas!=Version || Source!=stamp.SourceQuantity) return false;
                Source-=consume; First+=targets[0].Quantity; Second+=targets[1].Quantity; Prepared=false; return true;
            }
            public void Cancel() => Prepared=false;
        }
        [Test]
        public void TwoProductReservationFailureVersionChangeAndDuplicateCommitNeverLoseQuantity()
        {
            var port=new ReservationPort {SecondTargetAvailable=false};
            var ledger=new MaterialWorldQuantityLedger(); var transaction=new MaterialConversionTransaction(port,ledger);
            var source=new MaterialConversionAddress(Vector3Int.zero,MaterialId.Sticky);
            var targets=new[] {new MaterialConversionPlacement(Vector3Int.zero,MaterialId.DriedSlime,3),new MaterialConversionPlacement(Vector3Int.up,MaterialId.Steam,1)};
            Assert.That(transaction.TryPrepare(source,4,targets,2),Is.False);
            Assert.That(port.Source,Is.EqualTo(7)); Assert.That(ledger.CommittedTransactions,Is.Zero);
            port.SecondTargetAvailable=true;
            Assert.That(transaction.TryPrepare(source,4,targets,2),Is.True);
            Assert.That(port.Source,Is.EqualTo(7),"Prepared 期间源仍拥有数量。");
            port.Version++; Assert.That(transaction.Commit(),Is.False); Assert.That(port.Source,Is.EqualTo(7));
            Assert.That(transaction.TryPrepare(source,4,targets,2),Is.True); transaction.Cancel();
            Assert.That(transaction.TryPrepare(source,4,targets,2),Is.True); Assert.That(transaction.Commit(),Is.True);
            Assert.That(transaction.Commit(),Is.False);
            Assert.That(port.Source+port.First+port.Second,Is.EqualTo(7));
            Assert.That(ledger.Consumed(MaterialId.Sticky),Is.EqualTo(4UL));
            Assert.That(ledger.Produced(MaterialId.DriedSlime)+ledger.Produced(MaterialId.Steam),Is.EqualTo(4UL));
        }

        private sealed class World : IDisposable
        {
            internal readonly GameObject Host=new GameObject("Conversion Test World");
            internal readonly ElementWorldRuntime Runtime;
            internal readonly LiquidWorldRuntime Liquid;
            internal readonly FixedSolidWorldRuntime Solid;
            internal readonly GasWorldRuntime Gas;
            internal readonly MaterialConversionWorldPort Port;
            private readonly ElementWorldProfile _world=ScriptableObject.CreateInstance<ElementWorldProfile>();
            private readonly LiquidWorldProfile _liquid=ScriptableObject.CreateInstance<LiquidWorldProfile>();
            private readonly FixedSolidWorldProfile _solid=ScriptableObject.CreateInstance<FixedSolidWorldProfile>();
            private readonly GasWorldProfile _gas=ScriptableObject.CreateInstance<GasWorldProfile>();
            private readonly LiquidBehaviorProfile _water=ScriptableObject.CreateInstance<LiquidBehaviorProfile>();
            private readonly LiquidBehaviorProfile _sticky=ScriptableObject.CreateInstance<LiquidBehaviorProfile>();
            internal static void Set(object target,string name,object value) => target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(target,value);
            internal World(int gasCapacity=4096,int liquidScale=1,float liquidVolumeMultiplier=1,int solidCapacity=1024)
            {
                Host.hideFlags=HideFlags.DontSave;
                try
                {
                Host.SetActive(false);
                JsonUtility.FromJsonOverwrite("{\"_cellSize\":1,\"_chunkSize\":2,\"_activeRadiusXZChunks\":2,\"_activeRadiusYChunks\":2}",_world);
                JsonUtility.FromJsonOverwrite("{\"_resolutionScale\":1,\"_unitVolumeMultiplier\":1,\"_maximumChunks\":8,\"_maximumActiveCells\":128,\"_maximumCellsPerTick\":32,\"_maximumStableRegions\":4,\"_maximumStableOutlets\":4,\"_maximumStableSurfacePatches\":16,\"_maximumAutoCandidateCells\":64}",_liquid);
                JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":8,\"_maximumNavigationBoxesPerChunk\":8}",_solid);
                Set(_liquid,"_resolutionScale",liquidScale); Set(_liquid,"_unitVolumeMultiplier",liquidVolumeMultiplier);
                Set(_solid,"_cellCapacityGmu",solidCapacity);
                JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":8,\"_maximumActiveCells\":128,\"_maximumCellsPerTick\":32,\"_cellCapacityGmu\":"+gasCapacity+"}",_gas);
                JsonUtility.FromJsonOverwrite("{\"_material\":4}",_sticky);
                Set(_liquid,"_materials",new[]{_water,_sticky});
                _liquid.Validate();
                Runtime=Host.AddComponent<ElementWorldRuntime>(); Set(Runtime,"_profile",_world); Set(Runtime,"_origin",Vector3.one*100);
                Set(Runtime,"_hasInterestChunk",true); Set(Runtime,"<IsInitialized>k__BackingField",true);
                var backends=new MaterialSimulationBackendKind[256]; var present=new bool[256];
                foreach(var id in new[]{MaterialId.Water,MaterialId.Sticky,MaterialId.Ice,MaterialId.DriedSlime,MaterialId.Steam,MaterialId.ToxicVapor})
                { present[(byte)id]=true; backends[(byte)id]=id==MaterialId.Water || id==MaterialId.Sticky?MaterialSimulationBackendKind.GridLiquid:id==MaterialId.Ice || id==MaterialId.DriedSlime?MaterialSimulationBackendKind.FixedSolid:MaterialSimulationBackendKind.GridGas; }
                var routes=(MaterialSimulationRouteSnapshot)Activator.CreateInstance(typeof(MaterialSimulationRouteSnapshot),BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{backends,present},null);
                Set(Runtime,"_materialRoutes",routes);
                var baker=Host.GetComponent<ElementFieldSolidBaker>() ?? Host.AddComponent<ElementFieldSolidBaker>();
                Liquid=Host.AddComponent<LiquidWorldRuntime>(); Solid=Host.AddComponent<FixedSolidWorldRuntime>(); Gas=Host.AddComponent<GasWorldRuntime>();
                Set(Liquid,"_profile",_liquid); Set(Solid,"_profile",_solid); Set(Gas,"_profile",_gas);
                Set(Runtime,"_gridLiquidRuntime",Liquid); Set(Runtime,"_fixedSolidRuntime",Solid); Set(Runtime,"_gasRuntime",Gas);
                Assert.That(Liquid.TryInitialize(Vector3.one*100,1,2,baker),Is.True,"Liquid init; profile="+_liquid.name+", materials="+_liquid.MaterialCount+", baker="+(baker!=null));
                Assert.That(Solid.TryInitialize(Runtime,baker,Vector3.one*100,1,2),Is.True);
                Assert.That(Gas.TryInitialize(Runtime,baker,Vector3.one*100,1,2),Is.True);
                baker.SetMaterialSolids(Solid,Vector3.one*100,1); Set(Runtime,"_solidBaker",baker);
                Port=new MaterialConversionWorldPort(Runtime,Liquid,Solid,Gas);
                }
                catch { Dispose(); throw; }
            }
            public void Dispose()
            { UnityEngine.Object.DestroyImmediate(Host); foreach(var asset in new UnityEngine.Object[]{_world,_liquid,_solid,_gas,_water,_sticky}) UnityEngine.Object.DestroyImmediate(asset); }
        }
        [Test]
        public void RealOwnersRollbackBothProductsWhenSteamCapacityIsFull()
        {
            using var world=new World(1);
            Assert.That(world.Liquid.TryAddInitial(Vector3Int.zero,MaterialId.Sticky,7),Is.True);
            Assert.That(world.Gas.StageConversionAdd(Vector3Int.up,MaterialId.Steam,1),Is.True);
            var ledger=new MaterialWorldQuantityLedger(); var transaction=new MaterialConversionTransaction(world.Port,ledger);
            var targets=new[]{new MaterialConversionPlacement(Vector3Int.zero,MaterialId.DriedSlime,3),new MaterialConversionPlacement(Vector3Int.up,MaterialId.Steam,1)};
            Assert.That(transaction.TryPrepare(new MaterialConversionAddress(Vector3Int.zero,MaterialId.Sticky),4,targets,2),Is.False);
            Assert.That(world.Liquid.TotalQuantity,Is.EqualTo(7UL)); Assert.That(world.Solid.TotalQuantity,Is.Zero);
            Assert.That(world.Gas.TotalQuantity,Is.EqualTo(1UL)); Assert.That(world.Solid.SurfaceRebuildCount,Is.Zero);
        }
        [Test]
        public void RealDryingCommitsResidueAndSteamAndLeavesThreeUnitRemainder()
        {
            using var world=new World(); world.Liquid.TryAddInitial(Vector3Int.zero,MaterialId.Sticky,7);
            var ledger=new MaterialWorldQuantityLedger(); var transaction=new MaterialConversionTransaction(world.Port,ledger);
            var targets=new[]{new MaterialConversionPlacement(Vector3Int.zero,MaterialId.DriedSlime,3),new MaterialConversionPlacement(Vector3Int.up,MaterialId.Steam,1)};
            Assert.That(transaction.TryPrepare(new MaterialConversionAddress(Vector3Int.zero,MaterialId.Sticky),4,targets,2),Is.True);
            Assert.That(world.Liquid.TotalQuantity,Is.EqualTo(7UL)); Assert.That(world.Solid.TotalQuantity,Is.Zero);
            Assert.That(transaction.Commit(),Is.True);
            Assert.That(world.Liquid.TotalQuantity,Is.EqualTo(3UL)); Assert.That(world.Solid.TotalQuantity,Is.EqualTo(3UL)); Assert.That(world.Gas.TotalQuantity,Is.EqualTo(1UL));
            Assert.That(world.Solid.SurfaceRebuildCount,Is.EqualTo(1));
        }
        [Test]
        public void ChangedSourceCancelsRealPreparedTransactionWithoutConsumingIt()
        {
            using var world=new World(); world.Liquid.TryAddInitial(Vector3Int.zero,MaterialId.Water,20);
            var transaction=new MaterialConversionTransaction(world.Port,new MaterialWorldQuantityLedger());
            var targets=new[]{new MaterialConversionPlacement(Vector3Int.up,MaterialId.Steam,2)};
            Assert.That(transaction.TryPrepare(new MaterialConversionAddress(Vector3Int.zero,MaterialId.Water),2,targets,1),Is.True);
            Assert.That(world.Liquid.TryConsume(Vector3Int.zero,MaterialId.Water,1),Is.True);
            Assert.That(transaction.Commit(),Is.False); Assert.That(world.Liquid.TotalQuantity,Is.EqualTo(19UL)); Assert.That(world.Gas.TotalQuantity,Is.Zero);
        }
        [Test]
        public void FrozenConversionRestoresWithoutWallClockCatchUp()
        {
            using var world=new World(); var profile=ScriptableObject.CreateInstance<MaterialConversionProfile>();
            try
            {
                var scheduler=new MaterialConversionScheduler(world.Runtime,world.Liquid,world.Solid,world.Gas,profile);
                world.Gas.StageConversionAdd(Vector3Int.zero,MaterialId.Steam,100);
                World.Set(world.Runtime,"_hasInterestChunk",false);
                var domain=new ElementWorldActivityRegions();
                for(int i=0;i<300;i++) { scheduler.BeginTick(.1f,domain); scheduler.Step(.1f); }
                Assert.That(world.Gas.TotalQuantity,Is.EqualTo(100UL)); Assert.That(world.Liquid.TotalQuantity,Is.Zero);
                World.Set(world.Runtime,"_hasInterestChunk",true); domain.TryAdd(world.Runtime.GetActiveWorldBounds());
                scheduler.BeginTick(.1f,domain); scheduler.Step(.1f);
                Assert.That(world.Gas.TotalQuantity,Is.EqualTo(99UL)); Assert.That(world.Liquid.TotalQuantity,Is.EqualTo(1UL));
            }
            finally { UnityEngine.Object.DestroyImmediate(profile); }
        }

        [Test]
        public void RealFreezeRejectsActorThenCommitsAndMeltsWithoutLosingWater()
        {
            using var world=new World(); world.Liquid.TryAddInitial(Vector3Int.zero,MaterialId.Water,100);
            var actor=new GameObject("Conversion Actor"); actor.transform.position=new Vector3(100.5f,100,100.5f);
            var controller=actor.AddComponent<CharacterController>(); controller.center=Vector3.up; controller.height=2;
            var transaction=new MaterialConversionTransaction(world.Port,new MaterialWorldQuantityLedger());
            var ice=new[]{new MaterialConversionPlacement(Vector3Int.zero,MaterialId.Ice,100)};
            try
            {
                Physics.SyncTransforms();
                Assert.That(transaction.TryPrepare(new MaterialConversionAddress(Vector3Int.zero,MaterialId.Water),100,ice,1),Is.False);
                Assert.That(world.Liquid.TotalQuantity,Is.EqualTo(100UL)); Assert.That(world.Solid.TotalQuantity,Is.Zero);
                actor.transform.position+=Vector3.right*20; Physics.SyncTransforms();
                Assert.That(transaction.TryPrepare(new MaterialConversionAddress(Vector3Int.zero,MaterialId.Water),100,ice,1),Is.True);
                Assert.That(transaction.Commit(),Is.True);
                Assert.That(world.Solid.BlocksCell(Vector3Int.zero),Is.True); Assert.That(world.Liquid.TotalQuantity,Is.Zero);
                var water=new[]{new MaterialConversionPlacement(Vector3Int.zero,MaterialId.Water,100)};
                Assert.That(transaction.TryPrepare(new MaterialConversionAddress(Vector3Int.zero,MaterialId.Ice),100,water,1),Is.True);
                Assert.That(transaction.Commit(),Is.True);
                Assert.That(world.Solid.BlocksCell(Vector3Int.zero),Is.False); Assert.That(world.Solid.TotalQuantity,Is.Zero);
                Assert.That(world.Liquid.TotalQuantity,Is.EqualTo(100UL)); Assert.That(world.Solid.SurfaceRebuildCount,Is.EqualTo(2));
            }
            finally { UnityEngine.Object.DestroyImmediate(actor); }
        }
        [Test]
        public void StableWaterEvaporationPreservesOwnerOnCancelAndSupportsCommit()
        {
            using var world=new World();
            Assert.That(world.Liquid.TryRegisterStableRegion(MaterialId.Water,Vector3Int.zero,Vector3Int.one,0,0,100,out _),Is.True);
            var transaction=new MaterialConversionTransaction(world.Port,new MaterialWorldQuantityLedger());
            var steam=new[]{new MaterialConversionPlacement(Vector3Int.up,MaterialId.Steam,2)};
            Assert.That(transaction.TryPrepare(new MaterialConversionAddress(Vector3Int.zero,MaterialId.Water),2,steam,1),Is.True);
            transaction.Cancel(); Assert.That(world.Liquid.StableQuantity,Is.EqualTo(100UL)); Assert.That(world.Liquid.TotalQuantity,Is.EqualTo(100UL));
            Assert.That(transaction.TryPrepare(new MaterialConversionAddress(Vector3Int.zero,MaterialId.Water),2,steam,1),Is.True); Assert.That(transaction.Commit(),Is.True);
            Assert.That(world.Liquid.StableQuantity,Is.EqualTo(98UL)); Assert.That(world.Gas.TotalQuantity,Is.EqualTo(2UL));
        }
        [Test]
        public void ClosedConversionFamilyConservesQuantityAcrossThousandCyclesWithZeroManagedAllocAfterWarmup()
        {
            using var world=new World(); world.Liquid.TryAddInitial(Vector3Int.zero,MaterialId.Water,100);
            var ledger=new MaterialWorldQuantityLedger(); var transaction=new MaterialConversionTransaction(world.Port,ledger);
            var steam=new[]{new MaterialConversionPlacement(Vector3Int.up,MaterialId.Steam,1)};
            var water=new[]{new MaterialConversionPlacement(Vector3Int.zero,MaterialId.Water,1)};
            var wetAddress=new MaterialConversionAddress(Vector3Int.zero,MaterialId.Water);
            var gasAddress=new MaterialConversionAddress(Vector3Int.up,MaterialId.Steam);
            // 首次 Terrain/Dictionary/Physics 查询暖机；后续通过真实三个 Owner 的数量路径。
            for(int i=0;i<20;i++) { transaction.TryPrepare(wetAddress,1,steam,1); transaction.Commit(); transaction.TryPrepare(gasAddress,1,water,1); transaction.Commit(); }
            long before=GC.GetAllocatedBytesForCurrentThread();
            bool succeeded=true;
            for(int i=0;i<1000;i++)
            {
                succeeded&=transaction.TryPrepare(wetAddress,1,steam,1) && transaction.Commit();
                succeeded&=transaction.TryPrepare(gasAddress,1,water,1) && transaction.Commit();
            }
            long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
            Assert.That(succeeded,Is.True); Assert.That(allocated,Is.Zero);
            Assert.That(world.Liquid.TotalQuantity+world.Gas.TotalQuantity+world.Solid.TotalQuantity,Is.EqualTo(100UL));
            Assert.That(ledger.Consumed(MaterialId.Water),Is.EqualTo(1020UL)); Assert.That(ledger.Produced(MaterialId.Water),Is.EqualTo(1020UL));
        }
        [Test]
        public void FamilyAuditCountsPendingVentsAndExplicitFuelConsumptionExactlyOnce()
        {
            using var world=new World(64);
            var profile=ScriptableObject.CreateInstance<MaterialConversionProfile>();
            var ventHost=new GameObject("Conversion Vent"); ventHost.transform.position=Vector3.one*101;
            try
            {
                var scheduler=new MaterialConversionScheduler(world.Runtime,world.Liquid,world.Solid,world.Gas,profile);
                World.Set(world.Runtime,"_conversionScheduler",scheduler);
                world.Liquid.TryAddInitial(Vector3Int.zero,MaterialId.Sticky,7);
                var transaction=new MaterialConversionTransaction(world.Port,scheduler.Ledger);
                var targets=new[]{new MaterialConversionPlacement(Vector3Int.zero,MaterialId.DriedSlime,3),new MaterialConversionPlacement(Vector3Int.up,MaterialId.Steam,1)};
                Assert.That(transaction.TryPrepare(new MaterialConversionAddress(Vector3Int.zero,MaterialId.Sticky),4,targets,2) && transaction.Commit(),Is.True);
                var steam=new ElementWriteRequest(Vector3.one*100+Vector3.one*.1f,MaterialId.Steam,4,0,false);
                world.Gas.TryEnqueueDeposit(in steam);
                Assert.That(world.Runtime.TryCaptureMaterialQuantityFamily(MaterialQuantityFamily.WaterBased,out var pending),Is.True);
                Assert.That(pending.Injected,Is.EqualTo(11UL)); Assert.That(pending.Pending,Is.EqualTo(4UL)); Assert.That(pending.IsBalanced,Is.True);
                var vent=ventHost.AddComponent<GasVentAuthoring>(); World.Set(vent,"_size",Vector3.one*8); World.Set(vent,"_gmuPerSecond",100f);
                world.Gas.RegisterVent(vent); world.Gas.Step(.1f);
                Assert.That(world.Liquid.TryConsume(Vector3Int.zero,MaterialId.Sticky,1,true),Is.True);
                Assert.That(world.Runtime.TryCaptureMaterialQuantityFamily(MaterialQuantityFamily.WaterBased,out var final),Is.True);
                Assert.That(final.Pending,Is.Zero); Assert.That(final.Vented,Is.EqualTo(5UL)); Assert.That(final.RuleConsumed,Is.EqualTo(1UL)); Assert.That(final.IsBalanced,Is.True);
            }
            finally { UnityEngine.Object.DestroyImmediate(ventHost); UnityEngine.Object.DestroyImmediate(profile); }
        }
        [Test]
        public void CancelledMeltingDoesNotLeaveLiquidSolidCacheOpen()
        {
            using var world=new World();
            var request=new ElementWriteRequest(Vector3.one*100+Vector3.one*.1f,MaterialId.Ice,100,0,false);
            world.Solid.TryEnqueueDeposit(in request); world.Solid.Step();
            var transaction=new MaterialConversionTransaction(world.Port,new MaterialWorldQuantityLedger());
            var target=new[]{new MaterialConversionPlacement(Vector3Int.zero,MaterialId.Water,100)};
            Assert.That(transaction.TryPrepare(new MaterialConversionAddress(Vector3Int.zero,MaterialId.Ice),100,target,1),Is.True);
            transaction.Cancel();
            Assert.That(world.Liquid.TryAddInitial(Vector3Int.zero,MaterialId.Water,1),Is.False,"取消融化后，冰仍应阻挡后续液体。");
            Assert.That(world.Solid.TotalQuantity,Is.EqualTo(100UL));
        }
        [Test]
        public void DriedResidueUsesSpaceWhileRemainingNonBlocking()
        {
            using var world=new World(); var profile=ScriptableObject.CreateInstance<MaterialConversionProfile>();
            try
            {
                var scheduler=new MaterialConversionScheduler(world.Runtime,world.Liquid,world.Solid,world.Gas,profile);
                World.Set(world.Runtime,"_conversionScheduler",scheduler);
                world.Liquid.TryAddInitial(Vector3Int.zero,MaterialId.Sticky,7);
                var transaction=new MaterialConversionTransaction(world.Port,scheduler.Ledger);
                var products=new[]{new MaterialConversionPlacement(Vector3Int.zero,MaterialId.DriedSlime,3),new MaterialConversionPlacement(Vector3Int.up,MaterialId.Steam,1)};
                Assert.That(transaction.TryPrepare(new MaterialConversionAddress(Vector3Int.zero,MaterialId.Sticky),4,products,2) && transaction.Commit(),Is.True);
                Assert.That(world.Solid.BlocksCell(Vector3Int.zero),Is.False);
                Assert.That(world.Liquid.TryAddInitial(Vector3Int.zero,MaterialId.Water,250),Is.False,"3 残留 + 3 湿粘液 + 250 水超过此粗格 255 容量。");
            }
            finally { UnityEngine.Object.DestroyImmediate(profile); }
        }
        [Test]
        public void InvalidatedCacheSlotsDoNotLetDictionaryGrowBeyondPreallocatedBudget()
        {
            using var world=new World();
            var profile=(LiquidWorldProfile)typeof(LiquidWorldRuntime).GetField("_profile",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(world.Liquid);
            World.Set(profile,"_maximumActiveCells",2); world.Liquid.SetMaterialSolidOccupancy(world.Solid);
            world.Solid.StageConversionAdd(Vector3Int.zero,MaterialId.DriedSlime,1);
            world.Liquid.ConversionFreeCapacity(Vector3Int.zero); world.Liquid.ConversionFreeCapacity(Vector3Int.right);
            world.Liquid.InvalidateConversionCell(Vector3Int.right);
            world.Liquid.ConversionFreeCapacity(Vector3Int.right*2); world.Liquid.ConversionFreeCapacity(Vector3Int.right*3);
            var cache=(System.Collections.Generic.Dictionary<Vector3Int,uint>)typeof(LiquidWorldRuntime).GetField("_residueOccupancy",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(world.Liquid);
            Assert.That(cache.Count,Is.LessThanOrEqualTo(2),"失效空位会留下陈旧 Ring Key；不能仅在 Count==容量时才淘汰。");
        }
        [Test]
        public void DefaultAmbientWaterAndStickyDoNotQueueEveryOrdinaryLiquidWrite()
        {
            using var world=new World(); var profile=ScriptableObject.CreateInstance<MaterialConversionProfile>();
            try
            {
                var scheduler=new MaterialConversionScheduler(world.Runtime,world.Liquid,world.Solid,world.Gas,profile);
                world.Liquid.TryAddInitial(Vector3Int.zero,MaterialId.Water,100);
                world.Liquid.TryAddInitial(Vector3Int.right,MaterialId.Sticky,100);
                Assert.That(scheduler.PendingCandidates,Is.Zero,"默认常温普通流动不应占相变队列；Fire 与显式环境负责唤醒。");
            }
            finally { UnityEngine.Object.DestroyImmediate(profile); }
        }
        [Test]
        public void CoarseWaterFreezesToEightFineCellsWithMatchingDensityAndOneChunkPublish()
        {
            using var world=new World(4096,2,4,64); var profile=ScriptableObject.CreateInstance<MaterialConversionProfile>();
            try
            {
                World.Set(profile,"_ambientTemperature",-10f);
                var scheduler=new MaterialConversionScheduler(world.Runtime,world.Liquid,world.Solid,world.Gas,profile);
                Assert.That(world.Liquid.CellCapacityGmu,Is.EqualTo(510));
                world.Liquid.TryAddInitial(Vector3Int.zero,MaterialId.Water,510);
                var domain=new ElementWorldActivityRegions(); domain.TryAdd(world.Runtime.GetActiveWorldBounds());
                for(int i=0;i<100;i++) { scheduler.BeginTick(.1f,domain); scheduler.Step(.1f); }
                Assert.That(world.Liquid.TotalQuantity,Is.Zero); Assert.That(world.Solid.TotalQuantity,Is.EqualTo(510UL));
                int occupied=0;
                for(int y=0;y<2;y++) for(int z=0;z<2;z++) for(int x=0;x<2;x++)
                    if(world.Solid.TryGetQuantity(new Vector3Int(x,y,z),MaterialId.Ice,out uint q) && q>0) occupied++;
                Assert.That(occupied,Is.EqualTo(8)); Assert.That(world.Solid.SurfaceRebuildCount,Is.EqualTo(1));
                Assert.That(510d/64,Is.EqualTo(8).Within(.032),"整数容量舍入仅有约 0.4% 体积误差。");
            }
            finally { UnityEngine.Object.DestroyImmediate(profile); }
        }
        [Test]
        public void ColdIceRemainsEligibleWhenItsEnvironmentBecomesWarm()
        {
            using var world=new World(); var profile=ScriptableObject.CreateInstance<MaterialConversionProfile>();
            var zoneHost=new GameObject("Conversion Cold Zone"); zoneHost.transform.position=Vector3.one*100.5f;
            var zone=zoneHost.AddComponent<MaterialEnvironmentAuthoring>(); World.Set(zone,"_temperature",-10f);
            try
            {
                var scheduler=new MaterialConversionScheduler(world.Runtime,world.Liquid,world.Solid,world.Gas,profile);
                scheduler.Register(zone); world.Solid.StageConversionAdd(Vector3Int.zero,MaterialId.Ice,100);
                var domain=new ElementWorldActivityRegions(); domain.TryAdd(world.Runtime.GetActiveWorldBounds());
                scheduler.BeginTick(.1f,domain); scheduler.Step(.1f);
                Assert.That(world.Solid.TotalQuantity,Is.EqualTo(100UL));
                // 固体没有流动通知；环境升温后仍必须能够开始融化。
                World.Set(zone,"_temperature",20f);
                for(int i=0;i<4;i++) { scheduler.BeginTick(.1f,domain); scheduler.Step(.1f); }
                Assert.That(world.Liquid.TotalQuantity,Is.GreaterThan(0UL));
                Assert.That(world.Liquid.TotalQuantity+world.Solid.TotalQuantity,Is.EqualTo(100UL));
            }
            finally { UnityEngine.Object.DestroyImmediate(zoneHost); UnityEngine.Object.DestroyImmediate(profile); }
        }
        [Test]
        public void FullColdConditionQueueStillAdmitsNewSteamWithoutDeletingIce()
        {
            using var world=new World(); var profile=ScriptableObject.CreateInstance<MaterialConversionProfile>();
            try
            {
                World.Set(profile,"_ambientTemperature",-10f); World.Set(profile,"_maximumCandidates",32);
                var scheduler=new MaterialConversionScheduler(world.Runtime,world.Liquid,world.Solid,world.Gas,profile);
                for(int y=0;y<2;y++) for(int z=0;z<4;z++) for(int x=0;x<4;x++)
                    Assert.That(world.Solid.StageConversionAdd(new Vector3Int(x,y,z),MaterialId.Ice,1),Is.True);
                var domain=new ElementWorldActivityRegions(); domain.TryAdd(world.Runtime.GetActiveWorldBounds());
                scheduler.BeginTick(.1f,domain); scheduler.Step(.1f);
                Assert.That(scheduler.PendingCandidates,Is.EqualTo(32));
                Assert.That(world.Gas.StageConversionAdd(Vector3Int.right*4,MaterialId.Steam,1),Is.True);
                for(int i=0;i<10;i++) { scheduler.BeginTick(.1f,domain); scheduler.Step(.1f); }
                Assert.That(world.Gas.TotalQuantity,Is.Zero); Assert.That(world.Liquid.TotalQuantity,Is.EqualTo(1UL));
                Assert.That(world.Solid.TotalQuantity,Is.EqualTo(32UL)); Assert.That(scheduler.PendingCandidates,Is.LessThanOrEqualTo(32));
            }
            finally { UnityEngine.Object.DestroyImmediate(profile); }
        }
        [TestCase(true)]
        [TestCase(false)]
        public void ResidueOnlyPreventsStableCompressionInItsOwnLiquidBasin(bool residueInBasin)
        {
            bool Terrain(Vector3Int cell) => cell.y==-1 || cell.y==0
                && (cell.x==-1 || cell.x==6 || cell.z==-1 || cell.z==6);
            var store=new LiquidVolumeStore(4,32,255,new[]{MaterialId.Water},Terrain);
            var regions=new StableLiquidRegionRuntime(2,4,256,store,new LiquidActivityScheduler(64,store),Vector3.zero,1);
            Vector3Int residue=residueInBasin?Vector3Int.zero:Vector3Int.one*100;
            uint Residue(Vector3Int cell) => cell==residue?3u:0u;
            store.SetMaterialSolidOccupancy(Residue);
            store.SetExternalOccupancy(cell=>regions.OccupancyAt(cell)+Residue(cell));
            ulong total=0;
            for(int z=0;z<6;z++) for(int x=0;x<6;x++)
            {
                var cell=new Vector3Int(x,0,z); uint q=255-Residue(cell);
                Assert.That(store.TryAdd(cell,MaterialId.Water,q),Is.True); total+=q;
            }
            var detector=new StableLiquidRegionDetector(store,regions,new[]{MaterialId.Water},256,64,2);
            for(int tick=0;tick<300;tick++) detector.Step();
            Assert.That(regions.RegionCount,Is.EqualTo(residueInBasin?0:1),"远处的固化残留不应关闭全世界的平静液体压缩。");
            Assert.That(store.TotalQuantity+regions.TotalQuantity,Is.EqualTo(total));
        }
    }
}
