using UnityEngine;

namespace Game.Multiplayer
{
    /// <summary>本地通信实验配置；暂不允许填公网地址，也不存放密码或 JoinTicket。</summary>
    [CreateAssetMenu(menuName = "Norn/Online/Network Profile")]
    public sealed class OnlineNetworkProfile : ScriptableObject
    {
        [SerializeField, Range(1, 65535)] private int _port = 7777;
        [SerializeField, Range(1, 4)] private int _maxPlayers = 4;
        [SerializeField, Range(1, 64)] private int _messagePoolCapacity = 8;
        [SerializeField, Range(.1f, 5f)] private float _probeInterval = 1f;
        [SerializeField, Range(1, 60)] private int _sendRate = 30;
        public ushort Port => (ushort)_port;
        public int MaxPlayers => _maxPlayers;
        public int MessagePoolCapacity => _messagePoolCapacity;
        public float ProbeInterval => _probeInterval;
        public int SendRate => _sendRate;
    }
}
