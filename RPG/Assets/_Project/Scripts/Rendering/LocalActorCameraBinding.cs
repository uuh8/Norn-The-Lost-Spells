using Game.Core;
using Unity.Cinemachine;
using UnityEngine;

namespace Game.Rendering
{
    /// <summary>把同源 Cinemachine 接到本机只读表现目标，不参与预测、碰撞或角色输入。</summary>
    [DefaultExecutionOrder(-1100)]
    public sealed class LocalActorCameraBinding : MonoBehaviour
    {
        [SerializeField] private Camera _camera;
        [SerializeField] private CinemachineCamera _rig;
        private CinemachineBrain _brain;
        private AudioListener _listener;

#if UNITY_SERVER && !UNITY_EDITOR
        // 同一 Scene 也可能用于 Dedicated 构建；先于相机组件启用，避免 DS 启动表现生命周期。
        private void Awake() => gameObject.SetActive(false);
#endif
        private void OnEnable()
        {
            if (_camera != null)
            {
                _brain = _camera.GetComponent<CinemachineBrain>();
                _listener = _camera.GetComponent<AudioListener>();
            }
            EventBus<LocalActorPresentationEvent>.Subscribe(OnActor);
            // Actor 和相机树可按任意顺序加载；晚订阅读取缓存，不等待下一次重连。
            Bind(LocalActorPresentation.Actor, LocalActorPresentation.CameraTarget);
        }

        private void OnDisable()
        {
            EventBus<LocalActorPresentationEvent>.Unsubscribe(OnActor);
            Bind(null, null);
        }

        private void OnActor(LocalActorPresentationEvent state) => Bind(state.Actor, state.CameraTarget);

        public void Bind(GameObject actor, Transform target)
        {
            bool ready = actor != null && target != null && _camera != null && _rig != null && _brain != null;
            if (_rig != null)
            {
                var tracking = _rig.Target;
                tracking.TrackingTarget = ready ? target : null;
                tracking.LookAtTarget = null;
                _rig.Target = tracking;
                _rig.enabled = ready;
            }
            if (_brain != null) _brain.enabled = ready;
            if (_camera != null) _camera.enabled = ready;
            if (_listener != null) _listener.enabled = ready;
            // 无 Actor 时不抢据点 Camera；有效注册后准心射线与 Cinemachine 实际输出使用同一 Camera。
            if (ready) LocalActorCameraPresentation.Bind(GetInstanceID(), actor, _camera);
            else LocalActorCameraPresentation.Clear(GetInstanceID());
        }
    }
}
