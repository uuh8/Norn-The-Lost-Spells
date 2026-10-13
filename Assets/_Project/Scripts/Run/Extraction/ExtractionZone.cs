using UnityEngine;
using Game.Core;
namespace Game.Run
{
    /// <summary>球形距离检测避免依赖Trigger/Rigidbody配对；只检测绑定玩家，热路径无分配。</summary>
    public sealed class ExtractionZone : MonoBehaviour, IPortalVisualSource, IPortalInteractionTarget
    {
        private Transform _player;
        private string _raid;
        private float _radiusSquared;
        private bool _inside;
        public int PortalId => gameObject.GetInstanceID();
        public Transform PortalTransform => transform;
        public string InteractionLabel => "F 返回据点";
        public bool CanInteract(Collider actor) => actor != null && _player != null && actor.transform.IsChildOf(_player)
            && (_player.position - transform.position).sqrMagnitude <= _radiusSquared;
        public bool Interact(Collider actor)
        {
            if (!CanInteract(actor)) return false;
            EventBus<ExtractionRequestedEvent>.Publish(new ExtractionRequestedEvent { RaidId = _raid, Inside = true });
            EventBus<ExtractionRequestedEvent>.Publish(new ExtractionRequestedEvent { RaidId = _raid, Start = true }); return true;
        }
        private void OnEnable() => PortalInteractionRegistry.Register(this);
        private void OnDisable() => PortalInteractionRegistry.Unregister(this);
        private void Awake() => PortalTransitionCoordinator.EnsureVisual(transform);
        public void Configure(Transform player, string raid, Vector3 position, float radius)
        {
            _player = player; _raid = raid; transform.position = position; _radiusSquared = radius * radius;
            // Awake早于运行时出口位置注入；配置后再贴地，避免使用创建点的地面高度。
            PortalTransitionCoordinator.EnsureVisual(transform);
        }
        private void Update()
        {
            if (_player == null || Time.timeScale <= 0) return;
            bool inside = (_player.position - transform.position).sqrMagnitude <= _radiusSquared;
            if (inside == _inside) return; _inside = inside;
            EventBus<ExtractionRequestedEvent>.Publish(new ExtractionRequestedEvent { RaidId = _raid, Inside = inside });
        }
    }
}
