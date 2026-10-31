using Game.Character;
using UnityEngine;

namespace Game.Multiplayer
{
    /// <summary>
    /// 原 Cinemachine 的只读跟随目标。服务器校正的是 CharacterController 的物理位置，
    /// MovementPresentationSmoother 则让模型在有限时间内消化视觉位移；镜头必须使用同一个偏移，
    /// 否则角色看似平滑而背景仍随物理根跳动。独立 proxy 只传递表现姿态，不参与碰撞、输入或网络权威。
    /// </summary>
    [DefaultExecutionOrder(75)]
    public sealed class OnlineCameraTarget : MonoBehaviour
    {
        private PlayerControllerBase _player;
        private MovementPresentationSmoother _smoother;

        public void Bind(PlayerControllerBase player)
        {
            _player = player;
            _smoother = player != null ? player.GetComponent<MovementPresentationSmoother>() : null;
            LateUpdate();
        }

        private void LateUpdate()
        {
            if (_player == null) return;
            var source = _player.CameraRoot != null ? _player.CameraRoot : _player.transform;
            // Player 先更新鼠标轨道，Smoother(50)再衰减校正，最后由 CinemachineBrain(100)求镜头。
            // 只偏移 proxy；改 CameraRoot/CC 会反过来污染输入与服务器校正，产生反馈抖动。
            Vector3 offset = _smoother != null ? _smoother.VisualPosition - _player.transform.position : Vector3.zero;
            // SetPositionAndRotation 一次设置 world-space 姿态；这里不是修改角色的 localPosition。
            // proxy 的父级仅负责随在线世界释放，真实相机距离/FOV/避障继续由原 Cinemachine 配置决定。
            transform.SetPositionAndRotation(source.position + offset, source.rotation);
        }
    }
}
