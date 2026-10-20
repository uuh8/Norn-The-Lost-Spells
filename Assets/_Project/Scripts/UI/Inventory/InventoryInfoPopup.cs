using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>仅展示右键详情占位；不持有资产写权限，打开不会选择/移动/消耗物品。</summary>
    public sealed class InventoryInfoPopup:MonoBehaviour,IPointerClickHandler
    {
        [SerializeField] private RectTransform _frame;
        [SerializeField] private Text _title,_body;
        [SerializeField] private Button _close;
        public bool IsOpen=>gameObject.activeSelf;
        private void OnEnable(){_close?.onClick.AddListener(Hide);}
        private void OnDisable(){_close?.onClick.RemoveListener(Hide);}
        public void Show(string name,Vector2 pointer,Camera camera,Font font)
        {
            _title.font=font;_body.font=font;_title.text=name;_body.text="详细信息待开发";
            gameObject.SetActive(true);transform.SetAsLastSibling();
            var parent=(RectTransform)transform.parent;
            if(!RectTransformUtility.ScreenPointToLocalPointInRectangle(parent,pointer,camera,out var position))position=parent.rect.center;
            // UGUI指针是Screen Space，夹紧在父面板Local Space内，右下角也能完整看见窗口。
            position+=new Vector2(18,-10);Rect bounds=parent.rect;
            position.x=Mathf.Clamp(position.x,bounds.xMin,bounds.xMax-_frame.rect.width);
            position.y=Mathf.Clamp(position.y,bounds.yMin+_frame.rect.height,bounds.yMax);
            _frame.anchoredPosition=position-bounds.center;
        }
        public void Hide()=>gameObject.SetActive(false);
        public void OnPointerClick(PointerEventData data)
        {if(!RectTransformUtility.RectangleContainsScreenPoint(_frame,data.position,data.pressEventCamera))Hide();}
    }
}
