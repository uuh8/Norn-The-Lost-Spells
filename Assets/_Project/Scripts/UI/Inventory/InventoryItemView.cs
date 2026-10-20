using System.Collections.Generic;
using Game.Run;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.UI
{
    public enum InventoryItemDisplay : byte { Grid, Row, Program }
    /// <summary>只读物品图；真实空洞通过 Raycast Filter 放行，拖拽只提交意图。</summary>
    [RequireComponent(typeof(RectTransform), typeof(Image))]
    public sealed class InventoryItemView : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler,
        IPointerClickHandler, IPointerDownHandler, IDropHandler, ICanvasRaycastFilter
    {
        [SerializeField] private Text _label;
        [SerializeField,Tooltip("法术专用次数角标：位置/字号在Prefab设计，无限次数不显示。")]
        private Text _spellCount;
        [SerializeField] private Image _icon;
        [SerializeField,Tooltip("独立Prefab模式：只绑定数据；子对象的布局/样式由作者负责。")]
        private bool _authoredLayout;
        [SerializeField] private RectTransform _shapeVisualRoot,_iconRotationRoot;
        [SerializeField] private Image _occupiedCellTemplate;
        [SerializeField] private Text _wandResource;
        [SerializeField] private Color _normalOutlineColor=new Color(.52f,.59f,.57f,1);
        [SerializeField] private Color _selectedOutlineColor=new Color(1,.77f,.25f,1);
        [SerializeField] private Color _invalidOutlineColor=new Color(1,.20f,.16f,1);
        public bool AuthoredLayout=>_authoredLayout;
        private readonly List<Image> _cells = new List<Image>();
        private SinglePlayerInventoryController _controller;
        private InventoryItemSnapshot _snapshot;
        private InventoryItemDisplay _display;
        private float _cellSize;
        private int _turns;
        private bool _wasDragged;
        private bool _hasPressGrab;
        private Vector2 _pressGrab;
        private InventoryShapeOutlineGraphic _outline;
        private bool _selected,_invalid;
        private bool _catalogSquare;
        private Image _containerFill;
        private void Awake() { _containerFill = ContainerIconFill.Ensure(_icon); }
        private static readonly InventoryShape CatalogSquare = new InventoryShape(1, 1, new[] { Vector2Int.zero });
        private Color OutlineColor=>_invalid?_invalidOutlineColor:_selected?_selectedOutlineColor:_normalOutlineColor;
        public int DisplayTurns=>_turns;
        public InventoryItemSnapshot Snapshot => _snapshot;
        public RectTransform Rect => (RectTransform)transform;
        public void SetFont(Font font) { if (_label != null) _label.font = font;if(_spellCount!=null)_spellCount.font=font; }
        public void SetCaption(string caption){if(_label!=null)_label.text=caption;}
        public void ConfigureWandCard(string title, string resource, Font font,Font builtinFont=null)
        {
            if(_authoredLayout)
            {
                if(_label!=null){_label.text=title;if(_label.font==null || _label.font==builtinFont)_label.font=font;}
                if(_wandResource!=null){_wandResource.text=resource;if(_wandResource.font==null || _wandResource.font==builtinFont)_wandResource.font=font;}
                return;
            }
            // 卡片中图标、名称和Mana各占自己的区域，避免原Row文字直接压在法杖图片上。
            if (_wandResource == null)
            { var go = new GameObject("WandResource", typeof(RectTransform), typeof(Text)); go.transform.SetParent(transform, false); _wandResource = go.GetComponent<Text>(); }
            if (_label != null)
            {
                _label.text = title; _label.font = font; _label.alignment = TextAnchor.UpperLeft;
                _label.resizeTextForBestFit = true; _label.resizeTextMinSize = 12; _label.resizeTextMaxSize = 18;
                Stretch(_label.rectTransform); _label.rectTransform.anchorMin = new Vector2(.03f, .73f); _label.rectTransform.anchorMax = new Vector2(.97f, .97f);
            }
            if (_icon != null)
            { Stretch(_icon.rectTransform); _icon.rectTransform.anchorMin = new Vector2(.05f, .26f); _icon.rectTransform.anchorMax = new Vector2(.95f, .73f); }
            _wandResource.text = resource; _wandResource.font = font; _wandResource.color = new Color(.22f, .87f, .93f);
            _wandResource.raycastTarget = false; _wandResource.alignment = TextAnchor.MiddleCenter;
            _wandResource.resizeTextForBestFit = true; _wandResource.resizeTextMinSize = 10; _wandResource.resizeTextMaxSize = 16;
            Stretch(_wandResource.rectTransform); _wandResource.rectTransform.anchorMax = new Vector2(1, .26f);
        }

        // 在目录池预热时补齐组件。滚动只绑定预先计算的快照/文字，不在OnScroll中创建图标或格式化字符串。
        public void PrepareCatalog()
        {
            if (_icon == null)
            { var go = new GameObject("Item icon", typeof(RectTransform), typeof(Image)); go.transform.SetParent(transform, false); _icon = go.GetComponent<Image>(); }
            if (_label == null)
            { var go = new GameObject("Caption", typeof(RectTransform), typeof(Text)); go.transform.SetParent(transform, false); _label = go.GetComponent<Text>(); }
            if (_outline == null)
            { var go = new GameObject("Catalog selection", typeof(RectTransform), typeof(InventoryShapeOutlineGraphic)); go.transform.SetParent(transform, false); _outline = go.GetComponent<InventoryShapeOutlineGraphic>(); Stretch(_outline.rectTransform); }
            EnsureSpellCount();
            if(_containerFill==null)_containerFill=ContainerIconFill.Ensure(_icon);
        }
        public void BindCatalog(SinglePlayerInventoryController controller, in InventoryItemSnapshot item, float size, string caption)
        {
            _controller = controller; _snapshot = item; _display = InventoryItemDisplay.Program; _catalogSquare = true;
            _cellSize = size; _turns = 0; _invalid = false; _selected = controller != null && controller.IsViewSelected(item);
            Rect.sizeDelta = new Vector2(size, size);
            _icon.sprite = item.Icon; _icon.enabled = item.Icon != null; _icon.raycastTarget = false;if(!_authoredLayout)_icon.preserveAspect = true;
            var iconRect = _icon.rectTransform;if(!_authoredLayout)iconRect.localRotation = Quaternion.identity;
            if(!_authoredLayout)
            {
            iconRect.anchorMin = new Vector2(.08f, .44f); iconRect.anchorMax = new Vector2(.92f, .97f); iconRect.offsetMin = iconRect.offsetMax = Vector2.zero;
            _label.raycastTarget = false; _label.alignment = TextAnchor.MiddleCenter;
            _label.resizeTextForBestFit = true; _label.resizeTextMinSize = 14; _label.resizeTextMaxSize = 18;
            _label.horizontalOverflow = HorizontalWrapMode.Wrap;
            if (controller != null && controller.UiFont != null) _label.font = controller.UiFont;
            var labelRect = _label.rectTransform; labelRect.anchorMin = new Vector2(.02f, .01f); labelRect.anchorMax = new Vector2(.98f, .43f); labelRect.offsetMin = labelRect.offsetMax = Vector2.zero;
            }
            _label.text=item.Kind==ItemKind.Container?item.DisplayName:caption;
            if(_containerFill==null)_containerFill=ContainerIconFill.Ensure(_icon);
            ContainerIconFill.Set(_containerFill,_icon,item.Kind==ItemKind.Container,item.ContainerTotal,item.ContainerCapacity);
            // AddComponent<Text>隐式填入LegacyRuntime，不含中文；仅替换这个默认占位字体，保留作者明确提供的Font资产。
            if(controller!=null && (_label.font==null || _label.font==controller.BuiltinUiFont))_label.font=controller.UiFont;
            ApplySpellCount(item.Kind==ItemKind.Spell,caption,controller);
            _outline.Configure(CatalogSquare, 0, size); _outline.color = OutlineColor;
        }

        public void Bind(SinglePlayerInventoryController controller, in InventoryItemSnapshot snapshot,
            InventoryItemDisplay display, float cellSize, int count = 1, int turns = -1)
        {
            _controller = controller; _snapshot = snapshot; _display = display; _cellSize = cellSize; _catalogSquare = false;
            // 视图池Bind会重新设置边框；同步选中缓存和颜色，否则Refresh后LateUpdate会误判为“无需更新”。
            _selected=controller!=null && controller.IsViewSelected(snapshot);
            _invalid=controller!=null && controller.IsArrangementInvalid(snapshot.InstanceId);
            // 旧场景模板只有文字。离散Bind时补一次Image，确保持久Icon真正进入所有消费者；稳定帧不创建对象。
            if(_icon==null && snapshot.Icon!=null)
            {
                var go=new GameObject("Item icon",typeof(RectTransform),typeof(Image));
                go.transform.SetParent(transform,false);_icon=go.GetComponent<Image>();
            }
            _turns = turns < 0 ? snapshot.Location.QuarterTurns : turns;
            bool grid = display == InventoryItemDisplay.Grid;
            if(!_authoredLayout)GetComponent<Image>().color = grid ? Color.clear : new Color(.17f, .21f, .27f, .98f);
            if (grid)
            {
                InventoryShape shape = snapshot.Rules.Shape;
                Rect.sizeDelta = new Vector2(shape.GetWidth(_turns) * cellSize, shape.GetHeight(_turns) * cellSize);
                while (_cells.Count < shape.CellCount)
                {
                    var image=InventorySlotPrefabs.CreateCell(_occupiedCellTemplate,transform,"Occupied cell",Color.white);
                    image.raycastTarget=false;_cells.Add(image);
                }
                for (int i = 0; i < _cells.Count; i++)
                {
                    bool used = i < shape.CellCount; _cells[i].gameObject.SetActive(used);
                    if (!used) continue;
                    Vector2Int cell = shape.GetCell(i, _turns);
                    RectTransform rect = _cells[i].rectTransform; rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
                    // 实心占格完整填充，内部不露出底板缝；真实空洞仍由Shape/RaycastFilter保留。
                    rect.anchoredPosition = new Vector2(cell.x * cellSize, -cell.y * cellSize);
                    rect.sizeDelta = Vector2.one * cellSize;
                    if(_authoredLayout)rect.SetAsFirstSibling();
                    if(!_authoredLayout)_cells[i].color = snapshot.Kind == ItemKind.Spell ? new Color(.27f,.17f,.36f,.97f) : new Color(.10f,.22f,.24f,.97f);
                }
                if(_outline==null){var go=new GameObject("Item shape outline",typeof(RectTransform),typeof(InventoryShapeOutlineGraphic));go.transform.SetParent(transform,false);_outline=go.GetComponent<InventoryShapeOutlineGraphic>();Stretch(_outline.rectTransform);}
                _outline.Configure(shape,_turns,cellSize);
                if(_authoredLayout && _shapeVisualRoot!=null)
                {
                    // 只有这一层映射真实形状实心区域，避免图片跨越空洞。其下Icon/Caption局部Rect始终归作者。
                    var region=IconRegion(shape,_turns);
                    _shapeVisualRoot.anchorMin=_shapeVisualRoot.anchorMax=_shapeVisualRoot.pivot=new Vector2(0,1);
                    _shapeVisualRoot.anchoredPosition=new Vector2(region.x*cellSize,-region.y*cellSize);
                    _shapeVisualRoot.sizeDelta=new Vector2(region.width*cellSize,region.height*cellSize);
                    if(_iconRotationRoot!=null)
                    {
                        _iconRotationRoot.sizeDelta=(_turns&1)==0?_shapeVisualRoot.sizeDelta:new Vector2(_shapeVisualRoot.sizeDelta.y,_shapeVisualRoot.sizeDelta.x);
                        _iconRotationRoot.localRotation=Quaternion.Euler(0,0,-90*_turns);
                    }
                }
            }
            else for (int i = 0; i < _cells.Count; i++) _cells[i].gameObject.SetActive(false);
            if(_outline!=null){_outline.gameObject.SetActive(grid);_outline.color=OutlineColor;_outline.transform.SetAsLastSibling();}
            if (_icon != null) { _icon.sprite = snapshot.Icon; _icon.enabled = snapshot.Icon != null; _icon.raycastTarget = false;if(!_authoredLayout)_icon.preserveAspect=true;
                // 图片只落在最大连续实心矩形内，空洞仍能显示/接收其他物品，旋转不会遮住邻物。
                if(!_authoredLayout && grid){var region=IconRegion(snapshot.Rules.Shape,_turns);var rect=_icon.rectTransform;rect.anchorMin=rect.anchorMax=new Vector2(0,1);rect.pivot=new Vector2(.5f,.5f);
                    rect.anchoredPosition=new Vector2((region.x+region.width*.5f)*cellSize,-(region.y+region.height*.5f)*cellSize);
                    rect.sizeDelta=(_turns&1)==0?new Vector2(region.width*cellSize-8,region.height*cellSize-8):new Vector2(region.height*cellSize-8,region.width*cellSize-8);rect.localRotation=Quaternion.Euler(0,0,-90*_turns);}
                else if(!_authoredLayout){_icon.rectTransform.localRotation=Quaternion.identity;Stretch(_icon.rectTransform);_icon.rectTransform.offsetMin=new Vector2(6,18);_icon.rectTransform.offsetMax=new Vector2(-6,-6);}
                if(!_authoredLayout)_icon.transform.SetAsLastSibling(); }
            if (_label != null)
            {
                if (controller != null && controller.UiFont != null && (!_authoredLayout || _label.font==null || _label.font==controller.BuiltinUiFont)) _label.font = controller.UiFont;
                string charges = "";
                string members = count > 1 ? " ×" + count : "";
                if (snapshot.Kind == ItemKind.Material) charges = " · " + snapshot.MaterialQuantity;
                // 一格物品不能让长名称挤掉次数/堆叠数量。紧凑视图分两行并允许缩小字体，
                // 数字继续可见；完整名称与指定副本在详情区查看。
                if(!_authoredLayout)
                {
                _label.resizeTextForBestFit = display != InventoryItemDisplay.Row;
                // 大网格保留可读字号；不再为了把长名称塞进一格而缩成8像素。全名仍由所选详情承担。
                _label.resizeTextMinSize = grid ? 14 : 8; _label.resizeTextMaxSize = grid ? 18 : 14;
                _label.horizontalOverflow = HorizontalWrapMode.Wrap;
                if(grid)_label.fontSize=18;
                }
                _label.text = grid && snapshot.Icon!=null ? (charges.Length>0?charges.TrimStart(' ','·')+members:count>1?"×"+count:"") : display == InventoryItemDisplay.Row ? snapshot.DisplayName + charges + members
                    : snapshot.DisplayName + (charges.Length > 0 || members.Length > 0 ? "\n" + charges.TrimStart(' ', '·') + members : "");
                if(!_authoredLayout)_label.alignment=grid && snapshot.Icon!=null?TextAnchor.LowerRight:TextAnchor.MiddleCenter;
                if(!_authoredLayout && grid){var region=IconRegion(snapshot.Rules.Shape,_turns);var rect=_label.rectTransform;rect.anchorMin=rect.anchorMax=rect.pivot=new Vector2(0,1);
                    rect.anchoredPosition=new Vector2(region.x*cellSize+4,-region.y*cellSize-2);rect.sizeDelta=new Vector2(region.width*cellSize-8,region.height*cellSize-4);
                }
                _label.raycastTarget = false;if(!_authoredLayout)_label.transform.SetAsLastSibling();
            }
            ApplySpellCount(snapshot.Kind==ItemKind.Spell,snapshot.Kind==ItemKind.Spell
                ?controller!=null?controller.SpellCountCaption(snapshot):snapshot.HasUnlimitedCharges || snapshot.RemainingCharges<=0?"":snapshot.RemainingCharges.ToString():"",controller);
            if(_containerFill==null)_containerFill=ContainerIconFill.Ensure(_icon);
            ContainerIconFill.Set(_containerFill,_icon,snapshot.Kind==ItemKind.Container,snapshot.ContainerTotal,snapshot.ContainerCapacity);
        }

        private void EnsureSpellCount()
        {
            if(_spellCount!=null)return;
            // 旧Scene/Ghost模板首次绑定补齐一次；新Prefab预先带Text。持续Bind不创建对象、不重排作者Text。
            var go=new GameObject("SpellCount",typeof(RectTransform),typeof(Text));
            go.transform.SetParent(_shapeVisualRoot!=null?_shapeVisualRoot:transform,false);
            _spellCount=go.GetComponent<Text>();_spellCount.font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _spellCount.fontSize=28;_spellCount.fontStyle=FontStyle.Bold;_spellCount.alignment=TextAnchor.LowerCenter;
            _spellCount.color=Color.white;_spellCount.raycastTarget=false;_spellCount.resizeTextForBestFit=false;
            _spellCount.horizontalOverflow=HorizontalWrapMode.Overflow;_spellCount.verticalOverflow=VerticalWrapMode.Overflow;
            Stretch(_spellCount.rectTransform);_spellCount.rectTransform.offsetMin=new Vector2(2,0);_spellCount.rectTransform.offsetMax=new Vector2(-2,0);
            go.SetActive(false);
        }
        private void ApplySpellCount(bool spell,string caption,SinglePlayerInventoryController controller)
        {
            if(_snapshot.IsQualificationSpell)caption=""; // 角标只表示投射次数，修正/多重的资格数据继续留在Run。
            if(spell)EnsureSpellCount();
            if(_label!=null){if(spell)_label.text="";_label.gameObject.SetActive(!spell);}
            if(_spellCount==null)return;
            _spellCount.text=spell?caption:"";_spellCount.gameObject.SetActive(spell && caption.Length>0);
            if(!_authoredLayout)_spellCount.transform.SetAsLastSibling();
            if(controller!=null && (_spellCount.font==null || _spellCount.font==controller.BuiltinUiFont))_spellCount.font=controller.UiFont;
        }
        private void LateUpdate(){bool selected=_controller!=null && _controller.IsViewSelected(_snapshot);bool invalid=_controller!=null && _controller.IsArrangementInvalid(_snapshot.InstanceId);if(selected==_selected && invalid==_invalid)return;_selected=selected;_invalid=invalid;if(_outline!=null)_outline.color=OutlineColor;}
        private static void Stretch(RectTransform rect){rect.anchorMin=Vector2.zero;rect.anchorMax=Vector2.one;rect.offsetMin=rect.offsetMax=Vector2.zero;}
        public static RectInt IconRegion(InventoryShape shape,int turns)
        {
            RectInt best=new RectInt(0,0,1,1);int area=0;
            for(int y=0;y<shape.GetHeight(turns);y++)for(int x=0;x<shape.GetWidth(turns);x++)
                for(int h=1;h<=shape.GetHeight(turns)-y;h++)for(int w=1;w<=shape.GetWidth(turns)-x;w++)
                {if(w*h<=area)continue;bool valid=true;for(int cy=y;cy<y+h && valid;cy++)for(int cx=x;cx<x+w;cx++)
                    {bool occupied=false;for(int i=0;i<shape.CellCount;i++)if(shape.GetCell(i,turns)==new Vector2Int(cx,cy)){occupied=true;break;}if(!occupied){valid=false;break;}}
                    if(valid){area=w*h;best=new RectInt(x,y,w,h);}}
            return best;
        }
        public bool IsRaycastLocationValid(Vector2 screenPoint, Camera eventCamera)
        {
            if (_display != InventoryItemDisplay.Grid) return true;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(Rect, screenPoint, eventCamera, out Vector2 local)) return false;
            int x = Mathf.FloorToInt((local.x - Rect.rect.xMin) / _cellSize);
            int y = Mathf.FloorToInt((Rect.rect.yMax - local.y) / _cellSize);
            for (int i = 0; i < _snapshot.Rules.Shape.CellCount; i++)
            { Vector2Int cell = _snapshot.Rules.Shape.GetCell(i, _turns); if (cell.x == x && cell.y == y) return true; }
            return false;
        }
        public void OnBeginDrag(PointerEventData data)
        {
            if (_controller == null || data.button != PointerEventData.InputButton.Left) return;
            _wasDragged=true;
            Vector2Int grab = Vector2Int.zero;
            Vector2? preciseGrab=null;
            bool grid=_display==InventoryItemDisplay.Grid;
            // UGUI达到Drag Threshold才发BeginDrag；保存PointerDown的抓取点，不能用已经移动后的鼠标重算。
            if((grid || _catalogSquare) && (_hasPressGrab || TryGrabPoint(data.position,data.pressEventCamera,out _pressGrab)))
            {preciseGrab=_pressGrab;grab=new Vector2Int(Mathf.FloorToInt(_pressGrab.x),Mathf.FloorToInt(_pressGrab.y));}
            _hasPressGrab=false;
            _controller.BeginDrag(_snapshot, data.position, data.pressEventCamera, _controller.IsSplitPressed, grab,_turns,
                preciseGrab,grid?Rect:null,_cellSize);
        }
        public void OnDrag(PointerEventData data) => _controller?.MoveGhost(data.position, data.pressEventCamera);
        public void OnEndDrag(PointerEventData data) => _controller?.EndItemDrag(data.position,data.pressEventCamera);
        private bool TryGrabPoint(Vector2 pointer,Camera camera,out Vector2 grab)
        {
            grab=default;
            if(_cellSize<=0 || !RectTransformUtility.ScreenPointToLocalPointInRectangle(Rect,pointer,camera,out var local))return false;
            if (_catalogSquare)
            {
                // 一格目录变为多格Ghost时保留点击的相对部位，格索引仍按实际形状计算。
                grab = new Vector2((local.x-Rect.rect.xMin)/Rect.rect.width*_snapshot.Rules.Shape.GetWidth(_turns),
                    (Rect.rect.yMax-local.y)/Rect.rect.height*_snapshot.Rules.Shape.GetHeight(_turns));
            }
            else grab=new Vector2((local.x-Rect.rect.xMin)/_cellSize,(Rect.rect.yMax-local.y)/_cellSize);
            return true;
        }
        public void OnPointerDown(PointerEventData data)
        {
            if(data.button!=PointerEventData.InputButton.Left)return;
            _wasDragged=false;
            _hasPressGrab=(_display==InventoryItemDisplay.Grid || _catalogSquare) && TryGrabPoint(data.position,data.pressEventCamera,out _pressGrab);
        }
        public void OnPointerClick(PointerEventData data)
        {
            _hasPressGrab=false;
            if(data.button==PointerEventData.InputButton.Right){_controller?.ShowItemInfo(_snapshot.InstanceId,data.position,data.pressEventCamera);return;}
            // 某些输入模块会在EndDrag之后继续派发Click；同一次按下发生过拖动就不旋转。
            if(data.button==PointerEventData.InputButton.Left && !_wasDragged)_controller?.ClickItem(_snapshot.InstanceId);
        }
        public void OnDrop(PointerEventData data) => _controller?.DropOnItem(_snapshot,data.position,data.pressEventCamera);
    }
}
