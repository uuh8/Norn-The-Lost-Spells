using Game.Skills;
namespace Game.Run
{
    /// <summary>研究台一次捕获的只读输入；不持有库存写接口。</summary>
    public sealed class WandResearchSnapshot
    {
        public WandExecutionSnapshot Execution { get; }
        public SpellExecutionInputs Inputs { get; }
        public SpellProgram Library { get; }
        public bool AssumesRaidEntry { get; }
        internal WandResearchSnapshot(in WandExecutionSnapshot execution, in SpellExecutionInputs inputs, SpellProgram library, bool entry)
        { Execution = execution; Inputs = inputs; Library = library; AssumesRaidEntry = entry; }
    }
}
