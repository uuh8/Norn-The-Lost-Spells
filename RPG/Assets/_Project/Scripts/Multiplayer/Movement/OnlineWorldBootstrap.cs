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
        private RoomNetworkManager _manager;
        private MovementNetworkChannel _channel;
        private JoinTicketAuthenticator _auth;
        private RoomInstanceBootstrap _instance;
        private AuthorityTickDriver _driver;
        private OnlineOrbitCamera _orbitCamera;
        private MovementPresentationSmoother _smoother;
        private readonly LocalMovementPrediction _prediction = new LocalMovementPrediction();
        private uint _localSequence, _localEpoch, _snapshotSequence, _lastLocalSnapshot;
        private ulong _localActor;
        private double _nextSnapshot;
        private bool _stopped, _frozen, _announced, _localInputSuppressed;
        public bool WorldReady { get; private set; }
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
            get => _localInputSuppressed;
            set { if (_localInputSuppressed == value) return; _localInputSuppressed = value; if (LocalPlayer != null) LocalPlayer.ClearOnlineInput(); PublishView(); }
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
            if (_profile == null || _profile.SimulationHash == 0 || _profile.WorldPrefab == null || _profile.PlayerPrefab == null || _profile.EnemyPrefab == null)
            { GameLog.Error("运动世界配置缺失，请先Setup Online Movement World。", "OnlineMovement"); return; }
            Instantiate(_profile.WorldPrefab, transform);
            _combatDefinition = Resources.Load<OnlineCombatDefinition>("Online/CombatDefinition");
            if (_combatDefinition == null || _combatDefinition.WireHash == 0)
            { GameLog.Error("战斗配置缺失，请先 Setup Online Combat。", "OnlineCombat"); return; }
            _combatSimulation = new CombatSimulationWorld();
            Registry = new NetworkActorRegistry(_profile, transform, _combatDefinition, _combatSimulation);
            _driver = gameObject.AddComponent<AuthorityTickDriver>();
            BindNetwork();
            if (IsAuthority)
            {
                for (ulong actor = 5; actor <= 7; actor++) Registry.EnsureEnemy(actor, true);
                for (int i = 4; i < Registry.Capacity; i++) _combat?.RegisterActorHierarchy(Registry[i], (ulong)i + 1);
            }
            Physics.SyncTransforms(); WorldReady = true; _driver.Initialize(this, IsAuthority);
            if (!IsAuthority) EnsureLocalIdentity();
        }
        private void OnEnable() => EventBus<OnlineMovementMenuRequestEvent>.Subscribe(OnMenu);
        private void OnDisable() { EventBus<OnlineMovementMenuRequestEvent>.Unsubscribe(OnMenu); StopWorld(); }
        private void OnMenu(OnlineMovementMenuRequestEvent request) { if (!IsAuthority) LocalInputSuppressed = request.Open; }
        private void BindNetwork()
        {
            if (_manager != null)
            {
                _manager.ServerActorJoined -= OnJoined; _manager.ServerActorLeft -= OnLeft;
            }
            if (_channel != null) { _channel.InputReceived -= OnInput; _channel.StateReceived -= OnState; }
            _manager = NetworkManager.singleton as RoomNetworkManager;
            if (_manager == null) return;
            _auth = _manager.GetComponent<JoinTicketAuthenticator>(); _instance = _manager.GetComponent<RoomInstanceBootstrap>();
            _channel = _manager.GetComponent<MovementNetworkChannel>();
            if (_channel == null) _channel = _manager.gameObject.AddComponent<MovementNetworkChannel>();
            _channel.SimulationHash = _profile.SimulationHash;
            _channel.InputReceived += OnInput; _channel.StateReceived += OnState;
            _combat?.Dispose();
            var combatChannel = _manager.GetComponent<CombatNetworkChannel>();
            if (combatChannel == null) combatChannel = _manager.gameObject.AddComponent<CombatNetworkChannel>();
            _combat = new OnlineCombatWorld(this, _combatDefinition, _combatSimulation, combatChannel, _manager, _instance);
            // Awake 跨物体无顺序保证；若 Manager 较晚建立，在 Bind 成功时补登记已创建的 NPC。
            if (IsAuthority)
                for (int i = 0; i < Registry.Capacity; i++) _combat.RegisterActorHierarchy(Registry[i], (ulong)i + 1);
            if (IsAuthority)
            {
                _manager.ServerActorJoined += OnJoined; _manager.ServerActorLeft += OnLeft;
                for (int i = 0; i < 4; i++) { var connection = _manager.ActorConnection(i); if (connection != null) OnJoined(connection, connection.authenticationData as JoinConnectionIdentity); }
            }
        }
        private void Update()
        {
            if (_stopped || !WorldReady) return;
            if (_manager == null || _manager != NetworkManager.singleton) BindNetwork();
            if (IsAuthority)
            {
                if (!CanAdvance) Registry.Neutralize();
                return;
            }
            EnsureLocalIdentity();
            if (LocalPlayer != null)
            {
                var slot = Registry[(int)_localActor - 1];
                slot.Cast?.SetBlocked(_frozen || _localInputSuppressed || !Application.isFocused || !NetworkClient.isConnected
                    || slot.Health != null && !slot.Health.IsAlive);
            }
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
            if (_auth == null || !_auth.ClientAuthenticated || !NetworkClient.isConnected || _auth.ClientIdentity == null) return;
            var id = _auth.ClientIdentity; if (_localEpoch == id.ConnectionEpoch) return;
            _localActor = id.ActorId; _localEpoch = id.ConnectionEpoch; _localSequence = _lastLocalSnapshot = 0;
            _prediction.Reset(_localEpoch); _frozen = false; _announced = false; _localInputSuppressed = false;
            var slot = Registry.EnsurePlayer((int)_localActor - 1, PlayerExecutionMode.Prediction);
            slot.Epoch = _localEpoch; slot.Connected = true; LocalPlayer = slot.Player; LocalPlayer.ClearOnlineInput();
            _combat.BindClient(slot, _localActor, _localEpoch, true);
            _smoother = LocalPlayer.GetComponent<MovementPresentationSmoother>();
            if (_smoother == null) { _smoother = LocalPlayer.gameObject.AddComponent<MovementPresentationSmoother>(); _smoother.Bind(LocalPlayer); }
            InstallCamera();
        }
        private void InstallCamera()
        {
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
                _combat.StepAuthority(dt, tick);
                if (!CanAdvance) return; // 出生容量/配置错误可在施法内使世界 Faulted；同一 Tick 不再推进 AI/Physics。
                for (int i = 4; i < Registry.Capacity; i++)
                {
                    var slot = Registry[i];
                    if (slot.Enemy != null) slot.Enemy.StepAuthority(dt, tick);
                    if (slot.Boss != null)
                    {
                        // 保留单机 encounter 的接近触发含义，认证/加载完成本身不等于主动开战。
                        if (!slot.Boss.IsEncounterActive && OnlineEncounterEntry.CanActivate(Registry, slot.Body.position, _combatDefinition.BossActivationRadius))
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
                var frame = LocalPlayer.SampleInput(++_localSequence, _localEpoch, _localInputSuppressed || !Application.isFocused || dead);
                if (dead)
                {
                    // 死亡仍发送 neutral liveness frame，让新 Epoch 完成 ready 栅栏；只撤销 Gameplay 权限。
                    LocalPlayer.ClearOnlineInput(); _prediction.ClearHistory(); _channel.SendInput(frame, _profile.SimulationHash); return;
                }
                LocalPlayer.Simulate(frame, dt); _prediction.Record(frame, LocalPlayer.CaptureMovementSnapshot());
                _channel.SendInput(frame, _profile.SimulationHash);
                // Prediction replay 只重放移动，施法意图每个真实输入步只发送一次。
                _combat.StepPrediction(dt, tick, _localInputSuppressed || !Application.isFocused);
            }
        }
        public void AfterPhysics(float dt, uint tick)
        { if (IsAuthority && CanAdvance) _combatSimulation.StepAfterPhysics(dt, tick); }
        public void PumpSnapshots(double now)
        {
            _combat?.Pump(now);
            if (!IsAuthority || _stopped || _channel == null || ServerTick == 0 || now < _nextSnapshot) return;
            _nextSnapshot = now + _profile.SnapshotInterval; _snapshotSequence++;
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
            if (IsAuthority || _stopped || Registry == null || state.SimulationHash != _profile.SimulationHash || state.ActorId < 1 || state.ActorId > 7) return;
            EnsureLocalIdentity(); // Scene初帧收到Snapshot时也先确定本机Owner，不能先误建Replica。
            int index = (int)state.ActorId - 1;
            if (state.IsEnemy != (index >= 4)) return;
            if (state.ActorId == _localActor)
            {
                if (state.ConnectionEpoch != _localEpoch || state.SnapshotSequence <= _lastLocalSnapshot || LocalPlayer == null) return;
                _lastLocalSnapshot = state.SnapshotSequence; ServerTick = state.ServerTick;
                if (_frozen != state.Frozen) { _frozen = state.Frozen; LocalPlayer.ClearOnlineInput(); _prediction.ClearHistory(); PublishView(); }
                if (!state.Frozen)
                {
                    Vector3 visualBefore = _smoother != null ? _smoother.VisualPosition : LocalPlayer.transform.position;
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
        private static readonly int SpeedHash = Animator.StringToHash("speed");
        private void LateUpdate()
        {
            if (IsAuthority || Registry == null || _stopped) return;
            _combat?.Present(Time.realtimeSinceStartupAsDouble);
            for (int i = 0; i < Registry.Capacity; i++)
            {
                var slot = Registry[i]; if (i + 1 == (int)_localActor || slot.Root == null || !slot.Connected ||
                    !slot.Interpolation.TrySample(Time.realtimeSinceStartupAsDouble, _profile.InterpolationDelay, out var pose)) continue;
                var actor = slot.Body;
                float moved = Vector3.Distance(actor.position, pose.Position);
                if (slot.Player != null) { slot.Player.RestoreMovementSnapshot(pose); slot.Player.PresentReplicaMovement(moved > .001f ? 1 : 0); }
                else
                {
                    actor.SetPositionAndRotation(pose.Position, pose.Rotation);
                    var animator = slot.Enemy != null ? slot.Enemy.Animator : null;
                    if (animator != null) animator.SetFloat(SpeedHash, moved > .001f ? 1 : 0);
                }
            }
        }
        private void PublishView()
        {
            if (IsAuthority) return;
            EventBus<OnlineMovementPresentationEvent>.Publish(new OnlineMovementPresentationEvent
            { Active = _announced && !_stopped, MenuOpen = _localInputSuppressed, Frozen = _frozen, Connected = NetworkClient.isConnected });
        }
        public void StopWorld()
        {
            if (_stopped) return; _stopped = true;
            if (_driver != null) _driver.StopDriver();
            if (LocalPlayer != null) LocalPlayer.ClearOnlineInput(); _prediction.ClearHistory();
            if (_manager != null) { _manager.ServerActorJoined -= OnJoined; _manager.ServerActorLeft -= OnLeft; }
            if (_channel != null) { _channel.InputReceived -= OnInput; _channel.StateReceived -= OnState; }
            _combat?.Dispose(); _combat = null;
            Registry?.Neutralize(); Registry?.Dispose(); WorldReady = false; PublishView();
            if (Active == this) Active = null;
        }
        public static void ShutdownActive() { if (Active != null) Active.StopWorld(); }
    }
}
