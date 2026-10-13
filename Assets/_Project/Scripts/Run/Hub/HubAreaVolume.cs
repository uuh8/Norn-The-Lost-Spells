using UnityEngine;

namespace Game.Run
{
    /// <summary>Scene里的实际区域，盒子既供作者Edit Collider，也供游戏查询；不依赖Trigger进出回调缓存。</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(BoxCollider))]
    public sealed class HubAreaVolume : MonoBehaviour
    {
        [SerializeField] private BoxCollider _volume;
        [SerializeField] private Color _gizmoColor = new Color(.2f, .85f, .65f, .7f);
        public BoxCollider Volume => _volume != null ? _volume : _volume = GetComponent<BoxCollider>();
        public Vector3 Center => Volume.transform.TransformPoint(Volume.center);
        private void Reset() { _volume = GetComponent<BoxCollider>(); _volume.isTrigger = true; }
        private void Awake() { _volume = Volume; }
        public bool Contains(Vector3 point)
        {
            var box = Volume;
            if (!isActiveAndEnabled || box == null || !box.enabled || !box.gameObject.activeInHierarchy || !Finite(point)
                || !Finite(box.center) || !Finite(box.size) || box.size.x <= 0 || box.size.y <= 0 || box.size.z <= 0) return false;
            Vector3 scale = box.transform.lossyScale;
            if (!Finite(scale) || Mathf.Abs(scale.x) < .00001f || Mathf.Abs(scale.y) < .00001f || Mathf.Abs(scale.z) < .00001f) return false;
            // 世界点先转到盒子的本地空间；直接用world Bounds会把旋转盒外的四角也误判成区内。
            Vector3 delta = box.transform.InverseTransformPoint(point) - box.center, half = box.size * .5f;
            return Finite(delta) && delta.x >= -half.x && delta.x < half.x && delta.y >= -half.y && delta.y < half.y && delta.z >= -half.z && delta.z < half.z;
        }
        private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        private void OnDrawGizmos()
        {
            var box = Volume; if (box == null) return;
            // Gizmos只在Editor画作者边界；矩阵包含父层旋转/缩放，与Runtime本地判定使用同一盒子。
            Matrix4x4 matrix = Gizmos.matrix; Color color = Gizmos.color;
            Gizmos.matrix = box.transform.localToWorldMatrix; Gizmos.color = _gizmoColor; Gizmos.DrawWireCube(box.center, box.size);
            Gizmos.matrix = matrix; Gizmos.color = color;
        }
    }
}
