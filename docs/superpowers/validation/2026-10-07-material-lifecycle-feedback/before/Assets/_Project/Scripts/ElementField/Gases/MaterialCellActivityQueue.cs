using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>只排队发生变化的格；溢出与区域恢复使用有界游标，不遗忘保存在 Store 的气体。</summary>
    public sealed class MaterialCellActivityQueue
    {
        private readonly Queue<Vector3Int> _queue;
        private readonly HashSet<Vector3Int> _queued;
        private readonly SparseMaterialVolumeStore _store;
        private readonly int _capacity;
        private ElementWorldActivityRegions _regions;
        private Vector3 _origin;
        private float _size = 1f;
        private uint _signature;
        private int _cursor;
        private bool _rescan, _rescanAgain;
        public int Count => _queue.Count;
        public bool HasPendingRecovery => _rescan;
        public MaterialCellActivityQueue(int capacity, SparseMaterialVolumeStore store)
        {
            if (capacity < 1 || store == null) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity; _store = store;
            _queue = new Queue<Vector3Int>(capacity); _queued = new HashSet<Vector3Int>(capacity);
        }
        public void SetDomain(ElementWorldActivityRegions regions, Vector3 origin, float size)
        {
            _regions = regions; _origin = origin; _size = size;
            uint signature = regions == null ? 0u : 2166136261u;
            unchecked { if (regions != null) for (int i = 0; i < regions.Count; i++) signature = (signature ^ (uint)regions.GetBounds(i).GetHashCode()) * 16777619u; }
            if (_signature == signature) return;
            _signature = signature; RequestRecovery();
        }
        public bool Contains(Vector3Int cell) => _regions == null
            || _regions.Contains(_origin + ((Vector3)cell + Vector3.one * .5f) * _size);
        public void Wake(Vector3Int cell)
        {
            if (!Contains(cell) || _queued.Contains(cell)) return;
            if (_queue.Count == _capacity) { RequestRecovery(); return; }
            _queued.Add(cell); _queue.Enqueue(cell);
        }
        public void WakeNeighbors(Vector3Int cell)
        {
            Wake(cell); Wake(cell + Vector3Int.up); Wake(cell + Vector3Int.down);
            Wake(cell + Vector3Int.right); Wake(cell + Vector3Int.left);
            Wake(cell + Vector3Int.forward); Wake(cell + Vector3Int.back);
        }
        public void RequestRecovery()
        {
            // 已经扫过的格也可能再次溢出。保留第二轮请求，避免恢复游标经过后永远遗漏它；
            // 进行中的游标不重置，移动区域不能持续把远端格饿死在队列之外。
            if (_rescan) _rescanAgain = true;
            else { _cursor = 0; _rescan = true; }
        }
        public void BeginStep(int scanBudget = 1024)
        {
            while (_rescan && scanBudget-- > 0 && _queue.Count < _capacity)
            {
                if (_cursor >= _store.ResidentCellCount)
                {
                    if (_rescanAgain) { _rescanAgain = false; _cursor = 0; }
                    else { _rescan = false; break; }
                }
                if (_store.TryGetCell(_cursor++, out MaterialVolumeCell cell)) Wake(cell.Cell);
            }
        }
        public bool TryDequeue(out Vector3Int cell)
        {
            if (_queue.Count > 0)
            {
                cell = _queue.Dequeue(); _queued.Remove(cell);
                return Contains(cell);
            }
            cell = default; return false;
        }
    }
}
