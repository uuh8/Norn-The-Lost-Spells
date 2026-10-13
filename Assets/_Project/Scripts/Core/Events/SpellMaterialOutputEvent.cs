using UnityEngine;

namespace Game.Core
{
    // 唯一输出ID由Run批准，Character不能用预算值自行创建第二份资产。
    public struct SpellMaterialOutputEvent : IGameEvent
    { public string InventoryId, OutputId; public Vector3 Position; public bool Ended; }
    public struct SpellMaterialOutputResultEvent : IGameEvent
    { public string InventoryId, OutputId; public bool Resolved, Ended; public ulong Quantity; public MaterialTransferFailure Failure; }
}
