#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using Game.Character;
using Game.Core;
using Game.ElementField;
using Game.Materials;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace Game.Run.Tests
{
    /// <summary>直接进入 ContainerSandbox，注入独立临时档；验证真实 UI/Run/World 调用链。</summary>
    public sealed class ContainerSandboxPlayModeTests
    {
        private const string ScenePath = "Assets/_Project/Scenes/Tests/ContainerSandbox.unity";
        private const string Root = "Assets/_Project/ScriptableObjects/Tests/SinglePlayerContainers/";
        private Scene _scene;
        private SinglePlayerInventorySession _session;
        private ElementWorldRuntime _world;
        private MaterialCollectionController _collector;
        private ContainerSandboxMaterialAuthoring _supply;
        private string _directory;
        private float _time;
        private CursorLockMode _lock;
        private bool _cursor;
        private EditorWindow _previous;
        private Keyboard _keyboard;
        private GameObject _aimCameraObject;
        private readonly System.Collections.Generic.List<GraphicsBuffer> _domainBuffers = new System.Collections.Generic.List<GraphicsBuffer>();
        [UnitySetUp] public IEnumerator Setup()
        {
            _time = Time.timeScale; _lock = Cursor.lockState; _cursor = Cursor.visible; Time.timeScale = 1;
            _directory = Path.GetFullPath(Path.Combine("Temp/SinglePlayer/ContainerTests", Guid.NewGuid().ToString("N")));
            SceneManager.sceneLoaded += Loaded;
            _scene = EditorSceneManager.LoadSceneInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            yield return null; yield return null; yield return null; SceneManager.sceneLoaded -= Loaded;
            Assert.That(_session.ReadOnly, Is.Not.Null); Assert.That(_world.IsInitialized, Is.True, "World 未初始化"); Assert.That(_supply.Initialized, Is.True, "样本注入失败，请查 ContainerSandbox 日志");
        }
        private void Loaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.path != ScenePath) return;
            _scene = scene; _session = Find<SinglePlayerInventorySession>(); _world = Find<ElementWorldRuntime>();
            _collector = Find<MaterialCollectionController>(); _supply = Find<ContainerSandboxMaterialAuthoring>();
            Assert.That(_session.ConfigureAndInitialize(AssetDatabase.LoadAssetAtPath<ItemCatalog>(Root + "Catalog.asset"),
                AssetDatabase.LoadAssetAtPath<SinglePlayerStartingInventory>(Root + "StartingInventory.asset"), _directory), Is.True);
        }
        private T Find<T>() where T : Component
        { foreach (var root in _scene.GetRootGameObjects()) { var value = root.GetComponentInChildren<T>(true); if (value != null) return value; } return null; }
        private void Collect(Vector3 point, bool solid, ulong budget)
        { _collector.transform.position = point + new Vector3(0, .5f, -.5f); _collector.SubmitSample(point, solid, budget); }
        private static ulong Amount(MaterialAmount[] amounts, int count, byte key)
        { for (int i = 0; i < count; i++) if (amounts[i].MaterialKey == key) return amounts[i].Quantity; return 0; }
        [UnityTest] public IEnumerator MixedCollectionSplitsStackAndSavesActualComponents()
        {
            Collect(_supply.MixedSamplePosition, false, 30);
            Assert.That(_collector.LastFailure, Is.EqualTo(MaterialTransferFailure.None));
            var buffer = new MaterialAmount[8];
            Assert.That(_session.TryCopyContainer(_collector.SelectedContainerId, buffer, out int count, out ulong total, out ulong capacity), Is.True);
            Assert.That(total, Is.EqualTo(30)); Assert.That(capacity, Is.EqualTo(150));
            Assert.That(Amount(buffer, count, 1), Is.EqualTo(20)); Assert.That(Amount(buffer, count, 3), Is.EqualTo(10));
            var cell = Vector3Int.FloorToInt((_supply.MixedSamplePosition - _world.Origin) / _world.CellSize);
            _world.TryGetQuantity(cell, MaterialId.Water, out uint water); _world.TryGetQuantity(cell, MaterialId.Poison, out uint poison);
            Assert.That(water, Is.EqualTo(80)); Assert.That(poison, Is.EqualTo(40)); Assert.That(_session.Save().Success, Is.True);
            Assert.That(_world.TryCaptureMaterialQuantityFamily(MaterialQuantityFamily.WaterBased, out var family), Is.True);
            Assert.That(family.IsBalanced, Is.True, "注入/常驻/导出守恒。"); yield return null;
        }
        [UnityTest] public IEnumerator StableAndGasCollectionTransferActualOwnerQuantity()
        {
            Collect(_supply.StableSamplePosition, false, 20); Assert.That(_collector.LastFailure, Is.EqualTo(MaterialTransferFailure.None));
            Collect(_supply.GasSamplePosition, false, 10); Assert.That(_collector.LastFailure, Is.EqualTo(MaterialTransferFailure.None));
            var buffer = new MaterialAmount[8]; _session.TryCopyContainer(_collector.SelectedContainerId, buffer, out int count, out var total, out _);
            Assert.That(total, Is.EqualTo(30)); Assert.That(Amount(buffer, count, 1), Is.EqualTo(20)); Assert.That(Amount(buffer, count, 7), Is.EqualTo(10));
            Assert.That(_world.ExportedMaterialQuantity(MaterialId.Steam), Is.EqualTo(10)); yield return null;
        }
        [UnityTest] public IEnumerator SolidCreatesOneOwnedMaterialItemAndRejectsRepeatWithoutWorldStock()
        {
            Collect(_supply.SolidSamplePosition, true, 200); Assert.That(_collector.LastFailure, Is.EqualTo(MaterialTransferFailure.None));
            var items = new InventoryItemSnapshot[_session.ReadOnly.Count]; int count = _session.ReadOnly.CopyItemsTo(items); int solidCount = 0;
            for (int i = 0; i < count; i++) if (items[i].Kind == ItemKind.Material)
            { solidCount++; Assert.That(items[i].Location.Kind, Is.EqualTo(ItemLocationKind.Backpack)); }
            Assert.That(solidCount, Is.EqualTo(1)); Assert.That(_world.ExportedMaterialQuantity(MaterialId.Ice), Is.EqualTo(200));
            Collect(_supply.SolidSamplePosition, true, 200); Assert.That(_collector.LastFailure, Is.EqualTo(MaterialTransferFailure.NoMaterial));
            Assert.That(_session.Save().Success, Is.True); yield return null;
        }
        [UnityTest] public IEnumerator PauseAndOutOfReachNeverChangeQuantity()
        {
            Time.timeScale = 0; _collector.SubmitSample(_supply.MixedSamplePosition, false, 30);
            Assert.That(_world.ExportedMaterialQuantity(MaterialId.Water), Is.Zero); Time.timeScale = 1;
            _collector.SubmitSample(_supply.MixedSamplePosition + Vector3.forward * 10, false, 30);
            Assert.That(_collector.LastFailure, Is.EqualTo(MaterialTransferFailure.InvalidRequest));
            Assert.That(_world.ExportedMaterialQuantity(MaterialId.Water), Is.Zero); yield return null;
        }
        [UnityTest] public IEnumerator SceneRendersCompositionInGameView()
        {
            Collect(_supply.MixedSamplePosition, false, 30); yield return null;
            Assert.That(Find<ContainerCompositionView>(), Is.Not.Null);
            // 展示全部近处样本；只调整测试加载副本的 Camera，不保存到用户或交付 Scene。
            var camera = Find<Camera>(); var brain = camera.GetComponent("CinemachineBrain") as Behaviour; if (brain != null) brain.enabled = false;
            _collector.transform.position = new Vector3(0, .1f, -2.5f);
            camera.fieldOfView = 75; camera.transform.position = new Vector3(0, 6, -4);
            camera.transform.rotation = Quaternion.LookRotation(new Vector3(.25f, 0, .25f) - camera.transform.position);
            _previous = EditorWindow.focusedWindow; EditorWindow.GetWindow(Type.GetType("UnityEditor.GameView,UnityEditor")).Focus();
            yield return CaptureRenderedGameView("Temp/SinglePlayer/container-sandbox.png");
            // 读取一次实际 GameView PNG，不以 Draw 提交计数冒充像素出现。取样盒避开 HUD/角色。
            var screenshot = new Texture2D(2, 2);
            try
            {
                Assert.That(ImageConversion.LoadImage(screenshot, File.ReadAllBytes("Temp/SinglePlayer/container-sandbox.png")), Is.True);
                Assert.That(HasMaterialPixel(screenshot, camera, _supply.MixedSamplePosition, true), Is.True, "混合样本的绿色 Poison 表面必须实际可见。");
                Assert.That(HasMaterialPixel(screenshot, camera, _supply.StableSamplePosition, false), Is.True, "稳定 Water 的蓝色表面必须实际可见。");
            }
            finally { UnityEngine.Object.Destroy(screenshot); }
            var observations = new System.Text.StringBuilder();
            foreach (var root in _scene.GetRootGameObjects()) foreach (var component in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                var type = component.GetType(); if (type.Name != "LiquidSurfaceRenderer" && type.Name != "StableLiquidSurfaceRenderer") continue;
                // D3D12 会在 Rasterization 前拒绝缺失 SRV 的 Draw，即使运行时分支不读该 Buffer。
                var fallbackField = type.GetField("_emptyDomainLookup", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert.That(fallbackField, Is.Not.Null, "独立液面必须绑定空的 Shader domain Buffer。");
                var fallback = fallbackField.GetValue(component) as GraphicsBuffer;
                Assert.That(fallback != null && fallback.IsValid(), Is.True, "独立表面 Buffer 生命周期必须覆盖 Draw。");
                _domainBuffers.Add(fallback);
                observations.Append(type.Name).Append(" enabled=").Append(component.enabled);
                foreach (string fieldName in new[] { "_material", "_drawnViews", "_drawnVertices", "_visiblePatchCount", "_submittedDraws" })
                { var field = type.GetField(fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (field != null) observations.Append(' ').Append(fieldName).Append('=').Append(field.GetValue(component)); }
                observations.AppendLine();
                var materialField = type.GetField("_materialInstance", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var material = materialField?.GetValue(component) as Material;
                if (material != null) observations.Append("shader=").Append(material.shader.name).Append(" supported=").Append(material.shader.isSupported).AppendLine();
                var viewsField = type.GetField("_views", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (viewsField?.GetValue(component) is Array views) foreach (object view in views)
                {
                    if (view == null) continue; var viewType = view.GetType();
                    var mesh = viewType.GetField("Mesh")?.GetValue(view) as Mesh; if (mesh == null || mesh.vertexCount == 0) continue;
                    observations.Append("mesh=").Append(mesh.bounds).Append(" vertices=").Append(mesh.vertexCount);
                    foreach (string name in new[] { "Matrix", "LastMatrix", "Bounds", "LastBounds" })
                    { var field = viewType.GetField(name); if (field != null) observations.Append(' ').Append(name).Append('=').Append(field.GetValue(view)); }
                    observations.AppendLine();
                }
            }
            File.WriteAllText("Temp/SinglePlayer/container-render-observation.txt", observations.ToString());
        }
        private static IEnumerator CaptureRenderedGameView(string output)
        {
            // 切换GameView后可能截到尚未渲染的黑帧；有限等待真实画面，原材料颜色断言保持不变。
            // 不调用Camera.Render，避免用另一条渲染路径掩盖GameView实际问题。
            for (int attempt = 0; attempt < 6; attempt++)
            {
                DateTime capture = DateTime.UtcNow;
                EditorWindow.GetWindow(Type.GetType("UnityEditor.GameView,UnityEditor")).Repaint();
                ScreenCapture.CaptureScreenshot(output); yield return null; yield return new WaitForSecondsRealtime(.3f);
                if (!File.Exists(output) || File.GetLastWriteTimeUtc(output) < capture) continue;
                var texture = new Texture2D(2, 2); bool rendered = false;
                try
                {
                    Assert.That(ImageConversion.LoadImage(texture, File.ReadAllBytes(output)), Is.True);
                    for (int y = 0; y < texture.height && !rendered; y += 32)
                    for (int x = 0; x < texture.width && !rendered; x += 32)
                    { var color = texture.GetPixel(x, y); rendered = color.r > .05f || color.g > .05f || color.b > .05f; }
                }
                finally { UnityEngine.Object.Destroy(texture); }
                if (rendered) yield break;
            }
            Assert.Fail("有限等待后GameView截图仍未就绪或全黑；无法进行材料视觉验证。");
        }
        private static bool HasMaterialPixel(Texture2D screenshot, Camera camera, Vector3 point, bool poison)
        {
            Vector3 viewport = camera.WorldToViewportPoint(point);
            int cx = Mathf.RoundToInt(viewport.x * screenshot.width), cy = Mathf.RoundToInt(viewport.y * screenshot.height);
            for (int y = Mathf.Max(0, cy - 28); y <= Mathf.Min(screenshot.height - 1, cy + 28); y++)
            for (int x = Mathf.Max(0, cx - 28); x <= Mathf.Min(screenshot.width - 1, cx + 28); x++)
            {
                Color color = screenshot.GetPixel(x, y);
                if (poison ? color.g > color.r + .1f && color.g > color.b + .1f
                    : color.b > color.r + .1f && color.b > color.g + .025f) return true;
            }
            return false;
        }
        [UnityTest] public IEnumerator HeldInputCollectsAtFixedRateAndReleaseStops()
        {
            _keyboard = InputSystem.AddDevice<Keyboard>();
            _aimCameraObject = new GameObject("Collection Test Aim"); var camera = _aimCameraObject.AddComponent<Camera>(); camera.enabled = false;
            camera.transform.position = _supply.MixedSamplePosition + Vector3.up * 2; camera.transform.rotation = Quaternion.Euler(90, 0, 0);
            typeof(MaterialCollectionController).GetField("_aimCamera", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(_collector, camera);
            _collector.transform.position = _supply.MixedSamplePosition + new Vector3(0, .5f, -.5f);
            float start = Time.time;
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState(Key.E)); InputSystem.Update();
            yield return new WaitForSeconds(.32f);
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState()); InputSystem.Update(); yield return null;
            float elapsed = Time.time - start;
            var buffer = new MaterialAmount[8]; _session.TryCopyContainer(_collector.SelectedContainerId, buffer, out _, out ulong total, out _);
            Assert.That(total, Is.GreaterThan(0)); Assert.That(total, Is.LessThanOrEqualTo((ulong)Mathf.Ceil(elapsed * 100) + 1));
            yield return new WaitForSeconds(.2f); _session.TryCopyContainer(_collector.SelectedContainerId, buffer, out _, out ulong after, out _);
            Assert.That(after, Is.EqualTo(total));
        }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            SceneManager.sceneLoaded -= Loaded; var ui = _scene.IsValid() && _scene.isLoaded ? Find<SinglePlayerInventoryController>() : null; if (ui != null) ui.Close();
            if (_keyboard != null) { InputSystem.RemoveDevice(_keyboard); _keyboard = null; }
            if (_aimCameraObject != null) UnityEngine.Object.Destroy(_aimCameraObject);
            if (_scene.IsValid() && _scene.isLoaded) yield return SceneManager.UnloadSceneAsync(_scene);
            yield return null; Time.timeScale = _time; Cursor.lockState = _lock; Cursor.visible = _cursor; if (_previous != null) _previous.Focus();
            string root = Path.GetFullPath("Temp/SinglePlayer/ContainerTests") + Path.DirectorySeparatorChar;
            if (_directory.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(_directory)) Directory.Delete(_directory, true);
            // GPU 资源必须由 OnDestroy 释放，托管 GC 不负责清理 GraphicsBuffer。
            foreach (var buffer in _domainBuffers) Assert.That(buffer.IsValid(), Is.False, "Scene 卸载后独立液面 Buffer 未释放。");
            _domainBuffers.Clear();
        }
    }
}
#endif
