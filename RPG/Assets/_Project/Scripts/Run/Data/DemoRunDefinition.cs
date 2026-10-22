using System;
using Game.Skills;
using UnityEngine;

namespace Game.Run
{
    /// <summary>
    /// 单个 Encounter 的可编辑元数据。
    /// Enemy、Trigger、Gate 都是 Scene Object，不能放进可跨 Scene 复用的 ScriptableObject；
    /// 这里仅保存流程顺序、UI 文案与确定性奖励等 Authoring Data。
    /// </summary>
    [Serializable]
    public sealed class DemoEncounterDefinition
    {
        [SerializeField]
        [Tooltip("显示在 HUD 上的区域名称，例如：元素实验室。")]
        private string _displayName = "Encounter";

        [SerializeField, TextArea(2, 4)]
        [Tooltip("显示给玩家的本区目标说明，不承担胜负判断。")]
        private string _objectiveText = "击败区域内所有敌人";

        [SerializeField]
        [Tooltip("本区清理后可授予的确定性法术奖励；允许为空，例如最终区域无需再发奖励。")]
        private SpellDefinition _rewardSpell;

        public string DisplayName => _displayName;

        public string ObjectiveText => _objectiveText;

        public SpellDefinition RewardSpell => _rewardSpell;
    }

    /// <summary>
    /// 一局 Demo 的数据驱动编排入口。
    /// 数组顺序就是 Encounter 顺序；运行时流程由 RunProgressTracker 裁定，
    /// 本资产本身不保存“打到第几关”等会变化的 Run State。
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Run/Demo Run Definition", fileName = "DemoRunDefinition")]
    public sealed class DemoRunDefinition : ScriptableObject
    {
        [SerializeField]
        [Tooltip("最后一个 Encounter 清场后直接结算，或继续等待玩家通过 Stage Exit Portal。")]
        private RunCompletionMode _completionMode = RunCompletionMode.TerminalVictory;

        [SerializeField]
        [Tooltip("按游玩顺序配置 Encounter。P7 Demo 计划为三个普通区域加一个最终精英区域。")]
        private DemoEncounterDefinition[] _encounters = Array.Empty<DemoEncounterDefinition>();

        public int EncounterCount => _encounters?.Length ?? 0;

        public RunCompletionMode CompletionMode => _completionMode;

        public DemoEncounterDefinition GetEncounter(int index)
        {
            if (_encounters == null)
            {
                throw new InvalidOperationException("DemoRunDefinition has no encounter array.");
            }

            if ((uint)index >= (uint)_encounters.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "Encounter index is outside the run definition.");
            }

            DemoEncounterDefinition encounter = _encounters[index];
            if (encounter == null)
            {
                throw new InvalidOperationException($"Encounter definition at index {index} is null.");
            }

            return encounter;
        }
    }
}
