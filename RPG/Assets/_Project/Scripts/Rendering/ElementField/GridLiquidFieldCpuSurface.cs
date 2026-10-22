using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Profiling;

namespace Game.Rendering
{
    /// <summary>
    /// 与GPU相同连续场的CPU参考回退。按页分批构造后整体交换，不把某页超预算
    /// 解释成液量消失；其速度明显低于GPU，用于不支持Compute/诊断与容量溢出。
    /// </summary>
    internal sealed class GridLiquidFieldCpuSurface : IDisposable
    {
        private static readonly ProfilerMarker BuildMarker=new ProfilerMarker("GridLiquid.Field.CpuBuildPage");
        private readonly GridLiquidFieldFrame _frame;
        private readonly GridLiquidFieldSampler _sampler;
        // 相邻Owner的Halo会重复采同一个全局细节点。缓存只活到本次快照完成，
        // 固定容量在初始化预留；达到上限后直接采样，不能在回退热路径扩容或漏画。
        private const int NodeCacheCapacity=131072;
        private readonly Dictionary<Vector3Int,float> _nodeCache=new Dictionary<Vector3Int,float>(NodeCacheCapacity);
        private readonly Mesh[] _meshes,_pending;
        private readonly Material _material;
        private readonly float[] _field,_weights=new float[8],_dropWeights=new float[8];
        private readonly Vector3[] _positions,_other;
        private readonly int[] _vertexIds,_activeVertices;
        private int _activeCount;
        private readonly List<Vector3> _vertices,_normals;
        private readonly List<Vector4> _first,_second;
        private readonly List<Vector2> _swell,_batchSwell;
        private readonly List<int> _indices;
        private readonly List<Vector3> _batchVertices,_batchNormals;
        private readonly List<Vector4> _batchFirst,_batchSecond;
        private readonly List<int> _batchIndices;
        // 每16个输出页合成一个Mesh；支持全部Owner而不预建数十万个Unity对象。
        private const int PagesPerMesh=16;
        private readonly int _k,_nodeSide,_cubeSide;
        private const int Resolution=2;
        private int _built,_visible,_staged;
        private bool _building;
        // 一页宽核可能耗时数百ms，页不是合适的调度单位。节点、Cube、Normal分段推进，
        // CPU目标预算2ms；每32个元素检查时钟，且单次最多8192个元素，避免空页长循环。
        private const double WorkMilliseconds=2;
        private const int MaximumWork=8192;
        private enum Stage : byte { Idle,Nodes,Vertices,SmoothA,SmoothB,Finish,Normals,Faces,Commit }
        private Stage _stage;
        private int _cursor;
        private Vector3 _lowBound,_highBound;
        private bool _pageHasInside;
        public bool IsBuilding=>_building;
        public bool HasVisibleSurface=>_visible>0;
        internal int BuildFrames { get; private set; }
        internal double BuildMilliseconds { get; private set; }
        // 回到GPU后忘掉旧CPU可见批次；以后再次回退应保留最近GPU结果，不能复活更旧CPU壳。
        public void ClearVisible()=>_visible=0;
        // 取消只读构建不改变世界；转头时不能等待旧视野的数百页，旧完整CPU壳可继续保留。
        public void CancelBuild(){_building=false;_stage=Stage.Idle;}
        public void Cancel(){CancelBuild();_visible=0;}
        internal Material Material=>_material;
        private RenderParams _draw;
        private static readonly Vector3Int[] Corners={new Vector3Int(0,0,0),new Vector3Int(1,0,0),new Vector3Int(0,1,0),new Vector3Int(1,1,0),new Vector3Int(0,0,1),new Vector3Int(1,0,1),new Vector3Int(0,1,1),new Vector3Int(1,1,1)};
        private static readonly int[] Edges={0,1,2,3,4,5,6,7,0,2,1,3,4,6,5,7,0,4,1,5,2,6,3,7};
        public GridLiquidFieldCpuSurface(GridLiquidFieldFrame frame,GridLiquidFieldSampler sampler,Material material,int pages,int layer)
        {
            _frame=frame;_sampler=sampler;_k=frame.ChunkEdge;_nodeSide=_k*Resolution+7;_cubeSide=_k*Resolution+6;
            _field=new float[_nodeSide*_nodeSide*_nodeSide];_positions=new Vector3[_cubeSide*_cubeSide*_cubeSide];_other=new Vector3[_positions.Length];_vertexIds=new int[_positions.Length];
            _activeVertices=new int[_positions.Length];
            int pageCapacity=_positions.Length;
            int pageIndices=18*_k*_k*_k*Resolution*Resolution*Resolution;
            // 当前所有自由液体都进入Surface Nets。去掉已废弃的逐格Icosphere预留，
            // 仍覆盖每Cube一个Vertex和每格18个Index，既省启动内存又不降低画面。
            _vertices=new List<Vector3>(pageCapacity);_normals=new List<Vector3>(pageCapacity);_first=new List<Vector4>(pageCapacity);_second=new List<Vector4>(pageCapacity);_indices=new List<int>(pageIndices);
            _swell=new List<Vector2>(pageCapacity);
            int batches=(pages+PagesPerMesh-1)/PagesPerMesh;
            _meshes=new Mesh[batches];_pending=new Mesh[batches];
            for(int i=0;i<batches;i++){_meshes[i]=new Mesh{name="LiquidField.CPU",indexFormat=IndexFormat.UInt32};_pending[i]=new Mesh{name="LiquidField.CPU.Pending",indexFormat=IndexFormat.UInt32};}
            int vertexCapacity=pageCapacity*PagesPerMesh;
            _batchVertices=new List<Vector3>(vertexCapacity);_batchNormals=new List<Vector3>(vertexCapacity);
            _batchFirst=new List<Vector4>(vertexCapacity);_batchSecond=new List<Vector4>(vertexCapacity);_batchIndices=new List<int>(pageIndices*PagesPerMesh);
            _batchSwell=new List<Vector2>(vertexCapacity);
            _material=new Material(material);_material.EnableKeyword("LIQUID_CPU_REFERENCE");
            _draw=new RenderParams(_material){layer=layer,shadowCastingMode=ShadowCastingMode.Off,receiveShadows=true};
        }
        public void Begin(){if(_building)return;_built=_staged=_cursor=0;BuildFrames=0;BuildMilliseconds=0;_stage=Stage.Idle;_nodeCache.Clear();ClearBatch();_building=true;}
        private void ClearBatch(){_batchVertices.Clear();_batchNormals.Clear();_batchFirst.Clear();_batchSecond.Clear();_batchIndices.Clear();_batchSwell.Clear();}
        private static int Index(int x,int y,int z,int side)=>x+side*(y+side*z);
        private Vector3 Position(int page,int x,int y,int z)
        {var key=_frame.Owners[page];return _frame.Origin+(new Vector3(key.X*_k,key.Y*_k,key.Z*_k)+new Vector3(x-2.5f,y-2.5f,z-2.5f)/Resolution)*_frame.CellSize;}
        private float Node(int x,int y,int z)=>_field[Index(x,y,z,_nodeSide)];
        private float LocalField(int x,int y,int z,Vector3 q,out Vector3 gradient)
        {
            float value=0;gradient=Vector3.zero;
            for(int i=0;i<8;i++)
            {
                var bit=Corners[i];var w=new Vector3(bit.x==1?q.x:1-q.x,bit.y==1?q.y:1-q.y,bit.z==1?q.z:1-q.z);
                float f=Node(x+bit.x,y+bit.y,z+bit.z);value+=f*w.x*w.y*w.z;
                gradient+=new Vector3((bit.x*2-1)*w.y*w.z,(bit.y*2-1)*w.x*w.z,(bit.z*2-1)*w.x*w.y)*(f*Resolution/_frame.CellSize);
            }
            return value;
        }
        private void BeginPage()
        {
            _vertices.Clear();_normals.Clear();_first.Clear();_second.Clear();_indices.Clear();_swell.Clear();Array.Fill(_vertexIds,-1);
            Bounds bounds=_frame.OwnerBounds(_built);_lowBound=_frame.Origin+bounds.min*_frame.CellSize;_highBound=_frame.Origin+bounds.max*_frame.CellSize;
            _cursor=0;_activeCount=0;_stage=Stage.Nodes;_pageHasInside=false;
        }
        private void SampleNode(int index)
        {
            int x=index%_nodeSide,y=index/_nodeSide%_nodeSide,z=index/(_nodeSide*_nodeSide);
            Vector3 p=Position(_built,x,y,z);float value=-.65f;
            if(p.x>=_lowBound.x && p.y>=_lowBound.y && p.z>=_lowBound.z && p.x<=_highBound.x && p.y<=_highBound.y && p.z<=_highBound.z)
            {
                var owner=_frame.Owners[_built];var key=new Vector3Int(owner.X*_k*Resolution+x,owner.Y*_k*Resolution+y,owner.Z*_k*Resolution+z);
                if(!_nodeCache.TryGetValue(key,out value))
                {value=_sampler.EvaluateGroundProxy(p,1);if(_nodeCache.Count<NodeCacheCapacity)_nodeCache.Add(key,value);}
            }
            _field[index]=value;
            _pageHasInside|=value>0;
        }
        private void BuildVertex(int id)
        {
            int x=id%_cubeSide,y=id/_cubeSide%_cubeSide,z=id/(_cubeSide*_cubeSide);
            Vector3 pos=Vector3.zero;int count=0;
            for(int e=0;e<24;e+=2)
            {
                Vector3Int a=Corners[Edges[e]],b=Corners[Edges[e+1]];float fa=Node(x+a.x,y+a.y,z+a.z),fb=Node(x+b.x,y+b.y,z+b.z);
                if((fa>0)==(fb>0))continue;pos+=Vector3.Lerp((Vector3)a,(Vector3)b,fa/(fa-fb));count++;
            }
            if(count==0)return;_positions[id]=Position(_built,x,y,z)+pos*(_frame.CellSize/(count*Resolution));
            _vertexIds[id]=_vertices.Count;_vertices.Add(_positions[id]);
            _activeVertices[_activeCount++]=id;
        }
        private void SmoothVertex(int id,bool first)
        {
            int x=id%_cubeSide,y=id/_cubeSide%_cubeSide,z=id/(_cubeSide*_cubeSide);
            Vector3 at=first?_positions[id]:_other[id];
            if(_vertexIds[id]>=0 && x>0 && y>0 && z>0 && x<_cubeSide-1 && y<_cubeSide-1 && z<_cubeSide-1)
            {
                Vector3 sum=Vector3.zero;int count=0;
                for(int axis=0;axis<3;axis++)for(int sign=-1;sign<=1;sign+=2)
                {int nx=x,ny=y,nz=z;if(axis==0)nx+=sign;else if(axis==1)ny+=sign;else nz+=sign;int n=Index(nx,ny,nz,_cubeSide);if(_vertexIds[n]<0)continue;sum+=first?_positions[n]:_other[n];count++;}
                if(count>0)at+=(sum/count-at)*(first?.5f:-.53f);
                Vector3 low=Position(_built,x,y,z);at=Vector3.Min(low+Vector3.one*(_frame.CellSize/Resolution),Vector3.Max(low,at));
            }
            if(first)_other[id]=at;else _positions[id]=at;
        }
        private void FinishVertex(int id)
        {
            int v=_vertexIds[id];if(v<0)return;
            int x=id%_cubeSide,y=id/_cubeSide%_cubeSide,z=id/(_cubeSide*_cubeSide);
            Vector3 p=_positions[id],low=Position(_built,x,y,z),gradient;
            // 与GPU相同的局部投影、形变和权重；分帧只改变完成时间，不改变几何公式。
            for(int pass=0;pass<3;pass++)
            {float field=LocalField(x,y,z,(p-low)*(Resolution/_frame.CellSize),out gradient);float length=gradient.sqrMagnitude;if(length>1e-6f)p-=gradient*(field/length);p=Vector3.Min(low+Vector3.one*(_frame.CellSize/Resolution),Vector3.Max(low,p));}
            LocalField(x,y,z,(p-low)*(Resolution/_frame.CellSize),out gradient);
            Vector3 normal=gradient.sqrMagnitude>1e-10f?-gradient.normalized:Vector3.up;
            _sampler.CompressGroundProxy(ref p,ref normal,1);_swell.Add(new Vector2(_sampler.LastSwellWeight,0));
            _vertices[v]=p;_normals.Add(normal);_sampler.Weights(p,1,_weights);
            _first.Add(new Vector4(_weights[0],_weights[1],_weights[2],_weights[3]));_second.Add(new Vector4(_weights[4],_weights[5],_weights[6],_weights[7]));
        }
        private void CommitPage()
        {
            int offset=_batchVertices.Count;
            _batchVertices.AddRange(_vertices);_batchNormals.AddRange(_normals);_batchFirst.AddRange(_first);_batchSecond.AddRange(_second);
            _batchSwell.AddRange(_swell);
            for(int i=0;i<_indices.Count;i++)_batchIndices.Add(_indices[i]+offset);
            if((_built+1)%PagesPerMesh==0 || _built+1==_frame.OwnerCount)
            {
                Mesh mesh=_pending[_staged++];mesh.Clear();mesh.SetVertices(_batchVertices);mesh.SetNormals(_batchNormals);
                mesh.SetUVs(0,_batchFirst);mesh.SetUVs(1,_batchSecond);mesh.SetUVs(2,_batchSwell);mesh.SetTriangles(_batchIndices,0,true);ClearBatch();
            }
        }
        private Vector3 QuadNormal(Vector3Int cell,int axis,int vertexId)
        {
            Vector3Int next=cell;next[axis]++;bool inside=Node(cell.x,cell.y,cell.z)>0;
            if(inside==(Node(next.x,next.y,next.z)>0))return Vector3.zero;
            int u=(axis+1)%3,w=(axis+2)%3;Vector3Int b=cell,c=cell,d=cell;b[u]--;c[u]--;c[w]--;d[w]--;
            int i0=_vertexIds[Index(cell.x,cell.y,cell.z,_cubeSide)],i1=_vertexIds[Index(b.x,b.y,b.z,_cubeSide)],
                i2=_vertexIds[Index(c.x,c.y,c.z,_cubeSide)],i3=_vertexIds[Index(d.x,d.y,d.z,_cubeSide)];
            if(i0<0 || i1<0 || i2<0 || i3<0)return Vector3.zero;
            Vector3 p0=_vertices[i0],p1=_vertices[i1],p2=_vertices[i2],p3=_vertices[i3];
            Vector3 normal=Vector3.zero;
            if(vertexId==i0 || vertexId==i1 || vertexId==i2)normal+=Vector3.Cross(p1-p0,p2-p0);
            if(vertexId==i0 || vertexId==i2 || vertexId==i3)normal+=Vector3.Cross(p2-p0,p3-p0);
            return inside?normal:-normal;
        }
        private void RebuildNormal(int index)
        {
            // 使用与GPU相同的最终邻接面，Halo也参与面积加权；不依赖Mesh.RecalculateNormals
            // 的内部临时分配，也不让页面边缘因缺少邻面产生不同Normal。
            int x=index%_cubeSide,y=index/_cubeSide%_cubeSide,z=index/(_cubeSide*_cubeSide);
            if(x==0 || y==0 || z==0 || x==_cubeSide-1 || y==_cubeSide-1 || z==_cubeSide-1)return;
            int id=_vertexIds[index];if(id<0)return;Vector3 normal=Vector3.zero;
            for(int axis=0;axis<3;axis++)for(int u=0;u<2;u++)for(int w=0;w<2;w++)
            {var edge=new Vector3Int(x,y,z);edge[(axis+1)%3]+=u;edge[(axis+2)%3]+=w;normal+=QuadNormal(edge,axis,id);}
            if(normal.sqrMagnitude>1e-16f)_normals[id]=normal.normalized;
        }
        private void Face(int x,int y,int z,int axis)
        {
            int nx=x,ny=y,nz=z;if(axis==0)nx++;else if(axis==1)ny++;else nz++;
            bool inside=Node(x,y,z)>0;if(inside==(Node(nx,ny,nz)>0))return;
            int a=_vertexIds[Index(x,y,z,_cubeSide)],b,c,d;
            if(axis==0){b=_vertexIds[Index(x,y-1,z,_cubeSide)];c=_vertexIds[Index(x,y-1,z-1,_cubeSide)];d=_vertexIds[Index(x,y,z-1,_cubeSide)];}
            else if(axis==1){b=_vertexIds[Index(x,y,z-1,_cubeSide)];c=_vertexIds[Index(x-1,y,z-1,_cubeSide)];d=_vertexIds[Index(x-1,y,z,_cubeSide)];}
            else{b=_vertexIds[Index(x-1,y,z,_cubeSide)];c=_vertexIds[Index(x-1,y-1,z,_cubeSide)];d=_vertexIds[Index(x,y-1,z,_cubeSide)];}
            if(a<0 || b<0 || c<0 || d<0)return;
            _indices.Add(a);_indices.Add(inside?b:c);_indices.Add(inside?c:b);_indices.Add(a);_indices.Add(inside?c:d);_indices.Add(inside?d:c);
        }
        public void UpdateAndDraw()
        {
            if(_building)
            {
                using(BuildMarker.Auto())AdvanceBuild();
            }
            for(int i=0;i<_visible;i++){_draw.worldBounds=_meshes[i].bounds;Graphics.RenderMesh(_draw,_meshes[i],0,Matrix4x4.identity);}
        }
        private void AdvanceBuild()
        {
            BuildFrames++;
            long start=System.Diagnostics.Stopwatch.GetTimestamp();int work=0,core=_k*Resolution;
            while(_building && work<MaximumWork)
            {
                if(_built==_frame.OwnerCount)
                {
                    // 所有页/批次都完成才交换，不能让新旧快照在页面边缘混用。
                    for(int i=0;i<_staged;i++){Mesh swap=_meshes[i];_meshes[i]=_pending[i];_pending[i]=swap;}
                    _visible=_staged;_building=false;break;
                }
                if(_stage==Stage.Idle)BeginPage();
                if(_stage==Stage.Commit){CommitPage();_built++;_stage=Stage.Idle;continue;}
                // 只有穿过等值面的Cube拥有Vertex。平滑、投影和Normal只遍历这些Cube，
                // 保持原升序及相同邻接几何，避免空Cube占满分帧工作上限。
                int count=_stage==Stage.Nodes?_field.Length:_stage==Stage.Faces?core*core*core:_stage==Stage.Vertices?_positions.Length:_activeCount;
                switch(_stage)
                {
                    case Stage.Nodes:SampleNode(_cursor);break;
                    case Stage.Vertices:BuildVertex(_cursor);break;
                    case Stage.SmoothA:SmoothVertex(_activeVertices[_cursor],true);break;
                    case Stage.SmoothB:SmoothVertex(_activeVertices[_cursor],false);break;
                    case Stage.Finish:FinishVertex(_activeVertices[_cursor]);break;
                    case Stage.Normals:RebuildNormal(_activeVertices[_cursor]);break;
                    case Stage.Faces:
                        int x=3+_cursor%core,y=3+_cursor/core%core,z=3+_cursor/(core*core);
                        for(int axis=0;axis<3;axis++)Face(x,y,z,axis);break;
                }
                if(++_cursor==count)
                {
                    _cursor=0;
                    // 没有正值节点就不可能有正负交界；空Halo省去完整Cube/Normal/Face扫描。
                    if(_stage==Stage.Nodes && !_pageHasInside)_stage=Stage.Commit;
                    else if(_stage==Stage.Vertices && _activeCount==0)_stage=Stage.Commit;
                    else _stage++;
                }
                // 小批量检查提供软时间预算；Unity Mesh上传/native调用仍可能让单次超过2ms。
                if((++work&31)==0 && (System.Diagnostics.Stopwatch.GetTimestamp()-start)*1000.0/System.Diagnostics.Stopwatch.Frequency>=WorkMilliseconds)break;
            }
            // 区分真正CPU工作量和Editor低帧率造成的等待；整数/浮点累计不产生GC。
            BuildMilliseconds+=(System.Diagnostics.Stopwatch.GetTimestamp()-start)*1000.0/System.Diagnostics.Stopwatch.Frequency;
        }
        private static void Release(UnityEngine.Object value)
        {if(value==null)return;if(Application.isPlaying)UnityEngine.Object.Destroy(value);else UnityEngine.Object.DestroyImmediate(value);}
        public void Dispose(){for(int i=0;i<_meshes.Length;i++){Release(_meshes[i]);Release(_pending[i]);}Release(_material);}
    }
}
