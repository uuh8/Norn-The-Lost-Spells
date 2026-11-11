using Game.Core;
using Game.Run;
using UnityEngine;

namespace Game.Character
{
    /// <summary>
    /// 可信Home地图的角色接缝。World装配提供真实作者绑定，DS才选择和平Owner；Wire/UI没有purpose开关。
    /// Client仅注册原F目标和只读能力，真正Open/编辑仍是原Owner命令16/17，不在本机暂停或修改资源。
    /// </summary>
    public sealed partial class OnlineCastDriver : IOnlineHomeResearchAccess, IPortalInteractionTarget
    {
        private OnlineHomeWorldBindings _homeBindings;
        private PortalInteractInput _homePortalInput;
        private bool _homePresentationBound;
        public GameObject Actor => gameObject;
        public int WorldSceneHandle => _homeBindings != null ? (int)_homeBindings.gameObject.scene.handle : (int)gameObject.scene.handle;
        public uint ConnectionEpoch => _epoch;
        public bool CanResearch => isActiveAndEnabled && _homeBindings != null && _connected && _wizard != null
            && _wizard.Health != null && _wizard.Health.IsAlive && _homeBindings.ContainsResearch(transform.position);
        public bool ResearchPaused => _authority ? _workspace != null && _workspace.ResearchPaused
            : _clientOwner && ClientInventoryReady && _inventoryReplica.Source is OnlineInventoryPresentationSource source && source.ResearchOpen;
        public uint OwnedResearchSessionVersion => _workspace != null ? _workspace.ResearchSessionVersion : 0;
        public ulong CollectedGmu => _workspace != null ? _workspace.CollectedGmu : 0;
        public Transform PortalTransform => _homeBindings != null && _homeBindings.ResearchVolume != null ? _homeBindings.ResearchVolume.transform : null;
        public string InteractionLabel => "F 研究法术";

        public bool ConfigureHomeWorld(OnlineHomeWorldBindings bindings)
        {
            if (bindings == null || !bindings.gameObject.scene.IsValid() || !bindings.gameObject.scene.isLoaded || !bindings.TryValidate(out _)) return false;
            if (_homeBindings != null) return _homeBindings == bindings;
            if (_workspace != null && !_workspace.IsPeacefulPractice) return false;
            _homeBindings = bindings; Cache();
            if (_workspace != null && !_workspace.ConfigureHomeResearchAccess(HasAuthorityResearchAccess)) return false;
            BindHomeResearchPresentation(); return true;
        }
        private bool HasAuthorityResearchAccess() => _authority && CanResearch;
        private void BindHomeResearchPresentation()
        {
            if (_authority || !_clientOwner || !_connected || _homeBindings == null || !isActiveAndEnabled) return;
            if (_homePortalInput == null) _homePortalInput = GetComponent<PortalInteractInput>();
            if (_homePortalInput == null) _homePortalInput = gameObject.AddComponent<PortalInteractInput>();
            _homePortalInput.enabled = true;
            if (_homePresentationBound) return;
            _homePresentationBound = true; OnlineHomeResearchPresentation.Bind(GetInstanceID(), this); PortalInteractionRegistry.Register(this);
        }
        private void ClearHomeResearchPresentation()
        {
            PortalInteractionRegistry.Unregister(this); OnlineHomeResearchPresentation.Clear(GetInstanceID()); _homePresentationBound = false;
            if (_homePortalInput != null) _homePortalInput.enabled = false;
        }
        public bool CanInteract(Collider actor)
            => !_authority && _clientOwner && CanResearch && !_suppressed && _focused && !_applicationPaused
                && Time.timeScale > 0 && _wizard.CharacterController != null && actor == _wizard.CharacterController;
        public bool Interact(Collider actor)
        {
            if (!CanInteract(actor)) return false;
            EventBus<OnlineHomeResearchRequestedEvent>.Publish(new OnlineHomeResearchRequestedEvent
            { ActorId = _actor, ConnectionEpoch = _epoch, WorldSceneHandle = WorldSceneHandle });
            return true;
        }
        public void CloseResearchSession() => _workspace?.ForceCloseResearch();
        public void RefreshHomeResearchAccess()
        { if (_authority && _workspace != null && _workspace.ResearchOpen && !CanResearch) _workspace.ForceCloseResearch(); }
        public bool TryRestorePracticeContinuity(OnlineWandResearchStateSnapshot snapshot, out string error)
        {
            error = "只有可信Home权威角色可恢复本次session连续性。";
            return _authority && _homeBindings != null && _workspace != null && _workspace.IsPeacefulPractice
                && _workspace.TryRestorePracticeContinuity(snapshot, out error);
        }
        public bool TryCapturePracticeContinuity(out OnlineWandResearchStateSnapshot snapshot, out string error)
        {
            snapshot = null; error = "Home本人退出仍有待完成的原动作。";
            return _authority && _homeBindings != null && _workspace != null && _workspace.IsPeacefulPractice
                && HasMemberExitFence && !_queued && !_timeline.IsActive && _caster != null && _caster.PendingAuthorityActions == 0
                && _workspace.TryCapturePracticeContinuity(out snapshot, out error);
        }
    }
}
