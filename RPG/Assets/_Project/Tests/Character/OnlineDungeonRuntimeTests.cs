using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Core;
using Game.Run;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Character.Tests
{
    /// <summary>纯内存遭遇规则回归；真实玩家/敌人位置来自受控Roster，不创建网络身份、存档或奖励。</summary>
    public sealed class OnlineDungeonRuntimeTests
    {
        private sealed class Roster : ICombatActorRosterReadOnly, IOnlineDungeonNpcReadOnly
        {
            internal readonly CombatActorSnapshot[] Players = new CombatActorSnapshot[4];
            internal readonly bool[] Alive = new bool[16], Active = new bool[16];
            public int Count => Players.Length;
            public bool TryGetActor(int index, out CombatActorSnapshot actor)
            { actor = index >= 0 && index < Players.Length ? Players[index] : default; return actor.ActorId != 0; }
            public bool TryGetNpcState(ulong actor, out bool alive, out bool active)
            {
                alive = active = false; if (actor < 5 || actor > 16) return false;
                alive = Alive[(int)actor - 1]; active = Active[(int)actor - 1]; return true;
            }
        }
        private readonly List<Object> _owned = new List<Object>();
        private readonly List<ulong> _activated = new List<ulong>(), _bossStarts = new List<ulong>();
        private readonly List<IDisposable> _runtimes = new List<IDisposable>();
        private Type _runtimeType, _progressType, _phaseType;
        private Roster _roster;
        private object _bindings;
        private ArenaGate[] _gates;

        [SetUp] public void RequireRuntimeContract()
        {
            _runtimeType = typeof(WizardController).Assembly.GetType("Game.Character.OnlineDungeonRuntime");
            Assert.That(_runtimeType, Is.Not.Null, "缺少正式副本的单一遭遇Authority，不可沿用单人DemoRun Owner。");
            _progressType = typeof(GameLog).Assembly.GetType("Game.Core.OnlineDungeonProgress");
            _phaseType = typeof(GameLog).Assembly.GetType("Game.Core.OnlineDungeonPhase");
            Assert.That(_progressType, Is.Not.Null); Assert.That(_phaseType, Is.Not.Null);
            _activated.Clear(); _bossStarts.Clear();
        }
        [TearDown] public void ReleaseOnlyMemoryObjects()
        {
            foreach (var runtime in _runtimes) runtime.Dispose(); _runtimes.Clear();
            for (int i = _owned.Count - 1; i >= 0; i--) if (_owned[i] != null) Object.DestroyImmediate(_owned[i]);
            _owned.Clear();
        }

        [Test] public void OnlyCurrentEntryAndTargetableLivingPlayerMayStartP7Encounter()
        {
            object runtime = Create(false);
            SetPlayer(0, new Vector3(20, 0, 0)); Step(runtime, 1); AssertPhase(runtime, "Armed", 1, 1);
            SetPlayer(0, Vector3.zero, false); Step(runtime, 2); AssertPhase(runtime, "Armed", 1, 1);
            SetPlayer(0, Vector3.zero); Step(runtime, 3); AssertPhase(runtime, "Active", 1, 1);
            Assert.That(_activated, Is.EqualTo(new[] { 5ul })); Assert.That(_gates[0].IsClosed, Is.True); Assert.That(_gates[1].IsClosed, Is.False);
        }

        [TestCase(true)][TestCase(false)]
        public void HudPolicyComesFromDefinitionWithoutChangingEncounterAuthority(bool showEncounterHud)
        {
            object runtime = Create(false, showEncounterHud: showEncounterHud);
            var policy = runtime as IDungeonHudPresentationPolicy;
            Assert.That(policy, Is.Not.Null, "公共HUD只读可选策略，不反向引用Character里的Definition。");
            Assert.That(policy.ShowEncounterHud, Is.EqualTo(showEncounterHud));
            SetPlayer(0, Vector3.zero); Step(runtime, 1);
            AssertPhase(runtime, "Active", 1, 1);
            Assert.That(_activated, Is.EqualTo(new[] { 5ul }), "显隐策略不能改变服务器遭遇或NPC激活规则。");
        }

        [Test] public void OwnedNpcDeathsAdvanceInOrderWithoutRepeatedOrFutureDeathsClearingAnotherArea()
        {
            object runtime = Create(false); SetPlayer(0, Vector3.zero); Step(runtime, 1);
            _roster.Alive[4] = false; Step(runtime, 2); AssertPhase(runtime, "Armed", 2, 1);
            uint revision = Value<uint>(Capture(runtime), "Revision"); Step(runtime, 2); Step(runtime, 3);
            Assert.That(Value<uint>(Capture(runtime), "Revision"), Is.EqualTo(revision)); Assert.That(_gates[0].IsClosed, Is.False);
            SetPlayer(0, new Vector3(20, 0, 0)); Step(runtime, 4); AssertPhase(runtime, "Active", 2, 1);
            _roster.Alive[5] = false; Step(runtime, 5); AssertPhase(runtime, "StageCleared", 2, 0);
            Assert.That(_gates[1].IsClosed, Is.False); Assert.That(_activated, Is.EqualTo(new[] { 5ul, 6ul }));
            Step(runtime, 6); AssertPhase(runtime, "StageCleared", 2, 0);
        }

        [Test] public void BossRequiresSameLivingPlayerToEnterThenLeaveAndDisconnectIsNotAnExit()
        {
            object runtime = Create(true); SetPlayer(0, new Vector3(20, 0, 0)); Step(runtime, 1); AssertPhase(runtime, "Armed", 1, 1);
            SetPlayer(0, Vector3.zero); Step(runtime, 2); AssertPhase(runtime, "Armed", 1, 1);
            SetPlayer(0, new Vector3(20, 0, 0)); var player = _roster.Players[0]; player.IsConnected = false; _roster.Players[0] = player;
            Step(runtime, 3); AssertPhase(runtime, "Armed", 1, 1);
            SetPlayer(0, new Vector3(20, 0, 0)); Step(runtime, 4); AssertPhase(runtime, "Armed", 1, 1);
            SetPlayer(0, Vector3.zero); Step(runtime, 5); SetPlayer(0, new Vector3(20, 0, 0)); Step(runtime, 6);
            AssertPhase(runtime, "Active", 1, 1); Assert.That(_bossStarts, Is.EqualTo(new[] { 5ul }));
            Step(runtime, 7); Assert.That(_bossStarts.Count, Is.EqualTo(1));
        }

        [Test] public void PlayerDeathDoesNotTerminateThePartyOrClearItsCurrentEncounter()
        {
            object runtime = Create(false); SetPlayer(0, Vector3.zero); Step(runtime, 1);
            var player = _roster.Players[0]; player.IsAlive = false; player.Targetable = false; _roster.Players[0] = player;
            Step(runtime, 2); AssertPhase(runtime, "Active", 1, 1);
            SetPlayer(1, Vector3.zero); Step(runtime, 3); AssertPhase(runtime, "Active", 1, 1);
            Assert.That(_activated.Count, Is.EqualTo(1));
        }

        [Test] public void ReplicaDoesNotInferDeathsAndOnlyInstallsBoundedMonotonicProgress()
        {
            object runtime = Create(false, false); SetPlayer(0, Vector3.zero); Step(runtime, 1);
            Assert.That(_activated, Is.Empty, "Replica Step不能启动NPC或取得Authority。");
            object active = Progress(1, 10, 1, 1, "Active"); Assert.That(Apply(runtime, active), Is.True);
            Assert.That(_gates[0].IsClosed, Is.True); Assert.That(_activated, Is.Empty);
            Assert.That(Apply(runtime, active), Is.False);
            Assert.That(Apply(runtime, Progress(2, 11, 3, 1, "Active")), Is.False);
            Assert.That(Apply(runtime, Progress(2, 11, 1, 2, "Active")), Is.False);
            Assert.That(Apply(runtime, Progress(2, 9, 1, 1, "Active")), Is.False);
            Assert.That(Apply(runtime, Progress(2, 11, 2, 0, "StageCleared")), Is.True);
            Assert.That(_gates[0].IsClosed || _gates[1].IsClosed, Is.False);
        }

        [Test] public void EntryUsesActualColliderGeometryInsteadOfItsWorldAxisAlignedBounds()
        {
            object runtime = Create(false);
            var bindings = _bindings;
            Assert.That(bindings, Is.Not.Null);
            var areas = (Array)bindings.GetType().GetField("_areas", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(bindings);
            var first = areas.GetValue(0); var entries = (Collider[])first.GetType().GetField("EntryVolumes").GetValue(first);
            var box = (BoxCollider)entries[0]; box.size = new Vector3(8, 4, 1); box.transform.rotation = Quaternion.Euler(0, 45, 0);
            Physics.SyncTransforms();
            // AABB角落虽在bounds内，却不在旋转后真实薄Box内；ClosestPoint/OBB应拒绝。
            var point = new Vector3(box.bounds.max.x - .1f, 0, box.bounds.max.z - .1f);
            Assert.That(box.bounds.Contains(point), Is.True); SetPlayer(0, point); Step(runtime, 1);
            AssertPhase(runtime, "Armed", 1, 1);
        }

        [Test] public void ActiveAreaCannotRegainDeadMembersOrConsumeRevisionOfRejectedProgress()
        {
            object runtime = Create(false, false, 3);
            Assert.That(Apply(runtime, Progress(1, 10, 1, 3, "Active")), Is.True);
            Assert.That(Apply(runtime, Progress(2, 11, 1, 2, "Active")), Is.True);
            Assert.That(Apply(runtime, Progress(3, 12, 1, 3, "Active")), Is.False,
                "静态遭遇没有复活路径；同区Active剩余数不能在更高Revision下回升。");
            Assert.That(Apply(runtime, Progress(3, 12, 1, 1, "Active")), Is.True,
                "无效完整快照不能先消耗高Revision，从而阻塞同序号的合法读数。");
            AssertPhase(runtime, "Active", 1, 1);
        }

        private object Create(bool boss, bool authority = true, int firstAreaNpcCount = 1, bool showEncounterHud = true)
        {
            _roster = new Roster(); _roster.Alive[4] = _roster.Alive[5] = true;
            var assembly = typeof(WizardController).Assembly;
            var definitionType = assembly.GetType("Game.Character.OnlineDungeonDefinition"); Assert.That(definitionType, Is.Not.Null);
            var definition = ScriptableObject.CreateInstance(definitionType); _owned.Add(definition);
            var source = new SerializedObject(definition);
            // 夹具必须把参数写入真实Definition，不能只改变测试方法的局部变量。
            source.FindProperty("_showEncounterHud").boolValue = showEncounterHud;
            source.FindProperty("_dungeonId").stringValue = boss ? "p8" : "p7";
            source.FindProperty("_sourceScenePath").stringValue = boss ? "Assets/_Project/Scenes/P8_BossField.unity" : "Assets/_Project/Scenes/P7_DemoRun.unity";
            source.FindProperty("_sourceHash").stringValue = new string('a', 64);
            var world = Own(new GameObject("MemoryDungeonWorld")); source.FindProperty("_worldPrefab").objectReferenceValue = world;
            var players = source.FindProperty("_playerSpawns"); players.arraySize = 4;
            var npcs = source.FindProperty("_npcs"); npcs.arraySize = boss ? 1 : firstAreaNpcCount + 1;
            for (int i = 0; i < npcs.arraySize; i++)
            {
                var npc = npcs.GetArrayElementAtIndex(i);
                npc.FindPropertyRelative("ActorId").ulongValue = (ulong)i + 5;
                npc.FindPropertyRelative("Prefab").objectReferenceValue = AssetDatabase.LoadAssetAtPath<GameObject>(boss
                    ? "Assets/_Project/Art/Prefabs/Boss/Boss.prefab" : "Assets/_Project/Art/Prefabs/BaseEnemy/SwordEnemy_Base.prefab");
                npc.FindPropertyRelative("Position").vector3Value = new Vector3(i * 20, 0, 2);
                npc.FindPropertyRelative("Rotation").quaternionValue = Quaternion.identity;
                npc.FindPropertyRelative("AttackClipDuration").floatValue = 1;
                npc.FindPropertyRelative("EncounterIndex").intValue = boss ? 0 : i < firstAreaNpcCount ? 0 : 1;
                npc.FindPropertyRelative("InitiallyVisible").boolValue = boss;
                _roster.Alive[i + 4] = true;
            }
            var encounters = source.FindProperty("_encounters"); encounters.arraySize = boss ? 1 : 2;
            for (int i = 0; i < encounters.arraySize; i++)
            {
                var area = encounters.GetArrayElementAtIndex(i); area.FindPropertyRelative("DisplayName").stringValue = "Memory Area " + i;
                area.FindPropertyRelative("ExitToStart").boolValue = boss;
            }
            source.ApplyModifiedPropertiesWithoutUndo();
            var bindingsType = assembly.GetType("Game.Character.OnlineDungeonWorldBindings"); Assert.That(bindingsType, Is.Not.Null);
            var bindings = world.AddComponent(bindingsType); _bindings = bindings; var data = new SerializedObject(bindings);
            var areaArray = data.FindProperty("_areas"); areaArray.arraySize = encounters.arraySize; _gates = new ArenaGate[areaArray.arraySize];
            for (int i = 0; i < areaArray.arraySize; i++)
            {
                var entry = Own(new GameObject("Entry_" + i)); entry.transform.SetParent(world.transform); entry.transform.position = new Vector3(i * 20, 0, 0);
                var collider = entry.AddComponent<BoxCollider>(); collider.size = new Vector3(4, 4, 4); collider.isTrigger = true;
                _gates[i] = entry.AddComponent<ArenaGate>();
                var area = areaArray.GetArrayElementAtIndex(i); var volumes = area.FindPropertyRelative("EntryVolumes"); volumes.arraySize = 1;
                volumes.GetArrayElementAtIndex(0).objectReferenceValue = collider;
                var gates = area.FindPropertyRelative("Gates"); gates.arraySize = 1; gates.GetArrayElementAtIndex(0).objectReferenceValue = _gates[i];
            }
            data.ApplyModifiedPropertiesWithoutUndo(); Physics.SyncTransforms();
            Action<ulong, bool> activate = (actor, active) => { _roster.Active[(int)actor - 1] = active; if (active) _activated.Add(actor); };
            Action<ulong> bossStart = actor => _bossStarts.Add(actor);
            var runtime = Activator.CreateInstance(_runtimeType, definition, bindings, _roster, authority, activate, bossStart);
            _runtimes.Add((IDisposable)runtime); _activated.Clear(); return runtime;
        }
        private GameObject Own(GameObject value) { _owned.Add(value); return value; }
        private void SetPlayer(int index, Vector3 position, bool targetable = true)
            => _roster.Players[index] = new CombatActorSnapshot { ActorId = (ulong)index + 1, Position = position, IsAlive = true, IsConnected = true, Targetable = targetable };
        private void Step(object runtime, uint tick) => _runtimeType.GetMethod("Step").Invoke(runtime, new object[] { 1f / 30f, tick });
        private object Capture(object runtime) { var args = new object[] { null }; Assert.That(_runtimeType.GetMethod("TryCapture").Invoke(runtime, args), Is.True); return args[0]; }
        private void AssertPhase(object runtime, string phase, uint index, uint remaining)
        { var value = Capture(runtime); Assert.That(_progressType.GetField("Phase").GetValue(value).ToString(), Is.EqualTo(phase)); Assert.That(Value<uint>(value, "EncounterIndexPlusOne"), Is.EqualTo(index)); Assert.That(Value<uint>(value, "RemainingEnemies"), Is.EqualTo(remaining)); }
        private T Value<T>(object value, string field) => (T)_progressType.GetField(field).GetValue(value);
        private object Progress(uint revision, uint tick, uint index, uint remaining, string phase)
        {
            var value = Activator.CreateInstance(_progressType); _progressType.GetField("Revision").SetValue(value, revision); _progressType.GetField("ServerTick").SetValue(value, tick);
            _progressType.GetField("EncounterIndexPlusOne").SetValue(value, index); _progressType.GetField("RemainingEnemies").SetValue(value, remaining);
            _progressType.GetField("Phase").SetValue(value, Enum.Parse(_phaseType, phase)); return value;
        }
        private bool Apply(object runtime, object progress) => (bool)_runtimeType.GetMethod("ApplyReplica").Invoke(runtime, new[] { progress });
    }
}
