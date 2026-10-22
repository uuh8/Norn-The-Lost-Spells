using Game.Core;
using Game.ElementField;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Rendering
{
    /// <summary>只读共享固体外表面 Mesh；视觉色泽与高光不改变真实碰撞轮廓或 GMU。</summary>
    public sealed class FixedSolidSurfaceRenderer : MonoBehaviour
    {
        [SerializeField] private ElementWorldRuntime _world;
        [SerializeField] private Material _material;
        private MaterialPropertyBlock _properties;
        private Camera _camera;
        private Plane[] _planes;
        private void Start()
        {
            if (_world == null) _world = GetComponent<ElementWorldRuntime>();
            _properties = new MaterialPropertyBlock(); _camera = Camera.main; _planes = new Plane[6];
            if (_material == null) GameLog.Error("FixedSolid Renderer 缺少 Material。", "Rendering");
        }
        private void LateUpdate()
        {
            IFixedSolidSurfaceReadOnly source = _world != null ? _world.SolidSurfaces : null;
            if (source == null || _material == null) return;
            if (_camera == null) _camera = Camera.main;
            if (_camera == null) return;
            GeometryUtility.CalculateFrustumPlanes(_camera, _planes);
            for (int i = 0; i < source.SurfaceCapacity; i++)
                if (source.TryGetSurface(i, out Mesh mesh, out Vector3 position))
                {
                    Bounds bounds = mesh.bounds; bounds.center += position;
                    if (!GeometryUtility.TestPlanesAABB(_planes, bounds)) continue;
                    Graphics.DrawMesh(mesh, Matrix4x4.TRS(position, Quaternion.identity, Vector3.one),
                        _material, 0, _camera, 0, _properties, ShadowCastingMode.On, true);
                }
        }
    }
}
