using System.Collections.Generic;
using System;
using Game.Skills;
using UnityEngine;

namespace Game.UI
{
    public readonly struct WandProgramGridCard
    {
        public readonly int CardIndex, ScopeId, OutputId, ReadAtStep;
        public readonly bool Skipped;
        public readonly SpellTraceKind Reason;
        public readonly Vector2 Position;
        public readonly int BorrowedFrom;
        public readonly string BorrowedLabel;
        internal WandProgramGridCard(int card,int scope,int output,int step,bool skipped,SpellTraceKind reason,Vector2 position,int borrowedFrom=-1,string borrowedLabel=null)
        { CardIndex=card;ScopeId=scope;OutputId=output;ReadAtStep=step;Skipped=skipped;Reason=reason;Position=position;BorrowedFrom=borrowedFrom;BorrowedLabel=borrowedLabel; }
    }
    public sealed class WandProgramGridRow
    {
        public int ScopeId { get; internal set; }
        public int ParentScopeId { get; internal set; }
        public int OwnerOutputId { get; internal set; }
        public Vector2 Position { get; internal set; }
        public IReadOnlyList<WandProgramGridCard> Cards { get; internal set; }
    }
    /// <summary>从冻结Trace投影行与格子；不重新解释法术，不计算伤害。</summary>
    public sealed class WandProgramGridProjection
    {
        public const float CardWidth=96,CardHeight=104,CardPitch=110,RowPitch=148,LabelWidth=154,Padding=24;
        public IReadOnlyList<WandProgramGridRow> Rows { get; private set; }
        public Vector2 ContentSize { get; private set; }
        private WandProgramGridRow[] _byScope;
        public WandProgramGridProjection(SpellPreviewRoot root)
        {
            if(root==null)throw new ArgumentNullException(nameof(root));
            var graph=root.Graph;int count=graph.Scopes.Count;
            _byScope=new WandProgramGridRow[count];var reads=new List<SpellExecutionTraceStep>[count];
            for(int i=0;i<count;i++)reads[i]=new List<SpellExecutionTraceStep>();
            // CardDrawn只是尝试读取；ManaPaid才表示执行获准。Skip也占可观察位置，但不能假装产出。
            foreach(var step in root.Trace.Steps)
                if(step.ScopeId>=0 && step.ScopeId<count && (step.Kind==SpellTraceKind.ManaPaid || step.Kind==SpellTraceKind.BorrowedActionInvoked || step.Kind==SpellTraceKind.IntrinsicActionInvoked ||
                    step.Kind==SpellTraceKind.ManaSkipped || step.Kind==SpellTraceKind.UsesSkipped || step.Kind==SpellTraceKind.EligibilitySkipped))
                    reads[step.ScopeId].Add(step);
            var ordered=new List<WandProgramGridRow>(count);Vector2 extent=Vector2.zero;
            AddRow(root,0,Padding,reads,ordered,ref extent);
            Rows=ordered.AsReadOnly();ContentSize=extent;
        }
        public WandProgramGridRow FindScope(int scope)
        { return scope>=0 && scope<_byScope.Length?_byScope[scope]:null; }
        private void AddRow(SpellPreviewRoot root,int scopeId,float x,List<SpellExecutionTraceStep>[] reads,
            List<WandProgramGridRow> ordered,ref Vector2 extent)
        {
            var scope=root.Graph.Scopes[scopeId];var position=new Vector2(x,Padding+ordered.Count*RowPitch);
            var cards=new WandProgramGridCard[reads[scopeId].Count];
            for(int i=0;i<cards.Length;i++)
            {
                var step=reads[scopeId][i];bool skipped=step.Kind!=SpellTraceKind.ManaPaid && step.Kind!=SpellTraceKind.BorrowedActionInvoked && step.Kind!=SpellTraceKind.IntrinsicActionInvoked;int output=-1;
                if(!skipped)for(int j=0;j<scope.OutputIds.Count;j++)
                {
                    int id=scope.OutputIds[j];var shot=root.Graph.Outputs[id];
                    int next=i+1<cards.Length?reads[scopeId][i+1].StepId:int.MaxValue;
                    if(shot.SourceCardIndex==step.CardIndex && shot.RegisteredAtStep>=step.StepId && shot.RegisteredAtStep<next){output=id;break;}
                }
                cards[i]=new WandProgramGridCard(step.CardIndex,scopeId,output,step.StepId,skipped,step.Kind,
                    position+new Vector2(LabelWidth+i*CardPitch,0),step.CauseCardIndex,
                    step.InvocationMode==SpellInvocationMode.BorrowedCopy?"借 "+(step.From==SpellCardCollection.Deck?"待抽":step.From==SpellCardCollection.Hand?"本发":"回收")+(step.TargetPosition+1):null);
            }
            var row=new WandProgramGridRow{ScopeId=scopeId,ParentScopeId=scope.ParentScopeId,
                OwnerOutputId=scope.OwnerOutputId,Position=position,Cards=Array.AsReadOnly(cards)};
            _byScope[scopeId]=row;ordered.Add(row);
            extent.x=Mathf.Max(extent.x,x+LabelWidth+Mathf.Max(1,cards.Length)*CardPitch+Padding);
            extent.y=position.y+CardHeight+Padding;
            // 空Payload在Baker中可以退化，但它的Scope记录仍应展示，解释“为何没有子弹”。
            for(int child=1;child<root.Graph.Scopes.Count;child++)
            {
                var nested=root.Graph.Scopes[child];if(nested.ParentScopeId!=scopeId)continue;
                int ownerColumn=0;for(int i=0;i<cards.Length;i++)if(cards[i].OutputId==nested.OwnerOutputId){ownerColumn=i;break;}
                float childX=x+LabelWidth+ownerColumn*CardPitch+CardWidth*.5f+18;
                AddRow(root,child,childX,reads,ordered,ref extent);
            }
        }
    }
}
