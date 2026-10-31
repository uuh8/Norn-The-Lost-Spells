using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Character;
using Game.Core;
using Game.Networking;
using Mirror;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Game.Multiplayer.Tests
{
    /// <summary>正式世界先拿到可信副本身份再创建内容；不能从Scene Awake默认生成灰盒并报告Ready。</summary>
    public sealed class TrustedDungeonInitializationTests
    {
        private GameObject _root;
        private OnlineWorldBootstrap _world;
        private object _previousConnectState;
        private bool _borrowedClientState, _previousClientReady;
        private NetworkManager _previousManager;
        private bool _borrowedSingleton;
        private readonly List<GameObject> _fixtureActors = new List<GameObject>();
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        [SetUp] public void PrepareInactiveCompositionRoot()
        {
            Assert.That(OnlineWorldBootstrap.Active == null, Is.True, "不得接管其他正在运行的世界。");
            _root = new GameObject("TrustedDungeonInitializationFixture"); _root.SetActive(false);
            _world = _root.AddComponent<OnlineWorldBootstrap>();
        }
        [TearDown] public void ReleaseOnlyThisRoot()
        {
            // Registry是Player运行时所有者，Dispose使用延迟Destroy；EditMode夹具自己创建的临时Actor
            // 先用DestroyImmediate结束，避免把Editor测试的销毁语义强加给生产Runtime。
            for (int i = _fixtureActors.Count - 1; i >= 0; i--)
                if (_fixtureActors[i] != null) Object.DestroyImmediate(_fixtureActors[i]);
            _fixtureActors.Clear();
            // 夹具只借用Mirror的连接状态位，不创建Socket、Transport连接或真实账户；无论断言结果如何都恢复。
            if (_borrowedClientState)
            {
                typeof(NetworkClient).GetField("connectState", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, _previousConnectState);
                NetworkClient.ready = _previousClientReady; _borrowedClientState = false;
            }
            if (_borrowedSingleton)
            {
                typeof(NetworkManager).GetProperty("singleton").SetValue(null, _previousManager);
                _borrowedSingleton = false;
            }
            if (_world != null) _world.StopWorld();
            if (_root != null) Object.DestroyImmediate(_root);
            // inactive EditMode夹具不假设Unity会替手工Awake发送OnDestroy；真实Scene另有PlayMode验证。
            if (OnlineWorldBootstrap.Active == _world)
                typeof(OnlineWorldBootstrap).GetField("<Active>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, null);
        }

        [Test] public void AwakeBeforeAuthenticationDoesNotCreateMapActorsOrTickOwner()
        {
            typeof(OnlineWorldBootstrap).GetMethod("Awake", Private).Invoke(_world, null);
            Assert.That(_world.WorldReady, Is.False, "没有身份时不能默认生成灰盒并承诺世界Ready。");
            Assert.That(_world.Registry, Is.Null);
            Assert.That(_root.transform.childCount, Is.Zero);
            Assert.That(_root.GetComponent<AuthorityTickDriver>(), Is.Null, "装配成功前不应取得Tick/全局Physics所有权。");
        }

        [TestCase("p7")][TestCase("p8")][TestCase("")][TestCase("unregistered-dungeon")]
        public void ClientCannotInitializeAnUntrustedDungeonByCallingTheServerEntry(string dungeon)
        {
            var method = typeof(OnlineWorldBootstrap).GetMethod("TryInitializeDungeon");
            Assert.That(method, Is.Not.Null, "DS需要先核实启动描述再显式初始化。");
            Assert.That(method.Invoke(_world, new object[] { dungeon }), Is.False);
            Assert.That(_world.WorldReady, Is.False); Assert.That(_world.Registry, Is.Null);
            Assert.That(_root.transform.childCount, Is.Zero);
        }

        [Test] public void ReadyGuardAlsoChecksTheExpectedDungeonAndLoadedBindings()
        {
            var method = typeof(RoomInstanceBootstrap).GetMethod("IsMovementWorldReady");
            Assert.That(method.GetParameters().Length, Is.EqualTo(2), "Ready要核对Launch的地图，不能只看WorldReady布尔位。");
            Assert.That(method.Invoke(null, new object[] { _world, "p7" }), Is.False);
            Assert.That(typeof(OnlineWorldBootstrap).GetProperty("DungeonId"), Is.Not.Null);
        }

        [Test] public void RejectedIdentityInStateCallbackCannotCreateAnotherActorAfterCleanup()
        {
            PrepareWorldWhoseAuthenticatedIdentityChanged();
            int originalChildren = _root.transform.childCount;
            var content = Field(_world, "_worldContent") as GameObject;
            int expectedAfterCleanup = originalChildren - (content != null && content.transform.parent == _root.transform ? 1 : 0);
            LogAssert.Expect(LogType.Error, "[OnlineDungeon] 存活世界拒绝跨Run/副本身份替换。");
            var state = new MovementStateFrame { ActorId = 2, ConnectionEpoch = 1, SnapshotSequence = 1,
                SimulationHash = ((OnlineMovementProfile)Field(_world, "_profile")).SimulationHash, ServerTick = 1, Connected = true };
            // 远端Actor2此前不存在。旧实现会在StopWorld清理后继续EnsurePlayer，再访问已置空的Combat。
            Assert.DoesNotThrow(() => typeof(OnlineWorldBootstrap).GetMethod("OnState", Private).Invoke(_world, new object[] { state }));
            Assert.That(_world.WorldReady, Is.False);
            Assert.That(_world.Registry[1].Player, Is.Null, "身份拒绝已结束本次回调，不能再为同一条快照创建Replica。");
            Assert.That(_root.transform.childCount, Is.EqualTo(expectedAfterCleanup), "真实地图清理后不能漏出新生Actor根。");
        }

        [Test] public void RejectedIdentityInUpdateCannotResumeTheStoppedWorldTail()
        {
            var existingPlayer = PrepareWorldWhoseAuthenticatedIdentityChanged();
            SetWorld("<LocalPlayer>k__BackingField", existingPlayer); SetWorld("_localActor", 1ul);
            LogAssert.Expect(LogType.Error, "[OnlineDungeon] 存活世界拒绝跨Run/副本身份替换。");
            Assert.DoesNotThrow(() => typeof(OnlineWorldBootstrap).GetMethod("Update", Private).Invoke(_world, null));
            Assert.That(_world.WorldReady, Is.False);
            // StopWorld自身不改变冻结读数；若仍跑Update尾部，Disconnect造成的isConnected=false会把它改为true并再次发布视图。
            Assert.That((bool)typeof(OnlineWorldBootstrap).GetField("_frozen", Private).GetValue(_world), Is.False,
                "已停止世界不得继续执行断线分支或写入表现状态。");
            for (int i = 0; i < _world.Registry.Capacity; i++) Assert.That(_world.Registry[i].Root, Is.Null);
        }

        [Test] public void DestroyedManagerReplacementRebindsChannelsBeforeWorldUpdateContinues()
        {
            var old = PrepareLiveWorldWithInactiveManager();
            var registry = _world.Registry; var runtime = Field(_world, "_dungeonRuntime");
            var replacement = CreateInactiveManager("ReplacementManager");
            typeof(NetworkManager).GetProperty("singleton").SetValue(null, replacement);
            // 正式 OnlineRoomClient.Connect 会销毁旧 Manager，再创建下一代；不能只测同一个 Manager 重新连线。
            Object.DestroyImmediate(old.gameObject);
            typeof(OnlineWorldBootstrap).GetMethod("Update", Private).Invoke(_world, null);
            var movement = replacement.GetComponent<MovementNetworkChannel>();
            Assert.That(movement, Is.Not.Null,
                "ResolveManager不能只替换_manager便跳过BindNetwork，旧World必须连接到新Manager的实际通道。");
            Assert.That(Field(_world, "_channel"), Is.SameAs(movement));
            Assert.That(Field(_world, "_progressChannel"), Is.SameAs(replacement.GetComponent<DungeonProgressNetworkChannel>()));
            Assert.That(_world.Registry, Is.SameAs(registry)); Assert.That(Field(_world, "_dungeonRuntime"), Is.SameAs(runtime));
            Assert.That(_world.WorldReady, Is.True, "换连接不能重新创建地图、遭遇和角色权威。");
        }

        [Test] public void ExplicitNetworkRebindReconfiguresProgressFromExistingTrustedDungeon()
        {
            PrepareLiveWorldWithInactiveManager();
            var registry = _world.Registry; var runtime = Field(_world, "_dungeonRuntime");
            var replacement = CreateInactiveManager("ExplicitReplacement");
            var next = replacement.gameObject.AddComponent<DungeonProgressNetworkChannel>();
            next.Configure("untrusted-other-run", "p8", 7);
            // 预置不同World残留候选，重绑必须用当前可信身份配置并清队列，不能只交换引用。
            var inbox = Field(next, "_inbox");
            inbox.GetType().GetMethod("TryEnqueue").Invoke(inbox, new object[] { new OnlineDungeonProgress { Revision = 999 } });
            typeof(NetworkManager).GetProperty("singleton").SetValue(null, replacement);
            typeof(OnlineWorldBootstrap).GetMethod("BindNetwork", Private).Invoke(_world, null);
            Assert.That(Field(_world, "_progressChannel"), Is.SameAs(next),
                "DungeonProgress与Movement/Combat属于同一代Manager，不能继续引用上一代组件。");
            Assert.That(Field(next, "_run"), Is.EqualTo("same-run"));
            Assert.That(Field(next, "_dungeon"), Is.EqualTo("p7"));
            Assert.That(Field(next, "_hash"), Is.EqualTo(Resources.Load<OnlineCombatDefinition>("Online/CombatDefinition").WireHash));
            var take = new object[] { default(OnlineDungeonProgress) };
            Assert.That(inbox.GetType().GetMethod("TryDequeue").Invoke(inbox, take), Is.False,
                "重新配置前的异Run候选不能进入保留的DungeonRuntime。");
            Assert.That(_world.Registry, Is.SameAs(registry)); Assert.That(Field(_world, "_dungeonRuntime"), Is.SameAs(runtime));
        }

        [Test] public void ReusedCastPublishesOnlyToCurrentCombatOwnerAndRemoteBindCannotSendLocalIntent()
        {
            PrepareLiveWorldWithInactiveManager();
            var root = new GameObject("RetainedPredictionActor"); root.SetActive(false); root.transform.SetParent(_root.transform, false);
            _fixtureActors.Add(root);
            root.AddComponent<SpellCaster>();
            var slot = _world.Registry[0]; slot.Root = root; slot.Player = root.AddComponent<WizardController>();
            var previous = _world.Combat;
            previous.BindClient(slot, 1, 1, true); previous.BindClient(slot, 1, 1, true);
            var driver = slot.Cast;
            int observed = 0; Action<OnlineCastIntent> independentObserver = _ => observed++;
            driver.IntentProduced += independentObserver;
            try
            {
                var captured = IntentHandlers(driver);
                Assert.That(CountOwnedHandlers(captured, previous), Is.EqualTo(1), "重复Bind不能积累同一Owner的订阅。");
                captured.Invoke(default);
                Assert.That(observed, Is.EqualTo(1));
                previous.Dispose();
                Assert.That(CountOwnedHandlers(IntentHandlers(driver), previous), Is.Zero,
                    "角色比Manager活得久；旧Combat释放时必须解绑自己真正持有的Cast委托。");
                // 多播委托可能已被当前调用捕获。即使Dispose完成，旧Owner也必须自行拒绝迟到发布。
                Assert.DoesNotThrow(() => captured.Invoke(default)); Assert.That(observed, Is.EqualTo(2));
                var replacement = CreateInactiveManager("CastReplacementManager");
                typeof(NetworkManager).GetProperty("singleton").SetValue(null, replacement);
                typeof(OnlineWorldBootstrap).GetMethod("BindNetwork", Private).Invoke(_world, null);
                var current = _world.Combat;
                current.BindClient(slot, 1, 2, true); current.BindClient(slot, 1, 2, true);
                Assert.That(slot.Cast, Is.SameAs(driver), "重连复用角色Driver，不重发资产或用销毁角色掩盖委托泄漏。");
                var active = IntentHandlers(driver);
                Assert.That(CountOwnedHandlers(active, previous), Is.Zero);
                Assert.That(CountOwnedHandlers(active, current), Is.EqualTo(1));
                active.Invoke(default); Assert.That(observed, Is.EqualTo(3), "真实事件仍保留非Combat订阅者。");
                current.BindClient(slot, 1, 2, false);
                Assert.That(CountOwnedHandlers(IntentHandlers(driver), current), Is.Zero, "Replica远端角色没有本机发送权限。");
                current.Dispose(); IntentHandlers(driver).Invoke(default); Assert.That(observed, Is.EqualTo(4));
            }
            finally { driver.IntentProduced -= independentObserver; }
        }

        [Test] public void ReplacingFrozenCombatOwnerRestoresOnlyItsCapturedAnimatorSpeed()
        {
            PrepareLiveWorldWithInactiveManager();
            var root = new GameObject("RetainedFrozenActor"); root.SetActive(false); root.transform.SetParent(_root.transform, false);
            _fixtureActors.Add(root);
            var slot = _world.Registry[0]; slot.Root = root; slot.Player = root.AddComponent<WizardController>();
            var animator = root.AddComponent<Animator>(); animator.speed = .65f;
            var previous = _world.Combat;
            SetWorld("_frozen", true); previous.Present(1);
            Assert.That(animator.speed, Is.Zero, "先观察真实Present冻结边沿，不能直接伪造缓存中的速度。");
            previous.Dispose();
            Assert.That(animator.speed, Is.EqualTo(.65f),
                "旧Combat持有冻结权限；角色跨Manager存活时必须恢复原速，不能把speed=0交给默认未冻结的新Owner。");
            var replacement = CreateInactiveManager("FrozenReplacementManager");
            typeof(NetworkManager).GetProperty("singleton").SetValue(null, replacement);
            typeof(OnlineWorldBootstrap).GetMethod("BindNetwork", Private).Invoke(_world, null);
            SetWorld("_frozen", false); _world.Combat.Present(2);
            Assert.That(animator.speed, Is.EqualTo(.65f), "不能将作者原速度强改成1，也不能依赖新Owner不存在的冻结恢复边沿。");
            SetWorld("_frozen", true); _world.Combat.Present(3);
            SetWorld("_frozen", false); _world.Combat.Present(4);
            animator.speed = .8f; _world.Combat.Dispose();
            Assert.That(animator.speed, Is.EqualTo(.8f), "Owner已经结束冻结后，Dispose不能用历史快照覆盖角色后来的速度。");
        }

        private static Action<OnlineCastIntent> IntentHandlers(OnlineCastDriver driver) =>
            (Action<OnlineCastIntent>)typeof(OnlineCastDriver).GetField("IntentProduced", Private).GetValue(driver);
        private static int CountOwnedHandlers(Action<OnlineCastIntent> handlers, object owner)
        {
            int count = 0;
            if (handlers != null) foreach (var handler in handlers.GetInvocationList()) if (ReferenceEquals(handler.Target, owner)) count++;
            return count;
        }

        private RoomNetworkManager PrepareLiveWorldWithInactiveManager()
        {
            Assert.That(NetworkClient.active || NetworkServer.active || NetworkClient.connection != null || NetworkManager.singleton != null,
                Is.False, "测试只借用空闲singleton，不建立Socket、账户或真实连接。");
            _previousManager = NetworkManager.singleton; _borrowedSingleton = true;
            typeof(OnlineWorldBootstrap).GetMethod("Awake", Private).Invoke(_world, null);
            var profile = Resources.Load<OnlineMovementProfile>("Online/MovementProfile");
            var combat = Resources.Load<OnlineCombatDefinition>("Online/CombatDefinition");
            Assert.That(Resources.Load<OnlineDungeonCatalog>("Online/DungeonCatalog").TryGet("p7", out var dungeon), Is.True);
            // Clone保留inactive，导航/角色不执行生命周期；Runtime与Registry仍是真实领域实现，不用伪造对象代替所有权断言。
            var content = Object.Instantiate(dungeon.WorldPrefab, _root.transform, false); content.SetActive(false);
            var registry = new NetworkActorRegistry(profile, _root.transform);
            var runtime = new OnlineDungeonRuntime(dungeon, content.GetComponentInChildren<OnlineDungeonWorldBindings>(true),
                registry, false, null, null);
            SetWorld("_profile", profile); SetWorld("_combatDefinition", combat);
            SetWorld("_combatSimulation", new Game.Combat.CombatSimulationWorld());
            SetWorld("_dungeonDefinition", dungeon); SetWorld("_dungeonRuntime", runtime); SetWorld("_worldContent", content);
            SetWorld("_runId", "same-run"); SetWorld("<Registry>k__BackingField", registry); SetWorld("<WorldReady>k__BackingField", true);
            var manager = CreateInactiveManager("OriginalManager");
            typeof(NetworkManager).GetProperty("singleton").SetValue(null, manager);
            typeof(OnlineWorldBootstrap).GetMethod("BindNetwork", Private).Invoke(_world, null);
            // 旧实现只在InitializeDungeon建立进度通道；夹具完整还原这个已运行World的起点。
            var progress = manager.GetComponent<DungeonProgressNetworkChannel>();
            if (progress == null) progress = manager.gameObject.AddComponent<DungeonProgressNetworkChannel>();
            progress.Configure("same-run", "p7", combat.WireHash); SetWorld("_progressChannel", progress);
            return manager;
        }

        private RoomNetworkManager CreateInactiveManager(string name)
        {
            var root = new GameObject(name); root.SetActive(false); root.transform.SetParent(_root.transform, false);
            // 先建立具体RoomManager，再装Authenticator：Mirror的Authenticator要求NetworkManager，
            // 若反过来会自动补出基类Manager，阻止同物体再添加RoomManager。两者都必须早于BindNetwork，
            // 否则通道稍后补出的Auth不在World最初缓存中，测试会错误地绕过实际身份校验。
            var manager = root.AddComponent<RoomNetworkManager>();
            root.AddComponent<JoinTicketAuthenticator>();
            return manager;
        }
        private static object Field(object target, string name) => target.GetType().GetField(name, Private).GetValue(target);

        private WizardController PrepareWorldWhoseAuthenticatedIdentityChanged()
        {
            var manager = PrepareLiveWorldWithInactiveManager();
            // Update现在显式核对“完成绑定”的Manager。身份回归也必须给出一致的真实组合，
            // 不能只塞一个脱离singleton的_auth，让新的重绑逻辑先正确清掉这个伪造旧引用。
            var template = new GameObject("InactivePlayerTemplate"); template.SetActive(false); template.transform.SetParent(_root.transform, false);
            var player = template.AddComponent<WizardController>();
            var auth = manager.GetComponent<JoinTicketAuthenticator>();
            typeof(JoinTicketAuthenticator).GetField("<ClientAuthenticated>k__BackingField", Private).SetValue(auth, true);
            typeof(JoinTicketAuthenticator).GetField("<ClientIdentity>k__BackingField", Private).SetValue(auth,
                new JoinConnectionIdentity("player", "room", "new-run", 1, 2, 1, "p8"));
            var connectField = typeof(NetworkClient).GetField("connectState", BindingFlags.Static | BindingFlags.NonPublic);
            _previousConnectState = connectField.GetValue(null); _previousClientReady = NetworkClient.ready; _borrowedClientState = true;
            connectField.SetValue(null, Enum.Parse(connectField.FieldType, "Connected"));
            return player;
        }

        private void SetWorld(string field, object value) => typeof(OnlineWorldBootstrap).GetField(field, Private).SetValue(_world, value);
    }
}
