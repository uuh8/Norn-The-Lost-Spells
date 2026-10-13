using Game.Run;
using Game.Core;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Character
{
    /// <summary>一个玩家只有一个F门输入；按住键换Scene时先等释放，不给每扇门创建重复Action。</summary>
    public sealed class PortalInteractInput : MonoBehaviour
    {
        private InputAction _interact;
        private Collider _actor;
        private bool _waitRelease = true, _focused = true;
        internal static PortalInteractInput Current { get; private set; }
        public static bool HasTarget => Current != null && Current._actor != null
            && PortalInteractionRegistry.TryGetNearest(Current._actor, out _);
        private void Awake()
        { _actor = GetComponent<CharacterController>(); _interact = new InputAction("PortalInteract", InputActionType.Button, "<Keyboard>/f"); _interact.performed += OnInteract; }
        private void OnEnable() { Current = this; _waitRelease = true; _interact.Enable(); }
        private void OnDisable() { if (Current == this) Current = null; _interact.Disable(); _waitRelease = true; }
        private void OnDestroy() { _interact.performed -= OnInteract; _interact.Dispose(); }
        private void OnApplicationFocus(bool focus) { _focused = focus; _waitRelease = true; }
        private void Update()
        {
            if (Time.timeScale <= 0 || !_focused) { _waitRelease = true; return; }
            if (!_interact.IsPressed()) _waitRelease = false;
        }
        private void OnInteract(InputAction.CallbackContext _)
        {
            if (_waitRelease || !_focused || Time.timeScale <= 0 || (HubInteractInput.Current?.HasStation ?? false)) return;
            if (PortalInteractionRegistry.TryGetNearest(_actor, out var target))
            { _waitRelease = true; EventBus<PortalInteractionRequestedEvent>.Publish(new PortalInteractionRequestedEvent { Actor = _actor, PortalId = target.PortalTransform.gameObject.GetInstanceID() }); }
        }
    }
}
