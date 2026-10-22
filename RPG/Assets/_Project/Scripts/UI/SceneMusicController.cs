using Game.Core;
using UnityEngine;

namespace Game.UI
{
    /// <summary>
    /// Scene-local BGM 播放器。P7 与 P8 各自配置不同 AudioClip；
    /// 不使用 DontDestroyOnLoad，Scene 卸载天然终止旧曲，避免双 AudioSource 重叠。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(AudioSource))]
    public sealed class SceneMusicController : MonoBehaviour
    {
        [SerializeField] private AudioClip _music;
        [SerializeField, Range(0f, 1f)] private float _volume = 0.65f;
        [SerializeField] private bool _playOnStart = true;

        private AudioSource _source;

        private void Awake()
        {
            _source = GetComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.loop = true;
            _source.spatialBlend = 0f;
            _source.clip = _music;
            _source.volume = _volume;
        }

        private void Start()
        {
            if (!_playOnStart)
            {
                return;
            }

            if (_music == null)
            {
                GameLog.Warn($"SceneMusicController '{name}' 未绑定 BGM AudioClip。", "UI");
                return;
            }

            _source.Play();
        }
    }
}
