using System;
using System.Collections.Generic;
using Game.ElementField;
using Game.Materials;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Rendering
{
    /// <summary>
    /// 直接绘制压缩液体的外表面。满池只有一个盒时，大小不影响六个四边面的成本。
    /// 编辑产生的分盒共享内部面；按平面和二维矩形相减，洞壁、局部液位及跨区域连接都能保留。
    /// </summary>
    internal sealed class CellularRestingLiquidSurface
    {
        private readonly struct Box
        {
            internal readonly BoundsInt Cells;
            internal readonly uint Amount;
            internal Box(BoundsInt cells, uint amount) { Cells = cells; Amount = amount; }

            // 水平边沿用整数细格，竖直边沿用定点 GMU，避免比较两片共面液面时依赖浮点阈值。
            internal long Edge(int axis, bool upper, uint capacity)
            {
                if (axis == 0) return upper ? Cells.xMax : Cells.xMin;
                if (axis == 2) return upper ? Cells.zMax : Cells.zMin;
                if (!upper) return (long)Cells.yMin * capacity;
                return Amount == capacity ? (long)Cells.yMax * capacity : (long)Cells.yMin * capacity + Amount;
            }
        }

        private readonly struct Rect
        {
            internal readonly long U0, U1, V0, V1;
            internal Rect(long u0, long u1, long v0, long v1) { U0 = u0; U1 = u1; V0 = v0; V1 = v1; }
        }

        private sealed class Region
        {
            internal BoundsInt Cells;
            internal MaterialId Material;
            internal readonly List<Box> Boxes = new List<Box>();
            internal readonly HashSet<Vector3Int> ExplicitCells = new HashSet<Vector3Int>();
            internal Mesh Mesh;
            internal ulong Version;
            internal bool HasVersion, Queued, MeshDirty, Draw;
        }

        private const int DeferredCellLimit = 8192;
        private readonly List<Region> _regions = new List<Region>();
        private readonly List<int> _dirtyRegions = new List<int>();
        private readonly List<int> _visibleCandidates = new List<int>();
        private readonly HashSet<int> _layoutSlots = new HashSet<int>();
        private readonly HashSet<BoundsInt> _layoutBounds = new HashSet<BoundsInt>();
        private readonly Dictionary<Vector3Int, bool> _deferredCells = new Dictionary<Vector3Int, bool>();
        private readonly Dictionary<(int, bool, long), List<Rect>> _planes = new Dictionary<(int, bool, long), List<Rect>>();
        private readonly Stack<List<Rect>> _rectPools = new Stack<List<Rect>>();
        private readonly List<Rect> _fragments = new List<Rect>();
        private readonly List<Vector3> _vertices = new List<Vector3>(), _normals = new List<Vector3>();
        private readonly List<int> _indices = new List<int>();
        private readonly List<Color32> _solidIds = new List<Color32>();
        private readonly Action<BoundsInt, uint> _visit;
        private readonly Action<int> _registerNeighbor, _queueRegion, _collectCandidate;
        private readonly Action<int, BoundsInt, BoundsInt> _layoutChange;
        private List<Box> _collecting;
        private ICellularRestingLiquidReadOnly _owner;
        private uint _capacity;
        private ulong _layoutVersion;
        private bool _needsColdBind;
        private int _visibleCursor;
        private bool _haveVisibleRegions;
        private ulong _visibleLayout;
        private Matrix4x4 _visibleProjection, _visibleCulling;
        private Vector3 _visibleOrigin;
        private float _visibleSize, _visibleNear, _visibleFar;

        internal CellularRestingLiquidSurface()
        {
            _visit = AddBox;
            _registerNeighbor = RegisterNeighbor;
            _queueRegion = QueueRegion;
            _collectCandidate = _visibleCandidates.Add;
            _layoutChange = RecordLayout;
        }

        private bool _collectingWholeCells;
        private void AddBox(BoundsInt cells, uint amount)
        {
            if (amount != 0) _collecting.Add(new Box(cells, _collectingWholeCells ? _capacity : amount));
        }

        internal bool CellChanged(Vector3Int cell, bool hasExplicitSurface, bool explicitChanged)
        {
            if (_owner == null) return false;
            if (_owner.RegionLayoutVersion != _layoutVersion)
            {
                // 回调发生于 Gameplay 事务末尾。只记坐标；等渲染阶段先消费布局游标，再查询新 slot 身份。
                // 长时间没有相机时有界退回冷绑定，不能把循环复用的 slot 解释为旧水池。
                if (!_needsColdBind)
                {
                    if (_deferredCells.TryGetValue(cell, out bool changed)) _deferredCells[cell] = changed || explicitChanged;
                    else if (_deferredCells.Count < DeferredCellLimit) _deferredCells.Add(cell, explicitChanged);
                    else { _deferredCells.Clear(); _needsColdBind = true; }
                }
                return true;
            }
            if (_regions.Count == 0) return false;
            int center = _owner.FindRegionIndex(cell);
            bool restingChanged = center >= 0 && _regions[center].Version != _owner.GetRegionVersion(center);
            if (!explicitChanged && !restingChanged) return false;
            // BVH 只定位本格与六个共享面。气体远离静水时仅一次点查询，近处修改不遍历世界区域表。
            Track(center, cell, hasExplicitSurface);
            if (cell.x > int.MinValue) Track(_owner.FindRegionIndex(cell + Vector3Int.left), cell, hasExplicitSurface);
            if (cell.x < int.MaxValue) Track(_owner.FindRegionIndex(cell + Vector3Int.right), cell, hasExplicitSurface);
            if (cell.y > int.MinValue) Track(_owner.FindRegionIndex(cell + Vector3Int.down), cell, hasExplicitSurface);
            if (cell.y < int.MaxValue) Track(_owner.FindRegionIndex(cell + Vector3Int.up), cell, hasExplicitSurface);
            if (cell.z > int.MinValue) Track(_owner.FindRegionIndex(cell + Vector3Int.back), cell, hasExplicitSurface);
            if (cell.z < int.MaxValue) Track(_owner.FindRegionIndex(cell + Vector3Int.forward), cell, hasExplicitSurface);
            return restingChanged;
        }

        private void Track(int index, Vector3Int cell, bool hasSurface)
        {
            if (index < 0) return;
            var state = _regions[index];
            if (hasSurface) state.ExplicitCells.Add(cell);
            else state.ExplicitCells.Remove(cell);
            QueueRegion(index);
        }

        private void QueueRegion(int index)
        {
            var state = _regions[index];
            if (state.Queued) return;
            state.Queued = true;
            _dirtyRegions.Add(index);
        }

        private void RecordLayout(int index, BoundsInt before, BoundsInt after)
        {
            _layoutSlots.Add(index);
            if (before.size != Vector3Int.zero) _layoutBounds.Add(before);
            if (after.size != Vector3Int.zero) _layoutBounds.Add(after);
        }

        private void ColdBind(ICellularRestingLiquidReadOnly owner, CellularMaterialRenderer explicitCells)
        {
            Clear();
            _owner = owner;
            if (owner == null || owner.CellCapacity == 0) return;
            _layoutVersion = owner.RegionLayoutVersion;
            _capacity = owner.CellCapacity;
            for (int i = 0; i < owner.RegionCount; i++)
            {
                var region = owner.GetRegion(i);
                _regions.Add(new Region { Cells = region.Cells, Material = region.Material });
                QueueRegion(i);
            }
            if (owner.RegionCount > 0) explicitCells.SeedRestingInterfaces(this);
        }

        private bool ApplyLayout(CellularMaterialRenderer explicitCells)
        {
            ulong current = _owner.RegionLayoutVersion;
            if (!_owner.VisitLayoutChanges(_layoutVersion, _layoutChange)) return false;
            while (_regions.Count < _owner.RegionCount) _regions.Add(new Region());
            foreach (int index in _layoutSlots)
            {
                var state = _regions[index];
                var currentRegion = _owner.GetRegion(index);
                state.Cells = currentRegion.Cells; state.Material = currentRegion.Material;
                state.HasVersion = false; state.Draw = false;
                state.ExplicitCells.Clear();
                if (state.Cells.size != Vector3Int.zero)
                    explicitCells.SeedNewRestingRegion(state.Cells, state.ExplicitCells);
                QueueRegion(index);
            }
            // 撤掉旧湖会让旧邻居露面，新湖会挡住新邻居；在最终 BVH 上按两处范围分别标记。
            foreach (var bounds in _layoutBounds) _owner.VisitTouchingRegions(bounds, _queueRegion);
            _layoutSlots.Clear(); _layoutBounds.Clear();
            _layoutVersion = current;
            foreach (var pair in _deferredCells) CellChanged(pair.Key, explicitCells.Read(pair.Key).Quantity != 0, pair.Value);
            _deferredCells.Clear();
            return true;
        }

        internal void Refresh(ICellularRestingLiquidReadOnly owner, CellularMaterialRenderer explicitCells)
        {
            if (_needsColdBind || !ReferenceEquals(owner, _owner)) ColdBind(owner, explicitCells);
            else if (owner != null && owner.RegionLayoutVersion != _layoutVersion && !ApplyLayout(explicitCells))
                ColdBind(owner, explicitCells);
            // 先更新所有涉事区域的盒清单，再允许绘制接壤面。不让 A 区的几何使用 B 区上一版本。
            foreach (int index in _dirtyRegions)
            {
                var state = _regions[index];
                ulong version = owner.GetRegionVersion(index);
                if (!state.HasVersion || version != state.Version)
                {
                    state.Boxes.Clear();
                    if (state.Cells.size != Vector3Int.zero)
                    {
                        _collecting = state.Boxes;
                        _collectingWholeCells = ElementCellMaterialRules.OccupiesWholeCell(state.Material);
                        owner.VisitOccupiedBoxes(index, _visit);
                    }
                    state.HasVersion = true; state.Version = version;
                }
                state.MeshDirty = true; state.Queued = false;
            }
            _collecting = null;
            _dirtyRegions.Clear();
        }

        private void RegisterNeighbor(int index)
        {
            foreach (var box in _regions[index].Boxes)
                for (int axis = 0; axis < 3; axis++)
                {
                    var rect = Project(box, axis);
                    Register(axis, false, box.Edge(axis, false, _capacity), rect);
                    Register(axis, true, box.Edge(axis, true, _capacity), rect);
                }
        }

        private void Build(Region state, CellularMaterialRenderer explicitCells)
        {
            state.Draw = false;
            if (state.Boxes.Count == 0 || CellularMaterialSurfaceMesh.Slot(state.Material) < 0) return;
            foreach (var faces in _planes.Values) { faces.Clear(); _rectPools.Push(faces); }
            _planes.Clear();
            // 只取本区域与接壤区域。位于同一坐标平面且朝向相反的面才可能遮掉当前面的一个矩形。
            _owner.VisitTouchingRegions(state.Cells, _registerNeighbor);
            foreach (var cell in state.ExplicitCells)
            {
                var sample = explicitCells.Read(cell);
                if (sample.Quantity != 0) RegisterExplicitFacePlanes(cell, sample.Material, sample.Quantity, state.Material);
            }
            _vertices.Clear(); _normals.Clear(); _indices.Clear();
            foreach (var box in state.Boxes)
            {
                if (TryBuildDroplet(state, box, explicitCells)) continue;
                for (int axis = 0; axis < 3; axis++)
                {
                    var rect = Project(box, axis);
                    EmitFace(state.Cells.min, axis, false, box.Edge(axis, false, _capacity), rect);
                    EmitFace(state.Cells.min, axis, true, box.Edge(axis, true, _capacity), rect);
                }
            }
            if (_indices.Count == 0) return;
            if (state.Mesh == null)
            {
                state.Mesh = new Mesh { name = "Cellular Resting Liquid", indexFormat = IndexFormat.UInt32, hideFlags = HideFlags.DontSave };
                state.Mesh.MarkDynamic();
            }
            state.Mesh.Clear();
            state.Mesh.SetVertices(_vertices);
            state.Mesh.SetNormals(_normals);
            if (ElementCellMaterialRules.Motion(state.Material) != ElementCellMotion.Liquid)
            {
                _solidIds.Clear();
                var id = new Color32((byte)state.Material, 0, 0, 255);
                for (int i = 0; i < _vertices.Count; i++) _solidIds.Add(id);
                state.Mesh.SetColors(_solidIds);
            }
            state.Mesh.SetTriangles(_indices, 0, false);
            state.Mesh.bounds = new Bounds((Vector3)state.Cells.size * .5f, state.Cells.size);
            state.Draw = true;
        }

        private bool TryBuildDroplet(Region state, Box box, CellularMaterialRenderer explicitCells)
        {
            if ((ulong)box.Amount * 4 > _capacity || box.Cells.size != Vector3Int.one ||
                ElementCellMaterialRules.Motion(state.Material) != ElementCellMotion.Liquid) return false;
            var cell = box.Cells.min;
            foreach (var face in CellularMaterialRenderer.Faces)
            {
                var neighbor = cell + face;
                if (!CellularMaterialRenderer.OffsetWrapped(cell, face, neighbor) &&
                    ElementCellMaterialRules.Motion(explicitCells.ReadSurface(neighbor).Material) == ElementCellMotion.Liquid) return false;
            }
            // 数量从活动格交还给冷页时仍用同一份液滴模板和半径，几何不会突然摊成薄片。
            float radius = Mathf.Pow(box.Amount / (float)_capacity * (3f / (4f * Mathf.PI)), 1f / 3f);
            Vector3 center = (Vector3)(cell - state.Cells.min) + new Vector3(.5f, radius, .5f);
            int first = _vertices.Count;
            foreach (var unit in LiquidDropletTemplate.Vertices)
            { _vertices.Add(center + unit * radius); _normals.Add(unit); }
            foreach (int index in LiquidDropletTemplate.Indices) _indices.Add(first + index);
            return true;
        }

        private Rect Project(Box box, int axis)
        {
            int u = (axis + 1) % 3, v = (axis + 2) % 3;
            return new Rect(box.Edge(u, false, _capacity), box.Edge(u, true, _capacity), box.Edge(v, false, _capacity), box.Edge(v, true, _capacity));
        }

        private void Register(int axis, bool upper, long coordinate, Rect rect)
        {
            var key = (axis, upper, coordinate);
            if (!_planes.TryGetValue(key, out var faces))
            {
                faces = _rectPools.Count > 0 ? _rectPools.Pop() : new List<Rect>(4);
                _planes.Add(key, faces);
            }
            faces.Add(rect);
        }

        private void RegisterExplicitFacePlanes(Vector3Int cell, MaterialId material, uint quantity, MaterialId restingMaterial)
        {
            long x = cell.x, y = (long)cell.y * _capacity, z = cell.z;
            long height = ElementCellMaterialRules.OccupiesWholeCell(material) ? _capacity : Math.Min(quantity, _capacity);
            var horizontal = new Rect(z, z + 1, x, x + 1);
            Register(1, false, y, horizontal);
            if (height == _capacity) Register(1, true, y + height, horizontal);
            // 同材动态液体的两个共享角点已接上静液位，可以遮掉整个静水侧面。
            // 不同液体的角点独立变化，没有确定的矩形遮挡，保留交界；固定/颗粒侧面使用真实高度。
            if (ElementCellMaterialRules.Motion(material) == ElementCellMotion.Liquid)
            {
                if (material != restingMaterial) return;
                height = _capacity;
            }
            var sideX = new Rect(y, y + height, z, z + 1);
            var sideZ = new Rect(x, x + 1, y, y + height);
            Register(0, false, x, sideX); Register(0, true, x + 1, sideX);
            Register(2, false, z, sideZ); Register(2, true, z + 1, sideZ);
        }

        private void EmitFace(Vector3Int origin, int axis, bool upper, long coordinate, Rect face)
        {
            _fragments.Clear(); _fragments.Add(face);
            if (_planes.TryGetValue((axis, !upper, coordinate), out var blockers))
                foreach (var cut in blockers)
                {
                    // 每个交集最多剩左右两条和中间上下两条，片段互不重叠；面数随实际切口增加。
                    for (int i = _fragments.Count - 1; i >= 0; i--) Subtract(i, cut);
                    if (_fragments.Count == 0) return;
                }
            Vector3 normal = axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward;
            if (!upper) normal = -normal;
            foreach (var rect in _fragments)
            {
                int first = _vertices.Count;
                _vertices.Add(Point(origin, axis, coordinate, rect.U0, rect.V0));
                _vertices.Add(Point(origin, axis, coordinate, rect.U1, rect.V0));
                _vertices.Add(Point(origin, axis, coordinate, rect.U1, rect.V1));
                _vertices.Add(Point(origin, axis, coordinate, rect.U0, rect.V1));
                _normals.Add(normal); _normals.Add(normal); _normals.Add(normal); _normals.Add(normal);
                _indices.Add(first); _indices.Add(first + (upper ? 1 : 3)); _indices.Add(first + 2);
                _indices.Add(first); _indices.Add(first + 2); _indices.Add(first + (upper ? 3 : 1));
            }
        }

        private void Subtract(int index, Rect cut)
        {
            var src = _fragments[index];
            long u0 = Math.Max(src.U0, cut.U0), u1 = Math.Min(src.U1, cut.U1);
            long v0 = Math.Max(src.V0, cut.V0), v1 = Math.Min(src.V1, cut.V1);
            if (u0 >= u1 || v0 >= v1) return;
            int last = _fragments.Count - 1;
            _fragments[index] = _fragments[last];
            _fragments.RemoveAt(last);
            if (src.U0 < u0) _fragments.Add(new Rect(src.U0, u0, src.V0, src.V1));
            if (src.U1 > u1) _fragments.Add(new Rect(u1, src.U1, src.V0, src.V1));
            if (src.V0 < v0) _fragments.Add(new Rect(u0, u1, src.V0, v0));
            if (src.V1 > v1) _fragments.Add(new Rect(u0, u1, v1, src.V1));
        }

        private Vector3 Point(Vector3Int origin, int axis, long coordinate, long u, long v)
        {
            long x = axis == 0 ? coordinate : axis == 1 ? v : u;
            long y = axis == 0 ? u : axis == 1 ? coordinate : v;
            long z = axis == 0 ? v : axis == 1 ? u : coordinate;
            return new Vector3((float)(x - origin.x), (float)((y - (long)origin.y * _capacity) / (double)_capacity), (float)(z - origin.z));
        }

        internal void Draw(ElementWorldRuntime world, Camera camera, Plane[] frustum, Material liquid, Material solid,
            MaterialPropertyBlock[] palette, int layer, CellularMaterialRenderer explicitCells, int rebuildBudget)
        {
            if (_owner == null || _regions.Count == 0) return;
            float size = world.CellSize;
            RefreshVisibleRegions(world,camera,frustum);
            // 先收集候选，再建可见区网格。网格生成也会访问同一 BVH，分两步避免重入遍历。
            int start = _visibleCursor < _visibleCandidates.Count ? _visibleCursor : 0;
            for (int i = 0; i < _visibleCandidates.Count; i++)
            {
                int positionInList = start + i;
                if (positionInList >= _visibleCandidates.Count) positionInList -= _visibleCandidates.Count;
                var state = _regions[_visibleCandidates[positionInList]];
                Material material = ElementCellMaterialRules.Motion(state.Material) == ElementCellMotion.Liquid ? liquid : solid;
                if (state.Boxes.Count == 0 || material == null) continue;
                Vector3 position = world.Origin + (Vector3)state.Cells.min * size;
                if (state.MeshDirty && rebuildBudget > 0)
                {
                    // 相机一次扫到许多历史沙堆时，把 Mesh 构建摊到后续帧。已建几何可继续绘制。
                    rebuildBudget--;
                    _visibleCursor = positionInList + 1;
                    Build(state, explicitCells);
                    state.MeshDirty = false;
                }
                if (!state.Draw) continue;
                Graphics.DrawMesh(state.Mesh, Matrix4x4.TRS(position, Quaternion.identity, Vector3.one * size),
                    material, layer, camera, 0, palette[CellularMaterialSurfaceMesh.Slot(state.Material)], ShadowCastingMode.On, true);
            }
        }

        private void RefreshVisibleRegions(ElementWorldRuntime world, Camera camera, Plane[] frustum)
        {
            var projection = camera.projectionMatrix * camera.worldToCameraMatrix;
            var culling = camera.cullingMatrix;
            float size = world.CellSize, near = camera.nearClipPlane, far = camera.farClipPlane;
            var origin = world.Origin;
            if (_haveVisibleRegions && _visibleLayout == _layoutVersion && projection.Equals(_visibleProjection)
                && culling.Equals(_visibleCulling) && size == _visibleSize && near == _visibleNear && far == _visibleFar
                && origin.Equals(_visibleOrigin)) return;
            _visibleCandidates.Clear();
            if (TryGetViewCells(world,camera,out var view))
                _owner.VisitTouchingRegions(view,_collectCandidate);
            else
                for (int i = 0; i < _regions.Count; i++) _visibleCandidates.Add(i);
            // BVH 回调给的是视锥包围盒中的候选，斜向镜头还要剔除矩形角落。
            // 只缓存几何范围，水量变化仍会照常驱动该区域的 Mesh 重建。
            int kept = 0;
            for (int i = 0; i < _visibleCandidates.Count; i++)
            {
                int index = _visibleCandidates[i];
                var cells = _regions[index].Cells;
                Vector3 extents = (Vector3)cells.size * size, min = origin + (Vector3)cells.min * size;
                if (cells.size != Vector3Int.zero && GeometryUtility.TestPlanesAABB(frustum,new Bounds(min+extents*.5f,extents)))
                    _visibleCandidates[kept++] = index;
            }
            _visibleCandidates.RemoveRange(kept,_visibleCandidates.Count-kept);
            _visibleProjection = projection; _visibleCulling = culling; _visibleSize = size; _visibleOrigin = origin;
            _visibleNear = near; _visibleFar = far; _visibleLayout = _layoutVersion; _haveVisibleRegions = true;
        }

        private static bool TryGetViewCells(ElementWorldRuntime world, Camera camera, out BoundsInt cells)
        {
            cells = default;
            var min = Vector3.positiveInfinity;
            var max = Vector3.negativeInfinity;
            for (int i = 0; i < 8; i++)
            {
                float depth = i < 4 ? camera.nearClipPlane : camera.farClipPlane;
                var point = (camera.ViewportToWorldPoint(new Vector3(i & 1, (i >> 1) & 1, depth)) - world.Origin) / world.CellSize;
                min = Vector3.Min(point, min);
                max = Vector3.Max(point, max);
            }
            // BoundsInt 尺寸为有符号整数。极大视锥使用精确平面剔除作为后备，以免尺寸回绕漏画。
            for (int axis = 0; axis < 3; axis++)
            {
                double lower = Math.Max(int.MinValue, Math.Floor(min[axis]));
                double upper = Math.Min(int.MaxValue, Math.Ceiling(max[axis]) + 1d);
                if (!(upper > lower && upper - lower <= int.MaxValue)) return false;
                var low = cells.min;
                var high = cells.max;
                low[axis] = (int)lower;
                high[axis] = (int)upper;
                cells.SetMinMax(low, high);
            }
            return true;
        }

        internal void Clear()
        {
            foreach (var state in _regions) if (state.Mesh != null) UnityEngine.Object.Destroy(state.Mesh);
            _regions.Clear(); _dirtyRegions.Clear(); _visibleCandidates.Clear(); _planes.Clear(); _rectPools.Clear(); _fragments.Clear();
            _deferredCells.Clear(); _layoutBounds.Clear(); _layoutSlots.Clear();
            _needsColdBind = false; _haveVisibleRegions = false; _layoutVersion = 0;
            _owner = null; _collecting = null;
        }
    }
}
