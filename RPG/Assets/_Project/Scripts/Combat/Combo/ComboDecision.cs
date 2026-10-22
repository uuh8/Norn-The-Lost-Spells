namespace Game.Combat
{
    /// <summary>ComboResolver.Resolve 的输出：本帧连段该做什么。</summary>
    public enum ComboDecision : byte
    {
        Continue = 0, // 维持当前段，继续播放
        Advance  = 1, // 推进到下一段
        End      = 2, // 连段结束，退出攻击态
    }
}
