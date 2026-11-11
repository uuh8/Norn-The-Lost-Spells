using System;
using Game.Core;

namespace Game.Run
{
    /// <summary>
    /// 个人ESC的持久资产捕获边界。只Fence新玩家意图，不改变World写门，
    /// 已接受施法、G飞瓶、Impact与已扣量输出仍沿原Owner推进，直至自然稳定。
    /// </summary>
    public sealed partial class OnlineOwnedWorkspace
    {
        private string _memberExitCommandId;
        public bool CanAcceptNewPlayerIntents => !_disposed && _memberExitCommandId == null;
        public bool HasMemberExitFence => !_disposed && _memberExitCommandId != null;

        public bool TryBeginMemberExit(string commandId)
        {
            if (_disposed || !MemberExitId(commandId)) return false;
            if (_memberExitCommandId != null) return _memberExitCommandId == commandId;
            _memberExitCommandId = commandId;
            return true;
        }

        /// <summary>
        /// 仅供DS组合层在专用ConnectionSuperseded＋认证收据404后调用。
        /// 这里不判断HTTP、不解除Fence；未知正文不能走此接口，旧ID也不能重新读出资产候选。
        /// </summary>
        public bool TryReplaceRejectedMemberExit(string rejectedCommand, string nextCommand)
        {
            if (_disposed || !MemberExitId(rejectedCommand) || !MemberExitId(nextCommand) || rejectedCommand == nextCommand
                || _memberExitCommandId != rejectedCommand) return false;
            _memberExitCommandId = nextCommand; return true;
        }

        /// <summary>
        /// 不调用全Run TryCaptureCheckpoint/退役Journal：其他玩家的预约不能成为本人退出屏障。
        /// 这里只采当前本人Owned3；实例/epoch/持久版本由上层可信入局身份补齐。
        /// 失败保留原现场，不Dispose/EndAll/清计数来制造稳定状态。
        /// </summary>
        public bool TryCaptureMemberExitAssets(string commandId, out OnlineOwnedAssetSnapshot assets)
        {
            assets = null;
            if (_disposed || _memberExitCommandId == null || commandId != _memberExitCommandId
                || _profile.Items.IsResourceBusy || _casts.IsPending || _casts.ActiveReservations != 0
                || _casts.MaterialOutputs.ActiveOutputs != 0 || _profile.PendingMaterialOutputs != 0
                || _containerUses != null && _containerUses.PendingFlights != 0
                || _profile.Items.Count > OnlineOwnedAssetSnapshot.MaximumItems
                || _profile.Currency < 0 || _currencyVersion == 0) return false;
            _wands.Synchronize();
            var items = new OnlineOwnedItemSnapshot[_profile.Items.Count];
            int index = 0;
            foreach (var entry in _profile.Items)
            {
                // 不把仍属在途/地面/外Owner的实例抹掉后假称“完整背包”；必须全部是已批准携带闭包。
                if (!TryProjectOwnerItem(entry, out bool carried) || !carried
                    || !_itemRevisions.TryGetValue(entry.Item.InstanceId, out uint revision) || revision == 0) return false;
                items[index++] = CopyGroundItem(entry.Item, entry.Location, revision, _profile.Items.Catalog);
            }
            assets = new OnlineOwnedAssetSnapshot
            {
                SchemaVersion = OnlineOwnedAssetSnapshot.CurrentSchemaVersion,
                RunId = RunId, PlayerId = PlayerId, ActorId = ActorId, InventoryId = InventoryId,
                InventoryVersion = InventoryVersion, GameplayConfigHash = _gameplayHash,
                Gold = _profile.Currency, CurrencyVersion = _currencyVersion,
                SelectedWandInstanceId = _wands.ReadOnly.SelectedWandId ?? string.Empty, Items = items
            };
            return true;
        }

        /// <summary>
        /// 同一本人稳定边界复制资产和两份原累计损耗。只调用本人容器/供料的只读计数，
        /// 不借用全Run Journal捕获，所以队友的准备中预留不会阻塞个人退出，也不会丢失历史损耗。
        /// </summary>
        public bool TryCaptureMemberExitFacts(string commandId, out OnlineOwnedAssetSnapshot assets, out OnlineExitedMemberLossSnapshot losses)
        {
            assets = null; losses = null;
            if (!TryCaptureMemberExitAssets(commandId, out var owned)
                || !_casts.MaterialOutputs.TryCaptureCheckpoint(out var outputs, out var spellLost)
                || outputs == null || outputs.Length != 0) return false;
            OnlineOwnedMaterialAmountSnapshot[] containerLost = Array.Empty<OnlineOwnedMaterialAmountSnapshot>();
            if (_containerUses != null)
            {
                if (!_containerUses.TryCaptureCheckpoint(out var transits, out containerLost)
                    || transits == null || transits.Length != 0) return false;
            }
            var current = new OnlineExitedMemberLossSnapshot { RunId = RunId, PlayerId = PlayerId, ActorId = ActorId,
                ContainerLost = containerLost, SpellMaterialLost = spellLost };
            if (!OnlineExitedMemberLossSnapshot.TryCopy(current, out losses)) return false;
            assets = owned; return true;
        }

        private static bool MemberExitId(string value)
        {
            if (value == null || value.Length != 32) return false;
            for (int i = 0; i < value.Length; i++)
                if (!(value[i] >= '0' && value[i] <= '9') && !(value[i] >= 'a' && value[i] <= 'f')) return false;
            return true;
        }
    }
}
