using Game.Run;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace Game.UI
{
    /// <summary>商品/资产行只提交选中意图；复用行对象，不在每帧销毁重建列表。</summary>
    public sealed class ShopEntryView : MonoBehaviour, IPointerDownHandler
    {
        [SerializeField] private Button _button;
        [SerializeField] private Text _label;
        private ShopController _controller;
        private string _id;
        private bool _owned;
        private Outline _selection;
        private Image _icon;
        private Vector2 _clickPosition;
        private bool _hasPointerPosition;
        public string OfferId => _id;
        private void OnEnable(){if(_button!=null)_button.onClick.AddListener(Select);}
        private void OnDisable(){if(_button!=null)_button.onClick.RemoveListener(Select);}
        public void OnPointerDown(PointerEventData data)
        { if (data.button == PointerEventData.InputButton.Left) { _clickPosition = data.position; _hasPointerPosition = true; } }
        private void Select()
        {
            if (_controller == null) return;
            if (_owned) _controller.SelectOwned(_id);
            else
            {
                // PointerDown 只记位置；Button.onClick 是唯一选中入口，避免一个 click 被处理两次。
                var rect = (RectTransform)transform; var canvas = GetComponentInParent<Canvas>();
                var camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
                var point = _hasPointerPosition ? _clickPosition : RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(rect.rect.center));
                _controller.ClickOffer(_id, point);
            }
            _hasPointerPosition = false;
        }
        public void SetSelected(bool selected)
        { if (_selection != null) _selection.enabled = selected; }
        public void Bind(ShopController controller,string id,string label,bool owned,Font font,Sprite icon=null)
        {
            _controller=controller;_id=id;_owned=owned;_label.text=label;_label.font=font;
            if (_selection == null)
            {
                _selection = GetComponent<Outline>(); if (_selection == null) _selection = gameObject.AddComponent<Outline>();
                _selection.effectColor = new Color(1, .62f, .10f, 1); _selection.effectDistance = new Vector2(3, -3); _selection.useGraphicAlpha = false;
            }
            _selection.enabled = false;
            // 同一Prefab用于宽列表和窄商品卡；文字尺寸跟随父Rect，不能保留旧列表固定宽度。
            var rect=_label.rectTransform;rect.anchorMin=Vector2.zero;rect.anchorMax=Vector2.one;
            rect.offsetMin=new Vector2(10,8);rect.offsetMax=new Vector2(-10,-8);
            _label.horizontalOverflow=HorizontalWrapMode.Wrap;_label.verticalOverflow=VerticalWrapMode.Truncate;
            _label.fontSize=!owned && transform.parent.GetComponent<GridLayoutGroup>()!=null?18:20;
            // 旧卡片Prefab只有Text；离散绑定补齐图标，运行稳定帧继续复用，物品定义统一供应模型烘焙图。
            if (_icon == null && icon != null)
            { var art = new GameObject("OfferIcon", typeof(RectTransform), typeof(Image)); art.transform.SetParent(transform, false); _icon = art.GetComponent<Image>(); _icon.raycastTarget = false; _icon.preserveAspect = true; }
            if (_icon != null)
            {
                _icon.enabled = icon != null; _icon.sprite = icon;
                var artRect = _icon.rectTransform; artRect.anchorMin = new Vector2(.02f, .08f); artRect.anchorMax = new Vector2(.42f, .92f); artRect.offsetMin = artRect.offsetMax = Vector2.zero;
            }
            if (icon != null)
            { rect.anchorMin = new Vector2(.42f, 0); _label.resizeTextForBestFit = true; _label.resizeTextMinSize = 10; _label.resizeTextMaxSize = 14; }
            else _label.resizeTextForBestFit = false;
            if (icon != null && icon.rect.width > icon.rect.height * 2)
            {
                // 裁边后的长条法杖占卡片上方宽区，价格/库存留在下方，避免缩进旧左侧小方框。
                _icon.rectTransform.anchorMin=new Vector2(.04f,.55f);_icon.rectTransform.anchorMax=new Vector2(.96f,.94f);
                rect.anchorMin=Vector2.zero;rect.anchorMax=new Vector2(1,.53f);
                rect.offsetMin=new Vector2(8,2);rect.offsetMax=new Vector2(-8,-2);
            }
        }
    }
}
