using System.Collections.Generic;
using System;
using Game.Skills;
using UnityEngine;

namespace Game.UI
{
    /// <summary>独立工作台的只读交互入口；正式库存授权和Hub入口在后续阶段接入。</summary>
    public sealed class WandProgramWorkbenchPresenter : MonoBehaviour
    {
        [SerializeField] private SpellProgramSandboxDefinition[] _samples;
        public SpellProgram Program { get; private set; }
        public SpellPreviewRoot CurrentRoot { get; private set; }
        public WandDrawSnapshot SimulatedBefore { get; private set; }
        public int Cursor { get; private set; }
        public int SelectedCard { get; private set; } = -1;
        public int SelectedOutput { get; private set; } = -1;
        public IReadOnlyList<int> HighlightedOutputs => _highlighted;
        private readonly List<int> _highlighted = new List<int>();
        private readonly HashSet<int> _requests = new HashSet<int>();
        private readonly HashSet<int> _collapsedScopes = new HashSet<int>();
        private readonly SpellPreviewBuffer _preview = new SpellPreviewBuffer();
        private readonly SpellTracePileReplay _piles = new SpellTracePileReplay();
        private SpellExecutionInputs _inputs;
        private float _initialWait;
        private Font _ownedFont;
        private bool _initialized;
        private int _sampleIndex, _rootIndex;
        public event Action Changed;
        public string Status { get; private set; }
        public bool CycleMode { get; private set; }
        public bool IsOwned { get; private set; }
        public bool IsHistory { get; private set; }
        public IReadOnlyList<bool> RecordedReleased { get; private set; }
        public IReadOnlyList<bool> RecordedCancelledLayers { get; private set; }
        public string PreviewIdentity { get; private set; } = "规则推演";
        public int SelectedLibraryIndex { get; private set; } = -1;
        public SpellProgram LibraryProgram { get; private set; }
        public event Action<int> OwnedWandSelected;
        public event Action<string,int> InstallRequested;
        public event Action<string> RemoveRequested;
        public event Action ShowCurrentRequested, ShowReplayRequested, CloseRequested;
        public string DraggedInstanceId { get; private set; }
        public RectTransform DragGhost { get; set; }
        public bool PreviewTruncated => _preview.Truncated;
        public int RootCount => _preview.Roots.Count;
        public int RootIndex => _rootIndex;
        // 只供界面标示当前样例；不是Owned法杖ID，不能拿它授权库存操作。
        public int SampleIndex => _sampleIndex;
        public IReadOnlyList<int> Deck => _piles.Deck;
        public IReadOnlyList<int> Hand => _piles.Hand;
        public IReadOnlyList<int> Discard => _piles.Discard;
        public IReadOnlyList<SpellProgramSandboxDefinition> Samples => _samples;
        public SpellProgramSandboxDefinition Sample => _samples != null && _sampleIndex < _samples.Length ? _samples[_sampleIndex] : null;

        private void Start() { if (!_initialized) Initialize(_samples); }
        public void Initialize(SpellProgramSandboxDefinition[] samples)
        {
            if (_initialized) return;
            _initialized = true; _samples = samples == null ? Array.Empty<SpellProgramSandboxDefinition>() : (SpellProgramSandboxDefinition[])samples.Clone();
            // 一次创建系统字体，避免把临时Font写进Scene资产；正式美术字体可在后续外观迭代替换。
            _ownedFont = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei", "SimHei", "Arial" }, 24);
            WandProgramWorkbenchLayout.Build(this, _ownedFont);
            if (_samples.Length > 0) SelectSample(0);
            else { Status = "未配置Sandbox样例。请使用Norn菜单创建独立测试场景。"; Changed?.Invoke(); }
        }
        public void InitializeOwned()
        { if (_initialized) return; IsOwned = true; Initialize(new SpellProgramSandboxDefinition[3]); }
        public void BindOwned(SpellProgram program, WandDrawSnapshot before, in SpellExecutionInputs inputs,
            SpellProgram library, int slot, string identity = "当前推演", float initialWait = 0)
        {
            IsHistory = false;RecordedReleased=null;RecordedCancelledLayers=null; Program = program; SimulatedBefore = before; _inputs = inputs; LibraryProgram = library; _sampleIndex = slot;
            _initialWait=initialWait;
            PreviewIdentity = identity; _preview.Clear(); CurrentRoot = null; _highlighted.Clear();
            if (program == null || before == null) { Status = "该位置没有法杖。"; Changed?.Invoke(); return; }
            RebuildPreview();
        }
        public void BindRecorded(SpellPreviewRoot root, string identity,IReadOnlyList<bool> released=null,IReadOnlyList<bool> cancelledLayers=null)
        {
            IsHistory = true; CycleMode = false; Program = root.Graph.Program; SimulatedBefore = root.Before; _preview.Clear();
            RecordedReleased=released;RecordedCancelledLayers=cancelledLayers;
            CurrentRoot = root; PreviewIdentity = identity; SelectedCard = SelectedOutput = -1; _highlighted.Clear(); _collapsedScopes.Clear(); Seek(root.Trace.Steps.Count);
        }
        public void BeginOwnedDrag(string id) { if (IsOwned && !IsHistory) DraggedInstanceId = id; }
        public void DropOwned(int slot)
        { if (DraggedInstanceId != null && !IsHistory) InstallRequested?.Invoke(DraggedInstanceId, slot); EndOwnedDrag(); }
        public void RemoveOwned(string id) { if (IsOwned && !IsHistory) RemoveRequested?.Invoke(id); }
        public void EndOwnedDrag() { DraggedInstanceId = null; if (DragGhost != null) DragGhost.gameObject.SetActive(false); }
        public void ShowCurrent() => ShowCurrentRequested?.Invoke();
        public void ShowReplay() => ShowReplayRequested?.Invoke();
        public void CloseWorkbench() => CloseRequested?.Invoke();
        public void SetFeedback(string message) { PreviewIdentity=message;Changed?.Invoke(); }
        public void SelectLibraryCard(int index)
        {
            if (!IsOwned) { SelectCard(index); return; }
            SelectedLibraryIndex=index;SelectedCard=-1;_highlighted.Clear();
            if (LibraryProgram!=null && index>=0 && index<LibraryProgram.Count && Program!=null)
                for(int i=0;i<Program.Count;i++) if(Program.Instructions[i].InstanceId==LibraryProgram.Instructions[index].InstanceId)
                {SelectCard(i);break;}
            Changed?.Invoke();
        }
        private void OnDestroy()
        {
            Changed = null;
            if (_ownedFont != null)
            { if (Application.isPlaying) Destroy(_ownedFont); else DestroyImmediate(_ownedFont); }
        }
        public bool SelectSample(int index)
        {
            if (IsOwned) { OwnedWandSelected?.Invoke(index); return true; }
            if (_samples == null || index < 0 || index >= _samples.Length || _samples[index] == null) return false;
            _sampleIndex = index; var sample = _samples[index];
            // 编译失败也必须清掉旧结果；否则切换预览模式会把旧Program画在新样例身份下。
            CurrentRoot = null; Program = null; SimulatedBefore = null; _preview.Clear();
            SelectedCard = SelectedOutput = -1; _highlighted.Clear();
            if (!SpellProgramCompiler.TryCompile(sample.Spells, new SpellProgramCompileInputs(sample.BaseDraws,
                baseCastDelay:sample.CastDelay,baseReloadTime:sample.ReloadTime,shuffle:sample.Shuffle,
                manaRegenPerSecond:sample.ManaRegenPerSecond),out var program,out var diagnostic))
            { Status = diagnostic.Message; Changed?.Invoke(); return false; }
            Program = program; SimulatedBefore = WandDrawSnapshot.CreateInitial(program, sample.Seed);
            _inputs = new SpellExecutionInputs(sample.InitialMana, sample.MaxMana);
            if (sample.StartAfterRoots < 0 || sample.StartAfterRoots > SpellProgramLimits.MaxPreviewRoots)
            { Status = "样例起始Root数超出预算。"; Changed?.Invoke(); return false; }
            var machine = new SpellProgramMachine(); var work = new SpellBuildBuffer(false);
            for (int i = 0; i < sample.StartAfterRoots; i++)
            {
                if (!machine.TryBuild(program,SimulatedBefore,_inputs,work,out var summary))
                { Status=summary.Diagnostic.Message; Changed?.Invoke(); return false; }
                SimulatedBefore=work.NextState;
                float wait=summary.NeedsReload?Math.Max(summary.CastInterval,summary.ReloadInterval):summary.CastInterval;
                float mana=(float)Math.Min(sample.MaxMana,(double)summary.ManaAfter+wait*(double)sample.ManaRegenPerSecond);
                _inputs=new SpellExecutionInputs(mana,sample.MaxMana,work.RemainingUsesAfter);
            }
            return RebuildPreview();
        }
        public void SetCycleMode(bool cycle) { if (IsHistory) return; CycleMode=cycle; if(Program!=null) RebuildPreview(); }
        private bool RebuildPreview()
        {
            CurrentRoot=null;_rootIndex=0;_highlighted.Clear();
            bool ok=SpellProgramPreview.TryPreview(Program,SimulatedBefore,_inputs,
                new SpellPreviewOptions(CycleMode?32:1,initialWaitSeconds:_initialWait),_preview);
            Status=ok ? "只读规则推演 · 无真实发射/扣费" :
                _preview.Diagnostic.Error==SpellProgramError.TooComplex?"构筑过于复杂，请减少嵌套或重复。":
                _preview.Diagnostic.Error==SpellProgramError.EmptyProgram?"还没有装入可执行法术。":"无法推演，请检查法术配置。";
            if(_preview.Roots.Count>0) ActivateRoot(0); else Changed?.Invoke();
            return ok;
        }
        public void NextRoot()
        { if(_preview.Roots.Count>0) ActivateRoot((_rootIndex+1)%_preview.Roots.Count); }
        private void ActivateRoot(int index)
        {
            _rootIndex=index;CurrentRoot=_preview.Roots[index];SelectedCard=SelectedOutput=-1;_highlighted.Clear();_collapsedScopes.Clear();
            Seek(CurrentRoot.Trace.Steps.Count);
        }
        public void SelectCard(int index)
        {
            if(CurrentRoot==null || index<0 || index>=Program.Count)return;
            SelectedCard=index;SelectedOutput=-1;_highlighted.Clear();_requests.Clear();
            var kind=Program.Instructions[index].Kind;
            var steps=CurrentRoot.Trace.Steps;
            // 读取Machine记录的调用关系，不能按UI卡序猜“后续范围”。
            for(int i=0;i<steps.Count;i++)
            {
                var step=steps[i];
                if(step.Kind==SpellTraceKind.BorrowedActionInvoked && step.CauseCardIndex==index ||
                    step.Kind==SpellTraceKind.DrawRequested && (step.CardIndex==index || _requests.Contains(step.ParentRequestId)))
                    _requests.Add(step.RequestId);
            }
            var outputs=CurrentRoot.Graph.Outputs;
            for(int i=0;i<outputs.Count;i++)
            {
                var shot=outputs[i];bool related=shot.SourceCardIndex==index;
                if(kind==SpellKind.Modify)
                    for(int j=0;j<shot.Parameters.Contributions.Count;j++)
                        if(shot.Parameters.Contributions[j].CardIndex==index)related=true;
                for(int j=0;j<steps.Count && !related;j++)
                    if(steps[j].Kind==SpellTraceKind.OutputRegistered && steps[j].OutputId==i && _requests.Contains(steps[j].RequestId))related=true;
                if(kind==SpellKind.Copy)for(int j=0;j<shot.Parameters.Contributions.Count && !related;j++)
                {
                    int stepId=shot.Parameters.Contributions[j].StepId;
                    for(int k=0;k<steps.Count;k++)if(steps[k].StepId==stepId && _requests.Contains(steps[k].RequestId)){related=true;break;}
                }
                // Modifier的继续Draw与参数影响是两个概念；高亮严格表示最终参数来源。
                if(kind==SpellKind.Modify)
                {
                    related=false;
                    for(int j=0;j<shot.Parameters.Contributions.Count;j++)
                        if(shot.Parameters.Contributions[j].CardIndex==index)related=true;
                }
                if(related)_highlighted.Add(i);
            }
            Changed?.Invoke();
        }
        public void SelectOutput(int index)
        {
            if(CurrentRoot==null || index<0 || index>=CurrentRoot.Graph.Outputs.Count)return;
            SelectedOutput=index;SelectedCard=CurrentRoot.Graph.Outputs[index].SourceCardIndex;
            _highlighted.Clear();_highlighted.Add(index);Changed?.Invoke();
        }
        public void Seek(int eventCount)
        {
            if(CurrentRoot==null)return;
            Cursor=Math.Max(0,Math.Min(eventCount,CurrentRoot.Trace.Steps.Count));
            _piles.Seek(CurrentRoot.Before,CurrentRoot.Trace,Cursor-1);Changed?.Invoke();
        }
        public bool IsOutputSettled(int index)
        {
            if(CurrentRoot==null || index<0 || index>=CurrentRoot.Graph.Outputs.Count)return false;
            var shot=CurrentRoot.Graph.Outputs[index];
            return shot.RegisteredAtStep<Cursor && CurrentRoot.Graph.Scopes[shot.ScopeId].ClosedAtStep<Cursor;
        }
        public bool IsScopeCollapsed(int scope) { return _collapsedScopes.Contains(scope); }
        public void ToggleScope(int scope)
        {
            if(CurrentRoot==null || scope<0 || scope>=CurrentRoot.Graph.Scopes.Count)return;
            if(!_collapsedScopes.Add(scope))_collapsedScopes.Remove(scope);
            Changed?.Invoke();
        }
    }
}
