using System;
using System.Collections.Generic;
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
        [SerializeField, Tooltip("-1表示兼容旧资源，Reload取原CastCooldown；新配置可独立指定秒数。")]
        private float _baseReloadTime = -1;
        [SerializeField, Min(0)] private float _baseSpreadDegrees;
        [SerializeField] private bool _shuffle;
        [SerializeField,Tooltip("固定法术的稳定ContentId；不是物品实例，不占普通卡槽。最多4项。")]
        private string[] _alwaysCastContentIds=Array.Empty<string>();
        [SerializeField, Min(1)] private int _programCapacity = 4;
        [SerializeField, Min(1)] private int _baseDraws = 1;
        public bool TryFreeze(out WandStats stats)
        {
            stats = default;
            if (!WandRuntimeState.IsFinite(_maxMana) || _maxMana <= 0 || !WandRuntimeState.IsFinite(_manaRegenPerSecond)
                || _manaRegenPerSecond < 0 || !WandRuntimeState.IsFinite(_castCooldown) || _castCooldown < 0
                || _programCapacity < 1 || _baseDraws < 1) return false;
            float reload = _baseReloadTime == -1 ? _castCooldown : _baseReloadTime;
            stats = new WandStats(_maxMana, _manaRegenPerSecond, _castCooldown, _programCapacity, _baseDraws, reload, _baseSpreadDegrees, _shuffle,_alwaysCastContentIds);
            return stats.IsValid;
        }
    }
    public readonly struct WandStats
    {
        public readonly float MaxMana, ManaRegenPerSecond, CastCooldown, BaseCastDelay, BaseReloadTime, BaseSpreadDegrees;
        public readonly bool Shuffle;
        public readonly int ProgramCapacity, BaseDraws;
        private readonly IReadOnlyList<string> _alwaysCastContentIds;
        public IReadOnlyList<string> AlwaysCastContentIds=>_alwaysCastContentIds??Array.Empty<string>();
        public WandStats(float maxMana, float regen, float cooldown, int capacity, int draws)
            : this(maxMana, regen, cooldown, capacity, draws, cooldown, 0, false) { }
        public WandStats(float maxMana, float regen, float castDelay, int capacity, int draws, float reload, float spread, bool shuffle)
            :this(maxMana,regen,castDelay,capacity,draws,reload,spread,shuffle,null){}
        public WandStats(float maxMana, float regen, float castDelay, int capacity, int draws, float reload, float spread, bool shuffle,IReadOnlyList<string> alwaysCastContentIds)
        { MaxMana = maxMana; ManaRegenPerSecond = regen; CastCooldown = BaseCastDelay = castDelay;
            ProgramCapacity = capacity; BaseDraws = draws; BaseReloadTime = reload; BaseSpreadDegrees = spread; Shuffle = shuffle;
            var ids=new string[alwaysCastContentIds?.Count??0];for(int i=0;i<ids.Length;i++)ids[i]=alwaysCastContentIds[i];_alwaysCastContentIds=Array.AsReadOnly(ids); }
        public bool IsValid=>WandRuntimeState.IsFinite(MaxMana)&&MaxMana>0&&WandRuntimeState.IsFinite(ManaRegenPerSecond)&&ManaRegenPerSecond>=0
            &&WandRuntimeState.IsFinite(CastCooldown)&&CastCooldown>=0&&ProgramCapacity>=1&&ProgramCapacity<=32&&BaseDraws>=1&&BaseDraws<=ProgramCapacity
            &&WandRuntimeState.IsFinite(BaseReloadTime)&&BaseReloadTime>=0&&WandRuntimeState.IsFinite(BaseSpreadDegrees)&&BaseSpreadDegrees>=0&&ValidTraits();
        private bool ValidTraits(){if(AlwaysCastContentIds.Count>4)return false;for(int i=0;i<AlwaysCastContentIds.Count;i++)if(string.IsNullOrWhiteSpace(AlwaysCastContentIds[i]))return false;return true;}
        private bool SameTraits(in WandStats other){if(AlwaysCastContentIds.Count!=other.AlwaysCastContentIds.Count)return false;for(int i=0;i<AlwaysCastContentIds.Count;i++)if(AlwaysCastContentIds[i]!=other.AlwaysCastContentIds[i])return false;return true;}
        public bool SameAs(in WandStats other)=>MaxMana==other.MaxMana&&ManaRegenPerSecond==other.ManaRegenPerSecond
            &&CastCooldown==other.CastCooldown&&ProgramCapacity==other.ProgramCapacity&&BaseDraws==other.BaseDraws
            &&BaseReloadTime==other.BaseReloadTime&&BaseSpreadDegrees==other.BaseSpreadDegrees&&Shuffle==other.Shuffle&&SameTraits(other);
    }
}
