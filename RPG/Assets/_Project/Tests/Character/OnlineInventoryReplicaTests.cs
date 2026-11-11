using System;
using System.Reflection;
using System.Collections.Generic;
using Game.Core;
using Game.Run;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Character.Tests
{
    /// <summary>只测试场景中的读源所有权与拒绝边界；inactive Session只作只读引用，不初始化或访问磁盘。</summary>
    public sealed class OnlineInventoryReplicaTests
    {
        private GameObject _left, _right;
        private readonly List<UnityEngine.Object> _assets = new List<UnityEngine.Object>();
        private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;
        [TearDown] public void Cleanup()
        {
            var cache = Cache();
            if (cache != null) { cache.GetMethod("Clear", Static).Invoke(null, new object[] { 4101 }); cache.GetMethod("Clear", Static).Invoke(null, new object[] { 4202 }); }
            ReleaseInactiveReplica(_left); ReleaseInactiveReplica(_right);
            if (_left != null) UnityEngine.Object.DestroyImmediate(_left);
            if (_right != null) UnityEngine.Object.DestroyImmediate(_right);
            for (int i = _assets.Count - 1; i >= 0; i--) UnityEngine.Object.DestroyImmediate(_assets[i]);
            _assets.Clear();
        }

        [Test] public void LateSubscriberCanReadCurrentSourceAndOldOwnerCannotClearNewActor()
        {
            var cache = Cache(); Assert.That(cache, Is.Not.Null, "UI 晚启用应从 Owner-fenced Run 缓存重放，不寻找单机 Session。");
            _left = new GameObject("OldInventoryActor"); _left.SetActive(false);
            _right = new GameObject("NewInventoryActor"); _right.SetActive(false);
            var left = _left.AddComponent<SinglePlayerInventorySession>(); var right = _right.AddComponent<SinglePlayerInventorySession>();
            cache.GetMethod("Bind", Static).Invoke(null, new object[] { 4101, _left, left });
            cache.GetMethod("Bind", Static).Invoke(null, new object[] { 4202, _right, right });
            cache.GetMethod("Clear", Static).Invoke(null, new object[] { 4101 });
            Assert.That(cache.GetProperty("Actor", Static).GetValue(null), Is.SameAs(_right));
            Assert.That(cache.GetProperty("Source", Static).GetValue(null), Is.SameAs(right));
            cache.GetMethod("Clear", Static).Invoke(null, new object[] { 4202 });
            Assert.That(cache.GetProperty("Actor", Static).GetValue(null), Is.Null);
            Assert.That(cache.GetProperty("Source", Static).GetValue(null), Is.Null);
        }

        [Test] public void ReplicaRejectsMissingIdentityAndSnapshotWithoutCreatingAuthorityOrPublishingSource()
        {
            var type = typeof(PlayerControllerBase).Assembly.GetType("Game.Character.OnlineInventoryReplica");
            Assert.That(type, Is.Not.Null, "缺少 Character 中的只读组合桥，Multiplayer 不应直接依赖 Run。");
            var identity = typeof(GameLog).Assembly.GetType("Game.Core.OnlineInventoryPresentationIdentity");
            Assert.That(identity, Is.Not.Null);
            _left = new GameObject("ReplicaWithoutAuthority"); _left.SetActive(false);
            var replica = _left.AddComponent(type);
            var args = new[] { Activator.CreateInstance(identity), null, null, null };
            Assert.That((bool)type.GetMethod("Install").Invoke(replica, args), Is.False);
            Assert.That(type.GetProperty("Source").GetValue(replica), Is.Null);
            Assert.That(Cache().GetProperty("Actor", Static).GetValue(null), Is.Null);
        }

        [Test] public void EarlierResourceFactReplaysBeforeBindingAndLaterFullStateKeepsStableSource()
        {
            _left = new GameObject("LocalReplica"); _left.SetActive(false);
            var type = typeof(PlayerControllerBase).Assembly.GetType("Game.Character.OnlineInventoryReplica");
            Assert.That(type, Is.Not.Null);
            var replica = _left.AddComponent(type); var catalog = MemoryCatalog();
            Assert.That((bool)type.GetMethod("ApplyResources").Invoke(replica, new object[] { 7u, 15u, "wand", 17u, 21f, .5f }), Is.False);
            Assert.That(Install(type, replica, Identity(7), Snapshot(7, 1, 10), catalog), Is.True);
            var source = (IInventoryPresentationSource)type.GetProperty("Source").GetValue(replica);
            Assert.That(source.WandsReadOnly.TryGetState("wand", out var wand), Is.True);
            Assert.That(wand.CurrentMana, Is.EqualTo(21f), "快照到达前已收到较新资源事实，Bind前必须重放。");
            Assert.That(Cache().GetProperty("Source", Static).GetValue(null), Is.SameAs(source));
            Assert.That(Install(type, replica, Identity(7), Snapshot(7, 2, 20), catalog), Is.True);
            Assert.That(type.GetProperty("Source").GetValue(replica), Is.SameAs(source));
            Assert.That(source.WandsReadOnly.TryGetState("wand", out wand), Is.True);
            Assert.That(wand.CurrentMana, Is.EqualTo(37f), "完整快照基线已比缓存Tick15更新，不能再重放旧值。");
        }

        [Test] public void LastWandLeavingOwnerKeepsReplicaBoundAndRemovesOldResources()
        {
            _left = new GameObject("EmptyHandsReplica"); _left.SetActive(false);
            var type = typeof(PlayerControllerBase).Assembly.GetType("Game.Character.OnlineInventoryReplica");
            var replica = _left.AddComponent(type); var catalog = MemoryCatalog();
            Assert.That(Install(type, replica, Identity(7), Snapshot(7, 1, 10), catalog), Is.True);
            var before = (IInventoryPresentationSource)type.GetProperty("Source").GetValue(replica);
            Assert.That((bool)type.GetMethod("ApplyResources").Invoke(replica, new object[] { 7u, 15u, "wand", 17u, 21f, .5f }), Is.True);
            var empty = Snapshot(7, 2, 20); empty.Assets.InventoryVersion = 10;
            empty.ResourceVersion = 13; empty.SelectionVersion = empty.SpellUseVersion = 2;
            empty.Assets.SelectedWandInstanceId = ""; empty.Assets.Items = Array.Empty<OnlineOwnedItemSnapshot>();
            empty.Wands = Array.Empty<OnlineInventoryWandPresentationSnapshot>(); empty.SpellUses = Array.Empty<OnlineInventorySpellUsePresentationSnapshot>();
            Assert.That(Install(type, replica, Identity(7), empty, catalog), Is.True,
                "最后一杖离手后Owner仍是已安装的真实空库存，不能因至少一装备杖假设丢掉整个Source。");
            var source = (IInventoryPresentationSource)type.GetProperty("Source").GetValue(replica);
            Assert.That(source, Is.SameAs(before)); Assert.That(source.ReadOnly.Count, Is.Zero);
            Assert.That(source.WandsReadOnly.SelectedWandId, Is.Null.Or.Empty);
            Assert.That(source.WandsReadOnly.TryGetSlot(0, out _), Is.False);
            Assert.That(source.WandsReadOnly.TryGetState("wand", out _), Is.False);
            Assert.That(Cache().GetProperty("Source", Static).GetValue(null), Is.SameAs(source));
            Assert.That((bool)type.GetProperty("HasSnapshot").GetValue(replica), Is.True);
        }

        [Test] public void InvalidReplacementKeepsOldSourceAndNewEpochDisposesOnlyAfterValidInstall()
        {
            _left = new GameObject("ReconnectReplica"); _left.SetActive(false);
            var type = typeof(PlayerControllerBase).Assembly.GetType("Game.Character.OnlineInventoryReplica");
            Assert.That(type, Is.Not.Null);
            var replica = _left.AddComponent(type); var catalog = MemoryCatalog();
            Assert.That(Install(type, replica, Identity(7), Snapshot(7, 1, 10), catalog), Is.True);
            var old = (IInventoryPresentationSource)type.GetProperty("Source").GetValue(replica);
            var invalid = Snapshot(7, 2, 20); invalid.Assets.PlayerId = "foreign";
            Assert.That(Install(type, replica, Identity(7), invalid, catalog), Is.False);
            Assert.That(type.GetProperty("Source").GetValue(replica), Is.SameAs(old));
            Assert.That(Install(type, replica, Identity(8), Snapshot(7, 1, 20), catalog), Is.False,
                "新Epoch必须取得对应的新快照，不能给旧资产换个连接标签。");
            Assert.That(old.ReadOnly.Count, Is.EqualTo(2));
            Assert.That(Install(type, replica, Identity(8), Snapshot(8, 1, 20), catalog), Is.True);
            var next = (IInventoryPresentationSource)type.GetProperty("Source").GetValue(replica);
            Assert.That(next, Is.Not.SameAs(old)); Assert.That(old.ReadOnly.Count, Is.Zero);
            Assert.That(Cache().GetProperty("Source", Static).GetValue(null), Is.SameAs(next));
        }

        [Test] public void StableSlotResourceCannotUpdateAnotherWandWithTheSameProgramRevision()
        {
            _left = new GameObject("TwoWandReplica"); _left.SetActive(false);
            var type = typeof(PlayerControllerBase).Assembly.GetType("Game.Character.OnlineInventoryReplica");
            var method = type.GetMethod("ApplySlotResources");
            Assert.That(method, Is.Not.Null, "跨通道资源必须使用稳定装备槽，不能按刚收到的Setup所选杖反填身份。");
            var replica = _left.AddComponent(type); var catalog = MemoryCatalog();
            // 包先于完整库存到达，仍只缓存三个槽；无Source时不得宣称已更新可见资产。
            Assert.That(method.Invoke(replica, new object[] { 7u, 15u, 1u, 17u, 21f, .5f }), Is.False);
            var snapshot = Snapshot(7, 1, 10);
            var second = new OnlineOwnedItemSnapshot { InstanceId = "wand-two", ContentId = "replica.wand", Revision = 1, LocationKind = 3,
                Slot = 1, UseTemplateWandStats = true };
            Array.Resize(ref snapshot.Assets.Items, 3); snapshot.Assets.Items[2] = second;
            Array.Resize(ref snapshot.Wands, 2); snapshot.Wands[1] = new OnlineInventoryWandPresentationSnapshot
                { InstanceId = "wand-two", ProgramRevision = 17, CurrentMana = 60, CooldownRemaining = 0 };
            snapshot.Assets.SelectedWandInstanceId = "wand-two";
            Assert.That(Install(type, replica, Identity(7), snapshot, catalog), Is.True);
            var source = (IInventoryPresentationSource)type.GetProperty("Source").GetValue(replica);
            Assert.That(source.WandsReadOnly.TryGetState("wand", out var first), Is.True);
            Assert.That(source.WandsReadOnly.TryGetState("wand-two", out var selected), Is.True);
            Assert.That(first.CurrentMana, Is.EqualTo(21)); Assert.That(selected.CurrentMana, Is.EqualTo(60));
            Assert.That(method.Invoke(replica, new object[] { 7u, 16u, 4u, 17u, 1f, 0f }), Is.False);
            Assert.That(method.Invoke(replica, new object[] { 6u, 16u, 2u, 17u, 1f, 0f }), Is.False);
            Assert.That(source.WandsReadOnly.TryGetState("wand-two", out selected), Is.True); Assert.That(selected.CurrentMana, Is.EqualTo(60));
        }

        private ItemCatalogIndex MemoryCatalog()
        {
            var wand = Item("replica.wand", ItemKind.Wand); var card = Item("replica.card", ItemKind.Spell);
            var catalog = ScriptableObject.CreateInstance<ItemCatalog>(); _assets.Add(catalog);
            var data = new SerializedObject(catalog); var definitions = data.FindProperty("_definitions"); definitions.arraySize = 2;
            definitions.GetArrayElementAtIndex(0).objectReferenceValue = wand;
            definitions.GetArrayElementAtIndex(1).objectReferenceValue = card; data.ApplyModifiedPropertiesWithoutUndo();
            Assert.That(catalog.TryCreateIndex(out var index, out var error), Is.True, error); return index;
        }
        private ItemDefinition Item(string id, ItemKind kind)
        {
            var definition = ScriptableObject.CreateInstance<ItemDefinition>(); _assets.Add(definition);
            var data = new SerializedObject(definition); data.FindProperty("_contentId").stringValue = id; data.FindProperty("_kind").intValue = (int)kind;
            if (kind == ItemKind.Wand)
            {
                var wand = ScriptableObject.CreateInstance<WandDefinition>(); _assets.Add(wand);
                data.FindProperty("_wandDefinition").objectReferenceValue = wand;
            }
            else
            {
                var spell = ScriptableObject.CreateInstance<Game.Skills.SpellDefinition>(); _assets.Add(spell);
                spell.Kind = Game.Skills.SpellKind.Modify; data.FindProperty("_spellDefinition").objectReferenceValue = spell;
            }
            data.ApplyModifiedPropertiesWithoutUndo(); return definition;
        }
        private static OnlineInventoryPresentationIdentity Identity(uint epoch) =>
            new OnlineInventoryPresentationIdentity("run", "player", 1, "inventory", Hash, epoch, "World");
        private static OnlineInventoryPresentationSnapshot Snapshot(uint epoch, ulong sequence, uint tick) => new OnlineInventoryPresentationSnapshot
        {
            SchemaVersion = 1, ConnectionEpoch = epoch, SnapshotSequence = sequence, ServerTick = tick,
            ResourceVersion = 12, SelectionVersion = 1, SpellUseVersion = 1,
            Assets = new OnlineOwnedAssetSnapshot
            {
                SchemaVersion = OnlineOwnedAssetSnapshot.CurrentSchemaVersion, RunId = "run", PlayerId = "player", ActorId = 1, InventoryId = "inventory", InventoryVersion = 9,
                GameplayConfigHash = Hash, SelectedWandInstanceId = "wand", Items = new[]
                {
                    new OnlineOwnedItemSnapshot { InstanceId = "wand", ContentId = "replica.wand", Revision = 1,
                        LocationKind = (int)ItemLocationKind.EquippedWand, UseTemplateWandStats = true },
                    new OnlineOwnedItemSnapshot { InstanceId = "card", ContentId = "replica.card", Revision = 1,
                        LocationKind = (int)ItemLocationKind.WandSlot, ParentInstanceId = "wand", RemainingCharges = 0 }
                }
            },
            Wands = new[] { new OnlineInventoryWandPresentationSnapshot { InstanceId = "wand", ProgramRevision = 17, CurrentMana = 37, CooldownRemaining = 1 } },
            SpellUses = new[] { new OnlineInventorySpellUsePresentationSnapshot { InstanceId = "card", Eligible = true } }
        };
        private static bool Install(Type type, Component replica, OnlineInventoryPresentationIdentity identity,
            OnlineInventoryPresentationSnapshot snapshot, ItemCatalogIndex catalog)
        {
            var args = new object[] { identity, snapshot, catalog, null };
            return (bool)type.GetMethod("Install").Invoke(replica, args);
        }
        private static void ReleaseInactiveReplica(GameObject actor)
        {
            if (actor == null) return;
            var type = typeof(PlayerControllerBase).Assembly.GetType("Game.Character.OnlineInventoryReplica");
            if (type == null) return;
            var replica = actor.GetComponent(type); if (replica == null) return;
            // EditMode 使用从未激活的纯测试对象，不假设 Unity 会替它执行运行时 OnDestroy。
            // 显式驱动同一释放入口，避免清理缺失把前一用例缓存带给后一个；真实Destroy另由PlayMode验收。
            type.GetMethod("OnDestroy", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(replica, null);
        }
        private static Type Cache() => typeof(InventoryReadModel).Assembly.GetType("Game.Run.OnlineInventoryPresentation");
    }
}
