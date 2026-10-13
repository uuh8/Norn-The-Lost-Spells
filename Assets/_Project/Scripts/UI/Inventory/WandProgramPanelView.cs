using System.Collections.Generic;
using Game.Run;
using Game.Skills;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>三杖程序始终展开，每个DropTarget保存自己的OwnerId，查看不改变手持。</summary>
    public sealed class WandProgramPanelView : MonoBehaviour
    {
        [SerializeField] private int _wandSlot;
        [SerializeField] private Text _header;
        [SerializeField] private Image _icon;
        [SerializeField] private RectTransform _program;
        [SerializeField] private GameObject _expandedRoot;
        [SerializeField] private InventoryDropTarget _equipmentTarget;
        private readonly List<GameObject> _slots=new List<GameObject>();
        private readonly List<InventoryItemView> _cards=new List<InventoryItemView>();
        private readonly List<EmitCommand> _preview=new List<EmitCommand>();
        private InventoryDropTarget _regionTarget;
        private EquippedWandDragView _wandDrag;
        private InventoryGridLinesGraphic _lines;
        public void Configure(int slot,Text header,RectTransform program,GameObject expandedRoot,InventoryDropTarget equipmentTarget)
        { _wandSlot=slot; _header=header; _program=program; _expandedRoot=expandedRoot; _equipmentTarget=equipmentTarget; }
        public void Refresh(SinglePlayerInventoryController controller,InventoryItemView prefab,InventoryItemSnapshot[] items,int count,string actor)
        {
            if (_icon == null)
            {
                var portrait = new GameObject("WandModelIcon", typeof(RectTransform), typeof(Image)); portrait.transform.SetParent(transform, false);
                _icon = portrait.GetComponent<Image>(); _icon.preserveAspect = true; _icon.raycastTarget = false;
                var rect = _icon.rectTransform; rect.anchorMin = rect.anchorMax = new Vector2(1, 1); rect.pivot = new Vector2(1, 1);
                rect.anchoredPosition = new Vector2(-8, -8); rect.sizeDelta = new Vector2(80, 36);
            }
            if(_expandedRoot!=null && !_expandedRoot.activeSelf)_expandedRoot.SetActive(true);
            // Content的LayoutGroup负责Wand行，作者负责Attributes/Frame/Icon。
            // 仅绑定Sprite与拖拽组件，不能在Refresh重设这些子对象的RectTransform。
            _icon.raycastTarget=true;
            // UGUI只派发给最近的IDropHandler：背景给整行目标，程序子目标由Controller按源Kind重路由。
            if(_regionTarget==null)
            {
                _regionTarget=GetComponent<InventoryDropTarget>();
                if(_regionTarget==null)_regionTarget=gameObject.AddComponent<InventoryDropTarget>();
                var background=GetComponent<Image>();
                if(background==null){background=gameObject.AddComponent<Image>();background.color=Color.clear;}
                background.raycastTarget=true;
            }
            _regionTarget.Initialize(controller,InventoryDropKind.EquippedWand,actor,_wandSlot);
            _equipmentTarget.Initialize(controller,InventoryDropKind.EquippedWand,actor,_wandSlot);
            InventoryItemSnapshot wand=default; bool found=false;
            for (int i=0;i<count;i++) if (items[i].Location.Kind==ItemLocationKind.EquippedWand && items[i].Location.OwnerId==actor
                && items[i].Location.SlotIndex==_wandSlot) { wand=items[i]; found=true; break; }
            int capacity=found?wand.Rules.WandProgramCapacity:0, occupied=0, protectedCount=0;
            var protection=controller.Session.RunCoordinator?.Protection;
            for (int i=0;i<count;i++) if (items[i].Location.Kind==ItemLocationKind.WandSlot && items[i].Location.OwnerId==wand.InstanceId)
            { occupied++; if(protection!=null && protection.Contains(items[i].InstanceId)) protectedCount++; }
            if(_icon!=null) { _icon.sprite=found?wand.Icon:null;_icon.enabled=found && wand.Icon!=null; }
            if(_wandDrag==null)_wandDrag=_icon.GetComponent<EquippedWandDragView>()??_icon.gameObject.AddComponent<EquippedWandDragView>();
            _wandDrag.Bind(controller,wand,found);
            _header.font=controller.UiFont; _header.text=(_wandSlot+1)+" · "+(found?wand.DisplayName:"空法杖位 · 拖入法杖");
            var read=controller.Session.WandsReadOnly;
            if (found && read.TryGetState(wand.InstanceId,out var state) && read.TryGetProgram(wand.InstanceId,out var program))
            {
                // UI打开会暂停。离散库存刷新才求预览，持续恢复的资源由外部HUD显示。
                var summary=CastEvaluator.Evaluate(program.Program,program.BaseDraws,float.MaxValue,CastModifierState.Default,_preview);
                _header.text+="\nMana "+state.CurrentMana.ToString("0.#")+"/"+state.MaxMana.ToString("0.#")
                    +" · 恢复 "+state.ManaRegenPerSecond.ToString("0.#")+"/s · 冷却 "+state.CooldownRemaining.ToString("0.##")+"/"+state.CastCooldown.ToString("0.##")+"s"
                    +"\n法术槽 "+occupied+"/"+capacity+" · 基础抽取 "+program.BaseDraws+" · 本次预估蓝耗 "+summary.ManaSpent.ToString("0.#")
                    +" · 受保护法术 "+protectedCount;
            }
            while (_slots.Count<capacity)
            {
                int index=_slots.Count;
                var template=controller.SlotPrefabs!=null?controller.SlotPrefabs.WandProgramSlot:null;
                if(template!=null)
                {
                    var view=Instantiate(template,_program);view.name="SpellSlot"+(index+1);
                    view.BindIndex(index,controller.UiFont);_slots.Add(view.gameObject);_cards.Add(view.Card);continue;
                }
                var root=new GameObject("SpellSlot"+(index+1),typeof(RectTransform),typeof(Image),typeof(InventoryDropTarget));
                root.transform.SetParent(_program,false); root.GetComponent<Image>().color=new Color(.08f,.12f,.18f,.92f);
                _slots.Add(root); _cards.Add(Instantiate(prefab,root.transform));
                var label=new GameObject("Index",typeof(RectTransform),typeof(Text)); label.transform.SetParent(root.transform,false);
                var t=label.GetComponent<Text>(); t.font=controller.UiFont; t.fontSize=12; t.text=(index+1).ToString(); t.alignment=TextAnchor.LowerCenter; t.raycastTarget=false;
                var lr=(RectTransform)label.transform; lr.anchorMin=Vector2.zero; lr.anchorMax=Vector2.one; lr.offsetMin=new Vector2(0,-16); lr.offsetMax=new Vector2(0,-68);
            }
            _program.sizeDelta=new Vector2(capacity*76,96);
            for (int slot=0;slot<_slots.Count;slot++)
            {
                bool used=slot<capacity; _slots[slot].SetActive(used); if (!used) continue;
                var rect=(RectTransform)_slots[slot].transform; rect.anchorMin=rect.anchorMax=rect.pivot=new Vector2(0,1);
                rect.anchoredPosition=new Vector2(slot*76,0); rect.sizeDelta=new Vector2(76,76);
                _slots[slot].GetComponent<InventoryDropTarget>().Initialize(controller,InventoryDropKind.ProgramSlot,wand.InstanceId,slot);
                InventoryItemSnapshot spell=default; bool hasSpell=false;
                for (int i=0;i<count;i++) if (items[i].Location.Kind==ItemLocationKind.WandSlot && items[i].Location.OwnerId==wand.InstanceId
                    && items[i].Location.SlotIndex==slot) { spell=items[i]; hasSpell=true; break; }
                var card=_cards[slot]; card.gameObject.SetActive(hasSpell);
                if (hasSpell)
                {
                    if(!card.AuthoredLayout){card.Rect.anchorMin=card.Rect.anchorMax=card.Rect.pivot=new Vector2(0,1); card.Rect.anchoredPosition=Vector2.zero; card.Rect.sizeDelta=new Vector2(76,76);}
                    card.Bind(controller,spell,InventoryItemDisplay.Program,48);
                }
            }
            if(_lines==null)_lines=InventoryGridLinesGraphic.Ensure(_program);
            _lines.Configure(capacity,1,76);_lines.transform.SetAsLastSibling();
        }
    }
}
