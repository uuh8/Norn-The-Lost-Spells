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
    /// <summary>
    /// 用正式 Wizard/Catalog/Caster 验证输入意图之后的 Runtime 边界：Driver 的权威 Timeline
    /// 真正生成 Projectile，再由 Caster 发出 OutputReleased，最后恢复库存命令。
    /// 这里只直接提交已认证 Cast intent，不声称覆盖 Mouse、网络传输、区域遭遇或 Physics 命中。
    /// 复用Runner当前Scene，只跟踪自有对象；本例不运行Physics/射线放置，不依赖当前地图支撑。
    /// </summary>
    public sealed partial class OnlineCastReservationReleaseTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string RunId = "character.reservation.run";
        private const float TickSeconds = 1f / 30f;
        private sealed class Actor
        {
            internal GameObject Root;
            internal OnlineCastDriver Driver;
            internal OnlineOwnedWorkspace Owner;
            internal ulong Id, InventoryCommand;
            internal string Inventory, Wand, Fire, Bag, Loose;
            internal bool Rapid;
            internal readonly List<OnlineCastReceipt> Receipts = new List<OnlineCastReceipt>();
        }

        private readonly List<Actor> _actors = new List<Actor>();
        private readonly List<CombatOutputSpawnedEvent> _outputs = new List<CombatOutputSpawnedEvent>();
        private readonly List<SpellExecutionObservedEvent> _observations = new List<SpellExecutionObservedEvent>();
        private Scene _scene;
        private readonly HashSet<int> _initialRoots = new HashSet<int>();
        private OnlineCombatDefinition _definition;
        private OnlineDungeonDefinition _dungeon;
        private CombatSimulationWorld _world;
        private OnlineGroundItemAuthority _ground;
        private uint _tick;
        private ulong _snapshotSequence;

        [SetUp]
        public void CreateIsolatedAuthorityWorld()
        {
            _tick = 0; _snapshotSequence = 0;
            _definition = AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>("Assets/_Project/Resources/Online/CombatDefinition.asset");
            _dungeon = AssetDatabase.LoadAssetAtPath<OnlineDungeonDefinition>("Assets/_Project/ScriptableObjects/World/Online/P7.asset");
            Assert.That(_definition, Is.Not.Null); Assert.That(_dungeon, Is.Not.Null);
            Assert.That(_dungeon.FixedSolidCount, Is.EqualTo(1));
            Assert.That(_dungeon.GetFixedSolid(0).ContentId, Is.EqualTo("containertest.ice"));
            Assert.That(_dungeon.GetFixedSolid(0).Quantity, Is.EqualTo(256ul));
            // Runner可能使用尚未保存的InitScene；不创建/关闭/保存Scene，只记录原有root身份。
            _scene = SceneManager.GetActiveScene(); _initialRoots.Clear();
            foreach (var root in _scene.GetRootGameObjects()) _initialRoots.Add(root.GetInstanceID());
            _world = new CombatSimulationWorld();
            _ground = new OnlineGroundItemAuthority(_definition, _scene);
            // 用原P7固定来源创建真实模型/Store，不能凭空往玩家包里放一块冰冒充 Pickup。
            Assert.That(_ground.ConfigureDungeonLoot(_dungeon, RunId, out var error), Is.True, error);
            EventBus<CombatOutputSpawnedEvent>.Subscribe(OnBorn);
            EventBus<SpellExecutionObservedEvent>.Subscribe(OnObserved);
        }

        [TearDown]
        public void ReleaseOnlyOwnedActorsAndScene()
        {
            EventBus<CombatOutputSpawnedEvent>.Unsubscribe(OnBorn);
            EventBus<SpellExecutionObservedEvent>.Unsubscribe(OnObserved);
            foreach (var actor in _actors)
            {
                if (actor.Driver != null)
                {
                    actor.Driver.ReceiptProduced -= actor.Receipts.Add;
                    // 沿现有 EditMode fixture 补成对生命周期；不清全局 EventBus/Projectile registry。
                    InvokeLifecycle(actor.Driver, "OnDisable");
                    var adapter = actor.Root.GetComponent<OwnedWandCastAdapter>();
                    if (adapter != null) InvokeLifecycle(adapter, "OnDisable");
                    InvokeLifecycle(actor.Driver, "OnDestroy");
                }
            }
            _ground?.Dispose(); _ground = null;
            // 只销毁明确由本fixture持有的Actor和该CombatWorld发出的Projectile，保留Runner/Menu原root。
            foreach (var output in _outputs) DestroyOwned(output.Output);
            foreach (var actor in _actors) DestroyOwned(actor.Root);
            _actors.Clear(); _outputs.Clear(); _observations.Clear(); _world = null;
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void ActualCasterReleasesRapidReservationsAndAllowsMoveAndIcePickup(bool secondOwner, bool nearAreaTwoIce)
        {
            // 两个位置均读正式Authoring；第二组只验证坐标/出生链，不替代整张P7遭遇验收。
            Vector3 origin = nearAreaTwoIce ? _dungeon.GetFixedSolid(0).Position + Vector3.right : _dungeon.PlayerSpawn(0);
            var rapid = CreateActor(1, true, origin);
            Actor ordinary = secondOwner ? CreateActor(2, false, origin + Vector3.right * 4) : null;

            // 用正式移动命令建立当前实际的 Revision2：4Damage在0..3，Fire从4移到5，留下空槽4。
            var edit = InventoryIntent(rapid, OnlineInventoryCommandKind.PlaceCard, rapid.Fire);
            edit.SourceWandId = edit.TargetWandId = rapid.Wand;
            edit.SourceProgramRevision = edit.TargetProgramRevision = 1; edit.TargetSlot = 5;
            Assert.That(rapid.Driver.ExecuteInventoryCommand(edit).Success, Is.True);
            Assert.That(State(rapid).Program.Revision, Is.EqualTo(2));
            StepAll(1f); // Reprogram的合法reload自然结束，不更改正式速率或资源。

            uint ordinaryVersion = ordinary?.Owner.InventoryVersion ?? 0;
            Queue(rapid);
            Assert.That(MoveLoose(rapid).Failure, Is.EqualTo(OnlineInventoryCommandFailure.Busy),
                "真实Driver队列是Busy正控制，不能跳过动作门只测试Owner。");
            if (ordinary != null)
            {
                Assert.That(ordinary.Owner.V2CastResourcesReadOnly.ActiveReservations, Is.Zero);
                Assert.That(Charges(ordinary, ordinary.Fire), Is.EqualTo(29));
                Assert.That(ordinary.Owner.InventoryVersion, Is.EqualTo(ordinaryVersion));
                Queue(ordinary);
                Assert.That(MoveLoose(ordinary).Failure, Is.EqualTo(OnlineInventoryCommandFailure.Busy));
            }

            // Timeline当前以正式release时间结束。固定有界预算从两种姿态的配置上限推导，不用sleep。
            float release = Mathf.Max(_definition.ReleaseSeconds(false), _definition.ReleaseSeconds(true));
            Assert.That(release, Is.GreaterThan(0).And.LessThan(10));
            int steps = Mathf.CeilToInt(release / TickSeconds) + 2;
            for (int i = 0; i < steps; i++) StepAll(TickSeconds);

            AssertRealRelease(rapid, 12, 60);
            if (ordinary != null) AssertRealRelease(ordinary, 28, 80);
            Assert.That(_outputs.Count, Is.EqualTo(secondOwner ? 2 : 1), "每个Owner仅产生一次真实输出。");
            Assert.That(_observations.Count, Is.EqualTo(secondOwner ? 2 : 1), "观察来自生产Caster，夹具不手动Publish。");

            // 从Driver再走一次独立移动，避免仅检查ActiveReservations计数而漏掉实际授权门。
            Assert.That(MoveLoose(rapid).Success, Is.True);
            AssertMovedLoose(rapid);
            if (ordinary != null) { Assert.That(MoveLoose(ordinary).Success, Is.True); AssertMovedLoose(ordinary); }

            var groundBefore = _ground.Capture();
            Assert.That(groundBefore.Items.Length, Is.EqualTo(1));
            var ice = groundBefore.Items[0];
            Assert.That(ice.ContentId, Is.EqualTo("containertest.ice"));
            uint otherVersion = ordinary?.Owner.InventoryVersion ?? 0;
            rapid.Root.transform.position = ice.Position + Vector3.right;
            var pickup = InventoryIntent(rapid, OnlineInventoryCommandKind.PickupContainer, ice.InstanceId);
            pickup.WorldOwnerId = ice.WorldOwnerId; pickup.AutoPlace = nearAreaTwoIce;
            if (!pickup.AutoPlace) pickup.TargetBackpackId = rapid.Bag;
            var result = rapid.Driver.ExecuteInventoryCommand(pickup);
            Assert.That(result.Success, Is.True, result.Failure.ToString());
            Assert.That(result.Transferred, Is.EqualTo(256ul));
            Assert.That(_ground.Capture().Items, Is.Empty);
            Assert.That(_ground.Version, Is.GreaterThan(groundBefore.Version));
            var carried = Find(Snapshot(rapid), ice.InstanceId);
            Assert.That(carried.LocationKind, Is.EqualTo((int)ItemLocationKind.Backpack));
            Assert.That(carried.ParentInstanceId, Is.EqualTo(rapid.Bag));
            Assert.That(carried.MaterialState.MaterialKey, Is.EqualTo(5));
            Assert.That(carried.MaterialState.Quantity, Is.EqualTo(256ul));
            if (ordinary != null)
            {
                Assert.That(ordinary.Owner.InventoryVersion, Is.EqualTo(otherVersion), "另一Owner不能接收冰或被改资源版本。");
                Assert.That(ordinary.Owner.TryGetItem(ice.InstanceId, out _), Is.False);
            }
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void GroundStoragePickupDuringQueuedOrStartedCastKeepsTheOriginalCast(bool startTimeline, bool automatic)
        {
            var ice = _ground.Capture().Items[0];
            var actor = CreateActor(1, false, ice.Position + Vector3.right);
            Queue(actor);
            if (startTimeline)
            {
                StepAll(TickSeconds);
                Assert.That(_outputs.Count, Is.Zero, "本控制应停在真正起手、尚未发射的时刻。");
            }
            Assert.That(MoveLoose(actor).Failure, Is.EqualTo(OnlineInventoryCommandFailure.Busy));
            var pickup = InventoryIntent(actor, OnlineInventoryCommandKind.PickupContainer, ice.InstanceId);
            pickup.WorldOwnerId = ice.WorldOwnerId; pickup.AutoPlace = automatic;
            if (!automatic) pickup.TargetBackpackId = actor.Bag;
            var receipt = actor.Driver.ExecuteInventoryCommand(pickup);
            Assert.That(receipt.Success, Is.True, "动作门误锁地面收纳：" + receipt.Failure);
            Assert.That(_ground.Capture().Items, Is.Empty);
            Assert.That(actor.Owner.TryGetItem(ice.InstanceId, out var collected), Is.True);
            Assert.That(collected.Material.Quantity, Is.EqualTo(256ul));
            float release = Mathf.Max(_definition.ReleaseSeconds(false), _definition.ReleaseSeconds(true));
            for (int i = 0; i < Mathf.CeilToInt(release / TickSeconds) + 3; i++) StepAll(TickSeconds);
            AssertRealRelease(actor, 28, 80);
            Assert.That(_outputs.Count, Is.EqualTo(1)); Assert.That(_observations.Count, Is.EqualTo(1));
        }

        private Actor CreateActor(ulong id, bool rapid, Vector3 position)
        {
            string prefix = "character.owner." + id + ".";
            var actor = new Actor { Id = id, Rapid = rapid, Inventory = prefix + "inventory", Wand = prefix + "wand",
                Fire = prefix + "fire", Bag = prefix + "bag", Loose = prefix + "loose" };
            _actors.Add(actor); // 中途装配失败时也由TearDown精确清理该实例。
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Prefabs/WizardPlayer.prefab");
            Assert.That(prefab, Is.Not.Null);
            actor.Root = Object.Instantiate(prefab); actor.Root.name = "Reservation Actor " + id;
            SceneManager.MoveGameObjectToScene(actor.Root, _scene); actor.Root.transform.position = position;
            var wizard = actor.Root.GetComponent<WizardController>();
            wizard.ConfigureExecutionMode(PlayerExecutionMode.Authority);
            if (wizard.SpellCaster == null) InvokeLifecycle(wizard, "Awake");
            var health = actor.Root.GetComponent<HealthComponent>();
            if (health.CurrentHp <= 0) InvokeLifecycle(health, "Awake");

            var items = new List<OnlineOwnedItemSnapshot> {
                new OnlineOwnedItemSnapshot { InstanceId = actor.Wand, ContentId = rapid ? "norn.v2.wand.0" : "wandtest.wand.a",
                    Revision = 1, LocationKind = (int)ItemLocationKind.EquippedWand, UseTemplateWandStats = true },
                new OnlineOwnedItemSnapshot { InstanceId = actor.Bag, ContentId = "raidtest.starterbag", Revision = 1,
                    LocationKind = (int)ItemLocationKind.EquippedBackpack },
                new OnlineOwnedItemSnapshot { InstanceId = actor.Loose, ContentId = "wandtest.fire", Revision = 1,
                    LocationKind = (int)ItemLocationKind.SpellStorage, RemainingCharges = 9 },
                Card(actor.Fire, "wandtest.fire", actor.Wand, rapid ? 4 : 0, rapid ? 13 : 29)
            };
            if (rapid) for (int i = 0; i < 4; i++) items.Add(Card(prefix + "damage." + i, "norn.v2.damage", actor.Wand, i, 8));
            var assets = new OnlineOwnedAssetSnapshot { SchemaVersion = OnlineOwnedAssetSnapshot.CurrentSchemaVersion,
                RunId = RunId, PlayerId = prefix + "player", ActorId = id, InventoryId = actor.Inventory,
                InventoryVersion = 1, CurrencyVersion = 1, GameplayConfigHash = _definition.GameplayConfigHash,
                SelectedWandInstanceId = actor.Wand, Items = items.ToArray() };
            actor.Driver = actor.Root.AddComponent<OnlineCastDriver>();
            Assert.That(actor.Driver.InitializeAuthority(assets, _definition, _world, out var error), Is.True, error);
            var adapter = actor.Root.GetComponent<OwnedWandCastAdapter>();
            InvokeLifecycle(adapter, "OnDisable"); InvokeLifecycle(adapter, "OnEnable");
            actor.Driver.SetConnection(1, true);
            Assert.That(actor.Driver.ConfigureGroundWorld(_ground), Is.True);
            Assert.That(actor.Driver.ConfigureContainerWorld("character.reservation.material-world"), Is.True);
            actor.Owner = (OnlineOwnedWorkspace)typeof(OnlineCastDriver).GetField("_workspace", PrivateInstance).GetValue(actor.Driver);
            Assert.That(actor.Owner, Is.Not.Null);
            actor.Driver.ReceiptProduced += actor.Receipts.Add;
            return actor;
        }

        private static OnlineOwnedItemSnapshot Card(string id, string content, string wand, int slot, int charges)
            => new OnlineOwnedItemSnapshot { InstanceId = id, ContentId = content, Revision = 1,
                LocationKind = (int)ItemLocationKind.WandSlot, ParentInstanceId = wand, Slot = slot, RemainingCharges = charges };
        private void StepAll(float seconds)
        {
            _world.BeginTick(++_tick);
            foreach (var actor in _actors) actor.Driver.StepAuthority(seconds, _tick);
            // 本例不推进Projectile寿命/Physics：票据应在出生时结束，不借销毁/取消帮产品解锁。
        }
        private static WandExecutionSnapshot State(Actor actor)
        { Assert.That(actor.Owner.TryGetWandExecution(actor.Wand, out var state), Is.True); return state; }
        private static int Charges(Actor actor, string id)
        { Assert.That(actor.Owner.TryGetItem(id, out var item), Is.True); return item.RemainingCharges; }
        private static void Queue(Actor actor)
        {
            var intent = new OnlineCastIntent { CommandId = 1, ConnectionEpoch = 1, WandInstanceId = actor.Wand,
                ProgramRevision = (uint)State(actor).Program.Revision, ClaimedOrigin = actor.Root.transform.position,
                AimPoint = actor.Root.transform.position + Vector3.forward * 20 };
            Assert.That(actor.Driver.TryQueue(intent, out var receipt), Is.True, receipt.Failure.ToString());
            Assert.That(receipt.Result, Is.EqualTo(OnlineCastResult.Queued));
        }
        private OnlineInventoryCommandIntent InventoryIntent(Actor actor, OnlineInventoryCommandKind kind, string item)
            => new OnlineInventoryCommandIntent { CommandId = ++actor.InventoryCommand, ConnectionEpoch = 1,
                ExpectedInventoryVersion = actor.Driver.OwnedInventoryVersion, Kind = kind, ItemId = item };
        private OnlineInventoryCommandReceipt MoveLoose(Actor actor)
        {
            var intent = InventoryIntent(actor, OnlineInventoryCommandKind.MoveOwned, actor.Loose);
            intent.TargetLocationKind = (int)ItemLocationKind.SpellStorage; intent.TargetSlot = 1;
            return actor.Driver.ExecuteInventoryCommand(intent);
        }
        private OnlineInventoryPresentationSnapshot Snapshot(Actor actor)
        {
            Assert.That(actor.Driver.TryCaptureInventory(1, ++_snapshotSequence, Math.Max(1u, _tick), out var snapshot), Is.True);
            return snapshot;
        }
        private static OnlineOwnedItemSnapshot Find(OnlineInventoryPresentationSnapshot snapshot, string id)
        {
            var item = Array.Find(snapshot.Assets.Items, value => value.InstanceId == id);
            Assert.That(item, Is.Not.Null, id); return item;
        }
        private void AssertMovedLoose(Actor actor)
        {
            var loose = Find(Snapshot(actor), actor.Loose);
            Assert.That(loose.LocationKind, Is.EqualTo((int)ItemLocationKind.SpellStorage));
            Assert.That(loose.Slot, Is.EqualTo(1)); Assert.That(loose.RemainingCharges, Is.EqualTo(9));
        }
        private void AssertRealRelease(Actor actor, int remaining, float manaAfter)
        {
            Assert.That(actor.Receipts.Count, Is.EqualTo(2), "一次Queued与一次Released，不应被另一Owner的同步收据覆盖。");
            Assert.That(actor.Receipts[1].Result, Is.EqualTo(OnlineCastResult.Released));
            Assert.That(actor.Receipts[1].Failure, Is.EqualTo(OnlineCastFailure.None));
            Assert.That(actor.Receipts[1].ActorId, Is.EqualTo(actor.Id));
            Assert.That(Charges(actor, actor.Fire), Is.EqualTo(remaining));
            Assert.That(actor.Owner.TryGetLatestReplay(actor.Wand, out var replay), Is.True);
            Assert.That(replay.Graph.Scopes.Count, Is.EqualTo(1));
            Assert.That(replay.Graph.Outputs.Count, Is.EqualTo(1));
            Assert.That(replay.Graph.Outputs[0].SourceInstanceId, Is.EqualTo(actor.Fire));
            Assert.That(replay.Graph.Program.Instructions[replay.Graph.Outputs[0].SourceCardIndex].SourceSlot, Is.EqualTo(actor.Rapid ? 5 : 0));
            Assert.That(replay.Summary.ManaAfter, Is.EqualTo(manaAfter));
            Assert.That(replay.ReleasedOutputs[0], Is.True);
            Assert.That(replay.WorldStatus, Is.EqualTo(WandWorldExecutionStatus.Released));
            Assert.That(actor.Owner.V2CastResourcesReadOnly.ActiveReservations, Is.Zero,
                "真实Caster完成OutputReleased后立即释放普通root；不等待Projectile销毁。");
            var outputs = _outputs.FindAll(value => value.ActorId == actor.Id);
            Assert.That(outputs.Count, Is.EqualTo(1));
            Assert.That(outputs[0].CommandId, Is.EqualTo(1ul));
            Assert.That(outputs[0].Output, Is.Not.Null);
            Assert.That(outputs[0].Output.scene, Is.EqualTo(_scene));
            Assert.That(outputs[0].Output.GetComponent<ProjectileBase>().ExecutionMode, Is.EqualTo(CombatExecutionMode.Authority));
            var observations = _observations.FindAll(value => value.InventoryId == actor.Inventory);
            Assert.That(observations.Count, Is.EqualTo(1));
            Assert.That(observations[0].TicketId, Is.EqualTo(replay.TicketId));
            Assert.That(observations[0].LayerId, Is.Zero); Assert.That(observations[0].OutputId, Is.Zero);
            if (actor.Rapid)
                for (int i = 0; i < 4; i++) Assert.That(Charges(actor, "character.owner.1.damage." + i), Is.EqualTo(7));
        }
        private void OnBorn(CombatOutputSpawnedEvent value)
        {
            if (value.Output == null || value.Output.scene != _scene) return;
            var projectile = value.Output.GetComponent<ProjectileBase>();
            if (projectile != null && ReferenceEquals(typeof(ProjectileBase).GetProperty("SimulationWorld", PrivateInstance)
                    .GetValue(projectile), _world)) _outputs.Add(value);
        }
        private void DestroyOwned(GameObject value)
        {
            if (value != null && !_initialRoots.Contains(value.GetInstanceID())) Object.DestroyImmediate(value);
        }
        private void OnObserved(SpellExecutionObservedEvent value)
        { foreach (var actor in _actors) if (value.InventoryId == actor.Inventory) { _observations.Add(value); return; } }
        private static void InvokeLifecycle(object target, string method)
        {
            var member = target.GetType().GetMethod(method, PrivateInstance);
            Assert.That(member, Is.Not.Null, target.GetType().Name + "." + method); member.Invoke(target, null);
        }
    }
}
