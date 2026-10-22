using UnityEngine;

namespace Game.Rendering
{
    /// <summary>Camera Frustum只决定显示。固定Plane数组复用，绝不把可见性写回Gameplay。</summary>
    public sealed class MaterialPresentationView
    {
        private readonly Plane[] _planes=new Plane[6];
        private float _fadeStart,_fadeEnd,_padding;
        public bool Enabled {get;private set;}
        public Camera Camera {get;private set;}
        public Vector3 Center {get;private set;}
        public Vector4 ShaderParameters=>new Vector4(_fadeStart,_fadeEnd,Enabled?1:0,0);
        public void Update(Camera camera,Vector3 center,float fadeStart,float fadeEnd,float padding,bool enabled=true)
        {
            Camera=camera;Center=center;_fadeStart=Mathf.Max(0,fadeStart);_fadeEnd=Mathf.Max(_fadeStart+.01f,fadeEnd);_padding=Mathf.Max(0,padding);
            Enabled=enabled && camera!=null;
            // 带数组的重载不会像返回Plane[]的重载那样每帧产生GC Alloc。
            if(Enabled)GeometryUtility.CalculateFrustumPlanes(camera,_planes);
        }
        public bool Intersects(Bounds bounds,float extraPadding=0)
        {
            if(!Enabled)return true;
            float padding=_padding+extraPadding;
            Vector3 nearest=bounds.ClosestPoint(Center);
            if((nearest-Center).sqrMagnitude>(_fadeEnd+padding)*(_fadeEnd+padding))return false;
            Vector3 center=bounds.center,extent=bounds.extents;
            for(int i=0;i<6;i++)
            {
                Vector3 n=_planes[i].normal;
                float support=Mathf.Abs(n.x)*extent.x+Mathf.Abs(n.y)*extent.y+Mathf.Abs(n.z)*extent.z;
                if(_planes[i].GetDistanceToPoint(center)+support+padding<0)return false;
            }
            return true;
        }
        public float Priority(Bounds bounds)=>(bounds.ClosestPoint(Center)-Center).sqrMagnitude;
        public void Bind(Material material)
        {material.SetVector("_LiquidVisibilityCenter",Center);material.SetVector("_LiquidVisibility",ShaderParameters);}
    }
}
