namespace Game.Networking
{
    /// <summary>仅显式实验开启；按 Datagram 而非应用消息确定性丢弃，KCP 必须自行恢复可靠流。</summary>
    public sealed class DatagramLossGate
    {
        private int _attempts;
        public bool Enabled { get; set; }
        public long Dropped { get; private set; }
        public bool ShouldDrop()
        {
            if (!Enabled) return false;
            _attempts++;
            if (_attempts < 20) return false;
            _attempts = 0; Dropped++; return true;
        }
    }
}
