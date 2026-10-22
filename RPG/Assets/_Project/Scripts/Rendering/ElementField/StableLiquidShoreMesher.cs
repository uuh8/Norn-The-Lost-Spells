using System.Collections.Generic;
using Game.ElementField;
using UnityEngine;

namespace Game.Rendering
{
    /// <summary>
    /// 稳定水体只在岸线拓扑变化时生成连续轮廓。内部仍合并水平行段，避免把深水
    /// 变成三维粒子或每帧重建整湖；覆盖率 Kernel 与动态薄膜相同，交接后岸线不退回台阶。
    /// </summary>
    internal sealed class StableLiquidShoreMesher
    {
        private readonly HashSet<Vector2Int> _occupied;
        private readonly Dictionary<Vector2Int,float> _crownCache;
        private readonly System.Func<int,int,float> _sampleCrown;
        private readonly int _maximumCrownSamples;
        private readonly Vector3[] _polygon = new Vector3[8];
        private readonly Vector3[] _corners = new Vector3[4];
        private readonly bool[] _boundary = new bool[8];
        private readonly Vector3[] _roundPolygon=new Vector3[8];
        private readonly bool[] _roundBoundary=new bool[8];
        private readonly int[] _triangleScratch=new int[8];
        private readonly float[] _values = new float[4];
        private readonly int _maximumColumns, _maximumVertices;
        private Vector3 _origin;
        private float _cellSize, _viewX, _viewZ;
        private List<Vector3> _vertices, _normals;
        private List<Vector2> _uvs;
        private List<Vector4> _shapeGradients;
        private List<int> _triangles;
        private bool _exhausted;
        private float _rimDepth;

        internal StableLiquidShoreMesher(int maximumColumns, int maximumVertices)
        {
            _maximumColumns=maximumColumns; _maximumVertices=maximumVertices;
            _occupied=new HashSet<Vector2Int>(maximumColumns);
            _maximumCrownSamples=maximumColumns*4;
            _crownCache=new Dictionary<Vector2Int,float>(_maximumCrownSamples);
            _sampleCrown=SampleCrown;
        }

        internal bool Build(StableLiquidSurfacePatch[] patches,int start,int end,Vector3 origin,
            float cellSize,float viewX,float viewZ,List<Vector3> vertices,List<Vector3> normals,
            List<Vector2> uvs,List<int> triangles,float rimDepthCells=0f,
            List<Vector4> shapeGradients=null)
        {
            _origin=origin;_cellSize=cellSize;_viewX=viewX;_viewZ=viewZ;
            _vertices=vertices;_normals=normals;_uvs=uvs;_triangles=triangles;
            _shapeGradients=shapeGradients;
            _exhausted=false;
            _rimDepth=Mathf.Max(0f,rimDepthCells);
            _occupied.Clear();
            _crownCache.Clear();
            for(int i=start;i<end;i++)
            {
                Bounds(patches[i],out int left,out int right,out int bottom,out int top);
                for(int z=bottom;z<top;z++) for(int x=left;x<right;x++)
                {
                    if(_occupied.Count==_maximumColumns && !_occupied.Contains(new Vector2Int(x,z))) return false;
                    _occupied.Add(new Vector2Int(x,z));
                }
            }
            for(int i=start;i<end;i++)
            {
                Bounds(patches[i],out int left,out int right,out int bottom,out int top);
                for(int z=bottom;z<top;z++)
                {
                    int run=-1;
                    for(int x=left;x<=right;x++)
                    {
                        bool interior=x<right && Interior(new Vector2Int(x,z));
                        if(interior) { if(run<0) run=x; continue; }
                        if(run>=0) { Rectangle(run,x,z,z+1);run=-1;if(_exhausted)return false; }
                        if(x==right) continue;
                        var owner=new Vector2Int(x,z);
                        Tile(owner);
                        // 空邻格只提供圆润岸线的少量外沿。按全局最小邻格选 Owner，
                        // 同一 Tile 不因多个行段或 Chunk 接触重复绘制透明面。
                        for(int dz=-1;dz<=1;dz++) for(int dx=-1;dx<=1;dx++)
                        {
                            var candidate=owner+new Vector2Int(dx,dz);
                            if(!_occupied.Contains(candidate) && OwnsEmpty(candidate,owner)) Tile(candidate);
                        }
                        if(_exhausted || vertices.Count>_maximumVertices-64) return false;
                    }
                }
            }
            return true;
        }

        private void Bounds(StableLiquidSurfacePatch patch,out int left,out int right,out int bottom,out int top)
        {
            left=Mathf.RoundToInt((patch.Center.x-patch.SizeXZ.x*.5f-_origin.x)/_cellSize);
            right=Mathf.RoundToInt((patch.Center.x+patch.SizeXZ.x*.5f-_origin.x)/_cellSize);
            bottom=Mathf.RoundToInt((patch.Center.z-patch.SizeXZ.y*.5f-_origin.z)/_cellSize);
            top=Mathf.RoundToInt((patch.Center.z+patch.SizeXZ.y*.5f-_origin.z)/_cellSize);
        }
        private bool Interior(Vector2Int c)
        {
            for(int z=-1;z<=1;z++) for(int x=-1;x<=1;x++)
                if(!_occupied.Contains(c+new Vector2Int(x,z))) return false;
            return _rimDepth<=0f || SampleCrown(c.x,c.y)>.9999f
                && SampleCrown(c.x+1,c.y)>.9999f && SampleCrown(c.x,c.y+1)>.9999f
                && SampleCrown(c.x+1,c.y+1)>.9999f;
        }
        private float SampleCrown(int x,int z)
        {
            var key=new Vector2Int(x,z);
            if(_crownCache.TryGetValue(key,out float cached))return cached;
            float support=0f;
            for(int dz=-2;dz<=2;dz++)for(int dx=-2;dx<=2;dx++)
                if(_occupied.Contains(new Vector2Int(x+dx,z+dz)))
                    support+=LiquidPuddleShape.CrownKernel(dx,dz);
            support=Mathf.Clamp01(support);
            // 分散小岛可能产生很多 Halo 粗点；超缓存预算直接计算，不让 Dictionary 扩容。
            if(_crownCache.Count<_maximumCrownSamples)_crownCache.Add(key,support);
            return support;
        }
        private bool OwnsEmpty(Vector2Int tile,Vector2Int owner)
        {
            for(int z=-1;z<=1;z++) for(int x=-1;x<=1;x++)
            {
                var c=tile+new Vector2Int(x,z);
                if(_occupied.Contains(c) && (c.x<owner.x || c.x==owner.x && c.y<owner.y)) return false;
            }
            return true;
        }
        private float Coverage(float px,float pz) => Coverage(px,pz,out _,out _);

        private float Coverage(float px,float pz,out float shoreDistance,out Vector2 gradient)
        {
            int cx=Mathf.FloorToInt(px),cz=Mathf.FloorToInt(pz);float density=0f;
            gradient=Vector2.zero;
            for(int z=-1;z<=1;z++) for(int x=-1;x<=1;x++)
            {
                var c=new Vector2Int(cx+x,cz+z);
                if(!_occupied.Contains(c))continue;
                float dx=px-c.x-.5f,dz=pz-c.y-.5f;
                density+=LiquidPuddleShape.Kernel(dx,dz,out Vector2 sampleGradient);
                gradient+=sampleGradient;
            }
            float coverage=density-LiquidPuddleShape.CoverageThreshold;
            shoreDistance=LiquidPuddleShape.ShoreDistance(coverage,gradient);
            return coverage;
        }

        private void Shape(float x,float z,out float rim,out float crown)
        {
            Coverage(x,z,out float distance,out _);
            rim=LiquidPuddleShape.Rim(distance);
            crown=LiquidPuddleShape.CrownSupport(x,z,_sampleCrown)*rim*rim;
        }
        private void Tile(Vector2Int cell)
        {
            for(int z=0;z<4;z++) for(int x=0;x<4;x++)
            {
                float px=cell.x+x*.25f,pz=cell.y+z*.25f;
                _corners[0]=new Vector3(px,0,pz);_corners[1]=new Vector3(px,0,pz+.25f);
                _corners[2]=new Vector3(px+.25f,0,pz+.25f);_corners[3]=new Vector3(px+.25f,0,pz);
                int mask=0;
                for(int i=0;i<4;i++)
                {
                    _values[i]=Coverage(_corners[i].x,_corners[i].z,out float distance,out _);
                    float rim=_values[i]>0f?LiquidPuddleShape.Rim(distance):0f;
                    _corners[i].y=(rim-1f+(_rimDepth>0f?LiquidPuddleShape.CrownStrength
                        *LiquidPuddleShape.CrownSupport(_corners[i].x,_corners[i].z,_sampleCrown)*rim*rim:0f))*_rimDepth;
                    if(_values[i]>0f)mask|=1<<i;
                }
                if(mask==0)continue;
                if((mask==5 || mask==10) && Coverage(px+.125f,pz+.125f)<=0f)
                {
                    for(int i=0;i<4;i++) if(_values[i]>0f)
                    {
                        _polygon[0]=Intersection((i+3)&3,i);_polygon[1]=_corners[i];
                        _polygon[2]=Intersection(i,(i+1)&3);
                        _boundary[0]=_boundary[2]=true;_boundary[1]=false;Emit(3);
                    }
                    continue;
                }
                int count=0;
                for(int i=0;i<4;i++)
                {
                    int next=(i+1)&3;
                    if(_values[i]>0f){_boundary[count]=false;_polygon[count++]=_corners[i];}
                    if((_values[i]>0f)!=(_values[next]>0f)){_boundary[count]=true;_polygon[count++]=Intersection(i,next);}
                }
                Emit(count);
            }
        }
        private Vector3 Intersection(int a,int b)
        {
            float t=_values[a]/(_values[a]-_values[b]);
            Vector3 delta=_corners[b]-_corners[a];Vector2 edge=new Vector2(delta.x,delta.z);
            for(int step=0;step<2;step++)
            {
                Vector3 sample=Vector3.LerpUnclamped(_corners[a],_corners[b],t);
                float value=Coverage(sample.x,sample.z,out _,out Vector2 gradient);
                t=LiquidPuddleShape.RefineEdge(t,value,gradient,edge);
            }
            Vector3 p=Vector3.LerpUnclamped(_corners[a],_corners[b],t);
            p.y=-_rimDepth;return p;
        }
        private void Rectangle(float left,float right,float bottom,float top)
        {
            for(int i=0;i<4;i++)_boundary[i]=false;
            _polygon[0]=new Vector3(left,0,bottom);_polygon[1]=new Vector3(left,0,top);
            _polygon[2]=new Vector3(right,0,top);_polygon[3]=new Vector3(right,0,bottom);
            for(int i=0;i<4;i++)_polygon[i].y=LiquidPuddleShape.CrownStrength*_rimDepth;
            Emit(4);
        }
        private void Emit(int count)
        {
            int rounded=0;
            for(int i=0;i<count;i++)
            {
                _roundPolygon[rounded]=_polygon[i];_roundBoundary[rounded++]=_boundary[i];
                int next=(i+1)%count;if(!_boundary[i] || !_boundary[next])continue;
                Vector2 pa=new Vector2(_polygon[i].x,_polygon[i].z),pb=new Vector2(_polygon[next].x,_polygon[next].z);
                Vector2 p=(pa+pb)*.5f,low=Vector2.Min(pa,pb),high=Vector2.Max(pa,pb);
                for(int step=0;step<2;step++)
                {
                    float value=Coverage(p.x,p.y,out _,out Vector2 gradient);
                    p=LiquidPuddleShape.ProjectContour(p,value,gradient,low,high);
                }
                _roundPolygon[rounded]=new Vector3(p.x,-_rimDepth,p.y);_roundBoundary[rounded++]=true;
            }
            count=rounded;
            for(int i=0;i<count;i++)
            {
                _polygon[i]=_roundPolygon[i];_boundary[i]=_roundBoundary[i];
            }
            // 在 Add 之前检查预算，不能先让 List 在 Update 拓扑重建中扩容，再退回行段。
            if(_vertices.Count+count+(_rimDepth>0f?count*4:0)>_maximumVertices) {_exhausted=true;return;}
            int first=_vertices.Count;
            for(int i=0;i<count;i++)
            {
                Vector3 p=_polygon[i]*_cellSize;
                float wx=p.x+_origin.x,wz=p.z+_origin.z;
                _vertices.Add(new Vector3(wx-_viewX,p.y,wz-_viewZ));
                // 与动态岸沿使用同一距离圆弧；边界法线朝外，圆肩向内逐渐朝上。
                // Unity 用 inverse-transpose 变换法线，非等比 Y 缩放也能保持正确方向。
                Shape(_polygon[i].x,_polygon[i].z,out float rim,out float crown);
                Shape(_polygon[i].x+.25f,_polygon[i].z,out float rxp,out float cxp);
                Shape(_polygon[i].x-.25f,_polygon[i].z,out float rxm,out float cxm);
                Shape(_polygon[i].x,_polygon[i].z+.25f,out float rzp,out float czp);
                Shape(_polygon[i].x,_polygon[i].z-.25f,out float rzm,out float czm);
                var slopes=new Vector4((rxp-rxm)*2f,(rzp-rzm)*2f,(cxp-cxm)*2f,(czp-czm)*2f);
                float dx=(slopes.x+LiquidPuddleShape.CrownStrength*slopes.z)*_rimDepth;
                float dz=(slopes.y+LiquidPuddleShape.CrownStrength*slopes.w)*_rimDepth;
                Vector3 normal=new Vector3(-dx,_boundary[i] && _rimDepth>0f?0f:1f,-dz);
                _normals.Add(normal.sqrMagnitude>1e-8f?normal.normalized:Vector3.up);
                // 表面纹理由 Fragment 的 World Space Triplanar 采样；UV0 因此可以保存
                // 圆肩/圆冠系数，UV2 保存两者偏导，水位变化只改 Uniform 而不重建湖面。
                _uvs.Add(_shapeGradients!=null?new Vector2(rim,crown):new Vector2(wx,wz));
                _shapeGradients?.Add(slopes);
            }
            LiquidPuddleShape.Triangulate(_polygon,count,first,_triangles,_triangleScratch);
            if(_rimDepth<=0f)return;
            for(int i=0;i<count;i++)
            {
                int next=(i+1)%count;if(!_boundary[i] || !_boundary[next])continue;
                Vector3 a=_vertices[first+i],b=_vertices[first+next];
                Vector3 lowA=a,lowB=b;lowA.y=lowB.y=-_rimDepth*_cellSize-.002f;
                Vector3 normal=Vector3.Cross(b-a,Vector3.up).normalized;
                int side=_vertices.Count;
                _vertices.Add(a);_vertices.Add(lowA);_vertices.Add(lowB);_vertices.Add(b);
                for(int j=0;j<4;j++)
                {
                    _normals.Add(normal);
                    bool bottom=j==1 || j==2;int topIndex=j<2?first+i:first+next;
                    _uvs.Add(_shapeGradients!=null?(bottom?Vector2.zero:_uvs[topIndex])
                        :new Vector2(a.x+_viewX,a.z+_viewZ));
                    _shapeGradients?.Add(bottom?Vector4.zero:_shapeGradients[topIndex]);
                }
                _triangles.Add(side);_triangles.Add(side+1);_triangles.Add(side+2);
                _triangles.Add(side);_triangles.Add(side+2);_triangles.Add(side+3);
            }
        }
    }
}
