using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Game.Core;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>显式验收的只读 UI 输入诊断；记录 Unity 实际指针/Raycast，不 Invoke 按钮或改变输入状态。</summary>
    public sealed class OnlineInputAcceptanceProbe : MonoBehaviour
    {
        private const int MaxNodes = 256;
        private const int MaxStageTexts = 24;
        private const int MaxLabelLength = 160;
        [Serializable] private sealed class Config { public string Output, Command; }
        [Serializable] private struct ScreenBox
        {
            public float x, y, width, height;
            public ScreenBox(Rect rect) { x = rect.x; y = rect.y; width = rect.width; height = rect.height; }
        }
        [Serializable] private sealed class Node
        {
            public string Kind, Name, Path, Label;
            public bool Interactable;
            public ScreenBox ScreenRect;
            public Vector2 Center;
            public string ItemId, LocationKind, OwnerId, DropKind;
            public int SlotIndex = -1;
        }
        [Serializable] private sealed class Report
        {
            public string RecordedAtUtc, CursorLock, EventSystemName, ModuleName, Selected;
            public bool Focused, EventSystemFocused, RunInBackground, CursorVisible, ModuleEnabled, PointEnabled, ClickEnabled;
            public string BackgroundBehavior, DiagnosticError;
            public int Width, Height;
            public Vector2 MousePosition, UiPoint;
            public string[] RaycastTargets;
            public string[] PointerStates;
            // Unity Screen 像素空间从左下角起算；这是当前实际视口，不是 CanvasScaler 的 referenceResolution。
            public string CoordinateSpace;
            public bool InventoryIsOpen, InventoryIsDragging, NodesTruncated;
            public Node[] Nodes;
        }
        private string _output, _command;
        private bool _sampling;
        private double _next;
        private readonly List<RaycastResult> _hits = new List<RaycastResult>(64);
        private readonly List<Node> _nodes = new List<Node>(MaxNodes);
        private readonly Vector3[] _corners = new Vector3[4];

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
#if (UNITY_EDITOR || DEVELOPMENT_BUILD) && !UNITY_SERVER
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] != "--norn-portal-acceptance" || !File.Exists(args[i + 1])) continue;
                var config = JsonUtility.FromJson<Config>(File.ReadAllText(args[i + 1]));
                if (config == null || string.IsNullOrEmpty(config.Output) || string.IsNullOrEmpty(config.Command)) return;
                var root = new GameObject("ExplicitUiInputAcceptance"); DontDestroyOnLoad(root);
                var probe = root.AddComponent<OnlineInputAcceptanceProbe>();
                probe._output = config.Output + ".ui.json"; probe._command = config.Command + ".ui"; return;
            }
#endif
        }
        private void OnEnable() => EventBus<OnlineDiagnosticSamplingEvent>.Subscribe(OnSampling);
        private void OnDisable() => EventBus<OnlineDiagnosticSamplingEvent>.Unsubscribe(OnSampling);
        private void OnSampling(OnlineDiagnosticSamplingEvent state) => _sampling = state.Active;
        private void Update()
        {
            // 与其他显式验收 IO 相同：Profiler 窗口完全停查文件；只有人工请求才分配报告/Raycast 数据。
            if (_sampling || _command == null || Time.realtimeSinceStartupAsDouble < _next) return;
            _next = Time.realtimeSinceStartupAsDouble + 1;
            if (!File.Exists(_command)) return;
            File.Delete(_command);
            var system = EventSystem.current;
            var module = system != null ? system.currentInputModule as InputSystemUIInputModule : null;
            Vector2 mouse = Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
            Vector2 point = module != null && module.point != null && module.point.action != null
                ? module.point.action.ReadValue<Vector2>() : Vector2.zero;
            _hits.Clear();
            if (system != null) system.RaycastAll(new PointerEventData(system) { position = point }, _hits);
            var names = new string[_hits.Count];
            for (int i = 0; i < names.Length; i++)
            {
                var node = _hits[i].gameObject.transform; string path = node.name;
                while (node.parent != null) { node = node.parent; path = node.name + "/" + path; }
                var button = _hits[i].gameObject.GetComponentInParent<UnityEngine.UI.Button>();
                if (button != null) path += " | active=" + button.IsActive() + " interactable=" + button.IsInteractable();
                names[i] = path;
            }
            var report = new Report
            {
                RecordedAtUtc = DateTime.UtcNow.ToString("O"), Focused = Application.isFocused,
                EventSystemFocused = system != null && system.isFocused, RunInBackground = Application.runInBackground,
                BackgroundBehavior = InputSystem.settings.backgroundBehavior.ToString(),
                CursorLock = Cursor.lockState.ToString(), CursorVisible = Cursor.visible,
                Width = Screen.width, Height = Screen.height, MousePosition = mouse, UiPoint = point,
                EventSystemName = system != null ? system.name : "", ModuleName = module != null ? module.GetType().Name : "",
                ModuleEnabled = module != null && module.isActiveAndEnabled,
                PointEnabled = module != null && module.point != null && module.point.action != null && module.point.action.enabled,
                ClickEnabled = module != null && module.leftClick != null && module.leftClick.action != null && module.leftClick.action.enabled,
                Selected = system != null && system.currentSelectedGameObject != null ? system.currentSelectedGameObject.name : "",
                RaycastTargets = names
            };
            // 固定 Input System 版本的内部状态只读诊断；不把反射接入正式 UI 权限或状态修复路径。
            try { report.PointerStates = ReadPointerStates(module); }
            catch (Exception exception) { report.DiagnosticError = exception.GetType().Name; }
            CaptureGeometry(report);
            File.WriteAllText(_output, JsonUtility.ToJson(report, true));
        }

        /// <summary>
        /// 只在上面的显式报告请求内枚举原 UGUI，不修改布局、不调用 Button/拖拽回调、不触及资产命令。
        /// 多显示器 DPI、全屏恢复与不同 CanvasScaler 配置使参考分辨率推算失效；验收端应先读取这里的
        /// 实际矩形，再映射到 OS 客户区，最后用新鲜 MousePosition/RaycastTargets 确认命中才输入。
        /// FindObjectsByType 包含 DDOL 中的共同 HUD；只保留 active 且与当前视口相交的几何候选。
        /// 枚举、反射、字符串与 ToArray 都是离散诊断分配；采样门关闭此路径，不代表正式稳态开销。
        /// </summary>
        private void CaptureGeometry(Report report)
        {
            _nodes.Clear();
            report.CoordinateSpace = "Unity Screen pixels; bottom-left origin; viewport Width/Height";
            foreach (var controller in FindObjectsByType<SinglePlayerInventoryController>(FindObjectsSortMode.None))
            {
                if (!controller.isActiveAndEnabled) continue;
                report.InventoryIsOpen |= controller.IsOpen;
                report.InventoryIsDragging |= controller.IsDragging;
            }
            foreach (var input in FindObjectsByType<InputField>(FindObjectsSortMode.None))
            {
                var node = AddNode(input, "InputField", input.IsInteractable(), report);
                // 不访问 input.text，也不采集 InputField 的子 Text：普通用户名与密码都不进入诊断文件。
                if (node != null) node.Label = string.Empty;
            }
            foreach (var button in FindObjectsByType<Button>(FindObjectsSortMode.None))
            {
                var node = AddNode(button, "Button", button.IsInteractable(), report);
                if (node == null) continue;
                var label = button.GetComponentInChildren<Text>();
                if (label != null && label.GetComponentInParent<InputField>() == null) node.Label = Clip(label.text, MaxLabelLength);
            }
            foreach (var item in FindObjectsByType<InventoryItemView>(FindObjectsSortMode.None))
            {
                var node = AddNode(item, "InventoryItem", true, report);
                if (node == null) continue;
                var snapshot = item.Snapshot;
                node.ItemId = snapshot.InstanceId;
                node.LocationKind = snapshot.Location.Kind.ToString();
                node.OwnerId = snapshot.Location.OwnerId;
                node.SlotIndex = snapshot.Location.SlotIndex;
            }
            // 工具格使用自己的IDropHandler而非InventoryDropTarget；只在显式UI报告中给出真实可见矩形，
            // OS鼠标仍需走EventSystem完成拖放，探针不能调用OnDrop或改绑定，也不扩大正式UI写接口。
            foreach (var tool in FindObjectsByType<ToolbeltSlotView>(FindObjectsSortMode.None))
            {
                var node = AddNode(tool, "ToolbeltSlot", true, report);
                if (node == null) continue;
                node.SlotIndex = tool.ToolSlot;
                node.DropKind = tool.Compact ? "Compact" : "Inventory";
            }
            // 反射限于这个显式只读探针，避免为自动验收给正式库存 UI 扩大公共写接口。
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            var kind = typeof(InventoryDropTarget).GetField("_kind", fields);
            var owner = typeof(InventoryDropTarget).GetField("_ownerId", fields);
            var slot = typeof(InventoryDropTarget).GetField("_slot", fields);
            foreach (var target in FindObjectsByType<InventoryDropTarget>(FindObjectsSortMode.None))
            {
                var node = AddNode(target, "DropTarget", true, report);
                if (node == null) continue;
                if (kind == null || owner == null || slot == null)
                {
                    node.Interactable = false;
                    report.DiagnosticError = "DropTargetSemanticsUnavailable";
                    continue;
                }
                node.DropKind = ((InventoryDropKind)kind.GetValue(target)).ToString();
                node.OwnerId = (string)owner.GetValue(target);
                node.SlotIndex = (int)slot.GetValue(target);
            }
            int stageTexts = 0;
            foreach (var label in FindObjectsByType<Text>(FindObjectsSortMode.None))
            {
                // 只读取关卡/通知子树，定位默认文字或重复 Stage；不展开主菜单输入内容和任意业务状态。
                if (stageTexts >= MaxStageTexts || label.GetComponentInParent<InputField>() != null ||
                    label.GetComponentInParent<DemoRunHudController>() == null) continue;
                var node = AddNode(label, "StageText", label.raycastTarget, report);
                if (node == null) continue;
                node.Label = Clip(label.text, MaxLabelLength); stageTexts++;
            }
            report.Nodes = _nodes.ToArray();
        }

        private Node AddNode(Behaviour component, string kind, bool interactable, Report report)
        {
            if (component == null || !component.isActiveAndEnabled || !TryVisibleRect(component.transform as RectTransform, out var screen)) return null;
            if (_nodes.Count == MaxNodes) { report.NodesTruncated = true; return null; }
            var node = new Node { Kind = kind, Name = component.name, Path = HierarchyPath(component.transform),
                Interactable = interactable, ScreenRect = new ScreenBox(screen), Center = screen.center };
            _nodes.Add(node); return node;
        }

        /// <summary>
        /// GetWorldCorners 返回经过全部父级 Transform/布局后的世界坐标。Overlay 的投影相机必须传 null；
        /// Camera/World Canvas 使用其事件相机投影，不能把 localPosition 当屏幕坐标。
        /// 父级 RectMask2D/Mask 和视口只收窄候选矩形，不改变 ScrollRect；非矩形 Mask 或遮挡仍需 Raycast 复核。
        /// </summary>
        private bool TryVisibleRect(RectTransform rect, out Rect screen)
        {
            screen = default;
            if (rect == null) return false;
            var canvas = rect.GetComponentInParent<Canvas>();
            if (canvas == null || !canvas.isActiveAndEnabled || canvas.rootCanvas == null || !canvas.rootCanvas.isActiveAndEnabled) return false;
            var root = canvas.rootCanvas;
            var raycaster = canvas.GetComponent<GraphicRaycaster>();
            Camera camera = root.renderMode == RenderMode.ScreenSpaceOverlay ? null
                : raycaster != null ? raycaster.eventCamera : root.worldCamera;
            if (root.renderMode == RenderMode.WorldSpace && camera == null) camera = Camera.main;
            if (!ProjectRect(rect, camera, out screen)) return false;
            if (!Intersect(ref screen, new Rect(0, 0, Screen.width, Screen.height))) return false;
            bool checkParentGroups = true;
            for (Transform node = rect; node != null; node = node.parent)
            {
                if (checkParentGroups)
                {
                    foreach (var group in node.GetComponents<CanvasGroup>())
                    {
                        if (!group.isActiveAndEnabled) continue;
                        if (group.alpha <= .001f) return false;
                        if (group.ignoreParentGroups) checkParentGroups = false;
                    }
                }
                var clip = node.GetComponent<RectMask2D>();
                var mask = node.GetComponent<Mask>();
                if (clip != null && clip.isActiveAndEnabled || mask != null && mask.isActiveAndEnabled)
                {
                    if (!ProjectRect(node as RectTransform, camera, out var clipping) || !Intersect(ref screen, clipping)) return false;
                }
            }
            return screen.width > .5f && screen.height > .5f;
        }

        private bool ProjectRect(RectTransform rect, Camera camera, out Rect result)
        {
            result = default;
            if (rect == null) return false;
            rect.GetWorldCorners(_corners);
            var first = RectTransformUtility.WorldToScreenPoint(camera, _corners[0]);
            float minX = first.x, maxX = first.x, minY = first.y, maxY = first.y;
            for (int i = 1; i < 4; i++)
            {
                var point = RectTransformUtility.WorldToScreenPoint(camera, _corners[i]);
                minX = Mathf.Min(minX, point.x); maxX = Mathf.Max(maxX, point.x);
                minY = Mathf.Min(minY, point.y); maxY = Mathf.Max(maxY, point.y);
            }
            if (!float.IsFinite(minX) || !float.IsFinite(maxX) || !float.IsFinite(minY) || !float.IsFinite(maxY)) return false;
            result = Rect.MinMaxRect(minX, minY, maxX, maxY); return true;
        }

        private static bool Intersect(ref Rect rect, Rect bounds)
        {
            float x = Mathf.Max(rect.xMin, bounds.xMin), y = Mathf.Max(rect.yMin, bounds.yMin);
            float right = Mathf.Min(rect.xMax, bounds.xMax), top = Mathf.Min(rect.yMax, bounds.yMax);
            if (right <= x || top <= y) return false;
            rect = Rect.MinMaxRect(x, y, right, top); return true;
        }

        private static string HierarchyPath(Transform node)
        {
            string path = node.name;
            while (node.parent != null) { node = node.parent; path = node.name + "/" + path; }
            return Clip(path, 640);
        }
        private static string Clip(string value, int maximum) => value == null ? string.Empty
            : value.Length <= maximum ? value : value.Substring(0, maximum);

        private static string[] ReadPointerStates(InputSystemUIInputModule module)
        {
            if (module == null) return Array.Empty<string>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            object list = typeof(InputSystemUIInputModule).GetField("m_PointerStates", flags).GetValue(module);
            var type = list.GetType(); int count = (int)type.GetField("length", flags).GetValue(list);
            var extra = type.GetField("additionalValues", flags).GetValue(list) as Array;
            var rows = new string[count];
            for (int i = 0; i < count; i++)
            {
                object pointer = i == 0 ? type.GetField("firstValue", flags).GetValue(list) : extra.GetValue(i - 1);
                object button = pointer.GetType().GetField("leftButton", flags).GetValue(pointer);
                var buttonType = button.GetType();
                string Read(string field)
                {
                    object value = buttonType.GetField(field, flags).GetValue(button);
                    if (value is UnityEngine.Object item) return item != null ? item.name : "<destroyed>";
                    return value != null ? value.ToString() : "<null>";
                }
                rows[i] = "ignore=" + Read("m_IgnoreNextClick") + " pressed=" + Read("m_IsPressed")
                    + " frame=" + Read("m_FramePressState") + " press=" + Read("m_PressObject")
                    + " raw=" + Read("m_RawPressObject") + " dragging=" + Read("m_Dragging");
            }
            return rows;
        }
    }
}
