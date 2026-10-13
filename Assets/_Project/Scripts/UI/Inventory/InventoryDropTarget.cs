using UnityEngine;
using UnityEngine.EventSystems;

namespace Game.UI
{
    public enum InventoryDropKind : byte { Warehouse, Ground, EquippedWand, EquippedBackpack, ProgramSlot, SpellStorage }
    /// <summary>静态 UI 目标只提交位置意图，不拥有资产或修改物品配置。</summary>
    public sealed class InventoryDropTarget : MonoBehaviour, IDropHandler
    {
        private SinglePlayerInventoryController _controller;
        private InventoryDropKind _kind;
        private string _ownerId;
        private int _slot;
        public void Initialize(SinglePlayerInventoryController controller, InventoryDropKind kind, string ownerId = null, int slot = 0)
        { _controller = controller; _kind = kind; _ownerId = ownerId; _slot = slot; }
        public void OnDrop(PointerEventData data)
        {if(_kind==InventoryDropKind.Ground)_controller?.SubmitToNearby(data.position,data.pressEventCamera);else _controller?.SubmitToTarget(_kind,_ownerId,_slot);}
    }
}
