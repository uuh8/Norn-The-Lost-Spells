using System.Collections;
using Game.Core;
using Game.Run;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>
    /// Pause、Settings 与 Victory/Defeat Panel 的局内 UI 协调器。
    /// 胜负来自 RunStateChangedEvent；本组件不读取 Enemy，也不自行判定一局是否结束。
    /// </summary>
    public sealed class DemoPauseMenuController : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private RunPauseCoordinator _pauseCoordinator;
        private SinglePlayerInventoryController _inventory;
        public void ConfigurePracticeInventory(SinglePlayerInventoryController inventory)=>_inventory=inventory;
        [SerializeField] private InputActionReference _pauseAction;
        [SerializeField] private DemoSceneTransitionController _sceneTransition;

        [Header("Panels")]
        [SerializeField] private GameObject _pausePanel;
        [SerializeField] private GameObject _settingsPanel;
        [SerializeField] private GameObject _victoryPanel;
        [SerializeField] private GameObject _defeatPanel;
        [SerializeField] private Text _victoryTimeText;

        [Header("Terminal Result Presentation")]
        [SerializeField]
        [Min(0f)]
        [Tooltip("Boss 死亡后保留多少秒的正常游戏时间，再显示胜利界面。该时间使用 scaled time，确保 Die 动画实际推进。")]
        private float _victoryRevealDelay = 1.6f;

        [SerializeField]
        [Min(0f)]
        [Tooltip("玩家死亡后保留多少秒的正常游戏时间，再显示失败界面。该时间使用 scaled time，确保 Die 动画实际推进。")]
        private float _defeatRevealDelay = 1.3f;

        [SerializeField]
        [Min(0f)]
        [Tooltip("胜负界面从透明到完全可见的时长；使用 unscaled time，因此 Time Scale 为 0 后仍能播放。")]
        private float _resultFadeDuration = 0.35f;

        [SerializeField]
        [Range(0.5f, 1f)]
        [Tooltip("胜负界面入场第一帧的缩放比例；越小弹出感越强，越接近 1 越克制。")]
        private float _resultStartScale = 0.94f;

        [Header("Scene Navigation")]
        [SerializeField] private string _mainMenuSceneName = "P7_MainMenu";
        [SerializeField] private string _hubSceneName = "SinglePlayer_Hub";
        [SerializeField] private string _mainMenuScenePath = "Assets/_Project/Scenes/P7_MainMenu.unity";

        private float _runStartedRealtime;
        private bool _isLoadingScene;
        private bool _isTerminalPresentationPending;
        private Coroutine _terminalPresentationRoutine;
        private CanvasGroup _victoryCanvasGroup;
        private CanvasGroup _defeatCanvasGroup;

        private void OnEnable()
        {
            EventBus<RunStateChangedEvent>.Subscribe(OnRunStateChanged);
            if (_pauseAction != null && _pauseAction.action != null)
            {
                _pauseAction.action.performed += OnPausePerformed;
                _pauseAction.action.Enable();
            }
        }

        private void Start()
        {
            _runStartedRealtime = Time.realtimeSinceStartup;
            SetPanelActive(_pausePanel, false);
            SetPanelActive(_settingsPanel, false);
            _victoryCanvasGroup = ResolveResultCanvasGroup(_victoryPanel);
            _defeatCanvasGroup = ResolveResultCanvasGroup(_defeatPanel);
            ResetResultPanel(_victoryPanel, _victoryCanvasGroup);
            ResetResultPanel(_defeatPanel, _defeatCanvasGroup);
            SetPanelActive(_victoryPanel, false);
            SetPanelActive(_defeatPanel, false);
        }

        private void OnDisable()
        {
            EventBus<RunStateChangedEvent>.Unsubscribe(OnRunStateChanged);
            if (_pauseAction != null && _pauseAction.action != null)
            {
                _pauseAction.action.performed -= OnPausePerformed;
            }

            if (_terminalPresentationRoutine != null)
            {
                StopCoroutine(_terminalPresentationRoutine);
                _terminalPresentationRoutine = null;
            }

            _isTerminalPresentationPending = false;

            if (!_isLoadingScene && _pauseCoordinator != null)
            {
                _pauseCoordinator.ReleasePause(RunPauseReason.PauseMenu);
                _pauseCoordinator.ReleasePause(RunPauseReason.TerminalResult);
            }
        }

        public void TogglePauseMenu()
        {
            if (_pauseCoordinator == null ||
                _isTerminalPresentationPending ||
                _pauseCoordinator.HasReason(RunPauseReason.TerminalResult))
            {
                return;
            }

            if (_pauseCoordinator.HasReason(RunPauseReason.PauseMenu))
            {
                Resume();
                return;
            }

            // Escape 从背包切换到 Pause Menu，而不是让两个全屏交互层同时争抢 Cursor。
            _inventory?.Close();
_pauseCoordinator.RequestPause(RunPauseReason.PauseMenu);
            SetPanelActive(_settingsPanel, false);
            SetPanelActive(_pausePanel, true);
        }

        public void Resume()
        {
            if (_pauseCoordinator == null ||
                _pauseCoordinator.HasReason(RunPauseReason.TerminalResult))
            {
                return;
            }

            SetPanelActive(_settingsPanel, false);
            SetPanelActive(_pausePanel, false);
            _pauseCoordinator.ReleasePause(RunPauseReason.PauseMenu);
        }

        public void OpenSettings()
        {
            if (_pauseCoordinator == null ||
                !_pauseCoordinator.HasReason(RunPauseReason.PauseMenu))
            {
                return;
            }

            SetPanelActive(_pausePanel, false);
            SetPanelActive(_settingsPanel, true);
        }

        public void CloseSettings()
        {
            if (_pauseCoordinator == null ||
                !_pauseCoordinator.HasReason(RunPauseReason.PauseMenu))
            {
                return;
            }

            SetPanelActive(_settingsPanel, false);
            SetPanelActive(_pausePanel, true);
        }

        // 保留 Inspector 按钮方法名；“重开”现在回据点，不再创建旧 P7 开场配置。
        public void RestartRun()
        {
            if (!CanBeginNavigation(_hubSceneName)) return;
            var session = HubPracticeSession.Current;
            if (session != null) { if (session.ReturnToHub()) _isLoadingScene = true; return; }
            BeginSceneLoad(_hubSceneName);
        }
        public void RetryBoss()
        {
            if (_isLoadingScene) return;
            var session = HubPracticeSession.Current;
            if (session != null && session.RetryBoss()) _isLoadingScene = true;
        }
        public void ReturnToMainMenu()
        {
            if (!CanBeginNavigation(_mainMenuSceneName)) return;
            var session = HubPracticeSession.Current;
            if (session != null) { if (session.ReturnToMainMenu(_mainMenuScenePath)) _isLoadingScene = true; return; }
            BeginSceneLoad(_mainMenuSceneName);
        }

        private void OnPausePerformed(InputAction.CallbackContext context)
        {
            // InputAction回调早于MonoBehaviour.Update。此Esc由背包Update消费关闭，不再同时打开暂停菜单。
            if(_pauseCoordinator!=null && _pauseCoordinator.HasReason(RunPauseReason.Inventory))return;
            TogglePauseMenu();
        }

        private void OnRunStateChanged(RunStateChangedEvent runEvent)
        {
            if (runEvent.CurrentState == RunState.Running)
            {
                _runStartedRealtime = Time.realtimeSinceStartup;
                return;
            }

            if (runEvent.CurrentState != RunState.Completed &&
                runEvent.CurrentState != RunState.Failed)
            {
                return;
            }

            _inventory?.Close();

if (_pauseCoordinator != null)
            {
                _pauseCoordinator.ReleasePause(RunPauseReason.PauseMenu);
            }

            SetPanelActive(_pausePanel, false);
            SetPanelActive(_settingsPanel, false);
            bool isVictory = runEvent.CurrentState == RunState.Completed;
            SetPanelActive(_victoryPanel, false);
            SetPanelActive(_defeatPanel, false);
            // 现代练习由Run工作区决定自动死亡返回/胜利开门；UI只关闭旧框，不申请TerminalResult硬暂停。
            if(HubPracticeSession.Current?.OwnsScene(gameObject.scene)==true)return;

            if (isVictory && _victoryTimeText != null)
            {
                float elapsed = Mathf.Max(0f, Time.realtimeSinceStartup - _runStartedRealtime);
                int totalSeconds = Mathf.FloorToInt(elapsed);
                _victoryTimeText.text = $"通关时间  {totalSeconds / 60:00}:{totalSeconds % 60:00}";
            }

            // Run 已经进入 Terminal State，但此时不能立刻把 Time Scale 设为 0：
            // DeathEvent 同帧才刚让 Animator CrossFade 到 Die，硬暂停会冻结首帧姿势。
            if (_isTerminalPresentationPending ||
                (_pauseCoordinator != null &&
                 _pauseCoordinator.HasReason(RunPauseReason.TerminalResult)))
            {
                return;
            }

            _isTerminalPresentationPending = true;
            _terminalPresentationRoutine = StartCoroutine(
                RevealTerminalResultAfterDelay(isVictory));
        }

        private IEnumerator RevealTerminalResultAfterDelay(bool isVictory)
        {
            float revealDelay = isVictory
                ? Mathf.Max(0f, _victoryRevealDelay)
                : Mathf.Max(0f, _defeatRevealDelay);
            float elapsed = 0f;

            // 使用 scaled Delta Time：等待的是“死亡动画实际播放了多久”，而不是墙钟时间。
            // Coroutine 是一次性结算流程，不在常规 Update 热路径产生 GC Alloc。
            while (elapsed < revealDelay)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            if (_pauseCoordinator != null)
            {
                _pauseCoordinator.RequestPause(RunPauseReason.TerminalResult);
            }

            GameObject resultPanel = isVictory ? _victoryPanel : _defeatPanel;
            CanvasGroup resultCanvasGroup = isVictory
                ? _victoryCanvasGroup
                : _defeatCanvasGroup;
            PrepareResultPanelForReveal(resultPanel, resultCanvasGroup);

            float fadeDuration = Mathf.Max(0f, _resultFadeDuration);
            float startScale = Mathf.Clamp(_resultStartScale, 0.5f, 1f);
            float fadeElapsed = 0f;
            while (fadeElapsed < fadeDuration)
            {
                fadeElapsed += Time.unscaledDeltaTime;
                float linearProgress = Mathf.Clamp01(fadeElapsed / fadeDuration);
                // SmoothStep 让透明度和缩放都以 S Curve 加减速，避免 Linear 插值的机械感。
                float easedProgress = Mathf.SmoothStep(0f, 1f, linearProgress);
                ApplyResultPanelProgress(
                    resultPanel,
                    resultCanvasGroup,
                    startScale,
                    easedProgress);
                yield return null;
            }

            CompleteResultPanelReveal(resultPanel, resultCanvasGroup);
            _isTerminalPresentationPending = false;
            _terminalPresentationRoutine = null;
        }

        private static CanvasGroup ResolveResultCanvasGroup(GameObject panel)
        {
            if (panel == null)
            {
                return null;
            }

            if (panel.TryGetComponent(out CanvasGroup canvasGroup))
            {
                return canvasGroup;
            }

            // 两个现有 Scene 的结果 Panel 尚未挂 CanvasGroup。它是纯 UI Presentation 组件，
            // 在 Start 一次性补齐并缓存，可避免为了一个小过渡同时修改两份大型 Scene YAML。
            return panel.AddComponent<CanvasGroup>();
        }

        private static void ResetResultPanel(
            GameObject panel,
            CanvasGroup canvasGroup)
        {
            if (panel != null)
            {
                panel.transform.localScale = Vector3.one;
            }

            if (canvasGroup == null)
            {
                return;
            }

            canvasGroup.alpha = 0f;
            canvasGroup.interactable = false;
            canvasGroup.blocksRaycasts = false;
        }

        private void PrepareResultPanelForReveal(
            GameObject panel,
            CanvasGroup canvasGroup)
        {
            if (panel == null)
            {
                return;
            }

            float startScale = Mathf.Clamp(_resultStartScale, 0.5f, 1f);
            panel.transform.localScale = Vector3.one * startScale;
            if (canvasGroup != null)
            {
                canvasGroup.alpha = 0f;
                canvasGroup.interactable = false;
                canvasGroup.blocksRaycasts = false;
            }

            panel.SetActive(true);
        }

        private static void ApplyResultPanelProgress(
            GameObject panel,
            CanvasGroup canvasGroup,
            float startScale,
            float progress)
        {
            if (canvasGroup != null)
            {
                canvasGroup.alpha = progress;
            }

            if (panel != null)
            {
                float scale = Mathf.Lerp(startScale, 1f, progress);
                panel.transform.localScale = Vector3.one * scale;
            }
        }

        private static void CompleteResultPanelReveal(
            GameObject panel,
            CanvasGroup canvasGroup)
        {
            if (panel != null)
            {
                panel.transform.localScale = Vector3.one;
            }

            if (canvasGroup == null)
            {
                return;
            }

            canvasGroup.alpha = 1f;
            canvasGroup.interactable = true;
            canvasGroup.blocksRaycasts = true;
        }

        private void BeginSceneLoad(string sceneName)
        {
            if (string.IsNullOrWhiteSpace(sceneName))
            {
                GameLog.Error("DemoPauseMenuController received an empty Scene name.", "UI");
                return;
            }

            if (_sceneTransition != null && !_sceneTransition.LoadScene(sceneName))
            {
                return;
            }

            _isLoadingScene = true;
            if (_sceneTransition == null)
            {
                // 兼容尚未装配 Fade Prefab 的旧 Scene；P7_DemoRun 在 Task 7 Gate 必须绑定统一 Transition。
                if (_pauseCoordinator != null)
                {
                    _pauseCoordinator.PrepareForSceneTransition();
                }
                else
                {
                    Time.timeScale = 1f;
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;
                }

                SceneManager.LoadSceneAsync(sceneName);
            }
        }

        private bool CanBeginNavigation(string sceneName)
        {
            return !_isLoadingScene &&
                   !string.IsNullOrWhiteSpace(sceneName) &&
                   (_sceneTransition == null ||
                    !_sceneTransition.IsTransitioning);
        }



        private static void SetPanelActive(GameObject panel, bool active)
        {
            if (panel != null)
            {
                panel.SetActive(active);
            }
        }
    }
}
