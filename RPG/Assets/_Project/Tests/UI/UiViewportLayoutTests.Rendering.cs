using System;
using System.IO;
using System.Reflection;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Game.UI.Tests
{
    public sealed partial class UiViewportLayoutTests
    {
        [Test] public void OriginalLoginRendersCoveredBackgroundAndCompleteCardAtFiveTargetSizes()
        {
            Assert.That(SystemInfo.graphicsDeviceType, Is.Not.EqualTo(GraphicsDeviceType.Null),
                "图像验收需要真实GraphicsDevice；-nographics只能运行本类的几何用例。");
            var sourceScene = Open("Assets/_Project/Scenes/P7_MainMenu.unity");
            var source = Find<DemoMainMenuController>(sourceScene)[0];
            var scene = EditorSceneManager.NewPreviewScene(); _scenes.Add(scene);
            var root = new GameObject("Inactive login rendering fixture"); root.SetActive(false); _roots.Add(root);
            SceneManager.MoveGameObjectToScene(root, scene);
            var menu = Object.Instantiate(source, root.transform, false); Configure(menu);
            var data = new SerializedObject(menu);
            var background = (GameObject)data.FindProperty("_mainPanel").objectReferenceValue;
            background.SetActive(true);
            foreach (var button in background.GetComponentsInChildren<Button>(true)) button.gameObject.SetActive(false);
            ((GameObject)data.FindProperty("_settingsPanel").objectReferenceValue).SetActive(false);
            var account = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/_Project/Prefabs/UI/Online/AccountMenu.prefab"), root.transform, false);
            Configure(account.GetComponent<AccountMenuController>());
            var card = (RectTransform)account.transform.Find("Panel");
            var canvases = root.GetComponentsInChildren<Canvas>(true);
            // 只复制原UI视觉，删去夹具内的账号Bootstrap和Gameplay Adapter，绝不发认证/Observe请求。
            // 即使disabled组件也可能执行Awake，所以不能只把enabled设为false后就激活整棵树。
            foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                if (behaviour.GetType().Namespace?.StartsWith("Game.", StringComparison.Ordinal) == true)
                    Object.DestroyImmediate(behaviour);
            foreach (var node in root.GetComponentsInChildren<Transform>(true)) node.gameObject.layer = 5;

            var cameraRoot = new GameObject("Login fixture camera"); _roots.Add(cameraRoot);
            SceneManager.MoveGameObjectToScene(cameraRoot, scene);
            var camera = cameraRoot.AddComponent<Camera>(); camera.enabled = false;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.magenta;
            camera.cullingMask = 1 << 5; camera.nearClipPlane = .1f; camera.farClipPlane = 100;
            camera.overrideSceneCullingMask = EditorSceneManager.GetSceneCullingMask(scene);
            foreach (var canvas in canvases)
            { canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = camera; canvas.planeDistance = 10; }
            var font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei", "SimSun", "Arial" }, 20);
            foreach (var label in root.GetComponentsInChildren<Text>(true)) label.font = font;
            string directory = Path.GetFullPath("docs/superpowers/evidence/multiplayer/single-player-regression-20261107/ui-resolution");
            Directory.CreateDirectory(directory);
            root.SetActive(true);
            try
            {
                foreach (var size in Viewports)
                {
                    var target = new RenderTexture((int)size.x, (int)size.y, 24, RenderTextureFormat.ARGB32);
                    var pixels = new Texture2D((int)size.x, (int)size.y, TextureFormat.RGBA32, false);
                    var previous = RenderTexture.active;
                    try
                    {
                        target.Create(); camera.targetTexture = target;
                        Canvas.ForceUpdateCanvases();
                        foreach (var canvas in canvases)
                        {
                            Assert.That(canvas.renderingDisplaySize.x, Is.EqualTo(size.x).Within(.1f));
                            Assert.That(canvas.renderingDisplaySize.y, Is.EqualTo(size.y).Within(.1f));
                            // EditMode没有Player Update；显式执行原UGUI Scaler，而不是测试重算比例冒充渲染。
                            typeof(CanvasScaler).GetMethod("Handle", Private).Invoke(canvas.GetComponent<CanvasScaler>(), null);
                        }
                        Canvas.ForceUpdateCanvases();
                        AssertScreenFits(card, camera, size);
                        AssertScreenCovers((RectTransform)background.transform, camera, size);
                        if (GraphicsSettings.currentRenderPipeline == null) camera.Render();
                        else
                        {
                            var request = new RenderPipeline.StandardRequest { destination = target };
                            Assert.That(RenderPipeline.SupportsRenderRequest(camera, request), Is.True);
                            RenderPipeline.SubmitRenderRequest(camera, request);
                        }
                        RenderTexture.active = target;
                        pixels.ReadPixels(new Rect(0, 0, size.x, size.y), 0, 0); pixels.Apply();
                        // Camera清屏色故意为纯洋红。逐边采样必须真的被原RGB背景覆盖；
                        // 仅检查Image/Rect存在无法发现Canvas未绘制或超宽边缘露出世界。
                        for (int x = 0; x < target.width; x += 32)
                        { AssertCovered(pixels.GetPixel(x, 0), size); AssertCovered(pixels.GetPixel(x, target.height - 1), size); }
                        for (int y = 0; y < target.height; y += 32)
                        { AssertCovered(pixels.GetPixel(0, y), size); AssertCovered(pixels.GetPixel(target.width - 1, y), size); }
                        File.WriteAllBytes(Path.Combine(directory, $"login-{target.width}x{target.height}.png"), pixels.EncodeToPNG());
                        TestContext.WriteLine($"Rendered original login UI {target.width}x{target.height}; original camera target={camera.pixelRect}");
                    }
                    finally
                    {
                        camera.targetTexture = null; RenderTexture.active = previous;
                        Object.DestroyImmediate(pixels); target.Release(); Object.DestroyImmediate(target);
                    }
                }
            }
            finally { Object.DestroyImmediate(font); }
        }

        private static Rect ScreenRect(RectTransform rect, Camera camera)
        {
            var corners = new Vector3[4]; rect.GetWorldCorners(corners);
            var lower = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
            var upper = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
            return Rect.MinMaxRect(lower.x, lower.y, upper.x, upper.y);
        }
        private static void AssertScreenFits(RectTransform rect, Camera camera, Vector2 size)
        {
            var bounds = ScreenRect(rect, camera);
            Assert.That(bounds.xMin, Is.GreaterThanOrEqualTo(-1f)); Assert.That(bounds.yMin, Is.GreaterThanOrEqualTo(-1f));
            Assert.That(bounds.xMax, Is.LessThanOrEqualTo(size.x + 1)); Assert.That(bounds.yMax, Is.LessThanOrEqualTo(size.y + 1));
        }
        private static void AssertScreenCovers(RectTransform rect, Camera camera, Vector2 size)
        {
            var bounds = ScreenRect(rect, camera);
            Assert.That(bounds.xMin, Is.LessThanOrEqualTo(1f)); Assert.That(bounds.yMin, Is.LessThanOrEqualTo(1f));
            Assert.That(bounds.xMax, Is.GreaterThanOrEqualTo(size.x - 1)); Assert.That(bounds.yMax, Is.GreaterThanOrEqualTo(size.y - 1));
        }
        private static void AssertCovered(Color pixel, Vector2 size)
        { Assert.That(pixel.r > .95f && pixel.b > .95f && pixel.g < .05f, Is.False, "原背景边缘未绘制：" + size); }
    }
}
