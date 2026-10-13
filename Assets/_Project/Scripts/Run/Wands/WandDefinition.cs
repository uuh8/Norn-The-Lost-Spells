using System;
using UnityEngine;

namespace Game.Run
{
    /// <summary>共享固定属性；当前 Mana/冷却只属于具体实例。目录初始化冻结，避免 Play Mode 编辑污染资源边界。</summary>
    [CreateAssetMenu(menuName = "Game/Run/Wand Definition")]
    public sealed class WandDefinition : ScriptableObject
    {
        [SerializeField, Min(1f)] private float _maxMana = 100f;
        [SerializeField, Min(0f)] private float _manaRegenPerSecond = 10f;
        [SerializeField, Min(0f)] private float _castCooldown = .8f;
        [SerializeField, Min(1)] private int _programCapacity = 4;
        [SerializeField, Min(1)] private int _baseDraws = 1;
        public bool TryFreeze(out WandStats stats)
        {
            stats = default;
            if (!WandRuntimeState.IsFinite(_maxMana) || _maxMana <= 0 || !WandRuntimeState.IsFinite(_manaRegenPerSecond)
                || _manaRegenPerSecond < 0 || !WandRuntimeState.IsFinite(_castCooldown) || _castCooldown < 0
                || _programCapacity < 1 || _baseDraws < 1) return false;
            stats = new WandStats(_maxMana, _manaRegenPerSecond, _castCooldown, _programCapacity, _baseDraws); return true;
        }
    }
    public readonly struct WandStats
    {
        public readonly float MaxMana, ManaRegenPerSecond, CastCooldown;
        public readonly int ProgramCapacity, BaseDraws;
        public WandStats(float maxMana, float regen, float cooldown, int capacity, int draws)
        { MaxMana = maxMana; ManaRegenPerSecond = regen; CastCooldown = cooldown; ProgramCapacity = capacity; BaseDraws = draws; }
    }
}
