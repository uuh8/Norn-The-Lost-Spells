using Game.Core;
using UnityEngine;
using Unity.Profiling;

namespace Game.Combat
{
    /// <summary>只负责在途物理代理；材料真量由Run唯一实例持有，碰撞只发Core意图。</summary>
    [RequireComponent(typeof(Rigidbody), typeof(Collider))]
    public sealed class ThrownContainerProjectile : MonoBehaviour, IContainerThrowProxy, ICombatSimulationParticipant
    {
        private Rigidbody _body;
        private Collider _collider, _caster;
        private Renderer _renderer;
        private Renderer[] _visualRenderers;
        private MaterialPropertyBlock _properties;
        private string _inventoryId, _worldId, _flightId;
        private Vector3 _velocity, _origin, _lastSafe, _impactPoint;
        private float _seconds, _maximumDistance, _age, _retry;
        private bool _prepared, _launched, _impacted, _resolved, _modelInstalled;
        private static readonly ProfilerMarker FlightMarker = new ProfilerMarker("Norn.ContainerThrow.Flight");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        public bool IsWaiting => _impacted && !_resolved;
        public string FlightId => _flightId;
        private CombatExecutionState _execution;
        public CombatExecutionMode ExecutionMode => _execution.Mode;
        public void ConfigureExecutionMode(CombatExecutionMode mode, CombatSimulationWorld world = null)
        {
            _execution.Configure(this, mode, world, CombatTickPhase.AfterPhysics);
            if (mode == CombatExecutionMode.Replica)
            {
                if (_body == null) _body = GetComponent<Rigidbody>();
                if (_collider == null) _collider = GetComponent<Collider>();
                if (_body != null) _body.isKinematic = true;
                if (_collider != null) _collider.enabled = false;
            }
        }
        public void StepAuthority(float deltaTime, uint serverTick)
        { if (_execution.TryStep(deltaTime, serverTick)) Tick(deltaTime); }

        /// <summary>
        /// 发射前安装Catalog的纯模型，保留原ThrowPrefab作为唯一物理根。
        /// 先完成模型净化、原0.45m归一和Bounds居中，成功后才隐藏旧球；失败只清理候选，不改变原代理。
        /// </summary>
        public bool InstallWorldModel(GameObject modelPrefab)
        {
            if (_modelInstalled || _launched || _resolved || modelPrefab == null) return false;
            var previousRenderers = GetComponentsInChildren<Renderer>(true);
            GameObject model = null;
            bool installed = false;
            try
            {
                model = Instantiate(modelPrefab, transform, false); model.name = "ThrownContainerModel"; model.SetActive(true);
                // 只禁用克隆的行为与物理；不修改Catalog资源，也不把模型上的脚本/碰撞体授为第二套Gameplay权威。
                foreach (var behaviour in model.GetComponentsInChildren<MonoBehaviour>(true)) behaviour.enabled = false;
                foreach (var collider in model.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
                foreach (var body in model.GetComponentsInChildren<Rigidbody>(true))
                { body.isKinematic = true; body.detectCollisions = false; }
                var renderers = model.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length == 0) return false;
                Bounds bounds = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
                if (!Finite(bounds.center) || !Finite(bounds.size)) return false;
                float largest = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
                if (largest > .001f) model.transform.localScale *= .45f / largest;
                bounds = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
                if (!Finite(bounds.center) || !Finite(bounds.size) || !Finite(model.transform.localScale)) return false;
                model.transform.position += transform.position - bounds.center;
                if (!Finite(model.transform.position)) return false;

                // 以下才提交视觉替换；原根位置/旋转/缩放与Rigidbody资格始终由组合层和ExecutionMode掌管。
                if (_properties == null) _properties = new MaterialPropertyBlock();
                foreach (var renderer in previousRenderers) if (renderer != null) renderer.enabled = false;
                if (GetComponent<SphereCollider>() is SphereCollider sphere)
                { sphere.center = Vector3.zero; sphere.radius = .18f; }
                _visualRenderers = renderers; _renderer = renderers[0];
                foreach (var renderer in renderers) if (renderer.enabled) { _renderer = renderer; break; }
                _modelInstalled = true; installed = true;
                if (_execution.Mode == CombatExecutionMode.Replica && _impacted) ApplyWaitingColor();
                return true;
            }
            finally
            {
                if (!installed && model != null)
                {
                    model.SetActive(false);
                    if (Application.isPlaying) Destroy(model); else DestroyImmediate(model);
                }
            }
        }

        /// <summary>
        /// Replica只消费权威Waiting事实；迟到的Flying不得撤销它，不写位置/物理/Run，也不产生Impact或重试。
        /// Renderer数组与MPB只在准备/首次需要时缓存，重复状态无需重建材质或分配逐帧数组。
        /// </summary>
        public void ApplyReplicaWaiting(bool waiting)
        {
            if (_execution.Mode != CombatExecutionMode.Replica || !waiting || _impacted || _resolved) return;
            _impacted = true; ApplyWaitingColor();
        }

        private void ApplyWaitingColor()
        {
            if (_visualRenderers == null) _visualRenderers = GetComponentsInChildren<Renderer>(true);
            if (_properties == null) _properties = new MaterialPropertyBlock();
            foreach (var renderer in _visualRenderers)
            {
                if (renderer == null) continue;
                renderer.GetPropertyBlock(_properties); _properties.SetColor(BaseColorId, new Color(1, .55f, .08f));
                renderer.SetPropertyBlock(_properties);
            }
        }

        private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

        public void Prepare(string inventory, string world, string flight, Vector3 velocity, float seconds, float distance, Collider caster)
        {
            if (_execution.Mode == CombatExecutionMode.Replica) return;
            if (_prepared) throw new System.InvalidOperationException("投掷代理不能重复准备。");
            _body = GetComponent<Rigidbody>(); _collider = GetComponent<Collider>(); _renderer = GetComponentInChildren<Renderer>();
            if (_visualRenderers == null) _visualRenderers = GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in _visualRenderers) if (renderer.enabled) { _renderer = renderer; break; }
            if (_properties == null) _properties = new MaterialPropertyBlock();
            _caster = caster;
            _inventoryId = inventory; _worldId = world; _flightId = flight; _velocity = velocity;
            _seconds = Mathf.Max(1, seconds); _maximumDistance = Mathf.Max(1, distance); _origin = _lastSafe = transform.position;
            _body.useGravity = true; _body.isKinematic = false; _body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            _body.linearDamping = 0; // 与预览的解析抛物线使用相同的无空气阻力模型。
            _prepared = true;
        }
        public void Launch()
        {
            if (!_execution.AllowsGameplayWrites) return;
            if (!_prepared || _launched) throw new System.InvalidOperationException("投掷代理未准备或已发射。");
            _launched = true; gameObject.SetActive(true);
            if (_caster != null) Physics.IgnoreCollision(_collider, _caster, true);
            _body.linearVelocity = _velocity;
            _body.angularVelocity = new Vector3(3, 1, 2);
        }
        public void Cancel() { _resolved = true; Destroy(gameObject); }
        private void OnEnable()
        { _execution.Resume(this, CombatTickPhase.AfterPhysics); EventBus<ContainerTransitResolvedEvent>.Subscribe(OnResolved); }
        private void OnDisable()
        { _execution.Suspend(this); EventBus<ContainerTransitResolvedEvent>.Unsubscribe(OnResolved); }
        private void OnResolved(ContainerTransitResolvedEvent result)
        {
            if (_execution.Mode == CombatExecutionMode.Replica) return;
            if (result.InventoryId != _inventoryId || result.FlightId != _flightId) return;
            _resolved = true; Destroy(gameObject);
        }
        private void OnCollisionEnter(Collision collision)
        {
            if (!_execution.AllowsGameplayWrites) return;
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
            if (_execution.Mode != CombatExecutionMode.SinglePlayer || Time.timeScale <= 0) return;
            Tick(Time.deltaTime);
        }
        private void Tick(float deltaTime)
        {
            using var sample = FlightMarker.Auto();
            // 保留已有代理/等待预约，不在故障期间推进重试或发出新的材料影响意图。
            if (!_execution.AllowsGameplayWrites || !_launched || _resolved) return;
            if (_impacted)
            { _retry += deltaTime; if (_retry >= .2f) { _retry = 0; SendImpact(); } return; }
            _age += deltaTime;
            Vector3 point = transform.position;
            bool finite = float.IsFinite(point.x) && float.IsFinite(point.y) && float.IsFinite(point.z);
            if (!finite || _age >= _seconds || (point - _origin).sqrMagnitude > _maximumDistance * _maximumDistance)
            { BeginImpact(_lastSafe); return; }
            if (point.y >= _origin.y - .5f) _lastSafe = point;
        }
        private void OnDestroy()
        {
            _execution.Suspend(this);
            if (_execution.Mode != CombatExecutionMode.Replica && _launched && !_resolved) EventBus<ContainerTransitEndedEvent>.Publish(new ContainerTransitEndedEvent
            { InventoryId = _inventoryId, WorldId = _worldId, FlightId = _flightId, Reason = ContainerTransitEndReason.ProxyDestroyed });
        }
    }
}
