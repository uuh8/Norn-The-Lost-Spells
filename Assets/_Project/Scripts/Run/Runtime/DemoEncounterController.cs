using System;
using Game.Combat;
using Game.Core;
using UnityEngine;

namespace Game.Run
{
    /// <summary>
    /// 单个战斗区域的 Scene Adapter：把 Inspector 中的 Enemy/Gate 引用转换为纯 Tracker 输入，
    /// 再把 Tracker 接纳的状态变化转换为激活对象、开关门和发布 Event 等 Unity Side Effect。
    /// </summary>
    public sealed class DemoEncounterController : MonoBehaviour
    {
        [Header("Scene Members")]
        [SerializeField]
        [Tooltip("只填写属于本区域的 Enemy HealthComponent。Enemy 可在 Scene 中初始 inactive。")]
        private HealthComponent[] _enemyMembers = Array.Empty<HealthComponent>();

        [SerializeField]
        [Tooltip("本区开战时关闭、清场后打开的门。可以同时控制入口门和出口门。")]
        private ArenaGate[] _gates = Array.Empty<ArenaGate>();

        private readonly EncounterProgressTracker _tracker = new EncounterProgressTracker();
        private DemoEncounterDefinition _definition;
        private int _encounterIndex = -1;
        private int _encounterCount;
        private bool _isInitialized;

        public EncounterState State => _tracker.State;

        public int RemainingEnemyCount => _tracker.RemainingEnemyCount;

        public int EncounterIndex => _encounterIndex;
        public int EnemyCount=>_enemyMembers.Length;
        public HealthComponent GetEnemy(int index)=>_enemyMembers[index];

        private void OnEnable()
        {
            EventBus<DeathEvent>.Subscribe(OnDeath);
        }

        private void OnDisable()
        {
            EventBus<DeathEvent>.Unsubscribe(OnDeath);
        }

        /// <summary>
        /// DemoRunController 在 Scene 启动时调用一次，把 Definition 的顺序元数据与本区 Scene 引用结合。
        /// 注意：初始 inactive 的 GameObject 尚未执行 Awake，因此 HealthComponent.Id 可能仍为 0；
        /// 这里读取 gameObject.GetInstanceID()。HealthComponent 激活后写入的 Id 与它完全相同。
        /// </summary>
        public void Initialize(
            int encounterIndex,
            int encounterCount,
            DemoEncounterDefinition definition)
        {
            if (_isInitialized)
            {
                throw new InvalidOperationException($"Encounter '{name}' was initialized more than once.");
            }

            if (encounterIndex < 0 || encounterIndex >= encounterCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(encounterIndex),
                    encounterIndex,
                    "Encounter index must be inside the configured run.");
            }

            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            if (_enemyMembers == null || _enemyMembers.Length == 0)
            {
                GameLog.Error($"Encounter '{name}' has no Enemy members.", "Run");
                throw new InvalidOperationException("DemoEncounterController requires at least one Enemy member.");
            }

            var enemyIds = new int[_enemyMembers.Length];
            for (int i = 0; i < _enemyMembers.Length; i++)
            {
                HealthComponent enemy = _enemyMembers[i];
                if (enemy == null)
                {
                    GameLog.Error($"Encounter '{name}' contains a missing Enemy reference at index {i}.", "Run");
                    throw new InvalidOperationException("DemoEncounterController contains a missing Enemy reference.");
                }

                enemyIds[i] = enemy.gameObject.GetInstanceID();
                enemy.gameObject.SetActive(false);
            }

            _tracker.Configure(enemyIds);
            _encounterIndex = encounterIndex;
            _encounterCount = encounterCount;
            _definition = definition;
            BindChildRewards();
            _isInitialized = true;
            SetGatesClosed(false);
        }

        private void BindChildRewards()
        {
            // 奖励在关卡完成前通常处于隐藏状态，因此必须包含 inactive 子物体。
            // GetComponentsInChildren 会分配数组，但初始化每局只执行一次，不属于 Update 热路径。
            SpellRewardPickup[] rewardPickups = GetComponentsInChildren<SpellRewardPickup>(true);
            for (int i = 0; i < rewardPickups.Length; i++)
            {
                rewardPickups[i].BindEncounter(_encounterIndex);
            }

            DungeonFinalRewardPickup[] allSpellsRewards =
                GetComponentsInChildren<DungeonFinalRewardPickup>(true);
            for (int i = 0; i < allSpellsRewards.Length; i++)
            {
                allSpellsRewards[i].BindEncounter(_encounterIndex);
            }

            StageExitPortal[] stageExitPortals =
                GetComponentsInChildren<StageExitPortal>(true);
            for (int i = 0; i < stageExitPortals.Length; i++)
            {
                stageExitPortals[i].BindEncounter(_encounterIndex);
            }
        }

        /// <summary>
        /// 只有 DemoRunController 可以按顺序为当前区域取得 Armed 资格。
        /// Armed 不等于开战；玩家仍需真正进入 Entry Trigger。
        /// </summary>
        public bool Arm()
        {
            return _isInitialized && _tracker.TryArm();
        }

        /// <summary>
        /// Entry Trigger 验证 Player Layer 后调用。返回 true 才表示首次有效开战，
        /// 因此重复 Trigger 不会重复激活 Enemy、关闭门或发布 Started Event。
        /// </summary>
        public bool TryBeginEncounter()
        {
            if (!_isInitialized || !_tracker.TryBegin())
            {
                return false;
            }

            SetGatesClosed(true);

            for (int i = 0; i < _enemyMembers.Length; i++)
            {
                _enemyMembers[i].gameObject.SetActive(true);
            }

            EventBus<EncounterStartedEvent>.Publish(new EncounterStartedEvent
            {
                EncounterIndex = _encounterIndex,
                EncounterCount = _encounterCount,
                EnemyCount = _enemyMembers.Length
            });

            return true;
        }

        private void OnDeath(DeathEvent deathEvent)
        {
            if (!_tracker.RecordDeath(deathEvent.TargetId))
            {
                return;
            }

            EventBus<EncounterProgressChangedEvent>.Publish(
                new EncounterProgressChangedEvent
                {
                    EncounterIndex = _encounterIndex,
                    EncounterCount = _encounterCount,
                    RemainingEnemyCount = _tracker.RemainingEnemyCount
                });

            if (!_tracker.IsCleared)
            {
                return;
            }

            SetGatesClosed(false);

            EventBus<EncounterCompletedEvent>.Publish(new EncounterCompletedEvent
            {
                EncounterIndex = _encounterIndex,
                EncounterCount = _encounterCount,
                RewardSpell = _definition.RewardSpell
            });
        }

        private void SetGatesClosed(bool closed)
        {
            if (_gates == null)
            {
                return;
            }

            for (int i = 0; i < _gates.Length; i++)
            {
                ArenaGate gate = _gates[i];
                if (gate == null)
                {
                    GameLog.Warn($"Encounter '{name}' contains a missing Gate reference.", "Run");
                    continue;
                }

                gate.SetClosed(closed);
            }
        }
    }
}
