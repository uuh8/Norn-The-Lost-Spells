using Game.Core;
using Game.Run;
using UnityEngine;
using UnityEngine.UI;
namespace Game.UI
{
    /// <summary>整备意图与只读反馈；按会话发送Core请求，真实资产事务仍由Run裁决。</summary>
    public sealed class RaidPreparationView : MonoBehaviour
    {
        [SerializeField] private SinglePlayerInventorySession _session;
        [SerializeField] private SinglePlayerInventoryController _inventory;
        [SerializeField] private GameObject _controls;
        [SerializeField] private Button _protect, _unprotect, _depart, _starter, _abandon, _retry, _cancelExtraction, _startExtraction;
        [SerializeField] private Text _selection, _message;
        [SerializeField] private bool _externalDeparture;
        [SerializeField] private bool _contextualDetails;
        private string _selected; private uint _version = uint.MaxValue;
        private Font _font;
        private bool _lastWarehouse;
        private static readonly Unity.Profiling.ProfilerMarker UpdateMarker=new Unity.Profiling.ProfilerMarker("Norn.Inventory.ContextActions");
        // 用户拆分界面时可以先只配置保护按钮；未配置的其他动作不参与接线。
        private void OnEnable()
        {
            _protect?.onClick.AddListener(Protect); _unprotect?.onClick.AddListener(Unprotect); if(!_externalDeparture)_depart?.onClick.AddListener(Depart);
            _starter?.onClick.AddListener(Starter); _abandon?.onClick.AddListener(Abandon); _retry?.onClick.AddListener(Retry);
            _cancelExtraction?.onClick.AddListener(CancelExtraction);
            _startExtraction?.onClick.AddListener(StartExtraction);
            EventBus<RaidStateChangedEvent>.Subscribe(OnState);
        }
        private void OnDisable()
        {
            _protect?.onClick.RemoveListener(Protect); _unprotect?.onClick.RemoveListener(Unprotect); _depart?.onClick.RemoveListener(Depart);
            _starter?.onClick.RemoveListener(Starter); _abandon?.onClick.RemoveListener(Abandon); _retry?.onClick.RemoveListener(Retry);
            _cancelExtraction?.onClick.RemoveListener(CancelExtraction);
            _startExtraction?.onClick.RemoveListener(StartExtraction);
            EventBus<RaidStateChangedEvent>.Unsubscribe(OnState);
        }
        private void Send(RaidCommand command) => EventBus<RaidDepartureRequestedEvent>.Publish(new RaidDepartureRequestedEvent
        { InventoryId = _session.ReadOnly?.InventoryId, ItemId = _inventory.SelectedItemId, Command = command });
        public void Protect() { Send(RaidCommand.Protect); _version = uint.MaxValue; }
        public void Unprotect() { Send(RaidCommand.Unprotect); _version = uint.MaxValue; }
        public void Depart() { _inventory.Close(); Send(RaidCommand.Depart); }
        public void Starter() => Send(RaidCommand.StarterKit);
        public void Abandon() { _inventory.Close(); Send(RaidCommand.Abandon); }
        public void Retry() => Send(RaidCommand.RetrySave);
        public void StartExtraction()
        { _inventory.Close();EventBus<ExtractionRequestedEvent>.Publish(new ExtractionRequestedEvent{RaidId=_session.RunCoordinator?.State.RaidId,Start=true}); }
        public void CancelExtraction()
        {
            _inventory.Close();var run=_session.RunCoordinator;
            EventBus<ExtractionRequestedEvent>.Publish(new ExtractionRequestedEvent{RaidId=run?.State.RaidId,Cancel=true});
        }
        private void OnState(RaidStateChangedEvent state)
        { if (_session != null && state.InventoryId == _session.ReadOnly?.InventoryId) { if (_message != null) _message.text = state.Message; _version = uint.MaxValue;
                if(_contextualDetails && _inventory != null && state.Phase==RaidPhase.SaveFailed && !_inventory.IsOpen)_inventory.OpenBackpack(); } }
        private void Update()
        {
            using var marker=UpdateMarker.Auto();
            if (_session == null || _inventory == null || _controls == null) return;
            var run = _session.RunCoordinator; var read = _session.ReadOnly; if (run == null || read == null) return;
            bool show = _inventory.IsOpen || run.State.Phase == RaidPhase.SaveFailed;
            bool related=read.TryGetView(_inventory.SelectedItemId,out var selectedItem) && selectedItem.Kind==ItemKind.Spell;
            bool controls=show && (!_contextualDetails || related || run.State.Phase==RaidPhase.SaveFailed);
            if (_controls.activeSelf != controls) _controls.SetActive(controls); if (!show) return;
            if (_font != _inventory.UiFont && _inventory.UiFont != null)
            { _font = _inventory.UiFont; var labels = _controls.GetComponentsInChildren<Text>(true); for (int i = 0; i < labels.Length; i++) labels[i].font = _font; }
            bool hub = run.State.Phase == RaidPhase.HubReady;
            if (_depart != null) _depart.interactable = hub; if (_starter != null) _starter.interactable = hub;
            if (_abandon != null) _abandon.interactable = !hub && run.ActionsAllowed;
            if (_retry != null) _retry.interactable = run.State.Phase == RaidPhase.SaveFailed;
            if(_cancelExtraction!=null)_cancelExtraction.interactable=run.State.Phase==RaidPhase.Extracting;
            if(_startExtraction!=null)_startExtraction.interactable=run.ActionsAllowed&&run.Extraction!=null&&run.Extraction.CanStart;
            string id = _inventory.SelectedItemId;
            if (_selected == id && _version == read.Version && _lastWarehouse==_inventory.WarehouseMode) return; _selected = id; _version = read.Version;_lastWarehouse=_inventory.WarehouseMode;
            bool spell = read.TryGetView(id, out var item) && item.Kind == ItemKind.Spell;
            if(_contextualDetails)
            {
                // 保护属于选中卡详情；全局冒险动作放页脚，右栏不再常驻整套Debug按钮。
                _selection?.gameObject.SetActive(spell);_protect?.gameObject.SetActive(hub && spell);_unprotect?.gameObject.SetActive(hub && spell);
                _depart?.gameObject.SetActive(false);_starter?.gameObject.SetActive(hub && _inventory.WarehouseMode);
                _abandon?.gameObject.SetActive(!hub && run.ActionsAllowed);_retry?.gameObject.SetActive(run.State.Phase==RaidPhase.SaveFailed);
                _cancelExtraction?.gameObject.SetActive(run.State.Phase==RaidPhase.Extracting);
                _startExtraction?.gameObject.SetActive(run.Extraction!=null && run.Extraction.CanStart);
                _message?.gameObject.SetActive(run.State.Phase==RaidPhase.SaveFailed);
            }
            if (_protect != null) _protect.interactable = hub && spell;
            if (_unprotect != null) _unprotect.interactable = hub && spell;
            if (_selection != null) _selection.text = "保护 " + run.Protection.Count + "/2 · " + (spell ? item.DisplayName + (run.Protection.Contains(id) ? "（已选原卡）" : "（未保护）") : "先点击具体法术卡");
        }
    }
}
