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
            _catalog = Store(Define("command.wand", ItemKind.Wand), card).Catalog;
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

        [Test] public void StaleVersionsAndOccupiedDestinationRejectWithoutMovingEitherCard()
        {
            using var workspace = Create(true); uint before = workspace.InventoryVersion;
            var move = Storage(workspace, 1); Set(move, "ExpectedInventoryVersion", before - 1);
            AssertFailure(Execute(workspace, move), "StaleVersion");
            move = Storage(workspace, 2); Set(move, "SourceProgramRevision", Revision(workspace, "wand") + 1);
            AssertFailure(Execute(workspace, move), "StaleProgram");
            move = Intent(workspace, 3, 2, "card"); Set(move, "SourceWandId", "wand");
            Set(move, "SourceProgramRevision", Revision(workspace, "wand")); Set(move, "TargetWandId", "wand2");
            Set(move, "TargetProgramRevision", Revision(workspace, "wand2"));
            Assert.That((bool)Get(Execute(workspace, move), "Success"), Is.False);
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

        private OnlineOwnedWorkspace Create(bool secondCard = false)
        {
            var items = new OnlineOwnedItemSnapshot[secondCard ? 4 : 3];
            items[0] = new OnlineOwnedItemSnapshot { InstanceId = "wand", ContentId = "command.wand", Revision = 1,
                LocationKind = 3, ParentInstanceId = "", UseTemplateWandStats = true };
            items[1] = new OnlineOwnedItemSnapshot { InstanceId = "wand2", ContentId = "command.wand", Revision = 1,
                LocationKind = 3, ParentInstanceId = "", Slot = 1, UseTemplateWandStats = true };
            items[2] = new OnlineOwnedItemSnapshot { InstanceId = "card", ContentId = "command.card", Revision = 1,
                LocationKind = 2, ParentInstanceId = "wand", RemainingCharges = 1 };
            if (secondCard) items[3] = new OnlineOwnedItemSnapshot { InstanceId = "card2", ContentId = "command.card", Revision = 1,
                LocationKind = 2, ParentInstanceId = "wand2", RemainingCharges = 1 };
            Assert.That(OnlineOwnedWorkspaceFactory.TryCreate(_catalog, new OnlineOwnedAssetSnapshot { SchemaVersion = 1,
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
        { var method = typeof(OnlineOwnedWorkspace).GetMethod("ExecuteInventoryCommand"); Assert.That(method, Is.Not.Null);
            return method.Invoke(workspace, new[] { value }); }
        private static void AssertFailure(object receipt, string expected) => Assert.That(Get(receipt, "Failure").ToString(), Is.EqualTo(expected));
    }
}
