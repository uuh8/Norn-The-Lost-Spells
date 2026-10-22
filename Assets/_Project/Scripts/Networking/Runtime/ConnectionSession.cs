using System;

namespace Game.Networking
{
    /// <summary>单个本地实验连接的速率和计数，不表示已完成业务身份认证。</summary>
    public sealed class ConnectionSession
    {
        private readonly double _rate;
        private readonly int _burst;
        private double _tokens, _lastRefill;
        // 只在当前 Transport 进程有效，不是持久化账号/角色 ID；-1 表示空槽。
        public int ConnectionId { get; private set; } = -1;
        public ulong LastSequence { get; private set; }
        public long AcceptedMessages { get; private set; }
        public ConnectionSession(double messagesPerSecond = 20, int burstCapacity = 4)
        {
            if (!double.IsFinite(messagesPerSecond) || messagesPerSecond <= 0 || messagesPerSecond > 120 ||
                burstCapacity < 1 || burstCapacity > 64) throw new ArgumentOutOfRangeException(nameof(messagesPerSecond));
            _rate = messagesPerSecond;
            _burst = burstCapacity;
        }

        public void Attach(int connectionId)
        {
            ConnectionId = connectionId;
            LastSequence = 0;
            AcceptedMessages = 0;
            _lastRefill = 0;
            _tokens = _burst;
        }

        /// <summary>每连接独立预算；一个客户端不能占用其他玩家的速率窗口。</summary>
        // 角色重连时会复用槽；先拒绝旧连接，再读写序号/Token Bucket，避免迟到包污染新连接。
        public bool TryAccept(int connectionId, ulong sequence, double now) => connectionId == ConnectionId && TryAccept(sequence, now);

        public bool TryAccept(ulong sequence, double now)
        {
            if (ConnectionId < 0 || !double.IsFinite(now) || now < _lastRefill || sequence <= LastSequence) return false;
            // Token Bucket 允许有界聚合，避免 KCP 在同帧交付数包时被逐包间隔误杀。
            // 补充量上限固定；重复包不消耗 Token，也不能通过等待无限积攒突发额度。
            _tokens = Math.Min(_burst, _tokens + (now - _lastRefill) * _rate);
            _lastRefill = now;
            if (_tokens < 1) return false;
            _tokens--;
            LastSequence = sequence;
            AcceptedMessages++;
            return true;
        }

        public void Clear() => Attach(-1);
    }
}
