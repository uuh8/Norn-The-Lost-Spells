using Game.Character;
using UnityEngine;

namespace Game.Multiplayer
{
    /// <summary>本机表现跟随既有CameraRoot；物理校正不回滚鼠标轨道，Camera不拥有角色位置。</summary>
    [DefaultExecutionOrder(100)]
    public sealed class OnlineOrbitCamera : MonoBehaviour
    {
        private PlayerControllerBase _player;
        private MovementPresentationSmoother _smoother;
        public void Bind(PlayerControllerBase player) { _player = player; _smoother = player.GetComponent<MovementPresentationSmoother>(); }
        private void LateUpdate()
        {
            if (_player == null) return;
            var root = _player.CameraRoot;
            Quaternion rotation = root != null ? root.rotation : Quaternion.Euler(15, _player.transform.eulerAngles.y, 0);
            Vector3 pivot = (_smoother != null ? _smoother.VisualPosition : _player.transform.position) + Vector3.up * 1.4f;
            Vector3 direction = rotation * Vector3.back;
            float distance = 4.5f;
            // 当前角色自身可能被查询命中；从模型外开始并只缩短明确的世界遮挡距离。
            if (Physics.SphereCast(pivot + direction * .5f, .15f, direction, out var hit, 4, ~0, QueryTriggerInteraction.Ignore) &&
                !hit.collider.transform.IsChildOf(_player.transform)) distance = Mathf.Max(.6f, hit.distance + .4f);
            transform.SetPositionAndRotation(pivot + direction * distance, rotation);
        }
    }
}
