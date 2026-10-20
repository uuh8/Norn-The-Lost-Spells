using System.Collections;
using Game.Core;
using Game.Run;
using UnityEngine;

namespace Game.UI
{
    /// <summary>金门只消费转场事件；逻辑根在地面，UnifiedPortalVisual原点固定为角色缩入的门中心。</summary>
    [DefaultExecutionOrder(2000)]
    public sealed class HomePortalPresentation : MonoBehaviour
    {
        [SerializeField] private Transform _visual;
        private IPortalVisualSource _portal;
        private Vector3 _scale;
        private Coroutine _closing;
        private Camera _camera;
        private void Awake(){_portal=GetComponent<IPortalVisualSource>();if(_visual!=null){_scale=_visual.localScale;foreach(var p in _visual.GetComponentsInChildren<ParticleSystem>(true)){var main=p.main;main.useUnscaledTime=true;}}}
        private void OnEnable()=>EventBus<PortalStateChangedEvent>.Subscribe(OnState);
        private void OnDisable(){EventBus<PortalStateChangedEvent>.Unsubscribe(OnState);if(_closing!=null)StopCoroutine(_closing);}
        private void LateUpdate()
        {
            if (_visual == null) return;
            // Orbit Rig 在 LateUpdate 更新；只让视觉子根 billboard，不能转动 F 检测、落点或缩入中心。
            // Unity 已销毁引用必须用 == null 判断，不能用 ?? 保留失效 Camera。
            if (_camera == null) _camera = Camera.main;
            if (_camera == null) return;
            Vector3 direction = _camera.transform.position - _visual.position;
            if (direction.sqrMagnitude > .0001f)
                _visual.rotation = Quaternion.LookRotation(direction, _camera.transform.up);
        }
        private void OnState(PortalStateChangedEvent state)
        {
            if(state.PortalId!=_portal?.PortalId||_visual==null)return;
            if(_closing!=null){StopCoroutine(_closing);_closing=null;}
            if(state.State==PortalVfxState.Closing)_closing=StartCoroutine(Close());
            else _visual.localScale=_scale;
        }
        private IEnumerator Close()
        {
            float t=0;while(t<.9f){t+=Time.unscaledDeltaTime;_visual.localScale=_scale*Mathf.Max(.001f,1-Mathf.SmoothStep(0,1,t/.9f));yield return null;}
            _closing=null;
        }
    }
}
