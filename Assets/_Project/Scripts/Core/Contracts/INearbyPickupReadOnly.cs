namespace Game.Core
{
    /// <summary>附近候选的稳定身份与版本；UI按身份读库存快照，不获得资产写权限。</summary>
    public interface INearbyPickupReadOnly
    {int Count{get;}int SelectedIndex{get;}uint Revision{get;}string GetItemId(int index);}
}
