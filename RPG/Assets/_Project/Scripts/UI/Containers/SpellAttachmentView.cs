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
        private IInventoryPresentationSource _sourceIdentity, _onlineRequestSource;
        private ulong _onlineCommand, _onlineActor;
        private uint _onlineEpoch, _onlineInstalledVersion;
        private string _onlineCard, _onlineContainer;
        private ulong _onlineDose;
        private bool _onlineEnabled, _onlineHasReceipt, _displayedHasAttachment, _displayedAttachmentEnabled;
        private string _displayedAttachmentContainer;
        private ulong _displayedAttachmentDose;
        /// <summary>
        /// 线上必须沿原Controller的显式Source；空Source也不能借保留的Hub Session冒充线上资产。
        /// 单机仍允许原场景直接装配_session，不改变原配置/供料事件链。
        /// </summary>
        private IInventoryPresentationSource Source
        {
            get
            {
                if (_inventory != null)
                {
                    var source = _inventory.PresentationSource;
                    if (source != null || _inventory.PresentationIsReadOnly) return source;
                }
                return _session != null ? _session : null;
            }
        }
        public string Feedback => _feedback != null ? _feedback.text : "";
        private void Awake() { _caller = "attachment." + Guid.NewGuid().ToString("N"); }
        private void OnEnable()
        {
            _version = uint.MaxValue;
            _apply.onClick.AddListener(Apply); _clear.onClick.AddListener(Clear);
            EventBus<SpellAttachmentResultEvent>.Subscribe(OnResult); EventBus<SpellMaterialSupplyEvent>.Subscribe(OnSupply);
            EventBus<SpellMaterialOutputResultEvent>.Subscribe(OnOutput);
            EventBus<OnlineInventoryCommandResultEvent>.Subscribe(OnOnlineResult);
        }
        private void OnDisable()
        {
            if (_worldHud != null) _worldHud.SetActive(true);
            _apply.onClick.RemoveListener(Apply); _clear.onClick.RemoveListener(Clear);
            EventBus<SpellAttachmentResultEvent>.Unsubscribe(OnResult); EventBus<SpellMaterialSupplyEvent>.Unsubscribe(OnSupply);
            EventBus<SpellMaterialOutputResultEvent>.Unsubscribe(OnOutput);
            EventBus<OnlineInventoryCommandResultEvent>.Unsubscribe(OnOnlineResult);
            ClearOnlineRequest(); _sourceIdentity = null;
        }
        private void Update()
        {
            using var marker=UpdateMarker.Auto();
            bool open = _inventory != null && _inventory.IsOpen;
            var source = Source; var read = source?.ReadOnly;
            ObserveSource(source, open);
            CompleteOnlineRequestIfInstalled();
            // Gameplay HUD的独立Canvas排序较高；整备时隐藏，避免遮住附着来源与剂量。
            bool overlay=open || _shop!=null && _shop.IsOpen;
            if (_worldHud != null && _worldHud.activeSelf == overlay) _worldHud.SetActive(!overlay);
            bool related=!_contextualDetails || read!=null && read.TryGetView(_inventory.SelectedItemId,out var relatedItem) && relatedItem.Kind==ItemKind.Spell;
            if(_contextualDetails && related && source?.SpellUsesReadOnly!=null && source.SpellUsesReadOnly.TryGetState(_inventory.SelectedItemId,out var use) && use.IsQualification)related=false;
            if (_controls.activeSelf != (open && related)) _controls.SetActive(open && related);
            if (_inventory != null && _inventory.UiFont != null && _feedback.font != _inventory.UiFont) _feedback.font = _inventory.UiFont;
            if (!open || read == null || _inventory.UiFont == null) return;
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
            bool attachmentChanged = card && (_displayedHasAttachment != (item.Attachment != null)
                || item.Attachment != null && (_displayedAttachmentContainer != item.Attachment.ContainerId
                    || _displayedAttachmentDose != item.Attachment.Dose || _displayedAttachmentEnabled != item.Attachment.Enabled));
            bool online = source is IOnlineInventoryCommandContext;
            bool validBag = !online || HasEquippedBag(source, read);
            string preferred = selectionChanged || attachmentChanged ? configured : previous ?? configured;
            for (int i = 0; i < count; i++) if (_items[i].Kind == ItemKind.Container && _items[i].Location.Kind == ItemLocationKind.Backpack
                && (!online || validBag && _items[i].Location.OwnerId == source.EquippedBackpackId))
            {
                _sourceIds.Add(_items[i].InstanceId);
                _options.Add(new Dropdown.OptionData(_items[i].DisplayName + " · 瓶" + (_options.Count) + " · " + _items[i].ContainerTotal + " GMU"));
                if (_items[i].InstanceId == preferred) chosen = _options.Count - 1;
            }
            // 源瓶离包/消耗后，具体卡的设置仍是服务器确认事实。保留只读失效项和原剂量，
            // 不自动改绑第一瓶；再次Apply仍须通过真实包内资格，Clear可显式清除原配置。
            if (online && !string.IsNullOrEmpty(configured) && !_sourceIds.Contains(configured))
            {
                _sourceIds.Add(configured); _options.Add(new Dropdown.OptionData("已配置来源（当前不在随身包）"));
                if (preferred == configured) chosen = _options.Count - 1;
            }
            _sources.ClearOptions(); _sources.AddOptions(_options); _sources.SetValueWithoutNotify(chosen); _sources.RefreshShownValue();
            if (selectionChanged || attachmentChanged) _dose.SetTextWithoutNotify(card && item.Attachment != null ? item.Attachment.Dose.ToString() : "10");
            _displayedHasAttachment = card && item.Attachment != null;
            _displayedAttachmentContainer = _displayedHasAttachment ? item.Attachment.ContainerId : null;
            _displayedAttachmentDose = _displayedHasAttachment ? item.Attachment.Dose : 0;
            _displayedAttachmentEnabled = _displayedHasAttachment && item.Attachment.Enabled;
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
            if (Source is IOnlineInventoryCommandContext) { SubmitOnline(card, source, dose, enabled); return; }
            if (_inventory != null && _inventory.PresentationIsReadOnly) return;
            var read = _session?.ReadOnly; if (read == null) return;
            EventBus<SpellAttachmentRequestedEvent>.Publish(new SpellAttachmentRequestedEvent { InventoryId = read.InventoryId,
                SpellId = card, ContainerId = source, Dose = dose, Enabled = enabled, CallerId = _caller, RequestId = ++_request, ExpectedVersion = read.Version });
        }
        private void OnResult(SpellAttachmentResultEvent result)
        { if (Source is IOnlineInventoryCommandContext || _session?.ReadOnly == null || result.InventoryId != _session.ReadOnly.InventoryId || result.CallerId != _caller) return;
            _feedback.text = result.Success ? "具体卡片供料设置已更新 · 未扣材料" : "设置拒绝：检查产出卡、来源、剂量与格位相容。"; }
        private void OnSupply(SpellMaterialSupplyEvent supply)
        {
            if (Source is IOnlineInventoryCommandContext || _session?.ReadOnly == null || supply.InventoryId != _session.ReadOnly.InventoryId || supply.Status == SpellMaterialSupplyStatus.Disabled) return;
            _feedback.text = supply.Status == SpellMaterialSupplyStatus.InvalidSource ? "供料失效 · 基础法术照常执行"
                : supply.Status == SpellMaterialSupplyStatus.Empty ? "来源为空 · 基础法术照常执行"
                : "本次已附着 " + supply.Approved + "/" + supply.Requested + " GMU";
        }
        private void OnOutput(SpellMaterialOutputResultEvent output)
        {
            if (Source is IOnlineInventoryCommandContext || _session?.ReadOnly == null || output.InventoryId != _session.ReadOnly.InventoryId
                || output.Failure == MaterialTransferFailure.WrongOwnership) return;
            if (output.Resolved) { if (output.Ended) _feedback.text = "材料世界归属已结束 · 未返还库存"; return; }
            _feedback.text = "材料投放暂缓 · 真量保留在橙色标记处";
        }

        private static bool HasEquippedBag(IInventoryPresentationSource source, IInventoryReadOnly read)
            => source != null && read != null && !string.IsNullOrEmpty(source.EquippedBackpackId)
                && read.TryGetView(source.EquippedBackpackId, out var bag) && bag.Kind == ItemKind.Backpack
                && bag.Location.Kind == ItemLocationKind.EquippedBackpack && bag.Location.OwnerId == read.ActorId;
        private static bool OwnsOnlineCard(IInventoryPresentationSource source, IInventoryReadOnly read, string id)
        {
            if (read == null || !read.TryGetView(id, out var card) || card.Kind != ItemKind.Spell || card.IsQualificationSpell) return false;
            if (card.Location.Kind == ItemLocationKind.SpellStorage) return card.Location.OwnerId == read.ActorId;
            if (card.Location.Kind == ItemLocationKind.Backpack)
                return HasEquippedBag(source, read) && card.Location.OwnerId == source.EquippedBackpackId;
            return card.Location.Kind == ItemLocationKind.WandSlot && read.TryGetView(card.Location.OwnerId, out var wand)
                && wand.Kind == ItemKind.Wand && wand.Location.Kind == ItemLocationKind.EquippedWand && wand.Location.OwnerId == read.ActorId;
        }
        /// <summary>
        /// UI只提交认证命令的中立值。CallerId仅关联本视图，CommandId来自原全局序列；
        /// 正式身份、Busy/Frozen与128去重账本仍由原网络/Workspace裁决，设置不会在UI扣瓶。
        /// </summary>
        private void SubmitOnline(string card, string container, ulong dose, bool enabled)
        {
            var source = Source; var read = source?.ReadOnly;
            ObserveSource(source, _inventory != null && _inventory.IsOpen);
            if (_inventory == null || !_inventory.IsOpen || !(source is IOnlineInventoryCommandContext context)
                || context.ActorId == 0 || context.ConnectionEpoch == 0 || read == null || read.Version == 0
                || !OwnsOnlineCard(source, read, card)
                || enabled && (dose == 0 || string.IsNullOrWhiteSpace(container) || !HasEquippedBag(source, read)
                    || !read.TryGetView(container, out var bottle) || bottle.Kind != ItemKind.Container
                    || bottle.Location.Kind != ItemLocationKind.Backpack || bottle.Location.OwnerId != source.EquippedBackpackId))
            { _feedback.text = "设置拒绝：请选择自有产出卡、随身包内瓶和正整数剂量。"; return; }
            var intent = new OnlineInventoryCommandIntent
            {
                CommandId = OnlineInventoryCommandIds.Next(), ConnectionEpoch = context.ConnectionEpoch,
                ExpectedInventoryVersion = read.Version, Kind = OnlineInventoryCommandKind.ConfigureSpellAttachment,
                ItemId = card, AttachmentContainerId = enabled ? container : null,
                AttachmentDose = enabled ? dose : 0, AttachmentEnabled = enabled
            };
            _onlineRequestSource = source; _onlineCommand = intent.CommandId; _onlineActor = context.ActorId;
            _onlineEpoch = context.ConnectionEpoch; _onlineInstalledVersion = 0; _onlineHasReceipt = false;
            _onlineCard = card; _onlineContainer = intent.AttachmentContainerId; _onlineDose = intent.AttachmentDose; _onlineEnabled = enabled;
            _feedback.text = "已提交服务器，等待处理结果与库存快照。";
            EventBus<OnlineInventoryCommandRequestedEvent>.Publish(new OnlineInventoryCommandRequestedEvent
            { ActorId = context.ActorId, CallerId = GetInstanceID(), Intent = intent });
        }
        private void ObserveSource(IInventoryPresentationSource source, bool open)
        {
            if (!ReferenceEquals(_sourceIdentity, source))
            { ClearOnlineRequest(); _sourceIdentity = source; _version = uint.MaxValue; _selected = null; }
            if (!open || _onlineCommand != 0 && (!(source is IOnlineInventoryCommandContext context)
                || context.ActorId != _onlineActor || context.ConnectionEpoch != _onlineEpoch)) ClearOnlineRequest();
        }
        private void OnOnlineResult(OnlineInventoryCommandResultEvent result)
        {
            var source = Source;
            if (_onlineCommand == 0 || _onlineHasReceipt || _inventory == null || !_inventory.IsOpen
                || !ReferenceEquals(source, _onlineRequestSource) || !(source is IOnlineInventoryCommandContext context)
                || context.ActorId != _onlineActor || context.ConnectionEpoch != _onlineEpoch
                || result.ActorId != _onlineActor || result.CallerId != GetInstanceID()
                || result.Receipt.ConnectionEpoch != _onlineEpoch || result.Receipt.CommandId != _onlineCommand) return;
            if (!result.Receipt.Success)
            { ClearOnlineRequest(); _feedback.text = "设置拒绝：服务器未接受本次供料配置，请等待库存后重试。"; return; }
            _onlineHasReceipt = true; _onlineInstalledVersion = result.Receipt.InventoryVersion;
            _feedback.text = "服务器已处理，等待最新库存快照。"; CompleteOnlineRequestIfInstalled();
        }
        private void CompleteOnlineRequestIfInstalled()
        {
            var source = Source; var read = source?.ReadOnly;
            if (_onlineCommand == 0 || !_onlineHasReceipt || _onlineInstalledVersion == 0 || _inventory == null || !_inventory.IsOpen
                || !ReferenceEquals(source, _onlineRequestSource) || !(source is IOnlineInventoryCommandContext context)
                || context.ActorId != _onlineActor || context.ConnectionEpoch != _onlineEpoch
                || read == null || read.Version < _onlineInstalledVersion) return;
            // 对应提交版本必须实际包含本次设置；更高完整版本可以已经再次改卡，以当前真实值显示。
            if (read.Version == _onlineInstalledVersion)
            {
                if (!read.TryGetView(_onlineCard, out var card)) return;
                var setting = card.Attachment;
                if (_onlineEnabled ? setting == null || !setting.Enabled || setting.ContainerId != _onlineContainer || setting.Dose != _onlineDose
                    : setting != null && setting.Enabled) return;
            }
            ClearOnlineRequest(); _version = uint.MaxValue;
            _feedback.text = "具体卡片供料设置已更新 · 未扣材料";
        }
        private void ClearOnlineRequest()
        {
            _onlineRequestSource = null; _onlineCommand = _onlineActor = 0; _onlineEpoch = _onlineInstalledVersion = 0;
            _onlineCard = _onlineContainer = null; _onlineDose = 0; _onlineEnabled = _onlineHasReceipt = false;
        }
    }
}
