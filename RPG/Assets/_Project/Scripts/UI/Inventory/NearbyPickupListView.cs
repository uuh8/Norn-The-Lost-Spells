using Game.Core;
using Game.Run;
using UnityEngine;
using UnityEngine.UI;
using Unity.Profiling;
namespace Game.UI
{
    /// <summary>角色旁的固定屏幕列表：站点/传送门/物品共用F，稳定帧只读空间/版本，不提交交互。</summary>
    public sealed class NearbyPickupListView:MonoBehaviour
    {
        [SerializeField]private SinglePlayerInventorySession _session;
        [SerializeField]private NearbyPickupController _nearby;
        [SerializeField]private SinglePlayerInventoryController _inventory;
        [SerializeField]private GameObject _visual;
        [SerializeField]private GameObject[] _rows;
        [SerializeField]private Text[] _labels;
        [SerializeField]private Image[] _icons,_backgrounds;
        [SerializeField]private Text _footer;
        [SerializeField,Range(1,5)]private int _visibleRows=5;
        [SerializeField]private HubInteractionController _hub;
        private int _stationKind=-1;
        private Collider _portalActor;
        private IPortalInteractionTarget _portalTarget;
        private static readonly string[] StationNames={"F 仓库","F 商店","传送门","F 法术研究台"};
        private bool _dirty=true;private uint _revision;private Font _font;
        public int VisibleCount{get;private set;}public int FirstIndex{get;private set;}
        public RectTransform Panel=>_visual!=null?(RectTransform)_visual.transform:null;
        private static readonly ProfilerMarker Marker=new ProfilerMarker("Norn.Items.PickupList");
        private void Awake()
        {
            // 兼容已保存的旧Scene：只在初始化查找一次并复制现有行，保留素材/字体/作者样式。
            if(_hub==null && _session!=null)_hub=_session.GetComponent<HubInteractionController>();
            // 旧额外行仅做迁移清理；新的站点提示直接复用 Entry 池，不再创建特殊分支。
            if(Panel!=null)for(int i=Panel.childCount-1;i>=0;i--)
            {var child=Panel.GetChild(i);if(child.name.StartsWith("Station entry")){child.gameObject.SetActive(false);Destroy(child.gameObject);}}
            if(_hub!=null)foreach(var root in gameObject.scene.GetRootGameObjects())foreach(var label in root.GetComponentsInChildren<TextMesh>(true))
                if(IsLegacyStationLabel(label,_hub.Definition))label.gameObject.SetActive(false);
        }
        public static bool IsLegacyStationLabel(TextMesh label,HubDefinition definition)
        {
            if(label==null || definition?.Stations==null)return false;
            foreach(var station in definition.Stations)
                if((station.Kind==HubStationKind.Shop || station.Kind==HubStationKind.Warehouse)
                    && (label.text==station.DisplayName+" [E]" || label.text==station.DisplayName+" [F]"))return true;
            return false;
        }
        private void OnEnable(){EventBus<NearbyPickupListChangedEvent>.Subscribe(OnList);_dirty=true;}
        private void OnDisable(){EventBus<NearbyPickupListChangedEvent>.Unsubscribe(OnList);if(_visual!=null)_visual.SetActive(false);}
        private void OnList(NearbyPickupListChangedEvent e){if(_session?.ReadOnly?.InventoryId==e.InventoryId)_dirty=true;}
        private void LateUpdate()
        {
            using var sample=Marker.Auto();
            int station=-1;
            IPortalInteractionTarget portal = null;
            if (_portalActor == null && _session?.ActorTransform != null) _portalActor = _session.ActorTransform.GetComponent<CharacterController>();
            if(Time.timeScale>0 && _hub!=null && _hub.isActiveAndEnabled && _hub.TryGetStation(out var point)
                && (point.Kind==HubStationKind.Shop || point.Kind==HubStationKind.Warehouse || point.Kind==HubStationKind.SpellResearch))station=(int)point.Kind;
            if(station < 0 && Time.timeScale > 0 && PortalInteractionRegistry.TryGetNearest(_portalActor, out portal)) station = 2;
            if(station!=_stationKind || !ReferenceEquals(portal,_portalTarget)){_stationKind=station;_portalTarget=portal;_dirty=true;}
            bool hasItems=_nearby!=null && _nearby.isActiveAndEnabled && _nearby.Count>0;
            bool show=(hasItems || _stationKind>=0) && Time.timeScale>0;
            if(_visual==null)return;if(_visual.activeSelf!=show)_visual.SetActive(show);
            if(!show)return;var font=_inventory!=null?_inventory.UiFont:null;
            if(_nearby!=null && _revision!=_nearby.Revision || _font!=font)_dirty=true;
            if(_dirty)RefreshNow();
        }
        public void RefreshNow()
        {
            if(_session?.ReadOnly==null || _rows==null)return;
            INearbyPickupReadOnly read=_nearby;int count=_nearby!=null && _nearby.isActiveAndEnabled?read.Count:0;
            bool station=_stationKind>=0;
            int offset=station?1:0;
            int capacity=Mathf.Max(0,Mathf.Min(_visibleRows,_rows.Length)-offset);
            FirstIndex=count>0?Mathf.Clamp(read.SelectedIndex-capacity+1,0,Mathf.Max(0,count-capacity)):0;VisibleCount=Mathf.Min(capacity,count);
            _font=_inventory!=null?_inventory.UiFont:null;
            for(int i=0;i<_rows.Length;i++)
            {
                bool used=i<VisibleCount+offset;if(_rows[i].activeSelf!=used)_rows[i].SetActive(used);if(!used)continue;
                // 根位置、子 Text/Image 的 Rect 和对齐全部由作者决定；这里仅绑定数据。
                if(_font!=null && (_labels[i].font==null || _labels[i].font==_inventory.BuiltinUiFont))_labels[i].font=_font;
                if(station && i==0)
                {
                    _labels[i].text=_portalTarget!=null?_portalTarget.InteractionLabel:StationNames[_stationKind];
                    _labels[i].color=new Color(1,.92f,.6f,1);_icons[i].enabled=false;_backgrounds[i].color=Color.white;
                    continue;
                }
                int itemIndex=FirstIndex+i-offset;
                bool selected=itemIndex==read.SelectedIndex;
                if(_session.ReadOnly.TryGetView(read.GetItemId(itemIndex),out var item))
                {
                    _labels[i].text=(selected && !station?"F  ":"    ")+item.DisplayName;
                    _labels[i].color=selected?new Color(1,.92f,.6f,1):Color.white;
                    _icons[i].sprite=item.Icon;_icons[i].enabled=item.Icon!=null;
                    _backgrounds[i].color=selected?Color.white:new Color(.65f,.69f,.75f,.88f);
                }
            }
            if(_footer!=null){_footer.gameObject.SetActive(count>1 && !station);if(_font!=null && (_footer.font==null || _footer.font==_inventory.BuiltinUiFont))_footer.font=_font;
                if(count>1)_footer.text="滚轮选择 · F 拾取  "+(read.SelectedIndex+1)+" / "+count;}
            _revision=read!=null?read.Revision:0;_dirty=false;
        }
    }
}
