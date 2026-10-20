using System.Collections;
using System.Collections.Generic;
using System.IO;
using Game.Skills;
using Game.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.TestTools;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using Unity.Profiling;

namespace Game.Run.Tests
{
    public sealed class SpellProgrammingV2WorkbenchPlayModeTests
    {
        private GameObject _host,_input;
        private Scene _loadedScene;
        private System.Action _restoreView;
        private readonly List<Object> _assets=new List<Object>();
        [UnityTearDown] public IEnumerator Cleanup()
        {
            _restoreView?.Invoke();_restoreView=null;
            if(_host!=null)Object.Destroy(_host);if(_input!=null)Object.Destroy(_input);
            foreach(var asset in _assets)Object.Destroy(asset);_assets.Clear();yield return null;
            if(_loadedScene.IsValid() && _loadedScene.isLoaded)yield return SceneManager.UnloadSceneAsync(_loadedScene);
        }
        private T Asset<T>() where T:ScriptableObject
        {var asset=ScriptableObject.CreateInstance<T>();_assets.Add(asset);return asset;}
        private SpellDefinition Card(SpellKind kind,string name,float mana=5)
        {
            var c=Asset<SpellDefinition>();c.Kind=kind;c.DisplayName=name;c.ManaCost=mana;c.BaseDamage=10;c.BaseSpeed=20;
            c.ExtraDraws=1;c.ModDamageAddFlat=6;return c;
        }
        [UnityTest] public IEnumerator SavedSandbox_ThreeConfiguredSamplesMatchTheRuleGraph()
        {
#if UNITY_EDITOR
            const string path="Assets/_Project/Scenes/Tests/Spells/SpellWorkbench.unity";
            var scene=SceneManager.GetSceneByPath(path);
            if(!scene.isLoaded)
            {
                yield return UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(path,new LoadSceneParameters(LoadSceneMode.Additive));
                scene=SceneManager.GetSceneByPath(path);_loadedScene=scene;
            }
            yield return null;yield return null;
            WandProgramWorkbenchPresenter p=null;
            foreach(var root in scene.GetRootGameObjects())
            {var candidate=root.GetComponent<WandProgramWorkbenchPresenter>();if(candidate!=null)p=candidate;}
            Assert.That(p,Is.Not.Null);Assert.That(p.Samples.Count,Is.EqualTo(3));
            Assert.That(p.SelectSample(0),Is.True);p.SelectCard(4);yield return null;yield return Capture("grid-seven-icons-native.png");
            Assert.That(p.SelectSample(1),Is.True,p.Status);Assert.That(p.CurrentRoot.ManaBefore,Is.EqualTo(80));
            Assert.That(p.CurrentRoot.Summary.ManaAfter,Is.EqualTo(70));
            Assert.That(p.CurrentRoot.Graph.Outputs[0].Command.Damage,Is.EqualTo(16));
            Assert.That(p.CurrentRoot.Before.Deck,Is.EqualTo(new[]{1}));Assert.That(p.CurrentRoot.Before.Discard,Is.EqualTo(new[]{0}));
            yield return null;yield return Capture("workbench-wrap-native.png");
            Assert.That(p.SelectSample(2),Is.True,p.Status);Assert.That(p.CurrentRoot.Graph.RootOutputCount,Is.EqualTo(2));
            Assert.That(p.CurrentRoot.Graph.Scopes[1].OutputIds.Count,Is.EqualTo(2));
            Assert.That(p.CurrentRoot.Summary.CastInterval,Is.EqualTo(.05f).Within(.0001));
            Assert.That(p.CurrentRoot.Summary.ReloadInterval,Is.EqualTo(.4f).Within(.0001));
            foreach(var output in p.CurrentRoot.Graph.Outputs)Assert.That(output.Command.Damage,Is.EqualTo(output.ScopeId==0?20:10));
            p.SelectCard(6);yield return null;yield return Capture("workbench-eight-native.png");
#else
            Assert.Ignore("此项验证Editor保存的独立Sandbox资产，不作为Player Build测试。");yield break;
#endif
        }
        private static IEnumerator Capture(string file)
        {
            yield return new WaitForEndOfFrame();var texture=ScreenCapture.CaptureScreenshotAsTexture();
            Directory.CreateDirectory("Temp/Spells");
            File.WriteAllBytes("Temp/Spells/"+file,texture.EncodeToPNG());Object.Destroy(texture);
        }
        [UnityTest] public IEnumerator NativeUgui_ClickSourceAndStepWithoutCommittingGameplay()
        {
            var m=Card(SpellKind.Multicast,"双重",0);var t=Card(SpellKind.Emit,"触发弹",10);t.PayloadTrigger=PayloadTriggerMode.OnImpact;
            var f=Card(SpellKind.Emit,"普通弹");var d=Card(SpellKind.Modify,"增伤");
            var sample=Asset<SpellProgramSandboxDefinition>();sample.Title="七卡 · Scope隔离";
            sample.Spells=new[]{m,t,m,f,d,f,f};sample.InitialMana=80;
            if(EventSystem.current==null)
            {_input=new GameObject("V2TestEventSystem",typeof(EventSystem),typeof(InputSystemUIInputModule));_input.GetComponent<InputSystemUIInputModule>().AssignDefaultActions();}
            _host=new GameObject("V2WorkbenchPlayModeTest");var p=_host.AddComponent<WandProgramWorkbenchPresenter>();p.Initialize(new[]{sample});
            yield return null;yield return null;Canvas.ForceUpdateCanvases();
            Assert.That(p.CurrentRoot.Graph.Outputs.Count,Is.EqualTo(4));Assert.That(p.CurrentRoot.Summary.ManaAfter,Is.EqualTo(50));
            var buttons=_host.GetComponentsInChildren<Button>();Button damage=null,play=null;
            foreach(var button in buttons)
            {
                if(button.name=="Card4")damage=button;if(button.name=="Playback")play=button;
                Assert.That(new[]{"PreviousStep","NextStep","Restart","Mode","NextRoot","CurrentPreview","RecentReplay"},Does.Not.Contain(button.name));
            }
            Assert.That(damage,Is.Not.Null);Assert.That(play,Is.Not.Null);
            ExecuteEvents.Execute(damage.gameObject,new PointerEventData(EventSystem.current){button=PointerEventData.InputButton.Left},ExecuteEvents.pointerClickHandler);
            Assert.That(p.SelectedCard,Is.EqualTo(4));Assert.That(p.HighlightedOutputs.Count,Is.EqualTo(2));
            ExecuteEvents.Execute(play.gameObject,new PointerEventData(EventSystem.current){button=PointerEventData.InputButton.Left},ExecuteEvents.pointerClickHandler);
            var sequence=_host.GetComponentInChildren<WandProgramSequenceView>();
            Assert.That(sequence.IsPlaying,Is.True);
            yield return new WaitForSecondsRealtime(.25f);Assert.That(p.Cursor,Is.GreaterThan(0));
            ExecuteEvents.Execute(play.gameObject,new PointerEventData(EventSystem.current){button=PointerEventData.InputButton.Left},ExecuteEvents.pointerClickHandler);
            Assert.That(sequence.IsPlaying,Is.False);
            p.Seek(p.CurrentRoot.Trace.Steps.Count);yield return null;
            var grid=_host.GetComponentInChildren<WandScopeGraphView>();int marked=0;
            foreach(var cell in grid.Cells)if(cell.IsHighlighted)
            {marked++;Assert.That(cell.CardIndex==3 || cell.CardIndex==5,Is.True);}
            Assert.That(marked,Is.EqualTo(2));
            foreach(var text in _host.GetComponentsInChildren<Text>())
                Assert.That(text.text.Contains("Output") || text.text.Contains("DamageMul") || text.text.Contains("候选Mana"),Is.False);
            Assert.That(p.SimulatedBefore.ExecutionRevision,Is.Zero);
            Directory.CreateDirectory("Temp/Spells");
            yield return new WaitForEndOfFrame();
            // 真实Game View像素证据，不能用HTML草图或直接生成图片替代。
            var screenshot=ScreenCapture.CaptureScreenshotAsTexture();
            File.WriteAllBytes("Temp/Spells/workbench-native.png",screenshot.EncodeToPNG());Object.Destroy(screenshot);
            File.WriteAllText("Temp/Spells/workbench-capture.txt",$"{Screen.width}x{Screen.height}; root outputs=4; mana=50; selected modifier=5; no gameplay commit");
        }
        private WandProgramWorkbenchPresenter Build(SpellProgramSandboxDefinition sample)
        {
            if(EventSystem.current==null)
            {_input=new GameObject("V2TestEventSystem",typeof(EventSystem),typeof(InputSystemUIInputModule));_input.GetComponent<InputSystemUIInputModule>().AssignDefaultActions();}
            _host=new GameObject("GridWorkbenchTest");var p=_host.AddComponent<WandProgramWorkbenchPresenter>();p.Initialize(new[]{sample});return p;
        }
        private static void ClickVisibleCell(WandSpellCellView cell)
        {
            var pointer=new PointerEventData(EventSystem.current){button=PointerEventData.InputButton.Left,
                position=RectTransformUtility.WorldToScreenPoint(null,cell.Rect.TransformPoint(cell.Rect.rect.center))};
            var hits=new List<RaycastResult>();EventSystem.current.RaycastAll(pointer,hits);
            Assert.That(hits.Count,Is.GreaterThan(0));
            Assert.That(hits[0].gameObject.GetComponentInParent<WandSpellCellView>(),Is.SameAs(cell),"缩放/滚动后的实际Raycast必须命中正确格子。");
            ExecuteEvents.Execute(cell.Button.gameObject,pointer,ExecuteEvents.pointerClickHandler);
        }
        [UnityTest] public IEnumerator FourByThreeGameView_KeepsTheGridAndControlsUsable()
        {
#if UNITY_EDITOR
            SetTemporaryGameViewSize(1280,960);
            for(int i=0;i<5;i++)yield return null;
            Assert.That(Screen.width,Is.EqualTo(1280));Assert.That(Screen.height,Is.EqualTo(960));
            var sample=UnityEditor.AssetDatabase.LoadAssetAtPath<SpellProgramSandboxDefinition>(
                "Assets/_Project/Tests/Fixtures/Spells/Workbench/SevenCard.asset");
            var p=Build(sample);yield return null;yield return null;Canvas.ForceUpdateCanvases();p.SelectCard(4);
            var view=_host.GetComponentInChildren<WandProgramCanvasViewport>();var grid=_host.GetComponentInChildren<WandScopeGraphView>();
            int visible=0;foreach(var cell in grid.Cells)
            {
                var point=view.Scroll.viewport.InverseTransformPoint(cell.Rect.TransformPoint(cell.Rect.rect.center));
                if(view.Scroll.viewport.rect.Contains(point))visible++;
            }
            Assert.That(visible,Is.EqualTo(7));
            WandSpellCellView target=null;foreach(var cell in grid.Cells)if(cell.CardIndex==5)target=cell;
            Assert.That(target,Is.Not.Null);ClickVisibleCell(target);Assert.That(p.SelectedCard,Is.EqualTo(5));
            yield return Capture("grid-1280x960-native.png");
#else
            Assert.Ignore("Editor GameView分辨率验证，不作为Player Build测试。");yield break;
#endif
        }
#if UNITY_EDITOR
        private void SetTemporaryGameViewSize(int width,int height)
        {
            // 仅测试工具使用Editor内部API，创建自己的临时尺寸并在TearDown还原，产品代码不依赖反射。
            var assembly=typeof(UnityEditor.Editor).Assembly;
            var viewType=assembly.GetType("UnityEditor.GameView");
            var sizesType=assembly.GetType("UnityEditor.GameViewSizes");
            var singleton=typeof(UnityEditor.ScriptableSingleton<>).MakeGenericType(sizesType);
            var sizes=singleton.GetProperty("instance").GetValue(null);
            var groupType=assembly.GetType("UnityEditor.GameViewSizeGroupType");
            var group=sizesType.GetMethod("GetGroup").Invoke(sizes,new[]{System.Enum.Parse(groupType,"Standalone")});
            var groupClass=group.GetType();int built=(int)groupClass.GetMethod("GetBuiltinCount").Invoke(group,null);
            var sizeType=assembly.GetType("UnityEditor.GameViewSize");var kind=assembly.GetType("UnityEditor.GameViewSizeType");
            var window=UnityEditor.EditorWindow.GetWindow(viewType);
            var selected=viewType.GetProperty("selectedSizeIndex",System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
            var get=groupClass.GetMethod("GetGameViewSize");
            var originalSize=get.Invoke(group,new object[]{(int)selected.GetValue(window)});
            var label=sizeType.GetProperty("baseText",System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
            int stale=(int)groupClass.GetMethod("GetCustomCount").Invoke(group,null);
            for(int i=stale-1;i>=0;i--)
            {
                var candidate=get.Invoke(group,new object[]{built+i});
                if((string)label.GetValue(candidate)=="Norn Grid Responsive Test")
                    groupClass.GetMethod("RemoveCustomSize").Invoke(group,new object[]{built+i});
            }
            int custom=(int)groupClass.GetMethod("GetCustomCount").Invoke(group,null);
            int original=0;for(int i=0;i<built+custom;i++)if(ReferenceEquals(get.Invoke(group,new object[]{i}),originalSize)){original=i;break;}
            selected.SetValue(window,original);
            var size=System.Activator.CreateInstance(sizeType,System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic|
                System.Reflection.BindingFlags.Instance,null,new object[]{System.Enum.Parse(kind,"FixedResolution"),width,height,"Norn Grid Responsive Test"},null);
            groupClass.GetMethod("AddCustomSize").Invoke(group,new[]{size});
            _restoreView=()=>
            {
                if(window!=null)selected.SetValue(window,original);
                // RemoveCustomSize虽然名字带Custom，参数仍是包含Builtin的绝对索引。
                int current=(int)groupClass.GetMethod("GetCustomCount").Invoke(group,null);
                for(int i=current-1;i>=0;i--)if(ReferenceEquals(get.Invoke(group,new object[]{built+i}),size))
                    groupClass.GetMethod("RemoveCustomSize").Invoke(group,new object[]{built+i});
                if(window!=null)window.Repaint();
                int remaining=0;current=(int)groupClass.GetMethod("GetCustomCount").Invoke(group,null);
                for(int i=0;i<current;i++)if((string)label.GetValue(get.Invoke(group,new object[]{built+i}))=="Norn Grid Responsive Test")remaining++;
                File.WriteAllText("Temp/Spells/game-view-restore.json",$"{{\"TemporaryEntriesRemaining\":{remaining},\"SelectedSizeRestored\":{((int)selected.GetValue(window)==original).ToString().ToLowerInvariant()}}}");
                Assert.That(remaining,Is.Zero,"测试必须清理自己的临时分辨率。");
            };
            selected.SetValue(window,built+custom);window.Repaint();
        }
#endif
        [UnityTest] public IEnumerator LargeGraph_CanZoomPanFitAndSelectTheLastCard()
        {
            var sample=Asset<SpellProgramSandboxDefinition>();sample.Title="复杂构筑";
            var trigger=Card(SpellKind.Emit,"触发弹",0);trigger.PayloadTrigger=PayloadTriggerMode.OnImpact;
            var multi=Card(SpellKind.Multicast,"多重",0);multi.ExtraDraws=111;
            var bolt=Card(SpellKind.Emit,"普通弹",0);sample.Spells=new SpellDefinition[129];
            for(int i=0;i<16;i++)sample.Spells[i]=trigger;sample.Spells[16]=multi;
            for(int i=17;i<129;i++)sample.Spells[i]=bolt;
            var p=Build(sample);yield return null;yield return null;Canvas.ForceUpdateCanvases();
            var grid=_host.GetComponentInChildren<WandScopeGraphView>();var view=_host.GetComponentInChildren<WandProgramCanvasViewport>();
            Assert.That(grid.Cells.Count,Is.EqualTo(129));Assert.That(grid.Projection.Rows.Count,Is.EqualTo(17));
            Assert.That(view.LogicalSize.x,Is.GreaterThan(12000));Assert.That(view.LogicalSize.y,Is.GreaterThan(2000));
            Button fit=null;Slider zoom=null;foreach(var b in _host.GetComponentsInChildren<Button>())if(b.name=="FitAll")fit=b;
            foreach(var s in _host.GetComponentsInChildren<Slider>())if(s.name=="ZoomSlider")zoom=s;
            Assert.That(fit,Is.Not.Null);Assert.That(zoom,Is.Not.Null);fit.onClick.Invoke();yield return null;
            Assert.That(view.LogicalSize.x*view.Zoom,Is.LessThanOrEqualTo(view.Scroll.viewport.rect.width+.1f));
            Assert.That(view.LogicalSize.y*view.Zoom,Is.LessThanOrEqualTo(view.Scroll.viewport.rect.height+.1f));
            yield return Capture("grid-complex-fit-native.png");
            view.ResetView();zoom.value=.8f;Assert.That(view.Zoom,Is.EqualTo(.8f).Within(.0001));
            var origin=view.Scroll.content.anchoredPosition;
            var drag=new PointerEventData(EventSystem.current){button=PointerEventData.InputButton.Left,
                position=RectTransformUtility.WorldToScreenPoint(null,view.Scroll.viewport.TransformPoint(view.Scroll.viewport.rect.center))};
            view.Scroll.OnBeginDrag(drag);drag.position+=new Vector2(-180,140);drag.delta=new Vector2(-180,140);
            view.Scroll.OnDrag(drag);view.Scroll.OnEndDrag(drag);
            Assert.That(view.Scroll.content.anchoredPosition.x,Is.LessThan(origin.x));
            Assert.That(view.Scroll.content.anchoredPosition.y,Is.GreaterThan(origin.y));
            view.Scroll.horizontalNormalizedPosition=1;view.Scroll.verticalNormalizedPosition=0;yield return null;
            var last=grid.Cells[grid.Cells.Count-1];Assert.That(last.CardIndex,Is.EqualTo(128));ClickVisibleCell(last);
            Assert.That(p.SelectedCard,Is.EqualTo(128));Assert.That(p.SimulatedBefore.ExecutionRevision,Is.Zero);
            yield return Capture("grid-complex-last-card-native.png");
        }
        [UnityTest] public IEnumerator WarmStepAndZoom_ReuseViewsWithoutManagedAllocations()
        {
            var m=Card(SpellKind.Multicast,"双重",0);var t=Card(SpellKind.Emit,"触发弹",0);t.PayloadTrigger=PayloadTriggerMode.OnImpact;
            var f=Card(SpellKind.Emit,"普通弹",0);var d=Card(SpellKind.Modify,"增伤",0);
            var sample=Asset<SpellProgramSandboxDefinition>();sample.Spells=new[]{m,t,m,f,d,f,f};
            var p=Build(sample);yield return null;yield return null;var view=_host.GetComponentInChildren<WandProgramCanvasViewport>();
            int count=p.CurrentRoot.Trace.Steps.Count;var before=p.SimulatedBefore;
            for(int repeat=0;repeat<2;repeat++)for(int i=0;i<=count;i++){p.Seek(i);view.SetZoom(i%2==0 ? .8f : 1);}
            yield return null;yield return null;
            int objects=_host.GetComponentsInChildren<RectTransform>().Length;
            using(var recorder=ProfilerRecorder.StartNew(ProfilerCategory.Scripts,"Norn.WandPreview.Refresh",4))
            {
                long start=System.GC.GetAllocatedBytesForCurrentThread();
                for(int i=0;i<=count;i++){p.Seek(i);view.SetZoom(i%2==0 ? .8f : 1);}
                long allocated=System.GC.GetAllocatedBytesForCurrentThread()-start;
                yield return null;
                Directory.CreateDirectory("Temp/Spells");
                File.WriteAllText("Temp/Spells/grid-allocation-evidence.json",
                    $"{{\"ManagedBytesForWarmStepAndZoom\":{allocated},\"RefreshProfilerValid\":{recorder.Valid.ToString().ToLowerInvariant()},\"ProfilerSamples\":{recorder.Count},\"LastRefreshNanoseconds\":{recorder.LastValue}}}");
                Assert.That(allocated,Is.Zero,"步进/缩放不应每帧重新生成模型、格子或字符串。");
            }
            Assert.That(_host.GetComponentsInChildren<RectTransform>().Length,Is.EqualTo(objects));
            Assert.That(p.SimulatedBefore,Is.SameAs(before));Assert.That(before.ExecutionRevision,Is.Zero);
        }
    }
}
