using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Core;
using NUnit.Framework;

namespace Game.Run.Tests
{
    /// <summary>
    /// 个人退出只Fence本人新意图；已提交材料的世界写门仍由原World控制。
    /// 首轮用Reflection发现缺口，避免为等实现使整个Editor编译失败。只用内存目录/工作区，不读写真实资产。
    /// </summary>
    public sealed class RunMemberExitWorkspaceTests : InventoryTestFixture
    {
        private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private const string Command = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string OtherCommand = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private const string Wand = "11111111111111111111111111111111";
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly List<OnlineOwnedWorkspace> _workspaces = new List<OnlineOwnedWorkspace>();

        [TearDown] public void ReleaseWorkspaces()
        {
            foreach (var workspace in _workspaces) workspace.Dispose();
            _workspaces.Clear();
        }

        private OnlineOwnedWorkspace Create()
        {
            var catalog = Store(Define("exit.wand", ItemKind.Wand)).Catalog;
            var seed = new OnlineOwnedAssetSnapshot
            {
                SchemaVersion = OnlineOwnedAssetSnapshot.CurrentSchemaVersion, RunId = "run", PlayerId = "player",
                ActorId = 1, InventoryId = "run/player", InventoryVersion = 9, GameplayConfigHash = Hash,
                Gold = 12345, CurrencyVersion = 4, SelectedWandInstanceId = Wand,
                Items = new[] { new OnlineOwnedItemSnapshot { InstanceId = Wand, ContentId = "exit.wand", Revision = 7,
                    ParentInstanceId = "", LocationKind = (int)ItemLocationKind.EquippedWand, Slot = 0, UseTemplateWandStats = true } }
            };
            Assert.That(OnlineOwnedWorkspaceFactory.TryCreate(catalog, seed, Hash, out var workspace, out var error), Is.True, error);
            _workspaces.Add(workspace);
            Assert.That(workspace.ConfigureCheckpointJournal(new OnlineRunItemRetirementJournal(8)), Is.True);
            return workspace;
        }

        private static bool Begin(OnlineOwnedWorkspace workspace, string command)
        {
            var method = typeof(OnlineOwnedWorkspace).GetMethod("TryBeginMemberExit", All);
            Assert.That(method, Is.Not.Null, "缺少只针对本Owner的退出意图栅栏，不能套全局Frozen。");
            return (bool)method.Invoke(workspace, new object[] { command });
        }
        private static bool AcceptsNew(OnlineOwnedWorkspace workspace)
        {
            var property = typeof(OnlineOwnedWorkspace).GetProperty("CanAcceptNewPlayerIntents", All);
            Assert.That(property, Is.Not.Null, "新意图门必须独立于已提交G/供料输出的World写门。");
            return (bool)property.GetValue(workspace);
        }
        private static bool Capture(OnlineOwnedWorkspace workspace, string command, out OnlineOwnedAssetSnapshot assets)
        {
            var method = typeof(OnlineOwnedWorkspace).GetMethod("TryCaptureMemberExitAssets", All);
            Assert.That(method, Is.Not.Null, "必须从存活Owner取安全当前值，不能回Entry或由Client上传资产。");
            object[] args = { command, null };
            bool result = (bool)method.Invoke(workspace, args); assets = args[1] as OnlineOwnedAssetSnapshot;
            return result;
        }

        [TestCase(null)] [TestCase("")] [TestCase("not-a-stable-id")]
        public void InvalidCommandCannotFenceTheOwner(string command)
        {
            var workspace = Create(); Assert.That(Begin(workspace, command), Is.False);
            Assert.That(AcceptsNew(workspace), Is.True);
        }

        [Test] public void SameCommandReplaysFenceAndAnotherCommandCannotReplaceIt()
        {
            var workspace = Create();
            Assert.That(Begin(workspace, Command), Is.True);
            Assert.That(Begin(workspace, Command), Is.True);
            Assert.That(Begin(workspace, OtherCommand), Is.False);
            Assert.That(AcceptsNew(workspace), Is.False);
            Assert.That(Capture(workspace, Command, out var assets), Is.True);
            Assert.That(assets.Gold, Is.EqualTo(12345)); Assert.That(assets.CurrencyVersion, Is.EqualTo(4u));
            Assert.That(Capture(workspace, OtherCommand, out _), Is.False);
        }

        [Test] public void PersonalFenceRejectsNewInventoryButRetainsOriginalReceiptAndWorldOutputGate()
        {
            var workspace = Create();
            workspace.ConfigureAuthorityWriteGate(() => true);
            var original = new OnlineInventoryCommandIntent { CommandId = 1, ConnectionEpoch = 7,
                ExpectedInventoryVersion = workspace.InventoryVersion, Kind = OnlineInventoryCommandKind.SelectWand, ItemId = Wand, TargetSlot = 0 };
            var receipt = workspace.ExecuteInventoryCommand(original);
            Assert.That(receipt.Success, Is.True);
            uint version = workspace.InventoryVersion;
            Assert.That(Begin(workspace, Command), Is.True);
            Assert.That(AcceptsNew(workspace), Is.False);
            Assert.That(workspace.ExecuteInventoryCommand(original).Failure, Is.EqualTo(receipt.Failure),
                "已提交命令仍重放原收据，退出不能重写它。");
            var next = original; next.CommandId = 2;
            Assert.That(workspace.ExecuteInventoryCommand(next).Success, Is.False);
            Assert.That(workspace.InventoryVersion, Is.EqualTo(version));
            var gate = typeof(OnlineOwnedWorkspace).GetProperty("AuthorityWritesAllowed", All);
            Assert.That(gate, Is.Not.Null);
            Assert.That((bool)gate.GetValue(workspace), Is.True,
                "个人退出不封已提交材料/Impact/供料所需的原World写门。");
            Assert.That(workspace.WorldActionsAllowed, Is.True,
                "已接受动作的Adapter/供料仍读取原WorldActionsAllowed；新施法须在Driver意图入口单独Fence。");
        }

        [Test] public void CaptureRequiresMatchingFenceAndCopiesCurrentOwnedGoldAndInstances()
        {
            var workspace = Create();
            Assert.That(Capture(workspace, Command, out _), Is.False);
            Assert.That(Begin(workspace, Command), Is.True);
            Assert.That(Capture(workspace, Command, out var first), Is.True);
            Assert.That(first.SchemaVersion, Is.EqualTo(OnlineOwnedAssetSnapshot.CurrentSchemaVersion));
            Assert.That(first.PlayerId, Is.EqualTo("player")); Assert.That(first.RunId, Is.EqualTo("run"));
            Assert.That(first.Gold, Is.EqualTo(12345)); Assert.That(first.CurrencyVersion, Is.EqualTo(4u));
            Assert.That(first.Items.Length, Is.EqualTo(1)); Assert.That(first.Items[0].InstanceId, Is.EqualTo(Wand));
            first.Items[0].Revision = 999;
            Assert.That(Capture(workspace, Command, out var next), Is.True);
            Assert.That(next.Items[0].Revision, Is.EqualTo(7));
            Assert.That(next.Items, Is.Not.SameAs(first.Items));
        }

        [Test] public void OutstandingMaterialOwnerPreventsExitCaptureWithoutDiscardingItsQuantity()
        {
            var workspace = Create();
            var profile = (SinglePlayerProfile)typeof(OnlineOwnedWorkspace).GetField("_profile", All).GetValue(workspace);
            Assert.That(Begin(workspace, Command), Is.True);
            // 原Profile计数标识仍在别的Owner推进的已扣量输出。观测门不能为了退出直接清零/EndAll。
            profile.PendingMaterialOutputs = 1;
            try
            {
                Assert.That(Capture(workspace, Command, out _), Is.False);
                Assert.That(profile.PendingMaterialOutputs, Is.EqualTo(1));
                Assert.That(profile.Currency, Is.EqualTo(12345));
            }
            finally { profile.PendingMaterialOutputs = 0; }
            Assert.That(Capture(workspace, Command, out _), Is.True);
        }

        [Test] public void ConfirmedSupersededCommandCanBeReplacedWithoutReopeningOwnerWrites()
        {
            var workspace = Create(); Assert.That(Begin(workspace, Command), Is.True);
            var replace = typeof(OnlineOwnedWorkspace).GetMethod("TryReplaceRejectedMemberExit", All);
            Assert.That(replace, Is.Not.Null, "只有组合层拿到已证拒绝后才可替换Fence的命令，不能先解除写门。");
            Assert.That((bool)replace.Invoke(workspace, new object[] { Command, OtherCommand }), Is.True);
            Assert.That(AcceptsNew(workspace), Is.False);
            Assert.That(Capture(workspace, Command, out _), Is.False);
            Assert.That(Capture(workspace, OtherCommand, out var assets), Is.True);
            Assert.That(assets.Gold, Is.EqualTo(12345)); Assert.That(assets.Items[0].InstanceId, Is.EqualTo(Wand));
            Assert.That(Begin(workspace, Command), Is.False, "旧ID迟到不能重新激活已替换候选。");
        }
        [Test] public void MismatchedRejectedCommandCannotReplaceCurrentFence()
        {
            var workspace = Create(); Assert.That(Begin(workspace, Command), Is.True);
            var replace = typeof(OnlineOwnedWorkspace).GetMethod("TryReplaceRejectedMemberExit", All);
            Assert.That(replace, Is.Not.Null);
            Assert.That((bool)replace.Invoke(workspace, new object[] { OtherCommand, "cccccccccccccccccccccccccccccccc" }), Is.False);
            Assert.That(AcceptsNew(workspace), Is.False);
            Assert.That(Capture(workspace, Command, out _), Is.True);
        }
    }
}
