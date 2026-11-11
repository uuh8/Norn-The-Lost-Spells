using System.Collections;
using System.Reflection;
using Game.Character;
using Game.Combat;
using Game.Core;
using Game.ElementField;
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
        private OnlineMaterialReplicationProbe _replicaSampler;
        private Scene _samplerDestinationScene, _previousActiveScene;
        private string _samplerOutput;
        private string _timingConfig, _timingCommand;

        [System.Serializable] private sealed class TimingTestConfig
        {
            public string Output, Command;
            public int Capacity = 64, DurationMilliseconds = 1000, Domain, X, Y, Z;
            public ulong WorldEpoch = 7;
        }
        [System.Serializable] private sealed class TimingTestReport
        {
            public string State;
            public int EventCount;
            public TimingTestEvent[] Events;
        }
        [System.Serializable] private sealed class TimingTestEvent
        {
            public string Stage;
            public ulong Generation, Revision;
            public long Timestamp, MaterialTick;
        }

        private OnlineMaterialReplicationProbe InstallTimingTestProbe(int milliseconds)
        {
            var method = typeof(OnlineMaterialReplicationProbe).GetMethod("InstallTimingProbe", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null, "显式默认关闭的计时入口尚未实现");
            string stem = System.IO.Path.Combine(Application.temporaryCachePath, "n5-timing-" + System.Guid.NewGuid().ToString("N"));
            _timingConfig = stem + ".config.json"; _samplerOutput = stem + ".report.json"; _timingCommand = stem + ".request";
            var config = new TimingTestConfig { Output = _samplerOutput, Command = _timingCommand, DurationMilliseconds = milliseconds };
            System.IO.File.WriteAllText(_timingConfig, JsonUtility.ToJson(config));
            _replicaSampler = (OnlineMaterialReplicationProbe)method.Invoke(null, new object[] { _timingConfig });
            Assert.That(_replicaSampler, Is.Not.Null);
            return _replicaSampler;
        }

        private void RecordTimingTestEvent()
        {
            var header = new WorldChunkPayloadHeader(7, 3, new WorldChunkKey(WorldMaterialDomain.LegacyCell, 0, 0, 0),
                2, 0, 3, 42, 18, 123, true);
            Assert.That(WorldReplicationTimingTrace.Active.Record(new WorldReplicationTimingEvent
                { Stage = WorldReplicationTimingStage.Committed, Header = header }), Is.True);
        }

        [UnityTearDown] public IEnumerator Cleanup()
        {
            LocalActorCameraPresentation.Clear(197321); LocalActorCameraPresentation.Clear(197322);
            LocalActorPresentation.Clear(197311); LocalActorPresentation.Clear(197312);
            _runtime?.Dispose(); _runtime = null;
            _registry?.Dispose(); _registry = null;
            // 成功修复后Sampler归属DontDestroyOnLoad场景，卸载夹具Scene不会替我们销毁它。
            // 显式释放自有对象并等一帧，确保后续地图/默认关闭用例看不到诊断残留。
            if (_replicaSampler != null) Object.Destroy(_replicaSampler.gameObject);
            _replicaSampler = null;
            yield return null;
            if (_samplerOutput != null && System.IO.File.Exists(_samplerOutput)) System.IO.File.Delete(_samplerOutput);
            if (_samplerOutput != null && System.IO.File.Exists(_samplerOutput + ".status.json")) System.IO.File.Delete(_samplerOutput + ".status.json");
            if (_timingConfig != null && System.IO.File.Exists(_timingConfig)) System.IO.File.Delete(_timingConfig);
            if (_timingCommand != null && System.IO.File.Exists(_timingCommand)) System.IO.File.Delete(_timingCommand);
            _timingConfig = _timingCommand = null;
            _samplerOutput = null;
            if (_previousActiveScene.IsValid() && _previousActiveScene.isLoaded)
                SceneManager.SetActiveScene(_previousActiveScene);
            if (_scene.IsValid() && _scene.isLoaded) yield return SceneManager.UnloadSceneAsync(_scene);
            if (_samplerDestinationScene.IsValid() && _samplerDestinationScene.isLoaded)
                yield return SceneManager.UnloadSceneAsync(_samplerDestinationScene);
            yield return null;
        }

        [TestCase(null)]
        [TestCase("")]
        public void ExplicitTimingProbeDoesNotInstallWithoutConfig(string path)
        {
            var method = typeof(OnlineMaterialReplicationProbe).GetMethod("InstallTimingProbe", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null, "显式默认关闭的计时入口尚未实现");
            Assert.That(method.Invoke(null, new object[] { path }), Is.Null);
            Assert.That(WorldReplicationTimingTrace.Active, Is.Null);
        }

        [UnityTest]
        public IEnumerator ExplicitTimingWindowExportsAfterDetachAndPreservesNetworkStamp()
        {
            InstallTimingTestProbe(1000);
            Assert.That(WorldReplicationTimingTrace.Active, Is.Null, "只装配尚未收到start，不能自行开启采样");
            System.IO.File.WriteAllText(_timingCommand, "start");
            double limit = Time.realtimeSinceStartupAsDouble + 4;
            while (WorldReplicationTimingTrace.Active == null && Time.realtimeSinceStartupAsDouble < limit) yield return null;
            Assert.That(WorldReplicationTimingTrace.Active, Is.Not.Null);
            RecordTimingTestEvent();
            Assert.That(System.IO.File.Exists(_samplerOutput), Is.False, "窗口内不能逐事件导出JSON");
            limit = Time.realtimeSinceStartupAsDouble + 4;
            while (!System.IO.File.Exists(_samplerOutput) && Time.realtimeSinceStartupAsDouble < limit) yield return null;
            Assert.That(System.IO.File.Exists(_samplerOutput), Is.True);
            Assert.That(WorldReplicationTimingTrace.Active, Is.Null, "先脱离记录器再做导出分配/IO");
            var report = JsonUtility.FromJson<TimingTestReport>(System.IO.File.ReadAllText(_samplerOutput));
            Assert.That(report.State, Is.EqualTo("Completed")); Assert.That(report.EventCount, Is.EqualTo(1));
            Assert.That(report.Events[0].Stage, Is.EqualTo("Committed")); Assert.That(report.Events[0].Generation, Is.EqualTo(2));
            Assert.That(report.Events[0].Revision, Is.EqualTo(3)); Assert.That(report.Events[0].MaterialTick, Is.EqualTo(42));
            Assert.That(report.Events[0].Timestamp, Is.GreaterThan(0));
        }

        [UnityTest]
        public IEnumerator ExplicitTimingOwnerDestructionDetachesAndExportsInterruptedPrefix()
        {
            InstallTimingTestProbe(30000); System.IO.File.WriteAllText(_timingCommand, "start");
            double limit = Time.realtimeSinceStartupAsDouble + 4;
            while (WorldReplicationTimingTrace.Active == null && Time.realtimeSinceStartupAsDouble < limit) yield return null;
            Assert.That(WorldReplicationTimingTrace.Active, Is.Not.Null); RecordTimingTestEvent();
            Object.Destroy(_replicaSampler.gameObject); yield return null; _replicaSampler = null;
            Assert.That(WorldReplicationTimingTrace.Active, Is.Null);
            Assert.That(System.IO.File.Exists(_samplerOutput), Is.True);
            var report = JsonUtility.FromJson<TimingTestReport>(System.IO.File.ReadAllText(_samplerOutput));
            Assert.That(report.State, Is.EqualTo("Interrupted")); Assert.That(report.EventCount, Is.EqualTo(1));
        }

        [UnityTest] public IEnumerator P7OriginalEntryActivatesOnlyTheFirstTwoOfTwelveAuthoredEnemies()
            => CheckMap("p7", 12, 4);

        [UnityTest] public IEnumerator P8VisibleBossStartsOnlyAfterRealSafetyVolumeExit()
            => CheckMap("p8", 1, 1);

        [UnityTest] public IEnumerator ExplicitReplicaSamplerSurvivesItsStartupSceneUnload()
        {
            _previousActiveScene = SceneManager.GetActiveScene();
            _scene = SceneManager.CreateScene("ReplicaSamplerStartupFixture");
            Assert.That(SceneManager.SetActiveScene(_scene), Is.True);
            _samplerOutput = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "NornReplicaSamplerLifecycle_" + System.Guid.NewGuid().ToString("N") + ".json");
            _replicaSampler = InstallReplicaSampler(_samplerOutput);
            Assert.That(_replicaSampler != null, Is.True, "显式诊断须真实创建Sampler。");

            // Single切图的关键边界是旧Scene被卸载；测试用临时Scene完成相同销毁流程，
            // 不加载/覆盖正式菜单或Hub，也不改变全局Build Scene List。
            _samplerDestinationScene = SceneManager.CreateScene("ReplicaSamplerDestinationFixture");
            Assert.That(SceneManager.SetActiveScene(_samplerDestinationScene), Is.True);
            yield return SceneManager.UnloadSceneAsync(_scene);
            yield return null;

            Assert.That(_replicaSampler != null, Is.True,
                "RuntimeInitialize只执行一次；Sampler必须跨启动Scene卸载，才能继续观察菜单进入Hub后的Replica。");
            Assert.That(_replicaSampler.enabled, Is.True, "跨Scene后Sampler应保留采样生命周期。");
        }

        [TestCase(null)]
        [TestCase("")]
        public void ReplicaSamplerWithoutExplicitOutputDoesNotCreateAnObject(string output)
        {
            int before = Object.FindObjectsByType<OnlineMaterialReplicationProbe>(FindObjectsInactive.Include,
                FindObjectsSortMode.None).Length;
            _replicaSampler = InstallReplicaSampler(output);
            Assert.That(_replicaSampler == null, Is.True, "默认未开启诊断时不能创建Sampler。");
            Assert.That(Object.FindObjectsByType<OnlineMaterialReplicationProbe>(FindObjectsInactive.Include,
                FindObjectsSortMode.None).Length, Is.EqualTo(before));
        }

        private static OnlineMaterialReplicationProbe InstallReplicaSampler(string output)
        {
            var install = typeof(OnlineMaterialReplicationProbe).GetMethod("InstallReplicaSampler",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(install, Is.Not.Null);
            return (OnlineMaterialReplicationProbe)install.Invoke(null, new object[] { output });
        }

        [UnityTest]
        public IEnumerator ReplicaPresentationInterestFollowsSceneLocalActorAndRejectsOldSceneAfterReplacement()
        {
            _scene = SceneManager.CreateScene("ReplicaPresentationInterest_First");
            var first = CreateReplicaInterestWorld(_scene, 7001, out var firstRoot);
            Vector3 firstOrigin = first.Origin;
            var actor = new GameObject("First Scene local Actor");
            var cameraObject = new GameObject("First Scene local Camera");
            SceneManager.MoveGameObjectToScene(actor, _scene); SceneManager.MoveGameObjectToScene(cameraObject, _scene);
            actor.transform.position = new Vector3(-51, 0, 52);
            var camera = cameraObject.AddComponent<Camera>();
            // 先注册再激活真实P8 Replica，覆盖晚订阅，而非直接调用私有绑定函数。
            LocalActorPresentation.Bind(197311, actor, true, actor.transform);
            LocalActorCameraPresentation.Bind(197321, actor, camera);
            firstRoot.SetActive(true);
            yield return null;
            Assert.That(first.InterestPosition, Is.EqualTo(new Vector3(-51, 0, 52)));
            Assert.That(first.Origin, Is.EqualTo(firstOrigin));

            _samplerDestinationScene = SceneManager.CreateScene("ReplicaPresentationInterest_Second");
            SceneManager.MoveGameObjectToScene(actor, _samplerDestinationScene);
            yield return null;
            Assert.That(first.InterestPosition, Is.EqualTo(first.transform.position),
                "旧Scene不能继续跟随已离开的Actor，Origin也不能跟随移动。");
            Object.Destroy(firstRoot);
            yield return null;

            var second = CreateReplicaInterestWorld(_samplerDestinationScene, 7002, out var secondRoot);
            var nextActor = new GameObject("Replacement local Actor");
            var nextCameraObject = new GameObject("Replacement local Camera");
            SceneManager.MoveGameObjectToScene(nextActor, _samplerDestinationScene);
            SceneManager.MoveGameObjectToScene(nextCameraObject, _samplerDestinationScene);
            nextActor.transform.position = new Vector3(81, 2, -42);
            var nextCamera = nextCameraObject.AddComponent<Camera>();
            secondRoot.SetActive(true);
            LocalActorPresentation.Bind(197312, nextActor, true, nextActor.transform);
            LocalActorCameraPresentation.Bind(197322, nextActor, nextCamera);
            LocalActorCameraPresentation.Clear(197321); LocalActorPresentation.Clear(197311);
            yield return null;
            Assert.That(second.InterestPosition, Is.EqualTo(new Vector3(81, 2, -42)),
                "旧Owner的晚清理不能抢走新Scene本地表现目标。");
            LocalActorCameraPresentation.Clear(197322);
            Assert.That(second.InterestPosition, Is.EqualTo(second.transform.position));
            Assert.That(LocalActorPresentation.Actor, Is.EqualTo(nextActor),
                "材料消费者只能清自己的引用，不得修改Core Actor注册或影响其他HUD消费者。");
        }

        private static ElementWorldRuntime CreateReplicaInterestWorld(Scene scene, ulong epoch, out GameObject root)
        {
            var catalog = Resources.Load<OnlineDungeonCatalog>("Online/DungeonCatalog");
            Assert.That(catalog, Is.Not.Null);
            Assert.That(catalog.TryGet("p8", out var definition), Is.True);
            root = Object.Instantiate(definition.WorldPrefab);
            Assert.That(root.activeSelf, Is.False, "正式导出世界必须在inactive边界完成模式配置。");
            SceneManager.MoveGameObjectToScene(root, scene);
            IOnlineMaterialWorldBinding binding = null;
            foreach (var component in root.GetComponentsInChildren<MonoBehaviour>(true))
                if (component is IOnlineMaterialWorldBinding candidate) binding = candidate;
            Assert.That(binding, Is.Not.Null);
            Assert.That(binding.TryConfigure(WorldExecutionMode.Replica, epoch), Is.True);
            var runtime = root.GetComponentInChildren<ElementWorldRuntime>(true);
            Assert.That(runtime, Is.Not.Null);
            return runtime;
        }

        private IEnumerator CheckMap(string id, int expectedNpcs, int expectedAreas)
        {
            var catalog = Resources.Load<OnlineDungeonCatalog>(OnlineDungeonCatalog.ResourcePath);
            Assert.That(catalog, Is.Not.Null); Assert.That(catalog.TryGet(id, out var definition), Is.True);
            Assert.That(definition.TryValidate(out var error), Is.True, error);
            Assert.That(definition.NpcCount, Is.EqualTo(expectedNpcs)); Assert.That(definition.EncounterCount, Is.EqualTo(expectedAreas));
            _scene = SceneManager.CreateScene("FormalDungeonFixture_" + id);
            var world = Object.Instantiate(definition.WorldPrefab); SceneManager.MoveGameObjectToScene(world, _scene);
            IOnlineMaterialWorldBinding material = null;
            foreach (var component in world.GetComponentsInChildren<MonoBehaviour>(true))
                if (component is IOnlineMaterialWorldBinding candidate)
                { Assert.That(material, Is.Null, "正式世界须只有一个材料Binding。"); material = candidate; }
            Assert.That(material, Is.Not.Null);
            Assert.That(material.TryConfigure(WorldExecutionMode.Authority, 19), Is.True,
                "PlayMode夹具也须遵守激活前模式/世界身份配置，不能依赖Awake顺序。");
            // 验证真实保存的派生资源，而不只验证 Export Preview：Authority 不得启动客户端 Renderer。
            foreach (var component in world.GetComponentsInChildren<MonoBehaviour>(true))
                if (component != null) Assert.That(component.GetType().Namespace, Is.Not.EqualTo("Game.Rendering"),
                    component.GetType().FullName + " on " + component.name);
            world.SetActive(true); Physics.SyncTransforms(); yield return null;
            Assert.That(material.IsReady, Is.True); Assert.That(material.Faulted, Is.False);
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
