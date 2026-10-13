using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Run
{
    /// <summary>普通Drop的一次性支撑查询。先取得稳定落点，再提交库存，避免无地面时资产已消耗。</summary>
    internal sealed class WorldItemGroundPlacement
    {
        private readonly RaycastHit[] _hits = new RaycastHit[64];
        internal bool TryPlace(GameObject model, Scene scene, Transform actor, LayerMask mask,
            float above, float below, float minNormalY, float clearance)
        {
            if(model==null || !scene.IsValid() || !scene.isLoaded || above<=0 || below<=0)return false;
            var physics=scene.GetPhysicsScene();if(!physics.IsValid())return false;
            if(!TryBounds(model,out var bounds))return false;
            float highest=float.NegativeInfinity;
            // 世界AABB的底面覆盖模型实际尺寸。中心及四角都要有支撑，防止大物件一半悬在台面外。
            for(int i=0;i<5;i++)
            {
                Vector3 sample=i==0?bounds.center:new Vector3((i==1 || i==3)?bounds.min.x:bounds.max.x,bounds.center.y,i<=2?bounds.min.z:bounds.max.z);
                sample.y=model.transform.position.y;
                if(!TrySupport(physics,scene,model.transform,actor,sample,mask,above,below,minNormalY,out float surface))return false;
                highest=Mathf.Max(highest,surface);
            }
            // Pivot可能在物体中心/侧面；位置Y并不等于底边Y。只平移高度，不覆写作者旋转/Scale。
            model.transform.position+=Vector3.up*(highest+Mathf.Max(0,clearance)-bounds.min.y);
            return true;
        }
        private bool TrySupport(PhysicsScene physics,Scene scene,Transform model,Transform actor,Vector3 point,LayerMask mask,
            float above,float below,float minNormalY,out float surface)
        {
            surface=float.NegativeInfinity;
            int count=physics.Raycast(point+Vector3.up*above,Vector3.down,_hits,above+below,mask,QueryTriggerInteraction.Ignore);
            // NonAlloc满缓冲时不能宣称完整查询；拒绝落点比漏掉最近支撑后把资产放到错误楼层可靠。
            if(count==_hits.Length)return false;
            for(int i=0;i<count;i++)
            {
                var hit=_hits[i];var collider=hit.collider;
                if(collider==null || collider.gameObject.scene!=scene || hit.normal.y<minNormalY
                    || hit.point.y>point.y+.75f || collider.transform.IsChildOf(model)
                    || actor!=null && collider.transform.IsChildOf(actor)
                    || collider.GetComponentInParent<CharacterController>()!=null
                    || collider.GetComponentInParent<WorldItemPickup>()!=null
                    || collider.attachedRigidbody!=null && !collider.attachedRigidbody.isKinematic)continue;
                surface=Mathf.Max(surface,hit.point.y);
            }
            return !float.IsNegativeInfinity(surface);
        }
        internal static bool TryBounds(GameObject model,out Bounds bounds)
        {
            bounds=default;bool found=false;
            // 只在离散Drop/恢复时分配一次数组。Inactive预建模型的Renderer.bounds可能尚未刷新，
            // 用localBounds八角乘localToWorldMatrix获得正确世界范围，支持偏心Pivot和父缩放。
            foreach(var renderer in model.GetComponentsInChildren<Renderer>(true))
            {
                if(!renderer.enabled || !IsVisibleUnder(renderer.transform,model.transform))continue;
                var local=renderer.localBounds;
                for(int i=0;i<8;i++)
                {
                    var corner=renderer.transform.TransformPoint(new Vector3((i&1)==0?local.min.x:local.max.x,
                        (i&2)==0?local.min.y:local.max.y,(i&4)==0?local.min.z:local.max.z));
                    if(!found){bounds=new Bounds(corner,Vector3.zero);found=true;}else bounds.Encapsulate(corner);
                }
            }
            return found;
        }
        internal static bool IsVisibleUnder(Transform part,Transform root)
        {
            // 预建OwnedPickup根刻意Inactive；只检查根以下的activeSelf，排除作者隐藏的替换模型/LOD。
            for(var node=part;node!=null && node!=root;node=node.parent)
                if(!node.gameObject.activeSelf)return false;
            return true;
        }
    }
}
