using Game.Core;
using Game.Run;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Character
{
    /// <summary>统一手持输入；切换/暂停后先等鼠标释放，禁止旧长按和UI点击进入新物品。</summary>
    public sealed class HeldItemInputController : MonoBehaviour
    {
        [SerializeField] private SinglePlayerInventorySession _session;
        private InputAction[] _select;
        private InputAction _use;
        private bool _waitRelease = true, _focused = true, _paused;
        private uint _selectionVersion;
        public void ConfigurePractice(SinglePlayerInventorySession session){_session=session;_selectionVersion=uint.MaxValue;ResetUse();}
        public bool IsWandMode
        {
            get
            {
                var read = _session != null && _session.AccountInventoryReadOnly
                    ? _session.TryGetAccountHeldPresentation(out var account) ? account.ToolbeltReadOnly : null
                    : _session?.ToolbeltReadOnly;
                return read != null && read.SelectedSlot >= 0 && read.SelectedSlot < HeldItemSlots.WandCount;
            }
        }
        // Hub账号可以切换已确认物品的Presentation，但旧单机世界操作仍没有资产写权。
        // 必须把“允许选中”与“允许使用”分开，不能为恢复数字键而顺便开放本地Pour/Throw。
        public bool CanUseWorld => isActiveAndEnabled && _focused && Time.timeScale > 0 && _session != null
            && !_session.AccountInventoryReadOnly && _session.ToolbeltReadOnly != null;
        public bool CanPour => CanUseWorld && !IsWandMode && !_waitRelease && _use != null && _use.IsPressed();
        private void Awake()
        {
            if (_select != null) return;
            _select = new InputAction[HeldItemSlots.ToolCount];
            for (int i = 0; i < _select.Length; i++)
            {
                int slot = i + 3; // 初始化时闭包允许分配；输入热循环不创建委托。
                string key = HeldItemSlots.KeyLabel(slot);
                _select[i] = new InputAction("Tool" + key, InputActionType.Button, "<Keyboard>/" + key);
                _select[i].performed += _ => Select(slot);
            }
            _use = new InputAction("UseHeldItem", InputActionType.Button, "<Mouse>/leftButton");
        }
        private void OnEnable()
        {
            Awake(); for (int i = 0; i < _select.Length; i++) _select[i].Enable(); _use.Enable();
            EventBus<ToolbeltChangedEvent>.Subscribe(OnSelection); ResetUse();
        }
        private void OnDisable()
        {
            EventBus<ToolbeltChangedEvent>.Unsubscribe(OnSelection);
            if (_select != null) for (int i = 0; i < _select.Length; i++) _select[i].Disable();
            _use?.Disable(); ResetUse();
        }
        private void OnDestroy()
        { if (_select != null) for (int i = 0; i < _select.Length; i++) _select[i].Dispose(); _use?.Dispose(); }
        private void OnApplicationFocus(bool focus) { _focused = focus; ResetUse(); }
        private void Update()
        {
            bool paused = !CanUseWorld;
            if (paused && !_paused) ResetUse(); _paused = paused;
            if (!paused && !_use.IsPressed()) _waitRelease = false;
        }
        public void Select(int slot)
        {
            if (_session != null && _session.AccountInventoryReadOnly)
            {
                if (isActiveAndEnabled && _focused && Time.timeScale > 0 && _session.gameObject.scene == gameObject.scene
                    && _session.ActorTransform == transform && slot >= HeldItemSlots.WandCount && slot < HeldItemSlots.TotalCount)
                    _session.TrySelectAccountHeldTool(slot - HeldItemSlots.WandCount);
                return;
            }
            if (!CanUseWorld) return;
            EventBus<HeldItemSelectionRequestedEvent>.Publish(new HeldItemSelectionRequestedEvent
            { InventoryId = _session.ReadOnly.InventoryId, SlotIndex = slot });
        }
        private void OnSelection(ToolbeltChangedEvent change)
        {
            if (_session != null && _session.AccountInventoryReadOnly)
            {
                if (_session.TryGetAccountHeldPresentation(out var source) && change.InventoryId == source.InventoryId
                    && change.SelectionVersion != _selectionVersion)
                { _selectionVersion = change.SelectionVersion; ResetUse(); }
                return;
            }
            if (_session?.ReadOnly == null || change.InventoryId != _session.ReadOnly.InventoryId || change.SelectionVersion == _selectionVersion) return;
            _selectionVersion = change.SelectionVersion; ResetUse();
        }
        private void ResetUse()
        {
            _waitRelease = true;
            if (_session?.ReadOnly != null) EventBus<HeldItemUseResetEvent>.Publish(new HeldItemUseResetEvent { InventoryId = _session.ReadOnly.InventoryId });
        }
    }
}
