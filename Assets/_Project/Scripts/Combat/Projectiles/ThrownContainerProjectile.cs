using Game.Core;
using UnityEngine;
using Unity.Profiling;

namespace Game.Combat
{
    /// <summary>只负责在途物理代理；材料真量由Run唯一实例持有，碰撞只发Core意图。</summary>
    [RequireComponent(typeof(Rigidbody), typeof(Collider))]
    public sealed class ThrownContainerProjectile : MonoBehaviour, IContainerThrowProxy
    {
        private Rigidbody _body;
        private Collider _collider, _caster;
        private Renderer _renderer;
        private MaterialPropertyBlock _properties;
        private string _inventoryId, _worldId, _flightId;
        private Vector3 _velocity, _origin, _lastSafe, _impactPoint;
        private float _seconds, _maximumDistance, _age, _retry;
        private bool _prepared, _launched, _impacted, _resolved;
        private static readonly ProfilerMarker FlightMarker = new ProfilerMarker("Norn.ContainerThrow.Flight");
        public bool IsWaiting => _impacted && !_resolved;
        public string FlightId => _flightId;
        public void Prepare(string inventory, string world, string flight, Vector3 velocity, float seconds, float distance, Collider caster)
        {
            if (_prepared) throw new System.InvalidOperationException("投掷代理不能重复准备。");
            _body = GetComponent<Rigidbody>(); _collider = GetComponent<Collider>(); _renderer = GetComponentInChildren<Renderer>();
            foreach (var renderer in GetComponentsInChildren<Renderer>(true)) if (renderer.enabled) { _renderer = renderer; break; }
            _properties = new MaterialPropertyBlock(); _caster = caster;
            _inventoryId = inventory; _worldId = world; _flightId = flight; _velocity = velocity;
            _seconds = Mathf.Max(1, seconds); _maximumDistance = Mathf.Max(1, distance); _origin = _lastSafe = transform.position;
            _body.useGravity = true; _body.isKinematic = false; _body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            _body.linearDamping = 0; // 与预览的解析抛物线使用相同的无空气阻力模型。
            _prepared = true;
        }
        public void Launch()
        {
            if (!_prepared || _launched) throw new System.InvalidOperationException("投掷代理未准备或已发射。");
            _launched = true; gameObject.SetActive(true);
            if (_caster != null) Physics.IgnoreCollision(_collider, _caster, true);
            _body.linearVelocity = _velocity;
            _body.angularVelocity = new Vector3(3, 1, 2);
        }
        public void Cancel() { _resolved = true; Destroy(gameObject); }
        private void OnEnable() => EventBus<ContainerTransitResolvedEvent>.Subscribe(OnResolved);
        private void OnDisable() => EventBus<ContainerTransitResolvedEvent>.Unsubscribe(OnResolved);
        private void OnResolved(ContainerTransitResolvedEvent result)
        {
            if (result.InventoryId != _inventoryId || result.FlightId != _flightId) return;
            _resolved = true; Destroy(gameObject);
        }
        private void OnCollisionEnter(Collision collision)
        {
            if (!_launched || _impacted || _resolved) return;
            // GetContact返回值结构；Collision.contacts会复制并分配数组，热路径禁用。
            var contact = collision.GetContact(0); BeginImpact(contact.point + contact.normal * .12f);
        }
        private void BeginImpact(Vector3 point)
        {
            _impacted = true; _impactPoint = point; _body.linearVelocity = Vector3.zero; _body.angularVelocity = Vector3.zero;
            _body.isKinematic = true; _collider.enabled = false; transform.position = point;
            // 容量未就绪时保留可见的橙色待投放标记，不删除或免费恢复背包满瓶。
            if (_renderer != null)
            { _renderer.GetPropertyBlock(_properties); _properties.SetColor("_BaseColor", new Color(1, .55f, .08f)); _renderer.SetPropertyBlock(_properties); }
            SendImpact(); _retry = 0;
        }
        private void SendImpact() => EventBus<ContainerImpactEvent>.Publish(new ContainerImpactEvent
        { InventoryId = _inventoryId, WorldId = _worldId, FlightId = _flightId, Position = _impactPoint });
        private void Update()
        {
            using var sample = FlightMarker.Auto();
            if (!_launched || _resolved || Time.timeScale <= 0) return;
            if (_impacted)
            { _retry += Time.deltaTime; if (_retry >= .2f) { _retry = 0; SendImpact(); } return; }
            _age += Time.deltaTime;
            Vector3 point = transform.position;
            bool finite = float.IsFinite(point.x) && float.IsFinite(point.y) && float.IsFinite(point.z);
            if (!finite || _age >= _seconds || (point - _origin).sqrMagnitude > _maximumDistance * _maximumDistance)
            { BeginImpact(_lastSafe); return; }
            if (point.y >= _origin.y - .5f) _lastSafe = point;
        }
        private void OnDestroy()
        {
            if (_launched && !_resolved) EventBus<ContainerTransitEndedEvent>.Publish(new ContainerTransitEndedEvent
            { InventoryId = _inventoryId, WorldId = _worldId, FlightId = _flightId, Reason = ContainerTransitEndReason.ProxyDestroyed });
        }
    }
}
