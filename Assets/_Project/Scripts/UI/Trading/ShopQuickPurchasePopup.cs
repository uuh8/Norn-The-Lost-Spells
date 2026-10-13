using Unity.Profiling;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>只转发购买意图；复用同一弹层，位置在 Canvas 本地坐标中计算，不假定屏幕像素等于 UI 像素。</summary>
    public sealed class ShopQuickPurchasePopup : MonoBehaviour
    {
        [SerializeField] private ShopController _controller;
        [SerializeField] private RectTransform _menu;
        [SerializeField] private Button _buy;
        private RectTransform _root;
        private Canvas _canvas;
        private int _openedFrame;
        private static readonly ProfilerMarker UpdateMarker = new ProfilerMarker("Norn.Shop.QuickPopupUpdate");
        public bool IsOpen => gameObject.activeSelf;
        private Camera UiCamera => _canvas != null && _canvas.renderMode != RenderMode.ScreenSpaceOverlay ? _canvas.worldCamera : null;
        private void Awake()
        {
            _root = (RectTransform)transform; _canvas = GetComponentInParent<Canvas>();
            // 根节点不吃其他商品的 click：外部点击在 Update 收起，商品仍可正常选中。
            if (TryGetComponent<Image>(out var backdrop)) backdrop.raycastTarget = false;
        }
        private void OnEnable() { _buy?.onClick.AddListener(Purchase); }
        private void OnDisable() { _buy?.onClick.RemoveListener(Purchase); }
        public void Show(Vector2 screenPosition)
        {
            if (_menu == null || _controller == null) return;
            gameObject.SetActive(true); transform.SetAsLastSibling(); _openedFrame = Time.frameCount;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_root, screenPosition, UiCamera, out var point)) { Hide(); return; }
            var bounds = _root.rect;
            var size = new Vector2(Mathf.Min(220, Mathf.Max(1, bounds.width - 12)), Mathf.Min(76, Mathf.Max(1, bounds.height - 12)));
            _menu.sizeDelta = size;
            // Menu pivot=(0,1)：位置代表左上角。先偏离鼠标，再把整个矩形钳在面板内。
            point += new Vector2(12, -10);
            point.x = Mathf.Clamp(point.x, bounds.xMin + 6, bounds.xMax - size.x - 6);
            point.y = Mathf.Clamp(point.y, bounds.yMin + size.y + 6, bounds.yMax - 6);
            _menu.anchoredPosition = point - bounds.center;
        }
        public void Hide() => gameObject.SetActive(false);
        private void Purchase() { Hide(); _controller.Buy(); }
        private void Update()
        {
            using var sample = UpdateMarker.Auto(); var mouse = Mouse.current;
            // 打开弹层的 click 本身不能立刻取消它；滚轮被 ScrollRect 消费前即可取消旧弹层。
            if (mouse == null || Time.frameCount == _openedFrame) return;
            if (mouse.scroll.ReadValue() != Vector2.zero || mouse.leftButton.wasPressedThisFrame &&
                !RectTransformUtility.RectangleContainsScreenPoint(_menu, mouse.position.ReadValue(), UiCamera)) Hide();
        }
    }
}
