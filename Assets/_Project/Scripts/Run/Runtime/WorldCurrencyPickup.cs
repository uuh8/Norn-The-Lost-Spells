using UnityEngine;

namespace Game.Run
{
    /// <summary>金币只属于Scene直到成功入账；不占背包，不要求F，不生成第二套钱包。</summary>
    public sealed class WorldCurrencyPickup:MonoBehaviour
    {
        private SinglePlayerInventorySession _session;
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
        {_session=session;_amount=amount;}
        public bool TryCollect(Transform actor)
        {
            if(_collected||_session==null||actor==null||!actor.IsChildOf(_session.ActorTransform)
                ||actor.gameObject.scene!=gameObject.scene)return false;
            if(!_session.TryCreditDungeonCoins(_amount))return false;
            // 先封闭幂等入口，再销毁；同帧多个角色Collider不能重复加钱。
            _collected=true;for(int i=0;i<_triggers.Length;i++)_triggers[i].enabled=false;return true;
        }
        private void OnTriggerEnter(Collider other){if(other!=null)TryCollect(other.transform);}
        private void OnTriggerStay(Collider other){if(other!=null)TryCollect(other.transform);}
    }
}
