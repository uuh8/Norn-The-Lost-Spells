#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Game.Character;
using Game.Combat;
using Game.Core;
using Game.Skills;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using Object = UnityEngine.Object;

namespace Game.Run.Tests
{
    public sealed class OwnedWandV2PlayModeTests
    {
        private const string ScenePath = "Assets/_Project/Scenes/Tests/WandSandbox.unity";
        private const string DataPath = "Assets/_Project/Tests/Fixtures/Run/SinglePlayerWands/";
        private readonly List<Object> _temporary = new List<Object>();
        private ItemCatalog _catalog; private Scene _scene;
        private SinglePlayerInventorySession _session; private OwnedWandCastAdapter _adapter;
        private WizardController _wizard; private SpellCaster _caster; private SpellDefinition _trigger;
        private InputTestFixture _input;private Mouse _mouse;private bool _background;
        private bool Continuous=>TestContext.CurrentContext.Test.Name.StartsWith("Continuous");
        private static FieldInfo Field(Type type, string name) => type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        [UnitySetUp] public IEnumerator Setup()
        {
            Time.timeScale = 1;
            _background=Application.runInBackground;Application.runInBackground=true;_input=new InputTestFixture();_input.Setup();
            InputSystem.AddDevice<Keyboard>();_mouse=InputSystem.AddDevice<Mouse>();EditorWindow.GetWindow(Type.GetType("UnityEditor.GameView,UnityEditor")).Focus();
            _catalog = Object.Instantiate(AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/_Project/Tests/Fixtures/Run/SinglePlayerWands/Catalog.asset")); _temporary.Add(_catalog);
            var originals = (ItemDefinition[])Field(typeof(ItemCatalog), "_definitions").GetValue(_catalog); var copies = new ItemDefinition[originals.Length];
            for (int i = 0; i < copies.Length; i++)
            {
                copies[i] = Object.Instantiate(originals[i]); _temporary.Add(copies[i]);
                if(Continuous && copies[i].Kind==ItemKind.Wand)
                {
                    var stats=Object.Instantiate(copies[i].WandDefinition);_temporary.Add(stats);Field(typeof(ItemDefinition),"_wandDefinition").SetValue(copies[i],stats);
                    var data=new SerializedObject(stats);float delay=TestContext.CurrentContext.Test.Name.Contains("Fast")?.05f:.3f;
                    data.FindProperty("_castCooldown").floatValue=delay;data.FindProperty("_baseReloadTime").floatValue=delay;data.FindProperty("_maxMana").floatValue=100000;
                    data.ApplyModifiedPropertiesWithoutUndo();
                }
                if (copies[i].SpellDefinition == null) continue;
                var spell = Object.Instantiate(copies[i].SpellDefinition); spell.CastSfx = null; _temporary.Add(spell);
                if(TestContext.CurrentContext.Test.Name=="FastCastCommitsReducedDelayBeforeReload" && copies[i].ContentId=="wandtest.fire")
                {spell.Kind=SpellKind.Modify;spell.CastDelayDelta=-.12f;}
                Field(typeof(ItemDefinition), "_spellDefinition").SetValue(copies[i], spell);
                if(Continuous)spell.LifetimeSeconds=.15f;
                if (copies[i].ContentId == "wandtest.trigger") _trigger = spell;
            }
            Field(typeof(ItemCatalog), "_definitions").SetValue(_catalog, copies);
            SceneManager.sceneLoaded += Loaded;
            _scene = EditorSceneManager.LoadSceneInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            yield return null; yield return null; yield return null; SceneManager.sceneLoaded -= Loaded;
        }
        private T Find<T>() where T : Component
        { foreach (var root in _scene.GetRootGameObjects()) { var value = root.GetComponentInChildren<T>(true); if (value != null) return value; } return null; }
        private void Loaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.path != ScenePath) return; _scene = scene; _session = Find<SinglePlayerInventorySession>();
            _adapter = Find<OwnedWandCastAdapter>(); _wizard = Find<WizardController>(); _caster = Find<SpellCaster>();
            Field(typeof(OwnedWandCastAdapter), "_useV2").SetValue(_adapter, true);
            Assert.That(_session.ConfigureAndInitialize(_catalog, AssetDatabase.LoadAssetAtPath<SinglePlayerStartingInventory>("Assets/_Project/Tests/Fixtures/Run/SinglePlayerWands/StartingInventory.asset"),
                "Temp/Spells/RuntimeTests/" + Guid.NewGuid().ToString("N")), Is.True);
        }
        [UnityTest] public IEnumerator FastCastCommitsReducedDelayBeforeReload()
        {
            _session.WandsReadOnly.TryGetSlot(0,out var wand);
            for(int i=1;i<=2;i++)Assert.That(_session.Profile.Items.TryAdd(new RuntimeItemState("cadence"+i,"wandtest.ice",8),
                new ItemLocation(ItemLocationKind.WandSlot,wand.InstanceId,0,0,0,i)),Is.True);
            yield return null;
            _adapter.Select(0);Assert.That(_adapter.TryCapture(out var program,out var context),Is.True);
            Assert.That(_caster.CastOwnedProgram(program,context,new Vector3(200,100,200),new Vector3(200,100,230),1,777,_wizard.CharacterController),Is.EqualTo(1));
            _session.WandsReadOnly.TryGetSlot(0,out var after);
            Assert.That(after.CooldownRemaining,Is.EqualTo(.68f).Within(.0001f),"首组未进入装填，真实提交必须采用0.8−0.12，而非原基础间隔。");
        }

        [UnityTest] public IEnumerator RealTimedChildUsesPrepaidGraphAfterSwitchingWands()
        {
            _trigger.PayloadTrigger = PayloadTriggerMode.AfterDelay; _trigger.PayloadDelaySeconds = .1f;
            _adapter.Select(2); Assert.That(_adapter.TryCapture(out var program, out var context), Is.True);
            Assert.That(_caster.CastOwnedProgram(program, context, new Vector3(0, 4, 0), new Vector3(0, 4, 30), 1, 777, _wizard.CharacterController), Is.EqualTo(1));
            _session.WandsReadOnly.TryGetSlot(2, out var paid); Assert.That(paid.CurrentMana, Is.EqualTo(135));
            _adapter.Select(1); yield return new WaitForSeconds(.2f);
            _session.WandsReadOnly.TryGetSlot(2, out var later); Assert.That(later.CurrentMana, Is.GreaterThanOrEqualTo(135));
            Assert.That(_session.ExecutionReplays.TryGetLatest(program.SourceWandId, out var replay), Is.True);
            Assert.That(replay.GetLayerStatus(1), Is.EqualTo(WandLayerResourceStatus.Committed));
            Assert.That(replay.WorldStatus, Is.EqualTo(WandWorldExecutionStatus.Released), "真实发射要有独立于资源提交的世界观察证据");
        }
        [UnityTest] public IEnumerator QueuedV2ShotWaitsForAnimationThreshold()
        {
            _adapter.Select(0); Assert.That(_adapter.TryCapture(out var program, out var context), Is.True);
            Field(typeof(WizardController), "_pendingProgram").SetValue(_wizard, program);
            Field(typeof(WizardController), "_pendingSource").SetValue(_wizard, context);
            Field(typeof(WizardController), "_pendingCastAimPoint").SetValue(_wizard, new Vector3(0, 4, 30));
            _wizard.AttackBufferCounter = 1; _wizard.TryStartAttack();
            Assert.That(_adapter.LastRootCommitted, Is.False, "排队入口只开始动画，尚未提交资源。");
            yield return new WaitForSeconds(1);Assert.That(_adapter.LastRootCommitted,Is.True);
        }
        [UnityTest] public IEnumerator DelayedSkyfallIsObservedOnlyAfterActualBirth()
        {
            _trigger.SpawnMode=SpellSpawnMode.SkyfallAtPoint;_trigger.LandingSiteDuration=.2f;_trigger.PayloadTrigger=PayloadTriggerMode.None;
            _adapter.Select(2);Assert.That(_adapter.TryCapture(out var program,out var context),Is.True);
            Assert.That(_caster.CastOwnedProgram(program,context,new Vector3(0,4,0),new Vector3(0,0,20),1,777,_wizard.CharacterController),Is.EqualTo(1));
            Assert.That(_session.ExecutionReplays.TryGetLatest(program.SourceWandId,out var replay),Is.True);
            Assert.That(replay.WorldStatus,Is.EqualTo(WandWorldExecutionStatus.NotObserved));
            yield return new WaitForSeconds(.3f);Assert.That(replay.WorldStatus,Is.EqualTo(WandWorldExecutionStatus.Released));
        }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            SceneManager.sceneLoaded -= Loaded;
            if (_scene.IsValid() && _scene.isLoaded) yield return SceneManager.UnloadSceneAsync(_scene);
            for (int i = _temporary.Count - 1; i >= 0; i--) if (_temporary[i] != null) Object.Destroy(_temporary[i]);
            _temporary.Clear(); yield return null; Time.timeScale = 1;
            _input?.TearDown();_input=null;Application.runInBackground=_background;
        }
        [UnityTest] public IEnumerator V2PressWaitsForConfiguredAnimatorRelease()
        {
            _adapter.Select(0);yield return new WaitForSeconds(.2f);
            InputSystem.QueueStateEvent(_mouse,new MouseState{buttons=1});InputSystem.Update();yield return null;
            InputSystem.QueueStateEvent(_mouse,new MouseState());InputSystem.Update();
            Assert.That(_session.ExecutionReplays.Count,Is.Zero,"点击起手不能立刻提交或生成法术；应等待ArrowSpawnTime。");
            yield return new WaitForSeconds(1);
            Assert.That(_session.ExecutionReplays.Count,Is.EqualTo(1),"单击越过释放阈值只提交一发。");
        }
        [UnityTest]public IEnumerator SwitchingAwayAndBackDuringWindupCancelsOldShot()
        {
            _adapter.Select(0);yield return new WaitForSeconds(.2f);
            InputSystem.QueueStateEvent(_mouse,new MouseState{buttons=1});InputSystem.Update();yield return null;
            InputSystem.QueueStateEvent(_mouse,new MouseState());InputSystem.Update();_adapter.Select(1);_adapter.Select(0);
            yield return new WaitForSeconds(1);Assert.That(_session.ExecutionReplays.Count,Is.Zero,"换出再换回也不能复活未释放的旧动画来源。");
        }
        [UnityTest]public IEnumerator DisabledWizardCannotResumeUnreleasedShot()
        {
            _adapter.Select(0);yield return new WaitForSeconds(.2f);
            InputSystem.QueueStateEvent(_mouse,new MouseState{buttons=1});InputSystem.Update();yield return null;
            _wizard.enabled=false;InputSystem.QueueStateEvent(_mouse,new MouseState());InputSystem.Update();yield return null;_wizard.enabled=true;
            yield return new WaitForSeconds(1);Assert.That(_session.ExecutionReplays.Count,Is.Zero);
        }
        [UnityTest] public IEnumerator NormalLifetimeReleasesTerminationPayloadExactlyOnce()
        {
            _trigger.PayloadTrigger=PayloadTriggerMode.OnTermination;_trigger.LifetimeSeconds=.06f;
            _adapter.Select(2);Assert.That(_adapter.TryCapture(out var program,out var context),Is.True);
            Assert.That(_caster.CastOwnedProgram(program,context,new Vector3(0,8,0),new Vector3(0,8,40),1,777,_wizard.CharacterController),Is.EqualTo(1));
            _session.ExecutionReplays.TryGetLatest(program.SourceWandId,out var replay);
            Assert.That(replay.GetLayerStatus(1),Is.EqualTo(WandLayerResourceStatus.Unattempted));
            yield return new WaitForSeconds(.15f);Assert.That(replay.GetLayerStatus(1),Is.EqualTo(WandLayerResourceStatus.Committed));
            Assert.That(replay.ReleasedOutputs[1],Is.True);yield return new WaitForSeconds(.1f);
            _session.WandsReadOnly.TryGetSlot(2,out var wand);Assert.That(wand.CurrentMana,Is.GreaterThanOrEqualTo(135));
        }
        [UnityTest] public IEnumerator CancelledProjectileNeverReleasesTerminationPayload()
        {
            _trigger.PayloadTrigger=PayloadTriggerMode.OnTermination;_trigger.LifetimeSeconds=1;
            _adapter.Select(2);Assert.That(_adapter.TryCapture(out var program,out var context),Is.True);
            _caster.CastOwnedProgram(program,context,new Vector3(0,8,0),new Vector3(0,8,40),1,777,_wizard.CharacterController);
            _session.ExecutionReplays.TryGetLatest(program.SourceWandId,out var replay);
            // 取消走真实禁用/Destroy链；不能通过直接发一个假终止事件来验证。
            var shots=Object.FindObjectsByType<ProjectileBase>(FindObjectsSortMode.None);
            foreach(var shot in shots){shot.gameObject.SetActive(false);Object.Destroy(shot.gameObject);}
            yield return null;Assert.That(replay.GetLayerStatus(1),Is.EqualTo(WandLayerResourceStatus.Cancelled));Assert.That(replay.ReleasedOutputs[1],Is.False);
        }
        [UnityTest]public IEnumerator ContinuousFastInputFor30Seconds(){yield return HoldFor30(.05f);}
        [UnityTest]public IEnumerator GammaBorrowedOutputUsesRealOwnedAdapterWithoutChargingTargetMana()
        {
            _trigger.Kind=SpellKind.Copy;_trigger.CopyMode=SpellCopyMode.Gamma;_trigger.ManaCost=5;_adapter.Select(2);
            Assert.That(_adapter.TryCapture(out var program,out var context),Is.True);
            Assert.That(_caster.CastOwnedProgram(program,context,new Vector3(0,8,0),new Vector3(0,8,40),1,777,_wizard.CharacterController),Is.EqualTo(1));
            _session.WandsReadOnly.TryGetSlot(2,out var state);Assert.That(state.CurrentMana,Is.EqualTo(175));
            _session.ExecutionReplays.TryGetLatest(program.SourceWandId,out var replay);Assert.That(replay.WorldStatus,Is.EqualTo(WandWorldExecutionStatus.Released));Assert.That(replay.After.Deck,Is.EqualTo(new[]{1}));
            yield return null;
        }
        [UnityTest]public IEnumerator DisabledV2SourceCancelsPendingSkyfallWithoutPhantomRelease()
        {
            _trigger.SpawnMode=SpellSpawnMode.SkyfallAtPoint;_trigger.LandingSiteDuration=.2f;_trigger.PayloadTrigger=PayloadTriggerMode.None;
            _adapter.Select(2);_adapter.TryCapture(out var program,out var context);_caster.CastOwnedProgram(program,context,new Vector3(0,8,0),new Vector3(0,0,20),1,777,_wizard.CharacterController);
            _session.ExecutionReplays.TryGetLatest(program.SourceWandId,out var replay);Assert.That(replay.ReleasedOutputs[0],Is.False);
            _adapter.enabled=false;yield return new WaitForSeconds(.3f);
            Assert.That(replay.WorldStatus,Is.EqualTo(WandWorldExecutionStatus.Cancelled));Assert.That(replay.ReleasedOutputs[0],Is.False,"取消后延迟协程不得再生成根弹");
        }
        [UnityTest]public IEnumerator MouseHeldFromPausedUiNeedsReleaseBeforeV2ContinuousFire()
        {
            _adapter.Select(0);Time.timeScale=0;
            InputSystem.QueueStateEvent(_mouse,new MouseState{buttons=1});InputSystem.Update();
            EventBus<HeldItemUseResetEvent>.Publish(new HeldItemUseResetEvent{InventoryId=_session.ReadOnly.InventoryId});yield return null;
            Time.timeScale=1;yield return new WaitForSeconds(1);Assert.That(_session.ExecutionReplays.Count,Is.Zero,"UI中的按住不能在关闭后变成连续射击。");
            InputSystem.QueueStateEvent(_mouse,new MouseState());InputSystem.Update();yield return null;
            InputSystem.QueueStateEvent(_mouse,new MouseState{buttons=1});InputSystem.Update();yield return new WaitForSeconds(1);
            Assert.That(_session.ExecutionReplays.Count,Is.GreaterThan(0),"真正的新按下应开始动画并在释放点发射。");
        }
        [UnityTest]public IEnumerator ContinuousHeavyInputFor30Seconds(){yield return HoldFor30(.3f);}
        private IEnumerator HoldFor30(float configuredDelay)
        {
            SceneManager.SetActiveScene(_scene);_adapter.Select(0);yield return new WaitForSeconds(.2f);
            var shots=new List<double>(800);var frames=new List<float>(10000);
            void Completed(CastResourceResultEvent e){if(e.InventoryId==_session.ReadOnly.InventoryId && e.Operation==CastResourceOperation.Complete && e.Success)shots.Add(Time.timeAsDouble);}
            EventBus<CastResourceResultEvent>.Subscribe(Completed);
            try
            {
                InputSystem.QueueStateEvent(_mouse,new MouseState{buttons=1});InputSystem.Update();
                double stop=Time.realtimeSinceStartupAsDouble+30;
                while(Time.realtimeSinceStartupAsDouble<stop){yield return null;frames.Add(Time.unscaledDeltaTime*1000);}
                InputSystem.QueueStateEvent(_mouse,new MouseState());InputSystem.Update();
            }
            finally{EventBus<CastResourceResultEvent>.Unsubscribe(Completed);}
            var intervals=new List<double>(shots.Count);for(int i=1;i<shots.Count;i++)intervals.Add(shots[i]-shots[i-1]);intervals.Sort();frames.Sort();
            Assert.That(shots.Count,Is.GreaterThan(20),"必须由真实按住输入持续提交，不能仅直接调Caster。");
            Assert.That(intervals[0],Is.GreaterThanOrEqualTo(configuredDelay-.002));
            // 本轮恢复真实起手，间隔包含ArrowSpawnTime前的动画时间；不能沿用“动画仅反馈”的<0.2秒断言。
            Assert.That(intervals[intervals.Count/2],Is.LessThan(configuredDelay+1.0),"按住必须跨多次动画持续释放，不能卡在某次起手。");
            System.IO.Directory.CreateDirectory("Temp/Spells/Performance");
            System.IO.File.WriteAllText("Temp/Spells/Performance/"+TestContext.CurrentContext.Test.Name+".txt",
                "Scope=Editor actual InputSystem held LMB 30 real seconds; existing WandSandbox, no Standalone claim\nConfigured="+configuredDelay+"\nShots="+shots.Count+"\nMedianInterval="+intervals[intervals.Count/2]+"\nP95FrameMs="+frames[(int)((frames.Count-1)*.95)]+"\nP99FrameMs="+frames[(int)((frames.Count-1)*.99)]);
        }
    }
}
#endif
