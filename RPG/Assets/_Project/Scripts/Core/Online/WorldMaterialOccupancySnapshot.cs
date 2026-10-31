using System;

namespace Game.Core
{
    public enum WorldOccupancyFailure : byte
    {
        None, InvalidInput, Capacity, InspectionBudget, QuantityOverflow, DuplicateOwner
    }

    /// <summary>
    /// 一份受限冻结块的逻辑占据读模型，消除Liquid Stable/Dynamic交接造成的假数量变化。
    /// 不拥有世界写权，不包含Pending、网格Geometry、Solver进度或网络Revision；调用方必须
    /// 另行固定世界/几何身份。不可把这里的结果直接作为完整Rendering或Authority恢复数据。
    /// 构造时分配双缓冲；同主线程调用，成功才交换引用发布，失败不会污染上次已发布状态。
    /// </summary>
    public sealed class WorldMaterialOccupancySnapshot
    {
        private WorldMaterialCellSample[] _published, _candidate;
        private readonly byte[] _owners;
        private readonly int _inspectionBudget, _capacity;
        public int Count { get; private set; }
        /// <summary>仅TryCapture成功时代表本次比较；失败保留上次成功的值。</summary>
        public bool Changed { get; private set; }
        public WorldOccupancyFailure LastFailure { get; private set; }

        public WorldMaterialOccupancySnapshot(int capacity, int inspectionBudget)
        {
            if (capacity <= 0 || capacity > int.MaxValue / 2) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (inspectionBudget <= 0) throw new ArgumentOutOfRangeException(nameof(inspectionBudget));
            // 每个逻辑Liquid键至多有Stable/Dynamic两个Owner；预留两倍原始容量，
            // 排序后再归并，避免为每条输入扫描已有前缀形成平方时间。
            _capacity = capacity;
            _published = new WorldMaterialCellSample[capacity * 2];
            _candidate = new WorldMaterialCellSample[capacity * 2];
            _owners = new byte[capacity]; _inspectionBudget = inspectionBudget;
        }

        /// <summary>返回值副本，禁止借出可写数组；索引只允许当前有效前缀。</summary>
        public WorldMaterialCellSample GetCell(int index)
        {
            if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
            return _published[index];
        }

        /// <summary>
        /// 输入必须来自同一提交边界。先固定缓冲采样，再用无递归HeapSort保证O(n log n)，
        /// 最后线性归并；不使用Array.Sort的比较委托或递归栈。扫描、比较、交换、归并和
        /// 最终相等检查均消耗预算，超额明确失败，仍须按受限Chunk调用。
        /// 输出Storage统一为Resident只表示“逻辑占据”，不声称物理Store已经改变表示。
        /// Liquid逻辑输出不可回灌此原始采样输入；后续Owner另保留原Source供渲染/表示恢复。
        /// </summary>
        public bool TryCapture(WorldMaterialCellSample[] cells, int count)
        {
            if (cells == null || count < 0 || count > cells.Length) return Reject(WorldOccupancyFailure.InvalidInput);
            int budget = _inspectionBudget, rawCount = 0, length = 0;
            for (int input = 0; input < count; input++)
            {
                if (!Inspect(ref budget)) return false;
                WorldMaterialCellSample value = cells[input];
                if (value.Quantity == 0) continue;
                if (!Valid(value)) return Reject(WorldOccupancyFailure.InvalidInput);
                if(rawCount == _candidate.Length)return Reject(WorldOccupancyFailure.Capacity);
                _candidate[rawCount++] = value;
            }
            if(!Sort(rawCount,ref budget))return false;
            for(int input=0;input<rawCount;input++)
            {
                if(!Inspect(ref budget))return false;
                WorldMaterialCellSample value=_candidate[input];
                byte owner=(byte)(1 << (int)value.Storage);
                if(length>0 && Compare(value,_candidate[length-1])==0)
                {
                    // 当前Stable区域不重叠、Dynamic格也唯一；同Owner重复意味着源枚举错误，
                    // 不能当作额外资源相加。只有Liquid跨Stable/Dynamic才是合法合并。
                    int position=length-1;
                    if (value.Domain != WorldMaterialDomain.Liquid || (_owners[position] & owner) != 0)
                        return Reject(WorldOccupancyFailure.DuplicateOwner);
                    uint old = _candidate[position].Quantity;
                    if (value.Quantity > uint.MaxValue - old) return Reject(WorldOccupancyFailure.QuantityOverflow);
                    _candidate[position] = Logical(value, old + value.Quantity);
                    _owners[position] |= owner;
                    continue;
                }
                if (length == _capacity) return Reject(WorldOccupancyFailure.Capacity);
                _candidate[length] = Logical(value, value.Quantity); _owners[length] = owner; length++;
            }
            bool changed = length != Count;
            for (int i = 0; !changed && i < length; i++)
            {
                if (!Inspect(ref budget)) return false;
                changed = Compare(_candidate[i], _published[i]) != 0 || _candidate[i].Quantity != _published[i].Quantity;
            }
            // 唯一发布点：此前无外部回调，也未改动旧数组/Count/Changed。交换后旧前缀成为scratch。
            WorldMaterialCellSample[] previous = _published; _published = _candidate; _candidate = previous;
            Count = length; Changed = changed; LastFailure = WorldOccupancyFailure.None; return true;
        }

        private bool Sort(int count,ref int budget)
        {
            for(int root=count/2-1;root>=0;root--)if(!Sift(root,count,ref budget))return false;
            for(int end=count-1;end>0;end--)
            {
                if(!Inspect(ref budget))return false;
                Swap(0,end);
                if(!Sift(0,end,ref budget))return false;
            }
            return true;
        }
        private bool Sift(int root,int count,ref int budget)
        {
            while(root<count/2)
            {
                int child=root*2+1;
                if(child+1<count)
                {
                    if(!Inspect(ref budget))return false;
                    if(Compare(_candidate[child],_candidate[child+1])<0)child++;
                }
                if(!Inspect(ref budget))return false;
                if(Compare(_candidate[root],_candidate[child])>=0)return true;
                if(!Inspect(ref budget))return false;
                Swap(root,child);root=child;
            }
            return true;
        }
        private void Swap(int a,int b)
        {WorldMaterialCellSample value=_candidate[a];_candidate[a]=_candidate[b];_candidate[b]=value;}

        private static bool Valid(in WorldMaterialCellSample value)
        {
            if ((byte)value.Domain > (byte)WorldMaterialDomain.Gas || value.GridId != (byte)value.Domain
                || value.MaterialId == 0) return false;
            if (value.Domain == WorldMaterialDomain.Liquid)
                return value.Storage == WorldMaterialStorage.Dynamic || value.Storage == WorldMaterialStorage.Stable;
            return value.Storage == WorldMaterialStorage.Resident
                && (value.Domain != WorldMaterialDomain.LegacyCell || value.Quantity <= byte.MaxValue);
        }
        private static WorldMaterialCellSample Logical(in WorldMaterialCellSample value, uint quantity)
            => new WorldMaterialCellSample(value.Domain, WorldMaterialStorage.Resident, value.Cell, value.MaterialId, quantity);

        private static int Compare(in WorldMaterialCellSample left, in WorldMaterialCellSample right)
        {
            // CompareTo不做坐标相减，负坐标与int极值不会产生减法溢出。
            int c = ((byte)left.Domain).CompareTo((byte)right.Domain);
            if (c != 0) return c;
            c = left.GridId.CompareTo(right.GridId); if (c != 0) return c;
            c = left.Cell.x.CompareTo(right.Cell.x); if (c != 0) return c;
            c = left.Cell.y.CompareTo(right.Cell.y); if (c != 0) return c;
            c = left.Cell.z.CompareTo(right.Cell.z); if (c != 0) return c;
            return left.MaterialId.CompareTo(right.MaterialId);
        }
        private bool Inspect(ref int budget)
        {
            if (budget == 0) return Reject(WorldOccupancyFailure.InspectionBudget);
            budget--; return true;
        }
        private bool Reject(WorldOccupancyFailure failure) { LastFailure = failure; return false; }
    }
}
