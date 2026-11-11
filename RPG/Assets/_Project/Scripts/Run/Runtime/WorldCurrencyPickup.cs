using UnityEngine;
using Game.Core;

namespace Game.Run
{
    /// <summary>金币只属于Scene直到成功入账；不占背包，不要求F，不生成第二套钱包。</summary>
    public sealed class WorldCurrencyPickup:MonoBehaviour
    {
        private SinglePlayerInventorySession _session;
        private IWorldCurrencyCollector _authorityCollector;
        private ulong _coinId;
        private bool _replica;
        private long _amount;
        private bool _collected;
        [SerializeField]private Transform _visual;
        [SerializeField,Min(0)]private float _spinSpeed=55;
        [SerializeField,Min(.01f)]private float _collectDuration=.35f;
        [SerializeField,Min(0)]private float _jumpHeight=.65f;
        private Collider[] _triggers;
        private Vector3 _basePosition,_baseScale;
        private Quaternion _baseRotation;
        private float _angle,_elapsed;
        private static readonly Unity.Profiling.ProfilerMarker Marker=new Unity.Profiling.ProfilerMarker("Norn.Dungeon.CoinVisual");
        private void Awake()
        {
            if(_visual==null)_visual=transform.Find("Gold coin");
            _triggers=GetComponents<Collider>();
            if(_visual!=null){_basePosition=_visual.localPosition;_baseScale=_visual.localScale;_baseRotation=_visual.localRotation;}
        }
        private void Update()
        {
            using var marker=Marker.Auto();
            _angle=(_angle+_spinSpeed*Time.deltaTime)%360;
            if(_visual!=null)_visual.localRotation=Quaternion.AngleAxis(_angle,Vector3.up)*_baseRotation;
            if(!_collected)return;
            _elapsed+=Time.deltaTime;float t=Mathf.Clamp01(_elapsed/_collectDuration);
            // sin(pi*t)从0升起再落回；局部坐标只移动视觉，Trigger不会随着跳动继续入账。
            if(_visual!=null){_visual.localPosition=_basePosition+Vector3.up*(_jumpHeight*Mathf.Sin(Mathf.PI*t));_visual.localScale=_baseScale*(1-t);}
            if(t>=1)Destroy(gameObject);
        }
        public long Amount=>_amount;
        public bool Collected=>_collected;
        public void Configure(SinglePlayerInventorySession session,long amount)
        {_session=session;_amount=amount;_authorityCollector=null;_coinId=0;_replica=false;}
        /// <summary>DS显式接入可信Collector，Collider只提供实际接触对象，不从Client接受自报归属/金币量。</summary>
        public void ConfigureAuthority(IWorldCurrencyCollector collector,ulong coinId,long amount)
        {
            if(collector==null||coinId==0||amount<=0)throw new System.ArgumentException("金币权威配置缺少收集端口或真实身份/数量。");
            _session=null;_authorityCollector=collector;_coinId=coinId;_amount=amount;_replica=false;
        }
        /// <summary>Replica仅用原视觉/收取动画，不把接触变为记账请求，也不创建本地钱包。</summary>
        public void ConfigureReplica(long amount)
        {
            if(amount<=0)throw new System.ArgumentException("金币表现需要真实正数量。");
            _session=null;_authorityCollector=null;_coinId=0;_amount=amount;_replica=true;
        }
        public bool TryCollect(Transform actor)
        {
            if(_collected||actor==null||actor.gameObject.scene!=gameObject.scene||_replica)return false;
            if(_authorityCollector!=null)
            {
                if(_authorityCollector is UnityEngine.Object unity&&unity==null)return false;
                if(!_authorityCollector.TryCollectWorldCurrency(_coinId,actor))return false;
            }
            else
            {
                // 保留原单机Actor层级与Session事务，不因联机端口改变原触发/拾取范围。
                if(_session==null||!actor.IsChildOf(_session.ActorTransform)||!_session.TryCreditDungeonCoins(_amount))return false;
            }
            PresentCollected();return true;
        }
        /// <summary>网络确认后复用原0.35s动画；此口只封闭触发/显示，不授予任何资源。</summary>
        public void PresentCollected()
        {
            if(_collected)return;
            // 先封闭幂等入口，再销毁；同帧多个角色Collider不能重复加钱。
            _collected=true;if(_triggers==null)_triggers=GetComponents<Collider>();
            for(int i=0;i<_triggers.Length;i++)if(_triggers[i]!=null)_triggers[i].enabled=false;
        }
        private void OnTriggerEnter(Collider other){if(other!=null)TryCollect(other.transform);}
        private void OnTriggerStay(Collider other){if(other!=null)TryCollect(other.transform);}
    }
}
