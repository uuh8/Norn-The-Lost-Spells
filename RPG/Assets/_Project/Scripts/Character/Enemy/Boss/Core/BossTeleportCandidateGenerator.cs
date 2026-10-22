using UnityEngine;

namespace Game.Character
{
    /// <summary>
    /// 只负责在目标周围生成环带候选点，不查询 NavMesh、Collider 或 Scene。
    /// 把“数学上想去哪里”和“Unity 世界中能不能站”分开后，前者可以稳定复现并用 EditMode Test 验证。
    /// </summary>
    public static class BossTeleportCandidateGenerator
    {
        // 黄金角可以让固定数量的方向较均匀地铺开，避免候选点集中在四个坐标轴方向。
        private const float GoldenAngleDegrees = 137.50777f;

        public static int Generate(
            Vector3 center,
            float minRadius,
            float maxRadius,
            float angleOffsetDegrees,
            Vector3[] output)
        {
            if (output == null || output.Length == 0)
            {
                return 0;
            }

            float safeMin = Mathf.Max(0f, minRadius);
            float safeMax = Mathf.Max(safeMin, maxRadius);
            float minRadiusSquared = safeMin * safeMin;
            float maxRadiusSquared = safeMax * safeMax;

            for (int i = 0; i < output.Length; i++)
            {
                // 在半径平方上均匀采样，能让环带“单位面积”的候选密度更接近均匀；
                // 若直接线性插值半径，外圈面积更大却仍只得到相同数量的点。
                float ratio = (i + 0.5f) / output.Length;
                float radius = Mathf.Sqrt(
                    Mathf.Lerp(minRadiusSquared, maxRadiusSquared, ratio));
                float angleRadians =
                    (angleOffsetDegrees + i * GoldenAngleDegrees) *
                    Mathf.Deg2Rad;

                output[i] = new Vector3(
                    center.x + Mathf.Cos(angleRadians) * radius,
                    center.y,
                    center.z + Mathf.Sin(angleRadians) * radius);
            }

            return output.Length;
        }
    }
}
