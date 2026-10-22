using Game.Combat;
using Game.Core;
using UnityEngine;

namespace Game.Rendering
{
    /// <summary>
    /// 全局的一次性元素反应表现入口。它不按 TargetId 过滤，因此角色、敌人和世界物质触发的
    /// ToxicCombustion 都会在事件携带的 WorldPosition 播放同一套 Burst VFX。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ReactionBurstVisualController : MonoBehaviour
    {
        private const int MaximumPoolCapacity = 16;

        [SerializeField] private StatusReactionVisualProfile _profile;
        [SerializeField] private Transform _visualRoot;

        private BurstSlot[] _toxicSlots;
        private float[] _toxicRemainingSeconds;
        private int _nextToxicSlot;

        public int ToxicPoolSize => _toxicSlots != null ? _toxicSlots.Length : 0;
        public Vector3 LastToxicBurstPosition { get; private set; }

        public int ActiveToxicBurstCount
        {
            get
            {
                if (_toxicSlots == null) return 0;
                int count = 0;
                for (int i = 0; i < _toxicSlots.Length; i++)
                    if (_toxicSlots[i].IsActive) count++;
                return count;
            }
        }

        private void Awake()
        {
            BuildToxicPool();
        }

        private void OnEnable()
        {
            EventBus<ElementReactionEvent>.Subscribe(OnReaction);
        }

        private void OnDisable()
        {
            EventBus<ElementReactionEvent>.Unsubscribe(OnReaction);
            StopAllToxicBursts();
        }

        private void Update()
        {
            // 倒计时数组在 Awake 固定分配；热路径只有数组遍历，不产生 GC Alloc。
            if (_toxicSlots == null) return;
            float deltaTime = Time.deltaTime;
            for (int i = 0; i < _toxicSlots.Length; i++)
            {
                if (_toxicRemainingSeconds[i] <= 0f) continue;
                _toxicRemainingSeconds[i] -= deltaTime;
                if (_toxicRemainingSeconds[i] <= 0f)
                    _toxicSlots[i].Stop();
            }
        }

        private void BuildToxicPool()
        {
            if (_profile == null)
            {
                GameLog.Warn("ReactionBurstVisualController 缺少 StatusReactionVisualProfile。", "Rendering");
                return;
            }

            if (_profile.ToxicResolvedPrefab == null)
            {
                GameLog.Warn("StatusReactionVisualProfile 缺少 ToxicResolvedPrefab。", "Rendering");
                return;
            }

            int capacity = Mathf.Clamp(_profile.ToxicBurstPoolCapacity, 1, MaximumPoolCapacity);
            _toxicSlots = new BurstSlot[capacity];
            _toxicRemainingSeconds = new float[capacity];
            Transform parent = _visualRoot != null ? _visualRoot : transform;
            for (int i = 0; i < capacity; i++)
            {
                GameObject instance = Instantiate(_profile.ToxicResolvedPrefab, parent);
                instance.name = $"ToxicResolvedBurst_{i}";
                _toxicSlots[i] = new BurstSlot(instance);
            }
        }

        private void OnReaction(ElementReactionEvent reactionEvent)
        {
            if (reactionEvent.Reaction != ElementReactionId.ToxicCombustion
                || reactionEvent.Phase != ElementReactionPhase.Resolved
                || _toxicSlots == null
                || _toxicSlots.Length == 0)
                return;

            int slotIndex = _nextToxicSlot;
            _nextToxicSlot = (_nextToxicSlot + 1) % _toxicSlots.Length;
            LastToxicBurstPosition = reactionEvent.WorldPosition;
            _toxicSlots[slotIndex].Play(reactionEvent.WorldPosition);
            _toxicRemainingSeconds[slotIndex] = Mathf.Max(0.05f, _profile.ToxicResolvedLifetime);
        }

        private void StopAllToxicBursts()
        {
            if (_toxicSlots == null) return;
            for (int i = 0; i < _toxicSlots.Length; i++)
            {
                _toxicRemainingSeconds[i] = 0f;
                _toxicSlots[i].Stop();
            }
        }

#if UNITY_INCLUDE_TESTS
        public void ConfigureForTests(StatusReactionVisualProfile profile, Transform visualRoot = null)
        {
            _profile = profile;
            _visualRoot = visualRoot;
        }
#endif

        private sealed class BurstSlot
        {
            private readonly GameObject _instance;
            private readonly ParticleSystem[] _particleSystems;

            public BurstSlot(GameObject instance)
            {
                _instance = instance;
                _particleSystems = instance.GetComponentsInChildren<ParticleSystem>(true);
                Stop();
            }

            public bool IsActive => _instance != null && _instance.activeSelf;

            public void Play(Vector3 worldPosition)
            {
                if (_instance == null) return;
                _instance.transform.position = worldPosition;
                _instance.SetActive(true);
                for (int i = 0; i < _particleSystems.Length; i++)
                {
                    _particleSystems[i].Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    _particleSystems[i].Play(true);
                }
            }

            public void Stop()
            {
                if (_instance == null) return;
                for (int i = 0; i < _particleSystems.Length; i++)
                    _particleSystems[i].Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                _instance.SetActive(false);
            }
        }
    }
}
