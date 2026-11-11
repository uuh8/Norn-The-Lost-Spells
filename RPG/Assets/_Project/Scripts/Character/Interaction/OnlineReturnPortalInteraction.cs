using System;
using Game.Core;
using Game.Run;
using UnityEngine;

namespace Game.Character
{
    /// <summary>
    /// 把DS批准的门接回原F优先级。它只核本机真实Collider/Owner及只读World门，不授结算或写库存；
    /// Replica不能用本机位置宣告成功，最后仍由DS重验同一门与当前角色。
    /// </summary>
    public sealed class OnlineReturnPortalInteraction : MonoBehaviour, IPortalInteractionTarget
    {
        private Collider _portal, _actor;
        private Func<bool> _available;
        private ulong _actorId;
        private uint _epoch;
        private int _world;
        public Transform PortalTransform => _portal != null ? _portal.transform : transform;
        public string InteractionLabel => "F 返回据点";
        public void Configure(Collider portal, Collider actor, ulong actorId, uint epoch, int worldSceneHandle, Func<bool> available)
        {
            _portal = portal; _actor = actor; _actorId = actorId; _epoch = epoch; _world = worldSceneHandle; _available = available;
            if (isActiveAndEnabled) PortalInteractionRegistry.Register(this);
        }
        private void OnEnable() { if (_portal != null) PortalInteractionRegistry.Register(this); }
        private void OnDisable() => PortalInteractionRegistry.Unregister(this);
        public bool CanInteract(Collider actor) => _portal != null && _actor != null && actor == _actor && actor.enabled
            && actor.gameObject.activeInHierarchy && actor.gameObject.scene.handle == _world && _actorId > 0 && _epoch > 0
            && _portal.gameObject.scene == actor.gameObject.scene && _available != null && _available()
            && PortalInteractionRegistry.Contains(_portal, actor);
        public bool Interact(Collider actor)
        {
            if (!CanInteract(actor)) return false;
            EventBus<OnlineReturnPortalRequestedEvent>.Publish(new OnlineReturnPortalRequestedEvent
                { ActorId = _actorId, ConnectionEpoch = _epoch, WorldSceneHandle = _world }); return true;
        }
        // Character可依赖Run，Multiplayer仅通过此只读查询桥复用原动态placement，不反向取得Run写口。
        public static Pose ResolveDynamicPose(Transform actor) => ReturnPortalPlacement.Resolve(actor);
    }
}
