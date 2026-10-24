using System;
using Game.Combat;
using Game.Core;
using Game.Run;
using Game.Skills;
using UnityEngine;

namespace Game.Character
{
    /// <summary>
    /// 在线 Wizard 的动作/Owned 装配。移动仍由原 Simulate 推进；动作作为独立 Overlay，
    /// 因而 Prediction 重放移动不会再发包、扣资源、创建投射物或重播音效。
    /// </summary>
    [DefaultExecutionOrder(25)]
    public sealed class OnlineCastDriver : MonoBehaviour
    {
        private WizardController _wizard;
        private SpellCaster _caster;
        private OwnedWandCastAdapter _adapter;
        private OnlineCombatDefinition _definition;
        private OnlineOwnedWorkspace _workspace;
        private CombatSimulationWorld _world;
        private readonly AuthorityActionTimeline _timeline = new AuthorityActionTimeline();
        private readonly OnlineCastReceipt[] _receipts = new OnlineCastReceipt[128];
        private int _receiptCursor;
        private ulong _actor, _lastCommand;
        private uint _epoch, _lastTick, _clientSequence;
        private bool _authority, _connected, _hasTick, _queued, _activeAir, _clientOwner, _waitRelease, _suppressed;
        private float _queueRemaining, _clientCooldown, _castInterval = .8f;
        private OnlineCastIntent _pending, _active;
        private WandProgramSnapshot _activeProgram;
        private OwnedWandCastContext _activeSource;
        private string _clientWand;
        private uint _clientProgramRevision;
        private uint _predictionSoundKey;
        private readonly ulong[] _soundCommands = new ulong[64];
        private readonly uint[] _soundKeys = new uint[64];
        private Vector3 _clientPendingAim;
        private Transform _presentationVisual;
        private Quaternion _baseVisualLocalRotation;
        private bool _ownsVisualFacing;
        public bool IsAuthority => _authority;
        public bool Ready => _definition != null && _connected && (_authority ? _workspace != null : _clientOwner && _clientProgramRevision != 0);
        public ulong ActorId => _actor;
        public event Action<OnlineCastIntent> IntentProduced;
        public event Action<OnlineCastReceipt> ReceiptProduced;

        private void Cache()
        {
            if (_wizard == null) _wizard = GetComponent<WizardController>();
            if (_caster == null) _caster = GetComponent<SpellCaster>();
            if (_presentationVisual == null && _wizard != null && _wizard.Animator != null && _wizard.Animator.transform != transform)
            {
                _presentationVisual = _wizard.Animator.transform;
                _baseVisualLocalRotation = _presentationVisual.localRotation;
            }
            if (_adapter == null)
            {
                _adapter = GetComponent<OwnedWandCastAdapter>();
                if (_adapter == null) _adapter = gameObject.AddComponent<OwnedWandCastAdapter>();
            }
        }
        /// <summary>独立于 Gameplay Tick 的输入门。Frozen 停 Tick 时仍必须立即清掉 UI/失焦期间的点击。</summary>
        public void SetBlocked(bool blocked)
        {
            if (_authority || _suppressed == blocked) return;
            _suppressed = blocked;
            if (blocked) { CancelPending(); _waitRelease = true; }
        }
        public bool InitializeAuthority(OnlineOwnedAssetSnapshot assets, OnlineCombatDefinition definition,
            CombatSimulationWorld world, out string error)
        {
            error = null; Cache();
            if (assets == null || definition == null || world == null || _wizard == null || _caster == null)
            { error = "权威角色缺少资产或正式配置。"; return false; }
            if (_workspace != null)
            {
                // 连接代次变化不等于新 Run；重连续用原 Mana、Draw、冷却和次数。
                bool same = _workspace.RunId == assets.RunId && _workspace.PlayerId == assets.PlayerId && _workspace.ActorId == assets.ActorId;
                if (!same) error = "不能把存活的角色工作区替换为另一个入局身份。";
                return same;
            }
            if (!definition.Catalog.TryCreateIndex(out var catalog, out error)
                || !OnlineOwnedWorkspaceFactory.TryCreate(catalog, assets, definition.GameplayConfigHash, out var workspace, out error)) return false;
            _workspace = workspace; _definition = definition; _world = world; _actor = assets.ActorId; _authority = true;
            try
            {
                _world.ConfigureHierarchy(gameObject, CombatExecutionMode.Authority);
                _adapter.BindWorkspace(workspace); _caster.ConfigureAuthority(world, _actor);
                return true;
            }
            catch
            {
                _workspace.Dispose(); _workspace = null; _authority = false; throw;
            }
        }
        public void InitializeClient(ulong actorId, uint epoch, OnlineCombatDefinition definition, bool localOwner)
        {
            Cache(); _actor = actorId; _definition = definition; _clientOwner = localOwner; _authority = false;
            _caster.ConfigureReplica(); SetConnection(epoch, true);
        }
        public void SetConnection(uint epoch, bool connected)
        {
            if (epoch < _epoch) return;
            bool changed = epoch != _epoch || _connected != connected;
            if (!changed) return;
            CancelPending(); _epoch = epoch; _connected = connected; _lastCommand = 0; _clientSequence = 0;
            Array.Clear(_receipts, 0, _receipts.Length); _receiptCursor = 0; _waitRelease = true;
            Array.Clear(_soundCommands, 0, _soundCommands.Length); Array.Clear(_soundKeys, 0, _soundKeys.Length);
        }
        public void InstallClientSetup(string wandId, uint programRevision, float castInterval, uint predictionSoundKey = 0)
        {
            if (_authority || !_clientOwner) return;
            _clientWand = wandId; _clientProgramRevision = programRevision;
            _predictionSoundKey = predictionSoundKey;
            _castInterval = Mathf.Max(.05f, castInterval); _waitRelease = true;
            EventBus<CrosshairVisibilityEvent>.Publish(new CrosshairVisibilityEvent { Visible = true });
        }
        public void InstallClientCooldown(float cooldown)
        { if (!_authority && float.IsFinite(cooldown)) _clientCooldown = Mathf.Max(0, cooldown); }
        public void CaptureAttackPress()
        {
            if (!Ready || _authority || _suppressed || !_clientOwner || Time.timeScale <= 0) return;
            _clientPendingAim = _wizard.CaptureOnlineCastAim();
            _queued = true; _queueRemaining = _clientCooldown + _wizard.AttackBufferTime + .05f;
            _waitRelease = false;
        }
        public void StepPrediction(float deltaTime, uint tick, bool suppressed)
        {
            if (_authority || !_clientOwner || !Ready) return;
            SetBlocked(suppressed);
            bool held = _wizard.IsOnlineAttackHeld;
            if (_suppressed) return;
            if (!held) _waitRelease = false;
            _clientCooldown = Mathf.Max(0, _clientCooldown - deltaTime);
            if (_timeline.IsActive && _timeline.Step(deltaTime, tick))
            {
                _clientCooldown = _castInterval; _wizard.SetOnlineActionPresentation(false);
                PlayConfirmedCastSound(_active.CommandId, _predictionSoundKey, transform.position);
            }
            if (_queued) { _queueRemaining -= deltaTime; if (_queueRemaining <= 0) _queued = false; }
            if (_timeline.IsActive || _clientCooldown > 0 || _waitRelease || !_queued && !held
                || _wizard.CaptureMovementSnapshot().Locomotion == PlayerLocomotionKind.Dash) return;
            if (!_queued) _clientPendingAim = _wizard.CaptureOnlineCastAim();
            _queued = false;
            if (_clientSequence == uint.MaxValue) return;
            _active = new OnlineCastIntent { CommandId = ((ulong)_epoch << 32) | ++_clientSequence,
                ConnectionEpoch = _epoch, WandInstanceId = _clientWand, ProgramRevision = _clientProgramRevision,
                ClaimedOrigin = transform.position, AimPoint = _clientPendingAim };
            _activeAir = !_wizard.GroundChecker.IsGrounded;
            BeginTimeline(_active.CommandId, _activeAir); PresentAction(_activeAir);
            IntentProduced?.Invoke(_active);
        }

        public bool TryQueue(in OnlineCastIntent intent, out OnlineCastReceipt receipt)
        {
            for (int i = 0; i < _receipts.Length; i++)
                if (_receipts[i].CommandId == intent.CommandId && intent.CommandId != 0 && _receipts[i].ConnectionEpoch == intent.ConnectionEpoch)
                { receipt = _receipts[i]; ReceiptProduced?.Invoke(receipt); return receipt.Result != OnlineCastResult.Rejected; }
            var failure = Validate(intent);
            receipt = NewReceipt(intent, failure == OnlineCastFailure.None ? OnlineCastResult.Queued : OnlineCastResult.Rejected, failure);
            if (intent.CommandId > _lastCommand) _lastCommand = intent.CommandId;
            if (failure == OnlineCastFailure.None)
            {
                if (_queued) Store(NewReceipt(_pending, OnlineCastResult.Rejected, OnlineCastFailure.Interrupted));
                _pending = intent; _queued = true;
                TryGetAuthorityResources(out var resources); _queueRemaining = resources.CooldownRemaining + _wizard.AttackBufferTime + .1f;
            }
            Store(receipt); return failure == OnlineCastFailure.None;
        }
        private OnlineCastFailure Validate(in OnlineCastIntent intent)
        {
            if (!_authority || !Ready || _wizard.Health == null || !_wizard.Health.IsAlive) return OnlineCastFailure.NotReady;
            if (intent.CommandId == 0 || intent.ConnectionEpoch != _epoch || intent.CommandId <= _lastCommand) return OnlineCastFailure.StaleCommand;
            if (!OnlineCastIntentRules.ValidateAim(transform.position, intent.ClaimedOrigin, intent.AimPoint, 3, 120)) return OnlineCastFailure.InvalidInput;
            var read = _workspace.WandsReadOnly;
            if (intent.WandInstanceId != read.SelectedWandId || !read.TryGetState(intent.WandInstanceId, out var state) || !state.Carried) return OnlineCastFailure.NotOwned;
            if (!read.TryGetProgram(intent.WandInstanceId, out var program) || program.ProgramRevision != intent.ProgramRevision) return OnlineCastFailure.StaleProgram;
            // 当前入局 Schema 只导入装备与卡，不承载材料 Attachment / 容器组分；因此不能从客户端补传材料预算。
            // 材料并非 SpellDefinition 的数量字段，真实供应来自 Run 物品附件，N5 才扩展那份受控快照。
            return OnlineCastFailure.None;
        }
        public void StepAuthority(float deltaTime, uint tick)
        {
            if (!_authority || _workspace == null || !float.IsFinite(deltaTime) || deltaTime <= 0
                || _hasTick && unchecked((int)(tick - _lastTick)) <= 0) return;
            _hasTick = true; _lastTick = tick;
            // 上一 Physics Tick 的致死事件必须先取消旧 deadline；否则到期预警会在检查死亡前补打一发。
            if (!_connected || _wizard.Health == null || !_wizard.Health.IsAlive) { CancelPending(); return; }
            _workspace.StepAuthority(deltaTime, tick);
            _caster.StepAuthority(deltaTime, tick);
            if (_queued)
            {
                _queueRemaining -= deltaTime;
                if (_queueRemaining <= 0) { Store(NewReceipt(_pending, OnlineCastResult.Rejected, OnlineCastFailure.Interrupted)); _queued = false; }
            }
            if (!_timeline.IsActive && _queued && _wizard.CaptureMovementSnapshot().Locomotion != PlayerLocomotionKind.Dash
                && TryGetAuthorityResources(out var resources) && resources.CooldownRemaining <= 0)
            {
                _active = _pending; _queued = false;
                if (!_adapter.TryCapture(out _activeProgram, out _activeSource)) { Store(NewReceipt(_active, OnlineCastResult.Rejected, OnlineCastFailure.NotOwned)); return; }
                _activeAir = !_wizard.GroundChecker.IsGrounded; BeginTimeline(_active.CommandId, _activeAir);
            }
            if (!_timeline.IsActive) return;
            Vector3 facing = _active.AimPoint - transform.position; facing.y = 0;
            if (facing.sqrMagnitude > .001f) transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(facing), Mathf.Min(1, deltaTime * 15));
            if (!_timeline.Step(deltaTime, tick)) return;
            _caster.SetAuthorityCommand(_active.CommandId);
            Vector3 spawn = transform.TransformPoint(_definition.SpawnOffset(_activeAir));
            int emitted = _caster.CastOwnedProgram(_activeProgram, _activeSource, spawn, _active.AimPoint,
                _wizard.Health.TeamId, gameObject.GetInstanceID(), _wizard.CharacterController);
            Store(NewReceipt(_active, emitted > 0 ? OnlineCastResult.Released : OnlineCastResult.Rejected,
                emitted > 0 ? OnlineCastFailure.None : _adapter.LastFailure == OwnedWandFailureReason.None ? OnlineCastFailure.EmptyOutput : OnlineCastFailure.ResourceRejected));
        }
        private void BeginTimeline(ulong command, bool air)
        {
            float release = _definition.ReleaseSeconds(air);
            _timeline.TryStart(command, release, release); // 单机也是释放后立刻交回 locomotion，不锁整段 Clip。
            if (_authority) EventBus<OnlineCharacterActionEvent>.Publish(new OnlineCharacterActionEvent
            { ActorId = _actor, AnimationHash = _definition.AnimationHash(air), ActionKind = 1, FadeSeconds = .1f });
        }
        public void PresentAction(bool airborne)
        {
            if (_authority || _wizard == null || _wizard.Animator == null) return;
            _wizard.SetOnlineActionPresentation(true);
            _wizard.Animator.CrossFadeInFixedTime(_definition.AnimationHash(airborne), .1f, 0, 0);
        }
        /// <summary>远端只播放已批准动作；本机 Command Prediction 由网络组合层去重。</summary>
        public void ApplyReplicaAction(int animationHash, uint kind, float fadeSeconds)
        {
            if (_authority || _wizard == null || _wizard.Animator == null || animationHash == 0) return;
            if (kind == 1)
            {
                _wizard.SetOnlineActionPresentation(true);
                _wizard.Animator.CrossFadeInFixedTime(animationHash, Mathf.Clamp(fadeSeconds, 0, 1), 0, 0);
            }
            else if (kind == 2) _wizard.Animator.SetTrigger(animationHash);
        }
        public void ApplyReceipt(in OnlineCastReceipt receipt)
        {
            if (_authority || receipt.ConnectionEpoch != _epoch) return;
            if (receipt.Result == OnlineCastResult.Rejected && (!_clientOwner || _active.CommandId == receipt.CommandId))
            { _timeline.Cancel(); _clientCooldown = Mathf.Max(.05f, _clientCooldown); _wizard.SetOnlineActionPresentation(false); }
            if (receipt.Result == OnlineCastResult.Released && (!_clientOwner || _active.CommandId == receipt.CommandId))
                _wizard.SetOnlineActionPresentation(false);
        }
        /// <summary>确认先到或预测先到均只播放一次；固定窗口不随联机时长增长，也不参与移动 replay。</summary>
        public void PlayConfirmedCastSound(ulong command, uint key, Vector3 position)
        {
            if (_authority || !_clientOwner || command == 0 || key == 0 || _definition == null) return;
            int slot = (int)(command % (ulong)_soundCommands.Length);
            if (_soundCommands[slot] == command && _soundKeys[slot] == key) return;
            var clip = _definition.SoundClip(key); if (clip == null) return;
            _soundCommands[slot] = command; _soundKeys[slot] = key;
            AudioSource.PlayClipAtPoint(clip, position);
        }
        public bool TryGetAuthorityResources(out OnlineCastResourceSnapshot snapshot)
        {
            snapshot = default;
            if (_workspace == null || !_workspace.WandsReadOnly.TryGetState(_workspace.WandsReadOnly.SelectedWandId, out var state)
                || !_workspace.WandsReadOnly.TryGetProgram(state.InstanceId, out var program)) return false;
            snapshot = new OnlineCastResourceSnapshot { WandInstanceId = state.InstanceId, ProgramRevision = program.ProgramRevision,
                CurrentMana = state.CurrentMana, MaxMana = state.MaxMana, CooldownRemaining = state.CooldownRemaining, CastInterval = state.CastCooldown };
            if (_workspace.TryGetWandExecution(state.InstanceId, out var execution))
                for (int i = 0; i < execution.Program.Count; i++)
                {
                    var instruction = execution.Program.Instructions[i];
                    if (instruction.IsIntrinsic || instruction.Kind != SpellKind.Emit || !_workspace.TryGetItem(instruction.InstanceId, out var card)) continue;
                    snapshot.RemainingCharges = card.RemainingCharges; snapshot.Unlimited = card.HasUnlimitedCharges;
                    // 仅单 Emit、无 Trigger 的确定程序预测首声；复杂牌堆不猜本次会抽到哪张卡。
                    if (execution.Program.Count == 1 && instruction.Emission.PayloadTrigger == PayloadTriggerMode.None
                        && (card.HasUnlimitedCharges || card.RemainingCharges > 0))
                        snapshot.PredictionSoundKey = _definition.FindSoundKey(instruction.Emission.CastSfx);
                    break;
                }
            return true;
        }
        private OnlineCastReceipt NewReceipt(in OnlineCastIntent intent, OnlineCastResult result, OnlineCastFailure failure) =>
            new OnlineCastReceipt { ActorId = _actor, CommandId = intent.CommandId, ConnectionEpoch = intent.ConnectionEpoch,
                Result = result, Failure = failure, Airborne = _activeAir, AimPoint = intent.AimPoint };
        private void Store(in OnlineCastReceipt receipt)
        {
            for (int i = 0; i < _receipts.Length; i++) if (_receipts[i].CommandId == receipt.CommandId && _receipts[i].ConnectionEpoch == receipt.ConnectionEpoch)
            { _receipts[i] = receipt; ReceiptProduced?.Invoke(receipt); return; }
            _receipts[_receiptCursor] = receipt; _receiptCursor = (_receiptCursor + 1) % _receipts.Length; ReceiptProduced?.Invoke(receipt);
        }
        private void CancelPending()
        {
            _queued = false; _timeline.Cancel(); _activeProgram = default; _activeSource = default;
            RestoreVisualFacing();
            _wizard?.SetOnlineActionPresentation(false);
            if (_authority) { _adapter?.CancelOutstanding(); _caster?.CancelAuthorityPending(); }
        }
        private void OnEnable() => EventBus<DeathEvent>.Subscribe(OnActorDeath);
        private void OnApplicationFocus(bool focused)
        {
            // 必须保留失焦边沿；若同一轮 Update 前失焦又回焦，只轮询最终值会漏掉取消。
            if (!focused) SetBlocked(true);
        }
        private void OnApplicationPause(bool paused)
        {
            // 恢复回调不自行解锁：World 仍可能 Frozen/断线/打开菜单，且必须继续等待旧按键释放。
            if (paused) SetBlocked(true);
        }
        private void LateUpdate()
        {
            if (_authority || !_clientOwner || !_timeline.IsActive || _suppressed || _presentationVisual == null)
            { RestoreVisualFacing(); return; }
            // 单机施法时身体朝相机水平朝向。这里仅旋转 Animator 模型子节点：CC 根、TargetFacing、
            // Prediction 历史和网络 Snapshot 仍由 Movement Authority 独占，不把表现修正伪装成物理移动。
            var pivot = _wizard.CameraRoot;
            Vector3 direction = pivot != null ? pivot.forward : _active.AimPoint - transform.position;
            direction.y = 0;
            if (direction.sqrMagnitude < .000001f) return;
            Quaternion target = Quaternion.LookRotation(direction) * _baseVisualLocalRotation;
            _presentationVisual.rotation = Quaternion.Slerp(_presentationVisual.rotation, target, Mathf.Clamp01(_wizard.RotationSpeed * Time.deltaTime));
            _ownsVisualFacing = true;
        }
        private void RestoreVisualFacing()
        {
            if (!_ownsVisualFacing) return;
            if (_presentationVisual != null) _presentationVisual.localRotation = _baseVisualLocalRotation;
            _ownsVisualFacing = false;
        }
        private void OnActorDeath(DeathEvent fact)
        {
            // 在同步 DeathEvent 内取消，与该 Tick 后续角色推进之间没有可出手的空隙。
            if (fact.TargetId == gameObject.GetInstanceID()) { CancelPending(); if (!_authority) SetBlocked(true); }
        }
        private void OnDisable() { EventBus<DeathEvent>.Unsubscribe(OnActorDeath); CancelPending(); }
        private void OnDestroy() { CancelPending(); _workspace?.Dispose(); _workspace = null; }
    }
}
