using System;
using Game.Core;
using Game.Materials;
using UnityEngine;
using Unity.Profiling;

namespace Game.ElementField
{
    /// <summary>监听 Core 世界命令，世界 Owner 的写职责留在 ElementField；不引用 Run/UI。</summary>
    public sealed class MaterialInventoryTransferAdapter : MonoBehaviour
    {
        [SerializeField] private ElementWorldRuntime _world;
        [SerializeField] private string _worldId = "container-world";
        [SerializeField, Min(.01f)] private float _maximumReach = 3;
        [SerializeField, Min(.01f)] private float _maximumSampleRadius = .5f;
        private MaterialInventoryWorldPort _port;
        private bool _executing;
        private readonly RaycastHit[] _sightHits=new RaycastHit[32];
        private static readonly ProfilerMarker AimMarker = new ProfilerMarker("Norn.MaterialCollection.Aim");
        public string WorldId => _worldId;
        private void Awake() { if (_world == null) _world = GetComponent<ElementWorldRuntime>(); if (_world != null) _port = new MaterialInventoryWorldPort(_world); }
        public void Configure(ElementWorldRuntime world, string id)
        { if (_executing) throw new InvalidOperationException("采集期间不能更换世界。"); _world = world; _worldId = id; _port = new MaterialInventoryWorldPort(world); }
        private void OnEnable()
        { EventBus<MaterialWorldTransferRequestedEvent>.Subscribe(OnRequest); EventBus<MaterialCollectionAimRequestedEvent>.Subscribe(OnAim); }
        private void OnDisable()
        { EventBus<MaterialWorldTransferRequestedEvent>.Unsubscribe(OnRequest); EventBus<MaterialCollectionAimRequestedEvent>.Unsubscribe(OnAim); }
        private void OnAim(MaterialCollectionAimRequestedEvent command)
        {
            using var sample = AimMarker.Auto();
            var e = command.Exchange;
            if (e == null || e.Handled || e.WorldId != _worldId) return;
            e.Handled = true; e.Found = false;
            if (_world == null || !_world.IsInitialized || !Finite(e.RayOrigin) || !Finite(e.RayDirection)
                || !Finite(e.ActorPosition) || !float.IsFinite(e.Reach) || e.Reach <= 0 || e.Reach > _maximumReach
                || !float.IsFinite(e.MaximumRayDistance) || e.MaximumRayDistance <= 0 || e.RayDirection.sqrMagnitude < .001f) return;
            Vector3 direction = e.RayDirection.normalized, offset = e.RayOrigin - e.ActorPosition;
            float projection = Vector3.Dot(offset, direction);
            float discriminant = projection * projection - offset.sqrMagnitude + e.Reach * e.Reach;
            if (discriminant < 0) return;
            float extent = Mathf.Sqrt(discriminant), start = Mathf.Max(0, -projection - extent);
            float end = Mathf.Min(e.MaximumRayDistance, -projection + extent);
            if (end < start) return;
            // 只走角色触及球内的射线段，Camera 在角色背后也不扩大采集距离；不分配临时集合。
            float step = Mathf.Max(.025f, _world.CellSize * .4f);
            for (int i = 0; i < 256 && start + i * step <= end; i++)
            {
                Vector3 point = e.RayOrigin + direction * (start + i * step);
                if(TryAimPoint(e,point,false))return;
            }
            float radius=Mathf.Clamp(e.SampleRadius,0,.5f);if(radius<=0||!float.IsFinite(radius))return;
            Vector3 right=Vector3.Cross(direction,Mathf.Abs(direction.y)>.9f?Vector3.right:Vector3.up).normalized;
            Vector3 up=Vector3.Cross(right,direction);float stride=Mathf.Max(.05f,_world.CellSize);
            int width=Mathf.CeilToInt(radius/stride),visited=0;
            // 中心射线先命中；落空才搜索有界圆截面，避免必须精确瞄准一格。候选仍需实体可见。
            for(float t=start;t<=end&&visited<4096;t+=stride)
                for(int y=-width;y<=width;y++)for(int x=-width;x<=width;x++)
                {
                    float dx=x*stride,dy=y*stride;if(dx*dx+dy*dy>radius*radius)continue;
                    Vector3 point=e.RayOrigin+direction*t+right*dx+up*dy;visited++;
                    if((point-e.ActorPosition).sqrMagnitude>e.Reach*e.Reach)continue;
                    if(TryAimPoint(e,point,true))return;
                }
        }
        private bool TryAimPoint(MaterialCollectionAimExchange e,Vector3 point,bool visibility)
        {
            var cell=Vector3Int.FloorToInt((point-_world.Origin)/_world.CellSize);
            for(byte key=1;key<=8;key++)
                if((e.AllowedMaterials&(1<<key))!=0&&_world.TryGetQuantity(cell,(MaterialId)key,out uint quantity)&&quantity>0)
                {
                    if(visibility)
                    {
                        Vector3 delta=point-e.RayOrigin;int count=Physics.RaycastNonAlloc(e.RayOrigin,delta.normalized,_sightHits,delta.magnitude-.01f,~0,QueryTriggerInteraction.Ignore);
                        bool blocked=count==_sightHits.Length;
                        for(int i=0;i<count;i++)
                        {var hit=_sightHits[i].collider;if(hit is CharacterController||ElementFieldSolidBaker.HasStatusOwner(hit))continue;
                            if((key==5||key==6)&&hit.TryGetComponent<GeneratedMaterialSolidCollider>(out _))continue;blocked=true;}
                        if(blocked)continue;
                    }
                    e.Point=point;e.MaterialKey=key;e.Found=true;return true;
                }
            return false;
        }
        private void OnRequest(MaterialWorldTransferRequestedEvent command)
        {
            var exchange = command.Exchange;
            if (exchange == null || exchange.Handled || exchange.Request.WorldId != _worldId) return;
            exchange.Handled = true;
            var request = exchange.Request; var result = exchange.Result;
            if (_executing) { result.Failure = MaterialTransferFailure.ResourceBusy; exchange.Result = result; return; }
            if (_world == null || !_world.IsInitialized || _port == null || exchange.Destination == null)
            { result.Failure = MaterialTransferFailure.WorldUnavailable; exchange.Result = result; return; }
            if (!Finite(request.SampleCenter) || !Finite(request.CollectorPosition) || float.IsNaN(request.Radius)
                || request.Radius <= 0 || request.Radius > _maximumSampleRadius
                || (request.SampleCenter - request.CollectorPosition).sqrMagnitude > _maximumReach * _maximumReach)
            { result.Failure = MaterialTransferFailure.InvalidRequest; exchange.Result = result; return; }
            _executing = true;
            try { result.Failure = _port.Transfer(request, exchange.Destination, out result.Transferred); exchange.Result = result; }
            finally { _executing = false; exchange.Destination.Cancel(); }
        }
        private static bool Finite(Vector3 point) => float.IsFinite(point.x) && float.IsFinite(point.y) && float.IsFinite(point.z);
    }
}
