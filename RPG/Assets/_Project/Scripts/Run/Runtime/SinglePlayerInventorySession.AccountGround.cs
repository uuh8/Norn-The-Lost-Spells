using System;
using System.Collections.Generic;
using Game.Core;
using UnityEngine;

namespace Game.Run
{
    /// <summary>
    /// 原Hub Session只拥有账号地面的表现生命周期，不拥有账号资产写权。两个Canvas各自安装读模型，
    /// 这里另持同版只读Source和一套代理，避免重复实体，也不让某个Canvas Dispose另一方的地面注册。
    /// </summary>
    public sealed partial class SinglePlayerInventorySession
    {
        private AccountInventoryPresentationSource _accountGroundSource;
        private AccountHeldItemSelection _accountHeldSelection;
        private NearbyPickupController _accountNearby;
        private readonly Dictionary<string, WorldItemPickup> _accountGroundModels = new Dictionary<string, WorldItemPickup>(StringComparer.Ordinal);
        private readonly List<string> _accountGroundRemoved = new List<string>();

        /// <summary>
        /// 已运行的旧本地Hub切到账号Scope时，仅撤下本Session创建的表现；库存/地面记录仍留在旧Profile。
        /// 不Save、不结束冒险、不退役资产。先停用让旧代理立即退出附近/F，再沿Destroy释放其注册身份。
        /// </summary>
        private void ReleaseLocalGroundForAccount()
        {
            while (_spawnedPickups.Count != 0)
            {
                int index = _spawnedPickups.Count - 1;
                var proxy = _spawnedPickups[index]; _spawnedPickups.RemoveAt(index);
                // EditMode的DestroyImmediate会同步发布解绑事件；先移出本列表，避免回调改动正在枚举的集合。
                if (proxy != null) { proxy.gameObject.SetActive(false); DestroyAccountModel(proxy.gameObject); }
            }
            foreach (var root in _worldRoots.Values)
                if (root != null) { root.gameObject.SetActive(false); DestroyAccountModel(root.gameObject); }
            _worldRoots.Clear();
        }

        private bool PrepareAccountGround(ItemCatalogIndex catalog, OnlineAccountInventorySnapshot snapshot, out string error)
        {
            error = null;
            if (_accountGroundSource != null && _accountGroundSource.PlayerId == snapshot.PlayerId && _accountGroundSource.Version == snapshot.Version)
            {
                if (BindAccountGroundActor(out error)) return true;
                // 没有地面物时，纯账号库存UI可先于Actor/Awake安装；不因此补造角色或本地资产。
                if (_accountGroundSource.GroundItems.Count == 0) { error = null; return true; }
                return false;
            }
            if (!AccountInventoryPresentationSource.TryCreate(catalog, snapshot, out var next, out error)) return false;
            var previous = _accountGroundSource; _accountGroundSource = next;
            if (!BindAccountGroundActor(out error))
            {
                if (next.GroundItems.Count == 0) { error = null; previous?.Dispose(); return true; }
                _accountGroundSource = previous; next.Dispose(); return false;
            }
            try
            {
                if (!SyncAccountGroundModels(out error)) { ClearAccountGround(); previous?.Dispose(); return false; }
            }
            catch (Exception failure)
            { ClearAccountGround(); previous?.Dispose(); error = "账号地面表现安装失败：" + failure.GetType().Name; return false; }
            previous?.Dispose(); return true;
        }

        private bool BindAccountGroundActor(out string error)
        {
            error = null;
            if (_accountNearby == null) _accountNearby = GetComponent<NearbyPickupController>();
            if (WorldActor == null && _accountNearby != null) WorldActor = _accountNearby.Player;
            if (WorldActor == null || WorldActor.gameObject.scene != gameObject.scene || _accountNearby == null)
            { error = "原据点角色或附近拾取桥尚未准备，未发布半份地面表现。"; return false; }
            _accountGroundSource.BindGroundContext(WorldActor, WorldSceneKey);
            _accountNearby.ConfigureAccount(_accountGroundSource, WorldActor);
            return true;
        }
        private void OnAccountGroundScopeChanged(OnlineAccountInventoryHubChangedEvent change)
        {
            if (change.SceneHandle != gameObject.scene.handle) return;
            if (!OnlineAccountInventoryHubScope.TryGetSummaryForScene(gameObject.scene.handle, out var summary)
                || summary.AssetsLocked || !string.IsNullOrEmpty(summary.ActiveRunId)
                || _accountGroundSource != null && summary.PlayerId != _accountGroundSource.PlayerId)
                _accountHeldSelection = null;
            // 新Profile先发布Scope再由Canvas安装。若消费者尚未成功安装新版本，不能继续留下旧模型；
            // 正常安装会随同一事件重建当前版本，失败则保持没有可见旧账号物品的安全状态。
            if (!HasCurrentAccountGroundScope()) ClearAccountGround();
        }

        /// <summary>
        /// 数字键只读选择沿当前账号Scope和同Scene角色解析，不要求旧本地ReadOnly存在。
        /// 此接口不授予施法/采集/投掷写权；资产租约、Profile未安装或旧Scene仍一律拒绝。
        /// </summary>
        public bool TryGetAccountHeldPresentation(out AccountInventoryPresentationSource source)
        {
            source = null;
            if (!isActiveAndEnabled || !TryGetAccountHubSummary(out var summary)
                || _accountGroundSource == null || _accountGroundSource.ReadOnly == null
                || _accountGroundSource.PlayerId != summary.PlayerId || _accountGroundSource.Version != summary.Version
                || WorldActor == null || WorldActor.gameObject.scene != gameObject.scene) return false;
            source = _accountGroundSource; return true;
        }
        public bool TrySelectAccountHeldTool(int toolSlot)
            => TryGetAccountHeldPresentation(out var source) && source.TrySelectToolForPresentation(toolSlot);
        public bool TrySelectConfirmedAccountWandPresentation(int wandSlot)
            => TryGetAccountHeldPresentation(out var source) && source.TrySelectConfirmedWandForPresentation(wandSlot);

        public bool TryGetAccountGroundSource(out AccountInventoryPresentationSource source, out NearbyPickupController nearby)
        {
            source = null; nearby = null;
            if (!AccountInventoryReadOnly || _accountGroundSource == null || _accountGroundSource.ReadOnly == null
                || !BindAccountGroundActor(out _)) return false;
            source = _accountGroundSource; nearby = _accountNearby; return true;
        }

        /// <summary>只准备原模型的稳定落点，不迁移本地Store。HTTP未知或失败时模型仍不显示，原物品仍在原位。</summary>
        public bool TryPrepareAccountGroundDrop(string itemId, out OnlineAccountWorldItemPose pose, out string error)
        {
            pose = null; error = "当前账号地面来源或原实例尚未就绪。";
            if (!TryGetAccountGroundSource(out var source, out _) || string.IsNullOrEmpty(itemId)
                || !source.PresentationItems.TryGet(itemId, out var item) || item.EntryBoundBackpack
                || !source.PresentationItems.TryGetLocation(itemId, out var location) || location.Kind == ItemLocationKind.WorldItem
                || !source.PresentationItems.Catalog.TryGet(item.ContentId, out var definition)) return false;
            GameObject model = null;
            try
            {
                model = WorldItemVisualFactory.Create(definition);
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(model, gameObject.scene);
                // 沿原普通Drop候选与支撑参数；不启用投掷撞墙的额外搜索，也不改碰撞或拾取半径。
                int sequence = _dropSequence++;
                model.transform.position = WorldActor.position + WorldActor.forward * 1.2f + Vector3.up * .3f + WorldActor.right * (sequence % 3) * .35f;
                if (!_groundPlacement.TryPlace(model, gameObject.scene, WorldActor, _dropGroundMask,
                    _groundProbeAbove, _groundProbeBelow, _groundMinNormalY, _groundClearance))
                { error = "未找到完整支撑地面，物品保留原位。"; return false; }
                var p = model.transform.position; var q = model.transform.rotation;
                pose = new OnlineAccountWorldItemPose { SceneKey = WorldSceneKey, WorldSceneKey = WorldSceneKey,
                    PositionX = p.x, PositionY = p.y, PositionZ = p.z,
                    RotationX = q.x, RotationY = q.y, RotationZ = q.z, RotationW = q.w, PoseKind = (int)WorldItemPoseKind.Grounded };
                error = null; return true;
            }
            catch (Exception failure) { error = "物品模型准备失败，物品保留原位：" + failure.GetType().Name; return false; }
            finally { if (model != null) DestroyAccountModel(model); }
        }

        private bool SyncAccountGroundModels(out string error)
        {
            error = null;
            var source = _accountGroundSource;
            if (source == null || source.ReadOnly == null || WorldActor == null) return false;
            _accountGroundRemoved.Clear();
            foreach (var pair in _accountGroundModels)
                if (!source.GroundItems.TryGetValue(pair.Key, out var record) || record.Pose.WorldSceneKey != WorldSceneKey)
                    _accountGroundRemoved.Add(pair.Key);
            foreach (string id in _accountGroundRemoved) RemoveAccountGroundModel(id);
            foreach (var pair in source.GroundItems)
            {
                var world = pair.Value; var pose = world.Pose;
                if (pose.WorldSceneKey != WorldSceneKey) continue; // 别的私人Hub场景记录不能显示在当前地图。
                if (!source.PresentationItems.TryGet(pair.Key, out var item) || !source.PresentationItems.Catalog.TryGet(item.ContentId, out var definition))
                { error = "地面模型缺少真实实例或目录。"; return false; }
                bool created = !_accountGroundModels.TryGetValue(pair.Key, out var proxy) || proxy == null;
                if (!created && proxy.WorldOwner != world.WorldOwnerId)
                { RemoveAccountGroundModel(pair.Key); created = true; }
                if (created)
                {
                    var record = new HubWorldItemRecord { HasVisualScaleOverride = pose.HasVisualScaleOverride,
                        VisualScaleRatio = new Vector3(pose.VisualScaleX, pose.VisualScaleY, pose.VisualScaleZ) };
                    var model = WorldItemVisualFactory.Create(definition, record: record);
                    model.transform.SetParent(WorldRoot(gameObject.scene), false);
                    proxy = model.GetComponent<WorldItemPickup>();
                }
                if (!proxy.BindAccount(source, pair.Key, WorldActor))
                { if (created) DestroyAccountModel(proxy.gameObject); error = "账号地面代理绑定失败。"; return false; }
                _accountGroundModels[pair.Key] = proxy;
                proxy.gameObject.SetActive(true);
            }
            ActivateAccountGround(); return true;
        }
        private void ActivateAccountGround()
        {
            // Hub停用期间没有事件订阅，可能已经退出账号或被副本锁定。重新启用前必须重新核对，
            // 不能因缓存还在就重现原账号实体；这里只释放表现/读模型，不保存或清除真实资产。
            if (!HasCurrentAccountGroundScope()) { ClearAccountGround(); return; }
            foreach (var root in _worldRoots.Values) if (root != null) root.gameObject.SetActive(isActiveAndEnabled);
        }
        private bool HasCurrentAccountGroundScope()
            => _accountGroundSource != null && _accountGroundSource.ReadOnly != null
                && OnlineAccountInventoryHubScope.TryGetSummaryForScene(gameObject.scene.handle, out var summary)
                && !summary.AssetsLocked && string.IsNullOrEmpty(summary.ActiveRunId)
                && summary.PlayerId == _accountGroundSource.PlayerId && summary.Version == _accountGroundSource.Version;
        private void RemoveAccountGroundModel(string id)
        {
            if (!_accountGroundModels.TryGetValue(id, out var proxy)) return;
            _accountGroundModels.Remove(id);
            if (proxy != null) { proxy.ReleaseOnlineBinding(); proxy.gameObject.SetActive(false); DestroyAccountModel(proxy.gameObject); }
        }
        private void ClearAccountGround()
        {
            foreach (var proxy in _accountGroundModels.Values)
                if (proxy != null) { proxy.ReleaseOnlineBinding(); proxy.gameObject.SetActive(false); DestroyAccountModel(proxy.gameObject); }
            _accountGroundModels.Clear(); _accountGroundRemoved.Clear();
            _accountGroundSource?.Dispose(); _accountGroundSource = null;
        }
        private static void DestroyAccountModel(GameObject model)
        { if (Application.isPlaying) Destroy(model); else DestroyImmediate(model); }
    }
}
