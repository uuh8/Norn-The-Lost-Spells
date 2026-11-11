using Game.Core;

namespace Game.Character
{
    public sealed partial class OnlineGroundItemAuthority
    {
        // 仅暴露Core值合同，Multiplayer组合层不能拿到Run.Store或持久资产写口。
        public bool TryRestorePractice(OnlineGroundInventorySnapshot source, OnlineWandResearchStateSnapshot continuity, out string error)
            => _world.TryRestorePractice(source, continuity, out error);
        public bool TryCapturePracticeContinuity(OnlineWandResearchStateSnapshot target, out string error)
            => _world.TryCapturePracticeContinuity(target, out error);
        public void CompletePracticeHandoff() => _world.CompletePracticeHandoff();
    }
}
