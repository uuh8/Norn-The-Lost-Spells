using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>Prefab的语义引用。Refresh只改数据/显隐，不覆盖子Rect、背景或字体样式。</summary>
    public sealed class InventorySlotView : MonoBehaviour
    {
        [SerializeField] private InventoryDropTarget _target;
        [SerializeField] private InventoryItemView _card;
        [SerializeField] private Text _index,_empty;
        [SerializeField] private Image _selection;
        [SerializeField] private Sprite _selectedSprite;
        private Sprite _normalSprite;
        public InventoryDropTarget Target=>_target;
        public InventoryItemView Card=>_card;
        public Text EmptyLabel=>_empty;
        private void Awake(){if(_selection!=null){_normalSprite=_selection.sprite;_selection.enabled=_normalSprite!=null;}}
        public void BindIndex(int slot,Font fallbackFont)
        {if(_index!=null){if(_index.font==null)_index.font=fallbackFont;_index.text=(slot+1).ToString();}}
        public void ShowEmpty(bool empty,int slot,Font fallbackFont)
        {if(_empty!=null){_empty.gameObject.SetActive(empty);if(_empty.font==null)_empty.font=fallbackFont;_empty.text=(slot+1)+" · 空法杖位\n拖入法杖";}}
        public void SetSelected(bool selected)
        {if(_selection!=null){_selection.sprite=selected && _selectedSprite!=null?_selectedSprite:_normalSprite;_selection.enabled=_selection.sprite!=null;}}
    }
}
