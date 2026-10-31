using System;
using System.Collections.Generic;
using Game.Character;
using Game.Combat;
using Game.Core;
using Game.Networking;
using Mirror;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Multiplayer
{
    /// <summary>N3组合根：只存在正式在线Scene，不接管单机Session、不让网络适配拥有角色规则。</summary>
    public sealed class OnlineWorldBootstrap : MonoBehaviour
    {
        public static OnlineWorldBootstrap Active { get; private set; }
        public static bool IsDedicatedRuntime
        {
            get
            {
#if UNITY_SERVER && !UNITY_EDITOR
                return true;
#else
                return false;
#endif
            }
        }
        private OnlineMovementProfile _profile;
        private OnlineCombatDefinition _combatDefinition;
        private CombatSimulationWorld _combatSimulation;
        private OnlineCombatWorld _combat;
        private OnlineDungeonDefinition _dungeonDefinition;
        private OnlineDungeonRuntime _dungeonRuntime;
        private OnlineDungeonWorldBindings _dungeonBindings;
        private DungeonProgressNetworkChannel _progressChannel;
        private GameObject _worldContent;
        private string _runId;
        private bool _initializing;
        private readonly uint[] _progressRevisions = new uint[4], _progressEpochs = new uint[4];
        private RoomNetworkManager _manager;
        // 发现singleton与完成通道交接是两个阶段。ResolveManager可先取得新Manager供认证检查，
        // 但不能据此声称旧Movement/Combat/Progress已经迁移；正式重连会销毁并重建Manager。
        private RoomNetworkManager _boundManager;
        private MovementNetworkChannel _channel;
        private JoinTicketAuthenticator _auth;
        private RoomInstanceBootstrap _instance;
        private AuthorityTickDriver _driver;
        private OnlineOrbitCamera _orbitCamera;
        private OnlineCameraTarget _cameraTarget;
        private MovementPresentationSmoother _smoother;
        private readonly LocalMovementPrediction _prediction = new LocalMovementPrediction();
        private uint _localSequence, _localEpoch, _snapshotSequence, _lastLocalSnapshot;
        private ulong _localActor;
        // 发送时钟和模拟Tick分开：Render帧略晚不能不断推迟后续deadline，也不补发过期世界状态。
        private readonly SnapshotSendClock _snapshotClock = new SnapshotSendClock();
        public long SnapshotBatchesSent { get; private set; }
        private bool _stopped, _frozen, _announced, _localInputSuppressed;
        // UI 开闭是离散操作；HashSet 仅保存 Owner，不在 Tick 中查 Scene 或创建集合。
        private readonly HashSet<int> _inventoryInputOwners = new HashSet<int>(4);
        public bool WorldReady { get; private set; }
        public string DungeonId => _dungeonDefinition != null ? _dungeonDefinition.DungeonId : null;
        public bool IsConfiguredActor(ulong actor) => actor >= 1 && actor <= OnlineActorLimits.PlayerCount ||
            _dungeonDefinition != null && _dungeonDefinition.TryGetNpc(actor, out _);
        public bool IsAuthority { get; private set; }
        public bool MovementReady => IsAuthority ? CanAdvance : _announced;
        public NetworkActorRegistry Registry { get; private set; }
        public OnlineCombatWorld Combat => _combat;
        public PlayerControllerBase LocalPlayer { get; private set; }
        public uint ServerTick { get; private set; }
        public uint LastInputSequence => _localSequence;
        public int PredictionReplayCount => _prediction.LastReplayCount;
        public float LastCorrectionDistance => _prediction.LastCorrectionDistance;
        public bool Frozen => IsAuthority ? _manager == null || !_manager.CanSimulate : _frozen;
        public bool CanAdvance => WorldReady && !_stopped && _combat != null && !_combat.Faulted && _manager != null && (IsAuthority ? _manager.CanSimulate : NetworkClient.isConnected && LocalPlayer != null && !_frozen);
        public bool LocalInputSuppressed
        {
            get => _localInputSuppressed || _inventoryInputOwners.Count != 0;
            set { if (_localInputSuppressed == value) return; _localInputSuppressed = value; ApplyImmediateInputGate(); PublishView(); }
        }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatic() => Active = null;
        private void Awake()
        {
            if (Active != null && Active != this) { enabled = false; GameLog.Error("重复在线世界，拒绝两个Tick Owner。", "OnlineMovement"); return; }
            Active = this;
            // Build Server后Editor也可能带UNITY_SERVER；Play验收仍是Client，不能误接管全局Physics。
            IsAuthority = IsDedicatedRuntime;
            _profile = Resources.Load<OnlineMovementProfile>("Online/MovementProfile");
            _combatDefinition = Resources.Load<OnlineCombatDefinition>("Online/CombatDefinition");
            // Awake时尚没有可信Launch/ClientIdentity。这里只加载数据，不默认生成灰盒、Actor或Physics Owner。
            // Dedicated由Bootstrap在验证描述后显式装配；Client在认证后按同一个Catalog键装配。
        }

        /// <summary>DS专用入口：只有本进程已验证的Launch能选择内容，不能把公共string API当成客户端选图权限。</summary>
        public bool TryInitializeDungeon(string dungeonId)
        {
            ResolveManager();
            if (!IsAuthority || _instance == null || !_instance.HasValidatedLaunch ||
                !string.Equals(_instance.DungeonId, dungeonId, StringComparison.Ordinal)) return false;
            return InitializeDungeon(dungeonId, _instance.RunId);
        }

        private void ResolveManager()
        {
            if (_manager != null) return;
            _manager = NetworkManager.singleton as RoomNetworkManager;
            if (_manager == null) return;
            _auth = _manager.GetComponent<JoinTicketAuthenticator>(); _instance = _manager.GetComponent<RoomInstanceBootstrap>();
        }
        private bool InitializeDungeon(string dungeonId, string runId)
        {
            if (_stopped || _initializing || string.IsNullOrEmpty(dungeonId) || string.IsNullOrEmpty(runId)) return false;
            if (WorldReady) return DungeonId == dungeonId && _runId == runId;
            var catalog = Resources.Load<OnlineDungeonCatalog>("Online/DungeonCatalog");
            if (_profile == null || _profile.SimulationHash == 0 || _profile.PlayerPrefab == null ||
                _combatDefinition == null || _combatDefinition.WireHash == 0 || _manager == null ||
                catalog == null || !catalog.TryGet(dungeonId, out var definition) || !definition.TryValidate(out _))
            { GameLog.Error("可信副本配置缺失或不在Catalog中，拒绝回退灰盒。", "OnlineDungeon"); StopWorld(); return false; }
            _initializing = true;
            try
            {
                // Spawn使用作者世界Pose，因此组合根必须保持单位变换，不能悄悄缩放碰撞/NavMesh而仍使用旧Pose。
                if (transform.position != Vector3.zero || transform.rotation != Quaternion.identity || transform.lossyScale != Vector3.one)
                    throw new InvalidOperationException("在线世界根必须是单位变换。");
                _dungeonDefinition = definition; _runId = runId;
                _worldContent = Instantiate(definition.WorldPrefab, transform, false);
                _dungeonBindings = _worldContent.GetComponent<OnlineDungeonWorldBindings>();
                string bindingError = "缺少WorldBindings";
                if (_dungeonBindings == null || !_dungeonBindings.TryValidate(definition.EncounterCount, out bindingError))
                    throw new InvalidOperationException("正式地图入口/门接线缺失：" + bindingError);
                _worldContent.SetActive(true); // 离线导出根inactive，只有完成剥离的静态内容在此启用。
                _combatSimulation = new CombatSimulationWorld();
                Registry = new NetworkActorRegistry(_profile, transform, _combatDefinition, _combatSimulation, definition);
                BindNetwork();
                if (_combat == null) throw new InvalidOperationException("正式网络组合根缺失。");
                // Replica先建立受控NPC身份；未来区保持Dormant，避免先到事实触发未经配置的默认Prefab。
                for (int i = 0; i < definition.NpcCount; i++)
                {
                    ulong actor = definition.GetNpc(i).ActorId;
                    var slot = Registry.EnsureEnemy(actor, IsAuthority);
                    if (IsAuthority) _combat.RegisterActorHierarchy(slot, actor);
                }
                _dungeonRuntime = new OnlineDungeonRuntime(definition, _dungeonBindings, Registry, IsAuthority, SetNpcActive, ActivateDungeonBoss);
                Physics.SyncTransforms();
                _driver = gameObject.AddComponent<AuthorityTickDriver>(); _driver.Initialize(this, IsAuthority);
                WorldReady = true; return true;
            }
            catch (Exception error)
            { GameLog.Error("正式副本装配被拒绝：" + error.Message, "OnlineDungeon"); StopWorld(); return false; }
            finally { _initializing = false; }
        }
        private void SetNpcActive(ulong actor, bool active)
        { if (!Registry.SetNpcActive(actor, active)) throw new InvalidOperationException("遭遇引用未知NPC。"); }
        private void ActivateDungeonBoss(ulong actor)
        { if (IsConfiguredActor(actor)) Registry[(int)actor - 1].Boss?.TryActivateAuthorityEncounter(); }
        private void OnEnable()
        {
            EventBus<OnlineMovementMenuRequestEvent>.Subscribe(OnMenu);
            EventBus<OnlineMovementViewRequestedEvent>.Subscribe(OnViewRequested);
            EventBus<InventoryPresentationVisibilityEvent>.Subscribe(OnInventoryVisibility);
            EventBus<LocalActorCameraBoundEvent>.Subscribe(OnCameraBound);
        }
        private void OnDisable()
        {
            EventBus<OnlineMovementMenuRequestEvent>.Unsubscribe(OnMenu);
            EventBus<OnlineMovementViewRequestedEvent>.Unsubscribe(OnViewRequested);
            EventBus<InventoryPresentationVisibilityEvent>.Unsubscribe(OnInventoryVisibility);
            EventBus<LocalActorCameraBoundEvent>.Unsubscribe(OnCameraBound);
            StopWorld();
        }
        private void OnMenu(OnlineMovementMenuRequestEvent request) { if (!IsAuthority) LocalInputSuppressed = request.Open; }
        private void OnViewRequested(OnlineMovementViewRequestedEvent request)
        {
            // 常驻HUD可能来自另一个战斗Scene；只重放本World仍存活的视图，不借查询推进任何玩法状态。
            if (!_stopped && request.WorldSceneHandle == gameObject.scene.handle) PublishView();
        }
        private void OnInventoryVisibility(InventoryPresentationVisibilityEvent request)
        {
            if (IsAuthority || request.OwnerId == 0) return;
            bool changed = request.Open ? _inventoryInputOwners.Add(request.OwnerId) : _inventoryInputOwners.Remove(request.OwnerId);
            if (!changed) return;
            // 只释放自己的捕获；Tab、Esc、其他整备面板不能互相覆盖，也绝不改变 Time.timeScale。
            ApplyImmediateInputGate();
            PublishView();
        }
        private void ApplyImmediateInputGate()
        {
            if (IsAuthority || LocalPlayer == null) return;
            // InputSystem在同一轮中可先发Tab回调、随后发数字键回调；等到Update同步已经太晚。
            // 关闭一个UI后仍根据全部Owner、失焦/冻结/断线/死亡重新求门，不能把另一个窗口的捕获释放掉。
            var cast = LocalPlayer.GetComponent<OnlineCastDriver>();
            var health = LocalPlayer.GetComponent<HealthComponent>();
            bool blocked = LocalInputSuppressed || _frozen || !NetworkClient.isConnected || health != null && !health.IsAlive;
            // Movement 自己维护 OnApplicationFocus；这里传当前外部门，而不是把“关闭 UI”也当成一次清理命令。
            // 这样新 Jump/Dash 可在下一次固定采样前锁存，同时旧按住仍由角色的 release 栅栏拒绝。
            LocalPlayer.SetOnlineInputBlocked(blocked);
            if (cast != null) cast.SetBlocked(blocked || !Application.isFocused);
        }
        private void BindNetwork()
        {
            if (_boundManager != null)
            {
                _boundManager.ServerActorJoined -= OnJoined; _boundManager.ServerActorLeft -= OnLeft;
            }
            if (_channel != null) { _channel.InputReceived -= OnInput; _channel.StateReceived -= OnState; }
            // 先结束上一代传输适配器，保留同Run的Registry/DungeonRuntime/资源工作区。
            // Configure清空有界候选并使迟到的旧身份包无法通过；enabled=false让活跃组件退订Mirror handler。
            if (_progressChannel != null)
            {
                _progressChannel.Configure(null, null, 0);
                _progressChannel.enabled = false;
            }
            _combat?.Dispose(); _combat = null;
            _channel = null; _progressChannel = null; _boundManager = null;
            _manager = NetworkManager.singleton as RoomNetworkManager;
            if (_manager == null) { _auth = null; _instance = null; return; }
            _auth = _manager.GetComponent<JoinTicketAuthenticator>(); _instance = _manager.GetComponent<RoomInstanceBootstrap>();
            _channel = _manager.GetComponent<MovementNetworkChannel>();
            if (_channel == null) _channel = _manager.gameObject.AddComponent<MovementNetworkChannel>();
            _channel.SimulationHash = _profile.SimulationHash;
            _channel.InputReceived += OnInput; _channel.StateReceived += OnState;
            var combatChannel = _manager.GetComponent<CombatNetworkChannel>();
            if (combatChannel == null) combatChannel = _manager.gameObject.AddComponent<CombatNetworkChannel>();
            _combat = new OnlineCombatWorld(this, _combatDefinition, _combatSimulation, combatChannel, _manager, _instance);
            // 配置只取当前已通过Launch/ClientIdentity核验的World字段，不从新连接自报的字符串重新选图。
            // 首次初始化与同World换Manager共用这个入口，进度通道不会遗留在被销毁的上一代对象上。
            _progressChannel = _manager.GetComponent<DungeonProgressNetworkChannel>();
            if (_progressChannel == null) _progressChannel = _manager.gameObject.AddComponent<DungeonProgressNetworkChannel>();
            _progressChannel.Configure(_runId, DungeonId, _combatDefinition.WireHash);
            _progressChannel.enabled = true;
            Array.Clear(_progressRevisions, 0, _progressRevisions.Length);
            Array.Clear(_progressEpochs, 0, _progressEpochs.Length);
            // Awake 跨物体无顺序保证；若 Manager 较晚建立，在 Bind 成功时补登记已创建的 NPC。
            if (IsAuthority)
                for (int i = 0; i < Registry.Capacity; i++) _combat.RegisterActorHierarchy(Registry[i], (ulong)i + 1);
            if (IsAuthority)
            {
                _manager.ServerActorJoined += OnJoined; _manager.ServerActorLeft += OnLeft;
                for (int i = 0; i < 4; i++) { var connection = _manager.ActorConnection(i); if (connection != null) OnJoined(connection, connection.authenticationData as JoinConnectionIdentity); }
            }
            // 只有全部通道接线完成才提交代次；下帧发现的singleton相同不等于本次绑定已完成。
            _boundManager = _manager;
        }
        private void Update()
        {
            if (_stopped) return;
            ResolveManager();
            if (!WorldReady)
            {
                // 不能把此尝试放在WorldReady早退之后，否则Client永远等不到首次可信装配。
                if (!IsAuthority && _auth != null && _auth.ClientAuthenticated && NetworkClient.isConnected && _auth.ClientIdentity != null)
                {
                    var identity = _auth.ClientIdentity;
                    if (!InitializeDungeon(identity.DungeonId, identity.RunId))
                    { if (NetworkClient.active) NetworkClient.Disconnect(); return; }
                }
                else return;
            }
            if (_manager == null || _manager != NetworkManager.singleton || _boundManager != _manager) BindNetwork();
            if (IsAuthority)
            {
                if (!CanAdvance) Registry.Neutralize();
                return;
            }
            EnsureLocalIdentity();
            // 身份替换会同步StopWorld并释放Combat/Registry所属对象；调用成功返回不代表世界仍存活。
            // 必须先重新检查生命周期，不能继续消费进度或把已停止角色当成普通断线再写一轮表现状态。
            if (_stopped || !WorldReady) return;
            _combat?.PumpClientInventory();
            // 通道固定最多缓存8份候选，按到达顺序逐份交给领域校验；非法高Revision不能挤掉同帧合法进度。
            // 此处没有await或网络泵，取空即结束，不会变成持续接收网络消息的无界循环。
            while (_progressChannel != null && _progressChannel.TryTake(out var progress)) _dungeonRuntime.ApplyReplica(progress);
            if (LocalPlayer != null) ApplyImmediateInputGate();
            if (_auth != null && _auth.ClientAuthenticated && NetworkClient.isConnected && _auth.ClientIdentity != null)
            {
                // 已认证身份由EnsureLocalIdentity同步，等待真实运动状态后才隐藏旧连接弹窗。
            }
            else if (LocalPlayer != null)
            {
                LocalPlayer.ClearOnlineInput(); _prediction.ClearHistory();
                if (!_frozen) { _frozen = true; _localInputSuppressed = true; PublishView(); }
            }
        }
        private void EnsureLocalIdentity()
        {
            if (!WorldReady || _auth == null || !_auth.ClientAuthenticated || !NetworkClient.isConnected || _auth.ClientIdentity == null) return;
            if (_auth.ClientIdentity.DungeonId != DungeonId || _auth.ClientIdentity.RunId != _runId)
            { GameLog.Error("存活世界拒绝跨Run/副本身份替换。", "OnlineDungeon"); StopWorld(); NetworkClient.Disconnect(); return; }
            var id = _auth.ClientIdentity; if (_localEpoch == id.ConnectionEpoch) return;
            _localActor = id.ActorId; _localEpoch = id.ConnectionEpoch; _localSequence = _lastLocalSnapshot = 0;
            _prediction.Reset(_localEpoch); _frozen = false; _announced = false; _localInputSuppressed = false;
            var slot = Registry.EnsurePlayer((int)_localActor - 1, PlayerExecutionMode.Prediction);
            slot.Epoch = _localEpoch; slot.Connected = true; LocalPlayer = slot.Player; LocalPlayer.ClearOnlineInput();
            _combat.BindClient(slot, _localActor, _localEpoch, true);
            _smoother = LocalPlayer.GetComponent<MovementPresentationSmoother>();
            if (_smoother == null) { _smoother = LocalPlayer.gameObject.AddComponent<MovementPresentationSmoother>(); _smoother.Bind(LocalPlayer); }
            if (_cameraTarget == null)
            {
                var target = new GameObject("LocalCameraPresentationTarget"); target.transform.SetParent(transform, false);
                _cameraTarget = target.AddComponent<OnlineCameraTarget>();
            }
            _cameraTarget.Bind(LocalPlayer);
            // 网络层只公布本机角色，不依赖 UI 程序集或另造 HUD；晚加载 Canvas 可读取最近绑定。
            LocalActorPresentation.Bind(GetInstanceID(), LocalPlayer.gameObject,
                LocalPlayer is WizardController || LocalPlayer is ArcherController, _cameraTarget.transform);
            ApplyImmediateInputGate();
            InstallCamera();
        }
        private void OnCameraBound(LocalActorCameraBoundEvent view)
        {
            if (IsAuthority || _stopped || LocalPlayer == null || view.Actor != LocalPlayer.gameObject || view.Camera == null || !view.Camera.isActiveAndEnabled) return;
            // 原相机可以晚于角色加载；先停用旧 Camera/Listener，再等帧末销毁 lab fallback，防双画面与双 Listener。
            if (_orbitCamera != null)
            {
                _orbitCamera.gameObject.SetActive(false); Destroy(_orbitCamera.gameObject); _orbitCamera = null;
            }
            // 连接阶段的 Camera 仍在同一个 Scene，depth 可高于原 P7 镜头；不移交会覆盖正常世界。
            // 只在离散绑定时查找，保留其他 Scene（包括暂存 Hub）的 Camera/Listener 状态。
            var roots = gameObject.scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                var cameras = roots[i].GetComponentsInChildren<Camera>(true);
                for (int j = 0; j < cameras.Length; j++)
                {
                    var camera = cameras[j]; if (camera == view.Camera) continue;
                    camera.enabled = false;
                    var listener = camera.GetComponent<AudioListener>(); if (listener != null) listener.enabled = false;
                }
            }
            LocalPlayer.BindOnlineCamera(view.Camera);
        }
        private void InstallCamera()
        {
            if (LocalActorCameraPresentation.TryGet(LocalPlayer.gameObject, out var shared) && shared.isActiveAndEnabled)
            {
                OnCameraBound(new LocalActorCameraBoundEvent { Actor = LocalPlayer.gameObject, Camera = shared });
                Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; return;
            }
            if (_orbitCamera != null) { _orbitCamera.Bind(LocalPlayer); LocalPlayer.BindOnlineCamera(_orbitCamera.GetComponent<Camera>()); return; }
            var cameras = FindObjectsByType<Camera>(FindObjectsSortMode.None);
            for (int i = 0; i < cameras.Length; i++) if (cameras[i].gameObject.scene == gameObject.scene) cameras[i].enabled = false;
            var root = new GameObject("OnlineLocalCamera"); root.transform.SetParent(transform, false); root.tag = "MainCamera";
            var camera = root.AddComponent<Camera>(); camera.nearClipPlane = .1f; camera.farClipPlane = 150;
            root.AddComponent<AudioListener>(); _orbitCamera = root.AddComponent<OnlineOrbitCamera>(); _orbitCamera.Bind(LocalPlayer);
            LocalPlayer.BindOnlineCamera(camera);
            Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
        }
        private void OnJoined(NetworkConnectionToClient connection, JoinConnectionIdentity identity)
        {
            if (!IsAuthority || identity == null || Registry == null || _instance == null || _instance.Inventory == null ||
                !_instance.Inventory.Contains(identity.ActorId, identity.PlayerId)) { connection?.Disconnect(); return; }
            var slot = Registry.EnsurePlayer((int)identity.ActorId - 1, PlayerExecutionMode.Authority);
            slot.Epoch = identity.ConnectionEpoch; slot.ConnectionId = connection.connectionId; slot.Connected = true; slot.SimulationReady = false;
            slot.Input.Reset(identity.ConnectionEpoch); slot.Player.ClearOnlineInput();
            if (!_combat.BindAuthority(slot, identity)) { slot.Connected = false; connection.Disconnect(); }
        }
        private void OnLeft(NetworkConnectionToClient connection, JoinConnectionIdentity identity)
        {
            if (Registry == null || identity == null) return; var slot = Registry[(int)identity.ActorId - 1];
            if (slot.ConnectionId != connection.connectionId || slot.Epoch != identity.ConnectionEpoch) return;
            slot.Connected = slot.SimulationReady = false; slot.Input.Neutralize(); if (slot.Player != null) slot.Player.ClearOnlineInput();
            _combat?.ActorLeft(identity.ActorId, identity.ConnectionEpoch);
        }
        private void OnInput(JoinConnectionIdentity identity, PlayerSimulationFrame frame)
        {
            if (!CanAdvance || Registry == null) return;
            var slot = Registry[(int)identity.ActorId - 1];
            if (slot.Connected && slot.Epoch == identity.ConnectionEpoch &&
                slot.Input.TryEnqueue(frame, identity.ConnectionEpoch, Time.realtimeSinceStartupAsDouble)) slot.SimulationReady = true;
        }
        public void Step(float dt, uint tick)
        {
            if (!CanAdvance) return;
            if (IsAuthority)
            {
                ServerTick = tick;
                // 先标记出生 Tick，随后再推进动作与 Physics，防止新弹体被计入上一 Tick。
                _combatSimulation.BeginTick(tick);
                for (int i = 0; i < 4; i++)
                {
                    var slot = Registry[i]; if (slot.Player == null || !slot.Targetable) continue;
                    var frame = slot.Input.Consume(Time.realtimeSinceStartupAsDouble);
                    slot.Player.Simulate(frame, dt);
                }
                _dungeonRuntime?.Step(dt, tick);
                _combat.StepAuthority(dt, tick);
                if (!CanAdvance) return; // 出生容量/配置错误可在施法内使世界 Faulted；同一 Tick 不再推进 AI/Physics。
                for (int i = 4; i < Registry.Capacity; i++)
                {
                    var slot = Registry[i];
                    if (!slot.Connected || slot.Root == null || !slot.Root.activeInHierarchy) continue;
                    if (slot.Enemy != null) slot.Enemy.StepAuthority(dt, tick);
                    if (slot.Boss != null)
                    {
                        // 保留单机 encounter 的接近触发含义，认证/加载完成本身不等于主动开战。
                        if (_dungeonDefinition == null && !slot.Boss.IsEncounterActive && OnlineEncounterEntry.CanActivate(Registry, slot.Body.position, _combatDefinition.BossActivationRadius))
                            slot.Boss.TryActivateAuthorityEncounter();
                        slot.Boss.StepAuthority(dt, tick);
                    }
                    if (!CanAdvance) return;
                }
                _combatSimulation.StepBeforePhysics(dt, tick);
            }
            else
            {
                bool dead = Registry[(int)_localActor - 1].Health != null && !Registry[(int)_localActor - 1].Health.IsAlive;
                var frame = LocalPlayer.SampleInput(++_localSequence, _localEpoch, LocalInputSuppressed || !Application.isFocused || dead);
                if (dead)
                {
                    // 死亡仍发送 neutral liveness frame，让新 Epoch 完成 ready 栅栏；只撤销 Gameplay 权限。
                    LocalPlayer.ClearOnlineInput(); _prediction.ClearHistory(); _channel.SendInput(frame, _profile.SimulationHash); return;
                }
                LocalPlayer.Simulate(frame, dt); _prediction.Record(frame, LocalPlayer.CaptureMovementSnapshot());
                _channel.SendInput(frame, _profile.SimulationHash);
                // Prediction replay 只重放移动，施法意图每个真实输入步只发送一次。
                _combat.StepPrediction(dt, tick, LocalInputSuppressed || !Application.isFocused);
            }
        }
        public void AfterPhysics(float dt, uint tick)
        { if (IsAuthority && CanAdvance) _combatSimulation.StepAfterPhysics(dt, tick); }
        public void PumpSnapshots(double now)
        {
            _combat?.Pump(now);
            PumpDungeonProgress();
            if (!IsAuthority || _stopped || _channel == null || ServerTick == 0 ||
                !_snapshotClock.TrySend(now, _profile.SnapshotInterval)) return;
            SnapshotBatchesSent++; _snapshotSequence++;
            for (int i = 0; i < Registry.Capacity; i++)
            {
                var slot = Registry[i]; if (slot.Body == null) continue;
                PlayerMovementSnapshot pose;
                if (slot.Player != null) pose = slot.Player.CaptureMovementSnapshot();
                else pose = new PlayerMovementSnapshot { Position = slot.Body.position, Rotation = slot.Body.rotation,
                    TargetFacing = slot.Body.forward, IsGrounded = true, GroundNormal = Vector3.up };
                _channel.SendState(new MovementStateFrame { ActorId = (ulong)(i + 1), SimulationHash = _profile.SimulationHash,
                    ConnectionEpoch = slot.Epoch == 0 ? 1u : slot.Epoch, ServerTick = ServerTick, AcknowledgedInput = slot.Input.LastAcknowledged,
                    SnapshotSequence = _snapshotSequence, IsEnemy = i >= 4, Connected = slot.Connected, Targetable = i >= 4 ? slot.Connected && (slot.Health == null || slot.Health.IsAlive) : slot.Targetable,
                    Frozen = !CanAdvance, Movement = pose });
            }
        }
        private void OnState(MovementStateFrame state)
        {
            if (IsAuthority || _stopped || Registry == null || state.SimulationHash != _profile.SimulationHash || !IsConfiguredActor(state.ActorId)) return;
            EnsureLocalIdentity(); // Scene初帧收到Snapshot时也先确定本机Owner，不能先误建Replica。
            // EnsureLocalIdentity可能因跨Run身份而停止并Dispose世界；当前回调也必须结束，避免清理后再创建Replica。
            if (_stopped || !WorldReady) return;
            int index = (int)state.ActorId - 1;
            if (state.IsEnemy != (index >= 4)) return;
            if (state.ActorId == _localActor)
            {
                if (state.ConnectionEpoch != _localEpoch || state.SnapshotSequence <= _lastLocalSnapshot || LocalPlayer == null) return;
                _lastLocalSnapshot = state.SnapshotSequence; ServerTick = state.ServerTick;
                if (_frozen != state.Frozen)
                {
                    _frozen = state.Frozen; LocalPlayer.ClearOnlineInput(); _prediction.ClearHistory();
                    ApplyImmediateInputGate(); PublishView();
                }
                Vector3 visualBefore = _smoother != null ? _smoother.VisualPosition : LocalPlayer.transform.position;
                if (state.Frozen)
                {
                    // Frozen 只撤销继续预测的权限，不撤销安装权威事实的责任。否则未 Ack 位移会一直留在本机 CC，
                    // 其他玩家却已停在服务器位置。恢复完整状态但绝不 Simulate，也不重放冻结前的历史输入。
                    Vector3 physicalBefore = LocalPlayer.transform.position;
                    LocalPlayer.RestoreMovementSnapshot(state.Movement); _prediction.ClearHistory();
                    // 同一权威 Pose 会按发送频率重复到达；仅真正移动物理根才启动新的 100ms 校正，
                    // 否则每份重复快照都会延长剩余误差，使一个有限校正变成始终追不完的平滑。
                    if (_smoother != null && physicalBefore != LocalPlayer.transform.position)
                        _smoother.PreserveAfterCorrection(visualBefore);
                }
                else
                {
                    if (_prediction.Reconcile(LocalPlayer, state.Movement, state.AcknowledgedInput, state.ConnectionEpoch, FixedStepClock.DeltaTime) && _smoother != null)
                        _smoother.PreserveAfterCorrection(visualBefore);
                }
                if (!_announced) { _announced = true; PublishView(); }
                return;
            }
            var slot = index >= 4 ? Registry.EnsureEnemy(state.ActorId, false) : Registry.EnsurePlayer(index, PlayerExecutionMode.Replica);
            if (slot.Player != null && slot.Cast == null) _combat.BindClient(slot, state.ActorId, state.ConnectionEpoch, false);
            if (state.ConnectionEpoch < slot.Epoch || state.SnapshotSequence <= slot.LastSnapshot) return;
            if (state.ConnectionEpoch != slot.Epoch) { slot.Interpolation.Reset(); slot.Epoch = state.ConnectionEpoch; }
            slot.LastSnapshot = state.SnapshotSequence; slot.Connected = state.Connected;
            slot.Interpolation.TryAdd(state, Time.realtimeSinceStartupAsDouble);
            if (slot.Root.activeSelf != state.Connected) slot.Root.SetActive(state.Connected);
        }
        private void PumpDungeonProgress()
        {
            if (!IsAuthority || !WorldReady || _progressChannel == null || _dungeonRuntime == null ||
                !_dungeonRuntime.TryCapture(out var progress)) return;
            var movement = _manager.GetComponent<MovementNetworkChannel>();
            for (int i = 0; i < OnlineActorLimits.PlayerCount; i++)
            {
                var connection = _manager.ActorConnection(i);
                var identity = connection != null ? connection.authenticationData as JoinConnectionIdentity : null;
                if (identity == null || !movement.IsActorReady(identity.ActorId, identity.ConnectionEpoch) ||
                    _progressRevisions[i] == progress.Revision && _progressEpochs[i] == identity.ConnectionEpoch) continue;
                if (_progressChannel.SendToOwner(identity, progress))
                { _progressRevisions[i] = progress.Revision; _progressEpochs[i] = identity.ConnectionEpoch; }
            }
        }
        private void LateUpdate()
        {
            if (IsAuthority || Registry == null || _stopped) return;
            _combat?.Present(Time.realtimeSinceStartupAsDouble);
            for (int i = 0; i < Registry.Capacity; i++)
            {
                var slot = Registry[i]; if (i + 1 == (int)_localActor || slot.Root == null || !slot.Connected ||
                    !slot.Interpolation.TrySample(Time.realtimeSinceStartupAsDouble, _profile.InterpolationDelay, out var pose)) continue;
                var actor = slot.Body;
                // Animator读取“权威快照段每秒走了多远”，而不是“本Render帧是否恰好有新位置”。
                // 后者会使短缺包变成反复Idle/Run；原Controller的Transition还未结束就被打断，形成滑行。
                float speed = slot.Interpolation.PlanarSpeed;
                if (slot.Player != null)
                {
                    slot.Player.RestoreMovementSnapshot(pose);
                    float fullSpeed = Mathf.Max(.01f, slot.Player.MoveSpeed);
                    slot.Player.PresentReplicaMovement(Mathf.Clamp01(speed / fullSpeed));
                }
                else
                {
                    actor.SetPositionAndRotation(pose.Position, pose.Rotation);
                    // 普通敌人的speed使用米/秒；Boss原Blend Tree还需要本地方向moveX/moveZ。
                    // 只把权威段速度交给表现入口，不开启Replica AI/导航，也不每帧CrossFade覆盖施法/死亡。
                    if (slot.Enemy != null) slot.Enemy.PresentReplicaMovement(speed);
                    else if (slot.Boss != null)
                        slot.Boss.PresentReplicaMovement(slot.Interpolation.PlanarVelocity, slot.Interpolation.PlaybackTime);
                }
            }
        }
        private void PublishView()
        {
            if (IsAuthority) return;
            EventBus<OnlineMovementPresentationEvent>.Publish(new OnlineMovementPresentationEvent
            { WorldSceneHandle = gameObject.scene.handle, Active = _announced && !_stopped, MenuOpen = _localInputSuppressed, InputCaptured = LocalInputSuppressed,
                Frozen = _frozen, Connected = NetworkClient.isConnected });
        }
        public void StopWorld()
        {
            if (_stopped) return; _stopped = true;
            if (!IsAuthority) LocalActorPresentation.Clear(GetInstanceID());
            _inventoryInputOwners.Clear();
            if (_driver != null) _driver.StopDriver();
            if (LocalPlayer != null) LocalPlayer.ClearOnlineInput(); _prediction.ClearHistory();
            if (_boundManager != null) { _boundManager.ServerActorJoined -= OnJoined; _boundManager.ServerActorLeft -= OnLeft; }
            if (_channel != null) { _channel.InputReceived -= OnInput; _channel.StateReceived -= OnState; }
            if (_progressChannel != null) { _progressChannel.Configure(null, null, 0); _progressChannel.enabled = false; }
            _progressChannel = null; _boundManager = null;
            _combat?.Dispose(); _combat = null;
            _dungeonRuntime?.Dispose(); _dungeonRuntime = null;
            Registry?.Neutralize(); Registry?.Dispose(); WorldReady = false; PublishView();
            if (_worldContent != null) { if (Application.isPlaying) Destroy(_worldContent); else DestroyImmediate(_worldContent); _worldContent = null; }
            if (Active == this) Active = null;
        }
        public static void ShutdownActive() { if (Active != null) Active.StopWorld(); }
    }
}
