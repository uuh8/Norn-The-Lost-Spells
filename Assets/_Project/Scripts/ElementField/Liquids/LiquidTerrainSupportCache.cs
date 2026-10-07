using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;

namespace Game.ElementField
{
    public readonly struct LiquidTerrainSupport
    {
        public readonly float Height;
        public readonly Vector3 Normal;
        public readonly bool Valid;
        public LiquidTerrainSupport(float height, Vector3 normal, bool valid)
        { Height = height; Normal = normal; Valid = valid; }
    }

    /// <summary>缓存格中心实际Collider支撑；相接的坡脚也保留中心面的真实Normal。</summary>
    public sealed class LiquidTerrainSupportCache
    {
        private static readonly ProfilerMarker BakeMarker = new ProfilerMarker("GridLiquid.Terrain.SupportBake");
        private static readonly ProfilerMarker PresentationBakeMarker = new ProfilerMarker("GridLiquid.Terrain.PresentationSupportBake");
        private readonly Dictionary<Vector3Int, LiquidTerrainSupport> _cache;
        private readonly Dictionary<Vector3Int, LiquidTerrainSupport> _presentationCache;
        private readonly Vector3Int[] _presentationKeys;
        private readonly bool[] _presentationKeyValid;
        private readonly ElementFieldSolidBaker _baker;
        private readonly LiquidVolumeStore _store;
        private readonly Vector3 _origin;
        private readonly float _size;
        private readonly int _capacity;
        private int _remaining = 128;
        private int _presentationNext;
        public uint FallbackCount { get; private set; }
        public LiquidTerrainSupportCache(ElementFieldSolidBaker baker, LiquidVolumeStore store,
            Vector3 origin, float size, int capacity)
        { _baker = baker; _store = store; _origin = origin; _size = size; _capacity = capacity; _cache = new Dictionary<Vector3Int, LiquidTerrainSupport>(capacity);_presentationCache=new Dictionary<Vector3Int,LiquidTerrainSupport>(capacity);_presentationKeys=new Vector3Int[capacity];_presentationKeyValid=new bool[capacity]; }
        public void BeginTick() => _remaining = 128;
        public void Invalidate() { _cache.Clear();_presentationCache.Clear();_presentationNext=0; _remaining = 128; }
        public void InvalidateSupportedColumn(Vector3Int changedCell)
        {
            // 单格冰改变只影响同一 X/Z 柱的射线。支撑射线长度为 4 格，额外一格覆盖
            // Skin/边界；保留其他柱缓存，避免连续造冰让全世界液体反复重新 Raycast。
            for (int y = -1; y <= 5; y++)
            {
                Vector3Int cell = changedCell + Vector3Int.up * y;
                _cache.Remove(cell); _presentationCache.Remove(cell);
            }
        }
        public float CellBottom(Vector3Int cell)=>_origin.y+cell.y*_size;

        public bool TryGet(Vector3Int cell, out LiquidTerrainSupport support)
        {
            support = default;
            if (!_store.IsSolid(cell + Vector3Int.down) || _store.IsSolid(cell)) return false;
            if (_cache.TryGetValue(cell, out support)) return support.Valid;
            if (_remaining == 0 || _cache.Count == _capacity) { FallbackCount++; return false; }
            _remaining--;
            using (BakeMarker.Auto())
            {
                // 粗格按整个盒占用，60°坡面上的可用格底可能已高于中心支撑近两格。
                // 四角还会增加高差；覆盖4格后仍用Normal/平面一致性拒绝台阶，不能缓存成水平地板。
                Vector3 ray = _origin + new Vector3(cell.x + .5f, cell.y + .25f, cell.z + .5f) * _size;
                bool valid = _baker.TrySampleLiquidSupport(ray, _size * 4f, out float h, out Vector3 n);
                // 一格四角跨到相接的地面时，不能把整格支撑作废：那会把坡脚当水平粗格，
                // 触发聚团Reserve并永久堵住少量水。只采用中心真实命中，不平均两种Normal；
                // 显示场按真实高度/坡度分别归组，台阶仍由各自平面与SolidMask约束。
                support = new LiquidTerrainSupport(h, n, valid); _cache.Add(cell, support);
                if (!valid) FallbackCount++;
                return valid;
            }
        }

        public bool TryGetForPresentation(Vector3Int cell,out LiquidTerrainSupport support)
        {
            support=default;
            if(!_store.IsSolid(cell+Vector3Int.down) || _store.IsSolid(cell))return false;
            if(_cache.TryGetValue(cell,out support) || _presentationCache.TryGetValue(cell,out support))return support.Valid;
            // 显示不能把“本Tick预算已用完”当作水平地板，否则下一Tick会突然换高度/Normal。
            // 只读查询单独缓存真实Collider结果，不占Gameplay预算，也不提前填入Solver缓存：
            // Rendering是否可见不能改变FlowMaterial的采样成功时机、输运顺序或反应。
            using(PresentationBakeMarker.Auto())
            {
                Vector3 ray=_origin+new Vector3(cell.x+.5f,cell.y+.25f,cell.z+.5f)*_size;
                bool valid=_baker.TrySampleLiquidSupport(ray,_size*4f,out float height,out Vector3 normal);
                support=new LiquidTerrainSupport(height,normal,valid);
                // 玩家探索超过缓存容量后循环复用最早插入的槽位，避免新地点永久重复Raycast。
                // 淘汰只改变命中率；结果仍来自相同Collider。数组/Dictionary都在初始化时预留。
                if(_capacity>0)
                {
                    // 支撑局部失效会移除别的槽位；无论 Count 是否已满，覆盖槽位都先淘汰其旧键。
                    if(_presentationKeyValid[_presentationNext])_presentationCache.Remove(_presentationKeys[_presentationNext]);
                    _presentationKeyValid[_presentationNext]=true;
                    _presentationKeys[_presentationNext]=cell;
                    _presentationNext=(_presentationNext+1)%_capacity;
                    _presentationCache.Add(cell,support);
                }
                return valid;
            }
        }

        public float BaseHeight(Vector3Int cell)
        {
            Vector3Int floor = cell;
            for (int i = 0; i < 8; i++, floor += Vector3Int.down)
            {
                if (TryGet(floor, out LiquidTerrainSupport support))
                    return support.Height + (cell.y - floor.y) * _size;
                if (_store.OccupancyAt(floor + Vector3Int.down) < _store.CapacityGmu) break;
            }
            return _origin.y + cell.y * _size;
        }

        public float BaseHeightForPresentation(Vector3Int cell)
        {
            // 满格液柱的显示基底也沿同一只读缓存查找，不能从另一条路径消耗Solver预算。
            Vector3Int floor=cell;
            for(int i=0;i<8;i++,floor+=Vector3Int.down)
            {
                if(TryGetForPresentation(floor,out LiquidTerrainSupport support))return support.Height+(cell.y-floor.y)*_size;
                if(_store.OccupancyAt(floor+Vector3Int.down)<_store.CapacityGmu)break;
            }
            return _origin.y+cell.y*_size;
        }
    }
}

