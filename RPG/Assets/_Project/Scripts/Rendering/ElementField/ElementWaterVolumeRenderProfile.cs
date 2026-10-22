using UnityEngine;

namespace Game.Rendering
{
    /// <summary>
    /// P6-B 稀疏世界水体的 Presentation 参数。
    ///
    /// 这里不保存 Water Amount、Flow 或 Reaction；那些属于 Game.ElementField Gameplay。
    /// Profile 只决定同一份只读 Cell 数据怎样重建成视觉体积，因此美术调参不会改变 Wet、
    /// 灭火或水量守恒。运行时由 CreateSettings 创建一次不可变 Snapshot。
    /// </summary>
    [CreateAssetMenu(
        fileName = "ElementWaterVolumeRenderProfile",
        menuName = "Game/Element Field/Water Volume Render Profile")]
    public sealed class ElementWaterVolumeRenderProfile : ScriptableObject
    {
        [Header("Surface Sampling")]
        [Tooltip("每个 Gameplay Cell 沿单轴采样次数。2 比 1 更平滑，但 Scalar/Dual 数量近似按三次方增长。")]
        [SerializeField, Range(1, 2)] private int _samplesPerCell = 2;

        [Header("Supported Water")]
        [Tooltip("有底部支撑的水体边缘圆角，单位为 Cell。它只改变 Presentation 轮廓。")]
        [SerializeField, Range(0.01f, 0.45f)] private float _supportedCornerRadius = 0.12f;
        [Tooltip("极低 Amount 的 Supported Water 最小可见高度，单位为 Cell。")]
        [SerializeField, Range(0.01f, 1f)] private float _minimumSupportedHeight = 0.30f;

        [Header("Airborne Water")]
        [Tooltip("Unsupported Water 开放方向的最小可见半径，单位为 Cell。")]
        [SerializeField, Range(0.05f, 0.49f)] private float _minimumAirborneRadius = 0.30f;
        [Tooltip("满 Amount Unsupported Water 开放方向的最大半径，单位为 Cell。")]
        [SerializeField, Range(0.05f, 0.75f)] private float _maximumAirborneRadius = 0.48f;

        [Header("Volume Union")]
        [Tooltip("相邻隐式 Primitive 的平滑合并距离，单位为 Cell。")]
        [SerializeField, Range(0.001f, 0.49f)] private float _smoothUnionRadius = 0.16f;

        [Header("Visual Density Filter")]
        [Tooltip("仅用于 Presentation 的最低可视密度；低于它的离散残余水量不会生成视觉碎点。")]
        [SerializeField, Range(0.003921569f, 0.25f)]
        private float _visualDensityThreshold = 0.011764706f;
        [Tooltip("有竖直支撑水体在 X/Z 平面使用 [1,2,1] 卷积时的混合强度。")]
        [SerializeField, Range(0f, 1f)]
        private float _supportedSmoothingStrength = 0.90f;
        [Tooltip("空中水体在 X/Y/Z 三轴使用 [1,2,1] 卷积时的混合强度。")]
        [SerializeField, Range(0f, 1f)]
        private float _airborneSmoothingStrength = 0.90f;
        [Tooltip("Supported Primitive 底部压入支撑面的深度，单位为 Cell；只帮助有限采样捕获顶面，不抬高 Water Top，也不回写 Gameplay Cell。")]
        [SerializeField, Range(0.001f, 0.10f)]
        private float _supportedFloorCaptureDepth = 0.08f;

        public WaterVolumeMeshingSettings CreateSettings()
        {
            return new WaterVolumeMeshingSettings(
                _samplesPerCell,
                _supportedCornerRadius,
                _minimumSupportedHeight,
                _minimumAirborneRadius,
                _maximumAirborneRadius,
                _smoothUnionRadius,
                _visualDensityThreshold,
                _supportedSmoothingStrength,
                _airborneSmoothingStrength,
                _supportedFloorCaptureDepth);
        }

        private void OnValidate()
        {
            _samplesPerCell = Mathf.Clamp(_samplesPerCell, 1, 2);
            _supportedCornerRadius = Mathf.Clamp(_supportedCornerRadius, 0.01f, 0.45f);
            _minimumSupportedHeight = Mathf.Clamp(_minimumSupportedHeight, 0.01f, 1f);
            _minimumAirborneRadius = Mathf.Clamp(_minimumAirborneRadius, 0.05f, 0.49f);
            _maximumAirborneRadius = Mathf.Clamp(
                _maximumAirborneRadius,
                _minimumAirborneRadius,
                0.75f);
            _smoothUnionRadius = Mathf.Clamp(_smoothUnionRadius, 0.001f, 0.49f);
            _visualDensityThreshold = Mathf.Clamp(
                _visualDensityThreshold,
                1f / 255f,
                0.25f);
            _supportedSmoothingStrength = Mathf.Clamp01(_supportedSmoothingStrength);
            _airborneSmoothingStrength = Mathf.Clamp01(_airborneSmoothingStrength);
            _supportedFloorCaptureDepth = Mathf.Clamp(
                _supportedFloorCaptureDepth,
                0.001f,
                0.10f);
        }
    }
}
