using UnityEngine;

namespace Game.Character
{
    /// <summary>
    /// 管理单次跳跃的 Jump Cut 生命周期。该对象在 Awake 中预创建并复用，
    /// 每帧只执行数值判断，不产生 GC Alloc。
    /// </summary>
    public sealed class JumpCutRuntime
    {
        private readonly float _velocityMultiplier;
        private bool _isArmed;

        public JumpCutRuntime(float velocityMultiplier)
        {
            // RangeAttribute 只约束 Inspector；运行时仍夹紧一次，防止脚本或旧序列化数据传入非法值。
            _velocityMultiplier = Mathf.Clamp01(velocityMultiplier);
        }

        /// <summary>每次真正获得跳跃初速度时重新武装，保证普通跳、Coyote Jump、二段跳语义一致。</summary>
        public void Arm()
        {
            _isArmed = true;
        }

        /// <summary>
        /// 按住时保留完整上升速度；松键时仅截断一次。到达顶点后立即解除武装，
        /// 避免下降阶段或后续非跳跃产生的向上速度被旧输入误伤。
        /// </summary>
        public float Evaluate(float verticalVelocity, bool isJumpHeld)
        {
            if (!_isArmed)
                return verticalVelocity;

            if (verticalVelocity <= 0f)
            {
                _isArmed = false;
                return verticalVelocity;
            }

            if (isJumpHeld)
                return verticalVelocity;

            _isArmed = false;
            return verticalVelocity * _velocityMultiplier;
        }
    }
}
