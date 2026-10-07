using Game.Combat;
using Game.Materials;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class MaterialReactionProcessTableTests
    {
        [TestCase(4)]
        [TestCase(65536)]
        public void ReservedCapacityKeepsOutputOrderCooldownAndVacantSlotReuse(int capacity)
        {
            var tuning=new ToxicCombustionTuning {FireThreshold=1,PoisonThreshold=1,
                WindUpSeconds=.2f,MaxPoisonConsume=40,MinEffectiveConsume=1,
                BaseDamage=5,DamagePerPoison=1,Radius=2,CooldownSeconds=.3f};
            var table=new MaterialReactionProcessTable(capacity);var occupancy=new EveryCellPoison();
            var output=new ToxicWorldReactionResolution[2];
            for(int x=0;x<3;x++)Assert.That(table.TryStartToxic(new Vector3Int(x*11,0,0),
                new Vector3Int(x*11,0,0),100,80,in tuning),Is.True);
            Assert.That(table.TickToxic(.2f,occupancy,in tuning,output),Is.EqualTo(2));
            Assert.That(output[0].LiquidCell.x,Is.EqualTo(0));Assert.That(output[1].LiquidCell.x,Is.EqualTo(11));
            Assert.That(table.TryStartToxic(Vector3Int.zero,Vector3Int.zero,100,80,in tuning),Is.False);
            // destination装满时第三条不能提前推进；下一Tick才完成它的完整预热。
            Assert.That(table.TickToxic(.2f,occupancy,in tuning,output),Is.EqualTo(1));
            Assert.That(output[0].LiquidCell.x,Is.EqualTo(22));Assert.That(output[0].ConsumedGmu,Is.EqualTo(12));
            table.TickToxic(.15f,occupancy,in tuning,output);
            Assert.That(table.TryStartToxic(new Vector3Int(33,0,0),new Vector3Int(33,0,0),100,80,in tuning),Is.True);
            Assert.That(table.TryStartToxic(new Vector3Int(44,0,0),new Vector3Int(44,0,0),100,80,in tuning),Is.True);
            Assert.That(table.TickToxic(.2f,occupancy,in tuning,output),Is.EqualTo(2));
            Assert.That(output[0].LiquidCell.x,Is.EqualTo(33));Assert.That(output[1].LiquidCell.x,Is.EqualTo(44));
            table.TickToxic(.5f,occupancy,in tuning,output);
            Assert.That(table.TryStartToxic(Vector3Int.zero,Vector3Int.zero,100,80,in tuning),Is.True);
            // 只记录时间，不用抖动的性能阈值判断成功。无燃料查询必须保持失败且零分配。
            const int rounds=6,queries=512;var times=new double[rounds];var missing=new Vector3Int(1000,0,0);
            table.TryStartToxic(missing,missing,0,0,in tuning);long gc=System.GC.GetAllocatedBytesForCurrentThread();
            double elapsed=0;int accepted=0;
            for(int round=0;round<rounds;round++)
            {
                long started=System.Diagnostics.Stopwatch.GetTimestamp();
                for(int i=0;i<queries;i++)if(table.TryStartToxic(missing,missing,0,0,in tuning))accepted++;
                double ms=(System.Diagnostics.Stopwatch.GetTimestamp()-started)*1000.0/System.Diagnostics.Stopwatch.Frequency;
                times[round]=ms;elapsed+=ms;
            }
            long allocated=System.GC.GetAllocatedBytesForCurrentThread()-gc;
            System.IO.File.WriteAllText("Temp/ElementPerformance/toxic-reserved-scan-"+capacity+".json",JsonUtility.ToJson(
                new ReservedScanReport {Utc=System.DateTime.UtcNow.ToString("O"),Capacity=capacity,Queries=rounds*queries,
                QueryMeanNs=elapsed*1e6/(rounds*queries),RoundsMs=times,GcBytes=allocated},true));
            Assert.That(accepted,Is.Zero);Assert.That(allocated,Is.Zero);
        }

        [System.Serializable]private sealed class ReservedScanReport
        {public string Utc;public int Capacity,Queries;public double QueryMeanNs;public double[] RoundsMs;public long GcBytes;}
        private sealed class EveryCellPoison:IMaterialAmountReadOnly
        {public bool TryGetAmount(Vector3Int cell,MaterialId material,out byte amount){amount=material==MaterialId.Poison?(byte)12:(byte)0;return amount>0;}}

        [Test]
        public void SameContactStartsOnceAndResolvesUsingLatestSnapshot()
        {
            var tuning = new ToxicCombustionTuning
            {
                FireThreshold = 1f, PoisonThreshold = 1f, WindUpSeconds = 0.2f,
                MaxPoisonConsume = 40f, MinEffectiveConsume = 1f,
                BaseDamage = 5f, DamagePerPoison = 1f, Radius = 2f,
                CooldownSeconds = 0.3f,
            };
            var table = new MaterialReactionProcessTable(2);
            Vector3Int fire = Vector3Int.zero;
            Vector3Int poison = Vector3Int.right;
            Assert.That(table.TryStartToxic(fire, poison, 100, 80, in tuning), Is.True);
            Assert.That(table.TryStartToxic(fire, poison, 100, 80, in tuning), Is.False);
            var occupancy = new PoisonOccupancy(poison, 12);
            var output = new ToxicWorldReactionResolution[1];
            Assert.That(table.TickToxic(0.1f, occupancy, in tuning, output), Is.Zero);
            Assert.That(table.TickToxic(0.1f, occupancy, in tuning, output), Is.EqualTo(1));
            Assert.That(output[0].ConsumedGmu, Is.EqualTo(12));
            Assert.That(output[0].Damage, Is.EqualTo(17f));
            Assert.That(table.TryStartToxic(fire, poison, 100, 80, in tuning), Is.False,
                "同一接触点在 Cooldown 内不能每 Tick 重复触发毒爆。");
            Assert.That(table.TickToxic(0.3f, occupancy, in tuning, output), Is.Zero);
            Assert.That(table.TryStartToxic(fire, poison, 100, 80, in tuning), Is.True);
        }

        [Test]
        public void GridToxicCoalescesNeighboringFireContactsAndSurvivesPoisonMovement()
        {
            var character=new ToxicCombustionTuning { FireThreshold=25,PoisonThreshold=20,
                WindUpSeconds=.2f,MaxPoisonConsume=40,MinEffectiveConsume=5,
                BaseDamage=8,DamagePerPoison=.4f,Radius=2.5f,CooldownSeconds=1 };
            ToxicCombustionTuning world=LiquidReactionAdapter.CreateWorldToxicTuning(in character);
            var table=new MaterialReactionProcessTable(16,10,true);
            var occupancy=new PoisonOccupancy(Vector3Int.zero,8);
            Assert.That(table.TryStartToxic(Vector3Int.zero,Vector3Int.zero,8,8,in character),Is.False);
            Assert.That(table.TryStartToxic(Vector3Int.zero,Vector3Int.zero,8,8,in world),Is.True);
            for(int x=-2;x<=2;x++)
                Assert.That(table.TryStartToxic(new Vector3Int(x,1,0),new Vector3Int(x,0,0),
                    30,30,in world),Is.False,"同一爆炸范围不能为每个火格再登记一份毒爆。");
            var output=new ToxicWorldReactionResolution[16];
            Assert.That(table.TickToxic(.1f,occupancy,in world,output),Is.Zero);
            occupancy.Cell=Vector3Int.right; // 预热期间材料移到相邻格，原触发格已经为空。
            Assert.That(table.TickToxic(.1f,occupancy,in world,output),Is.EqualTo(1));
            Assert.That(output[0].ConsumedGmu,Is.EqualTo(8));
            Assert.That(output[0].Damage,Is.EqualTo(11.2f).Within(.001f));
            Assert.That(table.TryStartToxic(Vector3Int.up,Vector3Int.right,8,8,in world),Is.False);
            table.TickToxic(1f,occupancy,in world,output);
            Assert.That(table.TryStartToxic(Vector3Int.up,Vector3Int.right,8,8,in world),Is.True);
            Assert.That(character.FireThreshold,Is.EqualTo(25),"角色阈值不应被世界适配器修改。");
        }

        [Test]
        public void GridToxicCanStartSeparateBlastOutsideRadiusAndDoesNotDamageWithoutFuel()
        {
            var tuning=new ToxicCombustionTuning { FireThreshold=1,PoisonThreshold=1,
                WindUpSeconds=.2f,MaxPoisonConsume=40,MinEffectiveConsume=5,
                BaseDamage=8,DamagePerPoison=.4f,Radius=2.5f,CooldownSeconds=1 };
            var table=new MaterialReactionProcessTable(4,10,true);
            Assert.That(table.TryStartToxic(Vector3Int.zero,Vector3Int.zero,8,8,in tuning),Is.True);
            Assert.That(table.TryStartToxic(new Vector3Int(11,0,0),new Vector3Int(11,0,0),
                8,8,in tuning),Is.True);
            var output=new ToxicWorldReactionResolution[4];
            int count=table.TickToxic(.2f,new PoisonOccupancy(Vector3Int.zero,0),in tuning,output);
            Assert.That(count,Is.EqualTo(2));
            for(int i=0;i<count;i++){Assert.That(output[i].ConsumedGmu,Is.Zero);Assert.That(output[i].Damage,Is.Zero);}
        }

        [Test]
        public void GridAdapterConsumesMovingThinPoisonOnceWithoutTickAllocation()
        {
            var host=new GameObject("Grid toxic regression");
            try
            {
                var runtime=host.AddComponent<LiquidWorldRuntime>();
                var store=new LiquidVolumeStore(4,16,255,
                    new[]{MaterialId.Water,MaterialId.Poison,MaterialId.Sticky},c=>c.y<0);
                var activity=new LiquidActivityScheduler(128,store);
                var stable=new StableLiquidRegionRuntime(8,8,128,store,activity,Vector3.zero,.25f);
                SetRuntime(runtime,"_store",store);SetRuntime(runtime,"_activity",activity);
                SetRuntime(runtime,"_stable",stable);SetRuntime(runtime,"_cellSize",.25f);
                for(int z=0;z<3;z++)for(int x=0;x<3;x++)store.TryAdd(new Vector3Int(x,0,z),MaterialId.Poison,4);
                MaterialCatalog materials=AssetDatabase.LoadAssetAtPath<MaterialCatalog>(
                    "Assets/_Project/ScriptableObjects/Materials/MaterialCatalog_Default.asset");
                var bindings=AssetDatabase.LoadAssetAtPath<MaterialReactionBindingProfile>(
                    "Assets/_Project/ScriptableObjects/Combat/Reactions/MaterialReactionBindings_Default.asset");
                var profile=AssetDatabase.LoadAssetAtPath<ElementReactionProfile>(
                    "Assets/_Project/ScriptableObjects/Status/ElementReactionProfile_Default.asset");
                ElementReactionTuningSnapshot tuning=profile.CreateSnapshot(1f,1f);
                int blasts=0;WorldReactionDamageCommand last=default;
                var adapter=new LiquidReactionAdapter(runtime,new WeakFire(),
                    bindings.CreateSnapshot(materials.CreateSnapshot()),in tuning,16,
                    (in ElementCellDeltaRequest request)=>false,(in ElementCellAddRequest request)=>false,
                    (in WorldReactionDamageCommand command)=>{blasts++;last=command;});
                var fireCells=new[]{new LiquidCellFireSample(Vector3Int.zero,8),
                    new LiquidCellFireSample(Vector3Int.right,8),new LiquidCellFireSample(Vector3Int.right*2,8)};
                adapter.Process(fireCells,3,.1f);
                Assert.That(blasts,Is.Zero);
                store.TryRemove(Vector3Int.zero,MaterialId.Poison,4);
                store.TryAdd(Vector3Int.forward,MaterialId.Poison,4);
                long before=System.GC.GetAllocatedBytesForCurrentThread();
                adapter.Process(fireCells,3,.1f);
                long allocated=System.GC.GetAllocatedBytesForCurrentThread()-before;
                Assert.That(blasts,Is.EqualTo(1));
                Assert.That(last.Amount,Is.EqualTo(8+16*.4f).Within(.001f));
                Assert.That(store.TotalQuantity,Is.EqualTo(20UL));
                Assert.That(allocated,Is.Zero);
                adapter.Process(fireCells,3,.1f);
                Assert.That(blasts,Is.EqualTo(1),"不同火格不能绕过局部冷却。");
            }
            finally {Object.DestroyImmediate(host);}
        }

        private static void SetRuntime(LiquidWorldRuntime runtime,string field,object value) =>
            typeof(LiquidWorldRuntime).GetField(field,System.Reflection.BindingFlags.Instance
                |System.Reflection.BindingFlags.NonPublic).SetValue(runtime,value);

        private sealed class WeakFire : IMaterialAmountReadOnly
        {
            public bool TryGetAmount(Vector3Int cell,MaterialId material,out byte amount)
            {
                amount=material==MaterialId.Fire && cell.y==0 && cell.z==0 && cell.x>=0 && cell.x<=2
                    ?(byte)8:(byte)0;return amount>0;
            }
        }

        private sealed class PoisonOccupancy : ILiquidOccupancyReadOnly
        {
            private Vector3Int _cell; private readonly byte _amount;
            public Vector3Int Cell {set=>_cell=value;}
            public PoisonOccupancy(Vector3Int cell, byte amount) { _cell = cell; _amount = amount; }
            public bool HasValidSnapshot => true;
            public Bounds SnapshotBounds => new Bounds(Vector3.zero, Vector3.one);
            public uint SnapshotVersion => 1;
            public bool TryGetAmountUnitsPerParticle(MaterialId material, out uint amountUnits)
            { amountUnits = material == MaterialId.Poison ? 4u : 8u; return true; }
            public int CopyOccupiedCells(MaterialId material, LiquidMaterialCellSample[] destination) => 0;
            public bool TryGetAmount(Vector3Int cell, MaterialId material, out byte amount)
            { amount = cell == _cell && material == MaterialId.Poison ? _amount : (byte)0; return amount > 0; }
        }
    }
}
