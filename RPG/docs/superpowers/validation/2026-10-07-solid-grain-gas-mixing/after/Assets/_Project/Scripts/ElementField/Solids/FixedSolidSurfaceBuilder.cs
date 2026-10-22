using System;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 表面和 Collider 共用同一高度描述。相邻侧面只保留未被邻格覆盖的高度，
    /// Chunk 边缘读取邻 Chunk；材质 ID 写入 Color.r，颜色本身由 Rendering 决定。
    /// </summary>
    public sealed class FixedSolidSurfaceBuilder
    {
        public Vector3[] Vertices { get; }
        public Vector3[] Normals { get; }
        public Color32[] MaterialIds { get; }
        public int[] Indices { get; }
        public Bounds[] NavigationBoxes { get; }
        private readonly bool[] _navigationVisited;
        private readonly FixedSolidVolumeStore _store;
        private readonly Func<MaterialId, bool> _blocks;
        private readonly int _size;
        private readonly int _haloSize;
        private readonly uint[] _quantities;
        private readonly MaterialId[] _materials;
        private readonly Face[] _faces;
        private struct Face
        {
            internal uint Lower,Upper;
            internal MaterialId Material;
            internal bool Matches(in Face other) => Upper>0 && Lower==other.Lower && Upper==other.Upper && Material==other.Material;
        }
        public int VertexCount { get; private set; }
        public int IndexCount { get; private set; }
        public int NavigationBoxCount { get; private set; }
        public FixedSolidSurfaceBuilder(FixedSolidVolumeStore store, Func<MaterialId, bool> blocks, int maximumBoxes)
        {
            _store = store; _blocks = blocks; _size = store.ChunkSize;
            Vertices = new Vector3[checked(store.CellsPerChunk * 24)]; Normals = new Vector3[Vertices.Length];
            MaterialIds = new Color32[Vertices.Length]; Indices = new int[checked(store.CellsPerChunk * 36)];
            NavigationBoxes = new Bounds[maximumBoxes]; _navigationVisited = new bool[store.CellsPerChunk];
            _haloSize=_size+2;
            _quantities=new uint[checked(_haloSize*_haloSize*_haloSize)];
            _materials=new MaterialId[_quantities.Length]; _faces=new Face[checked(_size*_size)];
        }
        public void Build(ElementChunkKey key, float cellSize, bool blockingOnly)
        {
            VertexCount = 0; IndexCount = 0;
            FillHalo(key,blockingOnly);
            // Greedy Meshing 在二维面掩码合并同材质、同平面的矩形。数量仍保留细格，
            // 只消除平面内的冗余顶点/三角形；洞口、台阶和 Chunk 外邻格均参与去面。
            for(int direction=0;direction<6;direction++) for(int slice=0;slice<_size;slice++)
            {
                for(int v=0;v<_size;v++) for(int u=0;u<_size;u++)
                    _faces[u+v*_size]=GetFace(direction,slice,u,v);
                for(int v=0;v<_size;v++) for(int u=0;u<_size;u++)
                {
                    Face face=_faces[u+v*_size]; if(face.Upper==0) continue;
                    int width=1,height=1;
                    while(u+width<_size && face.Matches(in _faces[u+width+v*_size])) width++;
                    // 侧面的部分高度不能跨 Y 层合并，否则会把层间空隙错误封成实心墙。
                    if(direction<2 || face.Lower==0 && face.Upper==_store.CellCapacity)
                        while(v+height<_size && SameFaceRow(u,v+height,width,in face)) height++;
                    EmitFace(direction,slice,u,v,width,height,cellSize,in face);
                    for(int y=v;y<v+height;y++) for(int x=u;x<u+width;x++) _faces[x+y*_size]=default;
                }
            }
        }
        public bool BuildNavigationBoxes(ElementChunkKey key, float cellSize)
        {
            NavigationBoxCount = 0; Array.Clear(_navigationVisited, 0, _navigationVisited.Length);
            FillHalo(key,true);
            // 先合并 X/Z 等高矩形，再合并满格的 Y 层；访问标记防止重复段，真实洞口保持开放。
            for (int z = 0; z < _size; z++) for (int y = 0; y < _size; y++) for (int x = 0; x < _size; x++)
            {
                int index = x + _size * (y + _size * z); if (_navigationVisited[index]) continue;
                float height = CachedHeight(x,y,z); if (height == 0) continue;
                int end = x + 1;
                while (end < _size && !_navigationVisited[end + _size * (y + _size * z)]
                    && CachedHeight(end,y,z) == height) end++;
                int endZ = z + 1;
                while (endZ < _size && SameRectangle(x, end, y, endZ, endZ + 1, height)) endZ++;
                int endY = y + 1;
                if (height == 1f)
                    while (endY < _size && SameRectangle(x, end, endY, z, endZ, height)) endY++;
                if (NavigationBoxCount == NavigationBoxes.Length) return false;
                for (int ny = y; ny < endY; ny++) for (int nz = z; nz < endZ; nz++) for (int nx = x; nx < end; nx++)
                    _navigationVisited[nx + _size * (ny + _size * nz)] = true;
                var size = new Vector3(end - x, endY - y - 1 + height, endZ - z) * cellSize;
                NavigationBoxes[NavigationBoxCount++] = new Bounds(new Vector3(x, y, z) * cellSize + size * .5f, size);
            }
            return true;
        }
        private bool SameRectangle(int x, int end, int y, int z, int endZ, float height)
        {
            for (int nz = z; nz < endZ; nz++) for (int nx = x; nx < end; nx++)
                if (_navigationVisited[nx + _size * (y + _size * nz)]
                    || CachedHeight(nx,y,nz) != height) return false;
            return true;
        }
        private int HaloIndex(int x,int y,int z) => x+1+_haloSize*(y+1+_haloSize*(z+1));
        private float CachedHeight(int x,int y,int z) => _quantities[HaloIndex(x,y,z)]/(float)_store.CellCapacity;
        private void FillHalo(ElementChunkKey key,bool blockingOnly)
        {
            var origin=new Vector3Int(key.X,key.Y,key.Z)*_size;
            // 缓存一次 (K+2)^3 Halo；后续六个面和 Navigation 的重复邻格访问只读数组，
            // 不为每个面反复查询 Sparse Store。CPU 固定缓存不随地面大小或帧数增长。
            for(int z=-1;z<=_size;z++) for(int y=-1;y<=_size;y++) for(int x=-1;x<=_size;x++)
            {
                int index=HaloIndex(x,y,z); _quantities[index]=0; _materials[index]=MaterialId.Empty;
                for(int m=0;m<_store.MaterialCount;m++)
                {
                    var id=_store.GetMaterial(m); if(blockingOnly && !_blocks(id)) continue;
                    if(!_store.TryGetQuantity(origin+new Vector3Int(x,y,z),id,out uint quantity)) continue;
                    _quantities[index]=quantity; _materials[index]=id; break;
                }
            }
        }
        private Face GetFace(int direction,int slice,int u,int v)
        {
            int x=direction<2?u:direction<4?slice:u;
            int y=direction<2?slice:v,z=direction<2?v:direction<4?u:slice;
            int index=HaloIndex(x,y,z); uint quantity=_quantities[index]; if(quantity==0) return default;
            uint neighbor=direction==0?_quantities[HaloIndex(x,y+1,z)]:direction==1?_quantities[HaloIndex(x,y-1,z)]
                :direction==2?_quantities[HaloIndex(x+1,y,z)]:direction==3?_quantities[HaloIndex(x-1,y,z)]
                :direction==4?_quantities[HaloIndex(x,y,z+1)]:_quantities[HaloIndex(x,y,z-1)];
            if(direction==0 && quantity==_store.CellCapacity && neighbor>0
                || direction==1 && neighbor==_store.CellCapacity || direction>=2 && neighbor>=quantity) return default;
            return new Face{Material=_materials[index],Lower=direction<2?0:neighbor,Upper=direction==1?_store.CellCapacity:quantity};
        }
        private bool SameFaceRow(int u,int v,int width,in Face face)
        {
            for(int x=u;x<u+width;x++) if(!face.Matches(in _faces[x+v*_size])) return false;
            return true;
        }
        private void EmitFace(int direction,int slice,int u,int v,int width,int height,float size,in Face face)
        {
            var id=new Color32((byte)face.Material,0,0,255);
            float a=u*size,b=(u+width)*size;
            if(direction<2)
            {
                float c=v*size,d=(v+height)*size,y=(slice+(direction==0?face.Upper/(float)_store.CellCapacity:0))*size;
                if(direction==0) Quad(new Vector3(a,y,d),new Vector3(b,y,d),new Vector3(b,y,c),new Vector3(a,y,c),Vector3.up,id);
                else Quad(new Vector3(a,y,c),new Vector3(b,y,c),new Vector3(b,y,d),new Vector3(a,y,d),Vector3.down,id);
                return;
            }
            float low=(v+face.Lower/(float)_store.CellCapacity)*size;
            float top=(v+height-1+face.Upper/(float)_store.CellCapacity)*size;
            float plane=(slice+(direction==2 || direction==4?1:0))*size;
            // 顶点在 Chunk Local Space，三角形绕序与外法线一致；MeshCollider 复用同一表面。
            if(direction==2) Quad(new Vector3(plane,low,a),new Vector3(plane,top,a),new Vector3(plane,top,b),new Vector3(plane,low,b),Vector3.right,id);
            else if(direction==3) Quad(new Vector3(plane,low,b),new Vector3(plane,top,b),new Vector3(plane,top,a),new Vector3(plane,low,a),Vector3.left,id);
            else if(direction==4) Quad(new Vector3(a,low,plane),new Vector3(b,low,plane),new Vector3(b,top,plane),new Vector3(a,top,plane),Vector3.forward,id);
            else Quad(new Vector3(b,low,plane),new Vector3(a,low,plane),new Vector3(a,top,plane),new Vector3(b,top,plane),Vector3.back,id);
        }
        private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Color32 id)
        {
            int start = VertexCount;
            Vertices[VertexCount++] = a; Vertices[VertexCount++] = b;
            Vertices[VertexCount++] = c; Vertices[VertexCount++] = d;
            for (int i = start; i < VertexCount; i++) { Normals[i] = normal; MaterialIds[i] = id; }
            Indices[IndexCount++] = start; Indices[IndexCount++] = start + 1; Indices[IndexCount++] = start + 2;
            Indices[IndexCount++] = start; Indices[IndexCount++] = start + 2; Indices[IndexCount++] = start + 3;
        }
    }
}
