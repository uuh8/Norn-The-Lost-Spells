using System;
using Game.Core;
using Game.Combat;
using Game.Run;
using Game.Skills;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Character
{
    /// <summary>连接输入与库存只读来源；写资源通过同步 EventBus，未收到对应收据时拒绝施法。</summary>
    public sealed class OwnedWandCastAdapter : MonoBehaviour
    {
        [SerializeField] private SinglePlayerInventorySession _session;
        [SerializeField] private InputActionReference _select1, _select2, _select3;
        [SerializeField] private string _materialWorldId = "container-world";
        [SerializeField] private Material _materialWaitingMaterial;
        private readonly string _callerId = Guid.NewGuid().ToString("N");
        private ulong _sequence;
        private CastResourceRequestedEvent _waiting;
        private CastResourceResultEvent _lastResult;
        private bool _received, _success;
        private HeldItemInputController _heldInput;
        private void Awake() { _heldInput = GetComponent<HeldItemInputController>(); }
        public IWandLoadoutReadOnly ReadOnly => _session != null && _session.isActiveAndEnabled ? _session.WandsReadOnly : null;
        public OwnedWandFailureReason LastFailure { get; private set; }
        public void ConfigurePractice(SinglePlayerInventorySession session,InputActionReference one,InputActionReference two,
            InputActionReference three,Material waiting)
        {
            CancelOutstanding();
            if(isActiveAndEnabled){Unbind(_select1,Select1);Unbind(_select2,Select2);Unbind(_select3,Select3);}
            _session=session;_select1=one;_select2=two;_select3=three;_materialWaitingMaterial=waiting;
            _heldInput=GetComponent<HeldItemInputController>();
            if(isActiveAndEnabled){Bind(_select1,Select1);Bind(_select2,Select2);Bind(_select3,Select3);}
        }
        private void OnEnable()
        {
            EventBus<CastResourceResultEvent>.Subscribe(OnResult);
            EventBus<RaidResultEvent>.Subscribe(OnRaidResult);
            Bind(_select1, Select1); Bind(_select2, Select2); Bind(_select3, Select3);
        }
        private void OnDisable()
        {
            CancelOutstanding();
            EventBus<RaidResultEvent>.Unsubscribe(OnRaidResult);
            EventBus<CastResourceResultEvent>.Unsubscribe(OnResult);
            Unbind(_select1, Select1); Unbind(_select2, Select2); Unbind(_select3, Select3);
        }
        private void OnRaidResult(RaidResultEvent result)
        {
            if (_session?.ReadOnly == null || result.InventoryId != _session.ReadOnly.InventoryId) return;
            // 世界只有可靠结算后才结束；停止已提交Skyfall等延迟创建，防止稍后生成在据点Scene。
            var caster = GetComponent<SpellCaster>(); if (caster != null) caster.StopAllCoroutines();
        }
        private static void Bind(InputActionReference action, Action<InputAction.CallbackContext> handler)
        { if (action == null) return; action.action.performed += handler; action.action.Enable(); }
        private static void Unbind(InputActionReference action, Action<InputAction.CallbackContext> handler)
        { if (action == null) return; action.action.performed -= handler; action.action.Disable(); }
        private void Select1(InputAction.CallbackContext _) => Select(0);
        private void Select2(InputAction.CallbackContext _) => Select(1);
        private void Select3(InputAction.CallbackContext _) => Select(2);
        public void Select(int slot)
        { IWandLoadoutReadOnly read = ReadOnly; if (Time.timeScale > 0 && read != null)
                EventBus<WandSelectionRequestedEvent>.Publish(new WandSelectionRequestedEvent { InventoryId = read.InventoryId, SlotIndex = slot }); }
        public bool TryCapture(out WandProgramSnapshot program, out OwnedWandCastContext context)
        {
            program = default; context = default;
            IWandLoadoutReadOnly read = ReadOnly;
            if (_heldInput != null && !_heldInput.IsWandMode) return false;
            if (!isActiveAndEnabled || read == null || !read.TryGetProgram(read.SelectedWandId, out program)) return false;
            context = new OwnedWandCastContext(this, read.InventoryId, program.SourceWandId, program.ProgramRevision); return true;
        }
        public bool CanStart(in WandProgramSnapshot program)
        { IWandLoadoutReadOnly read = ReadOnly;
            return (_heldInput == null || _heldInput.IsWandMode) && isActiveAndEnabled && read != null && read.TryGetState(program.SourceWandId, out WandStateSnapshot state)
                && state.Carried && state.CooldownRemaining <= 0 && read.TryGetProgram(program.SourceWandId, out WandProgramSnapshot current)
                && current.ProgramRevision == program.ProgramRevision; }
        internal bool TryPrepare(in OwnedWandCastContext context, out OwnedWandCastContext prepared)
        {
            prepared = default;
            if (!Request(context, context.TicketId == null ? CastResourceOperation.PrepareRoot : CastResourceOperation.PrepareLayer)) return false;
            if (_session.CastResourcesReadOnly == null || !_session.CastResourcesReadOnly.TryGetLayer(_lastResult.TicketId, context.LayerIndex, out CastExecutionLayer layer))
            { Request(context.WithTicket(_lastResult.TicketId), CastResourceOperation.Cancel); return false; }
            var supplies = new SpellMaterialOutputSnapshot[layer.CommandCount];
            for (int i = 0; i < supplies.Length; i++)
                if (!_session.CastResourcesReadOnly.TryGetMaterialOutput(_lastResult.TicketId, context.LayerIndex, i, out supplies[i]))
                { Request(context.WithTicket(_lastResult.TicketId), CastResourceOperation.Cancel); return false; }
            prepared = context.WithPrepared(_lastResult.TicketId, layer, new CastSpawnTransaction(), supplies); return true;
        }
        internal bool Request(in OwnedWandCastContext context, CastResourceOperation operation)
        {
            IWandLoadoutReadOnly read = ReadOnly;
            if (!isActiveAndEnabled || read == null || read.InventoryId != context.InventoryId) { LastFailure = OwnedWandFailureReason.NoReceiver; return false; }
            _waiting = new CastResourceRequestedEvent { InventoryId = context.InventoryId, SourceWandId = context.SourceWandId,
                CallerId = _callerId, RequestId = ++_sequence, ProgramRevision = context.ProgramRevision,
                TicketId = context.TicketId, LayerIndex = context.LayerIndex, Operation = operation, MaterialWorldId = _materialWorldId };
            _received = false; _success = false; LastFailure = OwnedWandFailureReason.NoReceiver;
            EventBus<CastResourceRequestedEvent>.Publish(_waiting);
            return _received && _success;
        }
        internal void ReportSpawnFailure(in OwnedWandCastContext context)
        {
            LastFailure = OwnedWandFailureReason.SpawnFailed;
            EventBus<CastResourceResultEvent>.Publish(new CastResourceResultEvent { InventoryId = context.InventoryId,
                SourceWandId = context.SourceWandId, CallerId = _callerId, Reason = LastFailure });
        }
        internal void CancelOutstanding()
        {
            IWandLoadoutReadOnly read = ReadOnly; if (read == null) return;
            EventBus<CastResourceRequestedEvent>.Publish(new CastResourceRequestedEvent { InventoryId = read.InventoryId,
                CallerId = _callerId, RequestId = ++_sequence, Operation = CastResourceOperation.AbandonCaller });
        }
        internal Material WaitingMaterial => _materialWaitingMaterial;
        private void OnResult(CastResourceResultEvent result)
        {
            if (result.InventoryId != _waiting.InventoryId || result.SourceWandId != _waiting.SourceWandId
                || result.CallerId != _callerId || result.RequestId != _waiting.RequestId || _received) return;
            _lastResult = result; _received = true; _success = result.Success; LastFailure = result.Reason;
        }
    }
    public readonly struct OwnedWandCastContext
    {
        // Enabled 独立于 Unity 对象的假 null：来源销毁后仍是 owned 请求，不能变成旧无限资源路径。
        public readonly bool Enabled;
        public readonly OwnedWandCastAdapter Adapter;
        public readonly string InventoryId, SourceWandId;
        public readonly uint ProgramRevision;
        public readonly string TicketId;
        public readonly int LayerIndex;
        internal readonly CastExecutionLayer Layer;
        internal readonly CastSpawnTransaction SpawnTransaction;
        private readonly SpellMaterialOutputSnapshot[] _supplies;
        private readonly MaterialPayloadRuntimeAdapter[] _materials;
        internal OwnedWandCastContext(OwnedWandCastAdapter adapter, string inventory, string source, uint revision)
        { Enabled = true; Adapter = adapter; InventoryId = inventory; SourceWandId = source; ProgramRevision = revision;
            TicketId = null; LayerIndex = 0; Layer = null; SpawnTransaction = null; _supplies = null; _materials = null; }
        private OwnedWandCastContext(in OwnedWandCastContext original, string ticket, int index, CastExecutionLayer layer, CastSpawnTransaction transaction,
            SpellMaterialOutputSnapshot[] supplies = null, MaterialPayloadRuntimeAdapter[] materials = null)
        { Enabled = original.Enabled; Adapter = original.Adapter; InventoryId = original.InventoryId; SourceWandId = original.SourceWandId;
            ProgramRevision = original.ProgramRevision; TicketId = ticket; LayerIndex = index; Layer = layer; SpawnTransaction = transaction; _supplies = supplies; _materials = materials; }
        internal OwnedWandCastContext WithTicket(string ticket) => new OwnedWandCastContext(this, ticket, LayerIndex, null, null);
        internal OwnedWandCastContext WithPrepared(string ticket, CastExecutionLayer layer, CastSpawnTransaction transaction, SpellMaterialOutputSnapshot[] supplies) =>
            new OwnedWandCastContext(this, ticket, LayerIndex, layer, transaction, supplies);
        internal OwnedWandCastContext PrepareMaterials(MonoBehaviour owner, Vector3 position)
        {
            var proxies = new MaterialPayloadRuntimeAdapter[Layer.CommandCount];
            for (int i = 0; i < proxies.Length; i++) if (_supplies[i].Budget.Total > 0)
            {
                proxies[i] = MaterialPayloadRuntimeAdapter.Create(owner, InventoryId, _supplies[i].OutputId, position, Layer.GetCommand(i).LandingSiteDuration, Adapter.WaitingMaterial);
                Track(proxies[i].gameObject);
            }
            return new OwnedWandCastContext(this, TicketId, LayerIndex, Layer, SpawnTransaction, _supplies, proxies);
        }
        internal void LinkMaterial(int command, ProjectileBase projectile) { if (_materials != null) _materials[command]?.Link(projectile); }
        internal void ReleaseMaterial(int command, Vector3 position) { if (_materials != null) _materials[command]?.ReleaseAt(position); }
        internal void DelayMaterial(int command, Vector3 position, float delay) { if (_materials != null) _materials[command]?.SetFallback(position, delay); }
        internal void ActivateMaterials()
        {
            for (int i = 0; i < Layer.CommandCount; i++)
            {
                if (_materials != null && _materials[i] != null) _materials[i].Activate();
                var supply = _supplies[i];
                EventBus<SpellMaterialSupplyEvent>.Publish(new SpellMaterialSupplyEvent { InventoryId = InventoryId, SpellId = supply.SpellId,
                    Status = supply.Status, Requested = supply.Requested, Approved = supply.Budget.Total });
            }
        }
        internal OwnedWandCastContext Child(int command) => new OwnedWandCastContext(this, TicketId, Layer.GetChild(command), null, null);
        internal bool TryPrepare(out OwnedWandCastContext prepared)
        { prepared = default; return Adapter != null && Adapter.TryPrepare(this, out prepared); }
        internal bool Finish(bool commit) => Adapter != null && Adapter.Request(this, commit ? CastResourceOperation.Complete : CastResourceOperation.Cancel);
        internal void Abandon() { if (Adapter != null) Adapter.Request(this, CastResourceOperation.Abandon); }
        internal void Track(GameObject go)
        {
            // Additive测试/正式世界都必须跟随来源Scene结束，不能留在另一个active Scene中。
            if (Enabled && Adapter != null && go.scene != Adapter.gameObject.scene)
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, Adapter.gameObject.scene);
            SpawnTransaction?.Track(go);
        }
        internal void Track(Coroutine coroutine) => SpawnTransaction?.Track(coroutine);
    }
}
