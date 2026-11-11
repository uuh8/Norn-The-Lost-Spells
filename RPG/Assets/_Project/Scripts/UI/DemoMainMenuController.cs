using Game.Core;
using UnityEngine;

namespace Game.UI
{
    /// <summary>
    /// 主菜单的按钮 Adapter：开始进入据点，设置显隐和退出，不保存 Run State。
    /// </summary>
    public sealed class DemoMainMenuController : MonoBehaviour
    {
        [SerializeField] private DemoSceneTransitionController _sceneTransition;
        [SerializeField] private GameObject _mainPanel;
        [SerializeField] private GameObject _settingsPanel;
        // 新字段不迁移旧教学Scene字符串；否则旧Scene序列化的P7会覆盖正确的据点默认入口。
        [SerializeField] private string _hubSceneName = "SinglePlayer_Hub";
        [SerializeField] private GameObject _accountMenuPrefab;
        private GameObject _accountMenu;
        private bool _entering;
        private bool _hubRequestPending;
        private string _requestedHubTarget;

        private void OnEnable()
        {
            ConfigureViewportLayout();
            EventBus<AccountMenuCommandEvent>.Subscribe(OnCommand);
            EventBus<AccountHubEntryReadyEvent>.Subscribe(OnEntryReady);
        }

        private void ConfigureViewportLayout() => UiViewportLayout.CoverCanvas(_mainPanel);
        private void OnDisable()
        {
            _hubRequestPending = false; _requestedHubTarget = null;
            EventBus<AccountMenuCommandEvent>.Unsubscribe(OnCommand);
            EventBus<AccountHubEntryReadyEvent>.Unsubscribe(OnEntryReady);
        }
        private void OnCommand(AccountMenuCommandEvent command)
        {
            switch (command.Command)
            {
                case AccountMenuCommand.EnterHub: StartGame(); break;
                case AccountMenuCommand.Settings: OpenSettings(); break;
                case AccountMenuCommand.Quit: QuitGame(); break;
            }
        }

        private void Start()
        {
            Time.timeScale = 1f;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            _ = UserSettingsService.Current;

            // MainPanel自身Image就是原美术背景，不能把整根一起隐藏；只停用旧导航按钮。
            SetPanelActive(_mainPanel, true);
            if (_mainPanel != null)
                foreach (var button in _mainPanel.GetComponentsInChildren<UnityEngine.UI.Button>(true)) button.gameObject.SetActive(false);
            SetPanelActive(_settingsPanel, false);
            if (_accountMenuPrefab != null) _accountMenu = Instantiate(_accountMenuPrefab);
            else GameLog.Error("主菜单缺少AccountMenu Prefab；保持禁止进入，不能静默回落离线入口。", "Accounts");
        }

        public void StartGame()
        {
            if (_entering) return;
            _requestedHubTarget = _hubSceneName; _hubRequestPending = true;
            // 保留旧按钮回调名，但它现在只提交意图；单机也必须拿到本次业务验证成功。
            EventBus<OnlineLobbyRequestEvent>.Publish(new OnlineLobbyRequestEvent
            { Action = OnlineLobbyAction.EnterHub, Destination = _requestedHubTarget, RequestOwnerId = GetInstanceID() });
        }
        private bool TryGetReadyHubTarget(AccountHubEntryReadyEvent ready, out string target)
        {
            target = null;
            if (_entering || !_hubRequestPending || ready.RequestOwnerId != GetInstanceID()
                || string.IsNullOrWhiteSpace(ready.TargetScene) || ready.TargetScene != _requestedHubTarget) return false;
            target = ready.TargetScene; return true;
        }
        private void OnEntryReady(AccountHubEntryReadyEvent ready)
        {
            if (!TryGetReadyHubTarget(ready, out var target)) return;
            if (_sceneTransition == null)
            {
                GameLog.Error("Main Menu has no Scene Transition Controller.", "UI");
                return;
            }

            // HTTP等待期间Inspector/别的菜单可能改变字段；只装载业务已确认并预arm的那次原目标。
            _entering = _sceneTransition.LoadScene(target);
            if (_entering) _hubRequestPending = false;
        }

        public void OpenSettings()
        {
            // MainPanel现在只承担原美术背景，旧按钮已在Start停用；设置打开时同样要覆盖整个视口。
            SetPanelActive(_mainPanel, true);
            SetPanelActive(_accountMenu, false);
            SetPanelActive(_settingsPanel, true);
        }

        public void CloseSettings()
        {
            UserSettingsService.Save();
            SetPanelActive(_settingsPanel, false);
            SetPanelActive(_mainPanel, true);
            SetPanelActive(_accountMenu, true);
        }

        public void QuitGame()
        {
            UserSettingsService.Save();
#if UNITY_EDITOR
            GameLog.Info(
                "Quit requested in Editor. Standalone Build will call Application.Quit().",
                "UI");
#else
            Application.Quit();
#endif
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
