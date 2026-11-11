using System;
using System.Reflection;
using Game.Core;
using Game.Skills;
using Game.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

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

        [TestCase(0L)]
        [TestCase(10000L)]
        [TestCase(long.MaxValue)]
        public void OriginalBackpackWalletReadsCurrentSourceWithoutNarrowingBalance(long balance)
        {
            // 钱包只是现有Source的一项只读能力。用原目录/ReadModel保证库存合法，
            // 不创建Repository、线上账号或会自动扣量的Authority Workspace。
            var store = Store(Define("wallet.view.item", ItemKind.Wand));
            var profile = new SinglePlayerProfile("wallet-view-player", store);
            var read = new InventoryReadModel(profile, "wallet-view");
            var source = new WalletPresentationSource(read, new WalletValue(balance));
            var root = Track(new GameObject("Inactive wallet UI fixture")); root.SetActive(false);
            var controller = root.AddComponent<SinglePlayerInventoryController>();
            var view = root.AddComponent<BackpackWorkspaceView>();
            var textRoot = Track(new GameObject("Wallet text", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text)));
            textRoot.SetActive(false); var text = textRoot.GetComponent<Text>();
            view.Configure(null, text, null); controller.BindPresentationSource(source);
            uint inventoryVersion = store.Version;

            view.Refresh(controller, Array.Empty<InventoryItemSnapshot>(), 0, null);

            Assert.That(text.text, Is.EqualTo(balance.ToString()), "必须沿当前Source读完整long；合法0不能显示不可用。");
            Assert.That(store.Version, Is.EqualTo(inventoryVersion));
            Assert.That(profile.Currency, Is.Zero, "显示外部钱包不把余额写入另一份本地Profile。");
            Assert.That(source.WalletReadOnly.Currency, Is.EqualTo(balance));
        }

        [Test]
        public void OriginalBackpackWalletSwitchesSourceAndExplicitUnbindClearsPreviousValue()
        {
            var store = Store(Define("wallet.switch.item", ItemKind.Wand));
            var read = new InventoryReadModel(new SinglePlayerProfile("wallet-switch-player", store), "wallet-switch");
            var first = new WalletPresentationSource(read, new WalletValue(10000));
            var second = new WalletPresentationSource(read, new WalletValue(23));
            var root = Track(new GameObject("Inactive wallet switch fixture")); root.SetActive(false);
            var controller = root.AddComponent<SinglePlayerInventoryController>();
            // 模拟Inspector仍有另一份本地Session引用，证明显式缺能力/解绑不会静默回落。
            // 此Session从不初始化、无Repository且对象始终inactive，只持有夹具内存余额。
            var local = root.AddComponent<SinglePlayerInventorySession>();
            typeof(SinglePlayerInventorySession).GetField("_profile", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(local, new SinglePlayerProfile("local-wallet-sentinel", store, currency: 777777));
            typeof(SinglePlayerInventoryController).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(controller, local);
            var view = root.AddComponent<BackpackWorkspaceView>();
            var textRoot = Track(new GameObject("Wallet switch text", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text)));
            textRoot.SetActive(false); var text = textRoot.GetComponent<Text>(); view.Configure(null, text, null);
            controller.BindPresentationSource(first); view.Refresh(controller, Array.Empty<InventoryItemSnapshot>(), 0, null);
            Assert.That(text.text, Is.EqualTo("10000"));
            controller.BindPresentationSource(second); view.Refresh(controller, Array.Empty<InventoryItemSnapshot>(), 0, null);
            Assert.That(text.text, Is.EqualTo("23"));

            // 缺钱包能力和显式解绑均不能继续显示旧Source的23，也不能伪造为余额0。
            controller.BindPresentationSource(new WalletPresentationSource(read, null));
            view.Refresh(controller, Array.Empty<InventoryItemSnapshot>(), 0, null);
            Assert.That(text.text, Is.EqualTo("—"));
            controller.BindPresentationSource(first); view.Refresh(controller, Array.Empty<InventoryItemSnapshot>(), 0, null);
            Assert.That(text.text, Is.EqualTo("10000"));
            controller.BindPresentationSource(null); view.Refresh(controller, Array.Empty<InventoryItemSnapshot>(), 0, null);
            Assert.That(text.text, Is.EqualTo("—"));
            Assert.That(controller.PresentationSource, Is.Null);
            Assert.That(local.Currency, Is.EqualTo(777777), "外部钱包展示/解绑既不采用本地值也不写回本地余额。");
        }

        [Test]
        public void OriginalBackpackWalletDoesNotRewriteTextWhenBalanceAndAvailabilityStayUnchanged()
        {
            var store = Store(Define("wallet.stable.item", ItemKind.Wand));
            var read = new InventoryReadModel(new SinglePlayerProfile("wallet-stable-player", store), "wallet-stable");
            var wallet = new WalletValue(10000);
            var source = new WalletPresentationSource(read, wallet);
            var root = Track(new GameObject("Inactive stable wallet fixture")); root.SetActive(false);
            var controller = root.AddComponent<SinglePlayerInventoryController>();
            var view = root.AddComponent<BackpackWorkspaceView>();
            var textRoot = Track(new GameObject("Wallet stable text", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text)));
            textRoot.SetActive(false); var text = textRoot.GetComponent<Text>(); view.Configure(null, text, null);
            controller.BindPresentationSource(source); view.Refresh(controller, Array.Empty<InventoryItemSnapshot>(), 0, null);
            Assert.That(text.text, Is.EqualTo("10000"));
            // 只在夹具中放哨兵，区分稳定分支提前return与重新ToString后赋同值的伪稳定。
            // 不据此声称整个Unity帧零GC；这里只证原文本缓存路径不重复写入。
            text.text = "stable-text-sentinel";
            var refresh = typeof(BackpackWorkspaceView).GetMethod("RefreshWallet", BindingFlags.Instance | BindingFlags.NonPublic);
            for (int i = 0; i < 4; i++) refresh.Invoke(view, null);
            Assert.That(text.text, Is.EqualTo("stable-text-sentinel"));
            wallet.Value = 10001;
            refresh.Invoke(view, null);
            Assert.That(text.text, Is.EqualTo("10001"), "真实余额变化仍必须立即刷新。");
        }

        /// <summary>借用既有合法库存读口，仅提供钱包能力；不实现资产写入/保存或模拟网络余额。</summary>
        private sealed class WalletPresentationSource : IInventoryPresentationSource
        {
            public WalletPresentationSource(IInventoryReadOnly read, IWalletReadOnly wallet) { ReadOnly = read; WalletReadOnly = wallet; }
            public IInventoryReadOnly ReadOnly { get; }
            public IWalletReadOnly WalletReadOnly { get; }
            public IToolbeltReadOnly ToolbeltReadOnly => null;
            public IWandLoadoutReadOnly WandsReadOnly => null;
            public ISpellUseReadOnly SpellUsesReadOnly => null;
            public bool UseSpellProgrammingV2 => true;
            public bool CanAccessWarehouse => false;
            public string WorldSceneKey => "wallet-view";
            public string EquippedBackpackId => null;
            public int SpellStorageCapacity => 0;
            public string InitializationMessage => "read-only wallet view fixture";
            public bool HasUnsavedChanges => false;
            public bool TryCaptureWandResearch(string id, out WandResearchSnapshot snapshot) { snapshot = default; return false; }
            public bool TryCopyContainer(string id, MaterialAmount[] buffer, out int count, out ulong total, out ulong capacity)
            { count = 0; total = capacity = 0; return false; }
        }
        private sealed class WalletValue : IWalletReadOnly
        {
            private long _value;
            private uint _version = 1;
            public long Value
            {
                get => _value;
                set { if (_value == value) return; _value = value; _version++; }
            }
            public WalletValue(long value) => _value = value;
            public long Currency => _value;
            public uint Version => _version;
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
            SchemaVersion = OnlineOwnedAssetSnapshot.CurrentSchemaVersion, ActorId = 1, RunId = "run", PlayerId = "player", InventoryId = "inventory", InventoryVersion = 9,
            Gold = 0, CurrencyVersion = 1,
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
