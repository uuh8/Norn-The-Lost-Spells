using System;

namespace Game.Core
{
    /// <summary>
    /// 跨 Scene 用户设置的不可变值快照。
    /// 它不读写 PlayerPrefs，也不操作 Unity 对象，因此 Clamp 规则可以独立测试。
    /// </summary>
    public readonly struct UserSettingsValues : IEquatable<UserSettingsValues>
    {
        public const float MinMasterVolume = 0f;
        public const float MaxMasterVolume = 1f;
        public const float DefaultMasterVolume = 1f;

        // 使用倍率而不是直接保存“度/像素”，保留不同角色或设备的 Authoring Base Sensitivity。
        public const float MinMouseSensitivity = 0.25f;
        public const float MaxMouseSensitivity = 2f;
        public const float DefaultMouseSensitivity = 1f;
        public const bool DefaultFullscreen = true;

        public UserSettingsValues(
            float masterVolume,
            float mouseSensitivity,
            bool fullscreen)
        {
            MasterVolume = masterVolume;
            MouseSensitivity = mouseSensitivity;
            Fullscreen = fullscreen;
        }

        public float MasterVolume { get; }

        public float MouseSensitivity { get; }

        public bool Fullscreen { get; }

        public static UserSettingsValues Default => new UserSettingsValues(
            DefaultMasterVolume,
            DefaultMouseSensitivity,
            DefaultFullscreen);

        public UserSettingsValues Sanitize()
        {
            return new UserSettingsValues(
                ClampFinite(
                    MasterVolume,
                    DefaultMasterVolume,
                    MinMasterVolume,
                    MaxMasterVolume),
                ClampFinite(
                    MouseSensitivity,
                    DefaultMouseSensitivity,
                    MinMouseSensitivity,
                    MaxMouseSensitivity),
                Fullscreen);
        }

        public bool Equals(UserSettingsValues other)
        {
            return MasterVolume.Equals(other.MasterVolume) &&
                   MouseSensitivity.Equals(other.MouseSensitivity) &&
                   Fullscreen == other.Fullscreen;
        }

        public override bool Equals(object obj)
        {
            return obj is UserSettingsValues other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = MasterVolume.GetHashCode();
                hash = (hash * 397) ^ MouseSensitivity.GetHashCode();
                hash = (hash * 397) ^ Fullscreen.GetHashCode();
                return hash;
            }
        }

        public static bool operator ==(UserSettingsValues left, UserSettingsValues right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(UserSettingsValues left, UserSettingsValues right)
        {
            return !left.Equals(right);
        }

        private static float ClampFinite(float value, float fallback, float min, float max)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                return fallback;
            }

            return Math.Min(Math.Max(value, min), max);
        }
    }
}
