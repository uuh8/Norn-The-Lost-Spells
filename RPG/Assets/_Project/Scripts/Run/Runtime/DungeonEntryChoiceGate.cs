using System;
using Game.Core;
using UnityEngine;

namespace Game.Run
{
    /// <summary>门只发布单人/合作服务器意图，不持有本地资产写入或加载权限。</summary>
    public sealed class DungeonEntryChoiceGate
    {
        private readonly int _portalId;
        private readonly string _destination, _label;
        private readonly Func<Collider, bool> _eligible;
        private readonly Func<bool> _acquirePause;
        private readonly Action _releasePause;
        private Collider _actor;
        private bool _pending, _enabled, _matching;
        public bool Pending => _pending;
        public DungeonEntryChoiceGate(int portalId, string destination, string label, Func<Collider, bool> eligible,
            Func<bool> acquirePause, Action releasePause)
        { _portalId = portalId; _destination = destination; _label = label; _eligible = eligible; _acquirePause = acquirePause; _releasePause = releasePause; }
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
        /// <summary>
        /// 原和平实验场只有单人入口，不弹合作选项。仍持有同一入场暂停，直到业务释放或真实认证确认；
        /// F重复不会发第二份资产预留，也不借本地Repository切场景。
        /// </summary>
        public bool RequestSinglePlayer(Collider actor)
        {
            if (!_enabled || _pending || actor == null || !_eligible(actor)
                || !EventBus<DungeonSinglePlayerRequestedEvent>.HasSubscribers || !_acquirePause()) return false;
            _actor = actor; _pending = _matching = true;
            EventBus<DungeonSinglePlayerRequestedEvent>.Publish(new DungeonSinglePlayerRequestedEvent
                { PortalId = _portalId, DestinationKey = _destination });
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
            if ((choice.Mode == DungeonEntryMode.Matchmaking || choice.Mode == DungeonEntryMode.SinglePlayer) && actor != null && _eligible(actor))
            {
                // 两种方式都等待真实Ticket认证，不把单人伪装成合作Ready或回退本地_enter。
                _matching = true;
                if (choice.Mode == DungeonEntryMode.SinglePlayer)
                    EventBus<DungeonSinglePlayerRequestedEvent>.Publish(new DungeonSinglePlayerRequestedEvent { PortalId = _portalId, DestinationKey = _destination });
                else EventBus<DungeonMatchRequestedEvent>.Publish(new DungeonMatchRequestedEvent { PortalId = _portalId, DestinationKey = _destination });
                return;
            }
            Close(); // 先释放自己持有的暂停，其他Pause Reason仍由协调器保留。
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
