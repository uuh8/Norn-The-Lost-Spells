using System.Collections.Generic;
using Game.Core;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;

namespace Game.UI
{
    /// <summary>按F后的玩法选择Presentation；不拥有保存、LoadScene或数据库写入权。</summary>
    public sealed class DungeonEntryDialogController : MonoBehaviour
    {
        private static DungeonEntryDialogController _instance;
        [SerializeField] private GameObject _panel;
        [SerializeField] private Text _title, _status;
        [SerializeField] private Button _single, _match, _cancel;
        [SerializeField] private Button _ready, _start, _reconnect;
        [SerializeField] private Text _members;
        private DungeonEntryChoiceRequestedEvent _request;
        private bool _authenticated;
        private Font _font;
        private bool _roomMode;
        private int _roomPortal;
        private bool _gameplayActive, _gameplayMenu, _gameplayInputCaptured;
        // 菜单和库存可以同时占用本机输入；总 InputCaptured 只表示“至少一位 Owner”，
        // 不能再用 !MenuOpen 反推库存已经关闭。这里只记录原库存 Controller 的可见性事实。
        private readonly HashSet<int> _inventoryInputOwners = new HashSet<int>(4);
        // Cancel InputAction 在输入阶段先关闭 Tab，随后 Update 仍可读到同一次 Esc。
        // 保留释放帧，避免“刚释放捕获”被误判成允许打开下一层菜单；不吞掉后续帧的新按键。
        private int _inventoryCaptureReleasedFrame = -1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset() => _instance = null;
#if UNITY_EDITOR || !UNITY_SERVER
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            if (_instance != null) return;
            var prefab = Resources.Load<GameObject>("Online/DungeonEntryDialog");
            if (prefab == null) return; // 未创建Presentation时Run的HasSubscribers Gate拒绝请求且不暂停。
            var root = Instantiate(prefab); DontDestroyOnLoad(root);
        }
#endif
        private void Awake()
        {
            if (_instance != null && _instance != this) { Destroy(gameObject); return; }
            _instance = this; _panel.SetActive(false);
            _single.onClick.AddListener(Single); _match.onClick.AddListener(Match); _cancel.onClick.AddListener(Cancel);
            if (_ready != null) _ready.onClick.AddListener(Ready);
            if (_start != null) _start.onClick.AddListener(StartRoom);
            if (_reconnect != null) _reconnect.onClick.AddListener(Reconnect);
            _font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei", "SimSun", "Arial" }, 20);
            foreach (var text in GetComponentsInChildren<Text>(true)) text.font = _font;
        }
        private void OnEnable()
        {
            EventBus<DungeonEntryChoiceRequestedEvent>.Subscribe(OnRequested);
            EventBus<DungeonEntryChoiceClosedEvent>.Subscribe(OnClosed);
            EventBus<DungeonMatchStatusEvent>.Subscribe(OnMatchStatus);
            EventBus<OnlineLobbyChangedEvent>.Subscribe(OnIdentity);
            EventBus<OnlineRoomChangedEvent>.Subscribe(OnRoom);
            EventBus<OnlineMovementPresentationEvent>.Subscribe(OnGameplay);
            EventBus<InventoryPresentationVisibilityEvent>.Subscribe(OnInventoryVisibility);
            ReplayCurrentInputPresentation();
        }
        private void OnDisable()
        {
            if (_request.PortalId != 0) Submit(DungeonEntryMode.Cancel);
            EventBus<DungeonEntryChoiceRequestedEvent>.Unsubscribe(OnRequested);
            EventBus<DungeonEntryChoiceClosedEvent>.Unsubscribe(OnClosed);
            EventBus<DungeonMatchStatusEvent>.Unsubscribe(OnMatchStatus);
            EventBus<OnlineLobbyChangedEvent>.Unsubscribe(OnIdentity);
            EventBus<OnlineRoomChangedEvent>.Unsubscribe(OnRoom);
            EventBus<OnlineMovementPresentationEvent>.Unsubscribe(OnGameplay);
            EventBus<InventoryPresentationVisibilityEvent>.Unsubscribe(OnInventoryVisibility);
            _inventoryInputOwners.Clear(); _inventoryCaptureReleasedFrame = -1;
        }
        private void OnInventoryVisibility(InventoryPresentationVisibilityEvent state)
        {
            if (state.OwnerId == 0) return;
            if (state.Open) _inventoryInputOwners.Add(state.OwnerId);
            else if (_inventoryInputOwners.Remove(state.OwnerId))
                _inventoryCaptureReleasedFrame = Time.frameCount;
        }
        private void ReplayCurrentInputPresentation()
        {
            // EventBus 不缓存 Visibility。Dialog 可能晚于库存启用，或在库存保持打开时重启；
            // 此时从现有原 Controller 的只读显示事实恢复 Owner，不能要求它重新 Open 或关闭重开。
            // FindObjectsByType 会创建数组，只允许在这个离散启用边界使用，绝不放入 Update 热路径。
            _inventoryInputOwners.Clear();
            var inventories = FindObjectsByType<SinglePlayerInventoryController>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < inventories.Length; i++)
            {
                var inventory = inventories[i];
                if (inventory.isActiveAndEnabled && inventory.PresentationIsReadOnly && inventory.IsOpen)
                    _inventoryInputOwners.Add(inventory.GetInstanceID());
            }
            // 先恢复 Owner，再请求当前 World 的既有只读视图，避免同步重放时把总捕获位误当作完整归属。
            // Actor 仅用于取得本机 Scene handle，不读取网络身份、资产或推进模拟。
            var actor = LocalActorPresentation.Actor;
            if (actor != null && actor.scene.IsValid() && actor.scene.isLoaded)
                EventBus<OnlineMovementViewRequestedEvent>.Publish(new OnlineMovementViewRequestedEvent
                { WorldSceneHandle = actor.scene.handle });
        }
        private void OnGameplay(OnlineMovementPresentationEvent view)
        {
            // 保留原 View-only 合同的保守保护：尚未观察到 Owner 事件时，非菜单捕获仍属于上层交互。
            // 该 fallback 不能区分“菜单和库存同开”，所以明确 Owner 和其释放帧始终独立保留。
            bool inventoryWasCapturing = _gameplayActive && _gameplayInputCaptured && !_gameplayMenu;
            bool inventoryIsCapturing = view.Active && view.InputCaptured && !view.MenuOpen;
            if (inventoryWasCapturing && !inventoryIsCapturing) _inventoryCaptureReleasedFrame = Time.frameCount;
            _gameplayActive = view.Active; _gameplayMenu = view.MenuOpen; _gameplayInputCaptured = view.InputCaptured;
            if (view.Active)
            {
                bool open = view.MenuOpen || !view.Connected;
                _panel.SetActive(open);
                // 断线也会从事件打开UI，不能只在Escape分支解锁，否则按钮可见却点不到。
                // 背包捕获输入需要鼠标，但不能因此把连接/房间菜单一起显示。
                SetCursor(open || view.InputCaptured);
            }
        }
        private void Update()
        {
            if (!_gameplayActive || Keyboard.current == null || !Keyboard.current.escapeKey.wasPressedThisFrame) return;
            // 库存仍打开时 Esc 用于取消拖动/关闭原 Tab；本帧已关闭时，也不能让同一个按键穿透。
            // 明确检查库存 Owner，而非总捕获位；即使先开连接菜单再开 Tab，第一下 Esc 也只属于库存。
            // 释放帧只保护这次输入，下一帧新的 Esc 仍能关闭已经没有库存 Owner 的连接菜单。
            if (_inventoryInputOwners.Count != 0 || (_gameplayInputCaptured && !_gameplayMenu) ||
                _inventoryCaptureReleasedFrame == Time.frameCount) return;
            _gameplayMenu = !_gameplayMenu;
            EventBus<OnlineMovementMenuRequestEvent>.Publish(new OnlineMovementMenuRequestEvent { Open = _gameplayMenu });
            _panel.SetActive(_gameplayMenu);
            SetCursor(_gameplayMenu || _gameplayInputCaptured);
            // 只暂停本机意图；从不修改Time.timeScale，其他玩家/AI与网络继续。
        }
        private static void SetCursor(bool visible)
        { Cursor.lockState = visible ? CursorLockMode.None : CursorLockMode.Locked; Cursor.visible = visible; }
        private void OnDestroy() { if (_instance == this) _instance = null; if (_font != null) Destroy(_font); }
        private void OnRequested(DungeonEntryChoiceRequestedEvent request)
        {
            _request = request; _panel.SetActive(true); _title.text = request.Label.Replace("F ", string.Empty);
            _roomMode = false; ResetRoomButtons(); _authenticated = false; UpdateIdentity();
            EventBus<OnlineLobbyRequestEvent>.Publish(new OnlineLobbyRequestEvent { Action = OnlineLobbyAction.Observe });
        }
        private void OnIdentity(OnlineLobbyChangedEvent view)
        { _authenticated = view.Authenticated; if (_request.PortalId != 0 && !_roomMode) UpdateIdentity(); }
        private void UpdateIdentity()
        {
            _single.interactable = _match.interactable = _authenticated;
            _status.text = _authenticated ? "请选择本次副本的进入方式。" : "请先返回主菜单登录后进入副本。";
        }
        private void OnMatchStatus(DungeonMatchStatusEvent status)
        { if (_request.PortalId == status.PortalId) _status.text = status.Message; }
        private void OnClosed(DungeonEntryChoiceClosedEvent closed)
        { if (_request.PortalId != closed.PortalId) return; _request = default; _panel.SetActive(false); }
        private void Single() { if (_authenticated) Submit(DungeonEntryMode.SinglePlayer); }
        private void Match() { if (_authenticated) Submit(DungeonEntryMode.Matchmaking); }
        private void Cancel() { if (_roomMode) RoomCommand(OnlineRoomAction.Cancel); else Submit(DungeonEntryMode.Cancel); }
        private void Ready() => RoomCommand(OnlineRoomAction.Ready);
        private void StartRoom() => RoomCommand(OnlineRoomAction.Start);
        private void Reconnect() => RoomCommand(OnlineRoomAction.Reconnect);
        private void RoomCommand(OnlineRoomAction action) => EventBus<OnlineRoomRequestEvent>.Publish(new OnlineRoomRequestEvent { PortalId = _roomPortal, Action = action });
        private void ResetRoomButtons()
        {
            _single.gameObject.SetActive(true); _match.gameObject.SetActive(true); _cancel.interactable = true;
            _cancel.GetComponentInChildren<Text>().text = "取消";
            if (_ready != null) _ready.gameObject.SetActive(false);
            if (_start != null) _start.gameObject.SetActive(false);
            if (_reconnect != null) _reconnect.gameObject.SetActive(false);
            if (_members != null) _members.text = "";
        }
        private void OnRoom(OnlineRoomChangedEvent view)
        {
            if (view.Phase == OnlineRoomPhase.None)
            {
                // 返回据点结束上一局的输入显示寿命，不让常驻 Dialog 保留已卸载库存的 Owner。
                _inventoryInputOwners.Clear(); _inventoryCaptureReleasedFrame = -1;
                _roomMode = false; ResetRoomButtons();
                if (_request.PortalId == 0) _panel.SetActive(false); else UpdateIdentity();
                return;
            }
            if (_request.PortalId != view.PortalId && view.Phase != OnlineRoomPhase.Connected && view.Phase != OnlineRoomPhase.Disconnected) return;
            _roomMode = true; _roomPortal = view.PortalId; _panel.SetActive(!_gameplayActive || _gameplayMenu || view.Phase == OnlineRoomPhase.Disconnected);
            bool lobby = view.Phase == OnlineRoomPhase.Lobby;
            bool connected = view.Phase == OnlineRoomPhase.Connected || view.Phase == OnlineRoomPhase.Disconnected;
            _title.text = connected ? "独立副本连接" : "副本匹配与准备";
            _single.gameObject.SetActive(false); _match.gameObject.SetActive(false);
            _status.text = view.Status ?? "正在同步房间…";
            _cancel.interactable = view.Phase != OnlineRoomPhase.Starting && view.Phase != OnlineRoomPhase.Connecting;
            _cancel.GetComponentInChildren<Text>().text = connected ? "断开并返回据点" : "取消匹配";
            if (_ready != null) { _ready.gameObject.SetActive(lobby); _ready.interactable = !view.Busy; _ready.GetComponentInChildren<Text>().text = view.SelfReady ? "取消准备" : "准备"; }
            if (_start != null) { _start.gameObject.SetActive(lobby); _start.interactable = !view.Busy && view.CanStart; }
            if (_reconnect != null) { _reconnect.gameObject.SetActive(view.Phase == OnlineRoomPhase.Disconnected || view.Phase == OnlineRoomPhase.Error); _reconnect.interactable = !view.Busy; _reconnect.GetComponentInChildren<Text>().text = view.Phase == OnlineRoomPhase.Error ? "重试恢复房间" : "重连"; }
            if (_members != null)
            {
                // 只在房间应答/操作时重建最多4行文本，不在Update中拼接字符串。
                string text = connected ? "本局已确认成员：\n" : "";
                if (view.Members != null) for (int i = 0; i < view.Members.Length; i++)
                { var m = view.Members[i]; text += (m.Leader ? "[队长] " : "") + m.Name + (m.Self ? "（你）" : "") + (m.Ready ? "  已准备" : "  未准备") + "\n"; }
                _members.text = text;
            }
        }
        private void Submit(DungeonEntryMode mode)
        {
            if (_request.PortalId == 0) return;
            // 不先自行关框；Run结束选择后才发布Closed，匹配请求本身不能擅自切换场景。
            EventBus<DungeonEntryChoiceSubmittedEvent>.Publish(new DungeonEntryChoiceSubmittedEvent
            { PortalId = _request.PortalId, Actor = _request.Actor, Mode = mode });
        }
    }
}
