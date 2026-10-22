using Game.Run;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Game.UI
{
    /// <summary>装备图标只发拖拽意图，不复制法杖或它的程序/资源。整行接收目标由 Panel 管理。</summary>
    public sealed class EquippedWandDragView : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler, IPointerClickHandler, IPointerDownHandler
    {
        private SinglePlayerInventoryController _controller;
        private InventoryItemSnapshot _item;
        private bool _dragged;
        public void Bind(SinglePlayerInventoryController controller,in InventoryItemSnapshot item,bool occupied)
        { _controller=occupied?controller:null;_item=item; }
        public void OnPointerDown(PointerEventData data){if(data.button==PointerEventData.InputButton.Left)_dragged=false;}
        public void OnBeginDrag(PointerEventData data)
        {
            if(_controller==null || data.button!=PointerEventData.InputButton.Left)return;
            _dragged=true;_controller.BeginDrag(_item,data.position,data.pressEventCamera,true);
        }
        public void OnDrag(PointerEventData data)=>_controller?.MoveGhost(data.position,data.pressEventCamera);
        public void OnEndDrag(PointerEventData data)=>_controller?.EndItemDrag(data.position,data.pressEventCamera);
        public void OnPointerClick(PointerEventData data)
        {if(data.button==PointerEventData.InputButton.Right)_controller?.ShowItemInfo(_item.InstanceId,data.position,data.pressEventCamera);
            else if(!_dragged && data.button==PointerEventData.InputButton.Left)_controller?.ClickItem(_item.InstanceId);_dragged=false;}
    }
}
