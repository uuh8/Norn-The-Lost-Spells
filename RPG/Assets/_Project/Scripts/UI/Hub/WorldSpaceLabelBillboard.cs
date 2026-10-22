using UnityEngine;
using Unity.Profiling;

namespace Game.UI
{
    /// <summary>世界文字的local XY对齐屏幕XY；不让TextMesh背面镜像随观察角度出现。</summary>
    [DefaultExecutionOrder(10000)]
    public sealed class WorldSpaceLabelBillboard : MonoBehaviour
    {
        [SerializeField] private Camera _camera;
        private static readonly ProfilerMarker Marker = new ProfilerMarker("Norn.Hub.LabelBillboard");
        private void LateUpdate()
        {
            using (Marker.Auto())
            {
                // 在玩家和Cinemachine更新之后采样最终镜头；缓存失效时才重新查询，无每帧分配。
                if (_camera == null || !_camera.isActiveAndEnabled) _camera = Camera.main;
                if (_camera != null) transform.rotation = _camera.transform.rotation;
            }
        }
    }
}
