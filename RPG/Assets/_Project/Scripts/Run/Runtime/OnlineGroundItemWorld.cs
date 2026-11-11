using System;
using System.Collections.Generic;
using Game.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Run
{
    /// <summary>
    /// 一个DS场景的地面表现与支撑边界。资产迁移只交Workspace/GroundStore原子处理；
    /// 本层先准备禁用模型和合法落点，失败时不删除携带资产，不生成新的物品实例。
    /// </summary>
    public sealed partial class OnlineGroundItemWorld : IDisposable
    {
        private readonly Scene _scene;
        private readonly ItemCatalogIndex _catalog;
        private readonly OnlineGroundItemStore _ground;
        private readonly WorldItemGroundPlacement _placement = new WorldItemGroundPlacement();
        private readonly HashSet<OnlineOwnedWorkspace> _attached = new HashSet<OnlineOwnedWorkspace>();
        private readonly Dictionary<string, WorldItemPickup> _models = new Dictionary<string, WorldItemPickup>(StringComparer.Ordinal);
        private readonly WorldCurrencyPickup[] _coinModels = new WorldCurrencyPickup[OnlineCoinWorld.MaximumCoins];
        private readonly OnlineCoinRecord[] _coinBuffer = new OnlineCoinRecord[OnlineCoinWorld.MaximumCoins];
        private OnlineCoinWorld _coins;
        private GameObject _coinPrefab;
        private IWorldCurrencyCollector _coinCollector;
        private Func<bool> _coinCanAdvance;
        private string _runId;
        private int _dropSequence;
        private bool _disposed, _executing;

        public OnlineGroundItemWorld(ItemCatalogIndex catalog, Scene scene)
        {
            if (catalog == null || !scene.IsValid() || !scene.isLoaded) throw new ArgumentException("地面世界需要受信Catalog与已加载Scene。");
            _catalog = catalog; _scene = scene; _ground = new OnlineGroundItemStore(catalog);
        }
        public bool Faulted { get; private set; }
        public OnlineCoinWorld Coins => _coins;
        public uint Version
        {
            get
            {
                if (_disposed) return 0;
                // 两个独立事实源共用既有Ground版本；先在ulong域求和，绝不回绕成客户端可接受的旧版。
                ulong value = (ulong)_ground.Version + (_coins != null ? _coins.Version - 1ul : 0ul);
                if (value > uint.MaxValue) { Faulted = true; return 0; }
                return (uint)value;
            }
        }
        public OnlineGroundInventorySnapshot Capture()
        {
            uint version = Version;
            if (_disposed || Faulted || version == 0) return null;
            var snapshot = _ground.Capture(); snapshot.Version = version;
            if (_coins == null) return snapshot;
            int count = _coins.CopyTo(_coinBuffer), active = 0;
            for (int i = 0; i < count; i++) if (!_coinBuffer[i].Collected) active++;
            snapshot.Coins = new OnlineCoinSnapshot[active];
            for (int i = 0, at = 0; i < count; i++)
                if (!_coinBuffer[i].Collected) snapshot.Coins[at++] = new OnlineCoinSnapshot
                { Id = _coinBuffer[i].Id, Amount = _coinBuffer[i].Amount, Position = _coinBuffer[i].Position };
            return snapshot;
        }

        public bool ConfigureCoins(string runId, GameObject prefab, Func<bool> canAdvance, IWorldCurrencyCollector collector)
        {
            if (_disposed || _executing || string.IsNullOrWhiteSpace(runId) || prefab == null
                || prefab.GetComponent<WorldCurrencyPickup>() == null || canAdvance == null || collector == null
                || _runId != null && _runId != runId) return false;
            if (_coins != null) return _coins.RunId == runId && _coinPrefab == prefab && ReferenceEquals(_coinCollector, collector);
            _runId = runId; _coinPrefab = prefab; _coinCanAdvance = canAdvance; _coinCollector = collector;
            _coins = new OnlineCoinWorld(runId, CanWriteCoins); return true;
        }

        private bool CanWriteCoins()
        {
            if (_disposed || Faulted || _coinCanAdvance == null || !_coinCanAdvance()) return false;
            return CanAllocateGroundRevision();
        }
        private bool CanAllocateGroundRevision()
        {
            if (_disposed || Faulted) return false;
            uint version = Version;
            if (version == 0 || version == uint.MaxValue) { Faulted = true; return false; }
            return true;
        }

        public bool TryCollectCoin(ulong id, ulong actorId)
        {
            if (_disposed || _executing || _coins == null || !CanWriteCoins()) return false;
            OnlineOwnedWorkspace owner = null;
            foreach (var candidate in _attached)
                if (candidate.ActorId == actorId) { owner = candidate; break; }
            _executing = true;
            try
            {
                if (!_coins.TryCollect(id, actorId, owner)) return false;
                int slot = (int)(id - OnlineActorLimits.FirstNpcActorId);
                if (_coinModels[slot] != null) _coinModels[slot].PresentCollected();
                return true;
            }
            finally { _executing = false; }
        }

        public int CopyCoinCheckpointsTo(OnlineCoinCheckpointRecord[] target)
        {
            if (target == null || target.Length < OnlineCoinWorld.MaximumCoins) throw new ArgumentException("Coin检查点需要12条有界缓冲。");
            int count = _coins != null ? _coins.CopyTo(_coinBuffer) : 0;
            for (int i = 0; i < count; i++) target[i] = new OnlineCoinCheckpointRecord
            {
                Id = _coinBuffer[i].Id, Amount = _coinBuffer[i].Amount, Position = _coinBuffer[i].Position,
                Collected = _coinBuffer[i].Collected, CollectorActorId = _coinBuffer[i].CollectorActorId
            };
            return count;
        }

        /// <summary>只有注册成功的原Coin事实才能生成原Prefab；生成失败锁存现场，不重抽/补偿数量。</summary>
        public void SynchronizeCoins()
        {
            if (_disposed || _coins == null || Faulted || !_scene.IsValid() || !_scene.isLoaded) return;
            int count = _coins.CopyTo(_coinBuffer);
            for (int i = 0; i < count; i++)
            {
                var coin = _coinBuffer[i]; int slot = (int)(coin.Id - OnlineActorLimits.FirstNpcActorId);
                if (coin.Collected)
                { if (_coinModels[slot] != null) _coinModels[slot].PresentCollected(); continue; }
                if (_coinModels[slot] != null) continue;
                GameObject model = null;
                try
                {
                    model = UnityEngine.Object.Instantiate(_coinPrefab); model.SetActive(false);
                    SceneManager.MoveGameObjectToScene(model, _scene);
                    model.transform.position = coin.Position;
                    var pickup = model.GetComponent<WorldCurrencyPickup>();
                    if (pickup == null) throw new InvalidOperationException("原CoinPrefab缺失WorldCurrencyPickup。");
                    pickup.ConfigureAuthority(_coinCollector, coin.Id, coin.Amount);
                    _coinModels[slot] = pickup; model.SetActive(true); model = null;
                }
                catch (Exception exception)
                { Faulted = true; GameLog.Error("金币原事实已保留，但原Prefab装配失败：" + exception.Message, "OnlineInventory"); }
                finally { if (model != null) DestroyOwned(model); }
                if (Faulted) return;
            }
        }
        /// <summary>同Run组合根提供唯一退役Journal；地面本身不决定退出或资产返还。</summary>
        public bool ConfigureCheckpointJournal(IOnlineItemRetirementJournal journal)
            => !_disposed && !_executing && _ground.ConfigureCheckpointJournal(journal);

        /// <summary>只在原P7新来源配置时证明预算，旧Ground/单机构造不受此策略影响。</summary>
        public long ValidateContainerLootCapacity()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(OnlineGroundItemWorld));
            return DungeonLootPlanner.CalculateItemLootBound(_catalog);
        }
        public bool IsLootTableInCatalog(DungeonLootTable table)
        {
            if (_disposed || table == null || !table.IsValid) return false;
            foreach (var entry in table.Items) foreach (var definition in entry.Pool)
                if (!_catalog.TryGet(definition.ContentId, out var registered) || registered != definition) return false;
            return true;
        }

        /// <summary>
        /// 计划位置是敌人死亡点加原.9m散布。只做贴近该点的垂直接地，不参考玩家/NavMesh或横向另找楼层。
        /// 无支撑/忙/容量不足返回false，原计划保留重试；稳定key先查，已拾走的原计划绝不再次造模型。
        /// </summary>
        public bool TryRegisterLoot(DungeonLootPlannedItem item)
        {
            if (item == null || _disposed) return false;
            if (_ground.TryGetLootRegistration(item.Key, out _)) return true;
            if (!CanAllocateGroundRevision()) return false;
            if (_executing || !_scene.IsValid() || !_scene.isLoaded || !Finite(item.Position) || item.Definition == null
                || !_catalog.TryGet(item.Definition.ContentId, out var definition) || definition != item.Definition
                || !_catalog.TryGetRules(definition.ContentId, out var rules))
                return false;
            GameObject prepared = null; _executing = true;
            try
            {
                prepared = WorldItemVisualFactory.Create(definition);
                SceneManager.MoveGameObjectToScene(prepared, _scene);
                prepared.transform.SetPositionAndRotation(item.Position, Quaternion.identity);
                // P7原NPC立于地面；短探针只接受死亡点上.25m/下.5m内的支撑，避免套用玩家Drop的4m上探。
                // 仍用原模型Bounds、坡度.5、离地.01和零横向搜索；虚空/台边失败保持pending，绝不吸到队友脚下。
                if (!_placement.TryPlace(prepared, _scene, null, (1 << 0) | (1 << 8), .25f, .5f, .5f, .01f, 0)) return false;
                if (definition.Kind == ItemKind.Material && (item.MaterialQuantity <= 0 || (ulong)item.MaterialQuantity > rules.MaterialItemCapacity)) return false;
                var state = new RuntimeItemState(Guid.NewGuid().ToString("N"), definition.ContentId,
                    definition.Kind == ItemKind.Spell ? item.Charges : 0, definition.Kind == ItemKind.Spell && item.Unlimited,
                    composition: definition.Kind == ItemKind.Container ? new ContainerComposition(rules.ContainerCapacity, rules.ContainerMaterials) : null,
                    material: definition.Kind == ItemKind.Material ? new MaterialItemState(rules.MaterialKey, (ulong)item.MaterialQuantity) : null,
                    rolledWandStats: item.WandStats);
                _models.EnsureCapacity(_models.Count + 1);
                var failure = _ground.TryRegisterPlannedLoot(item.Key, state, prepared.transform.position, prepared.transform.rotation, out string id);
                if (failure != OnlineInventoryCommandFailure.None) return false;
                if (!_ground.TryGet(id, out var current)) throw new InvalidOperationException("空瓶注册已提交但共享地面记录缺失。");
                var proxy = prepared.GetComponent<WorldItemPickup>();
                if (proxy == null || !proxy.TryBindOnlineAuthority(_ground.Store, current.InstanceId, current.WorldOwnerId))
                    throw new InvalidOperationException("空瓶注册后的只读代理无法绑定。");
                _models.Add(id, proxy); prepared.SetActive(true); prepared = null;
                return true; // 无Player库存变化；现有全Owner GroundVersion泵负责发布。
            }
            finally { if (prepared != null) DestroyOwned(prepared); _executing = false; }
        }

        /// <summary>
        /// 原固定 SpawnPoint 的单次固体来源：直接使用原世界位姿和纯模型工厂，不散布、不探玩家楼层。
        /// 没有独立pending/重试时钟；失败返回调用方，初始化故障由正式装配显式处理。
        /// 稳定key先查，重连不得重新创建已经拾走的固定冰。
        /// </summary>
        public bool TryRegisterFixedMaterial(string stableKey, string contentId, ulong quantity, Vector3 position, Quaternion rotation)
        {
            if (_disposed) return false;
            if (_ground.TryGetMaterialLootRegistration(stableKey, out _)) return true;
            if (!CanAllocateGroundRevision()) return false;
            if (_executing || !_scene.IsValid() || !_scene.isLoaded || !Finite(position)
                || !_catalog.TryGet(contentId, out var definition) || definition.Kind != ItemKind.Material
                || !_catalog.TryGetRules(contentId, out var rules) || quantity == 0 || quantity > rules.MaterialItemCapacity) return false;
            GameObject prepared = null; _executing = true;
            try
            {
                prepared = WorldItemVisualFactory.Create(definition);
                SceneManager.MoveGameObjectToScene(prepared, _scene);
                prepared.transform.SetPositionAndRotation(position, rotation);
                var proxy = prepared.GetComponent<WorldItemPickup>();
                if (proxy == null) return false;
                _models.EnsureCapacity(_models.Count + 1);
                var failure = _ground.TryRegisterMaterialLoot(stableKey, contentId, quantity, position, rotation, out string id);
                if (failure != OnlineInventoryCommandFailure.None) return false;
                if (!_ground.TryGet(id, out var current) || !proxy.TryBindOnlineAuthority(_ground.Store, id, current.WorldOwnerId))
                    throw new InvalidOperationException("固定固体已登记，但原世界模型无法绑定同一共享实例。");
                _models.Add(id, proxy); prepared.SetActive(true); prepared = null;
                return true;
            }
            finally { if (prepared != null) DestroyOwned(prepared); _executing = false; }
        }

        public bool Attach(OnlineOwnedWorkspace workspace)
        {
            if (_disposed || workspace == null || _runId != null && workspace.RunId != _runId) return false;
            try { workspace.ConfigureGroundItems(_ground); }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            _runId = workspace.RunId; _attached.Add(workspace); return true;
        }

        public OnlineInventoryCommandReceipt Execute(OnlineOwnedWorkspace workspace, Transform actor, in OnlineInventoryCommandIntent intent)
        {
            if (workspace == null) return new OnlineInventoryCommandReceipt
            { CommandId = intent.CommandId, ConnectionEpoch = intent.ConnectionEpoch, Failure = OnlineInventoryCommandFailure.NotReady };
            // 先查统一账本：旧Drop被队友拾走后重放，不得再造模型、占用支撑查询或销毁后来再次丢出的同ID。
            if (workspace.TryGetRecordedInventoryCommand(intent, out var recorded)) return recorded;
            if (_disposed || !_attached.Contains(workspace) || actor == null || actor.gameObject.scene != _scene
                || !_scene.IsValid() || !_scene.isLoaded || !Finite(actor.position))
                return workspace.RejectInventoryCommand(intent, OnlineInventoryCommandFailure.NotReady);
            if (!CanAllocateGroundRevision()) return workspace.RejectInventoryCommand(intent, OnlineInventoryCommandFailure.NotReady);
            if (_executing) return workspace.RejectInventoryCommand(intent, OnlineInventoryCommandFailure.Busy);
            _executing = true;
            try
            {
                if (intent.Kind == OnlineInventoryCommandKind.DropContainer || intent.Kind == OnlineInventoryCommandKind.DropItem) return Drop(workspace, actor, intent);
                if (intent.Kind == OnlineInventoryCommandKind.PickupContainer || intent.Kind == OnlineInventoryCommandKind.PickupItem) return Pickup(workspace, actor, intent);
                return workspace.RejectInventoryCommand(intent, OnlineInventoryCommandFailure.InvalidInput);
            }
            finally { _executing = false; }
        }

        private OnlineInventoryCommandReceipt Drop(OnlineOwnedWorkspace workspace, Transform actor, in OnlineInventoryCommandIntent intent)
        {
            if (string.IsNullOrEmpty(intent.ItemId) || !workspace.TryGetItem(intent.ItemId, out var item)
                || !_catalog.TryGet(item.ContentId, out var definition)
                || intent.Kind == OnlineInventoryCommandKind.DropContainer && (item.Composition == null || definition.Kind != ItemKind.Container))
                return workspace.RejectInventoryCommand(intent, OnlineInventoryCommandFailure.NotOwned);
            var ids = workspace.GroundDepartureRoots(intent.ItemId, intent.WholeStack);
            if (ids.Count == 0) return workspace.RejectInventoryCommand(intent, OnlineInventoryCommandFailure.NotOwned);
            var prepared = new GameObject[ids.Count]; var placements = new OnlineGroundItemStore.Placement[ids.Count];
            try
            {
                for (int i = 0; i < ids.Count; i++)
                {
                    if (!workspace.TryGetItem(ids[i], out var currentItem) || !_catalog.TryGet(currentItem.ContentId, out var currentDefinition))
                        return workspace.RejectInventoryCommand(intent, OnlineInventoryCommandFailure.NotOwned);
                    prepared[i] = WorldItemVisualFactory.Create(currentDefinition);
                    SceneManager.MoveGameObjectToScene(prepared[i], _scene);
                    int sequence = unchecked(_dropSequence++);
                    prepared[i].transform.position = actor.position + actor.forward * 1.2f + Vector3.up * .3f + actor.right * (sequence % 3) * .35f;
                    // 整堆全部隐藏模型先接地，任何成员缺支撑都销毁准备物并保留整组原资产。
                    if (!_placement.TryPlace(prepared[i], _scene, actor, ~0, 4, 80, .5f, .01f, 0))
                        return workspace.RejectInventoryCommand(intent, OnlineInventoryCommandFailure.Rejected);
                    if (prepared[i].GetComponent<WorldItemPickup>() == null)
                        return workspace.RejectInventoryCommand(intent, OnlineInventoryCommandFailure.Rejected);
                    placements[i] = new OnlineGroundItemStore.Placement(ids[i], prepared[i].transform.position, prepared[i].transform.rotation);
                }
                _models.EnsureCapacity(_models.Count + ids.Count);
                var context = new OnlineContainerAuthorityContext
                {
                    CanAdvance = true, WorldId = _ground.WorldId, ActorPosition = actor.position,
                    TargetPosition = placements[0].Position, TargetRotation = placements[0].Rotation
                };
                workspace.PrepareGroundDrop(placements);
                OnlineInventoryCommandReceipt receipt;
                try { receipt = workspace.ExecuteInventoryCommand(intent, context); }
                finally { workspace.ClearPreparedGroundDrop(); }
                if (!receipt.Success) return receipt;
                for (int i = 0; i < ids.Count; i++)
                {
                    if (!_ground.TryGet(ids[i], out var current))
                        throw new InvalidOperationException("Drop已提交但共享地面记录缺失，不能伪造另一个实例补偿。");
                    RemoveModel(ids[i], null);
                    var proxy = prepared[i].GetComponent<WorldItemPickup>();
                    prepared[i].transform.SetPositionAndRotation(current.Position, current.Rotation);
                    if (!proxy.TryBindOnlineAuthority(_ground.Store, current.InstanceId, current.WorldOwnerId))
                        throw new InvalidOperationException("Drop提交后的只读代理无法绑定真实地面实例。");
                    _models.Add(current.InstanceId, proxy); prepared[i].SetActive(true); prepared[i] = null;
                }
                workspace.NotifyGroundInventoryCommitted();
                return receipt;
            }
            finally { for (int i = 0; i < prepared.Length; i++) if (prepared[i] != null) DestroyOwned(prepared[i]); }
        }

        private OnlineInventoryCommandReceipt Pickup(OnlineOwnedWorkspace workspace, Transform actor, in OnlineInventoryCommandIntent intent)
        {
            if (!_ground.TryGet(intent.ItemId, out var current) || current.WorldOwnerId != intent.WorldOwnerId
                || (current.Position - actor.position).sqrMagnitude > 9)
                return workspace.RejectInventoryCommand(intent, OnlineInventoryCommandFailure.NotOwned);
            var context = new OnlineContainerAuthorityContext
            {
                CanAdvance = true, WorldId = _ground.WorldId, ActorPosition = actor.position,
                TargetPosition = current.Position, TargetRotation = current.Rotation
            };
            var receipt = workspace.ExecuteInventoryCommand(intent, context);
            if (receipt.Success && !_ground.TryGet(intent.ItemId, out _))
                RemoveModel(intent.ItemId, current.WorldOwnerId);
            if (receipt.Success) workspace.NotifyGroundInventoryCommitted();
            return receipt;
        }

        private void RemoveModel(string id, string expectedOwner)
        {
            if (!_models.TryGetValue(id, out var proxy) || expectedOwner != null && proxy != null && proxy.WorldOwner != expectedOwner) return;
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
            _coins?.Dispose();
            _models.Clear(); _attached.Clear(); _ground.Dispose();
        }
        private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        private static void DestroyOwned(GameObject owned)
        { if (Application.isPlaying) UnityEngine.Object.Destroy(owned); else UnityEngine.Object.DestroyImmediate(owned); }
    }
}

