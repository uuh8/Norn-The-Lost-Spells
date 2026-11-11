using System;
using Game.Networking;

namespace Game.Multiplayer
{
    public sealed partial class OnlineCombatWorld
    {
        // 只表示网络Adapter是否附着，不是Gameplay生命周期。构造器首次附着后置于默认true；
        // 暂无Manager时仍保留字段指向的旧对象供安全退订和诊断，绝不Dispose原Owner。
        private bool _networkingAttached = true;
        internal RoomInstanceBootstrap GameplayInstanceOwner => _instance;
        internal RoomNetworkManager NetworkingManager => _manager;

        /// <summary>
        /// 同World只替换传输适配器：Workspace、共享地面、Home恢复标记、在途输出与资源事实均继续存活。
        /// Dedicated的Instance握有原Entry、最终收据和租约状态；相同Run字符串不能授权换成另一个实例对象。
        /// Client没有该Owner，允许null→null。所有拒绝发生在拆旧网络之前，Authority故障门随后阻止新玩法写入。
        /// </summary>
        public bool RebindNetworking(RoomNetworkManager manager, CombatNetworkChannel channel,
            RoomInstanceBootstrap instance, out string error)
        {
            error = null;
            if (_disposed) { error = "NetworkingOwnerDisposed"; return false; }
            if (!ReferenceEquals(_instance, instance) || _world.IsAuthority && (_instance == null || instance == null))
            {
                if (_world.IsAuthority) Faulted = true;
                error = "DedicatedGameplayOwnerChanged"; return false;
            }
            if (manager == null || channel == null || channel.gameObject != manager.gameObject)
            { error = "NetworkingChannelsUnavailable"; return false; }
            var movement = manager.GetComponent<MovementNetworkChannel>();
            if (movement == null) { error = "MovementChannelUnavailable"; return false; }
            var inventory = manager.GetComponent<InventoryNetworkChannel>();
            if (inventory == null) inventory = manager.gameObject.AddComponent<InventoryNetworkChannel>();
            if (_networkingAttached && ReferenceEquals(_manager, manager) && ReferenceEquals(_channel, channel)
                && ReferenceEquals(_movementChannel, movement) && ReferenceEquals(_inventoryChannel, inventory)) return true;

            // 新组件可能已经注册Mirror全局handler。先停新再停旧，避免旧OnDisable把新handler也删掉；
            // 新组件重新启用后各自恢复原注册流程，库存Configure还会立即装回首份Owner的可靠入口。
            if (!ReferenceEquals(channel, _channel)) channel.enabled = false;
            if (!ReferenceEquals(movement, _movementChannel)) movement.enabled = false;
            if (!ReferenceEquals(inventory, _inventoryChannel)) inventory.enabled = false;
            SuspendNetworking();
            _manager = manager; _channel = channel; _movementChannel = movement; _inventoryChannel = inventory;
            movement.enabled = true; channel.enabled = true; inventory.enabled = true;
            _inventoryChannel.ConfigureWorld(this, _definition.GameplayConfigHash, _definition.WireHash, ValidateClientInventoryCandidate);
            _inventoryChannel.CommandReceived += OnInventoryCommand;
            _channel.GameplayHash = _definition.WireHash;
            _channel.IntentReceived += OnIntent; _channel.FactReceived += OnFact;
            _networkingAttached = true;

            // 清的是“已给这条网络发过什么”的水位，不是资产/连接或事实本身。完整序号保持单调，
            // 同Epoch切Adapter也不会把SnapshotSequence重用为1，导致保留Source永远拒绝后续快照。
            Array.Clear(_inventoryEpochs, 0, _inventoryEpochs.Length);
            Array.Clear(_inventoryVersions, 0, _inventoryVersions.Length);
            Array.Clear(_inventorySelectionVersions, 0, _inventorySelectionVersions.Length);
            Array.Clear(_inventoryGroundVersions, 0, _inventoryGroundVersions.Length);
            Array.Clear(_inventoryCurrencyVersions, 0, _inventoryCurrencyVersions.Length);
            Array.Clear(_inventoryResearchVersions, 0, _inventoryResearchVersions.Length);
            Array.Clear(_bootstrappedEpoch, 0, _bootstrappedEpoch.Length);
            Array.Clear(_setupWands, 0, _setupWands.Length); Array.Clear(_setupRevisions, 0, _setupRevisions.Length);
            _snapshotClock.Reset(); return true;
        }

        /// <summary>
        /// Manager短暂缺席也只撤Adapter。不要用Unity的??访问已销毁组件；退订纯C#事件可清仍存活的managed引用，
        /// enabled/GameplayHash等操作则只在Unity对象有效时执行。原字段仍保留，下一次Rebind能核清旧所有权。
        /// </summary>
        internal void SuspendNetworking()
        {
            if (!_networkingAttached) return;
            if (!ReferenceEquals(_channel, null))
            { _channel.IntentReceived -= OnIntent; _channel.FactReceived -= OnFact; }
            if (!ReferenceEquals(_inventoryChannel, null))
            { _inventoryChannel.CommandReceived -= OnInventoryCommand; _inventoryChannel.DetachWorld(this); }
            if (_channel != null) { _channel.GameplayHash = 0; _channel.enabled = false; }
            if (_movementChannel != null) _movementChannel.enabled = false;
            if (_inventoryChannel != null) _inventoryChannel.enabled = false;
            _networkingAttached = false;
        }
    }
}
