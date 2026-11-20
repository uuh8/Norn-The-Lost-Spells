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
    public sealed class CellularMaterialRenderer : MonoBehaviour
    {
        private const int PageShift = 3;
        internal const int PageSize = 1 << PageShift;
        private const int TextureSize = PageSize + 2;
        private const int TextureCells = TextureSize * TextureSize * TextureSize;
        private const int RetainedEmptyPages = 8;
        private const float GasBlendSeconds = .3f;
        [SerializeField] private Material _liquidSurface;
        [SerializeField] private Material _solidSurface;
        [SerializeField] private Material _gasVolume;
        [SerializeField, Min(1)] private int _maximumPageRebuildsPerFrame = 8;

        internal struct Sample
        {
            internal MaterialId Material;
            internal uint Quantity, Steam, Toxic;
            internal int PageIndex;
            internal bool Resting;
            internal bool HasGas => Steam != 0 || Toxic != 0;
            internal bool IsEmpty => Quantity == 0 && !HasGas;
        }

        private sealed class Page
        {
            internal readonly List<Vector3Int> Cells = new List<Vector3Int>(4);
            internal ElementChunkKey Key;
            internal int SurfaceCells, GasCells;
            internal bool SurfaceDirty, GasDirty, Queued, HasSurface, HasGasTexture;
            internal float GasBlendStart;
            internal Mesh Surface;
            internal Texture3D GasTexture;
            internal Color32[] GasHistory;
        }

        private static ElementWorldRuntime _observedWorld;
        private readonly Dictionary<ElementChunkKey, Page> _pages = new Dictionary<ElementChunkKey, Page>();
        private readonly Dictionary<Vector3Int, Sample> _samples = new Dictionary<Vector3Int, Sample>();
        private readonly Stack<Page> _empty = new Stack<Page>(RetainedEmptyPages);
        private readonly Queue<Page> _dirty = new Queue<Page>();
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
        }

        private static void ObserveWorld(ScriptableRenderContext context, Camera camera)
        {
            var world = ElementRuntimeRegistry.ActiveSink as ElementWorldRuntime;
            if (ReferenceEquals(world, _observedWorld)) return;
            _observedWorld = world;
            AttachToWorld(world);
        }

        internal static bool AttachToWorld(ElementWorldRuntime world)
        {
            if (world == null || world.ExecutionMode != WorldExecutionMode.SinglePlayer || !world.UsesCellularWorld)
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
                || !_world.UsesCellularWorld || _world.ExecutionMode != WorldExecutionMode.SinglePlayer
                || _world.CellularCellCapacity == 0) return;

            _liquid = ResolveMaterial(_liquidSurface, "Game/Elemental/Cartoon Liquid", true, out _ownsLiquid);
            _solid = ResolveMaterial(_solidSurface, "Game/Materials/FixedSolid", true, out _ownsSolid);
            var volumeRenderer = GetComponent<GasFieldVolumeRenderer>();
            var gasTemplate = _gasVolume != null || volumeRenderer == null ? _gasVolume : volumeRenderer.SharedVolumeMaterial;
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
            }
            _samples.TryGetValue(cell, out var old);
            bool surfaceChanged = old.Material != sample.Material || old.Quantity != sample.Quantity;
            bool gasChanged = old.Steam != sample.Steam || old.Toxic != sample.Toxic;
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
                            neighbor.SurfaceDirty |= surfaceChanged;
                            neighbor.GasDirty |= gasChanged;
                            if (neighbor.Queued) continue;
                            neighbor.Queued = true;
                            _dirty.Enqueue(neighbor);
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
            // 范围非常大且稀疏时遍历实际页，避免为大片没有动态材料的空间查询空页。
            var low = new ElementChunkKey(PreviousPage(bounds.xMin), PreviousPage(bounds.yMin), PreviousPage(bounds.zMin));
            var high = Key(bounds.max);
            double probes = (double)(high.X - low.X + 1) * (high.Y - low.Y + 1) * (high.Z - low.Z + 1);
            if (probes <= _pages.Count)
            {
                for (int z = low.Z; z <= high.Z; z++)
                    for (int y = low.Y; y <= high.Y; y++)
                        for (int x = low.X; x <= high.X; x++)
                            if (_pages.TryGetValue(new ElementChunkKey(x, y, z), out var page))
                                SeedPage(page, bounds, touching);
                return;
            }
            foreach (var page in _pages.Values)
            {
                var key = page.Key;
                if (key.X >= low.X && key.X <= high.X && key.Y >= low.Y && key.Y <= high.Y && key.Z >= low.Z && key.Z <= high.Z)
                    SeedPage(page, bounds, touching);
            }
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

        private void LateUpdate()
        {
            if (!_bound) TryBind();
            if (!_bound || _world == null || !_world.isActiveAndEnabled) return;
            if (_camera == null || !_camera.isActiveAndEnabled) _camera = Camera.main;
            if (_camera == null) return;
            GeometryUtility.CalculateFrustumPlanes(_camera, _frustum);
            _resting.Refresh(_world.CellularRestingLiquids, this);
            float meters = _world.CellSize * PageSize;
            int limit = Mathf.Max(1, _maximumPageRebuildsPerFrame);
            int queued = _dirty.Count;
            for (int i = 0, rebuilt = 0; i < queued && rebuilt < limit; i++)
            {
                var page = _dirty.Dequeue();
                if (page.SurfaceCells == 0 && page.GasCells == 0)
                {
                    ReleasePage(page);
                    continue;
                }
                if (!IsVisible(page, meters)) { _dirty.Enqueue(page); continue; }
                page.Queued = false;
                if (page.SurfaceDirty) RebuildSurface(page);
                if (page.GasDirty) RebuildGas(page);
                page.SurfaceDirty = page.GasDirty = false;
                rebuilt++;
            }
            _resting.Draw(_world, _camera, _frustum, _liquid, _surfaceProperties, gameObject.layer, this);
            foreach (var page in _pages.Values)
            {
                if (!IsVisible(page, meters)) continue;
                Vector3 position = Position(page, meters);
                if (page.SurfaceCells > 0 && page.HasSurface)
                {
                    var matrix = Matrix4x4.TRS(position, Quaternion.identity, Vector3.one * _world.CellSize);
                    for (int slot = 0; slot < CellularMaterialSurfaceMesh.SlotCount; slot++)
                    {
                        var material = slot < CellularMaterialSurfaceMesh.LiquidSlots ? _liquid : _solid;
                        if (material != null && page.Surface.GetIndexCount(slot) != 0)
                            Graphics.DrawMesh(page.Surface, matrix, material, gameObject.layer, _camera, slot,
                                _surfaceProperties[slot], ShadowCastingMode.On, true);
                    }
                }
                if (page.GasCells > 0 && page.HasGasTexture && _gas != null)
                {
                    float t = Mathf.Clamp01((Time.time - page.GasBlendStart) / GasBlendSeconds);
                    _gasProperties.SetTexture(Density, page.GasTexture);
                    _gasProperties.SetVector(Layout, new Vector4(PageSize, TextureSize, meters, 0));
                    _gasProperties.SetFloat(Visibility, 1f);
                    _gasProperties.SetFloat(Blend, t * t * (3f - 2f * t));
                    _gasProperties.SetFloat(PresentationTime, Time.time);
                    Graphics.DrawMesh(_gasBounds, Matrix4x4.TRS(position, Quaternion.identity, Vector3.one * meters),
                        _gas, gameObject.layer, _camera, 0, _gasProperties, ShadowCastingMode.Off, false);
                }
            }
        }

        private Vector3 Position(Page page, float meters)
            => _world.Origin + new Vector3(page.Key.X, page.Key.Y, page.Key.Z) * meters;

        private bool IsVisible(Page page, float meters)
            => GeometryUtility.TestPlanesAABB(_frustum, new Bounds(Position(page, meters) + Vector3.one * (.5f * meters), Vector3.one * meters));

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
            Vector3Int start = new Vector3Int(page.Key.X, page.Key.Y, page.Key.Z) * PageSize;
            bool edgePage = IsCoordinateEdge(page.Key);
            float scale = 255f / _world.CellularCellCapacity;
            int index = 0;
            for (int z = 0; z < TextureSize; z++) for (int y = 0; y < TextureSize; y++) for (int x = 0; x < TextureSize; x++)
            {
                var delta = new Vector3Int(x - 1, y - 1, z - 1);
                var coordinate = start + delta;
                var cell = edgePage && OffsetWrapped(start, delta, coordinate) ? default : Read(coordinate);
                _gasPixels[index++] = new Color32(EncodeDensity(cell.Steam, scale), EncodeDensity(cell.Toxic, scale), 0, 0);
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

        private void ReleasePage(Page page)
        {
            _pages.Remove(page.Key);
            page.Queued = page.SurfaceDirty = page.GasDirty = page.HasSurface = page.HasGasTexture = false;
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
            _dirty.Clear();
            _samples.Clear();
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
