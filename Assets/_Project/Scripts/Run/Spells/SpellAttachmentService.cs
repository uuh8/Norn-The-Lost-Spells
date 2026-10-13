using System;
using System.Collections.Generic;
using Game.Core;
using Game.Skills;

namespace Game.Run
{
    /// <summary>配置只编辑具体卡，不扣料；来源失效保留设置，实际取料在施法提交时裁决。</summary>
    public sealed class SpellAttachmentService
    {
        private readonly SinglePlayerProfile _profile;
        private readonly string _inventoryId;
        private readonly Dictionary<string, ulong> _requests = new Dictionary<string, ulong>(StringComparer.Ordinal);
        public SpellAttachmentService(SinglePlayerProfile profile, string inventoryId) { _profile = profile; _inventoryId = inventoryId; }
        public SpellAttachmentResultEvent Execute(in SpellAttachmentRequestedEvent request)
        {
            var result = new SpellAttachmentResultEvent { InventoryId = _inventoryId, SpellId = request.SpellId,
                CallerId = request.CallerId, RequestId = request.RequestId, Reason = InventoryFailureReason.InvalidState };
            if (request.InventoryId != _inventoryId || string.IsNullOrWhiteSpace(request.CallerId) || request.RequestId == 0) return result;
            if (_requests.TryGetValue(request.CallerId, out ulong last) && request.RequestId <= last) return result;
            _requests[request.CallerId] = request.RequestId;
            if (request.ExpectedVersion != _profile.Items.Version) { result.Reason = InventoryFailureReason.StaleVersion; return result; }
            if (!_profile.Items.TryGet(request.SpellId, out var card) || !_profile.Items.Catalog.TryGet(card.ContentId, out var definition)
                || definition.Kind != ItemKind.Spell || definition.SpellDefinition == null
                || (definition.SpellDefinition.Kind != SpellKind.Emit && definition.SpellDefinition.Kind != SpellKind.StaticProjectile)) return result;
            SpellAttachmentState attachment = null;
            if (request.Enabled)
            {
                if (request.Dose == 0 || !_profile.Items.TryGet(request.ContainerId, out var source) || source.Composition == null) return result;
                attachment = new SpellAttachmentState(request.ContainerId, request.Dose);
            }
            _profile.Items.TryGetLocation(card.InstanceId, out var location);
            var changed = new RuntimeItemState(card.InstanceId, card.ContentId, card.RemainingCharges, card.HasUnlimitedCharges,
                card.StackStateKey, card.Composition, card.Material, attachment);
            var edit = ItemStackOps.TryChangeState(_profile.Items, changed, location, request.ExpectedVersion);
            result.Success = edit.Success; result.Reason = edit.Reason; return result;
        }
    }
}
