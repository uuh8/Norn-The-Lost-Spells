using UnityEngine;

namespace Game.Run
{
    /// <summary>地面是脚底，粒子原点是门中心；独立于Trigger大小，防止继承旧环缩放后埋地。</summary>
    public sealed class PortalVisualPlacement : MonoBehaviour
    {
        // P7蓝门纹理/VFX以门中心为原点，可见椭圆半高约2.5m；使用脚底1.5m会截掉门底。
        [SerializeField, Min(.1f)] private float _centerHeight = 2.5f;
        private readonly RaycastHit[] _hits = new RaycastHit[16];
        public void Place(Transform portal)
        {
            Vector3 foot = portal.position; float nearest = float.PositiveInfinity;
            int count = Physics.RaycastNonAlloc(foot + Vector3.up * 5, Vector3.down, _hits, 12, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                var hit = _hits[i];
                if (hit.collider.transform.IsChildOf(portal) || hit.collider.GetComponentInParent<CharacterController>() != null || hit.distance >= nearest) continue;
                nearest = hit.distance; foot.y = hit.point.y;
            }
            transform.SetPositionAndRotation(foot + Vector3.up * _centerHeight, Quaternion.Euler(0, portal.eulerAngles.y, 0));
            // lossyScale是累计父缩放。先恢复局部1，再抵消父尺度，让同一门在不同入口有同一世界大小。
            transform.localScale = Vector3.one; Vector3 scale = transform.lossyScale;
            transform.localScale = new Vector3(SafeInverse(scale.x), SafeInverse(scale.y), SafeInverse(scale.z));
        }
        private static float SafeInverse(float value) => Mathf.Abs(value) > .001f ? 1 / value : 1;
    }
}
