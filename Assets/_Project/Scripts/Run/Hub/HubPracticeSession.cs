using System;
using System.Collections;
using System.Collections.Generic;
using Game.Core;
using Game.Combat;
using UnityEngine;
using UnityEngine.SceneManagement;
#if UNITY_EDITOR
using UnityEditor.SceneManagement;
#endif

namespace Game.Run
{
    /// <summary>跨旧练习Scene携带纯资产与同一Repository；Scene对象、输入和世界代理各自随场景释放。</summary>
    public sealed class HubPracticeSession : MonoBehaviour
    {
        public static HubPracticeSession Current{get;private set;}
        private WandSessionTransfer _wandTransfer;
        private bool _preserveWandResources = true;
        public bool Loading{get;private set;}
        private string _hubPath;
        private GameObject _returnPrefab;
        private SinglePlayerRunCoordinator _sourceRun;
        private bool _returning;
        private GameObject _workspacePrefab;
        private PracticeInventoryWorkspace _workspace;
        private SinglePlayerProfile _profile;
        private ProfileSaveRepository _repository;
        private bool _scenePreview;
        private readonly Dictionary<(string,string),string> _spawns=new Dictionary<(string,string),string>();
        private readonly List<string> _qualified=new List<string>();
        private readonly string _practiceRaidId="practice."+Guid.NewGuid().ToString("N");
        private int _selectedHeldSlot;
        private Vector3 _hubReturnPosition;
        private bool _hasReturnPosition;
        private Transform _transitPlayer, _transitPortal;
        private int _transitPortalId;
        private SinglePlayerProfile _bossCheckpoint;
        private List<string> _bossQualified;
        private int _bossHeldSlot;
        private bool _retrying;
        private long _retryGeneration;
        private HealthComponent _practicePlayer;
        private BossEncounterController _bossEncounter;
        private HubPracticePortal _returnGate;
        private bool _deathPending;
        private bool _hasShownGameplayGuide;
        private string _menuDestination;
        public bool TryMarkGameplayGuideShown()
        {
            if (_hasShownGameplayGuide) return false;
            _hasShownGameplayGuide = true;
            return true;
        }
        public bool OwnsScene(Scene scene)=>_workspace!=null && _workspace.gameObject.scene==scene;
        private void OnEnable()
        {
            EventBus<DeathEvent>.Subscribe(OnPlayerDeath);
            EventBus<RunStateChangedEvent>.Subscribe(OnRunStateChanged);
        }
        private void OnDisable()
        {
            EventBus<DeathEvent>.Unsubscribe(OnPlayerDeath);
            EventBus<RunStateChangedEvent>.Unsubscribe(OnRunStateChanged);
        }
        private void OnPlayerDeath(DeathEvent death)
        {
            // P8安全区尚无Running Encounter也会死亡，因此筛选真实玩家Id，不依赖旧Run终局事件。
            if(_deathPending || Loading || _returning || _workspace==null || _practicePlayer==null || death.TargetId!=_practicePlayer.Id)return;
            _deathPending=true;StartCoroutine(ReturnAfterDeath());
        }
        private IEnumerator ReturnAfterDeath()
        {
            yield return new WaitForSecondsRealtime(.9f);
            string lastFailure=null;
            while(_workspace!=null)
            {
                var session=_workspace.Session;
                var result=session.BuildPortalCandidate(true,out var exit);
                SinglePlayerProfile candidate=null;
                if(result.Success && !PracticeDeathSettlement.TryCreate(exit,out candidate))
                    result=new ProfilePersistenceResult(ProfilePersistenceStatus.InvalidData,"死亡清算父链校验失败");
                if(result.Success)result=_repository.TrySave(candidate);
                if(result.Success)
                {
                    // Save成功才采用；旧Scene的OnDestroy不能用失败前的装备快照覆盖这份清算。
                    _workspace.SuppressExitSnapshot();session.AdoptPracticeDeath(candidate);
                    _profile=session.Profile;_profile.RaidActionsFrozen=true;
                    _qualified.Clear();_selectedHeldSlot=-1;_returning=true;_preserveWandResources=false;
                    _transitPlayer=null;_transitPortal=null;_transitPortalId=0;
                    _workspace.Pause.PrepareForSceneTransition();
                    do {yield return Load(_hubPath);if(_workspace!=null)yield return new WaitForSecondsRealtime(.5f);}
                    while(_workspace!=null && ReferenceEquals(Current,this));
                    yield break;
                }
                // 失败不修改原Store，也不把无按钮的死亡玩家永久困住；材料事务/临时IO恢复后自动重试。
                if(lastFailure!=result.Message){lastFailure=result.Message;GameLog.Warn("死亡返回暂未提交，将自动重试："+result.Message,"Practice");}
                yield return new WaitForSecondsRealtime(.5f);
            }
        }
        private void OnRunStateChanged(RunStateChangedEvent state)
        {
            // 全局Run事件没有SceneId，必须再核对本场Boss权威，不能把P7清场或另一场测试的Completed当胜利。
            if(state.CurrentState==RunState.Completed && !_deathPending && _bossEncounter!=null
                && _bossEncounter.State==BossEncounterState.Completed && _workspace!=null)SpawnReturnGate();
        }
        private void SpawnReturnGate()
        {
            if(_returnGate!=null || _workspace==null || _workspace.Player==null)return;
            var actor=_workspace.Player;Vector3 position=actor.position;
            // 优先玩家附近可站立地面，避免Boss死亡点在悬崖/树冠/障碍里。只在一次胜利时查询，不进热路径。
            for(int i=0;i<8;i++)
            {
                Vector3 offset=Quaternion.Euler(0,i*45,0)*actor.right*4;
                Vector3 point=actor.position+offset;
                if(!Physics.Raycast(point+Vector3.up*5,Vector3.down,out var ground,12,~0,QueryTriggerInteraction.Ignore)
                    || ground.normal.y<.6f)continue;
                point.y=ground.point.y;
                if(Physics.CheckCapsule(point+Vector3.up,point+Vector3.up*3,.8f,~0,QueryTriggerInteraction.Ignore))continue;
                position=point;break;
            }
            Vector3 direction=actor.position-position;direction.y=0;
            var rotation=direction.sqrMagnitude>.01f?Quaternion.LookRotation(direction):actor.rotation;
            var portal=Instantiate(_returnPrefab,position,rotation);SceneManager.MoveGameObjectToScene(portal,_workspace.gameObject.scene);
            portal.name="返回据点传送门";_returnGate=portal.GetComponent<HubPracticePortal>();_returnGate.ConfigureReturn(actor);
        }
        public bool HasBossCheckpoint => _bossCheckpoint != null;

        public bool CaptureBossCheckpoint(Collider actor)
        {
            if (Loading || _returning || _deathPending || _workspace == null || actor == null
                || !actor.transform.IsChildOf(_workspace.Player) || actor.gameObject.scene != _workspace.gameObject.scene) return false;
            var session = _workspace.Session;
            var built = session.BuildPortalCandidate(true, out var candidate);
            if (!built.Success) return false;
            var saved = _repository.TrySave(candidate);
            if (!saved.Success) { GameLog.Warn("Boss入场快照保存失败，保留现场并等待再次出圈：" + saved.Message, "Practice"); return false; }
            // 只同步提交代，不采用清地面候选；入场记录不能销毁当前地图的拾取/飞行代理。
            session.Profile.ConfirmSaved(candidate.SaveGeneration);
            _bossCheckpoint = RaidProfileOps.Clone(candidate);
            _bossQualified = new List<string>(); session.CopyPracticeQualifications(_bossQualified);
            _bossHeldSlot = session.ToolbeltReadOnly.SelectedSlot;
            return true;
        }

        public bool RetryBoss()
        {
            if (Loading || _returning || _deathPending || _workspace == null || _bossCheckpoint == null) return false;
            string path = _workspace.gameObject.scene.path; if (!CanLoad(path)) return false;
            var candidate = RaidProfileOps.Clone(_bossCheckpoint);
            candidate.ConfirmSaved(_workspace.Session.Profile.SaveGeneration);
            var saved = _repository.TrySave(candidate);
            if (!saved.Success) { GameLog.Warn("Boss重试快照保存失败，当前资产未改变：" + saved.Message, "Practice"); return false; }
            _retryGeneration=candidate.SaveGeneration;
            // 成功后才冻结写入并禁止旧Scene自动保存；地图/HP/元素由Scene重载，资产由同Repository恢复。
            _workspace.Session.Profile.RaidActionsFrozen = true; _workspace.SuppressExitSnapshot();
            _transitPlayer = null; _transitPortal = null; _transitPortalId = 0;
            _retrying=true;StartCoroutine(Load(path)); return true;
        }
        internal void RememberHeldSelection(PracticeInventoryWorkspace workspace,int slot)
        {if(ReferenceEquals(_workspace,workspace))_selectedHeldSlot=slot;}
        public static bool BeginDirectPractice(Scene scene,ItemCatalog catalog,SinglePlayerStartingInventory seeds,GameObject workspace,
            GameObject portal,string hub)
        {
#if UNITY_EDITOR
            if(Current!=null || catalog==null || seeds==null || workspace==null || portal==null
                || !catalog.TryCreateIndex(out var index,out _) || !seeds.TryCreate(index,out var profile,out _))return false;
            // 直接练习永远建立内存Repository；与正式持久化目录无关，Stop Play后自然销毁。
            var repository=new ProfileSaveRepository("PracticePreview/"+Guid.NewGuid().ToString("N"),index,new EditorScenePreviewFileSystem());
            if(!repository.TrySave(profile).Success)return false;
            var root=new GameObject("Direct practice return context");var context=root.AddComponent<HubPracticeSession>();
            context._hubPath=hub;context._workspacePrefab=workspace;context._returnPrefab=portal;context._scenePreview=true;
            context._repository=repository;context._profile=profile;Current=context;DontDestroyOnLoad(root);
            SceneManager.sceneLoaded+=context.OnLoaded;context.OnLoaded(scene,LoadSceneMode.Single);return true;
#else
            GameLog.Warn("练习直接初始化只用于Editor；正式游戏请从据点传送进入。","Practice");return false;
#endif
        }
        public static bool TryBegin(SinglePlayerInventorySession session,string target,GameObject returnPrefab, Transform portal = null, Transform player = null, int portalId = 0)
        {
            if(Current!=null || session==null || session.ReadOnly==null || returnPrefab==null || !CanLoad(target))return false;
            if(session.RunCoordinator==null || session.RunCoordinator.State.Phase!=RaidPhase.HubReady || !session.RunCoordinator.ActionsAllowed)return false;
            if(!RaidSpellEligibility.CanStartPractice(session.Profile))
            {
                EventBus<HubDepartureReviewResultEvent>.Publish(new HubDepartureReviewResultEvent{InventoryId=session.ReadOnly?.InventoryId,
                    WorldReady=false,Message="法杖中有已耗尽的资格法术，请先移除或更换后出发。"});
                return false;
            }
            if(session.PracticeWorkspacePrefab==null){GameLog.Error("练习入口缺少PracticeInventoryWorkspace Prefab，请运行物品携带接线工具。","Practice");return false;}
            var saved=session.SaveForPortalDeparture();if(!saved.Success)
            {
                GameLog.Warn("练习入口保存失败："+saved.Message,"Practice");
                EventBus<HubDepartureReviewResultEvent>.Publish(new HubDepartureReviewResultEvent{InventoryId=session.ReadOnly?.InventoryId,
                    WorldReady=false,Message="练习入口保存失败，请关闭提示后重试："+saved.Message});return false;
            }
            var root=new GameObject("Hub practice return context");var context=root.AddComponent<HubPracticeSession>();
            context._hubPath=session.gameObject.scene.path;context._returnPrefab=returnPrefab;
            context._profile=session.Profile;context._repository=session.Repository;context._scenePreview=session.IsScenePreview;
            context._selectedHeldSlot=session.ToolbeltReadOnly.SelectedSlot;
            context._wandTransfer=session.CaptureWandTransfer();
            context._workspacePrefab=session.PracticeWorkspacePrefab;
            context._sourceRun=session.RunCoordinator;context._sourceRun.SetPracticeTransition(true);
            context._transitPlayer=player;context._transitPortal=portal;context._transitPortalId=portalId;
            if(portal!=null && player!=null){context._hubReturnPosition=PortalTransitionCoordinator.Outside(portal,player);context._hasReturnPosition=true;}
            Current=context;DontDestroyOnLoad(root);SceneManager.sceneLoaded+=context.OnLoaded;
            context.StartCoroutine(context.Load(target));return true;
        }
        private static bool CanLoad(string path)
        {
#if UNITY_EDITOR
            return !string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path);
#else
            return Application.CanStreamedLevelBeLoaded(path);
#endif
        }
        public bool ReturnToHub(Transform player = null, Transform portal = null, int portalId = 0)
        {
            if(Loading || _returning || _deathPending || !CanLoad(_hubPath))return false;
            var saved=_workspace!=null?_workspace.SaveForLeaving():new ProfilePersistenceResult(ProfilePersistenceStatus.InvalidData,"缺少练习库存");
            if(!saved.Success){GameLog.Warn("回据点保存失败，留在练习地图："+saved.Message,"Practice");return false;}
            _profile.RaidActionsFrozen=true;
            _transitPlayer=player;_transitPortal=portal;_transitPortalId=portalId;
            _wandTransfer=_workspace.Session.CaptureWandTransfer();
            _returning=true;StartCoroutine(Load(_hubPath));return true;
        }
        /// <summary>回主菜单也必须先提交真实携带库存；加载成功后释放跨场景上下文。</summary>
        public bool ReturnToMainMenu(string path)
        {
            if (Loading || _returning || _deathPending || !CanLoad(path) || _workspace == null) return false;
            var saved = _workspace.SaveForLeaving();
            if (!saved.Success) { GameLog.Warn("返回主菜单保存失败：" + saved.Message, "Practice"); return false; }
            // 保存失败时不冻结、不换场景；成功后禁止重复导航，避免同一快照并发写入。
            _profile.RaidActionsFrozen = true;
            _menuDestination = path; _returning = true;
            _transitPlayer = null; _transitPortal = null; _transitPortalId = 0;
            StartCoroutine(Load(path)); return true;
        }
        private IEnumerator Load(string path)
        {
            if (_workspace != null && _workspace.Session != null) _wandTransfer = _workspace.Session.CaptureWandTransfer();
            Loading=true;Cursor.lockState=CursorLockMode.None;Cursor.visible=true;
            Time.timeScale=0;
            yield return PortalTransitionCoordinator.CloseAndFade(_transitPlayer,_transitPortal,_transitPortalId);
            AsyncOperation operation=null;
            try
            {
#if UNITY_EDITOR
                operation=EditorSceneManager.LoadSceneAsyncInPlayMode(path,new LoadSceneParameters(LoadSceneMode.Single));
#else
                operation=SceneManager.LoadSceneAsync(path,LoadSceneMode.Single);
#endif
            }
            catch(Exception e){GameLog.Error("练习Scene加载失败："+e.Message,"Practice");}
            if(operation!=null)yield return operation;
            Loading=false;
            if (operation==null && _retrying && _workspace!=null)
            {
                // 加载尚未发生时旧Scene仍在，保存代已提交但不该永久冻结现场或丢掉重试上下文。
                _workspace.Session.Profile.RaidActionsFrozen=false;_workspace.ResumeExitSnapshot();
                _workspace.Session.Profile.ConfirmSaved(_retryGeneration);
                _retrying=false;Time.timeScale=1;PortalTransitionCoordinator.Reveal(null);yield break;
            }
            _retrying=false;
            if(operation==null)
            {
                Time.timeScale=1;PortalTransitionCoordinator.Reveal(_transitPlayer);
                // 死亡已清算时保留上下文继续加载，不能解锁旧装备或销毁唯一返回入口。
                if(_deathPending)yield break;
                _returning=false;Destroy(gameObject);
            }
        }
        private void OnLoaded(Scene scene,LoadSceneMode mode)
        {
            if (!string.IsNullOrEmpty(_menuDestination) && scene.path == _menuDestination)
            {
                Time.timeScale = 1; PortalTransitionCoordinator.Reveal(null);
                Destroy(gameObject); return;
            }
            bool practice=scene.path=="Assets/_Project/Scenes/P7_DemoRun.unity" || scene.path=="Assets/_Project/Scenes/P8_BossField.unity";
            if(!practice)foreach(var root in scene.GetRootGameObjects())if(root.GetComponentInChildren<PracticeInventorySceneBootstrap>(true)!=null){practice=true;break;}
            if(scene.path!=_hubPath && !practice)return;
            // sceneLoaded先于目标Scene的Start；原教学自己的暂停仍由它随后申请，不能在OnDestroy强制覆盖。
            Time.timeScale=1;
            // 旧Session的资格策略持有旧Store；从同一Repository恢复纯数据，避免两个Scene共用写策略。
            // 同时读取卸载时最后保存的Generation，防止ScenePreview的OnDestroy保存使捕获快照过期。
            if(!_repository.TryLoad(out var transferred).Success)
            {GameLog.Error("跨Scene库存恢复失败，未发放新档。","Practice");return;}
            _profile=transferred;
            if (_retrying && practice)
            {
                // Scene真正加载后才替换附属状态；Load抛异常时旧地图仍可继续，不能提前清掉它的Spawn映射。
                _qualified.Clear();_qualified.AddRange(_bossQualified);_selectedHeldSlot=_bossHeldSlot;
                var stale=new List<(string,string)>();
                foreach(var spawn in _spawns)if(spawn.Key.Item1==scene.path)stale.Add(spawn.Key);
                foreach(var key in stale)_spawns.Remove(key);
            }
            if(_returning && scene.path==_hubPath)
            {
                // sceneLoaded早于Start，确保Hub使用原试玩Repository，不能另发新档或转去正式磁盘档。
                _profile.RaidActionsFrozen=false;
                foreach(var root in scene.GetRootGameObjects())
                {
                    var session=root.GetComponentInChildren<SinglePlayerInventorySession>(true);
                    if(session!=null && session.ReadOnly==null){session.InitializeTransferred(_profile,_repository,_scenePreview);session.RestorePracticeHeldSelection(_selectedHeldSlot);session.RestoreWandTransfer(_wandTransfer,_preserveWandResources);}
                    if(_hasReturnPosition)foreach(var controller in root.GetComponentsInChildren<CharacterController>(true))
                    {
                        if(controller.GetComponent<HealthComponent>()?.TeamId!=1)continue;
                        bool enabled=controller.enabled;controller.enabled=false;controller.transform.position=_hubReturnPosition;controller.enabled=enabled;
                    }
                }
                PortalTransitionCoordinator.Reveal(null);
                Destroy(gameObject);return;
            }
            if(scene.path==_hubPath || _returnPrefab==null)return;
            // 每张练习图分别初始化角色/世界并提供回程门，P7通关使用同一上下文回据点。
            HealthComponent actor=null;
            foreach(var root in scene.GetRootGameObjects())foreach(var health in root.GetComponentsInChildren<HealthComponent>(true))
                if(health.TeamId==1 && health.GetComponent<CharacterController>()!=null){actor=health;break;}
            if(actor==null)return;
            _practicePlayer=actor;_bossEncounter=null;_returnGate=null;
            foreach(var root in scene.GetRootGameObjects())
            {var boss=root.GetComponentInChildren<BossEncounterController>(true);if(boss!=null){_bossEncounter=boss;break;}}
            var workspace=Instantiate(_workspacePrefab);workspace.SetActive(false);SceneManager.MoveGameObjectToScene(workspace,scene);
            workspace.name="PracticeInventoryWorkspace_本轮携带资产";_workspace=workspace.GetComponent<PracticeInventoryWorkspace>();
            _workspace.Configure(_profile,_repository,_scenePreview,actor.transform,scene.path,_spawns,_practiceRaidId,_qualified);
            _workspace.Session.RestorePracticeHeldSelection(_selectedHeldSlot);
            _workspace.Session.RestoreWandTransfer(_wandTransfer, !_retrying && _preserveWandResources);
            workspace.SetActive(true);_workspace.Session.ActivateSceneItems(scene);
            // P7保留练习回程与最终房间出口；P8只在Boss胜利后生成门，不弹旧结果框。
            if(_bossEncounter==null)SpawnReturnGate();
            PortalTransitionCoordinator.Reveal(actor.transform);
        }
        private void OnDestroy()
        {SceneManager.sceneLoaded-=OnLoaded;if(_sourceRun!=null)_sourceRun.SetPracticeTransition(false);if(ReferenceEquals(Current,this))Current=null;}
    }
}
