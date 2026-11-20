using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Game.Core;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Game.Run.Tests
{
    /// <summary>
    /// 用正式共享背包的 Rect 树与真实 Refresh 生成格子，再测 Camera 投影后的格子和线条 mesh。
    /// 测试只持有内存只读数据；不启用账号、输入、存档或 Gameplay，也不保存 Prefab/Scene。
    /// 格子等宽不等于线条可见：小于 1 pixel 的边线可能漏采样，看起来就像几格被拉成一格。
    /// </summary>
    public sealed class InventoryGridPixelGeometryTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private Scene _scene;
        private GameObject _root, _cameraRoot;
        private Camera _camera;
        private Canvas _canvas;
        private RenderTexture _target;
        private InventoryGridLinesGraphic _lines;
        private readonly List<RectTransform> _cells = new List<RectTransform>();
        private int _columns, _rows;

        [TearDown] public void Cleanup()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            if (_camera != null) _camera.targetTexture = null;
            if (_target != null) Object.DestroyImmediate(_target);
            if (_cameraRoot != null) Object.DestroyImmediate(_cameraRoot);
            if (_scene.IsValid() && _scene.isLoaded) EditorSceneManager.ClosePreviewScene(_scene);
            _cells.Clear();
        }

        [TestCase("Nearby", 1280, 720)]
        [TestCase("Nearby", 1424, 714)]
        [TestCase("Nearby", 1920, 1080)]
        [TestCase("Storage", 1280, 720)]
        [TestCase("Storage", 1424, 714)]
        [TestCase("Storage", 1920, 1080)]
        [TestCase("Backpack", 1280, 720)]
        [TestCase("Backpack", 1424, 714)]
        [TestCase("Backpack", 1920, 1080)]
        [TestCase("Wand", 1280, 720)]
        [TestCase("Wand", 1424, 714)]
        [TestCase("Wand", 1920, 1080)]
        public void OriginalCellsStaySquareAndEveryBoundaryHasOneScreenPixel(string region, int width, int height)
        {
            Build(region);
            Viewport(width, height);
            AssertCells();
            AssertLines();
        }

        [Test] public void SameGridSurvivesScaleChangesWithoutRefreshingInventoryOrRecreatingCells()
        {
            Build("Nearby");
            int graphicId = _lines.GetInstanceID();
            var cellIds = _cells.ConvertAll(cell => cell.GetInstanceID());
            foreach (var size in new[] { new Vector2Int(1920, 1080), new Vector2Int(1280, 720),
                new Vector2Int(2560, 1440), new Vector2Int(1424, 714), new Vector2Int(1920, 1080) })
            {
                // 窗口换尺寸不会触发库存版本事件；必须靠 Graphic 自己感知 Canvas 缩放，不能依赖 Refresh。
                Viewport(size.x, size.y);
                AssertCells(); AssertLines();
                Assert.That(_lines.GetInstanceID(), Is.EqualTo(graphicId));
                for (int i = 0; i < _cells.Count; i++) Assert.That(_cells[i].GetInstanceID(), Is.EqualTo(cellIds[i]));
            }
        }

        private void Build(string region)
        {
            _scene = EditorSceneManager.NewPreviewScene();
            _root = new GameObject("Inactive original inventory geometry"); _root.SetActive(false);
            SceneManager.MoveGameObjectToScene(_root, _scene);
            var prefab = Resources.Load<GameObject>("Presentation/CombatInventory"); Assert.That(prefab, Is.Not.Null);
            var copy = Object.Instantiate(prefab, _root.transform, false);
            var ui = copy.GetComponentInChildren<SinglePlayerInventoryController>(true); Assert.That(ui, Is.Not.Null);
            typeof(SinglePlayerInventoryController).GetMethod("ConfigureViewportLayout", Private).Invoke(ui, null);
            Set(ui, "_hasPresentationOverride", true); Set(ui, "_presentationSource", new EmptySource());
            _canvas = ui.GetComponentInParent<Canvas>(true).rootCanvas;
            var scaler = _canvas.GetComponent<CanvasScaler>();
            Assert.That(scaler.screenMatchMode, Is.EqualTo(CanvasScaler.ScreenMatchMode.Expand));
            // inactive 原树先用作者参考面积解析 anchors；激活前只离散调用 View.Refresh，不调用 Controller 生命周期。
            _canvas.renderMode = RenderMode.WorldSpace;
            var canvasRect = (RectTransform)_canvas.transform;
            canvasRect.localScale = Vector3.one; canvasRect.sizeDelta = scaler.referenceResolution;
            canvasRect.ForceUpdateRectTransforms();
            var itemPrefab = (InventoryItemView)Get(ui, "_itemPrefab");
            RectTransform content;
            if (region == "Nearby")
            {
                var view = (NearbyItemGridView)Get(ui, "_nearbyGrid");
                view.Refresh(ui, itemPrefab, Array.Empty<InventoryItemSnapshot>(), 0, null);
                content = (RectTransform)view.transform; _columns = view.Columns; _rows = view.Rows;
                AddRects((IList)Get(view, "_cells"));
            }
            else if (region == "Storage")
            {
                var view = (SpellStorageView)Get(ui, "_spellStorage");
                view.Refresh(ui, itemPrefab, Array.Empty<InventoryItemSnapshot>(), 0, "geometry");
                content = (RectTransform)Get(view, "_content"); _columns = view.VisibleSlotCount; _rows = 1;
                AddRects((IList)Get(view, "_slots"));
            }
            else if (region == "Backpack")
            {
                var view = (InventoryGridView)Get(ui, "_gridView");
                var item = Item("Assets/_Project/ScriptableObjects/Items/Backpacks/Item_Backpack_StarterBag.asset",
                    new ItemLocation(ItemLocationKind.EquippedBackpack, "geometry", 0, 0, 0, 0));
                view.Initialize(ui, itemPrefab); view.Refresh(new[] { item }, 1, item.InstanceId);
                content = (RectTransform)view.transform; _columns = item.Rules.BackpackWidth; _rows = item.Rules.BackpackHeight;
                AddRects((IList)Get(view, "_cells"));
            }
            else
            {
                var view = copy.GetComponentInChildren<WandProgramPanelView>(true);
                Assert.That(view, Is.Not.Null);
                int slot = (int)Get(view, "_wandSlot");
                var item = Item("Assets/_Project/ScriptableObjects/Spell/Wands/Items/Item_Wand_StarterA.asset",
                    new ItemLocation(ItemLocationKind.EquippedWand, "geometry", 0, 0, 0, slot));
                view.Refresh(ui, itemPrefab, new[] { item }, 1, "geometry");
                content = (RectTransform)Get(view, "_program"); _columns = item.Rules.WandProgramCapacity; _rows = 1;
                foreach (GameObject cell in (IList)Get(view, "_slots")) _cells.Add((RectTransform)cell.transform);
            }
            Assert.That(_columns, Is.GreaterThan(1)); Assert.That(_rows, Is.GreaterThan(0));
            Assert.That(_cells.Count, Is.EqualTo(_columns * _rows));
            _lines = content.GetComponentInChildren<InventoryGridLinesGraphic>(true); Assert.That(_lines, Is.Not.Null);
            // 只打开测试根以下的祖先；外层 root 始终 inactive，避免在移除 Adapter 前执行 OnEnable/Awake。
            for (Transform node = content; node != null && node != _root.transform; node = node.parent) node.gameObject.SetActive(true);
            // disabled MonoBehaviour 仍可能 Awake；删除本测试副本里的 Game Adapter，只保留要验证的生产 Graphic。
            // 原资产完全不改，输入 ActionReference 也从未 Enable/Disable。
            foreach (var behaviour in copy.GetComponentsInChildren<MonoBehaviour>(true))
                if (behaviour != _lines && behaviour.GetType().Namespace?.StartsWith("Game.", StringComparison.Ordinal) == true)
                    Object.DestroyImmediate(behaviour);
            _cameraRoot = new GameObject("Inventory geometry camera"); SceneManager.MoveGameObjectToScene(_cameraRoot, _scene);
            _camera = _cameraRoot.AddComponent<Camera>(); _camera.enabled = false;
            _camera.nearClipPlane = .1f; _camera.farClipPlane = 100;
            _camera.overrideSceneCullingMask = EditorSceneManager.GetSceneCullingMask(_scene);
            _canvas.renderMode = RenderMode.ScreenSpaceCamera; _canvas.worldCamera = _camera; _canvas.planeDistance = 10;
            _root.SetActive(true);
        }

        private void Viewport(int width, int height)
        {
            _camera.targetTexture = null;
            if (_target != null) Object.DestroyImmediate(_target);
            _target = new RenderTexture(width, height, 0); _camera.targetTexture = _target;
            Canvas.ForceUpdateCanvases();
            typeof(CanvasScaler).GetMethod("Handle", Private).Invoke(_canvas.GetComponent<CanvasScaler>(), null);
            Canvas.ForceUpdateCanvases();
            // EditMode 测试没有 PlayerLoop；调用真实 LateUpdate。旧版没有此回调，保留其原行为得到行为 RED。
            typeof(InventoryGridLinesGraphic).GetMethod("LateUpdate", Private)?.Invoke(_lines, null);
            Canvas.ForceUpdateCanvases();
            // Camera/Canvas 内部浮点换算在 1920 附近会有不足百分之一 pixel 的舍入，
            // 这里只验证目标视口；实际 mesh 最小线宽仍独立严格要求 >= 0.998 pixel。
            Assert.That(Vector2.Distance(_canvas.renderingDisplaySize, new Vector2(width, height)), Is.LessThan(.01f));
        }

        private void AssertCells()
        {
            var first = Bounds(_cells[0]); Assert.That(first.width, Is.GreaterThan(8));
            for (int i = 0; i < _cells.Count; i++)
            {
                var bounds = Bounds(_cells[i]);
                Assert.That(bounds.width, Is.EqualTo(first.width).Within(.02f), "cell " + i + " width");
                Assert.That(bounds.height, Is.EqualTo(first.width).Within(.02f), "cell " + i + " square");
                Assert.That(bounds.xMin, Is.EqualTo(first.xMin + i % _columns * first.width).Within(.03f));
                Assert.That(bounds.yMax, Is.EqualTo(first.yMax - i / _columns * first.height).Within(.03f));
                Assert.That(_cells[i].anchorMin, Is.EqualTo(_cells[i].anchorMax), "单格不能靠 stretch 掩盖尺寸错误。");
            }
        }

        private void AssertLines()
        {
            // Unity 6000.3 只提供无参 GetMesh；返回 mesh 归 CanvasRenderer/Graphic 管理。
            // 本测试仅复制读取 vertices，绝不 Destroy 生产 Graphic 持有的 mesh。
            var mesh = _lines.canvasRenderer.GetMesh();
            Assert.That(mesh, Is.Not.Null);
            {
                var vertices = mesh.vertices;
                Assert.That(vertices.Length, Is.EqualTo((_columns + _rows + 2) * 4), "实际上传 mesh 必须包含每条横/竖边界。");
                float minimum = Mathf.Max(1f, _canvas.scaleFactor);
                for (int line = 0; line < _columns + _rows + 2; line++)
                {
                    int first = line * 4;
                    var a = Project(_lines.rectTransform, vertices[first]);
                    var b = Project(_lines.rectTransform, vertices[first + 2]);
                    float thickness = line <= _columns ? Mathf.Abs(b.x - a.x) : Mathf.Abs(b.y - a.y);
                    Assert.That(thickness, Is.GreaterThanOrEqualTo(minimum - .002f),
                        "边线 " + line + " 在当前缩放下不足 1 pixel，会把等宽格视觉合并；scale=" + _canvas.scaleFactor);
                    Assert.That(thickness, Is.LessThanOrEqualTo(minimum + .002f), "修复不能任意加粗边线遮盖物品。");
                }
            }
        }
        private Rect Bounds(RectTransform rect)
        {
            var corners = new Vector3[4]; rect.GetWorldCorners(corners);
            var a = _camera.WorldToScreenPoint(corners[0]); var b = _camera.WorldToScreenPoint(corners[2]);
            return Rect.MinMaxRect(a.x, a.y, b.x, b.y);
        }
        private Vector3 Project(RectTransform rect, Vector3 vertex) => _camera.WorldToScreenPoint(rect.TransformPoint(vertex));
        private void AddRects(IList values) { foreach (Component value in values) _cells.Add((RectTransform)value.transform); }
        private static object Get(object target, string field) => target.GetType().GetField(field, Private).GetValue(target);
        private static void Set(object target, string field, object value) => target.GetType().GetField(field, Private).SetValue(target, value);
        private static InventoryItemSnapshot Item(string path, ItemLocation location)
        {
            var definition = AssetDatabase.LoadAssetAtPath<ItemDefinition>(path); Assert.That(definition, Is.Not.Null);
            var args = new object[] { default(InventoryItemRules), null };
            Assert.That((bool)typeof(ItemDefinition).GetMethod("TryCreateRules", Private).Invoke(definition, args), Is.True, args[1] as string);
            return new InventoryItemSnapshot(new RuntimeItemState("geometry-item", definition.ContentId, 0), location,
                definition, (InventoryItemRules)args[0]);
        }

        private sealed class EmptySource : IInventoryPresentationSource, IWandLoadoutReadOnly
        {
            public IInventoryReadOnly ReadOnly => null; public IToolbeltReadOnly ToolbeltReadOnly => null;
            public IWandLoadoutReadOnly WandsReadOnly => this; public ISpellUseReadOnly SpellUsesReadOnly => null;
            public IWalletReadOnly WalletReadOnly => null; public bool UseSpellProgrammingV2 => true;
            public bool CanAccessWarehouse => false; public string WorldSceneKey => "geometry"; public string EquippedBackpackId => null;
            public int SpellStorageCapacity => 12; public string InitializationMessage => ""; public bool HasUnsavedChanges => false;
            public bool TryCaptureWandResearch(string id, out WandResearchSnapshot snapshot) { snapshot = default; return false; }
            public bool TryCopyContainer(string id, MaterialAmount[] buffer, out int count, out ulong total, out ulong capacity)
            { count = 0; total = capacity = 0; return false; }
            public string InventoryId => "geometry"; public string SelectedWandId => null; public uint ResourceVersion => 1;
            public bool TryGetSlot(int slot, out WandStateSnapshot state) { state = default; return false; }
            public bool TryGetState(string id, out WandStateSnapshot state) { state = default; return false; }
            public bool TryGetProgram(string id, out WandProgramSnapshot program) { program = default; return false; }
        }
    }
}
