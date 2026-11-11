using Game.Core;
using Game.Run;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Character
{
    /// <summary>
    /// 已认证本地Owner的F/H/G/K输入桥。只读取Driver解析的确认实例并发送意图，不持有单机Session或修改组分。
    /// F/H有界10Hz，G有效瞄准松开一次，K包内首材料按下一次；真实输出与数量仍由DS/Run事务裁决。
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(1000)]
    public sealed class OnlineContainerInput : MonoBehaviour
    {
        private OnlineCastDriver _driver;
        private Camera _camera;
        private InputAction _collect, _pour, _left, _throw, _solid;
        private readonly CollectionRateAccumulator _collectRate = new CollectionRateAccumulator();
        private readonly CollectionRateAccumulator _pourRate = new CollectionRateAccumulator();
        private ulong _collectBudget, _pourBudget;
        private float _collectInterval, _pourInterval;
        private string _collectItem, _pourItem, _throwItem;
        private int _throwSlot = -1;
        private uint _epoch;
        private bool _waitCollectRelease = true, _waitPourRelease = true, _focused = true, _paused, _subscribed, _disposed;
        private bool _waitThrowRelease = true, _throwAiming;
        private bool _waitSolidRelease = true;
        private GameObject _trajectoryRoot;
        private LineRenderer _trajectory;
        private Material _trajectoryMaterial;
        private readonly Vector3[] _points = new Vector3[OnlineContainerTrajectory.Samples];
        private readonly RaycastHit[] _hits = new RaycastHit[16];

        /// <summary>Driver只给本地Owner调用；Camera可由共享表现层晚到后绑定，不创建第二套相机。</summary>
        public void Configure(OnlineCastDriver driver, Camera camera = null)
        {
            if (_disposed) return;
            Awake();
            if (driver == null || !driver.IsLocalContainerOwner)
            {
                _driver = null; _camera = null; OnDisable(); enabled = false; return;
            }
            bool changed = !ReferenceEquals(_driver, driver);
            _driver = driver;
            if (changed) { _camera = null; ResetHeldState(); }
            if (camera != null && _camera != camera) { _camera = camera; ResetHeldState(); }
            enabled = true;
            if (isActiveAndEnabled) { SubscribeCamera(); EnableActions(); ResolveCamera(); }
        }

        internal void Detach(OnlineCastDriver owner)
        {
            if (_disposed || !ReferenceEquals(_driver, owner)) return;
            _driver = null; _camera = null; OnDisable(); enabled = false;
        }

        internal void DisposeForOwner(OnlineCastDriver owner)
        {
            if (_disposed || !ReferenceEquals(_driver, owner)) return;
            OnDestroy();
        }

        private void Awake()
        {
            if (_disposed || _collect != null) return;
            _collect = new InputAction("OnlineCollectMaterial", InputActionType.Button, "<Keyboard>/f");
            _pour = new InputAction("OnlinePourMaterial", InputActionType.Button, "<Keyboard>/h");
            _left = new InputAction("OnlineUseHeldContainer", InputActionType.Button, "<Mouse>/leftButton");
            _throw = new InputAction("OnlineThrowContainer", InputActionType.Button, "<Keyboard>/g");
            _solid = new InputAction("OnlineReleaseSolid", InputActionType.Button, "<Keyboard>/k");
            _trajectoryRoot = new GameObject("OnlineContainerPourTrajectory");
            _trajectoryRoot.transform.SetParent(transform, false);
            _trajectory = _trajectoryRoot.AddComponent<LineRenderer>();
            _trajectory.useWorldSpace = true; _trajectory.widthMultiplier = .025f;
            _trajectory.startColor = _trajectory.endColor = new Color(.8f, 1, 1, .8f);
            _trajectory.positionCount = 0; _trajectory.enabled = false;
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader != null)
            {
                _trajectoryMaterial = new Material(shader);
                _trajectoryMaterial.SetColor("_BaseColor", Color.white);
                _trajectory.sharedMaterial = _trajectoryMaterial;
            }
        }

        private void OnEnable()
        {
            Awake(); _focused = Application.isFocused; _paused = false; ResetHeldState();
            if (_driver == null || !_driver.IsLocalContainerOwner) return;
            SubscribeCamera(); EnableActions(); ResolveCamera();
        }
        private void EnableActions() { _collect.Enable(); _pour.Enable(); _left.Enable(); _throw.Enable(); _solid.Enable(); }
        private void SubscribeCamera()
        {
            if (_subscribed) return;
            EventBus<LocalActorCameraBoundEvent>.Subscribe(OnCamera); _subscribed = true;
        }
        private void OnCamera(LocalActorCameraBoundEvent value)
        {
            if (_driver == null) return;
            if (value.Actor == _driver.gameObject && value.Camera != null)
            {
                // 同一Camera的重复注册是幂等表现通知，不是换镜头；不能清除持键10Hz预算。
                // 真正换Camera仍要求松键，防止旧朝向/按住状态跨表现生命周期继续提交。
                if (_camera == value.Camera) return;
                _camera = value.Camera; ResetHeldState();
            }
            else if (value.Actor == null && _camera != null) { _camera = null; ResetHeldState(); }
        }
        private bool ResolveCamera()
        {
            if (_camera != null && _camera.isActiveAndEnabled) return true;
            if (_driver != null && LocalActorCameraPresentation.TryGet(_driver.gameObject, out var shared)
                && shared.isActiveAndEnabled) { _camera = shared; return true; }
            return false;
        }
        private void LateUpdate() => Step(Time.deltaTime);

        /// <summary>
        /// 正式LateUpdate和真实InputSystem测试共用此入口。只观察选择ID/Epoch与生命周期，
        /// 不用库存版本清计时：同一瓶的Owner快照更新不能让持续动作永远达不到10Hz。
        /// </summary>
        public void Step(float deltaTime)
        {
            if (_disposed) return;
            if (!isActiveAndEnabled || _driver == null || !_driver.CanUseContainerHeldInput || !_focused || _paused
                || !Application.isFocused || Time.timeScale <= 0 || !float.IsFinite(deltaTime) || deltaTime < 0 || !ResolveCamera())
            { ResetHeldState(); return; }
            var definition = _driver.ClientContainerUse;
            uint epoch = _driver.ContainerInputEpoch;
            if (_epoch != epoch) { ResetHeldState(); _epoch = epoch; }
            // K沿原自动包内第一材料规则；一次按下只提出一次请求，没有F/H计时预算，也不继承失焦/重连旧按住。
            if (!_solid.IsPressed()) _waitSolidRelease = false;
            else if (!_waitSolidRelease) { _waitSolidRelease = true; _driver.RequestReleaseSolid(); }
            bool hasCollect = _driver.TryGetClientContainer(OnlineInventoryCommandKind.CollectMaterial, out var collect);
            bool hasPour = _driver.TryGetClientContainer(OnlineInventoryCommandKind.PourContainer, out var pour);
            bool hasThrow = _driver.TryGetClientContainer(OnlineInventoryCommandKind.ThrowContainer, out var thrown);
            string collectId = hasCollect ? collect.InstanceId : null, pourId = hasPour ? pour.InstanceId : null;
            if (_collectItem != collectId) { ResetCollect(true); _collectItem = collectId; }
            if (_pourItem != pourId) { ResetPour(true); _pourItem = pourId; }
            string throwId = hasThrow ? thrown.InstanceId : null;
            int throwSlot = hasThrow ? thrown.Location.SlotIndex : -1;
            if (_throwItem != throwId || _throwSlot != throwSlot)
            { ResetThrow(true); _throwItem = throwId; _throwSlot = throwSlot; }
            Ray ray = _camera.ViewportPointToRay(new Vector3(.5f, .5f, 0));

            // G的瞄准资格属于当前瓶、工具槽与Epoch。生命周期/选择变化会清掉_正在瞄准_，
            // 因而失焦后迟到的松键只能解除等待，不能把旧瞄准改投成当前另一只瓶。
            bool throwHeld = _throw.IsPressed(), throwReleased = false;
            if (!throwHeld)
            {
                if (_throwAiming)
                {
                    _throwAiming = false; throwReleased = true;
                    _driver.RequestContainerThrow(ray);
                }
                _waitThrowRelease = false;
            }
            else if (!hasThrow) ResetThrow(true);
            else if (!_waitThrowRelease) _throwAiming = true;

            bool collectHeld = _collect.IsPressed();
            bool priority = (HubInteractInput.Current?.HasStation ?? false) || PortalInteractInput.HasTarget
                || (NearbyPickupInput.Current?.HasPickup ?? false) || NearbyPickupInput.LastPickupFrame == Time.frameCount;
            if (!collectHeld) ResetCollect(false);
            else if (priority || !hasCollect) ResetCollect(true);
            else if (!_waitCollectRelease)
                Advance(OnlineInventoryCommandKind.CollectMaterial, definition.CollectionRate, 4096, deltaTime, ray, true);

            // H沿有效容器规则，左键只代表当前真实手持容器。两种按键合并成一个流，不能双倍扣料。
            bool heldContainer = hasPour && pour.Location.Kind == ItemLocationKind.EquippedTool;
            bool pourHeld = _pour.IsPressed() || heldContainer && _left.IsPressed();
            // 瞄准和释放这一帧均停止H/左键，并丢弃原预算；恢复必须先看见H/左键松开，不能补倒一批。
            if (throwHeld || throwReleased) ResetPour(true);
            else if (!pourHeld) ResetPour(false);
            else if (!hasPour) ResetPour(true);
            else if (!_waitPourRelease)
            {
                double rate = (double)definition.PourRate * (_driver.ClientContainerContainsGas(pour.InstanceId) ? definition.GasPourMultiplier : 1);
                Advance(OnlineInventoryCommandKind.PourContainer, rate, System.Math.Min(1024UL, definition.MaximumPourBatchGmu), deltaTime, ray, false);
            }
            if (throwHeld) UpdatePreview(_throwAiming, thrown, definition, ray.direction, true);
            else UpdatePreview(!throwReleased && hasPour && pourHeld && !_waitPourRelease, pour, definition, ray.direction, false);
        }

        private void Advance(OnlineInventoryCommandKind kind, double rate, ulong capacity, float deltaTime, Ray ray, bool collect)
        {
            if (capacity == 0) { if (collect) ResetCollect(true); else ResetPour(true); return; }
            ulong budget = System.Math.Min(capacity, collect ? _collectBudget : _pourBudget);
            float interval = collect ? _collectInterval : _pourInterval;
            ulong gained = (collect ? _collectRate : _pourRate).Advance(rate, deltaTime);
            // 先算剩余空间再加，避免大帧或异常速率制造溢出/补追债务；每次Step每流至多发一批。
            budget += System.Math.Min(capacity - budget, gained);
            interval = Mathf.Min(.1f, interval + deltaTime);
            if (interval >= .1f && budget != 0)
            {
                bool accepted = _driver.RequestContainerTransfer(kind, budget, ray);
                budget = 0; interval = 0;
                if (!accepted) { if (collect) ResetCollect(true); else ResetPour(true); return; }
            }
            if (collect) { _collectBudget = budget; _collectInterval = interval; }
            else { _pourBudget = budget; _pourInterval = interval; }
        }

        private void UpdatePreview(bool active, in InventoryItemSnapshot item, ContainerUseDefinition definition, Vector3 direction, bool throwing)
        {
            if (_trajectory == null) return;
            if (!active || item.WorldModel == null)
            { _trajectory.enabled = false; return; }
            // 只切换原Definition中的投掷参数；仍复用48点、.065秒、.18碰撞半径的同一条几何路径。
            var failure = OnlineContainerTrajectory.Prepare(transform, direction,
                throwing ? definition.ThrowSpeed : definition.PourSpeed, throwing ? definition.ThrowLift : definition.PourLift,
                _hits, _points, out int count, out _);
            if (failure != MaterialTransferFailure.None) { _trajectory.enabled = false; return; }
            _trajectory.positionCount = count;
            for (int i = 0; i < count; i++) _trajectory.SetPosition(i, _points[i]);
            _trajectory.enabled = true;
        }
        private void ResetCollect(bool wait)
        { _collectBudget = 0; _collectInterval = 0; _collectRate.Reset(); _waitCollectRelease = wait; }
        private void ResetPour(bool wait)
        { _pourBudget = 0; _pourInterval = 0; _pourRate.Reset(); _waitPourRelease = wait; }
        private void ResetThrow(bool wait) { _throwAiming = false; _waitThrowRelease = wait; }
        internal void ResetHeldState()
        {
            ResetCollect(true); ResetPour(true); ResetThrow(true);
            _waitSolidRelease = true;
            _collectItem = _pourItem = _throwItem = null; _throwSlot = -1; _epoch = 0;
            if (_trajectory != null) _trajectory.enabled = false;
        }
        private void OnApplicationFocus(bool focused) { _focused = focused; ResetHeldState(); }
        private void OnApplicationPause(bool paused) { _paused = paused; ResetHeldState(); }
        private void OnDisable()
        {
            _collect?.Disable(); _pour?.Disable(); _left?.Disable(); _throw?.Disable(); _solid?.Disable();
            if (_subscribed) EventBus<LocalActorCameraBoundEvent>.Unsubscribe(OnCamera);
            _subscribed = false; ResetHeldState();
        }
        private void OnDestroy()
        {
            if (_disposed) return;
            OnDisable(); _disposed = true; _driver = null; _camera = null;
            _collect?.Dispose(); _pour?.Dispose(); _left?.Dispose(); _throw?.Dispose(); _solid?.Dispose();
            _collect = _pour = _left = _throw = _solid = null;
            // 只释放自己创建的预览资源；不销毁共享相机/材质，也不清空任何全局EventBus。
            Release(_trajectoryMaterial); Release(_trajectoryRoot);
            _trajectoryMaterial = null; _trajectoryRoot = null; _trajectory = null;
        }
        private static void Release(UnityEngine.Object owned)
        {
            if (owned == null) return;
            if (Application.isPlaying) Destroy(owned); else DestroyImmediate(owned);
        }
    }
}

