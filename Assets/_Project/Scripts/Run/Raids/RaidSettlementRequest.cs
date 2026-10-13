using Game.Core;
namespace Game.Run
{
    /// <summary>纯结果意图，以RaidId限定本次资产范围；不包含可发奖的场景对象。</summary>
    public readonly struct RaidSettlementRequest
    {
        public readonly string RaidId;
        public readonly RaidOutcome Outcome;
        public readonly ulong CollectedGmu;
        public readonly int DefeatedEnemies;
        public RaidSettlementRequest(string raidId, RaidOutcome outcome, ulong collectedGmu = 0, int defeatedEnemies = 0)
        { RaidId = raidId; Outcome = outcome; CollectedGmu = collectedGmu; DefeatedEnemies = defeatedEnemies; }
    }
}
