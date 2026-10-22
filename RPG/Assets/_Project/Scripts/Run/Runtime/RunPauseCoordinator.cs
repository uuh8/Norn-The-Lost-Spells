using UnityEngine;

namespace Game.Run
{
    /// <summary>
    /// 一局内唯一的暂停所有权协调器。
    /// UI 只申请或释放自己的 Pause Reason，只有最后一个 Reason 释放后才恢复 Gameplay。
    /// </summary>
    public sealed class RunPauseCoordinator : MonoBehaviour
    {
        private RunPauseReason _activeReasons;
        private float _timeScaleBeforePause = 1f;

        public RunPauseReason ActiveReasons => _activeReasons;

        public bool IsPaused => _activeReasons != RunPauseReason.None;

        private void Awake()
        {
            // Cursor 是跨 Scene 的全局状态。Gameplay Scene 的 Coordinator 在 Awake 先建立
            // “无暂停 = 锁定并隐藏”的默认值；随后 Guide/Pause/Wand 的 Start 或输入回调
            // 再通过 Pause Reason 解锁。这样结果不再依赖不同 MonoBehaviour 的 Start 顺序。
            ApplyGameplayCursorState();
        }

        public bool HasReason(RunPauseReason reason)
        {
            return reason != RunPauseReason.None && (_activeReasons & reason) == reason;
        }

        /// <summary>
        /// 幂等地申请暂停。首次申请会保存进入暂停前的 Time Scale，并统一解锁 Cursor。
        /// </summary>
        public bool RequestPause(RunPauseReason reason)
        {
            if (reason == RunPauseReason.None || (_activeReasons & reason) == reason)
            {
                return false;
            }

            bool wasPaused = IsPaused;
            _activeReasons |= reason;

            if (!wasPaused)
            {
                // 正常 Gameplay 为 1；保留非零自定义速度，避免暂停系统擅自覆盖 Slow Motion 等上层策略。
                _timeScaleBeforePause = Time.timeScale > 0f ? Time.timeScale : 1f;
                Time.timeScale = 0f;
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }

            return true;
        }

        /// <summary>
        /// 幂等地释放指定暂停原因。仍有其他 Reason 时保持 Time.timeScale=0。
        /// </summary>
        public bool ReleasePause(RunPauseReason reason)
        {
            if (reason == RunPauseReason.None || (_activeReasons & reason) == 0)
            {
                return false;
            }

            _activeReasons &= ~reason;
            if (!IsPaused)
            {
                RestoreGameplayState();
            }

            return true;
        }

        /// <summary>
        /// Scene Navigation 前清除所有局内暂停状态。
        /// 新 Scene 不能继承 Time.timeScale=0，否则其 Start/Coroutine 可能看起来像加载后卡死。
        /// </summary>
        public void PrepareForSceneTransition()
        {
            _activeReasons = RunPauseReason.None;
            _timeScaleBeforePause = 1f;
            Time.timeScale = 1f;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void OnDisable()
        {
            // 组件被禁用或 Scene 卸载时兜底释放，防止 static Time.timeScale 污染下一次 Play/Scene。
            if (IsPaused)
            {
                _activeReasons = RunPauseReason.None;
                RestoreGameplayState();
            }
        }

        private void RestoreGameplayState()
        {
            Time.timeScale = _timeScaleBeforePause > 0f ? _timeScaleBeforePause : 1f;
            ApplyGameplayCursorState();
            _timeScaleBeforePause = 1f;
        }

        private static void ApplyGameplayCursorState()
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }
}
