using Game.Core;
using Game.Networking;
using UnityEngine;

namespace Game.Multiplayer
{
    /// <summary>组合层按构建角色装配服务端；UNITY_SERVER 才自动监听，不用 batchmode 猜角色。</summary>
    [RequireComponent(typeof(NornNetworkManager))]
    public sealed class DedicatedServerBootstrap : MonoBehaviour
    {
        [SerializeField] private OnlineNetworkProfile _profile;
        private NornNetworkManager _manager;
        public void SetProfile(OnlineNetworkProfile profile) => _profile = profile;

        private void Start()
        {
            _manager = GetComponent<NornNetworkManager>();
            if (_profile == null) { GameLog.Error("实验缺少 OnlineNetworkProfile。", "OnlineLab"); return; }
            // 所有组件 Awake 已结束；这时 KCP 初始化完毕，配置和启动不会抢生命周期顺序。
            _manager.ConfigureLocalLab(_profile.Port, _profile.MaxPlayers, _profile.MessagePoolCapacity, _profile.ProbeInterval, _profile.SendRate);
#if UNITY_SERVER
            _manager.StartServer();
#endif
        }
    }
}
