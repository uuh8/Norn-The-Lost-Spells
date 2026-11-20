using System;
using System.Collections.Generic;
using Game.Materials;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Game.ElementField
{
    /// <summary>
    /// 基底按同质长方体存储，体内没有逐格对象。编辑某格时沿最长轴二分到目标，
    /// 保留未涉及的分支。叶节点的材质同时驱动逻辑查询、可见形状和 CPU BoxCollider。
    /// 此表示适于可悬空、可挖的固定固体，不参与颗粒重力。
    /// </summary>
    internal sealed class ElementTerrainVolume : IDisposable
    {
        private sealed class Node
        {
            internal BoundsInt Box;
            internal MaterialId Material;
            internal Node A, B;
            internal GameObject Geometry;
            internal bool Leaf => A == null;
            internal Node(BoundsInt box, MaterialId material) { Box = box; Material = material; }
            internal Node(BoundsInt box, Node a, Node b) { Box = box; A = a; B = b; }
        }

        // 目录与体素编辑树分开。目录包围盒可重叠，原始材料范围必须互斥。
        private sealed class Region
        {
            internal BoundsInt Bounds;
            internal Region A, B;
            internal Node Content;
        }

        private readonly ElementCellWorldProfile _profile;
        private readonly float _size;
        private readonly Vector3 _origin;
        private readonly Region _index;
        private readonly int _editChunkSize;
        private readonly Dictionary<ElementChunkKey, Region> _extraRegions = new Dictionary<ElementChunkKey, Region>();
        private readonly GameObject _geometryRoot;
        private readonly List<GameObject> _created = new List<GameObject>();
        private readonly List<GameObject> _retired = new List<GameObject>();

        internal ElementTerrainVolume(ElementCellWorldProfile profile, Transform parent, float size, int editChunkSize)
        {
            _profile = profile;
            _origin = parent.position;
            _size = size;
            _editChunkSize = editChunkSize;
            var regions = new Region[profile.RegionCount];
            for (int i = 0; i < regions.Length; i++)
            {
                ElementTerrainRegion item = profile.Region(i);
                var box = item.Cells;
                if (box.size.x <= 0 || box.size.y <= 0 || box.size.z <= 0 ||
                    (item.Material != MaterialId.Rock && item.Material != MaterialId.Soil && item.Material != MaterialId.Empty))
                    throw new ArgumentException($"Invalid cellular terrain region {i}.");
                // 要求整数端点与联合包围盒可表示。负坐标采用同样的半开区间。
                checked { _ = box.xMin + box.size.x; _ = box.yMin + box.size.y; _ = box.zMin + box.size.z; }
                regions[i] = new Region { Bounds = box, Content = new Node(box, item.Material) };
            }
            _index = regions.Length == 0 ? null : BuildIndex(regions, 0, regions.Length);
            foreach (Region region in regions)
                if (OverlapsOther(_index, region)) throw new ArgumentException("Cellular terrain base regions overlap.");

            _geometryRoot = new GameObject("Material Terrain");
            _geometryRoot.transform.SetParent(parent, false);
            _geometryRoot.SetActive(false);
            try
            {
                foreach (Region region in regions)
                {
                    if (region.Content.Material == MaterialId.Empty) continue;
                    region.Content.Geometry = MakeBox(region.Content);
                    region.Content.Geometry.SetActive(true);
                }
                _geometryRoot.SetActive(true);
                Physics.SyncTransforms();
            }
            catch { Dispose(); throw; }
        }

        private static Region BuildIndex(Region[] items, int start, int count)
        {
            if (count == 1) return items[start];
            Vector3Int min = items[start].Bounds.min, max = items[start].Bounds.max;
            for (int i = start + 1; i < start + count; i++)
            {
                min = Vector3Int.Min(min, items[i].Bounds.min);
                max = Vector3Int.Max(max, items[i].Bounds.max);
            }
            Vector3Int span = new Vector3Int(checked(max.x-min.x), checked(max.y-min.y), checked(max.z-min.z));
            int axis = LongAxis(span);
            Array.Sort(items, start, count, Comparer<Region>.Create((x, y) =>
                x.Bounds.min[axis].CompareTo(y.Bounds.min[axis])));
            int n = count / 2;
            return new Region { Bounds = new BoundsInt(min, span), A = BuildIndex(items, start, n),
                B = BuildIndex(items, start + n, count - n) };
        }

        private static bool Intersects(BoundsInt a, BoundsInt b) =>
            a.xMin < b.xMax && b.xMin < a.xMax && a.yMin < b.yMax && b.yMin < a.yMax &&
            a.zMin < b.zMax && b.zMin < a.zMax;

        private static bool OverlapsOther(Region node, Region item)
        {
            if (node == item || !Intersects(node.Bounds, item.Bounds)) return false;
            return node.Content != null || OverlapsOther(node.A, item) || OverlapsOther(node.B, item);
        }

        private static Region Find(Region r, Vector3Int p)
        {
            if (r == null || !r.Bounds.Contains(p)) return null;
            return r.Content != null ? r : Find(r.A, p) ?? Find(r.B, p);
        }

        private static MaterialId Read(Node n, Vector3Int cell)
        {
            while (!n.Leaf) n = n.A.Box.Contains(cell) ? n.A : n.B;
            return n.Material;
        }

        internal MaterialId Get(Vector3Int cell)
        {
            Region owner = Find(_index, cell);
            if (owner == null && _extraRegions.Count != 0)
            {
                ElementWorldCoordinates.GlobalCellToChunkAndLocal(cell, _editChunkSize, out var key, out _);
                _extraRegions.TryGetValue(key, out owner);
            }
            return owner == null ? MaterialId.Empty : Read(owner.Content, cell);
        }

        private static int LongAxis(Vector3Int size) =>
            size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;

        private static Node Replace(Node old, Vector3Int cell, MaterialId material)
        {
            if (old.Box.size == Vector3Int.one) return new Node(old.Box, material);
            Node a = old.A, b = old.B;
            if (old.Leaf)
            {
                int axis = LongAxis(old.Box.size);
                var cut = old.Box.min;
                cut[axis] += old.Box.size[axis] / 2;
                var first = old.Box.size;
                first[axis] = cut[axis] - old.Box.min[axis];
                a = new Node(new BoundsInt(old.Box.min, first), old.Material);
                b = new Node(new BoundsInt(cut, old.Box.max - cut), old.Material);
            }
            if (a.Box.Contains(cell)) a = Replace(a, cell, material);
            else b = Replace(b, cell, material);
            return a.Leaf && b.Leaf && a.Material == b.Material
                ? new Node(old.Box, a.Material) : new Node(old.Box, a, b);
        }

        private GameObject MakeBox(Node leaf)
        {
            bool headless = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
            var go = headless ? new GameObject() : GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                go.SetActive(false);
                if (headless) go.AddComponent<BoxCollider>();
                else if (_profile.Surface(leaf.Material) != null)
                    go.GetComponent<MeshRenderer>().sharedMaterial = _profile.Surface(leaf.Material);
                go.name = leaf.Material.ToString();
                go.layer = _profile.TerrainLayer;
                go.transform.SetParent(_geometryRoot.transform, false);
                go.transform.localPosition = ((Vector3)leaf.Box.min + (Vector3)leaf.Box.size * .5f) * _size;
                go.transform.localScale = (Vector3)leaf.Box.size * _size;
                return go;
            }
            catch { go.SetActive(false); Object.Destroy(go); throw; }
        }

        // 准备成功前逻辑根仍是旧树。复用节点直接跳过，挖一个格子的代价取决于树深度。
        private void Stage(Node old, Node next)
        {
            if (ReferenceEquals(old, next)) return;
            if (old != null && old.Geometry != null) _retired.Add(old.Geometry);
            if (next != null && next.Leaf && next.Material != MaterialId.Empty)
            {
                next.Geometry = MakeBox(next);
                _created.Add(next.Geometry);
            }
            if ((old != null && !old.Leaf) || (next != null && !next.Leaf))
            {
                Stage(old?.A, next?.A);
                Stage(old?.B, next?.B);
            }
        }

        internal bool TrySet(Vector3Int cell, MaterialId material)
        {
            var owner = Find(_index, cell);
            bool extra = owner == null;
            ElementChunkKey key = default;
            if (extra)
            {
                ElementWorldCoordinates.GlobalCellToChunkAndLocal(cell, _editChunkSize, out key, out var local);
                if (!_extraRegions.TryGetValue(key, out owner))
                {
                    if (material == MaterialId.Empty) return false;
                    var box = new BoundsInt(cell - local, Vector3Int.one * _editChunkSize);
                    if (!box.Contains(cell)) return false;
                    owner = new Region { Bounds = box, Content = new Node(box, MaterialId.Empty) };
                }
            }
            if (Read(owner.Content, cell) == material) return false;
            var next = Replace(owner.Content, cell, material);
            try
            {
                Stage(owner.Content, next);
                foreach (var go in _retired) go.SetActive(false);
                foreach (var go in _created) go.SetActive(true);
                Physics.SyncTransforms();
                owner.Content = next;
                if (extra)
                {
                    if (next.Leaf && next.Material == MaterialId.Empty) _extraRegions.Remove(key);
                    else _extraRegions[key] = owner;
                }
            }
            catch
            {
                foreach (var go in _created) { go.SetActive(false); Object.Destroy(go); }
                foreach (var go in _retired) go.SetActive(true);
                Physics.SyncTransforms();
                throw;
            }
            finally { _created.Clear(); if (owner.Content != next) _retired.Clear(); }
            foreach (var go in _retired) Object.Destroy(go);
            _retired.Clear();
            return true;
        }

        internal bool OwnsCollider(Collider hit) => _geometryRoot != null && hit.transform.parent == _geometryRoot.transform;

        // 粗格查询覆盖完整半开体积。静止巨大岩层命中一个叶节点即可，洞内会精确走到编辑叶。
        internal bool IntersectsSolid(Vector3 minimum, Vector3 size)
        {
            var lo = Vector3Int.FloorToInt((minimum - _origin) / _size);
            var hi = Vector3Int.CeilToInt((minimum + size - _origin) / _size);
            return IntersectsSolid(new BoundsInt(lo, hi-lo));
        }

        internal bool IntersectsSolid(BoundsInt box)
        {
            if (ContainsSolid(_index, box)) return true;
            if (_extraRegions.Count == 0) return false;
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(box.min, _editChunkSize, out var a, out _);
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(box.max - Vector3Int.one, _editChunkSize, out var b, out _);
            for (int z = a.Z; z <= b.Z; z++)
            for (int y = a.Y; y <= b.Y; y++)
            for (int x = a.X; x <= b.X; x++)
                if (_extraRegions.TryGetValue(new ElementChunkKey(x, y, z), out var region) &&
                    ContainsSolid(region.Content, box)) return true;
            return false;
        }

        private static bool ContainsSolid(Region r, BoundsInt box) =>
            r != null && Intersects(r.Bounds, box) && (r.Content != null ? ContainsSolid(r.Content, box)
                : ContainsSolid(r.A, box) || ContainsSolid(r.B, box));

        private static bool ContainsSolid(Node n, BoundsInt box) => Intersects(n.Box, box) &&
            (n.Leaf ? n.Material != MaterialId.Empty : ContainsSolid(n.A, box) || ContainsSolid(n.B, box));

        public void Dispose()
        {
            if (_geometryRoot == null) return;
            _geometryRoot.SetActive(false);
            Object.Destroy(_geometryRoot);
        }
    }
}
