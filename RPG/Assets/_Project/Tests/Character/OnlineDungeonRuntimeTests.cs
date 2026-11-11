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

        [Test]
        public void PeacefulHomeAcceptsTheOriginalEmptyWorldWithoutInventingEnemiesOrEncounters()
        {
            var home = MemoryHome();
            Assert.That(home.TryValidate(out var error), Is.True, error);
            Assert.That(home.NpcCount, Is.Zero); Assert.That(home.EncounterCount, Is.Zero);
            Assert.That(home.ShowEncounterHud, Is.False);
            SetLootField(home, "_dungeonId", "p7");
            Assert.That(home.TryValidate(out _), Is.False, "和平例外不能令正式战斗副本的坏配置获得准入。");
        }

        [Test]
        public void PeacefulHomeRuntimeNeverCreatesFakeStageCompletionOrBattleRewards()
        {
            var home = MemoryHome();
            var world = (GameObject)typeof(OnlineDungeonDefinition).GetField("_worldPrefab", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(home);
            var bindings = world.AddComponent<OnlineDungeonWorldBindings>();
            _roster = new Roster();
            OnlineDungeonRuntime runtime = null;
            Assert.DoesNotThrow(() => runtime = new OnlineDungeonRuntime(home, bindings, _roster, true, (_, __) => { }, _ => { }));
            _runtimes.Add(runtime);
            for (uint tick = 1; tick <= 3; tick++) runtime.Step(1f / 30f, tick);
            Assert.That(runtime.EncounterCount, Is.Zero); Assert.That(runtime.ShowEncounterHud, Is.False);
            Assert.That(runtime.TryCapture(out _), Is.False, "无遭遇就没有可广播的战斗进度，更不能把空场景当清关。");
        }

        private OnlineDungeonDefinition MemoryHome()
        {
            var home = ScriptableObject.CreateInstance<OnlineDungeonDefinition>(); _owned.Add(home);
            SetLootField(home, "_dungeonId", "home");
            SetLootField(home, "_sourceScenePath", "Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Home.unity");
            SetLootField(home, "_sourceHash", new string('a', 64));
            SetLootField(home, "_worldPrefab", Own(new GameObject("Original peaceful Home memory world")));
            SetLootField(home, "_playerSpawns", new Vector3[4]);
            SetLootField(home, "_npcs", Array.Empty<OnlineDungeonNpcSpawn>());
            SetLootField(home, "_encounters", Array.Empty<OnlineDungeonEncounterDefinition>());
            SetLootField(home, "_showEncounterHud", false);
            return home;
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

        // 原始投放位置来自实际Scene内Layout实例的世界Transform；不把Prefab局部坐标或NavMesh散布当新来源。
        [Test]
        public void FixedSolidExportKeepsTheOriginalP7IceWorldPoseAndP8HasNoNewSource()
        {
            var inspect = SolidExportTools().GetMethod("InspectFixedSolidSpawns", new[] { typeof(string) });
            Assert.That(inspect, Is.Not.Null, "窄刷新需先读取原固体来源，再剥离单机WorldItemSpawnPoint。");
            var p7 = (Array)inspect.Invoke(null, new object[] { "p7" });
            Assert.That(p7.Length, Is.EqualTo(1), "只接原p7.explore.0冰，原7个food点不导入线上来源。");
            object spawn = p7.GetValue(0);
            Assert.That(SolidSpawnValue<string>(spawn, "SpawnId"), Is.EqualTo("p7.explore.0"));
            Assert.That(SolidSpawnValue<string>(spawn, "ContentId"), Is.EqualTo("containertest.ice"));
            Assert.That(SolidSpawnValue<byte>(spawn, "MaterialKey"), Is.EqualTo((byte)5));
            Assert.That(SolidSpawnValue<ulong>(spawn, "Quantity"), Is.EqualTo(256ul));
            var scene = UnityEditor.SceneManagement.EditorSceneManager.OpenPreviewScene("Assets/_Project/Scenes/P7_DemoRun.unity");
            try
            {
                WorldItemSpawnPoint original = null;
                foreach (var root in scene.GetRootGameObjects()) foreach (var point in root.GetComponentsInChildren<WorldItemSpawnPoint>(true))
                    if (point.SpawnId == "p7.explore.0") { Assert.That(original, Is.Null); original = point; }
                Assert.That(original, Is.Not.Null);
                Assert.That(SolidSpawnValue<Vector3>(spawn, "Position"), Is.EqualTo(original.transform.position));
                Assert.That(SolidSpawnValue<Quaternion>(spawn, "Rotation"), Is.EqualTo(original.transform.rotation));
                var data = new SerializedObject(original);
                Assert.That((ulong)data.FindProperty("_initialMaterialQuantity").intValue, Is.EqualTo(256ul));
            }
            finally { UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene); }
            var definition = ScriptableObject.CreateInstance<OnlineDungeonDefinition>(); _owned.Add(definition);
            SetLootField(definition, "_fixedSolidSpawns", p7);
            var count = typeof(OnlineDungeonDefinition).GetProperty("FixedSolidCount");
            var get = typeof(OnlineDungeonDefinition).GetMethod("GetFixedSolid");
            Assert.That(count, Is.Not.Null); Assert.That(get, Is.Not.Null);
            Assert.That(count.GetValue(definition), Is.EqualTo(1)); Assert.That(get.Invoke(definition, new object[] { 0 }), Is.SameAs(spawn));
            var p8 = (Array)inspect.Invoke(null, new object[] { "p8" });
            Assert.That(p8.Length, Is.Zero, "P8不能借用P7来源或凭空投放固体。");
        }

        [Test]
        public void FixedSolidSourceHashIncludesOriginalLayoutIceAndMetadataWithoutAddingP8Inputs()
        {
            var enumerate = SolidExportTools().GetMethod("EnumerateLootHashInputs", new[] { typeof(string) });
            Assert.That(enumerate, Is.Not.Null);
            var paths = new List<string>((IEnumerable<string>)enumerate.Invoke(null, new object[] { "p7" }));
            const string layout = "Assets/_Project/Prefabs/SinglePlayer/P7Dungeon/FixedFourStageLayout.prefab";
            Assert.That(paths, Does.Contain(layout)); Assert.That(paths, Does.Contain(layout + ".meta"));
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(layout); WorldItemSpawnPoint ice = null;
            foreach (var point in source.GetComponentsInChildren<WorldItemSpawnPoint>(true)) if (point.SpawnId == "p7.explore.0") ice = point;
            Assert.That(ice, Is.Not.Null); string definitionPath = AssetDatabase.GetAssetPath(ice.Definition);
            Assert.That(paths, Does.Contain(definitionPath)); Assert.That(paths, Does.Contain(definitionPath + ".meta"));
            Assert.That(new List<string>((IEnumerable<string>)enumerate.Invoke(null, new object[] { "p8" })), Is.Empty);
        }

        private static Type SolidExportTools()
        {
            var type = Type.GetType("Game.Project.Editor.OnlineDungeonTools, Game.Project.Editor");
            Assert.That(type, Is.Not.Null, "使用现有正式导出器，测试不另存Scene或重导大WorldPrefab。"); return type;
        }
        private static T SolidSpawnValue<T>(object source, string name)
        {
            var field = source.GetType().GetField(name); Assert.That(field, Is.Not.Null, "固定固体缺少原来源值：" + name);
            return (T)field.GetValue(source);
        }

        [Test]
        public void ContainerLootKeepsOriginalStageTablesAndEachNpcsOriginalLocalEnemyIndex()
        {
            var definition = LootDefinition();
            Assert.That(ValidateLoot(definition, out var error), Is.True, error);
            Assert.That((int)typeof(OnlineDungeonDefinition).GetProperty("LootTableCount").GetValue(definition), Is.EqualTo(4));
            for (int stage = 0; stage < 4; stage++)
            {
                var table = LootTable(definition, stage);
                Assert.That(table, Is.SameAs(AssetDatabase.LoadAssetAtPath<DungeonLootTable>("Assets/_Project/ScriptableObjects/World/Dungeon/Stage" + (stage + 1) + "Loot.asset")));
                var expected = ContainerEntry(table); Assert.That(expected.Chance, Is.EqualTo(.12f)); Assert.That(expected.MinCount, Is.EqualTo(1)); Assert.That(expected.MaxCount, Is.EqualTo(1));
                Assert.That(expected.Pool.Length, Is.EqualTo(4));
                for (int enemy = 0; enemy < 3; enemy++)
                {
                    var args = new object[] { (ulong)(5 + stage * 3 + enemy), null, 0, 0 };
                    Assert.That((bool)RequireLootMethod("TryGetContainerDrop").Invoke(definition, args), Is.True);
                    Assert.That(args[1], Is.SameAs(expected)); Assert.That(args[2], Is.EqualTo(stage)); Assert.That(args[3], Is.EqualTo(enemy));
                }
            }
        }

        [TestCase("multiple-count")]
        [TestCase("second-container-row")]
        [TestCase("invalid-chance")]
        [TestCase("missing-stage")]
        [TestCase("missing-p7-tables")]
        [TestCase("too-many-npcs")]
        [TestCase("duplicate-local-index")]
        [TestCase("wrong-stage")]
        public void LootProofRejectsInvalidMappingOrSourcesBeyondTheOriginalFortyEightItems(string defect)
        {
            var definition = LootDefinition(); var tables = new DungeonLootTable[4];
            for (int i = 0; i < tables.Length; i++) { tables[i] = Object.Instantiate(LootTable(definition, i)); _owned.Add(tables[i]); }
            SetLootField(definition, "_lootTables", tables); var row = ContainerEntry(tables[0]);
            if (defect == "multiple-count") row.MaxCount = 2;
            if (defect == "second-container-row") { var items = tables[0].Items; Array.Resize(ref items, items.Length + 1); items[items.Length - 1] = row; tables[0].Items = items; }
            if (defect == "invalid-chance") row.Chance = float.NaN;
            if (defect == "missing-stage") tables[1] = null;
            if (defect == "missing-p7-tables") SetLootField(definition, "_lootTables", Array.Empty<DungeonLootTable>());
            if (defect == "too-many-npcs") { var npcs = LootNpcs(definition); Array.Resize(ref npcs, 13); npcs[12] = new OnlineDungeonNpcSpawn { ActorId = 17, EncounterIndex = 3 }; SetLootField(definition, "_npcs", npcs); }
            if (defect == "duplicate-local-index") SetLootField(LootNpcs(definition)[1], "LootEnemyIndex", 0);
            if (defect == "wrong-stage") SetLootField(LootNpcs(definition)[1], "LootStageIndex", 1);
            Assert.That(ValidateLoot(definition, out var error), Is.False); Assert.That(error, Is.Not.Empty);
        }

        [Test]
        public void CompleteItemLootKeepsAMixedOriginalPoolInsteadOfFilteringItsContainerCandidate()
        {
            var definition = LootDefinition(); var table = Object.Instantiate(LootTable(definition, 0)); _owned.Add(table);
            SetLootField(definition, "_lootTables", new[] { table, LootTable(definition, 1), LootTable(definition, 2), LootTable(definition, 3) });
            var row = ContainerEntry(table); var pool = row.Pool; Array.Resize(ref pool, pool.Length + 1);
            pool[pool.Length - 1] = table.Items[0].Pool[0]; row.Pool = pool;
            // 只有候选池混合，最大实例数仍为原48；不能因旧容器切片而过滤、重抽或拒绝完整Item计划。
            Assert.That(ValidateLoot(definition, out var error), Is.True, error);
            Assert.That(row.Pool, Is.SameAs(pool)); Assert.That(row.Pool[pool.Length - 1].Kind, Is.EqualTo(ItemKind.Spell));
        }

        [Test]
        public void ContainerLootReadsTheActualTableChanceAndPoolWithoutReplacingThemWithTwelvePercentDefaults()
        {
            var definition = LootDefinition(); var table = Object.Instantiate(LootTable(definition, 0)); _owned.Add(table);
            var tables = new[] { table, LootTable(definition, 1), LootTable(definition, 2), LootTable(definition, 3) };
            SetLootField(definition, "_lootTables", tables); var row = ContainerEntry(table); row.Chance = .37f; Array.Reverse(row.Pool);
            Assert.That(ValidateLoot(definition, out var error), Is.True, error);
            var args = new object[] { 5ul, null, 0, 0 };
            Assert.That((bool)RequireLootMethod("TryGetContainerDrop").Invoke(definition, args), Is.True);
            Assert.That(((DungeonItemDrop)args[1]).Chance, Is.EqualTo(.37f)); Assert.That(((DungeonItemDrop)args[1]).Pool, Is.SameAs(row.Pool));
        }

        [Test]
        public void P8WithoutOriginalLootTablesKeepsNoContainerLootInsteadOfBorrowingP7sRules()
        {
            var definition = ScriptableObject.CreateInstance<OnlineDungeonDefinition>(); _owned.Add(definition);
            SetLootField(definition, "_dungeonId", "p8"); SetLootField(definition, "_lootTables", Array.Empty<DungeonLootTable>());
            var npc = new OnlineDungeonNpcSpawn { ActorId = 5, EncounterIndex = 0 }; SetLootField(npc, "LootStageIndex", -1); SetLootField(npc, "LootEnemyIndex", -1);
            SetLootField(definition, "_npcs", new[] { npc }); SetLootField(definition, "_encounters", new[] { new OnlineDungeonEncounterDefinition { DisplayName = "Boss" } });
            Assert.That(ValidateLoot(definition, out var error), Is.True, error);
            var args = new object[] { 5ul, null, 0, 0 }; Assert.That((bool)RequireLootMethod("TryGetContainerDrop").Invoke(definition, args), Is.False);
            Assert.That(args[1], Is.Null);
        }

        [Test]
        public void LegacySerializedNpcArrayWithoutLootTablesNeverCreatesAContainerSource()
        {
            var definition = ScriptableObject.CreateInstance<OnlineDungeonDefinition>(); _owned.Add(definition);
            // 与原Create夹具相同：SerializedObject给旧作者数组增加元素会产生int默认0，
            // 独立JsonUtility.FromJson会执行initializer，不能用它冒充这个实际兼容入口。
            var data = new SerializedObject(definition); var npcs = data.FindProperty("_npcs"); npcs.arraySize = 1;
            var element = npcs.GetArrayElementAtIndex(0); element.FindPropertyRelative("ActorId").ulongValue = 5;
            element.FindPropertyRelative("EncounterIndex").intValue = 0; data.ApplyModifiedPropertiesWithoutUndo();
            var legacy = LootNpcs(definition)[0];
            Assert.That((int)legacy.GetType().GetField("LootStageIndex").GetValue(legacy), Is.Zero);
            Assert.That((int)legacy.GetType().GetField("LootEnemyIndex").GetValue(legacy), Is.Zero);
            SetLootField(definition, "_dungeonId", "p7"); SetLootField(definition, "_lootTables", Array.Empty<DungeonLootTable>());
            SetLootField(definition, "_encounters", new[] { new OnlineDungeonEncounterDefinition { DisplayName = "legacy" } });
            Assert.That(ValidateLoot(definition, out var error), Is.True, error);
            Assert.That((int)typeof(OnlineDungeonDefinition).GetProperty("LootTableCount").GetValue(definition), Is.Zero);
            var args = new object[] { 5ul, null, 0, 0 }; Assert.That((bool)RequireLootMethod("TryGetContainerDrop").Invoke(definition, args), Is.False);
            Assert.That(args[1], Is.Null); Assert.That(args[2], Is.EqualTo(-1)); Assert.That(args[3], Is.EqualTo(-1));
            SetLootField(legacy, "LootEnemyIndex", -1);
            Assert.That(ValidateLoot(definition, out _), Is.False, "仅成对0/0是旧缺字段表示；混合索引不能被当成新合法来源。");
        }

        [Test]
        public void NpcLootGateRequiresCurrentActiveDeadNpcAndDoesNotAdvanceEncounterState()
        {
            var runtime = Create(false); _roster.Alive[4] = false;
            Assert.That(CanAward(runtime, 5), Is.False, "Armed阶段不能因预先死亡绕过区域激活。");
            _roster.Alive[4] = true; SetPlayer(0, Vector3.zero); Step(runtime, 1);
            Assert.That(CanAward(runtime, 5), Is.False, "仍活着的NPC不能领取死亡奖励。");
            _roster.Alive[5] = false; Assert.That(CanAward(runtime, 6), Is.False, "未来区域即便Health已死也不能提前发放。");
            _roster.Alive[4] = false; uint revision = Value<uint>(Capture(runtime), "Revision");
            Assert.That(CanAward(runtime, 5), Is.True); Assert.That(Value<uint>(Capture(runtime), "Revision"), Is.EqualTo(revision));
        }

        [Test]
        public void NpcLootGateAllowsAlreadyClearedAreasAndStageClearedButStopsAfterDispose()
        {
            var runtime = Create(false); SetPlayer(0, Vector3.zero); Step(runtime, 1); _roster.Alive[4] = false; Step(runtime, 2);
            Assert.That(CanAward(runtime, 5), Is.True); _roster.Alive[5] = false;
            Assert.That(CanAward(runtime, 6), Is.False, "下一关仍Armed时只保留上一关的原死亡资格。");
            SetPlayer(0, new Vector3(20, 0, 0)); Step(runtime, 3); Assert.That(CanAward(runtime, 6), Is.True);
            Step(runtime, 4); AssertPhase(runtime, "StageCleared", 2, 0); Assert.That(CanAward(runtime, 5), Is.True); Assert.That(CanAward(runtime, 6), Is.True);
            ((IDisposable)runtime).Dispose(); Assert.That(CanAward(runtime, 5), Is.False);
        }

        [Test]
        public void NpcLootGateRejectsReplicaPlayersAndNpcIdsOutsideTheTrustedDefinition()
        {
            var replica = Create(false, false); _roster.Alive[4] = false; Assert.That(CanAward(replica, 5), Is.False);
            var authority = Create(false); SetPlayer(0, Vector3.zero); Step(authority, 1); _roster.Alive[4] = false;
            Assert.That(CanAward(authority, 5), Is.True); Assert.That(CanAward(authority, 0), Is.False); Assert.That(CanAward(authority, 1), Is.False); Assert.That(CanAward(authority, 16), Is.False);
        }

        private bool CanAward(object runtime, ulong actor)
        { var method = _runtimeType.GetMethod("CanAwardNpcDeath"); Assert.That(method, Is.Not.Null, "掉落需复用原Active/Cleared死亡权限门，不另建一套遭遇状态。"); return (bool)method.Invoke(runtime, new object[] { actor }); }

        private OnlineDungeonDefinition LootDefinition()
        {
            var definition = ScriptableObject.CreateInstance<OnlineDungeonDefinition>(); _owned.Add(definition);
            var tables = new DungeonLootTable[4]; var npcs = new OnlineDungeonNpcSpawn[12]; var encounters = new OnlineDungeonEncounterDefinition[4];
            for (int stage = 0; stage < 4; stage++)
            {
                tables[stage] = AssetDatabase.LoadAssetAtPath<DungeonLootTable>("Assets/_Project/ScriptableObjects/World/Dungeon/Stage" + (stage + 1) + "Loot.asset");
                Assert.That(tables[stage] != null, Is.True); encounters[stage] = new OnlineDungeonEncounterDefinition { DisplayName = "Stage" + stage };
                for (int enemy = 0; enemy < 3; enemy++)
                { var npc = new OnlineDungeonNpcSpawn { ActorId = (ulong)(5 + stage * 3 + enemy), EncounterIndex = stage }; SetLootField(npc, "LootStageIndex", stage); SetLootField(npc, "LootEnemyIndex", enemy); npcs[stage * 3 + enemy] = npc; }
            }
            SetLootField(definition, "_dungeonId", "p7"); SetLootField(definition, "_npcs", npcs); SetLootField(definition, "_encounters", encounters); SetLootField(definition, "_lootTables", tables); return definition;
        }
        private static void SetLootField(object target, string name, object value)
        { var field = target.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); Assert.That(field, Is.Not.Null, "缺少受控掉落配置字段：" + name); field.SetValue(target, value); }
        private static OnlineDungeonNpcSpawn[] LootNpcs(OnlineDungeonDefinition definition) => (OnlineDungeonNpcSpawn[])typeof(OnlineDungeonDefinition).GetField("_npcs", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(definition);
        private static MethodInfo RequireLootMethod(string name)
        { var method = typeof(OnlineDungeonDefinition).GetMethod(name); Assert.That(method, Is.Not.Null, "需要原SO容器掉落的纯配置API：" + name); return method; }
        private static bool ValidateLoot(OnlineDungeonDefinition definition, out string error)
        { var args = new object[] { null }; bool result = (bool)RequireLootMethod("TryValidateContainerDrops").Invoke(definition, args); error = (string)args[0]; return result; }
        private static DungeonLootTable LootTable(OnlineDungeonDefinition definition, int stage) => (DungeonLootTable)RequireLootMethod("GetLootTable").Invoke(definition, new object[] { stage });
        private static DungeonItemDrop ContainerEntry(DungeonLootTable table)
        { foreach (var entry in table.Items) foreach (var item in entry.Pool) if (item.Kind == ItemKind.Container) return entry; Assert.Fail("源StageLoot必须保留其原容器条目。"); return null; }

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
