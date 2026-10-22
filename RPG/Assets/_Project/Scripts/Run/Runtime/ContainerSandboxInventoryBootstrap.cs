using Game.Core;
using UnityEngine;

namespace Game.Run
{
    /// <summary>仅独立 Sandbox 使用：新档两只空瓶先在背包同格堆叠；恢复档不重复发放或搬动已有瓶。</summary>
    public sealed class ContainerSandboxInventoryBootstrap : MonoBehaviour
    {
        [SerializeField] private SinglePlayerInventorySession _session;
        private readonly InventoryItemSnapshot[] _buffer = new InventoryItemSnapshot[256];
        private bool _done;
        private void Update()
        {
            if (_done || _session == null || _session.ReadOnly == null) return;
            _done = true; var read = _session.ReadOnly; if (read.Count > _buffer.Length) return;
            int count = read.CopyItemsTo(_buffer); string bag = null;
            for (int i = 0; i < count; i++) if (_buffer[i].Kind == ItemKind.Backpack && _buffer[i].Location.Kind == ItemLocationKind.EquippedBackpack) bag = _buffer[i].InstanceId;
            for (int i = 0; i < count; i++) if (_buffer[i].ContentId == "containertest.bottle" && _buffer[i].Location.Kind == ItemLocationKind.Warehouse)
                EventBus<InventoryCommandEvent>.Publish(new InventoryCommandEvent { InventoryId = read.InventoryId, RequestId = (ulong)i + 1,
                    Kind = InventoryCommandKind.Place, Destination = InventoryDestination.Backpack, ItemId = _buffer[i].InstanceId, OwnerId = bag,
                    X = 0, Y = 2, ExpectedVersion = read.Version });
        }
    }
}
