using System.Collections.Generic;
using Game.Skills;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>行代表共享参数的Scope；卡牌来自Trace，连接来自实际Owner。缩放不重建规则和对象。</summary>
    public sealed class WandScopeGraphView : MonoBehaviour
    {
        private sealed class RowView
        {
            internal RectTransform Root;
            internal Button Header;
            internal Text Label;
            internal Text Empty;
            internal int Scope;
            internal readonly List<WandSpellCellView> Cells=new List<WandSpellCellView>();
        }
        private WandProgramWorkbenchPresenter _presenter;
        private WandProgramCanvasViewport _viewport;
        private RectTransform _drawing;
        private WandProgramLinkGraphic _links;
        private Font _font;
        private SpellPreviewRoot _root;
        private readonly List<RowView> _rows=new List<RowView>();
        private readonly List<WandSpellCellView> _cells=new List<WandSpellCellView>();
        private bool[] _collapsed,_visible;
        private Vector2[] _positions;
        public WandProgramGridProjection Projection { get; private set; }
        public IReadOnlyList<WandSpellCellView> Cells => _cells;
        private static readonly ProfilerMarker RefreshMarker=new ProfilerMarker("Norn.WandPreview.Refresh");
        public void Initialize(WandProgramWorkbenchPresenter presenter,WandProgramCanvasViewport viewport,Font font)
        {
            _presenter=presenter;_viewport=viewport;_drawing=viewport.Drawing;_font=font;
            var lineRect=WandWorkbenchWidgets.Rect("TriggerLinks",_drawing);lineRect.anchorMin=Vector2.zero;lineRect.anchorMax=Vector2.one;
            lineRect.offsetMin=lineRect.offsetMax=Vector2.zero;lineRect.pivot=new Vector2(0,1);
            _links=lineRect.gameObject.AddComponent<WandProgramLinkGraphic>();_links.raycastTarget=false;
            presenter.Changed+=Refresh;
        }
        private void OnDestroy(){if(_presenter!=null)_presenter.Changed-=Refresh;}
        private void Refresh()
        {
            using(RefreshMarker.Auto())
            {
                bool changed=!ReferenceEquals(_root,_presenter.CurrentRoot);
                if(changed)
                {
                    _root=_presenter.CurrentRoot;_cells.Clear();
                    if(_root==null)
                    {
                        Projection=null;foreach(var old in _rows)old.Root.gameObject.SetActive(false);_links.Begin();_links.End();
                        _viewport.SetLogicalSize(Vector2.one);_viewport.ResetView();return;
                    }
                    Projection=new WandProgramGridProjection(_root);int count=Projection.Rows.Count;
                    _collapsed=new bool[count];_visible=new bool[count];_positions=new Vector2[count];
                    for(int i=0;i<count;i++)BindRow(i,Projection.Rows[i]);
                    for(int i=count;i<_rows.Count;i++)_rows[i].Root.gameObject.SetActive(false);
                }
                if(_root==null)return;
                bool layoutChanged=changed;
                for(int i=0;i<Projection.Rows.Count;i++)
                {
                    int scope=Projection.Rows[i].ScopeId;bool collapsed=_presenter.IsScopeCollapsed(scope);
                    if(_collapsed[scope]!=collapsed){_collapsed[scope]=collapsed;layoutChanged=true;}
                }
                if(layoutChanged){LayoutRows();if(changed)_viewport.ResetView();}
                int current=_presenter.Cursor>0?_root.Trace.Steps[_presenter.Cursor-1].CardIndex:-1;
                for(int i=0;i<_cells.Count;i++)
                {
                    var cell=_cells[i];bool related=false;
                    if(cell.OutputId>=0 && _presenter.IsOutputSettled(cell.OutputId))
                        for(int j=0;j<_presenter.HighlightedOutputs.Count;j++)if(_presenter.HighlightedOutputs[j]==cell.OutputId){related=true;break;}
                    var card=Projection.FindScope(cell.ScopeId).Cards[cell.GridColumn];
                    related |= card.BorrowedFrom>=0 && card.BorrowedFrom==_presenter.SelectedCard;
                    cell.SetState(_presenter.SelectedCard==cell.CardIndex,related,card.ReadAtStep<_presenter.Cursor,
                        card.Skipped && card.ReadAtStep<_presenter.Cursor,current==cell.CardIndex);
                    cell.SetBorrowedLabel(card.BorrowedLabel,card.ReadAtStep<_presenter.Cursor);
                    if(_presenter.IsHistory && card.OutputId>=0 && _presenter.RecordedReleased!=null)
                        cell.SetObserved(_presenter.RecordedReleased[card.OutputId],_presenter.RecordedCancelledLayers!=null && _presenter.RecordedCancelledLayers[card.ScopeId]);
                }
                DrawLinks();
            }
        }
        private void BindRow(int index,WandProgramGridRow row)
        {
            if(index==_rows.Count)
            {
                var view=new RowView();view.Root=WandWorkbenchWidgets.Panel("SpellGroup",_drawing,new Color(.045f,.073f,.095f,.92f));
                view.Header=WandWorkbenchWidgets.Button("GroupHeader",view.Root,_font,"",()=>_presenter.ToggleScope(view.Scope));
                view.Label=view.Header.GetComponentInChildren<Text>();view.Label.fontSize=20;view.Label.color=WandWorkbenchWidgets.Gold;
                view.Empty=WandWorkbenchWidgets.Label("EmptyGroup",view.Root,_font,20);view.Empty.text="空";
                WandWorkbenchWidgets.Place(view.Empty.rectTransform,WandProgramGridProjection.LabelWidth,36,96,40);
                _rows.Add(view);
            }
            var target=_rows[index];target.Scope=row.ScopeId;
            for(int i=0;i<row.Cards.Count;i++)
            {
                if(i==target.Cells.Count)target.Cells.Add(WandSpellCellView.Create(target.Root,_font,_presenter.SelectCard));
                var cell=target.Cells[i];var data=row.Cards[i];cell.name="ExecutionCard"+data.CardIndex;cell.GridColumn=i;
                cell.gameObject.SetActive(true);cell.Bind(_root.Graph.Program.Instructions[data.CardIndex],data.CardIndex,
                    WandSpellCellRole.Execution,row.ScopeId,data.OutputId);
                WandWorkbenchWidgets.Place(cell.Rect,WandProgramGridProjection.LabelWidth+i*WandProgramGridProjection.CardPitch,0,
                    WandProgramGridProjection.CardWidth,WandProgramGridProjection.CardHeight);
                _cells.Add(cell);
            }
            for(int i=row.Cards.Count;i<target.Cells.Count;i++)target.Cells[i].gameObject.SetActive(false);
        }
        private void LayoutRows()
        {
            float y=WandProgramGridProjection.Padding,width=1;
            for(int i=0;i<Projection.Rows.Count;i++)
            {
                var row=Projection.Rows[i];var view=_rows[i];int parent=row.ParentScopeId;
                bool visible=parent<0 || _visible[parent] && !_collapsed[parent];_visible[row.ScopeId]=visible;
                view.Root.gameObject.SetActive(visible);if(!visible)continue;
                bool collapsed=_collapsed[row.ScopeId];var pos=new Vector2(row.Position.x,y);_positions[row.ScopeId]=pos;
                float rowWidth=WandProgramGridProjection.LabelWidth+(collapsed?0:Mathf.Max(1,row.Cards.Count)*WandProgramGridProjection.CardPitch);
                WandWorkbenchWidgets.Place(view.Root,pos.x,pos.y,rowWidth,collapsed?54:WandProgramGridProjection.CardHeight);
                WandWorkbenchWidgets.Place((RectTransform)view.Header.transform,8,collapsed?2:25,132,48);
                string title="施放时";
                if(row.OwnerOutputId>=0)
                {
                    var owner=_root.Graph.Outputs[row.OwnerOutputId];
                    // 空Payload会让输出命令退化；行的触发身份仍取冻结源指令，不能被误画成另一种Trigger。
                    var trigger=_root.Graph.Program.Instructions[owner.SourceCardIndex].Emission.PayloadTrigger;
                    title=trigger==PayloadTriggerMode.AfterDelay?"计时后":trigger==PayloadTriggerMode.OnTermination?"终止时":"命中后";
                }
                view.Label.text=collapsed?title+"  ▸":title+"  ▾";
                view.Empty.gameObject.SetActive(!collapsed && row.Cards.Count==0);
                for(int j=0;j<view.Cells.Count;j++)view.Cells[j].gameObject.SetActive(!collapsed && j<row.Cards.Count);
                width=Mathf.Max(width,pos.x+rowWidth+WandProgramGridProjection.Padding);
                y+=collapsed?74:WandProgramGridProjection.RowPitch;
            }
            _viewport.SetLogicalSize(new Vector2(width,y));
        }
        private void DrawLinks()
        {
            _links.Begin();
            for(int i=0;i<Projection.Rows.Count;i++)
            {
                var row=Projection.Rows[i];if(row.ParentScopeId<0 || !_visible[row.ScopeId])continue;
                var parent=Projection.FindScope(row.ParentScopeId);
                for(int j=0;j<parent.Cards.Count;j++)if(parent.Cards[j].OutputId==row.OwnerOutputId)
                {
                    var a=new Vector2(parent.Cards[j].Position.x+WandProgramGridProjection.CardWidth*.5f,
                        _positions[parent.ScopeId].y+WandProgramGridProjection.CardHeight+2);
                    var b=_positions[row.ScopeId]+new Vector2(-6,27);
                    _links.Add(a,b,new Color(.63f,.55f,.35f));break;
                }
            }
            _links.End();
        }
    }
}
