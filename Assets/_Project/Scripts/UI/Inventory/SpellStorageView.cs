using System.Collections.Generic;
using Game.Run;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>独立法术位只读展示；父区域由用户排布，资产移动仍通过Run事务。</summary>
    public sealed class SpellStorageView : MonoBehaviour
    {
        [SerializeField] private RectTransform _content;
        [SerializeField,Tooltip("装饰边框内的水平/垂直留白（Canvas 参考单位）；不改变作者外框位置或真实槽位数量。")]
        private Vector2 _innerPadding=new Vector2(8,8);
        [SerializeField,Tooltip("法术储存空槽背景；可在Spells Inspector指定Sprite，不需要修改Play临时槽。")]
        private Sprite _slotSprite;
        [SerializeField] private Color _slotColor=new Color(.12f,.16f,.22f,.96f);
        private InventoryGridLinesGraphic _lines;
        private RectTransform _innerClip;
        private Vector2 _lastSize;
        private int _layoutCount=-1;
        private readonly List<InventoryDropTarget> _slots = new List<InventoryDropTarget>();
        private readonly List<InventoryItemView> _cards = new List<InventoryItemView>();
        public int VisibleSlotCount => _slots.Count;
        public void Configure(RectTransform content) { _content = content; }

        public void Refresh(SinglePlayerInventoryController controller, InventoryItemView prefab,
            InventoryItemSnapshot[] items, int count, string actor)
        {
            if (_content == null) return;
            EnsureInnerClip();
            int capacity = controller.Session.SpellStorageCapacity;
            // 初始化/离散库存变化扩池，持续拖拽不创建对象。窄屏可横滚，不能压到无法点击。
            while (_slots.Count < capacity)
            {
                int slot = _slots.Count;
                var template=controller.SlotPrefabs!=null?controller.SlotPrefabs.SpellStorageSlot:null;
                if(template!=null)
                {
                    var view=Instantiate(template,_content);view.name="SpellStorageSlot"+(slot+1);
                    view.BindIndex(slot,controller.UiFont);_slots.Add(view.Target);_cards.Add(view.Card);continue;
                }
                var root = new GameObject("SpellStorageSlot" + (slot + 1), typeof(RectTransform), typeof(Image), typeof(InventoryDropTarget));
                root.transform.SetParent(_content, false);
                _slots.Add(root.GetComponent<InventoryDropTarget>());
                _cards.Add(Instantiate(prefab, root.transform));
            }
            if(_lines==null)_lines=InventoryGridLinesGraphic.Ensure(_content);
            LayoutSlots();
            float cell=_content.rect.height;
            _lines.transform.SetAsLastSibling();
            for (int slot = 0; slot < _slots.Count; slot++)
            {
                var target = _slots[slot]; bool active = slot < capacity;
                target.gameObject.SetActive(active); if (!active) continue;
                if(target.GetComponent<InventorySlotView>()==null)
                {var background=target.GetComponent<Image>();background.sprite=_slotSprite;background.color=_slotColor;background.type=_slotSprite!=null?Image.Type.Sliced:Image.Type.Simple;}
                target.Initialize(controller, InventoryDropKind.SpellStorage, actor, slot);
                InventoryItemSnapshot spell = default; bool found = false;
                for (int i = 0; i < count; i++)
                    if (items[i].Location.Kind == ItemLocationKind.SpellStorage && items[i].Location.OwnerId == actor && items[i].Location.SlotIndex == slot)
                    { spell = items[i]; found = true; break; }
                var card = _cards[slot]; card.gameObject.SetActive(found);
                if (!found) continue;
                if(!card.AuthoredLayout){card.Rect.anchorMin = Vector2.zero; card.Rect.anchorMax = Vector2.one;card.Rect.offsetMin = card.Rect.offsetMax = Vector2.zero;}
                card.Bind(controller, spell, InventoryItemDisplay.Program, cell);
                // 卡片只承载Icon/文字，避免通用Program底色遮住作者设置的储存槽背景。
                if(!card.AuthoredLayout)card.GetComponent<Image>().color=Color.clear;
            }
        }

        private void EnsureInnerClip()
        {
            if(_innerClip!=null)return;
            var viewport=_content.parent as RectTransform;
            if(viewport==null)return;
            var scroll=_content.GetComponentInParent<ScrollRect>();
            // 保留作者 Viewport/外框坐标，在其内部增加裁剪层。ScrollRect 必须使用这个内框，
            // 否则右端滚动范围仍按外框计算，最后一格会藏在装饰边框下面。
            var node=new GameObject("SpellStorageRuntime_InnerClip",typeof(RectTransform),typeof(RectMask2D));
            _innerClip=(RectTransform)node.transform;_innerClip.SetParent(viewport,false);
            _innerClip.anchorMin=Vector2.zero;_innerClip.anchorMax=Vector2.one;
            _innerClip.offsetMin=_innerPadding;_innerClip.offsetMax=-_innerPadding;
            _content.SetParent(_innerClip,false);
            _content.anchorMin=_content.anchorMax=_content.pivot=new Vector2(0,1);
            if(scroll!=null)scroll.viewport=_innerClip;
        }

        private void LateUpdate()
        {
            // Canvas Layout 可能晚于 Open/Refresh 才确定宽高。稳定帧只比较两个数；
            // 尺寸改变时才重排已存在的槽，不创建对象、不生成文字或重建整套库存。
            if(_innerClip!=null && _content!=null && _innerClip.rect.size!=_lastSize)LayoutSlots();
        }

        private void LayoutSlots()
        {
            if(_innerClip==null || _lines==null)return;
            Vector2 size=_innerClip.rect.size;
            if(size==_lastSize && _layoutCount==_slots.Count)return;
            _lastSize=size;_layoutCount=_slots.Count;
            float width=Mathf.Max(1,size.x),height=Mathf.Max(1,size.y);
            float maximum=Mathf.Min(height,76);
            int visible=Mathf.Max(1,Mathf.CeilToInt(width/maximum));
            float cell=width/visible;
            _content.sizeDelta=new Vector2(_slots.Count*cell,cell);
            // 两侧通过内框留白，上下再平分整数列适配后的剩余高度，消除单边黄色空白。
            var position=_content.anchoredPosition;position.y=-(height-cell)*.5f;_content.anchoredPosition=position;
            _lines.Configure(_slots.Count,1,cell,0,cell);
            for(int slot=0;slot<_slots.Count;slot++)
            {
                var rect=(RectTransform)_slots[slot].transform;
                rect.anchorMin=rect.anchorMax=rect.pivot=new Vector2(0,1);
                rect.anchoredPosition=new Vector2(slot*cell,0);rect.sizeDelta=Vector2.one*cell;
            }
        }
    }
}
