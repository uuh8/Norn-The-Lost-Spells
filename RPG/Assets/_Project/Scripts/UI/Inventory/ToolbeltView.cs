using UnityEngine;
namespace Game.UI
{
    public sealed class ToolbeltView : MonoBehaviour
    {
        [SerializeField] private ToolbeltSlotView[] _slots=new ToolbeltSlotView[Game.Core.HeldItemSlots.ToolCount];
        [SerializeField,Tooltip("道具槽装饰框之间的间距，与连续填充的背包网格区分。")]
        private float _spacing=4;
        private bool _templatesApplied;
        private RectTransform _row;
        private Vector2 _lastSize;
        public void Configure(ToolbeltSlotView[] slots) { _slots=slots; }
        public void Refresh(SinglePlayerInventoryController controller)
        {
            if(!_templatesApplied && controller.SlotPrefabs!=null)
            {
                _templatesApplied=true;
                for(int i=0;i<_slots.Length;i++)
                {
                    var old=_slots[i];if(old==null)continue;
                    var template=old.Compact?controller.SlotPrefabs.WorkspaceToolSlot:controller.SlotPrefabs.BackpackToolSlot;
                    if(template==null)continue;
                    var view=Instantiate(template,old.transform.parent);view.name=old.name;view.SetSlotIndex(old.ToolSlot);
                    // 复制作者所在区域与顺序；稍后仅排布动态槽根，Prefab的Icon/Caption局部样式不动。
                    var source=(RectTransform)old.transform;var rect=(RectTransform)view.transform;
                    rect.anchorMin=source.anchorMin;rect.anchorMax=source.anchorMax;rect.pivot=source.pivot;
                    rect.sizeDelta=source.sizeDelta;rect.anchoredPosition=source.anchoredPosition;rect.localScale=source.localScale;
                    rect.localRotation=source.localRotation;rect.SetSiblingIndex(source.GetSiblingIndex());
                    var sourceLayout=old.GetComponent<UnityEngine.UI.LayoutElement>();
                    if(sourceLayout!=null)
                    {
                        var layout=view.GetComponent<UnityEngine.UI.LayoutElement>()??view.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
                        layout.ignoreLayout=sourceLayout.ignoreLayout;layout.minWidth=sourceLayout.minWidth;layout.minHeight=sourceLayout.minHeight;
                        layout.preferredWidth=sourceLayout.preferredWidth;layout.preferredHeight=sourceLayout.preferredHeight;
                        layout.flexibleWidth=sourceLayout.flexibleWidth;layout.flexibleHeight=sourceLayout.flexibleHeight;layout.layoutPriority=sourceLayout.layoutPriority;
                    }
                    old.gameObject.SetActive(false);Destroy(old.gameObject);_slots[i]=view;
                }
            }
            if(_row==null && _slots.Length>0 && _slots[0]!=null)
            {
                _row=_slots[0].transform.parent as RectTransform;
                // HorizontalLayoutGroup会按父宽拉伸。此行改为统一边长，管理权只能保留一处，避免Layout循环覆盖。
                var layout=_row!=null?_row.GetComponent<UnityEngine.UI.HorizontalLayoutGroup>():null;
                if(layout!=null)layout.enabled=false;
            }
            LayoutSquares();
            for(int i=0;i<_slots.Length;i++)if(_slots[i]!=null)_slots[i].Refresh(controller);
        }
        private void LateUpdate(){if(_row!=null && _row.rect.size!=_lastSize)LayoutSquares();}
        private void LayoutSquares()
        {
            if(_row==null || _slots.Length==0)return;
            _lastSize=_row.rect.size;float spacing=Mathf.Min(Mathf.Max(0,_spacing),_lastSize.x/_slots.Length);
            float side=Mathf.Max(0,Mathf.Min(_lastSize.y,(_lastSize.x-spacing*(_slots.Length-1))/_slots.Length));
            float left=(_lastSize.x-side*_slots.Length-spacing*(_slots.Length-1))*.5f;
            float top=(_lastSize.y-side)*.5f;
            for(int i=0;i<_slots.Length;i++)if(_slots[i]!=null)
            {
                var rect=(RectTransform)_slots[i].transform;rect.anchorMin=rect.anchorMax=rect.pivot=new Vector2(0,1);
                rect.anchoredPosition=new Vector2(left+i*(side+spacing),-top);rect.sizeDelta=Vector2.one*side;
            }
        }
    }
}
