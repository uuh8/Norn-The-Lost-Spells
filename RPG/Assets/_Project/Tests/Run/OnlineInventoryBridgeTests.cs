using System;
using System.Reflection;
using Game.Core;
using Game.Skills;
using NUnit.Framework;

namespace Game.Run.Tests
{
    /// <summary>验证 Authority 导出与 Client 资源安装的边界；全程仅使用临时内存目录，不读取玩家档。</summary>
    public sealed class OnlineInventoryBridgeTests : InventoryTestFixture
    {
        private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private ItemCatalogIndex _catalog;
        [SetUp] public void CreateMemoryCatalog()
        {
            var card = Define("bridge.spell", ItemKind.Spell); card.SpellDefinition.Kind = SpellKind.Modify;
            _catalog = Store(Define("bridge.wand", ItemKind.Wand), card).Catalog;
        }

        [Test] public void InventoryValueContractBelongsToCoreAndCarriesAuthorityTickBaseline()
        {
            var type = typeof(GameLog).Assembly.GetType("Game.Core.OnlineInventoryPresentationSnapshot");
            Assert.That(type, Is.Not.Null, "网络层不能依赖 Run；纯库存值合同必须下沉 Core。");
            Assert.That(type.GetField("ServerTick"), Is.Not.Null, "完整快照需携带资源基线，不能让旧状态覆盖较新 Mana。");
            foreach (var reference in type.Assembly.GetReferencedAssemblies())
                Assert.That(reference.Name == "Game.Run" || reference.Name == "Game.Character", Is.False);
        }

        [Test] public void CaptureReadsCurrentQualificationAndKeepsOriginalItemRevisionWithoutChargingAgain()
        {
            Assert.That(OnlineOwnedWorkspaceFactory.TryCreate(_catalog, Assets(1), Hash, out var workspace, out var error), Is.True, error);
            using (workspace)
            {
                Assert.That(workspace.TryGetItem("card", out var before), Is.True);
                Assert.That(before.RemainingCharges, Is.Zero, "最后一次资格已由真实 Authority Factory 在入局时提交。");
                Assert.That(workspace.WandsReadOnly.TryGetProgram("wand", out var program), Is.True);
                // 这里验证导出读取的是当前服务余额，单独调用真实资源服务，不冒充完整玩家施法验收。
                var resourceService = (WandLoadoutService)typeof(OnlineOwnedWorkspace).GetField("_wands",
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(workspace);
                Assert.That(resourceService.Execute(new WandResourceRequestedEvent
                { InventoryId = "inventory", SourceWandId = "wand", CallerId = "capture-test", RequestId = 1,
                    ProgramRevision = program.ProgramRevision, ManaCost = 20, IsRootCast = true }).Success, Is.True);
                var method = typeof(OnlineOwnedWorkspace).GetMethod("TryCapturePresentation");
                Assert.That(method, Is.Not.Null, "重连必须从存活 Workspace 导出，而非使用过期 EntrySnapshot。");
                object[] args = { 7u, 4ul, 22u, null };
                Assert.That((bool)method.Invoke(workspace, args), Is.True);
                var captured = args[3];
                var assets = (OnlineOwnedAssetSnapshot)Get(captured, "Assets");
                Assert.That(assets.InventoryVersion, Is.EqualTo(workspace.InventoryVersion));
                Assert.That(assets.Items[1].RemainingCharges, Is.Zero);
                Assert.That(assets.Items[1].Revision, Is.EqualTo(13), "Store Version 不能冒充数据库物品 Revision。");
                Assert.That((uint)Get(captured, "ServerTick"), Is.EqualTo(22));
                Assert.That((float)Get(((Array)Get(captured, "Wands")).GetValue(0), "CurrentMana"), Is.EqualTo(80f),
                    "当前工作区已花20 Mana，不能导出业务入局时的默认满蓝。");
                var uses = (Array)Get(captured, "SpellUses");
                Assert.That((bool)Get(uses.GetValue(0), "Eligible"), Is.True);
                Assert.That(workspace.TryGetItem("card", out var after), Is.True);
                Assert.That(after.RemainingCharges, Is.Zero, "导出纯值不能再执行资格扣费。");
                assets.Items[1].RemainingCharges = 999;
                Assert.That(after.RemainingCharges, Is.Zero, "导出数组不能继续写回权威 Store。");
                workspace.Dispose();
                args = new object[] { 7u, 5ul, 23u, null };
                Assert.That((bool)method.Invoke(workspace, args), Is.False, "退出工作区不能再导出旧资产。");
            }
        }

        [Test] public void ResourceUpdatesKeepReadInterfacesAndItemVersionStable()
        {
            var source = CreateSource(); var read = source.ReadOnly; var wands = source.WandsReadOnly;
            var method = source.GetType().GetMethod("TryApplyResources");
            Assert.That(method, Is.Not.Null, "Mana/CD 高频更新不能调用完整 TryInstall 重建所有数组。");
            Assert.That(Apply(method, source, 7, 11, "wand", 17, 21f, .5f), Is.True);
            Assert.That(source.ReadOnly, Is.SameAs(read)); Assert.That(source.WandsReadOnly, Is.SameAs(wands));
            Assert.That(read.Version, Is.EqualTo(9)); Assert.That(read.Count, Is.EqualTo(2));
            Assert.That(wands.TryGetState("wand", out var state), Is.True);
            Assert.That(state.CurrentMana, Is.EqualTo(21f)); Assert.That(state.CooldownRemaining, Is.EqualTo(.5f));
            Assert.That(read.TryGetView("card", out var card), Is.True); Assert.That(card.RemainingCharges, Is.EqualTo(3));
            Assert.That(Apply(method, source, 7, 11, "wand", 17, 99f, 0), Is.False);
            Assert.That(Apply(method, source, 7, 10, "wand", 17, 99f, 0), Is.False);
            Assert.That(Apply(method, source, 6, 12, "wand", 17, 99f, 0), Is.False);
            Assert.That(Apply(method, source, 7, 12, "foreign", 17, 99f, 0), Is.False);
            Assert.That(Apply(method, source, 7, 12, "wand", 18, 99f, 0), Is.False);
            Assert.That(Apply(method, source, 7, 12, "wand", 17, float.NaN, 0), Is.False);
            Assert.That(wands.TryGetState("wand", out state), Is.True); Assert.That(state.CurrentMana, Is.EqualTo(21f));
            ((IDisposable)source).Dispose();
            Assert.That(Apply(method, source, 7, 12, "wand", 17, 22f, 0), Is.False);
        }

        [Test] public void FullSnapshotAfterResourceUpdatesStillUsesServerVersionNotClientRefreshCount()
        {
            var source = CreateSource(); var method = source.GetType().GetMethod("TryApplyResources");
            Assert.That(method, Is.Not.Null);
            for (uint tick = 11; tick < 30; tick++) Assert.That(Apply(method, source, 7, tick, "wand", 17, tick, 0), Is.True);
            var next = Snapshot(2, 30); Set(next, "ResourceVersion", 13u);
            var args = new[] { next, null };
            Assert.That((bool)source.GetType().GetMethod("TryInstall").Invoke(source, args), Is.True,
                "本地显示刷新次数不能与 Server ResourceVersion 混用，导致合法完整快照永久被拒绝。");
            Assert.That(source.WandsReadOnly.TryGetState("wand", out var state), Is.True);
            Assert.That(state.CurrentMana, Is.EqualTo(37f));
        }

        private IInventoryPresentationSource CreateSource()
        {
            var identity = Activator.CreateInstance(Contract("OnlineInventoryPresentationIdentity"), "run", "player", 1ul,
                "inventory", Hash, 7u, "World");
            var args = new[] { (object)_catalog, identity, Snapshot(1, 10), null, null };
            Assert.That((bool)typeof(OnlineInventoryPresentationSource).GetMethod("TryCreate").Invoke(null, args), Is.True, args[4] as string);
            return (IInventoryPresentationSource)args[3];
        }
        private static object Snapshot(ulong sequence, uint tick)
        {
            var snapshot = Activator.CreateInstance(Contract("OnlineInventoryPresentationSnapshot"));
            Set(snapshot, "SchemaVersion", 1u); Set(snapshot, "ConnectionEpoch", 7u); Set(snapshot, "SnapshotSequence", sequence);
            Set(snapshot, "ResourceVersion", 12u); Set(snapshot, "SelectionVersion", 5u); Set(snapshot, "SpellUseVersion", 4u);
            if (snapshot.GetType().GetField("ServerTick") != null) Set(snapshot, "ServerTick", tick);
            Set(snapshot, "Assets", Assets(3));
            var wand = Activator.CreateInstance(Contract("OnlineInventoryWandPresentationSnapshot"));
            Set(wand, "InstanceId", "wand"); Set(wand, "ProgramRevision", 17u); Set(wand, "CurrentMana", 37f); Set(wand, "CooldownRemaining", 1.25f);
            var wands = Array.CreateInstance(wand.GetType(), 1); wands.SetValue(wand, 0); Set(snapshot, "Wands", wands);
            var use = Activator.CreateInstance(Contract("OnlineInventorySpellUsePresentationSnapshot"));
            Set(use, "InstanceId", "card"); Set(use, "Eligible", true);
            var uses = Array.CreateInstance(use.GetType(), 1); uses.SetValue(use, 0); Set(snapshot, "SpellUses", uses);
            return snapshot;
        }
        private static OnlineOwnedAssetSnapshot Assets(int charges) => new OnlineOwnedAssetSnapshot
        {
            SchemaVersion = 1, ActorId = 1, RunId = "run", PlayerId = "player", InventoryId = "inventory", InventoryVersion = 9,
            GameplayConfigHash = Hash, SelectedWandInstanceId = "wand", Items = new[]
            {
                new OnlineOwnedItemSnapshot { InstanceId = "wand", ContentId = "bridge.wand", Revision = 7,
                    LocationKind = (int)ItemLocationKind.EquippedWand, UseTemplateWandStats = true },
                new OnlineOwnedItemSnapshot { InstanceId = "card", ContentId = "bridge.spell", Revision = 13,
                    LocationKind = (int)ItemLocationKind.WandSlot, ParentInstanceId = "wand", RemainingCharges = charges }
            }
        };
        private static Type Contract(string name) => typeof(GameLog).Assembly.GetType("Game.Core." + name)
            ?? typeof(InventoryReadModel).Assembly.GetType("Game.Run." + name);
        private static object Get(object target, string name) => target.GetType().GetField(name).GetValue(target);
        private static void Set(object target, string name, object value) => target.GetType().GetField(name).SetValue(target, value);
        private static bool Apply(MethodInfo method, IInventoryPresentationSource source, uint epoch, uint tick, string id, uint revision, float mana, float cooldown)
            => (bool)method.Invoke(source, new object[] { epoch, tick, id, revision, mana, cooldown });
    }
}
