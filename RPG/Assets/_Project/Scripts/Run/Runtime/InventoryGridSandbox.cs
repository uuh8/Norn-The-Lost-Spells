using System;
using Game.Core;
using UnityEngine;

namespace Game.Run
{
    /// <summary>
    /// M1-T2 独立灰盒，Inspector ContextMenu 驱动离散命令，不接入玩家/关卡/真实库存。
    /// 正式 UGUI 和输入在 M1-T3；此处每次运行重建测试资产，空间图只读权威归属表。
    /// </summary>
    public sealed class InventoryGridSandbox : MonoBehaviour
    {
        public enum TestItem : byte { Bar, Corner, Small, SpellA, SpellB, SpellC, BottleA, BottleB }
        [SerializeField] private ItemCatalog _catalog;
        [SerializeField] private TestItem _selectedItem;
        [SerializeField] private TestItem _mergeTarget = TestItem.SpellA;
        [SerializeField] private int _x, _y;
        [SerializeField, Range(0, 3)] private int _quarterTurns;
        [SerializeField] private string _lastResult = "进入 Play Mode 后使用组件菜单操作；Game 视图启用 Gizmos。";
        private ItemOwnershipStore _store;
        private WorldItemPickup _lastPickup;
        private ulong _requestId;
        private static readonly string[] _itemIds = { "bar", "corner", "small", "spell-a", "spell-b", "spell-c", "bottle-a", "bottle-b" };
        public ItemOwnershipStore Store => _store;

        private void Awake()
        {
            if (_catalog == null || !_catalog.TryCreateIndex(out ItemCatalogIndex index, out string error))
            { GameLog.Error("背包灰盒缺少有效目录。", "InventorySandbox"); enabled = false; return; }
            _store = new ItemOwnershipStore(index);
            bool ready = Add("bag", "sandbox.bag") && Add("bar", "sandbox.bar") && Add("corner", "sandbox.corner")
                && Add("small", "sandbox.small") && Add("spell-a", "sandbox.spell", 3) && Add("spell-b", "sandbox.spell", 3)
                && Add("spell-c", "sandbox.spell", 8) && Add("bottle-a", "sandbox.bottle") && Add("bottle-b", "sandbox.bottle");
            ready &= _store.TryMove("bag", new ItemLocation(ItemLocationKind.EquippedBackpack, "sandbox-player", 0, 0, 0, 0)).Success;
            ready &= Place("bar", 0, 0) && Place("corner", 0, 1) && Place("small", 1, 1)
                && Place("spell-a", 2, 0) && Place("spell-b", 2, 0) && Place("bottle-a", 3, 0) && Place("bottle-b", 3, 0);
            if (!ready) { GameLog.Error("背包灰盒初始配置放置失败。", "InventorySandbox"); enabled = false; return; }
            _lastResult = "就绪：4×4 灰盒（测试参数）；SpellA/SpellB 各3次，共用格位；SpellC 8次在仓库。";
        }

        private void OnEnable() => EventBus<InventoryCommandResultEvent>.Subscribe(OnResult);
        private void OnDisable() => EventBus<InventoryCommandResultEvent>.Unsubscribe(OnResult);
        private bool Add(string id, string content, int charges = 0) => _store.TryAdd(new RuntimeItemState(id, content, charges), ItemLocation.Warehouse);
        private bool Place(string id, int x, int y) => _store.TryMove(id, new GridPlacement(x, y, 0).InBackpack("bag")).Success;
        private string SelectedId => _itemIds[(int)_selectedItem];
        private GridPlacement Placement => new GridPlacement(_x, _y, _quarterTurns);
        private bool Ready => Application.isPlaying && enabled && _store != null;

        [ContextMenu("01 移动所选实例到目标格")]
        private void MoveItem() { if (Ready) Show(InventoryMoveOps.TryPlace(_store, SelectedId, "bag", Placement, _store.Version)); }
        [ContextMenu("02 整组堆叠移动到目标格")]
        private void MoveStack() { if (Ready) Show(ItemStackOps.TryMoveStack(_store, SelectedId, "bag", Placement, _store.Version)); }
        [ContextMenu("03 拆出所选实例到目标格")]
        private void SplitItem() { if (Ready) Show(ItemStackOps.TrySplit(_store, SelectedId, "bag", Placement, _store.Version)); }
        [ContextMenu("04 合并所选实例到 Merge Target")]
        private void MergeItem() { if (Ready) Show(ItemStackOps.TryMerge(_store, SelectedId, _itemIds[(int)_mergeTarget], _store.Version)); }
        [ContextMenu("05 原位旋转90度")]
        private void RotateItem()
        {
            if (!Ready || !_store.TryGetLocation(SelectedId, out ItemLocation source)) return;
            if (source.Kind != ItemLocationKind.Backpack) { Show(new InventoryOperationResult(false, InventoryFailureReason.InvalidOwnership, _store.Version)); return; }
            Show(ItemStackOps.TryMoveStack(_store, SelectedId, "bag", new GridPlacement(source.X, source.Y, (source.QuarterTurns + 1) % 4), _store.Version));
        }
        [ContextMenu("06 减少所选法术一次并放到目标格")]
        private void ChangeCharges()
        {
            if (!Ready || !_store.TryGet(SelectedId, out RuntimeItemState item)) return;
            _store.Catalog.TryGetRules(item.ContentId, out InventoryItemRules rules);
            if (rules.Kind != ItemKind.Spell || item.HasUnlimitedCharges || item.RemainingCharges <= 0)
            { Show(new InventoryOperationResult(false, InventoryFailureReason.InvalidState, _store.Version)); return; }
            Show(ItemStackOps.TryChangeState(_store, new RuntimeItemState(item.InstanceId, item.ContentId, item.RemainingCharges - 1,
                item.HasUnlimitedCharges, item.StackStateKey), Placement.InBackpack("bag"), _store.Version));
        }
        [ContextMenu("07 丢弃所选实例到地面")]
        private void DropItem()
        {
            if (!Ready) return;
            // 先创建隐藏代理，失败销毁；只有资产事务成功才能让它成为可见拾取物。
            GameObject proxy = GameObject.CreatePrimitive(PrimitiveType.Cube);
            proxy.SetActive(false);
            proxy.name = "WorldPickup_" + SelectedId;
            proxy.transform.position = transform.position + new Vector3(6f, .3f, _requestId % 4);
            proxy.transform.localScale = Vector3.one * .5f;
            var pickup = proxy.AddComponent<WorldItemPickup>();
            string worldOwner = Guid.NewGuid().ToString("N");
            InventoryOperationResult result = InventoryMoveOps.TryDrop(_store, SelectedId, worldOwner, _store.Version);
            if (!result.Success) Destroy(proxy);
            else if (pickup.TryBind(_store, SelectedId, worldOwner)) { _lastPickup = pickup; proxy.SetActive(true); }
            else { GameLog.Error("新世界代理绑定失败，需检查唯一代理生命周期。", "InventorySandbox"); Destroy(proxy); }
            Show(result);
        }
        [ContextMenu("08 拾取最后丢弃实例到目标格")]
        private void CollectItem()
        {
            if (!Ready || _lastPickup == null) return;
            EventBus<InventoryCommandEvent>.Publish(new InventoryCommandEvent
            {
                Kind = InventoryCommandKind.Pickup, RequestId = ++_requestId,
                ItemId = _lastPickup.ItemId, WorldOwner = _lastPickup.WorldOwner, OwnerId = "bag",
                X = _x, Y = _y, QuarterTurns = _quarterTurns, ExpectedVersion = _store.Version
            });
        }
        [ContextMenu("09 打印完整身份与归属快照")]
        private void PrintItems()
        {
            if (!Ready) return;
            foreach (InventoryEntry entry in _store)
                GameLog.Info($"ID={entry.Item.InstanceId} content={entry.Item.ContentId} charges={entry.Item.RemainingCharges} "
                    + $"location={entry.Location.Kind}/{entry.Location.OwnerId} ({entry.Location.X},{entry.Location.Y}) r={entry.Location.QuarterTurns}", "InventorySandbox");
        }
        private void Show(InventoryOperationResult result)
        {
            _lastResult = $"{SelectedId}: {(result.Success ? "成功" : result.Reason.ToString())}, version={result.Version}, assets={_store.Count}";
            GameLog.Info(_lastResult, "InventorySandbox");
        }
        private void OnResult(InventoryCommandResultEvent result)
        {
            // 灰盒只展示自己的拾取请求；其他会话/物品反馈不会覆盖此面板状态。
            if (_lastPickup == null || result.RequestId != _requestId || result.ItemId != _lastPickup.ItemId) return;
            Show(new InventoryOperationResult(result.Success, result.Reason, result.Version));
        }
        private void OnDrawGizmos()
        {
            if (_store == null || !enabled) return;
            _store.TryGet("bag", out RuntimeItemState bag);
            _store.Catalog.TryGetRules(bag.ContentId, out InventoryItemRules backpack);
            Gizmos.color = Color.gray;
            for (int y = 0; y < backpack.BackpackHeight; y++)
                for (int x = 0; x < backpack.BackpackWidth; x++) Gizmos.DrawWireCube(CellCenter(x, y), new Vector3(.95f, .05f, .95f));
            foreach (InventoryEntry entry in _store)
            {
                ItemLocation location = entry.Location;
                if (location.Kind != ItemLocationKind.Backpack || location.OwnerId != "bag") continue;
                _store.Catalog.TryGetRules(entry.Item.ContentId, out InventoryItemRules rules);
                Gizmos.color = entry.Item.ContentId == "sandbox.bar" ? Color.cyan
                    : entry.Item.ContentId == "sandbox.corner" ? Color.yellow
                    : entry.Item.ContentId == "sandbox.spell" ? Color.magenta : Color.green;
                for (int i = 0; i < rules.Shape.CellCount; i++)
                {
                    Vector2Int cell = rules.Shape.GetCell(i, location.QuarterTurns);
                    Gizmos.DrawCube(CellCenter(location.X + cell.x, location.Y + cell.y), new Vector3(.8f, .1f, .8f));
                }
            }
        }
        private Vector3 CellCenter(int x, int y) => transform.position + new Vector3(x + .5f, .1f, -y - .5f);
    }
}
