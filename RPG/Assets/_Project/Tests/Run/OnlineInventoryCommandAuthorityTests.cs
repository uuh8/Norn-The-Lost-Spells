using System;
using System.Reflection;
using Game.Core;
using Game.Skills;
using NUnit.Framework;

namespace Game.Run.Tests
{
    /// <summary>从真实 Authority 工作区验证命令账本、原子移动和读快照；不创建本地存档或客户端权限。</summary>
    public sealed class OnlineInventoryCommandAuthorityTests : InventoryTestFixture
    {
        private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private ItemCatalogIndex _catalog;
        [SetUp] public void CreateCatalog()
        {
            var card = Define("command.card", ItemKind.Spell); card.SpellDefinition.Kind = SpellKind.Modify;
            _catalog = Store(Define("command.wand", ItemKind.Wand), card,
                Define("command.bag", ItemKind.Backpack, bagWidth: 8, bagHeight: 8)).Catalog;
        }

        private static void ConfigureWriteGate(OnlineOwnedWorkspace workspace, Func<bool> gate)
        {
            var method = typeof(OnlineOwnedWorkspace).GetMethod("ConfigureAuthorityWriteGate", new[] { typeof(Func<bool>) });
            Assert.That(method, Is.Not.Null, "库存与原始选择事件也必须检查同一权威写入门。");
            method.Invoke(workspace, new object[] { gate });
        }

        [Test]
        public void ClosedAuthorityGateRejectsNewInventoryAndLegacySelectionWithoutChangingAssets()
        {
            using var workspace = Create(); uint version = workspace.InventoryVersion, selection = workspace.SelectionVersion;
            Assert.That(workspace.TryCapturePresentation(7, 1, 1, out var baseline), Is.True);
            int chargesBeforeGate = Find(baseline, "card").RemainingCharges;
            ConfigureWriteGate(workspace, () => false);
            var select = Intent(workspace, 1, 1, "wand2"); Set(select, "TargetSlot", 1);
            AssertFailure(Execute(workspace, select), "NotReady");
            AssertFailure(Execute(workspace, Storage(workspace, 2)), "NotReady");
            var rejected = Intent(workspace, 3, 1, "wand2"); Set(rejected, "TargetSlot", 1);
            AssertFailure(typeof(OnlineOwnedWorkspace).GetMethod("RejectInventoryCommand").Invoke(workspace,
                new[] { rejected, (object)OnlineInventoryCommandFailure.Busy }), "NotReady");
            EventBus<WandSelectionRequestedEvent>.Publish(new WandSelectionRequestedEvent
                { InventoryId = workspace.InventoryId, SlotIndex = 1 });
            Assert.That(workspace.WandsReadOnly.SelectedWandId, Is.EqualTo("wand"));
            Assert.That(workspace.SelectionVersion, Is.EqualTo(selection));
            Assert.That(workspace.InventoryVersion, Is.EqualTo(version));
            Assert.That(workspace.TryCapturePresentation(7, 1, 1, out var snapshot), Is.True);
            Assert.That(Find(snapshot, "card").LocationKind, Is.EqualTo((int)ItemLocationKind.WandSlot));
            Assert.That(Find(snapshot, "card").ParentInstanceId, Is.EqualTo("wand"));
            Assert.That(Find(snapshot, "card").RemainingCharges, Is.EqualTo(chargesBeforeGate));
        }

        [Test]
        public void ClosedAuthorityGateReplaysExactPriorInventorySuccessAndFailure()
        {
            using var workspace = Create(); bool open = true; ConfigureWriteGate(workspace, () => open);
            var move = Storage(workspace, 1); var committed = Execute(workspace, move); AssertFailure(committed, "None");
            var invalid = Intent(workspace, 2, 1, "missing");
            var rejected = Execute(workspace, invalid); AssertFailure(rejected, "NotOwned");
            uint version = workspace.InventoryVersion; open = false;
            var replay = Execute(workspace, move); AssertFailure(replay, "None");
            Assert.That(Get(replay, "InventoryVersion"), Is.EqualTo(Get(committed, "InventoryVersion")));
            AssertFailure(Execute(workspace, invalid), "NotOwned");
            AssertFailure(typeof(OnlineOwnedWorkspace).GetMethod("RejectInventoryCommand").Invoke(workspace,
                new[] { move, (object)OnlineInventoryCommandFailure.Busy }), "None");
            var fresh = Intent(workspace, 3, 1, "wand2"); Set(fresh, "TargetSlot", 1);
            AssertFailure(Execute(workspace, fresh), "NotReady");
            Assert.That(workspace.InventoryVersion, Is.EqualTo(version));
            Assert.That(workspace.WandsReadOnly.SelectedWandId, Is.EqualTo("wand"));
            Assert.That(workspace.TryCapturePresentation(7, 1, 1, out var snapshot), Is.True);
            Assert.That(Find(snapshot, "card").LocationKind, Is.EqualTo((int)ItemLocationKind.SpellStorage));
        }

        [Test] public void CoreContractAndReadContextDoNotExposeAuthorityServices()
        {
            Assert.That(Contract("OnlineInventoryCommandIntent"), Is.Not.Null);
            Assert.That(Contract("OnlineInventoryCommandReceipt"), Is.Not.Null);
            Assert.That(Contract("OnlineInventoryCommandRequestedEvent"), Is.Not.Null);
            Assert.That(Contract("OnlineInventoryCommandResultEvent"), Is.Not.Null);
            var context = typeof(OnlineInventoryPresentationSource).Assembly.GetType("Game.Run.IOnlineInventoryCommandContext");
            Assert.That(context, Is.Not.Null); Assert.That(context.IsAssignableFrom(typeof(OnlineInventoryPresentationSource)), Is.True);
            Assert.That(context.GetProperty("ActorId").PropertyType, Is.EqualTo(typeof(ulong)));
            Assert.That(context.GetMethods().Length, Is.EqualTo(2), "仅暴露 Actor/Epoch，不交出 Store 写口。");
        }

        [Test] public void SelectUsesRealEquippedWandAndPreservesInventoryAndMana()
        {
            using var workspace = Create(); uint version = workspace.InventoryVersion;
            var intent = Intent(workspace, 1, 1, "wand2"); Set(intent, "TargetSlot", 1);
            var receipt = Execute(workspace, intent); AssertFailure(receipt, "None");
            Assert.That(workspace.WandsReadOnly.SelectedWandId, Is.EqualTo("wand2"));
            Assert.That(workspace.InventoryVersion, Is.EqualTo(version));
            Assert.That(workspace.WandsReadOnly.TryGetState("wand2", out var state), Is.True);
            Assert.That(state.CurrentMana, Is.EqualTo(100));
            Set(intent, "CommandId", 2ul); Set(intent, "TargetSlot", 0);
            AssertFailure(Execute(workspace, intent), "NotOwned");
            Assert.That(workspace.WandsReadOnly.SelectedWandId, Is.EqualTo("wand2"));
        }

        [Test] public void MoveToRealStorageAndBackReusesQualificationAndExportsLocation()
        {
            using var workspace = Create(); var move = Storage(workspace, 1);
            AssertFailure(Execute(workspace, move), "None");
            Assert.That(workspace.TryCapturePresentation(7, 1, 1, out var snapshot), Is.True);
            var card = Find(snapshot, "card"); Assert.That(card.LocationKind, Is.EqualTo(7));
            Assert.That(card.ParentInstanceId, Is.Empty); Assert.That(card.RemainingCharges, Is.Zero);
            Assert.That(OnlineInventoryPresentationSource.TryCreate(_catalog,
                new OnlineInventoryPresentationIdentity("run", "player", 1, "inventory", Hash, 7, "World"), snapshot,
                out var source, out var error), Is.True, error);
            using (source) Assert.That(source.SpellStorageCapacity, Is.EqualTo(_catalog.SpellStorageCapacity));
            var install = Intent(workspace, 2, 2, "card"); Set(install, "TargetWandId", "wand2");
            Set(install, "TargetProgramRevision", Revision(workspace, "wand2"));
            AssertFailure(Execute(workspace, install), "None");
            Assert.That(workspace.TryGetItem("card", out var state), Is.True); Assert.That(state.RemainingCharges, Is.Zero,
                "已提交的本局资格不会因摘卡/装回重复扣费。");
        }

        [Test] public void ExactRetryReturnsOriginalReceiptBeforeCurrentVersionAndConflictIsRejected()
        {
            using var workspace = Create(); var move = Storage(workspace, 1);
            var first = Execute(workspace, move); uint committed = workspace.InventoryVersion;
            AssertFailure(first, "None"); AssertFailure(Execute(workspace, move), "None");
            Assert.That(workspace.InventoryVersion, Is.EqualTo(committed));
            Set(move, "TargetSlot", 1); AssertFailure(Execute(workspace, move), "CommandConflict");
            Assert.That(workspace.InventoryVersion, Is.EqualTo(committed));
        }

        [Test] public void RecordedBusyCannotExecuteSameIdAfterBusyHasEnded()
        {
            using var workspace = Create(); var move = Storage(workspace, 1);
            var method = typeof(OnlineOwnedWorkspace).GetMethod("RejectInventoryCommand");
            Assert.That(method, Is.Not.Null, "外层忙/断线拒绝也必须加入相同去重账本。");
            var failure = Enum.Parse(Contract("OnlineInventoryCommandFailure"), "Busy");
            AssertFailure(method.Invoke(workspace, new[] { move, failure }), "Busy");
            AssertFailure(Execute(workspace, move), "Busy");
            Assert.That(workspace.TryCapturePresentation(7, 1, 1, out var snapshot), Is.True);
            Assert.That(Find(snapshot, "card").LocationKind, Is.EqualTo(2));
            Set(move, "CommandId", 2ul); AssertFailure(Execute(workspace, move), "None");
        }

        [Test] public void StaleVersionsRejectWithoutMovingEitherCard()
        {
            using var workspace = Create(true); uint before = workspace.InventoryVersion;
            var move = Storage(workspace, 1); Set(move, "ExpectedInventoryVersion", before - 1);
            AssertFailure(Execute(workspace, move), "StaleVersion");
            move = Storage(workspace, 2); Set(move, "SourceProgramRevision", Revision(workspace, "wand") + 1);
            AssertFailure(Execute(workspace, move), "StaleProgram");
            Assert.That(workspace.InventoryVersion, Is.EqualTo(before));
            Assert.That(workspace.TryCapturePresentation(7, 1, 1, out var snapshot), Is.True);
            Assert.That(Find(snapshot, "card").ParentInstanceId, Is.EqualTo("wand"));
            Assert.That(Find(snapshot, "card2").ParentInstanceId, Is.EqualTo("wand2"));
        }

        [Test] public void BoundedLedgerRejectsEvictedOldCommandsAndEpochNeverRollsBack()
        {
            using var workspace = Create(); var first = Intent(workspace, 1, 1, "wand");
            AssertFailure(Execute(workspace, first), "None");
            for (ulong id = 2; id <= 130; id++) AssertFailure(Execute(workspace, Intent(workspace, id, 1, "wand")), "None");
            AssertFailure(Execute(workspace, first), "StaleCommand");
            var next = Intent(workspace, 1, 1, "wand2"); Set(next, "ConnectionEpoch", 8u); Set(next, "TargetSlot", 1);
            AssertFailure(Execute(workspace, next), "None");
            AssertFailure(Execute(workspace, first), "StaleCommand");
            Assert.That(workspace.WandsReadOnly.SelectedWandId, Is.EqualTo("wand2"));
        }

        [Test] public void OccupiedProgramSlotSwapsRealCardsAndRetainsAlreadyPaidQualifications()
        {
            using var workspace = Create(true);
            var move = Intent(workspace, 1, 2, "card"); Set(move, "SourceWandId", "wand");
            Set(move, "SourceProgramRevision", Revision(workspace, "wand")); Set(move, "TargetWandId", "wand2");
            Set(move, "TargetProgramRevision", Revision(workspace, "wand2"));
            AssertFailure(Execute(workspace, move), "None");
            Assert.That(workspace.TryCapturePresentation(7, 1, 1, out var snapshot), Is.True);
            Assert.That(Find(snapshot, "card").ParentInstanceId, Is.EqualTo("wand2"));
            Assert.That(Find(snapshot, "card2").ParentInstanceId, Is.EqualTo("wand"));
            Assert.That(Find(snapshot, "card").RemainingCharges, Is.Zero);
            Assert.That(Find(snapshot, "card2").RemainingCharges, Is.Zero,
                "交换实例只改变位置；本局已取得资格的最后一次不能再扣费或失效。");
        }

        [Test] public void CardInOwnedBackpackCanInstallUsingTheExistingProgramPlacementSemantics()
        {
            var seed = new OnlineOwnedAssetSnapshot { SchemaVersion = OnlineOwnedAssetSnapshot.CurrentSchemaVersion,
                RunId = "run", PlayerId = "player", ActorId = 1, InventoryId = "inventory", InventoryVersion = 9,
                GameplayConfigHash = Hash, SelectedWandInstanceId = "wand", Items = new[] {
                    new OnlineOwnedItemSnapshot { InstanceId = "wand", ContentId = "command.wand", Revision = 1,
                        LocationKind = 3, ParentInstanceId = "", UseTemplateWandStats = true },
                    new OnlineOwnedItemSnapshot { InstanceId = "bag", ContentId = "command.bag", Revision = 1,
                        LocationKind = 4, ParentInstanceId = "" },
                    new OnlineOwnedItemSnapshot { InstanceId = "card", ContentId = "command.card", Revision = 1,
                        LocationKind = 1, ParentInstanceId = "bag", Slot = -1, RemainingCharges = 1 }
                }};
            Assert.That(OnlineOwnedWorkspaceFactory.TryCreate(_catalog, seed, Hash, out var workspace, out var error), Is.True, error);
            using (workspace)
            {
                var install = Intent(workspace, 1, 2, "card"); Set(install, "TargetWandId", "wand");
                Set(install, "TargetProgramRevision", Revision(workspace, "wand"));
                AssertFailure(Execute(workspace, install), "None");
                Assert.That(workspace.TryCapturePresentation(7, 1, 1, out var snapshot), Is.True);
                Assert.That(Find(snapshot, "card").ParentInstanceId, Is.EqualTo("wand"));
                Assert.That(Find(snapshot, "card").RemainingCharges, Is.Zero, "第一次安装仍沿原资格事务扣一次。");
            }
        }

        [Test] public void InstalledCardMovesToBackpackAndBackWithoutRepayingItsQualification()
        {
            using var workspace = Create(bag: true);
            var move = Intent(workspace, 1, 12, "card"); Set(move, "TargetLocationKind", (int)ItemLocationKind.Backpack);
            Set(move, "TargetBackpackId", "bag"); Set(move, "SourceWandId", "wand");
            Set(move, "SourceProgramRevision", Revision(workspace, "wand"));
            AssertFailure(Execute(workspace, move), "None");
            Assert.That(workspace.TryCapturePresentation(7, 1, 1, out var packed), Is.True);
            Assert.That(Find(packed, "card").LocationKind, Is.EqualTo((int)ItemLocationKind.Backpack));
            Assert.That(Find(packed, "card").ParentInstanceId, Is.EqualTo("bag"));
            var install = Intent(workspace, 2, 12, "card"); Set(install, "TargetLocationKind", (int)ItemLocationKind.WandSlot);
            Set(install, "TargetWandId", "wand"); Set(install, "TargetProgramRevision", Revision(workspace, "wand"));
            AssertFailure(Execute(workspace, install), "None");
            Assert.That(workspace.TryGetItem("card", out var item), Is.True);
            Assert.That(item.RemainingCharges, Is.Zero);
        }

        [Test] public void OwnedWandMovesIntoBackpackWithItsProgramAndEquipsByAtomicSwap()
        {
            using var workspace = Create(bag: true);
            var pack = Intent(workspace, 1, 12, "wand"); Set(pack, "TargetLocationKind", (int)ItemLocationKind.Backpack);
            Set(pack, "TargetBackpackId", "bag"); AssertFailure(Execute(workspace, pack), "None");
            Assert.That(workspace.TryCapturePresentation(7, 1, 1, out var packed), Is.True);
            Assert.That(Find(packed, "card").ParentInstanceId, Is.EqualTo("wand"));
            Assert.That(workspace.WandsReadOnly.SelectedWandId, Is.EqualTo("wand2"));
            var equip = Intent(workspace, 2, 13, "wand"); Set(equip, "TargetSlot", 1); Set(equip, "TargetLocationKind", (int)ItemLocationKind.EquippedWand);
            AssertFailure(Execute(workspace, equip), "None");
            Assert.That(workspace.TryCapturePresentation(7, 2, 1, out var swapped), Is.True);
            Assert.That(Find(swapped, "wand").LocationKind, Is.EqualTo((int)ItemLocationKind.EquippedWand));
            Assert.That(Find(swapped, "wand").Slot, Is.EqualTo(1));
            Assert.That(Find(swapped, "wand2").ParentInstanceId, Is.EqualTo("bag"));
            Assert.That(Find(swapped, "card").ParentInstanceId, Is.EqualTo("wand"));
        }

        [Test] public void BackpackWandExportsRealProgramAndResourcesForEditingInstalledCards()
        {
            using var workspace = Create(bag: true);
            AssertFailure(Execute(workspace, Storage(workspace, 1)), "None");
            var reinstall = Intent(workspace, 2, 2, "card"); Set(reinstall, "TargetWandId", "wand");
            Set(reinstall, "TargetProgramRevision", Revision(workspace, "wand"));
            AssertFailure(Execute(workspace, reinstall), "None");
            uint actualRevision = Revision(workspace, "wand"); Assert.That(actualRevision, Is.GreaterThan(1u));
            var wands = (WandLoadoutService)typeof(OnlineOwnedWorkspace).GetField("_wands", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(workspace);
            Assert.That(wands.Execute(new WandResourceRequestedEvent { InventoryId = workspace.InventoryId, CallerId = "backup-resources",
                RequestId = 1, SourceWandId = "wand", ProgramRevision = actualRevision, ManaCost = 35, IsRootCast = true }).Success, Is.True);
            var pack = Intent(workspace, 3, 12, "wand"); Set(pack, "TargetLocationKind", (int)ItemLocationKind.Backpack);
            Set(pack, "TargetBackpackId", "bag"); AssertFailure(Execute(workspace, pack), "None");
            Assert.That(workspace.TryCapturePresentation(7, 1, 1, out var snapshot), Is.True);
            var backup = Array.Find(snapshot.Wands, value => value.InstanceId == "wand");
            Assert.That(backup, Is.Not.Null, "随身备用杖也必须导出真实读数，不能让客户端猜ProgramRevision=1。");
            Assert.That(backup.ProgramRevision, Is.EqualTo(actualRevision)); Assert.That(backup.CurrentMana, Is.EqualTo(65));
            Assert.That(workspace.WandsReadOnly.TryGetState("wand", out var state), Is.True);
            Assert.That(state.Carried, Is.True); Assert.That(state.SlotIndex, Is.EqualTo(-1)); Assert.That(state.Selected, Is.False);
            var remove = Intent(workspace, 4, 12, "card"); Set(remove, "SourceWandId", "wand");
            Set(remove, "SourceProgramRevision", backup.ProgramRevision); Set(remove, "TargetLocationKind", (int)ItemLocationKind.Backpack);
            Set(remove, "TargetBackpackId", "bag"); Set(remove, "GridX", 1);
            AssertFailure(Execute(workspace, remove), "None");
            Assert.That(workspace.TryCapturePresentation(7, 2, 1, out var removed), Is.True);
            var current = Array.Find(removed.Wands, value => value.InstanceId == "wand");
            Assert.That(current.ProgramRevision, Is.GreaterThan(actualRevision));
            var install = Intent(workspace, 5, 12, "card"); Set(install, "TargetLocationKind", (int)ItemLocationKind.WandSlot);
            Set(install, "TargetWandId", "wand"); Set(install, "TargetProgramRevision", current.ProgramRevision);
            AssertFailure(Execute(workspace, install), "None");
            Assert.That(workspace.TryGetItem("card", out var card), Is.True); Assert.That(card.RemainingCharges, Is.Zero);
            Assert.That(workspace.WandsReadOnly.TryGetState("wand", out var finalState), Is.True);
            Assert.That(finalState.CurrentMana, Is.EqualTo(65), "编辑备用杖不重建资源服务或补满Mana。");
        }

        private OnlineOwnedWorkspace Create(bool secondCard = false, bool bag = false)
        {
            var items = new OnlineOwnedItemSnapshot[3 + (secondCard ? 1 : 0) + (bag ? 1 : 0)];
            items[0] = new OnlineOwnedItemSnapshot { InstanceId = "wand", ContentId = "command.wand", Revision = 1,
                LocationKind = 3, ParentInstanceId = "", UseTemplateWandStats = true };
            items[1] = new OnlineOwnedItemSnapshot { InstanceId = "wand2", ContentId = "command.wand", Revision = 1,
                LocationKind = 3, ParentInstanceId = "", Slot = 1, UseTemplateWandStats = true };
            items[2] = new OnlineOwnedItemSnapshot { InstanceId = "card", ContentId = "command.card", Revision = 1,
                LocationKind = 2, ParentInstanceId = "wand", RemainingCharges = 1 };
            if (secondCard) items[3] = new OnlineOwnedItemSnapshot { InstanceId = "card2", ContentId = "command.card", Revision = 1,
                LocationKind = 2, ParentInstanceId = "wand2", RemainingCharges = 1 };
            if (bag) items[items.Length - 1] = new OnlineOwnedItemSnapshot { InstanceId = "bag", ContentId = "command.bag", Revision = 1,
                LocationKind = 4, ParentInstanceId = "" };
            Assert.That(OnlineOwnedWorkspaceFactory.TryCreate(_catalog, new OnlineOwnedAssetSnapshot { SchemaVersion = OnlineOwnedAssetSnapshot.CurrentSchemaVersion,
                RunId = "run", PlayerId = "player", ActorId = 1, InventoryId = "inventory", InventoryVersion = 9,
                GameplayConfigHash = Hash, SelectedWandInstanceId = "wand", Items = items }, Hash, out var workspace, out var error), Is.True, error);
            return workspace;
        }
        private static object Intent(OnlineOwnedWorkspace workspace, ulong id, int kind, string item)
        {
            var type = Contract("OnlineInventoryCommandIntent"); Assert.That(type, Is.Not.Null, "先建立 Core 值命令合同。");
            var value = Activator.CreateInstance(type); Set(value, "CommandId", id); Set(value, "ConnectionEpoch", 7u);
            Set(value, "ExpectedInventoryVersion", workspace.InventoryVersion); Set(value, "Kind", Enum.ToObject(Contract("OnlineInventoryCommandKind"), kind));
            Set(value, "ItemId", item); return value;
        }
        private static object Storage(OnlineOwnedWorkspace workspace, ulong id)
        { var value = Intent(workspace, id, 2, "card"); Set(value, "SourceWandId", "wand");
            Set(value, "SourceProgramRevision", Revision(workspace, "wand")); Set(value, "ToSpellStorage", true); return value; }
        private static uint Revision(OnlineOwnedWorkspace workspace, string id)
        { Assert.That(workspace.WandsReadOnly.TryGetProgram(id, out var program), Is.True); return program.ProgramRevision; }
        private static OnlineOwnedItemSnapshot Find(OnlineInventoryPresentationSnapshot snapshot, string id)
        { foreach (var item in snapshot.Assets.Items) if (item.InstanceId == id) return item; Assert.Fail("缺少实例"); return null; }
        private static Type Contract(string name) => typeof(GameLog).Assembly.GetType("Game.Core." + name);
        private static void Set(object value, string name, object field) => value.GetType().GetField(name).SetValue(value, field);
        private static object Get(object value, string name) => value.GetType().GetField(name)?.GetValue(value)
            ?? value.GetType().GetProperty(name)?.GetValue(value);
        private static object Execute(OnlineOwnedWorkspace workspace, object value)
        { var method = typeof(OnlineOwnedWorkspace).GetMethod("ExecuteInventoryCommand", new[] { Contract("OnlineInventoryCommandIntent").MakeByRefType() }); Assert.That(method, Is.Not.Null);
            return method.Invoke(workspace, new[] { value }); }
        private static void AssertFailure(object receipt, string expected) => Assert.That(Get(receipt, "Failure").ToString(), Is.EqualTo(expected));
    }
}
