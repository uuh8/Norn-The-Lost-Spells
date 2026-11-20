using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Game.ElementField;
using Game.Materials;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object=UnityEngine.Object;

namespace Game.Rendering.Tests
{
    /// <summary>从正式Profile进入同一个Renderer/Frame，按同GMU测量实际冻结几何输入，而不是只锁参数值。</summary>
    public sealed class LiquidNaturalSurfaceTests
    {
        private const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
        private readonly List<Object> _owned=new List<Object>();
        private readonly Dictionary<GridLiquidFieldFrame,GeometryMetrics> _geometry=new Dictionary<GridLiquidFieldFrame,GeometryMetrics>();
        private readonly Vector3 _origin=new Vector3(100,100,100);
        private LiquidWorldProfile _worldProfile;
        private GameObject _floor;
        private readonly string _evidence=Path.Combine("TestResults/LiquidNaturalSurface",DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ")+"-"+Guid.NewGuid().ToString("N"));
        [Serializable]private sealed class Metrics
        {
            public string Case;public ulong Gmu;
            public float BeforeHeight,AfterHeight,BeforeWidth,AfterWidth,BeforeFootprint,AfterFootprint,StickyHeight;
            public double BeforeVolume,AfterVolume;
        }
        private void Save(Metrics metrics){Directory.CreateDirectory(_evidence);File.WriteAllText(Path.Combine(_evidence,metrics.Case+".json"),JsonUtility.ToJson(metrics,true));}

        [SetUp]
        public void SetUp()
        {
            _worldProfile=Object.Instantiate(AssetDatabase.LoadAssetAtPath<LiquidWorldProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/LiquidWorldProfile_P7_DemoRun_Grid.asset"));_owned.Add(_worldProfile);
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":16,\"_maximumActiveCells\":128,\"_enableAutoStableDetection\":false}",_worldProfile);
            _floor=GameObject.CreatePrimitive(PrimitiveType.Cube);_owned.Add(_floor);
            _floor.transform.position=_origin+new Vector3(.5f,-.25f,.5f);_floor.transform.localScale=new Vector3(8,.5f,8);
            Physics.SyncTransforms();
        }
        [TearDown]public void TearDown(){for(int i=_owned.Count-1;i>=0;i--)if(_owned[i]!=null)Object.DestroyImmediate(_owned[i]);_owned.Clear();_geometry.Clear();}

        private GridLiquidFieldFrame Create(MaterialId material,uint perCell,int width,bool legacy,out LiquidWorldRuntime source,bool airborne=false,bool legacyAirControl=false)
        {
            var host=new GameObject("Natural liquid surface witness");host.SetActive(false);host.hideFlags=HideFlags.DontSave;_owned.Add(host);
            source=host.AddComponent<LiquidWorldRuntime>();typeof(LiquidWorldRuntime).GetField("_profile",Hidden).SetValue(source,_worldProfile);
            Assert.That(source.TryInitialize(_origin,.25f,8,host.AddComponent<ElementFieldSolidBaker>()),Is.True);
            for(int z=0;z<width;z++)for(int x=0;x<width;x++)Assert.That(source.TryAddInitial(new Vector3Int(x,airborne?4:0,z),material,perCell,false),Is.True);
            source.Step(.1f); // wake=false保持同一量；清掉出生渐入，两个相位都代表完整已存在的水。
            var profile=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GridLiquidFieldRenderProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/GridLiquidFieldRenderProfile_Default.asset"));_owned.Add(profile);
            JsonUtility.FromJsonOverwrite("{\"_inputChunks\":16,\"_views\":32,\"_fineViews\":32,\"_budgetMiB\":32,\"_cullByCamera\":false}",profile);
            if(legacy)JsonUtility.FromJsonOverwrite("{\"_cartoonCrownStrength\":0.8,\"_cartoonVolumeScale\":1.8,\"_useMaterialSurfaceTension\":false}",profile);
            // 隔离本轮地面特性：旧空中曲线仍取.8，但保持同Volume1。
            // 完整旧Preset的1.8会额外放大体积，不能拿它证明本轮改坏了空中形状。
            if(legacyAirControl)JsonUtility.FromJsonOverwrite("{\"_cartoonCrownStrength\":0.8,\"_cartoonVolumeScale\":1,\"_useMaterialSurfaceTension\":false,\"_useGroundContactCaps\":false}",profile);
            var renderer=host.AddComponent<GridLiquidFieldSurfaceRenderer>();renderer.ConfigureForValidation(source,profile);
            typeof(GridLiquidFieldSurfaceRenderer).GetMethod("Start",Hidden).Invoke(renderer,null);
            typeof(GridLiquidFieldSurfaceRenderer).GetMethod("LateUpdate",Hidden).Invoke(renderer,null);
            Assert.That(renderer.IsReady,Is.True);Assert.That(renderer.Frame,Is.Not.Null);
            return renderer.Frame;
        }
        private sealed class GeometryMetrics
        {internal Vector3 Minimum=Vector3.one*float.PositiveInfinity,Maximum=Vector3.one*float.NegativeInfinity;internal double Volume;internal float ContactArea;internal int Triangles;internal float Height=>Maximum.y-Minimum.y;internal float Width=>Mathf.Max(Maximum.x-Minimum.x,Maximum.z-Minimum.z);}
        private float Height(GridLiquidFieldFrame frame)=>MeasureGeometry(frame).Height;
        private float Footprint(GridLiquidFieldFrame frame,int width)=>MeasureGeometry(frame).ContactArea;
        private GeometryMetrics MeasureGeometry(GridLiquidFieldFrame frame)
        {
            if(_geometry.TryGetValue(frame,out var cached))return cached;var result=new GeometryMetrics();
            // Field与实例是同一数量的互斥显示归属。旧的OffsetY/单独Sampler截面会漏掉cap，
            // 所以读取真实参考Mesh及实际共享模板变换，不能测到0后说“变薄成功”。
            var material=new Material(AssetDatabase.LoadAssetAtPath<GridLiquidFieldRenderProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/GridLiquidFieldRenderProfile_Default.asset").Shader);_owned.Add(material);
            using(var cpu=new GridLiquidFieldCpuSurface(frame,new GridLiquidFieldSampler(frame),material,frame.Owners.Length,0))
            {
                cpu.Begin();double until=Time.realtimeSinceStartupAsDouble+15;while(cpu.IsBuilding){Assert.That(Time.realtimeSinceStartupAsDouble,Is.LessThan(until));cpu.UpdateAndDraw();}
                var meshes=(Mesh[])typeof(GridLiquidFieldCpuSurface).GetField("_meshes",Hidden).GetValue(cpu);int count=(int)typeof(GridLiquidFieldCpuSurface).GetField("_visible",Hidden).GetValue(cpu);
                for(int mesh=0;mesh<count;mesh++){var vertices=meshes[mesh].vertices;var indices=meshes[mesh].triangles;for(int at=0;at<indices.Length;at+=3)AddTriangle(vertices[indices[at]],vertices[indices[at+1]],vertices[indices[at+2]],result);}
            }
            var drops=new GridLiquidSmallDrop[1024];int instances=frame.CopyFallingDrops(1,drops);
            for(int instance=0;instance<instances;instance++)for(int at=0;at<LiquidDropletTemplate.Indices.Length;at+=3)
            {var a=GridLiquidGroundContactShape.TransformPosition(LiquidDropletTemplate.Vertices[LiquidDropletTemplate.Indices[at]],drops[instance]);var b=GridLiquidGroundContactShape.TransformPosition(LiquidDropletTemplate.Vertices[LiquidDropletTemplate.Indices[at+1]],drops[instance]);var c=GridLiquidGroundContactShape.TransformPosition(LiquidDropletTemplate.Vertices[LiquidDropletTemplate.Indices[at+2]],drops[instance]);AddTriangle(a,b,c,result);}
            result.Volume=Math.Abs(result.Volume);Assert.That(result.Triangles,Is.GreaterThan(0));_geometry.Add(frame,result);return result;
        }
        private void AddTriangle(Vector3 a,Vector3 b,Vector3 c,GeometryMetrics result)
        {
            a-=_origin;b-=_origin;c-=_origin;var cross=Vector3.Cross(b-a,c-a);if(cross.sqrMagnitude<1e-14f)return;
            result.Triangles++;result.Volume+=Vector3.Dot(a,Vector3.Cross(b,c))/6d;result.Minimum=Vector3.Min(result.Minimum,Vector3.Min(a,Vector3.Min(b,c)));result.Maximum=Vector3.Max(result.Maximum,Vector3.Max(a,Vector3.Max(b,c)));
            // 平地几何底盘的实际投影面积。这里只用于下面平地case；坡面仍由独立法线测试验证。
            if(cross.y<0 && Mathf.Max(a.y,Mathf.Max(b.y,c.y))<.001f)result.ContactArea+=-cross.y*.5f;
        }

        private LiquidWorldRuntime CreateSupportSource(out ElementFieldSolidBaker baker,FixedSolidWorldRuntime logicalSolids=null)
        {
            var host=new GameObject("Liquid exact support witness");host.SetActive(false);_owned.Add(host);
            baker=host.AddComponent<ElementFieldSolidBaker>();
            if(logicalSolids!=null)baker.SetMaterialSolids(logicalSolids,_origin,.25f);
            var source=host.AddComponent<LiquidWorldRuntime>();typeof(LiquidWorldRuntime).GetField("_profile",Hidden).SetValue(source,_worldProfile);
            Assert.That(source.TryInitialize(_origin,.25f,8,baker),Is.True);
            return source;
        }

        [Test]
        public void CoarseEnvironmentOverlapBeyondSlopeEdgeDoesNotInventAHorizontalLiquidShelf()
        {
            _floor.transform.position=_origin+new Vector3(2,-.25f,2);_floor.transform.localScale=new Vector3(6,.5f,6);
            _floor.transform.rotation=Quaternion.Euler(0,0,-30);Physics.SyncTransforms();
            var source=CreateSupportSource(out _);var cell=new Vector3Int(9,-2,1);
            // 盒体最右边约x=4.723m；粗格中心x=4.75m已在坡外，但下方粗格仍与盒边相交。
            // 这不是材料支撑，也没有真实接触平面，不能把保守碰撞OR结果画成y=-1的水平板。
            Assert.That(source.IsSolidForSurface(cell+Vector3Int.down),Is.True);
            Assert.That(source.TryGetTerrainSupportForPresentation(cell,out _),Is.False);
            Assert.That(source.TryAddInitial(cell,MaterialId.Water,2,false),Is.True);
            var frame=new GridLiquidFieldFrame(source,16,64,.25f,1);frame.Capture();
            Assert.That(frame.Supported(cell,false),Is.False,"坡沿外只有粗格重叠，不应虚构接地液面。离坡数量仍由原场重建为自由液体。");
            Assert.That(frame.Read(cell,0,false).Quantity,Is.EqualTo(2));Assert.That(source.TotalQuantity,Is.EqualTo(2UL));
        }

        [Test]
        public void ARealSlopePlaneStillSupportsLiquidWithItsActualNormalAndHeight()
        {
            _floor.transform.position=_origin+new Vector3(2,-.25f,2);_floor.transform.localScale=new Vector3(6,.5f,6);
            _floor.transform.rotation=Quaternion.Euler(0,0,-30);Physics.SyncTransforms();
            var source=CreateSupportSource(out _);var cell=new Vector3Int(8,-2,1);
            Assert.That(source.TryGetTerrainSupportForPresentation(cell,out var support),Is.True);
            Assert.That(source.TryAddInitial(cell,MaterialId.Water,64,false),Is.True);
            var frame=new GridLiquidFieldFrame(source,16,64,.25f,1);frame.Capture();
            Assert.That(frame.Supported(cell,false),Is.True);
            Assert.That(Vector3.Dot((Vector3)frame.Terrain(cell),_floor.transform.up),Is.GreaterThan(.999f));
            Assert.That(frame.Terrain(cell).w,Is.EqualTo(support.Height).Within(.00001f));Assert.That(source.TotalQuantity,Is.EqualTo(64UL));
        }

        [Test]
        public void LogicalIceStillSupportsLiquidBeforeItsColliderHasBeenPublished()
        {
            _floor.SetActive(false);Physics.SyncTransforms();
            var host=new GameObject("Logical ice without generated collider");host.SetActive(false);_owned.Add(host);
            var profile=ScriptableObject.CreateInstance<FixedSolidWorldProfile>();_owned.Add(profile);
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":4,\"_maximumNavigationBoxesPerChunk\":4}",profile);
            var solids=host.AddComponent<FixedSolidWorldRuntime>();typeof(FixedSolidWorldRuntime).GetField("_profile",Hidden).SetValue(solids,profile);
            Assert.That(solids.TryInitialize(null,null,_origin,.25f,4),Is.True);
            var store=(FixedSolidVolumeStore)typeof(FixedSolidWorldRuntime).GetField("_store",Hidden).GetValue(solids);
            // 只发布真实材料数量，不调用PhysicsAdapter.Publish；模拟几何更新晚于数量的合法窗口。
            for(int z=0;z<2;z++)for(int y=-2;y<0;y++)for(int x=0;x<2;x++)
                Assert.That(store.TryAdd(new Vector3Int(x,y,z),MaterialId.Ice,solids.CellCapacity),Is.True);
            var source=CreateSupportSource(out _,solids);var cell=Vector3Int.zero;
            Assert.That(source.IsSolidForSurface(cell+Vector3Int.down),Is.True);
            Assert.That(source.TryGetTerrainSupportForPresentation(cell,out _),Is.False,"本控制必须覆盖尚无Collider的真实数量支撑。");
            Assert.That(source.TryAddInitial(cell,MaterialId.Water,64,false),Is.True);
            var frame=new GridLiquidFieldFrame(source,16,64,.25f,1);frame.Capture();
            Assert.That(frame.Supported(cell,false),Is.True,"禁止为了消除坡沿假地板而删除真实逻辑材料的接地支持。");
            Assert.That(frame.Read(cell,0,false).Quantity,Is.EqualTo(64));Assert.That(source.TotalQuantity,Is.EqualTo(64UL));
        }

        [Test]
        public void NaturalGroundSettingsKeepAnAirborneBodyCohesiveInsteadOfFlatteningItIntoASheet()
        {
            var air=Create(MaterialId.Water,64,1,false,out var source,airborne:true);
            var oldAir=Create(MaterialId.Water,64,1,false,out var oldSource,airborne:true,legacyAirControl:true);
            var ground=Create(MaterialId.Water,64,1,false,out _);
            var currentGeometry=MeasureGeometry(air);var oldGeometry=MeasureGeometry(oldAir);
            Save(new Metrics{Case="air-ground-feature-isolation",Gmu=source.TotalQuantity,BeforeHeight=oldGeometry.Height,AfterHeight=currentGeometry.Height,BeforeWidth=oldGeometry.Width,AfterWidth=currentGeometry.Width,BeforeVolume=oldGeometry.Volume,AfterVolume=currentGeometry.Volume});
            Assert.That(air.ShapeScale(new Vector3Int(0,4,0),false),Is.LessThan(.65f));
            Assert.That(oldSource.TotalQuantity,Is.EqualTo(source.TotalQuantity));
            Assert.That(currentGeometry.Height,Is.EqualTo(oldGeometry.Height).Within(.00001f),"接地球冠不应改写无支撑空中液团的同体积高度。");
            Assert.That(currentGeometry.Width,Is.EqualTo(oldGeometry.Width).Within(.00001f));
            Assert.That(currentGeometry.Volume/oldGeometry.Volume,Is.EqualTo(1).Within(.0001));
            Assert.That(currentGeometry.Height/currentGeometry.Width,Is.InRange(.4f,1.6f),"空中液团保留有界厚度；Field离散圆肩并非完美球体，不能臆定其H/W必须>.6。");
            Assert.That(currentGeometry.Height/currentGeometry.Width,Is.GreaterThan(Height(ground)/MeasureGeometry(ground).Width*1.8f));
            Assert.That(Height(ground)/MeasureGeometry(ground).Width,Is.InRange(.12f,.35f));
            Assert.That(source.TotalQuantity,Is.EqualTo(64UL));
        }
        [Test]
        public void SameWaterGmuProducesLowerSurfaceAndWiderFootprintThanTheLegacyJellyPreset()
        {
            var before=Create(MaterialId.Water,64,2,true,out var sourceBefore);
            var after=Create(MaterialId.Water,64,2,false,out var sourceAfter);
            Save(new Metrics{Case="same-gmu-water",Gmu=sourceAfter.TotalQuantity,BeforeHeight=Height(before),AfterHeight=Height(after),BeforeFootprint=Footprint(before,2),AfterFootprint=Footprint(after,2)});
            Assert.That(sourceAfter.TotalQuantity,Is.EqualTo(sourceBefore.TotalQuantity).And.EqualTo(256UL));
            Assert.That(Height(after),Is.InRange(.12f,.28f),"同量小滩应有适当厚度和圆肩，不能是纸片或旧高胶块。");
            Assert.That(Height(after),Is.LessThan(Height(before)*.65f));
            Assert.That(Footprint(after,2),Is.GreaterThan(Footprint(before,2)*1.3f));
            Assert.That(MeasureGeometry(after).Volume/(256*Math.Pow(.5,3)/510),Is.InRange(.85,1.15));
        }
        [Test]
        public void FourTimesWaterQuantityRaisesDepthInsteadOfKeepingEveryAmountAsOneTallBlob()
        {
            var small=Create(MaterialId.Water,64,1,false,out _);var large=Create(MaterialId.Water,256,1,false,out _);
            Save(new Metrics{Case="quantity-depth",Gmu=256,BeforeHeight=Height(small),AfterHeight=Height(large)});
            float dimensionalRatio=Mathf.Pow(4,1f/3f);
            Assert.That(Height(large)/Height(small),Is.EqualTo(dimensionalRatio).Within(.1f),"相同接触角下体积增加4倍，H/W按立方根增长，不是固定横面积的薄片线性加高。");
            Assert.That(MeasureGeometry(large).Width/MeasureGeometry(small).Width,Is.EqualTo(dimensionalRatio).Within(.1f));
            Assert.That(MeasureGeometry(large).Volume/MeasureGeometry(small).Volume,Is.EqualTo(4).Within(.05));
            Assert.That(Height(large),Is.LessThan(.35f));
        }
        [Test]
        public void EqualTotalShallowWaterRetainsAConsistentContactShapeAcrossCellPartitions()
        {
            var one=Create(MaterialId.Water,256,1,false,out var sourceOne);var spread=Create(MaterialId.Water,64,2,false,out var sourceSpread);
            Save(new Metrics{Case="one-vs-four-cells",Gmu=sourceOne.TotalQuantity,BeforeHeight=Height(one),AfterHeight=Height(spread),BeforeFootprint=Footprint(one,1),AfterFootprint=Footprint(spread,2)});
            Assert.That(sourceOne.TotalQuantity,Is.EqualTo(sourceSpread.TotalQuantity));
            Assert.That(Height(spread),Is.EqualTo(Height(one)).Within(.005f));
            Assert.That(MeasureGeometry(spread).Width,Is.EqualTo(MeasureGeometry(one).Width).Within(.01f));
            Assert.That(MeasureGeometry(spread).Volume/MeasureGeometry(one).Volume,Is.InRange(.98,1.02),"同量、同接触角的小连通体不应因存储分成4格就凭空变成宽纸片。");
        }
        [Test]
        public void OneGmuRetainsNaturalThicknessAndStickyRetainsGreaterContactCohesion()
        {
            var trace=Create(MaterialId.Water,1,1,false,out var traceSource);
            Assert.That(Height(trace),Is.InRange(.015f,.06f));
            Assert.That(Footprint(trace,1),Is.InRange(.008f,.04f));Assert.That(traceSource.TotalQuantity,Is.EqualTo(1UL));
            Assert.That(MeasureGeometry(trace).Volume/(Math.Pow(.5,3)/510),Is.InRange(.85,1.15));
            var water=Create(MaterialId.Water,64,1,false,out _);var sticky=Create(MaterialId.Sticky,64,1,false,out _);
            Save(new Metrics{Case="trace-and-cohesion",Gmu=1,BeforeHeight=Height(trace),AfterHeight=Height(water),AfterFootprint=Footprint(trace,1),StickyHeight=Height(sticky)});
            Assert.That(Height(sticky),Is.GreaterThan(Height(water)*1.15f),"黏液继续比同量水更聚团，不能统一把全部材料压平。");
            Assert.That(MeasureGeometry(sticky).Width,Is.LessThan(MeasureGeometry(water).Width*.95f));
        }
    }
}
