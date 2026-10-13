using System.Collections.Generic;
using Game.Run;
using UnityEngine;
namespace Game.UI
{
    /// <summary>场景内的UI摆放表。世界坐标/资产Location完全不受影响；新物品才first-fit。</summary>
    public sealed class NearbyItemPlacementCache
    {
        private struct Entry {public InventoryShape Shape;public GridPlacement Placement;}
        private readonly Dictionary<string,Entry> _entries=new Dictionary<string,Entry>();
        private string _draftId;
        private GridPlacement _origin;
        public bool IsInvalid(string id)=>_draftId==id;
        public bool TryGet(string id,out GridPlacement placement)
        {if(_entries.TryGetValue(id,out var entry)){placement=entry.Placement;return true;}placement=default;return false;}
        public GridPlacement Ensure(string id,InventoryShape shape,int columns)
        {
            if(TryGet(id,out var existing))return existing;
            var placement=FindFree(id,shape,0,columns);_entries.Add(id,new Entry{Shape=shape,Placement=placement});return placement;
        }
        private bool Fits(string id,InventoryShape shape,in GridPlacement placement)
        {
            if(placement.X<0 || placement.Y<0)return false;
            foreach(var pair in _entries)
                if(pair.Key!=id && InventoryGrid.Overlaps(shape,placement.InBackpack("ui"),pair.Value.Shape,pair.Value.Placement.InBackpack("ui")))return false;
            return true;
        }
        private GridPlacement FindFree(string id,InventoryShape shape,int turns,int columns)
        {
            columns=Mathf.Max(columns,shape.GetWidth(turns));
            for(int y=0;;y++)for(int x=0;x<=columns-shape.GetWidth(turns);x++)
            {var candidate=new GridPlacement(x,y,turns);if(Fits(id,shape,candidate))return candidate;}
        }
        public void Cancel()
        {
            if(_draftId!=null && _entries.TryGetValue(_draftId,out var entry)){entry.Placement=_origin;_entries[_draftId]=entry;}
            _draftId=null;
        }
        public bool Move(string id,InventoryShape shape,in GridPlacement candidate,int columns)
        {
            if(_draftId!=id)Cancel();
            var origin=Ensure(id,shape,columns);bool valid=Fits(id,shape,candidate);
            if(!valid && _draftId==null){_draftId=id;_origin=origin;}
            _entries[id]=new Entry{Shape=shape,Placement=candidate};
            if(valid)_draftId=null;
            return valid;
        }
        public void Remove(string id){if(_draftId==id)_draftId=null;_entries.Remove(id);}
        public void Clear(){_entries.Clear();_draftId=null;}
        public void Bounds(out int columns,out int rows)
        {
            columns=rows=0;foreach(var pair in _entries)
            {var e=pair.Value;columns=Mathf.Max(columns,e.Placement.X+e.Shape.GetWidth(e.Placement.QuarterTurns));rows=Mathf.Max(rows,e.Placement.Y+e.Shape.GetHeight(e.Placement.QuarterTurns));}
        }
    }
}
