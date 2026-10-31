using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Game.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
#if UNITY_EDITOR
using UnityEditor;
#endif
using Object = UnityEngine.Object;

namespace Game.Run.Tests
{
    /// <summary>
    /// 在真实GameView viewport下验证导出的共同HUD，而不是把inactive Preview的参数一致当成响应式通过。
    /// 只启动隔离Scene中的原UGUI树；Game.* Adapter先禁用，不申请Host lease、不接入角色或库存。
    /// 这组测试覆盖布局与CanvasScaler，不代替战斗显隐、输入、Native画面或GC Alloc验收。
    /// </summary>
    public sealed class CombatHudResolutionPlayModeTests
    {
        private const string EvidenceDirectory = "docs/superpowers/evidence/multiplayer/n4/movement-smoothing/followup";
        private Scene _scene;
        private GameObject _fixtureRoot;
        private RectTransform _playerHealth, _boss, _bossName, _bossPhase, _stage, _notification, _held;
        private RectTransform[] _heldSlots;
        private Canvas[] _canvases;
#if UNITY_EDITOR
        private TemporaryFixedGameView _view;
#endif

        [UnityTearDown]
        public IEnumerator RestoreOnlyThisFixtureAndItsTemporaryGameViewSizes()
        {
            // 即使尺寸/布局断言失败，仍先恢复用户原选项，再删自己创建的尺寸；不保存Editor布局。
            // 恢复错误延后报告，保证隔离Scene也能释放，不能因首个异常遗留测试对象。
            Exception restoreError = null;
#if UNITY_EDITOR
            try { if (_view != null) _view.Dispose(); }
            catch (Exception error) { restoreError = error; }
            _view = null;
#endif
            if (_fixtureRoot != null) Object.Destroy(_fixtureRoot);
            if (_scene.IsValid() && _scene.isLoaded) yield return SceneManager.UnloadSceneAsync(_scene);
            yield return null;
            _fixtureRoot = null; _scene = default; _canvases = null; _heldSlots = null;
            if (restoreError != null) Assert.Fail("恢复本测试GameView状态失败：" + restoreError);
        }

        [UnityTest]
        public IEnumerator OriginalSharedHudKeepsItsScreenEdgesAndScaleInThreeActualViewports()
        {
#if !UNITY_EDITOR
            Assert.Ignore("本例临时切换Editor GameView，不作为Native Player的分辨率验收。");
            yield break;
#else
            _view = new TemporaryFixedGameView();
            BuildIsolatedOriginalHud();
            string evidence = Path.GetFullPath(Path.Combine(Application.dataPath, "..", EvidenceDirectory));
            Directory.CreateDirectory(evidence);
            File.WriteAllText(Path.Combine(evidence, "ui-layout-scope.txt"),
                "Editor isolated original CombatHud Resource layout evidence.\n" +
                "Game.* Adapters are disabled; there is no Actor, inventory session, network client or CombatPresentationHost lease.\n" +
                "Boss and Stage leaves are shown in separate captures, matching their mutually exclusive authoring policy.\n" +
                "The evidence covers actual GameView dimensions, original UGUI rendering and responsive layout.\n" +
                "It does not establish Native player flow, actual combat, account/asset authority or GC Alloc acceptance.\n");
            var sizes = new[] { new Vector2Int(1920, 1080), new Vector2Int(2560, 1440), new Vector2Int(2560, 1600) };
            // 独立手算的Match .5结果：1、4/3、sqrt((4/3)*(40/27))。
            // 不读取产品Helper生成预期，避免同一个错误同时出现在实际值和断言中。
            var expectedScales = new[] { 1f, 1.33333333f, 1.40545674f };
            EdgeMeasurement baseline = default;
            for (int index = 0; index < sizes.Length; index++)
            {
                var target = sizes[index]; _view.Select(target.x, target.y);
                // selectedSizeIndex已写入不等于viewport已生效。等Unity真正完成尺寸/Canvas刷新后再量屏幕角点。
                for (int frame = 0; frame < 60; frame++)
                {
                    Canvas.ForceUpdateCanvases();
                    if (Screen.width == target.x && Screen.height == target.y && CanvasesRenderAt(target)) break;
                    yield return null;
                }
                Assert.That(Screen.width, Is.EqualTo(target.x), "GameView实际宽度未切到目标，不能记录为分辨率通过。");
                Assert.That(Screen.height, Is.EqualTo(target.y), "GameView实际高度未切到目标，不能记录为分辨率通过。");
                Canvas.ForceUpdateCanvases();
                var heldCanvas = _held.GetComponentInParent<Canvas>();
                Assert.That(heldCanvas, Is.Not.Null);
                foreach (var canvas in _canvases)
                {
                    Assert.That(canvas.isRootCanvas, Is.True, "纯Transform包装不能改变原Canvas的Root身份。");
                    Assert.That(canvas.isActiveAndEnabled, Is.True);
                    Assert.That(canvas.renderingDisplaySize.x, Is.EqualTo(target.x).Within(.1f));
                    Assert.That(canvas.renderingDisplaySize.y, Is.EqualTo(target.y).Within(.1f));
                    Assert.That(canvas.scaleFactor, Is.EqualTo(expectedScales[index]).Within(.0001f), canvas.name);
                    Assert.That(canvas.scaleFactor, Is.EqualTo(heldCanvas.scaleFactor).Within(.0001f),
                        "HP/Boss/Stage必须与原十格栏按相同viewport缩放。");
                }

                var current = CaptureEdges(target, heldCanvas.scaleFactor);
                // 1080本来也曾重叠，不能只把有问题的第一帧当golden。独立要求顶部保留120设计像素。
                Assert.That(current.BossTop, Is.EqualTo(120f).Within(.75f));
                Assert.That(current.StageTop, Is.EqualTo(120f).Within(.75f));
                if (index == 0) baseline = current;
                else current.AssertSameDesignInsets(baseline);
                TestContext.WriteLine(
                    $"HUD viewport={Screen.width}x{Screen.height}; scale={heldCanvas.scaleFactor:F6}; " +
                    $"designInsets HP.bottom={current.PlayerBottom:F3}, Boss.top={current.BossTop:F3}, " +
                    $"Stage.top={current.StageTop:F3}, Notice.top/right={current.NoticeTop:F3}/{current.NoticeRight:F3}, " +
                    $"Held.top/left={current.HeldTop:F3}/{current.HeldLeft:F3}");
                // 正式P8的Boss与遭遇文字互斥。几何检查可同时量两组，截图则分别展示，
                // 不把测试强制打开两组造成的文字叠加伪装成正式玩家画面。
                _boss.gameObject.SetActive(true); _stage.gameObject.SetActive(false);
                yield return CaptureViewportPng(evidence, $"ui-{target.x}x{target.y}.png", target, "Boss");
                _boss.gameObject.SetActive(false); _stage.gameObject.SetActive(true);
                yield return CaptureViewportPng(evidence, $"ui-{target.x}x{target.y}-stage.png", target, "Stage");
                _boss.gameObject.SetActive(true); _stage.gameObject.SetActive(true);
            }
#endif
        }

        private void BuildIsolatedOriginalHud()
        {
            var prefab = Resources.Load<GameObject>(CombatPresentationHost.ResourcePath);
            Assert.That(prefab, Is.Not.Null, "请先从原P7/P8重新导出共同CombatHud Resource。");
            _scene = SceneManager.CreateScene("CombatHudResolutionFixture_" + Guid.NewGuid().ToString("N"));
            _fixtureRoot = new GameObject("InactiveResolutionFixture"); _fixtureRoot.SetActive(false);
            SceneManager.MoveGameObjectToScene(_fixtureRoot, _scene);
            var hud = Object.Instantiate(prefab, _fixtureRoot.transform, false);
            var health = hud.GetComponentInChildren<PlayerHealthBar>(true);
            var boss = hud.GetComponentInChildren<BossHealthBarController>(true);
            var stage = hud.GetComponentInChildren<DemoRunHudController>(true);
            var held = hud.GetComponentInChildren<HeldItemHud>(true);
            Assert.That(health != null && boss != null && stage != null && held != null, Is.True,
                "必须量实际原控件，不能用测试新建的替代血条冒充共同资源。");
            _playerHealth = health.transform as RectTransform;
            _boss = boss.transform.Find("BossHUDRoot") as RectTransform;
            _bossName = _boss != null ? _boss.Find("BossNameText") as RectTransform : null;
            _bossPhase = _boss != null ? _boss.Find("PhaseText") as RectTransform : null;
            _stage = stage.GameplayPresentationRoot != null ? stage.GameplayPresentationRoot.transform as RectTransform : null;
            _notification = stage.NotificationPresentationRoot != null ? stage.NotificationPresentationRoot.transform as RectTransform : null;
            _held = held.transform as RectTransform;
            Assert.That(_boss != null && _bossName != null && _bossPhase != null &&
                _stage != null && _notification != null && _held != null, Is.True);
            Assert.That(_held.childCount, Is.EqualTo(10), "原十格必须全部参加屏幕边界和碰撞检查。");
            _heldSlots = new RectTransform[_held.childCount];
            for (int index = 0; index < _heldSlots.Length; index++)
            {
                _heldSlots[index] = _held.GetChild(index) as RectTransform;
                Assert.That(_heldSlots[index], Is.Not.Null);
            }
            foreach (var behaviour in hud.GetComponentsInChildren<MonoBehaviour>(true))
            {
                Assert.That(behaviour, Is.Not.Null);
                if (behaviour.GetType().Namespace?.StartsWith("Game.", StringComparison.Ordinal) == true)
                    behaviour.enabled = false;
            }
            _canvases = hud.GetComponentsInChildren<Canvas>(true);
            Assert.That(_canvases.Length, Is.GreaterThanOrEqualTo(4));
            // Overlay Canvas不会清除颜色缓冲。纯UI隔离Scene若没有Camera，切换viewport/显隐后的
            // ScreenCapture可能保留旧像素，看似生成了第二套HUD，即使Hierarchy中只有一份。
            // 使用不绘制任何世界物体的清屏Camera；它只属于本夹具，随fixtureRoot一起销毁。
            var clearRoot = new GameObject("HudLayoutClearCamera");
            clearRoot.transform.SetParent(_fixtureRoot.transform, false);
            var clearCamera = clearRoot.AddComponent<Camera>();
            clearCamera.clearFlags = CameraClearFlags.SolidColor;
            clearCamera.backgroundColor = Color.black;
            clearCamera.cullingMask = 0;
            clearCamera.depth = -100;
            _fixtureRoot.SetActive(true); hud.SetActive(true);
            foreach (var canvas in Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None))
            {
                // 应用启动会安装独立DDOL过场黑幕，它不是第二套HUD。保留服务，仅确认它完全透明，
                // 不靠删除/禁用全局对象制造隔离成功；其他外部Canvas仍明确报错并附来源。
                if (canvas.GetComponent<PortalTransitionFade>() != null)
                {
                    var fade = canvas.GetComponent<CanvasGroup>();
                    Assert.That(fade, Is.Not.Null); Assert.That(fade.alpha, Is.Zero);
                    Assert.That(fade.blocksRaycasts, Is.False);
                    TestContext.Out.WriteLine("Preserved transparent application Canvas: " + canvas.name);
                    continue;
                }
                if (!canvas.transform.IsChildOf(_fixtureRoot.transform))
                {
                    // 登录/传送门应用服务也会常驻一个Canvas，但关闭的对话框没有任何活跃Graphic。
                    // 允许这个可验证的“不参与当前绘制”状态，仍拒绝真正可见的第二套界面。
                    bool hasActiveGraphic = false;
                    foreach (var graphic in canvas.GetComponentsInChildren<UnityEngine.UI.Graphic>(false))
                        if (graphic.isActiveAndEnabled) { hasActiveGraphic = true; break; }
                    if (!hasActiveGraphic)
                    {
                        TestContext.Out.WriteLine("Preserved application Canvas without active Graphics: " + canvas.name);
                        continue;
                    }
                }
                Assert.That(canvas.transform.IsChildOf(_fixtureRoot.transform), Is.True,
                    "隔离截图存在外部Canvas：" + canvas.transform.root.name + "/" + canvas.name +
                    "; scene=" + canvas.gameObject.scene.name);
            }
            // disabled并不阻止Awake；原Boss/状态Adapter可在Awake整理自己的显示叶树。
            // 此处只显式打开待测叶树及其祖先，不改任何Rect/Scaler，也不伪造Boss事件或库存数据。
            ShowAncestors(_playerHealth, hud.transform); ShowAncestors(_boss, hud.transform);
            ShowAncestors(_stage, hud.transform); ShowAncestors(_notification, hud.transform); ShowAncestors(_held, hud.transform);
        }

        private bool CanvasesRenderAt(Vector2Int size)
        {
            foreach (var canvas in _canvases)
                if (Mathf.Abs(canvas.renderingDisplaySize.x - size.x) > .1f ||
                    Mathf.Abs(canvas.renderingDisplaySize.y - size.y) > .1f) return false;
            return true;
        }

        private EdgeMeasurement CaptureEdges(Vector2Int viewport, float scale)
        {
            var player = ScreenBounds(_playerHealth); var boss = ScreenBounds(_boss);
            var bossName = ScreenBounds(_bossName); var bossPhase = ScreenBounds(_bossPhase);
            var stage = ScreenBounds(_stage); var notice = ScreenBounds(_notification);
            // HorizontalLayoutGroup允许十格超出父Rect；父Rect宽477.8，真实十格合计宽732.9。
            // 必须合并实际子格角点，否则第十格与BossName交叉时，父Rect测试仍会错误通过。
            Rect held = default;
            for (int index = 0; index < _heldSlots.Length; index++)
            {
                var slot = ScreenBounds(_heldSlots[index]); AssertOnScreen(slot, viewport, "原十格" + index);
                held = index == 0 ? slot : Rect.MinMaxRect(Mathf.Min(held.xMin, slot.xMin), Mathf.Min(held.yMin, slot.yMin),
                    Mathf.Max(held.xMax, slot.xMax), Mathf.Max(held.yMax, slot.yMax));
            }
            AssertOnScreen(player, viewport, "原HP"); AssertOnScreen(boss, viewport, "原Boss");
            AssertOnScreen(stage, viewport, "原Stage"); AssertOnScreen(notice, viewport, "原通知");
            AssertOnScreen(held, viewport, "原十格栏");
            AssertOnScreen(bossName, viewport, "BossName"); AssertOnScreen(bossPhase, viewport, "Boss Phase");
            Assert.That(held.Overlaps(bossName), Is.False, "十个实际子格不能与BossName屏幕区域交叉。");
            Assert.That(held.Overlaps(bossPhase), Is.False, "十个实际子格不能与Boss Phase屏幕区域交叉。");
            Assert.That(held.Overlaps(stage), Is.False, "Stage整体应处于原十格栏以下。");
            TestContext.WriteLine($"HUD screenRects {viewport.x}x{viewport.y}: HP={player}; Boss={boss}; " +
                $"BossName={bossName}; BossPhase={bossPhase}; Stage={stage}; Notice={notice}; Held10Union={held}");
            return new EdgeMeasurement
            {
                PlayerBottom = player.yMin / scale, PlayerCenterOffset = (player.center.x - viewport.x * .5f) / scale,
                BossTop = (viewport.y - boss.yMax) / scale, BossCenterOffset = (boss.center.x - viewport.x * .5f) / scale,
                StageTop = (viewport.y - stage.yMax) / scale, StageCenterOffset = (stage.center.x - viewport.x * .5f) / scale,
                NoticeTop = (viewport.y - notice.yMax) / scale, NoticeRight = (viewport.x - notice.xMax) / scale,
                HeldTop = (viewport.y - held.yMax) / scale, HeldLeft = held.xMin / scale
            };
        }

        private static IEnumerator CaptureViewportPng(string directory, string fileName, Vector2Int viewport, string mode)
        {
            Canvas.ForceUpdateCanvases();
            yield return new WaitForEndOfFrame();
            var texture = ScreenCapture.CaptureScreenshotAsTexture();
            Assert.That(texture, Is.Not.Null, "真实GameView ScreenCapture必须返回图像。");
            try
            {
                Assert.That(texture.width, Is.EqualTo(viewport.x)); Assert.That(texture.height, Is.EqualTo(viewport.y));
                string path = Path.Combine(directory, fileName);
                File.WriteAllBytes(path, texture.EncodeToPNG());
                // 再读实际产物的PNG IHDR尺寸，不把请求尺寸或Texture变量当成落盘证据。
                byte[] persisted = File.ReadAllBytes(path);
                Assert.That(persisted.Length, Is.GreaterThan(24));
                Assert.That(persisted[0], Is.EqualTo(137)); Assert.That(persisted[1], Is.EqualTo(80));
                Assert.That(ReadPngDimension(persisted, 16), Is.EqualTo(viewport.x));
                Assert.That(ReadPngDimension(persisted, 20), Is.EqualTo(viewport.y));
                TestContext.WriteLine($"Editor isolated HUD capture: {mode}; {viewport.x}x{viewport.y}; {persisted.Length} bytes; {path}");
            }
            finally { Object.Destroy(texture); }
        }

        private static int ReadPngDimension(byte[] bytes, int offset) =>
            bytes[offset] << 24 | bytes[offset + 1] << 16 | bytes[offset + 2] << 8 | bytes[offset + 3];

        private static Rect ScreenBounds(RectTransform rect)
        {
            Assert.That(rect != null && rect.gameObject.activeInHierarchy, Is.True);
            var canvas = rect.GetComponentInParent<Canvas>(); Assert.That(canvas, Is.Not.Null);
            var camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            var corners = new Vector3[4]; rect.GetWorldCorners(corners);
            var minimum = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            var maximum = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            foreach (var corner in corners)
            {
                var point = RectTransformUtility.WorldToScreenPoint(camera, corner);
                minimum = Vector2.Min(minimum, point); maximum = Vector2.Max(maximum, point);
            }
            return Rect.MinMaxRect(minimum.x, minimum.y, maximum.x, maximum.y);
        }

        private static void AssertOnScreen(Rect rect, Vector2Int size, string label)
        {
            Assert.That(rect.width, Is.GreaterThan(10f), label); Assert.That(rect.height, Is.GreaterThan(5f), label);
            Assert.That(rect.xMin, Is.GreaterThanOrEqualTo(-2f), label);
            Assert.That(rect.yMin, Is.GreaterThanOrEqualTo(-2f), label);
            Assert.That(rect.xMax, Is.LessThanOrEqualTo(size.x + 2f), label);
            Assert.That(rect.yMax, Is.LessThanOrEqualTo(size.y + 2f), label);
        }

        private static void ShowAncestors(Transform node, Transform root)
        { for (; node != null; node = node.parent) { node.gameObject.SetActive(true); if (node == root) break; } }

        private struct EdgeMeasurement
        {
            public float PlayerBottom, PlayerCenterOffset, BossTop, BossCenterOffset, StageTop, StageCenterOffset;
            public float NoticeTop, NoticeRight, HeldTop, HeldLeft;
            public void AssertSameDesignInsets(EdgeMeasurement baseline)
            {
                AssertInset(PlayerBottom, baseline.PlayerBottom, "HP底部");
                AssertInset(PlayerCenterOffset, baseline.PlayerCenterOffset, "HP横向中心");
                AssertInset(BossTop, baseline.BossTop, "Boss顶部");
                AssertInset(BossCenterOffset, baseline.BossCenterOffset, "Boss横向中心");
                AssertInset(StageTop, baseline.StageTop, "Stage顶部");
                AssertInset(StageCenterOffset, baseline.StageCenterOffset, "Stage横向中心");
                AssertInset(NoticeTop, baseline.NoticeTop, "通知顶部"); AssertInset(NoticeRight, baseline.NoticeRight, "通知右侧");
                AssertInset(HeldTop, baseline.HeldTop, "十格顶部"); AssertInset(HeldLeft, baseline.HeldLeft, "十格左侧");
            }
            private static void AssertInset(float actual, float expected, string label)
            { Assert.That(actual, Is.EqualTo(expected).Within(.75f), label + "设计单位边距随viewport漂移。"); }
        }

#if UNITY_EDITOR
        /// <summary>
        /// 反射只在Editor测试边界使用。保留已有Game窗口与原selectedSizeIndex，不创建窗口、
        /// 不调用Save/SaveToHDD，不按名字删除其他测试或用户的custom size；仅凭对象身份清理本例条目。
        /// </summary>
        private sealed class TemporaryFixedGameView : IDisposable
        {
            private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            private readonly EditorWindow _window;
            private readonly EditorWindow _previousFocusedWindow;
            private readonly object _group, _originalSize;
            private readonly PropertyInfo _selected;
            private readonly MethodInfo _builtinCount, _customCount, _get, _add, _remove;
            private readonly Type _sizeType, _kindType;
            private readonly int _originalIndex;
            private readonly List<object> _owned = new List<object>();
            private bool _restored;

            public TemporaryFixedGameView()
            {
                _previousFocusedWindow = EditorWindow.focusedWindow;
                if (BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget) != BuildTargetGroup.Standalone)
                    throw new InvalidOperationException("HUD viewport测试要求当前Standalone目标；不会替用户切换Build Target。");
                var assembly = typeof(Editor).Assembly;
                var viewType = RequireType(assembly, "UnityEditor.GameView");
                var windows = Resources.FindObjectsOfTypeAll(viewType);
                if (windows.Length == 0)
                    throw new InvalidOperationException("请先保留一个现有Game窗口；测试不会新建窗口或改持久Editor布局。");
                _window = windows[0] as EditorWindow;
                if (_window == null) throw new InvalidOperationException("现有Game窗口类型与当前Unity内部API不匹配。");
                _selected = viewType.GetProperty("selectedSizeIndex", Instance) ??
                    throw new InvalidOperationException("当前Unity缺少GameView.selectedSizeIndex。");
                _originalIndex = (int)_selected.GetValue(_window);
                var sizesType = RequireType(assembly, "UnityEditor.GameViewSizes");
                var singleton = typeof(ScriptableSingleton<>).MakeGenericType(sizesType);
                var instance = singleton.GetProperty("instance", BindingFlags.Public | BindingFlags.Static) ??
                    throw new InvalidOperationException("当前Unity缺少GameViewSizes singleton入口。");
                var sizes = instance.GetValue(null);
                var groupType = RequireType(assembly, "UnityEditor.GameViewSizeGroupType");
                var getGroup = RequireMethod(sizesType, "GetGroup", groupType);
                _group = getGroup.Invoke(sizes, new[] { Enum.Parse(groupType, "Standalone") });
                if (_group == null) throw new InvalidOperationException("Standalone GameView尺寸组缺失。");
                var groupClass = _group.GetType(); _sizeType = RequireType(assembly, "UnityEditor.GameViewSize");
                _kindType = RequireType(assembly, "UnityEditor.GameViewSizeType");
                _builtinCount = RequireMethod(groupClass, "GetBuiltinCount");
                _customCount = RequireMethod(groupClass, "GetCustomCount");
                _get = RequireMethod(groupClass, "GetGameViewSize", typeof(int));
                _add = RequireMethod(groupClass, "AddCustomSize", _sizeType);
                _remove = RequireMethod(groupClass, "RemoveCustomSize", typeof(int));
                _originalSize = _get.Invoke(_group, new object[] { _originalIndex });
            }

            public void Select(int width, int height)
            {
                var size = Activator.CreateInstance(_sizeType, Instance, null,
                    new object[] { Enum.Parse(_kindType, "FixedResolution"), width, height, "Norn HUD " + Guid.NewGuid().ToString("N") }, null);
                if (size == null) throw new InvalidOperationException("当前Unity的GameViewSize构造签名不兼容。");
                int index = (int)_builtinCount.Invoke(_group, null) + (int)_customCount.Invoke(_group, null);
                _owned.Add(size); _add.Invoke(_group, new[] { size });
                _selected.SetValue(_window, index);
                // WaitForEndOfFrame在Editor切到Scene/TestRunner页时可能一直等GameView绘制。
                // 显式显示已有Game页，截图完成后恢复原页；不新建窗口或持久保存Editor布局。
                _window.Focus(); _window.Repaint();
            }

            public void Dispose()
            {
                if (_restored) return; _restored = true;
                Exception restoreError = null;
                try
                {
                    if (_window == null) throw new InvalidOperationException("测试期间原Game窗口已销毁，无法恢复其selectedSizeIndex。");
                    _selected.SetValue(_window, _originalIndex);
                }
                catch (Exception error) { restoreError = error; }
                int built = (int)_builtinCount.Invoke(_group, null);
                for (int custom = (int)_customCount.Invoke(_group, null) - 1; custom >= 0; custom--)
                {
                    var candidate = _get.Invoke(_group, new object[] { built + custom });
                    foreach (var owned in _owned)
                        if (ReferenceEquals(candidate, owned))
                        {
                            // Unity此API接收含Builtin的绝对索引；与项目既有GameView测试相同。
                            _remove.Invoke(_group, new object[] { built + custom }); break;
                        }
                }
                int remainingCustom = (int)_customCount.Invoke(_group, null);
                for (int custom = 0; custom < remainingCustom; custom++)
                {
                    var candidate = _get.Invoke(_group, new object[] { built + custom });
                    foreach (var owned in _owned)
                        Assert.That(ReferenceEquals(candidate, owned), Is.False, "本例临时GameView尺寸必须全部移除。");
                }
                _owned.Clear();
                if (_window != null)
                {
                    _window.Repaint();
                    Assert.That((int)_selected.GetValue(_window), Is.EqualTo(_originalIndex));
                    Assert.That(_get.Invoke(_group, new object[] { _originalIndex }), Is.SameAs(_originalSize),
                        "恢复后仍必须是用户原尺寸对象，不能只碰巧落在同一个数字索引。");
                }
                if (_previousFocusedWindow != null) _previousFocusedWindow.Focus();
                if (restoreError != null) throw new InvalidOperationException("GameView恢复失败。", restoreError);
            }

            private static Type RequireType(Assembly assembly, string name) => assembly.GetType(name) ??
                throw new InvalidOperationException("当前Unity内部API缺少类型：" + name);
            private static MethodInfo RequireMethod(Type type, string name, params Type[] arguments) =>
                type.GetMethod(name, Instance, null, arguments, null) ??
                throw new InvalidOperationException("当前Unity内部API签名不兼容：" + type.FullName + "." + name);
        }
#endif
    }
}
