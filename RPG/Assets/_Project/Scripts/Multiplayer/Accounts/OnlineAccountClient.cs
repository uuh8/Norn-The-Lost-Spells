using System;
using System.Collections;
using Game.Core;
using Game.Networking;
using Game.NetworkProtocol;
using UnityEngine;
using UnityEngine.Networking;

namespace Game.Multiplayer
{
    /// <summary>账户组合层持内存Session；正式Application实例跨场景，诊断Lab仍按Scene释放，不引用Run存档。</summary>
    public sealed class OnlineAccountClient : MonoBehaviour
    {
        [SerializeField] private OnlineServiceProfile _profile;
        private ContentManifest _manifest;
        private UnityWebRequest _request;
        private string _token;
        private bool _busy;
        private OnlineProfileReply _view;
        private bool _applicationLifetime;
        private long _expiresUnixMilliseconds;
        public static OnlineAccountClient ApplicationSession { get; private set; }
        public bool Busy => _busy;
        // 本机到期检查只是UX；EnterHub/Profile/将来的JoinTicket仍由业务服校验真实Session。
        public bool Authenticated => !string.IsNullOrEmpty(_token) && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < _expiresUnixMilliseconds;
        public string LastStatus { get; private set; }
        public long LastHttpStatus { get; private set; }
        public string InitializationError { get; private set; }
        public void SetProfile(OnlineServiceProfile profile) => _profile = profile;
        public void ConfigureApplicationSession(OnlineServiceProfile profile) { _profile = profile; _applicationLifetime = true; }
        internal string PlayerId => _view?.PlayerId;
        internal uint InventoryVersion => _view?.Version ?? 0;
        internal UnityWebRequest CreateSessionRequest(string method, string path, string json)
        {
            if (!Authenticated || _manifest == null) throw new InvalidOperationException("SessionUnavailable");
            // 同程序集的房间组合复用授权，UI只收Core视图，不得到Session Token字符串。
            return BusinessHttpClient.Create(_profile.BaseUrl, method, path, json, _token, _manifest.Hash);
        }
        internal void InvalidateSession() { _token = null; _view = null; Publish("登录已失效，请重新登录。", false); }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetApplicationSession() => ApplicationSession = null;

        private void Awake()
        {
            if (_applicationLifetime)
            {
                if (ApplicationSession != null && ApplicationSession != this) { Destroy(gameObject); return; }
                ApplicationSession = this; DontDestroyOnLoad(gameObject); // 独立服务根，不把菜单Canvas一起跨场景。
            }
            // 只在面板建立时加载/校验；不在 Update 中读文件或反复解析 JSON。
            var asset = Resources.Load<TextAsset>("Online/ContentManifest");
            if (asset == null) { InitializationError = "MissingManifestResource"; return; }
            if (_profile == null) { InitializationError = "MissingServiceProfile"; return; }
            if (!BusinessHttpClient.IsAllowedBaseUrl(_profile.BaseUrl)) { InitializationError = "InvalidBusinessUrl"; return; }
            try
            {
                _manifest = JsonUtility.FromJson<ContentManifest>(asset.text);
                if (_manifest.Hash != ContentManifestRules.CalculateHash(_manifest)) { InitializationError = "ManifestHashMismatch"; _manifest = null; }
            }
            catch (ArgumentException) { InitializationError = "InvalidManifestData"; _manifest = null; }
            GameLog.Info("业务 Client 初始化：" + (InitializationError ?? "Ready"), "OnlineAccounts");
        }
        private void OnEnable()
        {
            EventBus<OnlineLobbyRequestEvent>.Subscribe(OnRequest);
        }
        private void Start() => Publish(_manifest != null ? (_applicationLifetime ? "请注册或登录后进入据点。" : "线上资产独立保存；请注册或登录。") : "线上配置无效：" + InitializationError, false);
        private void OnDisable()
        {
            EventBus<OnlineLobbyRequestEvent>.Unsubscribe(OnRequest);
            // Unity 停止 Coroutine 时不能靠猜测 finally 的触发时机管理 native Request；显式终止并 Dispose。
            _request?.Abort(); ReleaseRequest(); StopAllCoroutines();
            _token = null; _view = null; _lastBody = null; _busy = false;
        }
        private void OnDestroy() { if (ApplicationSession == this) ApplicationSession = null; }
        private void ReleaseRequest() { var request = _request; _request = null; request?.Dispose(); }

        private void OnRequest(OnlineLobbyRequestEvent request)
        {
            if (request.Action == OnlineLobbyAction.Observe) { Publish(LastStatus, _busy); return; }
            if (_busy || _manifest == null) return;
            if (!Authenticated && request.Action != OnlineLobbyAction.Register && request.Action != OnlineLobbyAction.Login)
            { _token = null; _view = null; Publish("请先登录。", false); return; }
            StartCoroutine(Execute(request));
        }
        private IEnumerator Execute(OnlineLobbyRequestEvent request)
        {
            _busy = true; Publish("正在请求业务服…", true);
            switch (request.Action)
            {
                case OnlineLobbyAction.Register:
                    yield return Send("POST", "/api/online/accounts", JsonUtility.ToJson(new RegisterAccountRequest { Username = request.Username, Password = request.Password }));
                    if (_lastSuccess) Publish(_applicationLifetime ? "注册成功，请登录。" : "注册成功，点击登录读取线上角色。", false);
                    break;
                case OnlineLobbyAction.Login:
                    yield return Send("POST", "/api/online/sessions", JsonUtility.ToJson(new LoginAccountRequest { Username = request.Username, Password = request.Password,
                        ProtocolVersion = _manifest.ProtocolVersion, ContentManifestHash = _manifest.Hash }));
                    if (_lastSuccess)
                    {
                        var login = JsonUtility.FromJson<LoginAccountReply>(_lastBody);
                        if (login == null || string.IsNullOrEmpty(login.Token)) Publish("登录应答缺少身份凭据。", false);
                        else { _token = login.Token; _expiresUnixMilliseconds = login.ExpiresUnixMilliseconds; yield return Refresh(); }
                    }
                    break;
                case OnlineLobbyAction.Refresh: yield return Refresh(); break;
                case OnlineLobbyAction.EnterHub:
                    yield return Refresh();
                    // 断网时即使旧Token尚未到期也不放行；必须本次真实Profile读取成功。
                    if (_lastSuccess && Authenticated && _view != null) EventBus<AccountHubEntryReadyEvent>.Publish(default);
                    break;
                case OnlineLobbyAction.Move:
                    // 新用户动作生成一个键；若以后加自动重试，必须复用此对象/键，不能每次重试 NewGuid。
                    yield return Send("POST", "/api/online/inventory/move", JsonUtility.ToJson(new InventoryMoveRequest { CommandId = Guid.NewGuid().ToString("N"),
                        ExpectedVersion = request.ExpectedVersion, InstanceId = request.InstanceId, Destination = request.Destination, Slot = request.Slot }));
                    if (_lastSuccess) yield return Refresh();
                    break;
                case OnlineLobbyAction.Logout:
                    yield return Send("DELETE", "/api/online/sessions/current", null);
                    _token = null; _view = null; Publish(_applicationLifetime ? "已注销，请重新登录。" : "已清除本机内存身份。", false); break;
            }
            _busy = false; Publish(LastStatus, false);
        }
        private bool _lastSuccess;
        private string _lastBody;
        private IEnumerator Send(string method, string path, string json)
        {
            _lastSuccess = false; _lastBody = null;
            _request = BusinessHttpClient.Create(_profile.BaseUrl, method, path, json, _token, _manifest.Hash);
            GameLog.Info("业务 HTTP 发起：" + method + " " + path, "OnlineAccounts"); // 不记录请求体、Header 或 Token。
            yield return _request.SendWebRequest();
            _lastBody = _request.downloadHandler.text;
            _lastSuccess = _request.result == UnityWebRequest.Result.Success;
            long status = _request.responseCode;
            LastHttpStatus = status;
            GameLog.Info("业务 HTTP 完成：" + status + " / " + _request.result, "OnlineAccounts");
            if (!_lastSuccess)
            {
                if (status == 401) { _token = null; _view = null; }
                string code = null;
                if (!string.IsNullOrEmpty(_lastBody))
                    try { code = JsonUtility.FromJson<OnlineErrorReply>(_lastBody)?.Code; } catch (ArgumentException) { }
                Publish(ErrorMessage(code, status), false);
            }
            ReleaseRequest();
        }
        private IEnumerator Refresh()
        {
            yield return Send("GET", "/api/online/profile", null);
            if (!_lastSuccess) yield break;
            var profile = JsonUtility.FromJson<OnlineProfileReply>(_lastBody);
            if (profile == null || profile.ContentManifestHash != _manifest.Hash || profile.Items == null || profile.Items.Length > 24)
            { _view = null; Publish("线上角色应答与本机内容清单不一致。", false); yield break; }
            _view = profile; Publish(_applicationLifetime ? "登录已确认，可以进入据点。" : "线上角色已读取，物品位置由业务服确认。", false);
        }
        private void Publish(string message, bool busy)
        {
            LastStatus = message;
            var items = _view == null ? Array.Empty<OnlineItemView>() : new OnlineItemView[_view.Items.Length];
            for (int i = 0; i < items.Length; i++) { var item = _view.Items[i]; items[i] = new OnlineItemView(item.InstanceId, item.DisplayName, item.Area, item.Slot); }
            EventBus<OnlineLobbyChangedEvent>.Publish(new OnlineLobbyChangedEvent { Busy = busy, Authenticated = Authenticated, Status = message,
                Username = _view?.Username, PlayerId = _view?.PlayerId, Version = _view?.Version ?? 0, Gold = _view?.Gold ?? 0, Items = items });
        }
        private static string ErrorMessage(string code, long status)
        {
            switch (code)
            {
                case "UsernameTaken": return "用户名已存在，请登录或换一个用户名。";
                case "InvalidCredentials": return "用户名或密码错误。";
                case "InvalidCredentialsFormat": return "用户名需 3–24 位字母/数字/下划线，密码需 10–128 字符。";
                case "ContentVersionMismatch": return "内容版本不匹配，请检查 Client 与业务服清单。";
                case "ProfileVersionConflict": return "线上资产已更新，请刷新后重试。";
                case "SlotOccupied": return "目标位置已占用，请刷新后重试。";
            }
            if (status == 401) return "登录已失效，请重新登录。";
            if (status == 429) return "请求过于频繁，请稍后重试。";
            if (status == 0) return "无法连接业务服或请求超时；若刚执行移动，请先刷新确认结果。";
            return "业务请求失败（HTTP " + status + "），请查看服务端错误代码。";
        }
    }
}
