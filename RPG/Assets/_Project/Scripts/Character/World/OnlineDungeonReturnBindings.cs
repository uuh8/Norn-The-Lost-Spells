using System;
using Game.Run;
using UnityEngine;

namespace Game.Character
{
    /// <summary>
    /// 原成功返回门的只读Authoring数据。P7保留真实固定Box；P8保存原Sphere门的inactive模板，
    /// 由DS在胜利后沿原Placement规则决定唯一位置。这里不读取账号、不裁决清场、不注册F或提交资产。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OnlineDungeonReturnBindings : MonoBehaviour, IPortalVisualSource
    {
        [SerializeField] private Collider _portalCollider;
        [SerializeField] private Transform _portalTransform;
        [SerializeField] private int _encounterIndex;
        [SerializeField] private LayerMask _playerLayers;
        [SerializeField] private bool _dynamicPlacement;
        [SerializeField] private GameObject _inactiveTemplateRoot;
        [SerializeField] private Behaviour[] _presentations = Array.Empty<Behaviour>();
        [SerializeField] private bool[] _authoredPresentationEnabled = Array.Empty<bool>();
        public Collider PortalCollider => _portalCollider;
        public Transform PortalTransform => _portalTransform;
        public int EncounterIndex => _encounterIndex;
        public LayerMask PlayerLayers => _playerLayers;
        public bool DynamicPlacement => _dynamicPlacement;
        public GameObject InactiveTemplateRoot => _inactiveTemplateRoot;
        public Behaviour[] Presentations => _presentations;
        public int PortalId => gameObject.GetInstanceID();

        public bool Contains(Collider actor)
            => isActiveAndEnabled && actor != null && (_playerLayers.value & (1 << actor.gameObject.layer)) != 0
                && PortalInteractionRegistry.Contains(_portalCollider, actor);

        public bool TryValidate(out string error)
        {
            error = null;
            if (_portalCollider == null || _portalTransform == null || _portalCollider.transform != _portalTransform
                || _portalTransform != transform || _encounterIndex < 0 || _playerLayers.value == 0
                || _dynamicPlacement && (_inactiveTemplateRoot != gameObject || !(_portalCollider is SphereCollider))
                || !_dynamicPlacement && (_inactiveTemplateRoot != null || !(_portalCollider is BoxCollider)))
            { error = "返回门必须保留原固定Box或原动态Sphere模板，不能互换几何与Owner。"; return false; }
            var scale = transform.lossyScale;
            if (!Finite(scale) || Mathf.Abs(scale.x) < .00001f || Mathf.Abs(scale.y) < .00001f || Mathf.Abs(scale.z) < .00001f)
            { error = "返回门原变换尺度非法。"; return false; }
            if (_portalCollider is BoxCollider box && (!Finite(box.center) || !Finite(box.size) || box.size.x <= 0 || box.size.y <= 0 || box.size.z <= 0)
                || _portalCollider is SphereCollider sphere && (!Finite(sphere.center) || !float.IsFinite(sphere.radius) || sphere.radius <= 0))
            { error = "返回门作者Collider尺寸必须有限且为正。"; return false; }
            if (_presentations == null || _authoredPresentationEnabled == null || _presentations.Length != _authoredPresentationEnabled.Length)
            { error = "返回门表现引用与作者enabled标记不完整。"; return false; }
            foreach (var presentation in _presentations)
                if (presentation == null || !presentation.transform.IsChildOf(transform)
                    || !(presentation is PortalVisualPlacement) && presentation.GetType().FullName != "Game.UI.PortalVfxController")
                { error = "返回门仅允许原Placement和原Vfx表现，不能保留旧Gameplay Owner。"; return false; }
            return true;
        }

        /// <summary>仅控制表现可用性，Gameplay Collider与清场许可仍由调用方权威Owner裁决。</summary>
        public void SetPresentationAvailable(bool available)
        {
#if UNITY_SERVER && !UNITY_EDITOR
            available = false;
#endif
            for (int i = 0; i < _presentations.Length; i++)
                if (_presentations[i] != null) _presentations[i].enabled = available && _authoredPresentationEnabled[i];
        }
        /// <summary>DS复制实际Pose后，Client复用原视觉接地；不另写视觉门中心和触发体位置的近似关系。</summary>
        public void RefreshVisualPlacement()
        {
#if !UNITY_SERVER || UNITY_EDITOR
            foreach (var presentation in _presentations)
                if (presentation is PortalVisualPlacement placement) placement.Place(_portalTransform);
#endif
        }
        private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    }
}
