namespace Game.Character
{
    /// <summary>
    /// Wizard 跳跃动画的语义阶段。Locomotion 代表 Idle/Run 等非跳跃状态，
    /// Resolver 只返回阶段决策，不直接依赖 Animator，便于 EditMode 测试。
    /// </summary>
    public enum JumpAnimationPhase : byte
    {
        Locomotion,
        JumpStart,
        JumpAir,
        JumpEnd
    }

    /// <summary>
    /// 把 Gameplay 的接地状态与垂直速度转换为 Animator 可消费的下落阶段。
    /// 保持为纯函数，避免 Animator 转移规则反过来推测物理状态。
    /// </summary>
    public static class JumpAnimationPhaseResolver
    {
        private const float JumpStartFallbackNormalizedTime = 0.625f;
        private const float JumpEndMinimumNormalizedTime = 0.15f;

        public static bool IsFalling(bool isGrounded, float verticalVelocity)
        {
            return !isGrounded && verticalVelocity <= 0f;
        }

        /// <summary>
        /// 空中阶段优先服从真实下落状态；完整长跳则在 JumpStart 播放到合适姿势后进入循环。
        /// 这样短跳不会等待固定时长，长跳也不会让非循环的 JumpStart 卡住。
        /// </summary>
        public static JumpAnimationPhase ResolveAirbornePhase(
            JumpAnimationPhase currentPhase,
            float normalizedTime,
            bool isFalling)
        {
            if (currentPhase != JumpAnimationPhase.JumpStart)
                return JumpAnimationPhase.JumpAir;

            return isFalling || normalizedTime >= JumpStartFallbackNormalizedTime
                ? JumpAnimationPhase.JumpAir
                : JumpAnimationPhase.JumpStart;
        }

        /// <summary>
        /// 接地时跳跃阶段立即转入 JumpEnd，但保留很短的落地可读时间后才回到 Locomotion。
        /// </summary>
        public static JumpAnimationPhase ResolveGroundedPhase(
            JumpAnimationPhase currentPhase,
            float normalizedTime)
        {
            if (currentPhase == JumpAnimationPhase.JumpStart ||
                currentPhase == JumpAnimationPhase.JumpAir)
            {
                return JumpAnimationPhase.JumpEnd;
            }

            if (currentPhase == JumpAnimationPhase.JumpEnd &&
                normalizedTime >= JumpEndMinimumNormalizedTime)
            {
                return JumpAnimationPhase.Locomotion;
            }

            return currentPhase;
        }
    }
}
