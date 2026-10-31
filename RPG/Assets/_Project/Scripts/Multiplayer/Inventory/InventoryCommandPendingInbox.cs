using System;

namespace Game.Multiplayer
{
    /// <summary>
    /// 只保存本机Caller与已发送命令的关联，绝不是服务器幂等账本。固定容量防止断线/无回执时无限增长；
    /// 满时拒绝新的提交，不覆盖尚未应答的请求。连接身份改变时由通道清空，迟到回执不会投递给新UI。
    /// </summary>
    public sealed class InventoryCommandPendingInbox
    {
        private struct Entry { internal ulong Command; internal uint Epoch; internal int Caller; }
        private readonly Entry[] _entries = new Entry[128];

        public bool TryAdd(ulong command, uint epoch, int caller)
        {
            if (command == 0 || epoch == 0) return false;
            int free = -1;
            for (int i = 0; i < _entries.Length; i++)
            {
                if (_entries[i].Command == command && _entries[i].Epoch == epoch) return false;
                if (free < 0 && _entries[i].Command == 0) free = i;
            }
            if (free < 0) return false;
            _entries[free] = new Entry { Command = command, Epoch = epoch, Caller = caller }; return true;
        }

        public bool TryTake(ulong command, uint epoch, out int caller)
        {
            caller = 0;
            if (command == 0 || epoch == 0) return false;
            for (int i = 0; i < _entries.Length; i++)
                if (_entries[i].Command == command && _entries[i].Epoch == epoch)
                { caller = _entries[i].Caller; _entries[i] = default; return true; }
            return false;
        }

        public void Clear() => Array.Clear(_entries, 0, _entries.Length);
    }
}
