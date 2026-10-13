using Game.Core;
using UnityEngine;

namespace Game.Run
{
    /// <summary>保存空世界的作者模板；真正离开后释放实例，返回重建，库存不参与清理。</summary>
    [DefaultExecutionOrder(-20000)]
    public sealed class HubExperimentWorldLifetime : MonoBehaviour
    {
        [SerializeField] private SinglePlayerInventorySession _session;
        [SerializeField] private SinglePlayerRunCoordinator _run;
        [SerializeField] private GameObject _world;
        private GameObject _template;
        private bool _discarded;
        private void Awake()
        {
            if (_world == null) return;
            // 在世界Awake之前复制inactive作者对象，模板不会注册写入口、运行模拟或分配GPU资源。
            bool active=_world.activeSelf;_world.SetActive(false);
            _template=Instantiate(_world);_template.name="Hub empty world template";
            _template.hideFlags=HideFlags.HideAndDontSave;DontDestroyOnLoad(_template);
            _world.SetActive(active);
        }
        private void OnEnable()=>EventBus<RaidStateChangedEvent>.Subscribe(OnState);
        private void OnDisable()=>EventBus<RaidStateChangedEvent>.Unsubscribe(OnState);
        private void OnDestroy(){if(_template!=null)Destroy(_template);}
        private void OnState(RaidStateChangedEvent e)
        {
            if(e.InventoryId!=_session?.ReadOnly?.InventoryId)return;
            if(e.Phase==RaidPhase.Exploring && !_discarded)
            {
                _discarded=true;
                // 已成功进入箱庭才清理；准备/加载失败仍留原世界，Destroy走各后端原OnDestroy资源释放链。
                if(_world!=null){_world.SetActive(false);Destroy(_world);}
            }
            else if(e.Phase==RaidPhase.HubReady && _discarded && _template!=null)
            {
                var old=_world;_world=Instantiate(_template);_world.name="Container Material World";
                _world.hideFlags=HideFlags.None;UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(_world,gameObject.scene);
                _run.ReplaceHubWorld(old,_world);_world.SetActive(true);_discarded=false;
            }
        }
    }
}
