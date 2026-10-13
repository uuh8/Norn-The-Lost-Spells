using UnityEngine;
namespace Game.Core
{
    /// <summary>资产代理生命周期仅通知表现；Core不引用库存或Rendering实现。</summary>
    public struct WorldItemVisualBoundEvent:IGameEvent
    {public Transform Root;public string InventoryId;public int RegisteredCount;public bool Added;}
}
