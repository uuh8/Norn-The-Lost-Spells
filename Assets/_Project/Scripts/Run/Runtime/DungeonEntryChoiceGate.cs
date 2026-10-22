using System;
using Game.Core;
using UnityEngine;

namespace Game.Run
{
    /// <summary>门选择的局部状态；不依赖UI或网络，保存/加载仍由门的原单机回调裁决。</summary>
    public sealed class DungeonEntryChoiceGate
    {
        private readonly int _portalId;
        private readonly string _destination, _label;
        private readonly Func<Collider, bool> _eligible, _enter;
        private readonly Func<bool> _acquirePause;
        private readonly Action _releasePause;
        private Collider _actor;
        private bool _pending, _enabled, _matching;
        public bool Pending => _pending;
        public DungeonEntryChoiceGate(int portalId, string destination, string label, Func<Collider, bool> eligible,
            Func<Collider, bool> enter, Func<bool> acquirePause, Action releasePause)
        { _portalId = portalId; _destination = destination; _label = label; _eligible = eligible; _enter = enter; _acquirePause = acquirePause; _releasePause = releasePause; }
        public void Enable()
        {
            if (_enabled) return; _enabled = true;
            EventBus<DungeonEntryChoiceSubmittedEvent>.Subscribe(OnChoice);
            EventBus<DungeonMatchReleasedEvent>.Subscribe(OnMatchReleased);
            EventBus<DungeonOnlineEntryConfirmedEvent>.Subscribe(OnOnlineEntry);
        }
        public void Disable()
        {
            if (!_enabled) return; _enabled = false; Close();
            EventBus<DungeonEntryChoiceSubmittedEvent>.Unsubscribe(OnChoice);
            EventBus<DungeonMatchReleasedEvent>.Unsubscribe(OnMatchReleased);
            EventBus<DungeonOnlineEntryConfirmedEvent>.Unsubscribe(OnOnlineEntry);
        }
        public bool Request(Collider actor)
        {
            if (!_enabled || _pending || actor == null || !_eligible(actor) || !EventBus<DungeonEntryChoiceRequestedEvent>.HasSubscribers || !_acquirePause()) return false;
            _actor = actor; _pending = true;
            EventBus<DungeonEntryChoiceRequestedEvent>.Publish(new DungeonEntryChoiceRequestedEvent
            { PortalId = _portalId, Actor = actor, DestinationKey = _destination, Label = _label });
            return true;
        }
        private void OnChoice(DungeonEntryChoiceSubmittedEvent choice)
        {
            if (!_enabled || !_pending || choice.PortalId != _portalId || choice.Actor != _actor) return;
            Collider actor = _actor;
            if (_matching)
            {
                // 先得到业务退出确认才能释放选择；不能一边占着线上席位一边进入单机。
                if (choice.Mode == DungeonEntryMode.Cancel) EventBus<OnlineRoomRequestEvent>.Publish(new OnlineRoomRequestEvent { PortalId = _portalId, Action = OnlineRoomAction.Cancel });
                return;
            }
            if (choice.Mode == DungeonEntryMode.Cancel) { Close(); return; }
            if (choice.Mode == DungeonEntryMode.Matchmaking && actor != null && _eligible(actor))
            {
                // 匹配保持弹窗/暂停，绝不调用单机保存/过门；N2独立完成业务入场后才切换玩法。
                _matching = true;
                EventBus<DungeonMatchRequestedEvent>.Publish(new DungeonMatchRequestedEvent { PortalId = _portalId, DestinationKey = _destination }); return;
            }
            Close(); // 先释放自己持有的暂停，其他Pause Reason仍由协调器保留。
            if (choice.Mode == DungeonEntryMode.SinglePlayer && actor != null && _eligible(actor)) _enter(actor);
        }
        private void OnMatchReleased(DungeonMatchReleasedEvent released)
        {
            if (!_pending || released.PortalId != _portalId) return;
            _matching = false; if (released.CloseChoice) Close();
        }
        private void OnOnlineEntry(DungeonOnlineEntryConfirmedEvent entry)
        { if (_pending && _matching && entry.PortalId == _portalId) Close(); }
        private void Close()
        {
            if (!_pending) return;
            _pending = _matching = false; _actor = null; _releasePause();
            EventBus<DungeonEntryChoiceClosedEvent>.Publish(new DungeonEntryChoiceClosedEvent { PortalId = _portalId });
        }
    }
}
