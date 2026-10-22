using UnityEngine;
using UnityEngine.EventSystems;
using Game.Skills;

namespace Game.UI
{
    /// <summary>格子只提交实例/槽位意图；交换、归属和次数校验继续由Run执行。</summary>
    public sealed class WandResearchCellInteraction : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler, IDropHandler, IPointerClickHandler
    {
        private WandSpellCellView _cell;
        private WandProgramWorkbenchPresenter _presenter;
        private RectTransform _ghostParent;
        private void Awake()
        { _cell=GetComponent<WandSpellCellView>(); _presenter=GetComponentInParent<WandProgramWorkbenchPresenter>(); }
        public void OnBeginDrag(PointerEventData e)
        {
            if (_presenter == null || _presenter.IsHistory || _cell.SourceSlot<0 || _cell.InstanceId == null || e.button != PointerEventData.InputButton.Left) return;
            _presenter.BeginOwnedDrag(_cell.InstanceId);
            var ghost=_presenter.DragGhost;
            if (ghost != null)
            {
                var program=_cell.Role==WandSpellCellRole.Library?_presenter.LibraryProgram:_presenter.Program;
                if (program!=null && _cell.CardIndex>=0 && _cell.CardIndex<program.Count)
                    ghost.GetComponent<WandSpellCellView>().Bind(program.Instructions[_cell.CardIndex],_cell.CardIndex,WandSpellCellRole.Library);
                ghost.gameObject.SetActive(true);ghost.SetAsLastSibling();_ghostParent=(RectTransform)ghost.parent;OnDrag(e);
            }
        }
        public void OnDrag(PointerEventData e)
        {
            if (_presenter == null || _presenter.DraggedInstanceId == null || _presenter.DragGhost == null || _ghostParent == null) return;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(_ghostParent,e.position,null,out var local))
                _presenter.DragGhost.anchoredPosition=local;
        }
        public void OnEndDrag(PointerEventData e) { if (_presenter!=null) _presenter.EndOwnedDrag(); }
        public void OnDrop(PointerEventData e)
        { if (_presenter!=null && _cell.Role==WandSpellCellRole.Sequence && _cell.SourceSlot>=0) _presenter.DropOwned(_cell.SourceSlot); }
        public void OnPointerClick(PointerEventData e)
        { if (e.button==PointerEventData.InputButton.Right && _cell.Role==WandSpellCellRole.Sequence && _cell.SourceSlot>=0 && _cell.InstanceId!=null) _presenter.RemoveOwned(_cell.InstanceId); }
    }
}
