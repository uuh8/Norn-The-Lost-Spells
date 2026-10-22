using System;
using System.Collections;
using Game.Core;
using Game.Combat;
using UnityEngine;
using UnityEngine.SceneManagement;
#if UNITY_EDITOR
using UnityEditor.SceneManagement;
#endif

namespace Game.Run
{
    /// <summary>家和城镇共享Repository的和平转场。只保存纯资产，不携带旧世界/角色/Scene引用。</summary>
    public sealed class HomeSceneTransfer : MonoBehaviour
    {
        public static HomeSceneTransfer Current{get;private set;}
        public bool Loading{get;private set;}
        private ProfileSaveRepository _repository;
        private SinglePlayerRunCoordinator _sourceRun;
        private bool _preview,_returning;
        private string _hubPath,_targetPath;
        private Vector3 _hubPosition,_arrival;
        private Quaternion _rotation;
        private bool _hasHubPosition;
        private int _heldSlot;
        private WandSessionTransfer _wandTransfer;
        private Transform _player,_portal;
        private int _portalId;
        public static bool TryTravel(SinglePlayerInventorySession session,HomePortal portal,Transform player)
        {
            if(session==null||portal==null||player==null||Current!=null&&Current.Loading)return false;
            string path=portal.Returning && Current!=null?Current._hubPath:portal.TargetScene;
            if(!CanLoad(path))return false;
            var saved=session.SaveForPortalDeparture();
            if(!saved.Success){GameLog.Warn("家转场保存失败，保留现场："+saved.Message,"Home");return false;}
            var context=Current;
            if(context==null)
            {
                var root=new GameObject("Home shared profile transfer");context=root.AddComponent<HomeSceneTransfer>();
                Current=context;DontDestroyOnLoad(root);SceneManager.sceneLoaded+=context.OnLoaded;
            }
            if(!portal.Returning)
            {
                context._hubPath=session.gameObject.scene.path;context._hubPosition=portal.OutsidePosition;context._hasHubPosition=true;
            }
            else if(string.IsNullOrEmpty(context._hubPath))context._hubPath=path;
            context._repository=session.Repository;context._preview=session.IsScenePreview;
            context._sourceRun=session.RunCoordinator;context._heldSlot=session.ToolbeltReadOnly.SelectedSlot;
            context._wandTransfer=session.CaptureWandTransfer();
            context._targetPath=path;context._returning=portal.Returning;
            context._arrival=portal.Returning && context._hasHubPosition?context._hubPosition:portal.ArrivalPosition;
            context._rotation=portal.ArrivalRotation;context._player=player;context._portal=portal.transform;context._portalId=portal.PortalId;
            // 保存之后才锁定，异步窗口不允许继续施法或修改已提交库存。
            context.Loading=true;context._sourceRun.SetPracticeTransition(true);context.StartCoroutine(context.Load());return true;
        }
        private static bool CanLoad(string path)
        {
#if UNITY_EDITOR
            return !string.IsNullOrWhiteSpace(path)&&System.IO.File.Exists(path);
#else
            return !string.IsNullOrWhiteSpace(path)&&Application.CanStreamedLevelBeLoaded(path);
#endif
        }
        private IEnumerator Load()
        {
            Time.timeScale=0;yield return PortalTransitionCoordinator.CloseAndFade(_player,_portal,_portalId);
            AsyncOperation operation=null;
            try
            {
#if UNITY_EDITOR
                operation=EditorSceneManager.LoadSceneAsyncInPlayMode(_targetPath,new LoadSceneParameters(LoadSceneMode.Single));
#else
                operation=SceneManager.LoadSceneAsync(_targetPath,LoadSceneMode.Single);
#endif
            }
            catch(Exception error){GameLog.Error("家Scene加载失败："+error.Message,"Home");}
            if(operation!=null)yield return operation;
            if(operation==null)
            {
                // 加载未启动时旧Scene尚在：释放原Pause/Frozen，恢复人物与金门，允许原位置重试。
                _sourceRun?.SetPracticeTransition(false);Time.timeScale=1;
                EventBus<PortalStateChangedEvent>.Publish(new PortalStateChangedEvent{PortalId=_portalId,State=PortalVfxState.Idle});
                PortalTransitionCoordinator.Reveal(_player);Loading=false;
                if(!_returning)Destroy(gameObject);
            }
        }
        private void OnLoaded(Scene scene,LoadSceneMode mode)
        {
            if(scene.path!=_targetPath)return;
            // 卸载旧Scene可能最后保存一次；读取Repository最新Generation，不能采用出门前旧快照覆盖它。
            SinglePlayerInventorySession target=null;CharacterController actor=null;
            foreach(var root in scene.GetRootGameObjects())
            {
                if(target==null)target=root.GetComponentInChildren<SinglePlayerInventorySession>(true);
                foreach(var cc in root.GetComponentsInChildren<CharacterController>(true))if(cc.GetComponent<HealthComponent>()?.TeamId==1)actor=cc;
            }
            if(!_repository.TryLoad(out var profile).Success)
            {target?.RejectTransferredProfile("家库存恢复失败，未创建新档。");GameLog.Error("家库存恢复失败，未创建新档。","Home");return;}
            profile.RaidActionsFrozen=false;
            if(target==null||actor==null||!target.InitializeTransferred(profile,_repository,_preview))
            {GameLog.Error("家目标Scene缺少库存或玩家，未另建存档。","Home");return;}
            target.RestorePracticeHeldSelection(_heldSlot);
            target.RestoreWandTransfer(_wandTransfer);
            bool enabled=actor.enabled;actor.enabled=false;actor.transform.SetPositionAndRotation(_arrival,_rotation);actor.enabled=enabled;
            _sourceRun=null;Loading=false;Time.timeScale=1;
            PortalTransitionCoordinator.Reveal(actor.transform);
            if(_returning)Destroy(gameObject);
        }
        private void OnDestroy()
        {SceneManager.sceneLoaded-=OnLoaded;if(ReferenceEquals(Current,this))Current=null;}
    }
}
