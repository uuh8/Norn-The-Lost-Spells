using System;
using Game.Combat;
using Game.Core;
using UnityEngine;

namespace Game.Run
{
    /// <summary>
    /// 整局 Demo 的 Scene Adapter。
    /// 它把 ScriptableObject 中的 Encounter 顺序与 Scene 中的 Controller 引用装配起来，
    /// 但把顺序是否合法、当前索引和 Terminal State 继续交给纯 RunProgressTracker 裁定。
    /// </summary>
    public sealed class DemoRunController : MonoBehaviour
    {
        [Header("Run Authoring Data")]
        [SerializeField]
        [Tooltip("只保存顺序、目标文案与奖励等 Authoring Data，不保存 Runtime State。")]
        private DemoRunDefinition _definition;

        [SerializeField]
        [Tooltip("必须按照 Definition 的顺序填写 Scene 中的 Encounter Controller。")]
        private DemoEncounterController[] _encounters =
            Array.Empty<DemoEncounterController>();

        [SerializeField]
        [Tooltip("用于精确识别 Player Death。Run 只比较 DeathEvent.TargetId，不把 Enemy Death 误判为失败。")]
        private HealthComponent _playerHealth;

        private readonly RunProgressTracker _tracker = new RunProgressTracker();
        private bool _isInitialized;

        public RunState State => _tracker.State;

        public int CurrentEncounterIndex => _tracker.CurrentEncounterIndex;

        public DemoRunDefinition Definition => _definition;

        private void OnEnable()
        {
            // Encounter Controller 只发布已经发生的事实；整局控制器在这里消费事实并推进顺序。
            // 使用 OnEnable/OnDisable 对称订阅，避免换 Scene 或禁用对象后留下 stale subscriber。
            EventBus<EncounterStartedEvent>.Subscribe(OnEncounterStarted);
            EventBus<EncounterCompletedEvent>.Subscribe(OnEncounterCompleted);
            EventBus<DeathEvent>.Subscribe(OnDeath);
        }

        private void Start()
        {
            // 选择 Start 而不是 Awake：Unity 会先让场景中所有已启用对象完成 Awake，
            // 再进入 Start，因此遭遇、敌人和门等 Scene 引用已完成各自的基础初始化。
            // 这能降低多个 GameObject 之间依赖 Awake 执行先后顺序所产生的隐患。
            // Initialize 自带幂等保护；测试或其他 Bootstrap 若已提前调用，
            // 此处不会重复武装遭遇，也不会重复发布 RunStateChangedEvent。
            Initialize();
        }

        private void OnDisable()
        {
            EventBus<EncounterStartedEvent>.Unsubscribe(OnEncounterStarted);
            EventBus<EncounterCompletedEvent>.Unsubscribe(OnEncounterCompleted);
            EventBus<DeathEvent>.Unsubscribe(OnDeath);
        }

        /// <summary>
        /// 将不可变的 Authoring Data 与本 Scene 的对象引用结合起来。
        /// 当前方法显式调用而不放进 Update：初始化是一次性离散操作，不应成为逐帧热路径。
        /// 返回 bool 让调用方区分首次初始化和重复请求，避免重复 Event 与重复配置。
        /// </summary>
        public bool Initialize()
        {
            if (_isInitialized)
            {
                return false;
            }

            ValidateConfiguration();

            int encounterCount = _definition.EncounterCount;
            RunState previousState = _tracker.State;
            if (!_tracker.StartRun(encounterCount, _definition.CompletionMode))
            {
                return false;
            }

            for (int i = 0; i < encounterCount; i++)
            {
                DemoEncounterController encounter = _encounters[i];
                encounter.Initialize(i, encounterCount, _definition.GetEncounter(i));
            }

            // 只有当前索引 0 获得 Armed 资格；其他区域保持 Waiting，
            // 这是“空间上能提前走到”与“流程上允许开战”之间的权限边界。
            if (!_encounters[0].Arm())
            {
                throw new InvalidOperationException(
                    "The first encounter could not enter the Armed state.");
            }

            _isInitialized = true;

            EventBus<RunStateChangedEvent>.Publish(new RunStateChangedEvent
            {
                PreviousState = previousState,
                CurrentState = _tracker.State,
                CurrentEncounterIndex = _tracker.CurrentEncounterIndex
            });

            return true;
        }

        private void OnEncounterStarted(EncounterStartedEvent encounterEvent)
        {
            if (!_isInitialized)
            {
                return;
            }

            // 纯 Tracker 会拒绝错误索引、重复开始以及 Terminal State 下的事件，
            // 因此 Scene 中重复触发 Collider 不会破坏整局流程。
            _tracker.TryStartEncounter(encounterEvent.EncounterIndex);
        }

        private void OnEncounterCompleted(EncounterCompletedEvent encounterEvent)
        {
            if (!_isInitialized ||
                encounterEvent.EncounterIndex != _tracker.CurrentEncounterIndex)
            {
                return;
            }

            // Scene Adapter 必须保留迁移前的状态，才能把“发生了什么变化”完整通知给 UI。
            // Tracker 仍然是唯一裁定者；Controller 不自行判断“最后一关”，只消费它的结果。
            RunState previousState = _tracker.State;
            if (!_tracker.CompleteCurrentEncounter())
            {
                return;
            }

            if (_tracker.State != RunState.Running)
            {
                // 最终 Encounter 根据 Definition 进入 Completed 或 StageCleared。
                // UI 只把 Completed/Failed 当 Terminal；StageCleared 继续允许 Reward、Portal 与玩家操作。
                EventBus<RunStateChangedEvent>.Publish(new RunStateChangedEvent
                {
                    PreviousState = previousState,
                    CurrentState = _tracker.State,
                    CurrentEncounterIndex = _tracker.CurrentEncounterIndex
                });
                return;
            }

            // CompleteCurrentEncounter 已经把 CurrentEncounterIndex 推进一位，
            // 所以这里只授权新的当前区域；更后面的区域仍保持 Waiting。
            if (!_encounters[_tracker.CurrentEncounterIndex].Arm())
            {
                throw new InvalidOperationException(
                    "The next encounter could not enter the Armed state.");
            }
        }

        private void OnDeath(DeathEvent deathEvent)
        {
            if (!_isInitialized ||
                _playerHealth == null ||
                deathEvent.TargetId != _playerHealth.Id)
            {
                return;
            }

            RunState previousState = _tracker.State;
            if (!_tracker.FailRun())
            {
                return;
            }

            // RunProgressTracker 负责保证 Failed 是 Terminal State；这里只把唯一有效状态变化通知给 UI。
            EventBus<RunStateChangedEvent>.Publish(new RunStateChangedEvent
            {
                PreviousState = previousState,
                CurrentState = _tracker.State,
                CurrentEncounterIndex = _tracker.CurrentEncounterIndex
            });
        }

        private void ValidateConfiguration()
        {
            if (_definition == null)
            {
                GameLog.Error($"DemoRunController '{name}' has no Definition.", "Run");
                throw new InvalidOperationException(
                    "DemoRunController requires a DemoRunDefinition.");
            }

            int definitionCount = _definition.EncounterCount;
            if (definitionCount <= 0)
            {
                GameLog.Error($"DemoRunController '{name}' has an empty Definition.", "Run");
                throw new InvalidOperationException(
                    "DemoRunDefinition must contain at least one encounter.");
            }

            if (_encounters == null || _encounters.Length != definitionCount)
            {
                int sceneCount = _encounters?.Length ?? 0;
                GameLog.Error(
                    $"DemoRunController '{name}' count mismatch: " +
                    $"Definition={definitionCount}, Scene={sceneCount}.",
                    "Run");
                throw new InvalidOperationException(
                    "Definition encounter count must match Scene encounter count.");
            }

            if (_playerHealth == null)
            {
                GameLog.Error($"DemoRunController '{name}' has no Player Health.", "Run");
                throw new InvalidOperationException(
                    "DemoRunController requires the player's HealthComponent.");
            }

            for (int i = 0; i < _encounters.Length; i++)
            {
                if (_encounters[i] != null)
                {
                    continue;
                }

                GameLog.Error(
                    $"DemoRunController '{name}' contains a missing Encounter at index {i}.",
                    "Run");
                throw new InvalidOperationException(
                    $"Scene encounter reference at index {i} is missing.");
            }
        }
    }
}
