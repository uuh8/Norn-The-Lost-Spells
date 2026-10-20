using System;
using System.Collections.Generic;
using System.Collections;
using Game.Core;
using Game.Run;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>
    /// 整备 UI 只读快照＋命令入口。Ghost 是暂时展示，拖拽开始/旋转/取消不移动资产；
    /// 快照、视图池只在打开或版本变化时重建，持续拖拽仅换坐标。
    /// </summary>
    public sealed class SinglePlayerInventoryController : MonoBehaviour
    {
        [SerializeField] private SinglePlayerInventorySession _session;
        [SerializeField] private HubInteractionController _hubAccess;
        [SerializeField] private RunPauseCoordinator _pauseCoordinator;
        [SerializeField] private GameObject _panelRoot;
        [SerializeField] private InventoryGridView _gridView;
        [SerializeField] private WandInventoryView _wandsView;
        [SerializeField] private RectTransform _warehouseRoot, _groundRoot;
        [SerializeField] private InventoryDropTarget _warehouseTarget, _groundTarget, _backpackTarget;
        [SerializeField] private InventoryItemView _itemPrefab, _ghost;
        [SerializeField,Tooltip("各类Slot的独立美术模板；留空使用项目默认模板集，不写回Scene。")]
        private InventorySlotPrefabs _slotPrefabs;
        private bool _stylesResolved;
        public InventorySlotPrefabs SlotPrefabs
        {
            get
            {
                if(!_stylesResolved){_stylesResolved=true;if(_slotPrefabs==null)_slotPrefabs=InventorySlotPrefabs.LoadDefault();}
                return _slotPrefabs;
            }
        }
        [SerializeField] private Text _details, _status;
        private InventoryInfoPopup _infoPopup;
        public void ShowItemInfo(string id,Vector2 pointer,Camera camera)
        {
            if(!_open || _read==null || !_read.TryGetView(id,out var item))return;
            if(_infoPopup==null && SlotPrefabs!=null && SlotPrefabs.InfoPopup!=null)
                _infoPopup=Instantiate(SlotPrefabs.InfoPopup,_panelRoot.transform);
            if(_infoPopup!=null){CancelDrag();_infoPopup.Show(item.DisplayName,pointer,camera,UiFont);}
        }
        [SerializeField] private InputActionReference _toggleAction, _rotateAction, _cancelAction, _splitAction;
        [SerializeField] private bool _openOnStart = true;
        [SerializeField] private InventoryWorkspaceView _workspace;
        [SerializeField] private BackpackWorkspaceView _backpackWorkspace;
        [SerializeField] private SpellStorageView _spellStorage;
        [SerializeField] private SinglePlayerInventoryController _otherWorkspace;
        [SerializeField] private NearbyPickupController _nearby;
        [SerializeField] private NearbyItemGridView _nearbyGrid;
        [SerializeField] private bool _warehouseTiles;
        [SerializeField, Min(48)] private float _warehouseTileSize = 112;
        [SerializeField] private WarehouseCatalogGridView _warehouseGrid;
        [SerializeField] private WarehouseWorkspaceView _warehouseWorkspace;
        [SerializeField, Tooltip("商店内嵌库存不拥有Tab、Esc和Pause；由ShopController统一开闭。")]
        private bool _embedded;
        public event Action<string> SelectionChanged;
        private bool _warehouseMode;
        public bool WarehouseMode=>_warehouseMode && (_session.RunCoordinator==null || _session.RunCoordinator.State.Phase==RaidPhase.HubReady);
        public SinglePlayerInventorySession Session=>_session;
        public void ConfigurePracticeWorkspace(SinglePlayerInventorySession session,NearbyPickupController nearby,RunPauseCoordinator pause)
        {_session=session;_nearby=nearby;_pauseCoordinator=pause;_hubAccess=null;_otherWorkspace=null;_openOnStart=false;}
        public void OpenWarehouse()
        {
            // 用户拆成两套Canvas后，随身控制器把站点请求交给原仓库控制器，避免再次混用布局。
            if (_backpackWorkspace!=null && _otherWorkspace!=null) { _otherWorkspace.OpenWarehouse();return; }
            // 视图访问也检查空间/阶段；真实资产命令仍由Run独立复核，不能把Tab随身界面变成远程仓库。
            if (_session.RunCoordinator != null && _session.RunCoordinator.State.Phase != RaidPhase.HubReady) return;
            var hub = _hubAccess;
            if (hub != null && (!hub.TryGetStation(out var station) || station.Kind != HubStationKind.Warehouse)) return;
            _warehouseMode=true;Open();
        }
        public void OpenBackpack(){_warehouseMode=false;Open();}
        public void RefreshPresentation()=>Refresh();
        public void DropSelected()
        {
            if(!_open || _read==null || !_read.TryGetView(_selectedId,out var item) || item.Location.Kind==ItemLocationKind.WorldItem)return;
            BeginDrag(item,Vector2.zero,null,true);SubmitToTarget(InventoryDropKind.Ground);
        }
        private readonly List<InventoryItemView> _warehouseViews = new List<InventoryItemView>();
        private readonly List<InventoryItemView> _groundViews = new List<InventoryItemView>();
        private InventoryItemSnapshot[] _items = Array.Empty<InventoryItemSnapshot>();
        private int _count;
        private IInventoryReadOnly _read;
        private bool _open, _dragging, _wholeStack, _waitingOpen;
        private InventoryItemSnapshot _dragSource;
        private uint _dragVersion;
        private int _turns;
        private Vector2 _grabPoint;
        private float _dragCellSize;
        private Vector2 _lastPointer;
        private Camera _pointerCamera;
        private InventoryViewportPan _backpackPan,_nearbyPan;
        private string _selectedId, _viewBackpackId;
        private ulong _requestId;
        private bool _pendingBackpackRefresh;
        private uint _dragToolVersion;
        private bool _previewVisible;
        private bool _outsideDropHint;
        private string _revealDroppedId;
        private readonly InventoryArrangementDraft _draft=new InventoryArrangementDraft();
        private bool _placingDropped;
        private GridPlacement _droppedPlacement;
        public bool IsArrangementInvalid(string id)=>_draft.Contains(id) || (_nearbyGrid!=null && _nearbyGrid.IsInvalid(id));
        public bool IsPendingPickup(string id)=>_draft.Active && _draft.Source.Location.Kind==ItemLocationKind.WorldItem && _draft.Contains(id);
        public bool IsViewSelected(in InventoryItemSnapshot item)
        {
            if(item.InstanceId==_selectedId)return true;
            return item.Location.Kind==ItemLocationKind.Backpack && _read!=null && _read.TryGetView(_selectedId,out var selected)
                && item.Location==selected.Location && _draft.Contains(item.InstanceId)==_draft.Contains(_selectedId);
        }
        public bool TryArrangement(string id,string bagId,out GridPlacement placement)
        {placement=_draft.Candidate;return _draft.Active && _draft.BagId==bagId && _draft.Contains(id);}
        private void CancelArrangement(){_draft.Clear();_nearbyGrid?.CancelArrangement();}
        private Vector2Int _previewCell;
        private int _previewTurns;
        private uint _previewVersion;
        private bool _previewNearby;
        private static readonly Unity.Profiling.ProfilerMarker PreviewMarker=new Unity.Profiling.ProfilerMarker("Norn.Backpack.DragPreview");
        private static readonly Unity.Profiling.ProfilerMarker InteractionMarker=new Unity.Profiling.ProfilerMarker("Norn.Inventory.InteractionTick");
        private static readonly Unity.Profiling.ProfilerMarker DragFollowMarker=new Unity.Profiling.ProfilerMarker("Norn.Backpack.DragFollow");
        public bool IsOpen => _open;
        public string SelectedItemId => _selectedId;
        public bool IsDragging => _dragging;
        public Font UiFont { get; private set; }
        public Font BuiltinUiFont { get; private set; }
        public bool IsSplitPressed => _embedded ? Keyboard.current != null && Keyboard.current.shiftKey.isPressed
            : _splitAction != null && _splitAction.action.IsPressed();
        public string DescribeSpellUse(in InventoryItemSnapshot item, bool compact = false)
        {
            if (item.Kind != ItemKind.Spell) return "";
            if (_session != null && _session.SpellUsesReadOnly != null && _session.SpellUsesReadOnly.TryGetState(item.InstanceId, out SpellUseState use)
                && use.IsQualification)
                return (use.Unlimited ? "无限入局" : compact ? "未来" + use.RemainingCharges + "局" : "未来入局 " + use.RemainingCharges + " 次")
                    + (use.Eligible ? " · 本局可用" : compact ? " · 未获资格" : " · 未取得本局资格");
            if (compact) return item.HasUnlimitedCharges ? "∞" : item.RemainingCharges + "次";
            return item.HasUnlimitedCharges ? "无限使用" : "剩余 " + item.RemainingCharges + " 次";
        }

        // 角标只呈现数值；资格/保护说明仍在已有详情里。目录Rebuild缓存结果，滚动不格式化。
        public string SpellCountCaption(in InventoryItemSnapshot item)
        {
            if(item.Kind!=ItemKind.Spell || item.IsQualificationSpell)return "";
            if(_session!=null && _session.SpellUsesReadOnly!=null && _session.SpellUsesReadOnly.TryGetState(item.InstanceId,out var use))
                return use.Unlimited || use.RemainingCharges<=0?"":use.RemainingCharges.ToString();
            return item.HasUnlimitedCharges || item.RemainingCharges<=0?"":item.RemainingCharges.ToString();
        }

        private void OnEnable()
        {
            EventBus<InventoryChangedEvent>.Subscribe(OnChanged);
            EventBus<RaidStateChangedEvent>.Subscribe(OnRaidPresentationChanged);
            EventBus<InventoryCommandResultEvent>.Subscribe(OnResult);
            EventBus<InventorySaveResultEvent>.Subscribe(OnSaveResult);
            EventBus<NearbyPickupResultEvent>.Subscribe(OnPickup);
            EventBus<NearbyPickupListChangedEvent>.Subscribe(OnNearbyChanged);
            EventBus<ToolbeltBindingResultEvent>.Subscribe(OnToolResult);
            EventBus<HubInteractionResultEvent>.Subscribe(OnHubInteraction);
            if (_toggleAction != null) { _toggleAction.action.performed += OnToggle; _toggleAction.action.Enable(); }
            if (_rotateAction != null) { _rotateAction.action.performed += OnRotate; _rotateAction.action.Enable(); }
            if (_cancelAction != null) { _cancelAction.action.performed += OnCancel; _cancelAction.action.Enable(); }
            if (_splitAction != null) _splitAction.action.Enable();
        }
        private void OnDisable()
        {
            EventBus<InventoryChangedEvent>.Unsubscribe(OnChanged);
            EventBus<RaidStateChangedEvent>.Unsubscribe(OnRaidPresentationChanged);
            EventBus<InventoryCommandResultEvent>.Unsubscribe(OnResult);
            EventBus<InventorySaveResultEvent>.Unsubscribe(OnSaveResult);
            EventBus<NearbyPickupResultEvent>.Unsubscribe(OnPickup);
            EventBus<NearbyPickupListChangedEvent>.Unsubscribe(OnNearbyChanged);
            EventBus<ToolbeltBindingResultEvent>.Unsubscribe(OnToolResult);
            EventBus<HubInteractionResultEvent>.Unsubscribe(OnHubInteraction);
            if (_toggleAction != null) { _toggleAction.action.performed -= OnToggle; _toggleAction.action.Disable(); }
            if (_rotateAction != null) { _rotateAction.action.performed -= OnRotate; _rotateAction.action.Disable(); }
            if (_cancelAction != null) { _cancelAction.action.performed -= OnCancel; _cancelAction.action.Disable(); }
            if (_splitAction != null) _splitAction.action.Disable();
            Close();
        }
        private IEnumerator Start()
        {
            if (_ghost != null) _ghost.gameObject.SetActive(false);
            if (_panelRoot != null) _panelRoot.SetActive(false);
            // 一个启动帧让同步 Profile Start 完成，避免依赖不同 GameObject 的 Start 顺序。
            // Coroutine 仅初始化时创建；持续拖拽不启动 Coroutine 或分配对象。
            yield return null;
            BuiltinUiFont=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            UiFont = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Noto Sans CJK SC", "Arial" }, 16);
            if (_panelRoot != null)
            {
                Text[] labels = _panelRoot.GetComponentsInChildren<Text>(true);
                for (int i = 0; i < labels.Length; i++) labels[i].font = UiFont;
            }
            _ghost?.SetFont(UiFont);
            _waitingOpen = _openOnStart;
            if (_waitingOpen) Open();
        }
        private void OnApplicationFocus(bool focus) { if (!focus){CancelDrag();CancelArrangement();Refresh();} }
        private void OnDestroy()
        {
            // OS 动态 Font 是本界面创建的 Unity native 对象，结束 Scene 时主动释放。
            if (UiFont != null) Destroy(UiFont);
        }
        private void OnToggle(InputAction.CallbackContext context)
        {
            if (_open && !_warehouseMode) Close();
            else OpenBackpack(); // 仓库中Tab切到随身编程；重复Tab关闭，不更换库存或暂停所有者。
        }
        private void OnRotate(InputAction.CallbackContext context) => RotateDrag();
        private void OnCancel(InputAction.CallbackContext context) { if(_infoPopup!=null && _infoPopup.IsOpen){_infoPopup.Hide();return;}if (_dragging){CancelDrag();CancelArrangement();Refresh();} else Close(); }
        public void Open()
        {
            if (_embedded) return;
            if (_otherWorkspace != null && _otherWorkspace.IsOpen) _otherWorkspace.Close();
            _read = _session != null ? _session.ReadOnly : null;
            if (_read == null)
            {
                if (_session != null && !string.IsNullOrWhiteSpace(_session.InitializationMessage))
                {
                    if (_panelRoot != null) _panelRoot.SetActive(true);
                    _open = true; _pauseCoordinator?.RequestPause(RunPauseReason.Inventory);
                    SetStatus(_session.InitializationMessage);
                }
                return;
            }
            if (_pauseCoordinator == null) { SetStatus("缺少暂停协调器，无法打开整备界面。"); return; }
            _waitingOpen = false; _open = true;
            if (_panelRoot != null) _panelRoot.SetActive(true);
            _pauseCoordinator.RequestPause(RunPauseReason.Inventory);
            _gridView?.Initialize(this, _itemPrefab);
            _warehouseTarget?.Initialize(this, InventoryDropKind.Warehouse);
            _groundTarget?.Initialize(this, InventoryDropKind.Ground);
            _backpackTarget?.Initialize(this, InventoryDropKind.EquippedBackpack);
            Refresh();
            SetStatus(_session.InitializationMessage ?? (_session.HasUnsavedChanges ? "有未保存更改" : "已读取保存的资产"));
        }
        public void Close()
        {
            _infoPopup?.Hide();
            _backpackPan?.Cancel();_nearbyPan?.Cancel();
            CancelDrag(); CancelArrangement(); _waitingOpen = false;
            if (_open && !_embedded) _pauseCoordinator?.ReleasePause(RunPauseReason.Inventory);
            _open = false; if (_panelRoot != null && !_embedded) _panelRoot.SetActive(false);
        }
        public void SetEmbeddedOpen(bool open)
        {
            // 只交接交互生命周期。Panel和Shop pause reason仍只有ShopController一个所有者。
            if (!_embedded) return;
            if (!open) { Close(); return; }
            _read = _session != null ? _session.ReadOnly : null; if (_read == null) return;
            _warehouseMode = true; _waitingOpen = false; _open = true;
            _gridView?.Initialize(this, _itemPrefab);
            _warehouseTarget?.Initialize(this, InventoryDropKind.Warehouse);
            _backpackTarget?.Initialize(this, InventoryDropKind.EquippedBackpack);
            Refresh();
        }
        public void BeginDrag(in InventoryItemSnapshot source, Vector2 pointer, Camera camera, bool split, Vector2Int grab = default,int displayTurns=-1,
            Vector2? preciseGrab=null,RectTransform sourceRect=null,float sourceCellSize=0)
        {
            if (!_open || _read == null) return;
            // 来源可能来自不同CellSize/Canvas缩放的附近区。先转换到Ghost Local Space，
            // 同步Select/Refresh可能复用来源View，必须在它们之前保存来源的实际显示尺寸。
            float visualCell=_gridView!=null?_gridView.CellSize:48;
            if(_ghost!=null && sourceRect!=null && sourceCellSize>0)
                visualCell=_ghost.Rect.InverseTransformVector(sourceRect.TransformVector(Vector3.right*sourceCellSize)).magnitude;
            // in可能引用池化View字段；SelectItem会同步Refresh，必须先复制，再选择Shift指定副本。
            InventoryItemSnapshot chosen = source;
            CancelDrag();
            // 选择“下一份”后 Shift 拆出的就是指定 ID，不把整个组固定成第一份副本。
            if (split && chosen.Location.Kind == ItemLocationKind.Backpack && _read.TryGetView(_selectedId, out InventoryItemSnapshot selected)
                && selected.Location == chosen.Location) chosen = selected;
            if(_selectedId!=chosen.InstanceId)SelectItem(chosen.InstanceId);
            _dragToolVersion = _session.ToolbeltReadOnly?.ConfigurationVersion ?? 0;
            _dragSource = chosen; _dragVersion = _read.Version; _turns = displayTurns<0?chosen.Location.QuarterTurns:displayTurns;
            _wholeStack = !split && chosen.Location.Kind == ItemLocationKind.Backpack;
            _grabPoint=preciseGrab??new Vector2(grab.x+.5f,grab.y+.5f);_dragCellSize=visualCell;_dragging = true;
            _nearbyGrid?.PrepareDrag(chosen.Rules.Shape,_turns);
            if (_ghost != null)
            {
                _ghost.gameObject.SetActive(true); _ghost.Rect.pivot = new Vector2(0, 1);
                _ghost.Bind(null, chosen, InventoryItemDisplay.Grid, _dragCellSize, StackCount(chosen), _turns);
                _ghost.transform.SetAsLastSibling();
            }
            MoveGhost(pointer, camera);
        }
        public void MoveGhost(Vector2 pointer, Camera camera)
        {
            if (!_dragging || _ghost == null) return;
            using var follow=DragFollowMarker.Auto();
            _lastPointer = pointer; _pointerCamera = camera;
            bool outside=_dragSource.Location.Kind!=ItemLocationKind.WorldItem && IsOutsidePanel(pointer,camera);
            if(outside!=_outsideDropHint){_outsideDropHint=outside;SetStatus(outside?"松开丢弃":"拖到附近物品或界面外松开可丢弃");}
            RectTransform parent = (RectTransform)_ghost.transform.parent;
            if (RectTransformUtility.ScreenPointToWorldPointInRectangle(parent, pointer, camera, out Vector3 world))
            {
                // 连续抓取点保留格子内的小数，跟随与落格共同使用实际 Ghost 轮廓。
                _ghost.Rect.position = world - _ghost.Rect.TransformVector(new Vector3(_grabPoint.x*_dragCellSize,-_grabPoint.y*_dragCellSize,0));
            }
            PreviewGrid();
        }
        private bool TryGridPlacement(out Vector2Int cell,out bool nearby)
        {
            cell=default;nearby=false;
            if(_ghost==null)return false;
            float bagCoverage=0;
            bool bag=_gridView!=null && InventoryDragGeometry.TryPlacement(_ghost.Rect,(RectTransform)_gridView.transform,_gridView.CellSize,out cell,out bagCoverage);
            float bagArea=bag?bagCoverage:0;
            if(_nearbyGrid!=null && InventoryDragGeometry.TryPlacement(_ghost.Rect,(RectTransform)_nearbyGrid.transform,_nearbyGrid.CellSize,out var groundCell,out var groundArea)
                && groundArea>bagArea)
            {cell=groundCell;nearby=true;return true;}
            return bag;
        }
        private void PreviewGrid()
        {
            using var marker=PreviewMarker.Auto();
            if (!TryGridPlacement(out var cell,out bool nearby))
            { if(_previewVisible){_gridView?.ClearPreview();_nearbyGrid?.ClearPreview();}_previewVisible=false;return; }
            if (_previewVisible && _previewNearby==nearby && cell==_previewCell && _turns==_previewTurns && _previewVersion==_read.Version) return;
            // 从一个网格跨到另一个必须清除原色，不让上一个目标残留绿色提示。
            _gridView?.ClearPreview();_nearbyGrid?.ClearPreview();
            _previewVisible=true;_previewNearby=nearby;_previewCell=cell;_previewTurns=_turns;_previewVersion=_read.Version;
            if(nearby)
            {
                var placement=new GridPlacement(cell.x,cell.y,_turns);
                _nearbyGrid.ShowPreview(_dragSource.Rules.Shape,placement,_nearbyGrid.CanPreview(_dragSource.InstanceId,_dragSource.Rules.Shape,placement));
                return;
            }
            bool valid=_read is IInventoryPlacementPreview preview && preview.PreviewPlacement(_dragSource.InstanceId,_gridView.BackpackId,
                new GridPlacement(cell.x,cell.y,_turns),_wholeStack,_dragVersion)==InventoryFailureReason.None;
            _gridView.ShowPreview(_dragSource.Rules.Shape,cell.x,cell.y,_turns,valid);
        }
        public void RotateDrag()
        {
            if (!_dragging) return;
            int height = _dragSource.Rules.Shape.GetHeight(_turns);
            _turns = (_turns + 1) % 4;
            // y向下的网格连续坐标顺时针90°为(height-y,x)，离散格索引才需要减1。
            _grabPoint=new Vector2(height-_grabPoint.y,_grabPoint.x);
            if (_ghost != null) _ghost.Bind(null, _dragSource, InventoryItemDisplay.Grid, _dragCellSize, StackCount(_dragSource), _turns);
            MoveGhost(_lastPointer, _pointerCamera);
        }
        public void CancelDrag() { _gridView?.ClearPreview(); _nearbyGrid?.ClearPreview(); _previewVisible=false; _outsideDropHint=false; _dragging = false; if (_ghost != null) _ghost.gameObject.SetActive(false); }
        private bool IsOutsidePanel(Vector2 pointer,Camera camera) => _panelRoot!=null && _panelRoot.transform is RectTransform rect
            && !RectTransformUtility.RectangleContainsScreenPoint(rect,pointer,camera);
        public void EndItemDrag(Vector2 pointer,Camera camera)
        {
            // OnDrop先执行且可能同步刷新源View；已经结束的拖拽不能再次丢弃。
            if(!_dragging)return;
            // UGUI 的 OnDrop 由鼠标 Raycast 决定；轮廓已经进入网格但鼠标尚未进入时在这里补交一次。
            if(TryDropOnGrid(pointer,camera))return;
            if(_dragSource.Location.Kind!=ItemLocationKind.WorldItem && IsOutsidePanel(pointer,camera))
                SubmitToTarget(InventoryDropKind.Ground);
            else CancelDrag();
        }
        public void SubmitToBackpack(int x, int y, string backpackId)
        {
            if (!_dragging) return;
            var command = BaseCommand(); command.Kind = InventoryCommandKind.Place;
            command.OwnerId = backpackId; command.X = x; command.Y = y; command.QuarterTurns = _turns;
            if (_dragSource.Location.Kind == ItemLocationKind.WorldItem) command.Kind = InventoryCommandKind.Pickup;
            Arrange(command);
        }
        private void Arrange(InventoryCommandEvent command)
        {
            if(_read==null || !_read.TryGetView(command.ItemId,out var source)){CancelDrag();CancelArrangement();return;}
            if(_draft.Active && !_draft.OriginUnchanged(_read)){CancelDrag();CancelArrangement();Refresh();return;}
            // 既有草稿允许无关资产变化后重验证；刚开始的拖拽仍保留版本快照，不能偷移动已变更的来源。
            if(_draft.Active && !_dragging)command.ExpectedVersion=_read.Version;
            var placement=new GridPlacement(command.X,command.Y,command.QuarterTurns);
            var reason=_read is IInventoryPlacementPreview preview?preview.PreviewPlacement(command.ItemId,command.OwnerId,placement,command.WholeStack,command.ExpectedVersion):InventoryFailureReason.InvalidState;
            if(reason==InventoryFailureReason.Occupied || reason==InventoryFailureReason.OutOfBounds || reason==InventoryFailureReason.Capacity)
            {
                _draft.Set(source,command.OwnerId,placement,command.WholeStack,_read);CancelDrag();Refresh();
                SetStatus("红框位置尚未整理；继续移动或旋转，选其他物品会撤销。");return;
            }
            _draft.Clear();Submit(command);if(reason!=InventoryFailureReason.None)Refresh();
        }
        public void SubmitToNearby(Vector2 pointer,Camera camera)
        {
            if(!_dragging)return;
            if(_nearbyGrid==null){SubmitToTarget(InventoryDropKind.Ground);return;}
            TryDropOnGrid(pointer,camera);
        }
        public bool TryDropOnGrid(Vector2 pointer,Camera camera)
        {
            if(!_dragging)return false;
            MoveGhost(pointer,camera);
            if(!TryGridPlacement(out var cell,out bool nearby))return false;
            if(!nearby){SubmitToBackpack(cell.x,cell.y,_gridView.BackpackId);return true;}
            var placement=new GridPlacement(cell.x,cell.y,_turns);
            if(_dragSource.Location.Kind==ItemLocationKind.WorldItem)
            {_nearbyGrid.MoveItem(_dragSource.InstanceId,_dragSource.Rules.Shape,placement);_draft.Clear();CancelDrag();Refresh();return true;}
            _placingDropped=true;_droppedPlacement=placement;SubmitToTarget(InventoryDropKind.Ground);
            return true;
        }
        public void SubmitToTarget(InventoryDropKind kind, string ownerId = null, int slot = 0)
        {
            if (!_dragging) return;
            // 拖法杖落到程序中的空槽/法术卡，应替换这行装备；拖Spell仍沿用精确程序槽语义。
            if(kind==InventoryDropKind.ProgramSlot && _dragSource.Kind==ItemKind.Wand
                && _read.TryGetView(ownerId,out var owner) && owner.Location.Kind==ItemLocationKind.EquippedWand)
            {kind=InventoryDropKind.EquippedWand;ownerId=owner.Location.OwnerId;slot=owner.Location.SlotIndex;}
            var command = BaseCommand(); command.OwnerId = ownerId; command.SlotIndex = slot;
            switch (kind)
            {
                case InventoryDropKind.SpellStorage:
                    command.Kind = InventoryCommandKind.Place; command.Destination = InventoryDestination.SpellStorage; command.WholeStack = false; break;
                case InventoryDropKind.Warehouse: command.Kind = InventoryCommandKind.Place; command.Destination = InventoryDestination.Warehouse; break;
                case InventoryDropKind.Ground: command.Kind = InventoryCommandKind.Drop; break;
                case InventoryDropKind.EquippedWand: command.Kind = InventoryCommandKind.EquipWand; break;
                case InventoryDropKind.EquippedBackpack: command.Kind = InventoryCommandKind.ReplaceBackpack; break;
                case InventoryDropKind.ProgramSlot:
                    command.Kind = InventoryCommandKind.Place; command.Destination = InventoryDestination.WandSlot; command.WholeStack = false; break;
            }
            if (_dragSource.Location.Kind == ItemLocationKind.WorldItem)
            {
                if (kind != InventoryDropKind.EquippedWand && kind != InventoryDropKind.EquippedBackpack && kind != InventoryDropKind.ProgramSlot && kind != InventoryDropKind.SpellStorage)
                { CancelDrag(); SetStatus("地面物品请拾取到背包或装备位。"); return; }
                command.Kind = InventoryCommandKind.Pickup;
                command.OwnerId = kind == InventoryDropKind.ProgramSlot ? ownerId : _read.ActorId;
                command.PickupDestination = kind == InventoryDropKind.EquippedWand ? InventoryPickupDestination.EquippedWand
                    : kind == InventoryDropKind.EquippedBackpack ? InventoryPickupDestination.EquippedBackpack
                    : kind == InventoryDropKind.SpellStorage ? InventoryPickupDestination.SpellStorage : InventoryPickupDestination.WandSlot;
            }
            Submit(command);
        }
        public void DropOnItem(in InventoryItemSnapshot target,Vector2 pointer,Camera camera)
        {
            if (!_dragging) return;
            if(_dragSource.Kind==ItemKind.Material&&target.Kind==ItemKind.Material&&_dragSource.ContentId==target.ContentId
                &&target.Location.Kind==ItemLocationKind.Backpack)
            {
                var merge=BaseCommand();merge.Kind=_dragSource.Location.Kind==ItemLocationKind.WorldItem?InventoryCommandKind.Pickup:InventoryCommandKind.Place;
                merge.Destination=InventoryDestination.Backpack;merge.OwnerId=target.Location.OwnerId;merge.X=target.Location.X;merge.Y=target.Location.Y;
                merge.QuarterTurns=target.Location.QuarterTurns;merge.WholeStack=false;Submit(merge);return;
            }
            if(IsPendingPickup(target.InstanceId) || _draft.Contains(target.InstanceId))
            {TryDropOnGrid(pointer,camera);return;}
            switch (target.Location.Kind)
            {
                case ItemLocationKind.SpellStorage: SubmitToTarget(InventoryDropKind.SpellStorage, target.Location.OwnerId, target.Location.SlotIndex); break;
                case ItemLocationKind.Backpack:
                    TryDropOnGrid(pointer,camera);break;
                case ItemLocationKind.Warehouse: SubmitToTarget(InventoryDropKind.Warehouse); break;
                case ItemLocationKind.WandSlot: SubmitToTarget(InventoryDropKind.ProgramSlot, target.Location.OwnerId, target.Location.SlotIndex); break;
                case ItemLocationKind.EquippedWand: SubmitToTarget(InventoryDropKind.EquippedWand, target.Location.OwnerId, target.Location.SlotIndex); break;
                case ItemLocationKind.EquippedBackpack: SubmitToTarget(InventoryDropKind.EquippedBackpack); break;
                case ItemLocationKind.WorldItem: SubmitToNearby(pointer,camera); break;
            }
        }
        private InventoryCommandEvent BaseCommand() => new InventoryCommandEvent
        {
            InventoryId = _read.InventoryId, RequestId = ++_requestId, ItemId = _dragSource.InstanceId,
            ExpectedVersion = _dragVersion, WholeStack = _wholeStack, WorldOwner = _dragSource.Location.OwnerId
        };
        private void Submit(InventoryCommandEvent command)
        {
            // 同步 EventBus 可能当场刷新/复用源 View，所以先结束视觉拖拽，再提交。
            if(command.Kind==InventoryCommandKind.Drop)_revealDroppedId=command.ItemId;
            _draft.Clear();CancelDrag(); EventBus<InventoryCommandEvent>.Publish(command);
        }
        public void Save()
        {
            if (_read == null) return;
            CancelDrag();CancelArrangement();Refresh(); EventBus<InventorySaveRequestedEvent>.Publish(new InventorySaveRequestedEvent { InventoryId = _read.InventoryId, RequestId = ++_requestId });
        }
        public void SelectItem(string instanceId)
        {
            if(_selectedId!=instanceId)CancelArrangement();
            _selectedId = instanceId;
            if (_read != null && _read.TryGetView(instanceId, out InventoryItemSnapshot item))
            {
                if (item.Kind == ItemKind.Wand) _wandsView?.SelectWand(instanceId);
                if (item.Kind == ItemKind.Backpack) _viewBackpackId = instanceId;
                if(_backpackWorkspace == null && item.Kind==ItemKind.Container && item.Location.Kind==ItemLocationKind.Backpack)
                    EventBus<ContainerSelectionRequestedEvent>.Publish(new ContainerSelectionRequestedEvent{InventoryId=_read.InventoryId,ItemId=instanceId});
            }
            Refresh();
            SelectionChanged?.Invoke(instanceId);
        }
        public void InspectWandSlot(int slot)
        {
            // 选择展示槽只刷新UI；没有发布手持切换事件，角色实际使用的法杖不受影响。
            _wandsView?.SelectSlot(slot); Refresh();
        }
        public void ClickItem(string instanceId)
        {
            if(_selectedId!=instanceId){SelectItem(instanceId);return;}
            if(_dragging || _read==null || !_read.TryGetView(instanceId,out var item))return;
            if(item.Location.Kind==ItemLocationKind.Warehouse && !_draft.Contains(instanceId))
            { SelectNextMember(); SetStatus("已切换同类物品中的具体一份；拖动只取这一份。"); return; }
            if(item.Location.Kind==ItemLocationKind.WorldItem && !IsPendingPickup(instanceId))
            {if(_nearbyGrid!=null && _nearbyGrid.TryPlacement(instanceId,out var ground))
                {_nearbyGrid.MoveItem(instanceId,item.Rules.Shape,new GridPlacement(ground.X,ground.Y,(ground.QuarterTurns+1)%4));Refresh();}return;}
            if(item.Location.Kind!=ItemLocationKind.Backpack && !_draft.Contains(instanceId) && !IsPendingPickup(instanceId))return;
            var location=_draft.Contains(instanceId)?_draft.Candidate.InBackpack(_draft.BagId):item.Location;
            Arrange(new InventoryCommandEvent{
                InventoryId=_read.InventoryId,RequestId=++_requestId,ItemId=instanceId,ExpectedVersion=_read.Version,
                Kind=item.Location.Kind==ItemLocationKind.WorldItem?InventoryCommandKind.Pickup:InventoryCommandKind.Place,Destination=InventoryDestination.Backpack,WholeStack=_draft.Active?_draft.WholeStack:true,
                WorldOwner=item.Location.OwnerId,OwnerId=location.OwnerId,X=location.X,Y=location.Y,QuarterTurns=(location.QuarterTurns+1)%4});
        }
        public void PickupSelected()=>_nearby?.TryCollect(_selectedId);
        private ulong _organizeRequest;
        public void OrganizeBackpack()
        {
            if(_read==null||_dragging||_draft.Active)return;CancelArrangement();
            _organizeRequest=++_requestId;
            EventBus<InventoryCommandEvent>.Publish(new InventoryCommandEvent{InventoryId=_read.InventoryId,RequestId=_organizeRequest,
                Kind=InventoryCommandKind.OrganizeBackpack,OwnerId=_session.EquippedBackpackId,ExpectedVersion=_read.Version});
            Refresh();
        }
        public void PickupAllNearby()
        {
            if(_read==null||_nearby==null||_dragging||_draft.Active)return;
            int collected=_nearby.CollectAll(out int remaining);Refresh();
            _backpackWorkspace?.ShowPickupResult(collected,remaining);
            SetStatus("已拾取 "+collected+" 件 · 附近剩余 "+remaining+" 件");
        }
        private void OnHubInteraction(HubInteractionResultEvent result)
        {
            if (_embedded) return;
            if(!result.Success || result.InventoryId!=_session.ReadOnly?.InventoryId)return;
            if(result.Kind==HubInteractionKind.Shop){Close();return;}
            // 只有有真实仓库区域的控制器消费此结果；独立Tab背包不再重复打开/重排仓库。
            if(result.Kind==HubInteractionKind.Warehouse && _warehouseRoot!=null)OpenWarehouse();
        }
        private void OnPickup(NearbyPickupResultEvent result)
        {
            if (_embedded) return;
            if (_otherWorkspace!=null && _backpackWorkspace==null)return;
            if(result.InventoryId!=_session.ReadOnly?.InventoryId)return;
            if(result.RequiresArrangement){OpenBackpack();SelectItem(result.ItemId);SetStatus("背包没有合适位置，请移动或旋转物品，再拖入附近物品。");}
            else {if(_open)Refresh();SetStatus(result.Success?"已拾取":FailureText(result.Reason));}
        }
        public void SelectNextMember()
        {
            if (_read == null || !_read.TryGetView(_selectedId, out InventoryItemSnapshot selected)) return;
            if (selected.Location.Kind == ItemLocationKind.Warehouse)
            {
                string firstId = null; bool after = false;
                for (int i = 0; i < _count; i++)
                    if (_items[i].Location.Kind == ItemLocationKind.Warehouse && _items[i].ContentId == selected.ContentId)
                    {
                        if (firstId == null) firstId = _items[i].InstanceId;
                        if (after) { SelectItem(_items[i].InstanceId); return; }
                        if (_items[i].InstanceId == _selectedId) after = true;
                    }
                if (firstId != null) SelectItem(firstId); return;
            }
            if (selected.Location.Kind != ItemLocationKind.Backpack) return;
            int first = -1; bool passed = false;
            for (int i = 0; i < _count; i++) if (_items[i].Location == selected.Location)
            {
                if (first < 0) first = i;
                if (passed) { SelectItem(_items[i].InstanceId); return; }
                if (_items[i].InstanceId == _selectedId) passed = true;
            }
            if (first >= 0) SelectItem(_items[first].InstanceId);
        }
        private void Refresh()
        {
            // 网格Refresh恢复作者底色，旧的预览缓存因此失效；即使指针/库存版本没变也必须重画。
            _previewVisible=false;
            if (!_open || _read == null || _itemPrefab == null) return;
            if(_draft.Active && !_draft.OriginUnchanged(_read))_draft.Clear();
            if (_items.Length < _read.Count) _items = new InventoryItemSnapshot[_read.Count];
            _count = _read.CopyItemsTo(_items);
            string equippedBag = null; bool viewedBagExists = false;
            for (int i = 0; i < _count; i++)
            {
                InventoryItemSnapshot item = _items[i];
                if (item.Kind == ItemKind.Backpack && item.InstanceId == _viewBackpackId) viewedBagExists = true;
                if (item.Location.Kind == ItemLocationKind.EquippedBackpack && item.Location.OwnerId == _read.ActorId) equippedBag = item.InstanceId;
            }
            if (!viewedBagExists) _viewBackpackId = equippedBag;
            // 仓库可预览其他背包；切回随身界面必须回到真实携装，不能把预览内容当可用道具。
            if(_embedded || (_backpackWorkspace!=null || _workspace!=null) && !WarehouseMode)_viewBackpackId=equippedBag;
            if (_backpackWorkspace == null) _workspace?.Arrange(WarehouseMode);
            if (_warehouseGrid != null) _warehouseGrid.Refresh(this, _itemPrefab, _items, _count);
            else RefreshRows(_warehouseRoot, _warehouseViews, ItemLocationKind.Warehouse);
            if(_nearbyGrid!=null)_nearbyGrid.Refresh(this,_itemPrefab,_items,_count,_nearby);
            else RefreshRows(_groundRoot, _groundViews, ItemLocationKind.WorldItem);
            _gridView?.Refresh(_items, _count, _viewBackpackId);
            _wandsView?.Refresh(this, _itemPrefab, _items, _count, _read.ActorId);
            _spellStorage?.Refresh(this, _itemPrefab, _items, _count, _read.ActorId);
            RefreshDetails();
            if (_backpackWorkspace != null) _backpackWorkspace.Refresh(this,_items,_count,_viewBackpackId);
            else _workspace?.Refresh(_items,_count,_viewBackpackId);
            _warehouseWorkspace?.Refresh(_items, _count, equippedBag);
            // 打开/版本变化是离散边界；组件查询与首次AddComponent在这里完成，不进入稳定LateUpdate。
            if(_backpackPan==null && _gridView!=null)_backpackPan=InventoryViewportPan.Ensure(_gridView.GetComponentInParent<ScrollRect>());
            if(_nearbyPan==null && _nearbyGrid!=null)_nearbyPan=InventoryViewportPan.Ensure(_nearbyGrid.GetComponentInParent<ScrollRect>());
        }
        private void RefreshRows(RectTransform parent, List<InventoryItemView> pool, ItemLocationKind kind)
        {
            if (parent == null) return; int shown = 0;
            for (int i = 0; i < _count; i++)
            {
                if (_items[i].Location.Kind != kind) continue;
                if(kind==ItemLocationKind.WorldItem && _nearby!=null && !_nearby.IsNearby(_items[i].InstanceId))continue;
                if(kind==ItemLocationKind.Warehouse && _workspace!=null && !_workspace.ShowWarehouseItem(_items[i]))continue;
                while (pool.Count <= shown) pool.Add(Instantiate(_itemPrefab, parent));
                InventoryItemView view = pool[shown]; view.gameObject.SetActive(true);
                view.Rect.anchorMin = view.Rect.anchorMax = view.Rect.pivot = new Vector2(0, 1);
                // 独立仓库可以直接使用图标目录，不依赖会重写作者布局的旧Workspace组件。
                // 仓库目录不表达背包占格；实际携带形状仍由右边的InventoryGridView显示。
                bool warehouseTiles=kind==ItemLocationKind.Warehouse && _warehouseTiles;
                bool tiles=_workspace!=null || warehouseTiles;float tile=kind==ItemLocationKind.WorldItem?84:warehouseTiles?_warehouseTileSize:112;
                int columns=warehouseTiles || _backpackWorkspace!=null?Mathf.Max(1,Mathf.FloorToInt(((RectTransform)parent.parent).rect.width/tile)):4;
                view.Rect.anchoredPosition = tiles?new Vector2(shown%columns*tile,-shown/columns*tile):new Vector2(0, -shown * 56);
                view.Rect.sizeDelta = tiles?new Vector2(tile-8,tile-8):new Vector2(270,52);
                view.Bind(this, _items[i], tiles?InventoryItemDisplay.Program:InventoryItemDisplay.Row,48); shown++;
            }
            for (int i = shown; i < pool.Count; i++) pool[i].gameObject.SetActive(false);
            bool tileWarehouse=kind==ItemLocationKind.Warehouse && _warehouseTiles;
            float rowTile=kind==ItemLocationKind.WorldItem?84:tileWarehouse?_warehouseTileSize:112;
            int rowColumns=tileWarehouse || _backpackWorkspace!=null?Mathf.Max(1,Mathf.FloorToInt(((RectTransform)parent.parent).rect.width/rowTile)):4;
            parent.sizeDelta = _workspace!=null || tileWarehouse?new Vector2(rowColumns*rowTile,Mathf.CeilToInt(shown/(float)rowColumns)*rowTile):new Vector2(270, shown * 56);
        }
        private int StackCount(in InventoryItemSnapshot item)
        {
            if (item.Location.Kind != ItemLocationKind.Backpack) return 1;
            int count = 0; for (int i = 0; i < _count; i++) if (_items[i].Location == item.Location) count++; return count;
        }
        private void RefreshDetails()
        {
            if (_details == null) return;
            if (_read.TryGetView(_selectedId, out InventoryItemSnapshot item))
            {
                _details.text = item.DisplayName + "\n当前位置：" + LocationText(item.Location.Kind) + "\n"
                    + (item.Kind == ItemKind.Spell ? DescribeSpellUse(item)
                        : item.Kind == ItemKind.Wand ? "程序容量 " + item.Rules.WandProgramCapacity
                        : item.Kind == ItemKind.Container ? "数量 " + item.ContainerTotal + "/" + item.ContainerCapacity
                        : item.Kind == ItemKind.Material ? "数量 " + item.MaterialQuantity : "")
                    + (StackCount(item)>1?" · 同格 " + StackCount(item) + " 份":"")
                    + (item.Attachment == null ? "" : "\n附着剂量 " + item.Attachment.Dose + " · 持续关联该卡");
            }
            else _details.text = "选择物品查看详情";
        }
        private static string LocationText(ItemLocationKind kind)
        {
            switch(kind){case ItemLocationKind.SpellStorage:return "随身法术专区";case ItemLocationKind.Backpack:return "携带背包";case ItemLocationKind.WorldItem:return "附近地面";
                case ItemLocationKind.WandSlot:return "法杖程序";case ItemLocationKind.Warehouse:return "仓库";
                case ItemLocationKind.EquippedWand:return "法杖装备位";case ItemLocationKind.EquippedBackpack:return "背包装备位";default:return "当前资产";}
        }
        private void OnRaidPresentationChanged(RaidStateChangedEvent change)
        {
            // 保护标记变化不移动库存，靠离散Raid事件刷新，避免面板持续轮询/分配字符串。
            if (_open && _read != null && change.InventoryId == _read.InventoryId && _backpackWorkspace != null) _pendingBackpackRefresh = true;
        }
        private void OnChanged(InventoryChangedEvent change)
        {
            if (_read == null && _session != null) _read = _session.ReadOnly;
            if (_read == null || change.InventoryId != _read.InventoryId) return;
            // EventBus同步调用，但不同对象OnEnable订阅次序不保证。等本轮事件完成后读程序投影，
            // 避免布局已含新卡而蓝耗仍读取前一版本；暂停期间LateUpdate照常执行。
            _pendingBackpackRefresh=true;
        }
        private void OnNearbyChanged(NearbyPickupListChangedEvent e)
        {if(_open && !_dragging && _read!=null && e.InventoryId==_read.InventoryId)_pendingBackpackRefresh=true;}
        private void LateUpdate()
        {
            // 覆盖暂停期间的预览/手动滚动热路径；离散dirty刷新另有分配，不计入稳定拖拽验证。
            using var interaction=InteractionMarker.Auto();
            if(_pendingBackpackRefresh)
            { _pendingBackpackRefresh=false;if(_waitingOpen)Open();else if(_open)Refresh(); }
            _backpackPan?.Tick(_open && !_dragging);_nearbyPan?.Tick(_open && !_dragging);
            _warehouseGrid?.TickInteraction(_open, _dragging, _lastPointer, _pointerCamera, Time.unscaledDeltaTime);
            // 拖出包外不再改变ScrollRect位置；手动滚动由ViewportPan/Scrollbar承担。
            if(_open && _dragging && _gridView!=null)
            {
                // 延后的库存Refresh会清色；静止指针没有OnDrag回调，也要补画失效的预览。
                if(!_previewVisible)PreviewGrid();
            }
        }
        private void OnResult(InventoryCommandResultEvent result)
        {
            if (_read == null || result.InventoryId != _read.InventoryId || result.RequestId != _requestId) return;
            if(result.RequestId==_organizeRequest)_backpackWorkspace?.ShowOrganizeResult(result.Success);
            if(!string.IsNullOrEmpty(_revealDroppedId))
            {
                if(result.Success){_nearby?.RefreshCandidates();Refresh();
                    if(_placingDropped && _read.TryGetView(_revealDroppedId,out var dropped))
                    {_nearbyGrid?.MoveItem(_revealDroppedId,dropped.Rules.Shape,_droppedPlacement);Refresh();}
                    // 区域内拖放保留松手位置；只有界面外丢弃/按钮丢弃才自动滚入视野。
                    if(!_placingDropped)_nearbyGrid?.RevealItem(_revealDroppedId);}
                _revealDroppedId=null;
                _placingDropped=false;
            }
            SetStatus(result.Success ? "操作成功 · 有未保存更改" : FailureText(result.Reason));
        }
        private void OnToolResult(ToolbeltBindingResultEvent result)
        {
            if (_read==null || result.InventoryId!=_read.InventoryId || result.RequestId!=_requestId)return;
            if (result.Failure == ToolbeltFailure.BackpackFull) { SetStatus("背包空间不足，无法取下或替换道具；物品保持原位。"); return; }
            SetStatus(result.Success?"道具栏已更新":result.Failure==ToolbeltFailure.StaleVersion?"配置已变化，请重新拖拽。"
                :result.Failure==ToolbeltFailure.Busy?"资产事务进行中，请稍后重试。":"请拖入当前背包中已有使用能力的道具。");
        }
        private void OnSaveResult(InventorySaveResultEvent result)
        {
            if (_read == null || result.InventoryId != _read.InventoryId || result.RequestId != _requestId) return;
            SetStatus(result.Success ? "已保存" : "保存失败：" + result.Message);
        }
        private void SetStatus(string text) { if (_status != null) _status.text = text; }
        public void BeginShortcutDrag(string id, Vector2 pointer, Camera camera)
        {
            if (_read==null || !_read.TryGetView(id,out var item)) return;
            // 装备栏有实际归属，拖出后与普通物品共用形状落格规则。
            BeginDrag(item,pointer,camera,true);
        }
        public void SubmitToShortcut(int toolSlot)
        {
            if (!_dragging || _session.ToolbeltReadOnly==null) return;
            string id=_dragSource.InstanceId; uint version=_dragToolVersion; CancelDrag(); RequestShortcut(toolSlot,id,version);
        }
        public void ClearShortcut(int slot)
        { if (_open && _session.ToolbeltReadOnly!=null) RequestShortcut(slot,null,_session.ToolbeltReadOnly.ConfigurationVersion); }
        private void RequestShortcut(int slot,string id,uint version)
        {
            EventBus<ToolbeltBindingRequestedEvent>.Publish(new ToolbeltBindingRequestedEvent
            {InventoryId=_session.ReadOnly.InventoryId,ToolSlot=slot,ItemId=id,ExpectedVersion=version,RequestId=++_requestId});
            Refresh();
        }
        private static string FailureText(InventoryFailureReason reason)
        {
            switch (reason)
            {
                case InventoryFailureReason.OutOfBounds: return "超出格子范围或程序容量，物品保留原位。";
                case InventoryFailureReason.Occupied: return "位置被占用或状态不相容，物品保留原位。";
                case InventoryFailureReason.Capacity: return "堆叠已满或携带法杖超过3根，物品保留原位。";
                case InventoryFailureReason.WrongKind: return "此位置不能放这种物品。";
                case InventoryFailureReason.StaleVersion: return "资产已变化，请重新拖拽。";
                case InventoryFailureReason.WarehouseUnavailable: return "请进入据点商店或仓库区域后存取，物品保留原位。";
                default: return "操作被拒绝，物品保留原位。";
            }
        }
    }
}
