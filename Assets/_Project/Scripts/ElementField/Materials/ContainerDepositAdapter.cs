using Game.Core;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>只接同一个WorldId的同步投放；拒绝时来源保持原量，代理负责可见等待。</summary>
    public sealed class ContainerDepositAdapter : MonoBehaviour
    {
        [SerializeField] private ElementWorldRuntime _world;
        [SerializeField] private string _worldId = "container-world";
        private MaterialInventoryDepositPort _port;
        private bool _executing;
        private void Awake() { if (_world == null) _world = GetComponent<ElementWorldRuntime>(); if (_world != null) _port = new MaterialInventoryDepositPort(_world); }
        private void OnEnable() => EventBus<MaterialWorldDepositRequestedEvent>.Subscribe(OnRequest);
        private void OnDisable() => EventBus<MaterialWorldDepositRequestedEvent>.Unsubscribe(OnRequest);
        public void Configure(ElementWorldRuntime world, string id) { _world = world; _worldId = id; _port = new MaterialInventoryDepositPort(world); }
        private void OnRequest(MaterialWorldDepositRequestedEvent command)
        {
            var exchange = command.Exchange; if (exchange == null || exchange.Handled || exchange.WorldId != _worldId) return;
            exchange.Handled = true;
            if (_executing) { exchange.Failure = MaterialTransferFailure.ResourceBusy; return; }
            if (_world == null || !_world.IsInitialized || _port == null) { exchange.Failure = MaterialTransferFailure.WorldUnavailable; return; }
            _executing = true;
            try { exchange.Failure = _port.Deposit(exchange, out exchange.Transferred); }
            finally { _executing = false; exchange.Source?.Cancel(); }
        }
    }
}
