using System;
using Game.Skills;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    public enum WandSpellCellRole : byte { Sequence, Execution, Library }

    /// <summary>三处使用同一种格子；只消费冻结指令与View状态，不包含伤害/Draw计算。</summary>
    public sealed class WandSpellCellView : MonoBehaviour
    {
        private Image _frame,_fill,_icon;
        private Text _name,_number,_glyph,_badge,_kind;
        private Action<int> _select;
        private Button _button;
        private Color _glyphTint;
        public RectTransform Rect => (RectTransform)transform;
        public int CardIndex { get; private set; }=-1;
        public string InstanceId { get; private set; }
        public int SourceSlot { get; private set; } = -1;
        public int ScopeId { get; private set; }=-1;
        public int OutputId { get; private set; }=-1;
        public WandSpellCellRole Role { get; private set; }
        public bool IsHighlighted { get; private set; }
        public bool IsSelected { get; private set; }
        public bool IsSkipped { get; private set; }
        public bool IsRevealed { get; private set; }
        public Button Button => _button;
        internal int GridColumn { get; set; }

        public static WandSpellCellView Create(Transform parent,Font font,Action<int> select)
        {
            var rect=WandWorkbenchWidgets.Panel("SpellCell",parent,WandWorkbenchWidgets.Gold);
            var cell=rect.gameObject.AddComponent<WandSpellCellView>();cell._select=select;
            cell._frame=rect.GetComponent<Image>();cell._button=rect.gameObject.AddComponent<Button>();
            cell._button.transition=Selectable.Transition.None;cell._button.onClick.AddListener(cell.Click);
            var fill=WandWorkbenchWidgets.Panel("Fill",rect,new Color(.035f,.055f,.075f));
            fill.anchorMin=Vector2.zero;fill.anchorMax=Vector2.one;fill.offsetMin=Vector2.one*2;fill.offsetMax=Vector2.one*-2;
            cell._fill=fill.GetComponent<Image>();cell._fill.raycastTarget=false;
            var icon=WandWorkbenchWidgets.Rect("Icon",rect);WandWorkbenchWidgets.Place(icon,8,8,80,66);
            cell._icon=icon.gameObject.AddComponent<Image>();cell._icon.preserveAspect=true;cell._icon.raycastTarget=false;
            cell._glyph=WandWorkbenchWidgets.Label("Glyph",rect,font,44);cell._glyph.alignment=TextAnchor.MiddleCenter;
            WandWorkbenchWidgets.Place(cell._glyph.rectTransform,8,4,80,70);
            cell._name=WandWorkbenchWidgets.Label("Name",rect,font,18);cell._name.alignment=TextAnchor.MiddleCenter;
            cell._name.verticalOverflow=VerticalWrapMode.Truncate;WandWorkbenchWidgets.Place(cell._name.rectTransform,4,76,88,26);
            cell._number=WandWorkbenchWidgets.Label("SourceSlot",rect,font,14);
            cell._number.horizontalOverflow=HorizontalWrapMode.Overflow;
            WandWorkbenchWidgets.Place(cell._number.rectTransform,5,2,44,22);
            cell._kind=WandWorkbenchWidgets.Label("Role",rect,font,17);cell._kind.alignment=TextAnchor.UpperRight;
            WandWorkbenchWidgets.Place(cell._kind.rectTransform,61,2,28,24);
            cell._badge=WandWorkbenchWidgets.Label("State",rect,font,14);cell._badge.alignment=TextAnchor.MiddleCenter;
            WandWorkbenchWidgets.Place(cell._badge.rectTransform,19,53,58,22);
            return cell;
        }
        private void Click(){if(CardIndex>=0)_select?.Invoke(CardIndex);}
        public void Bind(in SpellInstruction card,int index,WandSpellCellRole role,int scope=-1,int output=-1)
        {
            CardIndex=index;InstanceId=card.InstanceId;SourceSlot=card.SourceSlot;ScopeId=scope;OutputId=output;Role=role;_button.interactable=true;
            EnsureResearchInteraction();
            _name.text=card.DisplayName;_number.text=card.IsIntrinsic?"固定":(card.SourceSlot+1).ToString();
            _number.gameObject.SetActive(role!=WandSpellCellRole.Library);
            _icon.sprite=card.Icon;_icon.enabled=card.Icon!=null;_glyph.enabled=card.Icon==null;
            _glyph.text=card.Kind==SpellKind.Copy?(card.CopyMode==SpellCopyMode.Alpha?"α":"γ"):card.Kind==SpellKind.Modify?"✦":card.Kind==SpellKind.Multicast?"Ⅱ":"➶";
            _kind.text=card.Kind==SpellKind.Copy?(card.CopyMode==SpellCopyMode.Alpha?"α":"γ"):card.Kind==SpellKind.Modify?"+":card.Kind==SpellKind.Multicast?"Ⅱ":
                card.Emission.PayloadTrigger==PayloadTriggerMode.None?"":"↳";
            _glyphTint=card.Kind==SpellKind.Modify?new Color(.72f,.49f,.98f):card.Kind==SpellKind.Multicast?
                new Color(.44f,.63f,1):new Color(1,.60f,.20f);
            SetState(false,false,true,false,false);
        }
        public void BindEmpty(int slot)
        {
            CardIndex=-1;InstanceId=null;SourceSlot=slot;ScopeId=OutputId=-1;Role=WandSpellCellRole.Sequence;_button.interactable=false;
            EnsureResearchInteraction();
            IsSelected=IsHighlighted=IsSkipped=IsRevealed=false;
            _number.text=(slot+1).ToString();_name.text="";_kind.text="";_badge.text="";
            _number.gameObject.SetActive(true);
            _icon.enabled=false;_glyph.enabled=false;_frame.color=new Color(.38f,.32f,.22f,.45f);
            _fill.color=new Color(.025f,.035f,.05f,.65f);
        }
        public void SetState(bool selected,bool related,bool revealed,bool skipped,bool current)
        {
            IsSelected=selected;IsHighlighted=related;IsRevealed=revealed;IsSkipped=skipped;
            _frame.color=selected?new Color(1,.80f,.31f):related?new Color(.48f,.85f,.95f):
                new Color(.54f,.43f,.26f,revealed ? .8f : .3f);
            _fill.color=selected?new Color(.16f,.115f,.05f):related?new Color(.045f,.15f,.19f):new Color(.035f,.055f,.075f);
            float alpha=skipped ? .3f : revealed ? 1 : .35f;
            _icon.color=new Color(1,1,1,alpha);_name.color=new Color(.94f,.90f,.80f,alpha);
            _glyph.color=new Color(_glyphTint.r,_glyphTint.g,_glyphTint.b,alpha);
            _number.color=new Color(.85f,.70f,.42f,alpha);_kind.color=_number.color;
            _badge.text=skipped?"跳过":current?"读取":"";
            _badge.color=skipped?new Color(.8f,.7f,.6f):new Color(1,.85f,.43f);
        }
        public void SetBorrowedLabel(string label,bool revealed)
        {if(label!=null && revealed)_badge.text=label;}
        public void SetObserved(bool released,bool cancelled)
        {_badge.text=released?"已释放":cancelled?"已取消":"未释放";}
        private void EnsureResearchInteraction()
        {
            if (SourceSlot<0 || Role == WandSpellCellRole.Execution || GetComponent<WandResearchCellInteraction>() != null) return;
            var presenter = GetComponentInParent<WandProgramWorkbenchPresenter>();
            if (presenter != null && presenter.IsOwned) gameObject.AddComponent<WandResearchCellInteraction>();
        }
    }
}
