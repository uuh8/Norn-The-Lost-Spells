using UnityEngine;

namespace Game.Character
{
    /// <summary>
    /// 所有 Program 的可变运行态。数组在构造时一次性分配，Decision Tick 只原地读写。
    /// </summary>
    public sealed class BossProgramRuntimeState
    {
        private readonly float[] _cooldownRemaining;
        private readonly int[] _recentProgramIndices;
        private int _historyWriteIndex;
        private int _historyCount;

        public int ProgramCount => _cooldownRemaining.Length;
        public int HistoryCapacity => _recentProgramIndices.Length;

        public BossProgramRuntimeState(int programCount, int historyCapacity)
        {
            _cooldownRemaining = new float[Mathf.Max(0, programCount)];
            _recentProgramIndices = new int[Mathf.Max(0, historyCapacity)];
        }

        public void TickCooldowns(float deltaTime)
        {
            float delta = Mathf.Max(0f, deltaTime);
            for (int i = 0; i < _cooldownRemaining.Length; i++)
            {
                _cooldownRemaining[i] =
                    Mathf.Max(0f, _cooldownRemaining[i] - delta);
            }
        }

        public bool MarkUsed(int programIndex, float cooldown)
        {
            if (!IsValidProgramIndex(programIndex))
            {
                return false;
            }

            _cooldownRemaining[programIndex] = Mathf.Max(0f, cooldown);
            if (_recentProgramIndices.Length == 0)
            {
                return true;
            }

            _recentProgramIndices[_historyWriteIndex] = programIndex;
            _historyWriteIndex =
                (_historyWriteIndex + 1) % _recentProgramIndices.Length;
            _historyCount = Mathf.Min(
                _historyCount + 1,
                _recentProgramIndices.Length);
            return true;
        }

        public float GetCooldownRemaining(int programIndex)
        {
            return IsValidProgramIndex(programIndex)
                ? _cooldownRemaining[programIndex]
                : float.PositiveInfinity;
        }

        public int CountRecentUses(int programIndex)
        {
            if (!IsValidProgramIndex(programIndex))
            {
                return 0;
            }

            int count = 0;
            for (int i = 0; i < _historyCount; i++)
            {
                if (_recentProgramIndices[i] == programIndex)
                {
                    count++;
                }
            }

            return count;
        }

        private bool IsValidProgramIndex(int programIndex)
        {
            return programIndex >= 0 &&
                   programIndex < _cooldownRemaining.Length;
        }
    }
}
