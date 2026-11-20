using UnityEngine;
using Unity.Profiling;

namespace Game.UI
{
    /// <summary>
    /// 共用的纯视觉Billboard：文字默认把local XY对齐屏幕XY；薄平面门可显式朝Camera位置。
    /// 组件只写自身视觉Transform，不查Actor、库存、交互许可或转场Owner。
    /// </summary>
    [DefaultExecutionOrder(10000)]
    public sealed class WorldSpaceLabelBillboard : MonoBehaviour
    {
        [SerializeField] private Camera _camera;
        [SerializeField] private bool _faceCameraPosition;
        private static readonly ProfilerMarker Marker = new ProfilerMarker("Norn.Hub.LabelBillboard");

        private void Awake()
        {
#if UNITY_SERVER && !UNITY_EDITOR
            // 共享WorldPrefab也会进DS；服务器只保留门的作者Collider，不查询镜头或旋转视觉。
            enabled = false;
#endif
        }

        /// <summary>
        /// 只在导出或动态视觉实例创建时配置。默认false保留原世界文字的screen-aligned行为；
        /// 门需要true，避免镜头没正对门中心时仍拿Camera.forward当作门到镜头的方向。
        /// </summary>
        public void ConfigureCameraFacing(bool faceCameraPosition) => _faceCameraPosition = faceCameraPosition;

        private void LateUpdate()
        {
            using (Marker.Auto())
            {
                // 在玩家和Cinemachine更新之后采样最终镜头；缓存失效时才重新查询，无每帧分配。
                if (_camera == null || !_camera.isActiveAndEnabled) _camera = Camera.main;
                if (_camera == null) return;
                if (!_faceCameraPosition) { transform.rotation = _camera.transform.rotation; return; }
                Vector3 direction = _camera.transform.position - transform.position;
                // 相机恰好穿过平面中心时没有唯一朝向，保留上一帧，而不是向LookRotation传零向量。
                if (direction.sqrMagnitude <= .0001f) return;
                Vector3 up = _camera.transform.up;
                if (Vector3.Cross(direction, up).sqrMagnitude <= .000001f) up = _camera.transform.forward;
                transform.rotation = Quaternion.LookRotation(direction, up);
            }
        }
    }
}
