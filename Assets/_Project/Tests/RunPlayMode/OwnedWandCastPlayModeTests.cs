#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Game.Character;
using Game.Combat;
using Game.Core;
using Game.Skills;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Game.Run.Tests
{
    /// <summary>直接进入交付场景，验证真实库存、输入、Wizard Animator、支付与未来 Projectile 回调。</summary>
    public sealed class OwnedWandCastPlayModeTests
    {
        private const string PathRoot = "Assets/_Project/ScriptableObjects/Tests/SinglePlayerWands/";
        private const string ScenePath = "Assets/_Project/Scenes/Tests/WandSandbox.unity";
        private Scene _scene;
        private SinglePlayerInventorySession _session;
        private OwnedWandCastAdapter _adapter;
        private SpellCaster _caster;
        private WizardController _wizard;
        private SinglePlayerInventoryController _ui;
        private string _directory;
        private float _time;
        private CursorLockMode _lock;
        private bool _cursor;
        private Keyboard _keyboard;
        private Mouse _mouse;
        private EditorWindow _previousWindow;
        private readonly List<Object> _temporary = new List<Object>();
        private SpellDefinition _trigger;
        private ItemCatalog _catalog;
        private InputTestFixture _input;
        [UnitySetUp] public IEnumerator Setup()
        {
            // 官方隔离Runtime在Scene建立Action前设置，避免整批运行受Editor焦点/真实设备影响。
            _input=new InputTestFixture();_input.Setup();
            // Input System默认仅把有GameView焦点的键鼠事件交给Play；测试不能依赖上一例留下焦点。
            _previousWindow = EditorWindow.focusedWindow;
            EditorWindow.GetWindow(Type.GetType("UnityEditor.GameView,UnityEditor")).Focus();
            _time = Time.timeScale; _lock = Cursor.lockState; _cursor = Cursor.visible; Time.timeScale = 1;
            _directory = System.IO.Path.GetFullPath(System.IO.Path.Combine("Temp/SinglePlayer/WandTests", Guid.NewGuid().ToString("N")));
            _catalog = Object.Instantiate(AssetDatabase.LoadAssetAtPath<ItemCatalog>(PathRoot + "Catalog.asset")); _temporary.Add(_catalog);
            // 测试定时/非法输出只修改临时 SO 克隆，绝不改 Project 中的法术或目录。
            var entries = (ItemDefinition[])Field(typeof(ItemCatalog), "_definitions").GetValue(_catalog);
            var copies = new ItemDefinition[entries.Length];
            for (int i = 0; i < entries.Length; i++)
            {
                copies[i] = Object.Instantiate(entries[i]); _temporary.Add(copies[i]);
                if (copies[i].SpellDefinition == null) continue;
                var spell = Object.Instantiate(copies[i].SpellDefinition); _temporary.Add(spell);
                // 资源/来源测试不创建延迟销毁的全局音源，避免卸载隔离 Scene 后遗留一次性播放对象。
                spell.CastSfx = null;
                Field(typeof(ItemDefinition), "_spellDefinition").SetValue(copies[i], spell);
                if (copies[i].ContentId == "wandtest.trigger") _trigger = spell;
            }
            Field(typeof(ItemCatalog), "_definitions").SetValue(_catalog, copies);
            SceneManager.sceneLoaded += Loaded;
            _scene = EditorSceneManager.LoadSceneInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            yield return null; yield return null; yield return null;
            SceneManager.sceneLoaded -= Loaded;
            Assert.That(_session.WandsReadOnly, Is.Not.Null); Assert.That(_ui.IsOpen, Is.False);
            Assert.That(_session.WandsReadOnly.TryGetSlot(2, out _), Is.True);
        }
        private void Loaded(Scene scene, LoadSceneMode _)
        {
            if (scene.path != ScenePath) return; _scene = scene;
            _session = Find<SinglePlayerInventorySession>(); _adapter = Find<OwnedWandCastAdapter>(); _caster = Find<SpellCaster>();
            _wizard = Find<WizardController>(); _ui = Find<SinglePlayerInventoryController>();
            Assert.That(_session.ConfigureAndInitialize(_catalog, AssetDatabase.LoadAssetAtPath<SinglePlayerStartingInventory>(PathRoot + "StartingInventory.asset"), _directory), Is.True);
        }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            try
            {
            SceneManager.sceneLoaded -= Loaded;
            if (_keyboard != null) InputSystem.RemoveDevice(_keyboard); if (_mouse != null) InputSystem.RemoveDevice(_mouse);
            if (_ui != null) _ui.Close();
            if (_scene.IsValid() && _scene.isLoaded) yield return SceneManager.UnloadSceneAsync(_scene);
            // Instantiate 不继承施法者 Scene；清理测试期产生的投射物，避免跨例污染。
            foreach (ProjectileBase projectile in Object.FindObjectsByType<ProjectileBase>(FindObjectsSortMode.None)) Object.Destroy(projectile.gameObject);
            for (int i = _temporary.Count - 1; i >= 0; i--) if (_temporary[i] != null) Object.Destroy(_temporary[i]); _temporary.Clear();
            yield return null;
            Time.timeScale = _time; Cursor.lockState = _lock; Cursor.visible = _cursor;
            if (_previousWindow != null) _previousWindow.Focus();
            string root = System.IO.Path.GetFullPath("Temp/SinglePlayer/WandTests") + System.IO.Path.DirectorySeparatorChar;
            if (_directory.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(_directory)) Directory.Delete(_directory, true);
            }
            finally {_input?.TearDown();_input=null;}
        }
        private T Find<T>() where T : Component
        { foreach (var root in _scene.GetRootGameObjects()) { var found = root.GetComponentInChildren<T>(true); if (found != null) return found; } return null; }
        private static FieldInfo Field(Type type, string name) => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        private WandStateSnapshot State(int slot)
        { Assert.That(_session.WandsReadOnly.TryGetSlot(slot, out WandStateSnapshot state), Is.True); return state; }
        private int Cast(in WandProgramSnapshot program, in OwnedWandCastContext source) =>
            _caster.CastOwnedProgram(program, source, new Vector3(0, 4, 0), new Vector3(0, 4, 30), 1, 777, _wizard.CharacterController);
        private void Capture(int slot, out WandProgramSnapshot program, out OwnedWandCastContext source)
        { _adapter.Select(slot); Assert.That(_adapter.TryCapture(out program, out source), Is.True); }
        private ProjectileBase RuntimeProjectile()
        { var list = Object.FindObjectsByType<ProjectileBase>(FindObjectsSortMode.None); Assert.That(list, Has.Length.EqualTo(1)); return list[0]; }
        private static void Invoke(ProjectileBase projectile, string name)
        { var action = (Action<Vector3, Vector3>)Field(typeof(ProjectileBase), name).GetValue(projectile);
            Assert.That(action, Is.Not.Null); action(Vector3.up * 4, Vector3.forward); }

        [UnityTest] public IEnumerator RootLocksClickSourceAndUsesOwnedManaOnly()
        {
            Capture(0, out var program, out var source); _adapter.Select(1);
            float legacy = _wizard.GetComponent<ManaComponent>().CurrentMana;
            Assert.That(Cast(program, source), Is.EqualTo(1)); Assert.That(State(0).CurrentMana, Is.EqualTo(80));
            Assert.That(State(1).CurrentMana, Is.EqualTo(60)); Assert.That(State(0).CooldownRemaining, Is.EqualTo(.8f));
            Assert.That(_wizard.GetComponent<ManaComponent>().CurrentMana, Is.EqualTo(legacy)); yield return null;
        }
        [UnityTest] public IEnumerator ImpactPayloadKeepsOriginalWandAfterSwitch()
        {
            Capture(2, out var program, out var source); Assert.That(Cast(program, source), Is.EqualTo(1));
            var projectile = RuntimeProjectile(); _adapter.Select(1); Invoke(projectile, "Impacted");
            Assert.That(State(2).CurrentMana, Is.EqualTo(135)); Assert.That(State(1).CurrentMana, Is.EqualTo(60));
            Assert.That(State(2).CooldownRemaining, Is.EqualTo(1.2f)); yield return null;
        }
        [UnityTest] public IEnumerator TimedPayloadKeepsOriginalWandAfterSwitch()
        {
            _trigger.PayloadTrigger = PayloadTriggerMode.AfterDelay; _trigger.PayloadDelaySeconds = 10;
            Capture(2, out var program, out var source); Assert.That(Cast(program, source), Is.EqualTo(1));
            var projectile = RuntimeProjectile(); _adapter.Select(0); Invoke(projectile, "TimedTriggerElapsed");
            Assert.That(State(2).CurrentMana, Is.EqualTo(135)); Assert.That(State(0).CurrentMana, Is.EqualTo(100)); yield return null;
        }
        [UnityTest] public IEnumerator PayloadInsufficientManaDoesNotBorrowSelectedWand()
        {
            _trigger.ManaCost = 170; Capture(2, out var program, out var source);
            Assert.That(Cast(program, source), Is.EqualTo(1)); var projectile = RuntimeProjectile(); _adapter.Select(1);
            Invoke(projectile, "Impacted"); Assert.That(State(2).CurrentMana, Is.EqualTo(10)); Assert.That(State(1).CurrentMana, Is.EqualTo(60));
            Assert.That(Object.FindObjectsByType<ProjectileBase>(FindObjectsSortMode.None), Has.Length.EqualTo(1)); yield return null;
        }
        [UnityTest] public IEnumerator InvalidPrefabDoesNotChargeOrStartCooldown()
        { Capture(2, out var program, out var source); _trigger.ProjectilePrefab = null;
            Assert.That(Cast(program, source), Is.Zero); Assert.That(State(2).CurrentMana, Is.EqualTo(180)); Assert.That(State(2).CooldownRemaining, Is.Zero); yield return null; }
        [UnityTest] public IEnumerator ActualTimerExecutesPayloadFromOriginalWand()
        {
            _trigger.PayloadTrigger = PayloadTriggerMode.AfterDelay; _trigger.PayloadDelaySeconds = .1f;
            Capture(2, out var program, out var source); Assert.That(Cast(program, source), Is.EqualTo(1));
            ProjectileBase parent = RuntimeProjectile(); _adapter.Select(1);
            yield return new WaitForSeconds(.2f);
            Assert.That(State(2).CurrentMana, Is.InRange(135f, 138f)); Assert.That(State(1).CurrentMana, Is.EqualTo(60));
            // 当前生产计时合同触发后销毁父弹；留下一个真实子弹，不能要求父/子同时存在。
            Assert.That(parent == null, Is.True);
            Assert.That(Object.FindObjectsByType<ProjectileBase>(FindObjectsSortMode.None), Has.Length.EqualTo(1));
        }
        [UnityTest] public IEnumerator DelayedSkyfallPreservesSourceIntoImpactPayload()
        {
            _trigger.SpawnMode = SpellSpawnMode.SkyfallAtPoint; _trigger.LandingSiteDuration = .1f;
            _trigger.LandingSitePrefab = null; _trigger.SkyfallHeight = 12;
            Capture(2, out var program, out var source); Assert.That(Cast(program, source), Is.EqualTo(1)); _adapter.Select(1);
            yield return new WaitForSeconds(.15f);
            float before = State(2).CurrentMana; Invoke(RuntimeProjectile(), "Impacted");
            Assert.That(State(2).CurrentMana, Is.EqualTo(before - 20).Within(.001)); Assert.That(State(1).CurrentMana, Is.EqualTo(60));
        }
        [UnityTest] public IEnumerator EditingAfterLaunchPreservesCapturedPayload()
        {
            Capture(2, out var program, out var source); Assert.That(Cast(program, source), Is.EqualTo(1)); var projectile = RuntimeProjectile();
            var read = _session.ReadOnly;
            EventBus<InventoryCommandEvent>.Publish(new InventoryCommandEvent { InventoryId = read.InventoryId, Kind = InventoryCommandKind.Place,
                ItemId = program.Program.GetInstanceId(1), Destination = InventoryDestination.Warehouse, ExpectedVersion = read.Version });
            Invoke(projectile, "Impacted"); Assert.That(State(2).CurrentMana, Is.EqualTo(135));
            Assert.That(Object.FindObjectsByType<ProjectileBase>(FindObjectsSortMode.None), Has.Length.EqualTo(2)); yield return null;
        }
        [UnityTest] public IEnumerator IndependentRootCooldownAllowsAnotherWandImmediately()
        {
            Capture(0, out var first, out var a); Assert.That(Cast(first, a), Is.EqualTo(1));
            Capture(1, out var second, out var b); Assert.That(Cast(second, b), Is.EqualTo(1));
            Assert.That(State(0).CooldownRemaining, Is.EqualTo(.8f)); Assert.That(State(1).CooldownRemaining, Is.EqualTo(.25f));
            Assert.That(State(0).CurrentMana, Is.EqualTo(80)); Assert.That(State(1).CurrentMana, Is.EqualTo(55)); yield return null;
        }
        [UnityTest] public IEnumerator PauseFreezesResourcesAndDoesNotQueueUiClick()
        {
            Capture(0, out var program, out var source); Cast(program, source); _ui.Open();
            _mouse = InputSystem.AddDevice<Mouse>(); InputSystem.QueueStateEvent(_mouse, new MouseState { buttons = 1 }); InputSystem.Update();
            Assert.That(_wizard.AttackBufferCounter, Is.Zero);
            yield return null; yield return null; Assert.That(State(0).CurrentMana, Is.EqualTo(80)); Assert.That(State(0).CooldownRemaining, Is.EqualTo(.8f));
            InputSystem.QueueStateEvent(_mouse, new MouseState()); InputSystem.Update(); _ui.Close();
            Assert.That(_wizard.AttackBufferCounter, Is.Zero);
        }
        [UnityTest] public IEnumerator ChangedProgramRejectsQueuedRootWithoutPayment()
        {
            Capture(0, out var program, out var source); string card = program.Program.GetInstanceId(0);
            var read = _session.ReadOnly;
            EventBus<InventoryCommandEvent>.Publish(new InventoryCommandEvent { InventoryId = read.InventoryId,
                Kind = InventoryCommandKind.Place, ItemId = card, Destination = InventoryDestination.Warehouse, ExpectedVersion = read.Version });
            Assert.That(Cast(program, source), Is.Zero); Assert.That(State(0).CurrentMana, Is.EqualTo(100)); yield return null;
        }
        [UnityTest] public IEnumerator EmptyProgramDoesNotCharge()
        {
            Capture(0, out var before, out _); var read = _session.ReadOnly;
            EventBus<InventoryCommandEvent>.Publish(new InventoryCommandEvent { InventoryId = read.InventoryId, Kind = InventoryCommandKind.Place,
                ItemId = before.Program.GetInstanceId(0), Destination = InventoryDestination.Warehouse, ExpectedVersion = read.Version });
            Capture(0, out var program, out var source); Assert.That(Cast(program, source), Is.Zero);
            Assert.That(State(0).CurrentMana, Is.EqualTo(100)); Assert.That(State(0).CooldownRemaining, Is.Zero); yield return null;
        }
        [UnityTest] public IEnumerator DisabledSessionFailsWithoutLegacyFallback()
        { Capture(0, out var program, out var source); _session.enabled = false;
            Assert.That(Cast(program, source), Is.Zero); Assert.That(State(0).CurrentMana, Is.EqualTo(100)); yield return null; }
        [UnityTest] public IEnumerator MissingEventReceiverCannotApproveFreeCast()
        {
            Capture(0, out var program, out var source);
            EventBus<CastResourceRequestedEvent>.Clear();
            Assert.That(Cast(program, source), Is.Zero); Assert.That(State(0).CurrentMana, Is.EqualTo(100));
            Assert.That(_adapter.LastFailure, Is.EqualTo(OwnedWandFailureReason.NoReceiver)); yield return null;
        }
        [UnityTest] public IEnumerator WrongRuntimeComponentDoesNotCharge()
        {
            var invalid = new GameObject("Invalid Emit Prefab"); _temporary.Add(invalid); _trigger.ProjectilePrefab = invalid;
            Capture(2, out var program, out var source); Assert.That(Cast(program, source), Is.Zero);
            Assert.That(State(2).CurrentMana, Is.EqualTo(180)); Assert.That(State(2).CooldownRemaining, Is.Zero); yield return null;
        }
        private void MakeFinite(string id, int charges)
        {
            var profile = (SinglePlayerProfile)Field(typeof(SinglePlayerInventorySession), "_profile").GetValue(_session);
            Assert.That(profile.Items.TryGet(id, out var item), Is.True); profile.Items.TryGetLocation(id, out var location);
            typeof(ItemOwnershipStore).GetMethod("TryApply", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(profile.Items,
                new object[] { new[] { new InventoryEntry(new RuntimeItemState(id, item.ContentId, charges, false), location) }, profile.Items.Version });
            EventBus<InventoryChangedEvent>.Publish(new InventoryChangedEvent { InventoryId = _session.ReadOnly.InventoryId, Version = profile.Items.Version });
        }
        private int Remaining(string id) { Assert.That(_session.ReadOnly.TryGetView(id, out var item), Is.True); return item.RemainingCharges; }
        [UnityTest] public IEnumerator FiniteTriggerPrepaysBothInstancesAndRepeatedImpactCannotDuplicate()
        {
            Capture(2, out var program, out var source); string root = program.Program.GetInstanceId(0), child = program.Program.GetInstanceId(1);
            MakeFinite(root, 2); MakeFinite(child, 2);
            Assert.That(Cast(program, source), Is.EqualTo(1)); Assert.That(Remaining(root), Is.EqualTo(1)); Assert.That(Remaining(child), Is.EqualTo(1));
            var projectile = RuntimeProjectile(); Invoke(projectile, "Impacted"); Invoke(projectile, "Impacted");
            Assert.That(State(2).CurrentMana, Is.EqualTo(135)); Assert.That(Remaining(child), Is.EqualTo(1));
            Assert.That(Object.FindObjectsByType<ProjectileBase>(FindObjectsSortMode.None), Has.Length.EqualTo(2)); yield return null;
        }
        [UnityTest] public IEnumerator ExhaustedPayloadStopsRootWithoutManaCooldownOrProjectile()
        {
            Capture(2, out var program, out var source); MakeFinite(program.Program.GetInstanceId(1), 0);
            Assert.That(Cast(program, source), Is.Zero); Assert.That(State(2).CurrentMana, Is.EqualTo(180)); Assert.That(State(2).CooldownRemaining, Is.Zero);
            Assert.That(Object.FindObjectsByType<ProjectileBase>(FindObjectsSortMode.None), Is.Empty); yield return null;
        }
        [UnityTest] public IEnumerator MutatingPayloadDefinitionAfterLaunchDoesNotChangeFrozenDamage()
        {
            Capture(2, out var program, out var source); var payload = program.Program[1]; float damage = payload.BaseDamage;
            Assert.That(Cast(program, source), Is.EqualTo(1)); var parent = RuntimeProjectile(); payload.BaseDamage = 999;
            Invoke(parent, "Impacted");
            var projectiles = Object.FindObjectsByType<ProjectileBase>(FindObjectsSortMode.None);
            foreach (var projectile in projectiles) if (projectile != parent)
                Assert.That((float)Field(typeof(ProjectileBase), "_damage").GetValue(projectile), Is.EqualTo(damage));
            yield return null;
        }
        [UnityTest] public IEnumerator DestroyingUntriggeredParentReleasesReservationWithoutRefund()
        {
            Capture(2, out var program, out var source); string child = program.Program.GetInstanceId(1); MakeFinite(child, 2);
            Assert.That(Cast(program, source), Is.EqualTo(1)); Assert.That(_session.CastResourcesReadOnly.ActiveReservations, Is.EqualTo(1));
            Object.Destroy(RuntimeProjectile().gameObject); yield return null;
            Assert.That(_session.CastResourcesReadOnly.ActiveReservations, Is.Zero); Assert.That(Remaining(child), Is.EqualTo(1));
        }
        [UnityTest] public IEnumerator DestroyedCasterCancelsDelayedSkyfallBudgetWhileSessionRemains()
        {
            _trigger.SpawnMode = SpellSpawnMode.SkyfallAtPoint; _trigger.LandingSiteDuration = 10; _trigger.LandingSitePrefab = null;
            Capture(2, out var program, out var source); string child = program.Program.GetInstanceId(1); MakeFinite(child, 2);
            Assert.That(Cast(program, source), Is.EqualTo(1)); Assert.That(_session.CastResourcesReadOnly.ActiveReservations, Is.EqualTo(1));
            Object.Destroy(_caster); yield return null;
            Assert.That(_session.isActiveAndEnabled, Is.True); Assert.That(_session.CastResourcesReadOnly.ActiveReservations, Is.Zero);
            Assert.That(Remaining(child), Is.EqualTo(1));
        }
        [UnityTest] public IEnumerator RootInitExceptionCleansSpawnAndLeavesFiniteUsesManaCooldownUntouched()
        {
            var failing = new GameObject("Failing Test Prefab", typeof(Rigidbody), typeof(BoxCollider), typeof(ThrowingOwnedTestProjectile));
            failing.SetActive(false); _temporary.Add(failing); _trigger.ProjectilePrefab = failing;
            Capture(2, out var program, out var source); string root = program.Program.GetInstanceId(0), child = program.Program.GetInstanceId(1);
            MakeFinite(root, 2); MakeFinite(child, 2); uint version = _session.ReadOnly.Version;
            Assert.That(Cast(program, source), Is.Zero); Assert.That(State(2).CurrentMana, Is.EqualTo(180)); Assert.That(State(2).CooldownRemaining, Is.Zero);
            Assert.That(Remaining(root), Is.EqualTo(2)); Assert.That(Remaining(child), Is.EqualTo(2)); Assert.That(_session.ReadOnly.Version, Is.EqualTo(version));
            Assert.That(_adapter.LastFailure, Is.EqualTo(OwnedWandFailureReason.SpawnFailed));
            yield return null; Assert.That(_session.CastResourcesReadOnly.ActiveReservations, Is.Zero);
            Assert.That(Object.FindObjectsByType<ThrowingOwnedTestProjectile>(FindObjectsInactive.Include, FindObjectsSortMode.None), Has.Length.EqualTo(1));
        }
        [UnityTest] public IEnumerator ChildInitExceptionKeepsPrepaymentButDoesNotSpendFutureMana()
        {
            var failing = new GameObject("Failing Child Prefab", typeof(Rigidbody), typeof(BoxCollider), typeof(ThrowingOwnedTestProjectile));
            failing.SetActive(false); _temporary.Add(failing);
            Capture(2, out var program, out var source); program.Program[1].ProjectilePrefab = failing;
            string child = program.Program.GetInstanceId(1); MakeFinite(child, 2);
            Assert.That(Cast(program, source), Is.EqualTo(1)); Invoke(RuntimeProjectile(), "Impacted");
            Assert.That(State(2).CurrentMana, Is.EqualTo(155)); Assert.That(Remaining(child), Is.EqualTo(1));
            yield return null; Assert.That(_session.CastResourcesReadOnly.ActiveReservations, Is.Zero);
        }
        [UnityTest] public IEnumerator FailureOnSecondEmitRollsBackPreviouslyCreatedFirstEmit()
        {
            _trigger.Kind = SpellKind.Multicast; _trigger.ExtraDraws = 1; _trigger.ManaCost = 0;
            Capture(1, out var iceProgram, out _); string ice = iceProgram.Program.GetInstanceId(0);
            var failing = new GameObject("Second Emit Failure", typeof(Rigidbody), typeof(BoxCollider), typeof(ThrowingOwnedTestProjectile));
            failing.SetActive(false); _temporary.Add(failing); iceProgram.Program[0].ProjectilePrefab = failing; MakeFinite(ice, 2);
            var read = _session.ReadOnly; _session.WandsReadOnly.TryGetSlot(2, out var wand);
            EventBus<InventoryCommandEvent>.Publish(new InventoryCommandEvent { InventoryId = read.InventoryId, Kind = InventoryCommandKind.Place,
                ItemId = ice, Destination = InventoryDestination.WandSlot, OwnerId = wand.InstanceId, SlotIndex = 2, ExpectedVersion = read.Version });
            Capture(2, out var program, out var source); string fire = program.Program.GetInstanceId(1); MakeFinite(fire, 2);
            Assert.That(Cast(program, source), Is.Zero); Assert.That(State(2).CurrentMana, Is.EqualTo(180));
            Assert.That(Remaining(fire), Is.EqualTo(2)); Assert.That(Remaining(ice), Is.EqualTo(2));
            yield return null; Assert.That(Object.FindObjectsByType<ProjectileBase>(FindObjectsSortMode.None), Is.Empty);
            Assert.That(_session.CastResourcesReadOnly.ActiveReservations, Is.Zero);
        }
        [UnityTest] public IEnumerator NewClickDuringAnimationChangesPendingButKeepsActiveSource()
        {
            _mouse = InputSystem.AddDevice<Mouse>(); Capture(0, out _, out _);
            InputSystem.QueueStateEvent(_mouse, new MouseState { buttons = 1 }); InputSystem.Update();
            Assert.That(_wizard.TryStartAttack(), Is.True); // 进入真实 FSM，但先不推进 Animator 出手帧。
            InputSystem.QueueStateEvent(_mouse, new MouseState()); InputSystem.Update(); _adapter.Select(1);
            InputSystem.QueueStateEvent(_mouse, new MouseState { buttons = 1 }); InputSystem.Update();
            var active = (WandProgramSnapshot)Field(typeof(WizardController), "_activeProgram").GetValue(_wizard);
            var pending = (WandProgramSnapshot)Field(typeof(WizardController), "_pendingProgram").GetValue(_wizard);
            Assert.That(active.SourceWandId, Is.EqualTo(State(0).InstanceId)); Assert.That(pending.SourceWandId, Is.EqualTo(State(1).InstanceId));
            InputSystem.QueueStateEvent(_mouse, new MouseState()); InputSystem.Update();
            float timeout = Time.realtimeSinceStartup + 3;
            while (State(0).CurrentMana >= 100 && Time.realtimeSinceStartup < timeout) yield return null;
            Assert.That(State(0).CurrentMana, Is.LessThan(100)); Assert.That(State(1).CurrentMana, Is.EqualTo(60));
        }
        [UnityTest] public IEnumerator NumericKeysAndPauseGuardUseActualInputActions()
        {
            _keyboard = InputSystem.AddDevice<Keyboard>();
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState(Key.Digit2)); InputSystem.Update();
            Assert.That(State(1).Selected, Is.True);
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState()); InputSystem.Update();
            _ui.Open(); Assert.That(Time.timeScale, Is.Zero);
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState(Key.Digit3)); InputSystem.Update();
            Assert.That(State(1).Selected, Is.True); yield return null;
        }
        [UnityTest] public IEnumerator RealWizardAnimationReleasesClickSourceAfterSwitch()
        {
            _mouse = InputSystem.AddDevice<Mouse>(); Capture(0, out _, out _);
            InputSystem.QueueStateEvent(_mouse, new MouseState { buttons = 1 }); InputSystem.Update();
            Assert.That(_wizard.AttackBufferCounter, Is.GreaterThan(0)); _adapter.Select(1);
            InputSystem.QueueStateEvent(_mouse, new MouseState()); InputSystem.Update();
            float timeout = Time.realtimeSinceStartup + 3;
            while (State(0).CurrentMana >= 100 && Time.realtimeSinceStartup < timeout) yield return null;
            Assert.That(State(0).CurrentMana, Is.LessThan(100), "真实 Animator 出手阈值应提交 A 的资源。");
            Assert.That(State(1).CurrentMana, Is.EqualTo(60)); Assert.That(_wizard.AttackCooldownCounter, Is.Zero);
        }
        [UnityTest] public IEnumerator HudRendersReadableScene()
        {
            Assert.That(Find<WandResourceHud>(), Is.Not.Null); Assert.That(Find<Camera>().orthographic, Is.False);
            Assert.That(Find<AudioListener>(), Is.Not.Null);
            EditorWindow.GetWindow(Type.GetType("UnityEditor.GameView,UnityEditor")).Focus();
            yield return null; yield return new WaitForSecondsRealtime(.3f);
            ScreenCapture.CaptureScreenshot("Temp/SinglePlayer/wand-sandbox.png");
            yield return null; yield return new WaitForSecondsRealtime(.3f);
            Assert.That(File.Exists("Temp/SinglePlayer/wand-sandbox.png"), Is.True);
            foreach (Image image in Find<WandResourceHud>().GetComponentsInChildren<Image>())
                if (image.type == Image.Type.Filled)
                { Assert.That(image.sprite, Is.Not.Null, "Filled Image 必须绑定 Sprite 才会按 fillAmount 生成 Mesh。");
                    if (image.name == "Cooldown") Assert.That(image.fillAmount, Is.Zero); }
            foreach (GameObject root in _scene.GetRootGameObjects()) foreach (Text text in root.GetComponentsInChildren<Text>())
                Assert.That(text.font, Is.Not.Null);
        }
    }
    // 故障注入只存在于测试 Assembly；无需污染生产 Prefab 或增加 Runtime 测试开关。
    public sealed class ThrowingOwnedTestProjectile : ProjectileBase
    {
        public override void Init(byte team, int attacker, float damage, DamageType type, Vector3 velocity, Collider collider, bool useGravity = true)
        { throw new InvalidOperationException("测试 Init 故障"); }
    }
}
#endif
