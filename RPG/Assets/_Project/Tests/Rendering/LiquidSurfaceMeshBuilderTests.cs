using System.Collections.Generic;
using Game.ElementField;
using Game.Materials;
using NUnit.Framework;
using UnityEngine;

namespace Game.Rendering.Tests
{
    public sealed class LiquidSurfaceMeshBuilderTests
    {
        [Test]
        public void QuantityDrivenCrownGrowsContinuouslyWithoutLookingFullEarly()
        {
            // 注水必须逐渐抬高液面。旧固定厚度或跨阈值关圆冠会让水量增加时画面反而跳低。
            float previous=0f;
            for(int i=1;i<=1000;i++)
            {
                float fill=i/1000f;
                float body=LiquidPuddleShape.BodyHeight(fill,.64f);
                float height=LiquidPuddleShape.Height(body,LiquidPuddleShape.ShoulderWidthCells,.64f,1f);
                Assert.That(height,Is.GreaterThanOrEqualTo(previous-1e-6f));
                Assert.That(height,Is.LessThanOrEqualTo(1f+1e-6f));
                Assert.That(height,Is.LessThanOrEqualTo(fill*1.9f));
                Assert.That(height-previous,Is.LessThan(.002f));
                previous=height;
            }
            Assert.That(previous,Is.EqualTo(1f).Within(1e-6f));
        }

        // 使用真实数量 Store；这个适配器只补 Renderer 的只读契约和记录采样次数。
        private sealed class StoreSurface : ILiquidSurfaceReadOnly, ILiquidMotionReadOnly,
            ILiquidSurfaceLayerReadOnly, ILiquidStableSurfaceSnapshotReadOnly
        {
            public readonly LiquidVolumeStore Store;
            public readonly Dictionary<Vector3Int, int> Reads = new Dictionary<Vector3Int, int>();
            private readonly float _cellSize;
            public bool CountReads = true;
            public LiquidFluxSolver Falling;
            public float? FallingOffset;
            public StableLiquidSurfacePatch? StablePatch;
            public float MotionHorizon => Falling != null ? Falling.MotionHorizon : .1f;
            public bool TryGetFallingMotion(Vector3Int cell,MaterialId material,out LiquidFallingMotion motion)
            {
                if(FallingOffset.HasValue)
                {motion=new LiquidFallingMotion(FallingOffset.Value,1f,9.81f,2f,1f);return true;}
                motion=default;return Falling!=null && Falling.TryGetFallingMotion(cell,material,out motion);
            }
            public StoreSurface(MaterialId material, bool floor = true, float cellSize = .25f,
                MaterialId[] materials = null, float[] densities = null)
            {
                _cellSize = cellSize;
                Store = new LiquidVolumeStore(4, 64, 255, materials ?? new[] { material },
                    cell => floor && cell.y < 0,densities);
            }
            public Vector3 Origin => new Vector3(10f, 3f, -2f);
            public float CellSize => _cellSize;
            public int ChunkSize => Store.ChunkSize;
            public int CellCapacityGmu => Store.CapacityGmu;
            public int MaximumSurfaceCandidateKeys => Store.MaximumSurfaceCandidateKeys;
            public int MaximumStableRegions => StablePatch.HasValue?1:0;
            public int MaximumStableSurfacePatches => StablePatch.HasValue?1:0;
            public uint SurfaceVersion => Store.SurfaceVersion;
            public uint DynamicSurfaceVersion => Store.SurfaceVersion;
            public ulong TotalQuantity => Store.TotalQuantity;
            public ulong PendingQuantity => 0;
            public uint GetDynamicSurfaceVersion(MaterialId material) => Store.GetMaterialSurfaceVersion(material);
            public uint GetDynamicCandidateVersion(MaterialId material) => Store.GetCandidateVersion(material);
            public uint GetSurfaceChunkVersion(ElementChunkKey key, MaterialId material) =>
                Store.GetMaterialChunkSurfaceVersion(key, material);
            public bool HasDynamicMaterialInChunk(ElementChunkKey key, MaterialId material) =>
                Store.HasMaterialInChunk(key, material);
            public int CopySurfaceChunkKeys(MaterialId material, ElementChunkKey[] destination) =>
                Store.CopyChunkKeys(material, destination);
            public bool IsSurfaceMaterialDominant(Vector3Int cell, MaterialId material)
            {
                if (CountReads)
                {
                    Reads.TryGetValue(cell, out int count);
                    Reads[cell] = count + 1;
                }
                return Store.IsDominantMaterial(cell, material);
            }
            public bool TryGetDynamicQuantity(Vector3Int cell, MaterialId material, out uint quantity) =>
                Store.TryGetQuantity(cell, material, out quantity);
            public bool TryGetSurfaceFill(Vector3Int cell,MaterialId material,out float fill)
            {
                if(CountReads){Reads.TryGetValue(cell,out int count);Reads[cell]=count+1;}
                return Store.TryGetSurfaceFill(cell,material,out fill);
            }
            public float GetTotalFill(Vector3Int cell)=>Store.OccupancyAt(cell)/(float)CellCapacityGmu;
            public int CopyLiquidChunkKeys(ElementChunkKey[] destination)=>Store.CopyLiquidChunkKeys(destination);
            public bool TryGetQuantity(Vector3Int cell, MaterialId material, out uint quantity) =>
                Store.TryGetQuantity(cell, material, out quantity);
            public bool TryGetAmount(Vector3Int cell, MaterialId material, out byte amount) =>
                Store.TryGetAmount(cell, material, out amount);
            public bool TryGetStableSurfaceHeight(Vector3Int cell, MaterialId material, out float height)
            { height = 0f; return false; }
            public uint GetStableSurfaceVersion(MaterialId material) => StablePatch?.Version ?? 0;
            public uint GetStableNeighborhoodVersion(ElementChunkKey key, MaterialId material) => StablePatch?.Version ?? 0;
            public int CopyStableSurfacePatches(MaterialId material, StableLiquidSurfacePatch[] destination) =>
                StablePatch.HasValue && StablePatch.Value.Material==material?CopyAllStableSurfacePatches(destination):0;
            public int CopyAllStableSurfacePatches(StableLiquidSurfacePatch[] destination)
            {
                if(!StablePatch.HasValue || destination.Length==0)return 0;
                destination[0]=StablePatch.Value;return 1;
            }
            public bool IsStableSurfaceHandoffPending(MaterialId material, ElementChunkKey key) => false;
            public bool IsActiveStableSurfaceOverlapping(MaterialId material, ElementChunkKey key) => false;
            public bool IsSolidForSurface(Vector3Int cell) => Store.IsSolid(cell);
        }

        private readonly List<Vector3> _vertices = new List<Vector3>(4096);
        private readonly List<Vector3> _normals = new List<Vector3>(4096);
        private readonly List<Vector2> _uvs = new List<Vector2>(4096);
        private readonly List<int> _triangles = new List<int>(8192);

        private static void FillPlane(StoreSurface source, MaterialId material,
            int minimumX = -1, int maximumX = 5, uint quantity = 255)
        {
            for (int z = -1; z < 5; z++)
            for (int x = minimumX; x < maximumX; x++)
                Assert.That(source.Store.TryAdd(new Vector3Int(x, 0, z),
                    material, quantity), Is.True);
        }

        private void Build(LiquidSurfaceMeshBuilder builder, ElementChunkKey key) =>
            builder.BuildWorldChunk(key, _vertices, _normals, _uvs, _triangles);

        [TestCase(64)]
        [TestCase(128)]
        [TestCase(255)]
        public void AirborneVolumeRemainsVisibleThroughFractionalFallPhases(int quantity)
        {
            var source=new StoreSurface(MaterialId.Water,false, .5f){CountReads=false};
            Assert.That(source.Store.TryAdd(new Vector3Int(1,2,1),MaterialId.Water,(uint)quantity),Is.True);
            var builder=new LiquidSurfaceMeshBuilder(source,MaterialId.Water,source.MaximumSurfaceCandidateKeys);
            float minimum=float.MaxValue,maximum=0f;
            for(int phase=0;phase<9;phase++)
            {
                source.FallingOffset=phase/8f*.999f;
                Build(builder,new ElementChunkKey(0,0,0));
                AssertClosed(_vertices,_triangles);
                double volume=0d;
                for(int i=0;i<_triangles.Count;i+=3)
                    volume+=Vector3.Dot(_vertices[_triangles[i]],Vector3.Cross(
                        _vertices[_triangles[i+1]],_vertices[_triangles[i+2]]))/6d;
                float v=(float)System.Math.Abs(volume);
                // 同时支持官方TestRunner与保留未保存场景的直接诊断；后者没有NUnit Output Writer。
                Game.Core.GameLog.Info("fall phase="+source.FallingOffset+", volume m3="+v,"LiquidVolumeTest");
                minimum=Mathf.Min(minimum,v);maximum=Mathf.Max(maximum,v);
            }
            Assert.That(minimum,Is.GreaterThan(.125f*quantity/255f*.85f),"空中外壳不能只有同量落地液体的一小部分。");
            Assert.That(maximum/minimum,Is.LessThan(1.22f),"跨采样节点不能造成明显的周期性缩胀。");
            Assert.That(source.TotalQuantity,Is.EqualTo((ulong)quantity));
        }

        [Test]
        public void LightPoisonKeepsSurfaceWhenItsShareCrossesWaterShare()
        {
            var source=new StoreSurface(MaterialId.Water,materials:new[]{MaterialId.Water,MaterialId.Poison},
                densities:new[]{1f,.9f}){CountReads=false};
            var cell=new Vector3Int(1,0,1);
            Assert.That(source.Store.TryAdd(cell,MaterialId.Water,200),Is.True);
            Assert.That(source.Store.TryAdd(cell,MaterialId.Poison,40),Is.True);
            var poison=new LiquidSurfaceMeshBuilder(source,MaterialId.Poison,64);
            Build(poison,new ElementChunkKey(0,0,0));
            Assert.That(_triangles.Count,Is.GreaterThan(0));
            float highBefore=0;foreach(Vector3 p in _vertices)highBefore=Mathf.Max(highBefore,p.y);
            Assert.That(source.Store.TryConvert(cell,MaterialId.Water,MaterialId.Poison,160),Is.True);
            Build(poison,new ElementChunkKey(0,0,0));
            Assert.That(_triangles.Count,Is.GreaterThan(0));
            float highAfter=0;foreach(Vector3 p in _vertices)highAfter=Mathf.Max(highAfter,p.y);
            Assert.That(highAfter,Is.EqualTo(highBefore).Within(1e-6f),"总量未变，份额过半不能造成水位翻面。");
            var water=new LiquidSurfaceMeshBuilder(source,MaterialId.Water,64);
            Build(water,new ElementChunkKey(0,0,0));
            Assert.That(_triangles.Count,Is.Zero,"底层材料不生成与自由面重叠的透明壳。");
            Assert.That(source.TotalQuantity,Is.EqualTo(240UL));
        }

        [Test]
        public void FrozenStableBoundaryKeepsHeightAndSupportUntilNextCapture()
        {
            var source=new StoreSurface(MaterialId.Water){CountReads=false};
            source.StablePatch=new StableLiquidSurfacePatch(1,1,source.Origin+new Vector3(6,.75f,1)*source.CellSize,
                new Vector2(4,2)*source.CellSize,1,MaterialId.Water,0,2);
            source.Store.TryAdd(new Vector3Int(1,4,1),MaterialId.Water,100);
            var frozen=new LiquidSurfaceFrameSnapshot(source,MaterialId.Water);frozen.Capture();
            var lakeCell=new Vector3Int(5,0,0);
            source.StablePatch=new StableLiquidSurfacePatch(1,2,source.Origin+new Vector3(6,1.5f,1)*source.CellSize,
                new Vector2(4,2)*source.CellSize,1,MaterialId.Water,0,2);
            Assert.That(frozen.TryGetStableSurfaceHeight(lakeCell,MaterialId.Water,out float old),Is.True);
            Assert.That(old,Is.EqualTo(.75f).Within(1e-6f));
            Assert.That(frozen.GetTotalFill(lakeCell),Is.EqualTo(.75f).Within(1e-6f),
                "没有动态页的稳定湖水也必须参与跨页支撑。");
            var patches=new StableLiquidSurfacePatch[1];
            Assert.That(frozen.CopyStableSurfacePatches(MaterialId.Water,patches),Is.EqualTo(1));
            Assert.That(patches[0].Version,Is.EqualTo(1));
            frozen.Capture();
            Assert.That(frozen.TryGetStableSurfaceHeight(lakeCell,MaterialId.Water,out float next),Is.True);
            Assert.That(next,Is.EqualTo(1.5f).Within(1e-6f));
            Assert.That(frozen.GetTotalFill(lakeCell),Is.EqualTo(1));
            long before=System.GC.GetAllocatedBytesForCurrentThread();frozen.Capture();
            Assert.That(System.GC.GetAllocatedBytesForCurrentThread()-before,Is.Zero);
            source.StablePatch=new StableLiquidSurfacePatch(1,3,source.StablePatch.Value.Center,
                source.StablePatch.Value.SizeXZ,1,MaterialId.Water,0,2,false);
            frozen.Capture();
            Assert.That(frozen.CopyStableSurfacePatches(MaterialId.Water,patches),Is.Zero,
                "原子交接完成后不能再显示已经转入动态 Store 的旧湖面。");
        }

        [Test]
        public void FrozenSurfaceRetainsSourceAndDestinationUntilNextCaptureWithoutAllocation()
        {
            var source=new StoreSurface(MaterialId.Water,false){CountReads=false,FallingOffset=.4f};
            var from=new Vector3Int(1,4,1);var to=new Vector3Int(1,3,1);
            Assert.That(source.Store.TryAdd(from,MaterialId.Water,255),Is.True);
            var frozen=new LiquidSurfaceFrameSnapshot(source,MaterialId.Water);frozen.Capture();
            Assert.That(source.Store.TryTransfer(from,to,MaterialId.Water,255),Is.True);
            Assert.That(frozen.TryGetDynamicQuantity(from,MaterialId.Water,out uint old),Is.True);
            Assert.That(old,Is.EqualTo(255));
            Assert.That(frozen.TryGetDynamicQuantity(to,MaterialId.Water,out _),Is.False);
            Assert.That(frozen.TryGetFallingMotion(from,MaterialId.Water,out LiquidFallingMotion m),Is.True);
            Assert.That(m.OffsetCells,Is.EqualTo(.4f));
            frozen.Capture();
            Assert.That(frozen.TryGetDynamicQuantity(from,MaterialId.Water,out _),Is.False);
            Assert.That(frozen.TryGetDynamicQuantity(to,MaterialId.Water,out uint next),Is.True);
            Assert.That(next,Is.EqualTo(255));
            long before=System.GC.GetAllocatedBytesForCurrentThread();
            frozen.Capture();
            Assert.That(System.GC.GetAllocatedBytesForCurrentThread()-before,Is.Zero);
        }

        [Test]
        public void MaterialGroupKeepsOldPageUntilEveryReplacementIsReady()
        {
            // 检查实际 Renderer 的提交边界：Poison 未就绪时不能先撤下 Water。
            // EditMode 不调用 Draw，不需要修改场景或驱动真实 GPU。
            const System.Reflection.BindingFlags fields=System.Reflection.BindingFlags.NonPublic
                |System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Static;
            var type=typeof(LiquidSurfaceRenderer);var viewType=type.GetNestedType("View",fields);
            var shared=(LiquidSurfaceRenderer[])type.GetField("SharedRenderers",fields).GetValue(null);
            int previousCount=(int)type.GetField("_sharedCount",fields).GetValue(null);
            var source=new StoreSurface(MaterialId.Water){CountReads=false};
            var hosts=new GameObject[2];var slots=new int[2];var arrays=new System.Array[2];
            var active=new Mesh[2];var pending=new Mesh[2];
            try
            {
                for(int i=0;i<2;i++)
                {
                    hosts[i]=new GameObject("Surface commit regression");
                    var renderer=hosts[i].AddComponent<LiquidSurfaceRenderer>();
                    type.GetField("_source",fields).SetValue(renderer,source);
                    type.GetField("_batchActive",fields).SetValue(renderer,true);
                    type.GetField("_indices",fields).SetValue(renderer,new Dictionary<ElementChunkKey,int>{{default,0}});
                    object view=System.Activator.CreateInstance(viewType);
                    const System.Reflection.BindingFlags members=System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.Instance;
                    active[i]=new Mesh();pending[i]=new Mesh();
                    viewType.GetField("Mesh",members).SetValue(view,active[i]);
                    viewType.GetField("PendingMesh",members).SetValue(view,pending[i]);
                    viewType.GetField("Assigned",members).SetValue(view,true);
                    viewType.GetField("Built",members).SetValue(view,true);
                    viewType.GetField("BuiltVersion",members).SetValue(view,1u);
                    viewType.GetField("DesiredVersion",members).SetValue(view,2u);
                    viewType.GetField("Staged",members).SetValue(view,i==0);
                    viewType.GetField("Retire",members).SetValue(view,i==0);
                    arrays[i]=System.Array.CreateInstance(viewType,1);arrays[i].SetValue(view,0);
                    type.GetField("_views",fields).SetValue(renderer,arrays[i]);
                    int slot=0;while(shared[slot]!=null)slot++;
                    slots[i]=slot;shared[slot]=renderer;
                    type.GetField("_sharedCount",fields).SetValue(null,Mathf.Max(previousCount,slot+1));
                }
                Assert.That(LiquidSurfaceRenderer.TryCommitPresentationGroup(source),Is.False);
                Assert.That(LiquidSurfaceRenderer.IsPresentationPending(source),Is.True);
                Assert.That(viewType.GetField("Assigned").GetValue(arrays[0].GetValue(0)),Is.True);
                Assert.That(viewType.GetField("Mesh").GetValue(arrays[1].GetValue(0)),Is.SameAs(active[1]));
                object ready=arrays[1].GetValue(0);viewType.GetField("Staged").SetValue(ready,true);arrays[1].SetValue(ready,0);
                Assert.That(LiquidSurfaceRenderer.TryCommitPresentationGroup(source),Is.True);
                Assert.That(viewType.GetField("Assigned").GetValue(arrays[0].GetValue(0)),Is.False);
                Assert.That(viewType.GetField("Mesh").GetValue(arrays[1].GetValue(0)),Is.SameAs(pending[1]));
                Assert.That(LiquidSurfaceRenderer.IsPresentationPending(source),Is.False);
            }
            finally
            {
                for(int i=0;i<2;i++)if(hosts[i]!=null)
                {
                    // 测试自己拥有临时 Mesh；EditMode 不让 OnDestroy 调用延迟 Destroy。
                    type.GetField("_views",fields).SetValue(hosts[i].GetComponent<LiquidSurfaceRenderer>(),null);
                    Object.DestroyImmediate(hosts[i]);
                }
                type.GetField("_sharedCount",fields).SetValue(null,previousCount);
                for(int i=0;i<2;i++){if(active[i]!=null)Object.DestroyImmediate(active[i]);if(pending[i]!=null)Object.DestroyImmediate(pending[i]);}
            }
        }

        [TestCase(MaterialId.Water)]
        [TestCase(MaterialId.Poison)]
        [TestCase(MaterialId.Sticky)]
        public void FlatInteriorHasTwoTrianglesWithSameCoverageAndUpwardWinding(MaterialId material)
        {
            var source = new StoreSurface(material);
            FillPlane(source, material);
            var builder = new LiquidSurfaceMeshBuilder(source, material, source.MaximumSurfaceCandidateKeys);
            ulong before = source.TotalQuantity;

            Build(builder, new ElementChunkKey(0, 0, 0));

            Assert.That(_vertices.Count, Is.EqualTo(4));
            Assert.That(_triangles.Count, Is.EqualTo(6));
            float area = 0f;
            for (int i = 0; i < _triangles.Count; i += 3)
            {
                Vector3 a = _vertices[_triangles[i]], b = _vertices[_triangles[i + 1]],
                    c = _vertices[_triangles[i + 2]];
                Vector3 cross = Vector3.Cross(b - a, c - a);
                Assert.That(cross.y, Is.GreaterThan(0f));
                area += cross.magnitude * .5f;
            }
            Assert.That(area, Is.EqualTo(1f).Within(1e-6f));
            Assert.That(source.TotalQuantity, Is.EqualTo(before));
            foreach (int reads in source.Reads.Values) Assert.That(reads, Is.EqualTo(1));
        }

        [Test]
        public void SmallGroundPuddleHasRoundedVolumeAndBoundedSampling()
        {
            var source=new StoreSurface(MaterialId.Water){CountReads=false};
            for(int z=0;z<3;z++)for(int x=0;x<3;x++)
                source.Store.TryAdd(new Vector3Int(x,0,z),MaterialId.Water,64);
            var builder=new LiquidSurfaceMeshBuilder(source,MaterialId.Water,source.MaximumSurfaceCandidateKeys);
            ulong quantity=source.TotalQuantity;
            Build(builder,new ElementChunkKey(0,0,0));
            float lowest=float.MaxValue,highest=float.MinValue;bool curvedNormals=false;
            for(int i=0;i<_vertices.Count;i++)
            {
                lowest=Mathf.Min(lowest,_vertices[i].y);highest=Mathf.Max(highest,_vertices[i].y);
                curvedNormals|=Mathf.Abs(_normals[i].x)+Mathf.Abs(_normals[i].z)>.1f;
            }
            Assert.That(highest-lowest,Is.GreaterThan(source.CellSize*.2f),"侧视应有可见圆顶和侧壁。");
            Assert.That(curvedNormals,Is.True,"边缘不能全是向上的平面法线。");
            Assert.That(builder.LastBuildSourceSamples,Is.LessThan(400),"地面水滩不应查询三维邻域。");
            Assert.That(source.TotalQuantity,Is.EqualTo(quantity),"视觉厚度不能虚增GMU。");
        }

        [Test]
        public void GreedyTopDoesNotCoverAnEmptyCell()
        {
            var source = new StoreSurface(MaterialId.Water);
            FillPlane(source, MaterialId.Water);
            Assert.That(source.Store.TryRemove(new Vector3Int(2, 0, 2), MaterialId.Water, 255), Is.True);
            var builder = new LiquidSurfaceMeshBuilder(source, MaterialId.Water, source.MaximumSurfaceCandidateKeys);

            Build(builder, new ElementChunkKey(0, 0, 0));

            var hole = new Vector2(2.5f, 2.5f) * source.CellSize;
            for (int i = 0; i < _triangles.Count; i += 3)
            {
                Vector3 a = _vertices[_triangles[i]], b = _vertices[_triangles[i + 1]],
                    c = _vertices[_triangles[i + 2]];
                if (Vector3.Cross(b - a, c - a).y <= 1e-8f) continue;
                Assert.That(ContainsPointXZ(hole, a, b, c), Is.False,
                    "空格中心不能被平坦区合并的三角形覆盖。");
            }
        }

        [TestCase(MaterialId.Water)]
        [TestCase(MaterialId.Poison)]
        [TestCase(MaterialId.Sticky)]
        public void ThickGroundPuddleHasBroadConvexShoulderInsteadOfSharpLip(MaterialId material)
        {
            var source=new StoreSurface(material){CountReads=false};
            for(int z=0;z<3;z++)for(int x=0;x<3;x++)
                source.Store.TryAdd(new Vector3Int(x,0,z),material,224);
            const float depth=.22f;
            var builder=new LiquidSurfaceMeshBuilder(source,material,source.MaximumSurfaceCandidateKeys,
                depth/source.CellSize);
            Build(builder,new ElementChunkKey(0,0,0));

            // 在直岸的一条横截面看真实几何：圆肩要占据一段宽度，斜率逐渐减小。
            // 原覆盖率截面在第一个采样点已升到顶，增厚后会形成陡唇。
            float quarter=HeightAt(.25f*source.CellSize,1.5f*source.CellSize);
            float half=HeightAt(.5f*source.CellSize,1.5f*source.CellSize);
            float inner=HeightAt(.75f*source.CellSize,1.5f*source.CellSize);
            Assert.That(quarter,Is.GreaterThan(depth*.45f)
                .And.LessThan(HeightAt(1.5f*source.CellSize,1.5f*source.CellSize)*.8f));
            Assert.That(half,Is.GreaterThan(quarter));
            Assert.That(inner,Is.GreaterThan(half));
            Assert.That(inner-half,Is.LessThan(half-quarter),"凸圆弧应逐渐变平，避免直墙接平顶。");
            Assert.That(HeightAt(source.CellSize,1.5f*source.CellSize),Is.GreaterThan(depth));
            Assert.That(source.TotalQuantity,Is.EqualTo(2016UL));
            Assert.That(builder.LastBuildSourceSamples,Is.LessThan(400));
        }

        [TestCase(MaterialId.Water)]
        [TestCase(MaterialId.Poison)]
        [TestCase(MaterialId.Sticky)]
        public void ShallowPuddleCrownCurvesAcrossBodyInsteadOfAFlatMesa(MaterialId material)
        {
            // 使用正式配置的 .5m 粗粒度；接近满层时应按剩余高度约束圆冠。
            var source=new StoreSurface(material,cellSize:.5f){CountReads=false};
            for(int z=0;z<3;z++)for(int x=0;x<3;x++)source.Store.TryAdd(new Vector3Int(x,0,z),material,180);
            var builder=new LiquidSurfaceMeshBuilder(source,material,source.MaximumSurfaceCandidateKeys,.32f/source.CellSize);
            Build(builder,new ElementChunkKey(0,0,0));
            float center=HeightAt(.75f,.75f),shoulder=HeightAt(.375f,.75f);
            Assert.That(center-shoulder,Is.GreaterThan(.01f),"圆冠必须在水滩内部仍有曲率。");
            Assert.That(center,Is.GreaterThan(.40f));
            Assert.That(center,Is.LessThan(180f/255f*source.CellSize*1.9f),"圆冠需要对应真实液量，不能无条件显示为.53m。");
            Assert.That(source.TotalQuantity,Is.EqualTo(1620UL));
            Build(builder,new ElementChunkKey(0,0,0));
            long before=System.GC.GetAllocatedBytesForCurrentThread();
            builder.BuildWorldChunk(new ElementChunkKey(0,0,0),_vertices,_normals,_uvs,_triangles);
            long allocated=System.GC.GetAllocatedBytesForCurrentThread()-before;
            Assert.That(allocated,Is.Zero);
        }

        [Test]
        public void TinyQuantityDoesNotProduceTheSameThickBodyAsLargeQuantity()
        {
            var source=new StoreSurface(MaterialId.Water){CountReads=false};
            for(int z=0;z<3;z++)for(int x=0;x<3;x++)source.Store.TryAdd(new Vector3Int(x,0,z),MaterialId.Water,4);
            var builder=new LiquidSurfaceMeshBuilder(source,MaterialId.Water,source.MaximumSurfaceCandidateKeys,.32f/.25f);
            Build(builder,new ElementChunkKey(0,0,0));
            float tiny=HeightAt(.375f,.375f);
            Assert.That(tiny,Is.LessThan(4f/255f*.25f*1.9f));
            for(int z=0;z<3;z++)for(int x=0;x<3;x++)source.Store.TryAdd(new Vector3Int(x,0,z),MaterialId.Water,176);
            Build(builder,new ElementChunkKey(0,0,0));
            Assert.That(HeightAt(.375f,.375f),Is.GreaterThan(tiny*30f));
        }

        private float HeightAt(float x,float z)
        {
            float height=float.MinValue;
            foreach(Vector3 p in _vertices)
                if(Mathf.Abs(p.x-x)<1e-5f && Mathf.Abs(p.z-z)<1e-5f)height=Mathf.Max(height,p.y);
            Assert.That(height,Is.GreaterThan(float.MinValue),"横截面的采样顶点必须存在。");
            return height;
        }

        [TestCase(MaterialId.Water)]
        [TestCase(MaterialId.Poison)]
        [TestCase(MaterialId.Sticky)]
        public void IsolatedCartoonPuddleHasCurvedSilhouetteAndRaisedCrown(MaterialId material)
        {
            var source=new StoreSurface(material){CountReads=false};
            source.Store.TryAdd(Vector3Int.zero,material,128);
            var builder=new LiquidSurfaceMeshBuilder(source,material,source.MaximumSurfaceCandidateKeys,
                .32f/source.CellSize);
            Build(builder,new ElementChunkKey(0,0,0));
            float edgeHeight=LiquidPuddleShape.EdgeHeightCells*source.CellSize;
            var center=new Vector2(.5f,.5f)*source.CellSize;
            float minimum=float.MaxValue,maximum=0f,highest=0f,longest=0f;
            foreach(Vector3 p in _vertices)
            {
                highest=Mathf.Max(highest,p.y);
                if(Mathf.Abs(p.y-edgeHeight)>1e-6f)continue;
                float radius=Vector2.Distance(new Vector2(p.x,p.z),center);
                minimum=Mathf.Min(minimum,radius);maximum=Mathf.Max(maximum,radius);
            }
            longest=LongestCapBoundarySegment(_vertices,_triangles,edgeHeight);
            // 单格液包应形成接近圆形的剪影，外沿弦段不能仍然横跨整块粗网格。
            Assert.That(minimum,Is.GreaterThan(0f));
            Assert.That(maximum-minimum,Is.LessThan(.001f));
            Assert.That(longest,Is.GreaterThan(0f).And.LessThan(source.CellSize*.2f));
            // 半满单格仍有圆冠，但不能沿用旧“任意液量都抬到.32m”的断言。
            float physicalDepth=128f/source.CellCapacityGmu*source.CellSize;
            Assert.That(highest,Is.GreaterThan(physicalDepth)
                .And.LessThan(physicalDepth*1.9f));
            Assert.That(source.TotalQuantity,Is.EqualTo(128UL));
            Assert.That(builder.LastBuildSourceSamples,Is.LessThan(400));
        }

        internal static float LongestCapBoundarySegment(List<Vector3> vertices,List<int> triangles,float floor)
        {
            // Ear Clipping 会在两个边缘点之间生成内部对角线；不能把它统计成剪影。
            // 焊接位置后，只在顶面中恰好出现一次的边才是真正的外轮廓。
            var ids=new Dictionary<Vector3Int,int>();var points=new List<Vector3>();
            var edges=new Dictionary<long,int>();
            for(int i=0;i<triangles.Count;i+=3)
            {
                bool cap=true;
                for(int j=0;j<3;j++)if(vertices[triangles[i+j]].y<floor-1e-6f)cap=false;
                if(!cap)continue;
                for(int j=0;j<3;j++)
                {
                    Vector3 a=vertices[triangles[i+j]],b=vertices[triangles[i+(j+1)%3]];
                    if(Mathf.Abs(a.y-floor)>1e-6f || Mathf.Abs(b.y-floor)>1e-6f)continue;
                    var qa=Vector3Int.RoundToInt(a*100000f);var qb=Vector3Int.RoundToInt(b*100000f);
                    if(!ids.TryGetValue(qa,out int ia)){ia=points.Count;ids.Add(qa,ia);points.Add(a);}
                    if(!ids.TryGetValue(qb,out int ib)){ib=points.Count;ids.Add(qb,ib);points.Add(b);}
                    long key=((long)Mathf.Min(ia,ib)<<32)|(uint)Mathf.Max(ia,ib);
                    edges.TryGetValue(key,out int count);edges[key]=count+1;
                }
            }
            float longest=0f;
            foreach(var pair in edges)if(pair.Value==1)
                longest=Mathf.Max(longest,Vector3.Distance(points[(int)(pair.Key>>32)],points[(int)(pair.Key&uint.MaxValue)]));
            return longest;
        }

        [Test]
        public void NegativeChunkSeamRetainsWorldHeightAndUvOnPartialPlateau()
        {
            var source = new StoreSurface(MaterialId.Poison);
            FillPlane(source, MaterialId.Poison, -5, 5, 128);
            var builder = new LiquidSurfaceMeshBuilder(source, MaterialId.Poison, source.MaximumSurfaceCandidateKeys);
            Build(builder, new ElementChunkKey(-1, 0, 0));
            Vector3[] left = _vertices.ToArray();
            Vector3[] leftNormals = _normals.ToArray();
            Vector2[] leftUv = _uvs.ToArray();
            Build(builder, new ElementChunkKey(0, 0, 0));
            int matches = 0;
            Vector3 leftOffset = source.Origin + Vector3.left * source.ChunkSize * source.CellSize;
            for (int i = 0; i < left.Length; i++)
            for (int j = 0; j < _vertices.Count; j++)
            {
                if (Mathf.Abs(left[i].x - source.ChunkSize * source.CellSize) > 1e-6f
                    || Mathf.Abs(_vertices[j].x) > 1e-6f) continue;
                if ((left[i] + leftOffset - _vertices[j] - source.Origin).sqrMagnitude > 1e-10f) continue;
                Assert.That((leftUv[i] - _uvs[j]).sqrMagnitude, Is.LessThan(1e-10f));
                matches++;
            }
            // 部分填充的圆冠在 Halo 内仍有曲率，边界不再只能有合并平面的两个角点。
            // 按位置检查两侧全部接缝顶点，保留位置、UV、法线一致性的要求。
            Assert.That(matches, Is.GreaterThanOrEqualTo(2));
            for(int i=0;i<left.Length;i++)
            {
                if(Mathf.Abs(left[i].x-source.ChunkSize*source.CellSize)>1e-6f)continue;
                bool found=false;
                for(int j=0;j<_vertices.Count;j++)
                    found|=Mathf.Abs(_vertices[j].x)<1e-6f
                        && (left[i]+leftOffset-_vertices[j]-source.Origin).sqrMagnitude<1e-10f
                        && (leftNormals[i]-_normals[j]).sqrMagnitude<1e-8f;
                Assert.That(found,Is.True,"负坐标 Chunk 的每个接缝顶点都必须有位置/法线匹配；硬边允许同位置多法线。");
            }
        }

        [Test]
        public void RebuildReadsChangedHeightAndPreservesUnevenSurface()
        {
            var source = new StoreSurface(MaterialId.Water);
            FillPlane(source, MaterialId.Water);
            var builder = new LiquidSurfaceMeshBuilder(source, MaterialId.Water, source.MaximumSurfaceCandidateKeys);
            Build(builder, new ElementChunkKey(0, 0, 0));
            Assert.That(source.Store.TryRemove(new Vector3Int(1, 0, 1), MaterialId.Water, 155), Is.True);
            source.Reads.Clear();

            Build(builder, new ElementChunkKey(0, 0, 0));

            var expected = new Vector3(1.5f, 100f / 255f, 1.5f) * source.CellSize;
            float localHeight = float.MaxValue;
            for (int i = 0; i < _vertices.Count; i++)
                if (Mathf.Abs(_vertices[i].x-expected.x)<1e-5f && Mathf.Abs(_vertices[i].z-expected.z)<1e-5f
                    && _normals[i].y>0f) localHeight=Mathf.Min(localHeight,_vertices[i].y);
            // Surface Nets 会插值邻域，显示高度允许圆润，但必须响应减少的量且远低于旧平面。
            Assert.That(localHeight, Is.InRange(expected.y-.001f,source.CellSize-.02f));
            foreach (int reads in source.Reads.Values) Assert.That(reads, Is.EqualTo(1));
        }

        [Test]
        public void UnsupportedCellHasClosedRoundedShellWithoutChangingQuantity()
        {
            var source = new StoreSurface(MaterialId.Sticky, false);
            Assert.That(source.Store.TryAdd(Vector3Int.zero, MaterialId.Sticky, 128), Is.True);
            var builder = new LiquidSurfaceMeshBuilder(source, MaterialId.Sticky, source.MaximumSurfaceCandidateKeys);

            Build(builder, new ElementChunkKey(0, 0, 0));

            Assert.That(_vertices.Count, Is.GreaterThan(6));
            Assert.That(_triangles.Count, Is.GreaterThan(24));
            AssertClosed(_vertices, _triangles);
            Assert.That(source.TotalQuantity, Is.EqualTo(128));
            for (int i = 0; i < _normals.Count; i++)
                Assert.That(_normals[i].sqrMagnitude, Is.EqualTo(1f).Within(1e-5f));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AirborneStreamAcrossNegativeChunkBoundaryHasOneClosedShell(bool crossCorner)
        {
            var source = new StoreSurface(MaterialId.Water, false);
            for (int x = -1; x <= 0; x++)
            for (int z = crossCorner ? -1 : 1; z <= (crossCorner ? 0 : 1); z++)
                Assert.That(source.Store.TryAdd(new Vector3Int(x,1,z), MaterialId.Water, 255), Is.True);
            var builder = new LiquidSurfaceMeshBuilder(source, MaterialId.Water, source.MaximumSurfaceCandidateKeys);
            var combinedVertices = new List<Vector3>(); var combinedTriangles = new List<int>();
            var weld = new Dictionary<Vector3Int,int>();
            for (int page = -1; page <= 0; page++)
            for (int pageZ = crossCorner ? -1 : 0; pageZ <= 0; pageZ++)
            {
                Build(builder, new ElementChunkKey(page,0,pageZ));
                var remap = new int[_vertices.Count];
                for (int i=0;i<_vertices.Count;i++)
                {
                    Vector3 p = _vertices[i] + new Vector3(page,0,pageZ) * (source.ChunkSize * source.CellSize);
                    var quantized = Vector3Int.RoundToInt(p * 100000f);
                    if (!weld.TryGetValue(quantized, out int id))
                    { id=combinedVertices.Count; weld.Add(quantized,id); combinedVertices.Add(p); }
                    remap[i]=id;
                }
                for(int i=0;i<_triangles.Count;i++) combinedTriangles.Add(remap[_triangles[i]]);
            }
            AssertClosed(combinedVertices, combinedTriangles);
            // 两个相邻格必须连通；独立闭合滴也能通过 manifold 检查，故额外检查拓扑连通。
            var seen = new HashSet<int> { combinedTriangles[0] };
            bool changed;
            do
            {
                changed=false;
                for(int i=0;i<combinedTriangles.Count;i+=3)
                    if(seen.Contains(combinedTriangles[i]) || seen.Contains(combinedTriangles[i+1]) || seen.Contains(combinedTriangles[i+2]))
                        for(int j=0;j<3;j++) changed |= seen.Add(combinedTriangles[i+j]);
            } while(changed);
            foreach(int id in combinedTriangles) Assert.That(seen.Contains(id), Is.True);
        }

        [Test]
        public void AirborneMeshCarriesContinuousMotionButGroundMeshDoesNot()
        {
            var source=new StoreSurface(MaterialId.Water,false) {CountReads=false};
            var activity=new LiquidActivityScheduler(128,source.Store);
            source.Falling=new LiquidFluxSolver(source.Store,activity,null,new[]{MaterialId.Water},
                new[]{96},new[]{24},new[]{8});
            var cell=new Vector3Int(1,2,1);
            Assert.That(source.Store.TryAdd(cell,MaterialId.Water,100),Is.True);
            activity.WakeNeighbors(cell);source.Falling.Step(128,.1f,3f);
            var builder=new LiquidSurfaceMeshBuilder(source,MaterialId.Water,source.MaximumSurfaceCandidateKeys);
            var motion=new List<Vector4>(4096);
            builder.BuildWorldChunk(new ElementChunkKey(0,0,0),_vertices,_normals,_uvs,_triangles,motion);
            Assert.That(motion.Count,Is.EqualTo(_vertices.Count));
            Assert.That(motion.Count,Is.GreaterThan(0));
            foreach(Vector4 m in motion)
            {
                Assert.That(m.x,Is.EqualTo(.981f).Within(.0001f));
                Assert.That(m.y,Is.EqualTo(9.81f).Within(.0001f));
                Assert.That(m.w,Is.EqualTo(3f));
                Assert.That(m.z,Is.GreaterThan(0f));
            }
            // Vertex 运动属性生成也必须在热路径不分配托管内存。
            builder.BuildWorldChunk(new ElementChunkKey(0,0,0),_vertices,_normals,_uvs,_triangles,motion);
            long before=System.GC.GetAllocatedBytesForCurrentThread();
            builder.BuildWorldChunk(new ElementChunkKey(0,0,0),_vertices,_normals,_uvs,_triangles,motion);
            Assert.That(System.GC.GetAllocatedBytesForCurrentThread()-before,Is.Zero);
            var ground=new StoreSurface(MaterialId.Water);
            FillPlane(ground,MaterialId.Water);
            var groundBuilder=new LiquidSurfaceMeshBuilder(ground,MaterialId.Water,ground.MaximumSurfaceCandidateKeys);
            groundBuilder.BuildWorldChunk(new ElementChunkKey(0,0,0),_vertices,_normals,_uvs,_triangles,motion);
            foreach(Vector4 m in motion)Assert.That(m,Is.EqualTo(Vector4.zero));
        }

        [Test]
        public void FastFallingPacketsHaveOneClosedVisualStreamAcrossVerticalPages()
        {
            var source=new StoreSurface(MaterialId.Water,false) {CountReads=false};
            var activity=new LiquidActivityScheduler(256,source.Store);
            source.Falling=new LiquidFluxSolver(source.Store,activity,null,new[]{MaterialId.Water},
                new[]{96},new[]{24},new[]{8});
            for(int y=70;y<=75;y+=5)
            {
                var cell=new Vector3Int(1,y,1);
                Assert.That(source.Store.TryAdd(cell,MaterialId.Water,100),Is.True);activity.WakeNeighbors(cell);
            }
            for(int tick=1;tick<=10;tick++)source.Falling.Step(256,.1f,tick*.1f);
            var builder=new LiquidSurfaceMeshBuilder(source,MaterialId.Water,source.MaximumSurfaceCandidateKeys);
            var vertices=new List<Vector3>();var triangles=new List<int>();var weld=new Dictionary<Vector3Int,int>();
            int pages=builder.CopyCandidateKeys();Assert.That(pages,Is.EqualTo(2));
            for(int page=0;page<pages;page++)
            {
                ElementChunkKey key=builder.KeyAt(page);Build(builder,key);var remap=new int[_vertices.Count];
                for(int i=0;i<_vertices.Count;i++)
                {
                    Vector3 p=_vertices[i]+new Vector3(key.X,key.Y,key.Z)*(source.ChunkSize*source.CellSize);
                    var position=Vector3Int.RoundToInt(p*100000f);
                    if(!weld.TryGetValue(position,out int id)){id=vertices.Count;weld.Add(position,id);vertices.Add(p);}
                    remap[i]=id;
                }
                foreach(int index in _triangles)triangles.Add(remap[index]);
            }
            AssertClosed(vertices,triangles);
            var seen=new HashSet<int>{triangles[0]};bool changed;
            do
            {
                changed=false;
                for(int i=0;i<triangles.Count;i+=3)
                    if(seen.Contains(triangles[i])||seen.Contains(triangles[i+1])||seen.Contains(triangles[i+2]))
                        for(int j=0;j<3;j++)changed|=seen.Add(triangles[i+j]);
            }while(changed);
            foreach(int id in triangles)Assert.That(seen.Contains(id),Is.True,"高速落水不能退化为分离的串珠。");
            Assert.That(source.TotalQuantity,Is.EqualTo(200UL));
            Assert.That(source.Store.TryGetQuantity(new Vector3Int(1,52,1),MaterialId.Water,out _),Is.False,
                "视觉水柱不能伪造中间空格的 Gameplay 数量。");
        }

        [Test]
        public void VeryThinFilmRemainsVisibleAndSmoothRebuildHasZeroManagedAllocation()
        {
            var source = new StoreSurface(MaterialId.Water) { CountReads=false };
            Assert.That(source.Store.TryAdd(Vector3Int.zero,MaterialId.Water,1), Is.True);
            var builder = new LiquidSurfaceMeshBuilder(source,MaterialId.Water,source.MaximumSurfaceCandidateKeys);
            Build(builder,new ElementChunkKey(0,0,0));
            Assert.That(_triangles.Count, Is.GreaterThan(0), "少量液体不应在表面阈值处直接消失。");
            for (int i = 0; i < _vertices.Count; i++)
                if (_normals[i].y > .999f)
                    Assert.That(_vertices[i].y, Is.GreaterThan(0f)
                        .And.LessThan(1f/source.CellCapacityGmu*source.CellSize*1.9f),
                        "1 GMU 是微量残留，厚度必须与数量对应；微量表面仍有几何且保持连续。");
            Build(builder,new ElementChunkKey(0,0,0));
            long before=System.GC.GetAllocatedBytesForCurrentThread();
            for(int i=0;i<3;i++) Build(builder,new ElementChunkKey(0,0,0));
            Assert.That(System.GC.GetAllocatedBytesForCurrentThread()-before, Is.Zero);
            Assert.That(source.TotalQuantity, Is.EqualTo(1));
        }

        [Test]
        public void FallingPacketsSeparatedByOneEmptyCellHaveConnectedVisualShell()
        {
            var source=new StoreSurface(MaterialId.Water,false);
            Assert.That(source.Store.TryAdd(Vector3Int.zero,MaterialId.Water,128),Is.True);
            Assert.That(source.Store.TryAdd(new Vector3Int(0,2,0),MaterialId.Water,128),Is.True);
            var builder=new LiquidSurfaceMeshBuilder(source,MaterialId.Water,source.MaximumSurfaceCandidateKeys);
            Build(builder,new ElementChunkKey(0,0,0));
            AssertClosed(_vertices,_triangles);
            var seen=new HashSet<int>{_triangles[0]};bool changed;
            do
            {
                changed=false;
                for(int i=0;i<_triangles.Count;i+=3)
                    if(seen.Contains(_triangles[i]) || seen.Contains(_triangles[i+1]) || seen.Contains(_triangles[i+2]))
                        for(int j=0;j<3;j++) changed |= seen.Add(_triangles[i+j]);
            }while(changed);
            foreach(int id in _triangles) Assert.That(seen.Contains(id),Is.True);
            Assert.That(source.TotalQuantity,Is.EqualTo(256));
            Assert.That(source.Store.HasAnyLiquid(new Vector3Int(0,1,0)),Is.False,
                "视觉连接不能给空格增加 Gameplay 数量。");
        }

        private static void AssertClosed(List<Vector3> vertices, List<int> triangles)
        {
            Assert.That(triangles.Count, Is.GreaterThan(0));
            var edges = new Dictionary<long,int>();
            for(int i=0;i<triangles.Count;i+=3)
            {
                Vector3 cross=Vector3.Cross(vertices[triangles[i+1]]-vertices[triangles[i]],vertices[triangles[i+2]]-vertices[triangles[i]]);
                Assert.That(cross.sqrMagnitude, Is.GreaterThan(1e-16f), "不能生成退化 Triangle。");
                for(int j=0;j<3;j++)
                {
                    int a=triangles[i+j],b=triangles[i+(j+1)%3];
                    long key=((long)Mathf.Min(a,b)<<32)|(uint)Mathf.Max(a,b);
                    edges.TryGetValue(key,out int count);edges[key]=count+1;
                }
            }
            foreach(int count in edges.Values) Assert.That(count,Is.EqualTo(2),"闭合液团的每条边应恰被两个面共用。");
        }

        [Test]
        public void RepeatedMeshBuildReusesManagedBuffers()
        {
            var source = new StoreSurface(MaterialId.Water) { CountReads = false };
            FillPlane(source, MaterialId.Water);
            var builder = new LiquidSurfaceMeshBuilder(source, MaterialId.Water, source.MaximumSurfaceCandidateKeys);
            var key = new ElementChunkKey(0, 0, 0);
            Build(builder, key); // 预热只读源静态缓存与运行时首次调用。
            Build(builder, key);

            long before = System.GC.GetAllocatedBytesForCurrentThread();
            for (int repeat = 0; repeat < 4; repeat++) Build(builder, key);
            long allocated = System.GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.That(allocated, Is.Zero);
        }

        private static bool ContainsPointXZ(Vector2 point, Vector3 a, Vector3 b, Vector3 c)
        {
            float first = CrossXZ(point, a, b), second = CrossXZ(point, b, c), third = CrossXZ(point, c, a);
            return first >= -1e-7f && second >= -1e-7f && third >= -1e-7f
                || first <= 1e-7f && second <= 1e-7f && third <= 1e-7f;
        }

        private static float CrossXZ(Vector2 point, Vector3 a, Vector3 b) =>
            (b.x - a.x) * (point.y - a.z) - (b.z - a.z) * (point.x - a.x);
    }
}
