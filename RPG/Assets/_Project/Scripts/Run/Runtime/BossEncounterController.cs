using Game.Combat;
using Game.Core;
using UnityEngine;

namespace Game.Run
{
    /// <summary>
    /// 地图二 Boss Encounter 的 Scene Authority。
    /// 它只根据绑定 HealthComponent 的 Instance ID 裁定开始、胜利与失败，
    /// 不读取 Boss FSM，也不会把同场其他对象的 DeathEvent 误判为结算。
    /// </summary>
    public sealed class BossEncounterController : MonoBehaviour
    {
        [SerializeField] private HealthComponent _bossHealth;
        [SerializeField] private HealthComponent _playerHealth;

        public BossEncounterState State { get; private set; } =
            BossEncounterState.Inactive;

        private void OnEnable()
        {
            EventBus<DeathEvent>.Subscribe(OnDeath);
        }

        private void OnDisable()
        {
            EventBus<DeathEvent>.Unsubscribe(OnDeath);
        }

        public bool TryBegin()
        {
            if (State != BossEncounterState.Inactive)
            {
                return false;
            }

            if (_bossHealth == null || _playerHealth == null)
            {
                GameLog.Error(
                    $"BossEncounterController '{name}' 缺少 Boss/Player Health 引用。",
                    "Run");
                return false;
            }

            State = BossEncounterState.Running;
            EventBus<BossEncounterStartedEvent>.Publish(
                new BossEncounterStartedEvent
                {
                    BossId = ResolveHealthId(_bossHealth),
                    PlayerId = ResolveHealthId(_playerHealth),
                });
            return true;
        }

        private void OnDeath(DeathEvent deathEvent)
        {
            if (State != BossEncounterState.Running)
            {
                return;
            }

            RunState result;
            int bossId = ResolveHealthId(_bossHealth);
            int playerId = ResolveHealthId(_playerHealth);
            if (deathEvent.TargetId == bossId)
            {
                State = BossEncounterState.Completed;
                result = RunState.Completed;
            }
            else if (deathEvent.TargetId == playerId)
            {
                State = BossEncounterState.Failed;
                result = RunState.Failed;
            }
            else
            {
                return;
            }

            EventBus<RunStateChangedEvent>.Publish(
                new RunStateChangedEvent
                {
                    PreviousState = RunState.Running,
                    CurrentState = result,
                    CurrentEncounterIndex = -1,
                });
        }

        private static int ResolveHealthId(HealthComponent health)
        {
            // inactive Scene Object 尚未执行 Awake 时 Id 为 0；GameObject Instance ID
            // 与 HealthComponent.Awake 最终写入的值相同，可安全作为提前绑定的身份。
            return health.Id != 0
                ? health.Id
                : health.gameObject.GetInstanceID();
        }
    }
}
