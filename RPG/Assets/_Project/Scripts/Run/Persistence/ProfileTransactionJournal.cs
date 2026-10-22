using Game.Core;
namespace Game.Run
{
    /// <summary>复用Profile原子Replace作为事务日志，不另写两份必须同步的资产文件。</summary>
    public sealed class ProfileTransactionJournal
    {
        private readonly ProfileSaveRepository _repository;
        public ProfileTransactionJournal(ProfileSaveRepository repository) { _repository = repository; }
        public ProfilePersistenceResult Checkpoint(SinglePlayerProfile profile) => _repository.TrySave(profile);
        public ProfilePersistenceResult Recover(SinglePlayerProfile profile, out SinglePlayerProfile recovered)
        {
            recovered = profile; if (profile.PendingRaid == null) return new ProfilePersistenceResult(ProfilePersistenceStatus.Success);
            // 未开始地图保留完整出发前资产；已经开始仅恢复可靠资产记录，不重建旧箱庭。
            if (profile.PendingRaid.Phase == RaidPhase.Departing)
                return new RaidPreparationService().TryRollback(profile, _repository, out recovered);
            return new RaidSettlementService().TrySettle(profile, _repository,
                new RaidSettlementRequest(profile.PendingRaid.RaidId, RaidOutcome.Crash), out recovered, out _);
        }
    }
}
