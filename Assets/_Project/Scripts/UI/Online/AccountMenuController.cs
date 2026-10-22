using Game.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>正式主菜单只负责认证和导航；进入玩法的选择留在据点传送门。</summary>
    public sealed class AccountMenuController : MonoBehaviour
    {
        [SerializeField] private InputField _username, _password;
        [SerializeField] private Button _register, _login, _enter, _logout, _settings, _quit;
        [SerializeField] private Text _status, _identity;
        private Font _font;
        private void Awake()
        {
            _register.onClick.AddListener(Register); _login.onClick.AddListener(Login); _enter.onClick.AddListener(Enter);
            _logout.onClick.AddListener(Logout); _settings.onClick.AddListener(Settings); _quit.onClick.AddListener(Quit);
            _password.contentType = InputField.ContentType.Password; _password.characterLimit = 128; _username.characterLimit = 24;
            _font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei", "SimSun", "Arial" }, 20);
            foreach (var text in GetComponentsInChildren<Text>(true)) text.font = _font;
            _enter.interactable = _logout.interactable = false;
        }
        private void OnEnable()
        {
            EventBus<OnlineLobbyChangedEvent>.Subscribe(OnChanged);
            // Session跨场景存在，重新显示菜单要主动重放；事件不会自动保留最后一次值。
            EventBus<OnlineLobbyRequestEvent>.Publish(new OnlineLobbyRequestEvent { Action = OnlineLobbyAction.Observe });
        }
        private void Start() => EventBus<OnlineLobbyRequestEvent>.Publish(new OnlineLobbyRequestEvent { Action = OnlineLobbyAction.Observe });
        private void OnDisable() { EventBus<OnlineLobbyChangedEvent>.Unsubscribe(OnChanged); _password.text = string.Empty; }
        private void OnDestroy() { if (_font != null) Destroy(_font); }
        private void Register() => Send(OnlineLobbyAction.Register);
        private void Login() => Send(OnlineLobbyAction.Login);
        private void Logout() => Send(OnlineLobbyAction.Logout);
        private void Send(OnlineLobbyAction action) => EventBus<OnlineLobbyRequestEvent>.Publish(new OnlineLobbyRequestEvent
            { Action = action, Username = _username.text, Password = _password.text });
        private void Enter() => Command(AccountMenuCommand.EnterHub);
        private void Settings() => Command(AccountMenuCommand.Settings);
        private void Quit() => Command(AccountMenuCommand.Quit);
        private static void Command(AccountMenuCommand command) => EventBus<AccountMenuCommandEvent>.Publish(new AccountMenuCommandEvent { Command = command });
        private void OnChanged(OnlineLobbyChangedEvent view)
        {
            _register.interactable = _login.interactable = !view.Busy && !view.Authenticated;
            _username.interactable = _password.interactable = !view.Busy && !view.Authenticated;
            _enter.interactable = _logout.interactable = !view.Busy && view.Authenticated;
            if (view.Authenticated) _password.text = string.Empty;
            _identity.text = view.Authenticated ? "已登录：" + view.Username : "请先登录，单机与匹配均需要账号。";
            _status.text = view.Status ?? "进入据点后，在副本传送门选择单机或匹配。";
        }
    }
}
