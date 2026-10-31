using System;
using Game.Core;
using Game.Networking;

namespace Game.Multiplayer
{
    /// <summary>
    /// 一次认证连接的完整库存收件箱。身份来自 Authenticator，不能从待验证消息反填；
    /// 只保留最新的一份完整值，角色/界面稍晚创建时也不会拼接不同版本的半份库存。
    /// </summary>
    public sealed class InventorySnapshotInbox
    {
        private readonly JoinConnectionIdentity _identity;
        private readonly string _configHash;
        private readonly string _inventoryId;
        private OnlineInventoryPresentationSnapshot _pending;
        private ulong _lastSequence;

        public InventorySnapshotInbox(JoinConnectionIdentity identity, string expectedGameplayHash)
        {
            _identity = identity; _configHash = expectedGameplayHash;
            // 与业务 FreezeCombatAssets 的稳定身份规则一致；不能信任消息自己声明的 InventoryId。
            _inventoryId = identity != null ? identity.RunId + "/" + identity.PlayerId : null;
        }

        /// <summary>先判断所有权，再推进序号；错误玩家的999号包不能挡住正确玩家的第1号包。</summary>
        public bool TryQueue(OnlineInventoryPresentationSnapshot snapshot)
        {
            if (!Matches(_identity, snapshot, _configHash, _inventoryId) || snapshot.SnapshotSequence <= _lastSequence) return false;
            _lastSequence = snapshot.SnapshotSequence; _pending = snapshot;
            return true;
        }

        public bool TryTake(out OnlineInventoryPresentationSnapshot snapshot)
        {
            snapshot = _pending; _pending = null;
            return snapshot != null;
        }

        /// <summary>
        /// 客户端收包与服务器定向发包共用同一身份边界。这里只校验基础值归属；
        /// Wire数量/有限值由Codec校验，物品父链/模板/资格由Run只读Source再次校验。
        /// </summary>
        public static bool Matches(JoinConnectionIdentity identity, OnlineInventoryPresentationSnapshot snapshot,
            string configHash, string inventoryId = null)
        {
            var assets = snapshot != null ? snapshot.Assets : null;
            if (identity == null || assets == null || snapshot.SchemaVersion != OnlineInventoryPresentationSnapshot.CurrentSchemaVersion ||
                assets.SchemaVersion != OnlineOwnedAssetSnapshot.CurrentSchemaVersion || snapshot.SnapshotSequence == 0 ||
                identity.ActorId < 1 || identity.ActorId > 4 || identity.ConnectionEpoch == 0 ||
                string.IsNullOrEmpty(identity.PlayerId) || string.IsNullOrEmpty(identity.RunId) ||
                snapshot.ConnectionEpoch != identity.ConnectionEpoch || assets.ActorId != identity.ActorId ||
                assets.PlayerId != identity.PlayerId || assets.RunId != identity.RunId ||
                string.IsNullOrEmpty(configHash) || assets.GameplayConfigHash != configHash) return false;
            return assets.InventoryId == (inventoryId ?? identity.RunId + "/" + identity.PlayerId);
        }
    }
}
