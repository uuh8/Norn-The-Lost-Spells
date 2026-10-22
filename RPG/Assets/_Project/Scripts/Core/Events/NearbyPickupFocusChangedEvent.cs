using UnityEngine;

namespace Game.Core
{
    /// <summary>只读拾取焦点；UI跟随Anchor，游戏逻辑不引用Canvas或UI类型。</summary>
    public struct NearbyPickupFocusChangedEvent:IGameEvent
    {public string InventoryId,ItemId;public Transform Anchor;public bool Visible;}
}
