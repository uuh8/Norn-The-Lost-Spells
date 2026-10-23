namespace Game.Character
{
    /// <summary>
    /// 一次离地周期内可消费的空中能力预算。它不依赖 MonoBehaviour，
    /// 因而“何时重置、能否重复消费”可以用 EditMode Test 独立验证。
    /// </summary>
    public sealed class PlayerAirActionBudget
    {
        private readonly int _maxExtraJumps;
        private readonly int _maxAirDashes;
        private int _remainingExtraJumps;
        private int _remainingAirDashes;

        public int RemainingExtraJumps => _remainingExtraJumps;
        public int RemainingAirDashes => _remainingAirDashes;

        public PlayerAirActionBudget(int maxExtraJumps, int maxAirDashes)
        {
            _maxExtraJumps = maxExtraJumps < 0 ? 0 : maxExtraJumps;
            _maxAirDashes = maxAirDashes < 0 ? 0 : maxAirDashes;
            ResetForGrounded();
        }

        public void ResetForGrounded()
        {
            _remainingExtraJumps = _maxExtraJumps;
            _remainingAirDashes = _maxAirDashes;
        }

        /// <summary>校正不能调用 ResetForGrounded，否则每次收到 Snapshot 都会免费补充一次空中能力。</summary>
        public void RestoreRemaining(int extraJumps, int airDashes)
        {
            _remainingExtraJumps = extraJumps < 0 ? 0 : extraJumps > _maxExtraJumps ? _maxExtraJumps : extraJumps;
            _remainingAirDashes = airDashes < 0 ? 0 : airDashes > _maxAirDashes ? _maxAirDashes : airDashes;
        }

        public bool TryConsumeExtraJump()
        {
            if (_remainingExtraJumps <= 0)
            {
                return false;
            }

            _remainingExtraJumps--;
            return true;
        }

        public bool TryConsumeAirDash()
        {
            if (_remainingAirDashes <= 0)
            {
                return false;
            }

            _remainingAirDashes--;
            return true;
        }
    }
}
