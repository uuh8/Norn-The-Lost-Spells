using Game.Core;
using Game.Run;
using UnityEngine;

namespace Game.Character
{
    /// <summary>
    /// Client 角色上的线上库存读桥。Multiplayer 提供经过认证的 Core 值，Character 组合 Run 的纯读源，
    /// UI 经 Run 事件取得接口；因此网络程序集无需依赖 Run，也不会在 Client 构造 Authority Workspace。
    /// 该组件没有 Update、不读取存档、不恢复 Mana，不持有施法/库存事务的写权限。
    /// </summary>
    public sealed class OnlineInventoryReplica : MonoBehaviour
    {
        private struct ResourcePoint
        {
            public string Wand;
            public uint Epoch, Tick, Revision;
            public uint SlotPlusOne;
            public float Mana, Cooldown;
        }
        // 只缓存三根装备杖的最新资源。ActorState 可先于可靠完整快照到达，不能为此创建无限 pending 队列。
        private readonly ResourcePoint[] _pendingResources = new ResourcePoint[HeldItemSlots.WandCount];
        private OnlineInventoryPresentationSource _source;
        private OnlineInventoryPresentationIdentity _identity;
        public IInventoryPresentationSource Source => _source;
        public bool HasSnapshot => _source != null && _source.HasSnapshot;
        public uint InventoryVersion => _source != null ? _source.ReadOnly.Version : 0;
        public ulong SnapshotSequence => _source != null ? _source.SnapshotSequence : 0;
        public uint ConnectionEpoch => _source != null ? _source.ConnectionEpoch : 0;

        /// <summary>只读K自动来源，沿当前已完整安装Source的真实Store顺序，不截断或另造本地选择。</summary>
        public bool TryGetFirstBackpackMaterial(out InventoryItemSnapshot item)
        {
            item = default;
            return HasSnapshot && _source.TryGetFirstBackpackMaterial(out item);
        }

        /// <summary>
        /// CatalogIndex 只应由 Character 内部的 OnlineCastDriver 从受控 Definition 取得；Multiplayer
        /// 通过 Driver 的 Core DTO 包装入口调用，不能为这一步新增网络层到 Run 的反向依赖。
        /// 先完整验证候选，再替换当前 Source；非法/迟到快照不会清掉上一份仍一致的 UI。
        /// </summary>
        public bool Install(in OnlineInventoryPresentationIdentity expected, OnlineInventoryPresentationSnapshot snapshot,
            ItemCatalogIndex catalog, out string error)
        {
            error = null;
#if UNITY_SERVER && !UNITY_EDITOR
            error = "Dedicated Server 不安装 Client 库存读桥。"; return false;
#else
            var player = GetComponent<PlayerControllerBase>();
            if (player != null && player.ExecutionMode != PlayerExecutionMode.Prediction)
            { error = "只有本机 Prediction Actor 能绑定线上库存表现。"; return false; }
            uint previousInventory = InventoryVersion;
            uint previousGround = _source != null ? _source.GroundVersion : 0;
            uint previousSelection = _source != null ? _source.ToolbeltReadOnly.SelectionVersion : 0;
            string previousSelected = _source != null ? _source.WandsReadOnly.SelectedWandId : null;
            var candidate = _source;
            bool replace = candidate == null || expected.ConnectionEpoch != _identity.ConnectionEpoch;
            if (candidate != null && (!SameActor(expected, _identity)
                || unchecked((int)(expected.ConnectionEpoch - _identity.ConnectionEpoch)) < 0))
            { error = "库存预期身份不属于当前 Actor 或连接代次已过期。"; return false; }
            if (replace)
            {
                if (!OnlineInventoryPresentationSource.TryCreate(catalog, expected, snapshot, out candidate, out error)) return false;
            }
            else if (!candidate.TryInstall(snapshot, out error)) return false;

            var previous = _source; _source = candidate; _identity = expected;
            // 跨通道先到的资源先重放，再发布UI绑定；不会让较早完整快照把玩家看到的 Mana 短暂抬回旧值。
            for (int i = 0; i < _pendingResources.Length; i++)
            {
                var point = _pendingResources[i];
                if (point.Wand == null && point.SlotPlusOne == 0) continue;
                if (point.Epoch == expected.ConnectionEpoch)
                    InstallResource(point);
                else _pendingResources[i] = default;
            }
            OnlineInventoryPresentation.Bind(GetInstanceID(), gameObject, _source);
            if (previous != null && !ReferenceEquals(previous, _source)) previous.Dispose();
            // 他人拾放不会伪增个人库存版本，但原共享面板也要刷新其公开地面只读内容。
            if (replace || previousInventory != InventoryVersion || previousGround != _source.GroundVersion)
                EventBus<InventoryChangedEvent>.Publish(new InventoryChangedEvent { InventoryId = _source.ReadOnly.InventoryId, Version = InventoryVersion });
            if (replace || previousInventory != InventoryVersion || previousSelection != _source.ToolbeltReadOnly.SelectionVersion
                || previousSelected != _source.WandsReadOnly.SelectedWandId)
                EventBus<ToolbeltChangedEvent>.Publish(new ToolbeltChangedEvent
                {
                    InventoryId = _source.ReadOnly.InventoryId, ItemId = _source.ToolbeltReadOnly.SelectedItemId,
                    SelectedSlot = _source.ToolbeltReadOnly.SelectedSlot, ConfigurationVersion = _source.ToolbeltReadOnly.ConfigurationVersion,
                    SelectionVersion = _source.ToolbeltReadOnly.SelectionVersion
                });
            return true;
#endif
        }

        /// <summary>
        /// 资源事实是固定大小的值更新，不解析/重建物品数组。上层已校验本机连接 Owner；这里再次限制
        /// Epoch、Tick、字符串与有限数值，最多保留三条，以应对快照尚未到达或暂时跨通道乱序。
        /// 返回值表示已安装进可见 Source；尚无 Source 时可先缓存并返回 false，不能当作资产已就绪。
        /// </summary>
        public bool ApplyResources(uint epoch, uint serverTick, string wandId, uint programRevision, float currentMana, float cooldown)
        {
            if (epoch == 0 || serverTick == 0 || programRevision == 0 || string.IsNullOrEmpty(wandId) || wandId.Length > 64
                || !float.IsFinite(currentMana) || currentMana < 0 || !float.IsFinite(cooldown) || cooldown < 0
                || _source != null && epoch != _identity.ConnectionEpoch) return false;
            int slot = -1;
            for (int i = 0; i < _pendingResources.Length; i++)
            {
                var point = _pendingResources[i];
                if (point.Wand == wandId)
                {
                    if (unchecked((int)(epoch - point.Epoch)) < 0
                        || epoch == point.Epoch && unchecked((int)(serverTick - point.Tick)) <= 0) return false;
                    slot = i; break;
                }
                if (slot < 0 && point.Wand == null) slot = i;
            }
            if (slot < 0) return false; // 装备合同上限三根，不能让未知实例挤掉已有读数或无限扩容。
            _pendingResources[slot] = new ResourcePoint
            { Wand = wandId, Epoch = epoch, Tick = serverTick, Revision = programRevision, Mana = currentMana, Cooldown = cooldown };
            return _source != null && _source.TryApplyResources(epoch, serverTick, wandId, programRevision, currentMana, cooldown);
        }

        /// <summary>
        /// 高频网络消息使用固定装备槽，不依赖可靠Setup到达的先后顺序。两根杖都可能处于ProgramRevision=1，
        /// 因此Revision只能判断程序新旧，不能代替实例身份。缓存仍固定三个值，不在每个ActorState分配字符串。
        /// </summary>
        public bool ApplySlotResources(uint epoch, uint tick, uint slotPlusOne, uint revision, float mana, float cooldown)
        {
            if (epoch == 0 || tick == 0 || slotPlusOne < 1 || slotPlusOne > 3 || revision == 0 ||
                !float.IsFinite(mana) || mana < 0 || !float.IsFinite(cooldown) || cooldown < 0 ||
                _source != null && epoch != _identity.ConnectionEpoch) return false;
            int index = (int)slotPlusOne - 1; var before = _pendingResources[index];
            if (before.Epoch != 0 && (unchecked((int)(epoch - before.Epoch)) < 0 ||
                epoch == before.Epoch && unchecked((int)(tick - before.Tick)) <= 0)) return false;
            var point = new ResourcePoint { Epoch = epoch, Tick = tick, SlotPlusOne = slotPlusOne, Revision = revision, Mana = mana, Cooldown = cooldown };
            _pendingResources[index] = point;
            return InstallResource(point);
        }
        private bool InstallResource(in ResourcePoint point)
        {
            if (_source == null) return false;
            string id = point.Wand;
            if (point.SlotPlusOne != 0)
            {
                if (!_source.WandsReadOnly.TryGetSlot((int)point.SlotPlusOne - 1, out var state)) return false;
                id = state.InstanceId;
            }
            return _source.TryApplyResources(point.Epoch, point.Tick, id, point.Revision, point.Mana, point.Cooldown);
        }

        private static bool SameActor(in OnlineInventoryPresentationIdentity a, in OnlineInventoryPresentationIdentity b) =>
            a.ActorId == b.ActorId && a.RunId == b.RunId && a.PlayerId == b.PlayerId && a.InventoryId == b.InventoryId
            && a.GameplayConfigHash == b.GameplayConfigHash && a.WorldSceneKey == b.WorldSceneKey;

        private void OnDestroy()
        {
            // Source 只由本 Replica 释放。Scope 可以先关闭/解绑UI；旧Replica的销毁不会清较新Owner缓存。
            OnlineInventoryPresentation.Clear(GetInstanceID());
            if (_source != null) _source.Dispose();
            _source = null; _identity = default;
            for (int i = 0; i < _pendingResources.Length; i++) _pendingResources[i] = default;
        }
    }
}
