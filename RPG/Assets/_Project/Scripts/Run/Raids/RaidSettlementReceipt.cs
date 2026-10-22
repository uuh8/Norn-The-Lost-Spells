using System;
using Game.Core;
namespace Game.Run
{
    /// <summary>已经可靠提交的结果记录；用于重复通知返回同一结果，不重复增减库存。</summary>
    [Serializable]
    public sealed class RaidSettlementReceipt
    { public string RaidId; public RaidOutcome Outcome; public int Returned, Lost; public long Generation; }
}
