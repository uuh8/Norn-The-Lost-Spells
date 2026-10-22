using UnityEngine;

namespace Game.Skills
{
    /// <summary>隔离样例的Authoring Data；不是玩家拥有的法杖，也不授权正式库存变更。</summary>
    [CreateAssetMenu(menuName = "Norn/Spells/V2 Sandbox Sample")]
    public sealed class SpellProgramSandboxDefinition : ScriptableObject
    {
        public string Title = "独立规则样例";
        [TextArea] public string LearningGoal;
        public SpellDefinition[] Spells;
        [Min(1)] public int BaseDraws = 1;
        [Min(0)] public float InitialMana = 80, MaxMana = 100;
        [Min(0)] public float CastDelay = .15f, ReloadTime = .7f, ManaRegenPerSecond;
        public bool Shuffle;
        [Min(0)] public int StartAfterRoots;
        public uint Seed = 1;
    }
}
