using UnityEngine;
namespace Game.Core
{
    /// <summary>Character提交显式F意图；Run再次校验空间资格，Core不引用门或Scene实现。</summary>
    public struct PortalInteractionRequestedEvent : IGameEvent
    { public int PortalId; public Collider Actor; }
}
