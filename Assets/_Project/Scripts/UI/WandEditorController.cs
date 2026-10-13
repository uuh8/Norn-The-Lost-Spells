using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;
using Game.Core;
using Game.Run;
using Game.Skills;

namespace Game.UI
{
    /// <summary>
    /// 法杖编程界面总控：按键开关面板（Time.timeScale=0 暂停 + 解锁鼠标）；持有跟随光标的 ghost；
    /// 集中裁决拖放（调色板→框=插入；框内=移动；框→框外=移除）。实现 IWandDragHandler 供法术格回调。
    /// </summary>
    public class WandEditorController : MonoBehaviour, IWandDragHandler, ISpellTooltipHandler
    {
        [Header("References")]
        [SerializeField] private GameObject _panelRoot;        // 整个编程界面根（开关其 active）
        [SerializeField] private SpellPaletteView _palette;
        [SerializeField] private WandFrameView _frame;
        [SerializeField] private WandLoadout _wand;            // 旧场景回退数据；P7 会替换为 Runtime Wand
        [SerializeField]
        [Tooltip("P7 单局法术数据源；Palette、Frame 和 Header 都从同一份 Runtime Clone 读取。")]
        private RunSpellSession _runSpellSession;
        [SerializeField]
        [Tooltip("P7 局内统一暂停协调器；避免 Wand Editor 关闭时覆盖 Pause Menu 或结算的暂停状态。")]
        private RunPauseCoordinator _pauseCoordinator;
        [SerializeField] private Image _dragGhost;            // 跟随光标的拖拽影像（raycastTarget 关、置顶层、默认隐藏）
        [SerializeField] private SpellTooltipPresenter _tooltipPresenter;

        [Header("Header (只读展示)")]
        // 保留旧字段名的序列化迁移，P7/P8 现有场景不需要重新拖拽这两个标题 Text。
        [FormerlySerializedAs("_shuffleLabel")]
        [SerializeField] private Text _manaCostLabel;
        [FormerlySerializedAs("_castCountLabel")]
        [SerializeField] private Text _remainingDrawBudgetLabel;

        [Header("Input")]
        [SerializeField] private InputActionReference _toggleAction; // 开关界面动作（开发者在 Inspector 指派，如 Tab）

        private bool _open;

        // 拖拽状态
        private SpellDefinition _dragSpell;
        private SlotKind _dragOrigin;
        private int _dragFromIndex;
        private bool _dropHandled;

        public bool IsOpen => _open;
        /// <summary>从据点进入练习时Tab交给携带背包；保留旧教学数据/战斗，只暂停旧编辑器的输入与面板。</summary>
        public void SuspendForInventoryWorkspace()
        {ForceClose();if(_panelRoot!=null)_panelRoot.SetActive(false);enabled=false;}

        private void OnEnable()
        {
            EventBus<RunStateChangedEvent>.Subscribe(OnRunStateChanged);
            if (_toggleAction != null && _toggleAction.action != null)
            {
                _toggleAction.action.performed += OnTogglePerformed;
                _toggleAction.action.Enable();
            }
        }

        private void OnDisable()
        {
            EventBus<RunStateChangedEvent>.Unsubscribe(OnRunStateChanged);
            if (_toggleAction != null && _toggleAction.action != null)
                _toggleAction.action.performed -= OnTogglePerformed;
            if (_open) RestoreGameState(); // 兜底：禁用时仍打开则恢复时间/鼠标，避免卡死
        }

        private void Start()
        {
            BindRuntimeData();
            if (_panelRoot != null) _panelRoot.SetActive(false);
            _open = false;
        }

        private void OnTogglePerformed(InputAction.CallbackContext ctx) => Toggle();

        public void Toggle()
        {
            if (_open) Close();
            else Open();
        }

        public void ForceClose()
        {
            if (_open)
            {
                Close();
            }
        }

        private void Open()
        {
            if (_pauseCoordinator != null &&
                (_pauseCoordinator.HasReason(RunPauseReason.PauseMenu) ||
                 _pauseCoordinator.HasReason(RunPauseReason.TerminalResult)))
            {
                return;
            }

            // 每次打开都重新绑定，既规避 Script Execution Order，也能覆盖重开一局后的新 Runtime Clone。
            BindRuntimeData();
            _open = true;
            if (_panelRoot != null) _panelRoot.SetActive(true);
            RebuildViews();
            RefreshHeader();
            if (_pauseCoordinator != null)
            {
                _pauseCoordinator.RequestPause(RunPauseReason.WandEditor);
            }
            else
            {
                // 兼容尚未迁移到 P7 Coordinator 的旧测试 Scene；P7_DemoRun 必须绑定统一协调器。
                Time.timeScale = 0f;
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            if (_dragGhost != null) _dragGhost.enabled = false;
        }

        private void Close()
        {
            HideSpellTooltip();
            _open = false;
            if (_panelRoot != null) _panelRoot.SetActive(false);
            RestoreGameState();
        }

        private void RestoreGameState()
        {
            if (_pauseCoordinator != null)
            {
                _pauseCoordinator.ReleasePause(RunPauseReason.WandEditor);
            }
            else
            {
                Time.timeScale = 1f;
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
            if (_dragGhost != null) _dragGhost.enabled = false;
            HideSpellTooltip();
        }

        private void OnRunStateChanged(RunStateChangedEvent runEvent)
        {
            if (runEvent.CurrentState == RunState.Completed ||
                runEvent.CurrentState == RunState.Failed)
            {
                ForceClose();
            }
        }

        private void RebuildViews()
        {
            if (_palette != null) _palette.Rebuild(this);
            if (_frame != null) _frame.Rebuild(this);
        }

        private void RefreshHeader()
        {
            CastPreview preview = _wand != null
                ? CastEvaluator.Preview(_wand.Spells, _wand.BaseDraws, CastModifierState.Default)
                : new CastPreview(0f, 1);

            if (_manaCostLabel != null)
                _manaCostLabel.text = $"当前总耗蓝：{preview.ImmediateManaCost:0.#}";
            if (_remainingDrawBudgetLabel != null)
                _remainingDrawBudgetLabel.text = $"剩余施放数：{preview.RemainingDrawBudget}";
        }

        private void BindRuntimeData()
        {
            if (!TryBindCurrentRunSession())
                return;

            _wand = _runSpellSession.RuntimeWand;
            if (_palette != null)
            {
                _palette.BindLibrary(_runSpellSession.RuntimeLibrary);
            }

            if (_frame != null)
            {
                _frame.BindWand(_runSpellSession.RuntimeWand);
            }
        }

        /// <summary>
        /// Scene 切换后从静态入口找回 DontDestroyOnLoad 的单局数据。
        /// 不能把后续 Scene 的序列化字段直接指向前一 Scene 才会创建的运行时对象。
        /// </summary>
        public bool TryBindCurrentRunSession()
        {
            if (IsUsableRunSession(_runSpellSession))
                return true;

            RunSpellSession current = RunSpellSession.Current;
            if (!IsUsableRunSession(current))
                return false;

            _runSpellSession = current;
            return true;
        }

        private static bool IsUsableRunSession(RunSpellSession session)
        {
            return session != null &&
                   session.IsInitialized &&
                   session.RuntimeLibrary != null &&
                   session.RuntimeWand != null;
        }

        // ── IWandDragHandler ──
        public void BeginSpellDrag(SpellDefinition spell, SlotKind origin, int index, PointerEventData eventData)
        {
            _dragSpell = spell;
            _dragOrigin = origin;
            _dragFromIndex = index;
            _dropHandled = false;

            if (_dragGhost != null)
            {
                _dragGhost.enabled = true;
                _dragGhost.sprite = spell != null ? spell.Icon : null;
                _dragGhost.rectTransform.position = eventData.position;
            }
        }

        public void UpdateSpellDrag(PointerEventData eventData)
        {
            if (_dragGhost != null && _dragGhost.enabled)
                _dragGhost.rectTransform.position = eventData.position;
        }

        public void DropOnFrameSlot(int frameIndex)
        {
            if (_dragSpell == null || _frame == null) return;
            if (_dragOrigin == SlotKind.Palette)
                _frame.ApplyInsert(frameIndex, _dragSpell);
            else
                _frame.ApplyMove(_dragFromIndex, frameIndex);
            _dropHandled = true;
        }

        public void EndSpellDrag(PointerEventData eventData)
        {
            // 框内法术拖到框外（无 IDropHandler 接收）→ 移除
            if (!_dropHandled && _dragOrigin == SlotKind.Frame && _frame != null)
                _frame.ApplyRemove(_dragFromIndex);

            if (_frame != null) _frame.Rebuild(this); // 一次拖放结束统一重建（Destroy 延迟到帧末，安全）
            // 写回 RuntimeWand 后立即重算标题；UI 只消费解释器摘要，不复制任何法术规则。
            RefreshHeader();

            if (_dragGhost != null) _dragGhost.enabled = false;
            _dragSpell = null;
            _dropHandled = false;
        }

        public void ShowSpellTooltip(
            SpellDefinition spell,
            Vector2 screenPosition)
        {
            _tooltipPresenter?.Show(spell, screenPosition);
        }

        public void MoveSpellTooltip(Vector2 screenPosition)
        {
            _tooltipPresenter?.Move(screenPosition);
        }

        public void HideSpellTooltip()
        {
            _tooltipPresenter?.Hide();
        }
    }
}
