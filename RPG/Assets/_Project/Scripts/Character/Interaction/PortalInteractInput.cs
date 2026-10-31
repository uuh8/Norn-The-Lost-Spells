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
        private void OnEnable()
        {
            Current = this;
            // Hub 在联机期间停用，可能错过窗口的 Focus 回调；恢复时重采真实状态，不能沿用停用前的 false。
            // 同时仍等 F 释放，避免按住键跨 Scene 或恢复窗口时直接触发另一道门。
            _focused = Application.isFocused; _waitRelease = true; _interact.Enable();
        }
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
