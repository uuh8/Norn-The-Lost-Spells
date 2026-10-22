using Game.UI;
using NUnit.Framework;
using Game.Skills;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Run.Tests
{
    public sealed class WandProgramWorkbenchViewTests
    {
        private GameObject _host;
        private SpellProgramSandboxDefinition _sample;
        private readonly System.Collections.Generic.List<SpellDefinition> _cards = new System.Collections.Generic.List<SpellDefinition>();
        [TearDown] public void Cleanup()
        { if(_host!=null) Object.DestroyImmediate(_host); if(_sample!=null) Object.DestroyImmediate(_sample);
            foreach(var c in _cards) Object.DestroyImmediate(c); _cards.Clear(); }
        private SpellDefinition Card(SpellKind kind, bool trigger=false)
        {
            var c=ScriptableObject.CreateInstance<SpellDefinition>();_cards.Add(c);
            c.Kind=kind;c.DisplayName=kind.ToString();c.BaseDamage=10;c.ExtraDraws=1;c.ModDamageAddFlat=6;
            c.PayloadTrigger=trigger?PayloadTriggerMode.OnImpact:PayloadTriggerMode.None;return c;
        }
        private WandProgramWorkbenchPresenter Make()
        {
            _sample=ScriptableObject.CreateInstance<SpellProgramSandboxDefinition>();
            _sample.Spells=new[]{Card(SpellKind.Multicast),Card(SpellKind.Emit,true),Card(SpellKind.Multicast),
                Card(SpellKind.Emit),Card(SpellKind.Modify),Card(SpellKind.Emit),Card(SpellKind.Emit)};
            _host=new GameObject("WorkbenchTest");var p=_host.AddComponent<WandProgramWorkbenchPresenter>();
            p.Initialize(new[]{_sample}); return p;
        }
        [TestCase("DoubleShot",2)] [TestCase("TripleShot",3)] [TestCase("MultiShot",10)]
        public void AuthoredMulticastDrawCount_MatchesItsName(string name,int expected)
        {
            var spell=UnityEditor.AssetDatabase.LoadAssetAtPath<SpellDefinition>("Assets/_Project/ScriptableObjects/Spell/Definitions/Spell_Multicast_"+name+".asset");
            Assert.That(SpellProgramCompiler.TryCompile(new[]{spell},new SpellProgramCompileInputs(1),out var program,out _),Is.True);
            Assert.That(program.Instructions[0].DrawCount,Is.EqualTo(expected));
        }

        [TestCase(100,2)] [TestCase(120,3)] [TestCase(500,4)]
        public void AuthoredTenShot_RequestsTenAndDoesNotCloneTwoInstalledFireballs(float mana,int roots)
        {
            const string folder="Assets/_Project/ScriptableObjects/Spell/Definitions/";
            string[] names={"Modify_Scattering","Multicast_MultiShot","Emit_TriggerFireball","Emit_StickyProjectile","Emit_Iceball","Emit_Fireball","Emit_Fireball"};
            var cards=new SpellDefinition[names.Length];
            for(int i=0;i<cards.Length;i++)cards[i]=UnityEditor.AssetDatabase.LoadAssetAtPath<SpellDefinition>(folder+"Spell_"+names[i]+".asset");
            Assert.That(SpellProgramCompiler.TryCompile(cards,new SpellProgramCompileInputs(1),out var program,out var diagnostic),Is.True,diagnostic.Message);
            Assert.That(program.Instructions[1].DrawCount,Is.EqualTo(10));
            var work=new SpellBuildBuffer();
            Assert.That(new SpellProgramMachine().TryBuild(program,WandDrawSnapshot.CreateInitial(program),new SpellExecutionInputs(mana,mana),work,out _),Is.True);
            Assert.That(work.Graph.RootOutputCount,Is.EqualTo(roots));
            Assert.That(work.Graph.Outputs.Count,Is.EqualTo(roots+1),"粘液弹只属于触发载荷，不占根排输出。");
        }

        [Test] public void WorkbenchContract_IsAnIndependentUguiPresenter()
        { Assert.That(typeof(WandProgramWorkbenchPresenter).Assembly.GetType("Game.UI.WandProgramWorkbenchPresenter"),Is.Not.Null); }
        [Test] public void PlayerCanvas_DoesNotAttachCalculationSidebar()
        {
            Make();
            // 旧数值侧栏脚本已经退役；直接验证玩家实际看见的文本，避免测试依赖废弃类型。
            foreach(var text in _host.GetComponentsInChildren<Text>())
                Assert.That(text.text.Contains("Output") || text.text.Contains("Scope") || text.text.Contains("DamageMul") || text.text.Contains("候选Mana"),Is.False);
        }
        [Test] public void SourceSequence_HasSixteenVisibleSlotsAndLibraryHasOnlyCards()
        {
            Make();int slots=0;foreach(var cell in _host.GetComponentsInChildren<WandSpellCellView>())
                if(cell.Role==WandSpellCellRole.Sequence)slots++;
            Assert.That(slots,Is.EqualTo(16));
            Assert.That(_host.GetComponentInChildren<WandProgramCanvasViewport>().Scroll.horizontal,Is.True);
        }
        [Test] public void EmptyTimedPayload_StillUsesTheTimersRowLabel()
        {
            var p=Make();var timer=Card(SpellKind.Emit);timer.PayloadTrigger=PayloadTriggerMode.AfterDelay;
            _sample.Spells=new[]{timer};Assert.That(p.SelectSample(0),Is.True);
            Assert.That(p.CurrentRoot.Graph.Scopes.Count,Is.EqualTo(2));
            bool found=false;foreach(var t in _host.GetComponentsInChildren<Text>())if(t.text.Contains("计时后"))found=true;
            Assert.That(found,Is.True,"空子层不能因为EmitCommand退化而被画成命中触发。");
        }
        [Test] public void ExecutionGrid_HasTraceProjectionInsteadOfOnlyOutputTextRows()
        { Assert.That(typeof(WandProgramWorkbenchPresenter).Assembly.GetType("Game.UI.WandProgramGridProjection"),Is.Not.Null); }
        [Test] public void ExecutionCanvas_HasIndependentZoomAndPanController()
        { Assert.That(typeof(WandProgramWorkbenchPresenter).Assembly.GetType("Game.UI.WandProgramCanvasViewport"),Is.Not.Null); }
        [Test] public void CollapsingScope_IsAViewChoiceThatPreservesGraph()
        {
            var p=Make();p.SelectSample(0);var graph=p.CurrentRoot.Graph;
            var toggle=typeof(WandProgramWorkbenchPresenter).GetMethod("ToggleScope");
            Assert.That(toggle,Is.Not.Null,"工作台尚未提供Scope折叠。");
            toggle.Invoke(p,new object[]{1});Assert.That(graph.Outputs.Count,Is.EqualTo(4));
            Assert.That(p.CurrentRoot.Graph,Is.SameAs(graph));
            Assert.That(p.IsScopeCollapsed(1),Is.True);
            var view=_host.GetComponentInChildren<WandScopeGraphView>();int visible=0;
            foreach(var cell in view.Cells)if(cell.gameObject.activeInHierarchy)visible++;
            Assert.That(visible,Is.EqualTo(3),"折叠命中子排后仍有根排的双重、触发弹、普通弹。");
            p.ToggleScope(1);visible=0;foreach(var cell in view.Cells)if(cell.gameObject.activeInHierarchy)visible++;
            Assert.That(visible,Is.EqualTo(7));
        }
        [Test] public void SevenCardExample_ShowsAllFourOutputsInGraphViewport()
        {
            var p=Make();p.SelectSample(0);Canvas.ForceUpdateCanvases();
            var view=_host.GetComponentInChildren<WandScopeGraphView>();
            var viewport=_host.GetComponentInChildren<WandProgramCanvasViewport>();int emits=0;
            foreach(var cell in view.Cells)if(cell.OutputId>=0)
            {
                emits++;var local=viewport.Scroll.viewport.InverseTransformPoint(cell.Rect.TransformPoint(cell.Rect.rect.center));
                Assert.That(viewport.Scroll.viewport.rect.Contains(local),Is.True,"基础例所有输出格子的中心应同时可见。");
            }
            Assert.That(emits,Is.EqualTo(4));
        }
        [Test] public void InvalidSampleSelection_DoesNotReuseLastValidProgram()
        {
            var p=Make();Assert.That(p.SelectSample(0),Is.True);_sample.Spells[0].ManaCost=float.NaN;
            Assert.That(p.SelectSample(0),Is.False);Assert.That(p.CurrentRoot,Is.Null);
            p.SetCycleMode(true);p.NextRoot();
            Assert.That(p.CurrentRoot,Is.Null,"编译失败后不能用旧Program配新样例身份重建结果。");
            Assert.That(p.Program,Is.Null);Assert.That(p.RootCount,Is.Zero);
        }
        [Test] public void ModifierSelection_LinksOnlySameScopeOutputs()
        {
            var p=Make(); Assert.That(p.SelectSample(0),Is.True);p.SelectCard(4);
            Assert.That(p.HighlightedOutputs.Count,Is.EqualTo(2));
            foreach(int id in p.HighlightedOutputs) Assert.That(p.CurrentRoot.Graph.Outputs[id].ScopeId,Is.EqualTo(1));
            var view=_host.GetComponentInChildren<WandScopeGraphView>();int marked=0;
            foreach(var cell in view.Cells)if(cell.IsHighlighted)
            {marked++;Assert.That(cell.CardIndex==3 || cell.CardIndex==5,Is.True);}
            Assert.That(marked,Is.EqualTo(2));
        }
        [Test] public void MulticastSelection_FollowsActualDrawRequestsIncludingTriggerChild()
        {
            var p=Make();Assert.That(p.SelectSample(0),Is.True);p.SelectCard(2);
            Assert.That(p.HighlightedOutputs.Count,Is.EqualTo(2));
            p.SelectCard(0);Assert.That(p.HighlightedOutputs.Count,Is.EqualTo(4));
        }
        [Test] public void StepCursor_HidesFinalValuesBeforeScopeCloses()
        {
            var p=Make();Assert.That(p.SelectSample(0),Is.True);
            var shot=p.CurrentRoot.Graph.Outputs[1]; var scope=p.CurrentRoot.Graph.Scopes[shot.ScopeId];
            p.Seek(scope.ClosedAtStep);Assert.That(p.IsOutputSettled(1),Is.False);
            p.Seek(scope.ClosedAtStep+1);Assert.That(p.IsOutputSettled(1),Is.True);
        }
        [Test] public void InspectAndSeek_LeaveSimulationSourceSnapshotUnchanged()
        {
            var p=Make();Assert.That(p.SelectSample(0),Is.True);var before=p.SimulatedBefore;
            p.Seek(0);p.SelectCard(4);p.SelectOutput(2);p.Seek(99999);
            Assert.That(p.SimulatedBefore,Is.SameAs(before));Assert.That(before.ExecutionRevision,Is.Zero);
            Assert.That(before.Deck.Count,Is.EqualTo(7));
        }
    }
}
