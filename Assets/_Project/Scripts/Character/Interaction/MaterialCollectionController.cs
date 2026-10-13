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
        private InputAction _collect, _solid;
        private readonly CollectionRateAccumulator _rate = new CollectionRateAccumulator();
        private readonly InventoryItemSnapshot[] _items = new InventoryItemSnapshot[256];
        private string _sourceId, _containerId, _backpackId;
        private ulong _sequence, _budget;
        private float _interval;
        private bool _solidQueued, _waitCollectRelease;
        private HeldItemInputController _heldInput;
        public string SelectedContainerId => _containerId;
        public MaterialTransferFailure LastFailure { get; private set; }
        public void ConfigurePractice(SinglePlayerInventorySession session, string solidContentId = null, float collectionRate = 0)
        {
            if (!string.IsNullOrEmpty(solidContentId)) _solidContentId = solidContentId;
            if (collectionRate > 0) _collectionRate = collectionRate;
            _session=session;_heldInput=GetComponent<HeldItemInputController>();_aimCamera=Camera.main;
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
            _solid = new InputAction("CollectSolid", InputActionType.Button, "<Keyboard>/f");
            _solid.performed += OnSolid;
        }
        private void OnEnable()
        { _collect?.Enable(); _solid?.Enable(); EventBus<MaterialTransferResultEvent>.Subscribe(OnResult);EventBus<ContainerSelectionRequestedEvent>.Subscribe(OnSelect);
            EventBus<ToolbeltChangedEvent>.Subscribe(OnHeldSelection); EventBus<HeldItemUseResetEvent>.Subscribe(OnUseReset); }
        private void OnDisable()
        { _collect?.Disable(); _solid?.Disable(); EventBus<MaterialTransferResultEvent>.Unsubscribe(OnResult);EventBus<ContainerSelectionRequestedEvent>.Unsubscribe(OnSelect);
            EventBus<ToolbeltChangedEvent>.Unsubscribe(OnHeldSelection); EventBus<HeldItemUseResetEvent>.Unsubscribe(OnUseReset); ResetBudget(); _solidQueued = false; }
        private void OnUseReset(HeldItemUseResetEvent e)
        { if (e.InventoryId == _session?.ReadOnly?.InventoryId) { ResetBudget(); _solidQueued = false; _waitCollectRelease=true; } }
        private void OnHeldSelection(ToolbeltChangedEvent e)
        {
            if (_heldInput == null || e.InventoryId != _session?.ReadOnly?.InventoryId) return;
            if (e.SelectedSlot >= 3) OnSelect(new ContainerSelectionRequestedEvent { InventoryId = e.InventoryId, ItemId = e.ItemId });
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
        { _solid.performed -= OnSolid; _collect.Dispose(); _solid.Dispose(); }
        private void OnSolid(InputAction.CallbackContext _) { if (Time.timeScale > 0) _solidQueued = true; }
        private void OnResult(MaterialTransferResultEvent result)
        { if (result.SourceId == _sourceId) { LastFailure = result.Failure; if (!result.Success) ResetBudget(); } }
        private void Update()
        {
            var read = _session != null ? _session.ReadOnly : null;
            if (read == null || Time.timeScale <= 0 || _heldInput != null && !_heldInput.CanUseWorld) { ResetBudget(); _solidQueued = false; return; }
            if (_containerId == null || !read.TryGetView(_containerId, out var selected)
                || !IsCarriedContainer(read, selected))
            { if (_heldInput == null) SelectNext(read); else _containerId = null; ResetBudget(); }
            if ((HubInteractInput.Current?.HasStation ?? false) || PortalInteractInput.HasTarget || (NearbyPickupInput.Current?.HasPickup ?? false)
                || NearbyPickupInput.LastPickupFrame == Time.frameCount)
            { _solidQueued = false; if (_collect.IsPressed()) _waitCollectRelease = true; ResetBudget(); return; }
            if (_solidQueued)
            {
                _solidQueued = false; RequestAtAim(true, 100);
                // 同一个F先处理固体拾取；目标不是固体时继续原来的容器长按采集。
                if (LastFailure == MaterialTransferFailure.None) { _waitCollectRelease = true; ResetBudget(); return; }
            }
            if (!_collect.IsPressed()) { _waitCollectRelease=false; ResetBudget(); return; }
            // 拾取末件后也等待松开，不能在同一次按键长按尾部顺带抽取地面材料。
            if (_waitCollectRelease) { ResetBudget();return; }
            if (_containerId == null) return;
            _budget += _rate.Advance(_collectionRate, Time.deltaTime); _interval += Time.deltaTime;
            // 10Hz 有界事务让小数剂量先积累，再按真实混样比例转移，减少 1 GMU 舍入和几何更新频率。
            if (_interval < .1f || _budget == 0) return;
            ulong budget = _budget; _budget = 0; _interval = 0; RequestAtAim(false, budget);
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
            Vector3 point;
            if (Physics.Raycast(ray, out var hit, 100, ~0, QueryTriggerInteraction.Ignore)) point = hit.point + hit.normal * .03f;
            else point = transform.position + transform.forward * Mathf.Min(_reach, 2);
            SubmitSample(point, solid, budget);
        }
        public void SubmitSample(Vector3 point, bool solid, ulong budget)
        {
            if (_session == null || _session.ReadOnly == null || Time.timeScale <= 0) return;
            if (solid) _backpackId = _session.EquippedBackpackId;
            if (solid ? _backpackId == null : _containerId == null)
            { if (_heldInput == null) SelectNext(_session.ReadOnly); else { LastFailure = MaterialTransferFailure.WrongOwnership; return; } }
            EventBus<MaterialTransferRequestedEvent>.Publish(new MaterialTransferRequestedEvent
            { InventoryId = _session.ReadOnly.InventoryId, WorldId = _worldId, SourceId = _sourceId, RequestId = ++_sequence,
                ItemId = _containerId, BackpackId = _backpackId, MaterialContentId = _solidContentId, SolidItem = solid,
                Budget = budget, ExpectedVersion = _session.ReadOnly.Version, CollectorPosition = transform.position, SampleCenter = point, Radius = _sampleRadius });
        }
        private void ResetBudget() { _rate.Reset(); _budget = 0; _interval = 0; }
    }
}
