using System;
using System.Reflection;
using Game.Core;
using Game.Skills;
using NUnit.Framework;

namespace Game.Run.Tests
{
    /// <summary>
    /// Client 读模型与 DS Owned 工作区是不同职责：这里仅导入权威值，不安装资格策略、施法事务或资源 Tick。
    /// RED 阶段通过 Reflection 查找新 API，缺实现应表现为清楚的测试失败，而非让整个项目无法编译。
    /// </summary>
    public sealed class OnlineInventoryPresentationSourceTests : InventoryTestFixture
    {
        private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private ItemCatalogIndex _catalog;
        private Type _sourceType;

        [SetUp]
        public void CreateOnlyMemoryCatalog()
        {
            var spell = Define("online.view.spell", ItemKind.Spell);
            spell.SpellDefinition.Kind = SpellKind.Modify;
            _catalog = Store(Define("online.view.wand", ItemKind.Wand), spell).Catalog;
            _sourceType = typeof(InventoryReadModel).Assembly.GetType("Game.Run.OnlineInventoryPresentationSource");
            Assert.That(_sourceType, Is.Not.Null, "缺少纯线上库存 Presentation Source；不能在 Client 重建有写权限的 Owned 工作区。");
        }

        [Test]
        public void ImportKeepsAuthoritativeManaCooldownProgramRevisionAndSpellUses()
        {
            var source = Create(Snapshot());
            Assert.That(source.ReadOnly.InventoryId, Is.EqualTo("inventory"));
            Assert.That(source.ReadOnly.ActorId, Is.EqualTo("player"));
            Assert.That(source.ReadOnly.Version, Is.EqualTo(9));
            Assert.That(source.ReadOnly.Count, Is.EqualTo(2));
            Assert.That(source.ReadOnly.TryGetView("card", out var card), Is.True);
            Assert.That(card.Location.Kind, Is.EqualTo(ItemLocationKind.WandSlot));
            Assert.That(card.Location.OwnerId, Is.EqualTo("wand"));
            Assert.That(card.RemainingCharges, Is.EqualTo(3), "读模型不能像入局 Authority Factory 一样再次扣资格或次数。");
            Assert.That(source.WandsReadOnly.TryGetSlot(0, out var wand), Is.True);
            Assert.That(wand.CurrentMana, Is.EqualTo(37f), "重连应显示服务器当前 Mana，不能在 Client 构造默认满蓝。");
            Assert.That(wand.CooldownRemaining, Is.EqualTo(1.25f));
            Assert.That(source.WandsReadOnly.TryGetProgram("wand", out var program), Is.True);
            Assert.That(program.ProgramRevision, Is.EqualTo(17));
            Assert.That(program.Program.GetInstanceId(0), Is.EqualTo("card"));
            Assert.That(source.SpellUsesReadOnly.TryGetState("card", out var uses), Is.True);
            Assert.That(uses.RemainingCharges, Is.EqualTo(3)); Assert.That(uses.Eligible, Is.True);
        }

        [Test]
        public void StableReadInterfacesInstallNewValuesWithoutReplacingUiSource()
        {
            var source = Create(Snapshot()); var read = source.ReadOnly; var wands = source.WandsReadOnly;
            wands.TryGetProgram("wand", out var oldProgram);
            var next = Snapshot(2); Set(Wands(next).GetValue(0), "CurrentMana", 19f);
            Set(Wands(next).GetValue(0), "ProgramRevision", 22u);
            Set(next, "ResourceVersion", 13u); Assets(next).InventoryVersion = 10;
            Assets(next).Items[1].RemainingCharges = 1;
            Assert.That(Install(source, next, out _), Is.True);
            Assert.That(source.ReadOnly, Is.SameAs(read)); Assert.That(source.WandsReadOnly, Is.SameAs(wands));
            Assert.That(wands.TryGetState("wand", out var state), Is.True); Assert.That(state.CurrentMana, Is.EqualTo(19f));
            Assert.That(wands.TryGetProgram("wand", out var program), Is.True); Assert.That(program.ProgramRevision, Is.EqualTo(22));
            Assert.That(oldProgram.ProgramRevision, Is.EqualTo(17), "已捕获的旧程序快照不能被后来的网络安装原地覆盖。");
            Assert.That(read.TryGetView("card", out var card), Is.True); Assert.That(card.RemainingCharges, Is.EqualTo(1));
        }

        [Test]
        public void MutableIncomingArraysCannotRewritePublishedValues()
        {
            var incoming = Snapshot(); var source = Create(incoming);
            Assets(incoming).Items[1].RemainingCharges = 999;
            Set(Wands(incoming).GetValue(0), "CurrentMana", 999f);
            Set(SpellUses(incoming).GetValue(0), "Eligible", false);
            Assert.That(source.ReadOnly.TryGetView("card", out var card), Is.True); Assert.That(card.RemainingCharges, Is.EqualTo(3));
            source.WandsReadOnly.TryGetState("wand", out var wand); Assert.That(wand.CurrentMana, Is.EqualTo(37f));
            source.SpellUsesReadOnly.TryGetState("card", out var use); Assert.That(use.Eligible, Is.True);
        }

        [TestCase("RunId", "foreign-run")]
        [TestCase("PlayerId", "foreign-player")]
        [TestCase("InventoryId", "foreign-inventory")]
        [TestCase("GameplayConfigHash", "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
        public void ForeignIdentityNeverPublishesAReadModel(string field, string value)
        {
            var incoming = Snapshot(); Set(Assets(incoming), field, value);
            Assert.That(TryCreate(incoming, out var source, out var error), Is.False);
            Assert.That(source, Is.Null); Assert.That(error, Is.Not.Empty);
        }

        [Test]
        public void WrongEpochAndStaleSequenceCannotOverwriteCurrentState()
        {
            var source = Create(Snapshot(5));
            var old = Snapshot(4); Set(Wands(old).GetValue(0), "CurrentMana", 99f);
            Assert.That(Install(source, old, out _), Is.False);
            var previousConnection = Snapshot(6); Set(previousConnection, "ConnectionEpoch", 6u);
            Assert.That(Install(source, previousConnection, out _), Is.False);
            source.WandsReadOnly.TryGetState("wand", out var current); Assert.That(current.CurrentMana, Is.EqualTo(37f));
        }

        [Test]
        public void OrphanInstalledCardAndFlattenedBackpackAreRejected()
        {
            var incoming = Snapshot(); Assets(incoming).Items[1].ParentInstanceId = "missing-wand";
            Assert.That(TryCreate(incoming, out _, out _), Is.False);
            incoming = Snapshot(); Assets(incoming).Items[1].LocationKind = (int)ItemLocationKind.Backpack;
            Assets(incoming).Items[1].ParentInstanceId = "";
            Assert.That(TryCreate(incoming, out _, out _), Is.False,
                "业务平面 Backpack 槽不是实际 Bag/grid，Client 不能为了显示而发一只虚构背包。");
        }

        [Test]
        public void MissingResourceOrEligibilityStateNeverUsesClientDefaults()
        {
            var incoming = Snapshot(); Set(incoming, "Wands", Array.CreateInstance(Wands(incoming).GetType().GetElementType(), 0));
            Assert.That(TryCreate(incoming, out _, out _), Is.False, "不能因缺少服务器资源读数而默认满 Mana。");
            incoming = Snapshot(); Set(incoming, "SpellUses", Array.CreateInstance(SpellUses(incoming).GetType().GetElementType(), 0));
            Assert.That(TryCreate(incoming, out _, out _), Is.False, "不能在 Client 推导已取得本局资格。");
        }

        [Test]
        public void InvalidReplacementLeavesPreviousInventoryAndResourcesIntact()
        {
            var source = Create(Snapshot());
            var incoming = Snapshot(2); Set(Wands(incoming).GetValue(0), "CurrentMana", float.NaN);
            Assets(incoming).Items[1].RemainingCharges = 1;
            Assert.That(Install(source, incoming, out _), Is.False);
            source.WandsReadOnly.TryGetState("wand", out var state); Assert.That(state.CurrentMana, Is.EqualTo(37f));
            source.ReadOnly.TryGetView("card", out var card); Assert.That(card.RemainingCharges, Is.EqualTo(3));
        }

        [Test]
        public void ReadSourceDoesNotHandleAuthorityRequestsOrRegenerateResources()
        {
            var source = Create(Snapshot()); int responses = 0;
            Action<CastResourceResultEvent> receive = _ => responses++;
            EventBus<CastResourceResultEvent>.Subscribe(receive);
            try
            {
                EventBus<CastResourceRequestedEvent>.Publish(new CastResourceRequestedEvent
                { InventoryId = "inventory", SourceWandId = "wand", UseV2 = true, CallerId = "view", RequestId = 1,
                    Operation = CastResourceOperation.PrepareRoot });
                EventBus<WandSelectionRequestedEvent>.Publish(new WandSelectionRequestedEvent { InventoryId = "inventory", SlotIndex = 1 });
                Assert.That(responses, Is.Zero, "Presentation Source 不注册执行命令或返回权威收据。");
                source.WandsReadOnly.TryGetState("wand", out var state); Assert.That(state.CurrentMana, Is.EqualTo(37f));
                Assert.That(source.WandsReadOnly.SelectedWandId, Is.EqualTo("wand"));
            }
            finally { EventBus<CastResourceResultEvent>.Unsubscribe(receive); }
        }

        [Test]
        public void MissingBagAndUnsupportedCapabilitiesFailClosed()
        {
            var source = Create(Snapshot());
            Assert.That(source.EquippedBackpackId, Is.Null);
            Assert.That(source.SpellStorageCapacity, Is.EqualTo(_catalog.SpellStorageCapacity),
                "随身法术专区是已有根位置7；提供目录容量不代表创建普通Bag或仓库权限。");
            Assert.That(source.CanAccessWarehouse, Is.False); Assert.That(source.WalletReadOnly, Is.Null);
            Assert.That(source.TryCaptureWandResearch("wand", out _), Is.False);
            Assert.That(source.TryCopyContainer("card", new MaterialAmount[1], out _, out _, out _), Is.False);
            Assert.That(source.ToolbeltReadOnly.SelectedSlot, Is.Zero);
            Assert.That(source.ToolbeltReadOnly.TryGetBinding(0, out _), Is.False);
        }

        [Test]
        public void DisposeClearsPreviousReadReferencesAndRejectsLateSnapshots()
        {
            var source = Create(Snapshot()); var read = source.ReadOnly; var wands = source.WandsReadOnly;
            ((IDisposable)source).Dispose();
            Assert.That(read.Count, Is.Zero); Assert.That(read.TryGetView("card", out _), Is.False);
            Assert.That(wands.SelectedWandId, Is.Null); Assert.That(wands.TryGetState("wand", out _), Is.False);
            Assert.That(Install(source, Snapshot(2), out _), Is.False);
        }

        private IInventoryPresentationSource Create(object snapshot)
        { Assert.That(TryCreate(snapshot, out var source, out var error), Is.True, error); return source; }

        private bool TryCreate(object snapshot, out IInventoryPresentationSource source, out string error)
        {
            var identityType = RunType("OnlineInventoryPresentationIdentity");
            var identity = Activator.CreateInstance(identityType, "run", "player", 1ul, "inventory", Hash, 7u, "DungeonWorld");
            var arguments = new[] { (object)_catalog, identity, snapshot, null, null };
            bool accepted = (bool)_sourceType.GetMethod("TryCreate", BindingFlags.Public | BindingFlags.Static).Invoke(null, arguments);
            source = (IInventoryPresentationSource)arguments[3]; error = (string)arguments[4]; return accepted;
        }

        private static bool Install(IInventoryPresentationSource source, object snapshot, out string error)
        {
            var arguments = new[] { snapshot, null };
            bool accepted = (bool)source.GetType().GetMethod("TryInstall").Invoke(source, arguments);
            error = (string)arguments[1]; return accepted;
        }

        private static object Snapshot(ulong sequence = 1)
        {
            var snapshot = Activator.CreateInstance(RunType("OnlineInventoryPresentationSnapshot"));
            Set(snapshot, "SchemaVersion", 1u); Set(snapshot, "ConnectionEpoch", 7u); Set(snapshot, "SnapshotSequence", sequence);
            Set(snapshot, "ResourceVersion", 12u); Set(snapshot, "SelectionVersion", 5u); Set(snapshot, "SpellUseVersion", 4u);
            Set(snapshot, "Assets", new OnlineOwnedAssetSnapshot
            {
                SchemaVersion = 1, ActorId = 1, RunId = "run", PlayerId = "player", InventoryId = "inventory",
                GameplayConfigHash = Hash, InventoryVersion = 9, SelectedWandInstanceId = "wand",
                Items = new[]
                {
                    new OnlineOwnedItemSnapshot { InstanceId = "wand", ContentId = "online.view.wand", Revision = 1,
                        LocationKind = (int)ItemLocationKind.EquippedWand, Slot = 0, UseTemplateWandStats = true },
                    new OnlineOwnedItemSnapshot { InstanceId = "card", ContentId = "online.view.spell", Revision = 1,
                        LocationKind = (int)ItemLocationKind.WandSlot, Slot = 0, ParentInstanceId = "wand", RemainingCharges = 3 }
                }
            });
            var wand = Activator.CreateInstance(RunType("OnlineInventoryWandPresentationSnapshot"));
            Set(wand, "InstanceId", "wand"); Set(wand, "CurrentMana", 37f); Set(wand, "CooldownRemaining", 1.25f); Set(wand, "ProgramRevision", 17u);
            var wands = Array.CreateInstance(wand.GetType(), 1); wands.SetValue(wand, 0); Set(snapshot, "Wands", wands);
            var spell = Activator.CreateInstance(RunType("OnlineInventorySpellUsePresentationSnapshot"));
            Set(spell, "InstanceId", "card"); Set(spell, "Eligible", true);
            var spells = Array.CreateInstance(spell.GetType(), 1); spells.SetValue(spell, 0); Set(snapshot, "SpellUses", spells);
            return snapshot;
        }
        private static OnlineOwnedAssetSnapshot Assets(object snapshot) => (OnlineOwnedAssetSnapshot)snapshot.GetType().GetField("Assets").GetValue(snapshot);
        private static Array Wands(object snapshot) => (Array)snapshot.GetType().GetField("Wands").GetValue(snapshot);
        private static Array SpellUses(object snapshot) => (Array)snapshot.GetType().GetField("SpellUses").GetValue(snapshot);
        private static Type RunType(string name)
        {
            var type = typeof(GameLog).Assembly.GetType("Game.Core." + name);
            Assert.That(type, Is.Not.Null, "缺少 Core 中性库存合同：" + name); return type;
        }
        private static void Set(object target, string name, object value) => target.GetType().GetField(name).SetValue(target, value);
    }
}
