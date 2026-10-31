using System;
using Game.Core;
using Game.Run;
using UnityEngine;
using UnityEngine.UI;
using Unity.Profiling;

namespace Game.UI
{
    /// <summary>前三格法杖资源、后七格道具数量。接线允许作者删掉该类型不需要的Text/条。</summary>
    public sealed class HeldItemHud : MonoBehaviour, ICombatHudElement
    {
        public GameObject PresentationRoot => gameObject;
        [Serializable] public sealed class Cell
        {
            public Image Icon,Selection,Mana,Cooldown;
            [NonSerialized] internal Image Frame;
            [NonSerialized] internal Sprite DefaultFrame;
            [NonSerialized] internal Image ContainerFill;
            [NonSerialized] internal bool Container;
            public Text Title,Value;
            [NonSerialized] public string ItemId;
            [NonSerialized] internal HeldItemHudValues Display;
            [NonSerialized] internal ulong Maximum;
            [NonSerialized] internal int Quantity;
            [NonSerialized] internal bool Initialized;
            [NonSerialized] internal Color OriginalColor;
        }
        [SerializeField] private SinglePlayerInventorySession _session;
        private IInventoryPresentationSource _presentationSource;
        private bool _hasPresentationOverride, _readAvailable;
        public IInventoryPresentationSource PresentationSource
        {
            get
            {
                var source = _hasPresentationOverride ? _presentationSource : _session;
                return source is UnityEngine.Object value && value == null ? null : source;
            }
        }
        /// <summary>复用原十格的 Renderer/布局，只更换只读数据，不建立第二套联机快捷栏。</summary>
        public void BindPresentationSource(IInventoryPresentationSource source)
        {
            _hasPresentationOverride = true; _presentationSource = source;
            ClearBindings(); RefreshBindings();
        }
        [SerializeField] private Cell[] _cells=new Cell[Game.Core.HeldItemSlots.TotalCount];
        [SerializeField] private SinglePlayerInventoryController _backpack,_warehouse;
        [SerializeField] private ShopController _shop;
        [SerializeField] private Canvas _canvas;
        [SerializeField] private Sprite _selectedFrame;
        private Font _font;
        private bool _ready;
        private InventoryItemSnapshot[] _items = Array.Empty<InventoryItemSnapshot>();
        public void ConfigureVisibility(SinglePlayerInventoryController backpack,SinglePlayerInventoryController warehouse,ShopController shop,Canvas canvas)
        { _backpack=backpack;_warehouse=warehouse;_shop=shop;_canvas=canvas; }
        private static readonly ProfilerMarker UpdateMarker=new ProfilerMarker("Norn.HeldItemHud.Update");
        public void Configure(SinglePlayerInventorySession session,Cell[] cells)
        { _hasPresentationOverride=false;_presentationSource=null;_session=session;_cells=cells;if(_ready){ClearBindings();RefreshBindings();} }
        private void OnEnable()
        { EventBus<InventoryChangedEvent>.Subscribe(OnInventoryChanged); EventBus<ToolbeltChangedEvent>.Subscribe(OnToolsChanged); RefreshBindings(); }
        private void OnDisable()
        { EventBus<InventoryChangedEvent>.Unsubscribe(OnInventoryChanged); EventBus<ToolbeltChangedEvent>.Unsubscribe(OnToolsChanged); }
        private void OnInventoryChanged(InventoryChangedEvent change)
        { if (change.InventoryId == PresentationSource?.ReadOnly?.InventoryId) RefreshBindings(); }
        private void OnToolsChanged(ToolbeltChangedEvent change)
        { if (change.InventoryId == PresentationSource?.ReadOnly?.InventoryId) RefreshBindings(); }
        private void Start()
        {
            _font=Font.CreateDynamicFontFromOSFont(new[]{"Microsoft YaHei UI","Microsoft YaHei","Arial"},16);
            for (int i=0;i<_cells.Length;i++)
            {
                var cell = _cells[i]; if (cell == null) continue;
                if (cell.Title != null && (cell.Title.font == null || cell.Title.font.name == "LegacyRuntime")) cell.Title.font = _font;
                if (cell.Value != null)
                {
                    if (cell.Value.font == null) cell.Value.font = _font;
                    cell.Display = new HeldItemHudValues(cell.Value);
                }
                if (cell.Selection != null) cell.OriginalColor = cell.Selection.color;
                // 使用作者的装饰 Frame，选中不污染 Icon/槽背景的美术颜色。
                if (cell.Icon != null) cell.Frame = cell.Icon.transform.Find("Frame")?.GetComponent<Image>();
                if (cell.Frame != null) cell.DefaultFrame = cell.Frame.sprite;
                cell.ContainerFill = ContainerIconFill.Ensure(cell.Icon);
            }
            // Editor 接线保留作者预览；Runtime 必须先隐藏占位物品，可信来源 Ready 后才重新显示。
            _ready = true; ClearBindings(); RefreshBindings();
        }
        private void RefreshBindings()
        {
            if (!_ready) return;
            var source = PresentationSource;
            if (source?.ReadOnly == null) { if(_readAvailable)ClearBindings(); return; }
            _readAvailable = true;
            var read = source.ReadOnly; var tools = source.ToolbeltReadOnly; var wands = source.WandsReadOnly;
            int count = -1;
            for (int i = 0; i < _cells.Length; i++)
            {
                var cell = _cells[i]; if (cell == null) continue;
                string id = null; ulong maximum = 0; int quantity = 1; bool container = false;
                if (i < HeldItemSlots.WandCount && wands != null && wands.TryGetSlot(i, out var wand))
                { id = wand.InstanceId; maximum = (ulong)Math.Ceiling(wand.MaxMana); }
                else if (i >= HeldItemSlots.WandCount && tools != null && tools.TryGetBinding(i - HeldItemSlots.WandCount, out var tool) && read.TryGetView(tool, out var item))
                {
                    id = tool; container = item.Kind == ItemKind.Container; maximum = item.ContainerCapacity;
                    if (!container && item.Rules.MaxStack > 1)
                    {
                        // 只有可堆叠的绑定物品才扫描快照，普通倒料不会复制整份库存。
                        if (count < 0) { if (_items.Length < read.Count) _items = new InventoryItemSnapshot[Math.Max(16, read.Count)]; count = read.CopyItemsTo(_items); }
                        quantity = 0; for (int j = 0; j < count; j++) if (_items[j].Location == item.Location) quantity++;
                    }
                }
                if (cell.Initialized && cell.ItemId == id && cell.Maximum == maximum && cell.Quantity == quantity) continue;
                cell.Initialized = true; cell.ItemId = id; cell.Maximum = maximum; cell.Quantity = quantity;
                cell.Container = container;
                bool exists = read.TryGetView(id, out var view);
                if (cell.Title != null) cell.Title.text = HeldItemSlots.KeyLabel(i);
                if (cell.Icon != null) { cell.Icon.sprite = exists ? view.Icon : null; cell.Icon.enabled = cell.Icon.sprite != null; }
                if (id == null) cell.Display?.Clear();
                else if(container)cell.Display?.Clear();
                else cell.Display?.Bind(maximum, quantity, false, i < HeldItemSlots.WandCount);
                ContainerIconFill.Set(cell.ContainerFill,cell.Icon,container,exists?view.ContainerTotal:0,maximum);
                if (id == null || i >= HeldItemSlots.WandCount) { SetFill(cell.Mana, 0); SetFill(cell.Cooldown, 0); }
            }
        }
        private void Update()
        {
            if (!_ready) return;
            using var sample=UpdateMarker.Auto();
            // 独立HUD Canvas不依赖已停用的旧据点提示组件；关闭整备后必须能恢复显示。
            bool overlay=_backpack!=null && _backpack.IsOpen || _warehouse!=null && _warehouse.IsOpen || _shop!=null && _shop.IsOpen;
            if(_canvas!=null && _canvas.enabled==overlay)_canvas.enabled=!overlay;
            // Canvas 还包含原准心与空槽框；库存 Ready 只决定物品内容，不能阻止独立的界面显隐。
            var source=PresentationSource;
            if(source?.ReadOnly==null){if(_readAvailable)ClearBindings();return;}
            var tools=source.ToolbeltReadOnly; var wands=source.WandsReadOnly;
            for (int i=0;i<_cells.Length;i++)
            {
                var cell=_cells[i]; if (cell == null) continue;
                string id=null; double current=0,maximum=0; ulong amount=0; float cooldown=0,castCooldown=0;
                if (i<HeldItemSlots.WandCount && wands != null && wands.TryGetSlot(i,out var state))
                { id=state.InstanceId; current=state.CurrentMana; maximum=state.MaxMana; cooldown=state.CooldownRemaining; castCooldown=state.CastCooldown; }
                else if (i>=HeldItemSlots.WandCount && tools != null && tools.TryGetBinding(i-HeldItemSlots.WandCount,out var tool) && source.ReadOnly.TryGetView(tool,out var item))
                { id=tool; amount=item.ContainerTotal; }
                if (cell.Frame != null)
                {
                    var sprite = tools != null && tools.SelectedSlot == i && _selectedFrame != null ? _selectedFrame : cell.DefaultFrame;
                    if (cell.Frame.sprite != sprite) cell.Frame.sprite = sprite;
                }
                if (id==null || id != cell.ItemId) continue;
                if (i < HeldItemSlots.WandCount)
                {
                    cell.Display?.Set((ulong)Math.Max(0, current));
                    SetFill(cell.Mana,maximum>0?(float)(current/maximum):0);
                    SetFill(cell.Cooldown,castCooldown>0?Mathf.Clamp01(cooldown/castCooldown):0);
                }
                else if(cell.Container)ContainerIconFill.Set(cell.ContainerFill,cell.Icon,true,amount,cell.Maximum);
                else cell.Display?.Set(amount);
            }
        }
        private void ClearBindings()
        {
            _readAvailable = false;
            // 换身份或清空来源时清掉旧 Icon/资源缓存，不能因新来源尚未 Ready 而泄露旧存档内容。
            for(int i=0;i<_cells.Length;i++)
            {
                var cell=_cells[i];if(cell==null)continue;
                cell.ItemId=null;cell.Initialized=false;cell.Maximum=0;cell.Quantity=0;cell.Container=false;
                if(cell.Icon!=null){cell.Icon.sprite=null;cell.Icon.enabled=false;}
                if(cell.Frame!=null && cell.Frame.sprite!=cell.DefaultFrame)cell.Frame.sprite=cell.DefaultFrame;
                cell.Display?.Clear();SetFill(cell.Mana,0);SetFill(cell.Cooldown,0);
                ContainerIconFill.Set(cell.ContainerFill,cell.Icon,false,0,0);
            }
        }
        private static void SetFill(Image image,float ratio)
        {
            if (image == null) return;
            ratio=Mathf.Clamp01(ratio);image.fillAmount=ratio;image.enabled=ratio>0;
            // 无Sprite时UGUI Image生成普通四边形，Filled不生效；改变本条的宽度让纯色fallback仍正确。
            // 已有Sprite走Filled网格，保持满宽；避免美术后来补Sprite后宽度与fill双重缩放。
            float width=image.sprite==null?ratio:1;var r=image.rectTransform;var maximum=r.anchorMax;if(maximum.x!=width){maximum.x=width;r.anchorMax=maximum;}
        }
        private void OnDestroy() { if (_font!=null) Destroy(_font); }
    }
}
