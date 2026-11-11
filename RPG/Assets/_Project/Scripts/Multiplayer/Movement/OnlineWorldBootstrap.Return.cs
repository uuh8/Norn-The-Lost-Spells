using Game.Character;
using Game.Core;
using Game.Networking;
using Mirror;
using UnityEngine;

namespace Game.Multiplayer
{
    public sealed partial class OnlineWorldBootstrap
    {
        private OnlineDungeonReturnBindings _returnDefinition, _returnGate;
        private OnlineReturnPortalInteraction _returnInteraction;
        private GameObject _dynamicReturnRoot;
        private bool _returnAvailable;
        private uint _returnReplicaRevision;
        private OnlineDungeonReturnBindings ReturnDefinition
        {
            get
            {
                if (_returnDefinition == null && _worldContent != null)
                    _returnDefinition = _worldContent.GetComponentInChildren<OnlineDungeonReturnBindings>(true);
                return _returnDefinition;
            }
        }
        private void EnsureAuthorityReturnPortal()
        {
            if (!IsAuthority || _returnAvailable || _dungeonRuntime == null || !_dungeonRuntime.IsStageCleared) return;
            var data = ReturnDefinition;
            if (data == null || !data.TryValidate(out _) || data.EncounterIndex != _dungeonDefinition.EncounterCount - 1) return;
            if (data.DynamicPlacement)
            {
                Transform actor = null;
                for (int i = 0; i < OnlineActorLimits.PlayerCount; i++)
                    if (Registry[i].Targetable && Registry[i].Player != null) { actor = Registry[i].Player.transform; break; }
                if (actor == null) return;
                var pose = OnlineReturnPortalInteraction.ResolveDynamicPose(actor);
                if (!CreateDynamicReturnGate(pose)) return;
            }
            else _returnGate = data;
            _returnGate.PortalCollider.enabled = true; _returnGate.SetPresentationAvailable(true);
            _returnGate.RefreshVisualPlacement(); _returnAvailable = true; Physics.SyncTransforms();
        }
        private bool CreateDynamicReturnGate(Pose pose)
        {
            var data = ReturnDefinition;
            if (data == null || data.InactiveTemplateRoot == null || _dynamicReturnRoot != null) return _returnGate != null;
            _dynamicReturnRoot = Instantiate(data.InactiveTemplateRoot, _worldContent.transform);
            _dynamicReturnRoot.name = "原P8返回门"; _dynamicReturnRoot.transform.SetPositionAndRotation(pose.position, pose.rotation);
            _returnGate = _dynamicReturnRoot.GetComponent<OnlineDungeonReturnBindings>();
            if (_returnGate == null) return false;
            _dynamicReturnRoot.SetActive(true); return true;
        }
        private void AddReturnPortalProgress(ref OnlineDungeonProgress progress)
        {
            if (!_returnAvailable || _returnGate == null || progress.Phase != OnlineDungeonPhase.StageCleared) return;
            // Gate生成是独立离散事实；不重跑遭遇、不造一个完成事件。只有一次追加修订，重连沿同Pose补发。
            if (progress.Revision == uint.MaxValue) return;
            progress.Revision++; progress.HasReturnPortal = true;
            progress.ReturnPortalPosition = _returnGate.PortalTransform.position; progress.ReturnPortalRotation = _returnGate.PortalTransform.rotation;
        }
        private void ApplyReturnPortalProgress(in OnlineDungeonProgress progress)
        {
            if (IsAuthority || !progress.HasReturnPortal || progress.Phase != OnlineDungeonPhase.StageCleared
                || progress.Revision <= _returnReplicaRevision || _dungeonRuntime == null
                || !_dungeonRuntime.TryCapture(out var current) || current.Phase != OnlineDungeonPhase.StageCleared
                || current.EncounterIndexPlusOne != progress.EncounterIndexPlusOne || current.Revision > progress.Revision) return;
            var data = ReturnDefinition;
            if (data == null || !data.TryValidate(out _) || data.EncounterIndex != (int)progress.EncounterIndexPlusOne - 1) return;
            if (data.DynamicPlacement)
            { if (!CreateDynamicReturnGate(new Pose(progress.ReturnPortalPosition, progress.ReturnPortalRotation))) return; }
            else
            {
                // 固定门仍是原几何；可信事实也不能悄悄把原固定门平移到别处。
                if ((data.PortalTransform.position - progress.ReturnPortalPosition).sqrMagnitude > .0001f
                    || Quaternion.Angle(data.PortalTransform.rotation, progress.ReturnPortalRotation) > .01f) return;
                _returnGate = data;
            }
            _returnGate.PortalCollider.enabled = true; _returnGate.SetPresentationAvailable(true); _returnGate.RefreshVisualPlacement();
            _returnAvailable = true; _returnReplicaRevision = progress.Revision; Physics.SyncTransforms(); BindLocalReturnInteraction();
        }
        private void BindLocalReturnInteraction()
        {
            if (IsAuthority || LocalPlayer == null || _auth == null || !_auth.ClientAuthenticated || _auth.ClientIdentity == null) return;
            Collider portal = null;
            if (_dungeonDefinition != null && _dungeonDefinition.IsPeacefulHome) portal = HomeBindings != null ? HomeBindings.ReturnPortalCollider : null;
            else if (_returnAvailable && _returnGate != null) portal = _returnGate.PortalCollider;
            if (portal == null) return;
            if (_returnInteraction == null || _returnInteraction.gameObject != portal.gameObject)
            {
                if (_returnInteraction != null) ReleaseReturnObject(_returnInteraction);
                _returnInteraction = portal.gameObject.AddComponent<OnlineReturnPortalInteraction>();
            }
            var portalInput = LocalPlayer.GetComponent<PortalInteractInput>();
            if (portalInput == null) portalInput = LocalPlayer.gameObject.AddComponent<PortalInteractInput>();
            portalInput.enabled = true; // Prediction配置会禁用单机入口；只为当前认证本机门恢复同一F组件。
            var identity = _auth.ClientIdentity;
            _returnInteraction.Configure(portal, LocalPlayer.CharacterController, identity.ActorId, identity.ConnectionEpoch,
                (int)gameObject.scene.handle, CanUseLocalReturnPortal);
        }
        public bool CanUseLocalReturnPortal() => !IsAuthority && CanAdvance && !LocalInputSuppressed && LocalPlayer != null
            && CanUseReturnPortal(_localActor);
        internal bool CanUseReturnPortal(ulong actor)
        {
            if (!CanAdvance || Registry == null || actor < 1 || actor > 4) return false;
            var slot = Registry[(int)actor - 1];
            // SimulationReady是DS首输入门；Client本机Slot不写此位，只读当前连接与安装的生命事实。
            if (!slot.Connected || IsAuthority && !slot.SimulationReady || slot.Player == null || slot.Health == null || !slot.Health.IsAlive) return false;
            var collider = slot.Player.CharacterController;
            return _dungeonDefinition != null && _dungeonDefinition.IsPeacefulHome
                ? HomeBindings != null && HomeBindings.ContainsReturnPortal(collider)
                : _returnAvailable && _returnGate != null && _dungeonRuntime != null && _dungeonRuntime.IsStageCleared && _returnGate.Contains(collider);
        }
        internal bool TryCaptureSuccessfulExtraction(ulong actor, out ulong collected, out uint enemies)
        {
            collected = 0; enemies = 0;
            if (!IsAuthority || _dungeonDefinition == null || _dungeonDefinition.IsPeacefulHome || !CanUseReturnPortal(actor)) return false;
            collected = Registry[(int)actor - 1].Cast != null ? Registry[(int)actor - 1].Cast.CollectedGmu : 0;
            enemies = _dungeonRuntime.DefeatedEnemies; return true;
        }
        private void ClearReturnPortalRuntime()
        {
            if (_returnInteraction != null) ReleaseReturnObject(_returnInteraction);
            if (_dynamicReturnRoot != null) ReleaseReturnObject(_dynamicReturnRoot);
            _returnInteraction = null; _dynamicReturnRoot = null; _returnDefinition = _returnGate = null;
            _returnAvailable = false; _returnReplicaRevision = 0;
        }
        private static void ReleaseReturnObject(UnityEngine.Object value)
        { if (value == null) return; if (Application.isPlaying) Destroy(value); else DestroyImmediate(value); }
    }
}
