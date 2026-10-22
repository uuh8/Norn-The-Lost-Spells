using System;
using System.Collections.Generic;

namespace Game.Run
{
    /// <summary>
    /// 单个 Encounter 的纯规则内核：只认识 Combat 提供的 TargetId，不认识 GameObject、
    /// Collider 或 Enemy Controller。这样死亡去重和清场规则可以脱离 Scene 做 NUnit 测试。
    /// </summary>
    public sealed class EncounterProgressTracker
    {
        private readonly HashSet<int> _enemyIds = new HashSet<int>();
        private readonly HashSet<int> _deadEnemyIds = new HashSet<int>();
        private bool _isConfigured;

        public EncounterState State { get; private set; } = EncounterState.Waiting;

        public int RemainingEnemyCount => _enemyIds.Count - _deadEnemyIds.Count;

        public bool IsCleared => State == EncounterState.Cleared;

        /// <summary>
        /// Scene Adapter 在开战前一次性提交成员 ID。这里允许离散配置时使用 HashSet，
        /// 换取 O(1) 成员检查和重复 DeathEvent 去重；它不在 Update 热路径中执行。
        /// </summary>
        public void Configure(IEnumerable<int> enemyIds)
        {
            if (enemyIds == null)
            {
                throw new ArgumentNullException(nameof(enemyIds));
            }

            _enemyIds.Clear();
            _deadEnemyIds.Clear();

            foreach (int enemyId in enemyIds)
            {
                if (!_enemyIds.Add(enemyId))
                {
                    _enemyIds.Clear();
                    throw new ArgumentException(
                        "Encounter enemyIds contains a duplicate TargetId.",
                        nameof(enemyIds));
                }
            }

            if (_enemyIds.Count == 0)
            {
                throw new ArgumentException(
                    "Encounter must contain at least one enemy TargetId.",
                    nameof(enemyIds));
            }

            _isConfigured = true;
            State = EncounterState.Waiting;
        }

        public bool TryArm()
        {
            if (!_isConfigured || State != EncounterState.Waiting)
            {
                return false;
            }

            State = EncounterState.Armed;
            return true;
        }

        public bool TryBegin()
        {
            if (State != EncounterState.Armed)
            {
                return false;
            }

            State = EncounterState.Active;
            return true;
        }

        /// <summary>
        /// 返回 true 表示这次 Death 确实被本 Encounter 首次接纳；场外 ID、重复事件、
        /// 非 Active 阶段全部返回 false，防止迟到事件提前开门或覆盖后续状态。
        /// </summary>
        public bool RecordDeath(int targetId)
        {
            if (State != EncounterState.Active ||
                !_enemyIds.Contains(targetId) ||
                !_deadEnemyIds.Add(targetId))
            {
                return false;
            }

            if (_deadEnemyIds.Count == _enemyIds.Count)
            {
                State = EncounterState.Cleared;
            }

            return true;
        }
    }
}
