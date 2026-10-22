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
        public const int MaximumRadius = 8;
        private static readonly Vector3Int[][] Offsets = BuildOffsets();
        private readonly MaterialPhasePendingDeposit[] _entries;
        private int _head, _tail;
        public int Count { get; private set; }
        public ulong PendingQuantity { get; private set; }
        internal ulong MaterialQuantity(Game.Materials.MaterialId material)
        {
            ulong total=0; for(int i=0;i<Count;i++) { var entry=_entries[(_head+i)%_entries.Length]; if(entry.Request.MaterialKind==material) total+=entry.Remaining; }
            return total;
        }
        public MaterialPhaseDepositQueue(int capacity)
        {
            // 显式在初始化访问静态表，避免第一次 Impact Tick 触发邻域分配/排序。
            _ = Offsets[0].Length;
            _entries = new MaterialPhasePendingDeposit[capacity];
        }
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
            var offsets=Offsets[Mathf.Clamp(radius,0,MaximumRadius)];
            return offsets[cursor%offsets.Length];
        }
        public static int SampleCount(int radius) => Offsets[Mathf.Clamp(radius,0,MaximumRadius)].Length;
        private static Vector3Int[][] BuildOffsets()
        {
            // Sphere 的有效格按平方距离由近到远排列；旧线性 Cube 顺序首先扫到大量角点/墙内格，
            // 即使还有写入预算，也会把一颗普通法术拆成数十个 Tick 的散落小格。
            var result=new Vector3Int[MaximumRadius+1][];
            for(int radius=0;radius<=MaximumRadius;radius++)
            {
                int count=0,limit=radius*radius;
                for(int z=-radius;z<=radius;z++) for(int y=-radius;y<=radius;y++) for(int x=-radius;x<=radius;x++)
                    if(x*x+y*y+z*z<=limit) count++;
                var offsets=new Vector3Int[count]; int cursor=0;
                for(int distance=0;distance<=limit;distance++)
                    for(int z=-radius;z<=radius;z++) for(int y=-radius;y<=radius;y++) for(int x=-radius;x<=radius;x++)
                        if(x*x+y*y+z*z==distance) offsets[cursor++]=new Vector3Int(x,y,z);
                result[radius]=offsets;
            }
            return result;
        }
        private static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
    }
}
