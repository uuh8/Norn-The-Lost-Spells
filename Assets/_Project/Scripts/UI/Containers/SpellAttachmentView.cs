using System;
using System.Collections.Generic;
using Game.Core;
using Game.Run;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>整备中对具体卡下配置命令；UI只有只读投影，连续帧不生成来源列表或文字。</summary>
    public sealed class SpellAttachmentView : MonoBehaviour
    {
        [SerializeField] private SinglePlayerInventorySession _session;
        [SerializeField] private SinglePlayerInventoryController _inventory;
        [SerializeField] private GameObject _controls;
        [SerializeField] private GameObject _worldHud;
        [SerializeField] private ShopController _shop;
        [SerializeField] private Dropdown _sources;
        [SerializeField] private InputField _dose;
        [SerializeField] private Button _apply, _clear;
        [SerializeField] private Text _description, _feedback;
        [SerializeField] private bool _contextualDetails;
        private InventoryItemSnapshot[] _items = Array.Empty<InventoryItemSnapshot>();
        private readonly List<string> _sourceIds = new List<string>();
        private static readonly Unity.Profiling.ProfilerMarker UpdateMarker=new Unity.Profiling.ProfilerMarker("Norn.Inventory.AttachmentDetails");
        private readonly List<Dropdown.OptionData> _options = new List<Dropdown.OptionData>();
        private string _selected;
        private uint _version = uint.MaxValue;
        private ulong _request;
        private string _caller;
        public string Feedback => _feedback != null ? _feedback.text : "";
        private void Awake() { _caller = "attachment." + Guid.NewGuid().ToString("N"); }
        private void OnEnable()
        {
            _version = uint.MaxValue;
            _apply.onClick.AddListener(Apply); _clear.onClick.AddListener(Clear);
            EventBus<SpellAttachmentResultEvent>.Subscribe(OnResult); EventBus<SpellMaterialSupplyEvent>.Subscribe(OnSupply);
            EventBus<SpellMaterialOutputResultEvent>.Subscribe(OnOutput);
        }
        private void OnDisable()
        {
            if (_worldHud != null) _worldHud.SetActive(true);
            _apply.onClick.RemoveListener(Apply); _clear.onClick.RemoveListener(Clear);
            EventBus<SpellAttachmentResultEvent>.Unsubscribe(OnResult); EventBus<SpellMaterialSupplyEvent>.Unsubscribe(OnSupply);
            EventBus<SpellMaterialOutputResultEvent>.Unsubscribe(OnOutput);
        }
        private void Update()
        {
            using var marker=UpdateMarker.Auto();
            bool open = _inventory != null && _inventory.IsOpen;
            // Gameplay HUD的独立Canvas排序较高；整备时隐藏，避免遮住附着来源与剂量。
            bool overlay=open || _shop!=null && _shop.IsOpen;
            if (_worldHud != null && _worldHud.activeSelf == overlay) _worldHud.SetActive(!overlay);
            bool related=!_contextualDetails || _session?.ReadOnly!=null && _session.ReadOnly.TryGetView(_inventory.SelectedItemId,out var relatedItem) && relatedItem.Kind==ItemKind.Spell;
            if(_contextualDetails && related && _session.SpellUsesReadOnly.TryGetState(_inventory.SelectedItemId,out var use) && use.IsQualification)related=false;
            if (_controls.activeSelf != (open && related)) _controls.SetActive(open && related);
            if (_inventory != null && _inventory.UiFont != null && _feedback.font != _inventory.UiFont) _feedback.font = _inventory.UiFont;
            var read = _session?.ReadOnly; if (!open || read == null || _inventory.UiFont == null) return;
            if(!related){if(_feedback!=null)_feedback.gameObject.SetActive(false);return;}if(_feedback!=null && !_feedback.gameObject.activeSelf)_feedback.gameObject.SetActive(true);
            string selected = _inventory.SelectedItemId;
            if (_version == read.Version && _selected == selected) return;
            bool selectionChanged = selected != _selected; _selected = selected; _version = read.Version;
            if (_items.Length < read.Count) _items = new InventoryItemSnapshot[read.Count]; int count = read.CopyItemsTo(_items);
            string previous = _sources.value >= 0 && _sources.value < _sourceIds.Count ? _sourceIds[_sources.value] : null;
            _options.Clear(); _sourceIds.Clear(); _options.Add(new Dropdown.OptionData("选择来源容器")); _sourceIds.Add(null);
            int chosen = 0;
            bool card = read.TryGetView(selected, out var item) && item.Kind == ItemKind.Spell;
            string configured = card ? item.Attachment?.ContainerId : null;
            for (int i = 0; i < count; i++) if (_items[i].Kind == ItemKind.Container && _items[i].Location.Kind == ItemLocationKind.Backpack)
            {
                _sourceIds.Add(_items[i].InstanceId);
                _options.Add(new Dropdown.OptionData(_items[i].DisplayName + " · 瓶" + (_options.Count) + " · " + _items[i].ContainerTotal + " GMU"));
                if (_items[i].InstanceId == (selectionChanged ? configured : previous ?? configured)) chosen = _options.Count - 1;
            }
            _sources.ClearOptions(); _sources.AddOptions(_options); _sources.SetValueWithoutNotify(chosen); _sources.RefreshShownValue();
            if (selectionChanged) _dose.SetTextWithoutNotify(card && item.Attachment != null ? item.Attachment.Dose.ToString() : "10");
            _apply.interactable = _clear.interactable = card;
            _description.text = !card ? "在法杖或仓库中点击一份产出法术，再设置供料。"
                : item.DisplayName + (item.Attachment == null ? " · 附着关闭" : " · 每次产出请求 " + item.Attachment.Dose + " GMU") + "\n不足则部分附着；空瓶或失效来源仍可施基础法术。";
            _description.font = _feedback.font = _inventory.UiFont;
            var labels = _controls.GetComponentsInChildren<Text>(true);
            for (int i = 0; i < labels.Length; i++) labels[i].font = _inventory.UiFont;
        }
        public void Apply()
        {
            if (!ulong.TryParse(_dose.text, out ulong dose) || dose == 0) { _feedback.text = "剂量需要正整数GMU。"; return; }
            string source = _sources.value >= 0 && _sources.value < _sourceIds.Count ? _sourceIds[_sources.value] : null;
            Submit(_inventory.SelectedItemId, source, dose, true);
        }
        public void Clear() => Submit(_inventory.SelectedItemId, null, 0, false);
        public void Submit(string card, string source, ulong dose, bool enabled)
        {
            var read = _session?.ReadOnly; if (read == null) return;
            EventBus<SpellAttachmentRequestedEvent>.Publish(new SpellAttachmentRequestedEvent { InventoryId = read.InventoryId,
                SpellId = card, ContainerId = source, Dose = dose, Enabled = enabled, CallerId = _caller, RequestId = ++_request, ExpectedVersion = read.Version });
        }
        private void OnResult(SpellAttachmentResultEvent result)
        { if (_session?.ReadOnly == null || result.InventoryId != _session.ReadOnly.InventoryId || result.CallerId != _caller) return;
            _feedback.text = result.Success ? "具体卡片供料设置已更新 · 未扣材料" : "设置拒绝：检查产出卡、来源、剂量与格位相容。"; }
        private void OnSupply(SpellMaterialSupplyEvent supply)
        {
            if (_session?.ReadOnly == null || supply.InventoryId != _session.ReadOnly.InventoryId || supply.Status == SpellMaterialSupplyStatus.Disabled) return;
            _feedback.text = supply.Status == SpellMaterialSupplyStatus.InvalidSource ? "供料失效 · 基础法术照常执行"
                : supply.Status == SpellMaterialSupplyStatus.Empty ? "来源为空 · 基础法术照常执行"
                : "本次已附着 " + supply.Approved + "/" + supply.Requested + " GMU";
        }
        private void OnOutput(SpellMaterialOutputResultEvent output)
        {
            if (_session?.ReadOnly == null || output.InventoryId != _session.ReadOnly.InventoryId
                || output.Failure == MaterialTransferFailure.WrongOwnership) return;
            if (output.Resolved) { if (output.Ended) _feedback.text = "材料世界归属已结束 · 未返还库存"; return; }
            _feedback.text = "材料投放暂缓 · 真量保留在橙色标记处";
        }
    }
}
