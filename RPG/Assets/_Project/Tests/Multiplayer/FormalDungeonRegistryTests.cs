using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Character;
using Game.Combat;
using Game.Core;
using Game.Networking;
using Game.NetworkProtocol;
using Google.Protobuf;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Multiplayer.Tests
{
    /// <summary>
    /// 正式副本的Actor容量、受控Spawn映射与数据合法性；不通过放大数组让未知Prefab/非法地图取得执行权。
    /// Reflection仅用于新合同的RED和内存资产装配，生产代码不得依赖名称猜组件。
    /// </summary>
    public sealed class FormalDungeonRegistryTests
    {
        private readonly List<Object> _objects = new List<Object>();
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        [TearDown] public void Cleanup()
        {
            for (int i = _objects.Count - 1; i >= 0; i--) if (_objects[i] != null) Object.DestroyImmediate(_objects[i]);
            _objects.Clear();
        }
        [Test] public void ActorBudgetKeepsFourPlayersAndTwelveDistinctNpcSeats()
        {
            var type = typeof(GameLog).Assembly.GetType("Game.Core.OnlineActorLimits");
            Assert.That(type, Is.Not.Null, "应由Core共享容量，不能让Codec/Registry/事实数组各自声明不同上限。");
            Assert.That(type.GetField("PlayerCount").GetRawConstantValue(), Is.EqualTo(4));
            Assert.That(type.GetField("MaximumActors").GetRawConstantValue(), Is.EqualTo(16));
            Assert.That(type.GetField("FirstNpcActorId").GetRawConstantValue(), Is.EqualTo(5));
            Assert.That(type.GetField("MaximumNpcs").GetRawConstantValue(), Is.EqualTo(12));
        }

        [Test] public void MovementAndAllCombatActorReferencesAcceptSixteenAndRejectSeventeen()
        {
            var movement = new MovementCodec(); var bytes = new byte[512];
            var state = new MovementStateMessage { ProtocolVersion = ProtocolCompatibility.CurrentVersion, ActorId = 16,
                ConnectionEpoch = 1, ServerTick = 1, SimulationHash = 1, SnapshotSequence = 1, IsEnemy = true };
            Assert.That(movement.TryEncodeState(state, bytes, out int count), Is.True, "P7第十二敌对应Actor16。");
            Assert.That(movement.TryDecodeState(new ArraySegment<byte>(bytes, 0, count), out var restored), Is.True);
            Assert.That(restored.ActorId, Is.EqualTo(16)); state.ActorId = 17; Assert.That(movement.TryEncodeState(state, bytes, out _), Is.False);
            var combat = new CombatCodec();
            var fact = new CombatFactMessage { ProtocolVersion = ProtocolCompatibility.CurrentVersion, GameplayHash = 1, Sequence = 1,
                ServerTick = 1, Kind = 7, ActorId = 16, TargetActorId = 16, AttackerActorId = 16, ConnectionEpoch = 1,
                CurrentHp = 100, MaxHp = 100, MoveMultiplier = 1 };
            Assert.That(combat.TryDecodeFact(new ArraySegment<byte>(fact.ToByteArray()), out _), Is.True);
            fact.ActorId = 17; Assert.That(combat.TryDecodeFact(new ArraySegment<byte>(fact.ToByteArray()), out _), Is.False);
            fact.ActorId = 16; fact.TargetActorId = 17; Assert.That(combat.TryDecodeFact(new ArraySegment<byte>(fact.ToByteArray()), out _), Is.False);
            fact.TargetActorId = 16; fact.AttackerActorId = 17; Assert.That(combat.TryDecodeFact(new ArraySegment<byte>(fact.ToByteArray()), out _), Is.False);
        }

        [Test] public void InvalidDungeonTableRejectsPathIdMissingSourceAndNonFiniteSpawn()
        {
            var definition = Definition(1); AssertValid(definition, true);
            Set(definition, "_dungeonId", "../unknown"); AssertValid(definition, false); Set(definition, "_dungeonId", "p7");
            Set(definition, "_sourceHash", ""); AssertValid(definition, false); Set(definition, "_sourceHash", new string('a', 64));
            var npcs = (Array)Get(definition, "_npcs"); npcs.GetValue(0).GetType().GetField("Position").SetValue(npcs.GetValue(0), new Vector3(float.NaN, 0, 0));
            AssertValid(definition, false);
        }

        [Test] public void DuplicateOrSparseActorsCannotAliasTheControlledNpcSlots()
        {
            var definition = Definition(12); AssertValid(definition, true);
            var npcs = (Array)Get(definition, "_npcs"); var last = npcs.GetValue(11);
            last.GetType().GetField("ActorId").SetValue(last, 5ul); AssertValid(definition, false);
            last.GetType().GetField("ActorId").SetValue(last, 17ul); AssertValid(definition, false);
        }

        [Test] public void CatalogResolvesOnlyExplicitCanonicalDungeonAndRejectsDuplicates()
        {
            var type = RequireCharacterType("OnlineDungeonCatalog"); var catalog = Track(ScriptableObject.CreateInstance(type));
            var p7 = Definition(1); var p8 = Definition(1); Set(p8, "_dungeonId", "p8"); Set(p8, "_sourceScenePath", "Assets/_Project/Scenes/P8_BossField.unity");
            var entries = Array.CreateInstance(p7.GetType(), 2); entries.SetValue(p7, 0); entries.SetValue(p8, 1); Set(catalog, "_dungeons", entries);
            AssertValid(catalog, true); var args = new object[] { "p8", null };
            Assert.That(type.GetMethod("TryGet").Invoke(catalog, args), Is.True); Assert.That(args[1], Is.SameAs(p8));
            Assert.That(type.GetMethod("TryGet").Invoke(catalog, new object[] { "P8", null }), Is.False);
            Assert.That(type.GetMethod("TryGet").Invoke(catalog, new object[] { "unknown", null }), Is.False);
            Assert.That(type.GetMethod("TryGet").Invoke(catalog, new object[] { "", null }), Is.False);
            entries.SetValue(p7, 1); AssertValid(catalog, false);
        }

        [Test] public void RegistryUsesConfiguredNpcPrefabAndPoseEvenWhenBossIsActorSixteen()
        {
            var definition = Definition(12); var npcs = (Array)Get(definition, "_npcs"); var last = npcs.GetValue(11);
            var boss = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Prefabs/Boss/Boss.prefab");
            last.GetType().GetField("Prefab").SetValue(last, boss);
            var position = new Vector3(23000, 3000, 17000); var rotation = Quaternion.Euler(0, 127, 0);
            last.GetType().GetField("Position").SetValue(last, position); last.GetType().GetField("Rotation").SetValue(last, rotation);
            last.GetType().GetField("InitiallyVisible").SetValue(last, false);
            AssertValid(definition, true);
            var registry = Registry(definition); Assert.That(registry.Capacity, Is.EqualTo(16));
            var slot = registry.EnsureEnemy(16, false);
            Assert.That(slot.Boss, Is.Not.Null, "类型由配置Prefab决定，不能把Actor7当成唯一Boss。");
            Assert.That(slot.Enemy, Is.Null); Assert.That(Vector3.Distance(slot.Body.position, position), Is.LessThan(.001f));
            Assert.That(Quaternion.Angle(slot.Body.rotation, rotation), Is.LessThan(.01f));
            Assert.That(slot.Connected, Is.False); Assert.That(slot.Root.activeSelf, Is.False);
            Assert.Throws<ArgumentOutOfRangeException>(() => registry.EnsureEnemy(17, false));
        }

        [Test] public void RegistryPreservesEachNpcAttackDurationAndNeverCreatesUnknownActors()
        {
            var definition = Definition(2); var npcs = (Array)Get(definition, "_npcs");
            npcs.GetValue(1).GetType().GetField("AttackClipDuration").SetValue(npcs.GetValue(1), 1.75f);
            var registry = Registry(definition); var first = registry.EnsureEnemy(5, true); var second = registry.EnsureEnemy(6, true);
            Assert.That(first.Enemy.AttackClipDurationSeconds, Is.EqualTo(.6f)); Assert.That(second.Enemy.AttackClipDurationSeconds, Is.EqualTo(1.75f));
            Assert.Throws<ArgumentOutOfRangeException>(() => registry.EnsureEnemy(7, true));
            Assert.That(registry.EnsureEnemy(5, true), Is.SameAs(first));
        }

        [Test] public void NpcActivityChangesOnlyItsOwnStableSlotAndUnknownNpcReadFailsClosed()
        {
            var registry = Registry(Definition(2)); var first = registry.EnsureEnemy(5, false); var second = registry.EnsureEnemy(6, false);
            var activate = typeof(NetworkActorRegistry).GetMethod("SetNpcActive"); var read = typeof(NetworkActorRegistry).GetMethod("TryGetNpcState");
            Assert.That(activate, Is.Not.Null); Assert.That(read, Is.Not.Null);
            activate.Invoke(registry, new object[] { 5ul, true });
            Assert.That(first.Connected && first.Root.activeSelf, Is.True); Assert.That(second.Connected || second.Root.activeSelf, Is.False);
            var state = new object[] { 16ul, false, false }; Assert.That(read.Invoke(registry, state), Is.False);
            Assert.That(state[1], Is.False); Assert.That(state[2], Is.False);
            activate.Invoke(registry, new object[] { 5ul, false }); Assert.That(first.Connected || first.Root.activeSelf, Is.False);
        }

        private NetworkActorRegistry Registry(Object definition)
        {
            var parent = Track(new GameObject("ControlledDungeonActors"));
            var ctor = typeof(NetworkActorRegistry).GetConstructor(new[] { typeof(OnlineMovementProfile), typeof(Transform), typeof(OnlineCombatDefinition), typeof(CombatSimulationWorld), definition.GetType() });
            Assert.That(ctor, Is.Not.Null, "Registry必须消费明确Dungeon配置，不能在正式世界默默回退灰盒Prefab。");
            return (NetworkActorRegistry)ctor.Invoke(new object[] { Resources.Load<OnlineMovementProfile>("Online/MovementProfile"), parent.transform,
                Resources.Load<OnlineCombatDefinition>("Online/CombatDefinition"), new CombatSimulationWorld(), definition });
        }
        private Object Definition(int count)
        {
            var type = RequireCharacterType("OnlineDungeonDefinition"); var definition = Track(ScriptableObject.CreateInstance(type));
            Set(definition, "_dungeonId", "p7"); Set(definition, "_sourceScenePath", "Assets/_Project/Scenes/P7_DemoRun.unity"); Set(definition, "_sourceHash", new string('a', 64));
            Set(definition, "_worldPrefab", Track(new GameObject("ControlledWorld"))); Set(definition, "_playerSpawns", new[] { Vector3.zero, Vector3.right, Vector3.left, Vector3.back });
            var spawn = RequireCharacterType("OnlineDungeonNpcSpawn"); var npcs = Array.CreateInstance(spawn, count);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Prefabs/BaseEnemy/SwordEnemy_Base.prefab");
            for (int i = 0; i < count; i++)
            {
                var value = Activator.CreateInstance(spawn); spawn.GetField("ActorId").SetValue(value, (ulong)(5 + i)); spawn.GetField("Prefab").SetValue(value, prefab);
                spawn.GetField("Position").SetValue(value, new Vector3(23000 + i, 3000, 17000)); spawn.GetField("Rotation").SetValue(value, Quaternion.identity);
                spawn.GetField("AttackClipDuration").SetValue(value, .6f); spawn.GetField("EncounterIndex").SetValue(value, 0);
                spawn.GetField("InitiallyVisible").SetValue(value, false); npcs.SetValue(value, i);
            }
            Set(definition, "_npcs", npcs);
            var encounter = RequireCharacterType("OnlineDungeonEncounterDefinition"); var encounters = Array.CreateInstance(encounter, 1);
            var current = Activator.CreateInstance(encounter); encounter.GetField("DisplayName").SetValue(current, "Controlled encounter"); encounter.GetField("ObjectiveText").SetValue(current, "Defeat owned NPCs");
            encounters.SetValue(current, 0); Set(definition, "_encounters", encounters); return definition;
        }
        private static Type RequireCharacterType(string name)
        {
            var type = typeof(WizardController).Assembly.GetType("Game.Character." + name); Assert.That(type, Is.Not.Null, "缺少受控副本配置：" + name); return type;
        }
        private T Track<T>(T value) where T : Object { _objects.Add(value); return value; }
        private static void Set(object value, string field, object data) => value.GetType().GetField(field, Private).SetValue(value, data);
        private static object Get(object value, string field) => value.GetType().GetField(field, Private).GetValue(value);
        private static void AssertValid(object value, bool expected)
        { var args = new object[] { null }; Assert.That(value.GetType().GetMethod("TryValidate").Invoke(value, args), Is.EqualTo(expected), args[0] as string); }
    }
}
