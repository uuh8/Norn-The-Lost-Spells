using System;
using Game.Core;

namespace Game.Run
{
    public sealed partial class OnlineOwnedWorkspace
    {
        private bool _researchOpen;
        private uint _researchSessionVersion;
        private Func<bool> _homeResearchAccess;
        public bool ResearchOpen => !_disposed && _researchOpen;
        public bool ResearchPaused => ResearchOpen;
        public uint ResearchSessionVersion => _researchSessionVersion;

        /// <summary>仅可信Home装配绑定动态空间/活人/连接门。研究窗自身的输入捕获不能撤销此资格。</summary>
        public bool ConfigureHomeResearchAccess(Func<bool> canResearch)
        {
            if (_disposed || !_isPeacefulPractice || canResearch == null) return false;
            if (_homeResearchAccess != null) return _homeResearchAccess == canResearch;
            _homeResearchAccess = canResearch; return true;
        }
        public void ForceCloseResearch()
        {
            if (!_researchOpen) return;
            _researchOpen = false;
            if (_researchSessionVersion != uint.MaxValue) _researchSessionVersion++;
        }
        private static bool IsResearchCommand(OnlineInventoryCommandKind kind) => (byte)kind == 16 || (byte)kind == 17;
        private static bool IsResearchStructuralCommand(OnlineInventoryCommandKind kind)
            => kind == OnlineInventoryCommandKind.PlaceCard || kind == OnlineInventoryCommandKind.MoveOwned
                || kind == OnlineInventoryCommandKind.EquipWand || kind == OnlineInventoryCommandKind.ConfigureSpellAttachment
                || kind == OnlineInventoryCommandKind.SelectWand || kind == OnlineInventoryCommandKind.SelectTool || kind == OnlineInventoryCommandKind.BindTool;
        private bool RequiresResearchEditAccess(in OnlineInventoryCommandIntent intent)
            => intent.Kind == OnlineInventoryCommandKind.PlaceCard || intent.Kind == OnlineInventoryCommandKind.ConfigureSpellAttachment
                || intent.Kind == OnlineInventoryCommandKind.MoveOwned && (intent.TargetLocationKind == (int)ItemLocationKind.WandSlot
                    || _profile.Items.TryGetLocation(intent.ItemId, out var source) && source.Kind == ItemLocationKind.WandSlot);
        private OnlineInventoryCommandFailure ApplyResearchSessionCommand(in OnlineInventoryCommandIntent intent)
        {
            if (!_isPeacefulPractice || !NeutralResearch(intent)) return OnlineInventoryCommandFailure.InvalidInput;
            bool begin = (byte)intent.Kind == 16;
            if (intent.ExpectedInventoryVersion == 0 || intent.ExpectedInventoryVersion > InventoryVersion
                || begin && intent.ExpectedInventoryVersion != InventoryVersion) return OnlineInventoryCommandFailure.StaleVersion;
            // 关窗接受旧水位；编辑期间背包版本已经改变也不能因此永远关不掉。原ledger保证同ID精确重放。
            if (!begin) { ForceCloseResearch(); return OnlineInventoryCommandFailure.None; }
            if (_homeResearchAccess == null || !_homeResearchAccess()) return OnlineInventoryCommandFailure.NotOwned;
            if (_profile.Items.IsResourceBusy || _casts.IsPending || _casts.ActiveReservations != 0) return OnlineInventoryCommandFailure.Busy;
            if (_researchOpen) return OnlineInventoryCommandFailure.None;
            if (_researchSessionVersion == uint.MaxValue) return OnlineInventoryCommandFailure.Rejected;
            _researchOpen = true; _researchSessionVersion++; return OnlineInventoryCommandFailure.None;
        }
        private static bool NeutralResearch(in OnlineInventoryCommandIntent i)
            => string.IsNullOrEmpty(i.ItemId) && string.IsNullOrEmpty(i.SourceWandId) && string.IsNullOrEmpty(i.TargetWandId)
                && i.SourceProgramRevision == 0 && i.TargetProgramRevision == 0 && i.TargetSlot == 0 && !i.ToSpellStorage
                && i.TransferBudget == 0 && Zero(i.AimOrigin) && Zero(i.AimDirection) && string.IsNullOrEmpty(i.WorldOwnerId)
                && string.IsNullOrEmpty(i.TargetBackpackId) && i.GridX == 0 && i.GridY == 0 && i.QuarterTurns == 0 && !i.AutoPlace
                && string.IsNullOrEmpty(i.AttachmentContainerId) && i.AttachmentDose == 0 && !i.AttachmentEnabled
                && i.TargetLocationKind == 0 && !i.WholeStack;
        public bool TryCapturePracticeContinuity(out OnlineWandResearchStateSnapshot snapshot, out string error)
        {
            snapshot = null; error = null;
            if (!_isPeacefulPractice || !HasMemberExitFence || ResearchOpen
                || !TryCaptureMemberExitAssets(_memberExitCommandId, out _) || !TryCaptureResearchState(out snapshot))
                return OnlineWandResearchStateCodec.Fail("Home连续性须在本人可靠退出Fence及原drain稳定后捕获。", out error);
            return true;
        }
    }
}
