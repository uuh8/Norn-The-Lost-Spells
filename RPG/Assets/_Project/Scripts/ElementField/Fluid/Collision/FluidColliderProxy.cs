using System.Runtime.InteropServices;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// CPU Settings 与 HLSL Swept Collision 共享的有界工作量 Contract。
    /// </summary>
    public static class FluidCollisionSweep
    {
        public const int MaxSamples = 32;
        public const int BisectionIterations = 6;
    }

    public enum FluidColliderShape : uint
    {
        OrientedBox = 0u,
        Sphere = 1u,
        Capsule = 2u,
    }

    /// <summary>
    /// C#/HLSL 共享的 64-byte Collider 值快照。前三行是只含旋转与平移的 World-to-Shape
    /// 仿射矩阵；尺寸已换算成 World meters，所以 GPU 无需理解 Unity Transform 或 Collider。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public readonly struct FluidColliderProxy
    {
        public readonly Vector4 WorldToShapeRow0;
        public readonly Vector4 WorldToShapeRow1;
        public readonly Vector4 WorldToShapeRow2;
        public readonly Vector3 Parameters;
        public readonly FluidColliderShape Shape;

        internal static FluidColliderProxy Disabled => new FluidColliderProxy(
            Vector4.zero,
            Vector4.zero,
            Vector4.zero,
            Vector3.zero,
            (FluidColliderShape)uint.MaxValue);

        private FluidColliderProxy(
            Vector4 worldToShapeRow0,
            Vector4 worldToShapeRow1,
            Vector4 worldToShapeRow2,
            Vector3 parameters,
            FluidColliderShape shape)
        {
            WorldToShapeRow0 = worldToShapeRow0;
            WorldToShapeRow1 = worldToShapeRow1;
            WorldToShapeRow2 = worldToShapeRow2;
            Parameters = parameters;
            Shape = shape;
        }

        /// <summary>
        /// 把 Unity Collider 转成 GPU 可读快照。矩阵使用刚体 Basis，非均匀 Scale 被烘焙到参数；
        /// 若父层级制造 Shear（旋转与非均匀缩放混合），这里只能采用 lossyScale 的 OBB 近似。
        /// </summary>
        public static bool TryCreate(Collider collider, out FluidColliderProxy proxy)
        {
            if (collider == null)
            {
                proxy = default;
                return false;
            }

            Transform colliderTransform = collider.transform;
            Vector3 absoluteScale = Absolute(colliderTransform.lossyScale);
            Quaternion shapeRotation = colliderTransform.rotation;
            Vector3 worldCenter;
            Vector3 parameters;
            FluidColliderShape shape;

            if (collider is BoxCollider box)
            {
                worldCenter = colliderTransform.TransformPoint(box.center);
                parameters = Vector3.Scale(box.size * 0.5f, absoluteScale);
                shape = FluidColliderShape.OrientedBox;
            }
            else if (collider is SphereCollider sphere)
            {
                worldCenter = colliderTransform.TransformPoint(sphere.center);
                float worldRadius = sphere.radius
                    * Mathf.Max(absoluteScale.x, Mathf.Max(absoluteScale.y, absoluteScale.z));
                parameters = new Vector3(worldRadius, 0f, 0f);
                shape = FluidColliderShape.Sphere;
            }
            else if (collider is CapsuleCollider capsule)
            {
                worldCenter = colliderTransform.TransformPoint(capsule.center);
                Vector3 localAxis = GetCapsuleLocalAxis(capsule.direction);
                shapeRotation = colliderTransform.rotation
                    * Quaternion.FromToRotation(Vector3.up, localAxis);

                float axisScale;
                float perpendicularScale;
                GetCapsuleScales(capsule.direction, absoluteScale, out axisScale, out perpendicularScale);
                float worldRadius = capsule.radius * perpendicularScale;
                float worldHalfHeight = capsule.height * axisScale * 0.5f;
                float worldHalfSegment = Mathf.Max(0f, worldHalfHeight - worldRadius);
                parameters = new Vector3(worldRadius, worldHalfSegment, 0f);
                shape = FluidColliderShape.Capsule;
            }
            else
            {
                proxy = default;
                return false;
            }

            if (!IsFinite(worldCenter) || !IsFinite(parameters)
                || parameters.x <= 0f
                || (shape == FluidColliderShape.OrientedBox
                    && (parameters.y <= 0f || parameters.z <= 0f)))
            {
                proxy = default;
                return false;
            }

            Matrix4x4 worldToShape = Matrix4x4.TRS(
                worldCenter,
                shapeRotation,
                Vector3.one).inverse;
            proxy = new FluidColliderProxy(
                worldToShape.GetRow(0),
                worldToShape.GetRow(1),
                worldToShape.GetRow(2),
                parameters,
                shape);
            return IsFinite(proxy.WorldToShapeRow0)
                && IsFinite(proxy.WorldToShapeRow1)
                && IsFinite(proxy.WorldToShapeRow2);
        }

        /// <summary>
        /// CPU Reference 只用于 Authoring 验证与 EditMode Test；Runtime 粒子不会逐粒子调用它。
        /// </summary>
        public Vector3 TransformWorldPoint(Vector3 worldPoint)
        {
            Vector4 homogeneous = new Vector4(worldPoint.x, worldPoint.y, worldPoint.z, 1f);
            return new Vector3(
                Vector4.Dot(WorldToShapeRow0, homogeneous),
                Vector4.Dot(WorldToShapeRow1, homogeneous),
                Vector4.Dot(WorldToShapeRow2, homogeneous));
        }

        private static Vector3 Absolute(Vector3 value)
        {
            return new Vector3(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));
        }

        private static Vector3 GetCapsuleLocalAxis(int direction)
        {
            if (direction == 0)
                return Vector3.right;
            if (direction == 2)
                return Vector3.forward;
            return Vector3.up;
        }

        private static void GetCapsuleScales(
            int direction,
            Vector3 scale,
            out float axisScale,
            out float perpendicularScale)
        {
            if (direction == 0)
            {
                axisScale = scale.x;
                perpendicularScale = Mathf.Max(scale.y, scale.z);
            }
            else if (direction == 2)
            {
                axisScale = scale.z;
                perpendicularScale = Mathf.Max(scale.x, scale.y);
            }
            else
            {
                axisScale = scale.y;
                perpendicularScale = Mathf.Max(scale.x, scale.z);
            }
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsFinite(Vector4 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
