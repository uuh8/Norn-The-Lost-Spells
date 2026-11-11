using System.Reflection;
using System.Text.RegularExpressions;
using Game.Character;
using Game.Combat;
using Game.Core;
using Game.Networking;
using Game.NetworkProtocol;
using Mirror;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Game.Multiplayer.Tests
{
    /// <summary>
    /// 直接把已通过 Wire 校验的事实交给真实 OnlineCombatWorld，观察正式 Prefab 的 Health 与输出实例。
    /// 不启动 Transport，不模拟网络认证，也不复制一份消息安装逻辑；可靠/不可靠跨通道乱序在此精确排列。
    /// </summary>
    public sealed class OnlineCombatReplicationTests
    {
        private GameObject _worldRoot, _networkRoot;
        private OnlineCombatWorld _combat;
        private NetworkActorRegistry _registry;
        private OnlineCombatDefinition _definition;
        private int _damageEvents, _deathEvents;
        private float _lastDamageRemainingHp;
        private static readonly MethodInfo Install = typeof(OnlineCombatWorld).GetMethod("OnFact", BindingFlags.Instance | BindingFlags.NonPublic);

        [SetUp]
        public void SetUp()
        {
            _damageEvents = _deathEvents = 0; _lastDamageRemainingHp = -1;
            var profile = Resources.Load<OnlineMovementProfile>("Online/MovementProfile");
            var definitionAsset = Resources.Load<OnlineCombatDefinition>("Online/CombatDefinition");
            Assert.That(definitionAsset, Is.Not.Null);
            // 测试中的受控表变体只改内存副本，不能给用户真实SO留下dirty/持久化改动。
            _definition = Object.Instantiate(definitionAsset);
            Assert.That(profile, Is.Not.Null, "使用正式 MovementProfile，而不是在测试里重新搭近似角色。");
            Assert.That(_definition, Is.Not.Null);
            Assert.That(profile.PlayerPrefab, Is.Not.Null);
            Assert.That(Install, Is.Not.Null);

            // 保持 inactive，从而只测试安装结果，不运行世界 Awake、自动 Input 或真实 NetworkManager 单例注册。
            _worldRoot = new GameObject("N4 replica installation fixture"); _worldRoot.SetActive(false);
            var world = _worldRoot.AddComponent<OnlineWorldBootstrap>();
            var simulation = new CombatSimulationWorld();
            _registry = new NetworkActorRegistry(profile, _worldRoot.transform, _definition, simulation);
            typeof(OnlineWorldBootstrap).GetField("<Registry>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(world, _registry);
            typeof(OnlineWorldBootstrap).GetField("_stopped", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(world, true);

            var previousManager = NetworkManager.singleton;
            _networkRoot = new GameObject("N4 inactive channel fixture"); _networkRoot.SetActive(false);
            var manager = _networkRoot.AddComponent<RoomNetworkManager>();
            var channel = _networkRoot.AddComponent<CombatNetworkChannel>();
            Assert.That(NetworkManager.singleton, Is.SameAs(previousManager), "安装测试不能抢走 Editor 已有的网络组合根。");
            _combat = new OnlineCombatWorld(world, _definition, simulation, channel, manager, null);
            EventBus<DamageReceivedEvent>.Subscribe(OnDamage);
            EventBus<DeathEvent>.Subscribe(OnDeath);
        }

        [TearDown]
        public void TearDown()
        {
            EventBus<DamageReceivedEvent>.Unsubscribe(OnDamage);
            EventBus<DeathEvent>.Unsubscribe(OnDeath);
            // 先由 fixture Owner 释放仍在场的 inactive Replica，再 Dispose 订阅，避免在 EditMode 调用延迟 Destroy。
            if (_worldRoot != null) Object.DestroyImmediate(_worldRoot);
            _combat?.Dispose(); _combat = null;
            if (_networkRoot != null) Object.DestroyImmediate(_networkRoot);
            if (_definition != null) Object.DestroyImmediate(_definition);
        }

        [Test]
        public void EnvironmentToxicBurstPublishesOnceWithoutCreatingAnActorOrDamage()
        {
            int bursts = 0;
            Vector3 actual = default;
            System.Action<ElementReactionEvent> listener = value => { bursts++; actual = value.WorldPosition; };
            EventBus<ElementReactionEvent>.Subscribe(listener);
            try
            {
                var reaction = Fact(12, 0, 1, 10, 0);
                reaction.Result = 1; reaction.Phase = 3; reaction.Amount = .5f;
                reaction.PositionX = 2; reaction.PositionY = 3; reaction.PositionZ = 4;
                Receive(reaction);
                Receive(reaction);
                reaction.Sequence = 9; Receive(reaction);
                Assert.That(bursts, Is.EqualTo(1), "同一可靠反应事实只能播放一次。");
                Assert.That(actual, Is.EqualTo(new Vector3(2, 3, 4)));
                Assert.That(_combat.ActiveOutputs, Is.Zero, "Burst只发原表现事件，不创建战斗输出。");
                Assert.That(_damageEvents, Is.Zero);
                reaction.Sequence = 20; reaction.TargetActorId = ulong.MaxValue; Receive(reaction);
                reaction.Sequence = 11; reaction.TargetActorId = 0; Receive(reaction);
                Assert.That(bursts, Is.EqualTo(2), "非法载体不能推进可靠序号。");
                _combat.Dispose(); reaction.Sequence = 12; Receive(reaction);
                Assert.That(bursts, Is.EqualTo(2), "旧World退出后不再发布表现。");
            }
            finally { EventBus<ElementReactionEvent>.Unsubscribe(listener); }
        }

        private uint IncludeContainerThrowKey()
        {
            Assert.That(_definition.ContainerThrowPrefab, Is.Not.Null);
            uint existing = _definition.FindOutputKey(_definition.ContainerThrowPrefab);
            if (existing != 0) return existing;
            var data = new SerializedObject(_definition); var array = data.FindProperty("_outputPrefabs");
            int slot = array.arraySize; array.arraySize = slot + 1;
            array.GetArrayElementAtIndex(slot).objectReferenceValue = _definition.ContainerThrowPrefab;
            data.ApplyModifiedPropertiesWithoutUndo();
            return _definition.FindOutputKey(_definition.ContainerThrowPrefab);
        }

        [Test]
        public void MaterialMarkerBirthAndStateAreVisualOnlyAndWaitingCannotRegress()
        {
            int writes = 0; System.Action<SpellMaterialOutputEvent> listener = _ => writes++;
            EventBus<SpellMaterialOutputEvent>.Subscribe(listener);
            try
            {
                var birth = Fact(4, 1, 1, 10, 0); birth.OutputId = 77; birth.OutputOrdinal = 1;
                birth.PrefabKey = 0; birth.MaterialOutput = true; birth.MaterialWaiting = false;
                Receive(birth); Assert.That(_combat.ActiveOutputs, Is.EqualTo(1));
                var marker = _worldRoot.GetComponentInChildren<Game.Character.MaterialPayloadRuntimeAdapter>(true);
                Assert.That(marker, Is.Not.Null); Assert.That(marker.gameObject.activeSelf, Is.True);
                var renderer = marker.GetComponentInChildren<Renderer>(true);
                Assert.That(renderer, Is.Not.Null); Assert.That(renderer.enabled, Is.False, "随弹飞行预算仅占槽，原橙球只在待投放时显示。");
                var waiting = Fact(5, 1, 1, 11, 0); waiting.OutputId = 77;
                waiting.MaterialOutput = true; waiting.MaterialWaiting = true; Receive(waiting);
                Assert.That(marker.IsWaiting, Is.True); Assert.That(renderer.enabled, Is.True);
                Vector3 point = marker.transform.position;
                Assert.That(Mathf.Max(renderer.bounds.size.x, Mathf.Max(renderer.bounds.size.y, renderer.bounds.size.z)), Is.EqualTo(.15f).Within(.001f));
                var lateFlying = Fact(5, 1, 1, 12, 0); lateFlying.OutputId = 77;
                lateFlying.MaterialOutput = true; lateFlying.MaterialWaiting = false; lateFlying.PositionX += 5; Receive(lateFlying);
                Assert.That(marker.IsWaiting, Is.True); Assert.That(marker.transform.position, Is.EqualTo(point));
                foreach (var collider in marker.GetComponentsInChildren<Collider>(true)) Assert.That(collider.enabled, Is.False);
                typeof(Game.Character.MaterialPayloadRuntimeAdapter).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(marker, null);
                Assert.That(writes, Is.Zero, "材料Replica只画标记，不发送真实预算投放/Ended事件。");
            }
            finally { EventBus<SpellMaterialOutputEvent>.Unsubscribe(listener); }
        }

        [Test]
        public void LocalReconnectClearsOldMaterialMarkersAndReplaysTheSameLiveOutput()
        {
            var birth = Fact(4, 1, 1, 10, 0); birth.OutputId = 77; birth.OutputOrdinal = 1;
            birth.PrefabKey = 0; birth.MaterialOutput = true; birth.MaterialWaiting = true;
            Receive(birth); Assert.That(_combat.ActiveOutputs, Is.EqualTo(1));
            var slot = _registry[0]; _combat.BindClient(slot, 1, 1, true);
            Assert.That(_combat.ActiveOutputs, Is.EqualTo(1));
            _combat.BindClient(slot, 1, 2, true); Assert.That(_combat.ActiveOutputs, Is.Zero, "本地新Epoch清旧marker，不向DS发送预算结束。");
            slot.Epoch = 2; birth.ConnectionEpoch = 2; birth.Sequence = 20;
            Receive(birth); Assert.That(_combat.ActiveOutputs, Is.EqualTo(1));
            var marker = _worldRoot.GetComponentInChildren<Game.Character.MaterialPayloadRuntimeAdapter>(true);
            Assert.That(marker != null && marker.IsWaiting && marker.gameObject.activeSelf, Is.True);
            _combat.BindClient(slot, 1, 2, true); Assert.That(_combat.ActiveOutputs, Is.EqualTo(1));
        }

        [Test]
        public void ContainerBirthRestoresTheRealModelAndWaitingAndCannotReturnToFlying()
        {
            uint key = IncludeContainerThrowKey();
            Assert.That(_definition.TryGetContainerWorldModel("containertest.bottle", out _), Is.True);
            var birth = Fact(4, 1, 1, 10, 0); birth.OutputId = 55; birth.PrefabKey = key; birth.OutputOrdinal = 1;
            birth.ContainerContentId = "containertest.bottle"; birth.ContainerWaiting = true;
            Receive(birth);
            Assert.That(_combat.ActiveOutputs, Is.EqualTo(1));
            var bottle = _worldRoot.GetComponentInChildren<ThrownContainerProjectile>(true);
            Assert.That(bottle, Is.Not.Null);
            Assert.That(bottle.gameObject.activeSelf, Is.True,
                "原ThrowPrefab为Prepare而默认inactive；Replica完成净化后也必须显式出生可见。");
            Assert.That(bottle.IsWaiting, Is.True, "重连的可靠出生必须带当前Waiting，不能重新播放投掷。");
            Assert.That(bottle.transform.Find("ThrownContainerModel"), Is.Not.Null);
            foreach (var collider in bottle.GetComponentsInChildren<Collider>(true)) Assert.That(collider.enabled, Is.False);
            Vector3 waitingPoint = bottle.transform.position;
            var newerFlying = Fact(5, 1, 1, 11, 0); newerFlying.OutputId = 55;
            newerFlying.PositionX += 5; // 省略模型字符串，真正检查Waiting状态不可回退。
            Receive(newerFlying);
            Assert.That(bottle.IsWaiting, Is.True);
            Assert.That(bottle.transform.position, Is.EqualTo(waitingPoint), "已等待释放的同一输出不能由Flying状态重新飞走。");
        }

        [Test]
        public void LocalReconnectDropsOnlyOldContainerReplicasAndCanReplayTheSameLiveOutput()
        {
            uint key = IncludeContainerThrowKey();
            var birth = Fact(4, 1, 1, 10, 0); birth.OutputId = 55; birth.PrefabKey = key;
            birth.ContainerContentId = "containertest.bottle"; birth.ContainerWaiting = true;
            Receive(birth);
            var slot = _registry[0];
            _combat.BindClient(slot, 1, 1, true);
            Assert.That(_combat.ActiveOutputs, Is.EqualTo(1));
            _combat.BindClient(slot, 1, 2, true);
            Assert.That(_combat.ActiveOutputs, Is.Zero,
                "离线期间已Resolve的旧瓶不能永久留在本地；清理只作用于Replica，不碰DS在途资产。");
            slot.Epoch = 2;
            birth.ConnectionEpoch = 2; birth.Sequence = 20;
            Receive(birth);
            Assert.That(_combat.ActiveOutputs, Is.EqualTo(1), "DS仍存活的同OutputId可按当前Waiting重新安装。");
            var bottle = _worldRoot.GetComponentInChildren<ThrownContainerProjectile>(true);
            Assert.That(bottle != null && bottle.IsWaiting && bottle.gameObject.activeSelf, Is.True);
            _combat.BindClient(slot, 1, 2, true);
            Assert.That(_combat.ActiveOutputs, Is.EqualTo(1), "重复绑定同一Epoch不能重复清可见输出。");
        }

        [Test]
        public void ContainerBirthRejectsUnknownModelsAndAnUnrelatedControlledProjectilePrefab()
        {
            uint key = IncludeContainerThrowKey();
            var unknown = Fact(4, 1, 1, 10, 0); unknown.OutputId = 55; unknown.PrefabKey = key;
            unknown.ContainerContentId = "not.in.the.real.catalog"; Receive(unknown);
            Assert.That(_combat.ActiveOutputs, Is.Zero);
            var fireball = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Prefabs/Wizard/Magic/Fireball.prefab");
            unknown.Sequence = 11; unknown.OutputId = 56; unknown.PrefabKey = _definition.FindOutputKey(fireball);
            unknown.ContainerContentId = "containertest.bottle"; Receive(unknown);
            Assert.That(_combat.ActiveOutputs, Is.Zero, "瓶模型字段不能把受控Fireball改造成投瓶。");
        }

        [Test]
        public void UnreplicableCommittedOutputIsRetainedWhenTheWorldFaults()
        {
            // 输出出生时已代表上游提交的事实；复制拒绝只能停世界，不能借销毁输出来撤销事实。
            var world = _worldRoot.GetComponent<OnlineWorldBootstrap>();
            typeof(OnlineWorldBootstrap).GetProperty("IsAuthority").SetValue(world, true);
            var output = new GameObject("Committed output retained for fault inspection");
            output.transform.SetParent(_worldRoot.transform);
            var unknownPrefab = new GameObject("Not registered in the controlled output table");
            try
            {
                LogAssert.Expect(LogType.Error, new Regex("输出超出受控 Prefab 表或复制容量"));
                EventBus<CombatOutputSpawnedEvent>.Publish(new CombatOutputSpawnedEvent
                { Output = output, DefinitionPrefab = unknownPrefab, ActorId = 1, CommandId = 1 });
                Assert.That(_combat.Faulted, Is.True);
                Assert.That(output != null && output.activeSelf, Is.True,
                    "保留已提交输出；不得触发OnDisable/OnDestroy取消链或丢失在途材料。");
            }
            finally { Object.DestroyImmediate(unknownPrefab); }
        }

        [Test]
        public void NewerHpSnapshot_OlderReliableDamageKeepsInstalledHpAndFeedbackReadModel()
        {
            Receive(Fact(7, 1, 3, 20, 40f));
            var oldDamage = Fact(8, 1, 3, 19, 70f); oldDamage.Amount = 10f;
            Receive(oldDamage);
            Assert.That(_registry[0].Health.CurrentHp, Is.EqualTo(40f), "迟到的可靠命中不能回填旧 HP。");
            Assert.That(_damageEvents, Is.EqualTo(1), "尚未呈现的命中反馈仍应只到达一次。");
            Assert.That(_lastDamageRemainingHp, Is.EqualTo(40f), "反馈消费者同样必须读取已安装的新 HP，否则血条仍会短暂回涨。");
        }

        [Test]
        public void DeathFact_OlderLivingSnapshotCannotReviveActorOrRepeatDeath()
        {
            Receive(Fact(7, 1, 1, 10, 80f));
            Receive(Fact(9, 1, 1, 12, 0f));
            Receive(Fact(7, 1, 1, 11, 80f));
            Receive(Fact(9, 1, 1, 12, 0f));
            Assert.That(_registry[0].Health.CurrentHp, Is.Zero);
            Assert.That(_registry[0].Health.IsAlive, Is.False);
            Assert.That(_deathEvents, Is.EqualTo(1), "晚到状态与重复可靠事件都不能再次触发死亡表现。");
        }

        [Test]
        public void NewerDeadSnapshot_OlderNonlethalDamageCannotShortenCorpseDeadline()
        {
            var deathState = Fact(7, 1, 1, 100, 0f); deathState.Amount = 1.9f;
            Receive(deathState);
            var lifetime = _registry[0].BodyLifetime;
            Assert.That(lifetime.RemainingSeconds(100), Is.EqualTo(1.9f).Within(.0001f));

            // 两条通道没有总顺序：当前读模型已死亡，不代表这个旧 Damage 自身就是致死事实。
            // 若把 tick90 的非致死命中当成死亡起点，MarkDeath 的“只缩短”保护反而会永久提前清尸体。
            var oldDamage = Fact(8, 1, 1, 90, 50f); oldDamage.Amount = 10f;
            Receive(oldDamage);

            Assert.That(lifetime.RemainingSeconds(100), Is.EqualTo(1.9f).Within(.0001f),
                "非致死旧事实可保留命中反馈，但不能修改较新死亡事实给出的尸体期限。");
            Assert.That(_registry[0].Health.CurrentHp, Is.Zero);
            Assert.That(_damageEvents, Is.EqualTo(1));
            Assert.That(_deathEvents, Is.EqualTo(1));
        }

        [Test]
        public void NewerDeadSnapshot_OlderReliableActionCannotReclaimAnimatorFromDeath()
        {
            var wizard = PrepareRemoteWizard();
            var deathState = Fact(7, 2, 1, 20, 0f); deathState.Amount = 1.9f;
            Receive(deathState);
            Assert.That(wizard.IsOnlineActionPresentationActive, Is.False);

            // 可靠 Action 在自己的流里仍是新消息；较新的不可靠死亡状态已经让原 Prefab 进入死亡表现。
            // 检查实际施法 Overlay 入口的所有权，避免 inactive EditMode 的 Animator 求值时机影响断言。
            Receive(ActionFact(10));
            Assert.That(wizard.IsOnlineActionPresentationActive, Is.False,
                "死亡后不能由迟到施法重新取得 Animator 所有权并覆盖死亡动画。");
            var death = Fact(9, 2, 1, 19, 0f); death.Amount = 1.9f;
            Receive(death);
            Assert.That(_deathEvents, Is.EqualTo(1), "可靠死亡晚到不能靠重播 Death 来掩盖旧动作覆盖。");
        }

        [Test]
        public void NewerLivingSnapshot_OlderUnpresentedActionStillUsesOriginalCastPresentation()
        {
            var wizard = PrepareRemoteWizard();
            Receive(Fact(7, 2, 1, 20, 80f));
            Receive(ActionFact(10));
            Assert.That(wizard.IsOnlineActionPresentationActive, Is.True,
                "活人的普通 HP 状态比动作新，不代表可靠动作已经播放；不能用 ActorState 序列笼统丢弃。");
            Assert.That(_registry[1].Health.CurrentHp, Is.EqualTo(80f));
            Assert.That(_deathEvents, Is.Zero);
        }

        [Test]
        public void OutputStateAfterTermination_CannotRecreateTerminatedOutput()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Prefabs/Wizard/Magic/Fireball.prefab");
            uint key = _definition.FindOutputKey(prefab);
            Assert.That(key, Is.GreaterThan(0), "实际生产 Fireball 必须已在受控输出表中。");
            var birth = Fact(4, 1, 1, 10, 0); birth.OutputId = 41; birth.CommandId = 100;
            birth.OutputOrdinal = 1; birth.PrefabKey = key;
            Receive(birth);
            Assert.That(_combat.ActiveOutputs, Is.EqualTo(1));

            var terminate = Fact(6, 1, 1, 12, 0); terminate.OutputId = 41; terminate.CommandId = 100; terminate.Result = 1;
            // 保留真实的运行时 Destroy 路径；Editor 拒绝延迟销毁后，本 Fixture 最后会 DestroyImmediate 清理。
            LogAssert.Expect(LogType.Error, new Regex("Destroy may not be called from edit mode"));
            Receive(terminate);
            Assert.That(_combat.ActiveOutputs, Is.Zero);
            int remainingChildren = _worldRoot.transform.childCount;

            var lateState = Fact(5, 1, 1, 11, 0); lateState.OutputId = 41; lateState.CommandId = 100;
            lateState.PositionX = 800f; Receive(lateState);
            Assert.That(_combat.ActiveOutputs, Is.Zero);
            Assert.That(_worldRoot.transform.childCount, Is.EqualTo(remainingChildren), "快照只能修正已登记产出，不能拥有出生权限。");
        }

        [Test]
        public void ActorEpochs_AreIndependentAndRejectOnlyTheirOwnStaleGeneration()
        {
            Receive(Fact(7, 1, 9, 10, 81f));
            Receive(Fact(7, 2, 2, 11, 72f));
            Receive(Fact(7, 2, 3, 12, 63f));
            Receive(Fact(7, 1, 8, 13, 1f));
            Receive(Fact(7, 2, 3, 14, 53f));
            Assert.That(_registry[0].Epoch, Is.EqualTo(9));
            Assert.That(_registry[0].Health.CurrentHp, Is.EqualTo(81f));
            Assert.That(_registry[1].Epoch, Is.EqualTo(3));
            Assert.That(_registry[1].Health.CurrentHp, Is.EqualTo(53f), "Actor 1 的高 Epoch 不应拒绝 Actor 2 的当前连接。");
        }

        private CombatFactMessage Fact(uint kind, ulong actor, uint epoch, uint sequence, float hp) => new CombatFactMessage
        {
            ProtocolVersion = ProtocolCompatibility.CurrentVersion, GameplayHash = _definition.WireHash,
            Kind = kind, ActorId = actor, ConnectionEpoch = epoch, Sequence = sequence, ServerTick = sequence,
            CurrentHp = hp, MaxHp = 100f, Team = 1, MoveMultiplier = 1f,
            RotationW = 1f, PositionX = 32123f, PositionY = 19000f, PositionZ = 23123f
        };
        private WizardController PrepareRemoteWizard()
        {
            var slot = _registry.EnsurePlayer(1, PlayerExecutionMode.Replica);
            var wizard = slot.Player as WizardController;
            Assert.That(wizard, Is.Not.Null, "动作回归必须使用原生产 Wizard Prefab。");
            // 外层世界一直 inactive；夹具仅补齐真实 Awake 缓存，不启动 Update/Input 或另造动画控制器。
            typeof(WizardController).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(wizard, null);
            typeof(HealthComponent).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(slot.Health, null);
            Assert.That(wizard.Animator, Is.Not.Null);
            Assert.That(_definition.AnimationHash(false), Is.Not.Zero);
            return wizard;
        }
        private CombatFactMessage ActionFact(uint sequence)
        {
            var action = Fact(3, 2, 1, sequence, 0);
            action.AnimationHash = unchecked((uint)_definition.AnimationHash(false));
            action.ActionKind = 1; action.Amount = .1f;
            return action;
        }
        private void Receive(CombatFactMessage fact) => Install.Invoke(_combat, new object[] { fact });
        private void OnDamage(DamageReceivedEvent fact) { _damageEvents++; _lastDamageRemainingHp = fact.RemainingHp; }
        private void OnDeath(DeathEvent fact) => _deathEvents++;
    }
}
