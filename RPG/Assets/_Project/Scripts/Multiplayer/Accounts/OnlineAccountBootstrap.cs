using UnityEngine;

namespace Game.Multiplayer
{
    /// <summary>菜单Prefab只负责组合唯一Application Session，UI生命周期与身份生命周期分离。</summary>
    [DefaultExecutionOrder(-250)]
    public sealed class OnlineAccountBootstrap : MonoBehaviour
    {
        [SerializeField] private OnlineServiceProfile _profile;
        public void SetProfile(OnlineServiceProfile profile) => _profile = profile;
        private void Awake()
        {
            if (OnlineAccountClient.ApplicationSession != null) return;
            // inactive根允许先注入配置再Awake；AddComponent在active对象上会立即执行Awake。
            var root = new GameObject("OnlineAccountSession"); root.SetActive(false);
            var session = root.AddComponent<OnlineAccountClient>(); session.ConfigureApplicationSession(_profile);
            root.AddComponent<OnlineRoomClient>(); // 身份与房间同属Application根，不能随菜单卸载丢失入场状态。
            root.SetActive(true);
        }
    }
}
