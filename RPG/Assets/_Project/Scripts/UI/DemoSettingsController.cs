using Game.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>
    /// Main Menu 与 Pause Menu 共用的 Settings Panel Adapter。
    /// Slider/Toggle 只提交值快照；持久化、Clamp 与平台应用仍由 Game.Core 负责。
    /// </summary>
    public sealed class DemoSettingsController : MonoBehaviour
    {
        [SerializeField] private Slider _masterVolumeSlider;
        [SerializeField] private Slider _mouseSensitivitySlider;
        [SerializeField] private Toggle _fullscreenToggle;

        private void OnEnable()
        {
            RefreshFromService();
        }

        private void OnDisable()
        {
            // Slider 拖动只更新 PlayerPrefs 内存缓存；关闭 Panel 时统一刷盘，避免拖动过程连续磁盘写入。
            UserSettingsService.Save();
        }

        public void SetMasterVolume(float value)
        {
            UserSettingsValues current = UserSettingsService.Current;
            UserSettingsService.Set(new UserSettingsValues(
                value,
                current.MouseSensitivity,
                current.Fullscreen));
        }

        public void SetMouseSensitivity(float value)
        {
            UserSettingsValues current = UserSettingsService.Current;
            UserSettingsService.Set(new UserSettingsValues(
                current.MasterVolume,
                value,
                current.Fullscreen));
        }

        public void SetFullscreen(bool fullscreen)
        {
            UserSettingsValues current = UserSettingsService.Current;
            UserSettingsService.Set(new UserSettingsValues(
                current.MasterVolume,
                current.MouseSensitivity,
                fullscreen));
        }

        public void Save()
        {
            UserSettingsService.Save();
        }

        private void RefreshFromService()
        {
            UserSettingsValues values = UserSettingsService.Current;
            if (_masterVolumeSlider != null)
            {
                _masterVolumeSlider.minValue = UserSettingsValues.MinMasterVolume;
                _masterVolumeSlider.maxValue = UserSettingsValues.MaxMasterVolume;
                _masterVolumeSlider.SetValueWithoutNotify(values.MasterVolume);
            }

            if (_mouseSensitivitySlider != null)
            {
                _mouseSensitivitySlider.minValue = UserSettingsValues.MinMouseSensitivity;
                _mouseSensitivitySlider.maxValue = UserSettingsValues.MaxMouseSensitivity;
                _mouseSensitivitySlider.SetValueWithoutNotify(values.MouseSensitivity);
            }

            if (_fullscreenToggle != null)
            {
                _fullscreenToggle.SetIsOnWithoutNotify(values.Fullscreen);
            }
        }
    }
}
