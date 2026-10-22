using System;

namespace Game.Combat
{
    /// <summary>
    /// Fire+Poison 共用的纯跨 Tick Process。它只记录 Wind-up 和计划消费量，既不认识 Status，
    /// 也不认识 GPU Particle；调用方在完成时用最新可用 Poison clamp，避免消费过期 Snapshot。
    /// </summary>
    public struct ToxicCombustionProcess
    {
        private float _elapsed;
        private float _plannedPoison;
        private float _consumedPoison;
        public bool IsActive { get; private set; }
        public float PlannedPoison => _plannedPoison;
        public float ConsumedPoison => _consumedPoison;

        public bool TryStart(int fireGmu, int poisonGmu, in ToxicCombustionTuning tuning)
        {
            if (IsActive || fireGmu <= 0 || poisonGmu <= 0)
                return false;
            float fireIntensity = fireGmu * 100f / byte.MaxValue;
            float poisonIntensity = poisonGmu * 100f / byte.MaxValue;
            return TryStartInternal(fireIntensity, poisonIntensity, poisonGmu, in tuning);
        }

        /// <summary>Status Runtime 的单位本来就是 0..100 intensity，因此不做 byte/GMU 换算。</summary>
        public bool TryStartFromIntensity(
            float fireIntensity,
            float poisonIntensity,
            in ToxicCombustionTuning tuning)
        {
            return TryStartInternal(fireIntensity, poisonIntensity, poisonIntensity, in tuning);
        }

        private bool TryStartInternal(
            float fireIntensity,
            float poisonIntensity,
            float availablePoison,
            in ToxicCombustionTuning tuning)
        {
            if (IsActive
                || fireIntensity < Math.Max(0f, tuning.FireThreshold)
                || poisonIntensity < Math.Max(0f, tuning.PoisonThreshold))
                return false;
            _elapsed = 0f;
            _consumedPoison = 0f;
            _plannedPoison = Math.Min(Math.Max(0f, availablePoison), Math.Max(0f, tuning.MaxPoisonConsume));
            IsActive = _plannedPoison > 0f;
            return IsActive;
        }

        public bool Tick(
            float deltaTime,
            int currentPoisonGmu,
            in ToxicCombustionTuning tuning,
            out int consumedPoisonGmu,
            out float damage,
            out float radius)
        {
            consumedPoisonGmu = 0;
            damage = 0f;
            radius = 0f;
            if (!IsActive || deltaTime <= 0f) return false;
            _elapsed += deltaTime;
            if (_elapsed + 0.0001f < Math.Max(0.01f, tuning.WindUpSeconds)) return false;

            consumedPoisonGmu = Math.Min(
                Math.Max(0, currentPoisonGmu),
                (int)Math.Ceiling(_plannedPoison));
            IsActive = false;
            if (consumedPoisonGmu < Math.Max(0f, tuning.MinEffectiveConsume)) return true;
            damage = Math.Max(0f, tuning.BaseDamage)
                + consumedPoisonGmu * Math.Max(0f, tuning.DamagePerPoison);
            radius = Math.Max(0f, tuning.Radius);
            return true;
        }

        /// <summary>
        /// 角色 Status 沿用既有“Wind-up 内匀速消费”的手感；World Adapter 则调用上面的完成时消费 API。
        /// 两条 Carrier 路径仍共享同一个 elapsed/planned/consumed 状态与 Damage 公式。
        /// </summary>
        public bool TickProgressive(
            float deltaTime,
            float currentPoison,
            in ToxicCombustionTuning tuning,
            out float consumedThisTick,
            out bool effective,
            out float damage,
            out float radius)
        {
            consumedThisTick = 0f;
            effective = false;
            damage = 0f;
            radius = 0f;
            if (!IsActive || deltaTime <= 0f)
                return false;

            float duration = Math.Max(0.01f, tuning.WindUpSeconds);
            float remaining = Math.Max(0f, _plannedPoison - _consumedPoison);
            float rate = _plannedPoison / duration;
            consumedThisTick = Math.Min(Math.Min(Math.Max(0f, currentPoison), remaining), rate * deltaTime);
            _consumedPoison += consumedThisTick;
            _elapsed += deltaTime;
            if (_elapsed + 0.0001f < duration)
                return false;

            IsActive = false;
            effective = _consumedPoison >= Math.Max(0f, tuning.MinEffectiveConsume);
            if (effective)
            {
                damage = Math.Max(0f, tuning.BaseDamage)
                    + _consumedPoison * Math.Max(0f, tuning.DamagePerPoison);
                radius = Math.Max(0f, tuning.Radius);
            }
            return true;
        }

        public void Cancel()
        {
            IsActive = false;
            _elapsed = 0f;
            _plannedPoison = 0f;
            _consumedPoison = 0f;
        }
    }
}
