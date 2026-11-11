using Game.NetworkProtocol;
using Norn.Business.Infrastructure;

namespace Norn.Business.Features.Runs;

/// <summary>
/// 完整Coin事实的单调门，由正式完整检查点规则调用。它不抽奖、不加Wallet、不选择退出规则；
/// 只保护DS已经记录的一次身份/金额/死位与单赢家墓碑，防止后续保存使旧币重新可领。
/// </summary>
internal static class RunCheckpointCoinRules
{
    internal static void Require(OnlineRunAssetCheckpoint value, OnlineRunAssetCheckpoint? previous)
    {
        if (value.SchemaVersion != OnlineRunAssetCheckpoint.CompleteSchemaVersion) return;
        if (!OnlineRunAssetCheckpoint.HasValidCoinFacts(value.Coins)) throw Failure();
        // 历史1/2/3没有Coin分区，不能从原成员金币猜造或回填历史掉落。
        if (previous == null || previous.SchemaVersion != OnlineRunAssetCheckpoint.CompleteSchemaVersion) return;
        if (!OnlineRunAssetCheckpoint.HasValidCoinFacts(previous.Coins)) throw Failure();
        foreach (var old in previous.Coins)
        {
            OnlineRunAssetCheckpointCoin? current = null;
            foreach (var coin in value.Coins) if (coin.Id == old.Id) { current = coin; break; }
            if (current == null || current.Amount != old.Amount || current.Position.X != old.Position.X
                || current.Position.Y != old.Position.Y || current.Position.Z != old.Position.Z
                || old.Collected && (!current.Collected || current.CollectorActorId != old.CollectorActorId)) throw Failure();
        }
    }
    private static OnlineFailure Failure() => new(409, "RunCheckpointCoinInvalid");
}
