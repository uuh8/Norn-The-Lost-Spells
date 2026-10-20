using System;
using Game.Core;
using Game.Run;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Character
{
    /// <summary>固定速率近处采集：Input System 描述意图，Run/ElementField 验证归属、距离与真实数量。</summary>
    public sealed class MaterialCollectionController : MonoBehaviour
    {
        [SerializeField] private SinglePlayerInventorySession _session;
        [SerializeField] private string _worldId = "container-world";
        [SerializeField] private string _solidContentId = "containertest.ice";
        [SerializeField, Min(1)] private float _collectionRate = 2000;
        [SerializeField, Min(.01f)] private float _sampleRadius = .15f;
        [SerializeField, Min(.01f)] private float _reach = 3;
        [SerializeField] private Camera _aimCamera;
        private InputAction _collect;
        private ContainerUseDefinition _definition;
        private void Start(){if(TryGetComponent<ContainerUseController>(out var use))_definition=use.Definition;}
        private readonly CollectionRateAccumulator _rate = new CollectionRateAccumulator();
        private readonly InventoryItemSnapshot[] _items = new InventoryItemSnapshot[256];
        private string _sourceId, _containerId, _backpackId;
        private ulong _sequence, _budget;
        private float _interval;
        private bool _waitCollectRelease;
        private HeldItemInputController _heldInput;
        public string SelectedContainerId => _containerId;
        public MaterialTransferFailure LastFailure { get; private set; }
        public void ConfigurePractice(SinglePlayerInventorySession session, string solidContentId = null, float collectionRate = 0)
        {
            if (!string.IsNullOrEmpty(solidContentId)) _solidContentId = solidContentId;
            if (collectionRate > 0) _collectionRate = collectionRate;
            _session=session;_heldInput=GetComponent<HeldItemInputController>();_aimCamera=Camera.main;
            if(TryGetComponent<ContainerUseController>(out var use))_definition=use.Definition;
            _containerId=_backpackId=null;ResetBudget();_waitCollectRelease=true;
            // 初始化的SelectionChanged发生在订阅之前，主动读取已选快捷格；重复按相同键不会产生新事件。
            if(session.ToolbeltReadOnly.SelectedSlot>=3 && session.ToolbeltReadOnly.TryGetBinding(session.ToolbeltReadOnly.SelectedSlot-3,out var selected))
                OnSelect(new ContainerSelectionRequestedEvent{InventoryId=session.ReadOnly.InventoryId,ItemId=selected});
        }
        private void Awake()
        {
            _sourceId = Guid.NewGuid().ToString("N");
            _heldInput = GetComponent<HeldItemInputController>(); _waitCollectRelease=_heldInput!=null;
            _collect = new InputAction("CollectMaterial", InputActionType.Button, "<Keyboard>/f");
        }
        private void OnEnable()
        { _collect?.Enable(); EventBus<MaterialTransferResultEvent>.Subscribe(OnResult);EventBus<ContainerSelectionRequestedEvent>.Subscribe(OnSelect);
            EventBus<ToolbeltChangedEvent>.Subscribe(OnHeldSelection); EventBus<HeldItemUseResetEvent>.Subscribe(OnUseReset); }
        private void OnDisable()
        { _collect?.Disable(); EventBus<MaterialTransferResultEvent>.Unsubscribe(OnResult);EventBus<ContainerSelectionRequestedEvent>.Unsubscribe(OnSelect);
            EventBus<ToolbeltChangedEvent>.Unsubscribe(OnHeldSelection); EventBus<HeldItemUseResetEvent>.Unsubscribe(OnUseReset); ResetBudget(); }
        private void OnUseReset(HeldItemUseResetEvent e)
        { if (e.InventoryId == _session?.ReadOnly?.InventoryId) { ResetBudget(); _waitCollectRelease=true; } }
        private void OnHeldSelection(ToolbeltChangedEvent e)
        {
            if (_heldInput == null || e.InventoryId != _session?.ReadOnly?.InventoryId) return;
            if (e.SelectedSlot >= 3)
            {
                // 非容器道具不能继续借用之前选中的瓶；F只操作当前持有能力。
                _containerId = null; ResetBudget();
                OnSelect(new ContainerSelectionRequestedEvent { InventoryId = e.InventoryId, ItemId = e.ItemId });
            }
            else { _containerId = null; ResetBudget(); }
        }
        private void OnSelect(ContainerSelectionRequestedEvent e)
        {
            var read=_session?.ReadOnly;if(read==null || e.InventoryId!=read.InventoryId || !read.TryGetView(e.ItemId,out var item)
                || item.Kind!=ItemKind.Container || !IsCarriedContainer(read, item))return;
            // UI可以查看未装备背包的内容，但快捷选瓶必须属于当前携装，不能绕过携带资格。
            _containerId=e.ItemId;_backpackId=_session.EquippedBackpackId;ResetBudget();
            EventBus<ContainerSelectionChangedEvent>.Publish(new ContainerSelectionChangedEvent{InventoryId=read.InventoryId,ItemId=e.ItemId});
        }
        private void OnDestroy()
        { _collect.Dispose(); }
        private void OnResult(MaterialTransferResultEvent result)
        { if (result.SourceId == _sourceId) { LastFailure = result.Failure; if (!result.Success) ResetBudget(); } }
        private void Update()
        {
            var read = _session != null ? _session.ReadOnly : null;
            if (read == null || Time.timeScale <= 0 || _heldInput != null && !_heldInput.CanUseWorld) { ResetBudget(); return; }
            if (_containerId != null && (!read.TryGetView(_containerId, out var selected)
                || !IsCarriedContainer(read, selected)))
            { if (_heldInput == null) SelectNext(read); else _containerId = null; ResetBudget(); }
            // 没有选瓶时仍允许连续采固体；不能每帧清空计时，否则永远等不到下一次10Hz事务。
            if(_containerId==null&&_heldInput==null)SelectNext(read);
            if ((HubInteractInput.Current?.HasStation ?? false) || PortalInteractInput.HasTarget || (NearbyPickupInput.Current?.HasPickup ?? false)
                || NearbyPickupInput.LastPickupFrame == Time.frameCount)
            { if (_collect.IsPressed()) _waitCollectRelease = true; ResetBudget(); return; }
            if (!_collect.IsPressed()) { _waitCollectRelease=false; ResetBudget(); return; }
            // 拾取末件后也等待松开，不能在同一次按键长按尾部顺带抽取地面材料。
            if (_waitCollectRelease) { ResetBudget();return; }
            _budget += _rate.Advance(_definition!=null?_definition.CollectionRate:_collectionRate, Time.deltaTime); _interval += Time.deltaTime;
            // 10Hz 有界事务让小数剂量先积累，再按真实混样比例转移，减少 1 GMU 舍入和几何更新频率。
            if ((_interval < .1f&&!_collect.WasPressedThisFrame()) || _budget == 0) return;
            ulong budget = System.Math.Min(4096UL,_budget); _budget = 0; _interval = 0; RequestAtAim(_containerId==null, budget);
        }
        private static bool IsCarriedContainer(IInventoryReadOnly read, in InventoryItemSnapshot item)
            => item.Location.Kind == ItemLocationKind.EquippedTool && item.Location.OwnerId == read.ActorId
                || item.Location.Kind == ItemLocationKind.Backpack && read.TryGetView(item.Location.OwnerId, out var bag)
                && bag.Location.Kind == ItemLocationKind.EquippedBackpack && bag.Location.OwnerId == read.ActorId;
        private void SelectNext(IInventoryReadOnly read)
        {
            if (read.Count > _items.Length) { LastFailure = MaterialTransferFailure.SamplingLimit; _containerId = null; return; }
            int count = read.CopyItemsTo(_items); string first = null, next = null; bool after = _containerId == null;
            _backpackId = null;
            for (int i = 0; i < count; i++) if (_items[i].Kind == ItemKind.Backpack && _items[i].Location.Kind == ItemLocationKind.EquippedBackpack) _backpackId = _items[i].InstanceId;
            for (int i = 0; i < count; i++)
            {
                var item = _items[i];
                if (item.Kind != ItemKind.Container || !IsCarriedContainer(read, item)) continue;
                if (first == null) first = item.InstanceId;
                if (after && next == null) next = item.InstanceId;
                if (item.InstanceId == _containerId) after = true;
            }
            _containerId = next ?? first;
            EventBus<ContainerSelectionChangedEvent>.Publish(new ContainerSelectionChangedEvent { InventoryId = read.InventoryId, ItemId = _containerId });
        }
        private void RequestAtAim(bool solid, ulong budget)
        {
            Camera camera = _aimCamera != null ? _aimCamera : Camera.main;
            if (camera == null) return;
            var ray = camera.ViewportPointToRay(new Vector3(.5f, .5f, 0));
            Vector3 point = transform.position + transform.forward * Mathf.Min(_reach, 2);
            float limit = 100;
            int hits = Physics.RaycastNonAlloc(ray, _aimHits, 100, ~0, QueryTriggerInteraction.Ignore);
            if (hits == _aimHits.Length) { LastFailure = MaterialTransferFailure.SamplingLimit; return; }
            for (int i = 0; i < hits; i++)
                if (!_aimHits[i].transform.IsChildOf(transform) && _aimHits[i].distance < limit)
                { limit = _aimHits[i].distance; point = _aimHits[i].point + _aimHits[i].normal * .03f; }
            ushort allowed = (ushort)((1 << 5) | (1 << 6));
            if (!solid && _session != null && _session.ReadOnly.TryGetView(_containerId, out var container))
                allowed |= container.Rules.ContainerMaterials;
            _aim.WorldId = _worldId; _aim.RayOrigin = ray.origin; _aim.RayDirection = ray.direction;
            _aim.ActorPosition = transform.position; _aim.Reach = _reach; _aim.MaximumRayDistance = limit + .03f;
            _aim.AllowedMaterials = allowed; _aim.Handled = false; _aim.Found = false;
            _aim.SampleRadius=_definition!=null?_definition.CollectionRadius:_sampleRadius;
            EventBus<MaterialCollectionAimRequestedEvent>.Publish(new MaterialCollectionAimRequestedEvent { Exchange = _aim });
            string content = _solidContentId;
            if (_aim.Found)
            {
                point = _aim.Point;
                solid=_aim.MaterialKey==5||_aim.MaterialKey==6;
                if (solid && !_session.TryFindMaterialContent(_aim.MaterialKey, out content))
                { LastFailure = MaterialTransferFailure.NoMaterial; return; }
            }
            else { LastFailure=MaterialTransferFailure.NoMaterial;return; }
            SubmitSampleInternal(point, solid, budget, content,_definition!=null?_definition.CollectionRadius:_sampleRadius);
        }
        private readonly MaterialCollectionAimExchange _aim = new MaterialCollectionAimExchange();
        private readonly RaycastHit[] _aimHits = new RaycastHit[32];
        public void SubmitSample(Vector3 point, bool solid, ulong budget)
            => SubmitSampleInternal(point, solid, budget, _solidContentId,_sampleRadius);
        private void SubmitSampleInternal(Vector3 point, bool solid, ulong budget, string content,float radius)
        {
            if (_session == null || _session.ReadOnly == null || Time.timeScale <= 0) return;
            if (solid) _backpackId = _session.EquippedBackpackId;
            if (solid ? _backpackId == null : _containerId == null)
            { if (_heldInput == null) SelectNext(_session.ReadOnly); else { LastFailure = MaterialTransferFailure.WrongOwnership; return; } }
            EventBus<MaterialTransferRequestedEvent>.Publish(new MaterialTransferRequestedEvent
            { InventoryId = _session.ReadOnly.InventoryId, WorldId = _worldId, SourceId = _sourceId, RequestId = ++_sequence,
                ItemId = _containerId, BackpackId = _backpackId, MaterialContentId = content, SolidItem = solid,
                Budget = budget, ExpectedVersion = _session.ReadOnly.Version, CollectorPosition = transform.position, SampleCenter = point, Radius = radius });
        }
        private void ResetBudget() { _rate.Reset(); _budget = 0; _interval = 0; }
    }
}
