using System;
using System.Collections.Generic;
using Game.ElementField;
using Game.Materials;
using UnityEngine;

namespace Game.Rendering
{
    /// <summary>
    /// 一批动态 Mesh 的冻结输入。Simulation 可以继续运行，邻页仍从同一份 GMU/姿态生成。
    /// 只复制驻留动态 Chunk 与稀疏稳定行段；深水不会展开成密集快照。
    /// 数组/Dictionary 在构造期预留，Capture 不创建集合或扩大容量。
    /// </summary>
    internal sealed class LiquidSurfaceFrameSnapshot : ILiquidSurfaceReadOnly,
        ILiquidMotionReadOnly, ILiquidSurfaceLayerReadOnly
    {
        private readonly ILiquidSurfaceReadOnly _source;
        private readonly ILiquidSurfaceLayerReadOnly _layers;
        private readonly ILiquidMotionReadOnly _motions;
        private readonly MaterialId _material;
        private readonly ElementChunkKey[] _keys;
        private readonly Dictionary<ElementChunkKey,int> _indices;
        private readonly ushort[] _quantities;
        private readonly float[] _fills;
        private readonly bool[] _visible,_solids,_hasMaterial;
        private readonly uint[] _versions;
        private readonly uint[] _stableNeighborhoodVersions;
        private readonly StableLiquidSurfacePatch[] _stablePatches;
        private readonly int[] _nearStablePatches;
        private readonly bool _freezesStable;
        private readonly Dictionary<Vector3Int,LiquidFallingMotion> _falling;
        private readonly int _cellCount,_maximumMotions;
        private int _count;
        private int _stablePatchCount, _nearStablePatchCount;
        private uint _version,_candidateVersion,_stableVersion;
        private ulong _total;
        private float _motionHorizon;

        internal LiquidSurfaceFrameSnapshot(ILiquidSurfaceReadOnly source,MaterialId material)
        {
            _source=source;_material=material;
            _layers=source as ILiquidSurfaceLayerReadOnly;_motions=source as ILiquidMotionReadOnly;
            int maximum=Mathf.Max(1,source.MaximumSurfaceCandidateKeys);
            _cellCount=checked(source.ChunkSize*source.ChunkSize*source.ChunkSize);
            _keys=new ElementChunkKey[maximum];_versions=new uint[maximum];_hasMaterial=new bool[maximum];
            _stableNeighborhoodVersions=new uint[maximum];
            _stablePatches=new StableLiquidSurfacePatch[Mathf.Max(1,source.MaximumStableSurfacePatches)];
            _nearStablePatches=new int[_stablePatches.Length];
            _freezesStable=source is ILiquidStableSurfaceSnapshotReadOnly;
            _indices=new Dictionary<ElementChunkKey,int>(maximum);
            int cells=checked(maximum*_cellCount);
            _quantities=new ushort[cells];_fills=new float[cells];_visible=new bool[cells];_solids=new bool[cells];
            _maximumMotions=Math.Min(cells,65536);
            _falling=new Dictionary<Vector3Int,LiquidFallingMotion>(_maximumMotions);
        }

        internal void Capture(HashSet<ElementChunkKey> focus = null)
        {
            _count=_layers!=null?_layers.CopyLiquidChunkKeys(_keys):_source.CopySurfaceChunkKeys(_material,_keys);
            if(focus!=null)
            {
                int kept=0,radius=Mathf.CeilToInt(7f/ChunkSize);
                // 只复制可见页和其必要 Halo；远处活动水不会让相机附近每批复制整张地图。
                // 遍历预分配 HashSet 的值类型 Enumerator，不创建闭包/临时邻域集合。
                for(int i=0;i<_count;i++)
                {
                    ElementChunkKey key=_keys[i];bool near=false;
                    foreach(ElementChunkKey center in focus)
                        if(Math.Abs((long)key.X-center.X)<=radius && Math.Abs((long)key.Y-center.Y)<=radius
                            && Math.Abs((long)key.Z-center.Z)<=radius){near=true;break;}
                    if(near)_keys[kept++]=key;
                }
                _count=kept;
            }
            _indices.Clear();_falling.Clear();
            _version=_source.GetDynamicSurfaceVersion(_material);
            _candidateVersion=_source.GetDynamicCandidateVersion(_material);
            _stableVersion=_source.GetStableSurfaceVersion(_material);
            _total=_source.TotalQuantity;_motionHorizon=_motions?.MotionHorizon ?? .1f;
            _stablePatchCount=_freezesStable
                ?((ILiquidStableSurfaceSnapshotReadOnly)_source).CopyAllStableSurfacePatches(_stablePatches):0;
            _nearStablePatchCount=0;
            for(int i=0;i<_stablePatchCount;i++)
            {
                if(!_stablePatches[i].HasGameplayQuantity)continue;
                bool near=focus==null;
                if(focus!=null)
                    foreach(ElementChunkKey key in focus)
                        if(Overlaps(_stablePatches[i],key,7)){near=true;break;}
                if(near)_nearStablePatches[_nearStablePatchCount++]=i;
            }
            int size=ChunkSize;
            for(int page=0;page<_count;page++)
            {
                ElementChunkKey key=_keys[page];_indices.Add(key,page);
                _versions[page]=_source.GetSurfaceChunkVersion(key,_material);
                _stableNeighborhoodVersions[page]=_source.GetStableNeighborhoodVersion(key,_material);
                _hasMaterial[page]=false;
                var start=new Vector3Int(key.X,key.Y,key.Z)*size;
                for(int z=0;z<size;z++)for(int y=0;y<size;y++)for(int x=0;x<size;x++)
                {
                    var cell=start+new Vector3Int(x,y,z);
                    int index=page*_cellCount+x+y*size+z*size*size;
                    _source.TryGetDynamicQuantity(cell,_material,out uint q);
                    _quantities[index]=(ushort)q;_hasMaterial[page]|=q>0;
                    _solids[index]=_source.IsSolidForSurface(cell);
                    if(_layers!=null)_visible[index]=_layers.TryGetSurfaceFill(cell,_material,out _fills[index]);
                    else
                    {
                        _fills[index]=q/(float)CellCapacityGmu;
                        _visible[index]=q>0 && _source.IsSurfaceMaterialDominant(cell,_material);
                    }
                    if(q>0 && _motions!=null && _falling.Count<_maximumMotions
                        && _motions.TryGetFallingMotion(cell,_material,out LiquidFallingMotion m))_falling.Add(cell,m);
                }
            }
        }

        private bool Locate(Vector3Int cell,out int index)
        {
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(cell,ChunkSize,out var key,out var local);
            if(_indices.TryGetValue(key,out int page))
            {index=page*_cellCount+local.x+local.y*ChunkSize+local.z*ChunkSize*ChunkSize;return true;}
            index=0;return false;
        }
        public Vector3 Origin=>_source.Origin;
        public float CellSize=>_source.CellSize;
        public int ChunkSize=>_source.ChunkSize;
        public int CellCapacityGmu=>_source.CellCapacityGmu;
        public int MaximumSurfaceCandidateKeys=>_keys.Length;
        public int MaximumStableRegions=>_source.MaximumStableRegions;
        public int MaximumStableSurfacePatches=>_source.MaximumStableSurfacePatches;
        public uint SurfaceVersion=>_version;
        public uint DynamicSurfaceVersion=>_version;
        public ulong TotalQuantity=>_total;
        public ulong PendingQuantity=>0;
        public float MotionHorizon=>_motionHorizon;
        public uint GetDynamicSurfaceVersion(MaterialId material)=>_version;
        public uint GetDynamicCandidateVersion(MaterialId material)=>_candidateVersion;
        public uint GetSurfaceChunkVersion(ElementChunkKey key,MaterialId material)=>
            _indices.TryGetValue(key,out int page)?_versions[page]:0;
        public bool HasDynamicMaterialInChunk(ElementChunkKey key,MaterialId material)=>
            material==_material && _indices.TryGetValue(key,out int page) && _hasMaterial[page];
        public int CopySurfaceChunkKeys(MaterialId material,ElementChunkKey[] destination)
        {
            int count=0;
            for(int i=0;i<_count && count<destination.Length;i++)if(_hasMaterial[i])destination[count++]=_keys[i];
            return count;
        }
        public int CopyLiquidChunkKeys(ElementChunkKey[] destination)
        {int count=Math.Min(_count,destination.Length);Array.Copy(_keys,destination,count);return count;}
        public bool IsSurfaceMaterialDominant(Vector3Int cell,MaterialId material)=>
            TryGetSurfaceFill(cell,material,out _);
        public bool TryGetSurfaceFill(Vector3Int cell,MaterialId material,out float totalFill)
        {
            totalFill=0f;
            if(!Locate(cell,out int index))return false;
            totalFill=_fills[index];return material==_material && _visible[index];
        }
        public float GetTotalFill(Vector3Int cell)
        {
            if(Locate(cell,out int index))return _fills[index];
            // 稳定湖底没有动态 Chunk；跨垂直页读取支撑时仍必须看到冻结的湖水。
            // 稀疏行段只描述水面和边界，不复制湖底逐格数量。
            if(!_freezesStable)return _layers?.GetTotalFill(cell) ?? 0f;
            for(int i=0;i<_nearStablePatchCount;i++)
            {
                StableLiquidSurfacePatch patch=_stablePatches[_nearStablePatches[i]];
                if(Contains(patch,cell) && !IsSolidForSurface(cell))
                    return Mathf.Clamp01((patch.Center.y-Origin.y)/CellSize-cell.y);
            }
            return 0f;
        }
        public bool TryGetDynamicQuantity(Vector3Int cell,MaterialId material,out uint q)
        {q=material==_material && Locate(cell,out int index)?_quantities[index]:0u;return q>0;}
        public bool TryGetQuantity(Vector3Int cell,MaterialId material,out uint q)=>TryGetDynamicQuantity(cell,material,out q);
        public bool TryGetAmount(Vector3Int cell,MaterialId material,out byte amount)
        {bool found=TryGetQuantity(cell,material,out uint q);amount=(byte)Math.Min(255u,q);return found;}
        public bool TryGetFallingMotion(Vector3Int cell,MaterialId material,out LiquidFallingMotion motion)
        {motion=default;return material==_material && _falling.TryGetValue(cell,out motion);}
        public bool IsSolidForSurface(Vector3Int cell)=>Locate(cell,out int index)?_solids[index]:_source.IsSolidForSurface(cell);
        public bool TryGetStableSurfaceHeight(Vector3Int cell,MaterialId material,out float height)
        {
            if(!_freezesStable)return _source.TryGetStableSurfaceHeight(cell,material,out height);
            height=0f;
            if(material!=_material || Locate(cell,out int index) && _quantities[index]>0)return false;
            for(int i=0;i<_nearStablePatchCount;i++)
            {
                StableLiquidSurfacePatch patch=_stablePatches[_nearStablePatches[i]];
                if(patch.Material!=material || !Contains(patch,cell) || IsSolidForSurface(cell))continue;
                height=(patch.Center.y-Origin.y)/CellSize;
                if(height>cell.y)return true;
            }
            return false;
        }
        public uint GetStableSurfaceVersion(MaterialId material)=>_stableVersion;
        public uint GetStableNeighborhoodVersion(ElementChunkKey key,MaterialId material)=>
            _indices.TryGetValue(key,out int page)?_stableNeighborhoodVersions[page]:0;
        public int CopyStableSurfacePatches(MaterialId material,StableLiquidSurfacePatch[] destination)
        {
            if(!_freezesStable)return _source.CopyStableSurfacePatches(material,destination);
            int count=0;
            for(int i=0;i<_stablePatchCount && count<destination.Length;i++)
                if(_stablePatches[i].Material==material && _stablePatches[i].HasGameplayQuantity)
                    destination[count++]=_stablePatches[i];
            return count;
        }
        private bool Contains(StableLiquidSurfacePatch patch,Vector3Int cell)
        {
            Vector3 p=Origin+((Vector3)cell+new Vector3(.5f,0f,.5f))*CellSize;
            return cell.y>=patch.MinimumYCells && cell.y<patch.MaximumYCells
                && p.x>=patch.Center.x-patch.SizeXZ.x*.5f && p.x<patch.Center.x+patch.SizeXZ.x*.5f
                && p.z>=patch.Center.z-patch.SizeXZ.y*.5f && p.z<patch.Center.z+patch.SizeXZ.y*.5f;
        }
        private bool Overlaps(StableLiquidSurfacePatch patch,ElementChunkKey key,int halo)
        {
            float minX=Origin.x+(key.X*ChunkSize-halo)*CellSize;
            float maxX=Origin.x+((key.X+1)*ChunkSize+halo)*CellSize;
            float minZ=Origin.z+(key.Z*ChunkSize-halo)*CellSize;
            float maxZ=Origin.z+((key.Z+1)*ChunkSize+halo)*CellSize;
            return patch.Center.x+patch.SizeXZ.x*.5f>minX && patch.Center.x-patch.SizeXZ.x*.5f<maxX
                && patch.Center.z+patch.SizeXZ.y*.5f>minZ && patch.Center.z-patch.SizeXZ.y*.5f<maxZ;
        }
        public bool IsStableSurfaceHandoffPending(MaterialId material,ElementChunkKey key)=>_source.IsStableSurfaceHandoffPending(material,key);
        public bool IsActiveStableSurfaceOverlapping(MaterialId material,ElementChunkKey key)=>_source.IsActiveStableSurfaceOverlapping(material,key);
    }
}
