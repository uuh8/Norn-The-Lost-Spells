using System.Collections;
using Game.Core;
using Game.Run;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.UI
{
    /// <summary>
    /// 每个 P7 Scene 放置一份的轻量 Fade/Loading Adapter。
    /// 当前 Scene Fade Out 后异步加载；新 Scene 自己的实例负责 Fade In，不需要 DontDestroyOnLoad。
    /// </summary>
    public sealed class DemoSceneTransitionController : MonoBehaviour
    {
        [SerializeField] private CanvasGroup _fadeCanvasGroup;
        [SerializeField]
        [Tooltip("局内 Scene 应绑定统一暂停协调器；Fade Out 时冻结移动与施法，而不只阻挡 UGUI。")]
        private RunPauseCoordinator _pauseCoordinator;
        [SerializeField, Min(0f)] private float _fadeDuration = 0.25f;

        private bool _isTransitioning;

        public bool IsTransitioning => _isTransitioning;

        private void Awake()
        {
            if (_fadeCanvasGroup == null)
            {
                GameLog.Error(
                    $"DemoSceneTransitionController '{name}' has no Fade CanvasGroup.",
                    "UI");
                enabled = false;
                return;
            }

            // Scene 初始保持黑屏和输入阻挡，Start 中再用 unscaled time 淡入。
            _fadeCanvasGroup.alpha = 1f;
            _fadeCanvasGroup.blocksRaycasts = true;
            _fadeCanvasGroup.interactable = false;
        }

        private void Start()
        {
            StartCoroutine(FadeInRoutine());
        }

        private void OnEnable()
        {
            EventBus<SceneTransitionRequestedEvent>.Subscribe(OnTransitionRequested);
            GameLog.Info(
                $"Scene Transition Controller '{name}' subscribed; activeInHierarchy={gameObject.activeInHierarchy}.",
                "UI");
        }

        private void OnDisable()
        {
            EventBus<SceneTransitionRequestedEvent>.Unsubscribe(OnTransitionRequested);
            GameLog.Info(
                $"Scene Transition Controller '{name}' unsubscribed; transitioning={_isTransitioning}.",
                "UI");
            if (_pauseCoordinator != null)
            {
                // 组件若在加载完成前被外部禁用，必须释放自己的 Pause Reason，避免 Time Scale 泄漏。
                _pauseCoordinator.ReleasePause(RunPauseReason.SceneTransition);
            }
        }

        public bool LoadScene(string sceneName)
        {
            if (_isTransitioning || string.IsNullOrWhiteSpace(sceneName))
            {
                return false;
            }

            _isTransitioning = true;
            if (_pauseCoordinator != null)
            {
                // CanvasGroup 只能拦截 UI Raycast；Time Scale Gate 才能阻止玩家在 Fade 中移动和施法。
                _pauseCoordinator.RequestPause(RunPauseReason.SceneTransition);
            }
            StartCoroutine(LoadSceneRoutine(sceneName));
            return true;
        }

        public bool ReloadCurrentScene()
        {
            return LoadScene(SceneManager.GetActiveScene().name);
        }

        private void OnTransitionRequested(SceneTransitionRequestedEvent transitionEvent)
        {
            // EventBus 是同步发布；LoadScene 自带 _isTransitioning Gate，
            // 即使错误地重复发布，也只会启动一条 Fade/Load Coroutine。
            GameLog.Info(
                $"Scene Transition Controller received request for '{transitionEvent.SceneName}'.",
                "UI");
            bool accepted = LoadScene(transitionEvent.SceneName);
            GameLog.Info(
                $"Scene Transition Controller LoadScene accepted={accepted}.",
                "UI");
        }

        private IEnumerator FadeInRoutine()
        {
            yield return FadeRoutine(1f, 0f);
            if (!_isTransitioning)
            {
                _fadeCanvasGroup.blocksRaycasts = false;
            }
        }

        private IEnumerator LoadSceneRoutine(string sceneName)
        {
            _fadeCanvasGroup.blocksRaycasts = true;
            yield return FadeRoutine(_fadeCanvasGroup.alpha, 1f);

            // Time Scale 与 Cursor 是跨 Scene 的全局状态；加载边界再次兜底，避免暂停状态污染新 Scene。
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
            UserSettingsService.Save();

            GameLog.Info(
                $"Scene Transition Controller starting LoadSceneAsync('{sceneName}').",
                "UI");
            AsyncOperation loadOperation;
#if UNITY_EDITOR
            // 隔离Review地图无需加入正式Build Settings；Editor官方API支持完整Scene路径，Build仍按构建列表加载。
            if(System.IO.File.Exists(sceneName))loadOperation=UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(sceneName,new LoadSceneParameters(LoadSceneMode.Single));
            else loadOperation=SceneManager.LoadSceneAsync(sceneName);
#else
            loadOperation=SceneManager.LoadSceneAsync(sceneName);
#endif
            if (loadOperation == null)
            {
                GameLog.Error($"Failed to begin loading Scene '{sceneName}'.", "UI");
                _isTransitioning = false;
                yield return FadeRoutine(1f, 0f);
                _fadeCanvasGroup.blocksRaycasts = false;
                yield break;
            }

            while (!loadOperation.isDone)
            {
                yield return null;
            }
        }

        private IEnumerator FadeRoutine(float from, float to)
        {
            if (_fadeDuration <= 0f)
            {
                _fadeCanvasGroup.alpha = to;
                yield break;
            }

            float elapsed = 0f;
            while (elapsed < _fadeDuration)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / _fadeDuration);
                _fadeCanvasGroup.alpha = Mathf.Lerp(from, to, t);
                yield return null;
            }

            _fadeCanvasGroup.alpha = to;
        }
    }
}
