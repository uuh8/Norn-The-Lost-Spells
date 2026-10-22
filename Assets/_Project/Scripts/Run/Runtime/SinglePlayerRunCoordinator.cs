using System;
using System.Collections;
using System.Collections.Generic;
using Game.Combat;
using Game.Core;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Profiling;
#if UNITY_EDITOR
using UnityEditor.SceneManagement;
#endif
namespace Game.Run
{
    /// <summary>正式冒险生命周期唯一协调者；先提交资产，结果/Scene释放随后发生。没有世界续玩。</summary>
    public sealed class SinglePlayerRunCoordinator : MonoBehaviour
    {
        [SerializeField] private SinglePlayerInventorySession _session;
        [SerializeField] private RaidDefinition _definition;
        [SerializeField] private StarterKitDefinition _starter;
        [SerializeField] private HealthComponent _player;
        [SerializeField] private RunPauseCoordinator _pause;
        [SerializeField] private GameObject _hubWorld;
        [SerializeField] private GameObject[] _hubEnvironment = Array.Empty<GameObject>();
        private readonly RaidPreparationService _prepare = new RaidPreparationService();
        private readonly RaidSettlementService _settle = new RaidSettlementService();
        private readonly StarterKitService _kit = new StarterKitService();
        private readonly HashSet<int> _lootSources = new HashSet<int>();
        private readonly HashSet<int> _defeatedSources = new HashSet<int>();
        public int DefeatedEnemyCount => _defeatedSources.Count;
        public void RecordEnemyDefeat(int sourceId)
        { if (State.Phase == RaidPhase.Exploring || State.Phase == RaidPhase.Extracting) _defeatedSources.Add(sourceId); }
        public readonly ProtectedSpellSelection Protection = new ProtectedSpellSelection();
        public readonly RaidState State = new RaidState();
        public ExtractionProgress Extraction { get; private set; }
        public RaidSettlementReceipt LastResult => _session?.Profile?.LastRaidReceipt;
        public string Message { get; private set; } = "据点：选择携带卡保护，整备后出发。";
        public bool ActionsAllowed => !_practiceLoading && (State.Phase == RaidPhase.HubReady || State.Phase == RaidPhase.Exploring || State.Phase == RaidPhase.Extracting);
        public bool TryBeginDungeonEntryChoice() => _pause != null && !_pause.IsPaused && ActionsAllowed
            && State.Phase == RaidPhase.HubReady && _pause.RequestPause(RunPauseReason.DungeonEntryChoice);
        public void EndDungeonEntryChoice() => _pause?.ReleasePause(RunPauseReason.DungeonEntryChoice);
        private bool _practiceLoading;
        internal void SetPracticeTransition(bool loading)
        {
            // 独立练习不创建PendingRaid，但保存后的异步窗口同样必须锁资产/输入，防止提交后继续消费。
            _practiceLoading=loading;if(_session?.Profile!=null)_session.Profile.RaidActionsFrozen=loading;
            if(loading)_pause?.RequestPause(RunPauseReason.SceneTransition);else _pause?.ReleasePause(RunPauseReason.SceneTransition);
        }
        public byte PlayerTeam => _player != null ? _player.TeamId : (byte)1;
        public bool HasWorld => _world.IsValid() && _world.isLoaded;
        public RaidDefinition Definition => _definition;
        private Scene _world;
        private Scene _hubScene;
        private bool _committing, _loading, _finishing, _checkpointFailure, _needsRollback;
        private RaidOutcome _retryOutcome;
        private Vector3 _hubPosition;
        private int _lastProgressBucket = -1;
        private static readonly ProfilerMarker TickMarker = new ProfilerMarker("Norn.Raid.Update");
        private static readonly ProfilerMarker CheckpointMarker = new ProfilerMarker("Norn.Raid.Checkpoint");
        private void Awake() { if (_session != null) _session.RunCoordinator = this; }
        private void Start()
        {
            // HUD隐藏不等于无敌；据点的安全规则通过同一个Damage Funnel落实。
            if (_player != null && State.Phase == RaidPhase.HubReady) _player.SetInvulnerable(true);
        }
        private void OnEnable()
        {
            EventBus<RaidDepartureRequestedEvent>.Subscribe(OnCommand); EventBus<ExtractionRequestedEvent>.Subscribe(OnExtraction);
            EventBus<RaidConditionCompletedEvent>.Subscribe(OnCondition); EventBus<MaterialTransferResultEvent>.Subscribe(OnCollected);
            EventBus<InventoryChangedEvent>.Subscribe(OnInventory); EventBus<DeathEvent>.Subscribe(OnDeath);
        }
        private void OnDisable()
        {
            CloseSynchronously();
            EventBus<RaidDepartureRequestedEvent>.Unsubscribe(OnCommand); EventBus<ExtractionRequestedEvent>.Unsubscribe(OnExtraction);
            EventBus<RaidConditionCompletedEvent>.Unsubscribe(OnCondition); EventBus<MaterialTransferResultEvent>.Unsubscribe(OnCollected);
            EventBus<InventoryChangedEvent>.Unsubscribe(OnInventory); EventBus<DeathEvent>.Unsubscribe(OnDeath);
            _pause?.ReleasePause(RunPauseReason.SceneTransition);
        }
        private void Publish(string message, bool success = true)
        {
            Message = message;
            if(_session?.Profile!=null)_session.Profile.RaidActionsFrozen=!ActionsAllowed;
            EventBus<RaidStateChangedEvent>.Publish(new RaidStateChangedEvent { InventoryId = _session?.ReadOnly?.InventoryId,
                RaidId = State.RaidId, Phase = State.Phase, Message = message, Success = success });
        }
        private void OnCommand(RaidDepartureRequestedEvent command)
        {
            if (_session?.ReadOnly == null || command.InventoryId != _session.ReadOnly.InventoryId) return;
            if(_practiceLoading)return;
            switch (command.Command)
            {
                case RaidCommand.Protect: case RaidCommand.Unprotect:
                    bool changed = Protection.Select(_session.Profile, command.ItemId, command.Command == RaidCommand.Protect, _definition.ProtectedSlots);
                    Publish(changed ? "保护具体卡已更新 · 出发后锁定" : "保护拒绝：限2份、必须原实例、局内不可更换", changed); break;
                case RaidCommand.Depart: Depart(); break;
                case RaidCommand.StarterKit: Supply(); break;
                case RaidCommand.Abandon: if (State.Phase == RaidPhase.Exploring || State.Phase == RaidPhase.Extracting) State.RequestOutcome(RaidOutcome.Abandoned); break;
                case RaidCommand.RetrySave: Retry(); break;
            }
        }
        private Transform _departurePortal;
        private int _departurePortalId;
        public void SetDeparturePortal(Transform portal, int id) { _departurePortal = portal; _departurePortalId = id; }
        public bool Depart()
        {
            if (State.Phase != RaidPhase.HubReady || _session?.Profile == null || _loading || _finishing || _practiceLoading) return false;
            if(_session.HubInteraction!=null && (!_session.HubInteraction.TryGetStation(out var station) || station.Kind!=HubStationKind.Expedition))
            {Publish("请靠近据点地图入口再出发。",false);return false;}
            _committing = true;
            // 已投出的瓶属于旧世界；只构造候选，资格/可靠保存成功后AdoptCommitted才结束在途资源。
            // 预检或磁盘失败不能提前吞掉当前瓶，也不能将它退款成携带资产。
            var departure=_session.BuildPortalCandidate(false,out var source);
            if(!departure.Success){_committing=false;Publish(departure.Message??"离图候选失败，资产未改变。",false);return false;}
            var result = _prepare.TryPrepare(source, _session.Repository, _definition, Protection, out var next);
            if (!result.Success) { _committing = false; Publish(result.Message ?? "出发失败，资产未改变。", false); return false; }
            _session.AdoptCommitted(next); _committing = false; State.RaidId = next.PendingRaid.RaidId; State.Phase = RaidPhase.Departing;
            _hubPosition = _player.transform.position; _pause?.RequestPause(RunPauseReason.SceneTransition);
            if (_departurePortal != null) _hubPosition = PortalTransitionCoordinator.Outside(_departurePortal, _player.transform);
            _hubScene = _player.gameObject.scene;
            // Unity异步Scene在完成回调前已执行Awake/OnEnable；先释放据点唯一世界写入口，再加载箱庭。
            Publish("出发资产与保护已记录，加载新箱庭…"); _loading = true; StartCoroutine(LoadWorld()); return true;
        }
        private IEnumerator LoadWorld()
        {
            yield return PortalTransitionCoordinator.CloseAndFade(_player.transform, _departurePortal, _departurePortalId);
            SetHubEnvironment(false);
            AsyncOperation operation = null; string error = null;
            try
            {
#if UNITY_EDITOR
                if (!System.IO.File.Exists(_definition.WorldScenePath)) throw new InvalidOperationException("地图路径不存在。");
                operation = EditorSceneManager.LoadSceneAsyncInPlayMode(_definition.WorldScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
#else
                if (!Application.CanStreamedLevelBeLoaded(_definition.WorldScenePath)) throw new InvalidOperationException("地图未加入Build场景配置。");
                operation = SceneManager.LoadSceneAsync(_definition.WorldScenePath, LoadSceneMode.Additive);
#endif
            }
            catch (Exception e) { error = e.Message; }
            if (operation != null) yield return operation;
            _loading = false; _world = SceneManager.GetSceneByPath(_definition.WorldScenePath);
            if (error != null || !HasWorld) { RollbackDeparture(error ?? "加载地图失败。"); yield break; }
            var candidate = RaidProfileOps.Clone(_session.Profile); candidate.PendingRaid.Phase = RaidPhase.Exploring;
            var activated = _session.Repository.TrySave(candidate);
            if (!activated.Success) { RollbackDeparture(activated.Message); yield break; }
            _committing = true; _session.AdoptCommitted(candidate); _committing = false;
            _hubWorld?.SetActive(false);
            // 玩家移动到本局Scene，让SpellCaster/投掷的既有Scene归属自然覆盖所有攻击/VFX资源。
            SceneManager.MoveGameObjectToScene(_player.transform.root.gameObject, _world);
            _session.WorldActor=_player.transform;
            Teleport(_definition.SpawnPosition); _lootSources.Clear(); _defeatedSources.Clear();
            Extraction = new ExtractionProgress(new ExtractionConditionTracker(_definition.Condition), _definition.ExtractionSeconds);
            foreach (var root in _world.GetRootGameObjects())
            {
                foreach(var anchor in root.GetComponentsInChildren<RaidInterestAnchor>(true))anchor.Configure(_player.transform);
                foreach (var zone in root.GetComponentsInChildren<ExtractionZone>(true)) zone.Configure(_player.transform, State.RaidId, _definition.ExitPosition, _definition.ExitRadius);
                foreach (var enemy in root.GetComponentsInChildren<SpellLootDropAdapter>(true)) enemy.Configure(this);
            }
            State.Phase = RaidPhase.Exploring; _player.SetInvulnerable(false); _pause?.ReleasePause(RunPauseReason.SceneTransition); _lastProgressBucket = -1;
            Publish("出口就绪后靠近传送门按F撤离。退出/放弃损失普通携带；保护丢弃未拾回不返还。");
            _session.ActivateSceneItems(_world);
            PortalTransitionCoordinator.Reveal(_player.transform);
        }
        private void RollbackDeparture(string error)
        {
            _committing = true;
            var result = _prepare.TryRollback(_session.Profile, _session.Repository, out var hub);
            if (result.Success)
            { _session.AdoptCommitted(hub); State.Phase = RaidPhase.HubReady; State.RaidId = null; _needsRollback = false;
                if (HasWorld) StartCoroutine(UnloadFailedWorld()); else {SetHubEnvironment(true);_pause?.ReleasePause(RunPauseReason.SceneTransition);PortalTransitionCoordinator.Reveal(_player.transform);} }
            else
            {
                State.Phase = RaidPhase.SaveFailed; _needsRollback = true;
                // 写盘失败保留待回滚资产，但必须恢复可见据点，让玩家能使用已有重试入口。
                SetHubEnvironment(true); PortalTransitionCoordinator.Reveal(_player.transform);
            }
            _committing = false; Publish("地图未开始：" + error + (result.Success ? " · 已回滚出发资产/资格。" : " · 回滚写盘失败，请重试。"), false);
        }
        private IEnumerator UnloadFailedWorld() { yield return SceneManager.UnloadSceneAsync(_world); _world = default; SetHubEnvironment(true); _pause?.ReleasePause(RunPauseReason.SceneTransition); PortalTransitionCoordinator.Reveal(_player.transform); }
        private void Supply()
        {
            if (State.Phase != RaidPhase.HubReady || _session.Profile == null) return;
            _committing = true; var result = _kit.TrySupply(_session.Profile, _session.Repository, _starter, out var next);
            if (result.Success) _session.AdoptCommitted(next); _committing = false;
            Publish(result.Success ? "基础套装只补缺项 · 免费装备禁止直接出售/拆解" : result.Message ?? "基础补给未提交", result.Success);
        }
        private void OnInventory(InventoryChangedEvent changed)
        { if (!_committing && _session?.Profile?.PendingRaid != null && changed.InventoryId == _session.ReadOnly.InventoryId
            && (State.Phase == RaidPhase.Exploring || State.Phase == RaidPhase.Extracting)) Checkpoint(); }
        public ProfilePersistenceResult Checkpoint()
        {
            using var marker = CheckpointMarker.Auto();
            var result = _session.Repository.TrySave(_session.Profile);
            if (!result.Success) { _checkpointFailure = true; State.Phase = RaidPhase.SaveFailed; _pause?.RequestPause(RunPauseReason.SceneTransition);
                Publish("资产记录写入失败 · 已暂停新操作；重试保存：" + result.Message, false); }
            return result;
        }
        private void OnCollected(MaterialTransferResultEvent transfer)
        { if (_session?.ReadOnly != null && transfer.InventoryId == _session.ReadOnly.InventoryId && transfer.Success && Extraction != null
            && (State.Phase == RaidPhase.Exploring || State.Phase == RaidPhase.Extracting)) Extraction.Conditions.AddCollected(transfer.Transferred); }
        private void OnExtraction(ExtractionRequestedEvent request)
        { if (request.RaidId != State.RaidId || Extraction == null || !ActionsAllowed) return; if (request.Cancel) Extraction.Cancel(); else if(request.Start)Extraction.Start();else Extraction.SetInside(request.Inside); }
        private void OnCondition(RaidConditionCompletedEvent request)
        { if (request.RaidId == State.RaidId) Extraction?.Conditions.Complete(request.Key); }
        private void OnDeath(DeathEvent death)
        { if (_player != null && death.TargetId == _player.Id && (State.Phase == RaidPhase.Exploring || State.Phase == RaidPhase.Extracting)) State.RequestOutcome(RaidOutcome.Death); }
        private void Update()
        {
            using var marker = TickMarker.Auto();
            if (Extraction == null || (State.Phase != RaidPhase.Exploring && State.Phase != RaidPhase.Extracting)) return;
            if (Extraction.Tick(Time.deltaTime)) State.RequestOutcome(RaidOutcome.Success);
            State.Phase = Extraction.Running ? RaidPhase.Extracting : RaidPhase.Exploring;
            int bucket = Mathf.CeilToInt(Extraction.Remaining * 10);
            if (bucket == _lastProgressBucket && _lastCollected == Extraction.Conditions.Collected && _lastReady == Extraction.Conditions.Ready && _lastInside == Extraction.Inside && _lastCancelled==Extraction.Cancelled && _lastRunning==Extraction.Running) return;
            _lastProgressBucket = bucket; _lastCollected = Extraction.Conditions.Collected; _lastReady = Extraction.Conditions.Ready; _lastInside = Extraction.Inside;
            _lastCancelled=Extraction.Cancelled;
            _lastRunning=Extraction.Running;
            EventBus<ExtractionProgressChangedEvent>.Publish(new ExtractionProgressChangedEvent { InventoryId = _session.ReadOnly.InventoryId,
                RaidId = State.RaidId, Ready = _lastReady, Inside = _lastInside, Cancelled=_lastCancelled, Running=_lastRunning, Remaining = Extraction.Remaining, Collected = _lastCollected, Required = Extraction.Conditions.Required });
        }
        private ulong _lastCollected; private bool _lastReady, _lastInside, _lastCancelled, _lastRunning;
        private void LateUpdate()
        { if (!_finishing && (State.Phase == RaidPhase.Exploring || State.Phase == RaidPhase.Extracting) && State.TryResolve(out var outcome)) Finish(outcome); }
        private void Finish(RaidOutcome outcome)
        {
            if (_session.Profile.PendingRaid == null) return;
            _retryOutcome = outcome; State.Phase = RaidPhase.Settling; _pause?.RequestPause(RunPauseReason.SceneTransition); Publish("结算写盘中…");
            _committing = true;
            var result = _settle.TrySettle(_session.Profile, _session.Repository,
                new RaidSettlementRequest(State.RaidId, outcome, Extraction?.Conditions.Collected ?? 0, _defeatedSources.Count), out var next, out var receipt);
            if (!result.Success) { _committing = false; State.Phase = RaidPhase.SaveFailed; Publish("结算未提交，世界与资产保留，请重试：" + result.Message, false); return; }
            _session.AdoptCommitted(next); _committing = false; _finishing = true;
            EventBus<RaidResultEvent>.Publish(new RaidResultEvent { InventoryId = _session.ReadOnly.InventoryId, RaidId = receipt.RaidId,
                Outcome = receipt.Outcome, Returned = receipt.Returned, Lost = receipt.Lost, Generation = receipt.Generation });
            StartCoroutine(ReturnHub());
        }
        private IEnumerator ReturnHub()
        {
            Transform exit = null; int exitId = 0;
            if (HasWorld) foreach (var root in _world.GetRootGameObjects())
            { var zone = root.GetComponentInChildren<ExtractionZone>(true); if (zone != null) { exit = zone.transform; exitId = zone.PortalId; break; } }
            yield return PortalTransitionCoordinator.CloseAndFade(_player.transform, exit, exitId);
            if (_hubScene.IsValid() && _hubScene.isLoaded) SceneManager.MoveGameObjectToScene(_player.transform.root.gameObject, _hubScene);
            if (HasWorld) yield return SceneManager.UnloadSceneAsync(_world);
            _world = default; Extraction = null; SetHubEnvironment(true); _player.ResetForNewLife(); _player.SetInvulnerable(true); Teleport(_hubPosition);
            Protection.Clear(); State.Phase = RaidPhase.HubReady; State.RaidId = null; _finishing = false;
            _pause?.ReleasePause(RunPauseReason.SceneTransition); Publish("已回据点 · 实际结算已保存，可补缺并再次出发。");
            PortalTransitionCoordinator.Reveal(_player.transform);
        }
        private void Teleport(Vector3 position)
        { var controller = _player.GetComponent<CharacterController>(); bool enabled = controller != null && controller.enabled;
            if (enabled) controller.enabled = false; _player.transform.position = position; if (enabled) controller.enabled = true; }
        private void SetHubEnvironment(bool active)
        {
            if(_hubEnvironment.Length==0){_hubWorld?.SetActive(active);return;}
            // 只停用环境、材质呈现与光照；保留玩家/库存/UI，避免双世界模拟和重叠渲染。
            for(int i=0;i<_hubEnvironment.Length;i++)if(_hubEnvironment[i]!=null)_hubEnvironment[i].SetActive(active);
        }
        internal void ReplaceHubWorld(GameObject oldWorld,GameObject next)
        {
            // 世界资源根重建后，协调器必须替换真实引用；其余地图/玩家/UI继续使用原对象。
            for(int i=0;i<_hubEnvironment.Length;i++)if(ReferenceEquals(_hubEnvironment[i],oldWorld))_hubEnvironment[i]=next;
            _hubWorld=next;
        }
        private void Retry()
        {
            if (State.Phase != RaidPhase.SaveFailed) return;
            if (_needsRollback) { RollbackDeparture("重试未开始出发的回滚"); return; }
            if (_checkpointFailure)
            { if (Checkpoint().Success) { _checkpointFailure = false; State.Phase = RaidPhase.Exploring; _pause?.ReleasePause(RunPauseReason.SceneTransition); Publish("资产记录已可靠提交，继续本局。"); } return; }
            Finish(_retryOutcome);
        }
        public bool TryDropSpell(int source, SpellLootTable.Entry drop, Vector3 position)
        {
            if ((State.Phase != RaidPhase.Exploring && State.Phase != RaidPhase.Extracting) || !_lootSources.Add(source)) return false;
            return _session.TrySpawnRaidSpell(drop.ContentId, drop.Charges, drop.Unlimited, position, out _);
        }
        public bool CloseSynchronously()
        {
            if (_session?.Profile?.PendingRaid == null) return true;
            _committing = true; _session.EndWorldResources();
            SinglePlayerProfile next; ProfilePersistenceResult result;
            if (_session.Profile.PendingRaid.Phase == RaidPhase.Departing) result = _prepare.TryRollback(_session.Profile, _session.Repository, out next);
            else result = _settle.TrySettle(_session.Profile, _session.Repository,
                new RaidSettlementRequest(_session.Profile.PendingRaid.RaidId, RaidOutcome.Closed), out next, out _);
            if (result.Success) _session.AdoptCommitted(next); else GameLog.Warn("关闭结算写入失败，保留Pending供重启裁决：" + result.Message, "Raid");
            _committing = false; return result.Success;
        }
    }
}
