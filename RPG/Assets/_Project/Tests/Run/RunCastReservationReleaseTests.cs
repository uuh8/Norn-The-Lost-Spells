using System;
using System.Collections.Generic;
using Game.Core;
using Game.Skills;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Run.Tests
{
    /// <summary>
    /// 用正式 Catalog、Online Owner、ProgramMachine 和 GroundStore 复现施法后的库存门。
    /// OutputReleased 经正式 EventBus 进入 Owner，区别资源 Complete 与世界输出已观察；
    /// 本 fixture 不生成 Projectile，不声称覆盖 SpellCaster/Physics 或 Native 输入。
    /// </summary>
    public sealed class RunCastReservationReleaseTests
    {
        private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private const string WandId = "reservation.wand", BagId = "reservation.bag";
        private const string LooseId = "reservation.loose", Caller = "reservation.caster";
        private const string Rapid = "norn.v2.wand.0", Damage = "norn.v2.damage";
        private const string Fire = "wandtest.fire", Trigger = "norn.v2.termination";
        private ItemCatalogIndex _catalog;
        private OnlineOwnedWorkspace _owner;
        private OnlineGroundItemStore _ground;
        private string _iceId, _iceWorldOwner;
        private ulong _castRequest, _inventoryCommand;
        private double _observationTime;

        [SetUp]
        public void ReadProductionCatalog()
        {
            _castRequest = 0; _inventoryCommand = 0; _observationTime = 0;
            var catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>(
                "Assets/_Project/ScriptableObjects/World/Hub/Inventory/Catalog.asset");
            Assert.That(catalog, Is.Not.Null);
            Assert.That(catalog.TryCreateIndex(out _catalog, out var error), Is.True, error);
            // 不克隆/修改 SO，也不把未配置的空 GameObject 冒充可执行法术。
            foreach (string content in new[] { Rapid, Damage, Fire, Trigger, "raidtest.starterbag", "containertest.ice" })
                Assert.That(_catalog.TryGet(content, out _), Is.True, "正式 Catalog 缺少 " + content);
            _ground = new OnlineGroundItemStore(_catalog);
        }

        [TearDown]
        public void DisposeOwnedSubscriptionsAndReservations()
        {
            // Dispose 只释放本 fixture Owner 的订阅与票据，不清空其他实例的全局 EventBus。
            _owner?.Dispose(); _owner = null;
            _ground?.Dispose(); _ground = null;
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ObservedRapidFourDamageGapFireAtRevisionTwoReleasesInventoryAndFixedIce(bool autoPickup)
        {
            var cards = new List<OnlineOwnedItemSnapshot>();
            for (int i = 0; i < 4; i++) cards.Add(Card("damage." + i, Damage, i, 8));
            cards.Add(Card("fire", Fire, 4, 13));
            CreateOwner(cards);
            Assert.That(State().Mana, Is.EqualTo(120));
            Assert.That(State().Program.Revision, Is.EqualTo(1));

            // 用真实 PlaceCard 将 Fire 从 4 移到 5，得到 Revision 2 与实际空槽 4，
            // 不反射写 ProgramRevision，也不以全局 InventoryVersion 代替程序修订。
            var edit = Intent(OnlineInventoryCommandKind.PlaceCard, "fire");
            edit.SourceWandId = WandId; edit.TargetWandId = WandId;
            edit.SourceProgramRevision = edit.TargetProgramRevision = 1; edit.TargetSlot = 5;
            Assert.That(_owner.ExecuteInventoryCommand(edit).Success, Is.True);
            Assert.That(State().Program.Revision, Is.EqualTo(2));
            Assert.That(_owner.OwnershipStore.TryGetLocation("fire", out var fireLocation), Is.True);
            Assert.That(fireLocation.SlotIndex, Is.EqualTo(5));
            // 正式重编程会启动 reload，先由 Authority 时钟自然走完，不能将 Cooldown 前置失败当泄漏。
            _owner.StepAuthority(1, 1);

            var replay = CommitRoot();
            Assert.That(replay.Graph.Program.Revision, Is.EqualTo(2));
            Assert.That(replay.Graph.Scopes.Count, Is.EqualTo(1), "普通 Fire 没有 Trigger 子层。");
            Assert.That(replay.Graph.Outputs.Count, Is.EqualTo(1));
            Assert.That(replay.Graph.Outputs[0].SourceInstanceId, Is.EqualTo("fire"));
            Assert.That(replay.Graph.Program.Instructions[replay.Graph.Outputs[0].SourceCardIndex].SourceSlot, Is.EqualTo(5));
            Assert.That(State().Mana, Is.EqualTo(60), "四张正式 Damage 各 10，Fire 20；空槽没有额外费用。");
            for (int i = 0; i < 4; i++)
            {
                Assert.That(_owner.TryGetItem("damage." + i, out var modifier), Is.True);
                Assert.That(modifier.RemainingCharges, Is.EqualTo(7), "资格只在 Raid 入局扣一次。");
            }
            Assert.That(_owner.TryGetItem("fire", out var fire), Is.True);
            Assert.That(fire.RemainingCharges, Is.EqualTo(12));
            AssertInventoryBusy();
            ReleaseLayer(replay, 0);
            AssertInventoryOpen(autoPickup);
        }

        [TestCase(0, TestName = "LastTriggerWithoutFollowingCardReleasesReservationAfterRootOutput")]
        [TestCase(1, TestName = "TriggerFollowedOnlyByModifierReleasesReservationAfterRootOutput")]
        [TestCase(2, TestName = "TriggerWhoseChildCannotAffordManaReleasesReservationAfterRootOutput")]
        public void EmptyTriggerChildDoesNotKeepInventoryBusy(int variant)
        {
            var cards = new List<OnlineOwnedItemSnapshot> { Card("trigger", Trigger, 0, 8) };
            if (variant == 1) cards.Add(Card("modifier", Damage, 1, 8));
            if (variant == 2) cards.Add(Card("child", Fire, 1, 13));
            CreateOwner(cards, childCannotAffordMana: variant == 2);
            var replay = CommitRoot();
            Assert.That(replay.Graph.Scopes.Count, Is.EqualTo(2), "保留真实 Machine 的空子层及其稳定 ScopeId。");
            Assert.That(replay.Graph.Scopes[1].OutputIds.Count, Is.Zero);
            Assert.That(replay.Graph.Outputs.Count, Is.EqualTo(1));
            Assert.That(replay.Graph.Outputs[0].PayloadScopeId, Is.EqualTo(-1));
            Assert.That(_owner.V2CastResourcesReadOnly.TryGetLayer(replay.TicketId, 0, out var root), Is.True);
            Assert.That(root.GetChild(0), Is.EqualTo(-1), "Runtime 没有能触发/Abandon 此空层的 child 引用。");
            if (variant == 2)
            {
                Assert.That(State().Mana, Is.Zero);
                Assert.That(_owner.TryGetItem("child", out var child), Is.True);
                Assert.That(child.RemainingCharges, Is.EqualTo(13), "未发射的子 Fire 不扣次数。");
            }
            ReleaseLayer(replay, 0);
            // 明确覆盖停手很久；Authority Tick 不能靠凭空清除有效票据解决此泄漏。
            _owner.StepAuthority(60, 1);
            AssertInventoryOpen(autoPickup: true);
        }

        [Test]
        public void RootCommittedWithoutOutputObservationRemainsBusyUntilMatchingObservation()
        {
            CreateOwner(new[] { Card("fire", Fire, 0, 13) });
            var replay = CommitRoot();
            Assert.That(replay.WorldStatus, Is.EqualTo(WandWorldExecutionStatus.NotObserved));
            _owner.StepAuthority(60, 1);
            AssertInventoryBusy();
            ReleaseLayer(replay, 0);
            AssertInventoryOpen(autoPickup: true);
        }

        [Test]
        public void ExecutableChildRemainsBusyThroughUnattemptedPreparedAndUnobservedCommittedStates()
        {
            CreateOwner(new[] { Card("trigger", Trigger, 0, 8), Card("child", Fire, 1, 13) });
            var replay = CommitRoot();
            Assert.That(replay.Graph.Outputs.Count, Is.EqualTo(2));
            Assert.That(replay.Graph.Outputs[0].PayloadScopeId, Is.EqualTo(1));
            ReleaseLayer(replay, 0);
            Assert.That(replay.GetLayerStatus(1), Is.EqualTo(WandLayerResourceStatus.Unattempted));
            _owner.StepAuthority(60, 1);
            AssertInventoryBusy();

            Assert.That(_owner.Execute(Request(CastResourceOperation.PrepareLayer, replay, 1)).Success, Is.True);
            Assert.That(replay.GetLayerStatus(1), Is.EqualTo(WandLayerResourceStatus.Prepared));
            AssertInventoryBusy();
            Assert.That(_owner.Execute(Request(CastResourceOperation.Complete, replay, 1)).Success, Is.True);
            Assert.That(replay.GetLayerStatus(1), Is.EqualTo(WandLayerResourceStatus.Committed));
            Assert.That(replay.ReleasedOutputs[1], Is.False);
            AssertInventoryBusy();
            ReleaseLayer(replay, 1);
            AssertInventoryOpen(autoPickup: false);
        }

        [Test]
        public void AbandonExecutableChildAfterObservedRootReleasesReservationWithoutChildOutput()
        {
            CreateOwner(new[] { Card("trigger", Trigger, 0, 8), Card("child", Fire, 1, 13) });
            var replay = CommitRoot();
            ReleaseLayer(replay, 0);
            AssertInventoryBusy();
            Assert.That(_owner.Execute(Request(CastResourceOperation.Abandon, replay, 1)).Success, Is.True);
            Assert.That(replay.GetLayerStatus(1), Is.EqualTo(WandLayerResourceStatus.Cancelled));
            Assert.That(replay.ReleasedOutputs[1], Is.False);
            AssertInventoryOpen(autoPickup: true);
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void GroundStoragePickupPreservesACommittedCastWithALiveChild(bool automatic, bool material)
        {
            CreateOwner(new[] { Card("trigger", Trigger, 0, 8), Card("child", Fire, 1, 13) });
            string groundId = _iceId, worldOwner = _iceWorldOwner;
            if (!material)
            {
                groundId = Guid.NewGuid().ToString("N");
                Assert.That(_ground.TryRegisterPlannedLoot("new.ordinary.spell", new RuntimeItemState(groundId, Fire, 17),
                    Vector3.forward, Quaternion.identity, out _), Is.EqualTo(OnlineInventoryCommandFailure.None));
                Assert.That(_ground.TryGet(groundId, out var entry), Is.True); worldOwner = entry.WorldOwnerId;
            }
            var replay = CommitRoot(); ReleaseLayer(replay, 0);
            _owner.StepAuthority(60, 1);
            Assert.That(replay.GetLayerStatus(1), Is.EqualTo(WandLayerResourceStatus.Unattempted));
            Assert.That(_owner.V2CastResourcesReadOnly.ActiveReservations, Is.EqualTo(1));
            var before = State();
            var pickup = Intent(material ? OnlineInventoryCommandKind.PickupContainer : OnlineInventoryCommandKind.PickupItem, groundId);
            pickup.WorldOwnerId = worldOwner; pickup.AutoPlace = automatic;
            if (!automatic)
            {
                pickup.TargetBackpackId = BagId;
                pickup.TargetLocationKind = material ? 0 : (int)ItemLocationKind.Backpack;
            }
            // 等待命中/寿命的旧 payload 已支付并冻结；拾取另一件地面实物不应取消它，也不应等它死亡。
            var receipt = _owner.ExecuteInventoryCommand(pickup, new OnlineContainerAuthorityContext
            { CanAdvance = true, WorldId = _ground.WorldId, ActorPosition = Vector3.zero });
            Assert.That(receipt.Success, Is.True, "被动拾取被旧施法票据阻断：" + receipt.Failure);
            Assert.That(_owner.ExecuteInventoryCommand(pickup).InventoryVersion, Is.EqualTo(receipt.InventoryVersion), "重试只重放原收据。");
            Assert.That(_owner.OwnershipStore.TryGetLocation(groundId, out var location), Is.True);
            Assert.That(location.Kind, Is.EqualTo(!material && automatic ? ItemLocationKind.SpellStorage : ItemLocationKind.Backpack));
            Assert.That(_ground.TryGet(groundId, out _), Is.False);
            Assert.That(State().Mana, Is.EqualTo(before.Mana));
            Assert.That(State().Program.Revision, Is.EqualTo(before.Program.Revision));
            Assert.That(_owner.V2CastResourcesReadOnly.ActiveReservations, Is.EqualTo(1), "拾取不能假装取消旧输出。");
            Assert.That(MoveIndependentCard().Failure, Is.EqualTo(OnlineInventoryCommandFailure.Busy), "程序/旧库存编辑仍服从预约。");
            Assert.That(_owner.Execute(Request(CastResourceOperation.PrepareLayer, replay, 1)).Success, Is.True);
            Assert.That(_owner.Execute(Request(CastResourceOperation.Complete, replay, 1)).Success, Is.True);
            ReleaseLayer(replay, 1);
            Assert.That(_owner.V2CastResourcesReadOnly.ActiveReservations, Is.Zero, "旧 payload 必须继续完成，不能被拾取弄失效。");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PreparedRootOrChildStillRejectsGroundStoragePickup(bool childLayer)
        {
            CreateOwner(new[] { Card("trigger", Trigger, 0, 8), Card("child", Fire, 1, 13) });
            WandExecutionReplay replay = null;
            if (childLayer) { replay = CommitRoot(); ReleaseLayer(replay, 0); }
            var prepare = _owner.Execute(Request(childLayer ? CastResourceOperation.PrepareLayer : CastResourceOperation.PrepareRoot,
                replay, childLayer ? 1 : 0));
            Assert.That(prepare.Success, Is.True);
            uint version = _owner.InventoryVersion, groundVersion = _ground.Version;
            Assert.That(PickupFixedIce(true).Failure, Is.EqualTo(OnlineInventoryCommandFailure.Busy));
            Assert.That(_owner.InventoryVersion, Is.EqualTo(version)); Assert.That(_ground.Version, Is.EqualTo(groundVersion));
            var cancel = Request(CastResourceOperation.Cancel, replay, childLayer ? 1 : 0); cancel.TicketId = prepare.TicketId;
            Assert.That(_owner.Execute(cancel).Success, Is.True);
        }

        [TestCase(ItemLocationKind.WandSlot)]
        [TestCase(ItemLocationKind.EquippedWand)]
        [TestCase(ItemLocationKind.EquippedBackpack)]
        [TestCase(ItemLocationKind.EquippedTool)]
        public void LiveChildDoesNotAllowGroundPickupToRewriteEquipment(ItemLocationKind target)
        {
            CreateOwner(new[] { Card("trigger", Trigger, 0, 8), Card("child", Fire, 1, 13) });
            string id = Guid.NewGuid().ToString("N");
            Assert.That(_ground.TryRegisterPlannedLoot("ground.spell.equipment.guard", new RuntimeItemState(id, Fire, 17),
                Vector3.forward, Quaternion.identity, out _), Is.EqualTo(OnlineInventoryCommandFailure.None));
            Assert.That(_ground.TryGet(id, out var ground), Is.True);
            var replay = CommitRoot(); ReleaseLayer(replay, 0);
            var intent = Intent(OnlineInventoryCommandKind.PickupItem, id);
            intent.WorldOwnerId = ground.WorldOwnerId; intent.TargetLocationKind = (int)target;
            intent.TargetBackpackId = BagId; intent.TargetWandId = WandId;
            intent.TargetProgramRevision = checked((uint)State().Program.Revision);
            uint version = _owner.InventoryVersion;
            var receipt = _owner.ExecuteInventoryCommand(intent, new OnlineContainerAuthorityContext
            { CanAdvance = true, WorldId = _ground.WorldId, ActorPosition = Vector3.zero });
            Assert.That(receipt.Failure, Is.EqualTo(OnlineInventoryCommandFailure.Busy));
            Assert.That(_owner.InventoryVersion, Is.EqualTo(version));
            Assert.That(_ground.TryGet(id, out _), Is.True);
        }

        [Test]
        public void AutoEquippingGroundBackpackDoesNotUseTheStoragePickupException()
        {
            CreateOwner(new[] { Card("trigger", Trigger, 0, 8), Card("child", Fire, 1, 13) });
            string id = Guid.NewGuid().ToString("N");
            Assert.That(_ground.TryRegisterPlannedLoot("ground.backpack.equipment.guard", new RuntimeItemState(id, "raidtest.starterbag", 0),
                Vector3.forward, Quaternion.identity, out _), Is.EqualTo(OnlineInventoryCommandFailure.None));
            Assert.That(_ground.TryGet(id, out var ground), Is.True);
            var replay = CommitRoot(); ReleaseLayer(replay, 0);
            var intent = Intent(OnlineInventoryCommandKind.PickupItem, id);
            intent.WorldOwnerId = ground.WorldOwnerId; intent.AutoPlace = true;
            Assert.That(_owner.ExecuteInventoryCommand(intent, new OnlineContainerAuthorityContext
            { CanAdvance = true, WorldId = _ground.WorldId, ActorPosition = Vector3.zero }).Failure,
                Is.EqualTo(OnlineInventoryCommandFailure.Busy));
        }

        [Test]
        public void PreparedBusyReceiptStaysBusyAfterCancelButANewPickupCanSucceed()
        {
            CreateOwner(new[] { Card("fire", Fire, 0, 13) });
            var prepare = _owner.Execute(Request(CastResourceOperation.PrepareRoot)); Assert.That(prepare.Success, Is.True);
            var pickup = Intent(OnlineInventoryCommandKind.PickupContainer, _iceId);
            pickup.WorldOwnerId = _iceWorldOwner; pickup.AutoPlace = true;
            var context = new OnlineContainerAuthorityContext { CanAdvance = true, WorldId = _ground.WorldId, ActorPosition = Vector3.zero };
            var busy = _owner.ExecuteInventoryCommand(pickup, context);
            Assert.That(busy.Failure, Is.EqualTo(OnlineInventoryCommandFailure.Busy));
            var cancel = Request(CastResourceOperation.Cancel); cancel.TicketId = prepare.TicketId;
            Assert.That(_owner.Execute(cancel).Success, Is.True);
            Assert.That(_owner.ExecuteInventoryCommand(pickup, context).Failure, Is.EqualTo(OnlineInventoryCommandFailure.Busy));
            Assert.That(PickupFixedIce(true).Success, Is.True);
        }

        private void CreateOwner(IEnumerable<OnlineOwnedItemSnapshot> cards, bool childCannotAffordMana = false)
        {
            var wand = Entry(WandId, Rapid, ItemLocationKind.EquippedWand);
            wand.UseTemplateWandStats = !childCannotAffordMana;
            if (childCannotAffordMana)
            {
                // 低 Mana 是合法实例属性，只有此边界例使用；正式 Rapid 控制仍完整使用模板 120 Mana。
                // 5 Mana 恰够正式 Termination 根弹，子 Fire 的 20 Mana 不可支付。
                wand.WandStats = new OnlineOwnedWandStatsSnapshot { MaxMana = 5, ManaRegenPerSecond = 0,
                    BaseCastDelay = .05f, BaseReloadTime = .2f, ProgramCapacity = 16, BaseDraws = 1,
                    AlwaysCastContentIds = Array.Empty<string>() };
            }
            var items = new List<OnlineOwnedItemSnapshot> { wand,
                Entry(BagId, "raidtest.starterbag", ItemLocationKind.EquippedBackpack),
                Entry(LooseId, Fire, ItemLocationKind.SpellStorage) };
            items[2].RemainingCharges = 9;
            items.AddRange(cards);
            var seed = new OnlineOwnedAssetSnapshot { SchemaVersion = OnlineOwnedAssetSnapshot.CurrentSchemaVersion,
                RunId = "reservation.run", PlayerId = "reservation.player", ActorId = 1, InventoryId = "reservation.owner",
                GameplayConfigHash = Hash, InventoryVersion = 7, CurrencyVersion = 1, SelectedWandInstanceId = WandId,
                Items = items.ToArray() };
            Assert.That(OnlineOwnedWorkspaceFactory.TryCreate(_catalog, seed, Hash, out _owner, out var error), Is.True, error);
            _owner.ConfigureGroundItems(_ground);
            Assert.That(_ground.TryRegisterMaterialLoot("fixed:reservation.run:p7.explore.0", "containertest.ice", 256,
                Vector3.forward, Quaternion.identity, out _iceId), Is.EqualTo(OnlineInventoryCommandFailure.None));
            Assert.That(_ground.TryGet(_iceId, out var ice), Is.True);
            _iceWorldOwner = ice.WorldOwnerId;
        }

        private static OnlineOwnedItemSnapshot Entry(string id, string content, ItemLocationKind kind)
            => new OnlineOwnedItemSnapshot { InstanceId = id, ContentId = content, Revision = 1,
                LocationKind = (int)kind, ParentInstanceId = "", Slot = 0 };
        private static OnlineOwnedItemSnapshot Card(string id, string content, int slot, int charges)
            => new OnlineOwnedItemSnapshot { InstanceId = id, ContentId = content, Revision = 1,
                LocationKind = (int)ItemLocationKind.WandSlot, ParentInstanceId = WandId, Slot = slot, RemainingCharges = charges };
        private WandExecutionSnapshot State()
        { Assert.That(_owner.TryGetWandExecution(WandId, out var state), Is.True); return state; }

        private CastResourceRequestedEvent Request(CastResourceOperation operation, WandExecutionReplay replay = null, int layer = 0)
        {
            var state = State();
            return new CastResourceRequestedEvent { InventoryId = _owner.InventoryId, SourceWandId = WandId, CallerId = Caller,
                RequestId = ++_castRequest, UseV2 = true, Operation = operation, TicketId = replay?.TicketId,
                ProgramRevision = (uint)(replay == null ? state.Program.Revision : replay.Graph.Program.Revision),
                ExecutionRevision = state.Draw.ExecutionRevision, ResourceVersion = state.ResourceVersion,
                InventoryVersion = _owner.InventoryVersion, LayerIndex = layer };
        }

        private WandExecutionReplay CommitRoot()
        {
            var prepare = _owner.Execute(Request(CastResourceOperation.PrepareRoot));
            Assert.That(prepare.Success, Is.True, prepare.Reason.ToString());
            var complete = Request(CastResourceOperation.Complete); complete.TicketId = prepare.TicketId;
            var result = _owner.Execute(complete);
            Assert.That(result.Success, Is.True, result.Reason.ToString());
            Assert.That(_owner.TryGetLatestReplay(WandId, out var replay), Is.True);
            Assert.That(replay.TicketId, Is.EqualTo(prepare.TicketId));
            Assert.That(replay.GetLayerStatus(0), Is.EqualTo(WandLayerResourceStatus.Committed));
            Assert.That(_owner.V2CastResourcesReadOnly.ActiveReservations, Is.EqualTo(1));
            return replay;
        }

        private void ReleaseLayer(WandExecutionReplay replay, int layer)
        {
            foreach (int output in replay.Graph.Scopes[layer].OutputIds)
            {
                EventBus<SpellExecutionObservedEvent>.Publish(new SpellExecutionObservedEvent {
                    InventoryId = _owner.InventoryId, SourceWandId = WandId, CallerId = Caller,
                    TicketId = replay.TicketId, ProgramRevision = (uint)replay.Graph.Program.Revision,
                    LayerId = layer, OutputId = output, SimulationTime = ++_observationTime,
                    Kind = SpellExecutionObservationKind.OutputReleased });
                Assert.That(replay.ReleasedOutputs[output], Is.True, "必须由正式 Owner 接受世界事实，不能直接调用 Collect。");
            }
        }

        private OnlineInventoryCommandIntent Intent(OnlineInventoryCommandKind kind, string item)
            => new OnlineInventoryCommandIntent { CommandId = ++_inventoryCommand, ConnectionEpoch = 7,
                ExpectedInventoryVersion = _owner.InventoryVersion, Kind = kind, ItemId = item };
        private OnlineInventoryCommandReceipt MoveIndependentCard()
        {
            var intent = Intent(OnlineInventoryCommandKind.MoveOwned, LooseId);
            intent.TargetLocationKind = (int)ItemLocationKind.SpellStorage; intent.TargetSlot = 1;
            return _owner.ExecuteInventoryCommand(intent);
        }
        private OnlineInventoryCommandReceipt PickupFixedIce(bool automatic)
        {
            var intent = Intent(OnlineInventoryCommandKind.PickupContainer, _iceId);
            intent.WorldOwnerId = _iceWorldOwner; intent.AutoPlace = automatic;
            if (!automatic) { intent.TargetBackpackId = BagId; intent.GridX = 0; intent.GridY = 0; }
            return _owner.ExecuteInventoryCommand(intent, new OnlineContainerAuthorityContext { CanAdvance = true,
                WorldId = _ground.WorldId, ActorPosition = Vector3.zero, TargetPosition = Vector3.forward,
                TargetRotation = Quaternion.identity });
        }

        private void AssertInventoryBusy()
        {
            uint version = _owner.InventoryVersion, groundVersion = _ground.Version;
            Assert.That(_owner.V2CastResourcesReadOnly.ActiveReservations, Is.EqualTo(1));
            Assert.That(MoveIndependentCard().Failure, Is.EqualTo(OnlineInventoryCommandFailure.Busy));
            // 活票据继续保护旧物/程序编辑；地面收纳的窄例外由上面的独立矩阵覆盖。
            // 不在控制例里先拾走 Ice，否则会污染最后验证“票据结束后原命令链恢复”的资产前提。
            Assert.That(_owner.InventoryVersion, Is.EqualTo(version));
            Assert.That(_ground.Version, Is.EqualTo(groundVersion));
        }

        private void AssertInventoryOpen(bool autoPickup)
        {
            // 项目锁定的 NUnit 没有 Assert.Multiple；先执行两条命令，再将三项实际结果写入失败信息。
            // 不能只断言 Count=0，遗漏门仍锁住或 Ground 源没有实际转移的回归。
            var move = MoveIndependentCard(); var pickup = PickupFixedIce(autoPickup);
            Assert.That(_owner.V2CastResourcesReadOnly.ActiveReservations, Is.Zero,
                "独立移动=" + move.Failure + "，固定Ice拾取=" + pickup.Failure);
            Assert.That(move.Success, Is.True, "独立移动失败：" + move.Failure);
            Assert.That(pickup.Success, Is.True, "固定 Ice 拾取失败：" + pickup.Failure);
            Assert.That(pickup.Transferred, Is.EqualTo(256ul));
            Assert.That(_owner.OwnershipStore.TryGetLocation(LooseId, out var looseLocation), Is.True);
            Assert.That(looseLocation.SlotIndex, Is.EqualTo(1));
            Assert.That(_owner.TryGetItem(LooseId, out var loose), Is.True);
            Assert.That(loose.RemainingCharges, Is.EqualTo(9));
            Assert.That(_owner.TryGetItem(_iceId, out var ice), Is.True);
            Assert.That(ice.Material.MaterialKey, Is.EqualTo(5));
            Assert.That(ice.Material.Quantity, Is.EqualTo(256ul));
            Assert.That(_owner.OwnershipStore.TryGetLocation(_iceId, out var iceLocation), Is.True);
            Assert.That(iceLocation.Kind, Is.EqualTo(ItemLocationKind.Backpack));
            Assert.That(iceLocation.OwnerId, Is.EqualTo(BagId));
            Assert.That(_ground.TryGet(_iceId, out _), Is.False);
        }
    }
}
