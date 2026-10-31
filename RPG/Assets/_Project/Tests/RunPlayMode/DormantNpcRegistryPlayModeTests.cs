using System.Collections;
using System.Reflection;
using Game.Character;
using Game.Combat;
using Game.Core;
using Game.Multiplayer;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Game.Run.Tests
{
    /// <summary>
    /// 用真实Unity Awake/OnEnable/OnDisable覆盖Dormant角色寿命，不能以EditMode手动Invoke Awake替代。
    /// 只创建本测试的内存Prefab副本/配置，不修改原资源，不建立账号或向正式角色注入伤害。
    /// NavMeshAgent在模板inactive时停用，因为本组只验证HP和可见寿命，不伪装成导航验收。
    /// </summary>
    public sealed class DormantNpcRegistryPlayModeTests
    {
        private GameObject _templateOwner, _actors, _world;
        private OnlineDungeonDefinition _dungeon;

        [UnityTearDown] public IEnumerator Cleanup()
        {
            if (_actors != null) Object.Destroy(_actors);
            if (_templateOwner != null) Object.Destroy(_templateOwner);
            if (_world != null) Object.Destroy(_world);
            if (_dungeon != null) Object.Destroy(_dungeon);
            yield return null;
        }

        [UnityTest] public IEnumerator InactivePrefabWarmsAwakeBeforeReplicaFactsAndLaterActivationNeverRefillsHp()
        {
            var profile = Resources.Load<OnlineMovementProfile>("Online/MovementProfile"); Assert.That(profile, Is.Not.Null);
            var template = InactiveTemplate(profile.EnemyPrefab);
            var registry = Registry(template, false, false);
            var slot = registry.EnsureEnemy(5, false);
            Assert.That(template.activeSelf, Is.False, "必须覆盖源Encounter保存为inactive的实际Prefab情形。");
            Assert.That(slot.Root.activeSelf, Is.False); Assert.That(slot.Connected, Is.False);
            Assert.That(slot.Health.Id, Is.EqualTo(slot.Health.gameObject.GetInstanceID()), "首次事实到达前必须已经执行真实Awake。");
            Assert.That(slot.Health.CurrentHp, Is.GreaterThan(0));
            slot.Health.ApplyReplicatedState(new HealthReplicaSnapshot { CurrentHp = 13, MaxHp = 100, TeamId = 2 });
            Assert.That(registry.SetNpcActive(5, true), Is.True); yield return null;
            Assert.That(slot.Root.activeInHierarchy, Is.True); Assert.That(slot.Body.gameObject.activeSelf, Is.True);
            Assert.That(slot.Health.CurrentHp, Is.EqualTo(13), "遭遇激活仅执行OnEnable，不能让迟到Awake覆盖已安装HP。");
            Assert.That(registry.TryGetNpcState(5, out bool alive, out bool active), Is.True); Assert.That(alive && active, Is.True);
            registry.SetNpcActive(5, false); yield return null;
            registry.SetNpcActive(5, true); yield return null;
            Assert.That(slot.Health.CurrentHp, Is.EqualTo(13));
            slot.Health.ApplyReplicatedState(new HealthReplicaSnapshot { CurrentHp = 0, MaxHp = 100, TeamId = 2 });
            registry.SetNpcActive(5, false); registry.SetNpcActive(5, true); yield return null;
            Assert.That(slot.Health.IsAlive, Is.False, "可见性/重连不能复活死亡NPC或补满资源。");
        }

        [UnityTest] public IEnumerator VisibleBossAtActorFiveDoesNotBecomeEncounterActiveDuringWarmup()
        {
            var combat = Resources.Load<OnlineCombatDefinition>("Online/CombatDefinition"); Assert.That(combat, Is.Not.Null);
            var registry = Registry(InactiveTemplate(combat.BossPrefab), true, true);
            var slot = registry.EnsureEnemy(5, false); yield return null;
            Assert.That(slot.Boss, Is.Not.Null, "正式P8的Boss可以是配置中的首个NPC，不依赖Actor7。");
            Assert.That(slot.Root.activeInHierarchy, Is.True); Assert.That(slot.Connected, Is.True);
            Assert.That(slot.Boss.ExecutionMode, Is.EqualTo(CombatExecutionMode.Replica));
            Assert.That(slot.Boss.IsEncounterActive, Is.False, "InitiallyVisible只决定可见，不授予开战权限。");
            Assert.That(slot.Health.Id, Is.Not.Zero);
        }

        private GameObject InactiveTemplate(GameObject source)
        {
            Assert.That(source, Is.Not.Null);
            _templateOwner = new GameObject("InactiveTestPrefabOwner"); _templateOwner.SetActive(false);
            var copy = Object.Instantiate(source, _templateOwner.transform, false); copy.SetActive(false);
            var agent = copy.GetComponent<NavMeshAgent>(); if (agent != null) agent.enabled = false;
            return copy;
        }
        private NetworkActorRegistry Registry(GameObject template, bool boss, bool visible)
        {
            _actors = new GameObject("DormantRegistryActors"); _world = new GameObject("DungeonWorldMetadataOnly"); _world.SetActive(false);
            _dungeon = ScriptableObject.CreateInstance<OnlineDungeonDefinition>();
            Set("_dungeonId", boss ? "p8" : "p7"); Set("_sourceScenePath", boss ? "Assets/_Project/Scenes/P8_BossField.unity" : "Assets/_Project/Scenes/P7_DemoRun.unity");
            Set("_sourceHash", new string('a', 64)); Set("_worldPrefab", _world);
            Set("_playerSpawns", new[] { Vector3.zero, Vector3.right, Vector3.left, Vector3.back });
            Set("_npcs", new[] { new OnlineDungeonNpcSpawn { ActorId = 5, Prefab = template, Position = new Vector3(23000, 3000, 17000),
                Rotation = Quaternion.identity, AttackClipDuration = 1, EncounterIndex = 0, InitiallyVisible = visible } });
            Set("_encounters", new[] { new OnlineDungeonEncounterDefinition { DisplayName = "Dormant生命周期", ObjectiveText = "仅验证Awake与HP寿命" } });
            Assert.That(_dungeon.TryValidate(out var error), Is.True, error);
            return new NetworkActorRegistry(Resources.Load<OnlineMovementProfile>("Online/MovementProfile"), _actors.transform,
                Resources.Load<OnlineCombatDefinition>("Online/CombatDefinition"), new CombatSimulationWorld(), _dungeon);
        }
        private void Set(string name, object value) => typeof(OnlineDungeonDefinition).GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_dungeon, value);
    }
}
