using UnityEngine;

namespace Game.Multiplayer
{
    /// <summary>仅供 loopback 负载实验；控制有界容量与采样，不作为正式副本容量承诺。</summary>
    [CreateAssetMenu(menuName = "Norn/Diagnostics/Transport Load Profile")]
    public sealed class TransportLoadProfile : ScriptableObject
    {
        [SerializeField, Range(1, 65535)] private int _port = 7788;
        [SerializeField, Range(1, 60)] private int _requestsPerSecond = 30;
        [SerializeField, Range(30, 120)] private int _frameRate = 60;
        [SerializeField, Range(1, 60)] private int _sendRate = 30;
        [SerializeField, Range(1, 10)] private float _warmupSeconds = 3;
        [SerializeField, Range(2, 20)] private float _shortMeasureSeconds = 6;
        [SerializeField, Range(3, 25)] private float _longMeasureSeconds = 10;
        [SerializeField, Range(5, 30)] private float _serverMeasureSeconds = 14;
        [SerializeField] private string _text = "你好，Norn";
        public ushort Port => (ushort)_port;
        public int RequestsPerSecond => _requestsPerSecond;
        public int FrameRate => _frameRate;
        public int SendRate => _sendRate;
        public float WarmupSeconds => _warmupSeconds;
        public float ShortMeasureSeconds => _shortMeasureSeconds;
        public float LongMeasureSeconds => _longMeasureSeconds;
        public float ServerMeasureSeconds => _serverMeasureSeconds;
        public string Text => _text;
        public bool IsValid => _port > 0 && _port <= 65535 && _requestsPerSecond > 0 && _requestsPerSecond <= 60 &&
            _frameRate >= 30 && _frameRate <= 120 && _sendRate > 0 && _sendRate <= 60 &&
            float.IsFinite(_warmupSeconds) && _warmupSeconds >= 1 && _warmupSeconds <= 10 &&
            float.IsFinite(_shortMeasureSeconds) && _shortMeasureSeconds >= 2 &&
            float.IsFinite(_longMeasureSeconds) && _longMeasureSeconds >= _shortMeasureSeconds + 2 &&
            float.IsFinite(_serverMeasureSeconds) && _serverMeasureSeconds >= _longMeasureSeconds + 2 &&
            _serverMeasureSeconds <= 30 && !string.IsNullOrEmpty(_text);
    }
}
