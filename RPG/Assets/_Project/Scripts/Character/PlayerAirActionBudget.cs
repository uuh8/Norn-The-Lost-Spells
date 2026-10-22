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
