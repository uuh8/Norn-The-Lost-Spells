using System.Collections.Generic;
using Game.Skills;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>源序列/法术库复用格子。只有程序变化才重新绑定；播放热路径只切换已缓存的显示状态。</summary>
    public sealed class WandProgramSequenceView : MonoBehaviour
    {
        private WandProgramWorkbenchPresenter _presenter;
        private Font _font;
        private RectTransform _sourceContent,_libraryContent;
        private ScrollRect _libraryScroll;
        private Text _playText,_identity;
        private SpellProgram _program;
        private SpellProgram _libraryProgram;
        private SpellPreviewRoot _root;
        private readonly List<WandSpellCellView> _source=new List<WandSpellCellView>(),_library=new List<WandSpellCellView>();
        private readonly List<int> _libraryIndices=new List<int>();
        private float _timer;
        public bool IsPlaying { get; private set; }
        public void Initialize(WandProgramWorkbenchPresenter p,Font font,RectTransform source,ScrollRect library,
            Text play,Text identity)
        {
            _presenter=p;_font=font;_sourceContent=source;_libraryContent=library.content;_libraryScroll=library;
            _playText=play;_identity=identity;p.Changed+=Refresh;
        }
        private void OnDestroy(){if(_presenter!=null)_presenter.Changed-=Refresh;}
        private void OnDisable(){IsPlaying=false;if(_playText!=null)_playText.text="播放";}
        public void TogglePlayback()
        {
            if(_presenter.CurrentRoot==null)return;
            if(!IsPlaying && _presenter.Cursor>=_presenter.CurrentRoot.Trace.Steps.Count)_presenter.Seek(0);
            IsPlaying=!IsPlaying;_playText.text=IsPlaying?"暂停":"播放";_timer=.18f;
        }
        // Update只推进计时器和已有事件游标，不创建协程、字符串或重新编译Program。
        private void Update()
        {
            if(!IsPlaying)return;
            _timer-=Time.unscaledDeltaTime;if(_timer>0)return;_timer=.18f;
            NextVisibleStep();
            if(_presenter.CurrentRoot==null || _presenter.Cursor>=_presenter.CurrentRoot.Trace.Steps.Count)
            {IsPlaying=false;_playText.text="播放";}
        }
        public void NextVisibleStep()
        {
            var root=_presenter.CurrentRoot;if(root==null)return;
            for(int i=_presenter.Cursor;i<root.Trace.Steps.Count;i++)
                if(VisibleEvent(root.Trace.Steps[i].Kind)){_presenter.Seek(i+1);return;}
            _presenter.Seek(root.Trace.Steps.Count);
        }
        public void PreviousVisibleStep()
        {
            var root=_presenter.CurrentRoot;if(root==null)return;
            for(int i=_presenter.Cursor-2;i>=0;i--)
                if(VisibleEvent(root.Trace.Steps[i].Kind)){_presenter.Seek(i+1);return;}
            _presenter.Seek(0);
        }
        private static bool VisibleEvent(SpellTraceKind kind)
        {return kind==SpellTraceKind.ManaPaid || kind==SpellTraceKind.ManaSkipped || kind==SpellTraceKind.UsesSkipped ||
            kind==SpellTraceKind.EligibilitySkipped || kind==SpellTraceKind.ScopeClosed || kind==SpellTraceKind.BorrowedActionInvoked || kind==SpellTraceKind.IntrinsicActionInvoked || kind==SpellTraceKind.CopyRecursionRejected;}
        private void Refresh()
        {
            var p=_presenter;
            if(!ReferenceEquals(_program,p.Program) || !ReferenceEquals(_libraryProgram,p.LibraryProgram??p.Program))
            {_program=p.Program;_libraryProgram=p.LibraryProgram??p.Program;BindProgram();}
            if(!ReferenceEquals(_root,p.CurrentRoot))
            {
                _root=p.CurrentRoot;IsPlaying=false;_playText.text="播放";
            }
            for(int i=0;i<_source.Count;i++)if(_source[i].CardIndex>=0)
                _source[i].SetState(p.SelectedCard==_source[i].CardIndex,false,true,false,false);
            // 没有Graph时保留简短失败原因；不能再用“出发推演”把技术预算拒绝覆盖成空白。
            string identity=p.CurrentRoot==null && !string.IsNullOrEmpty(p.Status)?p.Status:p.PreviewIdentity;
            if (_identity.text != identity) _identity.text=identity;
            for(int i=0;i<_libraryIndices.Count;i++)
                _library[i].SetState((p.IsOwned?p.SelectedLibraryIndex:p.SelectedCard)==_library[i].CardIndex,false,true,false,false);
        }
        private void BindProgram()
        {
            int slots=_presenter.IsOwned?(_program==null?0:_program.SourceSlotCount):_program==null?16:Mathf.Max(16,_program.SourceSlotCount);
            int fixedCount=_program?.IntrinsicCount??0;
            for(int slot=0;slot<slots+fixedCount;slot++)
            {
                if(slot==_source.Count)_source.Add(WandSpellCellView.Create(_sourceContent,_font,_presenter.SelectCard));
                var cell=_source[slot];cell.name="Card"+slot;cell.gameObject.SetActive(true);
                int card=-1;
                if(slot<fixedCount)card=_program.Count-fixedCount+slot;
                else if(_program!=null)for(int i=0;i<_program.Count;i++)if(_program.Instructions[i].SourceSlot==slot-fixedCount){card=i;break;}
                if(card<0)cell.BindEmpty(slot-fixedCount);else cell.Bind(_program.Instructions[card],card,WandSpellCellRole.Sequence);
                WandWorkbenchWidgets.Place(cell.Rect,12+slot*88,8,96,104);cell.Rect.localScale=Vector3.one*.8f;
            }
            for(int i=slots+fixedCount;i<_source.Count;i++)_source[i].gameObject.SetActive(false);
            _sourceContent.sizeDelta=new Vector2((slots+fixedCount)*88+24,104);
            _libraryIndices.Clear();
            if(_libraryProgram!=null)for(int i=0;i<_libraryProgram.Count;i++)
            {
                var card=_libraryProgram.Instructions[i];bool duplicate=false;
                for(int j=0;j<_libraryIndices.Count && !_presenter.IsOwned;j++)
                {
                    var prior=_libraryProgram.Instructions[_libraryIndices[j]];
                    if(prior.DisplayName==card.DisplayName && prior.Kind==card.Kind &&
                        prior.Emission.PayloadTrigger==card.Emission.PayloadTrigger){duplicate=true;break;}
                }
                if(duplicate)continue;int index=_libraryIndices.Count;
                if(index==_library.Count)_library.Add(WandSpellCellView.Create(_libraryContent,_font,_presenter.SelectLibraryCard));
                var cell=_library[index];cell.gameObject.SetActive(true);cell.Bind(card,i,WandSpellCellRole.Library);_libraryIndices.Add(i);
            }
            for(int i=_libraryIndices.Count;i<_library.Count;i++)_library[i].gameObject.SetActive(false);
            LayoutLibrary();
        }
        private void OnRectTransformDimensionsChange(){if(_libraryScroll!=null)LayoutLibrary();}
        private void LayoutLibrary()
        {
            int columns=Mathf.Clamp(Mathf.FloorToInt((_libraryScroll.viewport.rect.width-16)/96),1,3);
            for(int i=0;i<_libraryIndices.Count;i++)
            {
                WandWorkbenchWidgets.Place(_library[i].Rect,8+i%columns*96,8+i/columns*108,96,104);
                _library[i].Rect.localScale=Vector3.one*.88f;
            }
            _libraryContent.sizeDelta=new Vector2(Mathf.Max(1,_libraryScroll.viewport.rect.width),
                16+Mathf.CeilToInt(_libraryIndices.Count/(float)columns)*108);
        }
    }
}
