using System;
using Game.Core;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>监听 Core 世界命令，世界 Owner 的写职责留在 ElementField；不引用 Run/UI。</summary>
    public sealed class MaterialInventoryTransferAdapter : MonoBehaviour
    {
        [SerializeField] private ElementWorldRuntime _world;
        [SerializeField] private string _worldId = "container-world";
        [SerializeField, Min(.01f)] private float _maximumReach = 3;
        [SerializeField, Min(.01f)] private float _maximumSampleRadius = .5f;
        private MaterialInventoryWorldPort _port;
        private bool _executing;
        public string WorldId => _worldId;
        private void Awake() { if (_world == null) _world = GetComponent<ElementWorldRuntime>(); if (_world != null) _port = new MaterialInventoryWorldPort(_world); }
        public void Configure(ElementWorldRuntime world, string id)
        { if (_executing) throw new InvalidOperationException("采集期间不能更换世界。"); _world = world; _worldId = id; _port = new MaterialInventoryWorldPort(world); }
        private void OnEnable() => EventBus<MaterialWorldTransferRequestedEvent>.Subscribe(OnRequest);
        private void OnDisable() => EventBus<MaterialWorldTransferRequestedEvent>.Unsubscribe(OnRequest);
        private void OnRequest(MaterialWorldTransferRequestedEvent command)
        {
            var exchange = command.Exchange;
            if (exchange == null || exchange.Handled || exchange.Request.WorldId != _worldId) return;
            exchange.Handled = true;
            var request = exchange.Request; var result = exchange.Result;
            if (_executing) { result.Failure = MaterialTransferFailure.ResourceBusy; exchange.Result = result; return; }
            if (_world == null || !_world.IsInitialized || _port == null || exchange.Destination == null)
            { result.Failure = MaterialTransferFailure.WorldUnavailable; exchange.Result = result; return; }
            if (!Finite(request.SampleCenter) || !Finite(request.CollectorPosition) || float.IsNaN(request.Radius)
                || request.Radius <= 0 || request.Radius > _maximumSampleRadius
                || (request.SampleCenter - request.CollectorPosition).sqrMagnitude > _maximumReach * _maximumReach)
            { result.Failure = MaterialTransferFailure.InvalidRequest; exchange.Result = result; return; }
            _executing = true;
            try { result.Failure = _port.Transfer(request, exchange.Destination, out result.Transferred); exchange.Result = result; }
            finally { _executing = false; exchange.Destination.Cancel(); }
        }
        private static bool Finite(Vector3 point) => float.IsFinite(point.x) && float.IsFinite(point.y) && float.IsFinite(point.z);
    }
}
