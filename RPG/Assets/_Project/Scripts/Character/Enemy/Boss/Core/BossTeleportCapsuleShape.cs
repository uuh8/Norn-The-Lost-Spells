using UnityEngine;

namespace Game.Character
{
    /// <summary>
    /// CharacterController 胶囊的值快照。Teleport 在禁用 Controller 前先用相同几何体检查目标空间，
    /// 避免“NavMesh 上有点”却没有足够体积容纳 Boss 的情况。
    /// </summary>
    public readonly struct BossTeleportCapsuleShape
    {
        private readonly Vector3 _localCenter;
        private readonly float _radius;
        private readonly float _height;
        private readonly float _skinWidth;
        private readonly Vector3 _lossyScale;

        public BossTeleportCapsuleShape(
            Vector3 localCenter,
            float radius,
            float height,
            float skinWidth,
            Vector3 lossyScale)
        {
            _localCenter = localCenter;
            _radius = radius;
            _height = height;
            _skinWidth = skinWidth;
            _lossyScale = lossyScale;
        }

        public void BuildWorldCapsule(
            Vector3 rootPosition,
            out Vector3 bottom,
            out Vector3 top,
            out float clearanceRadius)
        {
            float horizontalScale = Mathf.Max(
                Mathf.Abs(_lossyScale.x),
                Mathf.Abs(_lossyScale.z));
            float verticalScale = Mathf.Abs(_lossyScale.y);
            float bodyRadius = Mathf.Max(0.01f, _radius * horizontalScale);
            float worldHeight = Mathf.Max(
                _height * verticalScale,
                bodyRadius * 2f);
            float halfCylinder = Mathf.Max(
                0f,
                worldHeight * 0.5f - bodyRadius);
            Vector3 worldCenter = rootPosition +
                                  Vector3.Scale(_localCenter, _lossyScale);

            bottom = worldCenter - Vector3.up * halfCylinder;
            top = worldCenter + Vector3.up * halfCylinder;

            // CharacterController 允许 Skin Width 内的轻微接触。检测体积同步内缩，
            // 否则胶囊底部刚好贴住 Terrain 时可能被 Physics 当作已经重叠。
            clearanceRadius = Mathf.Max(
                0.01f,
                bodyRadius - Mathf.Max(0f, _skinWidth) * horizontalScale);
        }
    }
}
