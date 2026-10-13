using System;
using Game.Combat;
using Game.Core;
using Game.Run;
using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Profiling;

namespace Game.Character
{
    /// <summary>持续撒出和离散投掷分开输入；只通过Core取得资产批准与世界真实接收。</summary>
    [DefaultExecutionOrder(1000)]
    public sealed class ContainerUseController : MonoBehaviour
    {
        [SerializeField] private SinglePlayerInventorySession _session;
        [SerializeField] private MaterialCollectionController _collection;
        [SerializeField] private ContainerUseDefinition _definition;
        [SerializeField] private string _worldId = "container-world";
        private InputAction _pour, _throw, _solid;
        private HeldItemInputController _heldInput;
        private string _sourceId;
        private string _lastContainerId;
        private ulong _sequence, _budget;
        private float _interval;
        private bool _throwQueued, _solidQueued, _waitPourRelease;
        private bool _aiming, _waitThrowRelease, _focused = true;
        private Camera _camera;
        private PlayerControllerBase _player;
        private readonly CollectionRateAccumulator _rate = new CollectionRateAccumulator();
        private readonly InventoryItemSnapshot[] _items = new InventoryItemSnapshot[256];
        private LineRenderer _trajectory;
        private Material _trajectoryMaterial;
        private readonly RaycastHit[] _trajectoryHits = new RaycastHit[16];
        private const int TrajectorySamples = 48;
        private static readonly ProfilerMarker TrajectoryMarker = new ProfilerMarker("Norn.ContainerThrow.Trajectory");
        public ContainerUseResultEvent LastResult { get; private set; }
        public void ConfigurePractice(SinglePlayerInventorySession session,ContainerUseDefinition definition)
        {
            _session=session;_definition=definition;_collection=GetComponent<MaterialCollectionController>();
            _heldInput=GetComponent<HeldItemInputController>();Reset();_waitPourRelease=true;_throwQueued=_solidQueued=false;
        }
        private void Awake()
        {
            _heldInput = GetComponent<HeldItemInputController>();_waitPourRelease=_heldInput!=null;
            _sourceId = Guid.NewGuid().ToString("N");
            if (_collection == null) _collection = GetComponent<MaterialCollectionController>();
            _pour = new InputAction("PourMaterial", InputActionType.Button, "<Keyboard>/h");
            _throw = new InputAction("ThrowContainer", InputActionType.Button, "<Keyboard>/g");
            _solid = new InputAction("ReleaseSolid", InputActionType.Button, "<Keyboard>/k");
            _camera = Camera.main; _player = GetComponent<PlayerControllerBase>();
            _throw.performed += OnAimStarted; _throw.canceled += OnAimReleased; _solid.performed += OnSolid;
            var line = new GameObject("ContainerThrowTrajectory"); line.transform.SetParent(transform, false);
            _trajectory = line.AddComponent<LineRenderer>(); _trajectory.useWorldSpace = true;
            _trajectory.widthMultiplier = .025f; _trajectory.positionCount = TrajectorySamples;
            _trajectory.startColor = _trajectory.endColor = new Color(.8f, 1, 1, .8f);
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader != null) { _trajectoryMaterial = new Material(shader); _trajectoryMaterial.SetColor("_BaseColor", Color.white); _trajectory.sharedMaterial = _trajectoryMaterial; }
            _trajectory.enabled = false;
        }
        private void OnEnable()
        { _pour.Enable(); _throw.Enable(); _solid.Enable(); EventBus<ContainerThrownEvent>.Subscribe(OnPrepareThrow); EventBus<ContainerUseResultEvent>.Subscribe(OnResult);
            EventBus<HeldItemUseResetEvent>.Subscribe(OnUseReset); }
        private void OnDisable()
        { _pour.Disable(); _throw.Disable(); _solid.Disable(); EventBus<ContainerThrownEvent>.Unsubscribe(OnPrepareThrow); EventBus<ContainerUseResultEvent>.Unsubscribe(OnResult);
            EventBus<HeldItemUseResetEvent>.Unsubscribe(OnUseReset); Reset(); CancelAim(); _solidQueued = false; if (_trajectory != null) _trajectory.enabled = false; }
        private void OnUseReset(HeldItemUseResetEvent e)
        { if (e.InventoryId == _session?.ReadOnly?.InventoryId) { Reset(); CancelAim(); _solidQueued = false; _waitPourRelease=true; } }
        private void OnDestroy()
        { _throw.performed -= OnAimStarted; _throw.canceled -= OnAimReleased; _solid.performed -= OnSolid; _pour.Dispose(); _throw.Dispose(); _solid.Dispose(); if (_trajectoryMaterial != null) Destroy(_trajectoryMaterial); }
        private bool CanInput => _focused && Time.timeScale > 0 && _session?.ReadOnly != null && _definition != null
            && (_heldInput == null || _heldInput.CanUseWorld);
        private void OnApplicationFocus(bool focused) { _focused = focused; CancelAim(); _waitPourRelease = true; }
        private void OnAimStarted(InputAction.CallbackContext _)
        {
            string selected = _collection?.SelectedContainerId;
            if (CanInput && !_waitThrowRelease && selected != null && _session.ReadOnly.TryGetView(selected, out var item) && item.WorldModel != null) _aiming = true;
        }
        private void OnAimReleased(InputAction.CallbackContext _) { if (_aiming && CanInput) _throwQueued = true; }
        private void CancelAim() { _aiming = _throwQueued = false; _waitThrowRelease = true; }
        private void OnSolid(InputAction.CallbackContext _) { if (Time.timeScale > 0) _solidQueued = true; }
        private void OnResult(ContainerUseResultEvent result)
        { if (result.Transfer.SourceId == _sourceId) { LastResult = result;
                if (!result.Success && result.Transfer.RequestId != 0 && result.Kind == ContainerUseKind.Pour) Reset(); } }
        private void LateUpdate()
        {
            // Player的轨道相机及Cinemachine在LateUpdate更新；晚执行后再采样，避免轨迹落后视野一帧。
            if (!CanInput)
            { Reset(); CancelAim(); _solidQueued = false; if (_trajectory != null) _trajectory.enabled = false; return; }
            string selected = _collection != null ? _collection.SelectedContainerId : null;
            if (selected != _lastContainerId) { _lastContainerId = selected; Reset(); CancelAim(); }
            if (!_throw.IsPressed()) _waitThrowRelease = false;
            Vector3 target = transform.position + transform.forward * _definition.ForwardDistance + Vector3.up * .25f;
            if (_solidQueued) { _solidQueued = false; SubmitUse(ContainerUseKind.ReleaseSolid, 100, target); }
            if (!_pour.IsPressed()) _waitPourRelease=false;
            bool pouring = (_pour.IsPressed() && !_waitPourRelease) || (_heldInput != null && _heldInput.CanPour);
            if (_aiming)
            {
                AlignWithCamera(); UpdateTrajectory(selected, ThrowVelocity(), true);
                if (_throwQueued)
                { _throwQueued = false; SubmitUse(ContainerUseKind.Throw, 0, target); _aiming = false; _trajectory.enabled = false; Reset(); }
                return; // G瞄准优先，不能同时撒出正在瞄准瓶中的材料。
            }
            if (pouring) AlignWithCamera();
            target = UpdateTrajectory(selected, PourVelocity(), pouring);
            if (!pouring) { Reset(); return; }
            _budget += _rate.Advance(_definition.PourRate, Time.deltaTime); _interval += Time.deltaTime;
            if (_interval < .1f || _budget == 0) return;
            ulong budget = _budget; _budget = 0; _interval = 0; SubmitUse(ContainerUseKind.Pour, budget, target);
        }
        public void SubmitUse(ContainerUseKind kind, ulong budget, Vector3 target)
        {
            if (_session?.ReadOnly == null || _definition == null || Time.timeScale <= 0) return;
            string itemId = _collection != null ? _collection.SelectedContainerId : null;
            if (kind == ContainerUseKind.ReleaseSolid)
            {
                var read = _session.ReadOnly; if (read.Count > _items.Length) return;
                int count = read.CopyItemsTo(_items); itemId = null;
                string bag = null;
                for (int i = 0; i < count; i++) if (_items[i].Kind == ItemKind.Backpack
                    && _items[i].Location.Kind == ItemLocationKind.EquippedBackpack && _items[i].Location.OwnerId == read.ActorId) bag = _items[i].InstanceId;
                for (int i = 0; i < count; i++) if (_items[i].Kind == ItemKind.Material && _items[i].Location.Kind == ItemLocationKind.Backpack && _items[i].Location.OwnerId == bag)
                { itemId = _items[i].InstanceId; break; }
            }
            EventBus<ContainerUseRequestedEvent>.Publish(new ContainerUseRequestedEvent
            { InventoryId = _session.ReadOnly.InventoryId, WorldId = _worldId, SourceId = _sourceId, ItemId = itemId,
                Kind = kind, RequestId = ++_sequence, Budget = budget, ExpectedVersion = _session.ReadOnly.Version,
                ActorPosition = transform.position, TargetPosition = target, Radius = _definition.DepositRadius,
                ThrowVelocity = ThrowVelocity() });
        }
        private void OnPrepareThrow(ContainerThrownEvent command)
        {
            var exchange = command.Exchange;
            if (exchange == null || exchange.Proxy != null || exchange.Request.SourceId != _sourceId || _definition?.ThrowPrefab == null) return;
            if (_session?.ReadOnly == null || !_session.ReadOnly.TryGetView(exchange.Request.ItemId, out var item) || item.WorldModel == null)
            { GameLog.Warn("容器缺少 WorldModel，投掷取消并保留物品。", "ContainerUse"); return; }
            var go = Instantiate(_definition.ThrowPrefab, ThrowOrigin(), Quaternion.identity);
            go.SetActive(false);
            go.transform.localScale = Vector3.one;
            // 固定物理根只负责碰撞；去掉旧球体显示，纯模型不允许附带另一套行为/物理权威。
            foreach (var renderer in go.GetComponentsInChildren<Renderer>(true)) renderer.enabled = false;
            var model = Instantiate(item.WorldModel, go.transform, false); model.name = "ThrownContainerModel"; model.SetActive(true);
            foreach (var behaviour in model.GetComponentsInChildren<MonoBehaviour>(true)) behaviour.enabled = false;
            foreach (var collider in model.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
            foreach (var body in model.GetComponentsInChildren<Rigidbody>(true)) { body.isKinematic = true; body.detectCollisions = false; }
            var renderers = model.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) { Destroy(go); return; }
            Bounds bounds = renderers[0].bounds; for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            float largest = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
            if (largest > .001f) model.transform.localScale *= .45f / largest;
            // 以模型Bounds居中，修正导入模型pivot位于底部或远离mesh时的旋转/轨迹错位。
            bounds = renderers[0].bounds; for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            model.transform.position += go.transform.position - bounds.center;
            if (go.GetComponent<SphereCollider>() is SphereCollider sphere) { sphere.center = Vector3.zero; sphere.radius = .18f; }
            // Additive Scene 下 Instantiate 默认进入active Scene；代理必须跟随玩家世界卸载。
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, gameObject.scene);
            var projectile = go.GetComponent<ThrownContainerProjectile>();
            if (projectile == null) { Destroy(go); return; }
            try
            { projectile.Prepare(exchange.Request.InventoryId, exchange.Request.WorldId, exchange.FlightId,
                exchange.Request.ThrowVelocity, _definition.FlightSeconds, _definition.MaximumFlightDistance, GetComponent<Collider>());
                exchange.Proxy = projectile; }
            catch { Destroy(go); throw; }
        }
        private Vector3 ThrowOrigin() => transform.position + transform.forward * .8f + Vector3.up * 1.2f;
        private void AlignWithCamera()
        {
            Vector3 horizontal = _camera != null ? _camera.transform.forward : transform.forward; horizontal.y = 0;
            if (horizontal.sqrMagnitude < .001f) return; horizontal.Normalize();
            transform.rotation = Quaternion.LookRotation(horizontal, Vector3.up);
            if (_player != null) _player.TargetFacing = horizontal;
        }
        private Vector3 PourVelocity() => (_camera != null ? _camera.transform.forward : transform.forward) * _definition.PourSpeed + Vector3.up * _definition.PourLift;
        private Vector3 ThrowVelocity()
        {
            // 保留完整镜头pitch；只额外加上扬速度，不能把俯视方向夹成固定水平弧线。
            Vector3 direction = _camera != null ? _camera.transform.forward : transform.forward;
            return direction * _definition.ThrowSpeed + Vector3.up * _definition.ThrowLift;
        }
        private Vector3 UpdateTrajectory(string selected, Vector3 velocity, bool active)
        {
            using var sample = TrajectoryMarker.Auto();
            Vector3 origin = ThrowOrigin();
            if (_trajectory == null) return origin;
            bool visible = active && selected != null && _session.ReadOnly.TryGetView(selected, out var item) && item.WorldModel != null;
            _trajectory.enabled = visible; if (!visible) return origin;
            Vector3 previous = origin;
            _trajectory.positionCount = TrajectorySamples; _trajectory.SetPosition(0, origin);
            // 与Rigidbody同用Physics.gravity；解析抛物线 p=p0+v0*t+0.5*g*t²，固定数组保证预览无GC。
            for (int i = 1; i < TrajectorySamples; i++)
            {
                float t = i * .065f; Vector3 point = origin + velocity * t + Physics.gravity * (.5f * t * t);
                Vector3 delta = point - previous; float distance = delta.magnitude;
                int hits = Physics.SphereCastNonAlloc(previous, .18f, delta.normalized, _trajectoryHits, distance, ~0, QueryTriggerInteraction.Ignore);
                float nearest = distance; bool hit = false;
                for (int j = 0; j < hits; j++)
                    if (!_trajectoryHits[j].collider.transform.IsChildOf(transform) && _trajectoryHits[j].distance < nearest)
                    { nearest = _trajectoryHits[j].distance; point = _trajectoryHits[j].point; hit = true; }
                _trajectory.SetPosition(i, point);
                if (hit) { _trajectory.positionCount = i + 1; return point; }
                previous = point;
            }
            return previous;
        }
        private void Reset() { _rate.Reset(); _budget = 0; _interval = 0; }
    }
}
