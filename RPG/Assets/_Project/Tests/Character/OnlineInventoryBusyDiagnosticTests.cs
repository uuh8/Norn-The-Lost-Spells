using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Combat;
using Game.Core;
using Game.Run;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Game.Character.Tests
{
    /// <summary>缺少诊断API时由行为断言RED，不让尚未添加的合同类型造成整批编译失败。</summary>
    public sealed class OnlineInventoryBusyDiagnosticTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string Wand = "busy.diag.wand", Card = "busy.diag.fire";
        private OnlineCombatDefinition _definition;
        private OnlineOwnedWorkspace _owner;
        private GameObject _root;
        private OnlineCastDriver _driver;
        private Scene _scene;
        private CombatSimulationWorld _world;
        private readonly HashSet<int> _initialRoots = new HashSet<int>();
        private readonly List<GameObject> _outputs = new List<GameObject>();
        private readonly List<string> _busyLogs = new List<string>();

        [SetUp] public void Setup()
        {
            _definition = AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>("Assets/_Project/Resources/Online/CombatDefinition.asset");
            Assert.That(_definition, Is.Not.Null);
            Assert.That(_definition.Catalog.TryCreateIndex(out var catalog, out var error), Is.True, error);
            Assert.That(OnlineOwnedWorkspaceFactory.TryCreate(catalog, Assets(), _definition.GameplayConfigHash, out _owner, out error), Is.True, error);
            _busyLogs.Clear(); _initialRoots.Clear(); _outputs.Clear(); _scene = default; _world = null;
            Application.logMessageReceived += OnLog;
            EventBus<CombatOutputSpawnedEvent>.Subscribe(OnBorn);
        }

        [TearDown] public void Cleanup()
        {
            Application.logMessageReceived -= OnLog;
            EventBus<CombatOutputSpawnedEvent>.Unsubscribe(OnBorn);
            if (_driver != null)
            {
                Lifecycle(_driver, "OnDisable");
                var adapter = _root.GetComponent<OwnedWandCastAdapter>();
                if (adapter != null) Lifecycle(adapter, "OnDisable");
                Lifecycle(_driver, "OnDestroy");
            }
            _owner?.Dispose(); _owner = null;
            foreach (var output in _outputs) DestroyOwned(output);
            DestroyOwned(_root); _outputs.Clear();
            _root = null; _driver = null;
        }

        [Test] public void CoreBusyValueIsReadonlyAndDefaultMeansUnavailable()
        {
            var type = typeof(OnlineCastIntent).Assembly.GetType("Game.Core.OnlineInventoryBusyDiagnostic");
            Assert.That(type, Is.Not.Null, "缺少只读Busy值合同。");
            Assert.That(type.IsValueType, Is.True);
            foreach (var name in new[] { "Captured", "RaidActionsFrozen", "ItemsResourceBusy", "CastPending", "ActiveReservations" })
            {
                var field = type.GetField(name); Assert.That(field, Is.Not.Null, name);
                Assert.That(field.IsInitOnly, Is.True, "诊断不能泄露可写Owner状态：" + name);
            }
            AssertOwner(Activator.CreateInstance(type), false, false, false, false, 0);
        }

        [Test] public void OwnerCaptureTracksRealPrepareCommitAndObservedReleaseWithoutMutatingResources()
        {
            AssertOwner(CaptureOwner(), true, false, false, false, 0);
            var prepare = _owner.Execute(Request(CastResourceOperation.PrepareRoot, 1));
            Assert.That(prepare.Success, Is.True, prepare.Reason.ToString());
            AssertOwner(CaptureOwner(), true, false, true, true, 1);
            var commit = Request(CastResourceOperation.Complete, 2); commit.TicketId = prepare.TicketId;
            Assert.That(_owner.Execute(commit).Success, Is.True);
            AssertOwner(CaptureOwner(), true, false, false, false, 1);
            uint version = _owner.InventoryVersion;
            Assert.That(_owner.TryGetWandExecution(Wand, out var before), Is.True);
            var a = CaptureOwner(); var b = CaptureOwner();
            Assert.That(_owner.TryGetWandExecution(Wand, out var after), Is.True);
            Assert.That(_owner.InventoryVersion, Is.EqualTo(version));
            Assert.That(after.Mana, Is.EqualTo(before.Mana));
            Assert.That(after.Draw.ExecutionRevision, Is.EqualTo(before.Draw.ExecutionRevision));
            AssertOwner(a, true, false, false, false, 1); AssertOwner(b, true, false, false, false, 1);
            EventBus<SpellExecutionObservedEvent>.Publish(new SpellExecutionObservedEvent {
                InventoryId = _owner.InventoryId, CallerId = "busy.diag.caller", SourceWandId = Wand,
                TicketId = prepare.TicketId, ProgramRevision = 1, LayerId = 0, OutputId = 0,
                SimulationTime = 1, Kind = SpellExecutionObservationKind.OutputReleased });
            AssertOwner(CaptureOwner(), true, false, false, false, 0);
            _owner.Dispose(); AssertOwner(CaptureOwner(), false, false, false, false, 0);
        }

        [Test] public void OwnerCaptureSeparatesMaterialLockFromPendingCast()
        {
            var profile = (SinglePlayerProfile)typeof(OnlineOwnedWorkspace).GetField("_profile", Private).GetValue(_owner);
            var store = profile.Items;
            var reserve = typeof(ItemOwnershipStore).GetMethod("TryLockMaterial", Private);
            var finish = typeof(ItemOwnershipStore).GetMethod("FinishMaterial", Private);
            Assert.That((bool)reserve.Invoke(store, new object[] { store.Version }), Is.True);
            try { AssertOwner(CaptureOwner(), true, false, true, false, 0); }
            finally { finish.Invoke(store, new object[] { false }); }
            AssertOwner(CaptureOwner(), true, false, false, false, 0);
        }

        [Test] public void OwnerCaptureReportsRaidFreezeSeparatelyWithoutReleasingIt()
        {
            var profile = (SinglePlayerProfile)typeof(OnlineOwnedWorkspace).GetField("_profile", Private).GetValue(_owner);
            var frozen = typeof(SinglePlayerProfile).GetProperty("RaidActionsFrozen", Private);
            frozen.SetValue(profile, true);
            try { AssertOwner(CaptureOwner(), true, true, false, false, 0); Assert.That(frozen.GetValue(profile), Is.True); }
            finally { frozen.SetValue(profile, false); }
        }

        [Test] public void DriverCaptureDistinguishesQueueTimelineAndIdleAtTheRequestedProbeTick()
        {
            CreateDriver();
            var idle = CaptureDriver(101);
            Assert.That(Field<bool>(idle, "DriverBusyCaptured"), Is.True);
            Assert.That(Field<bool>(idle, "Queued"), Is.False); Assert.That(Field<bool>(idle, "TimelineActive"), Is.False);
            AssertOwner(Field<object>(idle, "OwnerBusy"), true, false, false, false, 0);
            var intent = CastIntent(); Assert.That(_driver.TryQueue(intent, out _), Is.True);
            var queued = CaptureDriver(102);
            Assert.That(Field<bool>(queued, "Queued"), Is.True); Assert.That(Field<float>(queued, "QueueRemaining"), Is.GreaterThan(0));
            Assert.That(Field<bool>(queued, "TimelineActive"), Is.False);
            string json = JsonUtility.ToJson(queued);
            Assert.That(json, Does.Contain("\"OwnerBusy\":").And.Contain("\"Captured\":true")
                .And.Contain("\"DriverBusyCaptured\":true").And.Contain("\"Queued\":true"),
                "Room state.json必须包含真实诊断字段，不能把readonly Core值序列化成空对象。");
            _driver.StepAuthority(.01f, 1);
            var running = CaptureDriver(103);
            Assert.That(Field<bool>(running, "Queued"), Is.False); Assert.That(Field<bool>(running, "TimelineActive"), Is.True);
            for (uint tick = 2; tick < 32; tick++) _driver.StepAuthority(1f / 30f, tick);
            var complete = CaptureDriver(104);
            Assert.That(Field<bool>(complete, "Queued"), Is.False); Assert.That(Field<bool>(complete, "TimelineActive"), Is.False);
            AssertOwner(Field<object>(complete, "OwnerBusy"), true, false, false, false, 0);
        }

        [Test] public void StaticAuthorityDtoAndClientDiagnosticsDoNotInventAuthorityBusyState()
        {
            Assert.That(_owner.TryCapturePresentation(1, 1, 11, out var source), Is.True);
            Assert.That(OnlineCastInventoryDiagnosticSnapshot.TryFromAuthoritySnapshot(source, 121, out var raw), Is.True);
            Assert.That(Field<bool>(raw, "DriverBusyCaptured"), Is.False);
            AssertOwner(Field<object>(raw, "OwnerBusy"), false, false, false, false, 0);
            CreateDriver(client: true);
            var identity = new OnlineInventoryPresentationIdentity(source.Assets.RunId, source.Assets.PlayerId,
                source.Assets.ActorId, source.Assets.InventoryId, _definition.GameplayConfigHash, 1, "p7");
            Assert.That(_driver.InstallClientInventory(identity, source, out var error), Is.True, error);
            Assert.That(_driver.TryCaptureClientInventoryDiagnostic(122, out var client), Is.True);
            Assert.That(client.ProbeSampleTick, Is.EqualTo(122));
            Assert.That(Field<bool>(client, "DriverBusyCaptured"), Is.False);
            AssertOwner(Field<object>(client, "OwnerBusy"), false, false, false, false, 0);
        }

        [Test] public void BusyLogRecordsOneNewRejectionAndPreservesTheOriginalReceiptOnReplay()
        {
            CreateDriver(); CaptureDriver(130); // 缺API时先明确RED，避免日志期待变成超时。
            Assert.That(_driver.TryQueue(CastIntent(), out _), Is.True);
            var intent = new OnlineInventoryCommandIntent { CommandId = 91, ConnectionEpoch = 1,
                ExpectedInventoryVersion = _driver.OwnedInventoryVersion, Kind = OnlineInventoryCommandKind.SelectWand,
                ItemId = Wand, TargetSlot = 0 };
            var first = _driver.ExecuteInventoryCommand(intent);
            Assert.That(first.Failure, Is.EqualTo(OnlineInventoryCommandFailure.Busy));
            Assert.That(_busyLogs.Count, Is.EqualTo(1));
            Assert.That(_busyLogs[0], Does.Contain("Actor=1").And.Contain("Command=91").And.Contain("Kind=SelectWand")
                .And.Contain("Queued=True").And.Contain("ActiveReservations=0"));
            var retry = _driver.ExecuteInventoryCommand(intent);
            Assert.That(retry.Failure, Is.EqualTo(first.Failure)); Assert.That(retry.InventoryVersion, Is.EqualTo(first.InventoryVersion));
            for (uint tick = 1; tick < 32; tick++) _driver.StepAuthority(1f / 30f, tick);
            var later = _driver.ExecuteInventoryCommand(intent);
            Assert.That(later.Failure, Is.EqualTo(first.Failure)); Assert.That(later.InventoryVersion, Is.EqualTo(first.InventoryVersion));
            Assert.That(_busyLogs.Count, Is.EqualTo(1), "原ledger重放不重新归因，也不重复记录离散拒绝。");
        }

        private object CaptureOwner()
        {
            var method = typeof(OnlineOwnedWorkspace).GetMethod("CaptureInventoryBusyDiagnostic");
            Assert.That(method, Is.Not.Null, "Owner缺少只读取值的Busy getter。"); return method.Invoke(_owner, null);
        }
        private object CaptureDriver(uint tick)
        {
            var method = typeof(OnlineCastDriver).GetMethod("TryCaptureAuthorityInventoryDiagnostic");
            Assert.That(method, Is.Not.Null, "必须从真实Driver读取门，静态DTO无法提供此值。");
            object[] args = { tick, null };
            Assert.That(method.Invoke(_driver, args), Is.True);
            Assert.That(Field<uint>(args[1], "ProbeSampleTick"), Is.EqualTo(tick)); return args[1];
        }
        private static T Field<T>(object value, string name)
        {
            var field = value.GetType().GetField(name); Assert.That(field, Is.Not.Null, name);
            return (T)field.GetValue(value);
        }
        private static void AssertOwner(object value, bool captured, bool frozen, bool items, bool pending, int reservations)
        {
            Assert.That(Field<bool>(value, "Captured"), Is.EqualTo(captured));
            Assert.That(Field<bool>(value, "RaidActionsFrozen"), Is.EqualTo(frozen));
            Assert.That(Field<bool>(value, "ItemsResourceBusy"), Is.EqualTo(items));
            Assert.That(Field<bool>(value, "CastPending"), Is.EqualTo(pending));
            Assert.That(Field<int>(value, "ActiveReservations"), Is.EqualTo(reservations));
        }
        private CastResourceRequestedEvent Request(CastResourceOperation operation, ulong sequence)
        {
            Assert.That(_owner.TryGetWandExecution(Wand, out var state), Is.True);
            return new CastResourceRequestedEvent { InventoryId = _owner.InventoryId, CallerId = "busy.diag.caller",
                SourceWandId = Wand, RequestId = sequence, UseV2 = true, Operation = operation,
                ProgramRevision = (uint)state.Program.Revision, ExecutionRevision = state.Draw.ExecutionRevision,
                ResourceVersion = state.ResourceVersion, InventoryVersion = _owner.InventoryVersion };
        }
        private OnlineOwnedAssetSnapshot Assets() => new OnlineOwnedAssetSnapshot {
            SchemaVersion = OnlineOwnedAssetSnapshot.CurrentSchemaVersion, ActorId = 1, PlayerId = "busy.diag.player",
            RunId = "busy.diag.run", InventoryId = "busy.diag.inventory", InventoryVersion = 1, CurrencyVersion = 1,
            GameplayConfigHash = _definition.GameplayConfigHash, SelectedWandInstanceId = Wand,
            Items = new[] {
                new OnlineOwnedItemSnapshot { InstanceId = Wand, ContentId = "wandtest.wand.a", Revision = 1,
                    LocationKind = (int)ItemLocationKind.EquippedWand, UseTemplateWandStats = true },
                new OnlineOwnedItemSnapshot { InstanceId = Card, ContentId = "wandtest.fire", Revision = 1,
                    LocationKind = (int)ItemLocationKind.WandSlot, ParentInstanceId = Wand, RemainingCharges = 30 }
            } };
        private void CreateDriver(bool client = false)
        {
            // 新Driver负责自己的唯一Owner；先释放SetUp的纯Owner，避免同InventoryId双订阅造成假失败。
            _owner.Dispose(); _owner = null;
            // Runner可能保留未保存InitScene；不增删Scene，且清理仅按本fixture的明确对象身份进行。
            _scene = SceneManager.GetActiveScene();
            foreach (var root in _scene.GetRootGameObjects()) _initialRoots.Add(root.GetInstanceID());
            _root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Prefabs/WizardPlayer.prefab"));
            _root.transform.position = new Vector3(15000, 16000, 17000);
            var wizard = _root.GetComponent<WizardController>(); wizard.ConfigureExecutionMode(client ? PlayerExecutionMode.Prediction : PlayerExecutionMode.Authority);
            if (wizard.SpellCaster == null) Lifecycle(wizard, "Awake");
            var health = _root.GetComponent<HealthComponent>(); if (health.CurrentHp <= 0) Lifecycle(health, "Awake");
            _driver = _root.AddComponent<OnlineCastDriver>();
            if (client) { _driver.InitializeClient(1, 1, _definition, true); return; }
            _world = new CombatSimulationWorld();
            Assert.That(_driver.InitializeAuthority(Assets(), _definition, _world, out var error), Is.True, error);
            var adapter = _root.GetComponent<OwnedWandCastAdapter>(); Lifecycle(adapter, "OnDisable"); Lifecycle(adapter, "OnEnable");
            _driver.SetConnection(1, true);
        }
        private OnlineCastIntent CastIntent() => new OnlineCastIntent { CommandId = 1, ConnectionEpoch = 1,
            ProgramRevision = 1, WandInstanceId = Wand, ClaimedOrigin = _root.transform.position, AimPoint = _root.transform.position + Vector3.forward * 20 };
        private static void Lifecycle(object target, string name) => target.GetType().GetMethod(name, Private).Invoke(target, null);
        private void OnLog(string message, string stack, LogType type)
        { if (message.StartsWith("[OnlineInventoryBusy]", StringComparison.Ordinal)) _busyLogs.Add(message); }
        private void OnBorn(CombatOutputSpawnedEvent value)
        {
            if (_world == null || value.Output == null || value.Output.scene != _scene) return;
            var projectile = value.Output.GetComponent<ProjectileBase>();
            if (projectile != null && ReferenceEquals(typeof(ProjectileBase).GetProperty("SimulationWorld", Private)
                    .GetValue(projectile), _world)) _outputs.Add(value.Output);
        }
        private void DestroyOwned(GameObject value)
        { if (value != null && !_initialRoots.Contains(value.GetInstanceID())) Object.DestroyImmediate(value); }
    }
}
