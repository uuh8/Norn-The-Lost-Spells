using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>View只发送装备/拖出意图；真实位置和失败回滚由Run事务裁决。</summary>
    public sealed class ToolbeltSlotView : MonoBehaviour, IDropHandler, IBeginDragHandler, IDragHandler, IEndDragHandler, IPointerClickHandler
    {
        [SerializeField] private int _toolSlot;
        [SerializeField] private Image _icon;
        [SerializeField] private Text _label;
        [SerializeField] private bool _compact;
        private SinglePlayerInventoryController _controller;
        private string _itemId;
        private Image _containerFill;
        private void Awake(){_containerFill=ContainerIconFill.Ensure(_icon);}
        public bool Compact=>_compact;
        public int ToolSlot=>_toolSlot;
        public void SetSlotIndex(int slot){_toolSlot=slot;}
        public void Configure(int slot, Image icon, Text label) { _toolSlot=slot; _icon=icon; _label=label; }
        public void ConfigureCompact() { _compact = true; }
        public void Refresh(SinglePlayerInventoryController controller)
        {
            _controller=controller; _itemId=null; var read=controller.Session.ToolbeltReadOnly;
            if (read!=null && read.TryGetBinding(_toolSlot,out var id) && controller.Session.ReadOnly.TryGetView(id,out var item))
            {
                _itemId=id; _icon.sprite=item.Icon; _icon.enabled=item.Icon!=null;
                // 名称/容量数字由图标与填充承担；快捷键数字仍用于辨识操作位置。
                _label.text=Game.Core.HeldItemSlots.KeyLabel(_toolSlot+3);
                if(_containerFill==null)_containerFill=ContainerIconFill.Ensure(_icon);
                ContainerIconFill.Set(_containerFill,_icon,item.Kind==Game.Run.ItemKind.Container,item.ContainerTotal,item.ContainerCapacity);
            }
            else { _icon.enabled=false; _label.text=Game.Core.HeldItemSlots.KeyLabel(_toolSlot+3);ContainerIconFill.Set(_containerFill,_icon,false,0,0); }
            if(_label.font==null)_label.font=controller.UiFont;
        }
        public void OnDrop(PointerEventData e)=>_controller?.SubmitToShortcut(_toolSlot);
        public void OnBeginDrag(PointerEventData e)
        { if (e.button==PointerEventData.InputButton.Left && _itemId!=null) _controller.BeginShortcutDrag(_itemId,e.position,e.pressEventCamera); }
        public void OnDrag(PointerEventData e)=>_controller?.MoveGhost(e.position,e.pressEventCamera);
        public void OnEndDrag(PointerEventData e)=>_controller?.CancelDrag();
        public void OnPointerClick(PointerEventData e)
        { if (e.button==PointerEventData.InputButton.Right && _itemId!=null) _controller?.ShowItemInfo(_itemId,e.position,e.pressEventCamera); else if (_itemId!=null) _controller?.SelectItem(_itemId); }
    }
}
