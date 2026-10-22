using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
namespace Game.Rendering
{
    /// <summary>生命周期拥有Material和两段Pass，游戏模块不引用URP。只对真实拾取代理的摄像机绘制。</summary>
    public sealed class WorldItemGlowRendererFeature:ScriptableRendererFeature
    {
        public WorldItemHighlightProfile Profile;
        public Shader GlowShader;
        private Material _material;
        private WorldItemGlowCapturePass _capture;
        private WorldItemGlowPass _compose;
        private readonly Plane[] _planes=new Plane[6];
        private static readonly Unity.Profiling.ProfilerMarker FrustumMarker=new Unity.Profiling.ProfilerMarker("Norn.ItemGlow.Frustum");
        public override void Create()
        {
            CoreUtils.Destroy(_material);
            if(GlowShader==null)GlowShader=Shader.Find("Hidden/Norn/ItemPickupGlow");
            if(GlowShader==null)return;
            _material=CoreUtils.CreateEngineMaterial(GlowShader);
            _capture=new WorldItemGlowCapturePass();_compose=new WorldItemGlowPass(_material);
        }
        public override void AddRenderPasses(ScriptableRenderer renderer,ref RenderingData data)
        {
            using var scope=FrustumMarker.Auto();
            if(_material==null || Profile==null || WorldItemHighlight.ActiveCount==0 || data.cameraData.renderType==CameraRenderType.Overlay
                || data.cameraData.cameraType!=CameraType.Game && data.cameraData.cameraType!=CameraType.SceneView)return;
            GeometryUtility.CalculateFrustumPlanes(data.cameraData.camera,_planes);
            if(!WorldItemHighlight.HasVisibleSource(data.cameraData.camera,_planes))return;
            _material.SetFloat("_RadiusPixels",Profile.HaloRadiusPixels);_material.SetFloat("_HaloStrength",Profile.HaloStrength);
            _material.SetFloat("_OutlinePixels",Profile.OutlinePixels);_material.SetColor("_OutlineColor",Profile.Color*Profile.Strength);
            _material.SetColor("_HaloColor",Profile.HaloColor);
            renderer.EnqueuePass(_capture);renderer.EnqueuePass(_compose);
        }
        protected override void Dispose(bool disposing){CoreUtils.Destroy(_material);_material=null;}
    }
}
