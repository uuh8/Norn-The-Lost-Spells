using Game.Run;
using UnityEngine;

namespace Game.Character
{
    /// <summary>
    /// 原Home研究区与返回门的只读空间载体。只保存导出时映射的作者Collider/Transform，
    /// 不持有账号、库存、施法或转场Owner，也不注册F输入；调用者仍须独立验证当前角色与世界权限。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OnlineHomeWorldBindings : MonoBehaviour
    {
        [SerializeField] private BoxCollider _researchVolume;
        [SerializeField] private Collider _returnPortalCollider;
        [SerializeField] private Transform _returnPortalTransform;
        // 原Owner会在离线导出中剥离；保留作者的enabled条件，不能把停用区域自动变成可交互区域。
        [SerializeField] private bool _researchEnabled = true, _returnPortalEnabled = true;
        public BoxCollider ResearchVolume => _researchVolume;
        public Collider ReturnPortalCollider => _returnPortalCollider;
        public Transform ReturnPortalTransform => _returnPortalTransform;

        /// <summary>
        /// 与原HubAreaVolume相同的本地半开区间，包含父级旋转/缩放；不用world AABB扩大旋转盒的权限。
        /// 世界根保持inactive时查询自然为false，TryValidate仍可检查未激活的Prefab，不触发Runtime装配。
        /// </summary>
        public bool ContainsResearch(Vector3 point)
        {
            var box = _researchVolume;
            if (!isActiveAndEnabled || !_researchEnabled || !ValidBox(box) || !box.enabled
                || !box.gameObject.activeInHierarchy || !Finite(point)) return false;
            Vector3 delta = box.transform.InverseTransformPoint(point) - box.center, half = box.size * .5f;
            return Finite(delta) && delta.x >= -half.x && delta.x < half.x && delta.y >= -half.y && delta.y < half.y
                && delta.z >= -half.z && delta.z < half.z;
        }

        /// <summary>返回门沿原PortalInteractionRegistry的Collider最近点/Actor中心语义，不另造3m半径。</summary>
        public bool ContainsReturnPortal(Collider actor)
            => isActiveAndEnabled && _returnPortalEnabled && PortalInteractionRegistry.Contains(_returnPortalCollider, actor);

        public bool TryValidate(out string error)
        {
            error = null;
            if (_researchVolume == null || _returnPortalCollider == null || _returnPortalTransform == null
                || !(_returnPortalCollider is BoxCollider portalBox) || !ValidBox(_researchVolume) || !ValidBox(portalBox))
            { error = "Home必须保留原研究Box与原返回门Box的有限正尺寸几何。"; return false; }
            if (!_researchVolume.transform.IsChildOf(transform) || !_returnPortalCollider.transform.IsChildOf(transform)
                || !_returnPortalTransform.IsChildOf(transform) || _returnPortalCollider.transform != _returnPortalTransform)
            { error = "Home研究/返回门引用必须属于本地图，并指向原返回门同一Transform。"; return false; }
            return true;
        }
        private static bool ValidBox(BoxCollider box)
        {
            if (box == null || !Finite(box.center) || !Finite(box.size) || box.size.x <= 0 || box.size.y <= 0 || box.size.z <= 0) return false;
            Vector3 scale = box.transform.lossyScale;
            return Finite(scale) && Mathf.Abs(scale.x) >= .00001f && Mathf.Abs(scale.y) >= .00001f && Mathf.Abs(scale.z) >= .00001f;
        }
        private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    }
}
