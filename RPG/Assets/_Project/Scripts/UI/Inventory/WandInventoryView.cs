using System.Collections.Generic;
using Game.Run;
using Game.Skills;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>三个装备位和当前选中法杖的程序槽；程序槽数量读取固定配置，与背包尺寸分开。</summary>
    public sealed class WandInventoryView : MonoBehaviour
    {
        [SerializeField] private RectTransform _equipmentRoot, _programRoot;
        [SerializeField] private Text _programTitle;
        [SerializeField] private Text _stats;
        private readonly List<EmitCommand> _preview=new List<EmitCommand>();
        [SerializeField] private bool _wideLayout;
        [SerializeField, Tooltip("仅仓库启用：三个选择卡共用下方一个程序。背包保持三根全部展开。")]
        private bool _selectorLayout;
        private int _selectedSlot;
        [SerializeField] private WandProgramPanelView[] _panels;
        public void ConfigurePanels(WandProgramPanelView[] panels) { _panels=panels; }
        private readonly List<InventoryItemView> _equipment = new List<InventoryItemView>();
        private readonly List<GameObject> _equipmentSlots = new List<GameObject>();
        private readonly List<Text> _emptySlotLabels = new List<Text>();
        private readonly List<GameObject> _programSlots = new List<GameObject>();
        private readonly List<InventoryItemView> _spells = new List<InventoryItemView>();
        private string _selectedWandId;
        public void SelectWand(string instanceId) { _selectedWandId = instanceId; }
        public void SelectSlot(int slot) { _selectedSlot = Mathf.Clamp(slot, 0, 2); _selectedWandId = null; }
        public void Refresh(SinglePlayerInventoryController controller, InventoryItemView prefab, InventoryItemSnapshot[] items, int count, string actorId)
        {
            if (!_selectorLayout && _panels!=null && _panels.Length>0)
            {
                for (int i=0;i<_panels.Length;i++) if (_panels[i]!=null) _panels[i].Refresh(controller,prefab,items,count,actorId);
                return;
            }
            // 展示模式是每个Canvas自己的配置，不能改共享法杖数据或背包的三行展开方式。
            if (_selectorLayout)
            {
                if (_panels != null) for (int i = 0; i < _panels.Length; i++)
                    if (_panels[i] != null) _panels[i].gameObject.SetActive(false);
                if (_selectedWandId != null) for (int i = 0; i < count; i++)
                    if (items[i].InstanceId == _selectedWandId && items[i].Location.Kind == ItemLocationKind.EquippedWand && items[i].Location.OwnerId == actorId)
                    { _selectedSlot = items[i].Location.SlotIndex; break; }
                // 按装备位跟随替换结果；空位清掉旧OwnerId，避免继续编辑已移入仓库的上一根法杖。
                _selectedWandId = null;
                for (int i = 0; i < count; i++) if (items[i].Location.Kind == ItemLocationKind.EquippedWand && items[i].Location.OwnerId == actorId && items[i].Location.SlotIndex == _selectedSlot)
                { _selectedWandId = items[i].InstanceId; break; }
            }
            float cardWidth = _selectorLayout ? Mathf.Max(1, (_equipmentRoot.rect.width - 16) / 3) : _wideLayout ? 220 : 150;
            float cardHeight = _selectorLayout ? Mathf.Max(1, _equipmentRoot.rect.height) : _wideLayout ? 100 : 52;
            var styles=controller.SlotPrefabs;
            while (_equipmentSlots.Count < 3)
            {
                int slot = _equipmentSlots.Count;
                _equipmentSlots.Add(Slot(styles!=null?styles.WandSelectorSlot:null,_equipmentRoot, new Vector2(slot * (_wideLayout?230:158), 0), new Vector2(_wideLayout?220:150,_wideLayout?100:52)));
                var authoredSlot=_equipmentSlots[slot].GetComponent<InventorySlotView>();
                _equipment.Add(authoredSlot!=null?authoredSlot.Card:Instantiate(prefab, _equipmentSlots[slot].transform));
                if (_selectorLayout)
                {
                    // 仅创建三个按钮时捕获slot；稳定帧不创建delegate。空位也能选中并接收装备拖入。
                    int index = slot; var button = _equipmentSlots[slot].GetComponent<Button>()??_equipmentSlots[slot].AddComponent<Button>();
                    button.transition = Selectable.Transition.None; button.onClick.AddListener(() => controller.InspectWandSlot(index));
                    if(authoredSlot!=null){_emptySlotLabels.Add(authoredSlot.EmptyLabel);continue;}
                    var label = new GameObject("EmptyWand_空位提示", typeof(RectTransform), typeof(Text)); label.transform.SetParent(button.transform, false);
                    var text = label.GetComponent<Text>(); text.font = controller.UiFont; text.text = (slot + 1) + " · 空法杖位\n拖入法杖";
                    text.fontSize = 16; text.alignment = TextAnchor.MiddleCenter; text.raycastTarget = false;
                    var labelRect = (RectTransform)label.transform; labelRect.anchorMin = Vector2.zero; labelRect.anchorMax = Vector2.one; labelRect.offsetMin = labelRect.offsetMax = Vector2.zero;
                    _emptySlotLabels.Add(text);
                }
            }
            for (int slot = 0; slot < 3; slot++)
            {
                _equipmentSlots[slot].GetComponent<InventoryDropTarget>().Initialize(controller, InventoryDropKind.EquippedWand, actorId, slot);
                if (_selectorLayout)
                {
                    var rect = (RectTransform)_equipmentSlots[slot].transform;
                    rect.anchoredPosition = new Vector2(slot * (cardWidth + 8), 0); rect.sizeDelta = new Vector2(cardWidth, cardHeight);
                    var authoredSlot=_equipmentSlots[slot].GetComponent<InventorySlotView>();
                    if(authoredSlot!=null)authoredSlot.SetSelected(slot==_selectedSlot);
                    else _equipmentSlots[slot].GetComponent<Image>().color = slot == _selectedSlot ? new Color(.95f, .66f, .14f, 1) : new Color(.09f, .15f, .23f, 1);
                }
                InventoryItemSnapshot equipped = default; bool found = false;
                for (int i = 0; i < count; i++) if (items[i].Location.Kind == ItemLocationKind.EquippedWand
                    && items[i].Location.OwnerId == actorId && items[i].Location.SlotIndex == slot) { equipped = items[i]; found = true; break; }
                _equipment[slot].gameObject.SetActive(found);
                if(_selectorLayout)
                {
                    var authoredSlot=_equipmentSlots[slot].GetComponent<InventorySlotView>();
                    if(authoredSlot!=null)authoredSlot.ShowEmpty(!found,slot,controller.UiFont);
                    else _emptySlotLabels[slot].gameObject.SetActive(!found);
                }
                if (found)
                {
                    if(!_equipment[slot].AuthoredLayout)PlaceView(_equipment[slot], new Vector2(_wideLayout?220:150,_wideLayout?100:52));
                    _equipment[slot].Bind(controller, equipped, InventoryItemDisplay.Row, 48);
                    if(_wideLayout && controller.Session.WandsReadOnly.TryGetState(equipped.InstanceId,out var resource))
                        _equipment[slot].SetCaption((slot+1)+" · "+equipped.DisplayName+"\nMana "+Mathf.RoundToInt(resource.MaxMana));
                    if (_selectorLayout)
                    {
                        if(!_equipment[slot].AuthoredLayout){PlaceView(_equipment[slot], new Vector2(cardWidth - 6, cardHeight - 6));_equipment[slot].Rect.anchoredPosition = new Vector2(3, -3);}
                        string resourceCaption = "";
                        if (controller.Session.WandsReadOnly.TryGetState(equipped.InstanceId, out var cardState))
                            resourceCaption = "Mana " + Mathf.RoundToInt(cardState.MaxMana);
                        _equipment[slot].ConfigureWandCard((slot + 1) + " · " + equipped.DisplayName, resourceCaption, controller.UiFont,controller.BuiltinUiFont);
                    }
                    else if (_selectedWandId == null) _selectedWandId = equipped.InstanceId;
                }
            }
            InventoryItemSnapshot wand = default; bool hasWand = false;
            for (int i = 0; i < count; i++) if (items[i].InstanceId == _selectedWandId && items[i].Kind == ItemKind.Wand) { wand = items[i]; hasWand = true; break; }
            int capacity = hasWand ? wand.Rules.WandProgramCapacity : 0;
            int occupied=0;for(int i=0;i<count;i++)if(items[i].Location.Kind==ItemLocationKind.WandSlot && items[i].Location.OwnerId==wand.InstanceId)occupied++;
            if (_programTitle != null) { _programTitle.fontSize=22;_programTitle.text = hasWand ? wand.DisplayName + " · 法术槽 "+occupied+" / " + capacity : "选择一根法杖查看法术"; }
            if(_wideLayout && hasWand && _programTitle != null && controller.Session.WandsReadOnly.TryGetProgram(wand.InstanceId,out var program))_programTitle.text+=" · 基础抽取 "+program.BaseDraws;
            if(_stats!=null)
            {
                _stats.text=hasWand?wand.DisplayName:"未装备法杖";
                if(hasWand && controller.Session.WandsReadOnly.TryGetState(wand.InstanceId,out var state)
                    && controller.Session.WandsReadOnly.TryGetProgram(wand.InstanceId,out var selectedProgram))
                {
                    // 只在离散UI刷新中求根层预算，不提交资源；不能把整串成本相加冒充每次施法蓝耗。
                    float mana=controller.Session.UseSpellProgrammingV2?V2ManaPreview(controller.Session,wand.InstanceId):
                        CastEvaluator.Evaluate(selectedProgram.Program,selectedProgram.BaseDraws,float.MaxValue,CastModifierState.Default,_preview).ManaSpent;
                    _stats.supportRichText=true;_stats.fontSize=20;_stats.resizeTextForBestFit=false;
                    _stats.text=ResourceCaption(state)+" · "+ManaPreviewCaption(selectedProgram,mana);
                }
            }
            while (_programSlots.Count < capacity)
            {
                int slot = _programSlots.Count;
                _programSlots.Add(Slot(styles!=null?styles.WandProgramSlot:null,_programRoot, _wideLayout?new Vector2(slot*76,0):new Vector2(slot % 6 * 76, -slot / 6 * 60), new Vector2(76,_wideLayout?76:60)));
                var authoredSlot=_programSlots[slot].GetComponent<InventorySlotView>();
                _spells.Add(authoredSlot!=null?authoredSlot.Card:Instantiate(prefab, _programSlots[slot].transform));
                if(authoredSlot!=null)authoredSlot.BindIndex(slot,controller.UiFont);
                if (_selectorLayout && authoredSlot==null)
                {
                    var index = new GameObject("Index", typeof(RectTransform), typeof(Text)); index.transform.SetParent(_programSlots[slot].transform, false);
                    var label = index.GetComponent<Text>(); label.font = controller.UiFont; label.fontSize = 12; label.text = (slot + 1).ToString(); label.alignment = TextAnchor.LowerCenter; label.raycastTarget = false;
                    var rect = (RectTransform)index.transform; rect.anchorMin = Vector2.zero; rect.anchorMax = new Vector2(1, .15f); rect.offsetMin = rect.offsetMax = Vector2.zero;
                }
            }
            if (_programRoot != null) _programRoot.sizeDelta = _wideLayout?new Vector2(capacity*76,96):new Vector2(456, Mathf.CeilToInt(capacity / 6f) * 60);
            for (int slot = 0; slot < _programSlots.Count; slot++)
            {
                bool used = slot < capacity; _programSlots[slot].SetActive(used); if (!used) continue;
                _programSlots[slot].GetComponent<InventoryDropTarget>().Initialize(controller, InventoryDropKind.ProgramSlot, wand.InstanceId, slot);
                bool found = false; InventoryItemSnapshot spell = default;
                for (int i = 0; i < count; i++) if (items[i].Location.Kind == ItemLocationKind.WandSlot
                    && items[i].Location.OwnerId == wand.InstanceId && items[i].Location.SlotIndex == slot) { spell = items[i]; found = true; break; }
                _spells[slot].gameObject.SetActive(found);
                if (found) { if(!_spells[slot].AuthoredLayout)PlaceView(_spells[slot], new Vector2(70,_selectorLayout?76:_wideLayout?90:54)); _spells[slot].Bind(controller, spell, InventoryItemDisplay.Program, 48); }
            }
        }
        // 仅在库存离散Refresh格式化文字，不在Update分配；显示取整不修改真实随机资源精度，Rich Text不改作者Rect。
        internal static string ResourceCaption(in WandStateSnapshot state) =>
            "<color=#389CFF>Mana上限 "+Mathf.RoundToInt(state.MaxMana)+"</color>"
            +" · <color=#9BDFFF>恢复 "+Mathf.RoundToInt(state.ManaRegenPerSecond)+"/s</color>"
            +" · <color=#FFD65C>冷却 "+state.CastCooldown.ToString("0.##")+"s</color>";
        internal static string ManaPreviewCaption(in WandProgramSnapshot program,float immediate)
        {
            // 槽内合计帮助观察编辑变化，但不能替代根层预算：未被抽取的Emit和Trigger后缀本次不支付。
            float total=0;for(int i=0;i<program.Program.Count;i++)if(program.Program[i]!=null)total+=Mathf.Max(0,program.Program[i].ManaCost);
            return "本次预估蓝耗 "+Mathf.RoundToInt(immediate)+" · 槽内合计 "+Mathf.RoundToInt(total);
        }
        internal static float V2ManaPreview(SinglePlayerInventorySession session,string wand)
        {
            // 只在离散库存Refresh执行，与研究台同源；不再对Copy调用Legacy解释器。
            if(!session.TryCaptureWandResearch(wand,out var snapshot))return 0;
            var b=new SpellBuildBuffer(false);
            return new SpellProgramMachine().TryBuild(snapshot.Execution.Program,snapshot.Execution.Draw,snapshot.Inputs,b,out var summary)
                ?Mathf.Max(0,snapshot.Inputs.AvailableMana-summary.ManaAfter):0;
        }
        private static GameObject Slot(InventorySlotView template,RectTransform parent, Vector2 position, Vector2 size)
        {
            GameObject slot;
            if(template!=null){slot=Instantiate(template,parent).gameObject;slot.name="Inventory slot";}
            else{slot = new GameObject("Inventory slot", typeof(RectTransform), typeof(Image), typeof(InventoryDropTarget));slot.transform.SetParent(parent,false);slot.GetComponent<Image>().color = new Color(.12f,.16f,.2f,1);}
            var rect = (RectTransform)slot.transform; rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = position; rect.sizeDelta = size;return slot;
        }
        private static void PlaceView(InventoryItemView view, Vector2 size)
        { view.Rect.anchorMin = view.Rect.anchorMax = view.Rect.pivot = new Vector2(0, 1); view.Rect.anchoredPosition = Vector2.zero; view.Rect.sizeDelta = size; }
    }
}
