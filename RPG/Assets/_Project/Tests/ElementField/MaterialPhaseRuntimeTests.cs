using System.Reflection;
using Game.Materials;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class MaterialPhaseRuntimeTests
    {
        private static void Profile(object target, Object value) => target.GetType().GetField("_profile", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        [Test]
        public void NavigationRejectionsDoNotPermanentlyStarveLaterWholeSolidImpact()
        {
            var host=new GameObject("Solid Retry Budget Test"); host.SetActive(false);
            var profile=ScriptableObject.CreateInstance<FixedSolidWorldProfile>();
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":4,\"_cellCapacityGmu\":64,\"_maximumCellsPerTick\":32,\"_maximumNavigationBoxesPerChunk\":1}",profile);
            try
            {
                var runtime=host.AddComponent<FixedSolidWorldRuntime>(); Profile(runtime,profile);
                Assert.That(runtime.TryInitialize(null,null,Vector3.one*100,.25f,8),Is.True);
                var rejected=new ElementWriteRequest(Vector3.one*100.875f,MaterialId.Ice,1024,.75f,false);
                runtime.TryEnqueueDeposit(in rejected); runtime.Step(); runtime.Step();
                Assert.That(runtime.TotalQuantity,Is.Zero,"碎片形状确实超过一个 Navigation Box，必须完整回滚。");
                // 远处已有完整结构只剩 4×4 平面孔洞。填满后整个 Chunk 可用一个 Box 表示；
                // 用真实几何验证新请求可行，避免仅断言内部 budget 字段而漏掉永久排队。
                var store=(FixedSolidVolumeStore)typeof(FixedSolidWorldRuntime).GetField("_store",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(runtime);
                for(int z=0;z<8;z++) for(int y=0;y<8;y++) for(int x=0;x<8;x++)
                    if(y!=3 || x<2 || x>5 || z<2 || z>5) store.TryAdd(new Vector3Int(x+32,y,z),MaterialId.Ice,64);
                ulong before=runtime.TotalQuantity;
                var valid=new ElementWriteRequest(new Vector3(108.875f,100.875f,100.875f),MaterialId.Ice,1024,.75f,false);
                runtime.TryEnqueueDeposit(in valid);
                for(int tick=0;tick<4;tick++) runtime.Step();
                Assert.That(runtime.TotalQuantity,Is.EqualTo(before+1024),"早先失败不能把单发预算永久压到 16 格以下。");
                Assert.That(runtime.PendingQuantity,Is.EqualTo(1024UL),"只有几何确实无法表达的原请求继续排队。");
            }
            finally { Object.DestroyImmediate(host); Object.DestroyImmediate(profile); }
        }
        [TestCase(MaterialId.Ice)]
        [TestCase(MaterialId.DriedSlime)]
        public void CoarseSolidGrainFormsHalfMeterCubeAndRetainsExactTailMass(MaterialId material)
        {
            var host=new GameObject("Coarse Solid Grain Test"); host.SetActive(false);
            var profile=ScriptableObject.CreateInstance<FixedSolidWorldProfile>();
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":4,\"_cellCapacityGmu\":64,\"_depositGrainCells\":2}",profile);
            try
            {
                var runtime=host.AddComponent<FixedSolidWorldRuntime>(); Profile(runtime,profile);
                Assert.That(runtime.TryInitialize(null,null,Vector3.one*100,.25f,8),Is.True);
                var request=new ElementWriteRequest(new Vector3(99.875f,100.875f,99.875f),material,512,0,false);
                host.SetActive(true);
                runtime.TryEnqueueDeposit(in request); runtime.Step();
                Assert.That(runtime.TotalQuantity,Is.EqualTo(512UL));
                int occupied=0; for(int i=0;i<runtime.ResidentCellCount;i++) if(runtime.TryGetCell(i,out _)) occupied++;
                Assert.That(occupied,Is.EqualTo(8));
                Assert.That(runtime.TryGetSurface(0,out Mesh mesh,out _),Is.True);
                Assert.That(mesh.bounds.size,Is.EqualTo(Vector3.one*.5f),"负坐标也应组成连续 2×2×2 细格，不能散成八个小格。");
                Physics.SyncTransforms();
                Assert.That(Physics.Raycast(new Vector3(99.75f,102,99.75f),Vector3.down,out var hit,3,1<<8),Is.True);
                Assert.That(hit.point.y,Is.EqualTo(101.25f).Within(.001f),"冰和固化粘液的成块 Collider 必须与可见顶部一致。");
                var tail=new ElementWriteRequest(new Vector3(102.875f,100.875f,100.875f),material,129,0,false);
                runtime.TryEnqueueDeposit(in tail); runtime.Step();
                Assert.That(runtime.TotalQuantity,Is.EqualTo(641UL)); Assert.That(runtime.PendingQuantity,Is.Zero);
            }
            finally { Object.DestroyImmediate(host); Object.DestroyImmediate(profile); }
        }
        [Test]
        public void SolidPoolExhaustionReportsCapacityAndKeepsPendingUntilAChunkIsReclaimed()
        {
            var host=new GameObject("Solid Capacity Diagnostic Test"); host.SetActive(false);
            var profile=ScriptableObject.CreateInstance<FixedSolidWorldProfile>();
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":1,\"_cellCapacityGmu\":64}",profile);
            try
            {
                var runtime=host.AddComponent<FixedSolidWorldRuntime>(); Profile(runtime,profile);
                Assert.That(runtime.TryInitialize(null,null,Vector3.one*100,.25f,8),Is.True);
                var first=new ElementWriteRequest(Vector3.one*100.125f,MaterialId.Ice,64,0,false);
                runtime.TryEnqueueDeposit(in first); runtime.Step();
                var second=new ElementWriteRequest(new Vector3(102.125f,100.125f,100.125f),MaterialId.Ice,64,0,false);
                runtime.TryEnqueueDeposit(in second); runtime.Step();
                Assert.That(runtime.MaximumChunks,Is.EqualTo(1));
                Assert.That(runtime.ChunkCapacityRejections,Is.GreaterThan(0u));
                Assert.That(runtime.TotalQuantity,Is.EqualTo(64UL)); Assert.That(runtime.PendingQuantity,Is.EqualTo(64UL));
                Assert.That(runtime.TryRemove(Vector3Int.zero,MaterialId.Ice,64),Is.True);
                runtime.Step();
                Assert.That(runtime.PendingQuantity,Is.Zero);
                Assert.That(runtime.TryGetQuantity(Vector3Int.right*8,MaterialId.Ice,out uint quantity),Is.True);
                Assert.That(quantity,Is.EqualTo(64u));
            }
            finally { Object.DestroyImmediate(host); Object.DestroyImmediate(profile); }
        }
        [Test]
        public void CoarseSolidSuccessfulBirthsUseBoundedCookingAndZeroManagedAllocation()
        {
            var host=new GameObject("Solid Grain Allocation Test"); host.SetActive(false);
            var profile=ScriptableObject.CreateInstance<FixedSolidWorldProfile>();
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":4,\"_cellCapacityGmu\":64,\"_depositGrainCells\":2}",profile);
            try
            {
                var runtime=host.AddComponent<FixedSolidWorldRuntime>(); Profile(runtime,profile);
                Assert.That(runtime.TryInitialize(null,null,Vector3.one*100,.25f,8),Is.True);
                var warm=new ElementWriteRequest(Vector3.one*99.125f,MaterialId.Ice,512,0,false);
                runtime.TryEnqueueDeposit(in warm); runtime.Step();
                bool accepted=true; uint maxRebuilds=0;
                long before=System.GC.GetAllocatedBytesForCurrentThread();
                for(int i=0;i<32;i++)
                {
                    var point=Vector3.one*100.125f+new Vector3(i%4*.5f,i/16*.5f,i/4%4*.5f);
                    var request=new ElementWriteRequest(point,MaterialId.Ice,512,0,false);
                    uint rebuilds=runtime.SurfaceRebuildCount;
                    accepted&=runtime.TryEnqueueDeposit(in request); runtime.Step();
                    maxRebuilds=System.Math.Max(maxRebuilds,runtime.SurfaceRebuildCount-rebuilds);
                }
                long allocated=System.GC.GetAllocatedBytesForCurrentThread()-before;
                Assert.That(accepted,Is.True); Assert.That(runtime.PendingQuantity,Is.Zero);
                Assert.That(runtime.TotalQuantity,Is.EqualTo(33UL*512));
                Assert.That(maxRebuilds,Is.LessThanOrEqualTo(8u)); Assert.That(allocated,Is.Zero);
            }
            finally { Object.DestroyImmediate(host); Object.DestroyImmediate(profile); }
        }
        [Test]
        public void SteamBirthCanOccupyRemainingConcentrationInsideToxicCloud()
        {
            var host=new GameObject("Mixed Gas Birth Test"); host.SetActive(false);
            var profile=ScriptableObject.CreateInstance<GasWorldProfile>();
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":64,\"_cellCapacityGmu\":32,\"_maximumActiveCells\":8192,\"_materials\":[{\"Material\":7},{\"Material\":8}]}",profile);
            try
            {
                var gas=host.AddComponent<GasWorldRuntime>(); Profile(gas,profile);
                Assert.That(gas.TryInitialize(null,null,Vector3.zero,.25f,8),Is.True);
                var store=(GasVolumeStore)typeof(GasWorldRuntime).GetField("_store",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(gas);
                for(int z=-5;z<=5;z++) for(int y=-5;y<=5;y++) for(int x=-5;x<=5;x++)
                    Assert.That(store.TryAdd(new Vector3Int(x,y,z),MaterialId.ToxicVapor,16),Is.True);
                ulong poison=store.TotalQuantity;
                var request=new ElementWriteRequest(Vector3.one*.125f,MaterialId.Steam,1600,.75f,false);
                gas.TryEnqueueDeposit(in request); gas.Step(.1f);
                Assert.That(gas.PendingQuantity,Is.Zero,"半浓度毒雾中的另一气体仍应在首 Tick 出生。");
                Assert.That(gas.TotalQuantity,Is.EqualTo(poison+1600));
                int mixed=0;
                for(int i=0;i<gas.ResidentCellCount;i++) if(gas.TryGetCell(i,out var cell) && cell.Material==MaterialId.Steam)
                {
                    Assert.That(gas.TryGetQuantity(cell.Cell,MaterialId.ToxicVapor,out uint toxic),Is.True);
                    Assert.That(toxic,Is.EqualTo(16u)); Assert.That(store.OccupancyAt(cell.Cell),Is.LessThanOrEqualTo(32u)); mixed++;
                }
                Assert.That(mixed,Is.EqualTo(100));
            }
            finally { Object.DestroyImmediate(host); Object.DestroyImmediate(profile); }
        }
        [TestCase(MaterialId.Ice)]
        [TestCase(MaterialId.DriedSlime)]
        public void BudgetDefersSecondOrdinarySolidImpactAsAWholeInsteadOfShowingHalf(MaterialId material)
        {
            var host=new GameObject("Atomic Solid Birth Test"); host.SetActive(false);
            var profile=ScriptableObject.CreateInstance<FixedSolidWorldProfile>();
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":4,\"_cellCapacityGmu\":64,\"_maximumCellsPerTick\":24}",profile);
            try
            {
                var runtime=host.AddComponent<FixedSolidWorldRuntime>(); Profile(runtime,profile);
                Assert.That(runtime.TryInitialize(null,null,Vector3.one*100,.25f,8),Is.True);
                for(int i=0;i<2;i++)
                {
                    var request=new ElementWriteRequest(Vector3.one*100.875f+Vector3.right*(i*2),material,1024,.75f,false);
                    runtime.TryEnqueueDeposit(in request);
                }
                runtime.Step();
                Assert.That(runtime.TotalQuantity,Is.EqualTo(1024UL),"共享预算不足时应推迟整个第二发，不能先显示半个实体。");
                Assert.That(runtime.PendingQuantity,Is.EqualTo(1024UL));
                runtime.Step(); Assert.That(runtime.TotalQuantity,Is.EqualTo(2048UL)); Assert.That(runtime.PendingQuantity,Is.Zero);
            }
            finally { Object.DestroyImmediate(host); Object.DestroyImmediate(profile); }
        }
        [TestCase(MaterialId.Ice)]
        [TestCase(MaterialId.DriedSlime)]
        public void OrdinarySolidImpactPlacesAllSixteenCellsAndPublishesOneChunkInFirstTick(MaterialId material)
        {
            var host=new GameObject("Solid Burst Test"); host.SetActive(false);
            var profile=ScriptableObject.CreateInstance<FixedSolidWorldProfile>();
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":2,\"_cellCapacityGmu\":64,\"_maximumCellsPerTick\":32,\"_maximumNavigationBoxesPerChunk\":16}",profile);
            try
            {
                var runtime=host.AddComponent<FixedSolidWorldRuntime>(); Profile(runtime,profile);
                Assert.That(runtime.TryInitialize(null,null,Vector3.one*100,.25f,8),Is.True);
                var request=new ElementWriteRequest(Vector3.one*100.875f,material,1024,.75f,false);
                runtime.TryEnqueueDeposit(in request); runtime.Step();
                Assert.That(runtime.TotalQuantity,Is.EqualTo(1024UL),"普通命中必须在下一次 World Tick 完成整批落位。");
                Assert.That(runtime.PendingQuantity,Is.Zero);
                Assert.That(runtime.SurfaceRebuildCount,Is.EqualTo(1u),"同一 Chunk 的全部小格一起 Cooking。");
                Assert.That(runtime.TryGetSurface(0,out Mesh mesh,out _),Is.True);
                Assert.That(mesh.vertexCount,Is.GreaterThan(24));
            }
            finally { Object.DestroyImmediate(host); Object.DestroyImmediate(profile); }
        }
        [Test]
        public void SolidBurstNavigationFailureRollsBackWholeRequestBeforePublishing()
        {
            var host=new GameObject("Solid Burst Rollback Test"); host.SetActive(false);
            var profile=ScriptableObject.CreateInstance<FixedSolidWorldProfile>();
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":2,\"_cellCapacityGmu\":64,\"_maximumCellsPerTick\":32,\"_maximumNavigationBoxesPerChunk\":1}",profile);
            try
            {
                var runtime=host.AddComponent<FixedSolidWorldRuntime>(); Profile(runtime,profile);
                Assert.That(runtime.TryInitialize(null,null,Vector3.one*100,.25f,8),Is.True);
                var request=new ElementWriteRequest(Vector3.one*100.875f,MaterialId.Ice,1024,.75f,false);
                runtime.TryEnqueueDeposit(in request); runtime.Step();
                Assert.That(runtime.TotalQuantity,Is.Zero);
                Assert.That(runtime.PendingQuantity,Is.EqualTo(1024UL));
                Assert.That(runtime.SurfaceRebuildCount,Is.Zero);
            }
            finally { Object.DestroyImmediate(host); Object.DestroyImmediate(profile); }
        }
        [TestCase(MaterialId.Steam,1600)]
        [TestCase(MaterialId.ToxicVapor,16384)]
        public void GasImpactSpreadsMassIntoAirInsteadOfConcentratingInOneVoxel(MaterialId material,int quantity)
        {
            var host=new GameObject("Gas Birth Volume Test"); host.SetActive(false);
            var profile=ScriptableObject.CreateInstance<GasWorldProfile>();
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":32,\"_cellCapacityGmu\":32,\"_maximumActiveCells\":8192,\"_maximumCellsPerTick\":1024,\"_windVelocity\":{\"x\":0,\"y\":0,\"z\":0},\"_materials\":[{\"Material\":7,\"DiffusionPerSecond\":0,\"BuoyancyPerSecond\":0},{\"Material\":8,\"DiffusionPerSecond\":0,\"BuoyancyPerSecond\":0}]}",profile);
            try
            {
                var runtime=host.AddComponent<GasWorldRuntime>(); Profile(runtime,profile);
                Assert.That(runtime.TryInitialize(null,null,Vector3.one*100,.25f,8),Is.True);
                var request=new ElementWriteRequest(Vector3.one*101.875f,material,(ushort)quantity,.75f,false);
                runtime.TryEnqueueDeposit(in request); runtime.Step(.1f);
                if(material==MaterialId.Steam) Assert.That(runtime.TotalQuantity,Is.EqualTo((ulong)quantity),"一颗蒸汽球应在首 Tick 形成连续云团。");
                for(int tick=0;tick<24;tick++)
                {
                    runtime.Step(.1f);
                    Assert.That(runtime.TotalQuantity+runtime.PendingQuantity,Is.EqualTo((ulong)quantity));
                    Assert.That(runtime.TerrainSamplesLastTick,Is.LessThanOrEqualTo(128));
                }
                Assert.That(runtime.PendingQuantity,Is.Zero,"大毒气团要扩展出生邻域，不能被原固定半径永久卡住。");
                int occupied=0;
                for(int i=0;i<runtime.ResidentCellCount;i++) if(runtime.TryGetCell(i,out _)) occupied++;
                Assert.That(occupied,Is.EqualTo((quantity+15)/16),"出生半浓度进一步扩大气团；总量仍准确保留。");
            }
            finally { Object.DestroyImmediate(host); Object.DestroyImmediate(profile); }
        }
        [Test]
        public void GasBirthAndWindCannotPlaceMaterialAcrossAThinWall()
        {
            var host=new GameObject("Gas Birth Wall Test"); host.SetActive(false);
            var wall=new GameObject("Gas Thin Wall"); wall.transform.position=new Vector3(100.875f,100.875f,100.875f);
            wall.AddComponent<BoxCollider>().size=new Vector3(.25f,8,8);
            var profile=ScriptableObject.CreateInstance<GasWorldProfile>();
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":32,\"_maximumActiveCells\":1024,\"_maximumCellsPerTick\":256,\"_windVelocity\":{\"x\":0.25,\"y\":0,\"z\":0},\"_materials\":[{\"Material\":8,\"DiffusionPerSecond\":0,\"BuoyancyPerSecond\":0}]}",profile);
            try
            {
                var baker=host.AddComponent<ElementFieldSolidBaker>();
                typeof(ElementFieldSolidBaker).GetField("_solidLayerMask",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(baker,(LayerMask)1);
                var runtime=host.AddComponent<GasWorldRuntime>(); Profile(runtime,profile);
                Assert.That(runtime.TryInitialize(null,baker,Vector3.one*100,.25f,8),Is.True); Physics.SyncTransforms();
                var request=new ElementWriteRequest(Vector3.one*100.375f,MaterialId.ToxicVapor,16384,1,false);
                runtime.TryEnqueueDeposit(in request);
                for(int tick=0;tick<90;tick++) runtime.Step(.1f);
                Assert.That(runtime.TotalQuantity+runtime.PendingQuantity,Is.EqualTo(16384UL));
                for(int i=0;i<runtime.ResidentCellCount;i++) if(runtime.TryGetCell(i,out var cell))
                    Assert.That(cell.Cell.x,Is.LessThan(3),"出生邻域和 Advection 都必须尊重墙，不能直接在墙后空格放气体。");
            }
            finally { Object.DestroyImmediate(host); Object.DestroyImmediate(wall); Object.DestroyImmediate(profile); }
        }
        [Test]
        public void SimultaneousSolidImpactsRespectTheChunkCookingBudgetAndKeepRemainingMass()
        {
            var host=new GameObject("Solid Cooking Budget Test"); host.SetActive(false);
            var profile=ScriptableObject.CreateInstance<FixedSolidWorldProfile>();
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":8,\"_cellCapacityGmu\":64,\"_maximumCellsPerTick\":64,\"_maximumColliderChunksPerTick\":2}",profile);
            try
            {
                var runtime=host.AddComponent<FixedSolidWorldRuntime>(); Profile(runtime,profile);
                Assert.That(runtime.TryInitialize(null,null,Vector3.one*100,.25f,8),Is.True);
                for(int i=0;i<8;i++)
                {
                    var request=new ElementWriteRequest(Vector3.one*100.875f+Vector3.right*(i*2),MaterialId.Ice,64,0,false);
                    runtime.TryEnqueueDeposit(in request);
                }
                for(int tick=0;tick<4;tick++)
                {
                    uint before=runtime.SurfaceRebuildCount; runtime.Step();
                    Assert.That(runtime.SurfaceRebuildCount-before,Is.LessThanOrEqualTo(2u));
                    Assert.That(runtime.TotalQuantity+runtime.PendingQuantity,Is.EqualTo(512UL));
                }
                Assert.That(runtime.PendingQuantity,Is.Zero); Assert.That(runtime.ResidentChunkCount,Is.EqualTo(8));
            }
            finally { Object.DestroyImmediate(host); Object.DestroyImmediate(profile); }
        }
        [Test]
        public void DiluteGasDriftsWithRealWindAndFrozenRegionDoesNotCatchUp()
        {
            var host=new GameObject("Gas Wind Test"); host.SetActive(false);
            var profile=ScriptableObject.CreateInstance<GasWorldProfile>();
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":16,\"_maximumActiveCells\":128,\"_maximumCellsPerTick\":32,\"_windVelocity\":{\"x\":0.25,\"y\":0,\"z\":0},\"_materials\":[{\"Material\":8}]}",profile);
            try
            {
                var runtime=host.AddComponent<GasWorldRuntime>(); Profile(runtime,profile);
                Assert.That(runtime.TryInitialize(null,null,Vector3.one*100,.25f,4),Is.True);
                var domain=new ElementWorldActivityRegions(); domain.TryAdd(new Bounds(Vector3.one*100,Vector3.one*30)); runtime.SetActivityDomain(domain);
                var request=new ElementWriteRequest(Vector3.one*100.125f,MaterialId.ToxicVapor,1,0,false);
                runtime.TryEnqueueDeposit(in request);
                for(int tick=0;tick<30;tick++) runtime.Step(.1f);
                int movedX=-1;
                for(int i=0;i<runtime.ResidentCellCount;i++) if(runtime.TryGetCell(i,out var cell)) movedX=cell.Cell.x;
                Assert.That(movedX,Is.GreaterThanOrEqualTo(2),"小于 1 GMU/Tick 的风通量也需要累积推进。");
                domain.Clear(); domain.TryAdd(new Bounds(Vector3.zero,Vector3.one*4)); runtime.SetActivityDomain(domain);
                for(int tick=0;tick<300;tick++) runtime.Step(.1f);
                Assert.That(runtime.TryGetQuantity(new Vector3Int(movedX,0,0),MaterialId.ToxicVapor,out uint frozen),Is.True);
                Assert.That(frozen,Is.EqualTo(1u));
                domain.Clear(); domain.TryAdd(new Bounds(Vector3.one*100,Vector3.one*30)); runtime.SetActivityDomain(domain);
                runtime.Step(.1f);
                for(int i=0;i<runtime.ResidentCellCount;i++) if(runtime.TryGetCell(i,out var cell)) Assert.That(cell.Cell.x,Is.LessThanOrEqualTo(movedX+1));
                Assert.That(runtime.TotalQuantity,Is.EqualTo(1UL));
            }
            finally { Object.DestroyImmediate(host); Object.DestroyImmediate(profile); }
        }
        [TestCase(MaterialId.Ice)]
        [TestCase(MaterialId.DriedSlime)]
        public void SolidPublishesColliderAndRemovalClearsSupportWithoutRepeatedRebuild(MaterialId material)
        {
            var host = new GameObject("Solid Physics Test"); host.SetActive(false);
            var profile = ScriptableObject.CreateInstance<FixedSolidWorldProfile>();
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":2,\"_maximumNavigationBoxesPerChunk\":8}", profile);
            try
            {
                var runtime = host.AddComponent<FixedSolidWorldRuntime>(); Profile(runtime, profile);
                Assert.That(runtime.TryInitialize(null, null, Vector3.one * 100, 1, 2), Is.True);
                host.SetActive(true); // Physics 只注册 activeInHierarchy 的 Collider。
                var request = new ElementWriteRequest(Vector3.one * 100 + Vector3.one * .1f, material, 512, 0, false);
                Assert.That(runtime.TryEnqueueDeposit(in request), Is.True); runtime.Step(); Physics.SyncTransforms();
                Assert.That(runtime.TotalQuantity, Is.EqualTo(512UL));
                Assert.That(runtime.TryGetSurface(0, out Mesh mesh, out _), Is.True);
                Assert.That(mesh.bounds.max.y, Is.EqualTo(.5f).Within(.001f));
                Assert.That(Physics.Raycast(new Vector3(100.5f, 102, 100.5f), Vector3.down, out var hit, 2, 1 << 8), Is.True);
                Assert.That(hit.point.y, Is.EqualTo(100.5f).Within(.01f));
                uint rebuilds = runtime.SurfaceRebuildCount;
                for (int i = 0; i < 300; i++) runtime.Step();
                Assert.That(runtime.SurfaceRebuildCount, Is.EqualTo(rebuilds));
                Assert.That(runtime.TryRemove(Vector3Int.zero, material, 512), Is.True); Physics.SyncTransforms();
                Assert.That(Physics.Raycast(new Vector3(100.5f, 102, 100.5f), Vector3.down, 2, 1 << 8), Is.False);
            }
            finally { Object.DestroyImmediate(host); Object.DestroyImmediate(profile); }
        }
        [TestCase(MaterialId.Ice)]
        [TestCase(MaterialId.DriedSlime)]
        public void SolidDepositAvoidsCharacterBodyAndRetainsPendingQuantity(MaterialId material)
        {
            var host = new GameObject("Solid Actor Test"); host.SetActive(false);
            var actor = new GameObject("Actor"); actor.transform.position = new Vector3(100.5f, 100, 100.5f);
            var controller = actor.AddComponent<CharacterController>(); controller.center = Vector3.up; controller.height = 2;
            var profile = ScriptableObject.CreateInstance<FixedSolidWorldProfile>();
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":1,\"_maximumNavigationBoxesPerChunk\":2}", profile);
            try
            {
                var runtime = host.AddComponent<FixedSolidWorldRuntime>(); Profile(runtime, profile);
                Assert.That(runtime.TryInitialize(null, null, Vector3.one * 100, 1, 2), Is.True); Physics.SyncTransforms();
                var request = new ElementWriteRequest(Vector3.one * 100 + Vector3.one * .1f, material, 512, 0, false);
                Assert.That(runtime.TryEnqueueDeposit(in request), Is.True); runtime.Step();
                Assert.That(runtime.TotalQuantity, Is.Zero); Assert.That(runtime.PendingQuantity, Is.EqualTo(512UL));
                actor.transform.position += Vector3.right * 5; Physics.SyncTransforms(); runtime.Step();
                Assert.That(runtime.TotalQuantity, Is.EqualTo(512UL)); Assert.That(runtime.PendingQuantity, Is.Zero);
            }
            finally { Object.DestroyImmediate(actor); Object.DestroyImmediate(host); Object.DestroyImmediate(profile); }
        }
        [Test]
        public void ResidueColliderDoesNotDoubleCountTheLiquidTerrainSupportHeight()
        {
            var host=new GameObject("Residue Support Test"); host.SetActive(false);
            var profile=ScriptableObject.CreateInstance<FixedSolidWorldProfile>();
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":2,\"_maximumNavigationBoxesPerChunk\":8}",profile);
            var floor=new GameObject("Residue Support Floor"); floor.transform.position=new Vector3(100.5f,99.5f,100.5f);
            floor.AddComponent<BoxCollider>().size=new Vector3(2,1,2);
            try
            {
                var runtime=host.AddComponent<FixedSolidWorldRuntime>(); Profile(runtime,profile);
                Assert.That(runtime.TryInitialize(null,null,Vector3.one*100,1,2),Is.True);
                var baker=host.AddComponent<ElementFieldSolidBaker>();
                typeof(ElementFieldSolidBaker).GetField("_solidLayerMask",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(baker,(LayerMask)(1<<8));
                baker.SetMaterialSolids(runtime,Vector3.one*100,1); host.SetActive(true);
                var request=new ElementWriteRequest(Vector3.one*100+Vector3.one*.1f,MaterialId.DriedSlime,512,0,false);
                runtime.TryEnqueueDeposit(in request); runtime.Step(); Physics.SyncTransforms();
                Assert.That(Physics.Raycast(new Vector3(100.5f,102,100.5f),Vector3.down,out var collision,3,1<<8),Is.True);
                Assert.That(collision.point.y,Is.EqualTo(100.5f).Within(.01f));
                Assert.That(baker.TrySampleLiquidSupport(new Vector3(100.5f,102,100.5f),3,out var terrainHeight,out _),Is.True);
                Assert.That(terrainHeight,Is.EqualTo(100).Within(.01f),"残留体积已由 Liquid 容量/底面适配器计算，Physics 地形支撑不得再次叠加。");
            }
            finally { Object.DestroyImmediate(host); Object.DestroyImmediate(profile); Object.DestroyImmediate(floor); }
        }
        [Test]
        public void GasRuntimeRetainsQuantityWhileFrozenAndContinuesAfterReturn()
        {
            var host = new GameObject("Gas Runtime Test"); host.SetActive(false);
            var profile = ScriptableObject.CreateInstance<GasWorldProfile>();
            // 本例隔离 Owner 冻结/返回行为；生产低浓度与出生空间由上面的 GasImpact 用例独立验证。
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":8,\"_cellCapacityGmu\":4096,\"_maximumActiveCells\":128,\"_maximumCellsPerTick\":32}", profile);
            try
            {
                var runtime = host.AddComponent<GasWorldRuntime>(); Profile(runtime, profile);
                Assert.That(runtime.TryInitialize(null, null, Vector3.one * 100, 1, 2), Is.True);
                var domain = new ElementWorldActivityRegions(); domain.TryAdd(new Bounds(Vector3.zero, Vector3.one * 4)); runtime.SetActivityDomain(domain);
                var request = new ElementWriteRequest(Vector3.one * 100 + Vector3.one * .1f, MaterialId.Steam, 2048, 0, false);
                Assert.That(runtime.TryEnqueueDeposit(in request), Is.True);
                for (int i = 0; i < 300; i++) runtime.Step(.1f);
                runtime.TryGetQuantity(Vector3Int.zero, MaterialId.Steam, out uint amount);
                Assert.That(amount, Is.EqualTo(2048)); Assert.That(runtime.TotalQuantity, Is.EqualTo(2048UL));
                domain.Clear(); domain.TryAdd(new Bounds(Vector3.one * 100, Vector3.one * 16)); runtime.SetActivityDomain(domain); runtime.Step(.1f);
                Assert.That(runtime.TryGetQuantity(Vector3Int.up, MaterialId.Steam, out _), Is.True);
                Assert.That(runtime.TotalQuantity, Is.EqualTo(2048UL));
            }
            finally { Object.DestroyImmediate(host); Object.DestroyImmediate(profile); }
        }
    }
}
