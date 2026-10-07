using System;
using UnityEngine;

namespace Game.ElementField
{
    public struct MaterialPhasePendingDeposit
    {
        public ElementWriteRequest Request;
        public uint Remaining;
        public int Cursor;
        public uint WaitingTicks;
    }
    /// <summary>固定环形队列；未落入实际空间的量继续属于 Pending，不静默丢弃。</summary>
    public sealed class MaterialPhaseDepositQueue
    {
        private readonly MaterialPhasePendingDeposit[] _entries;
        private int _head, _tail;
        public int Count { get; private set; }
        public ulong PendingQuantity { get; private set; }
        internal ulong MaterialQuantity(Game.Materials.MaterialId material)
        {
            ulong total=0; for(int i=0;i<Count;i++) { var entry=_entries[(_head+i)%_entries.Length]; if(entry.Request.MaterialKind==material) total+=entry.Remaining; }
            return total;
        }
        public MaterialPhaseDepositQueue(int capacity) { _entries = new MaterialPhasePendingDeposit[capacity]; }
        public bool Enqueue(in ElementWriteRequest request)
        {
            if (Count == _entries.Length || request.TotalAmount == 0 || request.Radius < 0
                || !float.IsFinite(request.Radius) || !Finite(request.WorldPosition) || !Finite(request.SurfaceNormal)) return false;
            var pending = new MaterialPhasePendingDeposit { Request = request, Remaining = request.TotalAmount };
            Requeue(in pending); return true;
        }
        public bool TryDequeue(out MaterialPhasePendingDeposit pending)
        {
            pending = default; if (Count == 0) return false;
            pending = _entries[_head]; _head = (_head + 1) % _entries.Length;
            Count--; PendingQuantity -= pending.Remaining; return true;
        }
        public void Requeue(in MaterialPhasePendingDeposit pending)
        {
            if (pending.Remaining == 0) return;
            if (Count == _entries.Length) throw new InvalidOperationException("内部 Pending 预约被破坏。");
            _entries[_tail] = pending; _tail = (_tail + 1) % _entries.Length;
            Count++; PendingQuantity += pending.Remaining;
        }
        public static Vector3Int Offset(int cursor, int radius)
        {
            // 中心优先，随后扫描邻域；没有随机分配和每帧排序。
            if (cursor == 0) return Vector3Int.zero;
            int side = radius * 2 + 1, index = cursor - 1;
            return new Vector3Int(index % side - radius, index / side % side - radius,
                index / (side * side) - radius);
        }
        private static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
    }
}
