using Game.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Multiplayer
{
    /// <summary>菜单Prefab只负责组合唯一Application Session，UI生命周期与身份生命周期分离。</summary>
    [DefaultExecutionOrder(-250)]
    public sealed class OnlineAccountBootstrap : MonoBehaviour
    {
        [SerializeField] private OnlineServiceProfile _profile;
        public void SetProfile(OnlineServiceProfile profile) => _profile = profile;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetHubLifecycle() => SceneManager.sceneLoaded -= OnHubSceneLoaded;

        private static void OnHubSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!OnlineAccountInventoryHubScope.IsRequiredForScene(scene)) return;
            var session = OnlineAccountClient.ApplicationSession;
            if (session == null) { GameLog.Warn("账号据点加载后身份不可用，保留禁写要求。", "Accounts"); return; }
            if (!session.TryPrepareVerifiedHubScene(scene, out var error))
            {
                // 加载前已封旧档；准备失败不能通过清Scope、发模板或本地初始化来制造成功。
                session.ReportHubEntryPreparationFailure(error);
                GameLog.Warn("账号据点展示未就绪，原资产未回落本地：" + error, "Accounts");
            }
        }
        private void Awake()
        {
            // 菜单会随Single加载销毁，静态回调不能跟着菜单OnDisable取消；只由SubsystemReset结束。
            SceneManager.sceneLoaded -= OnHubSceneLoaded; SceneManager.sceneLoaded += OnHubSceneLoaded;
            if (OnlineAccountClient.ApplicationSession != null) return;
            // inactive根允许先注入配置再Awake；AddComponent在active对象上会立即执行Awake。
            var root = new GameObject("OnlineAccountSession"); root.SetActive(false);
            var session = root.AddComponent<OnlineAccountClient>(); session.ConfigureApplicationSession(_profile);
            root.AddComponent<OnlineRoomClient>(); // 身份与房间同属Application根，不能随菜单卸载丢失入场状态。
            root.SetActive(true);
        }
    }
}
