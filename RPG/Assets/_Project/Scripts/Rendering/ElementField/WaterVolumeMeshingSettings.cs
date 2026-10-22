using UnityEngine;

namespace Game.Rendering
{
    /// <summary>
    /// 隐式水体重建使用的不可变参数快照。
    ///
    /// Runtime 算法读取普通值类型，而不是在每个 Sample 中反复访问 ScriptableObject。
    /// 这既让 Pure Geometry 更容易测试，也避免 Profile 在一次 Chunk Rebuild 中途被修改，
    /// 导致同一个 Mesh 的前半部分和后半部分使用不同参数。
    /// 所有长度均使用“Cell 为 1”的 Global Cell Space，不直接绑定具体世界米制 CellSize。
    /// </summary>
    public readonly struct WaterVolumeMeshingSettings
    {
        public WaterVolumeMeshingSettings(
            int samplesPerCell,
            float supportedCornerRadius,
            float minimumSupportedHeight,
            float minimumAirborneRadius,
            float maximumAirborneRadius,
            float smoothUnionRadius,
            float visualDensityThreshold = 3f / 255f,
            float supportedSmoothingStrength = 0.90f,
            float airborneSmoothingStrength = 0.90f,
            float supportedFloorCaptureDepth = 0.08f)
        {
            SamplesPerCell = Mathf.Clamp(samplesPerCell, 1, 2);
            SupportedCornerRadius = Mathf.Clamp(supportedCornerRadius, 0.01f, 0.45f);
            MinimumSupportedHeight = Mathf.Clamp(minimumSupportedHeight, 0.01f, 1f);
            MinimumAirborneRadius = Mathf.Clamp(minimumAirborneRadius, 0.05f, 0.49f);
            MaximumAirborneRadius = Mathf.Clamp(
                maximumAirborneRadius,
                MinimumAirborneRadius,
                0.75f);
            SmoothUnionRadius = Mathf.Clamp(smoothUnionRadius, 0.001f, 0.49f);
            VisualDensityThreshold = Mathf.Clamp(
                visualDensityThreshold,
                1f / 255f,
                0.25f);
            SupportedSmoothingStrength = Mathf.Clamp01(supportedSmoothingStrength);
            AirborneSmoothingStrength = Mathf.Clamp01(airborneSmoothingStrength);
            SupportedFloorCaptureDepth = Mathf.Clamp(
                supportedFloorCaptureDepth,
                0.001f,
                0.10f);
        }

        public int SamplesPerCell { get; }
        public float SupportedCornerRadius { get; }
        public float MinimumSupportedHeight { get; }
        public float MinimumAirborneRadius { get; }
        public float MaximumAirborneRadius { get; }
        public float SmoothUnionRadius { get; }
        public float VisualDensityThreshold { get; }
        public float SupportedSmoothingStrength { get; }
        public float AirborneSmoothingStrength { get; }
        public float SupportedFloorCaptureDepth { get; }
    }
}
