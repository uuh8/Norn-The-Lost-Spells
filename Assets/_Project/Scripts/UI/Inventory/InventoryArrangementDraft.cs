using Game.Run;
namespace Game.UI
{
    /// <summary>一次整理的UI候选；Origin仍由Run拥有，取消只清展示，不执行逆向资产事务。</summary>
    public sealed class InventoryArrangementDraft
    {
        public bool Active {get;private set;}
        public InventoryItemSnapshot Source {get;private set;}
        public ItemLocation Origin=>Source.Location;
        public GridPlacement Candidate {get;private set;}
        public string BagId {get;private set;}
        public bool WholeStack {get;private set;}
        private string[] _members;
        public void Set(in InventoryItemSnapshot source,string bagId,in GridPlacement candidate,bool wholeStack,IInventoryReadOnly read)
        {
            if(!Active || Source.InstanceId!=source.InstanceId || WholeStack!=wholeStack)
            {
                Source=source;WholeStack=wholeStack;
                var items=new InventoryItemSnapshot[read.Count];read.CopyItemsTo(items);
                int count=0;for(int i=0;i<items.Length;i++)if(items[i].Location==source.Location && (wholeStack || items[i].InstanceId==source.InstanceId))count++;
                _members=new string[count];count=0;for(int i=0;i<items.Length;i++)if(items[i].Location==source.Location && (wholeStack || items[i].InstanceId==source.InstanceId))_members[count++]=items[i].InstanceId;
            }
            BagId=bagId;Candidate=candidate;Active=true;
        }
        public bool Contains(string id)
        {if(!Active)return false;for(int i=0;i<_members.Length;i++)if(_members[i]==id)return true;return false;}
        public bool OriginUnchanged(IInventoryReadOnly read)
        {
            if(!Active)return true;
            for(int i=0;i<_members.Length;i++)if(!read.TryGetView(_members[i],out var item) || item.Location!=Origin)return false;
            if(WholeStack)
            {
                var items=new InventoryItemSnapshot[read.Count];read.CopyItemsTo(items);int count=0;
                for(int i=0;i<items.Length;i++)if(items[i].Location==Origin)count++;
                if(count!=_members.Length)return false;
            }
            return true;
        }
        public void Clear(){Active=false;_members=null;BagId=null;}
    }
}
