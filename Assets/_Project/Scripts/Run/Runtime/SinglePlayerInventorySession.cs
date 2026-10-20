using System;
using System.Collections.Generic;
using System.IO;
using Game.Core;
using UnityEngine;

namespace Game.Run
{
    /// <summary>单机整备的唯一写持有者。只读数据向 UI 开放，动作/保存通过带库存范围的 Core 命令。</summary>
    // 资源先走上一帧delta，再由角色支付本帧新施法；反序会在提交帧立刻减掉一帧冷却。
    [DefaultExecutionOrder(-100)]
    public sealed class SinglePlayerInventorySession : MonoBehaviour, IWalletReadOnly
    {
        [SerializeField] private ItemCatalog _catalog;
        [SerializeField] private SinglePlayerStartingInventory _startingInventory;
        [SerializeField,Tooltip("Owned法杖使用Draw V2；作者SO、敌人与Boss仍走各自Legacy入口。")]
        private bool _useSpellProgrammingV2;
        public bool UseSpellProgrammingV2 => _useSpellProgrammingV2;
        [SerializeField, Tooltip("独立 Sandbox 必须保持启用，避免测试动作写正式资产目录。")]
        private bool _sandboxProfile = true;
        [SerializeField,Tooltip("稳定世界键；Hub为hub.map1。仅保存据点地面资产，不保存元素世界。")] private string _worldSceneKey="";
        public string WorldSceneKey=>string.IsNullOrEmpty(_worldSceneKey)?gameObject.scene.path:_worldSceneKey;
        [SerializeField] private string _sandboxName = "Sandbox";
        [SerializeField,Tooltip("Scene实验许可：正式据点关闭，家/副本启用；UI不能绕过世界写入范围。")]
        private bool _allowWorldExperiments=true;
        [SerializeField,Tooltip("家没有仓库站点，禁止通过库存命令远程修改仓库。")]
        private bool _allowWarehouse=true;
#if UNITY_EDITOR
        [SerializeField,Tooltip("Scene Preview每次Play从场景布局建立临时资产，不触碰正式档；Saved Profile用于真实保存验证。")]
        private EditorInventoryProfileMode _editorProfileMode;
#endif
        [SerializeField,Tooltip("普通Drop允许支撑物的Physics Layer。Trigger、角色与拾取物始终排除。")]
        private LayerMask _dropGroundMask=~0;
        [SerializeField,Min(.1f),Tooltip("从候选位置向上开始探测的距离，单位米；不会把头顶屋顶当成可达支撑。")]
        private float _groundProbeAbove=4;
        [SerializeField,Min(.1f),Tooltip("从候选位置向下寻找地面的距离，单位米。")]
        private float _groundProbeBelow=80;
        [SerializeField,Range(0,1),Tooltip("地面Normal与世界Up的Dot下限；0.5允许最多约60度斜面。")]
        private float _groundMinNormalY=.5f;
        [SerializeField,Min(0)] private float _groundClearance=.01f;
        [SerializeField,Range(0,2),Tooltip("普通物体投到墙边时，在碰撞点附近寻找完整落地支撑的最大半径；普通Drop和保存姿态不使用此搜索。")] private float _throwLandingSearchRadius=1.5f;
        private readonly WorldItemGroundPlacement _groundPlacement=new WorldItemGroundPlacement();
        private readonly Dictionary<(string,string),WorldItemSpawnPoint> _visualSources=new Dictionary<(string,string),WorldItemSpawnPoint>();
        private readonly Dictionary<int,Transform> _worldRoots=new Dictionary<int,Transform>();
        private Dictionary<(string,string),string> _awaySpawnIds=new Dictionary<(string,string),string>();
        private readonly HashSet<string> _practiceWorldIds=new HashSet<string>(StringComparer.Ordinal);
        private bool _practiceWorkspace;
        private bool _practiceReloadCommitted;
        internal void SuppressPracticeExitSave() => _practiceReloadCommitted = true;
        internal void ResumePracticeExitSave() => _practiceReloadCommitted = false;
        internal bool IsAwayRuntime=>_practiceWorkspace || _profile?.PendingRaid!=null;
        [SerializeField,Tooltip("P7/P8共用背包和拾取的运行时工作区；由Editor工具生成，不保存Scene对象引用。")]
        private GameObject _practiceWorkspacePrefab;
        internal GameObject PracticeWorkspacePrefab=>_practiceWorkspacePrefab;
        public bool IsScenePreview{get;private set;}
        public string ProfileModeDescription=>IsScenePreview?"Scene Preview · 临时内存资产，下一次Play重置":"Saved Profile · 持久化资产";
        public string ProfileStoragePath=>_repository?.MainPath;
        private SinglePlayerProfile _profile;
        private ProfileSaveRepository _repository;
        private InventoryReadModel _read;
        private bool _subscribed;
        private bool _initializationAttempted;
        private uint _savedVersion;
        private bool _worldPresentationDirty;
        private uint _savedToolVersion, _walletVersion, _savedWalletVersion;
        internal int PendingDungeonDrops;
        internal bool TryRecordDungeonCompletion()
        {
            if(!_practiceWorkspace||_profile==null||RaidWriteLocked||_profile.AdventureProgress==long.MaxValue)return false;
            _profile.AdventureProgress++;_walletVersion++;return true;
        }
        internal bool TryCreditDungeonCoins(long amount)
        {
            if(!_practiceWorkspace||_profile==null||_read==null||WorldActor==null||amount<=0
                ||RaidWriteLocked||_profile.RaidActionsFrozen||_profile.Currency>long.MaxValue-amount)return false;
            _profile.Currency+=amount;_walletVersion++;
            EventBus<InventoryChangedEvent>.Publish(new InventoryChangedEvent{InventoryId=_read.InventoryId,Version=_read.Version});return true;
        }
        private ToolbeltService _toolbelt;
        public IToolbeltReadOnly ToolbeltReadOnly => _toolbelt;
        public IWalletReadOnly WalletReadOnly => this;
        public long Currency => _profile?.Currency ?? 0;
        uint IWalletReadOnly.Version => _walletVersion;
        private int _dropSequence;
        internal Transform WorldActor;
        /// <summary>表现只读角色空间位置，不能通过这个入口提交资产或切图。</summary>
        public Transform ActorTransform => WorldActor;
        private readonly List<WorldItemPickup> _spawnedPickups=new List<WorldItemPickup>();
        private WandLoadoutService _wands;
        private RaidSpellEligibility _eligibility;
        private CastResourceService _casts;
        private WandExecutionTransactionService _castsV2;
        public ICastResourceReadOnly V2CastResourcesReadOnly => _castsV2?.ReadOnly;
        public bool TryGetWandExecution(string id, out WandExecutionSnapshot snapshot)
        { snapshot = default; return _castsV2 != null && _castsV2.TryGetState(id, out snapshot); }
        public bool TryCaptureWandResearch(string id, out WandResearchSnapshot snapshot)
        {
            snapshot = null;
            if (_castsV2 == null || !TryGetWandExecution(id, out var state) || !_wands.ReadOnly.TryGetState(id, out var resources)) return false;
            var uses = new int[state.Program.Count]; var eligible = new bool[uses.Length];
            bool peacefulExperiment = _allowWorldExperiments && !IsAwayRuntime;
            bool entry = _eligibility.RaidId == null && !peacefulExperiment;
            for (int i = 0; i < uses.Length; i++)
            {
                if(state.Program.Instructions[i].IsIntrinsic){uses[i]=-1;eligible[i]=true;continue;}
                string card = state.Program.Instructions[i].InstanceId;
                if (!_profile.Items.TryGet(card, out var item)) return false;
                var kind = state.Program.Instructions[i].Kind;
                bool qualification = kind == Game.Skills.SpellKind.Modify || kind == Game.Skills.SpellKind.Multicast || kind == Game.Skills.SpellKind.Copy;
                uses[i] = qualification || item.HasUnlimitedCharges ? -1 : item.RemainingCharges;
                // 据点明确假设下次出发资格；不写真实资格，也不回满Mana。
                eligible[i] = !qualification || _eligibility.CanExecuteQualification(item, peacefulExperiment) || entry && item.RemainingCharges > 0;
            }
            var definitions = new List<Game.Skills.SpellDefinition>(); var ids = new List<string>();
            foreach (var owned in _profile.Items)
            {
                if (!_profile.Items.Catalog.TryGet(owned.Item.ContentId, out var definition) || definition.Kind != ItemKind.Spell) continue;
                var location = owned.Location; bool accessible = location.Kind == ItemLocationKind.SpellStorage && location.OwnerId == _profile.ActorId;
                // 库是“可安装的散装实例”，不是全部所有物；已装进任意法杖的卡只出现在对应序列。
                // 按实例过滤，保留同ContentId的其它副本；拆卸/交换后权威位置改变便自然返回库。
                if (location.Kind == ItemLocationKind.WandSlot) continue;
                var root = location;
                while (root.HasParentInstance && _profile.Items.TryGetLocation(root.OwnerId, out var parent)) root = parent;
                accessible |= root.OwnerId == _profile.ActorId && (root.Kind == ItemLocationKind.EquippedBackpack || root.Kind == ItemLocationKind.EquippedWand);
                if (!accessible) continue;
                definitions.Add(definition.SpellDefinition); ids.Add(owned.Item.InstanceId);
            }
            if (!Game.Skills.SpellProgramCompiler.TryCompile(definitions, new Game.Skills.SpellProgramCompileInputs(1, ids), out var library, out _)) return false;
            snapshot = new WandResearchSnapshot(state, new Game.Skills.SpellExecutionInputs(state.Mana, resources.MaxMana, uses, eligible,
                inputVersion: _profile.Items.Version), library, entry); return true;
        }
        // Session拥有归档，AdoptCommitted只重建资源服务；历史不进入Profile DTO或资源恢复。
        private WandExecutionReplayArchive _executionReplays = new WandExecutionReplayArchive();
        public WandExecutionReplayArchive ExecutionReplays => _executionReplays;
        internal WandSessionTransfer CaptureWandTransfer() => new WandSessionTransfer { Resources = _wands.CaptureContinuity(), Archive = _executionReplays, UseV2 = _useSpellProgrammingV2 };
        internal void RestoreWandTransfer(WandSessionTransfer transfer, bool resources = true)
        {
            if (transfer == null) return; _useSpellProgrammingV2=transfer.UseV2; if (resources) _wands.RestoreContinuity(transfer.Resources); _executionReplays = transfer.Archive;
            _castsV2?.CancelAll();
            _castsV2 = new WandExecutionTransactionService(_profile, _wands, _eligibility, _read.InventoryId, _executionReplays, _casts.MaterialOutputs, _allowWorldExperiments);
        }
        private InventoryMaterialTransferService _materials;
        private ContainerUseService _containerUses;
        private SpellAttachmentService _attachments;
        public SinglePlayerRunCoordinator RunCoordinator { get; internal set; }
        internal ExperimentAreaController ExperimentArea {get;set;}
        internal HubInteractionController HubInteraction {get;set;}
        // 练习副本复用Hub资产但不建立PendingRaid；资格/冻结仍由命令Gate裁定，不能继承城镇禁用开关。
        public bool WorldActionsAllowed=>_profile!=null && (IsAwayRuntime || _allowWorldExperiments && (ExperimentArea==null || ExperimentArea.IsInside));
        public bool TryFindMaterialContent(byte key, out string contentId)
        { contentId = null; return _profile != null && _profile.Items.Catalog.TryFindMaterialContent(key, out contentId); }
        public bool CanAccessWarehouse => _allowWarehouse && !_practiceWorkspace && _profile != null && _profile.PendingRaid == null && !_profile.RaidActionsFrozen
            && (RunCoordinator == null || RunCoordinator.State.Phase == RaidPhase.HubReady)
            && (HubInteraction == null || HubInteraction.TryGetStation(out var station)
                && (station.Kind == HubStationKind.Warehouse || station.Kind == HubStationKind.Shop));
        internal SinglePlayerProfile Profile => _profile;
        internal ProfileSaveRepository Repository => _repository;
        internal bool RaidWriteLocked => _practiceWorkspace && (_profile?.RaidActionsFrozen??false) || RunCoordinator != null && !RunCoordinator.ActionsAllowed;
        public int PendingMaterialOutputs => _profile?.PendingMaterialOutputs ?? 0;
        public int PendingContainerFlights => _containerUses?.PendingFlights ?? 0;
        [SerializeField, Tooltip("只供独立次数测试场景模拟入局；正式出发由 M4 发命令。")]
        private bool _startSandboxRaid;
        public ISpellUseReadOnly SpellUsesReadOnly => _eligibility?.ReadOnly;
        public ICastResourceReadOnly CastResourcesReadOnly => _casts?.ReadOnly;
        public IWandLoadoutReadOnly WandsReadOnly => _wands?.ReadOnly;
        public string EquippedBackpackId
        {
            get
            {
                if (_profile == null) return null;
                foreach (var entry in _profile.Items)
                    if (entry.Location.Kind == ItemLocationKind.EquippedBackpack && entry.Location.OwnerId == _profile.ActorId) return entry.Item.InstanceId;
                return null;
            }
        }
        private void Update() { if (_casts == null || !_casts.IsPending) _wands?.Tick(Time.deltaTime); }
        public IInventoryReadOnly ReadOnly => _read;
        public int SpellStorageCapacity => _profile?.Items.Catalog.SpellStorageCapacity ?? 12;
        public string InitializationMessage { get; private set; }
        public bool HasUnsavedChanges => _profile != null && (_worldPresentationDirty || _profile.SaveGeneration == 0 || _profile.Items.Version != _savedVersion
            || _profile.ToolConfigurationVersion != _savedToolVersion || _walletVersion!=_savedWalletVersion);
        public long SavedGeneration => _profile?.SaveGeneration ?? 0;

        private void Start()
        {
            if (_initializationAttempted) return;
            string directory = Path.Combine(Application.persistentDataPath, "SinglePlayer");
            if (_sandboxProfile)
            {
                if (string.IsNullOrWhiteSpace(_sandboxName) || _sandboxName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                    || _sandboxName == "." || _sandboxName == "..")
                { _initializationAttempted = true; InitializationMessage = "测试档子目录非法。"; return; }
                directory = Path.Combine(directory, _sandboxName);
            }
            IProfileFileSystem files=null;
#if UNITY_EDITOR
            if(_editorProfileMode==EditorInventoryProfileMode.ScenePreview)files=new EditorScenePreviewFileSystem();
#endif
            ConfigureAndInitialize(_catalog, _startingInventory, directory,files);
        }

        public bool ConfigureAndInitialize(ItemCatalog catalog, SinglePlayerStartingInventory seeds, string directory,IProfileFileSystem files=null)
        {
            if (_profile != null) return false;
            // 显式路径加载失败也属于已尝试初始化；Start 不能转去默认路径发放另一份新档。
            _initializationAttempted = true;
            if (catalog == null || !catalog.TryCreateIndex(out ItemCatalogIndex index, out string error))
            { InitializationMessage = "物品目录无效，未建立库存。"; return false; }
            var repository = new ProfileSaveRepository(directory, index,files);
#if UNITY_EDITOR
            IsScenePreview=files is EditorScenePreviewFileSystem;
#endif
            ProfilePersistenceResult load = repository.TryLoad(out SinglePlayerProfile profile);
            if (load.Status == ProfilePersistenceStatus.Missing)
            {
                if (seeds == null || !seeds.TryCreate(index, out profile, out error))
                { InitializationMessage = "缺少合法新档初始资产。"; return false; }
            }
            else if (!load.Success) { InitializationMessage = load.Message ?? "存档无法读取，原文件已保留。"; return false; }
            InitializationMessage = load.Message;
            if (profile.PendingRaid != null)
            {
                var recovered = new ProfileTransactionJournal(repository).Recover(profile, out var resolved);
                if (!recovered.Success) { InitializationMessage = "未结束冒险恢复失败：" + recovered.Message; return false; }
                profile = resolved; InitializationMessage = "未结束冒险已按可靠记录处理；未恢复旧箱庭。";
            }
            return Initialize(profile, repository);
        }

        public bool Initialize(SinglePlayerProfile profile, ProfileSaveRepository repository)
        {
            if (_profile != null || profile == null || repository == null) return false;
            _initializationAttempted = true;
            _profile = profile; _repository = repository;
            // 初始化只扫描本Scene一次。复制前缓存作者Renderer开关，之后模板隐藏仍能重建正确外观。
            foreach(var root in gameObject.scene.GetRootGameObjects())foreach(var point in root.GetComponentsInChildren<WorldItemSpawnPoint>(true))
                if(point.enabled && point.Definition!=null && !string.IsNullOrWhiteSpace(point.SpawnId))
                {point.CaptureAppearance();_visualSources.TryAdd((point.SceneKey,point.SpawnId),point);}
            _read = new InventoryReadModel(profile, Guid.NewGuid().ToString("N"));
            _wands = new WandLoadoutService(profile, _read.InventoryId);
            _toolbelt = new ToolbeltService(profile, _read.InventoryId);
            _eligibility = new RaidSpellEligibility(profile);
            _casts = new CastResourceService(profile, _wands, _eligibility, _read.InventoryId);
            _castsV2 = new WandExecutionTransactionService(profile, _wands, _eligibility, _read.InventoryId, _executionReplays, _casts.MaterialOutputs, _allowWorldExperiments);
            _attachments = new SpellAttachmentService(profile, _read.InventoryId);
            _materials = new InventoryMaterialTransferService(profile, _read.InventoryId);
            _containerUses = new ContainerUseService(profile, _read.InventoryId);
            if (_sandboxProfile && _startSandboxRaid && !_eligibility.TryBegin("sandbox." + _read.InventoryId, out InventoryFailureReason raidReason))
                InitializationMessage = "测试入局资格失败：" + raidReason;
            _wands.Synchronize();
            _savedVersion = profile.Items.Version;
            _savedToolVersion = profile.ToolConfigurationVersion;
            // 恢复地面资产仅重建整备拾取代理；没有恢复箱庭 Scene 或元素模拟进度。
            foreach (InventoryEntry entry in profile.Items)
                if (entry.Location.Kind == ItemLocationKind.WorldItem) CreateProxy(entry.Item.InstanceId, entry.Location.OwnerId, true);
            EventBus<InventoryChangedEvent>.Publish(new InventoryChangedEvent { InventoryId = _read.InventoryId, Version = _read.Version });
            return true;
        }

        private void OnEnable()
        {
            foreach(var root in _worldRoots.Values)if(root!=null)root.gameObject.SetActive(true);
            if (_subscribed) return;
            EventBus<InventoryCommandEvent>.Subscribe(OnCommand);
            EventBus<InventoryChangedEvent>.Subscribe(OnInventoryChanged);
            EventBus<WorldItemVisualBoundEvent>.Subscribe(OnVisualBindingChanged);
            EventBus<WandSelectionRequestedEvent>.Subscribe(OnWandSelection);
            EventBus<ToolbeltBindingRequestedEvent>.Subscribe(OnToolBinding);
            EventBus<HeldItemSelectionRequestedEvent>.Subscribe(OnHeldSelection);
            EventBus<WandResourceRequestedEvent>.Subscribe(OnWandResource);
            EventBus<CastResourceRequestedEvent>.Subscribe(OnCastResource);
            EventBus<SpellExecutionObservedEvent>.Subscribe(OnSpellObserved);
            EventBus<SpellRaidLifecycleRequestedEvent>.Subscribe(OnRaidLifecycle);
            EventBus<InventorySaveRequestedEvent>.Subscribe(OnSaveRequested); _subscribed = true;
            EventBus<MaterialTransferRequestedEvent>.Subscribe(OnMaterialTransfer);
            EventBus<ContainerUseRequestedEvent>.Subscribe(OnContainerUse);
            EventBus<ContainerImpactEvent>.Subscribe(OnContainerImpact);
            EventBus<ContainerTransitEndedEvent>.Subscribe(OnContainerTransitEnded);
            EventBus<SpellAttachmentRequestedEvent>.Subscribe(OnAttachment);
            EventBus<SpellMaterialOutputEvent>.Subscribe(OnMaterialOutput);
        }
        private void OnDisable()
        {
            // Runtime根与Session并列；必须显式同步停用，防止已关闭的Session仍留下可拾取代理。
            foreach(var root in _worldRoots.Values)if(root!=null)root.gameObject.SetActive(false);
            if (!_subscribed) return;
            EventBus<InventoryCommandEvent>.Unsubscribe(OnCommand);
            EventBus<InventoryChangedEvent>.Unsubscribe(OnInventoryChanged);
            EventBus<WorldItemVisualBoundEvent>.Unsubscribe(OnVisualBindingChanged);
            EventBus<WandSelectionRequestedEvent>.Unsubscribe(OnWandSelection);
            EventBus<ToolbeltBindingRequestedEvent>.Unsubscribe(OnToolBinding);
            EventBus<HeldItemSelectionRequestedEvent>.Unsubscribe(OnHeldSelection);
            EventBus<WandResourceRequestedEvent>.Unsubscribe(OnWandResource);
            EventBus<CastResourceRequestedEvent>.Unsubscribe(OnCastResource);
            EventBus<SpellExecutionObservedEvent>.Unsubscribe(OnSpellObserved);
            EventBus<SpellRaidLifecycleRequestedEvent>.Unsubscribe(OnRaidLifecycle);
            _casts?.CancelAll();
            _castsV2?.CancelAll();
            _casts?.MaterialOutputs.EndAll();
            EventBus<InventorySaveRequestedEvent>.Unsubscribe(OnSaveRequested); _subscribed = false;
            EventBus<MaterialTransferRequestedEvent>.Unsubscribe(OnMaterialTransfer);
            EventBus<ContainerUseRequestedEvent>.Unsubscribe(OnContainerUse);
            EventBus<ContainerImpactEvent>.Unsubscribe(OnContainerImpact);
            EventBus<ContainerTransitEndedEvent>.Unsubscribe(OnContainerTransitEnded);
            _containerUses?.EndWorldTransits();
            EventBus<SpellAttachmentRequestedEvent>.Unsubscribe(OnAttachment);
            EventBus<SpellMaterialOutputEvent>.Unsubscribe(OnMaterialOutput);
        }
        private void OnApplicationQuit()
        {
            if (_profile?.PendingRaid != null && RunCoordinator != null) { RunCoordinator.CloseSynchronously(); return; }
            // 已投出的量属于将结束的世界；显式终止在途，再保存剩余整备资产。正式Raid终局留M4。
            _containerUses?.EndWorldTransits();
            _casts?.MaterialOutputs.EndAll();
            // 当前只服务整备资产；M4 接 PendingRaid 后必须按 Raid 终局策略提交，不能沿用盲目退出保存。
            if (!HasUnsavedChanges) return;
            ProfilePersistenceResult result = Save();
            if (!result.Success) GameLog.Warn("退出时整备保存失败：" + result.Message, "InventorySave");
        }
        private void OnDestroy()
        {
            // Retry已可靠提交入场快照；旧Scene销毁不能再把战败后的资产写回覆盖它。
            if (_practiceReloadCommitted) { EndWorldResources(); }
            else if(_practiceWorkspace && _profile!=null)
            {var saved=SavePracticeForLeaving();if(!saved.Success)GameLog.Error("练习Scene退出保存失败："+saved.Message,"Practice");}
            // 同一轮Play换Scene保留临时状态；正式存档沿用既有退出/事务提交路径。
            else if(IsScenePreview && _profile!=null && _profile.PendingRaid==null){EndWorldResources();Save();}
            foreach(var root in _worldRoots.Values)if(root!=null)Destroy(root.gameObject);
            _worldRoots.Clear();_spawnedPickups.Clear();_visualSources.Clear();
        }
        private void OnVisualBindingChanged(WorldItemVisualBoundEvent change)
        {
            if(change.Added || _read==null || change.InventoryId!=_read.InventoryId)return;
            for(int i=_spawnedPickups.Count-1;i>=0;i--)
                if(_spawnedPickups[i]==null || _spawnedPickups[i].transform==change.Root)_spawnedPickups.RemoveAt(i);
        }
        private void OnInventoryChanged(InventoryChangedEvent e)
        { if (_read != null && e.InventoryId == _read.InventoryId) { _wands.Synchronize(); _toolbelt?.Synchronize();
                if (_toolbelt!=null && _toolbelt.SelectedSlot>=0 && _toolbelt.SelectedSlot<3) _wands.TrySelect(_toolbelt.SelectedSlot); } }
        private void OnWandSelection(WandSelectionRequestedEvent e)
        { if (_read != null && e.InventoryId == _read.InventoryId && !RaidWriteLocked && _wands.TrySelect(e.SlotIndex)) _toolbelt?.TrySelect(e.SlotIndex); }
        private void OnToolBinding(ToolbeltBindingRequestedEvent request)
        {
            if (_read == null || request.InventoryId != _read.InventoryId) return;
            bool success = !RaidWriteLocked && _toolbelt.TryBind(request.ToolSlot, request.ItemId, request.ExpectedVersion);
            if (success) EventBus<InventoryChangedEvent>.Publish(new InventoryChangedEvent { InventoryId = _read.InventoryId, Version = _read.Version });
            EventBus<ToolbeltBindingResultEvent>.Publish(new ToolbeltBindingResultEvent { InventoryId = request.InventoryId,
                RequestId = request.RequestId, Success = success, Failure = RaidWriteLocked ? ToolbeltFailure.Busy : _toolbelt.LastFailure });
        }
        private void OnHeldSelection(HeldItemSelectionRequestedEvent request)
        {
            if (_read == null || request.InventoryId != _read.InventoryId || RaidWriteLocked || Time.timeScale <= 0) return;
            if (request.SlotIndex < 3 && !_wands.TrySelect(request.SlotIndex)) return;
            _toolbelt.TrySelect(request.SlotIndex);
        }
        private void OnWandResource(WandResourceRequestedEvent e)
        { if (_read != null && e.InventoryId == _read.InventoryId && !_casts.IsPending)
                EventBus<WandResourceResultEvent>.Publish(_wands.Execute(e)); }
        private void OnSpellObserved(SpellExecutionObservedEvent observation) { _castsV2?.Observe(observation); }
        private void OnCastResource(CastResourceRequestedEvent request)
        {
            if (_read == null || request.InventoryId != _read.InventoryId) return;
            if ((RaidWriteLocked && (request.Operation == CastResourceOperation.PrepareRoot || request.Operation == CastResourceOperation.PrepareLayer))
                || !WorldActionsAllowed && request.Operation==CastResourceOperation.PrepareRoot)
            {
                EventBus<CastResourceResultEvent>.Publish(new CastResourceResultEvent { InventoryId = request.InventoryId,
                    SourceWandId = request.SourceWandId, CallerId = request.CallerId, RequestId = request.RequestId,
                    TicketId = request.TicketId, Operation = request.Operation, Reason = OwnedWandFailureReason.ResourceBusy }); return;
            }
            uint before = _profile.Items.Version;
            CastResourceResultEvent result = request.UseV2 ? _castsV2.Execute(request) : _casts.Execute(request);
            if (before != _profile.Items.Version)
                EventBus<InventoryChangedEvent>.Publish(new InventoryChangedEvent { InventoryId = _read.InventoryId, Version = _read.Version });
            EventBus<CastResourceResultEvent>.Publish(result);
        }
        private void OnRaidLifecycle(SpellRaidLifecycleRequestedEvent request)
        {
            if (_read == null || request.InventoryId != _read.InventoryId) return;
            InventoryFailureReason reason = InventoryFailureReason.ResourceBusy;
            bool success = false;
            if (!_casts.IsPending)
            {
                if (request.Begin) success = _eligibility.TryBegin(request.RaidId, out reason);
                else { _casts.CancelAll(); success = _eligibility.TryEnd(out reason); }
            }
            if (success) EventBus<InventoryChangedEvent>.Publish(new InventoryChangedEvent { InventoryId = _read.InventoryId, Version = _read.Version });
            EventBus<SpellRaidLifecycleResultEvent>.Publish(new SpellRaidLifecycleResultEvent { InventoryId = _read.InventoryId,
                RaidId = request.RaidId, Success = success, Reason = reason });
        }
        public ProfilePersistenceResult Save()
        {
            if (_profile == null) return new ProfilePersistenceResult(ProfilePersistenceStatus.WriteBlocked, "库存未初始化。");
            if (_profile.PendingRaid != null && RunCoordinator != null) return RunCoordinator.Checkpoint();
            // 本局资格不能写入 Hub 快照；M4 PendingRaid 接入前只允许整备保存。
            if (_containerUses.PendingFlights > 0)
                return new ProfilePersistenceResult(ProfilePersistenceStatus.WriteBlocked, "投掷材料仍在世界待投放，结束后再保存据点。");
            if (_eligibility.RaidId != null || _casts.IsPending || _profile.Items.IsResourceBusy)
                return new ProfilePersistenceResult(ProfilePersistenceStatus.WriteBlocked, "测试冒险进行中，请先结束测试局再保存整备。");
            ProfilePersistenceResult result = _repository.TrySave(_profile);
            if (result.Success) { _savedVersion = _profile.Items.Version; _savedToolVersion = _profile.ToolConfigurationVersion;_savedWalletVersion=_walletVersion;_worldPresentationDirty=false; }
            return result;
        }
        private void OnMaterialTransfer(MaterialTransferRequestedEvent request)
        {
            if (_read == null || request.InventoryId != _read.InventoryId) return;
            if (RaidWriteLocked || !WorldActionsAllowed) { EventBus<MaterialTransferResultEvent>.Publish(new MaterialTransferResultEvent
                { InventoryId = request.InventoryId, SourceId = request.SourceId, RequestId = request.RequestId, Failure = MaterialTransferFailure.ResourceBusy }); return; }
            var result = _materials.Execute(request);
            // 数量变化不重建法杖程序；UI 自己读取版本，避免连续采集触发全量程序快照分配。
            EventBus<MaterialTransferResultEvent>.Publish(result);
        }
        private void OnContainerUse(ContainerUseRequestedEvent request)
        {
            if (_read == null || request.InventoryId != _read.InventoryId) return;
            // 实验区限制的是材料世界写入；普通物体只迁移原有实例，和丢到地面一样可在据点其他地方执行。
            bool ordinaryThrow=request.Kind==ContainerUseKind.Throw && _profile.Items.TryGet(request.ItemId,out var throwable)
                && throwable.Composition==null && throwable.Material==null && _profile.Items.Catalog.TryGetRules(throwable.ContentId,out var rules) && rules.UseKind==ItemUseKind.Throw;
            if (RaidWriteLocked || !WorldActionsAllowed && !ordinaryThrow) {EventBus<ContainerUseResultEvent>.Publish(new ContainerUseResultEvent{Kind=request.Kind,
                Transfer=new MaterialTransferResultEvent{InventoryId=request.InventoryId,SourceId=request.SourceId,RequestId=request.RequestId,Failure=MaterialTransferFailure.ResourceBusy}});return;}
            var result = _containerUses.Execute(request);
            if (result.Success && request.Kind != ContainerUseKind.Pour)
                EventBus<InventoryChangedEvent>.Publish(new InventoryChangedEvent { InventoryId = _read.InventoryId, Version = _read.Version });
            EventBus<ContainerUseResultEvent>.Publish(result);
        }
        private void OnContainerImpact(ContainerImpactEvent impact)
        {
            if (_read == null || impact.InventoryId != _read.InventoryId) return;
            WorldItemPickup landing=null;string landedId=null;
            // 在途普通物体先准备隐藏Pickup，再提交落地。创建/地面检查失败让原代理重试，避免物品变隐形。
            foreach(var entry in _profile.Items)
                if(entry.Location.Kind==ItemLocationKind.ThrownContainer && entry.Location.OwnerId==impact.FlightId && entry.Item.Composition==null && entry.Item.Material==null)
                {landedId=entry.Item.InstanceId;break;}
            if(landedId!=null)
            {
                if(!float.IsFinite(impact.Position.x) || !float.IsFinite(impact.Position.y) || !float.IsFinite(impact.Position.z))return;
                try
                {
                    landing=CreateProxy(landedId,impact.FlightId,false);if(landing==null)return;
                    landing.transform.position=impact.Position;
                    if(!_groundPlacement.TryPlace(landing.gameObject,landing.gameObject.scene,WorldActor,_dropGroundMask,
                        _groundProbeAbove,_groundProbeBelow,_groundMinNormalY,_groundClearance,_throwLandingSearchRadius))
                    {Destroy(landing.gameObject);return;}
                }
                catch(Exception exception){if(landing!=null)Destroy(landing.gameObject);GameLog.Warn("投掷物落地代理准备失败："+exception.Message,"Items");return;}
            }
            var result = _containerUses.Impact(impact);
            if(landing!=null)
            {
                if(!result.Success)Destroy(landing.gameObject);
                else
                {landing.TryBind(_profile.Items,landedId,impact.FlightId,_read.InventoryId);RememberGround(landedId,landing.transform);landing.gameObject.SetActive(true);}
            }
            if (result.Success) EventBus<InventoryChangedEvent>.Publish(new InventoryChangedEvent { InventoryId = _read.InventoryId, Version = _read.Version });
            EventBus<ContainerUseResultEvent>.Publish(result);
        }
        private void OnContainerTransitEnded(ContainerTransitEndedEvent ended)
        { if (_read != null && ended.InventoryId == _read.InventoryId) _containerUses.End(ended); }
        public bool TryCopyContainer(string itemId, MaterialAmount[] buffer, out int count, out ulong total, out ulong capacity)
        {
            count = 0; total = capacity = 0;
            if (_profile == null || !_profile.Items.TryGet(itemId, out var item) || item.Composition == null) return false;
            count = item.Composition.CopyTo(buffer); total = item.Composition.Total; capacity = item.Composition.Capacity; return true;
        }
        private void OnAttachment(SpellAttachmentRequestedEvent request)
        {
            if (_read == null || request.InventoryId != _read.InventoryId) return;
            if (RaidWriteLocked) {EventBus<SpellAttachmentResultEvent>.Publish(new SpellAttachmentResultEvent{InventoryId=request.InventoryId,
                SpellId=request.SpellId,CallerId=request.CallerId,RequestId=request.RequestId,Reason=InventoryFailureReason.ResourceBusy});return;}
            var result = _attachments.Execute(request); EventBus<SpellAttachmentResultEvent>.Publish(result);
            if (result.Success) EventBus<InventoryChangedEvent>.Publish(new InventoryChangedEvent { InventoryId = _read.InventoryId, Version = _read.Version });
        }
        private void OnMaterialOutput(SpellMaterialOutputEvent request)
        {
            if (_read == null || request.InventoryId != _read.InventoryId) return;
            EventBus<SpellMaterialOutputResultEvent>.Publish(_casts.MaterialOutputs.Execute(request));
        }
        private void OnSaveRequested(InventorySaveRequestedEvent request)
        {
            if (_read == null || !string.Equals(request.InventoryId, _read.InventoryId, StringComparison.Ordinal)) return;
            ProfilePersistenceResult result = Save();
            EventBus<InventorySaveResultEvent>.Publish(new InventorySaveResultEvent
            { InventoryId = _read.InventoryId, RequestId = request.RequestId, Success = result.Success,
                Generation = SavedGeneration, Version = _read.Version, Message = result.Message });
        }
        private void OnCommand(InventoryCommandEvent command)
        {
            if (_read == null || command.Kind == InventoryCommandKind.Pickup
                || !string.Equals(command.InventoryId, _read.InventoryId, StringComparison.Ordinal)) return;
            uint before = _profile.Items.Version;
            InventoryOperationResult result = command.ExpectedVersion != before
                ? _profile.Items.Result(false, InventoryFailureReason.StaleVersion) : Execute(command);
            EventBus<InventoryCommandResultEvent>.Publish(new InventoryCommandResultEvent
            { InventoryId = _read.InventoryId, RequestId = command.RequestId, ItemId = command.ItemId,
                Success = result.Success, Reason = result.Reason, Version = result.Version });
            if (result.Success && result.Version != before)
                EventBus<InventoryChangedEvent>.Publish(new InventoryChangedEvent { InventoryId = _read.InventoryId, Version = result.Version });
        }
        private InventoryOperationResult Execute(in InventoryCommandEvent command)
        {
            ItemOwnershipStore store = _profile.Items;
            if (RaidWriteLocked) return store.Result(false, InventoryFailureReason.ResourceBusy);
            // 开窗验证不能替代写入权限：直接EventBus命令也要按真实区域复核。
            // 子物品跟随父链，区外编辑仓库中的背包/法杖同样属于访问仓库。
            bool warehouse = IsWarehouseRoot(command.ItemId)
                || (command.Kind == InventoryCommandKind.Place || command.Kind == InventoryCommandKind.MoveStack)
                    && (command.Destination == InventoryDestination.Warehouse || IsWarehouseRoot(command.OwnerId))
                || command.Kind == InventoryCommandKind.Split && IsWarehouseRoot(command.OwnerId);
            if (warehouse && !CanAccessWarehouse) return store.Result(false, InventoryFailureReason.WarehouseUnavailable);
            switch (command.Kind)
            {
                case InventoryCommandKind.OrganizeBackpack:
                    if(command.OwnerId!=EquippedBackpackId)return store.Result(false,InventoryFailureReason.InvalidOwnership);
                    return BackpackOrganizeOps.TryOrganize(store,command.OwnerId,command.ExpectedVersion);
                case InventoryCommandKind.Place:
                case InventoryCommandKind.MoveStack:
                    ItemLocation destination;
                    if (command.Destination == InventoryDestination.Warehouse) destination = ItemLocation.Warehouse;
                    else if (command.Destination == InventoryDestination.Backpack)
                        destination = new GridPlacement(command.X, command.Y, command.QuarterTurns).InBackpack(command.OwnerId);
                    else if (command.Destination == InventoryDestination.WandSlot)
                        destination = new ItemLocation(ItemLocationKind.WandSlot, command.OwnerId, 0, 0, 0, command.SlotIndex);
                    else if (command.Destination == InventoryDestination.SpellStorage)
                    {
                        if (command.OwnerId != _profile.ActorId) return store.Result(false, InventoryFailureReason.InvalidOwnership);
                        destination = new ItemLocation(ItemLocationKind.SpellStorage, command.OwnerId, 0, 0, 0, command.SlotIndex);
                    }
                    else return store.Result(false, InventoryFailureReason.InvalidState);
                    if (destination.Kind == ItemLocationKind.SpellStorage || destination.Kind == ItemLocationKind.WandSlot)
                        return InventoryMoveOps.TryPlaceSpell(store, command.ItemId, destination, command.ExpectedVersion);
                    if (command.WholeStack || command.Kind == InventoryCommandKind.MoveStack)
                        return ItemStackOps.TryMoveStack(store, command.ItemId, destination, command.ExpectedVersion);
                    return store.TryMove(command.ItemId, destination, command.ExpectedVersion);
                case InventoryCommandKind.Split:
                    return ItemStackOps.TrySplit(store, command.ItemId, command.OwnerId, new GridPlacement(command.X, command.Y, command.QuarterTurns), command.ExpectedVersion);
                case InventoryCommandKind.EquipWand:
                    return LoadoutOps.TryEquipWand(store, command.ItemId, _profile.ActorId, command.SlotIndex, command.ExpectedVersion);
                case InventoryCommandKind.ReplaceBackpack:
                    return LoadoutOps.TryReplaceBackpack(store, command.ItemId, _profile.ActorId, command.ExpectedVersion);
                case InventoryCommandKind.Drop: return Drop(command);
                default: return store.Result(false, InventoryFailureReason.InvalidState);
            }
        }
        private bool IsWarehouseRoot(string id)
        {
            // Store已保证父链无环；额外上界保证损坏输入不会在离散命令中无限循环。
            for (int depth = 0; depth <= _profile.Items.Count && id != null; depth++)
            {
                if (!_profile.Items.TryGetLocation(id, out var location)) return false;
                if (location.Kind == ItemLocationKind.Warehouse) return true;
                if (!location.HasParentInstance) return false;
                id = location.OwnerId;
            }
            return false;
        }
        private InventoryOperationResult Drop(in InventoryCommandEvent command)
        {
            ItemOwnershipStore store = _profile.Items;
            if (!store.TryGetLocation(command.ItemId, out ItemLocation source)) return store.Result(false, InventoryFailureReason.NotFound);
            if (source.Kind == ItemLocationKind.WorldItem || source.Kind == ItemLocationKind.ThrownContainer)
                return store.Result(false, InventoryFailureReason.InvalidOwnership);
            var targets = new List<InventoryEntry>();
            foreach (InventoryEntry entry in store)
                if (entry.Item.InstanceId == command.ItemId || (command.WholeStack && source.Kind == ItemLocationKind.Backpack && entry.Location == source))
                    targets.Add(entry);
            var changes = new InventoryEntry[targets.Count];
            var proxies = new WorldItemPickup[targets.Count];
            for (int i = 0; i < targets.Count; i++)
                if (!WorldItemPickup.CanBind(store, targets[i].Item.InstanceId)) return store.Result(false, InventoryFailureReason.InvalidOwnership);
            try
            {
                // 先创建全部隐藏代理、再原子迁移全部成员；不在创建一半对象时写入资产。
                for (int i = 0; i < targets.Count; i++)
                {
                    string owner = Guid.NewGuid().ToString("N");
                    changes[i] = new InventoryEntry(targets[i].Item, new ItemLocation(ItemLocationKind.WorldItem, owner, 0, 0, 0, -1));
                    proxies[i] = CreateProxy(targets[i].Item.InstanceId, owner, false);
                    if(proxies[i]==null || !TryGround(proxies[i].gameObject))
                    {DestroyProxies(proxies);GameLog.Warn("未找到合法支撑地面；丢弃取消，物品保留在背包。","Items");return store.Result(false,InventoryFailureReason.InvalidState);}
                }
            }
            catch (Exception exception)
            { DestroyProxies(proxies); GameLog.Warn("拾取代理创建失败：" + exception.Message, "Inventory"); return store.Result(false, InventoryFailureReason.InvalidState); }
            // 提交之后不能用创建阶段的 catch 伪装成“失败且未修改”。单线程预检后绑定应成立；
            // 若违反该不变量，资产仍保留在 Store 的地面位置，明确报告代理错误供排查。
            InventoryOperationResult result = store.TryApply(changes, command.ExpectedVersion);
            if (!result.Success) { DestroyProxies(proxies); return result; }
            for (int i = 0; i < proxies.Length; i++)
            {
                if (!proxies[i].TryBind(store, changes[i].Item.InstanceId, changes[i].Location.OwnerId, _read.InventoryId))
                    GameLog.Error("已提交地面资产的拾取代理绑定失败。", "Inventory");
                RememberGround(changes[i].Item.InstanceId,proxies[i].transform);
                proxies[i].gameObject.SetActive(true);
            }
            return result;
        }
        private WorldItemPickup CreateProxy(string itemId, string ownerId, bool bind,ItemDefinition knownDefinition=null,WorldItemSpawnPoint knownSource=null)
        {
            // PendingRaid仍保存据点未携带资产；这份记录用于结算，不能成为远征里的可拾取代理。
            if(bind && _profile.PendingRaid!=null && _profile.PendingRaid.IsHubAsset(itemId))return null;
            _profile.Items.Catalog.TryGet(_profile.Items.TryGet(itemId,out var item)?item.ContentId:null,out var definition);
            definition=knownDefinition??definition;var record=FindWorldRecord(itemId);
            if(bind && record!=null && _profile.PendingRaid==null && (string.IsNullOrEmpty(record.WorldSceneKey)?record.SceneKey:record.WorldSceneKey)!=WorldSceneKey)return null;
            WorldItemSpawnPoint source=null;
            if(record!=null && !string.IsNullOrEmpty(record.SpawnId))_visualSources.TryGetValue((record.SceneKey,record.SpawnId),out source);
            if(source!=null && source.Definition!=definition)source=null;
            source=knownSource??source;
            GameObject proxy = WorldItemVisualFactory.Create(definition,source,record);
            proxy.name = (definition!=null?definition.DisplayName:"物品")+" · "+itemId;
            // 结算时玩家尚在远征，已恢复的据点地面记录仍必须生成在Hub，避免随后被Unload销毁。
            var scene=bind && record!=null && !IsAwayRuntime?gameObject.scene:WorldActor!=null?WorldActor.gameObject.scene:gameObject.scene;
            proxy.transform.SetParent(WorldRoot(scene),false);
            if(record!=null)proxy.transform.rotation=record.Rotation;
            int sequence = _dropSequence++;
            proxy.transform.localPosition = new Vector3(6f + sequence % 4, .3f, -(sequence / 4));
            WorldItemPickup pickup = proxy.GetComponent<WorldItemPickup>();
            _spawnedPickups.Add(pickup);
            if(_practiceWorkspace)_practiceWorldIds.Add(itemId);
            if(WorldActor!=null)
            {
                proxy.transform.position=WorldActor.position+WorldActor.forward*1.2f+Vector3.up*.3f+WorldActor.right*(sequence%3)*.35f;
                pickup.ConfigureReach(WorldActor,3);
            }
            // 据点姿态只在据点恢复；携带物进入远征后丢下，应使用本局位置。
            if(bind && record!=null && _profile.PendingRaid==null)
            {
                proxy.transform.SetPositionAndRotation(record.Position,record.Rotation);
                if(record.PoseKind!=WorldItemPoseKind.Authored && TryGround(proxy))
                {
                    if((record.Position-proxy.transform.position).sqrMagnitude>.00000001f || record.PoseKind!=WorldItemPoseKind.Grounded)_worldPresentationDirty=true;
                    record.Position=proxy.transform.position;record.PoseKind=WorldItemPoseKind.Grounded;
                }
            }
            if (bind) { pickup.TryBind(_profile.Items, itemId, ownerId, _read.InventoryId); proxy.SetActive(true); }
            return pickup;
        }
        private bool TryGround(GameObject proxy)=>_groundPlacement.TryPlace(proxy,proxy.scene,WorldActor,_dropGroundMask,_groundProbeAbove,_groundProbeBelow,_groundMinNormalY,_groundClearance);
        private Transform WorldRoot(UnityEngine.SceneManagement.Scene scene)
        {
            if(_worldRoots.TryGetValue(scene.handle,out var root) && root!=null)return root;
            var go=new GameObject("WorldItems_Runtime");UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go,scene);
            if(!Application.isPlaying)go.transform.SetParent(transform,false);
            go.SetActive(isActiveAndEnabled);
            _worldRoots[scene.handle]=go.transform;return go.transform;
        }
        private void DestroyProxies(WorldItemPickup[] proxies)
        {
            for (int i = 0; i < proxies.Length; i++) if (proxies[i] != null)
            { _spawnedPickups.Remove(proxies[i]);if (Application.isPlaying) Destroy(proxies[i].gameObject); else DestroyImmediate(proxies[i].gameObject); }
        }
        private HubWorldItemRecord FindWorldRecord(string itemId)
        {for(int i=0;i<_profile.HubWorldItems.Count;i++)if(_profile.HubWorldItems[i].InstanceId==itemId)return _profile.HubWorldItems[i];return null;}
        /// <summary>一次性场景投放连同生成账写候选档；写盘失败不发布新资产。</summary>
        public bool TryRegisterWorldItem(ItemDefinition definition,string sceneKey,string spawnId,Vector3 position,Quaternion rotation,int charges=8,bool unlimited=false,WorldItemSpawnPoint source=null)
        {
            if(_profile==null || definition==null || _profile.PendingRaid!=null || RaidWriteLocked || _profile.Items.IsResourceBusy
                || _casts.IsPending || PendingMaterialOutputs>0 || PendingContainerFlights>0 || sceneKey!=WorldSceneKey
                || string.IsNullOrWhiteSpace(spawnId) || !_profile.Items.Catalog.TryGet(definition.ContentId,out var registered) || registered!=definition)return false;
            if(source!=null){source.CaptureAppearance();_visualSources[(sceneKey,spawnId)]=source;}
            foreach(var record in _profile.HubWorldItems)if(record.SceneKey==sceneKey && record.SpawnId==spawnId)return true;
            var candidate=RaidProfileOps.Clone(_profile);
            var state=RuntimeItemState.Create(definition.ContentId,definition.Kind==ItemKind.Spell?charges:0,definition.Kind==ItemKind.Spell && unlimited);
            string owner="scene."+sceneKey+"."+spawnId;
            if(!candidate.Items.TryAdd(state,new ItemLocation(ItemLocationKind.WorldItem,owner,0,0,0,-1)))return false;
            candidate.HubWorldItems.Add(new HubWorldItemRecord{SceneKey=sceneKey,WorldSceneKey=sceneKey,SpawnId=spawnId,InstanceId=state.InstanceId,Position=position,Rotation=rotation,
                PoseKind=WorldItemPoseKind.Authored,HasVisualScaleOverride=source!=null,VisualScaleRatio=source!=null?source.ScaleRatio():Vector3.one});
            if(!HubWorldItemRecord.IsValid(candidate.HubWorldItems) || !_repository.TrySave(candidate).Success)return false;
            AdoptCommitted(candidate,true);return true;
        }
        private void RememberGround(string id,Transform proxy)
        {
            if(IsAwayRuntime || string.IsNullOrEmpty(WorldSceneKey))return;
            var record=FindWorldRecord(id);
            if(record==null){record=new HubWorldItemRecord{InstanceId=id,SceneKey=WorldSceneKey};_profile.HubWorldItems.Add(record);}
            // Origin SceneKey+SpawnId不随丢弃地点修改，否则返回原场景会重新发放。
            record.WorldSceneKey=WorldSceneKey;record.Position=proxy.position;record.Rotation=proxy.rotation;
            record.PoseKind=WorldItemPoseKind.Grounded;
            if(!string.IsNullOrEmpty(record.SpawnId) && _visualSources.TryGetValue((record.SceneKey,record.SpawnId),out var source) && source!=null)
            {record.HasVisualScaleOverride=true;record.VisualScaleRatio=source.ScaleRatio();}
            _worldPresentationDirty=true;
        }
        public bool TryGetSpawnedItem(string sceneKey,string spawnId,out string instanceId,out ItemLocation location)
        {
            instanceId=null;location=default;if(_profile==null || string.IsNullOrWhiteSpace(spawnId))return false;
            if(IsAwayRuntime && _awaySpawnIds.TryGetValue((sceneKey,spawnId),out instanceId))return _profile.Items.TryGetLocation(instanceId,out location);
            foreach(var record in _profile.HubWorldItems)if(record.SceneKey==sceneKey && record.SpawnId==spawnId)
            {instanceId=record.InstanceId;return _profile.Items.TryGetLocation(instanceId,out location);}return false;
        }
        public WorldItemPickup FindWorldProxy(string instanceId)
        {if(string.IsNullOrEmpty(instanceId))return null;foreach(var proxy in _spawnedPickups)if(proxy!=null && proxy.ItemId==instanceId)return proxy;return null;}
        internal void EndWorldResources()
        { _casts?.CancelAll(); _castsV2?.CancelAll(); _casts?.MaterialOutputs.EndAll(); _containerUses?.EndWorldTransits(); }
        internal void AdoptPracticeDeath(SinglePlayerProfile candidate)
        {
            // 提交后仅更新资产与快捷投影；马上卸载地图，不能用通用AdoptCommitted在练习Scene重生据点地面代理。
            EndWorldResources();_profile.Adopt(candidate);_toolbelt.Synchronize();
            _savedVersion=_profile.Items.Version;_savedToolVersion=_profile.ToolConfigurationVersion;
            _savedWalletVersion=_walletVersion;
        }
        internal void AdoptCommitted(SinglePlayerProfile candidate, bool preserveWandResources = false)
        {
            if(candidate.PendingRaid==null && !_practiceWorkspace)_awaySpawnIds.Clear();
            EndWorldResources();
            // 箱庭中的代理归箱庭Scene，不再全部是Session子物体；显式追踪才能在采用新Profile时释放。
            for (int i = 0; i < _spawnedPickups.Count; i++) if(_spawnedPickups[i]!=null)
            { _spawnedPickups[i].gameObject.SetActive(false); Destroy(_spawnedPickups[i].gameObject); }
            _spawnedPickups.Clear();
            _profile.Adopt(candidate); _profile.Items.CommitPolicy = null;
            _worldPresentationDirty=false;
            _walletVersion++; _toolbelt?.Synchronize();
            // 据点交易不开始新生命：保留既有法杖Mana/冷却；Raid终局仍沿用完整重建。
            if (!preserveWandResources) _wands = new WandLoadoutService(_profile, _read.InventoryId);
            else _wands.Synchronize(true);
            _eligibility = new RaidSpellEligibility(_profile);
            if (_profile.PendingRaid != null) _eligibility.Resume(_profile.PendingRaid);
            _casts = new CastResourceService(_profile, _wands, _eligibility, _read.InventoryId);
            _castsV2 = new WandExecutionTransactionService(_profile, _wands, _eligibility, _read.InventoryId, _executionReplays, _casts.MaterialOutputs, _allowWorldExperiments);
            _attachments = new SpellAttachmentService(_profile, _read.InventoryId);
            _materials = new InventoryMaterialTransferService(_profile, _read.InventoryId);
            _containerUses = new ContainerUseService(_profile, _read.InventoryId);
            _savedVersion = _profile.Items.Version;
            _savedToolVersion = _profile.ToolConfigurationVersion;
            _savedWalletVersion = _walletVersion;
            // 场景退出的OnDisable也会结算并采用Profile。父物件正停用时Unity禁止SetParent；
            // 此时只提交资产数据，代理留给下次正常初始化创建，不能在销毁链重建层级。
            if(gameObject.activeInHierarchy)
                foreach (var entry in _profile.Items) if (entry.Location.Kind == ItemLocationKind.WorldItem) CreateProxy(entry.Item.InstanceId, entry.Location.OwnerId, true);
            EventBus<InventoryChangedEvent>.Publish(new InventoryChangedEvent { InventoryId = _read.InventoryId, Version = _read.Version });
        }
        internal bool InitializeTransferred(SinglePlayerProfile profile,ProfileSaveRepository repository,bool scenePreview)
        {IsScenePreview=scenePreview;return Initialize(profile,repository);}
        internal void RejectTransferredProfile(string message)
        {
            // 目标Scene恢复失败不能让Start沿作者种子另建一份新档，隐藏原资产丢失问题。
            _initializationAttempted=true;InitializationMessage=message;
        }
        internal void ResumePracticeQualifications(string raidId,IReadOnlyList<string> qualified)
        {
            if(!_eligibility.TryResumePractice(raidId,qualified))
                GameLog.Warn("有限资格卡无法开始练习；请在背包移除已耗尽的资格法术后重新配置。","Practice");
        }
        internal void CopyPracticeQualifications(List<string> destination)=>_eligibility.CopyPracticeQualifications(destination);
        internal void RestorePracticeHeldSelection(int slot)
        {if(slot<3)_wands.TrySelect(slot);_toolbelt.TrySelect(slot);}

        internal bool ConfigurePractice(SinglePlayerProfile profile,ProfileSaveRepository repository,bool scenePreview,Transform player,
            string sceneKey,Dictionary<(string,string),string> spawns)
        {
            _practiceWorkspace=true;_worldSceneKey=sceneKey;WorldActor=player;_awaySpawnIds=spawns;
            // Hub切换期间的进程内冻结标志不能流入已加载的练习Scene。
            profile.RaidActionsFrozen=false;
            return InitializeTransferred(profile,repository,scenePreview);
        }
        internal void ActivateSceneItems(UnityEngine.SceneManagement.Scene scene)
        {
            var points=new List<WorldItemSpawnPoint>();
            foreach(var root in scene.GetRootGameObjects())points.AddRange(root.GetComponentsInChildren<WorldItemSpawnPoint>(true));
            // 先绑定整图，再查重复ID/激活，不能依赖Start遍历次序。
            foreach(var point in points)if(point.enabled && point.gameObject.activeInHierarchy)point.BindRuntimeSession(this,scene.path);
            foreach(var point in points)if(point.enabled && point.gameObject.activeInHierarchy)point.TryActivate();
        }
        internal bool TryRegisterAwayWorldItem(ItemDefinition definition,string sceneKey,string spawnId,Vector3 position,Quaternion rotation,
            int charges,bool unlimited,WorldItemSpawnPoint source,int initialMaterialQuantity=0,WandStats? rolledWandStats=null)
        {
            if(_profile==null || !IsAwayRuntime || definition==null || string.IsNullOrWhiteSpace(sceneKey) || string.IsNullOrWhiteSpace(spawnId)
                || RaidWriteLocked || _profile.RaidActionsFrozen || _profile.Items.IsResourceBusy || _casts.IsPending
                || PendingMaterialOutputs>0 || PendingContainerFlights>0 || charges<0
                || ! _profile.Items.Catalog.TryGet(definition.ContentId,out var registered) || registered!=definition
                || WorldActor==null || sceneKey!=WorldActor.gameObject.scene.path)return false;
            if(!_practiceWorkspace && RunCoordinator!=null && RunCoordinator.State.Phase!=RaidPhase.Exploring)return false;
            if(_awaySpawnIds.ContainsKey((sceneKey,spawnId)))return true;
            var item=RuntimeItemState.Create(definition.ContentId,definition.Kind==ItemKind.Spell?charges:0,definition.Kind==ItemKind.Spell && unlimited);
            if(rolledWandStats.HasValue)
            {
                if(definition.Kind!=ItemKind.Wand||!rolledWandStats.Value.IsValid)return false;
                item=new RuntimeItemState(item.InstanceId,item.ContentId,0,rolledWandStats:rolledWandStats);
            }
            if(definition.Kind==ItemKind.Material && initialMaterialQuantity>0)
            {
                _profile.Items.Catalog.TryGetRules(definition.ContentId,out var rules);
                item=new RuntimeItemState(item.InstanceId,item.ContentId,0,material:new MaterialItemState(rules.MaterialKey,
                    Math.Min((ulong)initialMaterialQuantity,rules.MaterialItemCapacity)));
            }
            string owner="loot."+(_profile.PendingRaid?.RaidId??"practice")+"."+item.InstanceId;
            var proxy=CreateProxy(item.InstanceId,owner,false,definition,source);proxy.transform.SetPositionAndRotation(position,rotation);
            if(!_profile.Items.TryAdd(item,new ItemLocation(ItemLocationKind.WorldItem,owner,0,0,0,-1)))
            {_spawnedPickups.Remove(proxy);Destroy(proxy.gameObject);return false;}
            if(!proxy.TryBind(_profile.Items,item.InstanceId,owner,_read.InventoryId))
                throw new InvalidOperationException("已提交地图物品的代理绑定失败。");
            _awaySpawnIds.Add((sceneKey,spawnId),item.InstanceId);proxy.gameObject.SetActive(true);
            EventBus<InventoryChangedEvent>.Publish(new InventoryChangedEvent{InventoryId=_read.InventoryId,Version=_read.Version});return true;
        }
        /// <summary>练习转场只清理本Scene留地资产及其子项；不碰据点地面记录或已收进携带背包的实例。</summary>
        internal ProfilePersistenceResult SavePracticeForLeaving()
        {
            if(!_practiceWorkspace || _profile==null)return new ProfilePersistenceResult(ProfilePersistenceStatus.InvalidData);
            return SavePortalCandidate(true);
        }
        internal ProfilePersistenceResult SaveForPortalDeparture()
        {
            if (_profile == null || _profile.PendingRaid != null || RunCoordinator?.State.Phase != RaidPhase.HubReady)
                return new ProfilePersistenceResult(ProfilePersistenceStatus.WriteBlocked, "当前不是据点离图阶段。");
            return SavePortalCandidate(false);
        }
        private ProfilePersistenceResult SavePortalCandidate(bool removePracticeGround)
        {
            var built=BuildPortalCandidate(removePracticeGround,out var next);
            if(!built.Success)return built;
            // 先清理候选并可靠保存，再采用；失败保留当前地面Store/代理，允许原位置继续拾取或重试回程。
            var result=_repository.TrySave(next);
            if(result.Success)
            {
                EndWorldResources();_profile.Adopt(next);_toolbelt.Synchronize();
                _savedVersion=_profile.Items.Version;_savedToolVersion=_profile.ToolConfigurationVersion;
                _savedWalletVersion=_walletVersion;
            }
            return result;
        }
        /// <summary>正式远征把此候选送入原有资格预检/出发事务；不能先保存清场再发现资格不足。</summary>
        internal ProfilePersistenceResult BuildPortalCandidate(bool removePracticeGround,out SinglePlayerProfile next)
        {
            next=null;
            if (PendingDungeonDrops>0)
                return new ProfilePersistenceResult(ProfilePersistenceStatus.WriteBlocked,"战利品正在落地，请稍后重试。");
            if (_profile.Items.IsResourceBusy || _casts.IsPending)
                return new ProfilePersistenceResult(ProfilePersistenceStatus.WriteBlocked, "材料事务尚在执行，请稍后重试。");
            var entries=new List<InventoryEntry>();bool removed=false;
            foreach(var entry in _profile.Items)
            {
                var root=entry.Location;string rootId=entry.Item.InstanceId;
                for(int i=0;root.HasParentInstance && i<_profile.Items.Count;i++)
                {rootId=root.OwnerId;if(!_profile.Items.TryGetLocation(rootId,out root))break;}
                // 已掷出材料属于即将结束的世界；不能存成无代理瓶，更不能返还为满瓶。
                if(root.Kind==ItemLocationKind.ThrownContainer || removePracticeGround && root.Kind==ItemLocationKind.WorldItem && _practiceWorldIds.Contains(rootId))
                {removed=true;continue;}
                entries.Add(entry);
            }
            if(removed || _profile.PendingMaterialOutputs > 0)
            {if(!RaidProfileOps.RestoreEntries(_profile,entries,_profile.ActorId,out next))return new ProfilePersistenceResult(ProfilePersistenceStatus.InvalidData,"练习退出父链校验失败");}
            else next=RaidProfileOps.Clone(_profile);
            return new ProfilePersistenceResult(ProfilePersistenceStatus.Success);
        }
        public bool TrySpawnRaidSpell(string contentId, int charges, bool unlimited, Vector3 position, out string instanceId)
        {
            instanceId = null;
            if (_profile?.PendingRaid == null || RaidWriteLocked || !_profile.Items.Catalog.TryGetRules(contentId, out var rules) || rules.Kind != ItemKind.Spell) return false;
            var item = RuntimeItemState.Create(contentId, charges, unlimited); string owner = "loot." + _profile.PendingRaid.RaidId + "." + item.InstanceId;
            // 预建时Store尚无该实例，必须显式给Definition；不能因查不到未提交资产而生成Fallback。
            _profile.Items.Catalog.TryGet(contentId,out var definition);
            var proxy = CreateProxy(item.InstanceId, owner, false,definition); proxy.transform.position = position + Vector3.up * .3f;
            if (!_profile.Items.TryAdd(item, new ItemLocation(ItemLocationKind.WorldItem, owner, 0, 0, 0, -1))) { Destroy(proxy.gameObject); return false; }
            proxy.TryBind(_profile.Items, item.InstanceId, owner, _read.InventoryId); proxy.gameObject.SetActive(true); instanceId = item.InstanceId;
            EventBus<InventoryChangedEvent>.Publish(new InventoryChangedEvent { InventoryId = _read.InventoryId, Version = _read.Version }); return true;
        }
    }
}
