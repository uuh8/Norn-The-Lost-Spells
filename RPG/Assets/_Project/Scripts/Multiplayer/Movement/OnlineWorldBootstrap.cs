using System;
using System.Collections.Generic;
using Game.Character;
using Game.Combat;
using Game.Core;
using Game.Networking;
using Mirror;
using UnityEngine;
using UnityEngine.SceneManagement;
using System.Security.Cryptography;

namespace Game.Multiplayer
{
    /// <summary>N3组合根：只存在正式在线Scene，不接管单机Session、不让网络适配拥有角色规则。</summary>
    public sealed partial class OnlineWorldBootstrap : MonoBehaviour
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
        private IOnlineMaterialWorldBinding _materialWorld;
        /// <summary>当前World生命期身份。DS创建、认证后告知Client；不取RoomEpoch/ConnectionEpoch或渲染版本。</summary>
        public ulong WorldEpoch { get; private set; }
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
        // 运行期同步失败属于本World的不可自动解除事实，不能借Heartbeat恢复Manager.Frozen而解锁。
        // 只关闭新玩法写口；原Owner、已提交量与现场对象一直保留到明确StopWorld。
        private string _authoritySynchronizationFailure;
        private bool _authorityFailureReported, _materialSuspensionFailed, _materialSuspensionInProgress;
        // UI 开闭是离散操作；HashSet 仅保存 Owner，不在 Tick 中查 Scene 或创建集合。
        private readonly HashSet<int> _inventoryInputOwners = new HashSet<int>(4);
        public bool WorldReady { get; private set; }
        public string DungeonId => _dungeonDefinition != null ? _dungeonDefinition.DungeonId : null;
        // 组合层只读取可信导出配置；Run的库存/生成写口仍由Character facade隐藏。
        public OnlineDungeonDefinition DungeonDefinition => _dungeonDefinition;
        public bool CanAwardDungeonNpcLoot(ulong actorId) => IsAuthority && CanAdvance
            && _dungeonDefinition != null && _dungeonDefinition.LootTableCount != 0
            && _dungeonRuntime != null && _dungeonRuntime.CanAwardNpcDeath(actorId);

        internal string MaterialRunId => _runId;
        internal string MaterialTransferWorldId => (_materialWorld as IOnlineMaterialTransferWorld)?.TransferWorldId;
        private WorldReplicationCoordinator _materialReplication;
        private bool _replicationPauseReported;
        private bool MaterialReplicationFaulted => IsAuthority && _materialReplication != null && _materialReplication.Faulted;
        private WorldReplicationNetworkChannel _materialChannel;
        public WorldReplicationCoordinator MaterialReplication => _materialReplication;
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
        // 复制/材料故障属于整个世界的暂停事实；Client须收到既有Frozen标记，而不是继续本地预测。
        public bool Frozen => IsAuthority ? _authoritySynchronizationFailure != null || _manager == null || !_manager.CanSimulate || !MaterialCanAdvance
            || _combat != null && _combat.Faulted : _frozen;
        public bool CanAdvance => WorldReady && !_stopped && _authoritySynchronizationFailure == null && MaterialCanAdvance && _combat != null && !_combat.Faulted && _manager != null
            && (IsAuthority ? _manager.CanSimulate : NetworkClient.isConnected && LocalPlayer != null && !_frozen
                && (WorldEpoch == 0 || _auth != null && _auth.ClientWorldEpoch == WorldEpoch));
        private bool MaterialCanAdvance => !MaterialReplicationFaulted && (_materialWorld == null || MaterialBindingAlive && _materialWorld.IsReady && !_materialWorld.Faulted);
        private bool MaterialBindingAlive => _materialWorld != null && !(_materialWorld is UnityEngine.Object value && value == null);
        public bool LocalInputSuppressed
        {
            get => _localMemberExitPending || _localInputSuppressed || _inventoryInputOwners.Count != 0;
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
            return InitializeDungeon(dungeonId, _instance.RunId, WorldEpoch != 0 ? WorldEpoch : CreateWorldEpoch());
        }

        private static ulong CreateWorldEpoch()
        {
            // 只在DS建立新World时分配，随机不透明身份不作为可排序Revision；同World重连不调用这里。
            Span<byte> bytes = stackalloc byte[8]; ulong value;
            do { RandomNumberGenerator.Fill(bytes); value = BitConverter.ToUInt64(bytes); } while (value == 0);
            return value;
        }

        private void ResolveManager()
        {
            if (_manager != null) return;
            _manager = NetworkManager.singleton as RoomNetworkManager;
            if (_manager == null) return;
            _auth = _manager.GetComponent<JoinTicketAuthenticator>(); _instance = _manager.GetComponent<RoomInstanceBootstrap>();
        }
        private bool InitializeDungeon(string dungeonId, string runId, ulong worldEpoch)
        {
            if (_stopped || _initializing || worldEpoch == 0 || string.IsNullOrEmpty(dungeonId) || string.IsNullOrEmpty(runId)) return false;
            if (WorldReady) return DungeonId == dungeonId && _runId == runId && WorldEpoch == worldEpoch;
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
                if (definition.WorldPrefab.activeSelf) throw new InvalidOperationException("正式World Prefab必须以inactive根导出，不能先Awake再配置材料权威。");
                _worldContent = Instantiate(definition.WorldPrefab, transform, false);
                _dungeonBindings = _worldContent.GetComponent<OnlineDungeonWorldBindings>();
                string bindingError = "缺少WorldBindings";
                if (_dungeonBindings == null || !_dungeonBindings.TryValidate(definition.EncounterCount, out bindingError))
                    throw new InvalidOperationException("正式地图入口/门接线缺失：" + bindingError);
                ConfigureMaterialWorldBeforeActivation(_worldContent, worldEpoch);
                _worldContent.SetActive(true);
                if (!_materialWorld.IsReady || _materialWorld.Faulted) throw new InvalidOperationException("材料World激活后未完成可信模式初始化。");
                // 复制状态跟随 World，换连接/Manager 不重建已安装版本；无 Installer 的正式 Replica 禁止启动。
                if (!IsAuthority && _materialWorld.ReplicaInstaller == null) throw new InvalidOperationException("Replica缺少原子Installer。");
                var capture = _materialWorld.CaptureBuffer;
                _materialReplication = new WorldReplicationCoordinator(WorldEpoch, _combatDefinition.WireHash,
                    WorldReplicationCoordinator.DefaultChunks, WorldReplicationCoordinator.DefaultWords,
                    capture != null ? capture.Cells.Length : 65536, capture != null ? capture.Pending.Length : 1024,
                    capture != null ? capture.InspectionBudget : 2000000, IsAuthority ? null : _materialWorld.ReplicaInstaller);
                if (IsAuthority) _materialReplication.SetFaultHandler(SynchronizeMaterialLifecycle);
                // 独立网络配置属于两端兼容输入；不能借单机活动半径隐式改变DS模拟，也不能缺失时静默全量发送。
                var materialProfile=Resources.Load<OnlineWorldReplicationProfile>("Online/WorldReplicationProfile");
                if(materialProfile==null || !materialProfile.Validate(out _) || !_materialReplication.ConfigureInterest(
                    materialProfile.ReferenceChunkWorldSize,materialProfile.RadiusXZChunks,materialProfile.RadiusYChunks))
                    throw new InvalidOperationException("缺少有效的独立联机材料Interest配置。");
                _combatSimulation = new CombatSimulationWorld();
                if (IsAuthority) _combatSimulation.ConfigureAuthorityWriteGate(() => CanAdvance);
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
        private void ConfigureMaterialWorldBeforeActivation(GameObject content, ulong worldEpoch)
        {
            if (content == null || content.activeSelf || content.activeInHierarchy || worldEpoch == 0 || _materialWorld != null)
                throw new InvalidOperationException("材料World必须在inactive边界绑定唯一非零世界身份。");
            IOnlineMaterialWorldBinding found = null;
            // 仅离散装配扫描一次，Tick通过缓存Core合同调用，不向ElementField/Rendering加程序集依赖。
            foreach (var component in content.GetComponentsInChildren<MonoBehaviour>(true))
                if (component is IOnlineMaterialWorldBinding binding)
                {
                    if (found != null) throw new InvalidOperationException("正式地图存在多个材料Binding。");
                    found = binding;
                }
            if (found == null) throw new InvalidOperationException("正式地图缺少材料Binding，请重新导出CPU世界资源。");
            // 失败也保留候选引用供StopWorld.Detach清理，不把半份配置当作原状退回单机。
            _materialWorld = found;
            if (!found.TryConfigure(IsAuthority ? WorldExecutionMode.Authority : WorldExecutionMode.Replica, worldEpoch))
                throw new InvalidOperationException("材料World模式/身份配置失败。");
            WorldEpoch = worldEpoch; found.SetSuspended(true);
        }

        internal void SynchronizeMaterialLifecycle()
        {
            if (_materialWorld == null) return;
            try
            {
                if (!MaterialBindingAlive)
                {
                    if (IsAuthority) PauseAuthorityForFailure("MaterialBindingUnavailable");
                    else StopWorld();
                    return;
                }
                // 挂起边界本身已抛异常时，不从Render/After/catch再次进入同一失败回调。
                // 自身sticky门已锁住Combat、Owned与后续外部Step，即使材料Owner无法确认暂停也不继续推进。
                if (_materialSuspensionFailed) return;
                // 每Render帧也执行：Frozen可能让逻辑步数为0，不能仅在Step中传暂停。
                // Replica始终不运行材料规则；安装数据和Rendering不由此授予模拟权。
                SetMaterialSuspended(_stopped || !WorldReady || !IsAuthority || Frozen || ResearchPaused);
                if (_stopped || _materialWorld == null || !MaterialBindingAlive) return;
                if ((MaterialReplicationFaulted || _materialWorld.Faulted) && !_replicationPauseReported)
                {
                    _replicationPauseReported = true;
                    // 挂起先于日志回调；保留Material/Combat/Owned/在途对象，不调用会销毁现场的StopWorld。
                    string reason = MaterialReplicationFaulted ? _materialReplication.Failure : "MaterialSimulationFaulted";
                    GameLog.Error("材料复制故障，已暂停该世界的新玩法写入并保留现场：" + reason, "OnlineWorld");
                }
            }
            catch (Exception error) { PauseAuthorityAfterException(error); throw; }
        }

        /// <summary>
        /// Authority所有运行期异常共用最终门。调用者随后使用throw保留原异常栈；这里不退款、不回滚、
        /// 不StopDriver/Detach/Dispose。若尝试挂起又抛异常，只记录次因，不能用它替换最初故障。
        /// </summary>
        internal void PauseAuthorityAfterException(Exception error)
        {
            if (!IsAuthority || _stopped) return;
            LatchAuthorityFailure(error.GetType().Name + ": " + error.Message);
            if (_authorityFailureReported) return;
            try { SetMaterialSuspended(true); }
            catch
            {
                // SetMaterialSuspended已锁存失败并记录次因；外层仍须原样传播最初的error。
                // 后续帧由_materialSuspensionFailed阻止重复回调，并非吞掉最初异常后继续模拟。
            }
            finally { ReportAuthorityFailure(); }
        }

        private void PauseAuthorityForFailure(string reason)
        {
            if (!IsAuthority || _stopped || _authoritySynchronizationFailure != null) return;
            LatchAuthorityFailure(reason);
            try { SetMaterialSuspended(true); }
            finally { ReportAuthorityFailure(); }
        }

        private void LatchAuthorityFailure(string reason)
        {
            if (_authoritySynchronizationFailure == null) _authoritySynchronizationFailure = reason;
        }

        private void ReportAuthorityFailure()
        {
            if (_authorityFailureReported || _authoritySynchronizationFailure == null) return;
            _authorityFailureReported = true;
            GameLog.Error("世界同步故障，已暂停新玩法写入并保留现场：" + _authoritySynchronizationFailure, "OnlineWorld");
        }

        private void SetMaterialSuspended(bool suspended)
        {
            if (_materialSuspensionFailed || _materialSuspensionInProgress || !MaterialBindingAlive) return;
            _materialSuspensionInProgress = true;
            try { _materialWorld.SetSuspended(suspended); }
            catch (Exception error)
            {
                _materialSuspensionFailed = true;
                if (IsAuthority && !_stopped)
                {
                    if (_authoritySynchronizationFailure == null) LatchAuthorityFailure("SetSuspended: " + error.Message);
                    else _authoritySynchronizationFailure += "; SetSuspended: " + error.Message;
                    ReportAuthorityFailure();
                }
                throw;
            }
            finally { _materialSuspensionInProgress = false; }
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
            EventBus<OnlineRunExitRequestedEvent>.Subscribe(OnLocalMemberExitRequested);
            EventBus<OnlineRunExitStatusChangedEvent>.Subscribe(OnLocalMemberExitStatus);
        }
        private void OnDisable()
        {
            EventBus<OnlineMovementMenuRequestEvent>.Unsubscribe(OnMenu);
            EventBus<OnlineMovementViewRequestedEvent>.Unsubscribe(OnViewRequested);
            EventBus<InventoryPresentationVisibilityEvent>.Unsubscribe(OnInventoryVisibility);
            EventBus<LocalActorCameraBoundEvent>.Unsubscribe(OnCameraBound);
            EventBus<OnlineRunExitRequestedEvent>.Unsubscribe(OnLocalMemberExitRequested);
            EventBus<OnlineRunExitStatusChangedEvent>.Unsubscribe(OnLocalMemberExitStatus);
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
            var nextManager = NetworkManager.singleton as RoomNetworkManager;
            var nextInstance = nextManager != null ? nextManager.GetComponent<RoomInstanceBootstrap>() : null;
            if (nextManager != null)
            {
                // Dedicated拒绝更换原Entry/Receipt Owner后保留故障现场；同一坏Manager不在每帧反复报错。
                if (IsAuthority && _combat != null && _combat.Faulted && _authoritySynchronizationFailure != null
                    && !ReferenceEquals(nextInstance, _combat.GameplayInstanceOwner)) return;
                var nextCombat = nextManager.GetComponent<CombatNetworkChannel>();
                if (nextCombat == null) nextCombat = nextManager.gameObject.AddComponent<CombatNetworkChannel>();
                if (_combat == null)
                {
                    // Manager可被后续World复用；上一World网络gap留下的disabled Adapter不代表新World应失去消息入口。
                    nextCombat.enabled = true;
                    var initialMovement = nextManager.GetComponent<MovementNetworkChannel>();
                    if (initialMovement != null) initialMovement.enabled = true;
                    var initialInventory = nextManager.GetComponent<InventoryNetworkChannel>();
                    if (initialInventory != null) initialInventory.enabled = true;
                    _combat = new OnlineCombatWorld(this, _combatDefinition, _combatSimulation, nextCombat, nextManager, nextInstance);
                }
                else if (!_combat.RebindNetworking(nextManager, nextCombat, nextInstance, out var rebindError))
                {
                    // ResolveManager可能已看见新单例，但玩法Owner必须仍指向原Instance，不能先换Entry再验证。
                    _manager = _combat.NetworkingManager; _instance = _combat.GameplayInstanceOwner;
                    _auth = _manager != null ? _manager.GetComponent<JoinTicketAuthenticator>() : null;
                    if (IsAuthority) PauseAuthorityForFailure("NetworkingRebindRejected: " + rebindError);
                    else { GameLog.Error("客户端网络重绑被拒绝：" + rebindError, "OnlineWorld"); StopWorld(); }
                    return;
                }
            }

            var nextProgress = nextManager != null ? nextManager.GetComponent<DungeonProgressNetworkChannel>() : null;
            var nextMaterial = nextManager != null ? nextManager.GetComponent<WorldReplicationNetworkChannel>() : null;
            var nextExit = nextManager != null ? nextManager.GetComponent<RunExitNetworkChannel>() : null;
            if (nextProgress != null && !ReferenceEquals(nextProgress, _progressChannel)) nextProgress.enabled = false;
            if (nextMaterial != null && !ReferenceEquals(nextMaterial, _materialChannel)) nextMaterial.enabled = false;
            if (nextExit != null && !ReferenceEquals(nextExit, _memberExitChannel)) nextExit.enabled = false;
            var previousExit = _memberExitChannel;
            ClearMemberExitChannel();
            if (previousExit != null && !ReferenceEquals(previousExit, nextExit)) previousExit.enabled = false;
            if (_boundManager != null)
            {
                _boundManager.ServerActorJoined -= OnJoined; _boundManager.ServerActorLeft -= OnLeft;
            }
            if (_channel != null) { _channel.InputReceived -= OnInput; _channel.StateReceived -= OnState; }
            // 结束的只有上一代传输Adapter；Combat及其Ground/Home/输出状态保留同一对象。
            if (_progressChannel != null)
            {
                _progressChannel.Configure(null, null, 0);
                _progressChannel.enabled = false;
            }
            if (_materialChannel != null) { _materialChannel.Configure(null, null); _materialChannel.enabled = false; }
            _materialChannel = null;
            _channel = null; _progressChannel = null; _boundManager = null;
            _manager = nextManager;
            if (_manager == null)
            {
                _combat?.SuspendNetworking(); _auth = null;
                if (!IsAuthority) _instance = null;
                return;
            }
            _auth = _manager.GetComponent<JoinTicketAuthenticator>(); _instance = _manager.GetComponent<RoomInstanceBootstrap>();
            _channel = _manager.GetComponent<MovementNetworkChannel>();
            if (_channel == null) _channel = _manager.gameObject.AddComponent<MovementNetworkChannel>();
            _channel.enabled = true;
            _channel.SimulationHash = _profile.SimulationHash;
            _channel.InputReceived += OnInput; _channel.StateReceived += OnState;
            if (nextExit != null) nextExit.enabled = true;
            BindMemberExitChannel();
            // 配置只取当前已通过Launch/ClientIdentity核验的World字段，不从新连接自报的字符串重新选图。
            // 首次初始化与同World换Manager共用这个入口，进度通道不会遗留在被销毁的上一代对象上。
            _progressChannel = _manager.GetComponent<DungeonProgressNetworkChannel>();
            if (_progressChannel == null) _progressChannel = _manager.gameObject.AddComponent<DungeonProgressNetworkChannel>();
            _progressChannel.Configure(_runId, DungeonId, _combatDefinition.WireHash);
            _progressChannel.enabled = true;
            if (_materialReplication != null)
            {
                _materialChannel = _manager.GetComponent<WorldReplicationNetworkChannel>();
                if (_materialChannel == null) _materialChannel = _manager.gameObject.AddComponent<WorldReplicationNetworkChannel>();
                _materialChannel.Configure(this, _materialReplication); _materialChannel.enabled = true;
            }
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
                    if (_auth.ClientWorldEpoch == 0) return;
                    if (!InitializeDungeon(identity.DungeonId, identity.RunId, _auth.ClientWorldEpoch))
                    { if (NetworkClient.active) NetworkClient.Disconnect(); return; }
                }
                else return;
            }
            if (_manager == null || _manager != NetworkManager.singleton || _boundManager != _manager) BindNetwork();
            if (IsAuthority)
            {
                SynchronizeMaterialLifecycle();
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
            while (_progressChannel != null && _progressChannel.TryTake(out var progress))
            { _dungeonRuntime.ApplyReplica(progress); ApplyReturnPortalProgress(progress); }
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
            if (WorldEpoch != 0 && _auth.ClientWorldEpoch != WorldEpoch)
            {
                if (_auth.ClientWorldEpoch == 0) return; // 新认证上下文还在等可靠世界握手，不重置原世界。
                GameLog.Error("存活世界拒绝未经重建的WorldEpoch替换。", "OnlineDungeon"); StopWorld(); NetworkClient.Disconnect(); return;
            }
            var id = _auth.ClientIdentity; if (_localEpoch == id.ConnectionEpoch) return;
            _localActor = id.ActorId; _localEpoch = id.ConnectionEpoch; _localSequence = _lastLocalSnapshot = 0;
            _prediction.Reset(_localEpoch); _frozen = false; _announced = false; _localInputSuppressed = false;
            var slot = Registry.EnsurePlayer((int)_localActor - 1, PlayerExecutionMode.Prediction);
            slot.Epoch = _localEpoch; slot.Connected = true; LocalPlayer = slot.Player; LocalPlayer.ClearOnlineInput();
            _combat.BindClient(slot, _localActor, _localEpoch, true);
            BindLocalReturnInteraction();
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
            if (!_combat.BindAuthority(slot, identity) || !_auth.SendWorldIdentity(connection, identity, WorldEpoch))
            { slot.Connected = false; connection.Disconnect(); }
            else if (HasMemberExitFence(slot))
            {
                slot.SimulationReady = true;
                _instance.RefreshMemberExitStatus(identity);
            }
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
            if (slot.Connected && !HasMemberExitFence(slot) && slot.Epoch == identity.ConnectionEpoch &&
                slot.Input.TryEnqueue(frame, identity.ConnectionEpoch, Time.realtimeSinceStartupAsDouble)) slot.SimulationReady = true;
        }
        public void Step(float dt, uint tick)
        {
            // 角色、遭遇、施法和Combat Before属于同一权威阶段；直接调用Step也不能漏掉异常锁存。
            try { StepGameplay(dt, tick); }
            catch (Exception error) { PauseAuthorityAfterException(error); throw; }
        }
        private void StepGameplay(float dt, uint tick)
        {
            if (!CanAdvance || ResearchPaused) return;
            if (IsAuthority)
            {
                ServerTick = tick;
                // 先标记出生 Tick，随后再推进动作与 Physics，防止新弹体被计入上一 Tick。
                _combatSimulation.BeginTick(tick);
                for (int i = 0; i < 4; i++)
                {
                    if (!CanAdvance) return;
                    var slot = Registry[i]; if (slot.Player == null || !slot.Targetable) continue;
                    var frame = slot.Input.Consume(Time.realtimeSinceStartupAsDouble);
                    if (HasMemberExitFence(slot))
                    {
                        frame.MoveAxis = Vector2.zero; frame.JumpPressed = frame.JumpHeld = frame.DashPressed = false;
                        frame.InputSuppressed = true;
                    }
                    slot.Player.Simulate(frame, dt);
                }
                if (!CanAdvance) return;
                _dungeonRuntime?.Step(dt, tick);
                EnsureAuthorityReturnPortal();
                if (!CanAdvance) return;
                _combat.StepAuthority(dt, tick);
                if (!CanAdvance) return; // 出生容量/配置错误可在施法内使世界 Faulted；同一 Tick 不再推进 AI/Physics。
                for (int i = 4; i < Registry.Capacity; i++)
                {
                    if (!CanAdvance) return;
                    var slot = Registry[i];
                    if (!slot.Connected || slot.Root == null || !slot.Root.activeInHierarchy) continue;
                    if (slot.Enemy != null) slot.Enemy.StepAuthority(dt, tick);
                    if (!CanAdvance) return;
                    if (slot.Boss != null)
                    {
                        // 保留单机 encounter 的接近触发含义，认证/加载完成本身不等于主动开战。
                        if (_dungeonDefinition == null && !slot.Boss.IsEncounterActive && OnlineEncounterEntry.CanActivate(Registry, slot.Body.position, _combatDefinition.BossActivationRadius))
                            slot.Boss.TryActivateAuthorityEncounter();
                        if (!CanAdvance) return;
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
        {
            if (!IsAuthority) return;
            try
            {
                // 挂起与Combat回调也在保护范围内，不能让原try外的异常绕过最终世界写门。
                SynchronizeMaterialLifecycle();
                if (!CanAdvance) return;
                _combatSimulation.StepAfterPhysics(dt, tick);
                SynchronizeMaterialLifecycle();
                if (!CanAdvance) return;
                if (_materialWorld == null) { CompleteMemberExitCaptures(tick); return; }
                var material = _materialWorld;
                // 与角色本Tick完成后的Pose一致，四槽不会轮转漏掉远处玩家。认证/首输入/生命资格沿原Roster。
                for (int i = 0; i < OnlineActorLimits.PlayerCount; i++)
                {
                    if (!CanAdvance) return;
                    var slot = Registry[i];
                    if (slot.Targetable && slot.Body != null)
                    {
                        if (!material.TrySetActor(i, (ulong)i + 1, slot.Body.position))
                        { PauseAuthorityForFailure("MaterialActorSynchronizationRejected: " + ((ulong)i + 1)); return; }
                    }
                    else material.ClearActor(i);
                }
                if (!CanAdvance) return;
                // 唯一外部30Hz喂时点；内部既有累加器保持材料10Hz，不从Update再推进一遍。
                material.AuthoritySimulation.StepAuthority(dt, tick);
                // 材料反应回调可以同步停止世界；StopWorld 已解绑并释放 Owner，不能再读取它。
                if (_stopped || !WorldReady || !ReferenceEquals(_materialWorld, material)) return;
                if (material.Faulted) SynchronizeMaterialLifecycle();
                else if (tick % 3 == 0 && _materialReplication != null)
                {
                    if (!_materialReplication.Capture(material.ReplicationSource, material.CaptureBuffer))
                        SynchronizeMaterialLifecycle(); // 含回调未绑定的隔离Owner，失败当次也不留一帧写窗口。
                    else CompleteRequestedRunCheckpoint(tick);
                }
                if (CanAdvance) CompleteMemberExitCaptures(tick);
            }
            catch (Exception error) { PauseAuthorityAfterException(error); throw; }
        }
        public void PumpMaterialReplication(double now)
        {
            // Manager可能已经由Unity销毁，但CLR字段尚待下一轮Bind清理；必须使用Unity的存活判定。
            if (_materialChannel != null) _materialChannel.Pump(now);
        }
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
            AddReturnPortalProgress(ref progress);
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
            ClearReturnPortalRuntime();
            ClearMemberExitChannel();
            CancelRequestedRunCheckpoint();
            if (_stopped) return; _stopped = true;
            if (!IsAuthority) LocalActorPresentation.Clear(GetInstanceID());
            _inventoryInputOwners.Clear();
            if (_driver != null) _driver.StopDriver();
            if (_materialChannel != null) { _materialChannel.Configure(null, null); _materialChannel.enabled = false; }
            _materialChannel = null;
            _materialReplication?.SetFaultHandler(null);
            _materialReplication?.Stop(); _materialReplication = null;
            var material = _materialWorld; _materialWorld = null;
            if (material != null && !(material is UnityEngine.Object value && value == null)) material.Detach();
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
