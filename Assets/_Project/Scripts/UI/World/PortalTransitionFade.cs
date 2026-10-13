using System.Collections;
using Game.Core;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>跨Scene的唯一过场黑幕；unscaled time确保Gameplay暂停时仍能渐变。</summary>
    public sealed class PortalTransitionFade : MonoBehaviour
    {
        private CanvasGroup _group;
        private Coroutine _routine;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            var root = new GameObject("PortalTransitionFade"); DontDestroyOnLoad(root); root.AddComponent<PortalTransitionFade>();
        }
        private void Awake()
        {
            var canvas = gameObject.AddComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 32760;
            gameObject.AddComponent<GraphicRaycaster>();
            _group = gameObject.AddComponent<CanvasGroup>(); _group.alpha = 0; _group.blocksRaycasts = false;
            var cover = new GameObject("Black", typeof(RectTransform), typeof(Image)); cover.transform.SetParent(transform, false);
            var rect = (RectTransform)cover.transform; rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero;
            cover.GetComponent<Image>().color = Color.black;
            EventBus<PortalFadeRequestedEvent>.Subscribe(OnRequest); SceneManager.sceneLoaded += OnLoaded;
        }
        private void OnLoaded(Scene scene, LoadSceneMode mode)
        { EventBus<PortalFadeRequestedEvent>.Unsubscribe(OnRequest); EventBus<PortalFadeRequestedEvent>.Subscribe(OnRequest); }
        private void OnRequest(PortalFadeRequestedEvent e)
        { if (_routine != null) StopCoroutine(_routine); _routine = StartCoroutine(Fade(e.Black ? 1 : 0, e.Duration)); }
        private IEnumerator Fade(float target, float duration)
        {
            _group.blocksRaycasts = true;
            if (target == 0) { yield return null; yield return null; } // 等目标Scene Start完成，再揭开黑幕。
            float from = _group.alpha, time = 0;
            while (time < duration) { time += Time.unscaledDeltaTime; _group.alpha = Mathf.Lerp(from, target, duration > 0 ? time / duration : 1); yield return null; }
            _group.alpha = target; _group.blocksRaycasts = target > 0; _routine = null;
        }
        private void OnDestroy()
        { EventBus<PortalFadeRequestedEvent>.Unsubscribe(OnRequest); SceneManager.sceneLoaded -= OnLoaded; }
    }
}
