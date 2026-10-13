using System.Collections;
using Game.Core;
using UnityEngine;

namespace Game.Run
{
    /// <summary>场景初始投放模板；确认注册成功才隐藏作者模型。Runtime代理只表示同一份资产。</summary>
    public sealed class WorldItemSpawnPoint : MonoBehaviour
    {
        [SerializeField] private SinglePlayerInventorySession _session;
        [SerializeField] private ItemDefinition _definition;
        [SerializeField] private string _sceneKey="hub.map1",_spawnId;
        [SerializeField,Min(0)] private int _initialCharges=8;
        [SerializeField] private bool _unlimited;
        private Renderer[] _renderers;
        private bool[] _rendererEnabled;
        private bool _hidden;
        private string _runtimeSceneKey;
        public string SpawnId=>_spawnId;
        public ItemDefinition Definition=>_definition;
        public string SceneKey=>_runtimeSceneKey??_sceneKey;
        public bool AuthorVisualHidden=>_hidden;
        public string RuntimeInstanceId
        {
            get{if(_session==null)return null;_session.TryGetSpawnedItem(SceneKey,_spawnId,out var id,out _);return id;}
        }
        public bool TryGetRuntimeLocation(out ItemLocation location)
        {location=default;return _session!=null && _session.TryGetSpawnedItem(SceneKey,_spawnId,out _,out location);}
        /// <summary>只覆盖运行时上下文，不改作者Scene键；Additive远征和Single练习共用同一投放组件。</summary>
        public void BindRuntimeSession(SinglePlayerInventorySession session,string sceneKey)
        {_session=session;_runtimeSceneKey=sceneKey;CaptureAppearance();}
        public bool TryActivate()
        {
            if(_session?.ReadOnly==null || !enabled || string.IsNullOrWhiteSpace(_spawnId))return false;
            foreach(var root in gameObject.scene.GetRootGameObjects())foreach(var point in root.GetComponentsInChildren<WorldItemSpawnPoint>(true))
                if(point.enabled && point!=this && point.SceneKey==SceneKey && point.SpawnId==_spawnId)
                {GameLog.Error("场景物品SpawnId重复，作者模型保留："+_spawnId,"Items");return false;}
            bool accepted=_session.IsAwayRuntime
                ?_session.TryRegisterAwayWorldItem(_definition,SceneKey,_spawnId,transform.position,transform.rotation,_initialCharges,_unlimited,this)
                :_session.TryRegisterWorldItem(_definition,SceneKey,_spawnId,transform.position,transform.rotation,_initialCharges,_unlimited,this);
            if(accepted)HideAuthorVisual();return accepted;
        }
        internal void CaptureAppearance()
        {
            if(_renderers!=null)return;
            _renderers=GetComponentsInChildren<Renderer>(true);_rendererEnabled=new bool[_renderers.Length];
            for(int i=0;i<_renderers.Length;i++)_rendererEnabled[i]=_renderers[i].enabled;
        }
        internal bool HasVisualModel
        {
            get
            {
                CaptureAppearance();
                for(int i=0;i<_renderers.Length;i++)
                    if(_renderers[i]!=null && _rendererEnabled[i] && WorldItemGroundPlacement.IsVisibleUnder(_renderers[i].transform,transform))return true;
                return false;
            }
        }
        internal GameObject CreateVisualCopy(Transform parent)
        {
            CaptureAppearance();var visual=Instantiate(gameObject,parent,false);
            visual.transform.localPosition=Vector3.zero;visual.transform.localRotation=Quaternion.identity;
            // lossyScale包括Test等父节点的实际世界尺寸。运行时根保持单位Scale，防止重复继承父缩放。
            visual.transform.localScale=transform.lossyScale;
            var copies=visual.GetComponentsInChildren<Renderer>(true);
            for(int i=0;i<copies.Length && i<_rendererEnabled.Length;i++)copies[i].enabled=_rendererEnabled[i];
            visual.SetActive(true);return visual;
        }
        internal Vector3 ScaleRatio()
        {
            // Empty投放点的Scale作为Prefab倍率；已有作者模型时，Scale表示该实例最终世界尺寸。
            Vector3 baseScale=HasVisualModel && _definition!=null && _definition.PickupPrefab!=null?_definition.PickupPrefab.transform.localScale:Vector3.one;
            var authored=transform.lossyScale;
            return new Vector3(authored.x/baseScale.x,authored.y/baseScale.y,authored.z/baseScale.z);
        }
        private void HideAuthorVisual()
        {
            CaptureAppearance();_hidden=true;
            // 保留Test下的作者节点与子层级供Inspector定位；只关闭可见/物理表示，避免复制体与原模型重叠。
            foreach(var renderer in _renderers)if(renderer!=null)renderer.enabled=false;
            foreach(var collider in GetComponentsInChildren<Collider>(true))collider.enabled=false;
            foreach(var body in GetComponentsInChildren<Rigidbody>(true)){body.isKinematic=true;body.useGravity=false;}
        }
        private IEnumerator Start()
        {
            // 只在Start分配Scene查询数组。Update不扫描对象，不随启停重复发放。
            if(_session==null)foreach(var root in gameObject.scene.GetRootGameObjects())
            { _session=root.GetComponentInChildren<SinglePlayerInventorySession>(true);if(_session!=null)break; }
            if(_session==null){GameLog.Warn("场景物品缺少同Scene库存Session。","Items");yield break;}
            while(_session.ReadOnly==null && string.IsNullOrEmpty(_session.InitializationMessage))yield return null;
            if(_session.ReadOnly==null)yield break;
            if(!TryActivate())GameLog.Warn("场景物品注册被拒绝，作者模型保留："+_spawnId,"Items");
        }
    }
}
