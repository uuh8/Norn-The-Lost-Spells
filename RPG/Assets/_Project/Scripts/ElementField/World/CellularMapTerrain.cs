using System;
using System.Collections.Generic;
using Game.Materials;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Game.ElementField
{
    /// <summary>把正式关卡明确选中的自然几何接到材料基底。源 TerrainData 和 Mesh 都只读；未开挖的
    /// 地图直接使用原模型。Terrain 按原 holes texel 开洞，洞内的顶面、侧面和底面由同一份材料覆盖生成。</summary>
    internal sealed partial class CellularMapTerrain : IDisposable
    {
        internal sealed class Source
        {
            internal Collider Collider;
            internal Terrain Terrain;
            internal TerrainData Heightfield;
            internal Bounds Bounds;
            internal MaterialId Material;
            internal Renderer[] Renderers;
            internal bool[] RendererEnabled;
            internal Material Surface;
            internal PatchView Active;
            internal bool NativeReplaces, TerrainDrawHeightmap;
            private Vector2[,] _rockColumns;
            private Vector2Int _rockMinimum;

            internal bool Column(Vector3Int p, Vector3 origin, float size, out float bottom, out float top)
            {
                bottom = Bounds.min.y; top = Bounds.max.y;
                float x = origin.x + (p.x + .5f) * size, z = origin.z + (p.z + .5f) * size;
                if (x < Bounds.min.x || x >= Bounds.max.x || z < Bounds.min.z || z >= Bounds.max.z) return false;
                if (Heightfield != null)
                {
                    var tile = Tile(x, z);
                    if (Heightfield.IsHole(tile.x, tile.y)) return false;
                    top = Height(x, z);
                }
                else if (Collider is MeshCollider mesh)
                {
                    if (_rockColumns == null) ReadRockColumns(mesh, origin, size);
                    var span = _rockColumns[p.z - _rockMinimum.y, p.x - _rockMinimum.x];
                    bottom = span.x; top = span.y;
                }
                return top > bottom;
            }

            internal float Height(float x, float z) => Bounds.min.y + Heightfield.GetInterpolatedHeight(
                Mathf.Clamp01((x - Bounds.min.x) / Bounds.size.x), Mathf.Clamp01((z - Bounds.min.z) / Bounds.size.z));

            internal Vector2Int Tile(float x, float z)
            {
                int n = Heightfield.holesResolution;
                return new Vector2Int(Mathf.Clamp(Mathf.FloorToInt((x - Bounds.min.x) * n / Bounds.size.x), 0, n - 1),
                    Mathf.Clamp(Mathf.FloorToInt((z - Bounds.min.z) * n / Bounds.size.z), 0, n - 1));
            }

            internal Rect TileBounds(Vector2Int tile)
            {
                if (Heightfield == null) return Rect.MinMaxRect(Bounds.min.x, Bounds.min.z, Bounds.max.x, Bounds.max.z);
                int n = Heightfield.holesResolution;
                Vector3 step = Bounds.size / n;
                return Rect.MinMaxRect(Bounds.min.x + tile.x * step.x, Bounds.min.z + tile.y * step.z,
                    Bounds.min.x + (tile.x + 1) * step.x, Bounds.min.z + (tile.y + 1) * step.z);
            }

            internal bool Contains(Vector3Int p, Vector3 origin, float size)
            {
                if (NativeReplaces) return false;
                float y = origin.y + p.y * size;
                return y < Bounds.max.y && y + size > Bounds.min.y && Column(p, origin, size, out float bottom, out float top)
                    && y < top - size * .00001f && y + size > bottom + size * .00001f;
            }

            internal void ReadRockColumns(MeshCollider mesh, Vector3 origin, float size)
            {
                var lo = Vector3Int.FloorToInt((Bounds.min - origin) / size);
                var hi = Vector3Int.CeilToInt((Bounds.max - origin) / size);
                if ((long)(hi.x - lo.x) * (hi.z - lo.z) > 262144)
                    throw new InvalidOperationException("所选岩体超过单个自然物体的采样范围。");
                var columns = new Vector2[hi.z - lo.z, hi.x - lo.x];
                // 原 prefab 可能 inactive，或是 Replica 已隐藏的旧源。临时专用 Collider 只接收直接 Raycast，
                // 完成建表立即失活，不会激活敌人、Renderer 或任何关卡 MonoBehaviour。
                MeshCollider shape = mesh;
                GameObject probe = null;
                int count = 0;
                try
                {
                    if (!mesh.enabled || !mesh.gameObject.activeInHierarchy || !mesh.gameObject.scene.IsValid())
                    {
                        probe = new GameObject("Rock column sampler") { layer = 2 };
                        probe.transform.SetPositionAndRotation(mesh.transform.position, mesh.transform.rotation);
                        probe.transform.localScale = mesh.transform.lossyScale;
                        shape = probe.AddComponent<MeshCollider>(); shape.sharedMesh = mesh.sharedMesh; shape.convex = mesh.convex;
                    }
                    Physics.SyncTransforms();
                    float lower = Bounds.min.y - size, upper = Bounds.max.y + size;
                    for (int z = lo.z; z < hi.z; z++)
                    for (int x = lo.x; x < hi.x; x++)
                    {
                        float wx = origin.x + (x + .5f) * size, wz = origin.z + (z + .5f) * size;
                        if (shape.Raycast(new Ray(new Vector3(wx, upper, wz), Vector3.down), out var a, upper - lower)
                            && shape.Raycast(new Ray(new Vector3(wx, lower, wz), Vector3.up), out var b, upper - lower)
                            && a.point.y > b.point.y)
                        { columns[z - lo.z, x - lo.x] = new Vector2(b.point.y, a.point.y); count++; }
                    }
                }
                finally { if (probe != null) { probe.SetActive(false); ReleaseObject(probe); } }
                if (count == 0) throw new InvalidOperationException("所选岩体没有可表示的封闭纵列。");
                _rockMinimum = new Vector2Int(lo.x, lo.z); _rockColumns = columns;
            }
        }

        internal static Source[] ReadSources(ElementWorldRuntime world, BoxCollider ground, Terrain terrain, string[] rocks)
        {
            var sources = new List<Source>();
            if (ground != null)
            {
                if (ground.isTrigger || !ground.enabled || ground.attachedRigidbody != null || !StableLiquidRegionAuthoring.IsAxisAligned(ground.transform))
                    throw new InvalidOperationException("作者地板必须是静态、轴向且已启用的实体 BoxCollider。");
                sources.Add(MeshSource(ground, new Bounds(ground.center, ground.size), MaterialId.Soil));
            }
            if (terrain != null)
            {
                var collider = terrain.GetComponent<TerrainCollider>();
                if (terrain.terrainData == null || collider == null || !collider.enabled || collider.terrainData != terrain.terrainData
                    || Quaternion.Angle(terrain.transform.rotation, Quaternion.identity) > .001f
                    || (terrain.transform.lossyScale - Vector3.one).sqrMagnitude > .000001f)
                    throw new InvalidOperationException("Terrain 与 TerrainCollider 必须共享同一份原始高度场，且地图坐标不能缩放或旋转。");
                var data = terrain.terrainData;
                sources.Add(new Source { Terrain = terrain, Collider = collider, Heightfield = data, Material = MaterialId.Soil,
                    TerrainDrawHeightmap = terrain.drawHeightmap, Bounds = new Bounds(terrain.transform.position + data.size * .5f, data.size) });
            }
            if (rocks != null) foreach (string path in rocks)
            {
                var node = FindMapNode(world.transform, path);
                var mesh = node != null ? node.GetComponent<MeshCollider>() : null;
                if (mesh == null || !mesh.enabled || mesh.isTrigger || mesh.attachedRigidbody != null || mesh.sharedMesh == null)
                    throw new InvalidOperationException($"天然岩体路径缺失或含移动、触发碰撞：{path}");
                sources.Add(MeshSource(mesh, mesh.sharedMesh.bounds, MaterialId.Rock));
            }
            return sources.ToArray();
        }

        internal static void CheckNativeReplacement(Source[] sources, ElementCellWorldProfile profile, Vector3 origin, float size)
        {
            foreach (var source in sources)
            {
                var lo = Vector3Int.FloorToInt((source.Bounds.min-origin)/size);
                var hi = Vector3Int.CeilToInt((source.Bounds.max-origin)/size);
                for (int i = 0; i < profile.RegionCount; i++)
                {
                    var box = profile.Region(i).Cells;
                    if (box.xMax <= lo.x || box.xMin >= hi.x || box.yMax <= lo.y || box.yMin >= hi.y || box.zMax <= lo.z || box.zMin >= hi.z) continue;
                    if (!box.Contains(lo) || !box.Contains(hi - Vector3Int.one))
                        throw new InvalidOperationException("原生地层只覆盖了所选作者几何的一部分，无法保证挖掘后碰撞的唯一归属。");
                    source.NativeReplaces = true;
                }
                if (!source.NativeReplaces && source.Collider is MeshCollider mesh) source.ReadRockColumns(mesh, origin, size);
            }
        }

        private static Transform FindMapNode(Transform world, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            Transform found = world.root.Find(path);
            if (found != null) return found;
            var scene = world.gameObject.scene;
            if (!scene.IsValid()) return null;
            int slash = path.IndexOf('/');
            string rootName = slash >= 0 ? path.Substring(0, slash) : path;
            var roots = new List<GameObject>();
            CollectSceneRoots(world, roots);
            foreach (var root in roots)
                if (root.name == rootName) return slash >= 0 ? root.transform.Find(path.Substring(slash + 1)) : root.transform;
            return null;
        }

        internal static void CollectSceneRoots(Transform world, List<GameObject> roots)
        {
            var scene = world.gameObject.scene;
            if (scene.isLoaded) { scene.GetRootGameObjects(roots); return; }
            // Awake 可能早于 sceneLoaded。此时兄弟根已经反序列化，按 Scene handle 取回它们，避免退到别的副本。
            foreach (var candidate in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (candidate.parent == null && candidate.gameObject.scene == scene) roots.Add(candidate.gameObject);
        }

        internal static void ReleaseObject(Object value)
        {
            if (value == null) return;
            if (Application.isPlaying) Object.Destroy(value); else Object.DestroyImmediate(value);
        }

        private static Source MeshSource(Collider collider, Bounds local, MaterialId material)
        {
            var t = collider.transform;
            var bounds = new Bounds(t.TransformPoint(local.center), Vector3.zero);
            for (int i = 0; i < 8; i++)
                bounds.Encapsulate(t.TransformPoint(local.center + Vector3.Scale(local.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1))));
            var renderers = collider.GetComponentsInChildren<Renderer>(true);
            var enabled = new bool[renderers.Length];
            for (int i = 0; i < enabled.Length; i++) enabled[i] = renderers[i].enabled;
            return new Source { Collider = collider, Bounds = bounds, Material = material, Renderers = renderers,
                RendererEnabled = enabled, Surface = renderers.Length > 0 ? renderers[0].sharedMaterial : null };
        }

        private readonly PatchView[] _views;
        private readonly Vector3 _origin;
        private readonly float _size;
        internal CellularMapTerrain(Source[] sources, Transform root, float size, ElementCellWorldProfile profile)
        {
            _origin = root.position; _size = size;
            _views = new PatchView[sources.Length];
            for (int i = 0; i < sources.Length; i++) _views[i] = new PatchView(sources[i], root, size, profile);
        }

        internal MaterialId GetBase(Vector3Int p)
        {
            foreach (var view in _views) if (view.Source.Contains(p, _origin, _size)) return view.Source.Material;
            return MaterialId.Empty;
        }

        internal bool OwnsCollider(Collider collider)
        {
            foreach (var view in _views) if (view.Source.Collider == collider) return true;
            return false;
        }

        internal bool Intersects(BoundsInt box)
        {
            foreach (var view in _views)
            {
                if (view.Source.NativeReplaces) continue;
                var min = Vector3Int.Max(box.min, Vector3Int.FloorToInt((view.Source.Bounds.min - _origin) / _size));
                var max = Vector3Int.Min(box.max, Vector3Int.CeilToInt((view.Source.Bounds.max - _origin) / _size));
                if (min.x >= max.x || min.y >= max.y || min.z >= max.z) continue;
                for (int z = min.z; z < max.z; z++)
                for (int x = min.x; x < max.x; x++)
                {
                    var p = new Vector3Int(x, min.y, z);
                    if (!view.Source.Column(p, _origin, _size, out var bottom, out var top)) continue;
                    int low = Mathf.Max(min.y, Mathf.FloorToInt((bottom - _origin.y) / _size));
                    int high = Mathf.Min(max.y, Mathf.CeilToInt((top - _origin.y) / _size));
                    for (p.y = low; p.y < high; p.y++) if (!view.Edits.TryGetValue(p, out var mat) || mat != MaterialId.Empty) return true;
                }
            }
            return false;
        }

        internal bool TryEdit(Vector3Int cell, MaterialId material)
        {
            foreach (var view in _views)
                if (view.Source.Contains(cell, _origin, _size)) return view.Edit(cell, material);
            return false;
        }

        internal void Show(bool visible) { foreach (var view in _views) view.Show(visible); }
        public void Dispose() { foreach (var view in _views) view.Dispose(); }

        internal sealed partial class PatchView : IDisposable
        {
            internal readonly Source Source;
            internal readonly Dictionary<Vector3Int, MaterialId> Edits = new Dictionary<Vector3Int, MaterialId>();
            private readonly Dictionary<Vector2Int, GameObject> _patches = new Dictionary<Vector2Int, GameObject>();
            // 地形两端的编辑不能互相增加重建工作。列只保存本列被编辑的 Y，Terrain 洞瓦片和
            // Box 二分树各自保存覆盖计数；恢复到原材质时沿同一路径撤掉索引。
            private readonly Dictionary<Vector2Int, SortedSet<int>> _columnEdits = new Dictionary<Vector2Int, SortedSet<int>>();
            private readonly Dictionary<Vector2Int, int> _tileEdits = new Dictionary<Vector2Int, int>();
            private readonly Dictionary<BoundsInt, int> _boxEdits = new Dictionary<BoundsInt, int>();
            private readonly BoundsInt _boxCells;
            private readonly Dictionary<Vector2Int, GameObject> _boxTiles = new Dictionary<Vector2Int, GameObject>();
            private GameObject _boxTileRoot;
            private const int BoxTileWidth = 16;
            private readonly Transform _root;
            private readonly Vector3 _origin;
            private readonly float _size;
            private readonly ElementCellWorldProfile _profile;
            private TerrainData _runtimeHeightfield;
            private Material _terrainSurface;
            internal PatchView(Source source, Transform root, float size, ElementCellWorldProfile profile)
            {
                Source = source; _root = root; _origin = root.position; _size = size; _profile = profile;
                var lo = Vector3Int.FloorToInt((source.Bounds.min - _origin) / size);
                var hi = Vector3Int.CeilToInt((source.Bounds.max - _origin) / size);
                _boxCells = new BoundsInt(lo, hi - lo);
            }

            private void Bind(bool edited, bool owner = true)
            {
                if (Source.Collider == null) return;
                bool native = Source.NativeReplaces && owner;
                if (Source.Terrain != null)
                {
                    var data = edited && _runtimeHeightfield != null ? _runtimeHeightfield : Source.Heightfield;
                    if (Source.Terrain.terrainData != data) Source.Terrain.terrainData = data;
                    var collision = (TerrainCollider)Source.Collider;
                    if (collision.terrainData != data) collision.terrainData = data;
                    if (Source.NativeReplaces) { Source.Terrain.drawHeightmap = !native && Source.TerrainDrawHeightmap; collision.enabled = !native; }
                }
                else
                {
                    Source.Collider.enabled = !edited && !native;
                    for (int i = 0; i < Source.Renderers.Length; i++)
                        if (Source.Renderers[i] != null) Source.Renderers[i].enabled = !edited && !native && Source.RendererEnabled[i];
                }
            }

            internal void Show(bool visible)
            {
                if (_boxTileRoot != null) _boxTileRoot.SetActive(Edits.Count != 0);
                if (visible) { Source.Active = this; Bind(Edits.Count != 0); }
                else if (Source.Active == this) { Bind(false, false); Source.Active = null; }
            }

            private Rect CellRect(Vector3Int p) => new Rect(_origin.x + p.x * _size, _origin.z + p.z * _size, _size, _size);
            private void TileRange(Vector3Int p, out Vector2Int min, out Vector2Int max)
            {
                if (Source.Collider is BoxCollider) { min = max = BoxKey(p.x, p.z); return; }
                Rect rect = CellRect(p);
                min = Source.Heightfield != null ? Source.Tile(rect.xMin, rect.yMin) : Vector2Int.zero;
                max = Source.Heightfield != null ? Source.Tile(rect.xMax - _size * .00001f, rect.yMax - _size * .00001f) : min;
            }

            private static Vector2Int BoxKey(int x, int z) => new Vector2Int(
                x / BoxTileWidth - (x % BoxTileWidth < 0 ? 1 : 0), z / BoxTileWidth - (z % BoxTileWidth < 0 ? 1 : 0));
            private BoundsInt BoxTileCells(Vector2Int key)
            {
                var min = Vector3Int.Max(_boxCells.min, new Vector3Int(key.x*BoxTileWidth, _boxCells.yMin, key.y*BoxTileWidth));
                var max = Vector3Int.Min(_boxCells.max, new Vector3Int((key.x+1)*BoxTileWidth, _boxCells.yMax, (key.y+1)*BoxTileWidth));
                return new BoundsInt(min,max-min);
            }

            private void PrepareBoxTiles()
            {
                if (_boxTileRoot != null || !(Source.Collider is BoxCollider)) return;
                var min = BoxKey(_boxCells.xMin,_boxCells.zMin); var max = BoxKey(_boxCells.xMax-1,_boxCells.zMax-1);
                _boxTileRoot = new GameObject("Authored ground tiles");
                _boxTileRoot.SetActive(false); _boxTileRoot.transform.SetParent(_root,false);
                try
                {
                    for (int z = min.y; z <= max.y; z++)
                    for (int x = min.x; x <= max.x; x++)
                    {
                        var key = new Vector2Int(x,z); var box = BoxTileCells(key);
                        Vector3 a = Vector3.Max(Source.Bounds.min, _origin+(Vector3)box.min*_size);
                        Vector3 b = Vector3.Min(Source.Bounds.max, _origin+(Vector3)box.max*_size);
                        bool headless = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
                        var tile = headless ? new GameObject() : GameObject.CreatePrimitive(PrimitiveType.Cube);
                        tile.name = "Ground tile"; tile.layer = Source.Collider.gameObject.layer;
                        tile.transform.SetParent(_boxTileRoot.transform,false);
                        tile.transform.localPosition = (a+b)*.5f-_origin; tile.transform.localScale = b-a;
                        if (headless) tile.AddComponent<BoxCollider>();
                        else if (Source.Surface != null) tile.GetComponent<MeshRenderer>().sharedMaterial = Source.Surface;
                        tile.GetComponent<BoxCollider>().sharedMaterial = Source.Collider.sharedMaterial;
                        _boxTiles.Add(key,tile);
                    }
                }
                catch { ReleaseObject(_boxTileRoot); _boxTileRoot = null; _boxTiles.Clear(); throw; }
            }

            private static void Count<TKey>(Dictionary<TKey,int> counts, TKey key, int delta)
            {
                counts.TryGetValue(key, out int value);
                if (value + delta == 0) counts.Remove(key); else counts[key] = value + delta;
            }

            private void IndexEdit(Vector3Int p, int delta, Vector2Int min, Vector2Int max)
            {
                for (int z = min.y; z <= max.y; z++)
                for (int x = min.x; x <= max.x; x++) Count(_tileEdits, new Vector2Int(x,z), delta);
                if (Source.Collider is BoxCollider)
                {
                    var box = BoxTileCells(min);
                    while (true)
                    {
                        Count(_boxEdits, box, delta);
                        if (box.size == Vector3Int.one) return;
                        SplitBox(box, out var left, out var right);
                        box = left.Contains(p) ? left : right;
                    }
                }
                var key = new Vector2Int(p.x,p.z);
                if (delta > 0)
                {
                    if (!_columnEdits.TryGetValue(key, out var levels)) _columnEdits[key] = levels = new SortedSet<int>();
                    levels.Add(p.y);
                }
                else
                {
                    var levels = _columnEdits[key]; levels.Remove(p.y);
                    if (levels.Count == 0) _columnEdits.Remove(key);
                }
            }

            internal bool Edit(Vector3Int cell, MaterialId material)
            {
                bool hadOld = Edits.TryGetValue(cell, out var old);
                bool changed = material != Source.Material;
                int indexDelta = (changed ? 1 : 0) - (hadOld ? 1 : 0);
                TileRange(cell, out var min, out var max);
                if (changed) Edits[cell] = material; else Edits.Remove(cell);
                if (indexDelta != 0) IndexEdit(cell, indexDelta, min, max);
                var pending = new Dictionary<Vector2Int, GameObject>();
                try
                {
                    PrepareBoxTiles();
                    if (Source.Heightfield != null && _runtimeHeightfield == null)
                    {
                        _runtimeHeightfield = Object.Instantiate(Source.Heightfield);
                        _runtimeHeightfield.hideFlags = HideFlags.DontSave;
                        _runtimeHeightfield.enableHolesTextureCompression = false;
                    }
                    for (int z = min.y; z <= max.y; z++)
                    for (int x = min.x; x <= max.x; x++)
                    {
                        var key = new Vector2Int(x, z);
                        if (Source.Heightfield != null && Source.Heightfield.IsHole(x, z)) continue;
                        Rect rect = Source.TileBounds(key);
                        pending.Add(key, _tileEdits.ContainsKey(key) ? BakePatch(rect, key) : null);
                    }
                    foreach (var pair in pending)
                        if (_runtimeHeightfield != null) _runtimeHeightfield.SetHoles(pair.Key.x, pair.Key.y, new[,] { { pair.Value == null } });
                }
                catch
                {
                    if (hadOld) Edits[cell] = old; else Edits.Remove(cell);
                    if (indexDelta != 0) IndexEdit(cell, -indexDelta, min, max);
                    foreach (var pair in pending)
                    {
                        ReleasePatch(pair.Value);
                        if (_runtimeHeightfield != null) _runtimeHeightfield.SetHoles(pair.Key.x, pair.Key.y, new[,] { { !_patches.ContainsKey(pair.Key) } });
                    }
                    throw;
                }
                foreach (var pair in pending)
                {
                    if (_patches.TryGetValue(pair.Key, out var retired)) { ReleasePatch(retired); _patches.Remove(pair.Key); }
                    if (_boxTiles.TryGetValue(pair.Key,out var baseTile)) baseTile.SetActive(pair.Value == null);
                    if (pair.Value == null) continue;
                    _patches.Add(pair.Key, pair.Value); pair.Value.SetActive(true);
                }
                if (_boxTileRoot != null) _boxTileRoot.SetActive(Edits.Count != 0);
                if (Source.Active == this) Bind(Edits.Count != 0);
                Physics.SyncTransforms();
                return true;
            }

            private static void ReleasePatch(GameObject go)
            {
                if (go == null) return;
                go.SetActive(false);
                if (go.TryGetComponent<MeshCollider>(out var collider) && collider.sharedMesh != null) ReleaseObject(collider.sharedMesh);
                ReleaseObject(go);
            }

            public void Dispose()
            {
                Show(false);
                foreach (var patch in _patches.Values) ReleasePatch(patch);
                _patches.Clear();
                if (_boxTileRoot != null) ReleaseObject(_boxTileRoot);
                if (_runtimeHeightfield != null) ReleaseObject(_runtimeHeightfield);
                if (_terrainSurface != null) ReleaseObject(_terrainSurface);
            }
        }
    }
}
