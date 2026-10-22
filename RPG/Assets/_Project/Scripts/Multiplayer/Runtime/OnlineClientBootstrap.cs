using Game.Networking;
using Mirror;
using UnityEngine;

namespace Game.Multiplayer
{
    /// <summary>开发实验界面；不修改正式 MainMenu/Hub，不创建 Host 模式或读取单机存档。</summary>
    [RequireComponent(typeof(NornNetworkManager))]
    public sealed class OnlineClientBootstrap : MonoBehaviour
    {
        private NornNetworkManager _manager;
        private string _counterText = "Sent: 0   Replies: 0";
        private long _lastSent = -1, _lastReceived = -1;
        private void Awake() => _manager = GetComponent<NornNetworkManager>();

#if !UNITY_SERVER
        private void OnGUI()
        {
            // IMGUI 只作为可移除的实验工具；其绘制分配不代表正式游戏 UI 的性能方案。
            GUI.Box(new Rect(20, 20, 460, 220), "Norn - Local KCP / Protobuf Lab");
            GUI.Label(new Rect(40, 55, 420, 25), _manager.Status);
            // 仅当一秒探测计数变化时更新文字，不在每次 IMGUI Layout/Repaint 重复拼接。
            if (_lastSent != _manager.SentProbes || _lastReceived != _manager.ReceivedReplies)
            {
                _lastSent = _manager.SentProbes; _lastReceived = _manager.ReceivedReplies;
                _counterText = "Sent: " + _lastSent + "   Replies: " + _lastReceived;
            }
            GUI.Label(new Rect(40, 85, 420, 25), _counterText);
            GUI.Label(new Rect(40, 115, 420, 25), "127.0.0.1 only | no account / gameplay authority yet");
            if (!NetworkClient.active && _manager.IsConfigured)
            {
                if (GUI.Button(new Rect(40, 155, 150, 35), "Connect Client")) _manager.StartClient();
            }
            else if (GUI.Button(new Rect(40, 155, 150, 35), "Disconnect")) _manager.StopClient();
            if (GUI.Button(new Rect(220, 155, 150, 35), "Quit Client")) Application.Quit();
        }
#endif
    }
}
