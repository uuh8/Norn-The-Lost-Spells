using Game.Materials;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// 固定真实GMU与真实Store/Solver，只比较新空格的传播门槛；不拿Render增厚冒充水铺开。
    /// 单层平地用同一Solid采样合同表示，排除场景Collider与Camera对数值实验的干扰。
    /// </summary>
    public sealed class LiquidNaturalSpreadTests
    {
        private const int Capacity=510,WaterRate=112,WaterDeadband=3;
        private sealed class Plate
        {
            internal readonly LiquidVolumeStore Store;
            internal readonly LiquidActivityScheduler Activity;
            internal readonly LiquidFluxSolver Solver;
            internal readonly MaterialId Material;
            internal Plate(float reserve,MaterialId material=MaterialId.Water,int rate=WaterRate,
                int deadband=WaterDeadband,bool corridor=false)
            {
                Material=material;
                Store=new LiquidVolumeStore(4,128,Capacity,new[]{material},
                    c=>c.y!=0 || (corridor?(c.z!=0 || c.x<0 || c.x>1):Mathf.Abs(c.x)>16 || Mathf.Abs(c.z)>16));
                Activity=new LiquidActivityScheduler(2048,Store);
                Solver=new LiquidFluxSolver(Store,Activity,null,new[]{material},new[]{255},new[]{rate},new[]{deadband},
                    spreadReserves:new[]{Mathf.RoundToInt(reserve*Capacity)});
            }
            internal void Add(Vector3Int cell,uint amount)
            {Assert.That(Store.TryAdd(cell,Material,amount),Is.True);Activity.WakeNeighbors(cell);}
            internal void Step(int ticks,ulong expected)
            {
                for(int tick=0;tick<ticks;tick++)
                {
                    Solver.Step(2048,.1f,tick*.1f);
                    Assert.That(Store.TotalQuantity,Is.EqualTo(expected),"新空格传播、休眠与容量拒绝都不能损失GMU。");
                }
            }
            internal (int Count,uint Maximum,uint Minimum,int Radius) Measure()
            {
                var cells=new LiquidMaterialCellSample[4096];int count=Store.CopyOccupiedCells(Material,cells);
                uint largest=0,smallest=uint.MaxValue;int radius=0;ulong sum=0;
                for(int i=0;i<count;i++)
                {
                    var cell=cells[i].GlobalCell;
                    Assert.That(cell.y,Is.Zero);
                    Assert.That(Store.TryGetQuantity(cell,Material,out uint amount),Is.True);
                    // LiquidMaterialCellSample.Amount是旧byte采样，本实验直接读原生uint，不能截断510。
                    largest=System.Math.Max(largest,amount);smallest=System.Math.Min(smallest,amount);sum+=amount;
                    radius=Mathf.Max(radius,Mathf.Max(Mathf.Abs(cell.x),Mathf.Abs(cell.z)));
                }
                Assert.That(sum,Is.EqualTo(Store.TotalQuantity));
                return(count,largest,smallest,radius);
            }
        }

        [Test]
        public void SmallWaterPourCanWetADryNeighborWithoutAQuarterCellMinimum()
        {
            var plate=new Plate(.03f,corridor:true);plate.Add(Vector3Int.zero,30);plate.Step(1,30);
            Assert.That(plate.Store.TryGetQuantity(Vector3Int.right,MaterialId.Water,out uint moved),Is.True,
                "Water Reserve15GMU时，30GMU应该能留下15、向邻空格流15，不能继续被112GMU旧seed堵住。");
            Assert.That(moved,Is.EqualTo(15));
            Assert.That(plate.Store.OccupancyAt(Vector3Int.zero),Is.EqualTo(15));
        }

        [Test]
        public void FixedWaterQuantityQuantifiesPointFourteenVersusPointZeroThreeReserve()
        {
            var thick=new Plate(.14f);var shallow=new Plate(.03f);
            foreach(var plate in new[]{thick,shallow})
            {plate.Add(Vector3Int.zero,510);plate.Add(Vector3Int.right,510);plate.Step(256,1020);}
            var a=thick.Measure();var b=shallow.Measure();
            TestContext.Out.WriteLine($"1020GMU fixed: Reserve.14 wet={a.Count},max={a.Maximum},min={a.Minimum},radius={a.Radius},mean={1020f/a.Count:F3}; Reserve.03 wet={b.Count},max={b.Maximum},min={b.Minimum},radius={b.Radius},mean={1020f/b.Count:F3}");
            Assert.That(b.Count,Is.GreaterThan(a.Count),"同量低Reserve应能形成更浅、更宽的水面。");
            Assert.That(b.Maximum,Is.LessThan(a.Maximum));
        }

        [Test]
        public void LowerWaterReserveStillHasABoundedFootprintAndEventuallySleeps()
        {
            var plate=new Plate(.03f);plate.Add(Vector3Int.zero,510);plate.Add(Vector3Int.right,510);plate.Step(256,1020);
            var measured=plate.Measure();
            Assert.That(measured.Count,Is.LessThanOrEqualTo(128),"1020GMU不能唤醒整张33×33地板或变成数百个1GMU尾格。");
            Assert.That(measured.Minimum,Is.GreaterThan(1));
            Assert.That(measured.Radius,Is.LessThan(10));
            Assert.That(plate.Activity.Count,Is.Zero,"水位差进入原Deadband后仍应休眠。");
            uint version=plate.Store.SurfaceVersion;plate.Step(16,1020);
            Assert.That(plate.Store.SurfaceVersion,Is.EqualTo(version));
        }

        [Test]
        public void ExistingWetCellsKeepTheirDeadbandAndExactQuantity()
        {
            var plate=new Plate(.03f,corridor:true);plate.Add(Vector3Int.zero,15);plate.Add(Vector3Int.right,13);
            uint version=plate.Store.SurfaceVersion;plate.Step(16,28);
            Assert.That(plate.Store.OccupancyAt(Vector3Int.zero),Is.EqualTo(15));
            Assert.That(plate.Store.OccupancyAt(Vector3Int.right),Is.EqualTo(13));
            Assert.That(plate.Store.SurfaceVersion,Is.EqualTo(version));Assert.That(plate.Activity.Count,Is.Zero);
        }

        [Test]
        public void StableOwnerAbsorbsSmallWaterWithoutBeingBlockedByDryFrontSeed()
        {
            var store=new LiquidVolumeStore(4,16,Capacity,new[]{MaterialId.Water},c=>c.y!=0 || c.z!=0 || c.x<0 || c.x>4);
            var activity=new LiquidActivityScheduler(128,store);
            var stable=new StableLiquidRegionRuntime(1,1,4,store,activity,Vector3.zero,.25f);
            store.SetExternalOccupancy(stable.OccupancyAt);store.SetDynamicOccupancyChangedCallback(stable.OnDynamicOccupancyChanged);
            Assert.That(stable.TryRegister(MaterialId.Water,Vector3Int.right,new Vector3Int(5,1,1),1,0,0,out _),Is.True);
            // 故意用高于30GMU源量的Reserve，误把Stable当新干格时必定被门槛拒绝。
            var solver=new LiquidFluxSolver(store,activity,stable,new[]{MaterialId.Water},new[]{255},new[]{WaterRate},new[]{WaterDeadband},spreadReserves:new[]{71});
            Assert.That(store.TryAdd(Vector3Int.zero,MaterialId.Water,30),Is.True);activity.WakeNeighbors(Vector3Int.zero);
            for(int tick=0;tick<32;tick++)
            {solver.Step(128,.1f,tick*.1f);Assert.That(store.TotalQuantity+stable.TotalQuantity,Is.EqualTo(30));}
            Assert.That(stable.TotalQuantity,Is.GreaterThan(0));Assert.That(store.OccupancyAt(Vector3Int.right),Is.EqualTo(stable.OccupancyAt(Vector3Int.right)));
            Assert.That(store.TryGetQuantity(Vector3Int.right,MaterialId.Water,out _),Is.False,"Stable拥有的格不能出现第二份Dynamic数量。");
        }

        [Test]
        public void StickyKeepsItsViscousDryFrontWhileSmallWaterSpreads()
        {
            var water=new Plate(.03f,corridor:true);var sticky=new Plate(.55f,MaterialId.Sticky,16,8,true);
            water.Add(Vector3Int.zero,30);sticky.Add(Vector3Int.zero,30);water.Step(1,30);sticky.Step(1,30);
            Assert.That(water.Store.OccupancyAt(Vector3Int.right),Is.EqualTo(15));
            Assert.That(sticky.Store.OccupancyAt(Vector3Int.right),Is.Zero,"低水张力不能顺便把Sticky改成水。");
            var waterFull=new Plate(.03f,corridor:true);var stickyFull=new Plate(.55f,MaterialId.Sticky,16,8,true);
            waterFull.Add(Vector3Int.zero,510);stickyFull.Add(Vector3Int.zero,510);waterFull.Step(1,510);stickyFull.Step(1,510);
            Assert.That(waterFull.Store.OccupancyAt(Vector3Int.right),Is.EqualTo(112));
            Assert.That(stickyFull.Store.OccupancyAt(Vector3Int.right),Is.EqualTo(16),"既有每Tick通量差异保持不变。");
        }
    }
}
