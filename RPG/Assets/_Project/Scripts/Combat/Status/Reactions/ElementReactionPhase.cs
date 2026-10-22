namespace Game.Combat
{
    /// <summary>
    /// 一次元素反应在时间轴上的离散阶段通知。
    /// 它不是角色 FSM 的 State，也不保存反应进度；运行时只在阶段发生变化时发送它，
    /// 表现层可据此播放一次 VFX/SFX，而不必反向查询或控制战斗逻辑。
    /// </summary>
    public enum ElementReactionPhase : byte
    {
        /// <summary>满足门槛并创建反应 Process。</summary>
        Started = 0,

        /// <summary>反应仍存在，但因为抑制条件暂时停止连续积分。</summary>
        Paused = 1,

        /// <summary>抑制条件解除；这是一次通知，实际数值转换从下一模拟步继续。</summary>
        Resumed = 2,

        /// <summary>反应按规则完成，例如原料耗尽或伤害结算完成。</summary>
        Resolved = 3,

        /// <summary>反应被外部清理，或因有效消耗不足而未能结算。</summary>
        Cancelled = 4,
    }
}
