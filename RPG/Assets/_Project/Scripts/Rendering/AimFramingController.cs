using UnityEngine;
using Unity.Cinemachine;
using Game.Core;

namespace Game.Rendering
{
    /// <summary>
    /// 蓄力瞄准取景（仿原神弓箭手重击）：订阅 AimStateChangedEvent，瞄准时把相机平滑偏移，
    /// 使角色滑到画面左下、让出准心所在的画面中心区域；退出瞄准时平滑归位。
    ///
    /// 实现靠 Cinemachine 的 CinemachineCameraOffset 扩展（相机空间偏移、PreserveComposition=false →
    /// 相机连同瞄准点一起平移，不重新对准角色，于是角色在屏幕上向偏移反方向滑动）：
    ///   Offset.X > 0（相机右移）→ 角色在屏幕上左移；Offset.Y > 0（相机上移）→ 角色在屏幕上下移。
    /// 故 _aimOffset 取 (+X, +Y) 即把角色推向左下。
    ///
    /// 经 EventBus 与 gameplay 解耦：本组件在 Game.Rendering（表现层），只订阅事件，不引用 Game.Character。
    /// 与 CrosshairUI 是同一事件的两个独立订阅者。
    /// </summary>
    [RequireComponent(typeof(CinemachineCameraOffset))]
    public class AimFramingController : MonoBehaviour
    {
        [Header("瞄准取景偏移 (相机空间，单位：世界米)")]
        [Tooltip("+X 相机右移→角色左移；+Y 相机上移→角色下移。取 (+X,+Y) 把角色推到左下。具体数值随相机距离/FOV 调")]
        [SerializeField] private Vector3 _aimOffset = new Vector3(1.0f, 0.6f, 0f);

        [Tooltip("偏移进入/归位的平滑速度（越大越快贴近目标）")]
        [SerializeField] private float _transitionSpeed = 8f;

        private CinemachineCameraOffset _cameraOffset;
        private Vector3 _targetOffset; // 当前目标偏移：瞄准=_aimOffset，否则=zero

        private void Awake()
        {
            _cameraOffset = GetComponent<CinemachineCameraOffset>();
            _targetOffset = Vector3.zero;
            if (_cameraOffset != null) _cameraOffset.Offset = Vector3.zero; // 起始不偏移
        }

        private void OnEnable()  => EventBus<AimStateChangedEvent>.Subscribe(OnAimStateChanged);
        private void OnDisable() => EventBus<AimStateChangedEvent>.Unsubscribe(OnAimStateChanged);

        private void OnAimStateChanged(AimStateChangedEvent e)
        {
            // 只切换"目标"，实际偏移在 Update 里平滑逼近，避免瞬移
            _targetOffset = e.Active ? _aimOffset : Vector3.zero;
        }

        private void Update()
        {
            if (_cameraOffset == null) return;
            // 与项目转向同款的简单插值（speed * deltaTime）。Cinemachine Brain 在 LateUpdate 处理，
            // 这里在 Update 写好 Offset，本帧即被采用。
            _cameraOffset.Offset = Vector3.Lerp(
                _cameraOffset.Offset, _targetOffset, _transitionSpeed * Time.deltaTime);
        }
    }
}
