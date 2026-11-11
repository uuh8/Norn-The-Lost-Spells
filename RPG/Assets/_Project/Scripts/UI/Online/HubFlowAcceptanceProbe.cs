using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Game.Core;
using Game.Run;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>
    /// 显式 Development Hub 验收的只读补充。原 Portal/UI Probe 继续负责原导航与输入报告；
    /// 本组件只补原网格、真实账号版本/pending、商店行和纯拖拽几何，绝不代按按钮、改坐标或写资产。
    /// OS 编排器必须先证明鼠标命中，再输入，并等待同实例的新完整 Profile；HTTP 200本身不是通过条件。
    /// </summary>
    public sealed class HubFlowAcceptanceProbe : MonoBehaviour
    {
        private const string HubPath = "Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity";
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        [Serializable] private sealed class Config { public string HubOutput, HubCommand; public bool ObserveMovementOnly; public int MovementSamples; }
        [Serializable] private sealed class Request { public string Id, Stage; }
        [Serializable] private sealed class Box { public float x, y, width, height; public Box(Rect r) { x = r.x; y = r.y; width = r.width; height = r.height; } }
        [Serializable] private sealed class Node
        {
            public string Kind, Name, Path, Id, ContentId, LocationKind, OwnerId, Workspace;
            public bool Interactable, RaycastAccepted;
            public int Slot = -1;
            public Vector2 Point;
            public Box Rect;
        }
        [Serializable] private sealed class Navigation { public string Kind, ScenePath, Path; public int Id; public Vector3 Position; }
        [Serializable] private sealed class Item
        {
            public string Id, ContentId, Kind, LocationKind, OwnerId;
            public int Slot, X, Y, Turns;
        }
        [Serializable] private sealed class Workspace
        {
            public string Path, Status;
            public bool Open, Warehouse, Dragging, AccountPending, AccountUnknown;
            public uint Version, CommittedVersion;
        }
        [Serializable] private sealed class Offer { public string Id, ContentId, Kind; public long Price; public ulong Stock; }
        [Serializable] private sealed class Shop
        {
            public bool Open, ReadReady, CanTrade; public int OfferCount, VisibleRows; public long Version;
            public string Message; public Offer[] Offers = Array.Empty<Offer>();
        }
        [Serializable] private sealed class Grid
        {
            public string Kind, Path, OwnerId; public float CellSize; public int Columns, Rows;
            public Box Rect; public Vector2 Origin, CellRight, CellDown;
            public bool DragPlacementKnown, DragFits; public int DragX, DragY; public float Coverage;
        }
        [Serializable] private sealed class Landing
        {
            public string ItemId, Kind, GridPath, OwnerId; public int X, Y; public Vector2 Point; public bool RaycastAccepted;
        }
        [Serializable] private sealed class WalletText { public string Text, Path; public bool Visible; }
        [Serializable] private sealed class Report
        {
            public string Id, Stage, RecordedAtUtc, Scene, DiagnosticError, CursorLock, PlayerId, Status, Station, NearestPortalPath;
            public int Frame, Width, Height, NearestPortalId;
            public bool Focused, CursorVisible, Authenticated, Busy, HubActive, HubReady, AssetsLocked, OnlineConnected, OnlineInventoryReady;
            public uint HubVersion, ProfileVersion; public long Gold;
            public float TimeScale; public Vector2 MousePosition; public Vector3 ActorPosition, CameraForward, CameraRight;
            public string RoomPhase, RoomStatus, ExitPhase, ExitFailure; public uint ExitCommittedVersion;
            public string LastAccountState, LastAccountFailure; public uint LastAccountCommittedVersion;
            public Shop Shop = new Shop();
            public Navigation[] Navigation = Array.Empty<Navigation>(); public Item[] Items = Array.Empty<Item>();
            public Workspace[] Workspaces = Array.Empty<Workspace>(); public Node[] Nodes = Array.Empty<Node>();
            public Grid[] Grids = Array.Empty<Grid>(); public Landing[] Landings = Array.Empty<Landing>();
            public WalletText[] Wallets = Array.Empty<WalletText>();
            public string[] RaycastTargets = Array.Empty<string>(); public bool Dragging; public Vector2 GhostTopLeft;
            public int DragTurns; public string DragItem;
        }
        private Config _config;
        private bool _sampling;
        private double _next;
        private OnlineLobbyChangedEvent _lobby;
        private OnlineRoomChangedEvent _room;
        private OnlineRunExitStatus _exit;
        private OnlineAccountInventoryCommandResultEvent _account;
        private readonly List<RaycastResult> _hits = new List<RaycastResult>();
        private OnlineInputAcceptanceProbe _originalUi;
        private MethodInfo _visibleRect;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
#if (DEVELOPMENT_BUILD || UNITY_EDITOR) && !UNITY_SERVER
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] != "--norn-hub-flow-acceptance" || !File.Exists(args[i + 1])) continue;
                string directory = Path.GetDirectoryName(Path.GetFullPath(args[i + 1]));
                var config = JsonUtility.FromJson<Config>(File.ReadAllText(args[i + 1]));
                // 与原 --norn-portal-acceptance 共用配置，但必须显式选择零采样只读模式；不触发旧内存档/定位分支。
                if (config == null || !config.ObserveMovementOnly || config.MovementSamples != 0
                    || !Confined(directory, config.HubOutput) || !Confined(directory, config.HubCommand)) return;
                var root = new GameObject("ExplicitHubFlowReadOnly"); DontDestroyOnLoad(root);
                root.AddComponent<HubFlowAcceptanceProbe>()._config = config; return;
            }
#endif
        }
        private static bool Confined(string directory, string path) => !string.IsNullOrWhiteSpace(path)
            && Path.GetFullPath(path).StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        private void OnEnable()
        {
            EventBus<OnlineLobbyChangedEvent>.Subscribe(OnLobby); EventBus<OnlineRoomChangedEvent>.Subscribe(OnRoom);
            EventBus<OnlineRunExitStatusChangedEvent>.Subscribe(OnExit); EventBus<OnlineAccountInventoryCommandResultEvent>.Subscribe(OnAccount);
            EventBus<OnlineDiagnosticSamplingEvent>.Subscribe(OnSampling);
        }
        private void OnDisable()
        {
            EventBus<OnlineLobbyChangedEvent>.Unsubscribe(OnLobby); EventBus<OnlineRoomChangedEvent>.Unsubscribe(OnRoom);
            EventBus<OnlineRunExitStatusChangedEvent>.Unsubscribe(OnExit); EventBus<OnlineAccountInventoryCommandResultEvent>.Unsubscribe(OnAccount);
            EventBus<OnlineDiagnosticSamplingEvent>.Unsubscribe(OnSampling);
        }
        private void OnLobby(OnlineLobbyChangedEvent value) => _lobby = value;
        private void OnRoom(OnlineRoomChangedEvent value) => _room = value;
        private void OnExit(OnlineRunExitStatusChangedEvent value) => _exit = value.Status;
        private void OnAccount(OnlineAccountInventoryCommandResultEvent value) => _account = value;
        private void OnSampling(OnlineDiagnosticSamplingEvent value) => _sampling = value.Active;
        private void Update()
        {
            if (_config == null || _sampling || Time.realtimeSinceStartupAsDouble < _next) return;
            _next = Time.realtimeSinceStartupAsDouble + .1;
            if (!File.Exists(_config.HubCommand)) return;
            var report = new Report();
            try
            {
                if (new FileInfo(_config.HubCommand).Length > 1024) throw new ArgumentException("RequestTooLarge");
                var request = JsonUtility.FromJson<Request>(File.ReadAllText(_config.HubCommand)); File.Delete(_config.HubCommand);
                report.Id = request?.Id; report.Stage = request?.Stage;
                Capture(report);
            }
            catch (Exception error) { report.DiagnosticError = error.GetType().Name; }
            // 报告原子替换，编排器只接受匹配Id的新报告，不能用上一阶段的几何或版本代替本次结果。
            string temporary = _config.HubOutput + ".tmp";
            File.WriteAllText(temporary, JsonUtility.ToJson(report, true));
            if (File.Exists(_config.HubOutput)) File.Replace(temporary, _config.HubOutput, null);
            else File.Move(temporary, _config.HubOutput);
        }
        private void Capture(Report report)
        {
            report.RecordedAtUtc = DateTime.UtcNow.ToString("O"); report.Frame = Time.frameCount;
            report.Scene = SceneManager.GetActiveScene().path; report.Width = Screen.width; report.Height = Screen.height;
            report.Focused = Application.isFocused; report.CursorVisible = Cursor.visible; report.CursorLock = Cursor.lockState.ToString(); report.TimeScale = Time.timeScale;
            report.MousePosition = Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
            report.Authenticated = _lobby.Authenticated; report.Busy = _lobby.Busy; report.Status = _lobby.Status;
            report.ProfileVersion = _lobby.Version; report.PlayerId = _lobby.PlayerId;
            report.RoomPhase = _room.Phase.ToString(); report.RoomStatus = _room.Status;
            report.OnlineConnected = _room.Phase == OnlineRoomPhase.Connected && _room.ActorId != 0;
            report.OnlineInventoryReady = OnlineInventoryPresentation.Source?.ReadOnly != null;
            report.ExitPhase = _exit.Phase.ToString(); report.ExitFailure = _exit.Failure; report.ExitCommittedVersion = _exit.CommittedVersion;
            report.LastAccountState = _account.State.ToString(); report.LastAccountFailure = _account.Failure.ToString(); report.LastAccountCommittedVersion = _account.CommittedVersion;
            var camera = Camera.main; if (camera != null) { report.CameraForward = camera.transform.forward; report.CameraRight = camera.transform.right; }
            _originalUi = FindFirstObjectByType<OnlineInputAcceptanceProbe>();
            _visibleRect = typeof(OnlineInputAcceptanceProbe).GetMethod("TryVisibleRect", Private);
            if (_originalUi == null || _visibleRect == null) throw new InvalidOperationException("OriginalUiProbeUnavailable");
            var nodes = new List<Node>(); var navigation = new List<Navigation>(); var workspaces = new List<Workspace>(); var grids = new List<Grid>(); var landings = new List<Landing>();
            var hub = SceneManager.GetSceneByPath(HubPath); SinglePlayerInventorySession session = null;
            if (hub.IsValid() && hub.isLoaded)
                foreach (var root in hub.GetRootGameObjects())
                {
                    report.HubActive |= root.activeInHierarchy;
                    if (session == null) session = root.GetComponentInChildren<SinglePlayerInventorySession>(true);
                }
            if (session != null && session.ActorTransform != null)
            {
                report.ActorPosition = session.ActorTransform.position;
                // 原HubInteraction是Run内部接线；诊断只读原属性，不为验收扩大跨Assembly写/读入口。
                var access = typeof(SinglePlayerInventorySession).GetProperty("HubInteraction", Private)?.GetValue(session) as HubInteractionController;
                if (access != null)
                {
                    if (access.TryGetStation(out var station)) report.Station = station.Kind.ToString();
                    foreach (string kind in new[] { "Shop", "Warehouse" })
                    {
                        var area = Field<HubAreaVolume>(access, kind == "Shop" ? "_shopArea" : "_warehouseArea");
                        if (area != null && area.isActiveAndEnabled) navigation.Add(new Navigation { Kind = kind, Position = area.Center, Path = PathOf(area.transform) });
                    }
                }
                var actor = session.ActorTransform.GetComponent<CharacterController>();
                if (PortalInteractionRegistry.TryGetNearest(actor, out var nearest)) { report.NearestPortalId = nearest.PortalTransform.gameObject.GetInstanceID(); report.NearestPortalPath = PathOf(nearest.PortalTransform); }
                foreach (var root in hub.GetRootGameObjects()) foreach (var portal in root.GetComponentsInChildren<HubPracticePortal>(true))
                    if (portal.isActiveAndEnabled) navigation.Add(new Navigation { Kind = "Portal", ScenePath = portal.ScenePath, Id = portal.PortalId, Path = PathOf(portal.transform), Position = portal.transform.position });
            }
            if (hub.IsValid() && OnlineAccountInventoryHubScope.TryCopyForScene(hub.handle, out var scope))
            {
                report.HubVersion = scope.Version; report.Gold = scope.Gold; report.AssetsLocked = scope.AssetsLocked;
                report.HubReady = report.HubActive && session != null && session.TryGetAccountHubSummary(out _);
            }
            IInventoryReadOnly hubRead = null;
            foreach (var controller in FindObjectsByType<SinglePlayerInventoryController>(FindObjectsSortMode.None))
            {
                var read = controller.PresentationSource?.ReadOnly;
                if (read != null && controller.PresentationSource is IAccountInventoryCommandContext) hubRead = read;
                workspaces.Add(new Workspace { Path = PathOf(controller.transform), Open = controller.IsOpen, Warehouse = controller.WarehouseMode, Dragging = controller.IsDragging,
                    Version = read?.Version ?? 0, Status = controller.LastInteractionStatus, AccountPending = Field<bool>(controller, "_accountCommandPending"),
                    AccountUnknown = Field<bool>(controller, "_accountCommandUnknown"), CommittedVersion = Field<uint>(controller, "_accountCommittedVersion") });
                if (!controller.IsOpen) continue;
                var ghost = Field<InventoryItemView>(controller, "_ghost");
                if (controller.IsDragging && ghost != null)
                {
                    report.Dragging = true; report.DragItem = Field<InventoryItemSnapshot>(controller, "_dragSource").InstanceId; report.DragTurns = Field<int>(controller, "_turns");
                    report.GhostTopLeft = Project(ghost.Rect, new Vector2(ghost.Rect.rect.xMin, ghost.Rect.rect.yMax));
                }
                CaptureGrid(controller, Field<InventoryGridView>(controller, "_gridView"), Field<NearbyItemGridView>(controller, "_nearbyGrid"), ghost, report, nodes, grids, landings);
            }
            if (hubRead != null)
            {
                var buffer = new InventoryItemSnapshot[hubRead.Count]; int count = hubRead.CopyItemsTo(buffer); var items = new List<Item>();
                for (int i = 0; i < count; i++) { var item = buffer[i]; items.Add(new Item { Id = item.InstanceId, ContentId = item.ContentId, Kind = item.Kind.ToString(),
                    LocationKind = item.Location.Kind.ToString(), OwnerId = item.Location.OwnerId, Slot = item.Location.SlotIndex, X = item.Location.X, Y = item.Location.Y, Turns = item.Location.QuarterTurns }); }
                report.Items = items.ToArray();
            }
            CaptureControls(report, nodes);
            report.Navigation = navigation.ToArray(); report.Workspaces = workspaces.ToArray(); report.Nodes = nodes.ToArray(); report.Grids = grids.ToArray(); report.Landings = landings.ToArray();
            Raycast(report.MousePosition); var targets = new string[_hits.Count]; for (int i = 0; i < targets.Length; i++) targets[i] = PathOf(_hits[i].gameObject.transform); report.RaycastTargets = targets;
        }
        private void CaptureControls(Report report, List<Node> nodes)
        {
            // Scope余额正确不代表原钱包文字正确；只读最多8个active WalletView的实际Text与可见性。
            var wallets = new List<WalletText>(8);
            foreach (var wallet in FindObjectsByType<WalletView>(FindObjectsSortMode.None))
            {
                if (!wallet.isActiveAndEnabled) continue;
                if (wallets.Count == 8) break;
                var amount = Field<Text>(wallet, "_amount");
                wallets.Add(new WalletText { Text = amount != null ? amount.text : string.Empty,
                    Path = PathOf(amount != null ? amount.transform : wallet.transform), Visible = amount != null && TryRect(amount, out _) });
            }
            report.Wallets = wallets.ToArray();
            foreach (var menu in FindObjectsByType<AccountMenuController>(FindObjectsSortMode.None))
                foreach (var pair in new[] { ("Username", "_username"), ("Password", "_password"), ("Register", "_register"), ("Login", "_login"), ("EnterHub", "_enter") }) AddControl(nodes, menu, pair.Item1, pair.Item2);
            foreach (var dialog in FindObjectsByType<DungeonEntryDialogController>(FindObjectsSortMode.None))
            { AddControl(nodes, dialog, "SinglePlayer", "_single"); AddControl(nodes, dialog, "RoomCancelOrExit", "_cancel"); }
            foreach (var purchase in FindObjectsByType<ShopPurchaseDialog>(FindObjectsSortMode.None)) AddControl(nodes, purchase, "ConfirmPurchase", "_confirm");
            foreach (var view in FindObjectsByType<InventoryItemView>(FindObjectsSortMode.None))
            {
                if (string.IsNullOrEmpty(view.Snapshot.InstanceId)) continue;
                var controller = Field<SinglePlayerInventoryController>(view, "_controller");
                if (controller == null || !controller.IsOpen || Field<InventoryItemView>(controller, "_ghost") == view) continue;
                var node = AddNode(nodes, view, "Item", true, typeof(IBeginDragHandler)); if (node == null) continue;
                node.Id = view.Snapshot.InstanceId; node.ContentId = view.Snapshot.ContentId; node.LocationKind = view.Snapshot.Location.Kind.ToString();
                node.OwnerId = view.Snapshot.Location.OwnerId; node.Slot = view.Snapshot.Location.SlotIndex; node.Workspace = PathOf(controller.transform);
            }
            foreach (var warehouse in FindObjectsByType<WarehouseCatalogGridView>(FindObjectsSortMode.None)) AddNode(nodes, warehouse, "WarehouseDrop", true, typeof(IDropHandler));
            foreach (var controller in FindObjectsByType<ShopController>(FindObjectsSortMode.None))
            {
                report.Shop.Open |= controller.IsOpen; report.Shop.Message = controller.Status;
                AddControl(nodes, controller, "Buy", "_buyButton"); AddControl(nodes, controller, "CloseShop", "_closeButton");
                var session = Field<SinglePlayerShopSession>(controller, "_shop"); var read = session != null ? session.ReadOnly : null;
                report.Shop.CanTrade = session != null && session.CanTrade; report.Shop.ReadReady = read != null; report.Shop.OfferCount = read?.Count ?? 0; report.Shop.Version = read?.Version ?? 0;
                var offers = new List<Offer>();
                for (int i = 0; read != null && i < read.Count; i++) { var offer = read.GetOffer(i); offers.Add(new Offer { Id = offer.OfferId, ContentId = offer.ContentId, Kind = offer.Kind.ToString(), Price = offer.BuyPrice, Stock = offer.Stock }); }
                report.Shop.Offers = offers.ToArray();
                var root = Field<RectTransform>(controller, "_offersRoot");
                if (root != null) foreach (var row in root.GetComponentsInChildren<ShopEntryView>(false))
                {
                    var node = AddNode(nodes, row, "Offer", true, typeof(IPointerClickHandler));
                    if (node != null) { node.Id = row.OfferId; report.Shop.VisibleRows++; }
                }
            }
        }
        private void CaptureGrid(SinglePlayerInventoryController controller, InventoryGridView bag, NearbyItemGridView nearby, InventoryItemView ghost, Report report, List<Node> nodes, List<Grid> grids, List<Landing> landings)
        {
            var read = controller.PresentationSource?.ReadOnly;
            if (bag != null) CaptureOneGrid(controller, bag, "Backpack", bag.BackpackId, bag.CellSize, read, ghost, report, grids, landings);
            if (nearby != null) CaptureOneGrid(controller, nearby, "Nearby", null, nearby.CellSize, read, ghost, report, grids, landings);
        }
        private void CaptureOneGrid(SinglePlayerInventoryController controller, Behaviour behaviour, string kind, string owner, float cellSize, IInventoryReadOnly read, InventoryItemView ghost, Report report, List<Grid> grids, List<Landing> landings)
        {
            if (!TryRect(behaviour, out var visible) || cellSize <= 0) return;
            var rect = (RectTransform)behaviour.transform;
            var grid = new Grid { Kind = kind, Path = PathOf(rect), OwnerId = owner, CellSize = cellSize, Rect = new Box(visible),
                Columns = Mathf.RoundToInt(rect.rect.width / cellSize), Rows = Mathf.RoundToInt(rect.rect.height / cellSize),
                Origin = Project(rect, new Vector2(rect.rect.xMin, rect.rect.yMax)) };
            grid.CellRight = Project(rect, new Vector2(rect.rect.xMin + cellSize, rect.rect.yMax)) - grid.Origin;
            grid.CellDown = Project(rect, new Vector2(rect.rect.xMin, rect.rect.yMax - cellSize)) - grid.Origin;
            grids.Add(grid);
            if (!controller.IsDragging || ghost == null || read == null) return;
            var source = Field<InventoryItemSnapshot>(controller, "_dragSource"); int turns = Field<int>(controller, "_turns");
            bool Fits(int x, int y)
            {
                var placement = new GridPlacement(x, y, turns);
                return behaviour is NearbyItemGridView ground ? ground.CanPreview(source.InstanceId, source.Rules.Shape, placement)
                    : read is IInventoryPlacementPreview preview && preview.PreviewPlacement(source.InstanceId, owner, placement, Field<bool>(controller, "_wholeStack"), Field<uint>(controller, "_dragVersion")) == InventoryFailureReason.None;
            }
            grid.DragPlacementKnown = InventoryDragGeometry.TryPlacement(ghost.Rect, rect, cellSize, out var anchor, out var coverage);
            grid.DragX = anchor.x; grid.DragY = anchor.y; grid.Coverage = coverage; grid.DragFits = grid.DragPlacementKnown && Fits(anchor.x, anchor.y);
            Vector2 offset = report.MousePosition - Project(ghost.Rect, new Vector2(ghost.Rect.rect.xMin, ghost.Rect.rect.yMax));
            // 只求原纯Preview许可的候选。指针加上当前Ghost真实抓取偏移；跨不同缩放网格后编排器需重新采样校正。
            for (int y = 0; y < grid.Rows && landings.Count < 128; y++) for (int x = 0; x < grid.Columns && landings.Count < 128; x++)
            {
                if (!Fits(x, y)) continue;
                Vector2 point = grid.Origin + grid.CellRight * x + grid.CellDown * y + offset;
                if (!visible.Contains(point)) continue;
                landings.Add(new Landing { ItemId = source.InstanceId, Kind = kind, GridPath = grid.Path, OwnerId = owner, X = x, Y = y, Point = point, RaycastAccepted = HasDropHandler(point, behaviour) });
            }
        }
        private void AddControl(List<Node> nodes, object owner, string kind, string field)
        {
            var component = Field<Component>(owner, field); if (component == null) return;
            AddNode(nodes, component, kind, !(component is Selectable select) || select.IsInteractable(), typeof(IPointerClickHandler));
        }
        private Node AddNode(List<Node> nodes, Component component, string kind, bool interactable, Type handler)
        {
            if (nodes.Count >= 192 || !TryRect(component, out var rect)) return null;
            var node = new Node { Kind = kind, Name = component.name, Path = PathOf(component.transform), Rect = new Box(rect), Point = rect.center, Interactable = interactable };
            // AABB中心可能在L形空洞；逐个只读Raycast候选，先确定原EventSystem最终会交给这个控件。
            for (int y = 0; y < 7 && !node.RaycastAccepted; y++) for (int x = 0; x < 7 && !node.RaycastAccepted; x++)
            {
                Vector2 point = new Vector2(rect.xMin + rect.width * (x + .5f) / 7, rect.yMin + rect.height * (y + .5f) / 7);
                Raycast(point); if (_hits.Count == 0) continue;
                var hit = _hits[0].gameObject; GameObject receiver = handler == typeof(IBeginDragHandler) ? ExecuteEvents.GetEventHandler<IBeginDragHandler>(hit)
                    : handler == typeof(IDropHandler) ? ExecuteEvents.GetEventHandler<IDropHandler>(hit) : ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit);
                if (receiver != null && (receiver.transform == component.transform || receiver.transform.IsChildOf(component.transform))) { node.Point = point; node.RaycastAccepted = true; }
            }
            nodes.Add(node); return node;
        }
        private bool HasDropHandler(Vector2 point, Behaviour grid)
        {
            Raycast(point); if (_hits.Count == 0) return false;
            var receiver = ExecuteEvents.GetEventHandler<IDropHandler>(_hits[0].gameObject);
            return receiver != null && (receiver.transform == grid.transform || grid.transform.IsChildOf(receiver.transform));
        }
        private bool TryRect(Component component, out Rect rect)
        {
            rect = default;
            if (component == null || !component.gameObject.activeInHierarchy || component is Behaviour behaviour && !behaviour.isActiveAndEnabled) return false;
            object[] args = { component.transform as RectTransform, null };
            bool result = (bool)_visibleRect.Invoke(_originalUi, args); if (result) rect = (Rect)args[1]; return result;
        }
        private void Raycast(Vector2 point) { _hits.Clear(); if (EventSystem.current != null) EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, _hits); }
        private static Vector2 Project(RectTransform rect, Vector2 point)
        { var canvas = rect.GetComponentInParent<Canvas>(); var camera = canvas != null && canvas.rootCanvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.rootCanvas.worldCamera : null; return RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(point)); }
        private static T Field<T>(object owner, string name)
        { if (owner == null) return default; var field = owner.GetType().GetField(name, Private); return field != null ? (T)field.GetValue(owner) : default; }
        private static string PathOf(Transform node) { string path = node.name; while (node.parent != null) { node = node.parent; path = node.name + "/" + path; } return path; }
    }
}
