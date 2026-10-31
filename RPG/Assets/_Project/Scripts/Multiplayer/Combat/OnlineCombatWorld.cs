using System;
using System.Collections.Generic;
using Game.Character;
using Game.Combat;
using Game.Core;
using Game.Networking;
using Game.NetworkProtocol;
using Mirror;
using UnityEngine;

namespace Game.Multiplayer
{
    /// <summary>权威事实与本地 Replica 的组合。消息只安装结果；所有 Gameplay 仍在 Character / Combat / Run。</summary>
    public sealed class OnlineCombatWorld : IDisposable
    {
        private sealed class Output
        {
            internal ulong Id, Actor, Command;
            internal uint Key, Ordinal, LastSequence;
            internal GameObject Root;
            internal Rigidbody Body;
            internal ProjectileBase Projectile;
            internal bool Terminated;
            internal Vector3 LastPosition, Velocity;
            internal Quaternion LastRotation;
            internal double ReceivedAt;
            internal ParticleSystem[] Particles;
            internal bool[] ResumeParticles;
        }
        private readonly OnlineWorldBootstrap _world;
        private readonly OnlineCombatDefinition _definition;
        private readonly CombatSimulationWorld _simulation;
        private readonly CombatNetworkChannel _channel;
        private readonly RoomNetworkManager _manager;
        private readonly RoomInstanceBootstrap _instance;
        private readonly MovementNetworkChannel _movementChannel;
        private readonly InventoryNetworkChannel _inventoryChannel;
        private readonly uint[] _inventoryEpochs = new uint[4], _inventoryVersions = new uint[4];
        private readonly ulong[] _inventorySequences = new ulong[4];
        private readonly string[] _inventorySelections = new string[4], _setupWands = new string[4];
        private readonly uint[] _setupRevisions = new uint[4];
        // Cast属于同Run的角色，可比一次Manager/CombatWorld活得更久。按真正订阅过的Driver记录所有权，
        // Dispose只移除本实例的委托，不从已换代的可变Registry猜测应当解绑谁。
        private readonly OnlineCastDriver[] _intentDrivers = new OnlineCastDriver[OnlineActorLimits.PlayerCount];
        private readonly OnlineCastDriver[] _receiptDrivers = new OnlineCastDriver[OnlineActorLimits.PlayerCount];
        private readonly CombatFactMessage _out = new CombatFactMessage();
        private readonly Output[] _outputs = new Output[1024];
        private readonly uint[] _bootstrappedEpoch = new uint[4], _actorSequence = new uint[OnlineActorLimits.MaximumActors];
        private readonly bool[] _deadPresented = new bool[OnlineActorLimits.MaximumActors];
        private readonly OnlineCastResourceSnapshot[] _resources = new OnlineCastResourceSnapshot[OnlineActorLimits.MaximumActors];
        private readonly Dictionary<int, ulong> _instanceActors = new Dictionary<int, ulong>(2048);
        private ulong _nextOutput;
        private uint _sequence, _lastReliable;
        private readonly SnapshotSendClock _snapshotClock = new SnapshotSendClock();
        private bool _disposed, _presentationFrozen;
        private readonly Animator[] _actorAnimators = new Animator[OnlineActorLimits.MaximumActors];
        private readonly float[] _actorAnimationSpeeds = new float[OnlineActorLimits.MaximumActors];
        public bool Faulted { get; private set; }
        public int ActiveOutputs { get; private set; }
        public long SpawnedOutputs { get; private set; }
        public long DamageFacts { get; private set; }
        public bool TryGetResources(ulong actor, out OnlineCastResourceSnapshot resources)
        {
            resources = default; if (actor < 1 || actor > OnlineActorLimits.MaximumActors) return false;
            var slot = _world.Registry[(int)actor - 1];
            if (_world.IsAuthority) return slot.Cast != null && slot.Cast.TryGetAuthorityResources(out resources);
            resources = _resources[(int)actor - 1]; return resources.MaxMana > 0;
        }
        public OnlineCombatWorld(OnlineWorldBootstrap world, OnlineCombatDefinition definition,
            CombatSimulationWorld simulation, CombatNetworkChannel channel, RoomNetworkManager manager, RoomInstanceBootstrap instance)
        {
            _world = world; _definition = definition; _simulation = simulation; _channel = channel; _manager = manager; _instance = instance;
            _movementChannel = manager.GetComponent<MovementNetworkChannel>();
            _inventoryChannel = manager.GetComponent<InventoryNetworkChannel>();
            if (_inventoryChannel == null) _inventoryChannel = manager.gameObject.AddComponent<InventoryNetworkChannel>();
            _inventoryChannel.GameplayConfigHash = definition.GameplayConfigHash;
            _inventoryChannel.GameplayHash = definition.WireHash;
            _inventoryChannel.CommandReceived += OnInventoryCommand;
            for (int i = 0; i < _outputs.Length; i++) _outputs[i] = new Output();
            _channel.GameplayHash = definition.WireHash; _channel.IntentReceived += OnIntent; _channel.FactReceived += OnFact;
            EventBus<CombatOutputSpawnedEvent>.Subscribe(OnBorn);
            EventBus<DamageReceivedEvent>.Subscribe(OnDamage); EventBus<DeathEvent>.Subscribe(OnDeath);
            EventBus<OnlineCharacterActionEvent>.Subscribe(OnAction);
            EventBus<OnlineBossTeleportPresentationEvent>.Subscribe(OnTeleport);
            EventBus<CombatCastSoundEvent>.Subscribe(OnCastSound);
        }
        public bool BindAuthority(NetworkActorRegistry.Slot slot, JoinConnectionIdentity identity)
        {
            if (_instance == null || _instance.Inventory == null || !_instance.Inventory.TryGetCombatAssets(identity.ActorId, out var source)) return false;
            // 显式边界保留 nullable 语义，Run 仍不引用网络程序集；分配只发生在离散入局。
            var assets = OnlineOwnedAssetMapping.ToCore(source);
            var cast = slot.Cast;
            if (cast == null) { cast = slot.Player.gameObject.AddComponent<OnlineCastDriver>(); slot.Cast = cast; }
            if (!cast.InitializeAuthority(assets, _definition, _simulation, out string error))
            { GameLog.Error("入局战斗资产被拒绝：" + error, "OnlineCombat"); return false; }
            cast.SetConnection(identity.ConnectionEpoch, true);
            BindReceiptDriver((int)identity.ActorId - 1, cast);
            RegisterActorHierarchy(slot, identity.ActorId);
            return true;
        }
        public void BindClient(NetworkActorRegistry.Slot slot, ulong actor, uint epoch, bool local)
        {
            if (slot.Cast == null)
            {
                slot.Cast = slot.Player.gameObject.AddComponent<OnlineCastDriver>();
            }
            slot.Cast.InitializeClient(actor, epoch, _definition, local);
            BindIntentDriver((int)actor - 1, local ? slot.Cast : null);
        }
        private void BindIntentDriver(int index, OnlineCastDriver driver)
        {
            var previous = _intentDrivers[index];
            if (ReferenceEquals(previous, driver)) return;
            if (previous != null) previous.IntentProduced -= OnLocalIntent;
            _intentDrivers[index] = driver;
            if (driver != null) driver.IntentProduced += OnLocalIntent;
        }
        private void BindReceiptDriver(int index, OnlineCastDriver driver)
        {
            var previous = _receiptDrivers[index];
            if (ReferenceEquals(previous, driver)) return;
            if (previous != null) previous.ReceiptProduced -= OnReceipt;
            _receiptDrivers[index] = driver;
            if (driver != null) driver.ReceiptProduced += OnReceipt;
        }
        public void ActorLeft(ulong actor, uint epoch)
        { _world.Registry[(int)actor - 1].Cast?.SetConnection(epoch, false); }

        /// <summary>
        /// 完整值可早于角色或UI就绪到达；通道先保存最新整份，只有当前本机Prediction Actor就绪后才取走。
        /// Run类型由Character包装，Multiplayer仍只使用Core值合同，不取得库存写权限。
        /// </summary>
        public void PumpClientInventory()
        {
            if (_disposed || _world.IsAuthority || _world.LocalPlayer == null ||
                !_inventoryChannel.TryTakeClientSnapshot(out var snapshot)) return;
            var auth = _manager.GetComponent<JoinTicketAuthenticator>();
            var identity = auth != null ? auth.ClientIdentity : null;
            if (!InventorySnapshotInbox.Matches(identity, snapshot, _definition.GameplayConfigHash)) return;
            var slot = _world.Registry[(int)identity.ActorId - 1];
            if (slot.Player != _world.LocalPlayer || slot.Cast == null || slot.Epoch != identity.ConnectionEpoch) return;
            var expected = new OnlineInventoryPresentationIdentity(identity.RunId, identity.PlayerId, identity.ActorId,
                identity.RunId + "/" + identity.PlayerId, _definition.GameplayConfigHash, identity.ConnectionEpoch,
                _world.gameObject.scene.path);
            if (!slot.Cast.InstallClientInventory(expected, snapshot, out var error))
                GameLog.Warn("当前线上库存快照被拒绝，保留上一份一致读数：" + error, "OnlineInventory");
        }

        private void PumpOwnerInventories()
        {
            for (int i = 0; i < 4; i++)
            {
                var slot = _world.Registry[i]; var connection = _manager.ActorConnection(i);
                var identity = connection != null ? connection.authenticationData as JoinConnectionIdentity : null;
                if (identity == null || slot.Cast == null || !slot.SimulationReady ||
                    !_movementChannel.IsActorReady(identity.ActorId, identity.ConnectionEpoch)) continue;
                uint version = slot.Cast.OwnedInventoryVersion;
                string selection = slot.Cast.SelectedOwnedWandId;
                if (version == 0 || _inventoryEpochs[i] == identity.ConnectionEpoch &&
                    _inventoryVersions[i] == version && _inventorySelections[i] == selection) continue;
                // 每帧仅比较基础值。只有入场/重连/资产或选择离散变化才构造完整值；Mana恢复不走此路径。
                ulong next = _inventorySequences[i] + 1;
                if (next == 0 || !slot.Cast.TryCaptureInventory(identity.ConnectionEpoch, next, _world.ServerTick, out var snapshot) ||
                    !_inventoryChannel.SendToOwner(identity, snapshot))
                {
                    // 已就绪的权威数据仍无法导出/编码是确定的组合错误；停止而非每帧分配失败候选并刷日志。
                    Faulted = true; GameLog.Error("权威库存无法发布，已停止该副本模拟以保留当前资产。", "OnlineInventory"); return;
                }
                _inventorySequences[i] = next; _inventoryEpochs[i] = identity.ConnectionEpoch;
                _inventoryVersions[i] = version; _inventorySelections[i] = selection;
            }
        }
        private void OnLocalIntent(OnlineCastIntent intent)
        {
            // 与EventBus相同，C#事件本次Invoke可能已捕获旧委托；退订后仍拒绝向上一代通道发送。
            if (!_disposed) _channel.SendIntent(intent);
        }
        private void OnInventoryCommand(JoinConnectionIdentity identity, OnlineInventoryCommandIntent intent,
            OnlineInventoryCommandFailure preflightFailure)
        {
            if (_disposed || !_world.IsAuthority || identity == null || identity.ActorId < 1 || identity.ActorId > 4) return;
            int index = (int)identity.ActorId - 1;
            var slot = _world.Registry[index];
            if (slot.Cast == null || slot.Epoch != identity.ConnectionEpoch || intent.ConnectionEpoch != identity.ConnectionEpoch) return;
            if (!_world.CanAdvance || Faulted || !slot.SimulationReady) preflightFailure = OnlineInventoryCommandFailure.NotReady;
            var receipt = slot.Cast.ExecuteInventoryCommand(intent, preflightFailure);
            _inventoryChannel.SendReceipt(identity, receipt);
            // 真正成功的写入由版本/选中实例变化触发发布；只为未限流的版本冲突补发，重放旧成功不放大。
            if (InventoryCommandDeliveryPolicy.ShouldResynchronize(preflightFailure, receipt.Failure))
                _inventoryVersions[index] = 0;
        }
        private void OnIntent(JoinConnectionIdentity identity, OnlineCastIntent intent)
        {
            if (!_world.CanAdvance || Faulted) return;
            var slot = _world.Registry[(int)identity.ActorId - 1];
            if (slot.Cast != null && slot.Targetable && slot.Epoch == identity.ConnectionEpoch) slot.Cast.TryQueue(intent, out _);
        }
        public void StepAuthority(float dt, uint tick)
        {
            AdvanceActorBodies(tick);
            for (int i = 0; i < 4; i++)
            {
                var slot = _world.Registry[i];
                // 失联/死亡仍交由 Driver 取消旧任务；新连接尚未就绪时不启动玩法时钟。
                if (slot.Connected && !slot.SimulationReady) continue;
                slot.Cast?.StepAuthority(dt, tick);
            }
        }
        public void StepPrediction(float dt, uint tick, bool suppressed)
        { if (_world.LocalPlayer != null) _world.LocalPlayer.GetComponent<OnlineCastDriver>()?.StepPrediction(dt, tick, suppressed); }
        private CombatFactMessage NewFact(uint kind, ulong actor)
        {
            _out.ResetForReuse(); _out.ProtocolVersion = ProtocolCompatibility.CurrentVersion; _out.GameplayHash = _definition.WireHash;
            _out.Sequence = ++_sequence; _out.ServerTick = Math.Max(1u, _world.ServerTick); _out.Kind = kind; _out.ActorId = actor;
            _out.ConnectionEpoch = Math.Max(1u, _world.Registry[(int)actor - 1].Epoch);
            return _out;
        }
        private void OnReceipt(OnlineCastReceipt receipt)
        {
            if (_disposed || !_world.IsAuthority) return;
            var fact = NewFact(2, receipt.ActorId); fact.CommandId = receipt.CommandId;
            fact.Result = (uint)receipt.Result; fact.Phase = (uint)receipt.Failure; fact.Flags = receipt.Airborne ? 4u : 0;
            fact.PositionX = receipt.AimPoint.x; fact.PositionY = receipt.AimPoint.y; fact.PositionZ = receipt.AimPoint.z;
            _channel.SendFact(fact);
        }
        private void OnAction(OnlineCharacterActionEvent action)
        {
            if (_disposed || !_world.IsAuthority || !_world.IsConfiguredActor(action.ActorId)) return;
            var fact = NewFact(3, action.ActorId);
            fact.AnimationHash = unchecked((uint)action.AnimationHash); fact.ActionKind = action.ActionKind; fact.Amount = action.FadeSeconds;
            _channel.SendFact(fact);
        }
        private void OnTeleport(OnlineBossTeleportPresentationEvent teleport)
        {
            if (_disposed || !_world.IsAuthority || !_world.IsConfiguredActor(teleport.ActorId) || _world.Registry[(int)teleport.ActorId - 1].Boss == null) return;
            var fact = NewFact(10, teleport.ActorId); fact.Phase = teleport.Stage;
            fact.PositionX = teleport.DestinationPosition.x; fact.PositionY = teleport.DestinationPosition.y; fact.PositionZ = teleport.DestinationPosition.z;
            fact.VelocityX = teleport.SourcePosition.x; fact.VelocityY = teleport.SourcePosition.y; fact.VelocityZ = teleport.SourcePosition.z;
            _channel.SendFact(fact);
        }
        private void OnCastSound(CombatCastSoundEvent sound)
        {
            if (_disposed || !_world.IsAuthority || !_world.IsConfiguredActor(sound.ActorId)) return;
            uint key = _definition.FindSoundKey(sound.Clip); if (key == 0) return;
            var fact = NewFact(11, sound.ActorId); fact.SoundKey = key; fact.CommandId = sound.CommandId; fact.OutputOrdinal = sound.SoundOrdinal;
            fact.PositionX = sound.Position.x; fact.PositionY = sound.Position.y; fact.PositionZ = sound.Position.z;
            _channel.SendFact(fact);
        }
        public void RegisterActorHierarchy(NetworkActorRegistry.Slot slot, ulong actor)
        {
            if (slot.Body == null) return;
            // 攻击者可能是武器子物体；离散出生时建立映射，热路径不扫描层级或分配数组。
            var hierarchy = slot.Body.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < hierarchy.Length; i++) _instanceActors[hierarchy[i].gameObject.GetInstanceID()] = actor;
        }
        private void OnBorn(CombatOutputSpawnedEvent born)
        {
            if (_disposed || !_world.IsAuthority || born.Output == null) return;
            uint key = _definition.FindOutputKey(born.DefinitionPrefab);
            int free = FreeSlot();
            if (key == 0 || free < 0 || _nextOutput == uint.MaxValue)
            {
                // 无法复制的 Gameplay 不能继续偷偷结算；冻结该世界并保留显式故障，不能丢消息后继续跑。
                Faulted = true; born.Output.SetActive(false); UnityEngine.Object.Destroy(born.Output);
                GameLog.Error("输出超出受控 Prefab 表或复制容量，权威世界已停止。", "OnlineCombat"); return;
            }
            var output = _outputs[free]; output.Id = ++_nextOutput; output.Actor = born.ActorId; output.Command = born.CommandId;
            output.Ordinal = (uint)output.Id; output.Key = key; output.Root = born.Output; output.Terminated = false;
            output.Body = born.Output.GetComponent<Rigidbody>(); output.Projectile = born.Output.GetComponent<ProjectileBase>();
            if (output.Projectile != null)
            {
                // 离散出生时绑定一次；每 Tick 不创建闭包。终止回调先于 Unity 帧尾销毁，保留真实点/原因。
                output.Projectile.Terminated += (reason, point, direction) => Terminate(output, reason, point);
            }
            ActiveOutputs++; SpawnedOutputs++; SendBirth(output);
        }
        private int FreeSlot()
        { for (int i = 0; i < _outputs.Length; i++) if (_outputs[i].Id == 0) return i; return -1; }
        private Output FindOutput(ulong id)
        { for (int i = 0; i < _outputs.Length; i++) if (_outputs[i].Id == id) return _outputs[i]; return null; }
        private void SendBirth(Output output, NetworkConnectionToClient target = null)
        {
            if (output.Root == null || output.Terminated) return;
            var fact = NewFact(4, output.Actor); FillOutput(output, fact); fact.PrefabKey = output.Key; fact.OutputOrdinal = output.Ordinal;
            _channel.SendFact(fact, target);
        }
        private static void FillOutput(Output output, CombatFactMessage fact)
        {
            fact.OutputId = output.Id; fact.CommandId = output.Command;
            var pose = output.Root.transform; var p = pose.position; var q = pose.rotation;
            var v = output.Body != null && !output.Body.isKinematic ? output.Body.linearVelocity : Vector3.zero;
            output.LastPosition = p;
            fact.PositionX = p.x; fact.PositionY = p.y; fact.PositionZ = p.z;
            fact.RotationX = q.x; fact.RotationY = q.y; fact.RotationZ = q.z; fact.RotationW = q.w;
            fact.VelocityX = v.x; fact.VelocityY = v.y; fact.VelocityZ = v.z;
        }
        private void Terminate(Output output, ProjectileTerminationReason reason, Vector3 point)
        {
            if (_disposed || output.Id == 0 || output.Terminated) return;
            output.Terminated = true;
            var fact = NewFact(6, output.Actor); fact.OutputId = output.Id; fact.CommandId = output.Command; fact.Result = (uint)reason + 1;
            fact.PositionX = point.x; fact.PositionY = point.y; fact.PositionZ = point.z; _channel.SendFact(fact);
        }
        public void Pump(double now)
        {
            if (_disposed || !_world.IsAuthority || !_world.CanAdvance) return;
            AdvanceActorBodies(_world.ServerTick);
            PumpOwnerInventories();
            if (Faulted) return;
            for (int i = 0; i < 4; i++)
            {
                var slot = _world.Registry[i]; var connection = _manager.ActorConnection(i);
                if (connection == null || slot.Cast == null || !slot.SimulationReady || !_movementChannel.IsActorReady((ulong)i + 1, slot.Epoch)) continue;
                if (!slot.Cast.TryGetAuthorityResources(out var resources)) continue;
                if (_bootstrappedEpoch[i] == slot.Epoch && _setupWands[i] == resources.WandInstanceId &&
                    _setupRevisions[i] == resources.ProgramRevision) continue;
                var setup = NewFact(1, (ulong)i + 1); setup.WandInstanceId = resources.WandInstanceId;
                setup.ProgramRevision = resources.ProgramRevision; setup.Amount = resources.CastInterval;
                setup.SoundKey = resources.PredictionSoundKey;
                _channel.SendFact(setup, connection);
                if (_bootstrappedEpoch[i] != slot.Epoch)
                    for (int o = 0; o < _outputs.Length; o++) if (_outputs[o].Id != 0) SendBirth(_outputs[o], connection);
                _bootstrappedEpoch[i] = slot.Epoch;
                _setupWands[i] = resources.WandInstanceId; _setupRevisions[i] = resources.ProgramRevision;
            }
            // Unity 帧尾销毁的纯表现对象也有显式终止；正常弹体终止由上面的真实事件先行记录。
            for (int i = 0; i < _outputs.Length; i++)
            {
                var output = _outputs[i]; if (output.Id == 0 || output.Root != null) continue;
                if (!output.Terminated) Terminate(output, ProjectileTerminationReason.Cancelled, output.LastPosition);
                output.Id = 0; output.Body = null; output.Projectile = null; ActiveOutputs--;
            }
            // 与移动共用同一发送时钟：沿已有 deadline 保留晚帧余数，避免 50FPS 时把 15Hz 降成 12.5Hz。
            // hitch 后只取得一次当前状态的发送资格；不补发没有保存过的历史状态，也不扩大每帧带宽预算。
            if (!_snapshotClock.TrySend(now, 1f / 15f)) return;
            for (int i = 0; i < _world.Registry.Capacity; i++)
            {
                var slot = _world.Registry[i]; if (slot.Body == null || slot.Health == null) continue;
                var fact = NewFact(7, (ulong)i + 1); fact.CurrentHp = slot.Health.CurrentHp; fact.MaxHp = slot.Health.MaxHp;
                fact.Team = slot.Health.TeamId; fact.Flags = slot.Health.IsAlive ? 2u : 0; fact.MoveMultiplier = 1;
                if (!slot.Health.IsAlive && slot.BodyLifetime != null)
                    fact.Amount = slot.BodyLifetime.RemainingSeconds(_world.ServerTick); // Kind7 dead：尸体剩余秒数，而非伤害。
                var status = slot.Body.GetComponent<StatusController>();
                if (status != null)
                {
                    fact.Burning = status.GetIntensity(StatusKind.Burning); fact.Wet = status.GetIntensity(StatusKind.Wet);
                    fact.Poisoned = status.GetIntensity(StatusKind.Poisoned); fact.Sticky = status.GetIntensity(StatusKind.Sticky);
                    fact.MoveMultiplier = status.MoveSpeedMultiplier;
                }
                if (slot.Cast != null && slot.Cast.TryGetAuthorityResources(out var resources))
                {
                    fact.CurrentMana = resources.CurrentMana; fact.MaxMana = resources.MaxMana; fact.Cooldown = resources.CooldownRemaining;
                    // 槽标识与资源同包：可靠Setup和非可靠ActorState没有跨通道先后保证，Revision也不是物品身份。
                    fact.WandSlotPlusOne = slot.Cast.SelectedOwnedWandSlotPlusOne;
                    fact.ProgramRevision = resources.ProgramRevision;
                    fact.RemainingCharges = (uint)Math.Max(0, resources.RemainingCharges); if (resources.Unlimited) fact.Flags |= 1;
                }
                // 0 表示尚未进入 encounter；不能仅因 Boss 实体出生就提前显示整屏 BossBar。
                if (slot.Boss != null) { fact.Phase = slot.Health.IsAlive && slot.Boss.IsEncounterActive ? (uint)slot.Boss.CurrentPhase : 0u; fact.TargetActorId = slot.Boss.TargetActorId; }
                _channel.SendFact(fact, null, false);
            }
            for (int i = 0; i < _outputs.Length; i++)
            {
                var output = _outputs[i]; if (output.Id == 0 || output.Root == null || output.Terminated) continue;
                var fact = NewFact(5, output.Actor); FillOutput(output, fact); _channel.SendFact(fact, null, false);
            }
        }
        private ulong ActorForInstance(int instanceId)
        {
            if (_instanceActors.TryGetValue(instanceId, out ulong actor)) return actor;
            for (int i = 0; i < _world.Registry.Capacity; i++)
            { var slot = _world.Registry[i]; if (slot.Body != null && slot.Body.gameObject.GetInstanceID() == instanceId) return (ulong)i + 1; }
            return 0;
        }
        private void OnDamage(DamageReceivedEvent damage)
        {
            if (_disposed || !_world.IsAuthority) return;
            ulong target = ActorForInstance(damage.TargetId); if (target == 0) return;
            DamageFacts++;
            var fact = NewFact(8, target); fact.TargetActorId = target; fact.Amount = damage.Amount;
            fact.AttackerActorId = ActorForInstance(damage.AttackerId); fact.DamageType = (uint)damage.Type;
            fact.CurrentHp = damage.RemainingHp; fact.MaxHp = _world.Registry[(int)target - 1].Health.MaxHp;
            fact.PositionX = damage.HitPoint.x; fact.PositionY = damage.HitPoint.y; fact.PositionZ = damage.HitPoint.z;
            fact.VelocityX = damage.HitDirection.x; fact.VelocityY = damage.HitDirection.y; fact.VelocityZ = damage.HitDirection.z;
            fact.Flags = damage.TriggerHitReaction ? 2u : 0; _channel.SendFact(fact);
            // 远程首击也代表主动参战，不能站在触发半径外把未激活 Boss 当木桩。
            // 先发送 Damage 再激活，避免激活事件复用同一消息缓存覆盖尚未发送的事实。
            ulong attacker = ActorForInstance(damage.AttackerId);
            // 正式P8保留“先进入再离开安全区”的单一启动门，不让旧灰盒的Actor7远程首击分支绕过它。
            if (_world.DungeonId == null && attacker >= 1 && attacker <= 4 && _world.Registry[(int)attacker - 1].Targetable)
                _world.Registry[(int)target - 1].Boss?.TryActivateAuthorityEncounter();
        }
        private void OnDeath(DeathEvent death)
        {
            if (_disposed || !_world.IsAuthority) return;
            ulong actor = ActorForInstance(death.TargetId); if (actor == 0) return;
            var slot = _world.Registry[(int)actor - 1];
            slot.BodyLifetime?.MarkDeath(_world.ServerTick, slot.BodyLifetime.PrefabDelay);
            var fact = NewFact(9, actor); fact.MaxHp = slot.Health.MaxHp;
            fact.Amount = slot.BodyLifetime != null ? slot.BodyLifetime.RemainingSeconds(_world.ServerTick) : 0f;
            _channel.SendFact(fact);
        }
        private NetworkActorRegistry.Slot EnsureActor(ulong actor)
        {
            if (!_world.IsConfiguredActor(actor)) return null;
            if (actor >= 5) return _world.Registry.EnsureEnemy(actor, false);
            var slot = _world.Registry[(int)actor - 1];
            if (slot.Player == null) _world.Registry.EnsurePlayer((int)actor - 1, PlayerExecutionMode.Replica);
            return slot;
        }
        private void OnFact(CombatFactMessage fact)
        {
            if (_disposed || _world.IsAuthority || !_world.IsConfiguredActor(fact.ActorId) ||
                fact.AttackerActorId != 0 && !_world.IsConfiguredActor(fact.AttackerActorId)) return;
            bool snapshot = fact.Kind == 5 || fact.Kind == 7;
            if (!snapshot)
            {
                if (_lastReliable != 0 && unchecked((int)(fact.Sequence - _lastReliable)) <= 0) return;
                _lastReliable = fact.Sequence;
            }
            var slot = EnsureActor(fact.ActorId);
            if (fact.ConnectionEpoch < slot.Epoch) return;
            if (slot.Player != null && (slot.Cast == null || fact.ConnectionEpoch > slot.Epoch))
                BindClient(slot, fact.ActorId, fact.ConnectionEpoch, slot.Player == _world.LocalPlayer);
            slot.Epoch = fact.ConnectionEpoch;
            if (fact.Kind == 11)
            {
                var position = new Vector3(fact.PositionX, fact.PositionY, fact.PositionZ);
                if (slot.Player != null && slot.Player == _world.LocalPlayer && fact.OutputOrdinal == 1)
                    slot.Cast?.PlayConfirmedCastSound(fact.CommandId, fact.SoundKey, position);
                else
                {
                    var clip = _definition.SoundClip(fact.SoundKey);
                    if (clip != null) AudioSource.PlayClipAtPoint(clip, position);
                }
                return;
            }
            if (fact.Kind == 10)
            {
                if (slot.Boss != null) slot.Boss.ApplyReplicatedTeleport(fact.Phase,
                    new Vector3(fact.VelocityX, fact.VelocityY, fact.VelocityZ), new Vector3(fact.PositionX, fact.PositionY, fact.PositionZ), fact.ServerTick);
                return;
            }
            if (fact.Kind == 1)
            {
                if (slot.Cast != null) slot.Cast.InstallClientSetup(fact.WandInstanceId, fact.ProgramRevision, fact.Amount, fact.SoundKey);
                return;
            }
            if (fact.Kind == 2)
            {
                slot.Cast?.ApplyReceipt(new OnlineCastReceipt { ActorId = fact.ActorId, ConnectionEpoch = fact.ConnectionEpoch, CommandId = fact.CommandId,
                    Result = (OnlineCastResult)fact.Result, Failure = (OnlineCastFailure)fact.Phase });
                if (slot.Player != null && slot.Player == _world.LocalPlayer && fact.Result == (uint)OnlineCastResult.Rejected)
                    EventBus<OnlineCastFeedbackEvent>.Publish(new OnlineCastFeedbackEvent { Failure = (OnlineCastFailure)fact.Phase });
                return;
            }
            if (fact.Kind == 3)
            {
                // 不可靠完整状态可能先确认死亡，随后旧可靠动作才到达。此时死亡已拥有 Animator，
                // 不能让旧 CrossFade 覆盖它；之后的可靠 Death 又会被去重，无法替我们恢复死亡动画。
                // 这里只封住已死亡角色，活人的 HP 状态较新并不代表旧动作已播放，仍保留可靠动作资格。
                if (slot.Health != null && !slot.Health.IsAlive) return;
                // 本机已经预测过同命令动画；远端只消费服务器接受事实。
                int hash = unchecked((int)fact.AnimationHash);
                if (slot.Enemy != null) slot.Enemy.ApplyReplicaAction(hash, fact.ActionKind, fact.Amount);
                else if (slot.Boss != null) slot.Boss.ApplyReplicaAction(hash, fact.ActionKind, fact.Amount);
                else if (slot.Player != _world.LocalPlayer) slot.Cast?.ApplyReplicaAction(hash, fact.ActionKind, fact.Amount);
                return;
            }
            if (fact.Kind == 4) { CreateReplica(fact); return; }
            if (fact.Kind == 5)
            {
                var output = FindOutput(fact.OutputId);
                if (output == null || output.Terminated || output.LastSequence != 0 && unchecked((int)(fact.Sequence - output.LastSequence)) <= 0) return;
                output.LastSequence = fact.Sequence; InstallPose(output, fact); return;
            }
            if (fact.Kind == 6)
            {
                var output = FindOutput(fact.OutputId); if (output == null) return;
                if (output.Root != null) UnityEngine.Object.Destroy(output.Root);
                output.Id = 0; output.Root = null; output.Terminated = true; ActiveOutputs--; return;
            }
            int index = (int)fact.ActorId - 1;
            if (fact.Kind == 7)
            {
                if (_actorSequence[index] != 0 && unchecked((int)(fact.Sequence - _actorSequence[index])) <= 0) return;
                _actorSequence[index] = fact.Sequence;
                slot.Health?.ApplyReplicatedState(new HealthReplicaSnapshot { CurrentHp = fact.CurrentHp, MaxHp = fact.MaxHp, TeamId = (byte)fact.Team });
                var mana = slot.Body.GetComponent<ManaComponent>(); if (mana != null && fact.MaxMana > 0) mana.ApplyReplicatedState(new ManaReplicaSnapshot { CurrentMana = fact.CurrentMana, MaxMana = fact.MaxMana });
                var status = slot.Body.GetComponent<StatusController>();
                if (status != null) status.ApplyReplicatedState(new StatusReplicaSnapshot { Burning = fact.Burning, Wet = fact.Wet,
                    Poisoned = fact.Poisoned, Sticky = fact.Sticky, MoveSpeedMultiplier = fact.MoveMultiplier });
                slot.Cast?.InstallClientCooldown(fact.Cooldown);
                slot.Cast?.InstallClientInventoryResources(fact.ConnectionEpoch, fact.ServerTick, fact.WandSlotPlusOne, fact.ProgramRevision, fact.CurrentMana, fact.Cooldown);
                _resources[index] = new OnlineCastResourceSnapshot { CurrentMana = fact.CurrentMana, MaxMana = fact.MaxMana,
                    CooldownRemaining = fact.Cooldown, RemainingCharges = (int)fact.RemainingCharges, Unlimited = (fact.Flags & 1) != 0 };
                if (slot.Boss != null) slot.Boss.ApplyReplicatedPhase(fact.Phase);
                EventBus<OnlineCombatHudStateEvent>.Publish(new OnlineCombatHudStateEvent
                {
                    ActorId = fact.ActorId, IsLocal = slot.Player != null && slot.Player == _world.LocalPlayer, IsBoss = slot.Boss != null,
                    CurrentHp = fact.CurrentHp, MaxHp = fact.MaxHp, CurrentMana = fact.CurrentMana, MaxMana = fact.MaxMana,
                    Cooldown = fact.Cooldown, Charges = (int)fact.RemainingCharges, Unlimited = (fact.Flags & 1) != 0, BossPhase = fact.Phase
                });
                if (fact.CurrentHp <= 0) PresentDeath(slot, index, fact.ServerTick, fact.Amount);
                return;
            }
            if (fact.Kind == 8 && slot.Health != null)
            {
                DamageFacts++;
                // 可靠伤害与不可靠快照没有总顺序；旧 HP 不能覆盖已安装的新状态。
                if (_actorSequence[index] == 0 || unchecked((int)(fact.Sequence - _actorSequence[index])) > 0)
                {
                    _actorSequence[index] = fact.Sequence;
                    slot.Health.ApplyReplicatedState(new HealthReplicaSnapshot
                    { CurrentHp = fact.CurrentHp, MaxHp = fact.MaxHp, TeamId = slot.Health.TeamId });
                }
                var source = fact.AttackerActorId > 0 ? EnsureActor(fact.AttackerActorId) : null;
                EventBus<DamageReceivedEvent>.Publish(new DamageReceivedEvent { TargetId = slot.Body.gameObject.GetInstanceID(),
                    AttackerId = source != null && source.Body != null ? source.Body.gameObject.GetInstanceID() : 0, Type = (DamageType)fact.DamageType,
                    Amount = fact.Amount, RemainingHp = slot.Health.CurrentHp, TriggerHitReaction = (fact.Flags & 2) != 0,
                    HitPoint = new Vector3(fact.PositionX, fact.PositionY, fact.PositionZ), HitDirection = new Vector3(fact.VelocityX, fact.VelocityY, fact.VelocityZ) });
                // 当前读模型死亡与“这次伤害致死”是两件事。较新的死亡状态先到时，
                // 旧非致死 Damage 仍可呈现反馈，但其 ServerTick 不能被误用为死亡起点、提前缩短尸体期限。
                if (fact.CurrentHp <= 0f && !slot.Health.IsAlive)
                    PresentDeath(slot, index, fact.ServerTick, slot.BodyLifetime != null ? slot.BodyLifetime.PrefabDelay : 0f);
            }
            if (fact.Kind == 9)
            {
                if (_actorSequence[index] == 0 || unchecked((int)(fact.Sequence - _actorSequence[index])) > 0)
                {
                    _actorSequence[index] = fact.Sequence;
                    slot.Health?.ApplyReplicatedState(new HealthReplicaSnapshot
                    { CurrentHp = 0, MaxHp = fact.MaxHp, TeamId = slot.Health.TeamId });
                }
                PresentDeath(slot, index, fact.ServerTick, fact.Amount);
            }
        }
        private void PresentDeath(NetworkActorRegistry.Slot slot, int index, uint deathTick, float remainingSeconds)
        {
            if (!_deadPresented[index] && slot.Body != null)
            {
                _deadPresented[index] = true;
                EventBus<DeathEvent>.Publish(new DeathEvent { TargetId = slot.Body.gameObject.GetInstanceID(), Position = slot.Body.position });
            }
            // 首次死亡表现可能创建子特效；在事件后缓存，重复事实仍只校正 deadline，不重播或重扫。
            slot.BodyLifetime?.MarkDeath(deathTick, remainingSeconds);
        }
        private void CreateReplica(CombatFactMessage fact)
        {
            if (FindOutput(fact.OutputId) != null) return;
            var prefab = _definition.OutputPrefab(fact.PrefabKey); int free = FreeSlot(); if (prefab == null || free < 0) return;
            var output = _outputs[free]; output.Id = fact.OutputId; output.Actor = fact.ActorId; output.Command = fact.CommandId;
            output.Key = fact.PrefabKey; output.Terminated = false; output.LastSequence = fact.Sequence;
            var go = UnityEngine.Object.Instantiate(prefab, new Vector3(fact.PositionX, fact.PositionY, fact.PositionZ),
                new Quaternion(fact.RotationX, fact.RotationY, fact.RotationZ, fact.RotationW), _world.transform);
            _simulation.ConfigureHierarchy(go, CombatExecutionMode.Replica);
            foreach (var collider in go.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
            foreach (var body in go.GetComponentsInChildren<Rigidbody>(true)) body.isKinematic = true;
            output.Root = go; output.Body = go.GetComponent<Rigidbody>(); output.Projectile = go.GetComponent<ProjectileBase>();
            output.Particles = go.GetComponentsInChildren<ParticleSystem>(true);
            output.ResumeParticles = new bool[output.Particles.Length];
            if (_presentationFrozen) FreezeParticles(output, true);
            InstallPose(output, fact); ActiveOutputs++; SpawnedOutputs++;
        }
        private static void InstallPose(Output output, CombatFactMessage fact)
        {
            output.LastPosition = new Vector3(fact.PositionX, fact.PositionY, fact.PositionZ);
            output.LastRotation = new Quaternion(fact.RotationX, fact.RotationY, fact.RotationZ, fact.RotationW);
            output.Velocity = new Vector3(fact.VelocityX, fact.VelocityY, fact.VelocityZ); output.ReceivedAt = Time.realtimeSinceStartupAsDouble;
            if (output.Root != null) output.Root.transform.SetPositionAndRotation(output.LastPosition, output.LastRotation);
        }
        public void Present(double now)
        {
            if (_disposed || _world.IsAuthority) return;
            AdvanceActorBodies(_world.ServerTick);
            if (_presentationFrozen != _world.Frozen)
            {
                _presentationFrozen = _world.Frozen;
                // 冻结是离散边沿。保留每个粒子的播放资格，恢复时不重启本来已结束的爆炸。
                for (int i = 0; i < _outputs.Length; i++) if (_outputs[i].Id != 0) FreezeParticles(_outputs[i], _presentationFrozen);
                for (int i = 0; i < _world.Registry.Capacity; i++)
                {
                    if (_presentationFrozen)
                    {
                        var slot = _world.Registry[i];
                        _actorAnimators[i] = slot.Body != null ? slot.Body.GetComponentInChildren<Animator>(true) : null;
                        if (_actorAnimators[i] != null) { _actorAnimationSpeeds[i] = _actorAnimators[i].speed; _actorAnimators[i].speed = 0; }
                    }
                    else if (_actorAnimators[i] != null) _actorAnimators[i].speed = _actorAnimationSpeeds[i];
                }
            }
            if (_presentationFrozen) return;
            for (int i = 0; i < _outputs.Length; i++)
            {
                var output = _outputs[i]; if (output.Id == 0 || output.Root == null) continue;
                float dt = Mathf.Clamp((float)(now - output.ReceivedAt), 0, .1f);
                output.Root.transform.SetPositionAndRotation(output.LastPosition + output.Velocity * dt, output.LastRotation);
            }
        }
        private static void FreezeParticles(Output output, bool frozen)
        {
            if (output.Particles == null) return;
            for (int i = 0; i < output.Particles.Length; i++)
            {
                var particle = output.Particles[i]; if (particle == null) continue;
                if (frozen) { output.ResumeParticles[i] = particle.isPlaying; if (particle.isPlaying) particle.Pause(false); }
                else if (output.ResumeParticles[i]) particle.Play(false);
            }
        }
        private void AdvanceActorBodies(uint serverTick)
        {
            // ServerTick 在 Frozen 期间不变；即使表现帧仍运行，尸体也不会使用真实时间提前消失。
            for (int i = 0; i < _world.Registry.Capacity; i++)
                _world.Registry[i].BodyLifetime?.Step(serverTick);
        }
        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            // Animator属于保留的Scene角色，冻结速度只是本CombatOwner暂时接管的表现状态。
            // 换Manager会创建默认未冻结的新Owner；若旧Owner不交还速度，新实例没有true→false边沿可恢复。
            // 仅在当前仍持有冻结权限时恢复自己缓存的原值，不能在已恢复后覆盖角色后来设置的新速度。
            if (_presentationFrozen)
                for (int i = 0; i < _actorAnimators.Length; i++)
                    if (_actorAnimators[i] != null) _actorAnimators[i].speed = _actorAnimationSpeeds[i];
            _presentationFrozen = false;
            Array.Clear(_actorAnimators, 0, _actorAnimators.Length);
            Array.Clear(_actorAnimationSpeeds, 0, _actorAnimationSpeeds.Length);
            for (int i = 0; i < _intentDrivers.Length; i++)
            { BindIntentDriver(i, null); BindReceiptDriver(i, null); }
            _channel.IntentReceived -= OnIntent; _channel.FactReceived -= OnFact;
            _inventoryChannel.CommandReceived -= OnInventoryCommand;
            EventBus<CombatOutputSpawnedEvent>.Unsubscribe(OnBorn); EventBus<DamageReceivedEvent>.Unsubscribe(OnDamage); EventBus<DeathEvent>.Unsubscribe(OnDeath);
            EventBus<OnlineCharacterActionEvent>.Unsubscribe(OnAction);
            EventBus<OnlineBossTeleportPresentationEvent>.Unsubscribe(OnTeleport);
            EventBus<CombatCastSoundEvent>.Unsubscribe(OnCastSound);
            for (int i = 0; i < _outputs.Length; i++) if (_outputs[i].Root != null) UnityEngine.Object.Destroy(_outputs[i].Root);
        }
    }
}
