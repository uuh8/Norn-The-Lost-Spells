using Game.Materials;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class LiquidFluxSolverDeadbandTests
    {
        [Test]
        public void OverflowRecoverySharesOneBudgetAcrossAllDequeues()
        {
            var store = new LiquidVolumeStore(8, 32, 255,
                new[] { MaterialId.Water }, cell => cell.y < 0);
            var activity = new LiquidActivityScheduler(1, store);
            for (int i = 0; i < 32; i++)
            {
                var cell = new Vector3Int(i * 8, 0, 0);
                Assert.That(store.TryAdd(cell, MaterialId.Water, 200), Is.True);
                activity.Wake(cell);
            }

            activity.BeginStep();
            var recovered = new System.Collections.Generic.HashSet<Vector3Int>();
            for (int i = 0; i < 64; i++)
                if (activity.TryDequeue(out var cell)) recovered.Add(cell);
            // 观察扫描进度而非墙钟时间，避免机器负载影响性能回归结论。
            var cursor = typeof(LiquidActivityScheduler).GetField("_recoveryCursor",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That((int)cursor.GetValue(activity), Is.LessThanOrEqualTo(1024),
                "一个 Tick 内多次取格子必须共享扫描预算，不能各自重置 1024。");
            for (int tick = 0; tick < 100 && activity.HasPendingRecovery; tick++)
            {
                activity.BeginStep();
                while (activity.TryDequeue(out var cell)) recovered.Add(cell);
            }
            Assert.That(recovered.Count, Is.EqualTo(32), "分摊扫描不能遗忘溢出的材料。");
            Assert.That(store.TotalQuantity, Is.EqualTo(6400UL));
        }

        [Test]
        public void DomainChangeSpreadsSleepingWakeAcrossBoundedSteps()
        {
            var store = new LiquidVolumeStore(8, 64, 255,
                new[] { MaterialId.Water }, cell => cell.y < 0);
            var activity = new LiquidActivityScheduler(2048, store);
            var regions = new ElementWorldActivityRegions();
            regions.TryAdd(new Bounds(Vector3.zero, Vector3.one * 8));
            activity.SetActivityDomain(regions, Vector3.zero, 1f);
            for (int z = 0; z < 64; z++)
            for (int x = 0; x < 32; x++)
            {
                var cell = new Vector3Int(104 + x, 0, z);
                Assert.That(store.TryAdd(cell, MaterialId.Water, 200), Is.True);
                activity.Wake(cell);
            }
            regions.Clear();
            regions.TryAdd(new Bounds(new Vector3(120, 0, 32), Vector3.one * 128));
            activity.SetActivityDomain(regions, Vector3.zero, 1f);
            Assert.That(activity.Count, Is.LessThanOrEqualTo(128),
                "移动活动范围不能在一次调用中唤醒整个休眠集合。");
            var recovered = new System.Collections.Generic.HashSet<Vector3Int>();
            // 先为测试计数器留足空间，测量的分配只来自实际 Scheduler 热路径。
            recovered.EnsureCapacity(2048);
            long allocated = System.GC.GetAllocatedBytesForCurrentThread();
            int largestWake = 0;
            for (int tick = 0; tick < 32; tick++)
            {
                activity.BeginStep();
                largestWake = System.Math.Max(largestWake, activity.Count);
                while (activity.TryDequeue(out var cell)) recovered.Add(cell);
            }
            long bytes = System.GC.GetAllocatedBytesForCurrentThread() - allocated;
            Assert.That(largestWake, Is.LessThanOrEqualTo(128));
            Assert.That(bytes, Is.Zero, "初始化后的唤醒与出队不能产生 Managed Alloc。");
            Assert.That(recovered.Count, Is.EqualTo(2048));
            Assert.That(store.TotalQuantity, Is.EqualTo(409600UL));
        }

        [Test]
        public void DormantOverflowWaitsForDomainChangeAndWakesEveryStoredCell()
        {
            var store=new LiquidVolumeStore(4,8,510,new[]{MaterialId.Water},cell=>cell.y<0);
            var activity=new LiquidActivityScheduler(2,store);
            var regions=new ElementWorldActivityRegions();regions.TryAdd(new Bounds(Vector3.zero,Vector3.one*8));
            activity.SetActivityDomain(regions,Vector3.zero,1f);
            for(int i=0;i<8;i++)
            {
                var cell=new Vector3Int(100+i,0,0);
                Assert.That(store.TryAdd(cell,MaterialId.Water,200),Is.True);activity.Wake(cell);
            }
            Assert.That(activity.HasPendingRecovery,Is.False,"远处休眠队列满时只需保存数量，不能持续全Store重扫。");
            long allocated=System.GC.GetAllocatedBytesForCurrentThread();
            for(int i=0;i<32;i++){activity.BeginStep();var count=activity.Count;}
            Assert.That(System.GC.GetAllocatedBytesForCurrentThread()-allocated,Is.Zero);
            regions.Clear();regions.TryAdd(new Bounds(new Vector3(104,0,0),Vector3.one*16));
            activity.SetActivityDomain(regions,Vector3.zero,1f);
            var woken=new System.Collections.Generic.HashSet<Vector3Int>();
            for(int i=0;i<128 && (activity.Count>0 || activity.HasPendingRecovery);i++)
                if(activity.TryDequeue(out var cell))woken.Add(cell);
            Assert.That(woken.Count,Is.EqualTo(8),"超出休眠队列的格在返回后也必须恢复，不能永久遗忘。");
            Assert.That(store.TotalQuantity,Is.EqualTo(1600UL));
        }

        [Test]
        public void DistantWaterSleepsWithoutLossAndWakesWhenGameplayRegionReturns()
        {
            var materials=new[]{MaterialId.Water};var far=new Vector3Int(100,0,0);
            var store=new LiquidVolumeStore(4,8,510,materials,cell=>cell.y<0);
            var activity=new LiquidActivityScheduler(128,store);
            var solver=new LiquidFluxSolver(store,activity,null,materials,new[]{255},new[]{40},new[]{8});
            Assert.That(store.TryAdd(Vector3Int.zero,MaterialId.Water,200),Is.True);Assert.That(store.TryAdd(far,MaterialId.Water,200),Is.True);
            var scope=typeof(LiquidActivityScheduler).GetMethod("SetActivityDomain");
            Assert.That(scope,Is.Not.Null,"Grid Solver needs the existing world Gameplay activity domain, independently of Camera visibility.");
            var regions=new ElementWorldActivityRegions();regions.TryAdd(new Bounds(Vector3.zero,Vector3.one*8));
            scope.Invoke(activity,new object[]{regions,Vector3.zero,1f});
            activity.Wake(Vector3Int.zero);activity.Wake(far);
            for(int i=0;i<4;i++)solver.Step(128);
            Assert.That(store.TryGetQuantity(far,MaterialId.Water,out uint sleeping),Is.True);Assert.That(sleeping,Is.EqualTo(200));
            Assert.That(store.OccupancyAt(Vector3Int.right),Is.GreaterThan(0));Assert.That(store.TotalQuantity,Is.EqualTo(400UL));
            regions.Clear();regions.TryAdd(new Bounds((Vector3)far,Vector3.one*8));scope.Invoke(activity,new object[]{regions,Vector3.zero,1f});
            for(int i=0;i<4;i++)solver.Step(128);
            Assert.That(store.OccupancyAt(far+Vector3Int.right),Is.GreaterThan(0),"回到活动范围后必须继续流动，不能永久冻结。");
            Assert.That(store.TotalQuantity,Is.EqualTo(400UL));
        }

        [Test]
        public void FallingPoisonHandsPartialStableWaterToLayerSolverBeforeEntering()
        {
            var mats=new[]{MaterialId.Water,MaterialId.Poison};var densities=new[]{1f,.9f};
            var store=new LiquidVolumeStore(4,4,255,mats,cell=>cell!=Vector3Int.zero && cell!=Vector3Int.up,densities);
            var activity=new LiquidActivityScheduler(32,store);
            var regions=new StableLiquidRegionRuntime(4,4,16,store,activity,Vector3.zero,1f);
            store.SetExternalOccupancy(regions.OccupancyAt);
            store.SetDynamicOccupancyChangedCallback(regions.OnDynamicOccupancyChanged);
            Assert.That(regions.TryRegister(MaterialId.Water,Vector3Int.zero,Vector3Int.one,
                1,0,100,out _),Is.True);
            Assert.That(store.TryAdd(Vector3Int.up,MaterialId.Poison,40),Is.True);
            var solver=new LiquidFluxSolver(store,activity,regions,mats,new[]{255,255},new[]{80,40},
                new[]{0,0},relativeDensities:densities);
            activity.Wake(Vector3Int.up);solver.Step(1);
            Assert.That(regions.RegionCount,Is.Zero);
            Assert.That(store.TryGetQuantity(Vector3Int.zero,MaterialId.Water,out uint water),Is.True);
            Assert.That(water,Is.EqualTo(100));
            Assert.That(store.TryGetSurfaceFill(Vector3Int.zero,MaterialId.Poison,out float fill),Is.True);
            Assert.That(fill,Is.EqualTo(140f/255f).Within(1e-6f));
            Assert.That(store.TotalQuantity,Is.EqualTo(140UL));
        }

        [Test]
        public void DryFrontKeepsWaterAndStickyFlowRateDifference()
        {
            uint water=AdvanceDryFront(MaterialId.Water,80);
            uint sticky=AdvanceDryFront(MaterialId.Sticky,16);
            Assert.That(water,Is.EqualTo(80));
            Assert.That(sticky,Is.EqualTo(16));
            Assert.That(water,Is.GreaterThan(sticky*3));
        }

        private static uint AdvanceDryFront(MaterialId material,int rate)
        {
            var store=new LiquidVolumeStore(4,4,510,new[]{material},
                cell=>cell!=Vector3Int.zero && cell!=Vector3Int.right);
            var activity=new LiquidActivityScheduler(32,store);
            var solver=new LiquidFluxSolver(store,activity,null,new[]{material},new[]{510},
                new[]{rate},new[]{0},spreadReserves:new[]{204});
            Assert.That(store.TryAdd(Vector3Int.zero,material,510),Is.True);
            activity.Wake(Vector3Int.zero);
            solver.Step(1);
            store.TryGetQuantity(Vector3Int.right,material,out uint moved);
            Assert.That(store.TotalQuantity,Is.EqualTo(510UL));
            return moved;
        }

        [Test]
        public void LightPoisonRisesAboveWaterWithoutChangingEitherMaterialTotal()
        {
            var lower=Vector3Int.zero;var upper=Vector3Int.up;
            var mats=new[]{MaterialId.Water,MaterialId.Poison};var densities=new[]{1f,.9f};
            var store=new LiquidVolumeStore(4,4,510,mats,cell=>cell!=lower && cell!=upper,densities);
            var activity=new LiquidActivityScheduler(32,store);
            var solver=new LiquidFluxSolver(store,activity,null,mats,new[]{100,100},new[]{10,10},new[]{0,0},
                relativeDensities:densities);
            Assert.That(store.TryAdd(lower,MaterialId.Poison,510),Is.True);
            Assert.That(store.TryAdd(upper,MaterialId.Water,510),Is.True);
            for(int i=0;i<12;i++){activity.Wake(lower);activity.Wake(upper);solver.Step(32);}
            store.TryGetQuantity(upper,MaterialId.Poison,out uint top);
            store.TryGetQuantity(lower,MaterialId.Water,out uint bottom);
            Assert.That(top,Is.EqualTo(510));Assert.That(bottom,Is.EqualTo(510));
            Assert.That(store.TotalQuantity,Is.EqualTo(1020UL));
        }

        [Test]
        public void WaterDoesNotFlowHorizontallyIntoHigherMixedLiquidColumn()
        {
            var left=Vector3Int.zero;var right=Vector3Int.right;
            var mats=new[]{MaterialId.Water,MaterialId.Poison};
            var store=new LiquidVolumeStore(4,4,510,mats,cell=>cell!=left && cell!=right);
            var activity=new LiquidActivityScheduler(32,store);
            var solver=new LiquidFluxSolver(store,activity,null,mats,new[]{255,255},new[]{80,80},new[]{0,0});
            Assert.That(store.TryAdd(left,MaterialId.Water,200),Is.True);
            Assert.That(store.TryAdd(right,MaterialId.Poison,400),Is.True);
            activity.Wake(left);solver.Step(1);
            Assert.That(store.TryGetQuantity(right,MaterialId.Water,out _),Is.False);
            Assert.That(store.TotalQuantity,Is.EqualTo(600UL));
        }

        [Test]
        public void SmallDifferenceSleepsButVisibleDifferenceFlowsWithoutLosingQuantity()
        {
            var left = Vector3Int.zero;
            var right = Vector3Int.right;
            var store = new LiquidVolumeStore(4, 4, 255,
                new[] { MaterialId.Water },
                cell => cell != left && cell != right);
            var activity = new LiquidActivityScheduler(32, store);
            var solver = new LiquidFluxSolver(store, activity, null,
                new[] { MaterialId.Water }, new[] { 255 }, new[] { 40 }, new[] { 8 });
            Assert.That(store.TryAdd(left, MaterialId.Water, 100), Is.True);
            Assert.That(store.TryAdd(right, MaterialId.Water, 94), Is.True);
            activity.WakeNeighbors(left);
            activity.WakeNeighbors(right);

            solver.Step(32);

            store.TryGetQuantity(left, MaterialId.Water, out uint quietLeft);
            store.TryGetQuantity(right, MaterialId.Water, out uint quietRight);
            Assert.That(quietLeft, Is.EqualTo(100));
            Assert.That(quietRight, Is.EqualTo(94));

            Assert.That(store.TryAdd(left, MaterialId.Water, 4), Is.True);
            activity.WakeNeighbors(left);
            solver.Step(32);

            store.TryGetQuantity(left, MaterialId.Water, out uint flowingLeft);
            store.TryGetQuantity(right, MaterialId.Water, out uint flowingRight);
            Assert.That(flowingLeft, Is.LessThan(104));
            Assert.That(flowingRight, Is.GreaterThan(94));
            Assert.That(store.TotalQuantity, Is.EqualTo(198UL));
        }
    }
}
