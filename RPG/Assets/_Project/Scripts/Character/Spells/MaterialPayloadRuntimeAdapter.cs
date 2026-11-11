using Game.Core;
using Game.Combat;
using UnityEngine;
using Unity.Profiling;

namespace Game.Character
{
    /// <summary>批准材料的可见生命周期代理；独立于投射物销毁，真实量始终由Run的OutputId持有。</summary>
    public sealed class MaterialPayloadRuntimeAdapter : MonoBehaviour
    {
        private string _inventoryId, _outputId;
        private ProjectileBase _projectile;
        private Renderer _renderer;
        private Material _ownedMaterial;
        private Vector3 _position;
        private float _retry, _waitForProjectile;
        private bool _committed, _waiting, _resolved;
        private bool _subscribed;
        private ulong _actor, _command;
        private CombatSimulationBridge _execution;
        private CombatExecutionMode _mode;
        private System.Action<float, uint> _authorityStep;
        private bool AllowsWrites => _execution != null ? _execution.AllowsWrites : _mode == CombatExecutionMode.SinglePlayer;
        private static readonly ProfilerMarker Marker = new ProfilerMarker("Norn.SpellMaterials.Proxy");
        public bool IsWaiting => _waiting && !_resolved;
        public string OutputId => _outputId;
        public CombatExecutionMode ExecutionMode => _mode;
        public event System.Action<ProjectileTerminationReason, Vector3> Terminated;
        public event System.Action UnexpectedlyDestroyed;
        internal static MaterialPayloadRuntimeAdapter Create(MonoBehaviour owner, string inventory, string output, Vector3 position, float delay, Material material)
            => CreateCore(owner, inventory, output, position, delay, material, null, 0, 0, true, CombatExecutionMode.SinglePlayer);

        /// <summary>DS创建时注入真实World与冻结的根身份；无图形分支不访问Shader/Material/Renderer。</summary>
        internal static MaterialPayloadRuntimeAdapter CreateAuthority(MonoBehaviour owner, string inventory, string output, Vector3 position,
            float delay, Material material, CombatSimulationWorld world, ulong actor, ulong command, bool visuals)
        {
            if (world == null || actor == 0 || command == 0) throw new System.ArgumentException("材料输出缺少权威World/根命令身份。");
            return CreateCore(owner, inventory, output, position, delay, material, world, actor, command, visuals, CombatExecutionMode.Authority);
        }

        private static MaterialPayloadRuntimeAdapter CreateCore(MonoBehaviour owner, string inventory, string output, Vector3 position,
            float delay, Material material, CombatSimulationWorld world, ulong actor, ulong command, bool visuals, CombatExecutionMode mode)
        {
            var go = visuals ? GameObject.CreatePrimitive(PrimitiveType.Sphere) : new GameObject();
            go.SetActive(false); go.name = "SpellMaterial_" + output;
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, owner.gameObject.scene);
            go.transform.position = position; go.transform.localScale = Vector3.one * .15f;
            var collider = go.GetComponent<Collider>();
            if (collider != null) { collider.enabled = false; Release(collider); }
            var adapter = go.AddComponent<MaterialPayloadRuntimeAdapter>(); adapter._inventoryId = inventory; adapter._outputId = output;
            adapter._position = position; adapter._waitForProjectile = Mathf.Max(0, delay) + 1;
            adapter._actor = actor; adapter._command = command;
            adapter.ConfigureSimulation(mode, world);
            if (visuals) adapter.PrepareVisual(material);
            go.SetActive(true);
            // 工厂交付时完成显式World登记，OnEnable重复到达仍幂等；不依赖Editor是否派发运行时生命周期。
            adapter._execution.Resume(); adapter.SubscribeResults(); return adapter;
        }

        /// <summary>纯.15m橙球，不绑定私有预算/来源，不订阅投放结果，不发送任何材料事件。</summary>
        public static MaterialPayloadRuntimeAdapter CreateReplica(Transform parent, Vector3 position, bool waiting)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere); go.SetActive(false); go.name = "SpellMaterialReplica";
            if (parent != null) go.transform.SetParent(parent, false);
            go.transform.position = position; go.transform.localScale = Vector3.one * .15f;
            var collider = go.GetComponent<Collider>(); collider.enabled = false; Release(collider);
            var adapter = go.AddComponent<MaterialPayloadRuntimeAdapter>();
            adapter.ConfigureSimulation(CombatExecutionMode.Replica, null);
            adapter.PrepareVisual(null); adapter.ApplyReplicaWaiting(waiting); go.SetActive(true); return adapter;
        }

        private void PrepareVisual(Material material)
        {
            _renderer = GetComponent<Renderer>(); _renderer.enabled = false;
            if (material == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader != null) { _ownedMaterial = new Material(shader); material = _ownedMaterial; }
            }
            if (material != null) _renderer.sharedMaterial = material;
            var properties = new MaterialPropertyBlock(); properties.SetColor("_BaseColor", new Color(1, .55f, .08f)); _renderer.SetPropertyBlock(properties);
        }
        public void ApplyReplicaWaiting(bool waiting)
        {
            if (_mode != CombatExecutionMode.Replica || !waiting || _resolved) return;
            _waiting = true; if (_renderer != null) _renderer.enabled = true;
        }
        public void StepAuthority(float deltaTime, uint serverTick)
        { if (_execution != null) _execution.StepAuthority(deltaTime, serverTick); }
        private void ConfigureSimulation(CombatExecutionMode mode, CombatSimulationWorld world)
        {
            // 仅桥组件接触Combat内部状态；委托在创建时绑定一次。缓存Mode也避免销毁顺序把Authority误当单机。
            _mode = mode;
            if (_execution == null) _execution = gameObject.AddComponent<CombatSimulationBridge>();
            if (_authorityStep == null) _authorityStep = AdvanceAuthority;
            _execution.Configure(mode, world, _authorityStep);
        }
        private void AdvanceAuthority(float deltaTime, uint serverTick) => Tick(deltaTime);
        internal void Activate()
        {
            if (_mode == CombatExecutionMode.Replica || _resolved) return;
            if (_mode == CombatExecutionMode.Authority && _committed) return;
            _committed = true;
            // 只公开标记生命周期，真量仍是原Run OutputId；材料Key0由组件类型和Wire显式标识共同限定。
            if (_mode == CombatExecutionMode.Authority)
                EventBus<CombatOutputSpawnedEvent>.Publish(new CombatOutputSpawnedEvent
                    { Output = gameObject, ActorId = _actor, CommandId = _command });
            if (_waiting) Send();
        }
        internal void Link(ProjectileBase projectile)
        {
            if (_resolved || _waiting || projectile == null) return;
            Unlink(); _projectile = projectile; _position = projectile.transform.position;
            projectile.SurfaceImpacted += OnImpact; projectile.Finished += OnFinished;
        }
        internal void ReleaseAt(Vector3 point) { _position = point; BeginWaiting(); }
        internal void SetFallback(Vector3 point, float delay) { _position = point; _waitForProjectile = Mathf.Max(0, delay) + 1; }
        private void OnImpact(ProjectileImpactContext contact)
        { _position = contact.Point + contact.SurfaceNormal * .12f; BeginWaiting(); }
        private void OnFinished()
        {
            if (_waiting || _resolved) return;
            if (_projectile != null) _position = _projectile.transform.position;
            BeginWaiting();
        }
        private void BeginWaiting()
        {
            if (_resolved || _waiting) return;
            _waiting = true; Unlink(); transform.position = _position; if (_renderer != null) _renderer.enabled = true;
            if (_committed) Send(); _retry = 0;
        }
        private void Send()
        {
            // 已提交预算只服从同World模拟门，不以Actor在线/存活资格取消；Run仍会再核最终写门。
            if (!_committed || _resolved || !AllowsWrites) return;
            EventBus<SpellMaterialOutputEvent>.Publish(new SpellMaterialOutputEvent
                { InventoryId = _inventoryId, OutputId = _outputId, Position = _position });
        }
        private void OnEnable()
        {
            if (_execution != null) _execution.Resume();
            SubscribeResults();
        }
        private void SubscribeResults()
        {
            if (_mode == CombatExecutionMode.Replica || _subscribed) return;
            EventBus<SpellMaterialOutputResultEvent>.Subscribe(OnResult); _subscribed = true;
        }
        private void OnDisable()
        {
            if (_execution != null) _execution.Suspend();
            // Authority暂挂保留结果订阅和预算；Frozen/重连不得借Disable把已提交量计为Lost。
            if (_mode != CombatExecutionMode.SinglePlayer) return;
            Unsubscribe();
            // Disable同样停止Update与结果接收，已提交代理不能留下不可见且无法重试的Owner。
            // 准备阶段尚未扣量，Rollback销毁这种代理时不发送结束请求。
            if (!_committed || _resolved) return;
            CloseWorld();
        }
        /// <summary>仅真正World退出调用，沿原Run规则结束世界归属，不退款；暂挂和重连不调用。</summary>
        public void CloseWorld()
        {
            if (!_resolved && _committed && _mode != CombatExecutionMode.Replica)
            {
                _resolved = true;
                EventBus<SpellMaterialOutputEvent>.Publish(new SpellMaterialOutputEvent
                    { InventoryId = _inventoryId, OutputId = _outputId, Ended = true });
                Terminated?.Invoke(ProjectileTerminationReason.Cancelled, _position);
            }
            _resolved = true; Release(gameObject);
        }
        private void OnResult(SpellMaterialOutputResultEvent result)
        {
            if (_mode == CombatExecutionMode.Replica || result.InventoryId != _inventoryId || result.OutputId != _outputId || !result.Resolved) return;
            _resolved = true; if (_renderer != null) _renderer.enabled = false; Unlink();
            Terminated?.Invoke(result.Ended ? ProjectileTerminationReason.Cancelled : ProjectileTerminationReason.Impact, _position);
            Release(gameObject);
        }
        private void Update()
        { if (_mode == CombatExecutionMode.SinglePlayer) Tick(Time.deltaTime); }
        private void Tick(float deltaTime)
        {
            using var marker = Marker.Auto();
            if (!_committed || _resolved || !AllowsWrites
                || _mode == CombatExecutionMode.SinglePlayer && Time.timeScale <= 0) return;
            if (_waiting) { _retry += deltaTime; if (_retry >= .2f) { _retry = 0; Send(); } return; }
            if (_projectile != null) { _position = _projectile.transform.position; transform.position = _position; return; }
            // Skyfall调度已提交但实际对象未能建立时，保留落点处可见预算；不能丢料或再扣一份。
            _waitForProjectile -= deltaTime; if (_waitForProjectile <= 0) BeginWaiting();
        }
        private void Unlink()
        { if (_projectile != null) { _projectile.SurfaceImpacted -= OnImpact; _projectile.Finished -= OnFinished; } _projectile = null; }
        private void OnDestroy()
        {
            if (_execution != null) _execution.Suspend();
            Unsubscribe(); Unlink(); if (_ownedMaterial != null) Release(_ownedMaterial);
            if (_committed && !_resolved && _mode == CombatExecutionMode.SinglePlayer)
                EventBus<SpellMaterialOutputEvent>.Publish(new SpellMaterialOutputEvent
                    { InventoryId = _inventoryId, OutputId = _outputId, Ended = true });
            else if (_committed && !_resolved && _mode == CombatExecutionMode.Authority)
                UnexpectedlyDestroyed?.Invoke(); // 对象意外消失只锁存故障，预算留原服务，不自动结束/退款。
        }
        private void Unsubscribe()
        { if (_subscribed) EventBus<SpellMaterialOutputResultEvent>.Unsubscribe(OnResult); _subscribed = false; }
        private static void Release(UnityEngine.Object value)
        { if (Application.isPlaying) Destroy(value); else DestroyImmediate(value); }
    }
}
