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
        private bool _gameplayActive, _gameplayMenu;

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
        }
        private void OnGameplay(OnlineMovementPresentationEvent view)
        {
            _gameplayActive = view.Active; _gameplayMenu = view.MenuOpen;
            if (view.Active)
            {
                bool open = view.MenuOpen || !view.Connected;
                _panel.SetActive(open);
                // 断线也会从事件打开UI，不能只在Escape分支解锁，否则按钮可见却点不到。
                Cursor.lockState = open ? CursorLockMode.None : CursorLockMode.Locked; Cursor.visible = open;
            }
        }
        private void Update()
        {
            if (!_gameplayActive || Keyboard.current == null || !Keyboard.current.escapeKey.wasPressedThisFrame) return;
            _gameplayMenu = !_gameplayMenu;
            EventBus<OnlineMovementMenuRequestEvent>.Publish(new OnlineMovementMenuRequestEvent { Open = _gameplayMenu });
            _panel.SetActive(_gameplayMenu);
            Cursor.lockState = _gameplayMenu ? CursorLockMode.None : CursorLockMode.Locked; Cursor.visible = _gameplayMenu;
            // 只暂停本机意图；从不修改Time.timeScale，其他玩家/AI与网络继续。
        }
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
