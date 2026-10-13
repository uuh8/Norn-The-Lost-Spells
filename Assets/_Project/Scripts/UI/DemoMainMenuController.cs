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

        private void Start()
        {
            Time.timeScale = 1f;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            _ = UserSettingsService.Current;

            SetPanelActive(_mainPanel, true);
            SetPanelActive(_settingsPanel, false);
        }

        public void StartGame()
        {
            if (_sceneTransition == null)
            {
                GameLog.Error("Main Menu has no Scene Transition Controller.", "UI");
                return;
            }

            _sceneTransition.LoadScene(_hubSceneName);
        }

        public void OpenSettings()
        {
            SetPanelActive(_mainPanel, false);
            SetPanelActive(_settingsPanel, true);
        }

        public void CloseSettings()
        {
            UserSettingsService.Save();
            SetPanelActive(_settingsPanel, false);
            SetPanelActive(_mainPanel, true);
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
