using System;
using System.Collections.Generic;
using Game.Core;
using Game.ElementField;
using Game.Materials;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Rendering
{
    /// <summary>
    /// 细格材料的表现订阅者。只记录真正有主材或气体的细格，不遍历地形基底。
    /// 网格缓存按渲染页划分，可以独立于 Solver Chunk；固定岩层与碰撞由 ElementField 的提交结果维护。
    /// </summary>
    [DisallowMultipleComponent, RequireComponent(typeof(ElementWorldRuntime))]
    public sealed partial class CellularMaterialRenderer : MonoBehaviour
    {
        private const int PageShift = 3;
        internal const int PageSize = 1 << PageShift;
        private const int TextureSize = PageSize + 2;
        private const int TextureCells = TextureSize * TextureSize * TextureSize;
        private const int RetainedEmptyPages = 8;
        private const float GasBlendSeconds = .3f;
        private const double VisibleQueueDelaySeconds = .15;
        private const double MaximumExtraRebuildMs = 2;
        private const double FrameReserveMs = 2;
        private static readonly int MotionClock = Shader.PropertyToID("_CellularMotionClock");
        private static readonly int MotionRate = Shader.PropertyToID("_CellularMotionRate");
        private readonly Dictionary<Vector3Int, Sample> _departures = new Dictionary<Vector3Int, Sample>(1024);
        private int _departureFrame = -1;
        private float MotionDuration => Mathf.Clamp(_world.CellularTickDuration, .025f, .25f);
        internal static readonly Vector3Int[] Faces = { Vector3Int.down, Vector3Int.up, Vector3Int.left, Vector3Int.right,
            Vector3Int.back, Vector3Int.forward };
        [SerializeField] private Material _liquidSurface;
        [SerializeField] private Material _solidSurface;
        [SerializeField] private Material _gasVolume;
        [SerializeField, Min(1)] private int _maximumPageRebuildsPerFrame = 8;
        [Tooltip("可见页积压、且该帧无需执行本地材料 Tick 时的追赶上限。额外工作同时受毫秒预算约束。")]
        [SerializeField, Min(1)] private int _maximumCatchUpPages = 32;
        [Tooltip("无本地材料 Tick 的追赶预算（毫秒）。队首已等 150ms 且帧有余量时，最多再借 2ms，总追赶预算至多增至 4ms。")]
        [SerializeField, Min(0)] private float _catchUpRebuildMs = 2f;

        internal struct Sample
        {
            internal MaterialId Material;
            internal uint Quantity, Steam, Toxic;
            internal int PageIndex, ArrivalFrame;
            internal float ArrivalTime;
            internal bool Resting;
            internal bool HasGas => Steam != 0 || Toxic != 0;
            internal bool IsEmpty => Quantity == 0 && !HasGas;
        }

        private sealed class Page
        {
            internal readonly List<Vector3Int> Cells = new List<Vector3Int>(4);
            internal ElementChunkKey Key;
            internal int SurfaceCells, GasCells;
            internal bool SurfaceDirty, GasDirty, HasSurface, HasGasTexture, Visible;
            internal float GasBlendStart;
            internal Mesh Surface;
            internal Texture3D GasTexture;
            internal Color32[] GasHistory;
            // 局部地址为三组低 3 位，值为两种浓度的线性 byte。纯液体页不分配此表。
            internal Dictionary<int, ushort> GasDensity;
            internal bool NativeWaiting; internal int NativeSeenFrame,NativeDirtyFrame; internal double NativeDirtyAt;
            internal int VisibleIndex, SurfaceSlots;
            internal uint WaterIndices, SandIndices;
            internal bool Queued;
            internal long QueuedAt;
            internal Page PreviousDirty, NextDirty;
        }

        private static ElementWorldRuntime _observedWorld;
        private readonly Dictionary<ElementChunkKey, Page> _pages = new Dictionary<ElementChunkKey, Page>();
        private readonly Dictionary<Vector3Int, Sample> _samples = new Dictionary<Vector3Int, Sample>();
        private readonly Stack<Page> _empty = new Stack<Page>(RetainedEmptyPages);
        private List<Page> _visiblePages = new List<Page>(64), _previousVisiblePages = new List<Page>(64);
        private Page _firstDirty, _lastDirty;
        private long _frameStart;
        private readonly Plane[] _frustum = new Plane[6];
        private readonly Color32[] _gasPixels = new Color32[TextureCells];
        private readonly CellularMaterialSurfaceMesh _mesher = new CellularMaterialSurfaceMesh();
        private readonly CellularRestingLiquidSurface _resting = new CellularRestingLiquidSurface();
        private ElementWorldRuntime _world;
        private Camera _camera;
        private Mesh _gasBounds;
        private Material _liquid, _solid, _gas;
        private bool _ownsLiquid, _ownsSolid, _ownsGas, _bound;
        private MaterialPropertyBlock[] _surfaceProperties;
        private MaterialPropertyBlock _gasProperties;
        private static readonly int Density = Shader.PropertyToID("_GasDensity");
        private static readonly int Layout = Shader.PropertyToID("_GasLayout");
        private static readonly int Blend = Shader.PropertyToID("_DensityBlend");
        private static readonly int Visibility = Shader.PropertyToID("_Visibility");
        private static readonly int PresentationTime = Shader.PropertyToID("_PresentationTime");

        // 动态生成的场景也可能只有 ElementWorldRuntime。注册表给出唯一的本地写世界；
        // 相机开始渲染时只在 Owner 切换后查一次组件，避免逐帧搜索场景或从 Gameplay 反向依赖 Rendering。
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void RegisterCameraHook()
        {
            _observedWorld = null;
            RenderPipelineManager.beginCameraRendering -= ObserveWorld;
            RenderPipelineManager.beginCameraRendering += ObserveWorld;
            ElementWorldRuntime.CellularReplicaPresentationReady -= ObserveReplica;
            ElementWorldRuntime.CellularReplicaPresentationReady += ObserveReplica;
        }

        private static void ObserveReplica(ElementWorldRuntime world) => AttachToWorld(world);

        private static void ObserveWorld(ScriptableRenderContext context, Camera camera)
        {
            var world = ElementRuntimeRegistry.ActiveSink as ElementWorldRuntime;
            if (ReferenceEquals(world, _observedWorld)) return;
            _observedWorld = world;
            AttachToWorld(world);
        }

        internal static bool AttachToWorld(ElementWorldRuntime world)
        {
            if (world == null || world.ExecutionMode == WorldExecutionMode.Authority || !world.UsesCellularWorld)
                return false;
            if (!world.TryGetComponent<CellularMaterialRenderer>(out _))
                world.gameObject.AddComponent<CellularMaterialRenderer>();
            return true;
        }

        private void OnEnable()
        {
            _world = GetComponent<ElementWorldRuntime>();
            TryBind();
        }

        private void TryBind()
        {
            if (_bound || _world == null || !_world.isActiveAndEnabled || !_world.IsInitialized
                || !_world.UsesCellularWorld || _world.ExecutionMode == WorldExecutionMode.Authority
                || _world.CellularCellCapacity == 0) return;

            var definition = _world.CellularDefinition;
            var liquidTemplate = _liquidSurface != null ? _liquidSurface : definition?.LiquidSurface;
            var solidTemplate = _solidSurface != null ? _solidSurface : definition?.SolidSurface;
            _liquid = ResolveMaterial(liquidTemplate, "Game/Elemental/Cartoon Liquid", true, out _ownsLiquid);
            _solid = ResolveMaterial(solidTemplate, "Game/Materials/FixedSolid", true, out _ownsSolid);
            var volumeRenderer = GetComponent<GasFieldVolumeRenderer>();
            var gasTemplate = _gasVolume != null || volumeRenderer == null ? _gasVolume : volumeRenderer.SharedVolumeMaterial;
            if (gasTemplate == null) gasTemplate = definition?.GasVolume;
            _gas = ResolveMaterial(gasTemplate, "Game/Materials/RealGasVolume", false, out _ownsGas);
            if (_liquid == null || _solid == null || _gas == null)
                GameLog.Error("细格材质或 Shader 缺失；请在 CellularMaterialRenderer 指定液体、固体与 RealGasVolume 材质。", "Rendering");
            _surfaceProperties = CellularMaterialSurfaceMesh.CreatePalette();
            _gasProperties = new MaterialPropertyBlock();
            _bound = true;
            // 先订阅，再用当前提交补齐初始数量；同一格重复通知会比较样本，不会叠加。
            _world.CellularContentsChanged += RefreshCell;
            _world.VisitCellularOccupiedCells(RefreshCell);
        }

        private static Material ResolveMaterial(Material configured, string shaderName, bool allowLit, out bool owns)
        {
            owns = false;
            if (configured != null) return configured;
            var shader = Shader.Find(shaderName);
            if (shader == null && allowLit) shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) return null;
            owns = true;
            return new Material(shader) { hideFlags = HideFlags.DontSave };
        }

        private void RefreshCell(Vector3Int cell)
        {
            Sample sample = default;
            if (_world.TryGetCellularContents(cell, out var material, out uint quantity, out sample.Steam, out sample.Toxic)
                && quantity != 0 && CellularMaterialSurfaceMesh.Slot(material) >= 0)
            {
                sample.Material = material;
                sample.Quantity = quantity;
            }
            // 渲染页边长是 2 的幂。算术右移在负坐标也向下取整，低位掩码给出 [0,7] 的本地坐标。
            var key = Key(cell);
            if (!_pages.TryGetValue(key, out var page))
            {
                if (sample.IsEmpty)
                {
                    // 已压缩的水也可能在此被挖空。改动不建立显式格，却会令相邻动态格露出新侧面。
                    if (_resting.CellChanged(cell, false, false)) MarkNeighborPages(cell, key, true, false);
                    return;
                }
                page = _empty.Count != 0 ? _empty.Pop() : new Page();
                page.Key = key;
                _pages.Add(key, page);
                IndexPage(key, true);
                if (_visibleSetValid && IsVisible(page,_visibleMeters)) AddVisiblePage(page);
            }
            _samples.TryGetValue(cell, out var old);
            bool surfaceChanged = old.Material != sample.Material || old.Quantity != sample.Quantity;
            if (surfaceChanged && old.Quantity != 0 && ElementCellMaterialRules.Motion(old.Material) == ElementCellMotion.Liquid &&
                _world.CellularRestingLiquids?.TryGetCell(cell, out _, out _) != true)
            {
                if (_departureFrame != Time.frameCount) { _departures.Clear(); _departureFrame = Time.frameCount; }
                if (_departures.Count < 1024) _departures[cell] = old;
            }
            if (sample.Quantity != 0 && old.Quantity == 0)
            { sample.ArrivalFrame = Time.frameCount; sample.ArrivalTime = Time.time; }
            else { sample.ArrivalFrame = old.ArrivalFrame; sample.ArrivalTime = old.ArrivalTime; }
            bool gasChanged = old.Steam != sample.Steam || old.Toxic != sample.Toxic;
            if (CaptureNativeMotion) RecordNativeMotion(cell,in old, in sample);
            if (!surfaceChanged && !gasChanged)
            {
                if (_resting.CellChanged(cell, sample.Quantity != 0, false)) MarkNeighborPages(cell, key, true, false);
                return;
            }
            // 一格雾只占一个字典样本和一个坐标，不为每个稀疏远处的页预分配 8³ 份状态。
            // 删除时用末尾坐标填洞，修正其索引；重建只访问本页真正存在的材料。
            if (sample.IsEmpty)
            {
                int last = page.Cells.Count - 1;
                if (old.PageIndex != last)
                {
                    var movedCell = page.Cells[last];
                    page.Cells[old.PageIndex] = movedCell;
                    var moved = _samples[movedCell];
                    moved.PageIndex = old.PageIndex;
                    _samples[movedCell] = moved;
                }
                page.Cells.RemoveAt(last);
                _samples.Remove(cell);
            }
            else
            {
                sample.PageIndex = old.IsEmpty ? page.Cells.Count : old.PageIndex;
                if (old.IsEmpty) page.Cells.Add(cell);
                _samples[cell] = sample;
            }
            page.SurfaceCells += (sample.Quantity != 0 ? 1 : 0) - (old.Quantity != 0 ? 1 : 0);
            page.GasCells += (sample.HasGas ? 1 : 0) - (old.HasGas ? 1 : 0);
            // Draw 的可见条件要求本页有对应相。最后一格离开后立即令它失效，下一次
            // 该相进入时会重烘。跨相邻页的数据变化无需生成一张从来不会提交的空 Mesh/Texture。
            if (page.SurfaceCells == 0) page.HasSurface = page.SurfaceDirty = false;
            if (page.GasCells == 0) page.HasGasTexture = page.GasDirty = false;
            if (!page.SurfaceDirty && !page.GasDirty && page.Queued) DequeueVisibleDirty(page);
            if (gasChanged)
            {
                int local = LocalGasIndex(cell);
                if (sample.HasGas)
                {
                    float scale = 255f / _world.CellularCellCapacity;
                    page.GasDensity ??= new Dictionary<int, ushort>();
                    page.GasDensity[local] = (ushort)(EncodeDensity(sample.Steam, scale) | EncodeDensity(sample.Toxic, scale) << 8);
                }
                else page.GasDensity.Remove(local);
            }
            bool restingChanged = _resting.CellChanged(cell, sample.Quantity != 0, surfaceChanged);
            if (surfaceChanged)
            {
                // 显式格交还静水的当帧，旧 Mesh 不能和重新出现的压缩水体重合。其余活跃格按页重建。
                var resting = _world.CellularRestingLiquids;
                if (old.Quantity > 0 && sample.Quantity == 0 && resting != null && resting.TryGetCell(cell, out _, out _))
                    page.HasSurface = false;
            }
            if (surfaceChanged || restingChanged || gasChanged)
                MarkNeighborPages(cell, key, surfaceChanged || restingChanged, gasChanged);
            if (page.Cells.Count == 0) ReleasePage(page);
        }

        private void MarkNeighborPages(Vector3Int cell, ElementChunkKey key, bool surfaceChanged, bool gasChanged)
        {
            // 顶面角点和气体三线性过滤都读到对角邻格。只向共享当前细格 Halo 的已存在页传播脏标，
            // 在块内改一格只碰本页，在角落最多碰八页；不为了空邻居分配表现页。
            int lx = cell.x & (PageSize - 1), ly = cell.y & (PageSize - 1), lz = cell.z & (PageSize - 1);
            for (int z = key.Z - (lz == 0 ? 1 : 0); z <= key.Z + (lz == PageSize - 1 ? 1 : 0); z++)
                for (int y = key.Y - (ly == 0 ? 1 : 0); y <= key.Y + (ly == PageSize - 1 ? 1 : 0); y++)
                    for (int x = key.X - (lx == 0 ? 1 : 0); x <= key.X + (lx == PageSize - 1 ? 1 : 0); x++)
                        if (_pages.TryGetValue(new ElementChunkKey(x, y, z), out var neighbor))
                        {
                            neighbor.SurfaceDirty |= surfaceChanged && neighbor.SurfaceCells != 0;
                            neighbor.GasDirty |= gasChanged && neighbor.GasCells != 0;
                            QueueVisibleDirty(neighbor);
                        }
        }

        private static ElementChunkKey Key(Vector3Int cell)
            => new ElementChunkKey(cell.x >> PageShift, cell.y >> PageShift, cell.z >> PageShift);

        internal static bool IsCoordinateEdge(ElementChunkKey page)
        {
            const int low = int.MinValue >> PageShift, high = int.MaxValue >> PageShift;
            return page.X == low || page.X == high || page.Y == low || page.Y == high || page.Z == low || page.Z == high;
        }

        // 拓扑和纹理 Halo 都有一个格距。边界外为空；有符号坐标溢出的另一端不属于相邻世界。
        internal static bool OffsetWrapped(Vector3Int cell, Vector3Int delta, Vector3Int result)
            => delta.x < 0 && result.x > cell.x || delta.x > 0 && result.x < cell.x
                || delta.y < 0 && result.y > cell.y || delta.y > 0 && result.y < cell.y
                || delta.z < 0 && result.z > cell.z || delta.z > 0 && result.z < cell.z;

        internal Sample Read(Vector3Int cell)
            => _samples.TryGetValue(cell, out var sample) ? sample : default;

        internal Sample ReadSurface(Vector3Int cell)
        {
            var sample = Read(cell);
            if (sample.Quantity != 0 || _world.CellularRestingLiquids == null) return sample;
            if (_world.CellularRestingLiquids.TryGetCell(cell, out var material, out uint quantity))
            { sample.Material = material; sample.Quantity = quantity; sample.Resting = true; }
            return sample;
        }

        internal void SeedRestingInterfaces(CellularRestingLiquidSurface target)
        {
            // 只在冷启动执行。之后每个显式细格与邻近静水区域的关联由提交事件 O(1) 次 BVH 查询维护。
            foreach (var pair in _samples)
                if (pair.Value.Quantity != 0) target.CellChanged(pair.Key, true, true);
        }

        internal void SeedNewRestingRegion(BoundsInt bounds, HashSet<Vector3Int> touching)
        {
            // 新归档或复用的水池只需要本身与共享面的显式格。小池按页坐标直接找；
            // 范围较大时用已有的稀疏页树裁剪，避免读遍地图其他方向的动态页或逐个探测空坐标。
            var low = new ElementChunkKey(PreviousPage(bounds.xMin), PreviousPage(bounds.yMin), PreviousPage(bounds.zMin));
            var high = Key(bounds.max);
            if (_pages.Count == 0) return;
            double probes = (double)(high.X - low.X + 1) * (high.Y - low.Y + 1) * (high.Z - low.Z + 1);
            if (probes <= 32)
            {
                for (int z = low.Z; z <= high.Z; z++)
                    for (int y = low.Y; y <= high.Y; y++)
                        for (int x = low.X; x <= high.X; x++)
                            if (_pages.TryGetValue(new ElementChunkKey(x, y, z), out var page))
                                SeedPage(page, bounds, touching);
                return;
            }
            SeedRestingNode(PageTreeDepth, Vector3Int.zero, low, high, bounds, touching);
        }

        // 两个相邻的已提交样本，恰有一处同材同量消失和一处出现时，可连续显示独立水滴。
        // 采样模糊、Interest 切换或多滴分流不补造轨迹。已经完成的插值由 Shader 自动归零。
        internal Vector4 DropletArrival(Vector3Int cell, Sample arrival)
        {
            if (_departureFrame != arrival.ArrivalFrame || _departures.Count == 0 ||
                Time.time - arrival.ArrivalTime >= MotionDuration) return default;
            Vector3Int source = default;
            int matches = 0;
            foreach (var face in Faces)
            {
                var neighbor = cell + face;
                if (OffsetWrapped(cell, face, neighbor) || !_departures.TryGetValue(neighbor, out var previous)
                    || previous.Material != arrival.Material || previous.Quantity != arrival.Quantity || Read(neighbor).Quantity != 0) continue;
                source = neighbor;
                if (++matches > 1) return default;
            }
            if (matches != 1) return default;
            foreach (var face in Faces)
            {
                var other = source + face;
                if (other == cell || OffsetWrapped(source, face, other)) continue;
                var next = Read(other);
                if (next.Material == arrival.Material && next.Quantity == arrival.Quantity && next.ArrivalFrame == arrival.ArrivalFrame) return default;
            }
            var delta = source - cell;
            return new Vector4(delta.x, delta.y, delta.z, arrival.ArrivalTime + MotionDuration);
        }

        private static int PreviousPage(int value) => (value == int.MinValue ? value : value - 1) >> PageShift;

        private void SeedPage(Page page, BoundsInt bounds, HashSet<Vector3Int> touching)
        {
            if (page.SurfaceCells == 0) return;
            foreach (var cell in page.Cells)
            {
                if (Read(cell).Quantity == 0) continue;
                long distance = DistanceToAxis(cell.x, bounds.xMin, bounds.xMax)
                    + DistanceToAxis(cell.y, bounds.yMin, bounds.yMax) + DistanceToAxis(cell.z, bounds.zMin, bounds.zMax);
                if (distance <= 1) touching.Add(cell);
            }
        }

        private static long DistanceToAxis(int value, int min, int max)
            => value < min ? (long)min - value : value >= max ? (long)value - max + 1 : 0;

        internal static Camera ResolveCamera(ElementWorldRuntime world, Camera current)
        {
            if (world.ExecutionMode == WorldExecutionMode.Replica)
            {
                // OnlineRoom 的入场镜头、保留的 Hub 与正式角色镜头可以同时存在。可见页筛选和
                // DrawMesh 必须交给当前本机角色的相机，即使上一台相机仍然启用。
                var actor = LocalActorPresentation.Actor;
                if (actor != null && actor.scene == world.gameObject.scene && actor.activeInHierarchy &&
                    LocalActorCameraPresentation.TryGet(actor, out var bound) && bound.isActiveAndEnabled)
                    return bound;

                // 技术房间可以使用本场景的 MainCamera。无正式绑定时不沿用已解绑的相机或据点镜头。
                var fallback = Camera.main;
                return fallback != null && fallback.gameObject.scene == world.gameObject.scene ? fallback : null;
            }
            return current != null && current.isActiveAndEnabled ? current : Camera.main;
        }

        private void LateUpdate()
        {
            if (!_bound) TryBind();
            if (!_bound || _world == null || !_world.isActiveAndEnabled) { _frameStart = 0; return; }
            _camera = ResolveCamera(_world, _camera);
            if (_camera == null) { _frameStart = 0; return; }
            long previousFrameStart = _frameStart;
            _frameStart = System.Diagnostics.Stopwatch.GetTimestamp();
            long nativeStart = _frameStart;
            _resting.Refresh(_world.CellularRestingLiquids, this);
            float meters = _world.CellSize * PageSize;
            int limit = Mathf.Max(1, _maximumPageRebuildsPerFrame);
            CollectVisiblePages(meters);
            if (CaptureNativeFrames) BeginNativeDraw();
            // 真实 Tick 帧保留基础页数。即使处于积压状态，也只在最近帧有余量且队首已经等过
            // 一轮可见更新时借用额外时间。单页 Mesh/Texture 不可中断，页数仍有严格上限。
            int catchUp = _world.LastFrameTickCount == 0 && _catchUpRebuildMs > 0 ? Mathf.Max(limit,_maximumCatchUpPages) : limit;
            bool borrow = _visibleDirtyPages > limit && catchUp > limit;
            long deadline = borrow ? CatchUpDeadline(previousFrameStart) : 0;
            for (int rebuilt=0; _firstDirty!=null && rebuilt<catchUp; rebuilt++)
            {
                if(rebuilt>=limit && (!borrow || System.Diagnostics.Stopwatch.GetTimestamp()>=deadline))break;
                var page=_firstDirty;
                if (page.SurfaceDirty) RebuildSurface(page);
                if (page.GasDirty) RebuildGas(page);
                page.SurfaceDirty = page.GasDirty = false;
                DequeueVisibleDirty(page);
                if (CaptureNativeFrames) _nativeDraw.RebuiltPages++;
            }
            if (CaptureNativeFrames) { _nativeDraw.DirtyVisiblePages = _visibleDirtyPages; _nativeDraw.RebuildMs=NativeMilliseconds(_nativeRebuildStart); }
            for (int slot = 0; slot < CellularMaterialSurfaceMesh.LiquidSlots; slot++)
            {
                _surfaceProperties[slot].SetFloat(MotionClock, Time.time);
                _surfaceProperties[slot].SetFloat(MotionRate, 1f / MotionDuration);
            }
            _resting.Draw(_world, _camera, _frustum, _liquid, _solid, _surfaceProperties, gameObject.layer, this, limit);
            Vector3 benchmarkEye=CaptureNativeFrames?_camera.transform.position-Vector3.one*(meters*.5f):Vector3.zero;
            foreach (var page in _visiblePages)
            {
                Vector3 position = Position(page, meters);
                float distanceSquared = CaptureNativeFrames ? (position-benchmarkEye).sqrMagnitude : 0f;
                if(CaptureNativeFrames)RecordNativeAge(page);
                if (page.SurfaceCells > 0 && page.HasSurface)
                {
                    var matrix = Matrix4x4.TRS(position, Quaternion.identity, Vector3.one * _world.CellSize);
                    for (int slot=0,mask=page.SurfaceSlots; mask!=0; slot++,mask>>=1)
                    {
                        if((mask&1)==0)continue;
                        var material = slot < CellularMaterialSurfaceMesh.LiquidSlots ? _liquid : _solid;
                        if (material == null || CaptureNativeFrames && NativeHiddenMaterial != MaterialId.Empty
                            && slot == CellularMaterialSurfaceMesh.Slot(NativeHiddenMaterial)) continue;
                        Graphics.DrawMesh(page.Surface, matrix, material, gameObject.layer, _camera, slot,
                            _surfaceProperties[slot], ShadowCastingMode.On, true);
                        if (CaptureNativeFrames) RecordNativeSurface(slot,slot==CellularMaterialSurfaceMesh.Slot(MaterialId.Water)?page.WaterIndices:page.SandIndices,distanceSquared);
                    }
                }
                if (page.GasCells > 0 && page.HasGasTexture && _gas != null && (!CaptureNativeFrames || NativeHiddenMaterial != MaterialId.Steam))
                {
                    float t = Mathf.Clamp01((Time.time - page.GasBlendStart) / GasBlendSeconds);
                    _gasProperties.SetTexture(Density, page.GasTexture);
                    _gasProperties.SetVector(Layout, new Vector4(PageSize, TextureSize, meters, 0));
                    _gasProperties.SetFloat(Visibility, 1f);
                    _gasProperties.SetFloat(Blend, t * t * (3f - 2f * t));
                    _gasProperties.SetFloat(PresentationTime, Time.time);
                    Graphics.DrawMesh(_gasBounds, Matrix4x4.TRS(position, Quaternion.identity, Vector3.one * meters),
                        _gas, gameObject.layer, _camera, 0, _gasProperties, ShadowCastingMode.Off, false);
                    if (CaptureNativeFrames)
                    {
                        _nativeDraw.GasDraws++; _nativeDraw.GasCells += page.GasCells;
                        _nativeDraw.GasNearestSquared = Mathf.Min(_nativeDraw.GasNearestSquared, distanceSquared);
                    }
                }
            }
            if(CaptureNativeFrames)_nativeDraw.PresentationMs=NativeMilliseconds(nativeStart);
        }

        private long CatchUpDeadline(long previousFrameStart)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long frequency = System.Diagnostics.Stopwatch.Frequency;
            double budget = _catchUpRebuildMs;
            if (previousFrameStart != 0 && _firstDirty.QueuedAt != 0 &&
                (now - _firstDirty.QueuedAt) >= VisibleQueueDelaySeconds * frequency)
            {
                // 前一 LateUpdate 起点到当前重建的真实墙钟间隔，包含前次呈现、引擎/GPU 等待、
                // 当前 Update 与本帧剔除。保持至少 2ms 余量；指定高刷新帧率时使用更紧的目标。
                int targetRate = Math.Max(60, Application.targetFrameRate);
                double headroom = 1000d / targetRate - FrameReserveMs -
                    (now - previousFrameStart) * (1000d / frequency);
                double extra = Math.Min(MaximumExtraRebuildMs, Math.Min(budget, 4d - budget));
                if (extra > 0 && headroom > 0) budget += Math.Min(extra, headroom);
            }
            return now + (long)(budget * .001d * frequency);
        }

        private Vector3 Position(Page page, float meters)
            => _world.Origin + new Vector3(page.Key.X, page.Key.Y, page.Key.Z) * meters;

        private bool IsVisible(Page page, float meters)
            => GeometryUtility.TestPlanesAABB(_frustum, new Bounds(Position(page, meters) + Vector3.one * (.5f * meters), Vector3.one * (meters + 2f * _world.CellSize)));

        private void RebuildSurface(Page page)
        {
            page.HasSurface = false;
            if (page.SurfaceCells == 0) return;
            if (page.Surface == null)
            {
                page.Surface = new Mesh { name = "Cellular Material Surface", hideFlags = HideFlags.DontSave };
                page.Surface.MarkDynamic();
            }
            _mesher.Build(page.Surface, page.Key, page.Cells, this, _world.CellularCellCapacity);
            // 只有重烘此 Mesh 才改变 submesh 的索引。缓存几个 bit 后，整图每帧可省下上万次托管到
            // 原生 Mesh.GetIndexCount 调用；真正的 Draw 提交条件与网格生成时的同一份 index buffer 一致。
            page.SurfaceSlots=0;
            for(int slot=0;slot<CellularMaterialSurfaceMesh.SlotCount;slot++)
                if(_mesher.IndexCount(slot)>0)page.SurfaceSlots|=1<<slot;
            page.WaterIndices=(uint)_mesher.IndexCount(CellularMaterialSurfaceMesh.Slot(MaterialId.Water));
            page.SandIndices=(uint)_mesher.IndexCount(CellularMaterialSurfaceMesh.Slot(MaterialId.Sand));
            page.HasSurface = true;
        }

        private void RebuildGas(Page page)
        {
            if (page.GasCells == 0 || _gas == null) { page.HasGasTexture = false; return; }
            if (_gasBounds == null) _gasBounds = GasFieldVolumeRenderer.CreateCube();
            if (page.GasTexture == null)
            {
                page.GasTexture = new Texture3D(TextureSize, TextureSize, TextureSize, TextureFormat.RGBA32, false)
                { name = "Cellular Gas Density", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
                page.GasHistory = new Color32[TextureCells];
            }
            Array.Clear(_gasPixels, 0, _gasPixels.Length);
            for (int z = -1; z <= 1; z++) for (int y = -1; y <= 1; y++) for (int x = -1; x <= 1; x++)
            {
                // 渲染页坐标仅占 29 位；越过 int 世界边界的邻页不会出现在索引里。
                var neighbor = new ElementChunkKey(page.Key.X + x, page.Key.Y + y, page.Key.Z + z);
                if (_pages.TryGetValue(neighbor, out var source) && source.GasCells != 0)
                    CopyGasHalo(source.GasDensity, x, y, z, _gasPixels);
            }
            // 保留同一张 Texture 上的前后两份密度，复用原气体 Shader 的体积积分和深度截断。
            float blend = Mathf.Clamp01((Time.time - page.GasBlendStart) / GasBlendSeconds);
            GasDensitySnapshot.PackTemporalHistory(_gasPixels, page.GasHistory, 0, blend * blend * (3f - 2f * blend), !page.HasGasTexture);
            page.GasTexture.SetPixelData(_gasPixels, 0);
            page.GasTexture.Apply(false, false);
            page.GasBlendStart = Time.time;
            page.HasGasTexture = true;
        }

        private static byte EncodeDensity(uint quantity, float scale)
            => quantity == 0 ? (byte)0 : (byte)Mathf.Clamp(Mathf.RoundToInt(quantity * scale), 1, 255);

        internal static int LocalGasIndex(Vector3Int cell) => (cell.x & (PageSize - 1))
            | ((cell.y & (PageSize - 1)) << PageShift) | ((cell.z & (PageSize - 1)) << (PageShift * 2));

        // 每个邻页只贡献面、棱或一个角。稀疏页散写实际气体，稠密页读取所需矩形，
        // 因而单页访问数不超过 min(该页有气格数, 所需 Halo 格数)，不会遍历同页水、沙或固体。
        internal static void CopyGasHalo(Dictionary<int,ushort> gas, int dx, int dy, int dz, Color32[] pixels)
        {
            int x0 = dx < 0 ? PageSize - 1 : 0, x1 = dx > 0 ? 1 : PageSize;
            int y0 = dy < 0 ? PageSize - 1 : 0, y1 = dy > 0 ? 1 : PageSize;
            int z0 = dz < 0 ? PageSize - 1 : 0, z1 = dz > 0 ? 1 : PageSize;
            int start = (1 + dx * PageSize) + TextureSize * ((1 + dy * PageSize) + TextureSize * (1 + dz * PageSize));
            if (gas.Count < (x1-x0) * (y1-y0) * (z1-z0))
            {
                foreach (var pair in gas)
                {
                    int x = pair.Key & (PageSize-1), y = (pair.Key >> PageShift) & (PageSize-1), z = pair.Key >> (PageShift*2);
                    if (x >= x0 && x < x1 && y >= y0 && y < y1 && z >= z0 && z < z1)
                        pixels[start + x + TextureSize*(y+TextureSize*z)] = GasPixel(pair.Value);
                }
            }
            else
            {
                for (int z = z0; z < z1; z++) for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++)
                    if (gas.TryGetValue(x | (y << PageShift) | (z << (2*PageShift)), out ushort density))
                        pixels[start + x + TextureSize*(y+TextureSize*z)] = GasPixel(density);
            }
        }

        private static Color32 GasPixel(ushort density) => new Color32((byte)(density & 255), (byte)(density >> 8), 0, 0);

        private void ReleasePage(Page page)
        {
            RemoveVisiblePage(page);
            page.NativeWaiting=false;
            _pages.Remove(page.Key);
            IndexPage(page.Key, false);
            page.SurfaceDirty = page.GasDirty = page.HasSurface = page.HasGasTexture = false;
            if (_empty.Count < RetainedEmptyPages) _empty.Push(page);
            else DestroyPage(page);
        }

        private static void DestroyPage(Page page)
        {
            if (page.Surface != null) Destroy(page.Surface);
            if (page.GasTexture != null) Destroy(page.GasTexture);
        }

        private void OnDisable()
        {
            if (_bound && _world != null) _world.CellularContentsChanged -= RefreshCell;
            _bound = false;
            _visiblePages.Clear();
            ClearPageIndex();
            _samples.Clear();
            _departures.Clear(); _departureFrame = -1;
            _resting.Clear();
            foreach (var page in _pages.Values) DestroyPage(page);
            _pages.Clear();
            while (_empty.Count != 0) DestroyPage(_empty.Pop());
            if (_gasBounds != null) Destroy(_gasBounds);
            if (_ownsGas) Destroy(_gas);
            if (_ownsLiquid) Destroy(_liquid);
            if (_ownsSolid) Destroy(_solid);
            _ownsGas = _ownsLiquid = _ownsSolid = false;
            _gasBounds = null;
        }
    }
}
