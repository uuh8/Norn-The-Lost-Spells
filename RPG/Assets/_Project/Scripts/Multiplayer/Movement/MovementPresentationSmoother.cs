using Game.Character;
using UnityEngine;

namespace Game.Multiplayer
{
    /// <summary>只改Animator模型子物体，不改CC根/网络状态；100ms后严格归零，Camera共享同一有限偏移。</summary>
    [DefaultExecutionOrder(50)]
    public sealed class MovementPresentationSmoother : MonoBehaviour
    {
        private Transform _visual;
        private Vector3 _baseLocal;
        private MovementCorrectionBlend _blend;
        public Vector3 VisualPosition => transform.position + _blend.Offset;
        public void Bind(PlayerControllerBase player)
        {
            _visual = player.Animator != null ? player.Animator.transform : null;
            if (_visual == transform) _visual = null;
            if (_visual != null) _baseLocal = _visual.localPosition;
        }
        public void PreserveAfterCorrection(Vector3 previousVisualPosition)
        { _blend.Begin(previousVisualPosition - transform.position); Apply(); }
        private void LateUpdate() { _blend.Advance(Time.unscaledDeltaTime); Apply(); }
        private void Apply()
        { if (_visual != null) _visual.position = transform.TransformPoint(_baseLocal) + _blend.Offset; }
    }
}
