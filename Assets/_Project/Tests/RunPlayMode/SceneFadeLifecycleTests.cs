#if UNITY_EDITOR
using System.Collections;
using Game.UI;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Game.Run.Tests
{
    /// <summary>直接实例化磁盘Scene中的生产Fade对象，验证Canvas布局与暂停下的淡入，不走通关流程。</summary>
    public sealed class SceneFadeLifecycleTests
    {
        [UnityTest]
        public IEnumerator P7AuthoredFade_CoversScreenAndReleasesBlockerWhilePaused()
        {
            yield return CheckAuthoredFade("Assets/_Project/Scenes/P7_DemoRun.unity");
        }

        [UnityTest]
        public IEnumerator P8AuthoredFade_CoversScreenAndReleasesBlockerWhilePaused()
        {
            yield return CheckAuthoredFade("Assets/_Project/Scenes/P8_BossField.unity");
        }

        private static IEnumerator CheckAuthoredFade(string scenePath)
        {
            Scene preview = EditorSceneManager.OpenPreviewScene(scenePath);
            GameObject instance = null;
            try
            {
                DemoSceneTransitionController authored = null;
                foreach (GameObject root in preview.GetRootGameObjects())
                {
                    authored = root.GetComponentInChildren<DemoSceneTransitionController>(true);
                    if (authored != null) break;
                }
                Assert.That(authored, Is.Not.Null);
                // Instantiate保留Scene overrides，避免只测prefab而漏掉场景中的零scale或失效引用。
                instance = Object.Instantiate(authored.gameObject);
                Assert.That(EditorSceneManager.IsPreviewScene(instance.scene), Is.False);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(preview);
            }

            float previousTimeScale = Time.timeScale;
            bool previousBackground = Application.runInBackground;
            try
            {
                Application.runInBackground = true;
                UnityEditor.EditorWindow.GetWindow(
                    typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.GameView")).Focus();
                Time.timeScale = 0f;
                Canvas canvas = instance.GetComponent<Canvas>();
                CanvasGroup group = instance.GetComponentInChildren<CanvasGroup>();
                Image blocker = group.GetComponent<Image>();
                Assert.That(group.alpha, Is.EqualTo(1f), "Awake必须先阻挡初始画面。");
                Assert.That(group.blocksRaycasts, Is.True);
                Assert.That(canvas.renderMode, Is.EqualTo(RenderMode.ScreenSpaceOverlay));
                Canvas.ForceUpdateCanvases();
                yield return null;
                Canvas.ForceUpdateCanvases();
                Assert.That(canvas.pixelRect.width, Is.GreaterThan(0f));
                Assert.That(canvas.pixelRect.height, Is.GreaterThan(0f));
                var corners = new Vector3[4];
                blocker.rectTransform.GetWorldCorners(corners);
                // Overlay Canvas的world坐标就是屏幕坐标；最终黑幕必须覆盖完整pixelRect。
                Assert.That(corners[0].x, Is.EqualTo(canvas.pixelRect.xMin).Within(1f));
                Assert.That(corners[0].y, Is.EqualTo(canvas.pixelRect.yMin).Within(1f));
                Assert.That(corners[2].x, Is.EqualTo(canvas.pixelRect.xMax).Within(1f));
                Assert.That(corners[2].y, Is.EqualTo(canvas.pixelRect.yMax).Within(1f));
                Assert.That(instance.transform.localScale.sqrMagnitude, Is.GreaterThan(0f));
                yield return new WaitForSecondsRealtime(.4f);
                Assert.That(group.alpha, Is.EqualTo(0f));
                Assert.That(group.blocksRaycasts, Is.False,
                    "暂停时必须通过unscaledDeltaTime完成淡入并释放UI阻挡。");
                Assert.That(instance.activeInHierarchy, Is.True);
            }
            finally
            {
                Object.DestroyImmediate(instance);
                Time.timeScale = previousTimeScale;
                Application.runInBackground = previousBackground;
            }
        }
    }
}
#endif
