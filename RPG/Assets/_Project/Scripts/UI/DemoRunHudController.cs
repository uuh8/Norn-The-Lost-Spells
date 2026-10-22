using System.Collections;
using Game.Core;
using Game.Run;
using Game.Skills;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>
    /// P7 局内 HUD 的只读 Presentation Adapter。
    /// 它只消费 Game.Run 发布的权威快照，不轮询 Scene，也不自行推断 Enemy Death 或 Victory。
    /// </summary>
    public sealed class DemoRunHudController : MonoBehaviour
    {
        [Header("Run Data")]
        [SerializeField] private DemoRunController _runController;

        [Header("Gameplay HUD")]
        [SerializeField] private GameObject _gameplayRoot;
        [SerializeField] private Text _objectiveText;
        [SerializeField] private Text _encounterText;
        [SerializeField] private Text _remainingEnemyText;

        [Header("Reusable Notification")]
        [SerializeField] private GameObject _notificationRoot;
        [SerializeField] private Text _notificationText;
        [SerializeField] private Image _notificationIcon;
        [SerializeField, Min(0.1f)] private float _notificationDuration = 2.5f;

        private Coroutine _notificationRoutine;
        private int _currentEncounterIndex = -1;

        private void OnEnable()
        {
            EventBus<EncounterStartedEvent>.Subscribe(OnEncounterStarted);
            EventBus<EncounterProgressChangedEvent>.Subscribe(OnEncounterProgressChanged);
            EventBus<EncounterCompletedEvent>.Subscribe(OnEncounterCompleted);
            EventBus<SpellRewardGrantedEvent>.Subscribe(OnSpellRewardGranted);
            EventBus<RunStateChangedEvent>.Subscribe(OnRunStateChanged);
        }

        private void Start()
        {
            SetNotificationVisible(false);
            if (_gameplayRoot != null)
            {
                _gameplayRoot.SetActive(true);
            }

            if (_runController != null && _runController.State == RunState.Running)
            {
                ShowEncounterAuthoring(_runController.CurrentEncounterIndex);
                SetRemainingEnemyCount(0);
            }
        }

        private void OnDisable()
        {
            EventBus<EncounterStartedEvent>.Unsubscribe(OnEncounterStarted);
            EventBus<EncounterProgressChangedEvent>.Unsubscribe(OnEncounterProgressChanged);
            EventBus<EncounterCompletedEvent>.Unsubscribe(OnEncounterCompleted);
            EventBus<SpellRewardGrantedEvent>.Unsubscribe(OnSpellRewardGranted);
            EventBus<RunStateChangedEvent>.Unsubscribe(OnRunStateChanged);

            if (_notificationRoutine != null)
            {
                StopCoroutine(_notificationRoutine);
                _notificationRoutine = null;
            }
        }

        private void OnEncounterStarted(EncounterStartedEvent encounterEvent)
        {
            _currentEncounterIndex = encounterEvent.EncounterIndex;
            ShowEncounterAuthoring(encounterEvent.EncounterIndex);
            SetRemainingEnemyCount(encounterEvent.EnemyCount);

            string displayName = GetEncounterDisplayName(encounterEvent.EncounterIndex);
            ShowNotification($"进入 {displayName}", null);
        }

        private void OnEncounterProgressChanged(EncounterProgressChangedEvent progressEvent)
        {
            if (progressEvent.EncounterIndex != _currentEncounterIndex)
            {
                return;
            }

            SetRemainingEnemyCount(progressEvent.RemainingEnemyCount);
        }

        private void OnEncounterCompleted(EncounterCompletedEvent encounterEvent)
        {
            if (encounterEvent.EncounterIndex == _currentEncounterIndex)
            {
                SetRemainingEnemyCount(0);
            }

            string displayName = GetEncounterDisplayName(encounterEvent.EncounterIndex);
            ShowNotification($"已清理 {displayName}", null);
        }

        private void OnSpellRewardGranted(SpellRewardGrantedEvent rewardEvent)
        {
            SpellDefinition spell = rewardEvent.Spell;
            string displayName = spell != null && !string.IsNullOrWhiteSpace(spell.DisplayName)
                ? spell.DisplayName
                : "新法术";
            Sprite icon = spell != null ? spell.Icon : null;
            ShowNotification($"获得 {displayName}\nTab 打开背包配置法杖", icon);
        }

        private void OnRunStateChanged(RunStateChangedEvent runEvent)
        {
            if (runEvent.CurrentState == RunState.Running)
            {
                _currentEncounterIndex = runEvent.CurrentEncounterIndex;
                ShowEncounterAuthoring(runEvent.CurrentEncounterIndex);
                return;
            }

            if ((runEvent.CurrentState == RunState.Completed ||
                 runEvent.CurrentState == RunState.Failed) &&
                _gameplayRoot != null)
            {
                _gameplayRoot.SetActive(false);
            }
        }

        private void ShowEncounterAuthoring(int encounterIndex)
        {
            if (_runController == null || _runController.Definition == null)
            {
                return;
            }

            DemoRunDefinition definition = _runController.Definition;
            if (encounterIndex < 0 || encounterIndex >= definition.EncounterCount)
            {
                return;
            }

            DemoEncounterDefinition encounter = definition.GetEncounter(encounterIndex);
            if (_objectiveText != null)
            {
                _objectiveText.text = encounter.ObjectiveText;
            }

            if (_encounterText != null)
            {
                _encounterText.text =
                    $"区域 {encounterIndex + 1}/{definition.EncounterCount}  {encounter.DisplayName}";
            }
        }

        private string GetEncounterDisplayName(int encounterIndex)
        {
            if (_runController == null || _runController.Definition == null ||
                encounterIndex < 0 ||
                encounterIndex >= _runController.Definition.EncounterCount)
            {
                return $"区域 {encounterIndex + 1}";
            }

            return _runController.Definition.GetEncounter(encounterIndex).DisplayName;
        }

        private void SetRemainingEnemyCount(int remainingCount)
        {
            if (_remainingEnemyText != null)
            {
                _remainingEnemyText.text = $"剩余敌人：{remainingCount}";
            }
        }

        private void ShowNotification(string message, Sprite icon)
        {
            if (_notificationText != null)
            {
                _notificationText.text = message;
            }

            if (_notificationIcon != null)
            {
                _notificationIcon.sprite = icon;
                _notificationIcon.enabled = icon != null;
            }

            SetNotificationVisible(true);
            if (_notificationRoutine != null)
            {
                StopCoroutine(_notificationRoutine);
            }

            // Notification 是低频离散事件；使用 Realtime 等待，确保 Time.timeScale=0 时提示仍能正常收起。
            _notificationRoutine = StartCoroutine(HideNotificationAfterDelay());
        }

        private IEnumerator HideNotificationAfterDelay()
        {
            yield return new WaitForSecondsRealtime(_notificationDuration);
            SetNotificationVisible(false);
            _notificationRoutine = null;
        }

        private void SetNotificationVisible(bool visible)
        {
            if (_notificationRoot != null)
            {
                _notificationRoot.SetActive(visible);
            }
        }
    }
}
