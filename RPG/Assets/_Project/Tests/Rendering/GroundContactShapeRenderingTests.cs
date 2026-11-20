using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Core;
using Game.ElementField;
using Game.Materials;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object=UnityEngine.Object;

namespace Game.Rendering.Tests
{
    /// <summary>
    /// 几何需求RED：测实际CPU外壳Mesh或现有实例的共享模板变换，既不锁死minScale，
    /// 也不强迫实现必须是实例。体积由真实Store GMU独立换算，不接受拉高Alpha/VolumeScale。
    /// 模板Reflection分支仅在真实Draw已有实例时执行，旧实现会先量到纸片并在形状断言失败。
    /// </summary>
    public sealed class GroundContactShapeRenderingTests
    {
        private const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        private readonly List<Object> _owned=new List<Object>();
        private readonly Vector3 _origin=new Vector3(100,30,100);
        [TearDown]public void Cleanup(){for(int i=_owned.Count-1;i>=0;i--)if(_owned[i]!=null)Object.DestroyImmediate(_owned[i]);_owned.Clear();}
        private sealed class Fixture
        {internal WorldReplicaLiquidReadModel Source;internal GridLiquidFieldFrame Frame;internal uint PerCell;internal int Side;}
        private sealed class Measure
        {internal Vector3 Minimum=Vector3.one*float.PositiveInfinity,Maximum=Vector3.one*float.NegativeInfinity;internal double Volume;internal int Triangles;internal float Height=>Maximum.y-Minimum.y;internal float Width=>Mathf.Max(Maximum.x-Minimum.x,Maximum.z-Minimum.z);}

        [Test]public void OneGmuHasARoundedCompactContactShapeAndItsActualMeshVolumeMatchesQuantity()
        {
            var f=Create(1,1,Vector2.zero);var shape=Geometry(f);
            Assert.That(f.Source.TotalQuantity,Is.EqualTo(1UL));
            Assert.That(shape.Height,Is.GreaterThan(.01f),"1GMU不能只是1mm高、70cm宽的纸片。");
            Assert.That(shape.Width,Is.LessThan(.25f));Assert.That(shape.Height/shape.Width,Is.InRange(.12f,.5f));
            Assert.That(shape.Volume/ExpectedVolume(1),Is.InRange(.85,1.15),"实际闭合几何体积应来自同一GMU，不能用1.8倍率假装有厚度。");
        }
        [Test]public void EightTimesSmallQuantityDoublesContactDimensionsInsteadOfOnlyMakingTheSheetEightTimesThicker()
        {
            var a=Geometry(Create(1,1,Vector2.zero));var b=Geometry(Create(8,1,Vector2.zero));
            Assert.That(b.Height/a.Height,Is.InRange(1.8f,2.2f));Assert.That(b.Width/a.Width,Is.InRange(1.8f,2.2f));
            Assert.That(b.Volume/a.Volume,Is.InRange(7.5,8.5));
        }
        [Test]public void FourConnectedShallowCellsFormOneRoundedPuddleWithoutChangingTheirRealInventory()
        {
            var f=Create(64,2,Vector2.zero);var shape=Geometry(f);
            Assert.That(f.Source.TotalQuantity,Is.EqualTo(256UL));
            for(int x=2;x<4;x++)for(int z=2;z<4;z++){Assert.That(f.Source.TryGetQuantity(new Vector3Int(x,0,z),MaterialId.Water,out uint q),Is.True);Assert.That(q,Is.EqualTo(64u));}
            Assert.That(shape.Height/shape.Width,Is.InRange(.13f,.45f),"小滩要有自然圆肩，不能仍是方枕/薄片。");
            Assert.That(shape.Height,Is.LessThan(.35f));Assert.That(shape.Volume/ExpectedVolume(256),Is.InRange(.85,1.15));
        }
        [Test]public void AFullDeepPoolRetainsItsWaterLevelAndQuantity()
        {
            var f=Create(510,2,Vector2.zero);var shape=Geometry(f);
            Assert.That(f.Source.TotalQuantity,Is.EqualTo(2040UL));Assert.That(shape.Height,Is.InRange(.42f,.58f));
            Assert.That(shape.Width,Is.GreaterThan(1));Assert.That(shape.Triangles,Is.GreaterThan(0));
        }
        [Test]public void MicroContactGeometryDoesNotDisappearOrChangeVolumeAtArbitrarySubcellPhases()
        {
            Measure first=null;
            foreach(var offset in new[]{new Vector2(0,0),new Vector2(.125f,.25f),new Vector2(.25f,.375f),new Vector2(.375f,.125f),new Vector2(-.25f,.375f)})
            {
                var f=Create(1,1,offset);var shape=Geometry(f);Assert.That(shape.Triangles,Is.GreaterThan(0));
                Assert.That(shape.Height,Is.GreaterThan(.01f));Assert.That(shape.Width,Is.LessThan(.25f));
                Assert.That(shape.Volume/ExpectedVolume(1),Is.InRange(.85,1.15));
                if(first==null)first=shape;
                else {Assert.That(shape.Height,Is.EqualTo(first.Height).Within(.001f));Assert.That(shape.Width,Is.EqualTo(first.Width).Within(.001f));Assert.That(shape.Volume/first.Volume,Is.InRange(.98,1.02));}
            }
        }
        [Test]public void OneReservedInstanceSlotFallsBackToTheFieldWithoutLosingOrDuplicatingPhaseQuantity()
        {
            var f=Create(1,2,Vector2.zero,fallingCapacity:1);
            foreach(float alpha in new[]{0f,.25f,.5f,.75f,1f})
            {
                double mass=FieldMass(f.Frame,true)*(1-alpha)+FieldMass(f.Frame,false)*alpha+InstanceMass(f.Frame,alpha,out int count);
                Assert.That(count,Is.LessThanOrEqualTo(1));Assert.That(mass,Is.EqualTo(4).Within(.01));
                Assert.That(f.Source.TotalQuantity,Is.EqualTo(4UL));
            }
        }
        [Test]public void ContactQuantityChangesLinearlyAcrossPhasesWhileCurrentInventoryStaysAuthoritative()
        {
            var f=Create(8,1,Vector2.zero,previousQuantity:1);
            foreach(float alpha in new[]{0f,.25f,.5f,.75f,1f})
            {
                double mass=InstanceMass(f.Frame,alpha,out int count);
                Assert.That(count,Is.EqualTo(1));Assert.That(mass,Is.EqualTo(1+7*alpha).Within(.01));
                Assert.That(f.Source.TotalQuantity,Is.EqualTo(8UL));
            }
        }
        [Test]public void OneGmuContactWithRealReplicaDisplacementAndVelocityMovesAcrossFivePhasesWithoutChangingItsShapeOrVolume()
        {
            // 不是在Capture后只改Field buffer：真实Motion合同先给两端合法Offset/Velocity，
            // Replica见证通过后由Frame冻结为实例。0.2cell=.1m，与1m/s×100ms一致；
            // 相同端点恒速的一阶Warp会抵消，不把那种输入误断言成必有质心平移。
            var f=Create(1,1,new Vector2(.2f,0),previousOffset:Vector2.zero,velocity:Vector3.right);
            Assert.That(f.Frame.Interval,Is.EqualTo(.1f).Within(.000001f));
            Assert.That(FieldMass(f.Frame,true),Is.Zero);Assert.That(FieldMass(f.Frame,false),Is.Zero);
            var drops=new GridLiquidSmallDrop[2];Measure first=null;Vector3 start=default;
            foreach(float alpha in new[]{0f,.25f,.5f,.75f,1f})
            {
                Assert.That(f.Frame.CopyFallingDrops(alpha,drops),Is.EqualTo(1));var drop=drops[0];
                Assert.That(GridLiquidPackedCell.High(drop.Fourth),Is.LessThan(0),"合法运动不能使接地球冠误解码成空中stretch。");
                Assert.That(GridLiquidPackedCell.Low(drop.Third),Is.EqualTo(1));
                if(first==null)start=drop.Position;
                Assert.That(drop.Position.x,Is.EqualTo(start.x+.1f*alpha).Within(.00003f));
                Assert.That(drop.Position.y,Is.EqualTo(_origin.y).Within(.00001f));
                Assert.That(drop.Position.z,Is.EqualTo(start.z).Within(.00001f));
                // CPU fallback与GPU真实绘制共用这个32B实例和同一模板；Shader同式/真实像素
                // 另由ContactMicrodropShaderPixels...实际PlayMode覆盖，不能称本断言运行了GPU。
                var shape=new Measure();for(int at=0;at<LiquidDropletTemplate.Indices.Length;at+=3)
                {var a=GridLiquidGroundContactShape.TransformPosition(LiquidDropletTemplate.Vertices[LiquidDropletTemplate.Indices[at]],drop);var b=GridLiquidGroundContactShape.TransformPosition(LiquidDropletTemplate.Vertices[LiquidDropletTemplate.Indices[at+1]],drop);var c=GridLiquidGroundContactShape.TransformPosition(LiquidDropletTemplate.Vertices[LiquidDropletTemplate.Indices[at+2]],drop);Triangle(a,b,c,shape);}
                shape.Volume=Math.Abs(shape.Volume);Assert.That(shape.Triangles,Is.GreaterThan(0));
                Assert.That(shape.Volume/ExpectedVolume(1),Is.InRange(.98,1.02));
                if(first==null)first=shape;else{Assert.That(shape.Height,Is.EqualTo(first.Height).Within(.00003f));Assert.That(shape.Width,Is.EqualTo(first.Width).Within(.00003f));}
                Assert.That(f.Source.TotalQuantity,Is.EqualTo(1UL));
            }
        }
        [Test]public void AnUnavailableInstanceBudgetRetainsTheCompleteContinuousField()
        {
            var f=Create(1,2,Vector2.zero,fallingCapacity:0);
            Assert.That(FieldMass(f.Frame,true),Is.EqualTo(4));Assert.That(FieldMass(f.Frame,false),Is.EqualTo(4));
            Assert.That(InstanceMass(f.Frame,1,out int count),Is.Zero);Assert.That(count,Is.Zero);
            Assert.That(f.Source.TotalQuantity,Is.EqualTo(4UL));
        }
        [Test]public void KeepingBothGpuInputPagesRetainsBothLowGroundAndHighAirInstanceRecords()
        {
            var f=Create(1,1,Vector2.zero,extraAir:true);var before=new GridLiquidSmallDrop[1024];int original=f.Frame.CopyFallingDrops(1,before);
            Assert.That(original,Is.EqualTo(2));Assert.That(f.Source.TotalQuantity,Is.EqualTo(2UL));
            // 两个源页均保留，压紧不应改变任何实例姿态/半径/接触角。
            f.Frame.SelectGpuDomain(f.Frame.Count,f.Frame.OwnerCount);var after=new GridLiquidSmallDrop[1024];int remaining=f.Frame.CopyFallingDrops(1,after);
            Assert.That(remaining,Is.EqualTo(original),"低page contact不能被高page air开头的非升序列表跳过。");
            Array.Sort(before,0,original,Comparer<GridLiquidSmallDrop>.Create((a,b)=>a.Position.y.CompareTo(b.Position.y)));
            Array.Sort(after,0,remaining,Comparer<GridLiquidSmallDrop>.Create((a,b)=>a.Position.y.CompareTo(b.Position.y)));
            for(int i=0;i<remaining;i++){Assert.That(after[i].Position,Is.EqualTo(before[i].Position));Assert.That(after[i].Radius,Is.EqualTo(before[i].Radius));Assert.That(after[i].Fourth,Is.EqualTo(before[i].Fourth));}
            Assert.That(f.Source.TotalQuantity,Is.EqualTo(2UL));
        }
        [Test]public void AContactCapCannotExtendAcrossAnActualSideWallAndFallbackRetainsFieldQuantity()
        {
            var f=Create(356,1,Vector2.zero,sideWall:true);var drops=new GridLiquidSmallDrop[1024];int count=f.Frame.CopyFallingDrops(1,drops);
            if(count>0)
            {
                var geometry=Geometry(f);
                Assert.That(geometry.Maximum.x,Is.LessThanOrEqualTo(1.5005f),"球冠不能越过实际墙左面；无净空须保原Field，不能让新实例穿墙。");
            }
            Assert.That(count,Is.Zero,"本356GMU球冠足迹与墙相交，应拒绝cap准入。");
            Assert.That(FieldMass(f.Frame,false),Is.EqualTo(356));Assert.That(f.Source.TotalQuantity,Is.EqualTo(356UL));
        }
        private static double FieldMass(GridLiquidFieldFrame frame,bool previous)
        {
            double total=0;int cells=frame.ChunkEdge*frame.ChunkEdge*frame.ChunkEdge;
            for(int page=0;page<frame.Count;page++)for(int local=0;local<cells;local++)
            {var key=frame.Keys[page];var cell=new Vector3Int(key.X*frame.ChunkEdge+local%frame.ChunkEdge,key.Y*frame.ChunkEdge+local/frame.ChunkEdge%frame.ChunkEdge,key.Z*frame.ChunkEdge+local/(frame.ChunkEdge*frame.ChunkEdge));
                if(frame.FallingDropRow(cell,previous))continue;for(int material=0;material<frame.MaterialCount;material++)total+=frame.Read(cell,material,previous).Quantity;}
            return total;
        }
        private double InstanceMass(GridLiquidFieldFrame frame,float alpha,out int count)
        {
            var drops=new GridLiquidSmallDrop[1024];count=frame.CopyFallingDrops(alpha,drops);if(count==0)return 0;
            var transform=Type.GetType("Game.Rendering.GridLiquidGroundContactShape, Game.Rendering",true).GetMethod("TransformPosition",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic);
            var result=new Measure();for(int drop=0;drop<count;drop++)for(int at=0;at<LiquidDropletTemplate.Indices.Length;at+=3)
            {var a=(Vector3)transform.Invoke(null,new object[]{LiquidDropletTemplate.Vertices[LiquidDropletTemplate.Indices[at]],drops[drop]});var b=(Vector3)transform.Invoke(null,new object[]{LiquidDropletTemplate.Vertices[LiquidDropletTemplate.Indices[at+1]],drops[drop]});var c=(Vector3)transform.Invoke(null,new object[]{LiquidDropletTemplate.Vertices[LiquidDropletTemplate.Indices[at+2]],drops[drop]});Triangle(a,b,c,result);}
            return Math.Abs(result.Volume)/ExpectedVolume(1);
        }
        private double ExpectedVolume(uint quantity)=>quantity*Math.Pow(.5,3)/510;
        private Fixture Create(uint perCell,int side,Vector2 offset,int fallingCapacity=1024,uint? previousQuantity=null,bool extraAir=false,bool sideWall=false,Vector2? previousOffset=null,Vector3 velocity=default)
        {
            var floor=Own(GameObject.CreatePrimitive(PrimitiveType.Cube));floor.transform.position=_origin+new Vector3(1.5f,-.25f,1.5f);floor.transform.localScale=new Vector3(8,.5f,8);
            if(sideWall){var wall=Own(GameObject.CreatePrimitive(PrimitiveType.Cube));wall.transform.position=_origin+new Vector3(1.55f,.5f,1.25f);wall.transform.localScale=new Vector3(.1f,1,3);}
            Physics.SyncTransforms();
            var host=Own(new GameObject("Ground contact geometry witness"));host.SetActive(false);
            var profile=Own(Object.Instantiate(AssetDatabase.LoadAssetAtPath<LiquidWorldProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/LiquidWorldProfile_P7_DemoRun_Grid.asset")));
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":16,\"_maximumActiveCells\":256,\"_enableAutoStableDetection\":false}",profile);
            var baker=host.AddComponent<ElementFieldSolidBaker>();var empty=host.AddComponent<LiquidWorldRuntime>();typeof(LiquidWorldRuntime).GetField("_profile",Hidden).SetValue(empty,profile);
            Assert.That(empty.TryInitialize(_origin,.25f,4,baker),Is.True);
            var geometry=new WorldGridGeometry(WorldMaterialDomain.Liquid,_origin,.5f,4);var store=new WorldReplicaStore(7,16,256,0,2048);
            Assert.That(store.ConfigureGeometry(geometry),Is.True);Assert.That(store.ConfigureMaterials(WorldMaterialDomain.Liquid,510,new[]{(byte)MaterialId.Water}),Is.True);
            int count=side*side;var words=new uint[11+count*WorldChunkPayloadCodec.CellWords];words[0]=WorldChunkPayloadCodec.Magic;words[1]=(uint)count;words[3]=1;WorldChunkPayloadCodec.WriteGeometry(words,4,geometry);
            var frame=new WorldLiquidMotionFrame{WorldEpoch=7,ConnectionEpoch=3,Sequence=1,MaterialTick=1,SampleTime=10,Interval=.1f,ChunkSize=4,AnchorCount=extraAir?2:1,RowCount=count+(extraAir?1:0)};
            var key=new WorldChunkKey(WorldMaterialDomain.Liquid,0,0,0);frame.Anchors[0]=new WorldLiquidMotionAnchor(key,1,1,1,true);
            int at=0;for(int x=2;x<2+side;x++)for(int z=2;z<2+side;z++)
            {var cell=new Vector3Int(x,0,z);WorldChunkPayloadCodec.WriteCell(words,11+at*WorldChunkPayloadCodec.CellWords,new WorldMaterialCellSample(WorldMaterialDomain.Liquid,WorldMaterialStorage.Resident,cell,(byte)MaterialId.Water,perCell));
                var pose=new Vector3(offset.x,0,offset.y);var old=previousOffset??offset;var oldPose=new Vector3(old.x,0,old.y);frame.Rows[at++]=new WorldLiquidMotionSample(cell,(byte)MaterialId.Water,previousQuantity??perCell,perCell,oldPose,velocity,pose,velocity,0);}
            Assert.That(WorldChunkPayloadCodec.Validate(words,words.Length,key),Is.True,"Payload夹具必须先遵守网络规范化Resident数量合同。");
            Assert.That(store.TryInstall(new WorldChunkPayloadHeader(7,1,key,1,0,1,1,words.Length,WorldChunkPayloadCodec.ComputeChecksum(words,words.Length),true),words,words.Length),Is.True);
            if(extraAir)
            {
                var airCell=new Vector3Int(2,8,2);var airKey=new WorldChunkKey(WorldMaterialDomain.Liquid,0,2,0);var airWords=new uint[11+WorldChunkPayloadCodec.CellWords];airWords[0]=WorldChunkPayloadCodec.Magic;airWords[1]=1;airWords[3]=1;WorldChunkPayloadCodec.WriteGeometry(airWords,4,geometry);
                WorldChunkPayloadCodec.WriteCell(airWords,11,new WorldMaterialCellSample(WorldMaterialDomain.Liquid,WorldMaterialStorage.Resident,airCell,(byte)MaterialId.Water,1));
                Assert.That(store.TryInstall(new WorldChunkPayloadHeader(7,2,airKey,1,0,1,1,airWords.Length,WorldChunkPayloadCodec.ComputeChecksum(airWords,airWords.Length),true),airWords,airWords.Length),Is.True);
                frame.Anchors[1]=new WorldLiquidMotionAnchor(airKey,1,1,1,true);frame.Rows[count]=new WorldLiquidMotionSample(airCell,(byte)MaterialId.Water,1,1,Vector3.zero,Vector3.zero,Vector3.zero,Vector3.zero,1);
            }
            var source=new WorldReplicaLiquidReadModel(store,geometry,profile,.25f,baker);source.ResetConnection(3);Assert.That(source.TryPublish(frame,10.01),Is.True);
            var render=Own(Object.Instantiate(AssetDatabase.LoadAssetAtPath<GridLiquidFieldRenderProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/GridLiquidFieldRenderProfile_Default.asset")));
            JsonUtility.FromJsonOverwrite("{\"_useMaterialSurfaceTension\":true,\"_waterSurfaceTension\":0.3,\"_cartoonCrownStrength\":0.25,\"_cartoonVolumeScale\":1,\"_useGroundContactCaps\":true}",render);
            var shaped=new GridLiquidFieldFrame(source,16,64,.25f,1);if(fallingCapacity>0)shaped.ConfigureFallingDrops(fallingCapacity);shaped.ConfigureMaterialSurfaceTension(render);shaped.Capture();
            return new Fixture{Source=source,Frame=shaped,PerCell=perCell,Side=side};
        }
        private Measure Geometry(Fixture f)
        {
            var result=new Measure();var drops=new GridLiquidSmallDrop[1024];int count=f.Frame.CopyFallingDrops(1,drops);
            if(count>0)
            {
                var shapeType=Type.GetType("Game.Rendering.GridLiquidGroundContactShape, Game.Rendering",false);
                Assert.That(shapeType,Is.Not.Null,"存在接地实例时必须读取实际共享模板变换，不能用理想球体代替真实绘制几何。");
                var transform=shapeType.GetMethod("TransformPosition",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic);
                foreach(var drop in drops.AsSpan(0,count))for(int triangle=0;triangle<LiquidDropletTemplate.Indices.Length;triangle+=3)
                {
                    var a=(Vector3)transform.Invoke(null,new object[]{LiquidDropletTemplate.Vertices[LiquidDropletTemplate.Indices[triangle]],drop});
                    var b=(Vector3)transform.Invoke(null,new object[]{LiquidDropletTemplate.Vertices[LiquidDropletTemplate.Indices[triangle+1]],drop});
                    var c=(Vector3)transform.Invoke(null,new object[]{LiquidDropletTemplate.Vertices[LiquidDropletTemplate.Indices[triangle+2]],drop});Triangle(a,b,c,result);
                }
            }
            else
            {
                var render=AssetDatabase.LoadAssetAtPath<GridLiquidFieldRenderProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/GridLiquidFieldRenderProfile_Default.asset");
                var material=Own(new Material(render.Shader));using var cpu=new GridLiquidFieldCpuSurface(f.Frame,new GridLiquidFieldSampler(f.Frame),material,f.Frame.Owners.Length,0);
                cpu.Begin();double until=Time.realtimeSinceStartupAsDouble+15;while(cpu.IsBuilding){Assert.That(Time.realtimeSinceStartupAsDouble,Is.LessThan(until));cpu.UpdateAndDraw();}
                var meshes=(Mesh[])typeof(GridLiquidFieldCpuSurface).GetField("_meshes",Hidden).GetValue(cpu);int visible=(int)typeof(GridLiquidFieldCpuSurface).GetField("_visible",Hidden).GetValue(cpu);
                for(int mesh=0;mesh<visible;mesh++){var vertices=meshes[mesh].vertices;var indices=meshes[mesh].triangles;for(int at=0;at<indices.Length;at+=3)Triangle(vertices[indices[at]],vertices[indices[at+1]],vertices[indices[at+2]],result);}
            }
            result.Volume=Math.Abs(result.Volume);TestContext.WriteLine("ground-contact Gmu="+f.Source.TotalQuantity+" H="+result.Height+" W="+result.Width+" V="+result.Volume+" Tri="+result.Triangles);
            Assert.That(result.Triangles,Is.GreaterThan(0));return result;
        }
        private void Triangle(Vector3 a,Vector3 b,Vector3 c,Measure result)
        {a-=_origin;b-=_origin;c-=_origin;if(Vector3.Cross(b-a,c-a).sqrMagnitude<1e-14f)return;result.Triangles++;result.Volume+=Vector3.Dot(a,Vector3.Cross(b,c))/6d;result.Minimum=Vector3.Min(result.Minimum,Vector3.Min(a,Vector3.Min(b,c)));result.Maximum=Vector3.Max(result.Maximum,Vector3.Max(a,Vector3.Max(b,c)));}
        private T Own<T>(T value)where T:Object{_owned.Add(value);return value;}
    }
}
