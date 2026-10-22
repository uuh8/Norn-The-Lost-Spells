using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.Run.Tests
{
    /// <summary>
    /// Scene Transition 是常驻服务，不是“黑色图片是否显示”的开关。
    /// 因此 Fade Root 必须始终 active；真正的显隐由 CanvasGroup.alpha 管理。
    /// </summary>
    public sealed class SceneTransitionAuthoringTests
    {
        [TestCase("Assets/_Project/Scenes/P7_DemoRun.unity")]
        [TestCase("Assets/_Project/Scenes/P8_BossField.unity")]
        public void FadeControllerRoot_RemainsActiveAndRenderable(
            string scenePath)
        {
            // Preview Scene 不会替换开发者当前打开或尚未保存的 Scene，
            // 适合对磁盘上的 Authoring 数据执行自动化回归检查。
            Scene scene = EditorSceneManager.OpenPreviewScene(scenePath);
            try
            {
                DemoSceneTransitionController controller =
                    FindTransitionController(scene);

                Assert.That(
                    controller,
                    Is.Not.Null,
                    $"{scenePath} 缺少 DemoSceneTransitionController。");
                Assert.That(
                    controller.gameObject.activeSelf,
                    Is.True,
                    "Fade Root inactive 时 OnEnable 不会订阅 Scene Transition Event。");
                Assert.That(
                    controller.gameObject.activeInHierarchy,
                    Is.True,
                    "Fade Root 的父层级也必须保持 active。");
                Assert.That(controller.enabled, Is.True);
                Canvas canvas = controller.GetComponent<Canvas>();
                Assert.That(canvas, Is.Not.Null);
                Assert.That(canvas.enabled, Is.True);
                Assert.That(canvas.isRootCanvas, Is.True);
                Assert.That(canvas.renderMode, Is.EqualTo(RenderMode.ScreenSpaceOverlay));
                Assert.That(canvas.sortingOrder, Is.GreaterThan(0));
                CanvasScaler scaler = controller.GetComponent<CanvasScaler>();
                Assert.That(scaler, Is.Not.Null);
                Assert.That(scaler.enabled, Is.True);
                Assert.That(scaler.scaleFactor, Is.GreaterThan(0f));

                // Preview Scene没有GameView布局，Screen Space Canvas的驱动scale可以为0。
                // 静态合同检查独立于最终布局的黑幕接线；实际屏幕覆盖由PlayMode测试验证。
                var data = new SerializedObject(controller);
                var group = data.FindProperty("_fadeCanvasGroup").objectReferenceValue as CanvasGroup;
                Assert.That(group, Is.Not.Null);
                Assert.That(group.transform.IsChildOf(controller.transform), Is.True);
                Assert.That(group.gameObject.activeInHierarchy, Is.True);
                Assert.That(group.enabled, Is.True);
                Image blocker = group.GetComponent<Image>();
                Assert.That(blocker, Is.Not.Null);
                Assert.That(blocker.enabled, Is.True);
                Assert.That(blocker.raycastTarget, Is.True);
                Assert.That(blocker.color, Is.EqualTo(Color.black));
                RectTransform rect = blocker.rectTransform;
                Assert.That(rect.anchorMin, Is.EqualTo(Vector2.zero));
                Assert.That(rect.anchorMax, Is.EqualTo(Vector2.one));
                Assert.That(rect.offsetMin, Is.EqualTo(Vector2.zero));
                Assert.That(rect.offsetMax, Is.EqualTo(Vector2.zero));
                Assert.That(rect.localScale, Is.EqualTo(Vector3.one));
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        private static DemoSceneTransitionController FindTransitionController(
            Scene scene)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int rootIndex = 0; rootIndex < roots.Length; rootIndex++)
            {
                DemoSceneTransitionController[] controllers =
                    roots[rootIndex]
                        .GetComponentsInChildren<DemoSceneTransitionController>(
                            true);
                if (controllers.Length > 0)
                {
                    return controllers[0];
                }
            }

            return null;
        }
    }
}
