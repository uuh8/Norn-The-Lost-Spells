using UnityEngine;

namespace Game.Core
{
    /// <summary>
    /// 少量跨 Scene 用户偏好的唯一持久化入口。
    /// PlayerPrefs 只承担设置，不承担 Run Save；消费者通过值快照和 EventBus 读取变化。
    /// </summary>
    public static class UserSettingsService
    {
        private const string MasterVolumeKey = "settings.masterVolume";
        private const string MouseSensitivityKey = "settings.mouseSensitivity";
        private const string FullscreenKey = "settings.fullscreen";

        private static UserSettingsValues _current = UserSettingsValues.Default;
        private static bool _isLoaded;

        public static UserSettingsValues Current
        {
            get
            {
                EnsureLoaded();
                return _current;
            }
        }

#if UNITY_EDITOR || !UNITY_SERVER
        // 无画面 Server 不应加载个人显示/音量偏好，也不应对 Screen 或 AudioListener 应用客户端设置。
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InitializeBeforeSceneLoad()
        {
            // 正常游玩采用已约定的 60 FPS 目标，避免 VSync 关闭时无上限渲染持续占满 GPU。
            // 仅在进入游戏时设置：专用 Performance Lab 可在 Start 中覆盖为 uncapped，
            // 后续调整音量/全屏不会偷偷重置基准测试的帧率设置。
            Application.targetFrameRate = 60;
            // 兼容关闭 Domain Reload 的 Editor 配置：每次进入 Play 都重新从持久化数据建立静态快照。
            _isLoaded = false;
            LoadFromPlayerPrefs(publishChange: false);
        }
#endif

        public static bool Set(UserSettingsValues values)
        {
            EnsureLoaded();
            UserSettingsValues sanitized = values.Sanitize();
            if (sanitized == _current)
            {
                return false;
            }

            _current = sanitized;
            WriteToPlayerPrefs(_current);
            ApplyPlatformSettings(_current);

            EventBus<UserSettingsChangedEvent>.Publish(
                new UserSettingsChangedEvent { Values = _current });
            return true;
        }

        /// <summary>
        /// 把已写入 PlayerPrefs 内存缓存的值显式刷到磁盘。
        /// Slider 拖动时只 Set，关闭 Settings 或切换 Scene 时再 Save，避免连续磁盘写入。
        /// </summary>
        public static void Save()
        {
            EnsureLoaded();
            WriteToPlayerPrefs(_current);
            PlayerPrefs.Save();
        }

        public static UserSettingsValues Reload()
        {
            return LoadFromPlayerPrefs(publishChange: true);
        }

        private static void EnsureLoaded()
        {
            if (!_isLoaded)
            {
                LoadFromPlayerPrefs(publishChange: false);
            }
        }

        private static UserSettingsValues LoadFromPlayerPrefs(bool publishChange)
        {
            UserSettingsValues previous = _current;
            var loaded = new UserSettingsValues(
                PlayerPrefs.GetFloat(
                    MasterVolumeKey,
                    UserSettingsValues.DefaultMasterVolume),
                PlayerPrefs.GetFloat(
                    MouseSensitivityKey,
                    UserSettingsValues.DefaultMouseSensitivity),
                PlayerPrefs.GetInt(
                    FullscreenKey,
                    UserSettingsValues.DefaultFullscreen ? 1 : 0) != 0)
                .Sanitize();

            _current = loaded;
            _isLoaded = true;
            // 把旧版本或外部写入的非法值立即规范化到内存缓存，下一次 Save 会持久化规范值。
            WriteToPlayerPrefs(_current);
            ApplyPlatformSettings(_current);

            if (publishChange && previous != _current)
            {
                EventBus<UserSettingsChangedEvent>.Publish(
                    new UserSettingsChangedEvent { Values = _current });
            }

            return _current;
        }

        private static void WriteToPlayerPrefs(UserSettingsValues values)
        {
            PlayerPrefs.SetFloat(MasterVolumeKey, values.MasterVolume);
            PlayerPrefs.SetFloat(MouseSensitivityKey, values.MouseSensitivity);
            PlayerPrefs.SetInt(FullscreenKey, values.Fullscreen ? 1 : 0);
        }

        private static void ApplyPlatformSettings(UserSettingsValues values)
        {
            AudioListener.volume = values.MasterVolume;
            if (Screen.fullScreen != values.Fullscreen)
            {
                Screen.fullScreen = values.Fullscreen;
            }
        }
    }
}
