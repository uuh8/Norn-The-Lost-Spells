using System.Collections;
using System.Reflection;
using Game.Character;
using Game.Combat;
using Game.Core;
using Game.Multiplayer;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Game.Run.Tests
{
    /// <summary>
    /// 真实PlayMode装配正式导出世界、原NPC与入口；覆盖纯DTO测试看不到的Awake、NavMesh注册和Collider显隐。
    /// 只创建本夹具的Authority角色并移动测试Pose，不发网络身份、不读档、不把它称为双Client验收。
    /// </summary>
    public sealed class FormalDungeonWorldPlayModeTests
    {
        private Scene _scene;
        private OnlineDungeonRuntime _runtime;
        private NetworkActorRegistry _registry;

        [UnityTearDown] public IEnumerator Cleanup()
        {
            _runtime?.Dispose(); _runtime = null;
            _registry?.Dispose(); _registry = null;
            if (_scene.IsValid() && _scene.isLoaded) yield return SceneManager.UnloadSceneAsync(_scene);
            yield return null;
        }

        [UnityTest] public IEnumerator P7OriginalEntryActivatesOnlyTheFirstTwoOfTwelveAuthoredEnemies()
            => CheckMap("p7", 12, 4);

        [UnityTest] public IEnumerator P8VisibleBossStartsOnlyAfterRealSafetyVolumeExit()
            => CheckMap("p8", 1, 1);

        private IEnumerator CheckMap(string id, int expectedNpcs, int expectedAreas)
        {
            var catalog = Resources.Load<OnlineDungeonCatalog>(OnlineDungeonCatalog.ResourcePath);
            Assert.That(catalog, Is.Not.Null); Assert.That(catalog.TryGet(id, out var definition), Is.True);
            Assert.That(definition.TryValidate(out var error), Is.True, error);
            Assert.That(definition.NpcCount, Is.EqualTo(expectedNpcs)); Assert.That(definition.EncounterCount, Is.EqualTo(expectedAreas));
            _scene = SceneManager.CreateScene("FormalDungeonFixture_" + id);
            var world = Object.Instantiate(definition.WorldPrefab); SceneManager.MoveGameObjectToScene(world, _scene);
            world.SetActive(true); Physics.SyncTransforms(); yield return null;
            var bindings = world.GetComponent<OnlineDungeonWorldBindings>(); Assert.That(bindings, Is.Not.Null);
            Assert.That(bindings.TryValidate(expectedAreas, out error), Is.True, error);
            var actors = new GameObject("FixtureOwnedActors"); SceneManager.MoveGameObjectToScene(actors, _scene);
            _registry = new NetworkActorRegistry(Resources.Load<OnlineMovementProfile>("Online/MovementProfile"), actors.transform,
                Resources.Load<OnlineCombatDefinition>("Online/CombatDefinition"), new CombatSimulationWorld(), definition);
            for (int i = 0; i < definition.NpcCount; i++)
            {
                var npc = _registry.EnsureEnemy(definition.GetNpc(i).ActorId, true);
                Assert.That(npc.Health != null && npc.Health.IsAlive, Is.True, "原NPC必须完成真实Awake和Health初始化。");
            }
            var player = _registry.EnsurePlayer(0, PlayerExecutionMode.Authority);
            // 夹具安排完整的Authority可模拟席位；Targetable由连接、首输入Ready和生命共同推导，不能直接绕过。
            player.Connected = player.SimulationReady = true;
            _runtime = new OnlineDungeonRuntime(definition, bindings, _registry, true,
                (actor, active) => Assert.That(_registry.SetNpcActive(actor, active), Is.True),
                actor => Assert.That(_registry[(int)actor - 1].Boss.TryActivateAuthorityEncounter(), Is.True));
            // 从实际导出接线取原形状中心，不另造一个永远为true的Entry mock。
            var areas = (OnlineDungeonAreaBindings[])typeof(OnlineDungeonWorldBindings).GetField("_areas",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(bindings);
            var entry = areas[0].EntryVolumes[0]; Assert.That(entry.enabled && entry.gameObject.activeInHierarchy, Is.True);
            Vector3 center = entry is BoxCollider box ? entry.transform.TransformPoint(box.center) :
                entry is CapsuleCollider capsule ? entry.transform.TransformPoint(capsule.center) : entry.bounds.center;
            MovePlayerCenter(player.Player, center); Physics.SyncTransforms();
            Assert.That(bindings.ContainsEntry(0, center), Is.True, "必须在PlayMode用原入口的实际碰撞形状取得inside。");
            _runtime.Step(1f / 30f, 1);
            Assert.That(_runtime.TryCapture(out var progress), Is.True);
            if (id == "p8")
            {
                Assert.That(progress.Phase, Is.EqualTo(OnlineDungeonPhase.Armed));
                Assert.That(_registry[4].Boss.IsEncounterActive, Is.False, "Boss初始可见仍没有开战权限。");
                MovePlayerCenter(player.Player, center + Vector3.right * (entry.bounds.size.magnitude + 5f)); Physics.SyncTransforms();
                _runtime.Step(1f / 30f, 2); Assert.That(_runtime.TryCapture(out progress), Is.True);
                Assert.That(_registry[4].Boss.IsEncounterActive, Is.True);
            }
            Assert.That(progress.Phase, Is.EqualTo(OnlineDungeonPhase.Active));
            Assert.That(progress.RemainingEnemies, Is.EqualTo(id == "p7" ? 2u : 1u));
            int activeCount = 0;
            for (int i = 0; i < definition.NpcCount; i++) if (_registry[(int)definition.GetNpc(i).ActorId - 1].Connected) activeCount++;
            Assert.That(activeCount, Is.EqualTo(id == "p7" ? 2 : 1), "未来遭遇不能因为预热而提前激活。");
            yield return null;
        }

        private static void MovePlayerCenter(PlayerControllerBase player, Vector3 position)
        {
            var controller = player.CharacterController;
            controller.enabled = false;
            player.transform.position = position - player.transform.TransformVector(controller.center);
            controller.enabled = true;
        }
    }
}
