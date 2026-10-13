using System.Collections.Generic;
using Game.Run;
using UnityEngine;
using UnityEngine.UI;
namespace Game.UI
{
    public sealed class NearbyItemGridView:MonoBehaviour
    {
        [SerializeField,Min(24)]private float _cellSize=64;
        private readonly List<InventoryItemView> _views=new List<InventoryItemView>();
        private readonly List<Image> _cells=new List<Image>();
        private InventoryGridLinesGraphic _lines;
        private ScrollRect _scroll;
        private InventoryDragAutoScroll _autoScroll;
        private bool _hasLayout;
        private readonly NearbyItemPlacementCache _placements=new NearbyItemPlacementCache();
        private readonly List<string> _known=new List<string>();
        private SinglePlayerInventoryController _controller;
        private InventoryItemView _prefab;
        private InventoryItemSnapshot[] _items;
        private int _count;
        private NearbyPickupController _nearby;
        private int _spareColumns,_spareRows;
        private string _inventoryId,_sceneKey;
        public int Columns{get;private set;}public int Rows{get;private set;}
        public float CellSize=>_cellSize;
        public bool IsInvalid(string id)=>_placements.IsInvalid(id);
        public bool TryPlacement(string id,out GridPlacement placement)=>_placements.TryGet(id,out placement);
        public void CancelArrangement()=>_placements.Cancel();
        public void MoveItem(string id,InventoryShape shape,in GridPlacement placement)=>_placements.Move(id,shape,placement,Columns);
        public bool TryGetCell(Vector2 pointer,Camera camera,out Vector2Int cell)
        {
            cell=default;var rect=(RectTransform)transform;
            if(!RectTransformUtility.ScreenPointToLocalPointInRectangle(rect,pointer,camera,out var local))return false;
            cell=new Vector2Int(Mathf.FloorToInt((local.x-rect.rect.xMin)/_cellSize),Mathf.FloorToInt((rect.rect.yMax-local.y)/_cellSize));return true;
        }
        public void PrepareDrag(InventoryShape shape,int turns)
        {
            // 离散BeginDrag扩展池；稳定自动滚动帧只改ScrollRect，不新建格子。
            _placements.Bounds(out var occupiedColumns,out var occupiedRows);
            var viewport=(RectTransform)transform.parent;
            _spareColumns=Mathf.Max(_spareColumns,Mathf.Max(occupiedColumns,Mathf.CeilToInt(viewport.rect.width/_cellSize))+shape.GetWidth(turns)+2);
            _spareRows=Mathf.Max(_spareRows,Mathf.Max(occupiedRows,Mathf.CeilToInt(viewport.rect.height/_cellSize))+shape.GetHeight(turns)+2);
            if(_controller!=null)Refresh(_controller,_prefab,_items,_count,_nearby);
        }
        public void Refresh(SinglePlayerInventoryController controller,InventoryItemView prefab,InventoryItemSnapshot[] items,int count,NearbyPickupController nearby)
        {
            _controller=controller;_prefab=prefab;_items=items;_count=count;_nearby=nearby;
            string inventory=controller.Session.ReadOnly.InventoryId,scene=controller.Session.WorldSceneKey;
            if(_inventoryId!=inventory || _sceneKey!=scene){_placements.Clear();_known.Clear();_spareColumns=_spareRows=0;_hasLayout=false;_inventoryId=inventory;_sceneKey=scene;}
            for(int k=_known.Count-1;k>=0;k--)
            {bool exists=false;for(int i=0;i<count;i++)if(items[i].InstanceId==_known[k] && items[i].Location.Kind==ItemLocationKind.WorldItem){exists=true;break;}
                if(!exists){_placements.Remove(_known[k]);_known.RemoveAt(k);}}
            int n=0;for(int i=0;i<count;i++)if(items[i].Location.Kind==ItemLocationKind.WorldItem && nearby!=null && nearby.IsNearby(items[i].InstanceId))n++;
            var visible=new InventoryItemSnapshot[n];var shapes=new InventoryShape[n];int index=0;
            for(int i=0;i<count;i++)if(items[i].Location.Kind==ItemLocationKind.WorldItem && nearby!=null && nearby.IsNearby(items[i].InstanceId))
            {visible[index]=items[i];shapes[index++]=items[i].Rules.Shape;}
            if(_scroll==null)_scroll=GetComponentInParent<ScrollRect>();
            if(_scroll!=null && _autoScroll==null){_autoScroll=_scroll.GetComponent<InventoryDragAutoScroll>();if(_autoScroll==null)_autoScroll=_scroll.gameObject.AddComponent<InventoryDragAutoScroll>();_autoScroll.Configure(_scroll);}
            Vector2 previous=_hasLayout && _scroll!=null ? _scroll.normalizedPosition : new Vector2(0,1);
            var viewport=transform.parent as RectTransform;var content=(RectTransform)transform;
            // normalizedPosition是比例；Content扩展时保持它会让物品瞬间跳动。先保存实际滚过的局部距离。
            float previousX=previous.x*Mathf.Max(0,content.rect.width-viewport.rect.width);
            float previousY=(1-previous.y)*Mathf.Max(0,content.rect.height-viewport.rect.height);
            int columns=Mathf.Max(1,Mathf.FloorToInt(viewport.rect.width/_cellSize));
            var target=viewport.GetComponent<InventoryDropTarget>();if(target!=null)target.Initialize(controller,InventoryDropKind.Ground);
            for(int i=0;i<n;i++){_placements.Ensure(visible[i].InstanceId,shapes[i],columns);if(!_known.Contains(visible[i].InstanceId))_known.Add(visible[i].InstanceId);}
            _placements.Bounds(out var occupiedColumns,out var occupiedRows);Columns=Mathf.Max(columns,occupiedColumns,_spareColumns);
            // 附近是无限制的展示区，至少铺满Viewport并留一行空白，丢弃不参与这里的容量判断。
            Rows=Mathf.Max(Mathf.CeilToInt(viewport.rect.height/_cellSize)+1,occupiedRows+1,_spareRows);
            ((RectTransform)transform).sizeDelta=new Vector2(Columns*_cellSize,Rows*_cellSize);
            var styles=controller.SlotPrefabs;
            while(_cells.Count<Columns*Rows)_cells.Add(InventorySlotPrefabs.CreateCell(styles!=null?styles.NearbyCell:null,transform,"Nearby grid cell",new Color(.09f,.14f,.14f,1)));
            for(int i=0;i<_cells.Count;i++){bool used=i<Columns*Rows;_cells[i].gameObject.SetActive(used);if(!used)continue;var rect=_cells[i].rectTransform;
                rect.anchorMin=rect.anchorMax=rect.pivot=new Vector2(0,1);rect.anchoredPosition=new Vector2(i%Columns*_cellSize,-i/Columns*_cellSize);rect.sizeDelta=Vector2.one*_cellSize;rect.SetAsFirstSibling();}
            if(_lines==null)_lines=InventoryGridLinesGraphic.Ensure(transform);
            _lines.Configure(Columns,Rows,_cellSize);_lines.transform.SetAsLastSibling();
            while(_views.Count<n)_views.Add(Instantiate(styles!=null && styles.NearbyItem!=null?styles.NearbyItem:prefab,transform));
            for(int i=0;i<_views.Count;i++){_views[i].gameObject.SetActive(i<n);if(i>=n)continue;var view=_views[i];view.Rect.anchorMin=view.Rect.anchorMax=view.Rect.pivot=new Vector2(0,1);
                _placements.TryGet(visible[i].InstanceId,out var placement);
                view.gameObject.SetActive(!controller.IsPendingPickup(visible[i].InstanceId));
                view.Rect.anchoredPosition=new Vector2(placement.X*_cellSize,-placement.Y*_cellSize);view.Bind(controller,visible[i],InventoryItemDisplay.Grid,_cellSize,1,placement.QuarterTurns);view.transform.SetAsLastSibling();}
            for(int i=0;i<n;i++)if(_placements.IsInvalid(visible[i].InstanceId))_views[i].transform.SetAsLastSibling();
            if(_scroll!=null){_scroll.StopMovement();float overflowX=content.rect.width-viewport.rect.width,overflowY=content.rect.height-viewport.rect.height;
                _scroll.normalizedPosition=new Vector2(overflowX>0?Mathf.Clamp01(previousX/overflowX):0,overflowY>0?1-Mathf.Clamp01(previousY/overflowY):1);}
            _hasLayout=true;
        }
        public bool TickAutoScroll(Vector2 pointer,Camera camera,float delta)
        {
            return _autoScroll!=null && _autoScroll.Tick(pointer,camera,true,delta);
        }
        public void RevealItem(string id)
        {
            if(_scroll==null || string.IsNullOrEmpty(id))return;
            for(int i=0;i<_views.Count;i++)
            {
                var view=_views[i];if(!view.gameObject.activeSelf || view.Snapshot.InstanceId!=id)continue;
                var viewport=_scroll.viewport;var content=(RectTransform)transform;
                float overflowY=content.rect.height-viewport.rect.height,overflowX=content.rect.width-viewport.rect.width;
                // 对准新丢物品的起始行/列，原物品不被移动；特别大的形状继续用滚动查看全貌。
                if(overflowY>0)_scroll.verticalNormalizedPosition=1-Mathf.Clamp01(-view.Rect.anchoredPosition.y/overflowY);
                if(overflowX>0)_scroll.horizontalNormalizedPosition=Mathf.Clamp01(view.Rect.anchoredPosition.x/overflowX);
                return;
            }
        }
    }
}
