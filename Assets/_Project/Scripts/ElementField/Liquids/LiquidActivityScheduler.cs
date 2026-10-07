using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>格子有变化才入队；休眠仅停止求解，绝不清除 Store 或 Renderer 的数据。</summary>
    public sealed class LiquidActivityScheduler
    {
        private const int RecoveryCellsPerStep = 1024;
        private const int SleepingCellsPerStep = 128;
        private readonly Queue<Vector3Int> _queue;
        private readonly HashSet<Vector3Int> _queued;
        private readonly int _capacity;
        private readonly LiquidVolumeStore _store;
        private int _recoveryCursor;
        private int _recoveryEnd;
        private bool _recoveryAgain;
        private int _recoveryBudgetRemaining = RecoveryCellsPerStep;
        private readonly Queue<Vector3Int> _sleepingQueue;
        private readonly HashSet<Vector3Int> _sleeping;
        private ElementWorldActivityRegions _regions;
        private Vector3 _origin;
        private float _cellSize;
        private uint _domainSignature;
        private bool _hasUntrackedSleeping;
        private int _sleepingScanRemaining;
        public int SleepingCount=>_sleeping.Count;

        public LiquidActivityScheduler(int capacity, LiquidVolumeStore store)
        {
            if (capacity < 1 || store == null)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
            _store = store;
            _queue = new Queue<Vector3Int>(capacity);
            _queued = new HashSet<Vector3Int>(capacity);
            _sleepingQueue=new Queue<Vector3Int>(capacity);_sleeping=new HashSet<Vector3Int>(capacity);
        }

        public void SetActivityDomain(ElementWorldActivityRegions regions,Vector3 origin,float cellSize)
        {
            _regions=regions;_origin=origin;_cellSize=cellSize;
            uint signature=regions==null?0u:2166136261u;
            unchecked{if(regions!=null)for(int i=0;i<regions.Count;i++){var b=regions.GetBounds(i);signature=(signature^(uint)b.GetHashCode())*16777619u;}}
            if(signature==_domainSignature)return;_domainSignature=signature;
            // 活动区域只在Chunk边界／来源事件改变。重新分配已排队格不采样Collider，
            // 有界于初始化容量，避免远处旧队列排在新命中水之前导致求解饥饿。
            int count=_queue.Count;
            for(int i=0;i<count;i++)
            {var cell=_queue.Dequeue();_queued.Remove(cell);Wake(cell);}
            // 唤醒工作分摊到后续 Tick；切换区域时整批扫描会把旧湖的规模变成单帧尖峰。
            // 睡眠期间数量仍留在 Store，显示可以先读取，Solver 随队列逐步恢复。
            _sleepingScanRemaining = _sleepingQueue.Count;
            // 休眠列表是固定容量；未登记的远格仍存在Store，只有活动域改变才需要恢复扫描。
            // 若在远格入睡时不断设置RecoveryAgain，会让满列表的远湖每次求解都重扫全世界。
            if(_hasUntrackedSleeping)
            {
                _hasUntrackedSleeping=false;
                if(_recoveryEnd==0){_recoveryCursor=0;_recoveryEnd=_store.ResidentCellCount;}
                else _recoveryAgain=true;
            }
        }
        public bool IsInActivityDomain(Vector3Int cell)=>_regions==null || _regions.Contains(_origin+((Vector3)cell+Vector3.one*.5f)*_cellSize);
        public void BeginStep()
        {
            // Count 与 TryDequeue 都会推进恢复，必须共享整个 Tick 的预算。
            _recoveryBudgetRemaining = RecoveryCellsPerStep;
            PumpSleeping(SleepingCellsPerStep);
        }
        private void PumpSleeping(int budget)
        {
            int count=Math.Min(budget,Math.Min(_sleepingScanRemaining,_sleepingQueue.Count));
            for(int i=0;i<count;i++)
            {
                _sleepingScanRemaining--;
                var cell=_sleepingQueue.Dequeue();if(!_sleeping.Contains(cell))continue;
                if(_store.OccupancyAt(cell)==0){_sleeping.Remove(cell);continue;}
                if(IsInActivityDomain(cell)){_sleeping.Remove(cell);Wake(cell);}
                else _sleepingQueue.Enqueue(cell);
            }
        }
        private void Sleep(Vector3Int cell)
        {
            // 空邻格没有运动需要保存；实际GMU始终留在Store。满队列等待活动域变化再扫描，绝不删水。
            if(_store.OccupancyAt(cell)==0 || _sleeping.Contains(cell))return;
            if(_sleepingQueue.Count==_capacity)
            {_hasUntrackedSleeping=true;return;}
            _sleeping.Add(cell);_sleepingQueue.Enqueue(cell);
        }

        public int Count { get { PumpRecovery(); return _queue.Count; } }
        public bool HasPendingRecovery => _recoveryEnd > 0;
        public uint OverflowCount { get; private set; }

        public bool Wake(Vector3Int cell)
        {
            if(!IsInActivityDomain(cell)){Sleep(cell);return true;}
            _sleeping.Remove(cell);
            if (_queued.Contains(cell)) return true;
            if (_queue.Count == _capacity)
            {
                OverflowCount++;
                // 队列满后保留一次逐格恢复扫描；不能把漏掉的 Wake 当作已经休眠。
                if (_recoveryEnd == 0)
                {
                    _recoveryCursor = 0;
                    _recoveryEnd = _store.ResidentCellCount;
                }
                else _recoveryAgain = true;
                return true;
            }
            _queued.Add(cell);
            _queue.Enqueue(cell);
            return true;
        }

        public void WakeNeighbors(Vector3Int cell)
        {
            Wake(cell);
            Wake(cell + Vector3Int.up);
            Wake(cell + Vector3Int.down);
            Wake(cell + Vector3Int.right);
            Wake(cell + Vector3Int.left);
            Wake(cell + Vector3Int.forward);
            Wake(cell + Vector3Int.back);
        }

        public bool TryDequeue(out Vector3Int cell)
        {
            PumpRecovery();
            if (_queue.Count == 0) { cell = default; return false; }
            cell = _queue.Dequeue();
            _queued.Remove(cell);
            PumpRecovery();
            return true;
        }

        private void PumpRecovery()
        {
            // 预算只在 BeginStep 重置；一次 Tick 内取 N 个格也最多扫描 1024 格，
            // 避免原先 O(N * 1024) 的恢复工作。未扫描部分保留游标，下个 Tick 继续。
            while (_queue.Count < _capacity && _recoveryEnd > 0 && _recoveryBudgetRemaining > 0)
            {
                if (_recoveryCursor >= _recoveryEnd)
                {
                    if (_recoveryAgain)
                    {
                        _recoveryAgain = false;
                        _recoveryCursor = 0;
                        _recoveryEnd = _store.ResidentCellCount;
                    }
                    else _recoveryEnd = 0;
                    continue;
                }
                _recoveryBudgetRemaining--;
                int current = _recoveryCursor++;
                if (!_store.HasResidentLiquidAt(current)) continue;
                Vector3Int next = _store.GetResidentCellAt(current);
                if(!IsInActivityDomain(next)){Sleep(next);continue;}
                if (!_queued.Add(next)) continue;
                _queue.Enqueue(next);
            }
        }
    }
}
