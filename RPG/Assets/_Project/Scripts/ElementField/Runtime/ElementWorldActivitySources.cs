using Game.Core;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>有界来源表。所有存储初始化预分配；移动时只读取 Transform，不扫描 Scene。</summary>
    internal sealed class ElementWorldActivitySources
    {
        private readonly Transform[] _sources = new Transform[256];
        private readonly WorldActivitySourceKind[] _kinds = new WorldActivitySourceKind[256];
        private int _unitCursor;
        private int _projectileCursor;
        internal int Count { get; private set; }
        internal int RejectedCount { get; private set; }
        internal int UnitBudgetSkippedCount { get; private set; }
        internal int ProjectileBudgetSkippedCount { get; private set; }
        internal void Clear()
        {
            System.Array.Clear(_sources, 0, _sources.Length);
            Count = 0;
            _unitCursor = 0;
            _projectileCursor = 0;
            UnitBudgetSkippedCount = 0;
            ProjectileBudgetSkippedCount = 0;
        }

        internal void Apply(WorldActivitySourceEvent change)
        {
            if (change.Source == null) return;
            int free = -1;
            for (int i = 0; i < _sources.Length; i++)
            {
                if (_sources[i] == change.Source)
                {
                    if (!change.Enabled) { _sources[i] = null; Count--; }
                    else _kinds[i] = change.Kind;
                    return;
                }
                if (_sources[i] == null && free < 0) free = i;
            }
            if (!change.Enabled) return;
            if (free < 0) { RejectedCount++; return; }
            _sources[free] = change.Source;
            _kinds[free] = change.Kind;
            Count++;
        }

        internal int AppendRegions(ElementWorldActivityRegions regions, WorldActivitySourceKind kind,
            float radius, int budget, Vector3 origin, float chunkWorldSize)
        {
            int accepted = 0;
            int skipped = 0;
            int start = kind == WorldActivitySourceKind.Unit ? _unitCursor : _projectileCursor;
            int nextCursor = start;
            bool budgetFilled = false;
            for (int offset = 0; offset < _sources.Length; offset++)
            {
                int i = (start + offset) & (_sources.Length - 1);
                Transform source = _sources[i];
                // Unity 已销毁对象会保留托管壳；及时回收槽位，避免长生命周期世界的来源表虚满。
                if (source == null)
                {
                    if (!ReferenceEquals(_sources[i], null))
                    {
                        _sources[i] = null;
                        Count--;
                    }
                    continue;
                }
                if (!source.gameObject.activeInHierarchy || _kinds[i] != kind) continue;
                if (budgetFilled)
                {
                    skipped++;
                    continue;
                }
                Vector3 min = (source.position - Vector3.one * radius - origin) / chunkWorldSize;
                Vector3 max = (source.position + Vector3.one * radius - origin) / chunkWorldSize;
                min = new Vector3(Mathf.Floor(min.x), Mathf.Floor(min.y), Mathf.Floor(min.z));
                max = new Vector3(Mathf.Floor(max.x) + 1, Mathf.Floor(max.y) + 1, Mathf.Floor(max.z) + 1);
                Bounds bounds = default;
                bounds.SetMinMax(origin + min * chunkWorldSize, origin + max * chunkWorldSize);
                if (accepted >= budget) { budgetFilled = true; continue; }
                if (regions.TryAdd(bounds))
                {
                    accepted++;
                    nextCursor = (i + 1) & (_sources.Length - 1);
                    if (accepted >= budget) budgetFilled = true;
                }
            }
            // 每种来源独立轮转；Unit 的 12 个名额不会改变 Projectile 的 6 个名额起点。
            if (kind == WorldActivitySourceKind.Unit)
            {
                _unitCursor = nextCursor;
                UnitBudgetSkippedCount = skipped;
            }
            else
            {
                _projectileCursor = nextCursor;
                ProjectileBudgetSkippedCount = skipped;
            }
            return accepted;
        }

        /// <summary>
        /// 权威查询完整固定来源表，不沿用单机轮转预算。任何区域装不下都返回失败，由Runtime锁存Faulted；
        /// 返回true才允许整份候选集合发布，禁止只凭RejectedCount记录日志后继续漏掉角色或投射物。
        /// </summary>
        internal bool TryAppendAll(ElementWorldActivityRegions regions, float unitRadius, float projectileRadius,
            Vector3 origin, float chunkWorldSize)
        {
            for (int i = 0; i < _sources.Length; i++)
            {
                Transform source = _sources[i];
                if (source == null)
                {
                    if (!ReferenceEquals(source, null)) { _sources[i] = null; Count--; }
                    continue;
                }
                if (!source.gameObject.activeInHierarchy) continue;
                float radius = _kinds[i] == WorldActivitySourceKind.Unit ? unitRadius : projectileRadius;
                if (!TryBuildBounds(source.position, radius, origin, chunkWorldSize, out Bounds bounds)
                    || !regions.TryAdd(bounds)) return false;
            }
            return true;
        }

        /// <summary>Actor与事件来源共用Chunk对齐，负坐标向下取整，确保同一位置自然去重。</summary>
        internal static bool TryBuildBounds(Vector3 position, float radius, Vector3 origin, float size, out Bounds bounds)
        {
            bounds = default;
            if (!float.IsFinite(size) || size <= 0 || !float.IsFinite(radius) || radius < 0) return false;
            Vector3 min = (position - Vector3.one * radius - origin) / size;
            Vector3 max = (position + Vector3.one * radius - origin) / size;
            min = new Vector3(Mathf.Floor(min.x), Mathf.Floor(min.y), Mathf.Floor(min.z));
            max = new Vector3(Mathf.Floor(max.x) + 1, Mathf.Floor(max.y) + 1, Mathf.Floor(max.z) + 1);
            bounds.SetMinMax(origin + min * size, origin + max * size);
            return float.IsFinite(bounds.min.x) && float.IsFinite(bounds.min.y) && float.IsFinite(bounds.min.z)
                && float.IsFinite(bounds.max.x) && float.IsFinite(bounds.max.y) && float.IsFinite(bounds.max.z)
                && bounds.size.x > 0 && bounds.size.y > 0 && bounds.size.z > 0;
        }
    }
}
