using Game.Combat;
using Game.Core;
using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class ElementWorldRuntime
    {
        private readonly ElementWorldActivitySources _activitySources = new ElementWorldActivitySources();
        private readonly ElementWorldActivityRegions _activityRegions = new ElementWorldActivityRegions();
        private readonly ElementWorldActivityRegions _liquidPrimaryRegion=new ElementWorldActivityRegions(1);
        private ElementWorldActivityRegions LiquidActivityDomain()
        {
            if(ActivityExpansionEnabled)return _activityRegions;
            _liquidPrimaryRegion.Clear();_liquidPrimaryRegion.TryAdd(GetActiveWorldBounds());return _liquidPrimaryRegion;
        }
        private Camera _activityCamera;
        private bool? _activityOverride;
        private readonly Bounds[] _writeActivityBounds = new Bounds[6];
        private readonly long[] _writeActivityExpiry = new long[6];
        public bool ActivityExpansionEnabled => _activityOverride ?? (_profile != null && _profile.EnableWorldActivityExpansion);
        public ElementWorldActivityRegions ActivityRegions => ActivityExpansionEnabled ? _activityRegions : null;
        public int ActivitySourceCount => _activitySources.Count;
        public int UnitActivitySourcesSkippedByBudget => _activitySources.UnitBudgetSkippedCount;
        public int ProjectileActivitySourcesSkippedByBudget => _activitySources.ProjectileBudgetSkippedCount;

        internal void ConfigureActivityForPerformance(bool enabled)
        {
            DisableActivitySources();
            _activityOverride = enabled;
            EnableActivitySources();
            if (!enabled && _gpuPbfFluidRuntime != null) _gpuPbfFluidRuntime.SetWorldActivityRegions(null);
        }

        private bool RetainWriteActivity(in ElementWriteRequest request)
        {
            if (!ActivityExpansionEnabled) return true;
            Vector3 position = request.WorldPosition;
            if (_activityRegions.Contains(position)) return true;
            long expiry = _worldTick + System.Math.Max(20, _profile.SleepGraceTicks);
            for (int i = 0; i < _writeActivityBounds.Length; i++)
            {
                if (_writeActivityExpiry[i] > _worldTick && _writeActivityBounds[i].Contains(position))
                { _writeActivityExpiry[i] = expiry; return true; }
            }
            for (int i = 0; i < _writeActivityBounds.Length; i++)
            {
                if (_writeActivityExpiry[i] <= _worldTick) return StoreWriteActivity(i, in request, expiry);
            }

            int oldest = 0;
            for (int i = 1; i < _writeActivityExpiry.Length; i++)
                if (_writeActivityExpiry[i] < _writeActivityExpiry[oldest]) oldest = i;
            // 租约预算只决定哪里优先推进，绝不能反向拒绝 Gameplay 写入；满时替换最早到期项。
            return StoreWriteActivity(oldest, in request, expiry);
        }

        private bool StoreWriteActivity(int index, in ElementWriteRequest request, long expiry)
        {
            float size = CellSize * ChunkSize;
            Vector3 center = request.WorldPosition;
            center.x = Mathf.Floor((center.x - _origin.x) / size) * size + _origin.x + size * .5f;
            center.y = Mathf.Floor((center.y - _origin.y) / size) * size + _origin.y + size * .5f;
            center.z = Mathf.Floor((center.z - _origin.z) / size) * size + _origin.z + size * .5f;
            float radius = (Mathf.Ceil(Mathf.Max(0, request.Radius) / size) + 1) * size;
            _writeActivityBounds[index] = new Bounds(center, Vector3.one * (radius * 2 + size));
            _writeActivityExpiry[index] = expiry;
            RefreshWorldActivity();
            return true;
        }

        private void EnableActivitySources()
        {
            EventBus<WorldActivitySourceEvent>.Subscribe(OnActivitySourceChanged);
            if (!ActivityExpansionEnabled) return;
            // 场景初始对象的 OnEnable 可能早于世界订阅；只在启用边界补扫，后续对象池由事件维护。
            foreach (StatusController unit in FindObjectsByType<StatusController>(FindObjectsSortMode.None))
                if (unit.isActiveAndEnabled) OnActivitySourceChanged(new WorldActivitySourceEvent
                    { Source = unit.transform, Kind = WorldActivitySourceKind.Unit, Enabled = true });
            foreach (ProjectileBase projectile in FindObjectsByType<ProjectileBase>(FindObjectsSortMode.None))
                if (projectile.isActiveAndEnabled) OnActivitySourceChanged(new WorldActivitySourceEvent
                    { Source = projectile.transform, Kind = WorldActivitySourceKind.Projectile, Enabled = true });
            _activityCamera = Camera.main;
            RefreshWorldActivity();
        }

        private void DisableActivitySources()
        {
            EventBus<WorldActivitySourceEvent>.Unsubscribe(OnActivitySourceChanged);
            _activitySources.Clear();
            _activityRegions.Clear();
        }

        private void OnActivitySourceChanged(WorldActivitySourceEvent change)
        {
            if (ActivityExpansionEnabled) _activitySources.Apply(change);
        }

        private void RefreshWorldActivity()
        {
            if (!ActivityExpansionEnabled || !IsInitialized) return;
            _activityRegions.Clear();
            _activityRegions.TryAdd(GetActiveWorldBounds());
            float size = CellSize * ChunkSize;
            _activitySources.AppendRegions(_activityRegions, WorldActivitySourceKind.Unit,
                _profile.UnitActivityRadius, 12, _origin, size);
            _activitySources.AppendRegions(_activityRegions, WorldActivitySourceKind.Projectile,
                _profile.ProjectileActivityRadius, 6, _origin, size);
            for (int i = 0; i < _writeActivityBounds.Length; i++)
            {
                if (_writeActivityExpiry[i] <= _worldTick) continue;
                // Pending Write 未消费前保留命中点租期，投射物销毁不能让 Cold 恢复请求失去活动域。
                if (_fluidStreamingEnabled && _fluidChunkStreamingRuntime.PendingWriteCount > 0)
                    _writeActivityExpiry[i] = _worldTick + 20;
                _activityRegions.TryAdd(_writeActivityBounds[i]);
            }
            // Fire Cell 数量由 Chunk 更新时缓存，区域收集无需再次扫描整个 Cell 数组。
            foreach (var pair in _store.Chunks)
            {
                if (!pair.Value.HasFire) continue;
                Bounds bounds = ChunkBounds(pair.Key);
                bounds.Expand(size * 2f);
                _activityRegions.TryAdd(bounds);
            }
            if (_gpuPbfFluidRuntime != null) _gpuPbfFluidRuntime.SetWorldActivityRegions(_activityRegions);
            if (_fluidStreamingEnabled) _fluidChunkStreamingRuntime.SetWorldActivityRegions(_activityRegions);
        }

        internal Bounds ChunkBounds(ElementChunkKey key)
        {
            float size = CellSize * ChunkSize;
            return new Bounds(_origin + new Vector3(key.X + .5f, key.Y + .5f, key.Z + .5f) * size,
                Vector3.one * size);
        }

        private bool IsGameplayChunk(ElementChunkKey key)
        {
            return ActivityExpansionEnabled && _activityRegions.Count > 0
                ? _activityRegions.Intersects(ChunkBounds(key))
                : ElementChunkActivityPlanner.IsInsideActiveRegion(key, _interestChunk,
                    _profile.ActiveRadiusXZChunks, _profile.ActiveRadiusYChunks);
        }

        private bool IsVisibleChunk(ElementChunkKey key) => ActivityExpansionEnabled
            ? GetVisibleWorldBounds().Intersects(ChunkBounds(key)) : IsGameplayChunk(key);

        public Bounds GetVisibleWorldBounds()
        {
            if (!ActivityExpansionEnabled || _activityCamera == null) return GetActiveWorldBounds();
            // Camera 只提供世界空间 culling 范围，不写入活动集合。远处静态残留可见不等于要恢复 PBF。
            float far = Mathf.Min(_activityCamera.farClipPlane, 64f);
            Bounds bounds = new Bounds(_activityCamera.transform.position, Vector3.zero);
            for (int i = 0; i < 4; i++)
                bounds.Encapsulate(_activityCamera.ViewportToWorldPoint(new Vector3(i & 1, i >> 1, far)));
            float step = CellSize * ChunkSize;
            Vector3 min = bounds.min / step, max = bounds.max / step;
            bounds.SetMinMax(new Vector3(Mathf.Floor(min.x), Mathf.Floor(min.y), Mathf.Floor(min.z)) * step,
                new Vector3(Mathf.Ceil(max.x), Mathf.Ceil(max.y), Mathf.Ceil(max.z)) * step);
            return bounds;
        }
    }
}
