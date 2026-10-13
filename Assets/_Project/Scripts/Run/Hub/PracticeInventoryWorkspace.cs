using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Run
{
    /// <summary>每张练习Scene一个工作区。只接Run资产，不引用UI/Character；表现与输入由Prefab内部连接。</summary>
    public sealed class PracticeInventoryWorkspace : MonoBehaviour
    {
        [SerializeField] private SinglePlayerInventorySession _session;
        [SerializeField] private NearbyPickupController _nearby;
        public SinglePlayerInventorySession Session=>_session;
        public NearbyPickupController Nearby=>_nearby;
        public RunPauseCoordinator Pause {get;private set;}
        public Transform Player {get;private set;}
        private List<string> _qualified;
        private bool _reloadCommitted;
        internal void SuppressExitSnapshot() { _reloadCommitted = true; _session.SuppressPracticeExitSave(); }
        internal void ResumeExitSnapshot() { _reloadCommitted = false; _session.ResumePracticeExitSave(); }

        internal void Configure(SinglePlayerProfile profile,ProfileSaveRepository repository,bool scenePreview,Transform player,
            string sceneKey,Dictionary<(string,string),string> spawns,string raidId,List<string> qualified)
        {
            Player=player;
            // 旧教学已有暂停协调器，复用它才能同时持有Guide/Inventory理由，避免两个控制器覆盖TimeScale。
            foreach(var root in gameObject.scene.GetRootGameObjects())
            {Pause=root.GetComponentInChildren<RunPauseCoordinator>(true);if(Pause!=null)break;}
            if(Pause==null)Pause=gameObject.AddComponent<RunPauseCoordinator>();
            _nearby.Configure(_session,player);
            if(!_session.ConfigurePractice(profile,repository,scenePreview,player,sceneKey,spawns))
                throw new InvalidOperationException("练习库存初始化失败。");
            _qualified=qualified;_session.ResumePracticeQualifications(raidId,qualified);
        }
        internal ProfilePersistenceResult SaveForLeaving()
        {
            var result=_session.SavePracticeForLeaving();if(result.Success){_session.CopyPracticeQualifications(_qualified);RememberSelection();}return result;
        }
        private void RememberSelection()=>HubPracticeSession.Current?.RememberHeldSelection(this,_session.ToolbeltReadOnly.SelectedSlot);
        private void OnDestroy(){if(!_reloadCommitted && _qualified!=null){_session.CopyPracticeQualifications(_qualified);RememberSelection();}}
    }
}
