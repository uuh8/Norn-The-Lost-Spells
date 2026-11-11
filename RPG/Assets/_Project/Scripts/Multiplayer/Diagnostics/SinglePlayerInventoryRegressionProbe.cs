using UnityEngine;

#if (DEVELOPMENT_BUILD || UNITY_EDITOR) && !UNITY_SERVER
using System;
using System.IO;
using System.Reflection;
using Game.Character;
using Game.Core;
using Game.Networking;
using Game.NetworkProtocol;
using Mirror;
#endif

namespace Game.Multiplayer
{
    /// <summary>
    /// 显式 Native 回归诊断：观察真实认证 Client 的 Owner 库存，并通过原通道提交有限意图。
    /// 默认没有对象、订阅或文件 IO；只有 Development/Editor 的专用命令行开关才安装。
    /// 它不登录、不发物品、不改 Store/权限/快照，也不替代真实鼠标 Raycast 或玩家体验验收。
    /// </summary>
    public sealed class SinglePlayerInventoryRegressionProbe : MonoBehaviour
    {
#if (DEVELOPMENT_BUILD || UNITY_EDITOR) && !UNITY_SERVER
        [Serializable] private sealed class Config { public string Output, Command; }
        [Serializable] private sealed class Command
        {
            public string Action, ItemId, TargetKind, TargetId;
            public int TargetSlot;
            public IntentJson Intent;
        }

        // Core 意图不是 Unity 的 [Serializable] 数据类。只在诊断边界做等字段 JSON 映射，
        // 不能依赖 JsonUtility 静默忽略嵌套 struct，否则版本/种类会退成0而制造假失败。
        [Serializable] private sealed class IntentJson
        {
            public ulong CommandId, TransferBudget, AttachmentDose;
            public Vector3 AimOrigin, AimDirection;
            public uint ConnectionEpoch, ExpectedInventoryVersion, SourceProgramRevision, TargetProgramRevision;
            public OnlineInventoryCommandKind Kind;
            public string ItemId, SourceWandId, TargetWandId, WorldOwnerId, TargetBackpackId, AttachmentContainerId;
            public int TargetSlot, GridX, GridY, QuarterTurns, TargetLocationKind;
            public bool ToSpellStorage, AutoPlace, AttachmentEnabled, WholeStack;

            public OnlineInventoryCommandIntent Value() => new OnlineInventoryCommandIntent
            {
                CommandId = CommandId, TransferBudget = TransferBudget, AimOrigin = AimOrigin, AimDirection = AimDirection,
                ConnectionEpoch = ConnectionEpoch, ExpectedInventoryVersion = ExpectedInventoryVersion,
                SourceProgramRevision = SourceProgramRevision, TargetProgramRevision = TargetProgramRevision,
                Kind = Kind, ItemId = ItemId, SourceWandId = SourceWandId, TargetWandId = TargetWandId,
                TargetSlot = TargetSlot, ToSpellStorage = ToSpellStorage, WorldOwnerId = WorldOwnerId,
                TargetBackpackId = TargetBackpackId, GridX = GridX, GridY = GridY, QuarterTurns = QuarterTurns,
                AutoPlace = AutoPlace, AttachmentContainerId = AttachmentContainerId, AttachmentDose = AttachmentDose,
                AttachmentEnabled = AttachmentEnabled, TargetLocationKind = TargetLocationKind, WholeStack = WholeStack
            };

            public static IntentJson Copy(in OnlineInventoryCommandIntent value) => new IntentJson
            {
                CommandId = value.CommandId, TransferBudget = value.TransferBudget, AimOrigin = value.AimOrigin,
                AimDirection = value.AimDirection, ConnectionEpoch = value.ConnectionEpoch,
                ExpectedInventoryVersion = value.ExpectedInventoryVersion, SourceProgramRevision = value.SourceProgramRevision,
                TargetProgramRevision = value.TargetProgramRevision, Kind = value.Kind, ItemId = value.ItemId,
                SourceWandId = value.SourceWandId, TargetWandId = value.TargetWandId, TargetSlot = value.TargetSlot,
                ToSpellStorage = value.ToSpellStorage, WorldOwnerId = value.WorldOwnerId,
                TargetBackpackId = value.TargetBackpackId, GridX = value.GridX, GridY = value.GridY,
                QuarterTurns = value.QuarterTurns, AutoPlace = value.AutoPlace, AttachmentContainerId = value.AttachmentContainerId,
                AttachmentDose = value.AttachmentDose, AttachmentEnabled = value.AttachmentEnabled,
                TargetLocationKind = value.TargetLocationKind, WholeStack = value.WholeStack
            };
        }

        [Serializable] private sealed class ReceiptJson
        {
            public ulong CommandId, Transferred;
            public uint ConnectionEpoch, InventoryVersion;
            public string Failure, TransferFailure, ObservedAtUtc;
            public bool Success;
            public static ReceiptJson Copy(in OnlineInventoryCommandReceipt value) => new ReceiptJson
            {
                CommandId = value.CommandId, ConnectionEpoch = value.ConnectionEpoch, InventoryVersion = value.InventoryVersion,
                Failure = value.Failure.ToString(), TransferFailure = value.TransferFailure.ToString(),
                Transferred = value.Transferred, Success = value.Success, ObservedAtUtc = DateTime.UtcNow.ToString("O")
            };
        }
        [Serializable] private sealed class CastJson
        {
            public ulong CommandId;
            public uint ConnectionEpoch, ProgramRevision, ObservedInventoryVersion;
            public string WandInstanceId, Result, Failure;
            public Vector3 ClaimedOrigin, AimPoint;
        }
        [Serializable] private sealed class ExitJson
        {
            public string CommandId, Phase, Reason, Failure, PayloadHash;
            public uint ConnectionEpoch, CommittedVersion;
        }
        [Serializable] private sealed class UiJson
        {
            public bool Found, IsOpen, IsDragging;
            public int ControllerId, MatchingControllers;
            public string Failure, Scene, SourceType, LastInteractionStatus, SelectedItemId;
            public ulong PendingOnlineCommandId;
            public uint PendingOnlineInventoryVersion;
            public ReceiptJson LastOnlineCommandReceipt;
            public BadgeJson[] CardBadges;
            public RenderedBadgeJson[] RenderedCardBadges;
        }
        [Serializable] private sealed class BadgeJson { public string ItemId, ExpectedCaption; }
        [Serializable] private sealed class RenderedBadgeJson
        {
            public string ItemId, ViewName, ActualCaption;
            public int BoundRemainingCharges;
            public bool BadgeFound, Visible;
        }
        [Serializable] private sealed class HeldHudJson
        {
            public bool Found, CanvasFound, CanvasVisible, RenderedSourceMatches, SelectedFramePresent;
            public int MatchingHuds, HudId, SelectedFrameSpriteId;
            public string Failure, Scene;
            public HeldHudCellJson[] Cells = Array.Empty<HeldHudCellJson>();
        }
        [Serializable] private sealed class HeldHudCellJson
        {
            public int Slot, IconSpriteId, FrameSpriteId;
            public string ItemId, IconSpriteName, FrameSpriteName;
            public bool IconFound, IconEnabled, IconActive, IconSpritePresent;
            public bool SelectionFound, SelectionEnabled, SelectionActive;
            public bool FrameFound, FrameEnabled, FrameActive, FrameSelected;
        }
        [Serializable] private sealed class Report
        {
            public string RecordedAtUtc, Scene, DungeonId, LastAction, LastActionStatus, LastActionAtUtc, TransportType;
            public bool Connected, Authenticated, NetworkServerActive, WorldReady, MovementReady, Authority, CanAdvance;
            public bool Frozen, Suppressed, Focused, CursorVisible, ClientInventoryReady, InventoryCaptured, PlayerCastObserved;
            public string CursorLockMode;
            public float TimeScale;
            public ulong ActorId, WorldEpoch, ClientInventorySequence;
            public uint ConnectionEpoch, ServerTick, ClientInventoryVersion, ProbeCastSequence;
            public int ClientInventoryCount, LastInventoryCallerId, LocalInventoryDriverId;
            public float ClientInventoryMana;
            public OnlineCastInventoryDiagnosticSnapshot Inventory;
            // 这是本进程的 Channel 计数。Client 的发送计数通常为0，绝不能据此声称DS已发送。
            public long LocalSentSnapshots, LocalSentSnapshotPages, LocalRejectedSnapshots, LocalAcceptedCommands, LocalRejectedCommands;
            public InventoryNetworkChannel.OwnerDeliveryDiagnostic[] LocalOwnerDelivery;
            public long LocalRejectedCastFacts, LocalRejectedCastIntents;
            public IntentJson LastInventoryIntent;
            public ReceiptJson LastMatchingInventoryReceipt;
            public CastJson LastProbeCast;
            public ExitJson LastRunExitStatus;
            public UiJson Ui;
            public HeldHudJson HeldHud;
        }

        private Config _config;
        private string _directory;
        private double _next;
        private bool _sampling, _playerCastObserved;
        private uint _castSequence;
        private JoinConnectionIdentity _identity;
        private OnlineCastDriver _boundDriver;
        private CombatNetworkChannel _boundCombat;
        private IntentJson _lastInventoryIntent;
        private int _lastInventoryCaller;
        private ReceiptJson _lastInventoryReceipt;
        private CastJson _lastCast;
        private ExitJson _lastExit;
        private string _lastAction, _lastActionStatus, _lastActionAtUtc;
        private const int MaximumJsonBytes = 65536;
        private const BindingFlags PublicInstance = BindingFlags.Instance | BindingFlags.Public;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            string path = RoomInstanceBootstrap.Argument("--norn-inventory-regression");
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("显式配置必须使用绝对路径。");
                path = Path.GetFullPath(path);
                if (!File.Exists(path) || new FileInfo(path).Length > MaximumJsonBytes)
                    throw new ArgumentException("显式配置不存在或超过64KiB。");
                string directory = Path.GetDirectoryName(path);
                var config = JsonUtility.FromJson<Config>(File.ReadAllText(path));
                if (config == null) throw new ArgumentException("配置为空。");
                config.Output = ConfinedPath(directory, config.Output);
                config.Command = ConfinedPath(directory, config.Command);
                if (SamePath(config.Output, config.Command) || SamePath(config.Output, path) || SamePath(config.Command, path))
                    throw new ArgumentException("配置、命令和报告必须是三个不同文件。");
                var root = new GameObject("ExplicitSinglePlayerInventoryRegression"); DontDestroyOnLoad(root);
                var probe = root.AddComponent<SinglePlayerInventoryRegressionProbe>();
                probe._directory = directory; probe._config = config;
            }
            catch (Exception error)
            { GameLog.Error("库存回归诊断未安装：" + error.GetType().Name + " " + error.Message, "InventoryRegressionProbe"); }
        }

        private void OnEnable()
        {
            EventBus<OnlineInventoryCommandRequestedEvent>.Subscribe(OnRequested);
            EventBus<OnlineInventoryCommandResultEvent>.Subscribe(OnReceipt);
            EventBus<OnlineDiagnosticSamplingEvent>.Subscribe(OnSampling);
            EventBus<OnlineRunExitStatusChangedEvent>.Subscribe(OnExitStatus);
        }
        private void OnDisable()
        {
            EventBus<OnlineInventoryCommandRequestedEvent>.Unsubscribe(OnRequested);
            EventBus<OnlineInventoryCommandResultEvent>.Unsubscribe(OnReceipt);
            EventBus<OnlineDiagnosticSamplingEvent>.Unsubscribe(OnSampling);
            EventBus<OnlineRunExitStatusChangedEvent>.Unsubscribe(OnExitStatus);
            if (_boundDriver != null) _boundDriver.IntentProduced -= OnPlayerCast;
            if (_boundCombat != null) _boundCombat.FactReceived -= OnCastFact;
            _boundDriver = null; _boundCombat = null;
        }
        private void OnSampling(OnlineDiagnosticSamplingEvent value) => _sampling = value.Active;
        private void OnExitStatus(OnlineRunExitStatusChangedEvent value)
        {
            var status = value.Status;
            if ((_identity == null || status.ConnectionEpoch != _identity.ConnectionEpoch)
                && (_lastExit == null || status.ConnectionEpoch != _lastExit.ConnectionEpoch || status.CommandId != _lastExit.CommandId)) return;
            _lastExit = new ExitJson { CommandId = status.CommandId, ConnectionEpoch = status.ConnectionEpoch,
                Phase = status.Phase.ToString(), Reason = status.Reason.ToString(), Failure = status.Failure,
                PayloadHash = status.PayloadHash, CommittedVersion = status.CommittedVersion };
        }

        private void Update()
        {
            // 文件观察、Reflection、值副本和JSON都限定在1Hz冷路径；正式测量窗口完全停用。
            if (_config == null || _sampling || WorldReplicationTimingTrace.Active != null || Time.realtimeSinceStartupAsDouble < _next) return;
            _next = Time.realtimeSinceStartupAsDouble + 1;
            RefreshBindings();
            try
            {
                ConfinedPath(_directory, _config.Command);
                if (File.Exists(_config.Command))
                {
                    _lastAction = "read-command";
                    if (new FileInfo(_config.Command).Length > MaximumJsonBytes) throw new ArgumentException("命令超过64KiB。");
                    string json = File.ReadAllText(_config.Command);
                    // 主Agent用临时文件原子替换投递；读取后消费一次，不在每秒重复扣次/移动同一意图。
                    File.Delete(_config.Command);
                    var command = JsonUtility.FromJson<Command>(json);
                    _lastAction = command != null ? command.Action : string.Empty;
                    _lastActionAtUtc = DateTime.UtcNow.ToString("O");
                    _lastActionStatus = Execute(command);
                }
            }
            catch (Exception error)
            { _lastActionStatus = Failure(error); _lastActionAtUtc = DateTime.UtcNow.ToString("O"); }
            try
            {
                ConfinedPath(_directory, _config.Output);
                Directory.CreateDirectory(Path.GetDirectoryName(_config.Output));
                File.WriteAllText(_config.Output, JsonUtility.ToJson(Capture(), true));
            }
            catch (Exception error)
            { GameLog.Error("库存回归报告写入失败：" + Failure(error), "InventoryRegressionProbe"); }
        }

        private void RefreshBindings()
        {
            var manager = NetworkManager.singleton as RoomNetworkManager;
            var auth = manager != null ? manager.GetComponent<JoinTicketAuthenticator>() : null;
            var identity = auth != null && auth.ClientAuthenticated ? auth.ClientIdentity : null;
            if (!ReferenceEquals(identity, _identity))
            {
                _identity = identity; _castSequence = 0; _playerCastObserved = false;
                _lastInventoryIntent = null; _lastInventoryReceipt = null; _lastInventoryCaller = 0; _lastCast = null;
            }
            var world = OnlineWorldBootstrap.Active;
            var driver = world != null && world.LocalPlayer != null ? world.LocalPlayer.GetComponent<OnlineCastDriver>() : null;
            if (_boundDriver != driver)
            {
                if (_boundDriver != null) _boundDriver.IntentProduced -= OnPlayerCast;
                _boundDriver = driver;
                if (_boundDriver != null) _boundDriver.IntentProduced += OnPlayerCast;
            }
            var combat = manager != null ? manager.GetComponent<CombatNetworkChannel>() : null;
            if (_boundCombat == combat) return;
            if (_boundCombat != null) _boundCombat.FactReceived -= OnCastFact;
            _boundCombat = combat;
            if (_boundCombat != null) _boundCombat.FactReceived += OnCastFact;
        }

        private void OnRequested(OnlineInventoryCommandRequestedEvent value)
        {
            if (_identity == null || value.ActorId != _identity.ActorId || value.Intent.ConnectionEpoch != _identity.ConnectionEpoch) return;
            _lastInventoryCaller = value.CallerId; _lastInventoryIntent = IntentJson.Copy(value.Intent); _lastInventoryReceipt = null;
        }
        private void OnReceipt(OnlineInventoryCommandResultEvent value)
        {
            if (_identity == null || _lastInventoryIntent == null || value.ActorId != _identity.ActorId
                || value.CallerId != _lastInventoryCaller || value.Receipt.CommandId != _lastInventoryIntent.CommandId
                || value.Receipt.ConnectionEpoch != _identity.ConnectionEpoch) return;
            _lastInventoryReceipt = ReceiptJson.Copy(value.Receipt);
        }
        private void OnPlayerCast(OnlineCastIntent value)
        {
            // Probe序号只能用于无人操作的独立诊断进程。观察到原Driver发包后停止Probe施法，
            // 不反射读取/改写Driver序号，也不通过抢占高位ID破坏正常客户端的单调命令空间。
            if (_identity != null && value.ConnectionEpoch == _identity.ConnectionEpoch) _playerCastObserved = true;
        }
        private void OnCastFact(CombatFactMessage value)
        {
            if (_identity == null || _lastCast == null || value.Kind != 2 || value.ActorId != _identity.ActorId
                || value.ConnectionEpoch != _identity.ConnectionEpoch || value.CommandId != _lastCast.CommandId) return;
            // Protobuf消息会池化复用；这里只拷贝匹配的基础值，不持有消息对象。
            _lastCast.Result = ((OnlineCastResult)value.Result).ToString();
            _lastCast.Failure = ((OnlineCastFailure)value.Phase).ToString();
        }

        private string Execute(Command command)
        {
            if (command == null) return "Rejected: empty command";
            if (command.Action == "report") return "ReportRequested";
            if (command.Action != "inventory" && command.Action != "cast" && command.Action != "container-throw" && command.Action != "ui-open" && command.Action != "ui-close"
                && command.Action != "ui-drag" && command.Action != "exit")
                return "Rejected: Action is outside the explicit whitelist";
            if (!TryLiveContext(out var world, out var driver, out var inventory)) return "Rejected: current authenticated Client inventory is not ready";
            if (command.Action == "container-throw")
            {
                // 只复用实际本机角色的共享镜头与正式Driver意图入口。诊断不构造飞行速度/原点，
                // 不移动角色或创建瓶；DS仍验证选择、库存版本、Frozen、物理输出容量并提交原Flight。
                // 这覆盖G松开后的请求入口，不声称覆盖Keyboard G按住/松开或玩家鼠标体验。
                if (world.LocalPlayer == null || world.Frozen || !world.CanAdvance || world.LocalInputSuppressed
                    || !LocalActorCameraPresentation.TryGet(world.LocalPlayer.gameObject, out var camera)
                    || camera == null || !camera.isActiveAndEnabled)
                    return "Rejected: current local actor camera/world is not ready for container throw";
                Ray ray = camera.ViewportPointToRay(new Vector3(.5f, .5f, 0));
                return driver.RequestContainerThrow(ray)
                    ? "OriginalContainerThrowIntentSubmitted: wait for receipt and Owner installation"
                    : "Rejected: formal container Driver refused the current selection/lifecycle";
            }
            if (command.Action == "exit")
            {
                // 与原ESC相同的有限个人退出事件，仍由DS排空/捕获并向业务服提交。没有Client最终资产正文。
                // 正式退出ID的高32位是连接Epoch；DS据此前缀拒绝跨Epoch新命令。
                // 保留96bit随机尾，与OnlineRoomClient.CreateMemberExitCommand的格式一致。
                string id = _identity.ConnectionEpoch.ToString("x8") + Guid.NewGuid().ToString("N").Substring(8);
                _lastExit = new ExitJson { CommandId = id, ConnectionEpoch = _identity.ConnectionEpoch, Phase = "Requested" };
                EventBus<OnlineRunExitRequestedEvent>.Publish(new OnlineRunExitRequestedEvent { CommandId = id, ThroughReturnPortal = false });
                return "OriginalEscExitEventPublished: wait for Committed and the persisted receipt";
            }
            if (command.Action == "inventory")
            {
                if (command.Intent == null) return "Rejected: inventory requires Intent";
                var intent = command.Intent.Value();
                if ((byte)intent.Kind < (byte)OnlineInventoryCommandKind.SelectWand || (byte)intent.Kind > (byte)OnlineInventoryCommandKind.EndWandResearch)
                    return "Rejected: unknown inventory Kind";
                if (intent.Kind == OnlineInventoryCommandKind.SelectTool || intent.Kind == OnlineInventoryCommandKind.SelectWand)
                {
                    // 选择也必须经过数字键的同一个Driver入口，才能覆盖焦点/冻结/当前Source资格。
                    // 直接发布测试意图会绕过用户“不能切下一瓶”的故障点；这里只传槽，真实ID/版本由Driver读取。
                    bool submitted = intent.Kind == OnlineInventoryCommandKind.SelectTool
                        ? driver.RequestToolSelection(intent.TargetSlot) : driver.RequestWandSelection(intent.TargetSlot);
                    if (!submitted) return "Rejected: formal held-selection Driver refused the current selection/lifecycle";
                    if (_lastInventoryCaller != driver.GetInstanceID() || _lastInventoryIntent == null
                        || _lastInventoryIntent.Kind != intent.Kind || _lastInventoryIntent.TargetSlot != intent.TargetSlot)
                        return "Rejected: original Driver selection Caller/intent was not observed";
                    return "OriginalHeldSelectionIntentSubmitted: wait for receipt and Owner installation";
                }
                intent.CommandId = OnlineInventoryCommandIds.Next(); intent.ConnectionEpoch = _identity.ConnectionEpoch;
                if (intent.ExpectedInventoryVersion == 0) intent.ExpectedInventoryVersion = inventory.InventoryVersion;
                if (string.IsNullOrEmpty(intent.ItemId)) intent.ItemId = command.ItemId;
                EventBus<OnlineInventoryCommandRequestedEvent>.Publish(new OnlineInventoryCommandRequestedEvent
                { ActorId = _identity.ActorId, CallerId = GetInstanceID(), Intent = intent });
                return "InventoryIntentSubmitted: receipt and Owner installation are separate evidence";
            }
            if (command.Action == "cast")
            {
                if (_playerCastObserved) return "Rejected: normal Driver input was observed; restart an exclusive diagnostic Client";
                if (world.Frozen || !world.CanAdvance || world.LocalInputSuppressed || inventory.SelectedHeldSlot < 0
                    || inventory.SelectedHeldSlot >= 3 || !inventory.SelectedWandResourcesAvailable
                    || string.IsNullOrEmpty(inventory.SelectedWandInstanceId) || inventory.ProgramRevision == 0)
                    return "Rejected: current world/selection does not allow the diagnostic cast";
                if (_boundCombat == null || _boundCombat.GameplayHash == 0 || _castSequence == uint.MaxValue)
                    return "Rejected: combat channel/sequence unavailable";
                var wizard = world.LocalPlayer as WizardController;
                if (wizard == null) return "Rejected: current actor is not a Wizard";
                var intent = new OnlineCastIntent
                {
                    CommandId = ((ulong)_identity.ConnectionEpoch << 32) | ++_castSequence,
                    ConnectionEpoch = _identity.ConnectionEpoch, WandInstanceId = inventory.SelectedWandInstanceId,
                    ProgramRevision = inventory.ProgramRevision, ClaimedOrigin = wizard.transform.position,
                    AimPoint = wizard.CaptureOnlineCastAim()
                };
                _lastCast = new CastJson { CommandId = intent.CommandId, ConnectionEpoch = intent.ConnectionEpoch,
                    ProgramRevision = intent.ProgramRevision, ObservedInventoryVersion = inventory.InventoryVersion,
                    WandInstanceId = intent.WandInstanceId, ClaimedOrigin = intent.ClaimedOrigin, AimPoint = intent.AimPoint,
                    Result = "ChannelInvoked", Failure = string.Empty };
                _boundCombat.SendIntent(intent);
                return "CombatSendIntentInvoked: wait for the actual DS receipt and installed Source";
            }
            if (!TryFindUi(world, driver, out var controller, out var source, out var lookup)) return "Rejected: " + lookup.Failure;
            if (command.Action == "ui-close")
            {
                Invoke(controller, "Close", null);
                return !Read<bool>(controller, "IsOpen") ? "OriginalUiClosed" : "Rejected: original Controller did not close";
            }
            if (command.Action == "ui-open")
            {
                Invoke(controller, "OpenBackpack", null);
                return Read<bool>(controller, "IsOpen") ? "OriginalUiOpened" : "Rejected: original Controller did not open";
            }
            if (!Read<bool>(controller, "IsOpen")) return "Rejected: open the original UI before ui-drag";
            if (string.IsNullOrWhiteSpace(command.ItemId)) return "Rejected: ui-drag requires a real ItemId";
            PrepareUiTarget(command, out string submitMethod, out var submitArguments);
            // 原split拖动会优先使用同格当前选中的那份；先通过公开选择回调明确本次实例，
            // 再从刷新后的Source取值，避免诊断要求A却实际提交同格B。
            Invoke(controller, "SelectItem", new object[] { command.ItemId });
            object item = FindItem(source, command.ItemId);
            if (item == null) return "Rejected: ItemId is absent from the installed Source";
            Invoke(controller, "BeginDrag", new object[] { item, Vector2.zero, null, true, Vector2Int.zero, -1, null, null, 0f });
            if (!Read<bool>(controller, "IsDragging")) return "Rejected: original BeginDrag refused the current item";
            Invoke(controller, submitMethod, submitArguments);
            return "OriginalUiCallbacksInvoked: inspect status, receipt and Source; pointer Raycast was not exercised";
        }

        private bool TryLiveContext(out OnlineWorldBootstrap world, out OnlineCastDriver driver,
            out OnlineCastInventoryDiagnosticSnapshot inventory)
        {
            world = OnlineWorldBootstrap.Active; driver = _boundDriver; inventory = null;
            var connection = NetworkClient.connection;
            return _identity != null && NetworkClient.isConnected && connection != null && connection.isAuthenticated
                && ReferenceEquals(connection.authenticationData, _identity) && world != null && world.WorldReady
                && !world.IsAuthority && world.LocalPlayer != null && driver != null && driver.ClientInventoryReady
                && driver.TryCaptureClientInventoryDiagnostic(world.ServerTick, out inventory)
                && inventory.ActorId == _identity.ActorId && inventory.ConnectionEpoch == _identity.ConnectionEpoch;
        }

        private Report Capture()
        {
            var world = OnlineWorldBootstrap.Active; var manager = NetworkManager.singleton as RoomNetworkManager;
            var channel = manager != null ? manager.GetComponent<InventoryNetworkChannel>() : null;
            var driver = _boundDriver;
            var report = new Report
            {
                RecordedAtUtc = DateTime.UtcNow.ToString("O"), Connected = NetworkClient.isConnected,
                Authenticated = _identity != null, NetworkServerActive = NetworkServer.active,
                ActorId = _identity != null ? _identity.ActorId : 0, ConnectionEpoch = _identity != null ? _identity.ConnectionEpoch : 0,
                Focused = Application.isFocused, TimeScale = Time.timeScale, CursorVisible = Cursor.visible,
                CursorLockMode = Cursor.lockState.ToString(), PlayerCastObserved = _playerCastObserved, ProbeCastSequence = _castSequence,
                LastAction = _lastAction, LastActionStatus = _lastActionStatus, LastActionAtUtc = _lastActionAtUtc,
                LastInventoryIntent = _lastInventoryIntent, LastInventoryCallerId = _lastInventoryCaller,
                LocalInventoryDriverId = _boundDriver != null ? _boundDriver.GetInstanceID() : 0,
                LastMatchingInventoryReceipt = _lastInventoryReceipt, LastProbeCast = _lastCast, LastRunExitStatus = _lastExit,
                TransportType = Transport.active != null ? Transport.active.GetType().FullName : string.Empty,
                LocalSentSnapshots = channel != null ? channel.SentSnapshots : 0,
                LocalSentSnapshotPages = channel != null ? channel.SentSnapshotPages : 0,
                LocalRejectedSnapshots = channel != null ? channel.RejectedSnapshots : 0,
                LocalAcceptedCommands = channel != null ? channel.AcceptedCommands : 0,
                LocalRejectedCommands = channel != null ? channel.RejectedCommands : 0,
                LocalOwnerDelivery = channel != null ? channel.CaptureOwnerDeliveryDiagnostics() : null,
                LocalRejectedCastFacts = _boundCombat != null ? _boundCombat.RejectedFacts : 0,
                LocalRejectedCastIntents = _boundCombat != null ? _boundCombat.RejectedIntents : 0
            };
            if (world != null)
            {
                report.Scene = world.gameObject.scene.path; report.DungeonId = world.DungeonId; report.WorldEpoch = world.WorldEpoch;
                report.WorldReady = world.WorldReady; report.MovementReady = world.MovementReady; report.Authority = world.IsAuthority;
                report.CanAdvance = world.CanAdvance; report.Frozen = world.Frozen; report.Suppressed = world.LocalInputSuppressed;
                report.ServerTick = world.ServerTick;
            }
            if (driver != null)
            {
                report.ClientInventoryReady = driver.ClientInventoryReady; report.ClientInventoryCount = driver.ClientInventoryCount;
                report.ClientInventoryVersion = driver.ClientInventoryVersion; report.ClientInventorySequence = driver.ClientInventorySequence;
                report.ClientInventoryMana = driver.ClientInventoryMana;
                report.InventoryCaptured = driver.TryCaptureClientInventoryDiagnostic(report.ServerTick, out report.Inventory);
            }
            try
            {
                bool found = TryFindUi(world, driver, out var ui, out var source, out var uiReport); report.Ui = uiReport;
                report.HeldHud = CaptureHeldHud(found ? source : null);
                if (ui != null) CaptureUi(ui, source, report.Inventory, uiReport);
            }
            catch (Exception error)
            {
                report.Ui = new UiJson { Failure = Failure(error) };
                if (report.HeldHud == null) report.HeldHud = new HeldHudJson { Failure = Failure(error) };
            }
            return report;
        }

        /// <summary>
        /// 冷诊断只读正式HUD已经写入的Cell/Image，不调用Bind、Refresh或Update，不能替HUD修复残影。
        /// 选中表现实际由Frame.sprite切到作者_selectedFrame完成；Selection是原背景，单看其enabled不代表选中。
        /// 同时核对当前Source、渲染过的Source和Canvas显隐，防止用旧HUD或隐藏的正确缓存冒充可见结果。
        /// </summary>
        private static HeldHudJson CaptureHeldHud(object source)
        {
            var report = new HeldHudJson();
            if (source == null) { report.Failure = "Current formal local Source is unavailable"; return report; }
            Type hudType = FindType("Game.UI.HeldItemHud");
            if (hudType == null) { report.Failure = "Formal HeldItemHud type is unavailable"; return report; }
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Behaviour hud = null;
            foreach (var candidate in Resources.FindObjectsOfTypeAll(hudType))
            {
                if (!(candidate is Behaviour value) || !value.isActiveAndEnabled || !value.gameObject.scene.IsValid()
                    || !value.gameObject.scene.isLoaded || !ReferenceEquals(Read<object>(value, "PresentationSource"), source)) continue;
                hud = value; report.MatchingHuds++;
            }
            if (report.MatchingHuds != 1)
            { report.Failure = "Expected exactly one enabled formal HeldItemHud; found " + report.MatchingHuds; return report; }
            var cellsField = hudType.GetField("_cells", fields);
            var canvasField = hudType.GetField("_canvas", fields);
            var renderedField = hudType.GetField("_renderedSource", fields);
            var selectedFrameField = hudType.GetField("_selectedFrame", fields);
            if (cellsField == null || canvasField == null || renderedField == null || selectedFrameField == null)
                throw new MissingFieldException("Formal HeldItemHud diagnostic fields are unavailable.");
            var cells = cellsField.GetValue(hud) as Array;
            if (cells == null) { report.Failure = "Formal HeldItemHud has no Cell array"; return report; }
            var canvas = canvasField.GetValue(hud) as Canvas;
            var selectedFrame = selectedFrameField.GetValue(hud) as Sprite;
            report.Found = true; report.HudId = hud.GetInstanceID(); report.Scene = hud.gameObject.scene.path;
            report.CanvasFound = canvas != null; report.CanvasVisible = canvas != null && canvas.isActiveAndEnabled;
            report.RenderedSourceMatches = ReferenceEquals(renderedField.GetValue(hud), source);
            report.SelectedFramePresent = selectedFrame != null;
            report.SelectedFrameSpriteId = selectedFrame != null ? selectedFrame.GetInstanceID() : 0;
            report.Cells = new HeldHudCellJson[cells.Length];
            for (int slot = 0; slot < cells.Length; slot++)
            {
                var row = new HeldHudCellJson { Slot = slot }; report.Cells[slot] = row;
                object cell = cells.GetValue(slot); if (cell == null) continue;
                Type cellType = cell.GetType();
                row.ItemId = cellType.GetField("ItemId", fields)?.GetValue(cell) as string;
                var icon = cellType.GetField("Icon", fields)?.GetValue(cell) as Behaviour;
                var selection = cellType.GetField("Selection", fields)?.GetValue(cell) as Behaviour;
                var frame = cellType.GetField("Frame", fields)?.GetValue(cell) as Behaviour;
                var iconSprite = icon != null ? Read<object>(icon, "sprite") as Sprite : null;
                var frameSprite = frame != null ? Read<object>(frame, "sprite") as Sprite : null;
                row.IconFound = icon != null; row.IconEnabled = icon != null && icon.enabled;
                row.IconActive = icon != null && icon.gameObject.activeInHierarchy; row.IconSpritePresent = iconSprite != null;
                row.IconSpriteId = iconSprite != null ? iconSprite.GetInstanceID() : 0; row.IconSpriteName = iconSprite != null ? iconSprite.name : null;
                row.SelectionFound = selection != null; row.SelectionEnabled = selection != null && selection.enabled;
                row.SelectionActive = selection != null && selection.gameObject.activeInHierarchy;
                row.FrameFound = frame != null; row.FrameEnabled = frame != null && frame.enabled;
                row.FrameActive = frame != null && frame.gameObject.activeInHierarchy;
                row.FrameSpriteId = frameSprite != null ? frameSprite.GetInstanceID() : 0; row.FrameSpriteName = frameSprite != null ? frameSprite.name : null;
                row.FrameSelected = selectedFrame != null && frameSprite == selectedFrame;
            }
            return report;
        }

        private static bool TryFindUi(OnlineWorldBootstrap world, OnlineCastDriver driver,
            out Component controller, out object source, out UiJson report)
        {
            controller = null; source = null; report = new UiJson();
            if (world == null || world.LocalPlayer == null || driver == null || !driver.ClientInventoryReady)
            { report.Failure = "Current local actor inventory is not ready"; return false; }
            Type presentation = FindType("Game.Run.OnlineInventoryPresentation");
            Type uiType = FindType("Game.UI.SinglePlayerInventoryController");
            Type context = FindType("Game.Run.IOnlineInventoryCommandContext");
            if (presentation == null || uiType == null || context == null)
            { report.Failure = "Formal presentation/Controller types are not loaded"; return false; }
            var actor = presentation.GetProperty("Actor", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as GameObject;
            source = presentation.GetProperty("Source", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (actor != world.LocalPlayer.gameObject || source == null || source.GetType().FullName != "Game.Run.OnlineInventoryPresentationSource"
                || !context.IsInstanceOfType(source))
            { report.Failure = "Formal Source is not bound to the actual local actor"; return false; }
            var connection = NetworkClient.connection;
            var identity = connection != null ? connection.authenticationData as JoinConnectionIdentity : null;
            if (identity == null || (ulong)context.GetProperty("ActorId").GetValue(source) != identity.ActorId
                || (uint)context.GetProperty("ConnectionEpoch").GetValue(source) != identity.ConnectionEpoch
                || Read<ulong>(source, "SnapshotSequence") != driver.ClientInventorySequence)
            { report.Failure = "Formal Source actor/epoch/installed sequence does not match the real connection"; return false; }
            foreach (var value in Resources.FindObjectsOfTypeAll(uiType))
            {
                if (!(value is Behaviour candidate) || !candidate.isActiveAndEnabled || !candidate.gameObject.scene.IsValid()
                    || !candidate.gameObject.scene.isLoaded || !ReferenceEquals(Read<object>(candidate, "PresentationSource"), source)) continue;
                report.MatchingControllers++; controller = candidate;
            }
            if (report.MatchingControllers != 1)
            { controller = null; report.Failure = "Expected exactly one enabled formal Controller; found " + report.MatchingControllers; return false; }
            report.Found = true; report.ControllerId = controller.GetInstanceID(); report.Scene = controller.gameObject.scene.path;
            report.SourceType = source.GetType().FullName; return true;
        }

        private static void CaptureUi(Component ui, object source, OnlineCastInventoryDiagnosticSnapshot inventory, UiJson report)
        {
            report.IsOpen = Read<bool>(ui, "IsOpen"); report.IsDragging = Read<bool>(ui, "IsDragging");
            report.LastInteractionStatus = Read<string>(ui, "LastInteractionStatus"); report.SelectedItemId = Read<string>(ui, "SelectedItemId");
            report.PendingOnlineCommandId = Read<ulong>(ui, "PendingOnlineCommandId");
            report.PendingOnlineInventoryVersion = Read<uint>(ui, "PendingOnlineInventoryVersion");
            object receipt = Read<object>(ui, "LastOnlineCommandReceipt");
            if (receipt is OnlineInventoryCommandReceipt value) report.LastOnlineCommandReceipt = ReceiptJson.Copy(value);
            report.RenderedCardBadges = CaptureRenderedBadges(ui);
            if (inventory == null || inventory.Cards == null) return;
            report.CardBadges = new BadgeJson[inventory.Cards.Length];
            for (int i = 0; i < report.CardBadges.Length; i++)
            {
                string id = inventory.Cards[i].InstanceId; object item = FindItem(source, id);
                report.CardBadges[i] = new BadgeJson { ItemId = id,
                    ExpectedCaption = item != null ? Invoke(ui, "SpellCountCaption", new[] { item }) as string : "<missing>" };
            }
        }

        private static RenderedBadgeJson[] CaptureRenderedBadges(Component ui)
        {
            Type viewType = FindType("Game.UI.InventoryItemView");
            if (viewType == null) return Array.Empty<RenderedBadgeJson>();
            const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
            var controllerField = viewType.GetField("_controller", privateInstance);
            var badgeField = viewType.GetField("_spellCount", privateInstance);
            if (controllerField == null || badgeField == null) throw new MissingFieldException("Formal InventoryItemView diagnostic fields are unavailable.");
            var views = Resources.FindObjectsOfTypeAll(viewType);
            var result = new RenderedBadgeJson[views.Length]; int count = 0;
            foreach (var candidate in views)
            {
                if (!(candidate is Behaviour view) || !view.isActiveAndEnabled || !ReferenceEquals(controllerField.GetValue(view), ui)) continue;
                object snapshot = Read<object>(view, "Snapshot");
                var snapshotType = snapshot.GetType();
                if (snapshotType.GetField("Kind", PublicInstance).GetValue(snapshot).ToString() != "Spell") continue;
                var badge = badgeField.GetValue(view) as Behaviour;
                // computed caption不能证明画面已刷新；这里另读原View缓存的Snapshot和真实Text.text。
                // 仅观察已有私有表现字段，不调用Bind/Refresh，不替原UI修复陈旧数据。
                result[count++] = new RenderedBadgeJson
                {
                    ItemId = (string)snapshotType.GetField("InstanceId", PublicInstance).GetValue(snapshot),
                    BoundRemainingCharges = (int)snapshotType.GetField("RemainingCharges", PublicInstance).GetValue(snapshot),
                    ViewName = view.name, BadgeFound = badge != null, Visible = badge != null && badge.isActiveAndEnabled,
                    ActualCaption = badge != null ? Read<string>(badge, "text") : null
                };
            }
            Array.Resize(ref result, count); return result;
        }

        private static void PrepareUiTarget(Command command, out string method, out object[] arguments)
        {
            if (command.TargetKind == "Shortcut")
            { method = "SubmitToShortcut"; arguments = new object[] { command.TargetSlot }; return; }
            if (command.TargetKind == "Backpack")
            {
                // 包的生产落点是二维格，不把TargetSlot擅自解释成XY。命令沿用Intent中显式GridX/Y。
                if (command.Intent == null || string.IsNullOrEmpty(command.TargetId))
                    throw new ArgumentException("Backpack requires TargetId and Intent.GridX/GridY.");
                method = "SubmitToBackpack";
                arguments = new object[] { command.Intent.GridX, command.Intent.GridY, command.TargetId }; return;
            }
            switch (command.TargetKind)
            {
                case "Warehouse": case "Ground": case "EquippedWand": case "EquippedBackpack": case "ProgramSlot": case "SpellStorage": break;
                default: throw new ArgumentException("TargetKind is outside the explicit original-UI target whitelist.");
            }
            Type kind = FindType("Game.UI.InventoryDropKind");
            if (kind == null) throw new MissingMemberException("Formal InventoryDropKind is unavailable.");
            method = "SubmitToTarget";
            arguments = new[] { Enum.Parse(kind, command.TargetKind, false), command.TargetId, (object)command.TargetSlot };
        }

        private static object FindItem(object source, string id)
        {
            object read = Read<object>(source, "ReadOnly");
            if (read == null) return null;
            var args = new object[] { id, null };
            return Invoke(read, "TryGetView", args) is bool found && found ? args[1] : null;
        }
        private static Type FindType(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            { Type value = assembly.GetType(name, false); if (value != null) return value; }
            return null;
        }
        private static T Read<T>(object target, string name)
        {
            if (target == null) return default;
            var property = target.GetType().GetProperty(name, PublicInstance);
            if (property == null) throw new MissingMemberException(target.GetType().FullName, name);
            object value = property.GetValue(target); return value == null ? default : (T)value;
        }
        private static object Invoke(object target, string name, object[] args)
        {
            var method = target.GetType().GetMethod(name, PublicInstance);
            if (method == null) throw new MissingMethodException(target.GetType().FullName, name);
            return method.Invoke(target, args);
        }
        private static string Failure(Exception error)
        {
            if (error is TargetInvocationException invocation && invocation.InnerException != null) error = invocation.InnerException;
            return "Rejected: " + error.GetType().Name + " " + error.Message;
        }
        private static bool SamePath(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
        private static string ConfinedPath(string directory, string path)
        {
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(path)) throw new ArgumentException("配置中的报告/命令路径不能为空。");
            string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string full = Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(root, path));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("报告/命令路径必须位于本次显式配置的父目录内。");
            // GetFullPath只消除..，不会阻止junction/symlink穿出目录。冷路径同时拒绝现存重解析点。
            for (string cursor = full; !SamePath(cursor, root); cursor = Path.GetDirectoryName(cursor))
                if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("报告/命令路径不能经过重解析点。");
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("显式配置父目录不能是重解析点。");
            return full;
        }
#endif
    }
}
