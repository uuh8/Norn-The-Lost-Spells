using System;
using System.Linq;
using Game.Combat;
using Game.Core;
using Game.Skills;
using NUnit.Framework;
using UnityEngine;

namespace Game.Run.Tests
{
    /// <summary>
    /// 用真实Owned/容器/Store事务验证可靠检查点的事实来源。外部Physics和材料接收端只通过原Port代替，
    /// 不伪造库存提交或从缺失ID猜退役；这些开发测试不代表DS发布、游戏操作或资产结算验收。
    /// </summary>
    public sealed class OnlineRunCheckpointFactsTests : InventoryTestFixture
    {
        private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private ItemCatalogIndex _catalog;

        [SetUp]
        public void BuildCheckpointCatalog()
        {
            var wand = Define("checkpoint.wand", ItemKind.Wand);
            var card = Define("checkpoint.card", ItemKind.Spell);
            card.SpellDefinition.ManaCost = 20;
            card.SpellDefinition.ProjectilePrefab = Track(new GameObject("CheckpointProjectile", typeof(Rigidbody), typeof(BoxCollider), typeof(Arrow)));
            var bottle = Define("checkpoint.bottle", ItemKind.Container);
            JsonUtility.FromJsonOverwrite("{\"_containerCapacityGmu\":15000}", bottle);
            _catalog = Store(wand, card, bottle, Define("checkpoint.bag", ItemKind.Backpack, bagWidth: 8, bagHeight: 8)).Catalog;
        }

        private OnlineOwnedWorkspace Workspace(bool rolled = false, int uses = 3)
        {
            var stats = rolled ? new OnlineOwnedWandStatsSnapshot
            { MaxMana = 123.45678f, ManaRegenPerSecond = 0, BaseCastDelay = .125f, BaseReloadTime = .5f,
                BaseSpreadDegrees = 2.25f, ProgramCapacity = 8, BaseDraws = 1, AlwaysCastContentIds = Array.Empty<string>() } : null;
            var source = new OnlineOwnedAssetSnapshot
            {
                SchemaVersion = OnlineOwnedAssetSnapshot.CurrentSchemaVersion, RunId = "run", PlayerId = "player", ActorId = 1,
                InventoryId = "run/player", InventoryVersion = 7, GameplayConfigHash = Hash, SelectedWandInstanceId = "wand",
                Gold = 12345, CurrencyVersion = 4,
                Items = new[]
                {
                    new OnlineOwnedItemSnapshot { InstanceId = "wand", ContentId = "checkpoint.wand", Revision = 3,
                        LocationKind = 3, Slot = 0, UseTemplateWandStats = !rolled, WandStats = stats },
                    new OnlineOwnedItemSnapshot { InstanceId = "card", ContentId = "checkpoint.card", Revision = 4,
                        LocationKind = 2, ParentInstanceId = "wand", Slot = 0, RemainingCharges = uses },
                    new OnlineOwnedItemSnapshot { InstanceId = "bag", ContentId = "checkpoint.bag", Revision = 2, LocationKind = 4, Slot = 0 },
                    new OnlineOwnedItemSnapshot { InstanceId = "bottle", ContentId = "checkpoint.bottle", Revision = 5, LocationKind = 8, Slot = 0,
                        ContainerState = new OnlineOwnedContainerStateSnapshot { SchemaVersion = 1,
                            Components = new[] { new OnlineOwnedMaterialAmountSnapshot { MaterialKey = 1, Quantity = 1500 } } } }
                }
            };
            Assert.That(OnlineOwnedWorkspaceFactory.TryCreate(_catalog, source, Hash, out var workspace, out var error), Is.True, error);
            Assert.That(workspace.ConfigureCheckpointJournal(new OnlineRunItemRetirementJournal()), Is.True);
            return workspace;
        }

        private static CastResourceRequestedEvent Cast(OnlineOwnedWorkspace owner, CastResourceOperation operation, ulong request, string ticket = null)
        {
            Assert.That(owner.TryGetWandExecution("wand", out var state), Is.True);
            return new CastResourceRequestedEvent { InventoryId = owner.InventoryId, SourceWandId = "wand", CallerId = "checkpoint",
                RequestId = request, ProgramRevision = (uint)state.Program.Revision, ExecutionRevision = state.Draw.ExecutionRevision,
                InventoryVersion = owner.InventoryVersion, ResourceVersion = state.ResourceVersion, UseV2 = true,
                Operation = operation, TicketId = ticket, LayerIndex = 0 };
        }

        [Test]
        public void CurrentOwnerFactsPreserveRealRollAndCannotBeMutatedThroughACapturedArray()
        {
            using var owner = Workspace(rolled: true);
            Assert.That(owner.TryCaptureCheckpoint(out var captured), Is.True, "可靠捕获应读取活跃Workspace，不能返回默认空值。");
            var wand = captured.Assets.Items.Single(i => i.InstanceId == "wand");
            Assert.That(wand.UseTemplateWandStats, Is.False);
            Assert.That(wand.WandStats.MaxMana, Is.EqualTo(123.45678f));
            Assert.That(wand.WandStats.ProgramCapacity, Is.EqualTo(8));
            Assert.That(captured.Wands.Single(w => w.InstanceId == "wand").CurrentMana, Is.EqualTo(123.45678f));
            captured.Assets.Items.Single(i => i.InstanceId == "bottle").ContainerState.Components[0].Quantity = 999;
            wand.WandStats.MaxMana = 1;
            Assert.That(owner.TryCaptureCheckpoint(out var again), Is.True);
            Assert.That(again.Assets.Items.Single(i => i.InstanceId == "bottle").ContainerState.Components[0].Quantity, Is.EqualTo(1500));
            Assert.That(again.Assets.Items.Single(i => i.InstanceId == "wand").WandStats.MaxMana, Is.EqualTo(123.45678f));
        }

        [Test]
        public void PreparedInventoryCannotBeCapturedButCommittedChangesUseCurrentAmounts()
        {
            using var owner = Workspace();
            var prepared = owner.Execute(Cast(owner, CastResourceOperation.PrepareRoot, 1));
            Assert.That(prepared.Success, Is.True);
            Assert.That(owner.TryCaptureCheckpoint(out var pending), Is.False, "不得观察库存准备中或拼入候选扣量。");
            Assert.That(pending, Is.Null);
            Assert.That(owner.Execute(Cast(owner, CastResourceOperation.Complete, 2, prepared.TicketId)).Success, Is.True);
            Assert.That(owner.TryCaptureCheckpoint(out var committed), Is.True);
            Assert.That(committed.Assets.Items.Single(i => i.InstanceId == "card").RemainingCharges, Is.EqualTo(2));
            Assert.That(committed.Wands.Single(w => w.InstanceId == "wand").CurrentMana, Is.EqualTo(80));
        }

        [Test]
        public void InFlightBottleIsIncludedInCheckpointWhileUiProjectionStillExcludesIt()
        {
            using var owner = Workspace();
            var definition = Track(ScriptableObject.CreateInstance<ContainerUseDefinition>());
            owner.ConfigureContainerActions(definition, "world");
            Action<ContainerThrownEvent> prepare = e => e.Exchange.Proxy = new FlightProxy();
            EventBus<ContainerThrownEvent>.Subscribe(prepare);
            try
            {
                var select = new OnlineInventoryCommandIntent { Kind = OnlineInventoryCommandKind.SelectTool, ItemId = "bottle", TargetSlot = 0,
                    ConnectionEpoch = 1, CommandId = 1, ExpectedInventoryVersion = owner.InventoryVersion };
                Assert.That(owner.ExecuteInventoryCommand(select).Success, Is.True);
                var throwing = new OnlineInventoryCommandIntent { Kind = OnlineInventoryCommandKind.ThrowContainer, ItemId = "bottle", TargetSlot = 0,
                    ConnectionEpoch = 1, CommandId = 2, ExpectedInventoryVersion = owner.InventoryVersion, AimDirection = Vector3.forward };
                var context = new OnlineContainerAuthorityContext { CanAdvance = true, WorldId = "world", ActorPosition = Vector3.zero,
                    ThrowOrigin = Vector3.up, ThrowVelocity = Vector3.forward * 5 };
                Assert.That(owner.ExecuteInventoryCommand(throwing, context).Success, Is.True);
                Assert.That(owner.TryCapturePresentation(1, 1, 1, out var ui), Is.True);
                Assert.That(ui.Assets.Items.Any(i => i.InstanceId == "bottle"), Is.False, "原可视携带投影不把在途瓶装回工具位。");
                Assert.That(owner.TryCaptureCheckpoint(out var complete), Is.True);
                var flight = complete.ContainerTransits.Single();
                Assert.That(flight.ItemId, Is.EqualTo("bottle"));
                Assert.That(flight.Item.InstanceId, Is.EqualTo("bottle"));
                Assert.That(flight.Item.Revision, Is.EqualTo(5));
                Assert.That(flight.Item.LocationKind, Is.EqualTo((int)ItemLocationKind.ThrownContainer));
                Assert.That(flight.Item.ParentInstanceId, Is.Empty);
                Assert.That(flight.Item.ContainerState.Components[0].Quantity, Is.EqualTo(1500));
                Assert.That(flight.WorldId, Is.EqualTo("world"));
                Assert.That(flight.Impacted, Is.False);
                Assert.That(complete.Assets.Items.Count(i => i.InstanceId == "bottle"), Is.EqualTo(1));
            }
            finally { EventBus<ContainerThrownEvent>.Unsubscribe(prepare); }
        }

        [Test]
        public void MovingTheSameWandTreeToGroundDoesNotProduceRetirement()
        {
            using var owner = Workspace();
            using var ground = new OnlineGroundItemStore(_catalog);
            Assert.That(ground.Drop(owner, "wand", Vector3.one, Quaternion.identity), Is.EqualTo(OnlineInventoryCommandFailure.None));
            Assert.That(owner.TryCaptureCheckpoint(out var capture), Is.True);
            Assert.That(capture.Retired, Is.Empty, "跨Store迁移保持原ID，不能把源Store移除误记成消耗。");
            Assert.That(ground.TryGet("wand", out var root), Is.True);
            Assert.That(root.Children.Single().InstanceId, Is.EqualTo("card"));
        }

        [Test]
        public void PersonalExitKeepsActualNonzeroLossWithoutWaitingForAnotherOwnersJournalReservation()
        {
            using var owner = Workspace();
            owner.ConfigureContainerActions(Track(ScriptableObject.CreateInstance<ContainerUseDefinition>()), "world");
            Action<ContainerThrownEvent> prepare = e => e.Exchange.Proxy = new FlightProxy();
            EventBus<ContainerThrownEvent>.Subscribe(prepare);
            IOnlineItemRetirementReservation otherPending = null;
            try
            {
                var select = new OnlineInventoryCommandIntent { Kind = OnlineInventoryCommandKind.SelectTool, ItemId = "bottle", TargetSlot = 0,
                    ConnectionEpoch = 1, CommandId = 1, ExpectedInventoryVersion = owner.InventoryVersion };
                Assert.That(owner.ExecuteInventoryCommand(select).Success, Is.True);
                var throwing = new OnlineInventoryCommandIntent { Kind = OnlineInventoryCommandKind.ThrowContainer, ItemId = "bottle", TargetSlot = 0,
                    ConnectionEpoch = 1, CommandId = 2, ExpectedInventoryVersion = owner.InventoryVersion, AimDirection = Vector3.forward };
                var context = new OnlineContainerAuthorityContext { CanAdvance = true, WorldId = "world", ActorPosition = Vector3.zero,
                    ThrowOrigin = Vector3.up, ThrowVelocity = Vector3.forward * 5 };
                Assert.That(owner.ExecuteInventoryCommand(throwing, context).Success, Is.True);
                Assert.That(owner.TryCaptureCheckpoint(out var flying), Is.True);
                EventBus<ContainerTransitEndedEvent>.Publish(new ContainerTransitEndedEvent { InventoryId = owner.InventoryId,
                    WorldId = "world", FlightId = flying.ContainerTransits.Single().FlightId, Reason = ContainerTransitEndReason.ProxyDestroyed });
                Assert.That(owner.TryCaptureCheckpoint(out var beforeExit), Is.True);
                Assert.That(beforeExit.ContainerLost.Single().Quantity, Is.EqualTo(1500ul), "来自真实投掷结束账本，不直接伪造损耗数组。");
                Assert.That(beforeExit.SpellMaterialLost, Is.Empty);

                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
                var journal = (IOnlineItemRetirementJournal)typeof(OnlineOwnedWorkspace).GetField("_checkpointJournal", flags).GetValue(owner);
                Assert.That(journal.TryReserve(new[] { new OnlineItemRetirementSnapshot { InventoryId = "run/other", InstanceId = "other-pending",
                    ContentId = "other-item", Revision = 1, Reason = OnlineItemRetirementReason.Destroyed } }, out otherPending), Is.True);
                Assert.That(owner.TryCaptureCheckpoint(out _), Is.False, "全Run Journal仍被别人的准备占用。");
                const string exit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
                Assert.That(owner.TryBeginMemberExit(exit), Is.True);
                var capture = typeof(OnlineOwnedWorkspace).GetMethod("TryCaptureMemberExitFacts", flags);
                Assert.That(capture, Is.Not.Null, "个人退出需在同一稳定边界复制原损耗，不能在Exited后从Members中丢掉。");
                object[] args = { exit, null, null };
                Assert.That((bool)capture.Invoke(owner, args), Is.True, "本人稳定捕获不等待其他Owner的Journal空闲。");
                var assets = (OnlineOwnedAssetSnapshot)args[1]; var losses = args[2];
                Assert.That(assets.Items.Any(i => i.InstanceId == "bottle"), Is.False, "记录损耗不能退瓶或退款。");
                Assert.That(losses.GetType().GetField("RunId").GetValue(losses), Is.EqualTo(owner.RunId));
                Assert.That(losses.GetType().GetField("PlayerId").GetValue(losses), Is.EqualTo(owner.PlayerId));
                Assert.That(losses.GetType().GetField("ActorId").GetValue(losses), Is.EqualTo(owner.ActorId));
                var container = (OnlineOwnedMaterialAmountSnapshot[])losses.GetType().GetField("ContainerLost").GetValue(losses);
                Assert.That(container.Single().MaterialKey, Is.EqualTo(1)); Assert.That(container.Single().Quantity, Is.EqualTo(1500ul));
                Assert.That((OnlineOwnedMaterialAmountSnapshot[])losses.GetType().GetField("SpellMaterialLost").GetValue(losses), Is.Empty);
                container[0].Quantity = 0;
                object[] again = { exit, null, null };
                Assert.That((bool)capture.Invoke(owner, again), Is.True);
                var fresh = (OnlineOwnedMaterialAmountSnapshot[])again[2].GetType().GetField("ContainerLost").GetValue(again[2]);
                Assert.That(fresh.Single().Quantity, Is.EqualTo(1500ul), "借出的历史副本不能改回真实服务计数。");
            }
            finally { otherPending?.Cancel(); EventBus<ContainerThrownEvent>.Unsubscribe(prepare); }
        }

        private sealed class FlightProxy : IContainerThrowProxy
        { public void Launch() { } public void Cancel() { } }
    }

    /// <summary>退役必须来自真实提交语义；容量失败须在Mana/次数/材料改变之前拒绝。</summary>
    public sealed class OnlineCheckpointRetirementTests : WandExecutionTransactionTestFixture
    {
        private OnlineRunItemRetirementJournal Journal(int capacity = 32768)
        {
            var journal = new OnlineRunItemRetirementJournal(capacity);
            Assert.That(_store.ConfigureCheckpointJournal(journal, "scope", _ => 1u), Is.True);
            return journal;
        }

        [Test]
        public void LastFiniteUseIsRetiredOnlyAfterSuccessfulCommitAndReplayDoesNotDuplicateIt()
        {
            Build(root: 1, trigger: false); var journal = Journal();
            var root = Request(CastResourceOperation.PrepareRoot); Assert.That(root.Success, Is.True);
            var complete = NewRequest(CastResourceOperation.Complete, root.TicketId);
            Assert.That(_casts.Execute(complete).Success, Is.True);
            Assert.That(_casts.Execute(complete).Success, Is.True);
            Assert.That(journal.TryCapture(out var records), Is.True);
            var record = records.Single();
            Assert.That(record.InstanceId, Is.EqualTo("a")); Assert.That(record.ContentId, Is.EqualTo("emit"));
            Assert.That(record.InventoryId, Is.EqualTo("scope")); Assert.That(record.Revision, Is.EqualTo(1));
            Assert.That(record.Reason, Is.EqualTo(OnlineItemRetirementReason.Consumed));
            Assert.That(record.Sequence, Is.EqualTo(1)); Assert.That(_store.TryGet("a", out _), Is.False);
        }

        [Test]
        public void CancelledLastUseKeepsTheOriginalItemAndDoesNotRetireIt()
        {
            Build(root: 1, trigger: false); var journal = Journal(); var before = State();
            var root = Request(CastResourceOperation.PrepareRoot); Assert.That(root.Success, Is.True);
            Assert.That(Request(CastResourceOperation.Cancel, root.TicketId).Success, Is.True);
            Assert.That(journal.TryCapture(out var records), Is.True); Assert.That(records, Is.Empty);
            Assert.That(Uses("a"), Is.EqualTo(1)); Assert.That(State().Mana, Is.EqualTo(before.Mana));
        }

        [Test]
        public void FullRetirementBudgetRejectsBeforePayingManaOrRemovingTheLastUse()
        {
            Build(root: 1, trigger: false); var journal = Journal(1);
            Assert.That(journal.TryReserve(new[] { new OnlineItemRetirementSnapshot { InventoryId = "scope", InstanceId = "previous",
                ContentId = "emit", Revision = 1, Reason = OnlineItemRetirementReason.Consumed } }, out var reservation), Is.True);
            reservation.Commit(); uint version = _store.Version;
            Assert.That(Request(CastResourceOperation.PrepareRoot).Success, Is.False);
            Assert.That(Uses("a"), Is.EqualTo(1)); Assert.That(State().Mana, Is.EqualTo(100));
            Assert.That(_store.Version, Is.EqualTo(version)); Assert.That(journal.Count, Is.EqualTo(1));
        }

        [Test]
        public void UnboundSinglePlayerStoreKeepsItsOriginalLastUseBehavior()
        {
            Build(root: 1, trigger: false);
            var root = Request(CastResourceOperation.PrepareRoot); Assert.That(root.Success, Is.True);
            Assert.That(Request(CastResourceOperation.Complete, root.TicketId).Success, Is.True);
            Assert.That(_store.TryGet("a", out _), Is.False); Assert.That(State().Mana, Is.EqualTo(90));
        }
    }

    public sealed class OnlineCheckpointMaterialOutputTests : WandExecutionTransactionTestFixture
    {
        private void Supply()
        {
            Build(trigger: false, extra: 1, baseDraws: 2);
            Add(_store, "bag", "bag"); _store.TryMove("bag", new ItemLocation(ItemLocationKind.EquippedBackpack, "player", 0, 0, 0, 0));
            Add(_store, "source", "bottle"); Assert.That(_store.TryMove("source", new ItemLocation(ItemLocationKind.Backpack, "bag", 0, 0, 0, -1)).Success, Is.True);
            _store.TryGet("source", out var source); source.Composition.AddReserved(new[] { new MaterialAmount(1, 30) }, 1);
            var settings = new SpellAttachmentService(_profile, "scope"); ulong request = 0;
            foreach (string card in new[] { "a", "x0" })
                Assert.That(settings.Execute(new SpellAttachmentRequestedEvent { InventoryId = "scope", SpellId = card, ContainerId = "source",
                    CallerId = "checkpoint", RequestId = ++request, ExpectedVersion = _store.Version, Dose = 20, Enabled = true }).Success, Is.True);
        }

        [Test]
        public void CommittedOutputBudgetsAreIndependentOwnersAndCapturedComponentsCannotMutateThem()
        {
            Supply(); var request = NewRequest(CastResourceOperation.PrepareRoot); request.MaterialWorldId = "world";
            var root = _casts.Execute(request); Assert.That(root.Success, Is.True);
            Assert.That(_casts.MaterialOutputs.TryCaptureCheckpoint(out _, out _), Is.False, "尚未完成的库存准备不能暴露候选预算。");
            request = NewRequest(CastResourceOperation.Complete, root.TicketId); request.MaterialWorldId = "world";
            Assert.That(_casts.Execute(request).Success, Is.True);
            Assert.That(_casts.MaterialOutputs.TryCaptureCheckpoint(out var outputs, out var lost), Is.True);
            Assert.That(outputs.Length, Is.EqualTo(2)); Assert.That(lost, Is.Empty);
            Assert.That(outputs.Select(o => o.Components.Sum(c => (long)c.Quantity)).OrderBy(q => q).ToArray(), Is.EqualTo(new long[] { 10, 20 }));
            Assert.That(outputs.All(o => o.WorldId == "world"), Is.True);
            Assert.That(outputs.Select(o => o.OutputId).Distinct().Count(), Is.EqualTo(2));
            _store.TryGet("source", out var source); Assert.That(source.Composition.Total, Is.Zero);
            outputs[0].Components[0].Quantity = 999;
            Assert.That(_casts.MaterialOutputs.TryCaptureCheckpoint(out var again, out _), Is.True);
            Assert.That(again.Sum(o => (long)o.Components[0].Quantity), Is.EqualTo(30));
        }

        [Test]
        public void ExistingOutputEndLossIsCapturedWithoutReturningItToTheBottle()
        {
            Supply(); var request = NewRequest(CastResourceOperation.PrepareRoot); request.MaterialWorldId = "world";
            var root = _casts.Execute(request); Assert.That(root.Success, Is.True);
            // 原供料API只在Prepare持锁期间给执行器取本层OutputId；Complete后候选层会清理。
            // 先保存这份真实身份，再提交并结束其独立Owner，不为测试延长旧Ticket或重建预算。
            Assert.That(_casts.TryGetMaterialOutput(root.TicketId, 0, 0, out var first), Is.True);
            request = NewRequest(CastResourceOperation.Complete, root.TicketId); request.MaterialWorldId = "world";
            Assert.That(_casts.Execute(request).Success, Is.True);
            var ended = _casts.MaterialOutputs.Execute(new SpellMaterialOutputEvent { InventoryId = "scope", OutputId = first.OutputId, Ended = true });
            Assert.That(ended.Resolved, Is.True);
            Assert.That(_casts.MaterialOutputs.TryCaptureCheckpoint(out var outputs, out var lost), Is.True);
            Assert.That(outputs.Length, Is.EqualTo(1)); Assert.That(outputs[0].Components[0].Quantity, Is.EqualTo(10));
            Assert.That(lost.Single().MaterialKey, Is.EqualTo(1)); Assert.That(lost.Single().Quantity, Is.EqualTo(20));
            _store.TryGet("source", out var source); Assert.That(source.Composition.Total, Is.Zero);
        }

        [Test]
        public void EveryLegallyCommittedMaterialBudgetRemainsCheckpointCapturableBeyondTheNewCaptureLimit()
        {
            // 固定复现已证明合法的4097份状态，不追着实现上限移动目标；原4096捕获门是这项RED的缺陷。
            const int outputCount = 4097;
            const int sourceQuantity = 900;
            Build(root: outputCount + 1, trigger: false, cost: 0);
            Add(_store, "bag", "bag");
            Assert.That(_store.TryMove("bag", new ItemLocation(ItemLocationKind.EquippedBackpack, "player", 0, 0, 0, 0)).Success, Is.True);

            // 五个真实随身瓶各装900GMU，低于原模板1024GMU容量，且都放进原4×2包；
            // 不向单瓶塞入4097GMU，也不直接篡改输出字典或跳过扣量事务。
            int sourceCount = (outputCount + sourceQuantity - 1) / sourceQuantity;
            for (int i = 0; i < sourceCount; i++)
            {
                string sourceId = "capacity-source-" + i;
                Add(_store, sourceId, "bottle");
                Assert.That(_store.TryMove(sourceId, new ItemLocation(ItemLocationKind.Backpack, "bag", i % 4, i / 4, 0, -1)).Success, Is.True);
                Assert.That(_store.TryGet(sourceId, out var source), Is.True);
                source.Composition.AddReserved(new[] { new MaterialAmount(1, sourceQuantity) }, 1);
            }

            var settings = new SpellAttachmentService(_profile, "scope");
            ulong attachmentRequest = 0;
            for (int i = 0; i < outputCount; i++)
            {
                if (i % sourceQuantity == 0)
                    Assert.That(settings.Execute(new SpellAttachmentRequestedEvent { InventoryId = "scope", SpellId = "a",
                        ContainerId = "capacity-source-" + i / sourceQuantity, CallerId = "capacity-checkpoint",
                        RequestId = ++attachmentRequest, ExpectedVersion = _store.Version, Dose = 1, Enabled = true }).Success, Is.True);

                // 仅推进既有Mana/CD时钟，不修改射速或资源算法；每次都走真实V2准备、提交和释放观测。
                _wands.Tick(10);
                var prepare = NewRequest(CastResourceOperation.PrepareRoot); prepare.UseV2 = true; prepare.MaterialWorldId = "world";
                var root = _casts.Execute(prepare); Assert.That(root.Success, Is.True, "第" + i + "次合法准备应由现有规则裁决。");
                Assert.That(_casts.TryGetGraph(root.TicketId, out var graph), Is.True);
                Assert.That(_casts.TryGetMaterialOutput(root.TicketId, 0, 0, out var supply), Is.True);
                Assert.That(supply.Budget.Total, Is.EqualTo(1));
                Assert.That(Request(CastResourceOperation.Complete, root.TicketId).Success, Is.True);
                Assert.That(_casts.Observe(new SpellExecutionObservedEvent { InventoryId = "scope", SourceWandId = "wand", CallerId = "test",
                    TicketId = root.TicketId, ProgramRevision = (uint)graph.Program.Revision, LayerId = 0, OutputId = 0,
                    SimulationTime = i * 10, Kind = SpellExecutionObservationKind.OutputReleased }), Is.True);
            }

            Assert.That(_casts.ActiveReservations, Is.Zero, "释放观测清理演算Ticket，不冒充材料Owner已经沉积或结束。");
            Assert.That(_casts.MaterialOutputs.ActiveOutputs, Is.EqualTo(outputCount));
            Assert.That(_profile.PendingMaterialOutputs, Is.EqualTo(outputCount));
            ulong remaining = 0;
            for (int i = 0; i < sourceCount; i++)
            {
                Assert.That(_store.TryGet("capacity-source-" + i, out var source), Is.True);
                remaining += source.Composition.Total;
            }
            Assert.That(remaining + (ulong)outputCount, Is.EqualTo((ulong)sourceCount * sourceQuantity));

            // 此例只证明Run供料事务与新增捕获上界是否相容，不声称正式DS能越过原1024输出注册表。
            // 若这里RED，保留“已扣独立预算无法捕获”的证据，由协调方判断正式边界，不能在此偷偷拒绝、丢弃或退款。
            Assert.That(_casts.MaterialOutputs.TryCaptureCheckpoint(out var outputs, out _), Is.True,
                "现有事务接受并已扣量的每个材料Owner都需要可捕获；新增捕获上限不能隐去已提交事实。");
            Assert.That(outputs.Length, Is.EqualTo(outputCount));
        }
    }

    /// <summary>补充容量/归并边界回归；这组不冒充前一轮已执行的10项RED。</summary>
    public sealed class OnlineCheckpointMaterialRetirementTests : InventoryTestFixture
    {
        [TestCase(true)]
        [TestCase(false)]
        public void OnlyTheCompletelyMergedSourceIdRetires(bool fullyMerged)
        {
            var store = Store(Define("merge.bag", ItemKind.Backpack, bagWidth: 4, bagHeight: 2), Define("merge.ice", ItemKind.Material));
            Assert.That(store.TryAdd(new RuntimeItemState("bag", "merge.bag", 0), new ItemLocation(ItemLocationKind.EquippedBackpack, "player", 0, 0, 0, 0)), Is.True);
            Assert.That(store.TryAdd(new RuntimeItemState("target", "merge.ice", 0, material: new MaterialItemState(5, fullyMerged ? 900UL : 1000UL)),
                new ItemLocation(ItemLocationKind.Backpack, "bag", 0, 0, 0, -1)), Is.True);
            Assert.That(store.TryAdd(new RuntimeItemState("source", "merge.ice", 0, material: new MaterialItemState(5, 100)),
                new ItemLocation(ItemLocationKind.Backpack, "bag", 1, 0, 0, -1)), Is.True);
            var journal = new OnlineRunItemRetirementJournal();
            Assert.That(store.ConfigureCheckpointJournal(journal, "scope", _ => 7u), Is.True);
            Assert.That(MaterialStackOps.TryMerge(store, "source", "target", store.Version).Success, Is.True);
            Assert.That(journal.TryCapture(out var facts), Is.True);
            store.TryGet("target", out var target);
            Assert.That(target.Material.Quantity, Is.EqualTo(fullyMerged ? 1000 : 1024));
            if (fullyMerged)
            {
                Assert.That(store.TryGet("source", out _), Is.False);
                Assert.That(facts.Single().InstanceId, Is.EqualTo("source"));
                Assert.That(facts.Single().Revision, Is.EqualTo(7));
                Assert.That(facts.Single().Reason, Is.EqualTo(OnlineItemRetirementReason.Merged));
            }
            else
            {
                Assert.That(store.TryGet("source", out var source), Is.True);
                Assert.That(source.Material.Quantity, Is.EqualTo(76)); Assert.That(facts, Is.Empty);
            }
        }

        [Test]
        public void PendingRetirementIsInvisibleAndCancellationReturnsCapacityWithoutInventingARecord()
        {
            var journal = new OnlineRunItemRetirementJournal(1);
            var original = new OnlineItemRetirementSnapshot { InventoryId = "scope", InstanceId = "old", ContentId = "card",
                Revision = 2, Reason = OnlineItemRetirementReason.Consumed };
            Assert.That(journal.TryReserve(new[] { original }, out var pending), Is.True);
            Assert.That(journal.TryCapture(out _), Is.False); Assert.That(journal.Count, Is.Zero);
            pending.Cancel(); Assert.That(journal.TryCapture(out var empty), Is.True); Assert.That(empty, Is.Empty);
            Assert.That(journal.TryReserve(new[] { original }, out var committed), Is.True);
            original.ContentId = "mutated-caller"; committed.Commit(); committed.Commit();
            Assert.That(journal.TryCapture(out var facts), Is.True); Assert.That(facts.Single().ContentId, Is.EqualTo("card"));
            facts[0].ContentId = "mutated-reader";
            Assert.That(journal.TryCapture(out var again), Is.True); Assert.That(again.Single().ContentId, Is.EqualTo("card"));
            Assert.That(journal.Count, Is.EqualTo(1)); Assert.That(journal.Sequence, Is.EqualTo(1));
        }
    }
}
