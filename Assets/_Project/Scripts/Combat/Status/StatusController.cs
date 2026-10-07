using UnityEngine;
using Game.Core;

namespace Game.Combat
{
    /// <summary>
    /// 角色状态系统的 Unity Adapter 与唯一运行时所有者。
    /// 它持有四种状态的实例数据，驱动自然衰减和 DoT，并把 Snapshot 交给纯
    /// ElementReactionRuntime 计算；随后统一应用 Frame、发布 EventBus 事件并执行范围伤害。
    /// Game.Character 只读取 MoveSpeedMultiplier，不需要理解元素反应内部规则。
    /// </summary>
    [DisallowMultipleComponent]
    public class StatusController : MonoBehaviour
    {
        // 当前实现依赖 StatusKind 连续映射到 0..3，从而用定长数组取代 Dictionary 的查找与分配成本。
        private const int StatusCount = 4;
        private static readonly string[] DefaultDisplayNames = { "Burning", "Wet", "Poisoned", "Sticky" };

        [SerializeField] private StatusDatabase _database;
        [SerializeField] private ElementReactionProfile _reactionProfile;

        // 数组只在组件构造阶段创建一次，Update 热路径不 new、不使用 LINQ。
        private readonly StatusInstance[] _instances = new StatusInstance[StatusCount];
        private readonly StatusDefinition[] _definitions = new StatusDefinition[StatusCount];
        private readonly GameObject[] _vfxInstances = new GameObject[StatusCount];
        private readonly int[] _lastPublishedPercent = new int[StatusCount];
        private readonly bool[] _lastPublishedActive = new bool[StatusCount];
        private IDamageable _damageable;
        private HealthComponent _health;
        private int _targetId;
        private ElementReactionRuntime _reactionRuntime;
        private AreaReactionDamageResolver _areaReactionDamageResolver;

        public float MoveSpeedMultiplier { get; private set; } = 1f;

        private void Awake()
        {
            // Unity 会在首帧前调用 Awake；所有热路径需要的组件、身份和纯 C# 对象都在此预缓存。
            _damageable = GetComponent<IDamageable>();
            _health = GetComponent<HealthComponent>();
            _targetId = gameObject.GetInstanceID();
            CacheDefinitions();
            _areaReactionDamageResolver = new AreaReactionDamageResolver();
            RebuildReactionRuntime();
            RecalculateMoveSpeedMultiplier();
        }

        private void OnEnable()
        {
            EventBus<DeathEvent>.Subscribe(OnDeath);
            EventBus<WorldActivitySourceEvent>.Publish(new WorldActivitySourceEvent
                { Source = transform, Kind = WorldActivitySourceKind.Unit, Enabled = true });
        }

        private void OnDisable()
        {
            EventBus<WorldActivitySourceEvent>.Publish(new WorldActivitySourceEvent
                { Source = transform, Kind = WorldActivitySourceKind.Unit, Enabled = false });
            // OnEnable/OnDisable 成对订阅，防止 EventBus 留下指向已禁用组件的 delegate。
            EventBus<DeathEvent>.Unsubscribe(OnDeath);
            CancelReactions();
        }

        private void Update()
        {
            Tick(Time.deltaTime);
        }

        public void ApplyStatus(StatusKind kind, float amount, int sourceId, byte sourceTeam)
        {
            ApplyStatusInternal(kind, amount, sourceId, sourceTeam, 0f);
        }

        /// <summary>
        /// 持续环境来源使用的显式入口。Hold 只暂停 NaturalDecay；Reaction 与 DoT 仍正常执行。
        /// 来源应传入“下一次补充间隔 + 少量 Grace”，使低频采样之间不会误判为已经离开来源。
        /// </summary>
        public void ApplySustainedStatus(
            StatusKind kind,
            float amount,
            int sourceId,
            byte sourceTeam,
            float naturalDecayHoldSeconds)
        {
            float safeHoldSeconds = float.IsNaN(naturalDecayHoldSeconds)
                || float.IsInfinity(naturalDecayHoldSeconds)
                    ? 0f
                    : Mathf.Max(0f, naturalDecayHoldSeconds);
            ApplyStatusInternal(kind, amount, sourceId, sourceTeam, safeHoldSeconds);
        }

        private void ApplyStatusInternal(
            StatusKind kind,
            float amount,
            int sourceId,
            byte sourceTeam,
            float naturalDecayHoldSeconds)
        {
            // Invulnerability 是 Gameplay Effect 的统一权限门：保护期既不能扣血，也不能提前堆积
            // Burning/Poison 等状态，避免解除保护后的第一帧由预埋 DoT 或 Reaction 突然结算。
            // 已存在的状态仍可自然衰减；其伤害 Tick 继续由 HealthComponent 的 Damage Gate 拒绝。
            if (amount <= 0f || (_health != null && _health.IsInvulnerable))
                return;

            int index = ToIndex(kind);
            StatusInstance instance = _instances[index];

            // StatusInstance 是 struct；从数组取出的是值副本，修改后必须写回数组。
            instance.Active = true;
            instance.Intensity = Mathf.Clamp(instance.Intensity + amount, 0f, 100f);
            instance.SourceId = sourceId;
            instance.SourceTeam = sourceTeam;
            // 刷新而非相加：多个持续来源不能通过重复调用制造无限长的离场保护。
            instance.NaturalDecayHoldRemaining = Mathf.Max(
                instance.NaturalDecayHoldRemaining,
                naturalDecayHoldSeconds);

            StatusDefinition definition = GetDefinition(kind);
            if (definition != null && definition.DealsDamage && instance.TickTimer <= 0f)
                instance.TickTimer = Mathf.Max(0.05f, definition.DamageInterval);

            _instances[index] = instance;
            if (_reactionRuntime != null)
            {
                // Apply 当帧只用 deltaTime=0 检查启动门槛并发布 Started，
                // 连续消耗留给后续 Update，避免一次 Apply 调用隐藏时间推进。
                _reactionRuntime.MarkDirty(new StatusSource(sourceId, sourceTeam));
                ElementStateSnapshot snapshot = BuildReactionSnapshot();
                ElementReactionFrame startFrame = _reactionRuntime.Tick(in snapshot, 0f);
                ApplyReactionFrame(in startFrame);
            }
            EnsureVfx(kind);
            RecalculateMoveSpeedMultiplier();
            PublishStatusChanged(kind);
        }

        public bool HasStatus(StatusKind kind)
        {
            return _instances[ToIndex(kind)].Active;
        }

        public float GetIntensity(StatusKind kind)
        {
            return _instances[ToIndex(kind)].Intensity;
        }

        public bool TryGetIntensity(StatusKind kind, out float intensity)
        {
            StatusInstance instance = _instances[ToIndex(kind)];
            intensity = instance.Intensity;
            return instance.Active;
        }

        public void TickForTests(float deltaTime)
        {
            Tick(deltaTime);
        }

        public void SetDefinitionsForTests(params StatusDefinition[] definitions)
        {
            for (int i = 0; i < _definitions.Length; i++)
                _definitions[i] = null;

            if (definitions == null)
                return;

            for (int i = 0; i < definitions.Length; i++)
            {
                StatusDefinition definition = definitions[i];
                if (definition != null)
                    _definitions[ToIndex(definition.Kind)] = definition;
            }

            RecalculateMoveSpeedMultiplier();
            RebuildReactionRuntime();
        }

        public void SetReactionProfileForTests(ElementReactionProfile profile)
        {
            // EditMode 中 AddComponent 后不保证已经走过 PlayMode 的 Awake；测试入口补齐同一身份缓存。
            if (_targetId == 0)
                _targetId = gameObject.GetInstanceID();
            _reactionProfile = profile;
            RebuildReactionRuntime();
        }

        private void Tick(float deltaTime)
        {
            if (deltaTime <= 0f)
                return;

            StatusMask suppressed = StatusMask.None;
            if (_reactionRuntime != null)
            {
                // 两阶段结构：Snapshot（输入）→ Runtime.Frame（纯输出）→ Apply（唯一写入点）。
                ElementStateSnapshot snapshot = BuildReactionSnapshot();
                ElementReactionFrame reactionFrame = _reactionRuntime.Tick(in snapshot, deltaTime);
                suppressed = reactionFrame.SuppressNaturalDecay;
                ApplyReactionFrame(in reactionFrame);
            }

            // 反应优先于自然衰减。正在被反应主动消耗/生成的通道会通过 mask 抑制本帧普通衰减，
            // 防止同一状态在一次 Tick 中被两套规则重复扣减。
            ApplyNaturalDecay(deltaTime, suppressed);
            TickDamageOverTime(deltaTime);
            FinalizeStatusesAndPublish();
            RecalculateMoveSpeedMultiplier();
        }

        private void ApplyNaturalDecay(float deltaTime, StatusMask suppressed)
        {
            for (int i = 0; i < StatusCount; i++)
            {
                StatusInstance instance = _instances[i];
                if (!instance.Active)
                    continue;

                // Hold 可能在本帧中途耗尽。只对 deltaTime 中未被 Hold 覆盖的剩余部分计算衰减，
                // 避免不同帧率下出现“整帧免除”或“整帧多扣”的结果差异。
                float heldTime = Mathf.Min(instance.NaturalDecayHoldRemaining, deltaTime);
                instance.NaturalDecayHoldRemaining = Mathf.Max(
                    0f,
                    instance.NaturalDecayHoldRemaining - heldTime);
                float decayDeltaTime = deltaTime - heldTime;

                // Reaction 的 Delta 已在本方法之前应用；SuppressNaturalDecay 仍拥有更高优先级。
                if (IsSuppressed((StatusKind)i, suppressed) || decayDeltaTime <= 0f)
                {
                    _instances[i] = instance;
                    continue;
                }

                StatusKind kind = (StatusKind)i;
                StatusDefinition definition = GetDefinition(kind);
                float decay = definition != null ? definition.NaturalDecayPerSecond : 0f;
                instance.Intensity = Mathf.Max(0f, instance.Intensity - decay * decayDeltaTime);
                _instances[i] = instance;
            }
        }

        private void TickDamageOverTime(float deltaTime)
        {
            for (int i = 0; i < StatusCount; i++)
            {
                StatusInstance instance = _instances[i];
                if (!instance.Active)
                    continue;

                StatusKind kind = (StatusKind)i;
                StatusDefinition definition = GetDefinition(kind);
                if (definition != null && definition.DealsDamage && _damageable != null && _damageable.IsAlive)
                {
                    instance.TickTimer -= deltaTime;
                    if (instance.TickTimer <= 0f)
                    {
                        instance.TickTimer += Mathf.Max(0.05f, definition.DamageInterval);

                        // DoT = BaseDamagePerTick * intensity / 100；强度越低，每跳伤害越低。
                        float amount = definition.BaseDamagePerTick * (instance.Intensity / 100f);
                        if (amount > 0f)
                        {
                            var request = new DamageRequest(
                                instance.SourceId,
                                instance.SourceTeam,
                                amount,
                                definition.DamageType,
                                transform.position,
                                Vector3.up,
                                definition.TriggerHitReaction);
                            _damageable.ReceiveHit(in request);
                        }
                    }
                }

                _instances[i] = instance;
            }
        }

        private void FinalizeStatusesAndPublish()
        {
            for (int i = 0; i < StatusCount; i++)
            {
                StatusInstance instance = _instances[i];
                StatusKind kind = (StatusKind)i;
                if (instance.Intensity <= 0f)
                {
                    instance.Active = false;
                    instance.TickTimer = 0f;
                    // 状态结束时一并清除持续来源计时，防止下次新激活继承旧 Hold。
                    instance.NaturalDecayHoldRemaining = 0f;
                    DestroyVfx(kind);
                }

                _instances[i] = instance;
                PublishStatusChangedIfNeeded(kind);
            }
        }

        private void ApplyReactionFrame(in ElementReactionFrame frame)
        {
            // 先统一应用四通道 Delta，再发布离散信号和执行命令；Runtime 从不持有 MonoBehaviour 引用。
            AddIntensityDelta(StatusKind.Burning, frame.FireDelta);
            AddIntensityDelta(StatusKind.Wet, frame.WaterDelta);
            AddIntensityDelta(StatusKind.Poisoned, frame.PoisonDelta);
            AddIntensityDelta(StatusKind.Sticky, frame.GooDelta);

            if (frame.SignalCount > 0)
                PublishReactionSignal(in frame.Signal0);
            if (frame.SignalCount > 1)
                PublishReactionSignal(in frame.Signal1);
            if (frame.SignalCount > 2)
                PublishReactionSignal(in frame.Signal2);

            if (frame.HasAreaDamage)
            {
                // 正常由 Awake 预创建；EditMode 测试/工具若先调用公开 API，再做一次低频兜底。
                if (_areaReactionDamageResolver == null)
                    _areaReactionDamageResolver = new AreaReactionDamageResolver();
                _areaReactionDamageResolver.Resolve(in frame.AreaDamage, transform.position);
            }
        }

        private ElementStateSnapshot BuildReactionSnapshot()
        {
            // Snapshot 复制当前强度与各自来源，使本次计算不会在中途观察到可变数组的新值。
            StatusInstance fire = _instances[ToIndex(StatusKind.Burning)];
            StatusInstance water = _instances[ToIndex(StatusKind.Wet)];
            StatusInstance poison = _instances[ToIndex(StatusKind.Poisoned)];
            StatusInstance goo = _instances[ToIndex(StatusKind.Sticky)];
            return new ElementStateSnapshot(
                fire.Intensity, water.Intensity, poison.Intensity, goo.Intensity,
                ToSource(in fire), ToSource(in water), ToSource(in poison), ToSource(in goo));
        }

        private void RebuildReactionRuntime()
        {
            // Profile 或 Definitions 改变时丢弃旧 Process，再用新快照创建 Runtime；
            // 不在运行中的 Process 内热切参数，避免反应前后半程使用不同规则。
            CancelReactions();
            if (_reactionProfile == null)
            {
                _reactionRuntime = null;
                return;
            }

            float poisonMultiplier = GetWetCleanseMultiplier(StatusKind.Poisoned);
            float gooMultiplier = GetWetCleanseMultiplier(StatusKind.Sticky);
            ElementReactionTuningSnapshot tuning = _reactionProfile.CreateSnapshot(poisonMultiplier, gooMultiplier);
            _reactionRuntime = new ElementReactionRuntime(in tuning);
        }

        private void CancelReactions()
        {
            if (_reactionRuntime == null)
                return;

            ElementReactionFrame frame = _reactionRuntime.CancelAll();
            ApplyReactionFrame(in frame);
        }

        private void OnDeath(DeathEvent e)
        {
            if (e.TargetId == _targetId)
            {
                CancelReactions();
                EventBus<WorldActivitySourceEvent>.Publish(new WorldActivitySourceEvent
                    { Source = transform, Kind = WorldActivitySourceKind.Unit, Enabled = false });
            }
        }

        private void AddIntensityDelta(StatusKind kind, float delta)
        {
            if (Mathf.Abs(delta) <= 0f)
                return;

            int index = ToIndex(kind);
            StatusInstance instance = _instances[index];
            instance.Intensity = Mathf.Clamp(instance.Intensity + delta, 0f, 100f);
            instance.Active = instance.Intensity > 0f;
            _instances[index] = instance;
        }

        private float GetWetCleanseMultiplier(StatusKind kind)
        {
            StatusDefinition definition = GetDefinition(kind);
            return definition != null && definition.WetCleanseable
                ? Mathf.Max(0f, definition.WetCleanseMultiplier)
                : 0f;
        }

        private void PublishReactionSignal(in ReactionSignal signal)
        {
            // 正常 PlayMode 由 Awake 缓存；EditMode 工具/测试可能在 Awake 前直接调用公开 API。
            if (_targetId == 0)
                _targetId = gameObject.GetInstanceID();

            EventBus<ElementReactionEvent>.Publish(new ElementReactionEvent
            {
                TargetId = _targetId,
                Reaction = signal.Reaction,
                Phase = signal.Phase,
                WorldPosition = transform.position,
                NormalizedStrength = signal.NormalizedStrength,
                ExpectedDuration = signal.ExpectedDuration,
            });
        }

        private static StatusSource ToSource(in StatusInstance instance)
        {
            return instance.Active
                ? new StatusSource(instance.SourceId, instance.SourceTeam)
                : default;
        }

        private static bool IsSuppressed(StatusKind kind, StatusMask mask)
        {
            // StatusKind 的整数值映射到相同位置的 bit：bit = 1 << kind。
            StatusMask bit = (StatusMask)(1 << (int)kind);
            return (mask & bit) != 0;
        }

        private void CacheDefinitions()
        {
            for (int i = 0; i < StatusCount; i++)
            {
                StatusKind kind = (StatusKind)i;
                if (_database != null && _database.TryGet(kind, out StatusDefinition definition))
                    _definitions[i] = definition;
            }
        }

        private StatusDefinition GetDefinition(StatusKind kind)
        {
            return _definitions[ToIndex(kind)];
        }

        private void EnsureVfx(StatusKind kind)
        {
            // VFX 只在状态首次激活时 Instantiate；不会在每个 Tick 重复创建。
            int index = ToIndex(kind);
            if (_vfxInstances[index] != null)
                return;

            StatusDefinition definition = _definitions[index];
            if (definition == null || definition.VfxPrefab == null)
                return;

            _vfxInstances[index] = Instantiate(definition.VfxPrefab, transform.position, transform.rotation, transform);
        }

        private void DestroyVfx(StatusKind kind)
        {
            int index = ToIndex(kind);
            if (_vfxInstances[index] == null)
                return;

            Destroy(_vfxInstances[index]);
            _vfxInstances[index] = null;
        }

        private void RecalculateMoveSpeedMultiplier()
        {
            float multiplier = 1f;

            for (int i = 0; i < StatusCount; i++)
            {
                StatusInstance instance = _instances[i];
                if (!instance.Active)
                    continue;

                StatusDefinition definition = _definitions[i];
                if (definition == null || !definition.AffectsMoveSpeed)
                    continue;

                // 环境持续来源使用 NaturalDecay Hold 作为低频 Exposure 的 Contact Lease。
                // Sticky 在 Lease 有效时必须稳定保持完整 40% 控制，避免角色踩在稀薄边缘时忽快忽慢；
                // 离场后 Lease 到期，再回到按残留强度线性恢复的普通公式。
                bool hasSustainedStickyContact = i == (int)StatusKind.Sticky
                    && instance.NaturalDecayHoldRemaining > 0f;
                float slowRatio = hasSustainedStickyContact
                    ? definition.MaxMoveSpeedSlowRatio
                    : definition.MaxMoveSpeedSlowRatio * (instance.Intensity / 100f);

                // 多个减速采用乘法叠加：final = Π(1 - slowRatio_i)。
                // 相比直接相加，这不会轻易得到负速度，并让每个新增效果作用于当前速度。
                multiplier *= Mathf.Clamp01(1f - slowRatio);
            }

            MoveSpeedMultiplier = multiplier;
        }

        private void PublishStatusChanged(StatusKind kind)
        {
            int index = ToIndex(kind);
            StatusDefinition definition = GetDefinition(kind);
            StatusInstance instance = _instances[index];
            int roundedPercent = Mathf.RoundToInt(instance.Intensity);
            _lastPublishedPercent[index] = roundedPercent;
            _lastPublishedActive[index] = instance.Active;

            EventBus<StatusChangedEvent>.Publish(new StatusChangedEvent
            {
                TargetId = _targetId,
                Kind = kind,
                Intensity = instance.Intensity,
                IsActive = instance.Active,
                Icon = definition != null ? definition.Icon : null,
                DisplayName = definition != null && !string.IsNullOrEmpty(definition.DisplayName)
                    ? definition.DisplayName
                    : DefaultDisplayNames[index],
            });
        }

        private void PublishStatusChangedIfNeeded(StatusKind kind)
        {
            int index = ToIndex(kind);
            StatusInstance instance = _instances[index];
            int roundedPercent = Mathf.RoundToInt(instance.Intensity);

            // UI 只显示整数百分比，所以相同整数区间内不发布高频事件，减少无意义刷新。
            if (_lastPublishedActive[index] == instance.Active && _lastPublishedPercent[index] == roundedPercent)
                return;

            PublishStatusChanged(kind);
        }

        private static int ToIndex(StatusKind kind)
        {
            return (int)kind;
        }
    }
}
