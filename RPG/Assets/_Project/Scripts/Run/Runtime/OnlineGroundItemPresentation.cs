using System;
using System.Collections.Generic;
using Game.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Run
{
    /// <summary>
    /// 本地Owner已确认Ground快照的模型投影。与原附近物共用VisualFactory/注册表；
    /// 只维护自有模型，不写Source Store，也不在请求发出时乐观移除地面物。
    /// </summary>
    public sealed class OnlineGroundItemPresentation : IDisposable
    {
        private readonly OnlineInventoryPresentationSource _source;
        private readonly Transform _actor;
        private readonly GameObject _coinPrefab;
        private readonly OnlineCoinSnapshot[] _coins = new OnlineCoinSnapshot[OnlineGroundInventorySnapshot.MaximumCoins];
        private readonly WorldCurrencyPickup[] _coinModels = new WorldCurrencyPickup[OnlineGroundInventorySnapshot.MaximumCoins];
        private readonly bool[] _coinActive = new bool[OnlineGroundInventorySnapshot.MaximumCoins];
        private readonly Dictionary<string, WorldItemPickup> _models = new Dictionary<string, WorldItemPickup>(StringComparer.Ordinal);
        private readonly List<string> _removed = new List<string>();
        private ItemOwnershipStore _boundItems;
        private uint _version;
        private bool _disposed;

        public OnlineGroundItemPresentation(OnlineInventoryPresentationSource source, Transform actor)
            : this(source, actor, null) { }

        public OnlineGroundItemPresentation(OnlineInventoryPresentationSource source, Transform actor, GameObject coinPrefab)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _actor = actor != null ? actor : throw new ArgumentNullException(nameof(actor));
            _coinPrefab = coinPrefab;
        }
        public bool IsFor(OnlineInventoryPresentationSource source, Transform actor)
            => !_disposed && ReferenceEquals(_source, source) && _actor == actor;

        public bool Sync()
        {
            if (_disposed || _actor == null || !_actor.gameObject.scene.IsValid() || !_actor.gameObject.scene.isLoaded) return false;
            var items = _source.PresentationItems;
            if (items == null) return false;
            if (ReferenceEquals(items, _boundItems) && _version == _source.GroundVersion) return true;
            _removed.Clear();
            foreach (var pair in _models)
                if (!_source.TryGetGroundPose(pair.Key, out _, out _)) _removed.Add(pair.Key);
            for (int i = 0; i < _removed.Count; i++) Remove(_removed[i]);

            foreach (var entry in items)
            {
                if (entry.Location.Kind != ItemLocationKind.WorldItem) continue;
                if (!_source.TryGetGroundPose(entry.Item.InstanceId, out var position, out var rotation)
                    || !items.Catalog.TryGet(entry.Item.ContentId, out var definition)) return false;
                bool created = !_models.TryGetValue(entry.Item.InstanceId, out var proxy) || proxy == null;
                if (created)
                {
                    var model = WorldItemVisualFactory.Create(definition);
                    SceneManager.MoveGameObjectToScene(model, _actor.gameObject.scene);
                    proxy = model.GetComponent<WorldItemPickup>();
                }
                // 每次Source快照可能替换内部Store；BindOnline先解除旧key，再绑定当前完整投影。
                proxy.transform.SetPositionAndRotation(position, rotation);
                if (!proxy.BindOnline(_source, entry.Item.InstanceId, entry.Location.OwnerId, _actor))
                {
                    if (created) DestroyOwned(proxy.gameObject);
                    return false;
                }
                if (created) _models[entry.Item.InstanceId] = proxy;
                proxy.gameObject.SetActive(true);
            }
            if (!SyncCoins()) return false;
            _boundItems = items; _version = _source.GroundVersion;
            return true;
        }

        private bool SyncCoins()
        {
            int count = _source.CopyGroundCoinsTo(_coins);
            if (count != 0 && (_coinPrefab == null || _coinPrefab.GetComponent<WorldCurrencyPickup>() == null)) return false;
            // Coin身份固定对应本局12个NPC，不回收槽位；移除只播放原拾取动画，不在Replica授予金币。
            for (int slot = 0; slot < _coinModels.Length; slot++)
            {
                int found = -1;
                for (int i = 0; i < count; i++) if (_coins[i].Id == (ulong)slot + 5) { found = i; break; }
                if (found < 0)
                {
                    if (_coinActive[slot] && _coinModels[slot] != null) _coinModels[slot].PresentCollected();
                    _coinActive[slot] = false;
                    continue;
                }
                var coin = _coins[found];
                if (_coinModels[slot] == null)
                {
                    var model = UnityEngine.Object.Instantiate(_coinPrefab);
                    model.SetActive(false);
                    SceneManager.MoveGameObjectToScene(model, _actor.gameObject.scene);
                    var proxy = model.GetComponent<WorldCurrencyPickup>();
                    if (proxy == null) { DestroyOwned(model); return false; }
                    proxy.ConfigureReplica(coin.Amount);
                    _coinModels[slot] = proxy;
                }
                _coinModels[slot].transform.position = coin.Position;
                _coinModels[slot].gameObject.SetActive(true);
                _coinActive[slot] = true;
            }
            return true;
        }

        private void Remove(string id)
        {
            if (!_models.TryGetValue(id, out var proxy)) return;
            _models.Remove(id);
            if (proxy == null) return;
            proxy.ReleaseOnlineBinding(); proxy.gameObject.SetActive(false); DestroyOwned(proxy.gameObject);
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var proxy in _models.Values)
                if (proxy != null) { proxy.ReleaseOnlineBinding(); proxy.gameObject.SetActive(false); DestroyOwned(proxy.gameObject); }
            for (int i = 0; i < _coinModels.Length; i++)
                if (_coinModels[i] != null) { _coinModels[i].gameObject.SetActive(false); DestroyOwned(_coinModels[i].gameObject); _coinModels[i] = null; }
            _models.Clear(); _removed.Clear(); _boundItems = null;
        }
        private static void DestroyOwned(GameObject owned)
        { if (Application.isPlaying) UnityEngine.Object.Destroy(owned); else UnityEngine.Object.DestroyImmediate(owned); }
    }
}

