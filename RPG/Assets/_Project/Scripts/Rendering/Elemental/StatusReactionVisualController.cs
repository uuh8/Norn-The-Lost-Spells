using Game.Combat;
using Game.Core;
using UnityEngine;

namespace Game.Rendering
{
    /// <summary>
    /// ElementReactionEvent 的只读表现桥。它只管理视觉实例的生命周期，
    /// 不读取或修改状态强度，也不决定反应能否发生。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StatusReactionVisualController : MonoBehaviour
    {
        // Profile 决定“用什么表现”，visualRoot 决定实例挂载位置；两者都属于 Presentation 配置。
        [SerializeField] private StatusReactionVisualProfile _profile;
        [SerializeField] private Transform _visualRoot;

        private ContinuousVisualSlot _extinguishSlot;
        private ContinuousVisualSlot _igniteGooSlot;
        private int _carrierId;
        private float _extinguishStrength;
        private float _extinguishExpectedDuration;

        public bool IsExtinguishVisualActive => _extinguishSlot != null && _extinguishSlot.IsActive;
        public int ExtinguishVisualInstanceId => _extinguishSlot != null ? _extinguishSlot.InstanceId : 0;
        public float ExtinguishStrength => _extinguishStrength;
        public float ExtinguishExpectedDuration => _extinguishExpectedDuration;

        private void Awake()
        {
            // InstanceID 与 Combat 事件中的 TargetId 使用同一身份语义，因而一个全局 EventBus
            // 可以广播所有角色的反应，而每个 Controller 只消费属于自身的事件。
            _carrierId = gameObject.GetInstanceID();
            Transform parent = _visualRoot != null ? _visualRoot : transform;

            if (_profile == null)
            {
                GameLog.Warn("StatusReactionVisualController 未配置 Visual Profile", "ReactionVisual");
                return;
            }

            // Instantiate 和 ParticleSystem 数组收集只发生在初始化阶段；
            // EventBus 回调中只复用 Slot，避免反应开始时产生 Instantiate/GC 峰值。
            _extinguishSlot = ContinuousVisualSlot.Create(
                _profile.ExtinguishSteamPrefab,
                parent,
                "RV_ExtinguishSteam_Runtime");
            _igniteGooSlot = ContinuousVisualSlot.Create(
                _profile.IgniteGooPrefab,
                parent,
                "RV_IgniteGoo_Runtime");
        }

        private void OnEnable()
        {
            // 表现层只订阅 Combat 发布的事实；Combat 不持有本组件引用，保持依赖方向单向。
            EventBus<ElementReactionEvent>.Subscribe(OnReaction);
        }

        private void OnDisable()
        {
            EventBus<ElementReactionEvent>.Unsubscribe(OnReaction);
            ClearVisualState();
        }

        public void ConfigureForTests(StatusReactionVisualProfile profile)
        {
            // 测试在 inactive GameObject 上调用，等价于 Prefab 在 Awake 前完成 Inspector 序列化。
            _profile = profile;
        }

        private void OnReaction(ElementReactionEvent reactionEvent)
        {
            // 先过滤目标再分发类型，避免其他角色的全局事件误操作本角色 VFX。
            if (reactionEvent.TargetId != _carrierId)
                return;

            switch (reactionEvent.Reaction)
            {
                case ElementReactionId.Extinguish:
                    HandleExtinguish(in reactionEvent);
                    break;
                case ElementReactionId.IgniteGoo:
                    HandleContinuousSlot(_igniteGooSlot, reactionEvent.Phase);
                    break;
                case ElementReactionId.ToxicCombustion:
                    // Toxic 是 Wind-up + 瞬时爆发，Task 9/10 接入专用表现与对象池。
                    break;
            }
        }

        private void HandleExtinguish(in ElementReactionEvent reactionEvent)
        {
            switch (reactionEvent.Phase)
            {
                case ElementReactionPhase.Started:
                case ElementReactionPhase.Resumed:
                    // 事件携带的是值快照。Clamp 防止异常 Gameplay 数据扩散到粒子参数与测试接口。
                    _extinguishStrength = Mathf.Clamp01(reactionEvent.NormalizedStrength);
                    _extinguishExpectedDuration = Mathf.Max(0f, reactionEvent.ExpectedDuration);
                    _extinguishSlot?.Play();
                    break;
                case ElementReactionPhase.Paused:
                case ElementReactionPhase.Resolved:
                case ElementReactionPhase.Cancelled:
                    StopExtinguish();
                    break;
            }
        }

        private static void HandleContinuousSlot(
            ContinuousVisualSlot slot,
            ElementReactionPhase phase)
        {
            // Started/Resumed 对应播放，Paused/Resolved/Cancelled 对应停止，
            // 让生命周期映射集中在一个位置，新增持续反应时无需复制事件订阅代码。
            if (slot == null)
                return;

            switch (phase)
            {
                case ElementReactionPhase.Started:
                case ElementReactionPhase.Resumed:
                    slot.Play();
                    break;
                case ElementReactionPhase.Paused:
                case ElementReactionPhase.Resolved:
                case ElementReactionPhase.Cancelled:
                    slot.Stop();
                    break;
            }
        }

        private void StopExtinguish()
        {
            _extinguishSlot?.Stop();
            _extinguishStrength = 0f;
            _extinguishExpectedDuration = 0f;
        }

        private void ClearVisualState()
        {
            StopExtinguish();
            _igniteGooSlot?.Stop();
        }

        /// <summary>
        /// 一个预实例化的持续视觉槽。ParticleSystem 引用在 Awake 阶段缓存，
        /// 生命周期事件只遍历已有数组，不使用 GetComponentsInChildren 或 new。
        /// </summary>
        private sealed class ContinuousVisualSlot
        {
            private readonly GameObject _instance;
            private readonly ParticleSystem[] _particleSystems;

            private ContinuousVisualSlot(GameObject instance)
            {
                _instance = instance;

                // true 表示也收集当前 inactive 的子物体；缓存结果后，热路径不再遍历 Transform 层级。
                _particleSystems = instance.GetComponentsInChildren<ParticleSystem>(true);
                _instance.SetActive(false);
            }

            public bool IsActive => _instance != null && _instance.activeSelf;
            public int InstanceId => _instance != null ? _instance.GetInstanceID() : 0;

            public static ContinuousVisualSlot Create(
                GameObject prefab,
                Transform parent,
                string runtimeName)
            {
                if (prefab == null)
                    return null;

                // false 表示保持 Prefab 的 Local Transform，而不是保留原 World Transform。
                GameObject instance = Object.Instantiate(prefab, parent, false);
                instance.name = runtimeName;
                return new ContinuousVisualSlot(instance);
            }

            public void Play()
            {
                if (_instance == null)
                    return;

                _instance.SetActive(true);
                for (int i = 0; i < _particleSystems.Length; i++)
                {
                    ParticleSystem particles = _particleSystems[i];
                    if (particles != null && !particles.isPlaying)
                        particles.Play(true);
                }
            }

            public void Stop()
            {
                if (_instance == null)
                    return;

                for (int i = 0; i < _particleSystems.Length; i++)
                {
                    ParticleSystem particles = _particleSystems[i];
                    if (particles != null)
                        // StopEmittingAndClear 同时停止发射并清除残留粒子，下一次 Play 从干净状态开始。
                        particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                }

                _instance.SetActive(false);
            }
        }
    }
}
