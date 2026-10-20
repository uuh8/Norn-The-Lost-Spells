using Game.Materials;
using Game.Combat;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class LiquidVolumeMappingTests
    {
        private const System.Reflection.BindingFlags Fields = System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic;

        [Test]
        public void PresentationSupportCacheInvalidatesAndKeepsExactResultsWhenFull()
        {
            var host = new GameObject("Presentation terrain cache lifecycle");
            host.SetActive(false);
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                floor.transform.position = new Vector3(1, -.25f, 0);
                floor.transform.localScale = new Vector3(16, .5f, 8);
                Physics.SyncTransforms();
                var store = new LiquidVolumeStore(4, 2, 255,
                    new[] { MaterialId.Water }, cell => cell.y == -1);
                var cache = new LiquidTerrainSupportCache(host.AddComponent<ElementFieldSolidBaker>(),
                    store, Vector3.zero, .5f, 2);
                // 容量只有2，第三个坐标必须实时查询，不能以缓存满为由换成粗格地板。
                for (int x = 0; x < 3; x++)
                {
                    Assert.That(cache.TryGetForPresentation(new Vector3Int(x, 0, 0), out var support), Is.True);
                    Assert.That(support.Height, Is.EqualTo(0f).Within(.0001f));
                }
                var entries=(System.Collections.Generic.Dictionary<Vector3Int,LiquidTerrainSupport>)
                    typeof(LiquidTerrainSupportCache).GetField("_presentationCache",Fields).GetValue(cache);
                Assert.That(entries.Count,Is.EqualTo(2));
                Assert.That(entries.ContainsKey(new Vector3Int(2,0,0)),Is.True,
                    "满缓存后仍须复用槽位缓存新地点，不能让新探索区域永久重复Raycast。");
                // Raycast起点为0.125m，地板抬升后仍须低于起点，避免从Collider表面发射。
                floor.transform.position += Vector3.up * .0625f;
                Physics.SyncTransforms();
                cache.Invalidate();
                for (int x = 0; x < 3; x++)
                {
                    Assert.That(cache.TryGetForPresentation(new Vector3Int(x, 0, 0), out var support), Is.True);
                    Assert.That(support.Height, Is.EqualTo(.0625f).Within(.0001f));
                }
                Assert.That(cache.FallbackCount, Is.Zero);
                Assert.That((int)typeof(LiquidTerrainSupportCache).GetField("_remaining", Fields)
                    .GetValue(cache), Is.EqualTo(128));
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(floor);
            }
        }

        [TestCase(25f)]
        [TestCase(30f)]
        [TestCase(60f)]
        public void ActualRotatedColliderSuppliesPlaneAndDownhillVelocity(float angle)
        {
            using(var fixture=new Fixture())
            {
                fixture.Box("Slope",new Vector3(2,-.25f,2),new Vector3(6,.5f,6));
                fixture.Host.transform.GetChild(0).rotation=Quaternion.Euler(0,0,-angle);
                float radians=angle*Mathf.Deg2Rad;
                int y=Mathf.CeilToInt((-.25f+.25f/Mathf.Cos(radians)-.5f*Mathf.Tan(radians))/.5f);
                Physics.SyncTransforms();fixture.Initialize();var cell=new Vector3Int(5,y,4);
                Assert.That(fixture.Runtime.TryGetTerrainSupport(cell,out var support),Is.True);
                Assert.That(support.Normal.x,Is.EqualTo(Mathf.Sin(radians)).Within(.01f));Assert.That(support.Normal.y,Is.EqualTo(Mathf.Cos(radians)).Within(.01f));
                Assert.That(fixture.Runtime.TryAddInitial(cell,MaterialId.Water,510,true),Is.True);
                fixture.Runtime.Step(.1f);
                fixture.Runtime.TryGetPresentationSample(cell,MaterialId.Water,false,out var current);
                Assert.That(current.VelocityWorld.x,Is.GreaterThan(0));
                Assert.That(Vector3.Dot(current.VelocityWorld,support.Normal),Is.EqualTo(0).Within(.001f));
                Assert.That(fixture.Runtime.TotalQuantity,Is.EqualTo(510UL));
            }
        }

        [Test]
        public void CommittedHistoryPreservesDonorBeforeGravityTransfer()
        {
            using(var fixture=new Fixture())
            {
                fixture.Initialize();var cell=new Vector3Int(1,4,1);
                fixture.Runtime.TryAddInitial(cell,MaterialId.Water,510,true);
                fixture.Runtime.Step(.1f);
                fixture.Runtime.TryGetPresentationSample(cell,MaterialId.Water,true,out var previous);
                Assert.That(previous.Quantity,Is.EqualTo(510));
                Assert.That(previous.OffsetCells,Is.EqualTo(Vector3.zero));
                Assert.That(fixture.Runtime.TransportEpoch,Is.EqualTo(1));
                Assert.That(fixture.Runtime.TotalQuantity,Is.EqualTo(510UL));
                var changed=new Vector3Int[1024];Assert.That(fixture.Runtime.CopyTransportChangedCells(changed),Is.GreaterThan(0));
            }
        }

        [Test]
        public void DifferentDensityWorldProjectionKeepsPoisonAboveWater()
        {
            using (var fixture = new Fixture())
            {
                fixture.SetDensity(MaterialId.Water, 1f);
                fixture.SetDensity(MaterialId.Poison, .9f);
                fixture.Initialize();
                var runtime = fixture.Runtime;
                runtime.TryAddInitial(Vector3Int.zero, MaterialId.Water, 306, false);
                runtime.TryAddInitial(Vector3Int.zero, MaterialId.Poison, 204, false);
                Assert.That(runtime.TryGetWorldAmount(Vector3Int.zero, MaterialId.Poison, out _), Is.False,
                    "0–.25m的细格完全在Water中，不能按整池份额得到Poison。");
                Assert.That(runtime.TryGetWorldAmount(Vector3Int.zero, MaterialId.Water, out byte bottom), Is.True);
                Assert.That(bottom, Is.EqualTo(255));
                Assert.That(runtime.TryGetWorldAmount(Vector3Int.up, MaterialId.Poison, out byte top), Is.True);
                Assert.That(top, Is.EqualTo(204));
                runtime.TryGetWorldAmount(Vector3Int.up, MaterialId.Water, out byte upperWater);
                Assert.That(upperWater, Is.EqualTo(51));
                Assert.That(runtime.TotalQuantity, Is.EqualTo(510UL));
            }
        }

        [Test]
        public void PoisonContactExpandsOnlyItsStableWaterOwnerBeforeCreatingSurface()
        {
            using(var fixture=new Fixture())
            {
                fixture.SetDensity(MaterialId.Water,1f);fixture.SetDensity(MaterialId.Poison,.9f);
                fixture.Initialize();
                var runtime=fixture.Runtime;
                Assert.That(runtime.TryRegisterStableRegion(MaterialId.Water,Vector3Int.zero,
                    new Vector3Int(4,1,4),4,0,3200,out _),Is.True);
                Assert.That(runtime.TryRegisterStableRegion(MaterialId.Water,new Vector3Int(12,0,0),
                    new Vector3Int(16,1,4),16,0,3200,out _),Is.True);
                var contact=new Vector3Int(1,0,1);
                Assert.That(runtime.TryAddInitial(contact,MaterialId.Poison,40,false),Is.True);
                Assert.That(runtime.TryGetDynamicQuantity(contact,MaterialId.Water,out uint water),Is.True,
                    "接触片区必须交给统一的动态分层，不能保留两张穿插透明壳。");
                Assert.That(water,Is.EqualTo(200));
                Assert.That(runtime.StableRegionCount,Is.EqualTo(1),"远处静水不得整湖展开。");
                Assert.That(runtime.TryGetSurfaceFill(contact,MaterialId.Poison,out float fill),Is.True);
                Assert.That(fill,Is.EqualTo(240f/510f).Within(1e-6f));
                Assert.That(runtime.TotalQuantity,Is.EqualTo(6440UL));
            }
        }

        [Test]
        public void StableWaterConversionKeepsOneMixedSurfaceOwnerAndExactTotal()
        {
            using(var fixture=new Fixture())
            {
                fixture.SetDensity(MaterialId.Water,1f);fixture.SetDensity(MaterialId.Poison,.9f);
                fixture.Initialize();var runtime=fixture.Runtime;
                Assert.That(runtime.TryRegisterStableRegion(MaterialId.Water,Vector3Int.zero,
                    new Vector3Int(4,1,4),4,0,3200,out _),Is.True);
                Assert.That(runtime.TryConvert(Vector3Int.zero,MaterialId.Water,MaterialId.Poison,40),Is.True);
                Assert.That(runtime.StableRegionCount,Is.Zero);
                Assert.That(runtime.TryGetDynamicQuantity(Vector3Int.zero,MaterialId.Water,out uint water),Is.True);
                Assert.That(water,Is.EqualTo(160));
                Assert.That(runtime.TryGetSurfaceFill(Vector3Int.zero,MaterialId.Poison,out float fill),Is.True);
                Assert.That(fill,Is.EqualTo(200f/510f).Within(1e-6f));
                Assert.That(runtime.TotalQuantity,Is.EqualTo(3200UL));
            }
        }

        [Test]
        public void RuntimeSurfaceUsesDensityAndTotalQuantityWithoutChangingVolumeMapping()
        {
            using(var fixture=new Fixture())
            {
                fixture.SetDensity(MaterialId.Water,1f);fixture.SetDensity(MaterialId.Poison,.9f);
                fixture.Initialize();
                Assert.That(fixture.Runtime.TryAddInitial(Vector3Int.zero,MaterialId.Water,400,false),Is.True);
                Assert.That(fixture.Runtime.TryAddInitial(Vector3Int.zero,MaterialId.Poison,40,false),Is.True);
                Assert.That(fixture.Runtime.TryGetSurfaceFill(Vector3Int.zero,MaterialId.Poison,out float fill),Is.True);
                Assert.That(fill,Is.EqualTo(440f/510f).Within(1e-6f));
                Assert.That(fixture.Runtime.TryGetSurfaceFill(Vector3Int.zero,MaterialId.Water,out _),Is.False);
                Assert.That(fixture.Runtime.TotalQuantity,Is.EqualTo(440UL));
                Assert.That(fixture.Runtime.TotalVolumeCubicMeters,Is.EqualTo(440*.125/510).Within(1e-9));
            }
        }

        [Test]
        public void ChangingResolutionAlonePreservesVolumeButUnitMultiplierChangesBasinQuantity()
        {
            var fine = new LiquidVolumeSettings(.25f, 1, 255, 1f);
            var coarse = new LiquidVolumeSettings(.25f, 2, 255, 1f);
            var cartoon = new LiquidVolumeSettings(.25f, 2, 255, 4f);
            Assert.That(coarse.CapacityGmu, Is.EqualTo(fine.CapacityGmu * 8));
            Assert.That(coarse.CubicMetersPerGmu, Is.EqualTo(fine.CubicMetersPerGmu).Within(1e-12));
            Assert.That(cartoon.CubicMetersPerGmu, Is.EqualTo(fine.CubicMetersPerGmu * 4).Within(1e-12));
            Assert.That(16d * cartoon.CapacityGmu * cartoon.CubicMetersPerGmu,
                Is.EqualTo(2d), "2×2×.5m 水槽的真实容量为2m³。");
        }

        [TestCase(-3, -2)]
        [TestCase(-2, -1)]
        [TestCase(-1, -1)]
        [TestCase(0, 0)]
        [TestCase(1, 0)]
        [TestCase(2, 1)]
        public void FineToCoarseUsesFloorDivisionAcrossWorldOrigin(int fine, int coarse)
        {
            var settings = new LiquidVolumeSettings(.25f, 2, 255, 4f);
            Assert.That(settings.WorldCellToLiquidCell(new Vector3Int(fine, fine, fine)),
                Is.EqualTo(new Vector3Int(coarse, coarse, coarse)));
        }

        [Test]
        public void InvalidVolumeConfigurationIsRejectedInsteadOfTruncatingCapacity()
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(() => new LiquidVolumeSettings(.25f,2,255,0f));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => new LiquidVolumeSettings(.25f,4,255,.001f));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => new LiquidVolumeSettings(float.NaN,2,255,4f));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => new LiquidVolumeSettings(float.MaxValue,4,255,4f));
        }

        [TestCase(.25f,1020UL)]
        [TestCase(.5f,2040UL)]
        public void InitialWorldBoundsKeepsActualVolumeWhenCoarseCellIsOnlyPartlyFilled(float height,ulong quantity)
        {
            using(var fixture=new Fixture())
            {
                fixture.Box("Floor",new Vector3(.5f,-.25f,.5f),new Vector3(3,.5f,3));
                fixture.Initialize();
                var initial=fixture.Host.AddComponent<GridLiquidInitialVolume>();
                typeof(GridLiquidInitialVolume).GetField("_liquids",Fields).SetValue(initial,fixture.Runtime);
                typeof(GridLiquidInitialVolume).GetField("_worldBounds",Fields).SetValue(initial,
                    new Bounds(new Vector3(.5f,height*.5f,.5f),new Vector3(1,height,1)));
                // EditMode 显式调用 Unity Start，检查真正的 Authoring 入口，不只检查换算公式。
                typeof(GridLiquidInitialVolume).GetMethod("Start",Fields).Invoke(initial,null);
                Assert.That(fixture.Runtime.TotalQuantity,Is.EqualTo(quantity));
                Assert.That(fixture.Runtime.TotalVolumeCubicMeters,Is.EqualTo(height).Within(1e-12));
            }
        }

        [TestCase(MaterialId.Water)]
        [TestCase(MaterialId.Poison)]
        [TestCase(MaterialId.Sticky)]
        public void CoarseColliderBasinKeepsQuantityAndWorldQueryDistinguishesWetBottomFromDryTop(MaterialId material)
        {
            using (var fixture = new Fixture())
            {
                fixture.Box("Floor",new Vector3(1,-.25f,1),new Vector3(4,.5f,4));
                fixture.Box("Left",new Vector3(-.25f,.25f,1),new Vector3(.5f,.5f,3));
                fixture.Box("Right",new Vector3(2.25f,.25f,1),new Vector3(.5f,.5f,3));
                fixture.Box("Back",new Vector3(1,.25f,-.25f),new Vector3(2,.5f,.5f));
                fixture.Box("Front",new Vector3(1,.25f,2.25f),new Vector3(2,.5f,.5f));
                fixture.Initialize();
                var runtime = fixture.Runtime;
                Assert.That(runtime.TryRegisterStableRegion(material,Vector3Int.zero,
                    new Vector3Int(4,1,4),4,0,4080,out int id),Is.True);
                var query = new MaterialStateQueryRouter(new GridRoute(),null,null,runtime);
                Assert.That(query.TryGetAmount(new Vector3Int(1,0,1),material,out byte bottom),Is.True);
                Assert.That(bottom,Is.EqualTo(255));
                Assert.That(query.TryGetAmount(new Vector3Int(1,1,1),material,out _),Is.False);
                Assert.That(runtime.TotalVolumeCubicMeters,Is.EqualTo(1d).Within(.001d));
                Assert.That(runtime.TryAddInitial(Vector3Int.zero,material,4080,false),Is.True);
                Assert.That(runtime.TotalQuantity,Is.EqualTo(8160UL));
                Assert.That(runtime.TotalVolumeCubicMeters,Is.EqualTo(2d).Within(.001d));
                Assert.That(query.TryGetAmount(new Vector3Int(7,1,7),material,out byte top),Is.True);
                Assert.That(top,Is.EqualTo(255));
                Assert.That(runtime.TryAddInitial(new Vector3Int(4,0,0),material,1),Is.False,
                    "真实池壁的 Collider 应阻挡粗格水。");
                runtime.Step(.1f);
                long before=System.GC.GetAllocatedBytesForCurrentThread();
                for(int i=0;i<64;i++){runtime.Step(.1f);query.TryGetAmount(new Vector3Int(1,0,1),material,out _);}
                long allocated=System.GC.GetAllocatedBytesForCurrentThread()-before;
                Assert.That(allocated,Is.Zero,"暖机后的稳定水体与粗细接触查询不能产生GC。");
                Assert.That(runtime.TotalQuantity,Is.EqualTo(8160UL));
                Assert.That(id,Is.GreaterThanOrEqualTo(0));
            }
        }

        [Test]
        public void CohesiveSpreadUsesFewerOccupiedCellsWithoutLosingQuantity()
        {
            int legacy=SettledCount(false),cohesive=SettledCount(true);
            Assert.That(cohesive,Is.LessThan(legacy));
            Assert.That(cohesive,Is.LessThanOrEqualTo(5),"新干格不应只获得几GMU薄膜。");
        }

        private static int SettledCount(bool cohesive)
        {
            var store=new LiquidVolumeStore(4,16,510,new[]{MaterialId.Water},c=>c.y<0);
            var activity=new LiquidActivityScheduler(1024,store);
            var solver=new LiquidFluxSolver(store,activity,null,new[]{MaterialId.Water},
                new[]{192},new[]{48},new[]{16},.5f,9.81f,12f,1024,
                cohesive?new[]{204}:null);
            store.TryAdd(Vector3Int.zero,MaterialId.Water,510);activity.WakeNeighbors(Vector3Int.zero);
            for(int i=0;i<100;i++)solver.Step(1024);
            var samples=new LiquidMaterialCellSample[1024];
            int count=store.CopyOccupiedCells(MaterialId.Water,samples);
            Assert.That(store.TotalQuantity,Is.EqualTo(510UL));
            return count;
        }

        [Test]
        public void CoarseStickyIgnitionWritesFireIntoFineWorldCoordinates()
        {
            using(var fixture=new Fixture())
            {
                fixture.Initialize();
                var cell=new Vector3Int(-2,0,3);
                fixture.Runtime.TryAddInitial(cell,MaterialId.Sticky,510,false);
                Vector3Int fireCell=new Vector3Int(-4,0,7),added=default;
                var adapter=Adapter(fixture.Runtime,new FireAt(fireCell,255),
                    (in ElementCellAddRequest request)=>{added=request.GlobalCell;return true;},
                    (in WorldReactionDamageCommand request)=>{});
                adapter.Process(new[]{new LiquidCellFireSample(fireCell,255)},1,.1f);
                Assert.That(added,Is.EqualTo(new Vector3Int(-3,0,7)));
                Assert.That(fixture.Runtime.TotalQuantity,Is.LessThan(510UL));
                Assert.That(fixture.Runtime.IgnitedStickyQuantity,Is.GreaterThan(0UL));
            }
        }

        [Test]
        public void DistantFireWithoutLiquidContactSkipsWorldActivityQueries()
        {
            using(var fixture=new Fixture())
            {
                fixture.Initialize();var fire=new FireAt(Vector3Int.zero,255);
                var adapter=Adapter(fixture.Runtime,fire,
                    (in ElementCellAddRequest request)=>true,(in WorldReactionDamageCommand request)=>{});
                var sources=new LiquidCellFireSample[256];
                for(int i=0;i<sources.Length;i++)sources[i]=new LiquidCellFireSample(new Vector3Int(i,0,0),255);
                adapter.Process(sources,sources.Length,.1f);
                Assert.That(fire.Reads,Is.Zero,"没有液体的火区不应反复执行World活动区域查询。");
                Assert.That(fixture.Runtime.TotalQuantity,Is.Zero);
            }
        }

        [Test]
        public void CoarsePoisonExplosionCoalescesFineFireSamplesAtCorrectWorldPosition()
        {
            using(var fixture=new Fixture())
            {
                fixture.Initialize();
                var cell=new Vector3Int(-2,0,3);
                fixture.Runtime.TryAddInitial(cell,MaterialId.Poison,510,false);
                Vector3Int fireCell=new Vector3Int(-3,0,7);int bursts=0;Vector3 position=default;
                var adapter=Adapter(fixture.Runtime,new FireAt(fireCell,8),
                    (in ElementCellAddRequest request)=>false,
                    (in WorldReactionDamageCommand request)=>{bursts++;position=request.WorldPosition;});
                var fire=new[]{new LiquidCellFireSample(fireCell,8),new LiquidCellFireSample(fireCell,8)};
                adapter.Process(fire,2,.1f);adapter.Process(fire,2,.1f);adapter.Process(fire,2,.1f);
                Assert.That(bursts,Is.EqualTo(1));
                Assert.That(position,Is.EqualTo(new Vector3(-.75f,.25f,1.75f)));
            }
        }

        private static LiquidReactionAdapter Adapter(LiquidWorldRuntime runtime,IMaterialAmountReadOnly fire,
            GridApplyFireAdd add,GridApplyReactionDamage damage)
        {
            var materials=AssetDatabase.LoadAssetAtPath<MaterialCatalog>(
                "Assets/_Project/ScriptableObjects/Materials/MaterialCatalog_Default.asset");
            var bindings=AssetDatabase.LoadAssetAtPath<MaterialReactionBindingProfile>(
                "Assets/_Project/ScriptableObjects/Combat/Reactions/MaterialReactionBindings_Default.asset");
            var profile=AssetDatabase.LoadAssetAtPath<ElementReactionProfile>(
                "Assets/_Project/ScriptableObjects/Combat/Status/ElementReactionProfile_Default.asset");
            var tuning=profile.CreateSnapshot(1f,1f);
            return new LiquidReactionAdapter(runtime,fire,bindings.CreateSnapshot(materials.CreateSnapshot()),
                in tuning,16,(in ElementCellDeltaRequest request)=>false,add,damage);
        }

        private sealed class GridRoute:IMaterialSimulationRouteReadOnly
        {
            public bool TryResolve(MaterialId material,out MaterialSimulationBackendKind backend)
            {backend=MaterialSimulationBackendKind.GridLiquid;return true;}
        }
        private sealed class FireAt:IMaterialAmountReadOnly
        {
            private readonly Vector3Int _cell;private readonly byte _amount;
            internal int Reads;
            internal FireAt(Vector3Int cell,byte amount){_cell=cell;_amount=amount;}
            public bool TryGetAmount(Vector3Int cell,MaterialId material,out byte amount)
            {Reads++;amount=cell==_cell && material==MaterialId.Fire?_amount:(byte)0;return amount!=0;}
        }
        private sealed class Fixture:System.IDisposable
        {
            internal readonly GameObject Host=new GameObject("Coarse liquid regression");
            internal readonly LiquidWorldRuntime Runtime;
            private readonly LiquidWorldProfile _profile=ScriptableObject.CreateInstance<LiquidWorldProfile>();
            private readonly LiquidBehaviorProfile[] _materials=new LiquidBehaviorProfile[3];
            private readonly ElementFieldSolidBaker _baker;
            internal Fixture()
            {
                Runtime=Host.AddComponent<LiquidWorldRuntime>();_baker=Host.AddComponent<ElementFieldSolidBaker>();
                for(int i=0;i<3;i++)
                {
                    _materials[i]=ScriptableObject.CreateInstance<LiquidBehaviorProfile>();
                    typeof(LiquidBehaviorProfile).GetField("_material",Fields).SetValue(_materials[i],
                        i==0?MaterialId.Water:i==1?MaterialId.Poison:MaterialId.Sticky);
                }
                JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":16,\"_maximumActiveCells\":1024,"
                    +"\"_resolutionScale\":2,\"_unitVolumeMultiplier\":4}",_profile);
                typeof(LiquidWorldProfile).GetField("_materials",Fields).SetValue(_profile,_materials);
                typeof(LiquidWorldRuntime).GetField("_profile",Fields).SetValue(Runtime,_profile);
            }
            internal void Initialize()=>Assert.That(Runtime.TryInitialize(Vector3.zero,.25f,4,_baker),Is.True);
            internal void SetDensity(MaterialId material,float density)
            {
                foreach(var behavior in _materials)
                    if(behavior.Material==material)typeof(LiquidBehaviorProfile).GetField("_relativeDensity",Fields).SetValue(behavior,density);
            }
            internal void Box(string name,Vector3 position,Vector3 scale)
            {
                var box=GameObject.CreatePrimitive(PrimitiveType.Cube);box.name=name;
                box.transform.SetParent(Host.transform);box.transform.position=position;box.transform.localScale=scale;
            }
            public void Dispose()
            {Object.DestroyImmediate(Host);Object.DestroyImmediate(_profile);foreach(var item in _materials)Object.DestroyImmediate(item);}
        }

        [Test]
        public void CoarseLiquidCellRepresentsEightFineCellsWithQuarterBasinQuantity()
        {
            var host = new GameObject("Liquid volume mapping regression");
            var profile = ScriptableObject.CreateInstance<LiquidWorldProfile>();
            var behavior = ScriptableObject.CreateInstance<LiquidBehaviorProfile>();
            try
            {
                // 未实现的字段在旧版本中被 JsonUtility 忽略；验证的是实际初始化结果。
                JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":8,\"_maximumActiveCells\":128,"
                    + "\"_resolutionScale\":2,\"_unitVolumeMultiplier\":4}", profile);
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                typeof(LiquidWorldProfile).GetField("_materials", flags)
                    .SetValue(profile, new[] { behavior });
                var baker = host.AddComponent<ElementFieldSolidBaker>();
                var runtime = host.AddComponent<LiquidWorldRuntime>();
                typeof(LiquidWorldRuntime).GetField("_profile", flags).SetValue(runtime, profile);
                Assert.That(runtime.TryInitialize(Vector3.zero, .25f, 4, baker), Is.True);
                Assert.That(runtime.CellSize, Is.EqualTo(.5f));
                Assert.That(runtime.CellCapacityGmu, Is.EqualTo(510));
                Assert.That(runtime.TryAddInitial(Vector3Int.zero, MaterialId.Water, 510, false), Is.True);
                // .5m 立方格，旧表示需要八份255，新表示510：同体积所需量减少四倍。
                Assert.That(runtime.TotalQuantity, Is.EqualTo(8UL * 255UL / 4UL));
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(profile);
                Object.DestroyImmediate(behavior);
            }
        }
    }
}
