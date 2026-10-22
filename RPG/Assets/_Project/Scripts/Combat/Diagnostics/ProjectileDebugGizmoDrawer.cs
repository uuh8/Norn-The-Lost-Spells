using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Game.Combat
{
    public class ProjectileDebugGizmoDrawer : MonoBehaviour
    {
        [Header("Display Categories")]
        [SerializeField] private bool _drawVelocity = true;
        [SerializeField] private bool _drawGravity = true;
        [SerializeField] private bool _drawHoming = true;
        [SerializeField] private bool _drawOrbit = true;
        [SerializeField] private bool _drawCollision = true;

        [Header("Display Scope")]
        [SerializeField] private bool _onlyDrawSelected;
        [SerializeField, Min(0.01f)] private float _vectorScale = 0.15f;
        [SerializeField, Min(0.05f)] private float _collisionHistorySeconds = 0.5f;
        [SerializeField, Range(8, 64)] private int _orbitCircleSegments = 24;

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (!enabled || !gameObject.activeInHierarchy)
                return;

            Camera camera = Camera.current;
            if (camera == null || camera.cameraType != CameraType.SceneView)
                return;

            Color previousColor = Gizmos.color;
            Matrix4x4 previousMatrix = Gizmos.matrix;
            try
            {
                Gizmos.matrix = Matrix4x4.identity;
                IReadOnlyList<ProjectileBase> active = ProjectileBase.ActiveProjectiles;
                for (int i = 0; i < active.Count; i++)
                {
                    ProjectileBase projectile = active[i];
                    if (projectile == null || !ShouldDraw(projectile))
                        continue;

                    ProjectileDebugSnapshot snapshot = projectile.GetDebugSnapshot();
                    DrawSnapshot(snapshot);
                }
            }
            finally
            {
                Gizmos.color = previousColor;
                Gizmos.matrix = previousMatrix;
            }
        }

        private bool ShouldDraw(ProjectileBase projectile)
        {
            if (!_onlyDrawSelected)
                return true;

            Transform selected = Selection.activeTransform;
            return selected != null
                && (selected == projectile.transform
                    || selected.IsChildOf(projectile.transform));
        }

        private void DrawSnapshot(ProjectileDebugSnapshot snapshot)
        {
            if (_drawVelocity && snapshot.Velocity.sqrMagnitude > 1e-6f)
            {
                Gizmos.color = Color.white;
                DrawArrow(snapshot.Position, snapshot.Velocity * _vectorScale);
            }

            if (_drawGravity && snapshot.UsesGravity)
            {
                Gizmos.color = Color.yellow;
                DrawArrow(snapshot.Position, Vector3.down * 1.5f);
            }

            if (_drawHoming && snapshot.HomingEnabled)
            {
                Gizmos.color = Color.cyan;
                Gizmos.DrawWireSphere(snapshot.Position, snapshot.HomingRadius);
                if (snapshot.HasHomingTarget)
                {
                    Gizmos.color = Color.green;
                    Gizmos.DrawLine(snapshot.Position, snapshot.HomingTargetPosition);
                }
            }

            if (_drawOrbit && snapshot.OrbitEnabled)
            {
                Gizmos.color = Color.blue;
                Gizmos.DrawWireSphere(snapshot.OrbitCenter, 0.12f);
                DrawArrow(snapshot.OrbitCenter, snapshot.OrbitForward);

                Gizmos.color = new Color(1f, 0.5f, 0f);
                DrawCircle(snapshot.OrbitCenter, snapshot.OrbitRight,
                    snapshot.OrbitUp, snapshot.OrbitRadius, _orbitCircleSegments);
            }

            if (_drawCollision
                && snapshot.HasRecentReflection
                && Time.time - snapshot.LastCollisionTime <= _collisionHistorySeconds)
            {
                Gizmos.color = Color.red;
                DrawArrow(snapshot.LastCollisionPoint, snapshot.LastCollisionNormal);
                Gizmos.color = Color.green;
                DrawArrow(snapshot.LastCollisionPoint, snapshot.LastReflectedDirection);
            }
        }

        private static void DrawArrow(Vector3 origin, Vector3 vector)
        {
            if (vector.sqrMagnitude <= 1e-6f)
                return;

            Vector3 end = origin + vector;
            Vector3 direction = vector.normalized;
            Vector3 side = Vector3.Cross(direction, Vector3.up);
            if (side.sqrMagnitude <= 1e-6f)
                side = Vector3.Cross(direction, Vector3.right);
            side.Normalize();

            float length = Mathf.Min(0.25f, vector.magnitude * 0.25f);
            Vector3 headBase = end - direction * length;
            Gizmos.DrawLine(origin, end);
            Gizmos.DrawLine(end, headBase + side * length * 0.5f);
            Gizmos.DrawLine(end, headBase - side * length * 0.5f);
        }

        private static void DrawCircle(
            Vector3 center, Vector3 right, Vector3 up,
            float radius, int segments)
        {
            if (radius <= 0f || segments < 3)
                return;

            Vector3 previous = center + right * radius;
            for (int i = 1; i <= segments; i++)
            {
                float angle = i * Mathf.PI * 2f / segments;
                Vector3 current = center
                    + (right * Mathf.Cos(angle) + up * Mathf.Sin(angle)) * radius;
                Gizmos.DrawLine(previous, current);
                previous = current;
            }
        }
#endif
    }
}
