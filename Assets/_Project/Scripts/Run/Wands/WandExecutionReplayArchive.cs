using System;
using System.Collections.Generic;

namespace Game.Run
{
    /// <summary>Session诊断归档；后续服务替换不能把历史记录当作当前资源恢复。</summary>
    public sealed class WandExecutionReplayArchive
    {
        public const int Capacity = 3;
        private readonly List<WandExecutionReplay> _roots = new List<WandExecutionReplay>(Capacity);
        public int Count => _roots.Count;
        public bool TryGetLatest(string wandId, out WandExecutionReplay replay)
        {
            for (int i = _roots.Count - 1; i >= 0; i--)
                if (string.Equals(_roots[i].SourceWandId, wandId, StringComparison.Ordinal)) { replay = _roots[i]; return true; }
            replay = null; return false;
        }
        public WandExecutionReplay GetOldestFirst(int index) => _roots[index];
        internal void Add(WandExecutionReplay replay)
        {
            if (_roots.Count == Capacity) _roots.RemoveAt(0);
            // 淘汰只删除诊断引用，不能触碰仍在运行的Ticket/Graph。
            _roots.Add(replay);
        }
        internal void Clear() => _roots.Clear();
        internal bool TryGetTicket(string id,out WandExecutionReplay replay)
        {for(int i=_roots.Count-1;i>=0;i--)if(_roots[i].TicketId==id){replay=_roots[i];return true;}replay=null;return false;}
    }
}
