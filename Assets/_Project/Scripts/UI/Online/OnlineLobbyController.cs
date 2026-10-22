using Game.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>线上输入/展示 Adapter；不引用网络实现或单机 Profile，移动只是提交意图。</summary>
    public sealed class OnlineLobbyController : MonoBehaviour
    {
        [SerializeField] private InputField _username, _password;
        [SerializeField] private Button _register, _login, _refresh, _logout, _close;
        [SerializeField] private Text _status, _summary;
        [SerializeField] private Text[] _itemLabels;
        [SerializeField] private Button[] _moveButtons;
        private OnlineLobbyChangedEvent _view;
        private Font _runtimeFont;
        private void Awake()
        {
            _register.onClick.AddListener(Register); _login.onClick.AddListener(Login);
            _refresh.onClick.AddListener(Refresh); _logout.onClick.AddListener(Logout); _close.onClick.AddListener(Close);
            // Listener/索引闭包只在建面板时分配；响应才更新文字，没有 Update/OnGUI 拼字符串。
            for (int i = 0; i < _moveButtons.Length; i++) { int index = i; _moveButtons[i].onClick.AddListener(() => Move(index)); }
            _runtimeFont = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei", "SimSun", "Arial" }, 20);
            foreach (var label in GetComponentsInChildren<Text>(true)) label.font = _runtimeFont;
            _password.contentType = InputField.ContentType.Password;
            _password.characterLimit = 128; _username.characterLimit = 24;
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true; Time.timeScale = 1;
        }
        private void OnEnable() => EventBus<OnlineLobbyChangedEvent>.Subscribe(OnChanged);
        private void OnDisable() => EventBus<OnlineLobbyChangedEvent>.Unsubscribe(OnChanged);
        private void OnDestroy() { if (_runtimeFont != null) Destroy(_runtimeFont); }
        private void Register() => Submit(OnlineLobbyAction.Register);
        private void Login() => Submit(OnlineLobbyAction.Login);
        private void Refresh() => Submit(OnlineLobbyAction.Refresh);
        private void Logout() => Submit(OnlineLobbyAction.Logout);
        private void Submit(OnlineLobbyAction action) => EventBus<OnlineLobbyRequestEvent>.Publish(new OnlineLobbyRequestEvent
        { Action = action, Username = _username.text, Password = _password.text });
        private void Close()
        {
            _password.text = string.Empty;
            EventBus<OnlineLobbyClosedEvent>.Publish(default); Destroy(gameObject);
        }
        private void Move(int index)
        {
            if (_view.Busy || !_view.Authenticated || _view.Items == null || index >= _view.Items.Length) return;
            OnlineItemView item = _view.Items[index];
            string destination = item.Area == "Backpack" ? "Warehouse" : "Backpack";
            int capacity = destination == "Backpack" ? 8 : 16, free = -1;
            for (int slot = 0; slot < capacity; slot++)
            {
                bool used = false;
                for (int i = 0; i < _view.Items.Length; i++) if (_view.Items[i].Area == destination && _view.Items[i].Slot == slot) { used = true; break; }
                if (!used) { free = slot; break; }
            }
            if (free < 0) { _status.text = "目标区域已满。"; return; }
            EventBus<OnlineLobbyRequestEvent>.Publish(new OnlineLobbyRequestEvent { Action = OnlineLobbyAction.Move,
                InstanceId = item.InstanceId, ExpectedVersion = _view.Version, Destination = destination, Slot = free });
        }
        private void OnChanged(OnlineLobbyChangedEvent view)
        {
            _view = view; _status.text = view.Status ?? string.Empty;
            _register.interactable = _login.interactable = !_view.Busy && !_view.Authenticated;
            _username.interactable = _password.interactable = !_view.Busy && !_view.Authenticated;
            _refresh.interactable = _logout.interactable = !_view.Busy && _view.Authenticated;
            // 用户可关闭等待中的请求；HTTP取消不等于回滚已提交的SQL，重新登录后以服务器状态为准。
            _close.interactable = true;
            if (_view.Authenticated) _password.text = string.Empty;
            _summary.text = _view.Authenticated ? "线上账号：" + view.Username + "\n角色：" + view.PlayerId + "\n资产版本：" + view.Version + "    金币：" + view.Gold + "\n线上仓库与单机存档独立" : "登录后读取服务器保存的角色与资产。\n本页面不会导入单机装备或金币。";
            for (int i = 0; i < _itemLabels.Length; i++)
            {
                bool present = view.Items != null && i < view.Items.Length;
                _itemLabels[i].transform.parent.gameObject.SetActive(present);
                if (!present) continue;
                OnlineItemView item = view.Items[i];
                _itemLabels[i].text = item.DisplayName + "  |  " + (item.Area == "Backpack" ? "背包" : "仓库") + " #" + item.Slot;
                _moveButtons[i].GetComponentInChildren<Text>().text = item.Area == "Backpack" ? "移至仓库" : "移至背包";
                _moveButtons[i].interactable = !view.Busy && view.Authenticated;
            }
        }
    }
}
