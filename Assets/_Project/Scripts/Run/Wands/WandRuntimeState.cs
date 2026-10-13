using System;

namespace Game.Run
{
    /// <summary>会话持有的单杖资源。失败先返回，成功才一起提交；切换 UI 不参与资源写入。</summary>
    public sealed class WandRuntimeState
    {
        public string InstanceId { get; }
        public float MaxMana { get; }
        public float ManaRegenPerSecond { get; }
        public float CastCooldown { get; }
        public float CurrentMana { get; private set; }
        public float CooldownRemaining { get; private set; }
        public WandRuntimeState(string id, float maxMana, float regen, float cooldown)
        {
            if (string.IsNullOrWhiteSpace(id) || !IsFinite(maxMana) || maxMana <= 0 || !IsFinite(regen) || regen < 0
                || !IsFinite(cooldown) || cooldown < 0) throw new ArgumentException("法杖资源参数非法。");
            InstanceId = id; MaxMana = maxMana; ManaRegenPerSecond = regen; CastCooldown = cooldown; CurrentMana = maxMana;
        }
        public static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        public bool TrySpend(float cost, bool root)
        {
            if (!IsFinite(cost) || cost < 0 || cost > CurrentMana || (root && CooldownRemaining > 0)) return false;
            CurrentMana -= cost;
            if (root) CooldownRemaining = CastCooldown;
            return true;
        }
        public void Tick(float dt)
        {
            if (!IsFinite(dt) || dt <= 0) return;
            CurrentMana = Math.Min(MaxMana, CurrentMana + ManaRegenPerSecond * dt);
            CooldownRemaining = Math.Max(0, CooldownRemaining - dt);
        }
    }
}
