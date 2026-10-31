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
        private IDungeonPresentationReadOnly _presentationSource;
        private bool _hasPresentationOverride;
        private bool _onlineFrozen;
        private bool _onlineViewSubscribed;
        private int _notificationWorldSceneHandle;
        private int _lastStartedArea = -1, _lastCompletedArea = -1;

        // Stage 的文字与通知是两棵独立叶树。共同 HUD 接管时只能隐藏它们，
        // 不能把含 Pause/Guide 等兄弟内容的 DemoRunUIRoot 或整张 Canvas 一起关闭。
        public GameObject GameplayPresentationRoot => _gameplayRoot;
        public GameObject NotificationPresentationRoot => _notificationRoot;

        // EventBus 使用多播委托快照。前一个回调即使禁用并退订本组件，本次分发仍可能调用后续旧回调；
        // 因而事件入口还须检查 enabled，而公开 Bind 继续支持 Host 在 inactive 树上预先装配来源。
        private bool CanReceivePresentationEvent => this != null && enabled;

        /// <summary>
        /// 共同Host显式交接线上读源，单机原_runController仍保留原路径。null也代表明确的等待/解绑，
        /// 不能在重连或离开副本时悄悄回落到另一个单机Controller，把P7的目标文本留在P8画面上。
        /// 同一稳定Source内部只换进度，重复绑定不重置通知去重或制造第二份UI。
        /// </summary>
        public void BindPresentationSource(IDungeonPresentationReadOnly source)
        {
            // Source 本身不暴露 Scene/网络权限；只认 Core 已安装的当前缓存，不能把未知 World(0)
            // 当作全局广播接收者。这样即使旧副本的视图最后到达，也没有资格覆盖新副本的通知。
            int world = source != null && ReferenceEquals(DungeonPresentation.Source, source)
                ? DungeonPresentation.WorldSceneHandle : 0;
            if (!_hasPresentationOverride || !ReferenceEquals(_presentationSource, source) || _notificationWorldSceneHandle != world)
            {
                _lastStartedArea = _lastCompletedArea = -1;
                _onlineFrozen = false;
                ClearNotification();
            }
            _hasPresentationOverride = true; _presentationSource = source; _notificationWorldSceneHandle = world;
            RefreshBoundProgress();
            RequestOnlineMovementView();
        }

        private void OnEnable()
        {
            EventBus<EncounterStartedEvent>.Subscribe(OnEncounterStarted);
            EventBus<EncounterProgressChangedEvent>.Subscribe(OnEncounterProgressChanged);
            EventBus<EncounterCompletedEvent>.Subscribe(OnEncounterCompleted);
            EventBus<SpellRewardGrantedEvent>.Subscribe(OnSpellRewardGranted);
            EventBus<RunStateChangedEvent>.Subscribe(OnRunStateChanged);
            EventBus<OnlineCastFeedbackEvent>.Subscribe(OnOnlineCastFeedback);
            EventBus<OnlineMovementPresentationEvent>.Subscribe(OnOnlineMovementView);
            _onlineViewSubscribed = true;
            // EventBus 不保留历史：HUD 晚创建、Source 晚到、disable 后重启，都需要主动读取一次当前视图。
            // 必须先完成订阅再请求，因为 Bootstrap 可在同一调用栈同步发布回答。
            RequestOnlineMovementView();
        }

        private void Start()
        {
            if (!_onlineFrozen) SetNotificationVisible(false);
            // Host 可在 Start 前绑定来源。先走同一个线上刷新入口，避免把 Boss 区域的
            // Gameplay 叶树短暂 SetActive(true) 后再关闭，触发一次无意义的启用和布局重建。
            if (_hasPresentationOverride) { RefreshBoundProgress(); return; }
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
            _onlineViewSubscribed = false;
            EventBus<EncounterStartedEvent>.Unsubscribe(OnEncounterStarted);
            EventBus<EncounterProgressChangedEvent>.Unsubscribe(OnEncounterProgressChanged);
            EventBus<EncounterCompletedEvent>.Unsubscribe(OnEncounterCompleted);
            EventBus<SpellRewardGrantedEvent>.Unsubscribe(OnSpellRewardGranted);
            EventBus<RunStateChangedEvent>.Unsubscribe(OnRunStateChanged);
            EventBus<OnlineCastFeedbackEvent>.Unsubscribe(OnOnlineCastFeedback);
            EventBus<OnlineMovementPresentationEvent>.Unsubscribe(OnOnlineMovementView);
            _onlineFrozen = false;
            ClearNotification();
        }

        /// <summary>
        /// 原通知面板接收本机拒绝收据，样式、位置、字体继续来自 P7/P8 authoring。
        /// EventBus 是全局的；只有 Host 显式绑定了在线来源才接收，不能让据点/单机 HUD 响应在线残留事件。
        /// 收据已经由网络层校验当前连接和 Actor，这里只翻译显示，不裁决资源或重发施法。
        /// </summary>
        private void OnOnlineCastFeedback(OnlineCastFeedbackEvent feedback)
        {
            if (!CanReceivePresentationEvent) return;
            if (!HasOnlineNotificationSource() || _onlineFrozen || feedback.Failure == OnlineCastFailure.None) return;
            string message = feedback.Failure switch
            {
                OnlineCastFailure.ResourceRejected => "当前法力或法术次数不足",
                OnlineCastFailure.EmptyOutput => "没有可执行的法术，请检查法杖和剩余次数",
                OnlineCastFailure.UnsupportedMaterial => "此法术的材料互动暂未支持联机",
                OnlineCastFailure.NotOwned or OnlineCastFailure.StaleProgram => "法杖状态已变化，请重新连接后重试",
                OnlineCastFailure.InvalidInput => "瞄准位置无效，请重新瞄准",
                OnlineCastFailure.Interrupted => "施法已取消",
                _ => "角色当前无法施法"
            };
            ShowNotification(message, null);
        }

        private void OnOnlineMovementView(OnlineMovementPresentationEvent view)
        {
            if (!CanReceivePresentationEvent) return;
            if (!HasOnlineNotificationSource() || view.WorldSceneHandle != _notificationWorldSceneHandle) return;
            if (!view.Active) { _onlineFrozen = false; ClearNotification(); return; }
            if (_onlineFrozen == view.Frozen) return; // MenuOpen 等视图变化不反复刷新文字/启动 Coroutine。
            _onlineFrozen = view.Frozen;
            if (_onlineFrozen) ShowNotification("连接暂时中断，战斗已暂停；Esc打开联机菜单", null, true);
            else ShowNotification("连接已恢复", null);
        }

        private bool HasOnlineNotificationSource() => _hasPresentationOverride && _notificationWorldSceneHandle != 0 &&
            DungeonPresentation.TryGet(_notificationWorldSceneHandle, out var current) && ReferenceEquals(current, _presentationSource);

        private void RequestOnlineMovementView()
        {
            if (_onlineViewSubscribed && HasOnlineNotificationSource())
                EventBus<OnlineMovementViewRequestedEvent>.Publish(new OnlineMovementViewRequestedEvent
                    { WorldSceneHandle = _notificationWorldSceneHandle });
        }

        private void OnEncounterStarted(EncounterStartedEvent encounterEvent)
        {
            if (!CanReceivePresentationEvent) return;
            if (_hasPresentationOverride)
            {
                if (!TryReadBoundProgress(out var progress) || progress.Phase != OnlineDungeonPhase.Active ||
                    progress.EncounterIndexPlusOne != encounterEvent.EncounterIndex + 1) return;
                RefreshBoundProgress();
                if (_lastStartedArea != encounterEvent.EncounterIndex)
                { _lastStartedArea = encounterEvent.EncounterIndex; ShowNotification($"进入 {GetEncounterDisplayName(encounterEvent.EncounterIndex)}", null); }
                return;
            }
            _currentEncounterIndex = encounterEvent.EncounterIndex;
            ShowEncounterAuthoring(encounterEvent.EncounterIndex);
            SetRemainingEnemyCount(encounterEvent.EnemyCount);

            string displayName = GetEncounterDisplayName(encounterEvent.EncounterIndex);
            ShowNotification($"进入 {displayName}", null);
        }

        private void OnEncounterProgressChanged(EncounterProgressChangedEvent progressEvent)
        {
            if (!CanReceivePresentationEvent) return;
            // 旧Scene的全局事件可能迟到；线上始终从当前Source读完整事实，不信事件里剩余数的另一份副本。
            if (_hasPresentationOverride) { RefreshBoundProgress(); return; }
            if (progressEvent.EncounterIndex != _currentEncounterIndex)
            {
                return;
            }

            SetRemainingEnemyCount(progressEvent.RemainingEnemyCount);
        }

        private void OnEncounterCompleted(EncounterCompletedEvent encounterEvent)
        {
            if (!CanReceivePresentationEvent) return;
            if (_hasPresentationOverride)
            {
                if (!TryReadBoundProgress(out var progress)) return;
                int current = (int)progress.EncounterIndexPlusOne - 1;
                if (encounterEvent.EncounterIndex < 0 || encounterEvent.EncounterIndex >= _presentationSource.EncounterCount ||
                    encounterEvent.EncounterIndex > current || encounterEvent.EncounterIndex == current && progress.Phase != OnlineDungeonPhase.StageCleared) return;
                RefreshBoundProgress();
                if (encounterEvent.EncounterIndex > _lastCompletedArea)
                { _lastCompletedArea = encounterEvent.EncounterIndex; ShowNotification($"已清理 {GetEncounterDisplayName(encounterEvent.EncounterIndex)}", null); }
                return;
            }
            if (encounterEvent.EncounterIndex == _currentEncounterIndex)
            {
                SetRemainingEnemyCount(0);
            }

            string displayName = GetEncounterDisplayName(encounterEvent.EncounterIndex);
            ShowNotification($"已清理 {displayName}", null);
        }

        private void OnSpellRewardGranted(SpellRewardGrantedEvent rewardEvent)
        {
            if (!CanReceivePresentationEvent) return;
            if (_hasPresentationOverride) return; // 单机奖励事件不是线上资产或线上通知的授权。
            SpellDefinition spell = rewardEvent.Spell;
            string displayName = spell != null && !string.IsNullOrWhiteSpace(spell.DisplayName)
                ? spell.DisplayName
                : "新法术";
            Sprite icon = spell != null ? spell.Icon : null;
            ShowNotification($"获得 {displayName}\nTab 打开背包配置法杖", icon);
        }

        private void OnRunStateChanged(RunStateChangedEvent runEvent)
        {
            if (!CanReceivePresentationEvent) return;
            // 单人Failed事件不能隐藏整支线上队伍的HUD；线上状态只来自已安装的副本快照。
            if (_hasPresentationOverride) { RefreshBoundProgress(); return; }
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
            if (_hasPresentationOverride)
            {
                if (_presentationSource == null || encounterIndex < 0 || encounterIndex >= _presentationSource.EncounterCount) return;
                if (_objectiveText != null) _objectiveText.text = _presentationSource.GetObjectiveText(encounterIndex) ?? string.Empty;
                if (_encounterText != null) _encounterText.text = $"区域 {encounterIndex + 1}/{_presentationSource.EncounterCount}  {_presentationSource.GetDisplayName(encounterIndex)}";
                return;
            }
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
            if (_hasPresentationOverride) return _presentationSource != null ? _presentationSource.GetDisplayName(encounterIndex) ?? string.Empty : string.Empty;
            if (_runController == null || _runController.Definition == null ||
                encounterIndex < 0 ||
                encounterIndex >= _runController.Definition.EncounterCount)
            {
                return $"区域 {encounterIndex + 1}";
            }

            return _runController.Definition.GetEncounter(encounterIndex).DisplayName;
        }

        private bool TryReadBoundProgress(out OnlineDungeonProgress progress)
        {
            progress = default;
            return _presentationSource != null && !(_presentationSource is Object value && value == null) &&
                _presentationSource.TryCaptureProgress(out progress) && progress.EncounterIndexPlusOne >= 1 &&
                progress.EncounterIndexPlusOne <= _presentationSource.EncounterCount;
        }
        private void RefreshBoundProgress()
        {
            if (!_hasPresentationOverride) return;
            if (!TryReadBoundProgress(out var progress))
            {
                _currentEncounterIndex = -1;
                if (_objectiveText != null) _objectiveText.text = string.Empty;
                if (_encounterText != null) _encounterText.text = string.Empty;
                if (_remainingEnemyText != null) _remainingEnemyText.text = string.Empty;
                if (_gameplayRoot != null) _gameplayRoot.SetActive(false);
                SetNotificationVisible(false); return;
            }
            _currentEncounterIndex = (int)progress.EncounterIndexPlusOne - 1;
            // P8 的原 Boss 专属栏与区域／目标文本原本互斥，导出后仍须遵守同一 authoring。
            // 策略是可选读口：旧来源没有声明时保持原显示；本次只改变 Gameplay 叶树的显隐，
            // Adapter、NotificationRoot、玩家血条／十格等仍继续接收各自的事实与连接反馈。
            bool show = !(_presentationSource is IDungeonHudPresentationPolicy policy) || policy.ShowEncounterHud;
            if (_gameplayRoot != null) _gameplayRoot.SetActive(show);
            ShowEncounterAuthoring(_currentEncounterIndex); SetRemainingEnemyCount((int)progress.RemainingEnemies);
        }

        private void SetRemainingEnemyCount(int remainingCount)
        {
            if (_remainingEnemyText != null)
            {
                _remainingEnemyText.text = $"剩余敌人：{remainingCount}";
            }
        }

        private void ShowNotification(string message, Sprite icon, bool persistent = false)
        {
            // Frozen 提示需要持续可见；其它区域/施法通知不能在暂停期间盖掉连接状态。
            if (_onlineFrozen && !persistent) return;
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
                _notificationRoutine = null;
            }

            // Notification 是低频离散事件；使用 Realtime 等待，确保 Time.timeScale=0 时提示仍能正常收起。
            // Frozen 不设倒计时；inactive/Editor authoring 可以接线但不能启动 Runtime Coroutine。
            if (persistent || !Application.isPlaying || !isActiveAndEnabled) return;
            _notificationRoutine = StartCoroutine(HideNotificationAfterDelay());
        }

        private void ClearNotification()
        {
            if (_notificationRoutine != null) { StopCoroutine(_notificationRoutine); _notificationRoutine = null; }
            SetNotificationVisible(false);
            if (_notificationText != null) _notificationText.text = string.Empty;
            if (_notificationIcon != null) { _notificationIcon.sprite = null; _notificationIcon.enabled = false; }
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
