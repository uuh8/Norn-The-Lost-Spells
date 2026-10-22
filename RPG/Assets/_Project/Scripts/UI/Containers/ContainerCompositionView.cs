using Game.Core;
using Game.Run;
using UnityEngine;
using UnityEngine.UI;
using Unity.Profiling;

namespace Game.UI
{
    /// <summary>UGUI 数字复用固定字形字符串，不在连续采集的 Update 中 ToString/拼接账本。</summary>
    public sealed class ContainerCompositionView : MonoBehaviour
    {
        [SerializeField] private SinglePlayerInventorySession _session;
        [SerializeField] private Text[] _digits;
        [SerializeField] private Text _status;
        [SerializeField] private Text[] _labels;
        [SerializeField] private GameObject _compactPanel;
        private readonly MaterialAmount[] _snapshot = new MaterialAmount[8];
        private readonly ulong[] _values = new ulong[10];
        private static readonly string[] Glyphs = { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9" };
        private static readonly string[] Failures = { "采集成功", "采集点太远或请求无效", "库存已改变，请重试", "重复请求已拒绝", "先把容器放入装备背包",
            "资产正在交易", "容量或背包空间不足", "近处没有可采材料", "当前材料后端不支持真实数量采集", "采样范围超出预算", "材料世界未就绪", "固体几何或池底交接失败", "容器不兼容该混合样本" };
        private string _itemId;
        private Font _font;
        private uint _version = uint.MaxValue;
        private static readonly ProfilerMarker UpdateMarker = new ProfilerMarker("Norn.ContainerHud.Update");
        private void Awake()
        {
            _font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei", "Arial" }, 18);
            for (int i = 0; i < _digits.Length; i++) _digits[i].font = _font;
            for (int i = 0; i < _labels.Length; i++) _labels[i].font = _font;
            _status.font = _font;
        }
        private void OnEnable() { EventBus<MaterialTransferResultEvent>.Subscribe(OnResult); EventBus<ContainerSelectionChangedEvent>.Subscribe(OnSelection); EventBus<ContainerUseResultEvent>.Subscribe(OnUse); EventBus<ContainerTransitResolvedEvent>.Subscribe(OnTransitEnded); }
        private void OnDisable() { EventBus<MaterialTransferResultEvent>.Unsubscribe(OnResult); EventBus<ContainerSelectionChangedEvent>.Unsubscribe(OnSelection); EventBus<ContainerUseResultEvent>.Unsubscribe(OnUse); EventBus<ContainerTransitResolvedEvent>.Unsubscribe(OnTransitEnded); }
        private void OnTransitEnded(ContainerTransitResolvedEvent result)
        {
            if (_session?.ReadOnly == null || result.InventoryId != _session.ReadOnly.InventoryId || result.Reason == ContainerTransitEndReason.Deposited) return;
            _status.text = "投掷世界归属已结束 · 未返还瓶壳/材料";
        }
        private void OnUse(ContainerUseResultEvent result)
        {
            if (_session?.ReadOnly == null || result.Transfer.InventoryId != _session.ReadOnly.InventoryId) return;
            if (result.Success) _status.text = result.Kind == ContainerUseKind.Pour ? "持续向前撒出 · 已提交真实材料"
                : result.Kind == ContainerUseKind.ReleaseSolid ? "固体已放回世界" : result.Transfer.RequestId == 0 ? "投掷破碎 · 材料已进入世界" : "投掷已开始 · 瓶已离开背包";
            else if (result.FlightId != null && result.Transfer.Failure != MaterialTransferFailure.WrongOwnership) _status.text = "投放暂缓 · 材料仍在橙色标记处";
            else _status.text = Failures[(int)result.Transfer.Failure];
        }
        private void OnSelection(ContainerSelectionChangedEvent selection)
        { if (_session != null && _session.ReadOnly != null && selection.InventoryId == _session.ReadOnly.InventoryId) Select(selection.ItemId); }
        private void OnDestroy() { if (_font != null) Destroy(_font); }
        private void OnResult(MaterialTransferResultEvent result)
        {
            if (_session == null || _session.ReadOnly == null || result.InventoryId != _session.ReadOnly.InventoryId) return;
            _status.text = Failures[(int)result.Failure];
            if (result.Success && _session.TryCopyContainer(result.ItemId, _snapshot, out _, out _, out _)) _itemId = result.ItemId;
        }
        public void Select(string itemId) { _itemId = itemId; _version = uint.MaxValue; }
        private void Update()
        {
            using var sample = UpdateMarker.Auto();
            if(_compactPanel!=null)
            {
                bool valid=_session?.ReadOnly!=null && _session.ReadOnly.TryGetView(_itemId,out var selected) && selected.Location.Kind==ItemLocationKind.Backpack;
                if(_compactPanel.activeSelf!=valid)_compactPanel.SetActive(valid);
            }
            if (_session == null || _session.ReadOnly == null || _session.ReadOnly.Version == _version || string.IsNullOrEmpty(_itemId)) return;
            _version = _session.ReadOnly.Version;
            if (!_session.TryCopyContainer(_itemId, _snapshot, out int count, out ulong total, out ulong capacity))
            { for (int row = 0; row < 10; row++) SetNumber(row, 0); return; }
            System.Array.Clear(_values, 0, _values.Length); _values[0] = total; _values[1] = capacity;
            for (int i = 0; i < count; i++) _values[_snapshot[i].MaterialKey + 1] = _snapshot[i].Quantity;
            for (int row = 0; row < 10; row++) SetNumber(row, _values[row]);
        }
        private void SetNumber(int row, ulong value)
        {
            for (int column = 19; column >= 0; column--)
            { int digit = (int)(value % 10); _digits[row * 20 + column].text = column == 19 || value > 0 ? Glyphs[digit] : ""; value /= 10; }
        }
    }
}
