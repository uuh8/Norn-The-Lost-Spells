using System.Collections.Generic;
using Game.ElementField;
using Game.Materials;
using Unity.Profiling;
using UnityEngine;

namespace Game.Rendering
{
    /// <summary>
    /// 局部 Signed Distance Field + Surface Nets 外壳。半格采样连接相邻空中液滴，
    /// 格子只承担数据职责；视觉圆润不回写 Gameplay 数量，也不启动 PBF 邻居迭代。
    /// 所有工作数组初始化分配，跨页使用同一世界采样坐标和唯一 Face Owner。
    /// </summary>
    internal sealed class LiquidSmoothSurfaceMesher
    {
        private static readonly ProfilerMarker GroundMarker=new ProfilerMarker("GridLiquid.Mesh.GroundCap");
        private static readonly ProfilerMarker FieldMarker=new ProfilerMarker("GridLiquid.Mesh.FieldScatter");
        private static readonly ProfilerMarker VertexMarker=new ProfilerMarker("GridLiquid.Mesh.SurfaceNets");
        private static readonly ProfilerMarker SmoothMarker=new ProfilerMarker("GridLiquid.Mesh.Smoothing");
        private static readonly ProfilerMarker EdgeMarker=new ProfilerMarker("GridLiquid.Mesh.FaceAssembly");
        private readonly ILiquidSurfaceReadOnly _source;
        private readonly MaterialId _material;
        private readonly ILiquidMotionReadOnly _motionSource;
        private readonly ILiquidSurfaceLayerReadOnly _layers;
        private readonly float[] _fills, _radii, _combined, _field, _corners = new float[8];
        private readonly float[] _groundDensity, _groundTop;
        private readonly float[] _ownerDistances;
        private readonly Vector3Int[] _nodeOwners;
        private readonly LiquidFallingMotion[] _motion;
        private readonly bool[] _solids;
        private readonly uint[] _fillStamps, _combinedStamps, _solidStamps;
        private readonly uint[] _isolatedStamps;
        private readonly bool[] _isolated;
        private readonly int[] _ids;
        private readonly byte[] _masks;
        private readonly Vector3[] _positions, _smoothed;
        private readonly Vector3[] _shoreCorners=new Vector3[4],_shorePolygon=new Vector3[8];
        private readonly Vector3[] _shoreNormals=new Vector3[8],_cornerNormals=new Vector3[4];
        private readonly bool[] _shoreBoundary=new bool[8];
        private readonly Vector3[] _roundPolygon=new Vector3[8],_roundNormals=new Vector3[8];
        private readonly bool[] _roundBoundary=new bool[8];
        private readonly int[] _shoreTriangleScratch=new int[8];
        private readonly float[] _shoreValues=new float[4];
        private readonly Vector3[] _sheetPoints;
        private readonly float[] _sheetCoverage;
        private readonly Vector2[] _sheetGradients;
        private readonly uint[] _sheetStamps;
        private readonly float[] _crownSupports;
        private readonly uint[] _crownStamps;
        private readonly System.Func<int,int,float> _sampleCrown;
        private readonly int _crownSize;
        private readonly int _sheetSize;
        private readonly float _minimumPuddleDepthCells;
        private readonly int _cacheSize, _cacheSlice, _nodeSize, _nodeSlice, _cubeSize;
        private uint _stamp;
        private Vector3Int _base, _minimum;
        private ElementChunkKey _key;
        private float _flatHeight;
        private bool _nearStable;
        private bool _hasFalling;
        private int _groundLayer;
        private List<Vector3> _vertices, _normals;
        private List<Vector2> _uvs;
        private List<int> _indices;
        private List<Vector4> _motionData;
        internal int SourceSamples { get; private set; }

        internal LiquidSmoothSurfaceMesher(ILiquidSurfaceReadOnly source, MaterialId material,
            float minimumPuddleDepthCells=.35f)
        {
            _source = source; _material = material;
            _motionSource = source as ILiquidMotionReadOnly;
            _layers = source as ILiquidSurfaceLayerReadOnly;
            _minimumPuddleDepthCells=Mathf.Max(.08f,minimumPuddleDepthCells);
            _sheetSize=(source.ChunkSize+4)*4+1;
            _sheetPoints=new Vector3[_sheetSize*_sheetSize];
            _sheetGradients=new Vector2[_sheetPoints.Length];
            _sheetCoverage=new float[_sheetPoints.Length];_sheetStamps=new uint[_sheetPoints.Length];
            _crownSize=source.ChunkSize+12;
            _crownSupports=new float[_crownSize*_crownSize];_crownStamps=new uint[_crownSupports.Length];
            _sampleCrown=SampleCrown; // 委托只在初始化建立，热路径不创建闭包。
            // 六格 Halo 覆盖高速水柱中点和两步平滑邻域；缓存还包含连接远端数量/
            // Solid 查询。仅空中三维外壳使用这些 Node，地面/静湖走二维岸线路径。
            _cacheSize = source.ChunkSize + 32; _cacheSlice = _cacheSize * _cacheSize;
            int count = _cacheSlice * _cacheSize;
            _fills = new float[count]; _radii = new float[count]; _combined = new float[count]; _solids = new bool[count];
            _motion = new LiquidFallingMotion[count];
            _fillStamps = new uint[count]; _combinedStamps = new uint[count]; _solidStamps = new uint[count];
            _isolated=new bool[count];_isolatedStamps=new uint[count];
            // 在构造期触发模板初始化，首滴落水不能在 Mesh 热路径创建集合。
            _ = LiquidDropletTemplate.Vertices.Length;
            // Presentation 的三维采样步长为半个 Gameplay Cell。粗格数量不变，
            // 但落水重心移动半格时外壳仍有足够的采样点，避免一整个液团周期性缩没。
            _nodeSize = (source.ChunkSize + 12) * 2 + 1; _nodeSlice = _nodeSize * _nodeSize;
            _field = new float[_nodeSlice * _nodeSize];
            _groundDensity = new float[_field.Length]; _groundTop = new float[_field.Length];
            _ownerDistances=new float[_field.Length];_nodeOwners=new Vector3Int[_field.Length];
            _cubeSize = _nodeSize - 1; _ids = new int[_cubeSize * _cubeSize * _cubeSize];
            _masks = new byte[_ids.Length];
            _positions = new Vector3[_ids.Length]; _smoothed = new Vector3[_ids.Length];
        }

        internal bool IsCompleteFlatPlane(ElementChunkKey key)
        {
            _key = key; _base = new Vector3Int(key.X, key.Y, key.Z) * _source.ChunkSize;
            _minimum = _base - new Vector3Int(16, 16, 16);
            unchecked { _stamp++; }
            if (_stamp == 0)
            {
                System.Array.Clear(_fillStamps, 0, _fillStamps.Length);
                System.Array.Clear(_combinedStamps, 0, _combinedStamps.Length);
                System.Array.Clear(_solidStamps, 0, _solidStamps.Length); _stamp = 1;
                System.Array.Clear(_isolatedStamps,0,_isolatedStamps.Length);
                System.Array.Clear(_sheetStamps,0,_sheetStamps.Length);
                System.Array.Clear(_crownStamps,0,_crownStamps.Length);
            }
            SourceSamples = 0;
            _hasFalling = false;
            _nearStable = _source.MaximumStableRegions > 0 && _source.GetStableNeighborhoodVersion(key, _material) != 0;
            int size = _source.ChunkSize, layer = -1; float height = 0;
            for (int y = 0; y < size; y++)
            for (int z = 0; z < size; z++)
            for (int x = 0; x < size; x++)
            {
                float f = Fill(_base + new Vector3Int(x, y, z));
                if (f <= 0f) continue;
                if (layer < 0) { layer = y; height = f; }
                if (layer != y || height != f) return false;
            }
            if (layer < 0) return false;
            for (int z = -1; z <= size; z++)
            for (int x = -1; x <= size; x++)
            {
                var c = _base + new Vector3Int(x, layer, z);
                if (Fill(c) != height || !Supported(c)) return false;
            }
            float body=LiquidPuddleShape.BodyHeight(height,_minimumPuddleDepthCells);
            if(LiquidPuddleShape.CrownHeight(body)>0f)
                // Bicubic 的邻点和法线的 ±.25 Cell 差分也必须处于完整支持区。
                // 只检查顶点自身会把接缝一侧合并成 Up 法线，另一侧仍保留圆冠斜率。
                for(int z=-4;z<=size+3;z++)for(int x=-4;x<=size+3;x++)
                    if(Combined(_base+new Vector3Int(x,layer,z))!=height)return false;
            _flatHeight = layer + body + LiquidPuddleShape.CrownHeight(body);
            return true;
        }

        internal void Build(ElementChunkKey key, List<Vector3> vertices,
            List<Vector3> normals, List<Vector2> uvs, List<int> indices, bool flat,
            List<Vector4> motionData = null)
        {
            // IsCompleteFlatPlane 已开启本次缓存版本，失败后直接复用采样，避免重复读取。
            _vertices = vertices; _normals = normals; _uvs = uvs; _indices = indices;
            _motionData = motionData;
            if (flat)
            {
                int size = _source.ChunkSize;
                FlatVertex(new Vector3(0, _flatHeight, 0));
                FlatVertex(new Vector3(0, _flatHeight, size));
                FlatVertex(new Vector3(size, _flatHeight, size));
                FlatVertex(new Vector3(size, _flatHeight, 0));
                indices.Add(0); indices.Add(1); indices.Add(2);
                indices.Add(0); indices.Add(2); indices.Add(3);
                if (_motionData != null) for (int i = 0; i < 4; i++) _motionData.Add(Vector4.zero);
                return;
            }
            // 只计算拥有动态数量的局部包围体，水滩只有一层时不扫描整页高度。
            // 范围留出足够的采样 Halo，跨页连接与平滑仍使用相同全局场。
            Vector3Int occupiedMin = new Vector3Int(_source.ChunkSize,_source.ChunkSize,_source.ChunkSize);
            Vector3Int occupiedMax = new Vector3Int(-1,-1,-1);
            for(int z=0;z<_source.ChunkSize;z++) for(int y=0;y<_source.ChunkSize;y++) for(int x=0;x<_source.ChunkSize;x++)
            {
                var c=new Vector3Int(x,y,z); if(Fill(_base+c)<=0f) continue;
                occupiedMin=Vector3Int.Min(occupiedMin,c); occupiedMax=Vector3Int.Max(occupiedMax,c);
            }
            if(occupiedMax.x<0) return;
            if(occupiedMin.y==occupiedMax.y)
                using(GroundMarker.Auto())if(BuildGroundSheet(occupiedMin.y))return;
            bool allIsolated=true;
            for(int z=occupiedMin.z;z<=occupiedMax.z;z++)for(int y=occupiedMin.y;y<=occupiedMax.y;y++)
            for(int x=occupiedMin.x;x<=occupiedMax.x;x++)
            {
                var cell=_base+new Vector3Int(x,y,z);if(Fill(cell)<=0f)continue;
                if(IsIsolated(cell))EmitDroplet(cell);else allIsolated=false;
            }
            if(allIsolated)return;
            // XZ 只留三格 Halo（滤波/轮廓所需），Y 留六格以连接高速包。
            // 避免细化后对完全空的整页进行三维遍历。
            var start=Vector3Int.Max(Vector3Int.zero,
                (occupiedMin+new Vector3Int(3,0,3))*2);
            var end=Vector3Int.Min(Vector3Int.one*(_nodeSize-1),
                (occupiedMax+new Vector3Int(9,12,9))*2);
            using(FieldMarker.Auto())
            {
                AccumulateField(start, end);
                FinishGroundField(start, end);
            }
            for (int i = 0; i < _ids.Length; i++) _ids[i] = -1;
            using(VertexMarker.Auto())
            for (int z = start.z; z < end.z; z++)
            for (int y = start.y; y < end.y; y++)
            for (int x = start.x; x < end.x; x++) Vertex(x, y, z);
            using(SmoothMarker.Auto())SmoothSurface(start, end);
            start = Vector3Int.Max(start+Vector3Int.one,Vector3Int.one);
            end=Vector3Int.Min(end,Vector3Int.one*(_nodeSize-2));
            using(EdgeMarker.Auto())
            for (int z = start.z; z <= end.z; z++)
            for (int y = start.y; y <= end.y; y++)
            for (int x = start.x; x <= end.x; x++)
            { Edge(x, y, z, 0); Edge(x, y, z, 1); Edge(x, y, z, 2); }
        }

        private void FlatVertex(Vector3 p)
        {
            Vector3 local = p * _source.CellSize;
            Vector3 world = _source.Origin + (Vector3)_base * _source.CellSize + local;
            _vertices.Add(local); _normals.Add(Vector3.up); _uvs.Add(new Vector2(world.x, world.z));
        }

        private bool BuildGroundSheet(int layer)
        {
            int size=_source.ChunkSize;
            for(int z=0;z<size;z++) for(int x=0;x<size;x++)
            {
                var c=_base+new Vector3Int(x,layer,z);
                if(Fill(c)>0f && !Supported(c)) return false;
            }
            _groundLayer=layer;
            // 共用采样点缓存代替每个 Quad 重算四角；Owner 每个 Cell 判断一次，
            // 不再对 16 个细分面分别搜索 27 邻格。圆顶和侧面只增加岸边几何。
            for(int z=-1;z<=size;z++)
            {
                int run=-1;float runHeight=0f;
                for(int x=-1;x<=size+1;x++)
                {
                    float height=0f;
                    bool interior=x<size && x>=0 && z>=0 && z<size && GroundInterior(x,layer,z,out height);
                    if(interior && (run<0 || height==runHeight))
                    {if(run<0){run=x;runHeight=height;}continue;}
                    if(run>=0)
                    {
                        _shorePolygon[0]=new Vector3(run,layer+runHeight,z);
                        _shorePolygon[1]=new Vector3(run,layer+runHeight,z+1);
                        _shorePolygon[2]=new Vector3(x,layer+runHeight,z+1);
                        _shorePolygon[3]=new Vector3(x,layer+runHeight,z);
                        for(int i=0;i<4;i++)
                        {
                            // 合并内部面也从世界连续场取法线，保证与相邻细分面的光照接缝一致。
                            _shoreNormals[i]=GroundNormal(_shorePolygon[i].x,_shorePolygon[i].z,layer);
                            _shoreBoundary[i]=false;
                        }
                        EmitGround(4);run=-1;
                    }
                    if(x>size)continue;
                    if(interior){run=x;runHeight=height;continue;}
                    if(!OwnsGroundTile(x,layer,z))continue;
                    for(int dz=0;dz<4;dz++) for(int dx=0;dx<4;dx++)
                    {
                        float px=x+dx*.25f,pz=z+dz*.25f;
                        _shoreCorners[0]=GroundPoint(px,pz,layer,out _shoreValues[0]);
                        _shoreCorners[1]=GroundPoint(px,pz+.25f,layer,out _shoreValues[1]);
                        _shoreCorners[2]=GroundPoint(px+.25f,pz+.25f,layer,out _shoreValues[2]);
                        _shoreCorners[3]=GroundPoint(px+.25f,pz,layer,out _shoreValues[3]);
                        int mask=0;for(int i=0;i<4;i++)if(_shoreValues[i]>0f)mask|=1<<i;
                        if(mask==0)continue;
                        for(int i=0;i<4;i++)
                            _cornerNormals[i]=GroundNormal(_shoreCorners[i].x,_shoreCorners[i].z,layer);
                        if(mask==5 || mask==10)
                        {
                            GroundPoint(px+.125f,pz+.125f,layer,out float center);
                            if(center<=0f)
                            {
                                for(int i=0;i<4;i++)if(_shoreValues[i]>0f)
                                {
                                    ShoreIntersection(0,(i+3)&3,i);ShoreCorner(1,i);
                                    ShoreIntersection(2,i,(i+1)&3);EmitGround(3);
                                }
                                continue;
                            }
                        }
                        int count=0;for(int i=0;i<4;i++)
                        {
                            int next=(i+1)&3;if(_shoreValues[i]>0f)ShoreCorner(count++,i);
                            if((_shoreValues[i]>0f)!=(_shoreValues[next]>0f))ShoreIntersection(count++,i,next);
                        }
                        EmitGround(count);
                    }
                }
            }
            return true;
        }
        private bool GroundInterior(int x,int y,int z,out float height)
        {
            var c=_base+new Vector3Int(x,y,z);height=LiquidPuddleShape.BodyHeight(Fill(c),_minimumPuddleDepthCells);if(Fill(c)<=0f)return false;
            for(int dz=-1;dz<=1;dz++) for(int dx=-1;dx<=1;dx++)
            {
                var neighbor=c+new Vector3Int(dx,0,dz);
                if(Combined(neighbor)<=0f || LiquidPuddleShape.BodyHeight(Combined(neighbor),_minimumPuddleDepthCells)!=height
                    || !(Supported(neighbor) || Fill(neighbor)<=0f))return false;
            }
            if(LiquidPuddleShape.CrownHeight(height)<=0f)return true;
            // 支持度仍变化时保留顶面采样，不能把圆冠中间重新合并成平顶。
            if(SampleCrown(x,z)<.9999f || SampleCrown(x+1,z)<.9999f
                || SampleCrown(x,z+1)<.9999f || SampleCrown(x+1,z+1)<.9999f)return false;
            height+=LiquidPuddleShape.CrownHeight(height);
            return true;
        }
        private float SampleCrown(int x,int z)
        {
            int id=x+5+(z+5)*_crownSize;
            if(_crownStamps[id]==_stamp)return _crownSupports[id];
            float support=0f;
            for(int dz=-2;dz<=2;dz++)for(int dx=-2;dx<=2;dx++)
                if(Combined(_base+new Vector3Int(x+dx,_groundLayer,z+dz))>0f)
                    support+=LiquidPuddleShape.CrownKernel(dx,dz);
            _crownStamps[id]=_stamp;return _crownSupports[id]=Mathf.Clamp01(support);
        }
        private Vector3 GroundPoint(float x,float z,int layer,out float coverage) =>
            GroundPoint(x,z,layer,out coverage,out _);

        private Vector3 GroundPoint(float x,float z,int layer,out float coverage,out Vector2 gradient)
        {
            int id=Mathf.RoundToInt((x+2f)*4f)+Mathf.RoundToInt((z+2f)*4f)*_sheetSize;
            // 小面中心用于判断 Marching Squares 歧义，八分之一格点不写入四分之一格缓存。
            bool cacheable=Mathf.Abs(x*4f-Mathf.Round(x*4f))<.001f
                && Mathf.Abs(z*4f-Mathf.Round(z*4f))<.001f;
            if(cacheable && _sheetStamps[id]==_stamp)
            {coverage=_sheetCoverage[id];gradient=_sheetGradients[id];return _sheetPoints[id];}
            int cx=Mathf.FloorToInt(x),cz=Mathf.FloorToInt(z);float density=0f,top=0f,firstHeight=-1f;
            gradient=Vector2.zero;
            bool uniform=true;
            for(int dz=-1;dz<=1;dz++) for(int dx=-1;dx<=1;dx++)
            {
                var c=_base+new Vector3Int(cx+dx,layer,cz+dz);float fill=Combined(c);
                if(fill<=0f || Fill(c)>0f && !Supported(c))
                    continue;
                float vx=x-cx-dx-.5f,vz=z-cz-dz-.5f;
                float w=LiquidPuddleShape.Kernel(vx,vz,out Vector2 sampleGradient);
                if(w<=0f)continue;
                float height=LiquidPuddleShape.BodyHeight(fill,_minimumPuddleDepthCells);
                if(firstHeight<0f)firstHeight=height;else if(height!=firstHeight)uniform=false;
                density+=w;top+=w*height;gradient+=sampleGradient;
            }
            coverage=density-LiquidPuddleShape.CoverageThreshold;
            // 轮廓与圆肩使用同一个连续场；不再混入最近空矩形的折脊。
            float fillHeight=density>0f?(uniform?firstHeight:top/density):0f;
            float surfaceHeight=coverage>0f
                ? LiquidPuddleShape.Height(fillHeight,LiquidPuddleShape.ShoreDistance(coverage,gradient),
                    _minimumPuddleDepthCells,LiquidPuddleShape.CrownHeight(fillHeight)>0f
                        ?LiquidPuddleShape.CrownSupport(x,z,_sampleCrown):0f)
                : LiquidPuddleShape.EdgeHeightCells;
            var point=new Vector3(x,layer+surfaceHeight,z);
            if(cacheable){_sheetStamps[id]=_stamp;_sheetCoverage[id]=coverage;_sheetGradients[id]=gradient;_sheetPoints[id]=point;}
            return point;
        }
        private Vector3 GroundNormal(float x,float z,int layer)
        {
            float dx=GroundPoint(x+.25f,z,layer,out _).y-GroundPoint(x-.25f,z,layer,out _).y;
            float dz=GroundPoint(x,z+.25f,layer,out _).y-GroundPoint(x,z-.25f,layer,out _).y;
            return new Vector3(-dx*2f,1f,-dz*2f).normalized;
        }
        private bool OwnsGroundTile(int x,int y,int z)
        {
            var cell=_base+new Vector3Int(x,y,z);Vector3Int owner=cell;bool found=Fill(cell)>0f;
            float best=found?0f:float.MaxValue;
            // 液体间的交界共享同一总高度场，但每个 Tile 仅由顶部材料绘制一次。
            // 不能让 Water 的邻格 Halo 再为 Poison 占地生成共面的透明顶面。
            if(!found && _layers!=null && _layers.GetTotalFill(cell)>0f)return false;
            if(!found)for(int dz=-1;dz<=1;dz++)for(int dx=-1;dx<=1;dx++)
            {
                var candidate=cell+new Vector3Int(dx,0,dz);
                if(_layers!=null ? _layers.GetTotalFill(candidate)<=0f : Fill(candidate)<=0f)continue;
                float distance=dx*dx+dz*dz;
                if(!found || distance<best || distance==best && Earlier(candidate,owner))
                {found=true;best=distance;owner=candidate;}
            }
            if(!found)return false;
            if(Fill(owner)<=0f)return false;
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(owner,_source.ChunkSize,out var key,out _);
            return key==_key;
        }
        private void ShoreCorner(int at,int corner)
        {_shorePolygon[at]=_shoreCorners[corner];_shoreNormals[at]=_cornerNormals[corner];_shoreBoundary[at]=false;}
        private void ShoreIntersection(int at,int a,int b)
        {
            float t=_shoreValues[a]/(_shoreValues[a]-_shoreValues[b]);
            Vector3 delta=_shoreCorners[b]-_shoreCorners[a];
            Vector2 edge=new Vector2(delta.x,delta.z),gradient=Vector2.zero;
            // 沿同一网格边修正两次，邻页仍共享端点；每次都限制在边内。
            for(int step=0;step<2;step++)
            {
                Vector3 p=Vector3.LerpUnclamped(_shoreCorners[a],_shoreCorners[b],t);
                GroundPoint(p.x,p.z,_groundLayer,out float value,out gradient);
                t=LiquidPuddleShape.RefineEdge(t,value,gradient,edge);
            }
            _shorePolygon[at]=Vector3.LerpUnclamped(_shoreCorners[a],_shoreCorners[b],t);
            // 轮廓交点属于弧线底缘，不能沿顶点高度线性插值成参差不齐的高墙。
            _shorePolygon[at].y=_groundLayer+LiquidPuddleShape.EdgeHeightCells;
            Vector3 outward=new Vector3(-gradient.x,0f,-gradient.y);
            _shoreNormals[at]=outward.sqrMagnitude>1e-8f?outward.normalized:Vector3.up;
            _shoreBoundary[at]=true;
        }
        private void EmitGround(int count)
        {
            // 只在零轮廓线加一个曲线中点，网格内部边保持原顶点，避免 T-junction 裂缝。
            int rounded=0;
            for(int i=0;i<count;i++)
            {
                _roundPolygon[rounded]=_shorePolygon[i];_roundNormals[rounded]=_shoreNormals[i];
                _roundBoundary[rounded++]=_shoreBoundary[i];
                int next=(i+1)%count;if(!_shoreBoundary[i] || !_shoreBoundary[next])continue;
                Vector3 a=_shorePolygon[i],b=_shorePolygon[next];
                Vector2 pa=new Vector2(a.x,a.z),pb=new Vector2(b.x,b.z),p=(pa+pb)*.5f;
                Vector2 low=Vector2.Min(pa,pb),high=Vector2.Max(pa,pb),gradient=Vector2.zero;
                for(int step=0;step<2;step++)
                {
                    GroundPoint(p.x,p.y,_groundLayer,out float value,out gradient);
                    p=LiquidPuddleShape.ProjectContour(p,value,gradient,low,high);
                }
                _roundPolygon[rounded]=new Vector3(p.x,_groundLayer+LiquidPuddleShape.EdgeHeightCells,p.y);
                Vector3 outward=new Vector3(-gradient.x,0f,-gradient.y);
                _roundNormals[rounded]=outward.sqrMagnitude>1e-8f?outward.normalized:_shoreNormals[i];
                _roundBoundary[rounded++]=true;
            }
            count=rounded;
            for(int i=0;i<count;i++)
            {
                _shorePolygon[i]=_roundPolygon[i];_shoreNormals[i]=_roundNormals[i];_shoreBoundary[i]=_roundBoundary[i];
            }
            int first=_vertices.Count;
            for(int i=0;i<count;i++)
            {
                FlatVertex(_shorePolygon[i]);_normals[_normals.Count-1]=_shoreNormals[i];
                _motionData?.Add(Vector4.zero);
            }
            LiquidPuddleShape.Triangulate(_shorePolygon,count,first,_indices,_shoreTriangleScratch);
            for(int i=0;i<count;i++)
            {
                int next=(i+1)%count;if(!_shoreBoundary[i] || !_shoreBoundary[next])continue;
                Vector3 a=_shorePolygon[i],b=_shorePolygon[next];
                Vector3 normal=Vector3.Cross(b-a,Vector3.up).normalized;
                int side=_vertices.Count;
                FlatVertex(a);FlatVertex(new Vector3(a.x,_groundLayer-.01f,a.z));
                FlatVertex(new Vector3(b.x,_groundLayer-.01f,b.z));FlatVertex(b);
                for(int v=0;v<4;v++){_normals[side+v]=normal;_motionData?.Add(Vector4.zero);}
                _indices.Add(side);_indices.Add(side+1);_indices.Add(side+2);
                _indices.Add(side);_indices.Add(side+2);_indices.Add(side+3);
            }
        }

        private int Cache(Vector3Int c) => c.x - _minimum.x + (c.y - _minimum.y) * _cacheSize
            + (c.z - _minimum.z) * _cacheSlice;
        private float Fill(Vector3Int c)
        {
            int i = Cache(c);
            if (_fillStamps[i] != _stamp)
            {
                bool own=_source.TryGetDynamicQuantity(c,_material,out uint q);
                if(_layers!=null)
                    _fills[i]=own && _layers.TryGetSurfaceFill(c,_material,out float total)?total:0f;
                else _fills[i]=own && _source.IsSurfaceMaterialDominant(c,_material)
                    ?Mathf.Clamp01(q/(float)_source.CellCapacityGmu):0f;
                _fillStamps[i] = _stamp; SourceSamples++;
                // 位移半格时也须跨过至少一个粗 Node；半径下限避免少量落水闪没。
                // 球体 V=4πr³/3；使用真实填充的立方根。最小半径仅确保微量包有采样，
                // 不再用厚地面圆冠与被粗格采样压扁的空中外壳组合出落地突然增量。
                _radii[i]=_fills[i]>0f?Mathf.Max(.32f,Mathf.Pow(_fills[i]*3f/(4f*Mathf.PI),1f/3f)*1.2f):0f;
                _motion[i] = default;
                if (_fills[i] > 0f && _motionSource != null && !Supported(c))
                    _motionSource.TryGetFallingMotion(c, _material, out _motion[i]);
                if (_motion[i].Velocity > 0f) _hasFalling = true;
            }
            return _fills[i];
        }
        private bool Solid(Vector3Int c)
        {
            int i = Cache(c);
            if (_solidStamps[i] != _stamp)
            { _solids[i] = _source.IsSolidForSurface(c); _solidStamps[i] = _stamp; }
            return _solids[i];
        }

        private bool Supported(Vector3Int c) => Solid(c+Vector3Int.down)
            || _layers!=null && _layers.GetTotalFill(c+Vector3Int.down)>=.999f;

        private bool IsIsolated(Vector3Int cell)
        {
            int id=Cache(cell);if(_isolatedStamps[id]==_stamp)return _isolated[id];
            _isolatedStamps[id]=_stamp;bool alone=!Supported(cell);
            for(int z=-1;alone && z<=1;z++)for(int y=-1;alone && y<=1;y++)for(int x=-1;alone && x<=1;x++)
            {
                if(x==0 && y==0 && z==0)continue;
                var neighbor=cell+new Vector3Int(x,y,z);
                if(_layers!=null ? _layers.GetTotalFill(neighbor)>0f : Combined(neighbor)>0f)alone=false;
            }
            // 同列的高速包由 SDF 连接，不能误当成孤立球绕过水柱路径。
            for(int gap=2;alone && gap<=6;gap++)
                if(_source.TryGetDynamicQuantity(cell+Vector3Int.up*gap,_material,out _)
                    || _source.TryGetDynamicQuantity(cell+Vector3Int.down*gap,_material,out _))alone=false;
            return _isolated[id]=alone;
        }

        private void EmitDroplet(Vector3Int cell)
        {
            int first=_vertices.Count;LiquidFallingMotion motion=_motion[Cache(cell)];
            // 补偿有限 Triangle 对单位球的约2%体积损失；r³ 与 GMU 成正比。
            float radius=Mathf.Pow(Fill(cell)*3f/(4f*Mathf.PI),1f/3f)*1.01f*_source.CellSize;
            Vector3 center=((Vector3)(cell-_base)+Vector3.one*.5f-Vector3.up*motion.OffsetCells)*_source.CellSize;
            Vector4 data=motion.Velocity>0f?new Vector4(motion.Velocity,motion.Acceleration,motion.SafeDistance,motion.SampleTime):Vector4.zero;
            for(int i=0;i<LiquidDropletTemplate.Vertices.Length;i++)
            {
                Vector3 normal=LiquidDropletTemplate.Vertices[i],p=center+normal*radius;
                _vertices.Add(p);_normals.Add(normal);
                Vector3 world=_source.Origin+(Vector3)_base*_source.CellSize+p;
                _uvs.Add(new Vector2(world.x,world.z));_motionData?.Add(data);
            }
            for(int i=0;i<LiquidDropletTemplate.Indices.Length;i++)_indices.Add(first+LiquidDropletTemplate.Indices[i]);
        }

        private float Combined(Vector3Int c)
        {
            int i = Cache(c);
            if (_combinedStamps[i] != _stamp)
            {
                float dynamic = Fill(c);
                // 稳定区域只参与接边；Face Owner 仍由动态数据决定，不重复绘制整湖。
                float shared=_layers!=null && Supported(c)?_layers.GetTotalFill(c):0f;
                _combined[i] = dynamic > 0f ? dynamic : shared>0f?shared:
                    _nearStable && _source.TryGetStableSurfaceHeight(c, _material, out float h)
                    ? Mathf.Clamp01(h - c.y) : 0f;
                _combinedStamps[i] = _stamp;
            }
            return _combined[i];
        }

        private void AccumulateField(Vector3Int start, Vector3Int end)
        {
            for(int z=start.z;z<=end.z;z++) for(int y=start.y;y<=end.y;y++) for(int x=start.x;x<=end.x;x++)
            {
                int id=Node(x,y,z); _field[id]=2f;
                _groundDensity[id]=_groundTop[id]=0f;
                _ownerDistances[id]=float.MaxValue;
            }
            Vector3Int minimum=Vector3Int.FloorToInt(Position(start.x,start.y,start.z))-Vector3Int.one;
            Vector3Int maximum=Vector3Int.FloorToInt(Position(end.x,end.y,end.z))+Vector3Int.one;
            // 高速瀑布的上一个包可能在六格之外；读入上端才能让两页各自生成同一段水柱。
            maximum.y+=5;
            // Scatter：每个非空格只向半径范围内的半格 Node 贡献距离，空格整批跳过。
            // 比每个 Node 再查 27 个邻格减少调用与稳定区域查询，顺序保持全局 Z/Y/X 一致。
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(_base+minimum,_source.ChunkSize,out ElementChunkKey firstChunk,out _);
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(_base+maximum,_source.ChunkSize,out ElementChunkKey lastChunk,out _);
            // 按实际有材料的 Chunk 批量跳过空空间；不会为六格 Halo 的数千空格逐格查 Store。
            // Chunk/Cell 都使用固定全局顺序，重叠 Halo 的浮点累积保持一致。
            for(int cz=firstChunk.Z;cz<=lastChunk.Z;cz++)
            for(int cy=firstChunk.Y;cy<=lastChunk.Y;cy++)
            for(int cx=firstChunk.X;cx<=lastChunk.X;cx++)
            {
            var sourceKey=new ElementChunkKey(cx,cy,cz);
            if(!_nearStable && !_source.HasDynamicMaterialInChunk(sourceKey,_material))continue;
            var corner=new Vector3Int(cx,cy,cz)*_source.ChunkSize-_base;
            Vector3Int lowCell=Vector3Int.Max(minimum,corner);
            Vector3Int highCell=Vector3Int.Min(maximum,corner+Vector3Int.one*(_source.ChunkSize-1));
            for(int z=lowCell.z;z<=highCell.z;z++) for(int y=lowCell.y;y<=highCell.y;y++) for(int x=lowCell.x;x<=highCell.x;x++)
            {
                var localCell=new Vector3Int(x,y,z); var c=_base+localCell;
                float fill=Combined(c); if(fill<=0f) continue;
                bool dynamic=Fill(c)>0f;
                if(dynamic && IsIsolated(c))continue;
                bool grounded=Supported(c) || !dynamic;
                float radius=_radii[Cache(c)];
                float fallOffset=_motion[Cache(c)].OffsetCells;
                // 包的 Y 中心已包含不足一格的落水 Offset，Scatter 范围也必须跟着移动。
                var center=(Vector3)localCell+Vector3.one*.5f-Vector3.up*(grounded?0f:fallOffset);
                var low=Vector3Int.Max(start,Vector3Int.FloorToInt((center-Vector3.one*1.5f+Vector3.one*5.5f)*2f));
                var high=Vector3Int.Min(end,Vector3Int.CeilToInt((center+Vector3.one*1.5f+Vector3.one*5.5f)*2f));
                for(int nz=low.z;nz<=high.z;nz++) for(int ny=low.y;ny<=high.y;ny++) for(int nx=low.x;nx<=high.x;nx++)
                {
                    Vector3 p=Position(nx,ny,nz);
                    Vector3 delta=p-((Vector3)localCell+Vector3.one*.5f);
                    if(!grounded) delta.y+=fallOffset;
                    int id=Node(nx,ny,nz); float d;
                    if(dynamic)RecordOwner(id,c,delta.sqrMagnitude);
                    if(grounded)
                    {
                        // 地面薄膜只求自由面，下面延入 Solid，由地形 Depth 遮住。
                        // 同一 Cube 内同时求薄膜上下两面会把顶面压向底板。
                        // 连续覆盖率 Kernel；硬 Min 合并独立圆柱会保留每格凹凸台阶。
                        // 三次紧支撑权重在 1.45 格外为零；半格 Node 必须留住单格真孔洞。
                        float w=Mathf.Max(0f,1f-(delta.x*delta.x+delta.z*delta.z)/LiquidPuddleShape.KernelRadiusSquared);
                        w=w*w*w;
                        _groundDensity[id]+=w;
                        _groundTop[id]+=w*(p.y-y-LiquidPuddleShape.BodyHeight(fill,_minimumPuddleDepthCells));
                    }
                    else
                    {
                        d=delta.magnitude-radius;
                        // Smooth Union 让邻格接成液团，不增加 Gameplay 数量。
                        float h=Mathf.Max(.26f-Mathf.Abs(_field[id]-d),0f)/.26f;
                        _field[id]=Mathf.Min(_field[id],d)-h*h*.065f;
                    }
                }
                var middle=c+Vector3Int.down;
                int gap=2;
                int limit=_motion[Cache(c)].Velocity>0f && _motionSource!=null
                    ? Mathf.Clamp(Mathf.CeilToInt(_motion[Cache(c)].Velocity*_motionSource.MotionHorizon/_source.CellSize)+1,2,6):2;
                if(grounded || Fill(middle)>0f || Solid(middle)) continue;
                while(gap<=limit && !Solid(c+Vector3Int.down*gap) && Fill(c+Vector3Int.down*gap)<=0f)gap++;
                var lower=c+Vector3Int.down*gap;
                if(gap>limit || Solid(lower) || Fill(lower)<=0f)continue;
                // 速度越大，10 Hz 发出的相邻包间距越大。只沿无 Solid 的同一竖直列补水柱，
                // 上限六格；不会把旁边无关水滩粘起来，也不把空格记成 Gameplay 液量。
                float lowerFill=Fill(lower);
                Vector3 first=(Vector3)localCell+Vector3.one*.5f-Vector3.up*fallOffset;
                Vector3 last=(Vector3)localCell+Vector3.one*.5f-Vector3.up*(gap+_motion[Cache(lower)].OffsetCells);
                if(Solid(lower+Vector3Int.down)) last.y=lower.y-_base.y+Mathf.Min(.5f,Mathf.Max(.08f,lowerFill));
                float neck=Mathf.Min(radius,_radii[Cache(lower)])*.78f;
                low.y=Mathf.Max(start.y,Mathf.FloorToInt((last.y-1.5f+5.5f)*2f));
                for(int nz=low.z;nz<=high.z;nz++) for(int ny=low.y;ny<=high.y;ny++) for(int nx=low.x;nx<=high.x;nx++)
                {
                    Vector3 p=Position(nx,ny,nz);
                    Vector3 closest=new Vector3(first.x,Mathf.Clamp(p.y,last.y,first.y),first.z);
                    int id=Node(nx,ny,nz);float d=(p-closest).magnitude-neck;
                    // 在 Scatter 时记录最近真实液包，后续 Face/Vertex 不再搜索117邻格。
                    float upperDistance=(p-first).sqrMagnitude,lowerDistance=(p-last).sqrMagnitude;
                    RecordOwner(id,upperDistance<=lowerDistance?c:lower,Mathf.Min(upperDistance,lowerDistance));
                    float h=Mathf.Max(.26f-Mathf.Abs(_field[id]-d),0f)/.26f;
                    _field[id]=Mathf.Min(_field[id],d)-h*h*.065f;
                }
            }
            }
        }

        private void FinishGroundField(Vector3Int start, Vector3Int end)
        {
            for(int z=start.z;z<=end.z;z++) for(int y=start.y;y<=end.y;y++) for(int x=start.x;x<=end.x;x++)
            {
                int id=Node(x,y,z); float density=_groundDensity[id];
                if(density>0f)
                    _field[id]=Mathf.Min(_field[id],Mathf.Max((LiquidPuddleShape.CoverageThreshold-density)*.6f,_groundTop[id]/density));
            }
        }

        private static bool Earlier(Vector3Int candidate,Vector3Int current) =>
            candidate.x<current.x || candidate.x==current.x
                && (candidate.y<current.y || candidate.y==current.y && candidate.z<current.z);

        private void RecordOwner(int node,Vector3Int cell,float distance)
        {
            float best=_ownerDistances[node];
            if(distance<best-.000001f || Mathf.Abs(distance-best)<=.000001f && Earlier(cell,_nodeOwners[node]))
            {_ownerDistances[node]=distance;_nodeOwners[node]=cell;}
        }

        private int Node(int x, int y, int z) => x + y * _nodeSize + z * _nodeSlice;
        private int Cube(int x, int y, int z) => x + y * _cubeSize + z * _cubeSize * _cubeSize;
        // 采样节点位于全局 Cell 中心；相邻 Chunk 的 Halo 节点完全重合。
        // 空中流体做粗外壳，支撑水滩由 Ground Sheet 独立保持连续岸线。
        private static Vector3 Position(int x, int y, int z) =>
            new Vector3(x*.5f-5.5f,y*.5f-5.5f,z*.5f-5.5f);

        private void Vertex(int x, int y, int z)
        {
            int mask = 0;
            for (int c = 0; c < 8; c++)
            {
                float value = _field[Node(x + (c & 1), y + ((c >> 1) & 1), z + ((c >> 2) & 1))];
                _corners[c] = value; if (value < 0f) mask |= 1 << c;
            }
            if (mask == 0 || mask == 255) return;
            Vector3 sum = Vector3.zero; int count = 0;
            for (int c = 0; c < 8; c++)
            for (int axis = 0; axis < 3; axis++)
            {
                int bit = 1 << axis; if ((c & bit) != 0) continue;
                int other = c | bit; if ((_corners[c] < 0f) == (_corners[other] < 0f)) continue;
                Vector3 p = new Vector3(c & 1, (c >> 1) & 1, (c >> 2) & 1);
                p[axis] += _corners[c] / (_corners[c] - _corners[other]); sum += p; count++;
            }
            Vector3 position = (Position(x, y, z) + sum / count*.5f) * _source.CellSize;
            // Trilinear 场梯度指向外侧，用来平滑表面法线；无逐面法线复制。
            Vector3 n = new Vector3(
                _corners[1]+_corners[3]+_corners[5]+_corners[7]-_corners[0]-_corners[2]-_corners[4]-_corners[6],
                _corners[2]+_corners[3]+_corners[6]+_corners[7]-_corners[0]-_corners[1]-_corners[4]-_corners[5],
                _corners[4]+_corners[5]+_corners[6]+_corners[7]-_corners[0]-_corners[1]-_corners[2]-_corners[3]);
            int cube = Cube(x, y, z);
            int id = _vertices.Count; _ids[cube] = id;
            _masks[cube] = (byte)mask; _positions[cube] = position;
            _vertices.Add(position); _normals.Add(n.sqrMagnitude > .000001f ? n.normalized : Vector3.up);
            Vector3 world = _source.Origin + (Vector3)_base * _source.CellSize + position;
            _uvs.Add(new Vector2(world.x, world.z));
            if(_motionData!=null)
            {
                LiquidFallingMotion selected=default;float best=float.MaxValue;
                for(int corner=0;_hasFalling && corner<8;corner++)
                {
                    int node=Node(x+(corner&1),y+((corner>>1)&1),z+((corner>>2)&1));
                    if(_corners[corner]>=0f || _ownerDistances[node]==float.MaxValue)continue;
                    Vector3Int owner=_nodeOwners[node];LiquidFallingMotion motion=_motion[Cache(owner)];
                    Vector3 center=((Vector3)(owner-_base)+Vector3.one*.5f-Vector3.up*motion.OffsetCells)*_source.CellSize;
                    float distance=(position-center).sqrMagnitude;
                    if(distance<best){best=distance;selected=motion;}
                }
                _motionData.Add(selected.Velocity>0f
                    ?new Vector4(selected.Velocity,selected.Acceleration,selected.SafeDistance,selected.SampleTime):Vector4.zero);
            }
        }

        private void SmoothSurface(Vector3Int start, Vector3Int end)
        {
            // Taubin 的正/负两步过滤高频台阶，同时抵消单纯 Laplacian 的持续收缩。
            // 仅在脏 Mesh 构建时执行，不新增 Solver Tick；连接关系不变，真实孔洞不会填成水。
            // 所有 Halo 顶点也参与，页边界的同一个顶点能读取相同两圈邻域。
            // 正/负系数保持相近，抵消重采样拓扑改变时的曲率收缩。
            // 原 .65/-.45 在只有少数 Node 的空中液包上会明显缩小体积。
            SmoothPass(start, end, _positions, _smoothed, .5f);
            SmoothPass(start, end, _smoothed, _positions, -.53f);
            for (int z = start.z; z < end.z; z++)
            for (int y = start.y; y < end.y; y++)
            for (int x = start.x; x < end.x; x++)
            {
                int cube = Cube(x, y, z), id = _ids[cube];
                if (id < 0) continue;
                Vector3 original = _vertices[id], delta = _positions[cube] - original;
                var cell = _base + Vector3Int.FloorToInt(original / _source.CellSize);
                // 地面薄膜仅有几厘米厚，邻接平均混入向下的侧壁会把自由面拖入地板，
                // 再被 Depth 裁成台阶/缺口。支撑附近仅修正 XZ 轮廓，保留采样得到的高度。
                // 空中液团没有 Solid 支撑，仍可进行三维圆润处理。
                if (Solid(cell) || Solid(cell + Vector3Int.down)) delta.y = 0f;
                // 允许约三分之一格的显示修正，不让轮廓滤波掩盖 Gameplay 的大缺口。
                Vector3 p = original + Vector3.ClampMagnitude(delta, _source.CellSize * .35f);
                if(!Solid(cell) && !Supported(cell))
                    p=ProjectToField(x,y,z,p/_source.CellSize)*_source.CellSize;
                _vertices[id] = p;
                Vector3 world = _source.Origin + (Vector3)_base * _source.CellSize + p;
                _uvs[id] = new Vector2(world.x, world.z);
            }
        }

        private Vector3 ProjectToField(int x,int y,int z,Vector3 point)
        {
            // Surface Nets 的边交点平均和 Taubin 均可能落入 SDF 内部。
            // 沿 Trilinear 场的梯度做两步 Newton 投影，把圆润顶点放回零面；
            // 限制在原 Cube 内，不改变拓扑，也不查询邻粒子/分配临时数组。
            Vector3 origin=Position(x,y,z),t=(point-origin)*2f;
            t=Vector3.Max(Vector3.zero,Vector3.Min(Vector3.one,t));
            for(int step=0;step<2;step++)
            {
                float value=0f;Vector3 gradient=Vector3.zero;
                for(int corner=0;corner<8;corner++)
                {
                    int cx=corner&1,cy=(corner>>1)&1,cz=(corner>>2)&1;
                    float vx=cx==0?1f-t.x:t.x,vy=cy==0?1f-t.y:t.y,vz=cz==0?1f-t.z:t.z;
                    float v=_field[Node(x+cx,y+cy,z+cz)];value+=v*vx*vy*vz;
                    gradient+=new Vector3((cx==0?-1f:1f)*vy*vz,
                        (cy==0?-1f:1f)*vx*vz,(cz==0?-1f:1f)*vx*vy)*v;
                }
                float norm=gradient.sqrMagnitude;if(norm<1e-8f)break;
                t=Vector3.Max(Vector3.zero,Vector3.Min(Vector3.one,t-gradient*(value/norm)));
            }
            return origin+t*.5f;
        }

        private void SmoothPass(Vector3Int start, Vector3Int end,
            Vector3[] source, Vector3[] target, float factor)
        {
            for (int z = start.z; z < end.z; z++)
            for (int y = start.y; y < end.y; y++)
            for (int x = start.x; x < end.x; x++)
            {
                int cube = Cube(x, y, z);
                if (_ids[cube] < 0) continue;
                Vector3 sum = Vector3.zero; int count = 0;
                for (int axis = 0; axis < 3; axis++)
                for (int side = -1; side <= 1; side += 2)
                {
                    int nx = x + (axis == 0 ? side : 0);
                    int ny = y + (axis == 1 ? side : 0);
                    int nz = z + (axis == 2 ? side : 0);
                    if (nx < start.x || ny < start.y || nz < start.z
                        || nx >= end.x || ny >= end.y || nz >= end.z) continue;
                    int other = Cube(nx, ny, nz);
                    if (_ids[other] < 0) continue;
                    int face = axis == 0 ? (side < 0 ? 85 : 170)
                        : axis == 1 ? (side < 0 ? 51 : 204) : (side < 0 ? 15 : 240);
                    int signs = _masks[cube] & face;
                    // 共享 Face 必须穿过零面。仅凭“另一 Cube 有顶点”会跨过两片分开的水面。
                    if (signs == 0 || signs == face) continue;
                    sum += source[other]; count++;
                }
                target[cube] = count == 0 ? source[cube]
                    : source[cube] + (sum / count - source[cube]) * factor;
            }
        }

        private void Edge(int x, int y, int z, int axis)
        {
            float low = _field[Node(x,y,z)], high = _field[Node(x+(axis==0?1:0),y+(axis==1?1:0),z+(axis==2?1:0))];
            if ((low < 0f) == (high < 0f)) return;
            int a,b,c,d;
            if (axis == 0) { a=Cube(x,y-1,z-1); b=Cube(x,y,z-1); c=Cube(x,y,z); d=Cube(x,y-1,z); }
            else if (axis == 1) { a=Cube(x-1,y,z-1); b=Cube(x-1,y,z); c=Cube(x,y,z); d=Cube(x,y,z-1); }
            else { a=Cube(x-1,y-1,z); b=Cube(x,y-1,z); c=Cube(x,y,z); d=Cube(x-1,y,z); }
            a=_ids[a]; b=_ids[b]; c=_ids[c]; d=_ids[d];
            if (a<0 || b<0 || c<0 || d<0) return;
            int crossing=low<0f?Node(x,y,z):Node(x+(axis==0?1:0),y+(axis==1?1:0),z+(axis==2?1:0));
            if(_ownerDistances[crossing]==float.MaxValue)return;
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(_nodeOwners[crossing],_source.ChunkSize,
                out ElementChunkKey owner,out _);
            if(owner!=_key)return;
            // 符号决定外侧方向，防止 Cull Back 造成随机缺片。
            if (low<0f) { _indices.Add(a);_indices.Add(b);_indices.Add(c);_indices.Add(a);_indices.Add(c);_indices.Add(d); }
            else { _indices.Add(a);_indices.Add(c);_indices.Add(b);_indices.Add(a);_indices.Add(d);_indices.Add(c); }
        }

    }
}
